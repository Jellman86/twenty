<script lang="ts">
  import { onMount } from 'svelte';
  let { username, passwordChangedAt, authDisabled = false, onSignOut }: { username: string; passwordChangedAt: string | null; authDisabled?: boolean; onSignOut: () => void } = $props();
  type Runtime = { configured: boolean; enabled: boolean; automatic: boolean; environment: string; monthlyAllowanceGbp: number | null; credentialsConfigured: boolean; paused: boolean; configurationError: string | null; demoReceipts: { synthetic: boolean }[] };
  type Meta = { commit: string; dataset: string; mode: string };
  type Source = { id: string; title: string; defaultHost: string; cadenceHours: number; collectedHere: boolean; lastSuccessAt: string | null; lastAttemptAt: string | null; pausedUntil: string | null; lastError: string | null; observations: number; lastCollector: string | null };
  type Collection = { role: string; collector: string; receivingRemote: boolean; sources: Source[] };
  let collection = $state<Collection | null>(null);
  type Tiingo = { configured: boolean; lastSuccessAt?: string | null; lastError?: string | null; quota?: { uniqueSymbols: number; dayRequests: number; monthBytes: number } | null };
  let tiingo = $state<Tiingo | null>(null);
  const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : '—');
  let runtime = $state<Runtime | null>(null);
  let meta = $state<Meta | null>(null);
  let current = $state('');
  let next = $state('');
  let confirm = $state('');
  let busy = $state(false);
  let error = $state('');
  let notice = $state('');
  const messages: Record<string, string> = {
    'password-current-invalid': 'The current password is not correct.',
    'password-too-short': 'Use a new password of at least 12 characters.',
    'password-too-long': 'Use a new password of at most 256 characters.',
  };
  const post = (path: string, body: unknown = {}) =>
    fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Twenty-Request': '1' }, body: JSON.stringify(body) });
  async function load() {
    const [r, m, c, t] = await Promise.all([fetch('/api/execution/broker'), fetch('/api/meta'), fetch('/api/ingest/status'), fetch('/api/ingest/tiingo')]);
    if (r.ok) runtime = await r.json();
    if (m.ok) meta = await m.json();
    if (c.ok) collection = await c.json();
    if (t.ok) tiingo = await t.json();
    // A failed safety read must be visible, never rendered as an indefinite "Loading…".
    const failed = [!r.ok && 'broker status', !m.ok && 'server mode', !c.ok && 'collection status', !t.ok && 'Tiingo status'].filter(Boolean);
    if (failed.length) error = `Could not load ${failed.join(', ')}. Treat the missing state as unknown and reload the page.`;
  }
  async function checkTiingo() {
    busy = true; error = notice = '';
    try {
      const response = await post('/api/ingest/tiingo/check');
      const result = await response.json();
      if (!response.ok) throw new Error(result.error === 'tiingo-request-not-due' ? 'Wait at least 90 seconds between Tiingo requests.' : result.error ?? 'Tiingo check failed.');
      notice = result.authenticated ? 'Tiingo authentication succeeded. No backfill or trade was started.' : 'Tiingo authentication was not confirmed.';
      await load();
    } catch (e) { error = e instanceof Error ? e.message : 'Tiingo check failed.'; }
    finally { busy = false; }
  }
  async function changePassword(e: SubmitEvent) {
    e.preventDefault();
    error = notice = '';
    if (next !== confirm) { error = 'The new passwords do not match.'; return; }
    busy = true;
    try {
      const response = await post('/api/auth/password', { currentPassword: current, newPassword: next });
      if (!response.ok) {
        const value = await response.json().catch(() => ({}));
        throw new Error(messages[value.error] ?? (response.status === 429 ? 'Too many attempts. Wait a minute.' : 'The password could not be changed.'));
      }
      current = next = confirm = '';
      notice = 'Password changed. Other devices have been signed out.';
    } catch (e) {
      error = e instanceof Error ? e.message : 'The password could not be changed.';
    } finally {
      busy = false;
    }
  }
  async function togglePause() {
    if (!runtime) return;
    busy = true;
    error = notice = '';
    try {
      const response = await post(`/api/execution/pause?paused=${!runtime.paused}`);
      if (!response.ok) throw new Error('The execution switch could not be changed.');
      runtime = { ...runtime, paused: (await response.json()).paused };
    } catch (e) {
      error = e instanceof Error ? e.message : 'The execution switch could not be changed.';
    } finally {
      busy = false;
    }
  }
  async function signOut() {
    await post('/api/auth/logout').catch(() => {});
    onSignOut();
  }
  const autonomy = $derived(
    !runtime ? 'Loading…'
      : !runtime.credentialsConfigured ? 'No broker credentials'
      : !runtime.enabled ? 'Execution disabled'
      : runtime.paused ? 'Paused'
      : runtime.automatic ? `Autonomous · ${runtime.environment}`
      : `Manual cycles · ${runtime.environment}`,
  );
  onMount(() => void load());
</script>

<div class="page-head">
  <h1>Settings</h1>
  <span class="pill">{authDisabled ? 'Sign-in off' : `Signed in as ${username}`}</span>
