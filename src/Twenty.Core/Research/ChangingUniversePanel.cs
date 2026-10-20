using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Twenty.Core.Data;

namespace Twenty.Core.Research;

// Preserve the numeric values in previously serialized reports when adding new statuses.
public enum UniverseReturnStatus { Priced = 0, Entered = 1, TerminalZero = 2, TerminalCash = 3, ExitedUnexplained = 4, MissingPrice = 5, TerminalStock = 6 }
public sealed record UniverseMonth(DateTimeOffset DecisionAt, ImmutableArray<string> EligibleListingIds, ImmutableArray<string> UnpricedListingIds);
public sealed record UniverseReturn(string ListingId, UniverseReturnStatus Status, decimal? Return);
public sealed record UniversePeriod(DateTimeOffset From, DateTimeOffset To, ImmutableArray<UniverseReturn> Returns);

/// <summary>Realized monthly returns over a point-in-time changing universe, built from a
/// validated v1 dataset. Decisions must form a monthly grid (consecutive calendar months,
/// 20-45 days apart): the consuming backtest deposits, measures momentum and annualises in
/// monthly units, so a denser or sparser grid would be silently misinterpreted as months.
/// Eligibility at each decision uses only information available then;
/// each period return is priced from the later slice at both endpoints so split/dividend
/// adjustments form one consistent vintage (realized outcomes, never decision features).
/// A holding that leaves the universe is never silently dropped: its exit is classified,
/// and a delisting with missing proceeds fails the whole build.</summary>
public sealed class ChangingUniversePanel
{
    public const string BuilderVersion = "changing-universe-v2";
    public string DatasetId { get; }
    public string UniverseId { get; }
    public bool Synthetic { get; }
    public TimeSpan MaximumPriceAge { get; }
    public ImmutableArray<UniverseMonth> Months { get; }
    public ImmutableArray<UniversePeriod> Periods { get; }
    public ImmutableDictionary<UniverseReturnStatus, int> ReturnStatusCounts { get; }
    public string Sha256 { get; }
    /// <summary>True when any period contains an entry a simulator must not ignore:
    /// an unexplained exit or a missing price for a previously eligible listing.</summary>
    public bool HasGaps => ReturnStatusCounts.GetValueOrDefault(UniverseReturnStatus.ExitedUnexplained) > 0 ||
        ReturnStatusCounts.GetValueOrDefault(UniverseReturnStatus.MissingPrice) > 0;

