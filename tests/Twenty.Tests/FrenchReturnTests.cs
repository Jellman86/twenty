using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Twenty.Core.Research;
using Twenty.Ingestion.Capture;
using Twenty.Ingestion.Returns;
using Twenty.Research;

namespace Twenty.Tests;

public sealed class FrenchReturnTests
{
    private static byte[] Zip(string text, bool industry = true)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(industry ? "10_Industry_Portfolios.csv" : "F-F_Research_Data_Factors.csv").Open())) writer.Write(text);
        return output.ToArray();
    }
    private static string Header => "Authored test input; no historical evidence.\nAverage Value Weighted Returns -- Monthly\n," + string.Join(',', FrenchReturnPanel.Industries) + "\n";
    private static string Row(string date, string value = "1.00") => date + "," + string.Join(',', Enumerable.Repeat(value, 10)) + "\n";
    [Fact]
    public void OnlyValueWeightedMonthlyReturnsAreParsedNotEqualWeightedOrAnnualTables()
    {
        var bytes = Zip(Header + Row("199901") + Row("199902") + "\nAverage Equal Weighted Returns -- Monthly\n" + Row("199901", "99") + "\nAnnual Returns\n1999,999\n");
        var rows = FrenchReturnPanel.Read(bytes, true);
        Assert.Equal(2, rows.Length); Assert.Equal(new DateOnly(1999, 2, 28), rows[^1].MonthEnd); Assert.All(rows.SelectMany(x => x.PercentReturns), r => Assert.Equal(1m, r));
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("gap")]
    [InlineData("duplicate")]
    [InlineData("section")]
    public void SourceGapsMissingValuesAndWrongSectionFailClosed(string kind)
    {
        var text = Header + Row("199901") + Row(kind == "duplicate" ? "199901" : kind == "gap" ? "199903" : "199902", kind == "missing" ? "-99.99" : "1");
        if (kind == "section") text = text.Replace("Value Weighted", "Equal Weighted");
        Assert.Throws<ResearchException>(() => FrenchReturnPanel.Read(Zip(text), true));
    }
    [Fact]
    public void GbpConversionUsesPercentUnitsMarketPlusRfAndAnExplicitBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "twenty-french-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = new CaptureStore(Path.Combine(root, "captures"));
            var industry = new StringBuilder(Header); var factors = new StringBuilder("Authored unit input\n,Mkt-RF,SMB,HML,RF\n");
            var xml = new StringBuilder("<gesmes:Envelope xmlns:gesmes=\"http://www.gesmes.org/xml/2002-08-01\" xmlns=\"http://www.ecb.int/vocabulary/2002-01/eurofxref\"><Cube>");
            // Use the parser's exact official namespace; all values here are authored test data.
            xml.Replace("2002-01", "2002-08-01");
            for (var i = 0; i < 16; i++)
            {
                var first = new DateOnly(1999, 1, 1).AddMonths(i); var month = first.ToString("yyyyMM", CultureInfo.InvariantCulture);
                industry.Append(Row(month)); factors.Append(month).Append(",0.75,0,0,0.25\n");
                var date = first.AddMonths(1).AddDays(-1);
                xml.Append("<Cube time=\"").Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("\"><Cube currency=\"USD\" rate=\"2\"/><Cube currency=\"GBP\" rate=\"").Append(i == 15 ? "2" : "1").Append("\"/></Cube>");
            }
            xml.Append("</Cube></gesmes:Envelope>");
            foreach (var (id, bytes) in new[] { ("french-industry-monthly", Zip(industry.ToString())), ("french-factors-monthly", Zip(factors.ToString(), false)), ("ecb-reference-history", Encoding.UTF8.GetBytes(xml.ToString())) })
                archive.Record(new(Guid.NewGuid().ToString("N"), id, CaptureSources.Find(id).Url.AbsoluteUri, CaptureStore.Hash(bytes), bytes.Length,
                    "application/octet-stream", DateTimeOffset.UtcNow, "authored-unit", false), bytes, false);
            var files = FrenchReturnPanel.Build(archive, new(1999, 1, 31), new(2000, 4, 30));
            var panel = MonthlyPanel.Read(files.Csv, files.Metadata);
            Assert.Equal(100m, panel.Index(0, "FRENCH_MARKET"));
            var expected = 200m; for (var i = 0; i < 15; i++) expected *= 1.01m;
            Assert.InRange(Math.Abs(panel.Index(15, "FF_NODUR") - expected), 0, .00000000000000000001m);
            Assert.Equal(panel.Index(15, "FF_NODUR"), panel.Index(15, "FRENCH_MARKET"));
            Assert.False(panel.Synthetic); Assert.True(panel.ResearchOnly); Assert.Equal(files.Id, panel.Sha256);
            Assert.Equal("study-research-only-panel", Assert.Throws<ResearchException>(() => StudyProtocol.Declare(panel, 20, new(), "research", "test")).Code);
            Assert.Equal("signal-research-only-panel", Assert.Throws<ResearchException>(() => ForwardSignalBuilder.Build("unit", "unit", null!, panel, panel, DateTimeOffset.UtcNow)).Code);
            using var audit = JsonDocument.Parse(files.Audit); Assert.Equal(16, audit.RootElement.GetProperty("monthly").GetArrayLength());
            using var research = new ResearchStore(Path.Combine(root, "app")); var inbox = Path.Combine(research.Root, "imports"); Directory.CreateDirectory(inbox);
            File.WriteAllBytes(Path.Combine(inbox, files.Id + ".csv"), files.Csv); File.WriteAllBytes(Path.Combine(inbox, files.Id + ".metadata.json"), files.Metadata);
            var entry = research.ImportInbox(new(files.Id, "Authored converter unit input")); Assert.True(entry.ResearchOnly);
            Assert.Equal(entry.Id, research.ImportInbox(new(files.Id, "Repeated import")).Id);
            Assert.Throws<ResearchException>(() => research.ImportInbox(new("../../outside", "unit")));
            File.AppendAllText(Path.Combine(inbox, files.Id + ".metadata.json"), " ");
            Assert.Equal("import-panel-integrity", Assert.Throws<ResearchException>(() => research.ImportInbox(new(files.Id, "tampered"))).Code);
        }
        finally { Directory.Delete(root, true); }
    }
}
