<script lang="ts">
  import { onMount } from 'svelte';
  import ResearchLab from './ResearchLab.svelte';
  import ExecutionLab from './ExecutionLab.svelte';
  type Point = { year: number; contributions: number; balance: number };
  type Scenario = {
    assumptions: {
      monthly: number;
      years: number;
      annualReturn: number;
      target: number;
    };
    points: Point[];
    requiredAnnualReturn: number | null;
  };
  type Issue = {
    number: number;
    title: string;
    url: string;
    milestone: string;
  };
  const money = (n: number) =>
    new Intl.NumberFormat('en-GB', {
      style: 'currency',
      currency: 'GBP',
      maximumFractionDigits: 0,
    }).format(n);
  let section = $state('research');
  let monthly = $state(20);
  let years = $state(30);
  let annualPercent = $state(7);
  let result = $state<Scenario | null>(null);
  let issues = $state<Issue[]>([]);
  let commit = $state('local');
  let executionMode = $state('Research only');
  let busy = $state(false);
  let error = $state('');
  let roadmapError = $state(false);
  const phases = [
    'M1 · Research workbench',
    'M2 · Trustworthy data',
    'M3 · Evidence and models',
    'M4 · Paper portfolio',
  ];
  const final = $derived(result?.points.at(-1));
  const max = $derived(
    Math.max(
      1,
      ...(result?.points.flatMap((p) => [p.balance, p.contributions]) ?? [1]),
    ),
  );
  const plot = (key: 'balance' | 'contributions') =>
    result?.points
      .map(
        (p) =>
          `${48 + (p.year / result!.assumptions.years) * 704},${226 - (p[key] / max) * 196}`,
      )
      .join(' ') ?? '';
  async function calculate() {
    busy = true;
    error = '';
    try {
      const response = await fetch('/api/projections', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Twenty-Request': '1' },
        body: JSON.stringify({
          monthly,
          years,
          annualReturn: annualPercent / 100,
          target: 1000000,
        }),
      });
      if (!response.ok)
        throw new Error(
          response.status === 400
            ? 'Check the values: monthly £1–£100,000, years 1–60, annual return −99%–100%.'
            : 'The projection service is unavailable. Try again.',
        );
      result = await response.json();
    } catch (e) {
      error =
        e instanceof Error
          ? e.message
          : 'Could not load the scenario. Try again.';
    } finally {
      busy = false;
    }
  }
  onMount(() => {
    const navigate = () => {
      const hash = location.hash.slice(1);
      section = ['roadmap', 'evidence', 'workbench', 'execution'].includes(hash) ? hash : 'research';
    };
    navigate();
    window.addEventListener('hashchange', navigate);
    void calculate();
    fetch('/api/roadmap')
      .then((r) => {
        if (!r.ok) throw new Error();
        return r.json();
      })
      .then((data) => (issues = data))
      .catch(() => (roadmapError = true));
    const refreshMode = () => { void fetch('/api/meta').then(r=>r.json()).then(data=>{commit=data.commit.slice(0,7);executionMode=data.mode;}).catch(()=>{}); };
    refreshMode();
    const modeTimer=setInterval(refreshMode,15000);
    return () => { window.removeEventListener('hashchange', navigate);clearInterval(modeTimer); };

  });
</script>

