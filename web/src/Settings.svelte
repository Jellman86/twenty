<script lang="ts">
  import { onMount } from 'svelte';
  let { username, passwordChangedAt, authDisabled = false, onSignOut }: { username: string; passwordChangedAt: string | null; authDisabled?: boolean; onSignOut: () => void } = $props();
  type Runtime = { configured: boolean; enabled: boolean; automatic: boolean; environment: string; monthlyAllowanceGbp: number | null; credentialsConfigured: boolean; paused: boolean; configurationError: string | null; demoReceipts: { synthetic: boolean }[] };
  type Meta = { commit: string; dataset: string; mode: string };
  type Source = { id: string; title: string; defaultHost: string; cadenceHours: number; collectedHere: boolean; lastSuccessAt: string | null; lastAttemptAt: string | null; pausedUntil: string | null; lastError: string | null; observations: number; lastCollector: string | null };
  type Collection = { role: string; collector: string; receivingRemote: boolean; sources: Source[] };
  let collection = $state<Collection | null>(null);
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
    const [r, m, c] = await Promise.all([fetch('/api/execution/broker'), fetch('/api/meta'), fetch('/api/ingest/status')]);
    if (r.ok) runtime = await r.json();
    if (m.ok) meta = await m.json();
    if (c.ok) collection = await c.json();
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

<div class="page-heading">
  <div>
    <p class="eyebrow">SETTINGS</p>
    <h1>Account, execution<br /><em>and deployment.</em></h1>
    <p class="intro">Credentials and the execution policy live in private files on the server.<br class="desktop-break" /> They are shown here, never typed here.</p>
  </div>
  <div class="mode-stamp"><span>{authDisabled ? 'SIGN-IN' : 'SIGNED IN AS'}</span><strong>{authDisabled ? 'Off' : username}</strong><small>{passwordChangedAt ? `Password set ${new Date(passwordChangedAt).toLocaleDateString('en-GB')}` : ''}</small></div>
