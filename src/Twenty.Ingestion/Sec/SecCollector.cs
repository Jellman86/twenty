using System.Collections.Immutable;

namespace Twenty.Ingestion.Sec;

public sealed class SecCollector(SecClient client, SecArchive archive, SecLimits limits, ISecClock? clock = null)
{
    private readonly ISecClock clock = clock ?? SecSystemClock.Instance;
    private static readonly HashSet<string> Forms = ["10-K", "10-K/A", "10-Q", "10-Q/A", "20-F", "20-F/A", "40-F", "40-F/A"];
    public async Task<SecCollectionResult> Collect(string cik, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        limits.Validate();
        cik = SecIdentity.Cik(cik);
        if (from > to || to > DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)) throw new SecCollectionException("invalid-date-window");
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        run.CancelAfter(limits.RunTimeout);
        ct = run.Token;
        var initial = await Page(SecIdentity.Submissions(cik), history: false);
        var metadata = initial.Page.Filings.Select(x => (Row: x, Source: initial.Payload.Receipt)).ToList();
        var chunks = initial.Page.History.Where(x => x.To >= from && x.From <= to)
            .OrderByDescending(x => x.To).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var visited = 0;
        foreach (var chunk in chunks.Take(limits.MaximumHistoryFiles))
        {
            var page = await Page(SecIdentity.History(cik, chunk.Name), history: true);
            if (page.Page.Filings.Length != chunk.FilingCount || page.Page.Filings.Any(x => x.FilingDate < chunk.From || x.FilingDate > chunk.To))
            {
                archive.Quarantine(SecIdentity.History(cik, chunk.Name), page.Payload.Bytes, "history-range-mismatch", clock.UtcNow);
                throw new SecCollectionException("history-range-mismatch");
            }
            metadata.AddRange(page.Page.Filings.Select(x => (x, page.Payload.Receipt)));
            visited++;
        }
        try { _ = SecParser.Merge(metadata.Select(x => x.Row)); }
        catch (SecCollectionException error)
        {
            // Originals are already preserved; record conflict evidence without picking a winner.
            archive.Quarantine(SecIdentity.Submissions(cik), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                metadata.Select(x => new { x.Source.Url, x.Source.ContentSha256 })), error.Code, clock.UtcNow);
            throw;
        }
        var selected = metadata.GroupBy(x => x.Row.Accession).Select(x => x.OrderBy(y => y.Source.FirstSeenAt).First())
            .Where(x => x.Row.FilingDate >= from && x.Row.FilingDate <= to && Forms.Contains(x.Row.Form))
            .OrderByDescending(x => x.Row.AcceptedAt).ThenBy(x => x.Row.Accession, StringComparer.Ordinal).ToArray();
        var collected = ImmutableArray.CreateBuilder<CollectedSecFiling>();
        foreach (var item in selected.Take(limits.MaximumDocuments))
        {
            var document = await client.Fetch(SecIdentity.PrimaryDocument(cik, item.Row.Accession, item.Row.PrimaryDocument),
                json: false, (bytes, type, _) => SecParser.ValidateDocument(bytes, type), ct);
            if (document.Receipt.FirstSeenAt < item.Row.AcceptedAt) throw new SecCollectionException("document-before-acceptance");
            collected.Add(new(item.Row, item.Source, document.Receipt));
        }
        var result = new SecCollectionResult(cik, clock.UtcNow, collected.ToImmutable(), client.Requests, visited,
            chunks.Length > limits.MaximumHistoryFiles, selected.Length > limits.MaximumDocuments, SecParser.Version);
        archive.SaveCatalogue(result);
        return result;

        async Task<(SecSubmissionPage Page, SecPayload Payload)> Page(Uri uri, bool history)
        {
            var payload = await client.Fetch(uri, json: true,
                (bytes, _, observed) => { SecParser.Parse(bytes, cik, history, observed); }, ct);
            return (SecParser.Parse(payload.Bytes, cik, history, payload.Receipt.FirstSeenAt), payload);
        }
    }
}