<div class="shell">
  <aside>
    <a class="brand" href="#workbench" aria-label="Twenty workbench"
      ><span class="brand-mark">20<span>·</span></span><span
        >twenty<small>INVESTMENT RESEARCH</small></span
      ></a
    >
    <div class="workspace-label">YOUR WORKSPACE</div>
    <nav aria-label="Main navigation">
      <a href="#research" class:active={section === 'research'} aria-current={section === 'research' ? 'page' : undefined}><span class="nav-icon">⌁</span>Growth lab</a>
      <a href="#execution" class:active={section === 'execution'} aria-current={section === 'execution' ? 'page' : undefined}><span class="nav-icon">⇢</span>Decisions & orders</a>
      <a
        href="#workbench"
        class:active={section === 'workbench'}
        aria-current={section === 'workbench' ? 'page' : undefined}
        ><span class="nav-icon">↗</span>Workbench</a
      >
      <a
        href="#roadmap"
        class:active={section === 'roadmap'}
        aria-current={section === 'roadmap' ? 'page' : undefined}
        ><span class="nav-icon">≡</span>Roadmap</a
      >
      <a
        href="#evidence"
        class:active={section === 'evidence'}
        aria-current={section === 'evidence' ? 'page' : undefined}
        ><span class="nav-icon">⌕</span>Evidence</a
      >
    </nav>
    <div class="sidebar-note">
      <span class="status-dot"></span>{executionMode}
      <p>Build the evidence.<br />Then test the strategy.</p>
    </div>
    <a class="source-link" href="https://github.com/Jellman86/twenty"
      >View project on GitHub ↗</a
    >
  </aside>

  <div class="content">
    <header>
      <span
        >PROJECT TWENTY <span class="slash">/</span>
        {section === 'research' ? 'Speculative growth lab' : section === 'execution' ? 'Decisions & orders' : section === 'workbench'
          ? 'Contribution scenarios'
          : section === 'roadmap'
            ? 'Project roadmap'
            : 'Evidence review'}</span
      ><span class="environment"
        ><span class="status-dot"></span>DEV · {commit}</span
      >
    </header>
    <main id="main">
      {#if section === 'research'}
        <ResearchLab />
      {:else if section === 'execution'}
    <ExecutionLab />
    {:else if section === 'workbench'}
        <div class="page-heading">
          <div>
            <p class="eyebrow">THE £20 EXPERIMENT</p>
            <h1>Small contributions.<br /><em>Testable ideas.</em></h1>
            <p class="intro">
              A research workbench for a long-term GBP portfolio.<br
                class="desktop-break"
              /> Start with the arithmetic. Make every claim earn its place.
            </p>
          </div>
          <div class="mode-stamp">
            <span>INITIAL STAGE</span><strong>Research<br />foundation</strong
            ><small>No broker connected</small>
          </div>
        </div>
        <section class="panel scenario" aria-labelledby="scenario-title">
          <div class="panel-heading">
            <div>
              <p class="eyebrow">CONTRIBUTION SCENARIO</p>
              <h2 id="scenario-title">What can £20 a month become?</h2>
            </div>
            <span class="badge">Illustration · not a forecast</span>
          </div>
          <form
            onsubmit={(e) => {
              e.preventDefault();
              void calculate();
            }}
          >
            <label
              >Monthly contribution <div class="input-wrap">
                <span>£</span><input
                  aria-label="Monthly contribution in pounds"
                  type="number"
                  min="1"
                  max="100000"
                  step="0.01"
                  bind:value={monthly}
                  required
                />
              </div></label
            >
            <label
              >Time horizon <div class="input-wrap">
                <input
                  aria-label="Time horizon in years"
                  type="number"
                  min="1"
                  max="60"
                  step="1"
                  bind:value={years}
                  required
                /><span>years</span>
              </div></label
            >
            <label
              >Assumed annual return <div class="input-wrap">
                <input
                  aria-label="Assumed annual return in percent"
                  type="number"
                  min="-99"
                  max="100"
                  step="0.1"
                  bind:value={annualPercent}
                  required
                /><span>%</span>
              </div></label
            >
            <button disabled={busy} type="submit"
              >{busy ? 'Calculating…' : 'Calculate scenario'}<span
                aria-hidden="true">↗</span
              ></button
            >
          </form>
          <div aria-live="polite">
            {#if error}<p class="error" role="alert">{error}</p>{/if}
          </div>
          {#if result && final}
            <div class="scenario-summary" aria-live="polite">
              <div>
                <span
                  >Illustrated value after {result.assumptions.years} years</span
                ><strong>{money(final.balance)}</strong>
              </div>
              <div>
                <span>Total contributed</span><strong
                  >{money(final.contributions)}</strong
                >
              </div>
              <div class="assumption">
                <span>Annual return assumption</span><strong
                  >{(result.assumptions.annualReturn * 100).toFixed(1)}%</strong
                >
              </div>
            </div>
            <figure>
              <div class="chart-key">
                <span><i class="balance-key"></i>Illustrated value</span><span
                  ><i class="deposit-key"></i>Contributions</span
                >
              </div>
              <svg
                class="projection-chart"
                viewBox="0 0 800 265"
                role="img"
                aria-labelledby="chart-title chart-description"
              >
                <title id="chart-title"
                  >Contribution scenario over {result.assumptions.years} years</title
                ><desc id="chart-description"
                  >A constant {(result.assumptions.annualReturn * 100).toFixed(
                    1,
                  )}% annual return illustrates {money(final.balance)} from {money(
                    final.contributions,
                  )} deposited. Detailed yearly figures are available below.</desc
                >
                {#each [0, 0.5, 1] as step}<line
                    x1="48"
                    y1={226 - step * 196}
                    x2="752"
                    y2={226 - step * 196}
                    class="grid-line"
                  /><text x="48" y={217 - step * 196} class="axis-label"
                    >{money(max * step)}</text
                  >{/each}
                <polyline points={plot('contributions')} class="deposit-line" />
                <polyline points={plot('balance')} class="balance-line" />
                <circle
                  cx="752"
                  cy={226 - (final.balance / max) * 196}
                  r="5"
                  class="endpoint"
                />
                <text x="48" y="254" class="axis-label">Today</text><text
                  x="752"
                  y="254"
                  text-anchor="end"
                  class="axis-label">Year {result.assumptions.years}</text
                >
              </svg>
              <figcaption>
                End-of-month deposits. Constant nominal returns with monthly
                compounding. Excludes inflation, fees and tax; real returns vary
                and losses are possible.
              </figcaption>
            </figure>
            <details>
              <summary>View yearly figures</summary>
              <div class="table-scroll">
                <table>
                  <caption>Scenario values in nominal GBP</caption><thead
                    ><tr
                      ><th>Year</th><th>Contributed</th><th
                        >Illustrated value</th
                      ></tr
                    ></thead
                  ><tbody
                    >{#each result.points as point}<tr
                        ><td>{point.year}</td><td
                          >{money(point.contributions)}</td
                        ><td>{money(point.balance)}</td></tr
                      >{/each}</tbody
                  >
                </table>
              </div>
            </details>
            <div class="target-note">
              <span class="target-symbol" aria-hidden="true">◎</span>
              <p>
                <strong>The £1 million question</strong>With {money(
                  result.assumptions.monthly,
                )} a month over {result.assumptions.years} years, reaching £1m would
                require {result.requiredAnnualReturn === null
                  ? 'an annual return outside the supported calculation range'
                  : `approximately ${(result.requiredAnnualReturn * 100).toFixed(1)}% every year`}.
                This is a mathematical requirement, not an expected or promised
                outcome.
              </p>
            </div>
          {:else if !error}<p class="loading" role="status">
              Loading the first scenario…
            </p>{/if}
        </section>
        <div class="bottom-grid">
          <section class="panel compact">
            <p class="eyebrow">NEXT RESEARCH GATE</p>
            <h2>Trust the data first.</h2>
            <p>
              Historical prices, delisted stocks and information availability
              need an audit before any strategy result is credible.
            </p>
            <a href="#roadmap" class="text-link"
              >Explore the roadmap <span>→</span></a
            >
          </section>
          <section class="panel compact">
            <p class="eyebrow">CURRENT CAPABILITIES</p>
            <dl class="capabilities">
              <div>
                <dt>Research harness</dt>
                <dd>Available · offline</dd>
              </div>
              <div>
                <dt>Historical dataset</dt>
                <dd>Not connected</dd>
              </div>
              <div>
                <dt>Broker / live orders</dt>
                <dd>Not connected / disabled</dd>
              </div>
            </dl>
            <a href="#evidence" class="text-link"
              >Read the evidence <span>→</span></a
            >
          </section>
        </div>
      {:else if section === 'roadmap'}
        <div class="page-heading">
          <div>
            <p class="eyebrow">FROM IDEA TO EVIDENCE</p>
            <h1>The project roadmap.</h1>
            <p class="intro">
              Four milestones. Each step has a testable acceptance gate.<br
              />GitHub issues are the source of truth for progress.
            </p>
          </div>
          <a
            class="button secondary"
            href="https://github.com/users/Jellman86/projects/1"
            >Open project board ↗</a
          >
        </div>
        {#if roadmapError}<p class="error" role="alert">
            Could not load the roadmap. <a
              href="https://github.com/Jellman86/twenty/issues"
              >View the issues on GitHub.</a
            >
          </p>{/if}
        {#each phases as phase, i}<section class="panel roadmap-phase">
            <div class="phase-heading">
              <span class="phase-number">{String(i + 1).padStart(2, '0')}</span>
              <div>
                <p class="eyebrow">MILESTONE {i + 1}</p>
                <h2>{phase.split(' · ')[1]}</h2>
              </div>
            </div>
            <ul class="issue-list">
              {#each issues.filter((issue) => issue.milestone === phase) as issue}<li
                >
                  <a href={issue.url}
                    ><span class="issue-number">#{issue.number}</span><span
                      >{issue.title}</span
                    ><span aria-hidden="true">↗</span></a
                  >
                </li>{/each}
            </ul>
          </section>{/each}
      {:else}
        <div class="page-heading">
          <div>
            <p class="eyebrow">ACADEMIC GROUNDING</p>
            <h1>Evidence before confidence.</h1>
            <p class="intro">
              24 primary studies reviewed, with access depth and limitations
              recorded.<br />Published performance does not establish achievable
              ISA returns.
            </p>
          </div>
        </div>
        <section class="panel compact evidence-intro">
          <h2>Our starting hypotheses</h2>
          <p>
            Compare simple baselines first, preserve a final chronological
            holdout and register every trial. Filing changes are the first
            distinctive signal to investigate once point-in-time data is
            available.
          </p>
          <a
            class="text-link"
            href="https://github.com/Jellman86/twenty/blob/dev/docs/literature-review.md"
            >Read the full critical review ↗</a
          >
        </section>
        <div class="evidence-grid">
          <article class="panel compact">
            <p class="eyebrow">TRAINED MODELS</p>
            <h2>Can complexity help?</h2>
            <p>
              Gu, Kelly & Xiu (2020) find gains from machine learning in asset
              pricing. Transfer to a small long-only ISA, net of attainable
              costs, remains an experiment.
            </p>
            <a href="https://doi.org/10.1093/rfs/hhaa009" class="text-link"
              >Read the paper ↗</a
            >
          </article>
          <article class="panel compact">
            <p class="eyebrow">BASELINE DISCIPLINE</p>
            <h2>Beat a simple comparator.</h2>
            <p>
              DeMiguel, Garlappi & Uppal (2009) show the difficulty of reliably
              beating equal weighting out of sample. Twenty retains
              same-universe equal weight as a challenger.
            </p>
            <a href="https://doi.org/10.1093/rfs/hhm075" class="text-link"
              >Read the paper ↗</a
            >
          </article>
          <article class="panel compact">
            <p class="eyebrow">FILING CHANGES</p>
            <h2>Read what changed.</h2>
            <p>
              Cohen, Malloy & Nguyen (2020), “Lazy Prices”, motivates testing
              changes in company filings. Availability timestamps, coverage and
              post-publication decay need explicit checks.
            </p>
            <a href="https://doi.org/10.1111/jofi.12885" class="text-link"
              >Read the paper ↗</a
            >
          </article>
          <article class="panel compact">
            <p class="eyebrow">MULTIPLE TESTING</p>
            <h2>Count every attempt.</h2>
            <p>
              Bailey and colleagues (2017) show why selecting a winner from many
              backtests can mislead. Faster hardware must not hide failed or
              discarded trials.
            </p>
            <a href="https://doi.org/10.21314/JCF.2016.322" class="text-link"
              >Read the paper ↗</a
            >
          </article>
        </div>
      {/if}
      <footer>
        <span
          >TWENTY <span class="slash">/</span> £20 is the starting point. Evidence
          is the method.</span
        ><span>{executionMode}</span>
      </footer>
    </main>
  </div>
</div>
