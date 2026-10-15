using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twenty.Api.Research;
using Twenty.Core.Execution;
using Twenty.Core.Research;

namespace Twenty.Api.Execution;

public sealed record EvidenceReference(string Path, string Sha256);
public sealed record WalletDebitRule(string NetValueConvention, [property: JsonRequired] bool IncludesTaxes, string TaxQuantityConvention);
public sealed record ExecutionPolicy(bool Enabled, bool Automatic, string Environment, long AccountId, string AccountType,
    string RunId, string ReportSha256, decimal MonthlyGbp, WalletDebitRule WalletRule, EvidenceReference AccountEvidence,
    EvidenceReference ResearchEvidence, EvidenceReference MarketDataEvidence, EvidenceReference WalletEvidence,
    string QuotesFile, string DatasetId, [property: JsonRequired] bool SyntheticEvidence)
{
    public BrokerEnvironment BrokerEnvironment => Environment == "demo" ? BrokerEnvironment.Demo : BrokerEnvironment.Live;
    public string AccountKey => Hash(new { Environment, AccountId });
    public string ContractHash => Hash(new { RunId, ReportSha256, MonthlyGbp, WalletRule, ResearchEvidence.Sha256, Data = MarketDataEvidence.Sha256, Wallet = WalletEvidence.Sha256 });
    public string PolicyHash => Hash(new { Environment, AccountId, AccountType, RunId, ReportSha256, MonthlyGbp, WalletRule,
        Account = AccountEvidence.Sha256, Research = ResearchEvidence.Sha256, Data = MarketDataEvidence.Sha256, Wallet = WalletEvidence.Sha256, SyntheticEvidence });
    // Semantic contract hashes ignore object ordering and decimal spelling; raw archive hashes retain exact bytes.
    public static string Hash(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, ResearchStore.Json));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) WriteCanonical(document.RootElement, writer);
        return Convert.ToHexStringLower(SHA256.HashData(output.ToArray()));
    }
    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(property.Value, writer); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer); writer.WriteEndArray(); break;
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                writer.WriteRawValue(number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)); break;
            default: value.WriteTo(writer); break;
        }
    }
    public static bool IsHash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    public void Validate(bool verifyFiles = true)
    {
        if (Environment is not ("demo" or "live") || AccountId <= 0 ||
            (Environment == "live" ? AccountType != "stocks-isa" || SyntheticEvidence : AccountType is not ("demo-invest" or "demo-isa")) ||
            string.IsNullOrWhiteSpace(RunId) || !IsHash(DatasetId) || !IsHash(ReportSha256) || MonthlyGbp is <= 0 or > 1000 ||
            WalletRule is null || WalletRule.NetValueConvention is not ("negative-debit" or "positive-debit") ||
            WalletRule.TaxQuantityConvention is not ("positive-charge" or "negative-charge") || string.IsNullOrWhiteSpace(QuotesFile) || !Path.IsPathFullyQualified(QuotesFile))
            throw new ResearchException("execution-policy");
        foreach (var reference in new[] { AccountEvidence, ResearchEvidence, MarketDataEvidence, WalletEvidence })
        {
            if (reference is null || !IsHash(reference.Sha256) || string.IsNullOrWhiteSpace(reference.Path) || !Path.IsPathFullyQualified(reference.Path))
                throw new ResearchException("execution-evidence-required");
            if (verifyFiles && Convert.ToHexStringLower(SHA256.HashData(ReadPrivate(reference.Path, 1024 * 1024))) != reference.Sha256)
                throw new ResearchException("execution-evidence-integrity");
        }
    }
    public static byte[] ReadPrivate(string path, int maximum)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new ResearchException("execution-private-file");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximum) throw new ResearchException("execution-private-file-limit");
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = stream.Read(buffer)) > 0)
            { if (output.Length + count > maximum) throw new ResearchException("execution-private-file-limit"); output.Write(buffer, 0, count); }
            return output.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ResearchException("execution-private-file"); }
    }
    public static ExecutionPolicy? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var options = new JsonSerializerOptions(ResearchStore.Json) { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            var policy = JsonSerializer.Deserialize<ExecutionPolicy>(ReadPrivate(path, 65536), options) ?? throw new ResearchException("execution-policy");
            policy.Validate(); return policy;
        }
        catch (JsonException) { throw new ResearchException("execution-policy-json"); }
    }
}

public sealed record QuoteEntry(InstrumentRule Rule, string SecurityId, decimal PriceTick, string Source, string RuleEvidenceSha256,
    string OrderCurrency, [property: JsonRequired] bool Synthetic);