</div>
{#if error}<p class="error" role="alert">{error}</p>{/if}
{#if notice}<p class="notice" role="status">{notice}</p>{/if}

<div class="bottom-grid settings-grid">
  {#if authDisabled}
  <section class="panel compact" aria-labelledby="account-title">
    <h2 id="account-title">Sign-in is off</h2>
    <p>Anyone who can reach this address can use the app. Turn sign-in back on by removing <code>TWENTY_AUTH=off</code> from the stack in Dockhand. The app refuses to start with sign-in off while a live Trading 212 key is configured.</p>
  </section>
  {:else}
  <section class="panel compact" aria-labelledby="account-title">
    <h2 id="account-title">Password</h2>
    <form class="auth-form" onsubmit={changePassword}>
      <input class="visually-hidden" autocomplete="username" value={username} readonly tabindex="-1" aria-hidden="true" />
      <label>Current password<div class="input-wrap"><input type="password" bind:value={current} autocomplete="current-password" required /></div></label>
      <label>New password<div class="input-wrap"><input type="password" bind:value={next} autocomplete="new-password" minlength="12" required /></div></label>
      <label>Confirm new password<div class="input-wrap"><input type="password" bind:value={confirm} autocomplete="new-password" minlength="12" required /></div></label>
      <button type="submit" disabled={busy}>Change password</button>
    </form>
    <p class="dataset-note">{passwordChangedAt ? `Last changed ${new Date(passwordChangedAt).toLocaleDateString('en-GB')}. ` : ''}Changing it signs out other devices.</p>
    <button class="secondary" onclick={() => void signOut()}>Sign out</button>
  </section>
  {/if}

  <section class="panel compact" aria-labelledby="execution-title">
    <h2 id="execution-title">Order execution · {autonomy}</h2>
    <div class="lab-metrics settings-metrics">
      <div><span>Broker environment</span><strong>{runtime?.environment ?? '—'}</strong></div>
      <div><span>Credentials</span><strong>{runtime ? (runtime.credentialsConfigured ? 'Configured' : 'Missing') : '—'}</strong></div>
      <div><span>Automatic orders</span><strong>{runtime?.automatic ? 'On' : 'Off'}</strong></div>
      <div><span>Monthly allowance</span><strong>{runtime?.monthlyAllowanceGbp != null ? new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' }).format(runtime.monthlyAllowanceGbp) : 'Not set'}</strong></div>
    </div>
    <button class:secondary={!runtime?.paused} disabled={busy || !runtime} onclick={() => void togglePause()}>{runtime?.paused ? 'Resume new orders' : 'Pause all new orders'}</button>
    {#if runtime?.configurationError}<p class="error">Configuration problem: {runtime.configurationError}</p>{/if}
    <p class="dataset-note">The pause survives restarts and stops new simulated and broker orders. Trading 212's API Terms §4.2(a) prohibit algorithmic trading; automatic orders on a live account risk restriction.</p>
    <a class="text-link" href="https://github.com/Jellman86/twenty/blob/dev/docs/broker-execution.md" target="_blank" rel="noreferrer">Execution setup ↗</a>
  </section>
</div>

<section class="panel" aria-labelledby="collection-title">
  <div class="panel-heading">
    <h2 id="collection-title">Data collection · {!collection ? 'Loading…' : collection.role === 'local' ? `on ${collection.collector}` : 'off here'}</h2>
    {#if collection?.receivingRemote}<span class="badge">Accepts remote captures</span>{/if}
  </div>
  <div class="table-scroll">
    <table class="stack">
      <thead><tr><th>Source</th><th>Runs here</th><th>Last capture</th><th>Captures</th><th>State</th></tr></thead>
      <tbody>
        {#each collection?.sources ?? [] as s}
          <tr>
            <td><strong>{s.title}</strong><small>every {s.cadenceHours >= 100 ? 'week' : 'day'}</small></td>
            <td data-label="Runs here">{s.collectedHere ? 'Yes' : 'No'}</td>
            <td data-label="Last capture">{when(s.lastSuccessAt)}{#if s.lastCollector}<small>by {s.lastCollector}</small>{/if}</td>
            <td data-label="Captures">{s.observations}</td>
            <td data-label="State" class:loss={!!s.lastError}>{s.pausedUntil && s.lastError ? `Paused until ${when(s.pausedUntil)}` : s.lastError ? 'Error' : s.lastSuccessAt ? 'OK' : '—'}{#if s.lastError}<small>{s.lastError}</small>{/if}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</section>

<div class="bottom-grid">
  <section class="panel compact" aria-labelledby="tiingo-title">
    <h2 id="tiingo-title">Tiingo</h2>
    <p>{tiingo?.configured ? 'API key configured.' : 'No API key configured.'} Last success: {when(tiingo?.lastSuccessAt ?? null)}.</p>
    {#if tiingo?.quota}<p class="dataset-note">This month: {tiingo.quota.uniqueSymbols}/500 symbols · {(tiingo.quota.monthBytes / 1000000).toFixed(2)} MB. Today: {tiingo.quota.dayRequests}/900 requests.</p>{/if}
    {#if tiingo?.lastError}<p class="error">{tiingo.lastError}</p>{/if}
    <button class="secondary" disabled={busy || !tiingo?.configured} onclick={()=>void checkTiingo()}>Check connection</button>
  </section>
  <section class="panel compact" aria-labelledby="about-title">
    <h2 id="about-title">This instance</h2>
    <dl class="capabilities">
      <div><dt>Build</dt><dd>{meta?.commit.slice(0, 7) ?? '—'}</dd></div>
      <div><dt>Mode</dt><dd>{meta?.mode ?? '—'}</dd></div>
      <div><dt>Data</dt><dd>{meta?.dataset ?? '—'}</dd></div>
    </dl>
  </section>
</div>
