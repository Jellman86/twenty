using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

public sealed record RawReplayBuildIdentity(string Scheme, string CoreSha256, string Framework, string RuntimeIdentifier)
{
    private static readonly Lazy<RawReplayBuildIdentity> identity = new(() => new("raw-replay-core-runtime-v1",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(RawEventReplay).Assembly.Location))),
        RuntimeInformation.FrameworkDescription, RuntimeInformation.RuntimeIdentifier));
    public static RawReplayBuildIdentity Current => identity.Value;
}
public sealed record RawReplayEventSpec(string Id, int Sequence, DateTimeOffset At, string DatasetSha256, RawReplayOperation Operation);
public sealed record RawReplayDocument(string Version, string EngineVersion, RawReplayBuildIdentity Build,
    long MaximumPriceAgeTicks, long MaximumFxAgeTicks, FxPurpose FxPurpose, ImmutableArray<RawReplayEventSpec> Events)
{
    public RawReplayScheduleBinding? Schedule { get; init; }
}
public sealed record RawReplayReceipt(string Version, string RequestSha256, string RequestBase64, string ResultSha256, string ResultBase64);

/// <summary>Content-bound offline replay. Recovery verifies inputs and re-executes a fresh ledger;
/// saved balances are never trusted or injected into a ledger. No external side effects occur.</summary>
public static class RawReplayArchive
{
    public const string Version = "raw-replay-archive-v1";
    public const int MaximumRequestBytes = 2 * 1024 * 1024;
    public const int MaximumResultBytes = 16 * 1024 * 1024;
    public const int MaximumReceiptBytes = 32 * 1024 * 1024;
    public const int MaximumDatasets = 16;
    public const long MaximumDatasetBytes = 32L * 1024 * 1024;
    public const long MaximumSliceObservations = 500000;
    public const long MaximumEventObservationWork = 500000;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static RawReplayReceipt Execute(ReadOnlySpan<byte> requestBytes, Func<string, byte[]> readDataset)
    {
        ArgumentNullException.ThrowIfNull(readDataset);
        if (requestBytes.IsEmpty || requestBytes.Length > MaximumRequestBytes) throw new ResearchException("raw-replay-size");
        requestBytes = requestBytes.ToArray(); // Resolver callbacks cannot change the bytes bound below.
        var request = RawReplayJson.Read<RawReplayDocument>(requestBytes, MaximumRequestBytes);
        if (request.Version != Version || request.EngineVersion != RawEventReplay.Version)
            throw new ResearchException("raw-replay-version");
        if (request.Build != RawReplayBuildIdentity.Current) throw new ResearchException("raw-replay-build");
        if (request.MaximumPriceAgeTicks < 0 || request.MaximumFxAgeTicks < 0 || !Enum.IsDefined(request.FxPurpose) ||
            request.Events.IsDefaultOrEmpty || request.Events.Length > 10000)
            throw new ResearchException("raw-replay-input");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < request.Events.Length; i++)
        {
            var item = request.Events[i];
            if (item is null || item.Sequence != i || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 ||
                !ids.Add(item.Id) || !Hash(item.DatasetSha256) || item.Operation is null)
                throw new ResearchException("raw-replay-input");
            if (item.At == default || i > 0 && item.At < request.Events[i - 1].At) throw new ResearchException("raw-replay-time");
        }
        if (request.Events[^1].Operation is not RawReplayMark) throw new ResearchException("raw-replay-final-mark");
        if (request.Events.Select(e => e.DatasetSha256).Distinct(StringComparer.Ordinal).Count() > MaximumDatasets)
            throw new ResearchException("raw-replay-datasets");
        if (request.Schedule is { } binding) RawEventSchedule.VerifyBinding(request, binding, readDataset);
        var datasets = new Dictionary<string, PointInTimeDataset>(StringComparer.Ordinal);
        var slices = new Dictionary<(string, DateTimeOffset), DataSlice>();
        var events = new List<RawReplayItem>(); long totalBytes = 0, totalObservations = 0, observationWork = 0;
        foreach (var item in request.Events)
        {
            if (!datasets.TryGetValue(item.DatasetSha256, out var dataset))
            {
                var bytes = readDataset(item.DatasetSha256);
                if (bytes is null || bytes.Length > DatasetJson.MaximumBytes || (totalBytes += bytes.Length) > MaximumDatasetBytes)
                    throw new ResearchException("raw-replay-datasets");
                bytes = bytes.ToArray();
                if (Digest(bytes) != item.DatasetSha256) throw new ResearchException("raw-replay-dataset-hash");
                dataset = new(RawReplayJson.Read<DatasetFile>(bytes, DatasetJson.MaximumBytes));
                datasets.Add(item.DatasetSha256, dataset);
            }
            var key = (item.DatasetSha256, item.At);
            if (!slices.TryGetValue(key, out var market))
            {
                market = dataset.AsOf(item.At);
                if ((totalObservations += market.Observations.Length) > MaximumSliceObservations)
                    throw new ResearchException("raw-replay-slices");
                slices.Add(key, market);
            }
            // Count repeated slices too: cached inputs must not permit an unbounded
            // number of position/claim marks to be materialized before output limits.
            if ((observationWork += market.Observations.Length) > MaximumEventObservationWork)
                throw new ResearchException("raw-replay-work");
            events.Add(new(item.Id, item.Sequence, market, item.Operation));
        }
        var result = RawEventReplay.Run(events, TimeSpan.FromTicks(request.MaximumPriceAgeTicks),
            TimeSpan.FromTicks(request.MaximumFxAgeTicks), request.FxPurpose);
        var resultBytes = RawReplayJson.Write(result, MaximumResultBytes);
        return new(Version, Digest(requestBytes), Convert.ToBase64String(requestBytes), Digest(resultBytes), Convert.ToBase64String(resultBytes));
    }

    public static RawReplayReceipt Verify(ReadOnlySpan<byte> receiptBytes, Func<string, byte[]> readDataset)
    {
        var receipt = RawReplayJson.Read<RawReplayReceipt>(receiptBytes, MaximumReceiptBytes);
        if (receipt.Version != Version) throw new ResearchException("raw-replay-version");
        var request = Decode(receipt.RequestBase64, MaximumRequestBytes);
        var result = Decode(receipt.ResultBase64, MaximumResultBytes);
        if (!Hash(receipt.RequestSha256) || !Hash(receipt.ResultSha256) ||
            Digest(request) != receipt.RequestSha256 || Digest(result) != receipt.ResultSha256)
            throw new ResearchException("raw-replay-receipt-hash");
        var replay = Execute(request, readDataset);
        if (replay.ResultBase64 != receipt.ResultBase64) throw new ResearchException("raw-replay-result-mismatch");
        return replay;
    }
    private static byte[] Decode(string text, int maximumBytes)
    {
        if (text is null || text.Length > 4L * ((maximumBytes + 2L) / 3)) throw new ResearchException("raw-replay-size");
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length > maximumBytes) throw new ResearchException("raw-replay-size");
            return bytes;
        }
        catch (FormatException) { throw new ResearchException("raw-replay-encoding"); }
    }
    public static string Store(string root, ReadOnlySpan<byte> requestBytes, Func<string, byte[]> readDataset)
    {
        var receipt = Execute(requestBytes, readDataset);
        var bytes = RawReplayJson.Write(receipt, MaximumReceiptBytes);
        var directory = Path.Combine(Path.GetFullPath(root), "raw-replays", Version);
        var path = Path.Combine(directory, receipt.RequestSha256 + ".json");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, ".replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                file.Write(bytes); file.Flush(true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                if (!ReadBounded(path, MaximumReceiptBytes).AsSpan().SequenceEqual(bytes))
                    throw new ResearchException("raw-replay-existing-mismatch");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    public static RawReplayReceipt Load(string root, string requestSha256, Func<string, byte[]> readDataset)
    {
        if (!Hash(requestSha256)) throw new ResearchException("raw-replay-input");
        var bytes = ReadBounded(Path.Combine(Path.GetFullPath(root), "raw-replays", Version, requestSha256 + ".json"), MaximumReceiptBytes);
        var receipt = Verify(bytes, readDataset);
        if (receipt.RequestSha256 != requestSha256) throw new ResearchException("raw-replay-receipt-hash");
        return receipt;
    }
    public static byte[] ReadRequest(string path) => ReadBounded(path, MaximumRequestBytes);
    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var file = File.OpenRead(path); using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (buffer.Length <= maximumBytes)
        {
            var count = file.Read(block, 0, (int)Math.Min(block.Length, maximumBytes + 1L - buffer.Length));
            if (count == 0) break;
            buffer.Write(block, 0, count);
        }
        if (buffer.Length > maximumBytes) throw new ResearchException("raw-replay-size");
        return buffer.ToArray();
    }
}
