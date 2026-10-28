<script lang="ts">
  import { onMount } from 'svelte';
  import ResearchLab from './ResearchLab.svelte';
  import ResearchExtras from './ResearchExtras.svelte';
  import ExecutionLab from './ExecutionLab.svelte';
  import Login from './Login.svelte';
  import Portfolio from './Portfolio.svelte';
  import Activity from './Activity.svelte';
  import PaperAccounts from './PaperAccounts.svelte';
  import Settings from './Settings.svelte';
  type AuthState = { authenticated: boolean; username: string | null; setupRequired: boolean; passwordChangedAt: string | null; authDisabled?: boolean };
  let auth = $state<AuthState | null>(null);
  let authError = $state(false);
  // Older bookmarks (#workbench, #roadmap, #evidence) stay valid as Research sub-pages.
  const sections = ['portfolio', 'activity', 'execution', 'paper', 'research', 'workbench', 'roadmap', 'evidence', 'settings'];
  const research = ['research', 'workbench', 'roadmap', 'evidence'];
  const nav = [
    ['portfolio', 'Portfolio'],
    ['activity', 'Activity'],
    ['execution', 'Orders'],
    ['paper', 'Paper accounts'],
    ['research', 'Research'],
    ['settings', 'Settings'],
  ];
  let section = $state('portfolio');
  // Never assert the execution mode without evidence: unknown until /api/meta answers.
  let mode = $state<string | null>(null);
  const navSection = $derived(research.includes(section) ? 'research' : section);
  const modePill = $derived(
    mode === null ? { kind: '', label: 'Checking…' }
      : mode.startsWith('Live') ? { kind: 'live', label: 'Live' }
      : mode.startsWith('Demo') ? { kind: 'demo', label: 'Demo' }
      : mode === 'Research only' ? { kind: '', label: 'Research only' }
      : { kind: '', label: 'Mode unknown' },
  );
  async function checkAuth() {
    try {
      const response = await fetch('/api/auth/state');
      if (!response.ok) throw new Error();
      auth = await response.json();
      authError = false;
    } catch {
      authError = true;
    }
  }
  onMount(() => {
    const navigate = () => {
      const hash = location.hash.slice(1);
      section = sections.includes(hash) ? hash : 'portfolio';
    };
    navigate();
    window.addEventListener('hashchange', navigate);
    const expired = () => { if (auth?.authenticated) auth = { ...auth, authenticated: false }; void checkAuth(); };
    window.addEventListener('twenty:unauthorized', expired);
    void checkAuth();
    return () => { window.removeEventListener('hashchange', navigate); window.removeEventListener('twenty:unauthorized', expired); };
  });
  // Poll the server mode only while signed in.
  const signedIn = $derived(auth?.authenticated === true);
  $effect(() => {
    if (!signedIn) return;
    const refreshMode = () => {
      void fetch('/api/meta').then((r) => { if (!r.ok) throw new Error(); return r.json(); })
        .then((data) => (mode = data.mode)).catch(() => (mode = 'unknown'));
    };
    refreshMode();
    const timer = setInterval(refreshMode, 15000);
    return () => clearInterval(timer);
  });
</script>

{#if !auth}
  <div class="auth-screen">
    {#if authError}<p class="error" role="alert">Twenty is not responding. <button class="secondary small-button" onclick={() => void checkAuth()}>Try again</button></p>{:else}<p class="loading">Loading…</p>{/if}
  </div>
{:else if !auth.authenticated}
  <Login setupRequired={auth.setupRequired} onAuthenticated={() => void checkAuth()} />
{:else}
  <header class="topbar">
    <a class="mark" href="#portfolio" aria-label="Twenty portfolio">20<i>·</i><span>twenty</span></a>
    <nav class="topnav" aria-label="Main">
      {#each nav as [id, label]}
        <a href={`#${id}`} class:active={navSection === id} aria-current={navSection === id ? 'page' : undefined}>{label}</a>
      {/each}
    </nav>
    <a class="pill {modePill.kind}" href="#settings" title={mode ?? 'Checking execution mode'}><span class="d"></span>{modePill.label}</a>
  </header>
  <main id="main">
    {#if section === 'portfolio'}
      <Portfolio />
    {:else if section === 'activity'}
      <Activity />
    {:else if section === 'execution'}
      <ExecutionLab />
    {:else if section === 'paper'}
      <PaperAccounts />
    {:else if section === 'settings'}
      <Settings username={auth.username ?? ''} passwordChangedAt={auth.passwordChangedAt} authDisabled={auth.authDisabled === true} onSignOut={() => void checkAuth()} />
    {:else}
      <div class="page-head">
        <h1>{section === 'research' ? 'Research' : section === 'workbench' ? 'Scenarios' : section === 'roadmap' ? 'Roadmap' : 'Evidence'}</h1>
        <nav class="subnav" aria-label="Research">
          <a href="#research" class:active={section === 'research'}>Studies</a>
          <a href="#workbench" class:active={section === 'workbench'}>Scenarios</a>
          <a href="#roadmap" class:active={section === 'roadmap'}>Roadmap</a>
          <a href="#evidence" class:active={section === 'evidence'}>Evidence</a>
        </nav>
      </div>
      {#if section === 'research'}<ResearchLab />{:else}<ResearchExtras view={section} />{/if}
    {/if}
  </main>
{/if}
