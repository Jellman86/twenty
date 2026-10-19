using System.Globalization;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Sec;
using Twenty.Ingestion.Fx;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Listings;
using Twenty.Ingestion.Returns;
using Twenty.Ingestion.Tiingo;

try
{
    if ((args.Length == 9 || args.Length == 11 && args[9] == "--portfolios") && args[0] == "french-panel" && args[1] == "--archive" && args[3] == "--from" && args[5] == "--to" && args[7] == "--output")
    {
        if (!DateOnly.TryParseExact(args[4], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(args[6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) throw new Twenty.Core.Research.ResearchException("return-panel-window");
        var set = args.Length == 9 || args[10] == "industry" ? FrenchPortfolioSet.Industry10
            : args[10] == "size-value25" ? FrenchPortfolioSet.SizeValue25
            : throw new Twenty.Core.Research.ResearchException("french-portfolio-set");
        using var archive = new CaptureStore(args[2]);
        var files = FrenchReturnPanel.Build(archive, from, to, set);
        var directory = Path.GetFullPath(args[8]); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (suffix, outputBytes) in new[] { (".csv", files.Csv), (".metadata.json", files.Metadata), (".audit.json", files.Audit) })
        {
            var path = Path.Combine(directory, files.Id + suffix);
            if (File.Exists(path)) { if (!File.ReadAllBytes(path).SequenceEqual(outputBytes)) throw new IOException(); continue; }
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(outputBytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, panelId = files.Id, researchOnly = true, converter = FrenchReturnPanel.VersionFor(set) }));
        return 0;
    }
    if ((args.Length == 5 || args.Length == 7 && args[5] == "--sources") && args[0] == "listing-intervals" && args[1] == "--archive" && args[3] == "--output")
    {
        var sourceIds = args.Length == 5 ? ListingIntervals.SupportedSources
            : System.Collections.Immutable.ImmutableArray.CreateRange(args[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        using var archive = new CaptureStore(args[2]);
        var files = ListingIntervals.Build(archive, sourceIds);
        var directory = Path.GetFullPath(args[4]); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (suffix, outputBytes) in new[] { (".csv", files.Csv), (".audit.json", files.Audit) })
        {
            var path = Path.Combine(directory, files.Id + suffix);
            if (File.Exists(path)) { if (!File.ReadAllBytes(path).SequenceEqual(outputBytes)) throw new IOException(); continue; }
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(outputBytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, intervalId = files.Id, intervals = files.Intervals, converter = ListingIntervals.Version, sources = sourceIds }));
        return 0;
    }
    if (args.Length == 9 && args[0] == "listing-audit" && args[1] == "--intervals" && args[3] == "--archive" && args[5] == "--symbols" && args[7] == "--output")
    {
        var intervalsPath = Path.GetFullPath(args[2]);
        var info = new FileInfo(intervalsPath);
        if (!info.Exists || info.Length is 0 or > 64L * 1024 * 1024) throw new Twenty.Core.Research.ResearchException("listing-audit-interval-size");
        var intervalBytes = File.ReadAllBytes(intervalsPath);
        var intervalId = Path.GetFileNameWithoutExtension(intervalsPath);
        var symbols = System.Collections.Immutable.ImmutableArray.CreateRange(args[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        using var archive = new CaptureStore(args[4]);
        var files = ListingAudit.Build(intervalBytes, intervalId, archive, symbols);
        var directory = Path.GetFullPath(args[8]); Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (suffix, outputBytes) in new[] { (".csv", files.Csv), (".audit.json", files.Audit) })
        {
            var path = Path.Combine(directory, files.Id + suffix);
            if (File.Exists(path)) { if (!File.ReadAllBytes(path).SequenceEqual(outputBytes)) throw new IOException(); continue; }
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(outputBytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, auditId = files.Id, intervalId, symbols = files.Subjects.Length, converter = ListingAudit.Version,
            subjects = files.Subjects.Select(s => new { s.Symbol, s.TerminalClass, s.Findings }) }));
        return 0;
    }
    if ((args.Length == 11 || args.Length == 13) && args[0] == "sec-facts" && args[1] == "--dataset" && args[3] == "--bulk" &&
        args[5] == "--retrieved-at" && args[7] == "--concepts" && args[9] == "--archive" && (args.Length == 11 || args[11] == "--dataset-id"))
    {
        if (!DateTimeOffset.TryParse(args[6], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var retrievedAt) ||
            !(args[6].EndsWith('Z') || args[6].Contains('+') || args[6].LastIndexOf('-') > 10))
            throw new Twenty.Ingestion.Sec.SecCollectionException("sec-facts-retrieved-at");
        var input = DatasetJson.Read(DatasetArchive.ReadBounded(Path.GetFullPath(args[2])));
        string bulkSha;
        using (var hashStream = File.OpenRead(Path.GetFullPath(args[4])))
            bulkSha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(hashStream));
        var concepts = System.Collections.Immutable.ImmutableArray.CreateRange(
            args[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        using var bulk = File.OpenRead(Path.GetFullPath(args[4]));
        var result = SecCompanyFacts.Convert(input, bulk, bulkSha, new(concepts, ["us-gaap", "dei"], retrievedAt.ToUniversalTime()));
        var dataset = args.Length == 13
            ? result.Dataset with { Manifest = result.Dataset.Manifest with { DatasetId = args[12] } }
            : result.Dataset;
        var mergedBytes = JsonSerializer.SerializeToUtf8Bytes(dataset, DatasetJson.Options);
        var storedPath = DatasetArchive.Store(Path.GetFullPath(args[10]), mergedBytes);
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, datasetId = dataset.Manifest.DatasetId, companies = result.Companies,
            factsAdded = result.FactsAdded, factsSkippedExisting = result.FactsSkippedExisting, bulkSha256 = result.BulkSha256,
            converter = SecCompanyFacts.ParserVersion, stored = storedPath }));
        return 0;
    }
    if (args.Length == 3 && args[0] == "tiingo-check" && args[1] == "--archive" ||
        args.Length == 9 && args[0] == "tiingo-history" && args[2] == "--from" && args[4] == "--to" && args[6] == "--archive" && args[8] == "--capture")
    {
        var token = TiingoClient.ReadToken(Environment.GetEnvironmentVariable("TWENTY_TIINGO_KEY"), Environment.GetEnvironmentVariable("TWENTY_TIINGO_KEY_FILE")) ?? throw new Twenty.Core.Research.ResearchException("tiingo-key-missing");
        using var store = new CaptureStore(args[0] == "tiingo-check" ? args[2] : args[7]);
        using var http = CaptureCollector.CreateHttpClient(); var client = new TiingoClient(http, store, token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        if (args[0] == "tiingo-check") Console.WriteLine(JsonSerializer.Serialize(new { authenticated = await client.Check(deadline.Token) }));
        else
        {
            if (!DateOnly.TryParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !DateOnly.TryParseExact(args[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) throw new Twenty.Core.Research.ResearchException("tiingo-history-request");
            Console.WriteLine(JsonSerializer.Serialize(await client.Capture(new(args[1], from, to), deadline.Token), DatasetJson.Options));
        }
        return 0;
    }
    if (args.Length == 5 && args[0] == "capture-public" && args[1] == "--archive" && args[3] == "--sources")
    {
        var sources = CaptureSources.For(CaptureRole.Local, args[4]);
        using var archive = new CaptureStore(args[2]);
        using var http = CaptureCollector.CreateHttpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await new CaptureCollector(http, archive, CaptureRole.Local, new() { Collector = "offline-research" }).RunDue(sources, deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, DatasetJson.Options));
        return result.Any(r => r.Result is not ("captured" or "unchanged" or "not-due")) ? 1 : 0;
    }
    if (args.Length == 8 && args[0] == "ecb-inspect" && args[2] == "--date" && args[4] == "--base" && args[6] == "--quote")
    {
        if (!DateOnly.TryParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new EcbDataException("invalid-reference-date");
        var book = EcbReferenceRates.Parse(DatasetArchive.ReadBounded(args[1]));
        var quote = book.CrossRate(date, args[5], args[7]);
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, rates = book.Rates.Length,
            quote, parserVersion = EcbReferenceRates.ParserVersion }, DatasetJson.Options));
        return 0;
    }
    if (args.Length == 3 && args[0] == "sec-inspect")
    {
        var page = SecParser.Parse(DatasetArchive.ReadBounded(args[2]), args[1], history: false, DateTimeOffset.UtcNow);
        Console.WriteLine(JsonSerializer.Serialize(new { valid = true, filings = page.Filings.Length,
            historyFiles = page.History.Length, parserVersion = SecParser.Version }));
        return 0;
    }
    if (args.Length == 8 && args[0] == "sec-collect" && args[2] == "--from" && args[4] == "--to" && args[6] == "--archive")
    {
        var contact = Environment.GetEnvironmentVariable("TWENTY_SEC_CONTACT") ?? "";
        _ = SecIdentity.UserAgent(contact); // Fail before opening files or making requests.
        if (!DateOnly.TryParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(args[5], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
            throw new SecCollectionException("invalid-date-window");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var limits = new SecLimits();
            using var archive = new SecArchive(args[7], limits);
            using var http = SecClient.CreateHttpClient();
            var client = new SecClient(http, archive, contact, limits);
            var result = await new SecCollector(client, archive, limits).Collect(args[1], from, to, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { valid = true, cik = result.Cik, filings = result.Filings.Length,
                requests = result.Requests, historyFilesVisited = result.HistoryFilesVisited,
                historyLimitReached = result.HistoryLimitReached, documentLimitReached = result.DocumentLimitReached,
                completedAt = result.CompletedAt, parserVersion = result.ParserVersion }));
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    if (args.Length is 7 or 9 or 11 && args[0] == "universe-backtest" && args[1] == "--dataset" && args[3] == "--universe" && args[5] == "--decisions" &&
        (args.Length < 9 || args[7] == "--max-price-age-days") && (args.Length < 11 || args[9] == "--top-k"))
    {
        var maxAgeDays = 45; var topK = 3;
        if ((args.Length >= 9 && !int.TryParse(args[8], out maxAgeDays)) || (args.Length == 11 && !int.TryParse(args[10], out topK)))
            throw new ResearchException("universe-backtest-arguments");
        var decisions = new List<DateTimeOffset>();
        foreach (var token in args[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out var decision) ||
                !(token.EndsWith('Z') || token.Contains('+') || token.LastIndexOf('-') > 10))
                throw new ResearchException("universe-backtest-decisions");
            decisions.Add(decision.ToUniversalTime());
        }
        var dataset = new PointInTimeDataset(DatasetJson.Read(DatasetArchive.ReadBounded(Path.GetFullPath(args[2]))));
        var panel = ChangingUniversePanel.FromDataset(dataset, args[4], decisions, TimeSpan.FromDays(maxAgeDays));
        UniverseBacktestReport? report = null; string? refusal = null;
        try { report = UniverseBacktest.Run(panel, new(TopK: topK), new()); }
        catch (ResearchException error) { refusal = error.Code; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            valid = true,
            panel = new
            {
                sha256 = panel.Sha256, datasetId = panel.DatasetId, universeId = panel.UniverseId, synthetic = panel.Synthetic,
                months = panel.Months.Length, periods = panel.Periods.Length, hasGaps = panel.HasGaps,
                statusCounts = panel.ReturnStatusCounts.ToDictionary(x => x.Key.ToString(), x => x.Value),
                builder = ChangingUniversePanel.BuilderVersion
            },
            backtest = report is null ? null : new
            {
                engine = report.EngineVersion, evaluationStart = report.EvaluationStart, evaluationEnd = report.EvaluationEnd,
                summaries = report.Summaries.Select(s => new { s.Strategy, s.EndingEquityGbp, s.ContributionsGbp, s.ProfitGbp, s.TradingCostGbp, s.MaxDrawdown })
            },
            refusal
        }, DatasetJson.Options));
        return report is null ? 1 : 0;
    }
    if ((args.Length == 3 || args.Length == 4 && args[3] == "--sections") && args[0] == "filing-change")
    {
        string ReadDocument(string path)
        {
            var bytes = DatasetArchive.ReadBounded(Path.GetFullPath(path));
            try { return new System.Text.UTF8Encoding(false, true).GetString(bytes); }
            catch (System.Text.DecoderFallbackException) { throw new ResearchException("filing-change-encoding"); }
        }
        var prior = ReadDocument(args[1]); var current = ReadDocument(args[2]);
        var score = FilingChange.Compare(prior, current);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            valid = true, version = FilingChange.Version, score,
            sections = args.Length == 4 ? FilingSections.Compare(prior, current)
                .Select(s => new { s.Part, s.Item, s.Heading, Status = s.Status.ToString(), s.Score }) : null,
            sectionsVersion = args.Length == 4 ? FilingSections.Version : null,
            priorSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(DatasetArchive.ReadBounded(Path.GetFullPath(args[1])))),
            currentSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(DatasetArchive.ReadBounded(Path.GetFullPath(args[2]))))
        }, DatasetJson.Options));
        return 0;
    }
    if (args.Length is not (2 or 4) || args[0] != "validate" || (args.Length == 4 && args[2] != "--archive"))
    {
        Console.Error.WriteLine("Usage: Twenty.Data french-panel --archive <captures> --from yyyy-mm-dd --to yyyy-mm-dd --output <private-inbox> [--portfolios industry|size-value25]\n" +
            "       Twenty.Data tiingo-check --archive <private-directory>\n" +
            "       Twenty.Data tiingo-history <SYMBOL> --from yyyy-mm-dd --to yyyy-mm-dd --archive <private-directory> --capture\n" +
            "       Twenty.Data listing-intervals --archive <captures> --output <private-directory> [--sources nasdaq-listed,nasdaq-other,tiingo-tickers]\n" +
            "       Twenty.Data listing-audit --intervals <interval-id.csv> --archive <tiingo-captures> --symbols AAA,BBB --output <private-directory>\n" +
            "       Twenty.Data sec-facts --dataset <input.json> --bulk <companyfacts.zip> --retrieved-at <iso8601> --concepts Revenues,Assets --archive <dataset-archive> [--dataset-id <id>]\n" +
            "       Twenty.Data universe-backtest --dataset <input.json> --universe <id> --decisions <iso8601,iso8601,...> [--max-price-age-days 45 [--top-k 3]]\n" +
            "       Twenty.Data filing-change <prior-document> <current-document> [--sections]\n" +
            "       Twenty.Data capture-public --archive <private-directory> --sources <comma-separated-source-ids>\n" +
            "       Twenty.Data validate <dataset.json> [--archive <directory>]\n" +
            "       Twenty.Data sec-inspect <CIK> <submissions.json>\n" +
            "       Twenty.Data sec-collect <CIK> --from yyyy-mm-dd --to yyyy-mm-dd --archive <private-directory>\n" +
            "       Twenty.Data ecb-inspect <xml> --date yyyy-mm-dd --base USD --quote GBP\n" +
            "Live collection requires TWENTY_SEC_CONTACT (a monitored email address; never committed or printed).");
        return 2;
    }
    var bytes = DatasetArchive.ReadBounded(args[1]);
    var file = DatasetJson.Read(bytes);
    var validation = DatasetValidator.Validate(file);
    var stored = validation.IsValid && args.Length == 4 ? DatasetArchive.Store(args[3], bytes) : null;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        valid = validation.IsValid, schemaVersion = file.Manifest.SchemaVersion,
        datasetId = validation.IsValid ? file.Manifest.DatasetId : null, synthetic = file.Manifest.Synthetic,
        observations = file.Observations.Length, issues = validation.Issues, archive = stored
    }, DatasetJson.Options));
    return validation.IsValid ? 0 : 1;
}
catch (Twenty.Core.Research.ResearchException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = error.Code }));
    return 1;
}
catch (CaptureException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = error.Code }));
    return 1;
}
catch (EcbDataException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = error.Code }));
    return 1;
}
catch (SecCollectionException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = error.Code }));
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("{\"valid\":false,\"code\":\"collection-cancelled-or-deadline\"}");
    return 1;
}
catch (JsonException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = "json-shape", line = error.LineNumber, position = error.BytePositionInLine }));
    return 1;
}
catch (InvalidDatasetException error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { valid = false, code = error.Validation.Issues.FirstOrDefault()?.Code ?? "dataset-invalid", issues = error.Validation.Issues }, DatasetJson.Options));
    return 1;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("{\"valid\":false,\"code\":\"input-or-archive\"}");
    return 1;
}
