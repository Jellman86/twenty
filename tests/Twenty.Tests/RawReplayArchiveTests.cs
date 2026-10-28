using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed partial class RawGbpLedgerTests
{
    private static (byte[] Request, byte[] Dataset, string Digest) DurableReplay()
    {
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var dataset = JsonSerializer.SerializeToUtf8Bytes(fixture with { Observations = Market(BuyAt).Observations }, DatasetJson.Options);
        var digest = Convert.ToHexStringLower(SHA256.HashData(dataset));
        var document = new RawReplayDocument(RawReplayArchive.Version, RawEventReplay.Version, RawReplayBuildIdentity.Current,
            Age.Ticks, Age.Ticks, FxPurpose.Executable,
            [new("cash", 0, BuyAt, digest, new RawReplayDeposit(300)),
             new("buy", 1, BuyAt, digest, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
             new("closing", 2, BuyAt, digest, new RawReplayMark([], []))]);
        return (JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options), dataset, digest);
    }

    [Fact]
    public void DurableReplayBindsExactRequestAndDatasetBytesAndReexecutesTheResult()
    {
        var (request, dataset, digest) = DurableReplay(); var reads = 0;
        byte[] Read(string hash) { Assert.Equal(digest, hash); reads++; return dataset; }
        var receipt = RawReplayArchive.Execute(request, Read);
        Assert.Equal(1, reads); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(request)), receipt.RequestSha256);
        var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(receipt.ResultBase64), RawReplayJson.Options)!;
        Assert.Equal(140, result.CashGbp); Assert.Equal(300, result.Marks[0].Valuation.ExpectedEquityGbp);
        var stored = JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options);
        Assert.Equal(receipt, RawReplayArchive.Verify(stored, Read)); Assert.Equal(2, reads);
        Assert.Equal(receipt, RawReplayArchive.Execute(request, Read));
    }

    [Fact]
    public void DurableReplayRestartsFromAnImmutableReceiptWithoutDuplicatingCashOrFills()
    {
        var (request, dataset, _) = DurableReplay();
        var root = Path.Combine(Path.GetTempPath(), "twenty-raw-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = RawReplayArchive.Store(root, request, _ => dataset);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(path, RawReplayArchive.Store(root, request, _ => dataset));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            var loaded = RawReplayArchive.Load(root, Path.GetFileNameWithoutExtension(path), _ => dataset);
            var result = JsonSerializer.Deserialize<RawEventReplayResult>(Convert.FromBase64String(loaded.ResultBase64), RawReplayJson.Options)!;
            Assert.Equal(2, result.Entries.Length); Assert.Equal(300, result.ContributionsGbp); Assert.Equal(140, result.CashGbp);
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void DurableReplaySnapshotsRequestBeforeCallingAnExternalDatasetResolver()
    {
        var (request, dataset, _) = DurableReplay(); var original = request.ToArray();
        var receipt = RawReplayArchive.Execute(request, _ => { request[0] = (byte)' '; return dataset; });
        Assert.Equal(original, Convert.FromBase64String(receipt.RequestBase64));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), receipt.RequestSha256);
    }

    [Theory]
    [InlineData("version", "raw-replay-version")]
    [InlineData("engine", "raw-replay-version")]
    [InlineData("code", "raw-replay-build")]
    [InlineData("runtime", "raw-replay-build")]
    [InlineData("scheme", "raw-replay-build")]
    [InlineData("price-age", "raw-replay-input")]
    [InlineData("fx-age", "raw-replay-input")]
    [InlineData("events", "raw-replay-input")]
    [InlineData("sequence", "raw-replay-input")]
    [InlineData("duplicate", "raw-replay-input")]
    [InlineData("path", "raw-replay-input")]
    [InlineData("time", "raw-replay-time")]
    [InlineData("final-mark", "raw-replay-final-mark")]
    public void DurableReplayRejectsChangedContractsBeforeResolvingInputs(string kind, string expected)
    {
        var (bytes, _, _) = DurableReplay(); var document = JsonSerializer.Deserialize<RawReplayDocument>(bytes, RawReplayJson.Options)!;
        var first = document.Events[0];
        document = kind switch
        {
            "version" => document with { Version = "other" },
            "engine" => document with { EngineVersion = "other" },
            "code" => document with { Build = document.Build with { CoreSha256 = new string('0', 64) } },
            "runtime" => document with { Build = document.Build with { Framework = "other" } },
            "scheme" => document with { Build = document.Build with { Scheme = "other" } },
            "price-age" => document with { MaximumPriceAgeTicks = -1 },
            "fx-age" => document with { MaximumFxAgeTicks = -1 },
            "events" => document with { Events = [] },
            "sequence" => document with { Events = document.Events.SetItem(0, first with { Sequence = 1 }) },
            "duplicate" => document with { Events = document.Events.SetItem(1, document.Events[1] with { Id = first.Id }) },
            "path" => document with { Events = document.Events.SetItem(0, first with { DatasetSha256 = "../outside" }) },
            "time" => document with { Events = document.Events.SetItem(0, first with { At = BuyAt.AddHours(1) }) },
            _ => document with { Events = document.Events.SetItem(2, document.Events[2] with { Operation = new RawReplayDeposit(1) }) }
        };
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options), _ => throw new InvalidOperationException("Resolver must not run"))).Code);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("missing")]
    [InlineData("unknown-kind")]
    [InlineData("enum-number")]
    [InlineData("offset")]
    [InlineData("null-operation")]
    public void DurableWireRejectsAmbiguousOrIncompleteJson(string kind)
    {
        var (request, _, _) = DurableReplay(); var text = System.Text.Encoding.UTF8.GetString(request);
        text = kind switch
        {
            "unknown" => text.Replace("\"version\":", "\"extra\":true,\"version\":"),
            "duplicate" => text.Replace("\"engineVersion\":", "\"version\":\"other\",\"engineVersion\":"),
            "nested-duplicate" => text.Replace("\"amountGbp\": 300", "\"amountGbp\": 300,\"amountGbp\": 1"),
            "missing" => text.Replace("\"maximumFxAgeTicks\"", "\"absentFxAge\""),
            "unknown-kind" => text.Replace("\"deposit\"", "\"executeAnything\""),
            "enum-number" => text.Replace("\"fxPurpose\": \"executable\"", "\"fxPurpose\": 2"),
            "offset" => text.Replace("2024-01-02T17:00:00+00:00", "2024-01-02T17:00:00"),
            _ => text.Replace("\"operation\": {", "\"operation\": null, \"ignoredOperation\": {")
        };
        Assert.NotEqual(System.Text.Encoding.UTF8.GetString(request), text);
        Assert.Throws<JsonException>(() => RawReplayArchive.Execute(System.Text.Encoding.UTF8.GetBytes(text), _ => throw new InvalidOperationException("Resolver must not run")));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("null")]
    [InlineData("oversized")]
    public void DurableReplayVerifiesDatasetBytesBeforeUse(string kind)
    {
        var (request, dataset, _) = DurableReplay();
        var changed = kind == "null" ? null! : kind == "oversized" ? new byte[DatasetJson.MaximumBytes + 1] : dataset.Concat(new byte[] { 32 }).ToArray();
        Assert.Equal(kind == "changed" ? "raw-replay-dataset-hash" : "raw-replay-datasets", Assert.Throws<ResearchException>(() =>
            RawReplayArchive.Execute(request, _ => changed)).Code);
    }

    [Theory]
    [InlineData("request-hash", "raw-replay-receipt-hash")]
    [InlineData("result-hash", "raw-replay-receipt-hash")]
    [InlineData("forged-result", "raw-replay-result-mismatch")]
    [InlineData("base64", "raw-replay-encoding")]
    [InlineData("version", "raw-replay-version")]
    public void RecoveryRefusesCorruptionEvenWhenAChangedResultHasANewMatchingHash(string kind, string expected)
    {
        var (request, dataset, _) = DurableReplay(); var receipt = RawReplayArchive.Execute(request, _ => dataset);
        var forged = System.Text.Encoding.UTF8.GetBytes("{\"cashGbp\":999999}");
        receipt = kind switch
        {
            "request-hash" => receipt with { RequestSha256 = new string('0', 64) },
            "result-hash" => receipt with { ResultSha256 = new string('0', 64) },
            "forged-result" => receipt with { ResultBase64 = Convert.ToBase64String(forged), ResultSha256 = Convert.ToHexStringLower(SHA256.HashData(forged)) },
            "base64" => receipt with { RequestBase64 = "!" },
            _ => receipt with { Version = "other" }
        };
        Assert.Equal(expected, Assert.Throws<ResearchException>(() => RawReplayArchive.Verify(
            JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options), _ => dataset)).Code);
    }

    [Fact]
    public void InvalidReplayCreatesNoArchiveAndAConflictingReceiptIsNeverOverwritten()
    {
        var (request, dataset, _) = DurableReplay(); var root = Path.Combine(Path.GetTempPath(), "twenty-raw-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<ResearchException>(() => RawReplayArchive.Store(root, request, _ => [])); Assert.False(Directory.Exists(root));
            var path = RawReplayArchive.Store(root, request, _ => dataset);
            File.WriteAllText(path, "broken"); var broken = File.ReadAllBytes(path);
            Assert.Equal("raw-replay-existing-mismatch", Assert.Throws<ResearchException>(() => RawReplayArchive.Store(root, request, _ => dataset)).Code);
            Assert.Equal(broken, File.ReadAllBytes(path)); Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
            Assert.ThrowsAny<JsonException>(() => RawReplayArchive.Load(root, Path.GetFileNameWithoutExtension(path), _ => dataset));
            Assert.Equal("raw-replay-input", Assert.Throws<ResearchException>(() => RawReplayArchive.Load(root, "../file", _ => dataset)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DurableReplayEnforcesRequestAndDatasetCountBoundsBeforeResolvingInputs()
    {
        Assert.Equal("raw-replay-size", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(new byte[RawReplayArchive.MaximumRequestBytes + 1], _ => throw new InvalidOperationException())).Code);
        var (bytes, _, _) = DurableReplay(); var document = JsonSerializer.Deserialize<RawReplayDocument>(bytes, RawReplayJson.Options)!;
        document = document with { Events = Enumerable.Range(0, 17).Select(i => new RawReplayEventSpec("event-" + i, i, BuyAt,
            Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            i == 16 ? new RawReplayMark([], []) : new RawReplayDeposit(1))).ToImmutableArray() };
        Assert.Equal("raw-replay-datasets", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options), _ => throw new InvalidOperationException())).Code);
    }

    [Fact]
    public async Task ConcurrentIdenticalImportsConvergeOnOneCompleteReceipt()
    {
        var (request, dataset, _) = DurableReplay(); var root = Path.Combine(Path.GetTempPath(), "twenty-raw-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RawReplayArchive.Store(root, request, _ => dataset))));
            Assert.Single(paths.Distinct()); Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(paths[0])!));
            Assert.Equal(RawReplayArchive.Execute(request, _ => dataset), RawReplayArchive.Load(root, Path.GetFileNameWithoutExtension(paths[0]), _ => dataset));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(TerminalOutcome.Cash)]
    [InlineData(TerminalOutcome.Stock)]
    public void DurableReplayPreservesDividendTerminalAndSplitEvidenceAcrossRestart(TerminalOutcome outcome)
    {
        var dividend = Dividend(); var terminal = Terminal(outcome); var paid = TerminalAt.AddDays(1); var after = paid.AddDays(1);
        var split = Split() with { ListingId = "successor", EffectiveAt = after, Evidence = Known(after) };
        var initial = Market(BuyAt); var ex = Market(dividend.EffectiveAt, actions: [dividend]);
        var exit = TerminalMarket(TerminalAt, terminal, ended: true, extra: [dividend]);
        var payment = TerminalMarket(paid, terminal, ended: true, extra: [dividend]);
        var events = new List<RawReplayItem>
        {
            new("opening", 0, initial, new RawReplayDeposit(300)),
            new("buy", 1, initial, new RawReplayAssumedFill("listing-a", 2, 100, PriceBasis.Raw, 0)),
            new("dividend", 2, ex, new RawReplayDividendCapture("listing-a", dividend.ActionId, EntitlementEvidence(dividend))),
            new("exit", 3, exit, new RawReplayTerminalCapture("listing-a", terminal.ActionId, TerminalProof(terminal))),
            new("pending", 4, exit, new RawReplayMark([DividendMark(dividend)], [TerminalMark(terminal, outcome == TerminalOutcome.Cash ? 20 : 0)])),
            new("settle", 5, payment, new RawReplayTerminalSettlement("listing-a", terminal.ActionId, outcome == TerminalOutcome.Cash ? CashSettlement(paid) : StockSettlement(paid))),
            new("pay", 6, payment, new RawReplayDividendPayment("listing-a", dividend.ActionId, Payment(paid)))
        };
        var closing = payment;
        if (outcome == TerminalOutcome.Stock)
        {
            closing = TerminalMarket(after.AddMinutes(3), terminal, ended: true, successorPrice: 40, extra: [dividend, split], priceCloseAt: after);
            events.Add(new("split", events.Count, closing, new RawReplaySplit("successor", split.ActionId)));
        }
        events.Add(new("closing", events.Count, closing, new RawReplayMark([], [])));
        var fixture = DatasetJson.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "point-in-time.v1.json")));
        var datasets = new Dictionary<string, byte[]>();
        var specs = events.Select(e =>
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(fixture with { Observations = e.Market.Observations }, DatasetJson.Options);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); datasets[hash] = bytes;
            return new RawReplayEventSpec(e.Id, e.Sequence, e.Market.DecisionAt, hash, e.Operation);
        }).ToImmutableArray();
        var document = new RawReplayDocument(RawReplayArchive.Version, RawEventReplay.Version, RawReplayBuildIdentity.Current, Age.Ticks, Age.Ticks, FxPurpose.Executable, specs);
        var receipt = RawReplayArchive.Execute(JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options), h => datasets[h]);
        var reference = RawEventReplay.Run(events, Age, Age, FxPurpose.Executable);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(reference, RawReplayJson.Options), Convert.FromBase64String(receipt.ResultBase64));
        Assert.Equal(outcome == TerminalOutcome.Cash ? 291.55m : 321.55m, reference.Marks[^1].Valuation.ExpectedEquityGbp);
        Assert.Equal(receipt, RawReplayArchive.Verify(JsonSerializer.SerializeToUtf8Bytes(receipt, RawReplayJson.Options), h => datasets[h]));
    }

    [Fact]
    public void DurableReplayCapsAggregateDatasetBytes()
    {
        var (request, dataset, _) = DurableReplay(); var document = JsonSerializer.Deserialize<RawReplayDocument>(request, RawReplayJson.Options)!;
        var inputs = new Dictionary<string, byte[]>(); var events = ImmutableArray.CreateBuilder<RawReplayEventSpec>();
        for (var i = 0; i < 5; i++)
        {
            var bytes = new byte[DatasetJson.MaximumBytes - i]; bytes.AsSpan().Fill(32); dataset.CopyTo(bytes, 0);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); inputs[hash] = bytes;
            events.Add(new("mark-" + i, i, BuyAt, hash, new RawReplayMark([], [])));
        }
        document = document with { Events = events.ToImmutable() };
        Assert.Equal("raw-replay-datasets", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options), h => inputs[h])).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableReplayCapsCachedSliceObservationsAndRepeatedWork(bool sameTime)
    {
        var (request, original, _) = DurableReplay(); var dataset = DatasetJson.Read(original);
        var action = Split() with { SplitRatio = 1, EffectiveAt = BuyAt.AddMinutes(-1), Evidence = Known(BuyAt.AddMinutes(-1)) };
        dataset = dataset with { Observations = dataset.Observations.AddRange(Enumerable.Range(0, 200).Select(i =>
            action with { Id = "neutral-" + i, ActionId = "neutral-" + i })) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dataset, DatasetJson.Options); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var document = JsonSerializer.Deserialize<RawReplayDocument>(request, RawReplayJson.Options)!;
        document = document with { Events = Enumerable.Range(0, 3000).Select(i =>
            new RawReplayEventSpec("mark-" + i, i, BuyAt.AddTicks(sameTime ? 0 : i), hash, new RawReplayMark([], []))).ToImmutableArray() };
        Assert.Equal(sameTime ? "raw-replay-work" : "raw-replay-slices", Assert.Throws<ResearchException>(() => RawReplayArchive.Execute(
            JsonSerializer.SerializeToUtf8Bytes(document, RawReplayJson.Options), _ => bytes)).Code);
    }

    [Fact]
    public void InterruptedUnpublishedStageCannotBecomeACompletedReplay()
    {
        var (request, dataset, _) = DurableReplay(); var root = Path.Combine(Path.GetTempPath(), "twenty-raw-interrupted-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "raw-replays", RawReplayArchive.Version); Directory.CreateDirectory(directory);
        var orphan = Path.Combine(directory, ".replay-interrupted"); File.WriteAllText(orphan, "partial output");
        var hash = Convert.ToHexStringLower(SHA256.HashData(request));
        try
        {
            Assert.Throws<FileNotFoundException>(() => RawReplayArchive.Load(root, hash, _ => dataset));
            var path = RawReplayArchive.Store(root, request, _ => dataset);
            Assert.Equal("partial output", File.ReadAllText(orphan));
            Assert.Equal(RawReplayArchive.Execute(request, _ => dataset), RawReplayArchive.Load(root, hash, _ => dataset));
            Assert.Equal(hash, Path.GetFileNameWithoutExtension(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExactRequestBytesAreTheArtifactIdentityAndAddressSwapsAreRefused()
    {
        var (request, dataset, _) = DurableReplay(); var root = Path.Combine(Path.GetTempPath(), "twenty-raw-address-" + Guid.NewGuid().ToString("N"));
        var changed = request.Concat(new byte[] { 32 }).ToArray();
        try
        {
            var first = RawReplayArchive.Store(root, request, _ => dataset);
            var second = RawReplayArchive.Store(root, changed, _ => dataset);
            Assert.NotEqual(first, second);
            File.Copy(second, first, overwrite: true);
            Assert.Equal("raw-replay-receipt-hash", Assert.Throws<ResearchException>(() =>
                RawReplayArchive.Load(root, Path.GetFileNameWithoutExtension(first), _ => dataset)).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