public sealed record ExecutionQuotes(string Version, QuoteEntry[] Items)
{
    public string ContractHash => ExecutionPolicy.Hash(Items.OrderBy(i => i.Rule.Symbol, StringComparer.Ordinal).Select(i => new
    { i.Rule.Symbol, i.Rule.BrokerTicker, i.Rule.QuoteCurrency, i.Rule.QuantityStep, i.Rule.MinimumNotionalGbp, i.Rule.MaximumFeeBps,
        i.Rule.Eligible, i.SecurityId, i.PriceTick, i.RuleEvidenceSha256, i.OrderCurrency, i.Synthetic }));
    public void Validate(DateTimeOffset now, bool live)
    {
        if (Version != "execution-quotes-v1" || Items is null || Items.Length is < 1 or > 100 || Items.Any(i => i?.Rule is null)) throw new ResearchException("execution-quotes-schema");
        if (Items.Select(i => i.Rule.Symbol).Distinct(StringComparer.Ordinal).Count() != Items.Length ||
            Items.Select(i => i.Rule.BrokerTicker).Distinct(StringComparer.Ordinal).Count() != Items.Length ||
            Items.Select(i => i.SecurityId).Distinct(StringComparer.Ordinal).Count() != Items.Length)
            throw new ResearchException("execution-quotes-identity");
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.SecurityId) || item.SecurityId.Length > 80 || string.IsNullOrWhiteSpace(item.Source) || item.Source.Length > 2048 ||
                !ExecutionPolicy.IsHash(item.RuleEvidenceSha256) || item.PriceTick is < .00000001m or > 1000 || item.Rule.LimitPrice % item.PriceTick != 0 ||
                item.OrderCurrency is not ("GBP" or "GBX") || (live && item.Synthetic) || item.Rule.ObservedAt > now || now - item.Rule.ObservedAt > TimeSpan.FromMinutes(5))
                throw new ResearchException("execution-quotes-unverified");
        }
    }
    public static ExecutionQuotes Load(string path)
    {
        try { return JsonSerializer.Deserialize<ExecutionQuotes>(ExecutionPolicy.ReadPrivate(path, 65536), new JsonSerializerOptions(ResearchStore.Json) { RespectRequiredConstructorParameters = true, MaxDepth = 16 }) ?? throw new ResearchException("execution-quotes-schema"); }
        catch (JsonException) { throw new ResearchException("execution-quotes-schema"); }
    }
}

public static class BrokerFillAccounting
{
    // The API schema does not define the sign or tax-inclusion semantics of netValue.
    // They must be established from private authenticated samples; there is no guessed default.
    public static decimal Debit(BrokerFill fill, WalletDebitRule rule)
    {
        if (fill is null || fill.Id <= 0 || fill.Type != "TRADE" || fill.Quantity <= 0 || fill.Price <= 0 || fill.WalletImpact is null ||
            fill.WalletImpact.Currency != "GBP" || fill.WalletImpact.FxRate <= 0) throw new ResearchException("broker-fill-units");
        var debit = rule.NetValueConvention switch { "negative-debit" => -fill.WalletImpact.NetValue, "positive-debit" => fill.WalletImpact.NetValue,
            _ => throw new ResearchException("broker-wallet-convention") };
        if (!rule.IncludesTaxes && fill.WalletImpact.Taxes is null) throw new ResearchException("broker-tax-evidence-missing");
        var taxes = 0m;
        foreach (var tax in fill.WalletImpact.Taxes ?? [])
        {
            if (tax is null || tax.Currency != "GBP" || tax.Name is not ("COMMISSION_TURNOVER" or "CURRENCY_CONVERSION_FEE" or "FINRA_FEE" or
                "FRENCH_TRANSACTION_TAX" or "PTM_LEVY" or "STAMP_DUTY" or "STAMP_DUTY_RESERVE_TAX" or "TRANSACTION_FEE"))
                throw new ResearchException("broker-tax-units");
            var charge = rule.TaxQuantityConvention switch { "positive-charge" => tax.Quantity, "negative-charge" => -tax.Quantity,
                _ => throw new ResearchException("broker-wallet-convention") };
            if (charge < 0) throw new ResearchException("broker-tax-sign");
            taxes += charge;
        }
        if (!rule.IncludesTaxes) debit += taxes;
        if (debit <= 0 || (rule.IncludesTaxes && taxes > debit)) throw new ResearchException("broker-wallet-sign");
        return debit;
    }
}
