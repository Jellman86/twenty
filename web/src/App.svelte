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
  // Plain-language map of the app: trading pages first, strategy research second.
  type NavItem = { id: string; label: string; short: string; hint: string; icon: string };
  const groups: { title: string; items: NavItem[] }[] = [
    { title: 'Trading', items: [
      { id: 'portfolio', label: 'Portfolio', short: 'Portfolio', hint: 'What you own now', icon: 'M4 19V9m5.33 10V5m5.34 14v-7M20 19V8' },
      { id: 'activity', label: 'Activity', short: 'Activity', hint: 'Orders and fills', icon: 'M3 12h4l3 7 4-14 3 7h4' },
      { id: 'execution', label: 'Orders', short: 'Orders', hint: 'Run or pause trading', icon: 'M7 4v16m0 0-3-3m3 3 3-3M17 20V4m0 0-3 3m3-3 3 3' },
    ] },
    { title: 'Strategy lab', items: [
      { id: 'research', label: 'Research', short: 'Research', hint: 'Test strategies on past data', icon: 'M9 3h6M10 3v6.5L4.6 18.2A1.8 1.8 0 0 0 6.1 21h11.8a1.8 1.8 0 0 0 1.5-2.8L14 9.5V3M7.5 15h9' },
      { id: 'paper', label: 'Paper trading', short: 'Paper', hint: 'Practice accounts, no real money', icon: 'M6 3h9l4 4v14H6zM14 3v5h5M9 13h7M9 17h5' },
    ] },
  ];
  const settingsItem: NavItem = { id: 'settings', label: 'Settings', short: 'Settings', hint: 'Sign-in, broker and data', icon: 'M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4ZM19.4 13.5l1.6 1.2-1.9 3.3-1.9-.7a7.4 7.4 0 0 1-2.1 1.2L14.8 21h-3.6l-.3-2.5a7.4 7.4 0 0 1-2.1-1.2l-1.9.7L5 14.7l1.6-1.2a7.6 7.6 0 0 1 0-3L5 9.3 6.9 6l1.9.7a7.4 7.4 0 0 1 2.1-1.2L11.2 3h3.6l.3 2.5a7.4 7.4 0 0 1 2.1 1.2l1.9-.7L21 9.3l-1.6 1.2a7.6 7.6 0 0 1 0 3Z' };
  const researchPages: Record<string, { title: string; lede: string }> = {
    research: { title: 'Research', lede: 'Test strategy ideas on historical data before any of them is trusted with money. Every attempt is kept, including the ones that failed.' },
    workbench: { title: 'Scenarios', lede: 'See how regular monthly contributions could grow under a return you choose. This is arithmetic, not a forecast.' },
    roadmap: { title: 'Roadmap', lede: 'What is being built next, from the project board.' },
    evidence: { title: 'Evidence', lede: 'What Twenty’s own tests on real data have found so far, and the published research behind the strategies.' },
  };
  type ThemePref = 'system' | 'light' | 'dark';
  const themeOptions: [ThemePref, string, string][] = [
    ['system', 'Match this device', 'M4 5h16v11H4zM9 20h6M12 16v4'],
    ['light', 'Light theme', 'M12 4V2m0 20v-2m8-8h2M2 12h2m13.66-5.66 1.41-1.41M4.93 19.07l1.41-1.41m0-11.32L4.93 4.93m14.14 14.14-1.41-1.41M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z'],
    ['dark', 'Dark theme', 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z'],
  ];
  const storedTheme = (() => { try { return localStorage.getItem('twenty-theme'); } catch { return null; } })();
  let themePref = $state<ThemePref>(storedTheme === 'light' || storedTheme === 'dark' ? storedTheme : 'system');
  // Mirrors public/theme-init.js, which applies the same rule before first paint.
  $effect(() => {
    const pref = themePref;
    const media = window.matchMedia('(prefers-color-scheme: light)');
    const apply = () => {
      const light = pref === 'light' || (pref === 'system' && media.matches);
      document.documentElement.dataset.theme = light ? 'light' : 'dark';
      document.querySelector('meta[name=theme-color]')?.setAttribute('content', light ? '#f1f2f4' : '#111214');
    };
    apply();
    try { if (pref === 'system') localStorage.removeItem('twenty-theme'); else localStorage.setItem('twenty-theme', pref); } catch { /* storage blocked */ }
    if (pref !== 'system') return;
    media.addEventListener('change', apply);
    return () => media.removeEventListener('change', apply);
  });
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
  const modeHint = $derived(
    mode === null ? 'Checking whether orders are enabled.'
      : mode.startsWith('Live') ? 'Orders spend real money.'
      : mode.startsWith('Demo') ? 'Orders go to a Trading 212 demo account.'
      : mode === 'Research only' ? 'Order execution is off. Nothing is sent to a broker.'
      : 'Could not read the order mode.',
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
  <a class="skip-link" href="#main">Skip to content</a>
  <div class="shell">
    <aside class="sidebar">
      <a class="mark" href="#portfolio" aria-label="Twenty portfolio">20<i>·</i><span>twenty</span></a>
      <nav class="sidenav" aria-label="Main">
        {#each groups as group}
          <p class="nav-group" aria-hidden="true">{group.title}</p>
          {#each group.items as item}{@render navLink(item)}{/each}
        {/each}
        <div class="nav-end">{@render navLink(settingsItem)}</div>
      </nav>
      <div class="side-foot">
        <div class="theme-switch" role="group" aria-label="Colour theme">
          {#each themeOptions as [id, label, icon]}
            <button type="button" aria-pressed={themePref === id} aria-label={label} title={label} onclick={() => (themePref = id)}>
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d={icon} /></svg>
            </button>
          {/each}
        </div>
        <a class="mode-card" href="#settings" title={mode ?? 'Checking execution mode'}>
          <span class="pill {modePill.kind}"><span class="d"></span>{modePill.label}</span>
          <small>{modeHint}</small>
        </a>
      </div>
    </aside>
    <main id="main" tabindex="-1">
      {#key navSection}
        <div class="page">
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
              <div class="title">
                <p class="eyebrow">Strategy lab</p>
                <h1>{researchPages[section].title}</h1>
                <p class="lede">{researchPages[section].lede}</p>
              </div>
              <nav class="subnav" aria-label="Research">
                <a href="#research" class:active={section === 'research'} aria-current={section === 'research' ? 'page' : undefined}>Studies</a>
                <a href="#workbench" class:active={section === 'workbench'} aria-current={section === 'workbench' ? 'page' : undefined}>Scenarios</a>
                <span class="subnav-sep" aria-hidden="true"></span>
                <a class="quiet" href="#roadmap" class:active={section === 'roadmap'} aria-current={section === 'roadmap' ? 'page' : undefined}>Roadmap</a>
                <a class="quiet" href="#evidence" class:active={section === 'evidence'} aria-current={section === 'evidence' ? 'page' : undefined}>Evidence</a>
              </nav>
            </div>
            {#if section === 'research'}<ResearchLab />{:else}<ResearchExtras view={section} />{/if}
          {/if}
        </div>
      {/key}
    </main>
  </div>
{/if}

{#snippet navLink(item: NavItem)}
  <a href={`#${item.id}`} class:active={navSection === item.id} aria-current={navSection === item.id ? 'page' : undefined} title={item.label}>
    <svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true"><path d={item.icon} /></svg>
    <span class="nav-text">
      <span class="nav-label"><span class="long">{item.label}</span><span class="short">{item.short}</span></span>
      <span class="nav-hint" aria-hidden="true">{item.hint}</span>
    </span>
  </a>
{/snippet}
