<script lang="ts">
  let { setupRequired, onAuthenticated }: { setupRequired: boolean; onAuthenticated: () => void } = $props();
  let username = $state('');
  let password = $state('');
  let confirm = $state('');
  let setupCode = $state('');
  let busy = $state(false);
  let error = $state('');
  const messages: Record<string, string> = {
    'login-invalid': 'That username and password do not match.',
    'login-locked': 'Too many failed attempts. Sign-in is locked for 15 minutes.',
    'setup-code-invalid': 'That setup code is not valid. Copy it from the container log or your private configuration.',
    'setup-closed': 'An account already exists. Sign in instead.',
    'password-too-short': 'Use a password of at least 12 characters.',
    'password-too-long': 'Use a password of at most 256 characters.',
    'username-invalid': 'Use a username of 1–64 characters.',
  };
  async function submit(e: SubmitEvent) {
    e.preventDefault();
    error = '';
    if (setupRequired && password !== confirm) { error = 'The two passwords do not match.'; return; }
    busy = true;
    try {
      const response = await fetch(setupRequired ? '/api/auth/setup' : '/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Twenty-Request': '1' },
        body: JSON.stringify(setupRequired ? { setupCode, username, password } : { username, password }),
      });
      if (response.status === 429 && !setupRequired) {
        const value = await response.json().catch(() => ({}));
        throw new Error(messages[value.error] ?? 'Too many attempts. Wait a minute and try again.');
      }
      if (!response.ok) {
        const value = await response.json().catch(() => ({}));
        throw new Error(messages[value.error] ?? 'Sign-in is unavailable. Try again.');
      }
      password = confirm = setupCode = '';
      onAuthenticated();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Sign-in failed.';
    } finally {
      busy = false;
    }
  }
</script>

<div class="auth-screen">
  <section class="panel auth-card" aria-labelledby="auth-title">
    <span class="mark" aria-hidden="true">20<i>·</i><span>twenty</span></span>
    <h1 id="auth-title">{setupRequired ? 'Create your account' : 'Sign in'}</h1>
    {#if setupRequired}
      <p class="dataset-note">Enter the one-time setup code from the container log (or <code>TWENTY_SETUP_CODE</code>), then choose a password of at least 12 characters.</p>
    {/if}
    <form class="auth-form" onsubmit={submit}>
      {#if setupRequired}
        <label>Setup code<div class="input-wrap"><input bind:value={setupCode} autocomplete="one-time-code" required spellcheck="false" /></div></label>
      {/if}
      <label>Username<div class="input-wrap"><input bind:value={username} autocomplete="username" required maxlength="64" spellcheck="false" /></div></label>
      <label>Password<div class="input-wrap"><input type="password" bind:value={password} autocomplete={setupRequired ? 'new-password' : 'current-password'} required minlength={setupRequired ? 12 : undefined} /></div></label>
      {#if setupRequired}
        <label>Confirm password<div class="input-wrap"><input type="password" bind:value={confirm} autocomplete="new-password" required minlength="12" /></div></label>
      {/if}
      {#if error}<p class="error" role="alert">{error}</p>{/if}
      <button type="submit" disabled={busy}>{busy ? 'Checking…' : setupRequired ? 'Create account and sign in' : 'Sign in'}</button>
    </form>
  </section>
</div>
