using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Twenty.Core.Research;

public sealed record FilingSection(string Part, string Item, string Heading, string Text);
public enum SectionChangeStatus { Compared, OnlyPrior, OnlyCurrent }
public sealed record SectionChange(string Part, string Item, string Heading, SectionChangeStatus Status, FilingChangeScore? Score);

/// <summary>Deterministic Part/Item section extraction for periodic reports (10-K/10-Q
/// style headings) and per-section FilingChange scoring. Heading rules are fixed: a
/// short line starting with "Part &lt;roman&gt;" or "Item &lt;n&gt;[letter][./-/—/:]" after the
/// shared FilingText normalization. Tables of contents repeat every heading, so for a
/// duplicated (part, item) the titled, token-richest segment wins — the body, not the contents page.
/// Item numbers repeat across parts in 10-Qs, so sections are keyed by (part, item).
/// Segments with no scored tokens (contents-page stubs, number-only blocks) are not
/// sections. Extraction failure yields fewer sections, never guessed ones: a document
/// with no recognizable headings returns an empty list. This selects sections; it
/// still does not classify routine vs substantive change.</summary>
public static partial class FilingSections
{
    public const string Version = "filing-sections-v1";
    private const int MaximumHeadingCharacters = 200;

    [GeneratedRegex(@"^part\s+(iv|iii|ii|i)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartHeading();
    [GeneratedRegex(@"^item\s+(\d{1,2})([a-z])?\s*[.\-—:](\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemHeading();

    private sealed record Segment(string Part, string Item, string Heading, int Start, int End, bool HasTitle);

    public static ImmutableArray<FilingSection> Split(string document) => SplitPlain(FilingText.ToPlainText(document));

    internal static ImmutableArray<FilingSection> SplitPlain(string plain)
    {
        var segments = new List<Segment>();
        var part = "";
        string? openItem = null;
        var openPart = "";
        var openHeading = "";
        var openStart = 0;
        var openHasTitle = false;
        var offset = 0;
        foreach (var rawLine in plain.Split('\n'))
        {
            var line = rawLine.Trim();
            var lineStart = offset;
            offset += rawLine.Length + 1;
            if (line.Length == 0 || line.Length > MaximumHeadingCharacters) continue;
            var partMatch = PartHeading().Match(line);
            if (partMatch.Success)
            {
                if (openItem is not null) segments.Add(new(openPart, openItem, openHeading, openStart, lineStart, openHasTitle));
                openItem = null;
                part = partMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "i" => "1", "ii" => "2", "iii" => "3", _ => "4"
                };
                continue;
            }
            var itemMatch = ItemHeading().Match(line);
            if (!itemMatch.Success) continue;
            if (openItem is not null) segments.Add(new(openPart, openItem, openHeading, openStart, lineStart, openHasTitle));
            openPart = part;
            openItem = itemMatch.Groups[1].Value + itemMatch.Groups[2].Value.ToLowerInvariant();
            openHeading = line;
            openStart = offset;
            openHasTitle = line[itemMatch.Length..].Any(char.IsLetter);
        }
        if (openItem is not null) segments.Add(new(openPart, openItem, openHeading, openStart, plain.Length, openHasTitle));
        // Selection among duplicates (contents page vs body): a heading carrying its
        // title inline beats a bare one, then more scored tokens wins. Genuine tiny
        // body sections ("None.") must not lose to a bare contents stub whose
        // segment happens to contain neighbouring contents lines.
        var best = new Dictionary<(string, string), (Segment Segment, int Tokens)>();
        foreach (var segment in segments)
        {
            var tokens = FilingChange.TokenizePlain(plain[segment.Start..segment.End]).Length;
            var key = (segment.Part, segment.Item);
            if (!best.TryGetValue(key, out var prior)
                || (segment.HasTitle, tokens).CompareTo((prior.Segment.HasTitle, prior.Tokens)) > 0)
                best[key] = (segment, tokens);
        }
        return best.Values.Where(v => v.Tokens > 0).OrderBy(v => v.Segment.Start)
            .Select(v => new FilingSection(v.Segment.Part, v.Segment.Item, v.Segment.Heading, plain[v.Segment.Start..v.Segment.End]))
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
