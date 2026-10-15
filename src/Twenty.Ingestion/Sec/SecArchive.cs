using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twenty.Core.Data;

namespace Twenty.Ingestion.Sec;

/// <summary>Private originals and cache metadata. One exclusive writer per archive root.</summary>
public sealed class SecArchive : IDisposable
{
    private readonly string root;
    private readonly SecLimits limits;
    private readonly FileStream lease;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 32
    };
    public SecArchive(string directory, SecLimits limits)
    {
        limits.Validate(); this.limits = limits;
        root = Path.Combine(Path.GetFullPath(directory), "sec-v1");
        Directory.CreateDirectory(root);
        // Restrict a newly selected private archive; refuse concurrent runs rather than split the rate budget.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        lease = new FileStream(Path.Combine(root, ".collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(root, ".collector.lock"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        foreach (var part in new[] { "objects", "receipts", "cache", "catalogues", "quarantine" })
            Directory.CreateDirectory(Path.Combine(root, part));
        foreach (var temporary in Directory.EnumerateFiles(root, ".write-*", SearchOption.AllDirectories)) File.Delete(temporary);
    }
    public void Dispose() => lease.Dispose();
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string UrlKey(Uri uri) => Hash(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
    public SecCacheEntry? Cached(Uri uri)
    {
        SecIdentity.AllowedUri(uri);
        var path = Path.Combine(root, "cache", UrlKey(uri) + ".json");
        if (!File.Exists(path)) return null;
        var entry = Read<SecCacheEntry>(path);
        if (entry.Receipt.Url != uri.AbsoluteUri || entry.Receipt.FirstSeenAt == default ||
            entry.CheckedAt < entry.Receipt.FirstSeenAt) throw new SecCollectionException("archive-cache-integrity");
        _ = Bytes(entry.Receipt);
        var originalReceipt = Read<SecReceipt>(Path.Combine(root, "receipts", UrlKey(uri) + "-" + entry.Receipt.ContentSha256 + ".json"));
        if (originalReceipt != entry.Receipt) throw new SecCollectionException("archive-receipt-integrity");
        return entry;
    }
    public byte[] Bytes(SecReceipt receipt)
    {
        if (receipt.ContentSha256.Length != 64 || receipt.ContentSha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new SecCollectionException("archive-digest");
        var bytes = DatasetArchive.ReadBounded(Path.Combine(root, "objects", receipt.ContentSha256 + ".bin"));
        if (Hash(bytes) != receipt.ContentSha256) throw new SecCollectionException("archive-content-integrity");
        return bytes;
    }
    public SecReceipt Store(Uri uri, byte[] bytes, string contentType, string? etag, DateTimeOffset? modified, DateTimeOffset now)
    {
        SecIdentity.AllowedUri(uri);
        if (bytes.Length > limits.MaximumResponseBytes) throw new SecCollectionException("response-size-limit");
        var hash = Hash(bytes);
        var receiptPath = Path.Combine(root, "receipts", UrlKey(uri) + "-" + hash + ".json");
        SecReceipt receipt;
        if (File.Exists(receiptPath))
        {
            receipt = Read<SecReceipt>(receiptPath);
            if (receipt.Url != uri.AbsoluteUri || receipt.ContentSha256 != hash || receipt.FirstSeenAt > now)
                throw new SecCollectionException("archive-receipt-integrity");
            if (!Bytes(receipt).AsSpan().SequenceEqual(bytes)) throw new SecCollectionException("archive-content-integrity");
        }
        else
        {
            receipt = new(uri.AbsoluteUri, hash, now, contentType, SafeETag(etag), modified, SecParser.Version);
            Write(Path.Combine(root, "objects", hash + ".bin"), bytes, immutable: true);
            Write(receiptPath, JsonSerializer.SerializeToUtf8Bytes(receipt, Json), immutable: true);
        }
        Checked(uri, receipt, now);
        return receipt;
    }
    public void Checked(Uri uri, SecReceipt receipt, DateTimeOffset now)
    {
        if (receipt.Url != uri.AbsoluteUri || receipt.FirstSeenAt > now) throw new SecCollectionException("archive-clock");
        Write(Path.Combine(root, "cache", UrlKey(uri) + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(new SecCacheEntry(receipt, now), Json), immutable: false);
    }
    public string SaveCatalogue(SecCollectionResult result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        var path = Path.Combine(root, "catalogues", Hash(bytes) + ".json");
        Write(path, bytes, immutable: true);
        return path;
    }
    public void Quarantine(Uri uri, byte[] bytes, string code, DateTimeOffset now)
    {
        // One bounded file contains both safe metadata and the original response bytes (base64 JSON).
        var contents = JsonSerializer.SerializeToUtf8Bytes(new { url = uri.AbsoluteUri, code,
            observedAt = now, sha256 = Hash(bytes), original = bytes }, Json);
        var directory = Path.Combine(root, "quarantine");
        if (contents.LongLength > limits.MaximumQuarantineBytes) throw new SecCollectionException("quarantine-size-limit");
        var old = Directory.GetFiles(directory, "*.json").Select(x => new FileInfo(x)).OrderBy(x => x.LastWriteTimeUtc).ToList();
        while (old.Count >= limits.MaximumQuarantineEntries || old.Sum(x => x.Length) + contents.Length > limits.MaximumQuarantineBytes)
        {
            if (old.Count == 0) break;
            old[0].Delete(); old.RemoveAt(0);
        }
        Write(Path.Combine(directory, now.UtcTicks + "-" + Guid.NewGuid().ToString("N") + ".json"), contents, immutable: true);
    }
    private T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(DatasetArchive.ReadBounded(path), Json) ?? throw new SecCollectionException("archive-json-integrity"); }
        catch (JsonException) { throw new SecCollectionException("archive-json-integrity"); }
    }
    private void Write(string destination, byte[] bytes, bool immutable)
    {
        if (immutable && File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            if (existing.Length != bytes.Length || !SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new SecCollectionException("archive-content-integrity");
            return;
        }
        var existingLength = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) + bytes.Length - existingLength > limits.MaximumArchiveBytes)
            throw new SecCollectionException("archive-size-limit");
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, ".write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                output.Write(bytes); output.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: !immutable);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string? SafeETag(string? value) => value is { Length: <= 256 } && !value.Any(char.IsControl) ? value : null;
}
