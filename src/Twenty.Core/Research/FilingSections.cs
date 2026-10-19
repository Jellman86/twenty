using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Twenty.Core.Research;

public sealed record FilingSection(string Part, string Item, string Heading, string Text);
public enum SectionChangeStatus { Compared, OnlyPrior, OnlyCurrent }
public sealed record SectionChange(string Part, string Item, string Heading, SectionChangeStatus Status, FilingChangeScore? Score);

/// <summary>Deterministic Part/Item section extraction for periodic reports and
/// per-section FilingChange scoring. Heading rules are fixed and were validated against
/// genuine SEC documents in four styles (modern iXBRL from three issuers, 2000-2001
/// SGML-wrapped ASCII/early-HTML filings, and a conventional Form 20-F): a short line
/// "Part &lt;roman|digit&gt;" followed by end-of-line or a dash/dot separator (comma and
/// colon cross-references like "Part II, Item 7 of this Form 10-K" and exhibit
/// sub-headings like "Part II: Demographic Background" are not headings), or
/// "Item &lt;n&gt;[.letter|letter]" terminated by punctuation (em-dash titles such as
/// "Item 1—Business" included; decimals like "Item 1.5" excluded) or by whitespace
/// before a capitalized title ("ITEM 2 PROPERTIES"; lowercase continuations like
/// "Item 7 of this..." are cross-references). Form 20-F dotted sub-items ("Item 3.D.")
/// canonicalize to "3d". Consecutive same-key segments under bare headings merge:
/// some filers repeat running page headers ("PART I / Item 1") throughout a section.
/// Tables of contents repeat every heading, so for a duplicated (part, item) the
/// titled, token-richest segment wins — the body, not the contents page. Short
/// non-heading lines repeated at least eight times document-wide (running heads,
/// "Table of Contents" furniture) are boilerplate and excluded from section text.
/// Segments with no scored tokens are not sections. Extraction failure yields fewer
/// sections, never guessed ones. This selects sections; it still does not classify
/// routine vs substantive change.</summary>
public static partial class FilingSections
{
    public const string Version = "filing-sections-v3";
    private const int MaximumHeadingCharacters = 200;
    private const int BoilerplateMinimumOccurrences = 8;
    private const int BoilerplateMaximumCharacters = 80;

