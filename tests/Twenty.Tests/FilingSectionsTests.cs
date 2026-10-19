using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class FilingSectionsTests
{
    private static string Report(string mda, string risk, bool includeSecondLegal = true, string partOneItemOne = "condensed balance sheet assets liabilities equity") => $"""
        <html><body>
        <p>Table of Contents</p>
        <p>Part I</p><p>Item 1.</p><p>Item 2.</p><p>Part II</p><p>Item 1.</p><p>Item 1A.</p>
        <p>PART I — FINANCIAL INFORMATION</p>
        <p>Item 1. Financial Statements</p>
        <p>{partOneItemOne}</p>
        <p>Item 2. Management's Discussion and Analysis</p>
        <p>{mda}</p>
        <p>PART II — OTHER INFORMATION</p>
        <p>Item 1. Legal Proceedings</p>
        <p>the company is subject to various legal proceedings and claims arising in the ordinary course of business</p>
        {(includeSecondLegal ? $"<p>Item 1A. Risk Factors</p><p>{risk}</p>" : "")}
        </body></html>
        """;

    private static readonly string Prior = Report(
        "revenue grew as services demand remained strong across regions during the quarter",
        "global economic conditions could materially affect demand supply chains and results");
    private static readonly string Current = Report(
        "revenue declined as hardware demand weakened sharply across regions during the quarter",
        "global economic conditions could materially affect demand supply chains and results");

    [Fact]
    public void BodySegmentsWinOverTableOfContentsDuplicates()
    {
        var sections = FilingSections.Split(Prior);
        Assert.Equal([("1", "1"), ("1", "2"), ("2", "1"), ("2", "1a")],
            sections.Select(s => (s.Part, s.Item)).ToArray());
        Assert.Contains("services demand remained strong", sections.Single(s => s is { Part: "1", Item: "2" }).Text);
        Assert.Equal("Item 2. Management's Discussion and Analysis", sections.Single(s => s is { Part: "1", Item: "2" }).Heading);
    }

    [Fact]
    public void ItemNumbersAreQualifiedByPart()
    {
        var sections = FilingSections.Split(Prior);
        Assert.Contains("balance sheet", sections.Single(s => s is { Part: "1", Item: "1" }).Text);
        Assert.Contains("legal proceedings", sections.Single(s => s is { Part: "2", Item: "1" }).Text);
    }

    [Fact]
    public void RomanNumeralPartsCanonicalizeAndMissingHeadingsYieldNoSections()
    {
        var doc = "<p>PART III</p><p>Item 10. Directors and Governance</p><p>board composition and committee structure disclosure</p>";
        Assert.Equal(("3", "10"), FilingSections.Split(doc).Select(s => (s.Part, s.Item)).Single());
        Assert.Empty(FilingSections.Split("<p>no headings in this document at all</p>"));
        Assert.Empty(FilingSections.Split(""));
    }

    [Fact]
    public void LongBodyLinesMentioningItemsAreNotHeadings()
    {
        var doc = "<p>PART I</p><p>Item 1. Business</p>" +
                  "<p>Item 1A. should be read together with the many other disclosures that appear throughout this lengthy narrative paragraph about operations in every region and segment of the company during the period covered by this report and beyond</p>" +
                  "<p>Item 2. Properties</p><p>facilities are located in several countries and regions worldwide</p>";
        var sections = FilingSections.Split(doc);
        Assert.Equal([("1", "1"), ("1", "2")], sections.Select(s => (s.Part, s.Item)).ToArray());
    }

    [Fact]
    public void PerSectionComparisonIsolatesTheChangedSection()
    {
        var changes = FilingSections.Compare(Prior, Current);
        var mda = changes.Single(c => c is { Part: "1", Item: "2" });
        var risk = changes.Single(c => c is { Part: "2", Item: "1a" });
        Assert.Equal(SectionChangeStatus.Compared, mda.Status);
        Assert.True(mda.Score!.CosineSimilarity < risk.Score!.CosineSimilarity);
        Assert.Equal(1d, risk.Score!.CosineSimilarity, 12);
        Assert.All(changes, c => Assert.Equal(SectionChangeStatus.Compared, c.Status));
    }

    [Fact]
    public void AddedRemovedAndEmptySectionsAreClassifiedNotDropped()
    {
        var withoutRisk = Report("revenue grew", "", includeSecondLegal: false);
        var removed = FilingSections.Compare(Prior, withoutRisk).Single(c => c is { Part: "2", Item: "1a" });
        Assert.Equal(SectionChangeStatus.OnlyPrior, removed.Status);
        var added = FilingSections.Compare(withoutRisk, Prior).Single(c => c is { Part: "2", Item: "1a" });
        Assert.Equal(SectionChangeStatus.OnlyCurrent, added.Status);
        // A body segment with no scored tokens (numbers only) is not a section at all.
        var numericOnly = Report("revenue grew", "risk text here", partOneItemOne: "2024 2025 100 200");
        var dropped = FilingSections.Compare(Prior, numericOnly).Single(c => c is { Part: "1", Item: "1" });
        Assert.Equal(SectionChangeStatus.OnlyPrior, dropped.Status);
        Assert.Null(dropped.Score);
    }

    [Fact]
    public void SplittingAndComparisonAreDeterministic()
    {
        Assert.Equal(FilingSections.Split(Prior).ToArray(), FilingSections.Split(Prior).ToArray());
        Assert.Equal(FilingSections.Compare(Prior, Current).ToArray(), FilingSections.Compare(Prior, Current).ToArray());
    }
}