</div>
{#if error}<p class="error" role="alert">{error}</p>{/if}
{#if notice}<p class="notice" role="status">{notice}</p>{/if}

<div class="bottom-grid settings-grid">
  {#if authDisabled}
  <section class="panel compact" aria-labelledby="account-title">
    <p class="eyebrow">ACCOUNT</p>
    <h2 id="account-title">Sign-in is off</h2>
    <p>Anyone who can reach this address can use the app, so keep the route private. Turn sign-in back on by removing <code>TWENTY_AUTH=off</code> from the stack in Dockhand. The app refuses to run with sign-in off while a live Trading 212 key is configured.</p>
  </section>
  {:else}
  <section class="panel compact" aria-labelledby="account-title">
    <p class="eyebrow">ACCOUNT</p>
    <h2 id="account-title">Change password</h2>
    <form class="auth-form" onsubmit={changePassword}>
      <input class="visually-hidden" autocomplete="username" value={username} readonly tabindex="-1" aria-hidden="true" />
      <label>Current password<div class="input-wrap"><input type="password" bind:value={current} autocomplete="current-password" required /></div></label>
      <label>New password<div class="input-wrap"><input type="password" bind:value={next} autocomplete="new-password" minlength="12" required /></div></label>
      <label>Confirm new password<div class="input-wrap"><input type="password" bind:value={confirm} autocomplete="new-password" minlength="12" required /></div></label>
      <button type="submit" disabled={busy}>Change password</button>
    </form>
    <p class="dataset-note">Changing the password signs out every other device. Five wrong passwords lock sign-in for 15 minutes.</p>
    <button class="secondary" onclick={() => void signOut()}>Sign out</button>
  </section>
  {/if}

  <section class="panel compact" aria-labelledby="execution-title">
    <p class="eyebrow">EXECUTION</p>
    <h2 id="execution-title">{autonomy}</h2>
    <div class="lab-metrics settings-metrics">
      <div><span>Broker environment</span><strong>{runtime?.environment ?? '—'}</strong></div>
      <div><span>Credentials</span><strong>{runtime?.credentialsConfigured ? 'Configured' : 'Missing'}</strong></div>
      <div><span>Automatic worker</span><strong>{runtime?.automatic ? 'On' : 'Off'}</strong></div>
      <div><span>Monthly allowance</span><strong>{runtime?.monthlyAllowanceGbp != null ? new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' }).format(runtime.monthlyAllowanceGbp) : 'Not set'}</strong></div>
    </div>
    <button class:secondary={!runtime?.paused} disabled={busy || !runtime} onclick={() => void togglePause()}>{runtime?.paused ? 'Resume new orders' : 'Pause all new orders'}</button>
    <p class="dataset-note">The pause survives restarts and stops new simulated and broker submissions; reconciliation continues. Environment, allowance and the automatic worker come from the private execution policy file.</p>
    {#if runtime?.configurationError}<p class="error">Private configuration: {runtime.configurationError}</p>{/if}
    <p class="dataset-note">Real autonomous orders also need a validated strategy and a reconciled demo receipt for the current code ({runtime?.demoReceipts.filter((r) => !r.synthetic).length ?? 0} so far). Trading 212's API Terms §4.2(a) prohibit algorithmic trading; running the automatic worker against a live account risks the account being restricted.</p>
    <a class="text-link" href="https://github.com/Jellman86/twenty/blob/dev/docs/broker-execution.md" target="_blank" rel="noreferrer">Execution policy and setup ↗</a>
  </section>
</div>

<section class="panel lab-results" aria-labelledby="collection-title">
  <div class="panel-heading">
    <div><p class="eyebrow">DATA COLLECTION</p><h2 id="collection-title">{!collection ? 'Loading…' : collection.role === 'local' ? `Collecting on ${collection.collector}` : 'Collection off on this instance'}</h2></div>
    <span class="badge">{collection?.receivingRemote ? 'ACCEPTS REMOTE CAPTURES' : 'NO REMOTE COLLECTORS'}</span>
  </div>
  <div class="table-scroll">
    <table>
      <thead><tr><th>Source</th><th>Runs here</th><th>Last capture</th><th>Captures</th><th>State</th></tr></thead>
      <tbody>
        {#each collection?.sources ?? [] as s}
          <tr>
            <td>{s.title}<small>{s.id} · every {s.cadenceHours >= 100 ? 'week' : 'day'} · default {s.defaultHost}</small></td>
            <td>{s.collectedHere ? 'Yes' : 'No'}</td>
            <td>{when(s.lastSuccessAt)}{#if s.lastCollector}<small>by {s.lastCollector}</small>{/if}</td>
            <td>{s.observations}</td>
            <td class:negative={!!s.lastError}>{s.pausedUntil && s.lastError ? `Paused until ${when(s.pausedUntil)}` : s.lastError ? s.lastError : s.lastSuccessAt ? 'OK' : '—'}{#if s.lastError}<small>{s.lastError}</small>{/if}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="dataset-note">Raw files are kept privately on this server, one copy per distinct version, with every check recorded, so an unchanged daily list still counts as evidence for that day. Collection is set with <code>TWENTY_COLLECTOR_ROLE</code>; remote collectors ship here with a private ingest token. See docs/collectors.md.</p>
</section>

<section class="panel compact settings-about" aria-labelledby="about-title">
  <p class="eyebrow">DEPLOYMENT</p>
  <h2 id="about-title">This instance</h2>
  <dl class="capabilities">
    <div><dt>Build</dt><dd>{meta?.commit.slice(0, 7) ?? '—'}</dd></div>
    <div><dt>Mode</dt><dd>{meta?.mode ?? '—'}</dd></div>
    <div><dt>Data</dt><dd>{meta?.dataset ?? '—'}</dd></div>
  </dl>
</section>
