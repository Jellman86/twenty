using Twenty.Core.Research;

namespace Twenty.Tests;

public sealed class FilingChangeTests
{
    [Fact]
    public void IdenticalDocumentsAreFullySimilar()
    {
        var score = FilingChange.Compare("revenue grew in services", "revenue grew in services");
        Assert.Equal(1d, score.CosineSimilarity, 12);
        Assert.Equal(1d, score.JaccardSimilarity, 12);
        Assert.Equal(1d, score.LengthRatio, 12);
        Assert.Equal(4, score.SharedVocabulary);
    }

    [Fact]
    public void HandComputedCosineJaccardAndLengthRatio()
    {
        var score = FilingChange.Compare("alpha beta beta gamma", "alpha beta delta");
        Assert.Equal(3d / Math.Sqrt(18d), score.CosineSimilarity, 12);
        Assert.Equal(0.5d, score.JaccardSimilarity, 12);
        Assert.Equal(0.75d, score.LengthRatio, 12);
        Assert.Equal(4, score.PriorTokens);
        Assert.Equal(3, score.CurrentTokens);
        Assert.Equal(2, score.SharedVocabulary);
    }

    [Fact]
    public void MarkupScriptsAndEntitiesDoNotChangeTokens()
    {
        var html = "<html><head><style>.x{color:red}</style><script>var gamma = 1;</script></head>" +
                   "<body><!-- note --><p>Alpha &amp; BETA&#8217;s</p></body></html>";
        Assert.Equal(["alpha", "beta", "s"], FilingChange.Tokenize(html).ToArray());
        Assert.Equal(1d, FilingChange.Compare(html, "alpha beta s").CosineSimilarity, 12);
    }

    [Fact]
    public void PureNumbersAreExcludedSoStatementChurnAloneIsNotChange()
    {
        var score = FilingChange.Compare("revenue 2024 100 200", "revenue 1999 300");
        Assert.Equal(1d, score.CosineSimilarity, 12);
        Assert.Equal(1, score.PriorTokens);
        // Letter-bearing alphanumeric tokens survive.
        Assert.Equal(["q3", "10k"], FilingChange.Tokenize("Q3 2024 10K").ToArray());
    }

    [Fact]
    public void DisjointVocabularyHasZeroSimilarity()
    {
        var score = FilingChange.Compare("alpha beta", "gamma delta");
        Assert.Equal(0d, score.CosineSimilarity, 12);
        Assert.Equal(0d, score.JaccardSimilarity, 12);
    }

    [Fact]
    public void EmptyAndOversizedDocumentsFailClosed()
    {
        Assert.Equal("filing-change-empty",
            Assert.Throws<ResearchException>(() => FilingChange.Compare("<p>2024</p>", "alpha")).Code);
        Assert.Equal("filing-change-size",
            Assert.Throws<ResearchException>(() => FilingChange.Compare(new string('a', 16 * 1024 * 1024 + 1), "alpha")).Code);
    }

    [Fact]
    public void ScoringIsDeterministicAndSymmetricInSimilarity()
    {
        var a = "the quick brown fox jumps over the lazy dog";
        var b = "the quick brown cat leaps over the lazy dog today";
        Assert.Equal(FilingChange.Compare(a, b), FilingChange.Compare(a, b));
        Assert.Equal(FilingChange.Compare(a, b).CosineSimilarity, FilingChange.Compare(b, a).CosineSimilarity, 12);
        Assert.Equal(1d / FilingChange.Compare(a, b).LengthRatio, FilingChange.Compare(b, a).LengthRatio, 12);
    }
}
