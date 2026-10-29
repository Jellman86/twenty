<script lang="ts">
  import { onMount } from 'svelte';
  import RawPaperAccounts from './RawPaperAccounts.svelte';
  type Study = { id: string; name: string; status: string; planSha256: string; selectionReportSha256?: string | null; selectedCandidateId?: string | null; plan: { synthetic: boolean; codeSha256: string; monthlyGbp: number } };
  let currentCode = $state(''), studies = $state<Study[]>([]), ready = $state(false), error = $state('');
  // Same eligibility rule as the Research raw-study page: current-build authored studies with a frozen candidate.
  const sources = $derived(studies.filter((s) => s.plan.synthetic && s.plan.codeSha256 === currentCode && s.selectionReportSha256 && s.selectedCandidateId && ['candidate-frozen', 'engineering-complete'].includes(s.status))
    .map((s) => ({ id: s.id, name: s.name, planSha256: s.planSha256, selectionReportSha256: s.selectionReportSha256!, selectedCandidateId: s.selectedCandidateId!, monthlyGbp: s.plan.monthlyGbp })));
  const studyNames = $derived(Object.fromEntries(studies.map((s) => [s.id, s.name])));
  onMount(() => {
    void Promise.all([
      fetch('/api/research/capabilities').then((r) => { if (!r.ok) throw new Error(); return r.json(); }).then((c) => (currentCode = c.researchIdentity.sha256)),
      fetch('/api/research/raw-studies').then((r) => { if (!r.ok) throw new Error(); return r.json(); }).then((r) => (studies = r.studies ?? [])),
    ]).catch(() => (error = 'Study details are unavailable, so accounts are read-only. Refresh to try again.')).finally(() => (ready = true));
  });
</script>

{#if error}<p class="error" role="alert">{error}</p>{/if}
{#if ready}
  <RawPaperAccounts standalone {sources} {currentCode} {studyNames} />
{:else}
  <div class="page-head"><div class="title"><p class="eyebrow">Strategy lab</p><h1>Paper trading</h1><p class="lede">Pretend accounts that follow one researched strategy month by month with fake deposits, so it can be judged before real money is involved.</p></div></div>
  <p class="loading" role="status">Loading…</p>
{/if}
