using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Twenty.Core.Research;

public sealed class ResearchException(string code) : Exception("Research operation failed; inspect its safe code.")
{
    public string Code { get; } = code;
}
public sealed partial class MonthlyPanel
{
    public const int Warmup = 13;
    public const string FeatureVersion = "monthly-lagged-v1";
    public static readonly ImmutableArray<string> FeatureNames = ["momentum_1m", "momentum_3m", "momentum_12m_skip_1m", "volatility_12m", "market_momentum_3m"];
    public ImmutableArray<DateOnly> Dates { get; }
    public ImmutableArray<string> Symbols { get; }
    public ImmutableArray<string> Assets { get; }
    public string Benchmark { get; }
    public string Sha256 { get; }
    public string Source { get; }
    public bool Synthetic { get; }
    private readonly decimal[,] values;
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_.:-]{0,31}$", RegexOptions.CultureInvariant)] private static partial Regex Symbol();
    private MonthlyPanel(ImmutableArray<DateOnly> dates, ImmutableArray<string> symbols, decimal[,] values, string benchmark, string hash, string source, bool synthetic)
    { Dates = dates; Symbols = symbols; Assets = symbols.Where(x => x != benchmark).ToImmutableArray(); this.values = values; Benchmark = benchmark; Sha256 = hash; Source = source; Synthetic = synthetic; }
    public static MonthlyPanel Read(byte[] csv, byte[] metadata)
    {
        if (csv.Length > 8 * 1024 * 1024 || metadata.Length > 65536) throw new ResearchException("panel-size-limit");
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 8 });
            var m = document.RootElement;
            if (m.ValueKind != JsonValueKind.Object || m.EnumerateObject().Select(x => x.Name).Distinct().Count() != m.EnumerateObject().Count() ||
                m.GetProperty("currency").GetString() != "GBP" || m.GetProperty("frequency").GetString() != "monthly" ||
                m.GetProperty("source").GetString() is not { Length: > 0 and <= 2048 } source ||
                m.GetProperty("benchmark").GetString() is not { } benchmark || !Symbol().IsMatch(benchmark) ||
                m.GetProperty("synthetic").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ResearchException("panel-metadata");
            var synthetic = m.GetProperty("synthetic").GetBoolean();
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(csv));
            if (reader.ReadLine()?.TrimStart('\uFEFF') != "date,symbol,total_return_index_gbp") throw new ResearchException("panel-header");
            var records = new SortedDictionary<DateOnly, Dictionary<string, decimal>>();
            var rows = 0;
            while (reader.ReadLine() is { } line)
            {
                if (++rows > 60600) throw new ResearchException("panel-row-limit");
                var cells = line.Split(',');
                if (cells.Length != 3 || !DateOnly.TryParseExact(cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    !Symbol().IsMatch(cells[1]) || !decimal.TryParse(cells[2], NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) ||
                    value < 0.00000001m || value > 1000000000000m) throw new ResearchException("panel-row");
                if (!records.TryGetValue(date, out var bucket)) records[date] = bucket = new(StringComparer.Ordinal);
                if (!bucket.TryAdd(cells[1], value)) throw new ResearchException("panel-duplicate");
            }
            if (records.Count is < 16 or > 600) throw new ResearchException("panel-month-count");
            var dates = records.Keys.ToImmutableArray();
            for (var i = 1; i < dates.Length; i++)
                if (dates[i].Year * 12 + dates[i].Month - dates[i - 1].Year * 12 - dates[i - 1].Month != 1) throw new ResearchException("panel-month-gap");
            var symbols = records.First().Value.Keys.Order(StringComparer.Ordinal).ToImmutableArray();
            if (symbols.Length is < 2 or > 101 || !symbols.Contains(benchmark)) throw new ResearchException("panel-universe");
            var values = new decimal[dates.Length, symbols.Length];
            for (var i = 0; i < dates.Length; i++)
            {
                var bucket = records[dates[i]];
                if (bucket.Count != symbols.Length || symbols.Any(s => !bucket.ContainsKey(s))) throw new ResearchException("panel-incomplete-universe");
                for (var j = 0; j < symbols.Length; j++) values[i, j] = bucket[symbols[j]];
            }
            var payload = new byte[csv.Length + metadata.Length + 1];
            csv.CopyTo(payload, 0); metadata.CopyTo(payload, csv.Length + 1);
            return new(dates, symbols, values, benchmark, Convert.ToHexStringLower(SHA256.HashData(payload)), source, synthetic);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or DecoderFallbackException)
        { throw new ResearchException("panel-metadata-or-encoding"); }
    }
    public decimal Index(int t, string symbol) => values[t, Symbols.IndexOf(symbol) is var index && index >= 0 ? index : throw new ResearchException("panel-symbol")];
    public Dictionary<string, decimal> Returns(int t) => Symbols.ToDictionary(s => s, s => Index(t + 1, s) / Index(t, s) - 1m, StringComparer.Ordinal);
    public double[] Labels(int t)
    {
        var returns = Returns(t);
        return Assets.Select(s => (double)(returns[s] - returns[Benchmark])).ToArray();
    }
    public double[] UpsideLabels(int t) => Assets.Select(s => Index(t + 6, s) / Index(t, s) - 1m >= .5m ? 1d : 0d).ToArray();
    public double[] BreakoutScores(int t) => Assets.Select(s => (double)(Index(t - 1, s) /
        Enumerable.Range(t - 13, 12).Max(i => Index(i, s)) - 1m)).ToArray();
    public double[][] Features(int t)
    {
        if (t < Warmup || t > Dates.Length) throw new ResearchException("feature-window");
        var market = (double)(Index(t - 1, Benchmark) / Index(t - 4, Benchmark) - 1m);
        return Assets.Select(s =>
        {
            var logs = Enumerable.Range(t - 13, 12).Select(i => Math.Log((double)(Index(i + 1, s) / Index(i, s)))).ToArray();
            var mean = logs.Average();
            var vol = Math.Sqrt(logs.Sum(x => (x - mean) * (x - mean)) / 11 * 12);
            return new[] { (double)(Index(t - 1, s) / Index(t - 2, s) - 1m), (double)(Index(t - 1, s) / Index(t - 4, s) - 1m),
                (double)(Index(t - 2, s) / Index(t - 13, s) - 1m), vol, market };
        }).ToArray();
    }
}
