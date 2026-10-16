using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twenty.Ingestion.Capture;

/// <summary>
/// Private, append-only store of raw source captures. Identical bytes are stored once; every fetch, including an
/// unchanged one, is a separate observation, because "still listed today" is evidence too. One writer per root.
/// A remote collector also keeps an outbox of observations not yet acknowledged by the receiving store.
/// </summary>
public sealed class CaptureStore : IDisposable
{
    public const long MaximumCaptureBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string root;
    private readonly long maximumStoreBytes;
    private readonly FileStream lease;
    private readonly object gate = new();

    public CaptureStore(string directory, long maximumStoreBytes = 4L * 1024 * 1024 * 1024)
    {
        if (maximumStoreBytes < MaximumCaptureBytes) throw new CaptureException("invalid-store-limit");
        this.maximumStoreBytes = maximumStoreBytes;
        root = Path.Combine(Path.GetFullPath(directory), "capture-v1");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { lease = new FileStream(Path.Combine(root, ".writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new CaptureException("store-in-use"); }
        foreach (var part in new[] { "objects", "observations", "status", "outbox", "received" }) Directory.CreateDirectory(Path.Combine(root, part));
        foreach (var temporary in Directory.EnumerateFiles(root, ".write-*", SearchOption.AllDirectories)) File.Delete(temporary);
    }

    public void Dispose() => lease.Dispose();
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Records a fetched capture. A not-modified fetch must name bytes the store already holds.</summary>
    public CaptureObservation Record(CaptureObservation observation, byte[]? bytes, bool queueForShipping)
    {
        lock (gate)
        {
            Check(observation);
            if (bytes is not null)
            {
                if (bytes.LongLength != observation.Bytes || Hash(bytes) != observation.Sha256) throw new CaptureException("capture-hash-mismatch");
                WriteObject(observation.Sha256, bytes);
            }
            else if (!File.Exists(ObjectPath(observation.Sha256))) throw new CaptureException("capture-object-missing");
            Append(observation);
            if (queueForShipping) WriteFile(Path.Combine(root, "outbox", observation.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(observation, Json), immutable: true);
            return observation;
        }
    }

    /// <summary>Accepts a capture shipped from a remote collector. Replaying the same observation is a no-op.</summary>
    public bool Receive(CaptureObservation observation, string stagedFile, DateTimeOffset now)
    {
        lock (gate)
        {
            Check(observation);
            if (observation.ObservedAt > now + TimeSpan.FromMinutes(5)) throw new CaptureException("capture-from-future");
            var marker = Path.Combine(root, "received", observation.Id);
            if (File.Exists(marker))
            {
                if (File.ReadAllText(marker) != observation.Sha256) throw new CaptureException("capture-id-conflict");
                File.Delete(stagedFile);
                return false;
            }
            var info = new FileInfo(stagedFile);
            if (info.Length != observation.Bytes) throw new CaptureException("capture-hash-mismatch");
            using (var input = File.OpenRead(stagedFile))
                if (Convert.ToHexStringLower(SHA256.HashData(input)) != observation.Sha256) throw new CaptureException("capture-hash-mismatch");
            var destination = ObjectPath(observation.Sha256);
            if (File.Exists(destination)) File.Delete(stagedFile);
            else { EnsureSpace(info.Length); File.Move(stagedFile, destination); Restrict(destination); }
            Append(observation with { ReceivedAt = now });
            WriteFile(marker, Encoding.ASCII.GetBytes(observation.Sha256), immutable: true);
            return true;
        }
    }

    /// <summary>A private temporary file on the same volume, so an accepted upload is moved rather than copied.</summary>
    public string StagingPath() => Path.Combine(root, "objects", ".write-" + Guid.NewGuid().ToString("N"));

    public IReadOnlyList<CaptureObservation> Outbox()
    {
        lock (gate) return Directory.EnumerateFiles(Path.Combine(root, "outbox"), "*.json").Select(p => Read<CaptureObservation>(p)).OrderBy(o => o.ObservedAt).ToList();
    }

    /// <summary>After the receiver acknowledged it, the remote copy is no longer needed. The local log keeps the observation.</summary>
    public void Shipped(CaptureObservation observation)
    {
        lock (gate)
        {
            File.Delete(Path.Combine(root, "outbox", observation.Id + ".json"));
            var stillQueued = Outbox().Any(o => o.Sha256 == observation.Sha256);
            var current = Directory.EnumerateFiles(Path.Combine(root, "status"), "*.json").Select(p => Read<CaptureStatus>(p)).Any(s => s.LastSha256 == observation.Sha256);
            // Keep the object while a later conditional request might answer 304 against it.
            if (!stillQueued && !current) File.Delete(ObjectPath(observation.Sha256));
        }
    }

    public byte[] Bytes(string sha256)
    {
        var bytes = File.ReadAllBytes(ObjectPath(sha256));
        if (Hash(bytes) != sha256) throw new CaptureException("store-content-integrity");
        return bytes;
    }
    public bool HasObject(string sha256) => File.Exists(ObjectPath(sha256));

    public CaptureStatus Status(string sourceId)
    {
        var path = Path.Combine(root, "status", SafeId(sourceId) + ".json");
        return File.Exists(path) ? Read<CaptureStatus>(path) : new(sourceId, null, null, null, null, null, 0, null, null, 0);
    }
    public void SaveStatus(CaptureStatus status)
    {
        lock (gate) WriteFile(Path.Combine(root, "status", SafeId(status.SourceId) + ".json"), JsonSerializer.SerializeToUtf8Bytes(status, Json), immutable: false);
    }

    public IReadOnlyList<CaptureObservation> Observations(string sourceId)
    {
        var directory = Path.Combine(root, "observations", SafeId(sourceId));
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal)
            .SelectMany(File.ReadLines).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<CaptureObservation>(l, Json)!).ToList();
    }

    private void Check(CaptureObservation o)
    {
        _ = SafeId(o.SourceId);
        if (!Guid.TryParseExact(o.Id, "N", out _) || o.Sha256.Length != 64 || o.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            o.Bytes is < 0 or > MaximumCaptureBytes || o.Collector.Length is < 1 or > 64 || o.Collector.Any(char.IsControl) ||
            !Uri.TryCreate(o.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new CaptureException("capture-observation-invalid");
    }
    private static string SafeId(string id) =>
        id.Length is >= 1 and <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') ? id : throw new CaptureException("capture-source-invalid");
    private string ObjectPath(string sha256) => Path.Combine(root, "objects", sha256 + ".bin");
    private void WriteObject(string sha256, byte[] bytes)
    {
        if (File.Exists(ObjectPath(sha256))) { if (Hash(File.ReadAllBytes(ObjectPath(sha256))) != sha256) throw new CaptureException("store-content-integrity"); return; }
        EnsureSpace(bytes.LongLength);
        WriteFile(ObjectPath(sha256), bytes, immutable: true);
    }
    private void Append(CaptureObservation observation)
    {
        var directory = Path.Combine(root, "observations", observation.SourceId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, observation.ObservedAt.UtcDateTime.ToString("yyyy-MM") + ".jsonl");
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        output.Write(JsonSerializer.SerializeToUtf8Bytes(observation, Json)); output.WriteByte((byte)'\n'); output.Flush(flushToDisk: true);
        Restrict(path);
    }
    private void EnsureSpace(long adding)
    {
        var used = Directory.EnumerateFiles(Path.Combine(root, "objects"), "*.bin").Sum(f => new FileInfo(f).Length);
        if (used + adding > maximumStoreBytes) throw new CaptureException("store-size-limit");
    }
    private static T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new CaptureException("store-json-integrity"); }
        catch (JsonException) { throw new CaptureException("store-json-integrity"); }
    }
    private static void WriteFile(string destination, byte[] bytes, bool immutable)
    {
        if (immutable && File.Exists(destination)) return;
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(flushToDisk: true); }
            Restrict(temp);
            File.Move(temp, destination, overwrite: !immutable);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
