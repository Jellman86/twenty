using System.Globalization;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Ingestion.Sec;
using Twenty.Ingestion.Fx;

try
{
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
    if (args.Length is not (2 or 4) || args[0] != "validate" || (args.Length == 4 && args[2] != "--archive"))
    {
        Console.Error.WriteLine("Usage: Twenty.Data validate <dataset.json> [--archive <directory>]\n" +
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
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDatasetException)
{
    Console.Error.WriteLine("{\"valid\":false,\"code\":\"input-or-archive\"}");
    return 1;
}
