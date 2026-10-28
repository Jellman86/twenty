export type BrokerAccountRef = { environment: string; account: string };
export type BrokerInstrumentRef = { symbol: string; brokerTicker?: string | null };
export type BrokerNames = BrokerAccountRef & { companies: ReadonlyMap<string, string> };

// Optional display metadata. Failure must never conceal the order journal.
export async function loadBrokerNames(signal: AbortSignal): Promise<BrokerNames | null> {
  try {
    const response = await fetch('/api/portfolio', { signal });
    if (!response.ok) return null;
    const { broker } = await response.json();
    if (!broker || typeof broker.environment !== 'string' || typeof broker.account !== 'string' || !Array.isArray(broker.holdings)) return null;
    const companies = new Map<string, string>();
    const ambiguous = new Set<string>();
    for (const holding of broker.holdings) {
      if (!holding || typeof holding.symbol !== 'string' || typeof holding.name !== 'string' || !holding.name.trim()) continue;
      const name = holding.name.trim();
      if (companies.has(holding.symbol) && companies.get(holding.symbol) !== name) {
        companies.delete(holding.symbol);
        ambiguous.add(holding.symbol);
      }
      if (!ambiguous.has(holding.symbol)) companies.set(holding.symbol, name);
    }
    return { environment: broker.environment, account: broker.account, companies };
  } catch {
    return null;
  }
}

export function brokerCompany(names: BrokerNames | null, account: BrokerAccountRef | undefined, instrument: BrokerInstrumentRef): string | null {
  if (!names || !account || names.environment !== account.environment || names.account !== account.account) return null;
  // The strategy symbol and the broker instrument ID are different namespaces.
  // Only old records without a broker ticker may fall back to their exact symbol.
  return names.companies.get(instrument.brokerTicker ?? instrument.symbol) ?? null;
}

export const orderStatusLabels: Readonly<Record<string, string>> = {
  planned: 'Planned', submitting: 'Submitting', pending: 'Open', partially_filled: 'Part filled', filled: 'Filled',
  cancel_requested: 'Cancellation requested', cancelled: 'Cancelled', rejected: 'Rejected', expired: 'Expired', unknown: 'Needs check',
};
export const openOrderStatuses = new Set(['planned', 'submitting', 'pending', 'partially_filled', 'cancel_requested', 'unknown']);

// A prior fill must not hide a cancellation request or reconciliation problem.
export const orderDisplayStatus = (order: { phase: string; progress: { status: string } }) =>
  order.phase === 'pending' ? order.progress.status : order.phase;
