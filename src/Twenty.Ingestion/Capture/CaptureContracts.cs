using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Twenty.Ingestion.Capture;

public sealed class CaptureException(string code) : Exception("Capture failed; inspect the safe error code.")
{
    public string Code { get; } = code;
}

/// <summary>Where a source is collected by default. A collector can be told to run any source explicitly.</summary>
public enum CaptureHost { Local, Remote, Any }
public enum CaptureRole { Local, Remote }

public sealed record CaptureSource(string Id, string Title, Uri Url, TimeSpan Cadence, CaptureHost Host,
    ImmutableArray<string> AllowedHosts, Func<byte[], string?> Validate, Regex? DiscoverLink = null)
{
    /// <summary>When set, <see cref="Url"/> is a page and the file is the first link matching this pattern.</summary>
    public bool Discovers => DiscoverLink is not null;
    public bool RunsOn(CaptureRole role) => role == CaptureRole.Local ? Host != CaptureHost.Remote : Host == CaptureHost.Remote;
}

/// <summary>An immutable record that a source returned exactly these bytes at this time.</summary>
public sealed record CaptureObservation(string Id, string SourceId, string Url, string Sha256, long Bytes, string ContentType,
    DateTimeOffset ObservedAt, string Collector, bool NotModified, string? ETag = null, DateTimeOffset? LastModified = null,
    DateTimeOffset? ReceivedAt = null);

public sealed record CaptureStatus(string SourceId, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastSha256,
    string? ETag, DateTimeOffset? LastModified, int ConsecutiveFailures, DateTimeOffset? PausedUntil, string? LastError, long Observations);

public static partial class CaptureSources
{
    public static readonly TimeSpan Daily = TimeSpan.FromHours(20), Weekly = TimeSpan.FromDays(6);

    // Every source here needs no account. Terms were reviewed in docs/market-data-selection.md; the owner's
    // scraping decision applies, but nothing here passes a login, paywall or bot challenge.
    public static readonly ImmutableArray<CaptureSource> All =
    [
        new("nasdaq-listed", "Nasdaq-listed securities", new("https://www.nasdaqtrader.com/dynamic/SymDir/nasdaqlisted.txt"), Daily, CaptureHost.Any,
            ["www.nasdaqtrader.com"], b => PipeFile(b, "Symbol|Security Name|Market Category|Test Issue|")),
        new("nasdaq-other", "Other exchange-listed securities", new("https://www.nasdaqtrader.com/dynamic/SymDir/otherlisted.txt"), Daily, CaptureHost.Any,
            ["www.nasdaqtrader.com"], b => PipeFile(b, "ACT Symbol|Security Name|Exchange|CQS Symbol|")),
        new("tiingo-tickers", "Tiingo supported tickers", new("https://apimedia.tiingo.com/docs/tiingo/daily/supported_tickers.zip"), Daily, CaptureHost.Any,
            ["apimedia.tiingo.com"], b => ZipCsv(b, "supported_tickers.csv", "ticker,exchange,assetType,priceCurrency,startDate,endDate")),
        new("french-factors-monthly", "Fama-French factors, monthly", new("https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Research_Data_Factors_CSV.zip"),
            Weekly, CaptureHost.Local, ["mba.tuck.dartmouth.edu"], b => ZipCsv(b, "F-F_Research_Data_Factors.csv", ",Mkt-RF,SMB,HML,RF")),
        new("french-factors-daily", "Fama-French factors, daily", new("https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/F-F_Research_Data_Factors_daily_CSV.zip"),
            Weekly, CaptureHost.Local, ["mba.tuck.dartmouth.edu"], b => ZipCsv(b, "F-F_Research_Data_Factors_daily.csv", ",Mkt-RF,SMB,HML,RF")),
        new("french-industry-monthly", "French 10 industry portfolios, monthly", new("https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/ftp/10_Industry_Portfolios_CSV.zip"),
            Weekly, CaptureHost.Local, ["mba.tuck.dartmouth.edu"], b => ZipCsv(b, "10_Industry_Portfolios.csv", ",NoDur,Durbl,Manuf,Enrgy,HiTec,Telcm,Shops,Hlth,Utils,Other")),
        new("ecb-reference-history", "ECB reference FX history", new("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-hist.xml"),
            Weekly, CaptureHost.Local, ["www.ecb.europa.eu"], b => b.Length > 100 && Encoding.UTF8.GetString(b.AsSpan(0, Math.Min(b.Length, 2048))).Contains("gesmes:Envelope", StringComparison.Ordinal) ? null : "unexpected-xml"),
        new("shiller-ie-data", "Shiller S&P data", new("https://shillerdata.com/"), Weekly, CaptureHost.Local,
            ["shillerdata.com", "img1.wsimg.com"], OleSpreadsheet, ShillerLink()),
    ];

    public static CaptureSource Find(string id) => All.SingleOrDefault(s => s.Id == id) ?? throw new CaptureException("unknown-source");

    /// <summary>Sources for a role, or the named ones. Explicit names may run sources outside the role's default.</summary>
    public static ImmutableArray<CaptureSource> For(CaptureRole role, string? names)
    {
        if (string.IsNullOrWhiteSpace(names)) return All.Where(s => s.RunsOn(role)).ToImmutableArray();
        if (names.Trim() == "all") return All;
        return names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Select(Find).ToImmutableArray();
    }

    [GeneratedRegex(@"href=""(?<url>(?:https:)?//img1\.wsimg\.com/[^""]*/ie_data\.xls(?:\?[^""]*)?)""", RegexOptions.IgnoreCase)]
    private static partial Regex ShillerLink();

    // The Nasdaq Trader files end with a "File Creation Time" trailer; a truncated download lacks it.
    internal static string? PipeFile(byte[] bytes, string header)
    {
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.StartsWith(header, StringComparison.Ordinal)) return "unexpected-header";
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 100) return "too-few-rows";
        return lines[^1].StartsWith("File Creation Time:", StringComparison.Ordinal) ? null : "missing-trailer";
    }

    internal static string? ZipCsv(byte[] bytes, string entryName, string header)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.GetEntry(entryName);
            if (entry is null) return "missing-entry";
            if (entry.Length > 256L * 1024 * 1024) return "entry-too-large";
            using var reader = new StreamReader(entry.Open(), Encoding.ASCII);
            // French files begin with a few lines of notes before the column header.
            for (var i = 0; i < 20 && reader.ReadLine() is { } line; i++)
                if (line.TrimEnd().StartsWith(header, StringComparison.Ordinal)) return null;
            return "unexpected-header";
        }
        catch (InvalidDataException) { return "invalid-zip"; }
    }

    internal static string? OleSpreadsheet(byte[] bytes) =>
        bytes.Length > 4096 && bytes.AsSpan(0, 8).SequenceEqual((byte[])[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]) ? null : "not-a-spreadsheet";
}