    // No colon: genuine exhibit sub-headings inside a section use it ("Part I: Gender
    // Identity" inside Baidu's Item 6), while observed filing part headings are bare
    // or dash-titled ("PART I — FINANCIAL INFORMATION").
    [GeneratedRegex(@"^part\s+(iv|iii|ii|i|[1-4])\s*([.\-—]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartHeading();
    [GeneratedRegex(@"^item\s+(\d{1,2})(?:\.([a-z])|([a-z]))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemPrefix();

    private sealed record Segment(string Part, string Item, string Heading, List<(int Start, int End)> Ranges, bool HasTitle)
    {
        public int FirstLine => Ranges[0].Start;
    }

    public static ImmutableArray<FilingSection> Split(string document) => SplitPlain(FilingText.ToPlainText(document));

    private static bool IsPartHeading(string line, out string part)
    {
        part = "";
        var match = PartHeading().Match(line);
        if (!match.Success) return false;
        part = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", var digit => digit
        };
        return true;
    }

    private static bool IsItemHeading(string line, out string item, out bool hasTitle)
    {
        item = ""; hasTitle = false;
        var match = ItemPrefix().Match(line);
        if (!match.Success) return false;
        var rest = line[match.Length..];
        if (rest.Length > 0)
        {
            if (rest[0] is '.' or '-' or '—' or ':')
            {
                // Punctuation terminator: what follows may be a title glued by an em dash
                // ("Item 1—Business") but never a digit ("Item 1.5" is a decimal).
                if (rest.Length > 1 && char.IsDigit(rest[1])) return false;
                hasTitle = rest[1..].Any(char.IsLetter);
            }
            else if (char.IsWhiteSpace(rest[0]))
            {
                // Bare whitespace terminator: a capitalized title ("ITEM 2 PROPERTIES")
                // or nothing. Lowercase continuations are cross-references, not headings.
                var title = rest.Trim();
                if (title.Length > 0 && !char.IsUpper(title[0]) && title[0] != '[') return false;
                hasTitle = title.Any(char.IsLetter);
            }
            else return false;
        }
        var letter = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        item = match.Groups[1].Value + letter.ToLowerInvariant();
        return true;
    }

    internal static ImmutableArray<FilingSection> SplitPlain(string plain)
    {
        var rawLines = plain.Split('\n');
        var lines = rawLines.Select(l => l.Trim()).ToArray();
        // Boilerplate: short lines repeated document-wide (running heads, contents
        // furniture). Heading lines are boundaries, never body text, so only body
        // membership matters here.
        var boilerplate = lines
            .Where(l => l.Length is > 0 and <= BoilerplateMaximumCharacters)
            .GroupBy(l => l, StringComparer.Ordinal)
            .Where(g => g.Count() >= BoilerplateMinimumOccurrences)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var segments = new List<Segment>();
        var part = "";
        string? openItem = null;
        var openPart = "";
        var openHeading = "";
        var openFirstLine = 0;
        var openHasTitle = false;
        void Close(int endLine)
        {
            if (openItem is null) return;
            var range = (openFirstLine, endLine);
            if (segments.Count > 0 && segments[^1].Part == openPart && segments[^1].Item == openItem && !openHasTitle)
            {
                // Consecutive same-key segment under a bare heading: a repeated running
                // page header inside one body section. A titled heading always starts a
                // new segment, so a contents entry never merges into the body (or back).
                segments[^1].Ranges.Add(range);
            }
            else segments.Add(new(openPart, openItem, openHeading, [range], openHasTitle));
            openItem = null;
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.Length > MaximumHeadingCharacters) continue;
            if (IsPartHeading(line, out var newPart)) { Close(i); part = newPart; continue; }
            if (!IsItemHeading(line, out var item, out var hasTitle)) continue;
            Close(i);
            openPart = part; openItem = item; openHeading = line; openFirstLine = i + 1; openHasTitle = hasTitle;
        }
        Close(lines.Length);

        string TextOf(Segment segment) => string.Join("\n",
            segment.Ranges.SelectMany(r => Enumerable.Range(r.Start, r.End - r.Start))
                .Where(i => !boilerplate.Contains(lines[i])).Select(i => rawLines[i]));

        // Selection among duplicates (contents page vs body): a heading carrying its
        // title inline beats a bare one, then more scored tokens wins; exact ties go
        // to the later segment, since the body always follows the contents page.
        // Genuine tiny body sections ("None.") must not lose to a bare contents stub
        // whose segment happens to contain neighbouring contents lines.
        var best = new Dictionary<(string, string), (Segment Segment, string Text, int Tokens)>();
        foreach (var segment in segments)
        {
            var text = TextOf(segment);
            var tokens = FilingChange.TokenizePlain(text).Length;
            var key = (segment.Part, segment.Item);
            if (!best.TryGetValue(key, out var prior)
                || (segment.HasTitle, tokens).CompareTo((prior.Segment.HasTitle, prior.Tokens)) >= 0)
                best[key] = (segment, text, tokens);
        }
        return best.Values.Where(v => v.Tokens > 0).OrderBy(v => v.Segment.FirstLine)
            .Select(v => new FilingSection(v.Segment.Part, v.Segment.Item, v.Segment.Heading, v.Text))
            .ToImmutableArray();
    }

    public static ImmutableArray<SectionChange> Compare(string prior, string current)
    {
        var priorSections = Split(prior);
        var currentSections = Split(current);
        var currentByKey = currentSections.ToDictionary(s => (s.Part, s.Item));
        var result = ImmutableArray.CreateBuilder<SectionChange>();
        foreach (var before in priorSections)
        {
            if (!currentByKey.TryGetValue((before.Part, before.Item), out var after))
            {
                result.Add(new(before.Part, before.Item, before.Heading, SectionChangeStatus.OnlyPrior, null));
                continue;
            }
            result.Add(new(before.Part, before.Item, after.Heading, SectionChangeStatus.Compared,
                FilingChange.ScoreTokens(FilingChange.TokenizePlain(before.Text),
                    FilingChange.TokenizePlain(after.Text))));
        }
        var priorKeys = priorSections.Select(s => (s.Part, s.Item)).ToHashSet();
        foreach (var after in currentSections.Where(s => !priorKeys.Contains((s.Part, s.Item))))
            result.Add(new(after.Part, after.Item, after.Heading, SectionChangeStatus.OnlyCurrent, null));
        return result.ToImmutable();
    }
}
