using System.Collections.Immutable;
using System.Text.Json;
using Twenty.Core.Data;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;

namespace Twenty.Ingestion.Listings;

public sealed record TerminalDispositionInput(string Symbol, ImmutableArray<string> LastTradingSourceIds);
public sealed record TerminalDispositionFile(string SchemaVersion, ImmutableArray<TerminalDispositionInput> Subjects);
public sealed record TerminalDisposition(string Symbol, string Outcome, DateOnly? FinalSession, ImmutableArray<string> Reasons,
    int RefusedRowCount, ImmutableArray<DateOnly> RefusedRows, ImmutableArray<string> LastTradingSourceIds);
public sealed record TerminalDispositionReport(string Version, string AuditReportSha256, string AuditVersion, string ReferenceSha256,
    ImmutableArray<TerminalDisposition> Subjects, int Accepted, int Refused, ImmutableArray<string> Limitations);

/// <summary>Turns a terminal-history audit into one explicit accepted/refused outcome per declared symbol.
/// Acceptance needs a supported cash-only merger, a present final session and a last trading date stated by two
/// original responses from different hosts. Vendor rows after that date are refused rather than repaired or
/// explained. Every audited symbol must be dispositioned; none can be omitted.</summary>
public static class TerminalDispositions
{
    public const string Version = "terminal-disposition-v1";
    private static readonly HashSet<string> Tolerated = ["post-last-trading-bars"];

    public static TerminalDispositionReport Build(TerminalHistoryReport audit, byte[] dispositions)
    {
        var file = Read(dispositions);
        var audited = audit.Subjects.Select(s => s.Reference.Symbol).ToArray();
        if (file.Subjects.Length != audited.Length ||
            file.Subjects.Select(s => s.Symbol).Distinct(StringComparer.Ordinal).Count() != file.Subjects.Length ||
            !file.Subjects.Select(s => s.Symbol).ToHashSet(StringComparer.Ordinal).SetEquals(audited))
            throw new ResearchException("terminal-disposition-subjects");
        var sources = audit.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var results = ImmutableArray.CreateBuilder<TerminalDisposition>();
        foreach (var result in audit.Subjects)
        {
            var subject = result.Reference;
            var cited = file.Subjects.Single(s => s.Symbol == subject.Symbol).LastTradingSourceIds;
            if (cited.Any(id => !subject.SourceIds.Contains(id)) || cited.Distinct(StringComparer.Ordinal).Count() != cited.Length)
                throw new ResearchException("terminal-disposition-shape");
            var reasons = result.Issues.Where(i => !Tolerated.Contains(i)).ToList();
            var originals = cited.Select(id => sources[id]).Where(s => s.Representation == "original-response").ToArray();
            if (subject.LastTradingDate is not null &&
                originals.Select(s => new Uri(s.Url).Host.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count() < 2)
                reasons.Add("last-trading-corroboration-insufficient");
            var accepted = reasons.Count == 0;
            results.Add(new(subject.Symbol, accepted ? "accepted" : "refused", accepted ? subject.LastTradingDate : null,
                [.. reasons], result.PostLastTradingBars, result.PostLastTradingDates, cited));
        }
        var subjects = results.ToImmutable();
        return new(Version, CaptureStore.Hash(JsonSerializer.SerializeToUtf8Bytes(audit, DatasetJson.Options)), audit.AuditVersion,
            audit.ReferenceSha256, subjects, subjects.Count(s => s.Outcome == "accepted"), subjects.Count(s => s.Outcome == "refused"),
            ["Covers only the declared symbols and captured request windows. A refused subject stays in the sample with its reasons; it is not dropped or substituted.",
             "Refused rows are vendor bars dated after an independently stated last trading date. They are not executable sessions, returns or proceeds, and are not deleted from captured histories.",
             "Acceptance checks dates and a stated cash-only common-share entitlement. It does not certify payment dates, taxes, FX, adjusted prices or point-in-time availability, and does not yet filter dataset conversion.",
             "Mixed stock/cash consideration, contingent value rights, unverified final sessions and non-terminal subjects are refused until a supported accounting path and evidence exist."]);
    }

    private static TerminalDispositionFile Read(byte[] bytes)
    {
        if (bytes.Length is 0 or > 256 * 1024) throw new ResearchException("terminal-disposition-shape");
        TerminalDispositionFile? file;
        try { file = JsonSerializer.Deserialize<TerminalDispositionFile>(bytes, DatasetJson.Options); }
        catch (JsonException) { throw new ResearchException("terminal-disposition-shape"); }
        if (file is null || file.SchemaVersion != "terminal-disposition.v1" || file.Subjects.IsDefault || file.Subjects.Length > 50 ||
            file.Subjects.Any(s => s is null || string.IsNullOrWhiteSpace(s.Symbol) || s.LastTradingSourceIds.IsDefault ||
                s.LastTradingSourceIds.Length > 10 || s.LastTradingSourceIds.Any(string.IsNullOrWhiteSpace)))
            throw new ResearchException("terminal-disposition-shape");
        return file;
    }
}
