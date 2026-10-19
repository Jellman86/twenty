using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Twenty.Core.Research;

public sealed record FilingChangeScore(double CosineSimilarity, double JaccardSimilarity, double LengthRatio,
    int PriorTokens, int CurrentTokens, int SharedVocabulary);

/// <summary>Deterministic markup stripping and entity decoding shared by the filing
/// text features. Block-level tags become newlines so heading structure survives;
/// all other tags become spaces. Tokenization is unaffected by the distinction.</summary>
internal static class FilingText
{
    // Aligned with the SEC collector's per-response cap: the largest measured genuine
    // primary document (ASML 2024 20-F) is 32.3 MiB, which a 16M-character cap rejected.
    internal const int MaximumCharacters = 48 * 1024 * 1024;

    internal static string ToPlainText(string document)
    {
        if (document is null || document.Length > MaximumCharacters) throw new ResearchException("filing-change-size");
        return DecodeEntities(StripMarkup(document));
    }

    private static readonly HashSet<string> BlockTags =
        ["p", "div", "table", "tr", "td", "th", "ul", "ol", "li", "br", "hr", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6"];

    private static string StripMarkup(string document)
    {
        var text = new StringBuilder(document.Length);
        var i = 0;
        while (i < document.Length)
        {
            if (document[i] != '<') { text.Append(document[i++]); continue; }
            if (document.AsSpan(i).StartsWith("<!--"))
            {
                var commentEnd = document.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = commentEnd < 0 ? document.Length : commentEnd + 3;
                text.Append(' ');
                continue;
            }
            var close = document.IndexOf('>', i + 1);
            if (close < 0) break;
            var tag = document.AsSpan(i + 1, close - i - 1).Trim();
            var closing = tag.StartsWith("/");
            if (closing) tag = tag[1..].TrimStart();
            var nameEnd = 0;
            while (nameEnd < tag.Length && (char.IsLetter(tag[nameEnd]) || char.IsDigit(tag[nameEnd]))) nameEnd++;
            var name = tag[..nameEnd].ToString().ToLowerInvariant();
            i = close + 1;
            text.Append(BlockTags.Contains(name) ? '\n' : ' ');
            if (!closing && name is "script" or "style")
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

/// <summary>Deterministic whole-document text-change baseline in the spirit of Cohen,
/// Malloy &amp; Nguyen's Lazy Prices similarity measures. Normalization is fixed and
/// versioned: markup/script/style removal, a small explicit entity set, invariant
/// lowercase, and letter-bearing alphanumeric tokens (pure numbers are excluded so
/// financial-statement churn does not dominate the prose signal). This computes a
/// similarity feature only; it does not classify routine vs substantive change or
/// establish any return relation. Section-level scoring lives in FilingSections.</summary>
public static class FilingChange
{
    public const string Version = "filing-change-v1";

    public static FilingChangeScore Compare(string prior, string current)
    {
        var priorTokens = Tokenize(prior);
        var currentTokens = Tokenize(current);
        if (priorTokens.IsEmpty || currentTokens.IsEmpty) throw new ResearchException("filing-change-empty");
        return ScoreTokens(priorTokens, currentTokens);
    }

    internal static FilingChangeScore ScoreTokens(ImmutableArray<string> priorTokens, ImmutableArray<string> currentTokens)
    {
        var priorCounts = Counts(priorTokens);
        var currentCounts = Counts(currentTokens);
        var shared = priorCounts.Keys.Intersect(currentCounts.Keys).ToArray();
        var dot = shared.Sum(token => (double)priorCounts[token] * currentCounts[token]);
        var cosine = dot / (Norm(priorCounts) * Norm(currentCounts));
        var jaccard = (double)shared.Length / (priorCounts.Count + currentCounts.Count - shared.Length);
        return new(cosine, jaccard, (double)currentTokens.Length / priorTokens.Length,
            priorTokens.Length, currentTokens.Length, shared.Length);
    }

    public static ImmutableArray<string> Tokenize(string document) => TokenizePlain(FilingText.ToPlainText(document));

    internal static ImmutableArray<string> TokenizePlain(string plain)
    {
        var text = plain.ToLowerInvariant();
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
}
