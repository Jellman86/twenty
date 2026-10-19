using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Twenty.Core.Research;

public sealed record FilingChangeScore(double CosineSimilarity, double JaccardSimilarity, double LengthRatio,
    int PriorTokens, int CurrentTokens, int SharedVocabulary);

/// <summary>Deterministic whole-document text-change baseline in the spirit of Cohen,
/// Malloy &amp; Nguyen's Lazy Prices similarity measures. Normalization is fixed and
/// versioned: markup/script/style removal, a small explicit entity set, invariant
/// lowercase, and letter-bearing alphanumeric tokens (pure numbers are excluded so
/// financial-statement churn does not dominate the prose signal). This computes a
/// similarity feature only; it does not classify routine vs substantive change, select
/// sections, or establish any return relation.</summary>
public static class FilingChange
{
    public const string Version = "filing-change-v1";
    private const int MaximumCharacters = 16 * 1024 * 1024;

    public static FilingChangeScore Compare(string prior, string current)
    {
        var priorCounts = Counts(Tokenize(prior));
        var currentCounts = Counts(Tokenize(current));
        var priorTokens = priorCounts.Values.Sum();
        var currentTokens = currentCounts.Values.Sum();
        if (priorTokens == 0 || currentTokens == 0) throw new ResearchException("filing-change-empty");
        var shared = priorCounts.Keys.Intersect(currentCounts.Keys).ToArray();
        var dot = shared.Sum(token => (double)priorCounts[token] * currentCounts[token]);
        var cosine = dot / (Norm(priorCounts) * Norm(currentCounts));
        var jaccard = (double)shared.Length / (priorCounts.Count + currentCounts.Count - shared.Length);
        return new(cosine, jaccard, (double)currentTokens / priorTokens, priorTokens, currentTokens, shared.Length);
    }

    public static ImmutableArray<string> Tokenize(string document)
    {
        if (document is null || document.Length > MaximumCharacters) throw new ResearchException("filing-change-size");
        var text = DecodeEntities(StripMarkup(document)).ToLowerInvariant();
        var tokens = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        var hasLetter = false;
        void Flush()
        {
            if (current.Length > 0 && hasLetter) tokens.Add(current.ToString());
            current.Clear(); hasLetter = false;
        }
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z') { current.Append(ch); hasLetter = true; }
            else if (ch is >= '0' and <= '9') current.Append(ch);
            else Flush();
        }
        Flush();
        return tokens.ToImmutable();
    }

    private static Dictionary<string, int> Counts(ImmutableArray<string> tokens) =>
        tokens.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static double Norm(Dictionary<string, int> counts) =>
        Math.Sqrt(counts.Values.Sum(v => (double)v * v));

    private static string StripMarkup(string document)
    {
        var text = new StringBuilder(document.Length);
        var i = 0;
        while (i < document.Length)
        {
            if (document[i] != '<') { text.Append(document[i++]); continue; }
            if (document.AsSpan(i).StartsWith("<!--"))
            {
                var end = document.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? document.Length : end + 3;
                text.Append(' ');
                continue;
            }
            var close = document.IndexOf('>', i + 1);
            if (close < 0) break;
            var tag = document.AsSpan(i + 1, close - i - 1).Trim();
            var nameEnd = 0;
            while (nameEnd < tag.Length && char.IsLetter(tag[nameEnd])) nameEnd++;
            var name = tag[..nameEnd].ToString().ToLowerInvariant();
            i = close + 1;
            text.Append(' ');
            if (name is "script" or "style")
            {
                var endTag = document.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (endTag < 0) break;
                var endClose = document.IndexOf('>', endTag + 2);
                i = endClose < 0 ? document.Length : endClose + 1;
            }
        }
        return text.ToString();
    }

    private static string DecodeEntities(string text)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '&') { result.Append(text[i++]); continue; }
            var semi = text.IndexOf(';', i + 1);
            if (semi < 0 || semi - i > 10) { result.Append(text[i++]); continue; }
            var entity = text.AsSpan(i + 1, semi - i - 1).ToString();
            var decoded = entity switch
            {
                "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", "nbsp" => " ",
                _ => DecodeNumeric(entity)
            };
            if (decoded is null) result.Append(text[i++]);
            else { result.Append(decoded); i = semi + 1; }
        }
        return result.ToString();
        static string? DecodeNumeric(string entity)
        {
            if (entity.Length < 2 || entity[0] != '#') return null;
            var parsed = entity.Length > 2 && entity[1] is 'x' or 'X'
                ? int.TryParse(entity[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : -1
                : int.TryParse(entity[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var dec) ? dec : -1;
            return parsed is > 0 and <= 0x10FFFF && !(parsed is >= 0xD800 and <= 0xDFFF)
                ? char.ConvertFromUtf32(parsed) : null;
        }
    }
}
