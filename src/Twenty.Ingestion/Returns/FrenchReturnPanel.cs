using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Fx;

namespace Twenty.Ingestion.Returns;

public sealed record FrenchMonth(DateOnly MonthEnd, ImmutableArray<decimal> PercentReturns);
public sealed record ReturnPanelFiles(string Id, byte[] Csv, byte[] Metadata, byte[] Audit);

public enum FrenchPortfolioSet { Industry10, SizeValue25 }

/// <summary>Offline conversion of a captured, revised research vintage. These portfolios are never broker instruments.</summary>
public static class FrenchReturnPanel
{
    public const string Version = "french-industry-gbp-v1";
    public const string SizeValueVersion = "french-size-value25-gbp-v1";
    public const string Restriction = "Non-executable industry research portfolios from a retrospective revised vintage, not individual stocks or historical point-in-time inputs. FX is a reference fixing, not a US-close or broker quote. No strategy promotion or broker signals.";
    public const string SizeValueRestriction = "Non-executable size/value research portfolios from a retrospective revised vintage, not individual stocks or historical point-in-time inputs. FX is a reference fixing, not a US-close or broker quote. No strategy promotion or broker signals.";
    public static readonly ImmutableArray<string> Industries = ["NoDur", "Durbl", "Manuf", "Enrgy", "HiTec", "Telcm", "Shops", "Hlth", "Utils", "Other"];
    // Upstream column order: size quintiles outer (ME1 smallest), book-to-market inner (BM1 growth .. BM5 value).
    public static readonly ImmutableArray<string> SizeValueColumns = ["SMALL LoBM", "ME1 BM2", "ME1 BM3", "ME1 BM4", "SMALL HiBM",
        "ME2 BM1", "ME2 BM2", "ME2 BM3", "ME2 BM4", "ME2 BM5", "ME3 BM1", "ME3 BM2", "ME3 BM3", "ME3 BM4", "ME3 BM5",
        "ME4 BM1", "ME4 BM2", "ME4 BM3", "ME4 BM4", "ME4 BM5", "BIG LoBM", "ME5 BM2", "ME5 BM3", "ME5 BM4", "BIG HiBM"];
    public static readonly ImmutableArray<string> SizeValueSymbols =
        [.. Enumerable.Range(1, 5).SelectMany(size => Enumerable.Range(1, 5).Select(bm => $"FF25_ME{size}_BM{bm}"))];
    public static string VersionFor(FrenchPortfolioSet set) => set == FrenchPortfolioSet.SizeValue25 ? SizeValueVersion : Version;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ImmutableArray<FrenchMonth> Read(byte[] zipBytes, bool industry) =>
        industry ? ReadCore(zipBytes, "10_Industry_Portfolios.csv", Industries, true, false)
                 : ReadCore(zipBytes, "F-F_Research_Data_Factors.csv", ["Mkt-RF", "SMB", "HML", "RF"], false, true);
    public static ImmutableArray<FrenchMonth> Read(byte[] zipBytes, FrenchPortfolioSet set) => set switch
    {
        FrenchPortfolioSet.Industry10 => Read(zipBytes, true),
        FrenchPortfolioSet.SizeValue25 => ReadCore(zipBytes, "25_Portfolios_5x5.csv", SizeValueColumns, true, false),
        _ => throw new ResearchException("french-portfolio-set")
    };
    private static ImmutableArray<FrenchMonth> ReadCore(byte[] zipBytes, string name, ImmutableArray<string> columns, bool requireValueWeightedSection, bool factors)
    {
        if (zipBytes.Length is 0 or > 2 * 1024 * 1024) throw new ResearchException("french-zip-size");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var entries = zip.Entries.Where(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1 || entries[0].Length is <= 0 or > 4 * 1024 * 1024) throw new ResearchException("french-entry");
            using var input = entries[0].Open(); using var content = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = input.Read(buffer)) > 0)
            { if (content.Length + count > 4 * 1024 * 1024) throw new ResearchException("french-entry-size"); content.Write(buffer, 0, count); }
            var lines = new UTF8Encoding(false, true).GetString(content.ToArray()).Replace("\r", "").Split('\n');
            var header = "," + string.Join(',', columns);
            var start = Array.FindIndex(lines, 0, Math.Min(20, lines.Length), l => l.Trim() == header);
            if (start < 0 || requireValueWeightedSection && (start == 0 || lines[start - 1].Trim() != "Average Value Weighted Returns -- Monthly"))
                throw new ResearchException("french-monthly-section");
            var rows = ImmutableArray.CreateBuilder<FrenchMonth>();
            for (var i = start + 1; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                if (rows.Count >= 2000) throw new ResearchException("french-row-limit");
                var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
                if (cells.Length != columns.Length + 1 || cells[0].Length != 6 ||
                    !DateOnly.TryParseExact(cells[0] + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
                    throw new ResearchException("french-row-shape");
                var end = first.AddMonths(1).AddDays(-1);
                if (rows.Count > 0 && first != new DateOnly(rows[^1].MonthEnd.Year, rows[^1].MonthEnd.Month, 1).AddMonths(1))
                    throw new ResearchException("french-month-gap-or-duplicate");
                var values = ImmutableArray.CreateBuilder<decimal>();
                foreach (var cell in cells.Skip(1))
                {
                    if (cell.Length > 16 || !decimal.TryParse(cell, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                        value is -99.99m or -999m || value <= -100 || value > 1000) throw new ResearchException("french-return-missing-or-range");
                    values.Add(value);
                }
                if (factors && values[0] + values[3] <= -100) throw new ResearchException("french-market-return");
                rows.Add(new(end, values.ToImmutable()));
            }
            if (rows.Count == 0) throw new ResearchException("french-empty");
            return rows.ToImmutable();
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException or OverflowException or ArgumentOutOfRangeException)
        { throw new ResearchException("french-file-invalid"); }
    }

    public static ReturnPanelFiles Build(CaptureStore store, DateOnly from, DateOnly to) => Build(store, from, to, FrenchPortfolioSet.Industry10);
    public static ReturnPanelFiles Build(CaptureStore store, DateOnly from, DateOnly to, FrenchPortfolioSet set)
    {
        if (from.Day != DateTime.DaysInMonth(from.Year, from.Month) || to.Day != DateTime.DaysInMonth(to.Year, to.Month) ||
            from < new DateOnly(1999, 1, 31) || to <= from) throw new ResearchException("return-panel-window");
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        if (months is < 16 or > 600) throw new ResearchException("return-panel-month-count");
        var portfolioSource = set == FrenchPortfolioSet.SizeValue25 ? "french-size-value25-monthly" : "french-industry-monthly";
        var symbols = set == FrenchPortfolioSet.SizeValue25 ? SizeValueSymbols
            : Industries.Select(i => "FF_" + i.ToUpperInvariant()).ToImmutableArray();
        var restriction = set == FrenchPortfolioSet.SizeValue25 ? SizeValueRestriction : Restriction;
        var converter = VersionFor(set);
        var sources = new[] { portfolioSource, "french-factors-monthly", "ecb-reference-history" }
            .Select(id => store.Observations(id).OrderBy(o => o.ObservedAt).LastOrDefault() ?? throw new ResearchException("return-capture-missing")).ToArray();
        var industry = Read(store.Bytes(sources[0].Sha256), set).ToDictionary(x => x.MonthEnd);
        var factors = Read(store.Bytes(sources[1].Sha256), false).ToDictionary(x => x.MonthEnd);
        var rates = EcbReferenceRates.Parse(store.Bytes(sources[2].Sha256)).Rates.Where(r => r.Currency is "GBP" or "USD")
            .GroupBy(r => r.ReferenceDate).Where(g => g.Count() == 2).ToDictionary(g => g.Key,
                g => g.Single(r => r.Currency == "GBP").CurrencyUnitsPerEuro / g.Single(r => r.Currency == "USD").CurrencyUnitsPerEuro);
        var csv = new StringBuilder("date,symbol,total_return_index_gbp\n");
        var indices = Enumerable.Repeat(100m, symbols.Length + 1).ToArray(); decimal initialFx = 0;
        var audit = new List<object>();
        for (var i = 0; i < months; i++)
        {
            var first = new DateOnly(from.Year, from.Month, 1).AddMonths(i); var end = first.AddMonths(1).AddDays(-1);
            if (!industry.TryGetValue(end, out var ir) || !factors.TryGetValue(end, out var fr)) throw new ResearchException("return-panel-month-missing");
            var fxDate = rates.Keys.Where(d => d <= end && d >= end.AddDays(-7)).DefaultIfEmpty().Max();
            if (fxDate == default) throw new ResearchException("return-panel-fx-missing");
            var fx = rates[fxDate]; if (i == 0) initialFx = fx;
            var usd = ir.PercentReturns.Add(fr.PercentReturns[0] + fr.PercentReturns[3]);
            for (var j = 0; j < indices.Length; j++)
            {
                if (i > 0) indices[j] = checked(indices[j] * (1m + usd[j] / 100m));
                var symbol = j == symbols.Length ? "FRENCH_MARKET" : symbols[j];
                csv.Append(end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',').Append(symbol).Append(',')
                    .Append(checked(indices[j] * fx / initialFx).ToString("G29", CultureInfo.InvariantCulture)).Append('\n');
            }
            audit.Add(new { monthEnd = end, fxReferenceDate = fxDate, gbpPerUsd = fx, usdPercentReturns = usd, baselineOnly = i == 0 });
        }
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = (set == FrenchPortfolioSet.SizeValue25
                ? "Kenneth French 25 value-weighted size/book-to-market research portfolios + market (Mkt-RF + RF); GBP conversion from ECB reference rates. "
                : "Kenneth French 10 value-weighted industry research portfolios + market (Mkt-RF + RF); GBP conversion from ECB reference rates. ") + restriction,
            synthetic = false, currency = "GBP", frequency = "monthly", benchmark = "FRENCH_MARKET", researchOnly = true, researchOnlyReason = restriction,
            converter, from, to, sources,
            returnConvention = "USD monthly total research returns in percent; initial month is an index baseline only. GBP index = USD index * (GBP per USD / initial GBP per USD).",
            availability = "Retrieved vintage only; no original historical publication or availability times inferred.",
            fxConvention = "Last reference date on or before calendar month-end, at most seven calendar days old. Reference and US closing instants differ."
        }, Json);
        var panel = MonthlyPanel.Read(bytes, metadata);
        return new(panel.Sha256, bytes, metadata, JsonSerializer.SerializeToUtf8Bytes(new
        {
            converter, panelSha256 = panel.Sha256, csvSha256 = Hash(bytes), metadataSha256 = Hash(metadata), from, to,
            observations = months, assets = symbols.Length, baselineMonth = from, sources, monthly = audit,
            limitations = new[] { restriction, "Research portfolios cannot be bought directly; fees are scenario assumptions, not verified ETF or stock execution costs.",
                "Published portfolio returns do not supply individual delisting proceeds, historical ISA eligibility or a multibagger denominator. No predictive edge is established by conversion." }
        }, Json));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
