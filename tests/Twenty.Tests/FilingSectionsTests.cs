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
    public void EmDashTitlesWithoutFollowingSpaceAreHeadings()
    {
        // Genuine Costco style: "Item 1—Business" with the title glued to the dash.
        var doc = "<p>PART I</p><p>Item 1—Business</p><p>warehouse club operations across north america and other regions</p>" +
                  "<p>Item 1A—Risk Factors</p><p>competition and international operations involve substantial risks</p>" +
                  "<p>Item 7—Management's Discussion and Analysis of Financial Condition and Results of Operations (amounts in millions)</p>" +
                  "<p>net sales increased primarily due to comparable sales growth and new warehouses</p>";
        var sections = FilingSections.Split(doc);
        Assert.Equal([("1", "1"), ("1", "1a"), ("1", "7")], sections.Select(s => (s.Part, s.Item)).ToArray());
        Assert.Contains("warehouse club", sections[0].Text);
    }

    [Fact]
    public void LegacyPlainTextStyleWithCrossReferencesSplitsCorrectly()
    {
        // Genuine 2000-era ASCII style: ALL-CAPS headings, wrapped cross-references that
        // begin lines with "Part II, Item 7 of..." or "Item 7 of..." must not split.
        var doc = """
            PART I
            ITEM 1. BUSINESS
            The Company designs manufacture and markets personal computers and related software. Additional
            information about future products appears in
            Part II, Item 7 of this Form 10-K under the subheading "Product Introductions and Transitions."
            Item 7 of this Form 10-K under the heading "Net Sales" describes quarterly variation in detail.
            ITEM 2. PROPERTIES
            The Company leases facilities for research and development manufacturing and administration worldwide.
            PART II
            ITEM 7. MANAGEMENT'S DISCUSSION AND ANALYSIS OF FINANCIAL CONDITION AND RESULTS
            Net sales rose during the year driven by strong demand for newly introduced products and services.
            ITEM 7A. DISCLOSURES ABOUT MARKET RISK
            The Company uses derivative instruments to manage foreign currency and interest rate exposures.
            """;
        var sections = FilingSections.Split(doc);
        Assert.Equal([("1", "1"), ("1", "2"), ("2", "7"), ("2", "7a")],
            sections.Select(s => (s.Part, s.Item)).ToArray());
        Assert.Contains("Product Introductions", sections.Single(s => s is { Part: "1", Item: "1" }).Text);
    }

    [Fact]
    public void ForeignFormDottedSubItemsCanonicalize()
    {
        // Authored 20-F-style fixture ("Item 3.D."): genuine 20-F documents currently
        // exceed the SEC collector response cap, so this style is fixture-verified only.
        var doc = "<p>PART I</p><p>Item 3.D. Risk Factors</p><p>investing in our securities involves a high degree of risk across markets</p>" +
                  "<p>Item 4.B Business Overview</p><p>we design and manufacture lithography systems for semiconductor producers</p>" +
                  "<p>ITEM 5. OPERATING AND FINANCIAL REVIEW</p><p>net sales increased due to higher system shipments to customers</p>";
        var sections = FilingSections.Split(doc);
        Assert.Equal([("1", "3d"), ("1", "4b"), ("1", "5")], sections.Select(s => (s.Part, s.Item)).ToArray());
    }

    [Fact]
    public void BareHeadingsWithoutPunctuationNeedACapitalizedTitle()
    {
        var doc = "<p>PART I</p><p>ITEM 2 PROPERTIES</p><p>facilities are located in several countries and regions worldwide</p>" +
                  "<p>ITEM 3 LEGAL PROCEEDINGS</p><p>the company is subject to various legal proceedings and claims</p>";
        Assert.Equal([("1", "2"), ("1", "3")], FilingSections.Split(doc).Select(s => (s.Part, s.Item)).ToArray());
        // A decimal is not an item number.
        var notHeading = "<p>PART I</p><p>Item 1.5 percent of revenue came from services during the year</p>" +
                         "<p>Item 1. Business</p><p>the company designs and sells consumer electronics products globally</p>";
        Assert.Equal([("1", "1")], FilingSections.Split(notHeading).Select(s => (s.Part, s.Item)).ToArray());
    }

    [Fact]
    public void ExhibitSubPartsWithColonsDoNotResetTheFilingPart()
    {
        // Genuine Baidu 20-F: an exhibit inside Item 6 uses "Part I: Gender Identity"
        // and "Part II: Demographic Background" sub-headings. They must not re-key
        // the filing's own Items 7-12 from Part I to Part II.
        var doc = "<p>PART I</p><p>ITEM 6. DIRECTORS, SENIOR MANAGEMENT AND EMPLOYEES</p>" +
                  "<p>Part I: Gender Identity</p><p>employees by gender and region as of year end</p>" +
                  "<p>Part II: Demographic Background</p><p>employees by age group and geography as of year end</p>" +
                  "<p>ITEM 7. MAJOR SHAREHOLDERS AND RELATED PARTY TRANSACTIONS</p><p>principal shareholders beneficially own the following shares</p>" +
                  "<p>PART II</p><p>ITEM 13. DEFAULTS, DIVIDEND ARREARAGES AND DELINQUENCIES</p><p>none during the periods presented in this report</p>";
        Assert.Equal([("1", "6"), ("1", "7"), ("2", "13")],
            FilingSections.Split(doc).Select(s => (s.Part, s.Item)).ToArray());
    }

    [Fact]
    public void BareDuplicateHeadingsResolveToTheLaterBodySegmentOnTies()
    {
        // Contents stub and body both bare with equal scored tokens ("Not applicable"
        // shells, other contents items between them as in genuine filings): the
        // later (body) segment wins the tie.
        var doc = "<p>Part I</p><p>Item 1.</p><p>Identity of Directors and Advisers</p><p>Item 2.</p><p>Offer Statistics</p>" +
                  "<p>PART I</p><p>ITEM 1.</p><p>IDENTITY OF DIRECTORS AND ADVISERS</p><p>Not applicable.</p>" +
                  "<p>ITEM 2.</p><p>OFFER STATISTICS</p><p>Not applicable.</p>";
        var section = FilingSections.Split(doc).Single(s => s.Item == "1");
        Assert.Equal("ITEM 1.", section.Heading);
        Assert.Contains("Not applicable", section.Text);
    }

    [Fact]
    public void RunningPageHeadersMergeIntoOneBodySection()
    {
        // Genuine Microsoft style: "PART I / Item 1" running headers repeat on every
        // page inside Item 1, fragmenting it. Consecutive same-key fragments merge;
        // the contents entry stays a separate (losing) candidate.
        var doc = "<p>PART I</p><p>Item 1.</p><p>Item 1A.</p>" +
                  "<p>PART I</p><p>ITEM 1. BUSINESS</p><p>first page describes cloud services and devices sold worldwide</p>" +
                  "<p>PART I</p><p>Item 1</p><p>second page continues describing segments and distribution channels</p>" +
                  "<p>PART I</p><p>Item 1</p><p>third page describes competition and seasonal revenue patterns</p>" +
                  "<p>ITEM 1A. RISK FACTORS</p><p>intense competition could harm operating results and margins</p>";
        var sections = FilingSections.Split(doc);
        Assert.Equal([("1", "1"), ("1", "1a")], sections.Select(s => (s.Part, s.Item)).ToArray());
        var business = sections[0];
        Assert.Equal("ITEM 1. BUSINESS", business.Heading);
        Assert.Contains("first page", business.Text);
        Assert.Contains("second page", business.Text);
        Assert.Contains("third page", business.Text);
    }

    [Fact]
    public void RepeatedShortBoilerplateLinesAreExcludedFromSectionText()
    {
        var head = string.Concat(Enumerable.Repeat("<p>NOTES TO FINANCIAL STATEMENTS (CONTINUED)</p><p>Table of Contents</p>", 9));
        var rare = string.Concat(Enumerable.Repeat("<p>See accompanying notes</p>", 7));
        var doc = "<p>PART I</p><p>Item 8. Financial Statements</p>" + head +
                  "<p>inventories consist of raw materials work in process and finished goods held for sale</p>" + rare;
        var section = FilingSections.Split(doc).Single();
        Assert.DoesNotContain("CONTINUED", section.Text);
        Assert.DoesNotContain("Table of Contents", section.Text);
        Assert.Contains("See accompanying notes", section.Text); // 7 repeats stay below the boilerplate threshold
        Assert.Contains("inventories consist", section.Text);
    }

    [Fact]
    public void SplittingAndComparisonAreDeterministic()
    {
        Assert.Equal(FilingSections.Split(Prior).ToArray(), FilingSections.Split(Prior).ToArray());
        Assert.Equal(FilingSections.Compare(Prior, Current).ToArray(), FilingSections.Compare(Prior, Current).ToArray());
    }
}
