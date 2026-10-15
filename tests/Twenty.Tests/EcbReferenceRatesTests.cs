using System.Text;
using System.Xml.Linq;
using Twenty.Core.Data;
using Twenty.Ingestion.Fx;

namespace Twenty.Tests;

public class EcbReferenceRatesTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ecb.synthetic.xml"));
    private static readonly XNamespace Ns = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
    private static byte[] Change(Action<XDocument> edit)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(Fixture())); edit(document);
        return Encoding.UTF8.GetBytes(document.ToString());
    }
    [Fact]
    public void CrossRateHasExplicitMajorUnitDirectionAndReferencePurpose()
    {
        var rates = EcbReferenceRates.Parse(Fixture());
        Assert.Equal(5, rates.Rates.Length);
        var quote = rates.CrossRate(new(2024, 4, 2), "USD", "GBP");
        Assert.Equal(0.64m, quote.QuoteUnitsPerBase);
        Assert.Equal(FxPurpose.Reference, quote.Purpose);
        Assert.Equal(64m, quote.Convert(100m, "USD"));
        Assert.Throws<EcbDataException>(() => quote.Convert(100m, "GBP"));
        Assert.Equal(1.5625m, rates.CrossRate(new(2024, 4, 2), "GBP", "USD").QuoteUnitsPerBase);
        Assert.Equal(0.8m, rates.CrossRate(new(2024, 4, 2), "EUR", "GBP").QuoteUnitsPerBase);
        Assert.Equal(1.25m, rates.CrossRate(new(2024, 4, 2), "GBP", "EUR").QuoteUnitsPerBase);
    }
    [Theory]
    [InlineData("2024-03-29")]
    [InlineData("2024-03-30")]
    [InlineData("2024-04-01")]
    public void MissingDatesAreNotFilledFromEarlierOrLaterRates(string date)
    {
        var rates = EcbReferenceRates.Parse(Fixture());
        Assert.Equal("fx-missing-date", Assert.Throws<EcbDataException>(() => rates.CrossRate(DateOnly.Parse(date), "USD", "GBP")).Code);
    }
    [Fact]
    public void MissingCurrencyCannotBeMixedAcrossReferenceDates()
    {
        var rates = EcbReferenceRates.Parse(Fixture());
        Assert.Equal("fx-missing-currency", Assert.Throws<EcbDataException>(() => rates.CrossRate(new(2024, 3, 28), "JPY", "GBP")).Code);
    }
    [Theory]
    [InlineData("GBX", "USD")]
    [InlineData("GBP", "GBP")]
    [InlineData("usd", "GBP")]
    [InlineData("PLN", "GBP")]
    public void UnsupportedOrAmbiguousPairsNeedAnExplicitContractChange(string from, string to) =>
        Assert.Throws<EcbDataException>(() => EcbReferenceRates.Parse(Fixture()).CrossRate(new(2024, 4, 2), from, to));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,25")]
    [InlineData("1e3")]
    [InlineData("0.123456789012345678901234567895")]
    public void NonPositiveOrNonDecimalValuesFail(string value)
    {
        var bytes = Change(x => x.Descendants(Ns + "Cube").First(c => c.Attribute("rate") is not null).SetAttributeValue("rate", value));
        Assert.Equal("ecb-rate-shape", Assert.Throws<EcbDataException>(() => EcbReferenceRates.Parse(bytes)).Code);
    }
    [Theory]
    [InlineData("duplicate-date")]
    [InlineData("duplicate-currency")]
    [InlineData("wrong-namespace")]
    [InlineData("missing-rate")]
    [InlineData("extra-attribute")]
    [InlineData("invalid-date")]
    [InlineData("empty-day")]
    [InlineData("implicit-eur")]
    [InlineData("unexpected-text")]
    public void AmbiguousOrChangedWireShapesFailRatherThanPickingOne(string mutation)
    {
        var bytes = Change(x =>
        {
            var container = x.Root!.Element(Ns + "Cube")!;
            var day = container.Elements().First();
            var rate = day.Elements().First();
            switch (mutation)
            {
                case "duplicate-date": container.Add(new XElement(day)); break;
                case "duplicate-currency": day.Add(new XElement(rate)); break;
                case "wrong-namespace": rate.Name = XName.Get("Cube", "https://untrusted.invalid/"); break;
                case "missing-rate": rate.Attribute("rate")!.Remove(); break;
                case "extra-attribute": rate.SetAttributeValue("unit", "GBX"); break;
                case "invalid-date": day.SetAttributeValue("time", "04/02/2024"); break;
                case "empty-day": day.RemoveNodes(); break;
                case "implicit-eur": rate.SetAttributeValue("currency", "EUR"); break;
                case "unexpected-text": rate.Value = "unexpected content"; break;
            }
        });
        Assert.Throws<EcbDataException>(() => EcbReferenceRates.Parse(bytes));
    }
    [Fact]
    public void DtdAndExternalEntitiesAreProhibited()
    {
        var malicious = "<!DOCTYPE Envelope [<!ENTITY private SYSTEM 'file:///never-read-this'>]>" + Encoding.UTF8.GetString(Fixture()).Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "", StringComparison.Ordinal);
        Assert.Equal("ecb-xml-shape", Assert.Throws<EcbDataException>(() => EcbReferenceRates.Parse(Encoding.UTF8.GetBytes(malicious))).Code);
    }
    [Fact]
    public void InputAndResultDecimalRangesAreBounded()
    {
        Assert.Throws<EcbDataException>(() => EcbReferenceRates.Parse(new byte[EcbReferenceRates.MaximumBytes + 1]));
        var bytes = Change(x =>
        {
            var day = x.Descendants(Ns + "Cube").First(c => c.Attribute("time") is not null);
            day.Elements().First(c => (string?)c.Attribute("currency") == "GBP").SetAttributeValue("rate", "10000000000000000000000000000");
            day.Elements().First(c => (string?)c.Attribute("currency") == "USD").SetAttributeValue("rate", "0.0000000000000000000000000001");
        });
        var rates = EcbReferenceRates.Parse(bytes);
        Assert.Equal("fx-decimal-range", Assert.Throws<EcbDataException>(() => rates.CrossRate(new(2024, 4, 2), "USD", "GBP")).Code);
        Assert.Equal("fx-decimal-range", Assert.Throws<EcbDataException>(() => rates.CrossRate(new(2024, 4, 2), "GBP", "USD")).Code);
    }
}