    private ChangingUniversePanel(string datasetId, string universeId, bool synthetic, TimeSpan maximumPriceAge,
        ImmutableArray<UniverseMonth> months, ImmutableArray<UniversePeriod> periods)
    {
        DatasetId = datasetId; UniverseId = universeId; Synthetic = synthetic; MaximumPriceAge = maximumPriceAge;
        Months = months; Periods = periods;
        ReturnStatusCounts = periods.SelectMany(p => p.Returns).GroupBy(r => r.Status)
            .ToImmutableDictionary(g => g.Key, g => g.Count());
        var text = new StringBuilder();
        text.Append(BuilderVersion).Append('\n').Append(datasetId).Append('\n').Append(universeId).Append('\n')
            .Append(maximumPriceAge.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var month in months)
            text.Append("M ").Append(month.DecisionAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(' ')
                .Append(string.Join(",", month.EligibleListingIds)).Append(' ')
                .Append(string.Join(",", month.UnpricedListingIds)).Append('\n');
        foreach (var period in periods)
            foreach (var r in period.Returns)
                text.Append("R ").Append(period.From.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(r.ListingId).Append(' ').Append(r.Status).Append(' ')
                    .Append(r.Return?.ToString(CultureInfo.InvariantCulture) ?? "null").Append('\n');
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private sealed record Close(decimal Major, string Currency, DateTimeOffset SessionCloseAt);

    private static InvalidDatasetException Fail(string code, string message) =>
        new(new([new(code, "selection", message)]));

    // Monthly by contract: consecutive decisions sit in consecutive calendar months (evaluated
    // on their UTC instants) and span 20-45 days. Hourly, weekly, twice-monthly or quarterly
    // grids would otherwise build successfully and later be annualised as if they were months.
    private static bool MonthlyGrid(IReadOnlyList<DateTimeOffset> decisions)
    {
        for (var i = 0; i + 1 < decisions.Count; i++)
        {
            var from = decisions[i].ToUniversalTime();
            var to = decisions[i + 1].ToUniversalTime();
            var months = (to.Year * 12 + to.Month) - (from.Year * 12 + from.Month);
            var days = (to - from).TotalDays;
            if (months != 1 || days is < 20 or > 45) return false;
        }
        return true;
    }

    public static ChangingUniversePanel FromDataset(PointInTimeDataset dataset, string universeId,
        IReadOnlyList<DateTimeOffset> decisions, TimeSpan maximumPriceAge)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (string.IsNullOrWhiteSpace(universeId) || decisions is null || decisions.Count is < 2 or > 600 ||
            decisions.Any(d => d == default) || decisions.Zip(decisions.Skip(1)).Any(p => p.Second <= p.First) ||
            !MonthlyGrid(decisions) ||
            maximumPriceAge <= TimeSpan.Zero || maximumPriceAge > TimeSpan.FromDays(92))
            throw new ResearchException("universe-panel-decisions");
        var slices = decisions.Select(dataset.AsOf).ToArray();
        var priceCache = new Dictionary<(int Slice, string Listing, PriceBasis Basis), ImmutableArray<PriceBar>>();
        ImmutableArray<PriceBar> Prices(int slice, string listing, PriceBasis basis)
        {
            if (!priceCache.TryGetValue((slice, listing, basis), out var rows))
                priceCache[(slice, listing, basis)] = rows = slices[slice].Prices(listing, basis);
            return rows;
        }
        Close? LatestClose(int slice, string listing, PriceBasis basis, DateTimeOffset at)
        {
            var bar = Prices(slice, listing, basis).LastOrDefault(b => b.SessionCloseAt <= at);
            if (bar is null || at - bar.SessionCloseAt > maximumPriceAge) return null;
            return new(bar.Denomination.ToMajor(bar.Close), bar.Denomination.Currency, bar.SessionCloseAt);
        }

        var months = ImmutableArray.CreateBuilder<UniverseMonth>();
        var eligibleByMonth = new ImmutableArray<string>[slices.Length];
        for (var i = 0; i < slices.Length; i++)
        {
            var eligible = slices[i].Universe(universeId).Select(x => x.ListingId).ToImmutableArray();
            eligibleByMonth[i] = eligible;
            var unpriced = eligible.Where(id => LatestClose(i, id, PriceBasis.TotalReturnAdjusted, decisions[i]) is null)
                .ToImmutableArray();
            months.Add(new(decisions[i], eligible, unpriced));
        }
        if (eligibleByMonth.All(e => e.IsEmpty))
            throw Fail("universe-panel-empty", "The universe is empty at every decision time.");

        var periods = ImmutableArray.CreateBuilder<UniversePeriod>();
        for (var i = 0; i + 1 < slices.Length; i++)
        {
            var later = i + 1;
            var returns = ImmutableArray.CreateBuilder<UniverseReturn>();
            foreach (var listing in eligibleByMonth[i])
            {
                var from = LatestClose(later, listing, PriceBasis.TotalReturnAdjusted, decisions[i]);
                if (from is null) { returns.Add(new(listing, UniverseReturnStatus.MissingPrice, null)); continue; }
                if (eligibleByMonth[later].Contains(listing))
                {
                    var to = LatestClose(later, listing, PriceBasis.TotalReturnAdjusted, decisions[later]);
                    if (to is null) { returns.Add(new(listing, UniverseReturnStatus.MissingPrice, null)); continue; }
                    if (to.Currency != from.Currency)
                        throw Fail("universe-panel-currency-change", "A continuing listing changed price currency; explicit FX handling is required.");
                    returns.Add(new(listing, UniverseReturnStatus.Priced, to.Major / from.Major - 1m));
                    continue;
                }
                // Exited between the decisions. A delisting known by the later slice explains
                // the exit even when its effective time precedes the earlier decision
                // (knowledge lag): the holder at the earlier decision still realized it.
                var delistings = slices[later].EffectiveActions(listing)
                    .Where(a => a.Action == CorporateActionKind.Delisting).ToArray();
                if (delistings.Length == 0) { returns.Add(new(listing, UniverseReturnStatus.ExitedUnexplained, null)); continue; }
                if (delistings.Select(a => a.ActionId).Distinct().Count() != 1)
                    throw Fail("universe-panel-terminal-ambiguous", "Multiple delisting actions claim one exit.");
                var action = delistings[0];
                switch (action.TerminalOutcome)
                {
                    case TerminalOutcome.Zero:
                        returns.Add(new(listing, UniverseReturnStatus.TerminalZero, -1m)); break;
                    case TerminalOutcome.Cash:
                        returns.Add(new(listing, UniverseReturnStatus.TerminalCash,
                            CashTerminalReturn(slices[later], later, listing, action, decisions[i], LatestClose)));
                        break;
                    case TerminalOutcome.Stock:
                        returns.Add(new(listing, UniverseReturnStatus.TerminalStock,
                            StockTerminalReturn(slices[later], later, listing, action, decisions[i], decisions[later], LatestClose)));
                        break;
                    case TerminalOutcome.Missing:
                        throw Fail("universe-panel-terminal-missing",
                            "A held listing delisted with unknown proceeds; dropping it would fabricate survivorship-biased returns.");
                    default:
                        throw Fail("universe-panel-terminal-ambiguous", "A delisting carries no terminal outcome.");
                }
            }
            foreach (var listing in eligibleByMonth[later].Except(eligibleByMonth[i]))
                returns.Add(new(listing, UniverseReturnStatus.Entered, null));
            periods.Add(new(decisions[i], decisions[later],
                returns.OrderBy(r => r.ListingId, StringComparer.Ordinal).ToImmutableArray()));
        }
        return new(dataset.Manifest.DatasetId, universeId, dataset.Manifest.Synthetic, maximumPriceAge,
            months.ToImmutable(), periods.ToImmutable());
    }

    // Cash proceeds are per actual share, so the terminal leg is priced from raw closes.
    // Any split/dividend/merger/spinoff between the raw close and the later decision makes
    // that simple ratio wrong; such periods fail closed instead of approximating.
    private static decimal CashTerminalReturn(DataSlice laterSlice, int later, string listing, CorporateAction action,
        DateTimeOffset fromDecision, Func<int, string, PriceBasis, DateTimeOffset, Close?> latestClose)
    {
        var raw = latestClose(later, listing, PriceBasis.Raw, fromDecision)
            ?? throw Fail("universe-panel-terminal-price", "A cash delisting needs a fresh raw close before exit.");
        if (action.CashCurrency != raw.Currency)
            throw Fail("universe-panel-terminal-currency", "Terminal cash currency differs from the raw price currency; explicit FX handling is required.");
        var complex = laterSlice.EffectiveActions(listing).Any(a => a.EffectiveAt > raw.SessionCloseAt &&
            a.Action is CorporateActionKind.Split or CorporateActionKind.Dividend or CorporateActionKind.Merger or CorporateActionKind.SpinOff);
        if (complex)
            throw Fail("universe-panel-terminal-complex", "A corporate action between the raw close and delisting prevents a simple cash terminal return.");
        return action.CashPerShare!.Value / raw.Major - 1m;
    }

    // Stock proceeds: the holder receives successor shares at the exchange ratio, valued at
    // the successor's raw close at the exit decision. The same no-intervening-action guard
    // applies, because any split/dividend/merger/spinoff between the original raw close and
    // the exit would make the simple ratio wrong.
    private static decimal StockTerminalReturn(DataSlice laterSlice, int later, string listing, CorporateAction action,
        DateTimeOffset fromDecision, DateTimeOffset toDecision, Func<int, string, PriceBasis, DateTimeOffset, Close?> latestClose)
    {
        if (string.IsNullOrWhiteSpace(action.SuccessorListingId))
            throw Fail("universe-panel-terminal-successor", "A stock delisting needs a successor listing.");
        if (action.SplitRatio is null or <= 0)
            throw Fail("universe-panel-terminal-ratio", "A stock delisting needs a positive exchange ratio.");
        var originalRaw = latestClose(later, listing, PriceBasis.Raw, fromDecision)
            ?? throw Fail("universe-panel-terminal-price", "A stock delisting needs a fresh raw close before exit.");
        var successorRaw = latestClose(later, action.SuccessorListingId, PriceBasis.Raw, toDecision)
            ?? throw Fail("universe-panel-terminal-successor-price", "A stock delisting needs a fresh successor raw close at exit.");
        if (action.CashCurrency is not null && action.CashCurrency != originalRaw.Currency)
            throw Fail("universe-panel-terminal-currency", "Terminal currency differs from the raw price currency; explicit FX handling is required.");
        if (successorRaw.Currency != originalRaw.Currency)
            throw Fail("universe-panel-terminal-currency", "Successor and original price currencies differ; explicit FX handling is required.");
        var complex = laterSlice.EffectiveActions(listing).Any(a => a.EffectiveAt > originalRaw.SessionCloseAt &&
            a.Action is CorporateActionKind.Split or CorporateActionKind.Dividend or CorporateActionKind.Merger or CorporateActionKind.SpinOff);
        if (complex)
            throw Fail("universe-panel-terminal-complex", "A corporate action between the raw close and delisting prevents a simple stock terminal return.");
        // The ratio describes shares delivered at the exchange. A subsequent successor
        // split/dividend/delisting cannot be valued with that unchanged ratio and raw close.
        var successorFrom = successorRaw.SessionCloseAt < action.EffectiveAt ? successorRaw.SessionCloseAt : action.EffectiveAt;
        if (laterSlice.EffectiveActions(action.SuccessorListingId).Any(a => a.EffectiveAt > successorFrom &&
            a.Action is CorporateActionKind.Split or CorporateActionKind.Dividend or CorporateActionKind.Merger or
                CorporateActionKind.SpinOff or CorporateActionKind.Delisting))
            throw Fail("universe-panel-terminal-complex", "Successor corporate actions require explicit share settlement handling.");
        return action.SplitRatio.Value * successorRaw.Major / originalRaw.Major - 1m;
    }
}
