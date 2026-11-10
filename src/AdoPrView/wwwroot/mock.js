'use strict';
// Fixture data + fake host used when the UI runs outside the app or with ?mock=1.
(function () {
  const now = Date.now();
  const iso = (mins) => new Date(now - mins * 60000).toISOString();
  const org = 'contoso', project = 'Sample';
  const url = (repo, id) => `https://dev.azure.com/${org}/${project}/_git/${repo}/pullrequest/${id}`;
  const me = { name: 'Sam Example', uniqueName: 'sam@contoso.example', id: 'me' };
  const g = (state, label, detail) => ({ state, label, detail });
  const pol = (kind, name, status, extra = {}) => ({ evaluationId: Math.random().toString(16).slice(2), kind, name, status, isBlocking: true, buildId: null, buildUrl: null, isExpired: false, ...extra });
  const base = (id, repo, title, lane, o = {}) => ({
    key: `${repo}-id:${id}`, id, title, url: url(repo, id), org, project, projectId: 'p', repo, repoId: `${repo}-id`,
    sourceBranch: o.branch || `user/sam/${title.toLowerCase().replace(/[^a-z0-9]+/g, '-').slice(0, 38)}`, targetBranch: 'main',
    author: o.author || me, isMine: !o.author, isHidden: false, isDraft: !!o.draft,
    isActive: true,
    reviewVote: { reviewerId: me.id, reviewerName: me.name, vote: 0, canApprove: true,
      detail: 'Offline demonstration only. This records no real vote; other reviewers and policies remain unchanged.',
      target: { reviewerId: me.id, sourceCommit: 'a'.repeat(40), targetCommit: 'b'.repeat(40),
        sourceRef: `refs/heads/${o.branch || 'sample-change'}`, targetRef: 'refs/heads/main', iterationId: 4, vote: 0 } },
    approval: o.approval === undefined && repo === 'sample-service' ? { title: 'Request approval via the sample provider for only this PR' } : o.approval || null,
    created: iso(o.created || 60 * 30), updated: iso(o.updated || 20), description: o.description || '',
    mergeStatus: o.mergeStatus || 'succeeded', mergeFailure: null, mergeStale: !!o.stale, lastMergeTargetCommit: 'abc',
    autoCompleteBy: o.autoCompleteBy ?? null, mergeStrategy: o.autoCompleteBy ? 'squash' : null, lane,
    blockers: o.blockers || [], waiting: o.waiting || [], humanActions: o.human || [], gates: o.gates, policies: o.policies || [],
    failedBuilds: o.failedBuilds || [], threads: o.threads || [], reviewers: o.reviewers || [], agency: o.agency || { state: 'clean', detail: 'No issues on iteration #4' }, error: null,
  });
  const okGates = {
    merge: g('ok', 'Clean'), build: g('ok', 'Passed'), checks: g('ok', '5 passed'), comments: g('ok', 'Resolved'),
    agency: g('ok', 'Clean'), reviews: g('ok', '2 ✓'), pop: g('ok', 'Done'), autocomplete: g('ok', 'squash'),
  };
  const desc = `## Summary

Moves the **sample-agent** reconciliation loop onto the shared \`workqueue\` primitives and removes the bespoke retry timer.

### Changes
- Replace \`retryTimer\` with \`workqueue.RateLimitingInterface\`
- Add jittered exponential back-off (base 2s, max 5m)
- Emit \`sample_agent_requeue_total\` metric

### Validation
| Check | Result |
|---|---|
| \`make code-qa\` | ✅ |
| envtest | ✅ 212 passed |

Fixes AB#1234`;

  const logText = Array.from({ length: 60 }, (_, i) => {
    const t = new Date(now - (60 - i) * 1000).toISOString().replace(/\.\d+Z$/, '.1234567Z');
    const lines = [
      '=== RUN   TestReconcile/requeue_on_conflict',
      '    reconcile_test.go:142: expected requeue after 2s, got 0s',
      '--- FAIL: TestReconcile/requeue_on_conflict (0.03s)',
      '=== RUN   TestReconcile/happy_path',
      '--- PASS: TestReconcile/happy_path (0.01s)',
      'ok  	sample-service/internal/agent/health	0.412s',
      'FAIL	sample-service/internal/agent	3.118s',
      '##[error]Bash exited with code \'2\'.',
      '##[section]Finishing: Run unit tests',
    ];
    return `${t} ${i > 50 ? lines[i - 51] : `go test ./internal/... -count=1 -race  [pkg ${i}]`}`;
  }).join('\n');

  const prs = [
    base(101, 'sample-service', 'Add per-cluster health probe for sample agent', 'needsHuman', {
      human: ['pop', 'autocomplete'], updated: 8, description: desc,
      gates: { ...okGates, pop: g('human', 'Tap key'), autocomplete: g('human', 'Not set') },
      reviewers: [{ name: 'Riley Reviewer', vote: 10, isRequired: true, isContainer: false }, { name: '[Sample]\\service-owners', vote: 10, isRequired: true, isContainer: true }],
      policies: [pol('build', 'sample-service-PR', 'approved', { buildId: 991231, buildUrl: '#' }), pol('pop', 'Proof Of Presence', 'rejected'), pol('mergeStrategy', 'Require a merge strategy', 'rejected'), pol('status', 'ci/security-scan', 'approved'), pol('review', 'Required reviewers', 'approved')],
    }),
    base(102, 'sample-deploy', 'Bump sample-api chart to 1.42.0', 'needsHuman', {
      author: { name: 'Sample Build Service', uniqueName: 'build-service@contoso.example', id: 'svc' },
      human: ['pop'], autoCompleteBy: 'Sam Example', updated: 45, branch: 'bot/bump-sample-api-1.42.0',
      gates: { ...okGates, pop: g('human', 'Tap key') },
    }),
    base(103, 'sample-service', 'build-ops: awaiting human reviewer approval', 'needsHuman', {
      human: ['reviews'], autoCompleteBy: 'Sam Example', updated: 1,
      description: 'Offline fixture: only another human reviewer approval is needed. The review bot completed with partial coverage (a warning, not work still running). Builds, checks, PoP and auto-complete are satisfied.',
      agency: { state: 'partial', detail: 'Review Bot completed with partial coverage on iteration #4' },
      gates: { ...okGates, agency: g('warn', 'Partial', 'Review Bot completed with partial coverage on iteration #4'), reviews: g('human', '1 ✓ +more', 'Minimum number of reviewers') },
      reviewers: [{ name: 'Sam Example', vote: 10, isRequired: false, isContainer: false }, { name: 'Riley Reviewer', vote: 0, isRequired: true, isContainer: false }],
      policies: [pol('review', 'Minimum number of reviewers', 'rejected')],
    }),
    base(104, 'sample-service', 'SKU catalog: waiting for PoP and human sign-off', 'needsHuman', {
      approval: { title: 'Request approval via the sample provider for only this PR', detail: 'Synthetic provider detail shown as plain text.' },
      human: ['reviews', 'pop'], autoCompleteBy: 'Sam Example', updated: 2,
      description: 'Offline fixture: the review bot reviewed iteration #1 and its finding is resolved. ADO updates through iteration #7 do not start another bot review. Only PoP and human sign-off remain.',
      agency: { state: 'addressed', iteration: 1, currentIteration: 7, detail: 'Review Bot findings from iteration #1 resolved (PR now at iteration #7; updates do not trigger another Review Bot review)' },
      gates: { ...okGates, agency: g('ok', 'Addressed', 'Initial Review Bot finding resolved; no new run is assumed after ADO updates'), reviews: g('human', '1 ✓ +more', 'Additional human sign-off is required on the latest iteration'), pop: g('human', 'Tap key') },
      reviewers: [{ name: 'Jane Doe', id: '00000000-0000-4000-8000-0000000000d0', vote: 10, isRequired: false, isContainer: false }],
      policies: [pol('review', 'Latest iteration sign-off', 'rejected', { isAutomatedReview: true, statusDescription: 'Latest iteration sign-off - Additional human sign-off is required on the latest iteration.' }), pol('pop', 'Proof Of Presence', 'rejected')],
    }),
    base(105, 'sample-service', 'Move reconciliation loop onto shared workqueue', 'blocked', {
      blockers: ['Build failed: sample-service-PR', '2 unresolved comment thread(s)'], human: ['reviews', 'autocomplete'], updated: 32, description: desc,
      gates: { ...okGates, build: g('fail', 'Failed', 'sample-service-PR'), comments: g('fail', '2 open'), reviews: g('human', 'Needs +1'), pop: g('human', 'Tap key'), autocomplete: g('human', 'Not set') },
      failedBuilds: [{ buildId: 991200, name: 'sample-service-PR', url: '#', evaluationId: 'e1', tasks: [
        { name: 'Run unit tests', path: 'Validate › Unit', result: 'failed', logId: 41, issues: ["Bash exited with code '2'.", 'reconcile_test.go:142: expected requeue after 2s, got 0s'] },
        { name: 'Publish test results', path: 'Validate › Unit', result: 'failed', logId: 44, issues: ['There are one or more test failures.'] },
      ] }],
      threads: [
        { id: 88121, status: 'active', author: 'Riley Reviewer', isBot: false, isAgency: false, content: 'Should the back-off cap be configurable? 5m seems long for **edge** sites with flaky links.', replies: 1, filePath: '/internal/agent/reconcile.go', line: 118, updated: iso(50), url: '#' },
        { id: 88140, status: 'active', author: 'Build Bot', isBot: true, isAgency: false, content: 'This PR touches `go.mod`. Please make sure `make vendor` was run.', replies: 0, filePath: null, line: null, updated: iso(90), url: '#' },
      ],
      reviewers: [{ name: 'Riley Reviewer', vote: 0, isRequired: true, isContainer: false }, { name: 'Jane Doe', vote: 5, isRequired: false, isContainer: false }],
      policies: [pol('build', 'sample-service-PR', 'rejected', { buildId: 991200, buildUrl: '#' }), pol('comments', 'Comment requirements', 'rejected'), pol('review', 'Required reviewers', 'rejected'), pol('pop', 'Proof Of Presence', 'rejected')],
    }),
    base(106, 'sample-operator', 'Refactor operator CRD validation webhooks', 'blocked', {
      mergeStatus: 'conflicts', blockers: ['Merge conflicts with main'], human: ['pop', 'autocomplete'], updated: 180,
      gates: { ...okGates, merge: g('fail', 'Conflicts', 'Conflicts with main'), build: g('warn', 'Expired'), pop: g('human', 'Tap key'), autocomplete: g('human', 'Not set') },
    }),
    base(107, 'sample-service', 'Address review bot findings on iteration 3', 'blocked', {
      author: { name: 'Riley Reviewer', uniqueName: 'riley@contoso.example', id: 'riley' }, autoCompleteBy: 'Riley Reviewer',
      blockers: ['1 open Review Bot finding thread(s) on iteration #3'], updated: 12,
      agency: { state: 'findings', detail: '1 open Review Bot finding thread(s) on iteration #3' },
      gates: { ...okGates, agency: g('fail', 'Findings'), checks: g('fail', '1 failed', 'ci/security-scan'), pop: g('na', '—') },
    }),
    base(108, 'sample-api', 'Expose cluster lifecycle events over gRPC stream', 'waiting', {
      waiting: ['Build running: sample-api-PR'], human: ['reviews'], autoCompleteBy: 'Sam Example', updated: 3,
      agency: { state: 'clean', iteration: 1, currentIteration: 2, detail: 'No issues found in Review Bot review of iteration #1; ADO updates do not trigger another review' },
      gates: { ...okGates, build: g('pending', 'Running'), reviews: g('human', '1 ✓ +more'), pop: g('ok', 'Done') },
    }),
    base(109, 'sample-service', 'WIP: experiment with structured logging', 'needsHuman', {
      draft: true, human: ['reviews', 'publish', 'autocomplete'], updated: 240,
      agency: { state: 'none', detail: 'No Review Bot review reported on this PR; no active run is confirmed' },
      gates: { ...okGates, build: g('na', 'None'), reviews: g('human', 'Needs +1'), agency: g('warn', 'No report'), pop: g('na', '—'), autocomplete: g('human', 'Not set') },
    }),
    base(110, 'sample-deploy', 'Rotate ingress certificates for sample sites', 'ready', {
      autoCompleteBy: 'Sam Example', updated: 1, gates: okGates,
    }),
  ];

  const settings = {
    repos: [
      { org, project, repo: 'sample-service' }, { org, project, repo: 'sample-operator' },
      { org, project, repo: 'sample-api' }, { org, project, repo: 'sample-deploy' },
    ],
    whitelist: ['riley', 'Sample Build Service'],
    reviewBot: { name: 'Review Bot', authorId: '00000000-0000-4000-8000-0000000000b0', organization: org },
    hidden: ['sample-deploy-id:1001'],
    refreshSeconds: 120, mergeCheckMinutes: 10, notifications: true,
    autoComplete: { mergeStrategy: 'squash', deleteSourceBranch: true, transitionWorkItems: true },
    triageInstructions: null,
    wslDistro: null, wslRepoDirectories: {},
  };
  const snapshot = { prs, lastRefresh: iso(0.4), refreshing: false, error: null, repoErrors: [], me: 'Sam Example',
    approvalProvider: { name: 'Sample approval', linkUrl: null, linkLabel: null }, approvalProviderError: null };
  const runs = new Map();

  window.MockHost = {
    onEvent: null,
    async rpc(method, p) {
      await new Promise((r) => setTimeout(r, 150));
      switch (method) {
        case 'init': return { settings, snapshot, runs: [...runs.values()], version: 'mock' };
        case 'logTail': return logText;
        case 'triage': case 'copyPrompt': return { prompt: 'mock' };
        case 'openPop': return { message: `Mock inline PoP for ${p.key}; no browser, sign-in or approval was started. The installed app uses a separate native Edge WebView2 pane.` };
        case 'startCopilot': {
          if (['preparing', 'running'].includes(runs.get(p.key)?.status)) throw new Error('A run is already active for this PR.');
          const pr = prs.find(x => x.key === p.key);
          const run = { key: pr.key, id: pr.id, title: pr.title, repo: pr.repo, runId: 'mock-' + Date.now(),
            status: 'running', message: 'Mock run: no process or live fix was started.', exitCode: null,
            worktreePath: '/home/offline/.local/share/ado-prview/worktrees/mock-' + pr.id,
            worktreeRetained: false, output: 'Mock: isolated PR worktree ready.\nStarting fake Copilot output...\n', updatedAt: new Date().toISOString() };
          runs.set(p.key, run);
          setTimeout(() => {
            if (runs.get(p.key)?.status !== 'running') return;
            const done = { ...run, status: 'completed', exitCode: 0, message: 'Mock CLI exited normally. PR fix was not verified.',
              output: run.output + 'Mock stdout: inspecting failed gate.\nMock stderr: sample diagnostic.\nFake CLI exited 0; no live changes were made.\n', updatedAt: new Date().toISOString() };
            runs.set(p.key, done); this.onEvent?.({ event: 'copilotRun', data: done });
          }, 1200);
          return run;
        }
        case 'cancelCopilot': {
          const run = runs.get(p.key);
          if (!run || run.runId !== p.runId) throw new Error('This run is no longer current.');
          const cancelled = { ...run, status: 'cancelled', message: 'Mock run cancelled; worktree preserved.', worktreeRetained: true };
          runs.set(p.key, cancelled);
          this.onEvent?.({ event: 'copilotRun', data: cancelled });
          return cancelled;
        }
        case 'approvalSettings': return { message: 'Mock approval provider: nothing is stored.', help: null, actions: [] };
        case 'refresh':
          snapshot.orderRevision = (snapshot.orderRevision || 0) + 1;
          this.onEvent?.({ event: 'snapshot', data: { ...snapshot, prs: [...prs] } });
          return null;
        case 'restartMerge': return { requested: true, message: `Mock merge re-check for !${prs.find(x => x.key === p.key).id}; no live request submitted.` };
        case 'generateApproval': {
          const pr = prs.find((x) => x.key === p.key);
          if (!pr?.approval) throw new Error('No approval is offered for this pull request.');
          await new Promise((r) => setTimeout(r, 700));
          const failed = new URLSearchParams(location.search).get('approval') === 'error';
          return {
            kind: failed ? 'error' : 'returned',
            message: failed ? 'Mock provider returned HTTP 403; approval is not confirmed.' : 'Mock provider response. This does not confirm a live approval.',
            response: JSON.stringify({ mock: true, pr: `${pr.repo}/${pr.id}` }, null, 2),
          };
        }
        case 'approve': {
          const pr = prs.find(x => x.key === p.key);
          if (!pr?.reviewVote?.canApprove || JSON.stringify(p.expected) !== JSON.stringify(pr.reviewVote.target))
            throw new Error('Mock review state changed. Refresh first.');
          pr.reviewVote = { ...pr.reviewVote, vote: 10, canApprove: false, target: { ...pr.reviewVote.target, vote: 10 } };
          pr.reviewers = pr.reviewers.filter(r => r.id !== me.id).concat({ ...me, vote: 10, isRequired: false, isContainer: false });
          return { confirmed: true, alreadyApproved: false, message: 'Mock vote recorded only in this offline demonstration. Other policies remain unchanged.', pr: structuredClone(pr) };
        }
        case 'hide': settings.hidden.push(p.key); return settings;
        case 'unhide': settings.hidden = settings.hidden.filter((h) => h !== p.key); return settings;
        case 'saveSettings': Object.assign(settings, p.settings); return settings;
        case 'discoverRepos': return [{ org, project, repo: 'sample-tools' }];
        case 'addRepo': case 'addRepos': case 'removeRepo': return settings;
        case 'autoComplete': {
          const pr = prs.find((x) => x.key === p.key);
          pr.autoCompleteBy = p.enable ? 'Sam Example' : null;
          pr.humanActions = pr.humanActions.filter((a) => a !== 'autocomplete').concat(p.enable ? [] : ['autocomplete']);
          pr.gates.autocomplete = p.enable ? g('ok', 'squash') : g('human', 'Not set');
          setTimeout(() => this.onEvent?.({ event: 'snapshot', data: { ...snapshot, prs: [...prs] } }), 10);
          return structuredClone(pr);
        }
        default: return null;
      }
    },
  };
})();
