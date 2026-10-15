using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Twenty.Core.Data;

namespace Twenty.Ingestion.Fx;

public sealed class EcbDataException(string code) : Exception("ECB reference data failed validation; inspect the safe error code.")
{
    public string Code { get; } = code;
}
public sealed record EcbDailyRate(DateOnly ReferenceDate, string Currency, decimal CurrencyUnitsPerEuro);
public sealed record EcbCrossRate(DateOnly ReferenceDate, string BaseCurrency, string QuoteCurrency, decimal QuoteUnitsPerBase)
{
    public FxPurpose Purpose => FxPurpose.Reference;
    public decimal Convert(decimal amount, string inputCurrency) => inputCurrency == BaseCurrency
        ? checked(amount * QuoteUnitsPerBase) : throw new EcbDataException("fx-input-currency");
}

/// <summary>ECB eurofxref XML wire format. A date is retained as a date; no dissemination time is inferred.</summary>
public sealed partial class EcbReferenceRates
{
    public const string ParserVersion = "ecb-eurofxref-xml-v1";
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly XNamespace Gesmes = "http://www.gesmes.org/xml/2002-08-01";
    private static readonly XNamespace RatesNamespace = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
    // Match the currencies supported by the v1 domain. Extra historical wire currencies are preserved,
    // but need an explicit contract review before they can be requested as an application FX quote.
    private static readonly HashSet<string> Supported = ["EUR", "GBP", "USD", "CAD", "AUD", "CHF", "JPY", "SEK", "NOK", "DKK", "HKD", "SGD", "NZD", "ZAR"];
    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)] private static partial Regex CurrencyCode();
    public ImmutableArray<EcbDailyRate> Rates { get; }
    private EcbReferenceRates(ImmutableArray<EcbDailyRate> rates) => Rates = rates;

    public static EcbReferenceRates Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new EcbDataException("ecb-size-limit");
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0,
                IgnoreComments = true, IgnoreProcessingInstructions = true
            });
            var xml = XDocument.Load(reader, LoadOptions.None);
            var root = xml.Root;
            if (root?.Name != Gesmes + "Envelope") throw new EcbDataException("ecb-envelope");
            var containers = root.Elements(RatesNamespace + "Cube").ToArray();
            if (containers.Length != 1) throw new EcbDataException("ecb-cube-shape");
            var container = containers[0];
            if (container.HasAttributes || container.Elements().Any(x => x.Name != RatesNamespace + "Cube"))
                throw new EcbDataException("ecb-cube-shape");
            var rows = ImmutableArray.CreateBuilder<EcbDailyRate>();
            var dates = new HashSet<DateOnly>();
            foreach (var day in container.Elements())
            {
                if (day.Attributes().Count() != 1 || !DateOnly.TryParseExact((string?)day.Attribute("time"), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || !dates.Add(date))
                    throw new EcbDataException("ecb-date-shape");
                var currencies = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rate in day.Elements())
                {
                    var currency = (string?)rate.Attribute("currency");
                    var value = (string?)rate.Attribute("rate");
                    if (rate.Name != RatesNamespace + "Cube" || rate.HasElements || !string.IsNullOrWhiteSpace(rate.Value) || rate.Attributes().Count() != 2 ||
                        currency is null || !CurrencyCode().IsMatch(currency) || currency is "EUR" or "GBX" ||
                        !currencies.Add(currency) || value is null || value.Length > 32 ||
                        !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                        number.ToString("0.############################", CultureInfo.InvariantCulture) != CanonicalDecimal(value))
                        throw new EcbDataException("ecb-rate-shape");
                    rows.Add(new(date, currency, number));
                    if (rows.Count > 300000) throw new EcbDataException("ecb-row-limit");
                }
                if (currencies.Count == 0) throw new EcbDataException("ecb-empty-day");
            }
            if (rows.Count == 0) throw new EcbDataException("ecb-empty-dataset");
            return new(rows.ToImmutable());
        }
        catch (XmlException) { throw new EcbDataException("ecb-xml-shape"); }
    }
    private static string CanonicalDecimal(string value)
    {
        // TryParse can round excessive input precision; accept only exactly representable wire values.
        var canonical = value.TrimStart('0');
        if (canonical.StartsWith('.')) canonical = "0" + canonical;
        if (canonical.Contains('.')) canonical = canonical.TrimEnd('0').TrimEnd('.');
        return canonical.Length == 0 ? "0" : canonical;
    }
    public EcbCrossRate CrossRate(DateOnly date, string baseCurrency, string quoteCurrency)
    {
        if (!Supported.Contains(baseCurrency) || !Supported.Contains(quoteCurrency) || baseCurrency == quoteCurrency)
            throw new EcbDataException("fx-currency-pair");
        // Exact same-day lookup: no weekend/holiday forward fill, date mixing or inferred inversion.
        var day = Rates.Where(x => x.ReferenceDate == date).ToArray();
        if (day.Length == 0) throw new EcbDataException("fx-missing-date");
        decimal PerEuro(string currency) => currency == "EUR" ? 1m :
            day.SingleOrDefault(x => x.Currency == currency)?.CurrencyUnitsPerEuro ?? throw new EcbDataException("fx-missing-currency");
        try
        {
            var value = checked(PerEuro(quoteCurrency) / PerEuro(baseCurrency));
            if (value <= 0) throw new EcbDataException("fx-decimal-range");
            return new(date, baseCurrency, quoteCurrency, value);
        }
        catch (OverflowException) { throw new EcbDataException("fx-decimal-range"); }
    }
}
