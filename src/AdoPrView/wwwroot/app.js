'use strict';

// ------------------------------------------------------------------ bridge
const params = new URLSearchParams(location.search);
const MOCK = params.has('mock') || !(window.chrome && window.chrome.webview);
const host = MOCK ? null : window.chrome.webview;
let seq = 0;
const pending = new Map();

function rpc(method, p = {}) {
  if (MOCK) return window.MockHost.rpc(method, p);
  return new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    host.postMessage(JSON.stringify({ id, method, params: p }));
  });
}

function onHostMessage(m) {
  if (m.id != null && pending.has(m.id)) {
    const { resolve, reject } = pending.get(m.id);
    pending.delete(m.id);
    m.error ? reject(new Error(m.error)) : resolve(m.result);
  } else if (m.event === 'snapshot') {
    setSnapshot(m.data);
  } else if (m.event === 'settings') {
    S.settings = m.data; render();
  } else if (m.event === 'toast') {
    toast(m.data.text, m.data.kind);
  } else if (m.event === 'copilotRun') {
    try { updateCopilotRun(m.data); } catch (e) { toast(e.message || String(e), 'error'); }
  }
}

// ------------------------------------------------------------------- state
const S = {
  settings: null,
  snap: { prs: [] },
  expanded: new Set(),
  activeKeys: new Set(),
  lane: null,
  laneRetained: new Set(),
  scope: 'all',
  repo: '',
  q: '',
  logs: new Map(),          // logKey() -> text | Error | 'loading'
  showTs: new Set(),
  busy: new Set(),          // "key:action"
  approvals: new Map(),    // key -> last approval provider outcome (never a credential)
  autoCompleteErrors: new Map(),
  lastSig: '',
  pendingSnapshot: null,
  pointerInRows: false,
  rowPointerDown: false,
  rowsPending: false,
  rowOrder: [],
  orderRevision: null,
  hideUndo: [],
  hideUndoBusy: false,
  approvalSettings: null,
  approvalSettingsBusy: false,
  runs: new Map(),
  rowMenu: null,
  drawer: false,
  discovered: null,
};

const LANES = {
  needsHuman: { label: 'Needs Human', hint: 'Only human actions remain', short: 'Reviewer · PoP · Auto', c: 'var(--needs)' },
  blocked: { label: 'Blocked', hint: 'Agent-fixable problems', short: 'Fix with agent', c: 'var(--blocked)' },
  waiting: { label: 'Waiting', hint: 'Automated builds, checks or review bot in progress', short: 'In progress', c: 'var(--waiting)' },
  ready: { label: 'Ready', hint: 'No outstanding actions — completing', short: 'Completing', c: 'var(--ready)' },
};
const GATES = ['merge', 'build', 'checks', 'comments', 'agency'];

const I = {
  key: '<circle cx="7.5" cy="15.5" r="5.5"/><path d="m21 2-9.6 9.6M15.5 7.5l3 3L22 7l-3-3"/>',
  zap: '<path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z"/>',
  send: '<path d="M22 2 11 13M22 2l-7 20-4-9-9-4 20-7z"/>',
  spark: '<path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M19 15l.7 1.8 1.8.7-1.8.7L19 20l-.7-1.8-1.8-.7 1.8-.7z"/>',
  ext: '<path d="M15 3h6v6M10 14 21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>',
  eyeoff: '<path d="M9.9 4.2A10 10 0 0 1 12 4c7 0 10 8 10 8a17 17 0 0 1-2.2 3.4M6.6 6.6A17 17 0 0 0 2 12s3 8 10 8a9.7 9.7 0 0 0 5.4-1.6M2 2l20 20M9.9 9.9a3 3 0 0 0 4.2 4.2"/>',
  chev: '<path d="m9 6 6 6-6 6"/>',
  copy: '<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-2.64-6.36"/><path d="M21 3v6h-6"/>',
  merge: '<circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M6 21V9a9 9 0 0 0 9 9"/>',
  x: '<path d="M18 6 6 18M6 6l12 12"/>',
  check: '<path d="M20 6 9 17l-5-5"/>',
  approval: '<path d="M3 12h8M7 8v8M15 9l3-2v10M15 17h6"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  undo: '<path d="M9 14 4 9l5-5"/><path d="M4 9h11a5 5 0 0 1 0 10h-3"/>',
  play: '<path d="m6 3 14 9-14 9z"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
};
const svg = (n) => `<svg viewBox="0 0 24 24">${I[n]}</svg>`;

// ----------------------------------------------------------------- helpers
const $ = (s, r = document) => r.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const repoKey = (r) => `${r.org}/${r.project}/${r.repo}`.toLowerCase();

function ago(iso, long = false) {
  if (!iso) return '';
  const t = new Date(iso).getTime();
  if (!t || new Date(iso).getFullYear() < 2000) return '';
  const s = Math.max(0, (Date.now() - t) / 1000);
  const f = (n, u) => `${Math.floor(n)}${long ? ' ' + u + (Math.floor(n) === 1 ? '' : 's') : u[0]}`;
  if (s < 60) return long ? 'just now' : 'now';
  if (s < 3600) return f(s / 60, long ? 'minute' : 'm');
  if (s < 86400) return f(s / 3600, long ? 'hour' : 'h');
  if (s < 86400 * 30) return f(s / 86400, long ? 'day' : 'd');
  return f(s / (86400 * 30), long ? 'month' : 'mo');
}
const ago2 = (iso) => { const a = ago(iso); return a === 'now' ? 'now' : a ? a + ' ago' : ''; };

function md(text) {
  if (!text || !text.trim()) return '<span class="small">No description.</span>';
  const html = window.marked ? marked.parse(text, { gfm: true, breaks: true }) : esc(text);
  return window.DOMPurify ? DOMPurify.sanitize(html, { ADD_ATTR: ['target'] }) : esc(text);
}
if (window.DOMPurify) {
  DOMPurify.addHook('afterSanitizeAttributes', (n) => { if (n.tagName === 'A') { n.setAttribute('target', '_blank'); n.setAttribute('rel', 'noopener'); } });
}

function initials(name) {
  const parts = String(name || '?').replace(/[^\p{L}\p{N} ]/gu, ' ').trim().split(/\s+/);
  return ((parts[0] || '?')[0] + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase();
}
function hue(s) { let h = 0; for (const c of String(s)) h = (h * 31 + c.charCodeAt(0)) % 360; return h; }
const avatar = (name) => `<span class="avatar" style="background:hsl(${hue(name)} 70% 70%)" title="${esc(name)}">${esc(initials(name))}</span>`;

function toast(text, kind = 'ok', action) {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `<span class="ic"></span><span class="grow">${esc(text)}</span>`;
  if (action) {
    const b = document.createElement('button');
    b.textContent = action.label;
    b.onclick = () => { action.run(); el.remove(); };
    el.appendChild(b);
  }
  $('#toasts').appendChild(el);
  setTimeout(() => el.remove(), kind === 'error' ? 9000 : 5000);
}

const findPr = (key) => S.snap.prs.find((p) => p.key === key);

// --------------------------------------------------------------- filtering
function hiddenSet() { return new Set((S.settings?.hidden || []).map((h) => h.toLowerCase())); }

function ignoredDraft(p) {
  const fresh = S.pendingSnapshot?.prs.find(pr => pr.key === p.key) || p;
  return !!(fresh.isIgnoredDraft || (S.settings?.ignoreDrafts && fresh.isDraft));
}

function baseVisible() {
  const hidden = hiddenSet();
  const q = S.q.trim().toLowerCase();
  return S.snap.prs.filter((p) => {
    // Settings can undo a hide before a refreshed snapshot clears its old flag.
    if (hidden.has(p.key.toLowerCase())) return false;
    if (S.scope === 'drafts' ? !ignoredDraft(p) : ignoredDraft(p)) return false;
    if (S.scope === 'mine' && !p.isMine) return false;
    if (S.scope === 'watched' && p.isMine) return false;
    if (S.repo && p.repo !== S.repo) return false;
    if (q) {
      const hay = `${p.id} !${p.id} ${p.title} ${p.sourceBranch} ${p.repo} ${p.author?.name} ${p.author?.uniqueName}`.toLowerCase();
      if (!q.split(/\s+/).every((t) => hay.includes(t))) return false;
    }
    return true;
  });
}

// --------------------------------------------------------------- rendering
function rowsInUse() {
  const focused = document.activeElement;
  return !!S.rowMenu || S.pointerInRows || S.rowPointerDown || !!(focused?.closest('.pr') && focused.matches(':focus-visible'));
}

function flushRowUpdates() {
  if (rowsInUse()) return;
  if (S.pendingSnapshot) setSnapshot(S.pendingSnapshot);
  if (S.rowsPending) renderRows();
  renderStatus();
}

function setSnapshot(snap) {
  snap = snap || { prs: [] };
  const revision = snap.orderRevision ?? 0;
  const sorted = [...snap.prs].sort((a, b) =>
    Object.keys(LANES).indexOf(a.lane) - Object.keys(LANES).indexOf(b.lane) ||
    (Date.parse(b.updated) || 0) - (Date.parse(a.updated) || 0));
  const byKey = new Map(snap.prs.map(p => [p.key, p]));
  const oldKeys = new Set(S.rowOrder);
  const order = S.orderRevision === null || revision !== S.orderRevision
    ? sorted.map(p => p.key)
    : [...S.rowOrder.filter(key => byKey.has(key)), ...sorted.filter(p => !oldKeys.has(p.key)).map(p => p.key)];
  snap = { ...snap, prs: order.map(key => byKey.get(key)) };
  const sig = JSON.stringify(snap.prs);
  const rowsChanged = sig !== S.lastSig || revision !== S.orderRevision;
  // Keep the displayed row and its action target together until interaction ends.
  if (rowsChanged && rowsInUse()) {
    S.pendingSnapshot = snap;
    if (!S.rowPointerDown) {
      S.snap.prs = S.snap.prs.map(pr => {
        const fresh = byKey.get(pr.key);
        if (!fresh || fresh.autoCompleteBy === pr.autoCompleteBy) return pr;
        const updated = { ...pr, autoCompleteBy: fresh.autoCompleteBy, mergeStrategy: fresh.mergeStrategy,
          humanActions: (pr.humanActions || []).filter(a => a !== 'autocomplete').concat(fresh.autoCompleteBy ? [] : ['autocomplete']) };
        return updated;
      });
      for (const pr of S.snap.prs) syncAutoCompleteButtons(pr.key);
    }
    renderStatus();
    return;
  }
  S.pendingSnapshot = null;
  if (revision !== S.orderRevision) S.laneRetained.clear();
  S.snap = snap;
  S.rowOrder = order;
  S.orderRevision = revision;
  S.lastSig = sig;
  renderHeader();
  if (rowsChanged) renderRows();
}

function render() { renderHeader(); renderRows(); if (S.drawer) renderDrawer(); }

function renderHeader() {
  if (!S.settings?.ignoreDrafts && S.scope === 'drafts') S.scope = 'all';
  const vis = baseVisible();
  $('#draftFilter').hidden = !S.settings?.ignoreDrafts;
  document.querySelectorAll('#scope button').forEach(b => b.classList.toggle('on', b.dataset.scope === S.scope));
  $('#laneCounters').innerHTML = Object.entries(LANES).map(([k, m]) => {
    const n = vis.filter((p) => !ignoredDraft(p) && p.lane === k).length;
    return `<button class="lane-btn ${S.lane === k ? 'on' : ''} ${n ? '' : 'zero'} ${n && (k === 'needsHuman' || k === 'blocked') ? 'pulse' : ''}" data-lane="${k}" style="--c:${m.c}">
      <span class="bar"></span><span class="n">${n}</span><span class="l"><b>${m.label}</b><span>${m.short}</span></span></button>`;
  }).join('');

  const repos = [...new Set(S.snap.prs.map((p) => p.repo))].sort();
  const sel = $('#repoFilter');
  const opts = `<option value="">All repositories</option>` + repos.map((r) => `<option ${r === S.repo ? 'selected' : ''}>${esc(r)}</option>`).join('');
  if (sel.dataset.sig !== opts) { sel.innerHTML = opts; sel.dataset.sig = opts; }
  const botHead = $('#botHead');
  if (botHead && botHead.textContent !== botName()) botHead.textContent = botName();

  $('#who').textContent = S.snap.me ? `${S.snap.me}${MOCK ? ' · mock data' : ''}` : (MOCK ? 'mock data' : 'Azure DevOps');
  $('#refreshBtn').classList.toggle('spin', !!S.snap.refreshing);
  renderStatus();
}

function renderStatus() {
  const s = S.pendingSnapshot || S.snap;
  const vis = baseVisible();
  const repoCount = new Set(vis.map((p) => p.repo)).size;
  const hiddenCount = hiddenSet().size;
  const parts = [];
  parts.push(`<span><span class="dot ${s.refreshing ? 'busy' : ''}"></span>${s.refreshing ? 'Refreshing…' : s.lastRefresh ? 'Updated ' + ago2(s.lastRefresh) : 'Loading…'}</span>`);
  if (s.lastRefresh) parts.push(`<span>${vis.length} PR${vis.length === 1 ? '' : 's'} across ${repoCount} repo${repoCount === 1 ? '' : 's'}</span>`);
  if (hiddenCount) parts.push(`<span>${hiddenCount} hidden</span>`);
  if (S.settings) parts.push(`<span>auto-refresh ${Math.round(S.settings.refreshSeconds / 60 * 10) / 10}m · merge re-check ${S.settings.mergeCheckMinutes}m</span>`);
  parts.push('<span>Row order stays fixed until Refresh (R)</span>');
  if (s.mergeCheckStatus) parts.push(`<span>${esc(s.mergeCheckStatus)}</span>`);
  if (S.pendingSnapshot || S.rowsPending) parts.push('<span>Table updates paused while interacting with rows</span>');
  if (S.lane && vis.some(p => S.laneRetained.has(p.key) && p.lane !== S.lane))
    parts.push('<span>Rows that changed lane stay here until Refresh or a filter change</span>');
  if (s.error) parts.push(`<span class="err">⚠ ${esc(s.error)}</span>`);
  for (const e of s.repoErrors || []) parts.push(`<span class="err">⚠ ${esc(e)}</span>`);
  $('#statusline').innerHTML = parts.join('<span class="small">·</span>');
}

function renderRows(whenIdle = false) {
  if (S.rowPointerDown || (whenIdle && rowsInUse())) { S.rowsPending = true; renderStatus(); return; }
  S.rowsPending = false;
  const vis = baseVisible();
  renderRunShelf();
  const rows = $('#rows');
  const empty = $('#empty');
  if (!S.snap.lastRefresh && !vis.length) {
    rows.innerHTML = '';
    empty.hidden = false;
    empty.innerHTML = S.snap.error
      ? `<div class="big">⚠</div><h2>Couldn't load pull requests</h2><div>${esc(S.snap.error)}</div><button class="btn" data-act="refresh">${svg('refresh')} Try again</button>`
      : `<div class="big">⏳</div><h2>Loading your pull requests…</h2><div>Querying Azure DevOps policies, threads and builds.</div>`;
    return;
  }
  if (!S.settings?.repos?.length) {
    rows.innerHTML = '';
    empty.hidden = false;
    empty.innerHTML = `<div class="big">📂</div><h2>No repositories yet</h2><div>Add an Azure DevOps repository URL in settings.</div><button class="btn primary" data-act="settings">Open settings</button>`;
    return;
  }
  const shown = S.lane ? vis.filter((p) => !ignoredDraft(p) && (p.lane === S.lane || S.laneRetained.has(p.key))) : vis;
  if (S.lane) shown.forEach(p => S.laneRetained.add(p.key));
  if (!shown.length) {
    rows.innerHTML = '';
    empty.hidden = false;
    empty.innerHTML = `<div class="big">✨</div><h2>Nothing here</h2><div>${S.q || S.lane || S.repo || S.scope !== 'all' ? 'No PRs match the current filters.' : 'You have no active pull requests in the watched repositories.'}</div>`;
    return;
  }
  empty.hidden = true;
  const html = shown.map(p => renderPr(p, ignoredDraft(p)
    ? { label: 'Ignored draft', hint: 'Not monitored until published or Ignore draft PRs is turned off', c: 'var(--muted)' }
    : LANES[p.lane]));
  const scroller = $('main');
  const top = scroller.scrollTop;
  const logScroll = {};
  rows.querySelectorAll('pre.log[data-log]').forEach((el) => { logScroll[el.dataset.log] = el.scrollHeight - el.scrollTop - el.clientHeight; });
  rows.innerHTML = html.join('');
  scroller.scrollTop = top;
  rows.querySelectorAll('pre.log[data-log]').forEach((el) => {
    const fromBottom = logScroll[el.dataset.log];
    el.scrollTop = fromBottom == null ? el.scrollHeight : el.scrollHeight - el.clientHeight - fromBottom;
  });
}

const runActive = r => !!r && ['preparing', 'recovering', 'running', 'cancelling'].includes(r.status);
const runCancelable = r => runActive(r) || (r?.status === 'needs-attention' && r.id > 0);
const runsForPr = key => [...S.runs.values()].reverse().filter(r => r.key === key)
  .sort((a, b) => (Date.parse(b.startedAt) || 0) - (Date.parse(a.startedAt) || 0));
const runForPr = key => runsForPr(key).at(0);

function runSummary(r) {
  return `${r.status}${r.exitCode == null ? '' : ` · exit ${r.exitCode}`}: ${r.message || ''}`;
}

function runWorktreeText(r) {
  if (!r) return '';
  const label = r.worktreeStatus === 'removed-clean' ? 'Clean worktree removed' : r.worktreeRetained && !runActive(r) ? 'Preserved worktree' : 'Worktree';
  return [r.worktreePath && `${label}: ${r.worktreePath}`, r.sessionId && `Copilot session: ${r.sessionId}`,
    r.logPath && `Supervised log: ${r.logPath}`].filter(Boolean).join('\n');
}

function renderRunCard(key, r = runForPr(key), earlier = false) {
  return `<div class="card copilot-run${earlier ? ' run-earlier' : ''}" data-run-key="${esc(key)}" data-run-id="${esc(r?.runId || '')}">
    <h3><span class="grow">${earlier ? 'Earlier run · Copilot in WSL' : 'Fix PR · Copilot in WSL'}</span>
      <button class="mini" data-act="cancelRun" data-run-id="${esc(r?.runId || '')}" ${runCancelable(r) ? '' : 'hidden'}>${svg('x')} Cancel run</button></h3>
    <div class="body">
      <p class="small">Starts in a separate PR worktree with --yolo: tools, paths and URLs are allowed without confirmation for this run, not globally. This is not a filesystem sandbox. New runs are told to commit validated fixes and ordinary-push them only to this PR's captured source branch; that limit is a prompt instruction, not enforced. Merge/PR-update command denies and the prompt's approval/policy boundaries remain; earlier runs keep their original no-push scope. Sign-in is required beforehand; this panel is noninteractive. Exit 0 does not verify the PR fix.</p>
      <p class="run-status" role="status">${esc(r ? runSummary(r) : 'Not started. Use Fix PR to run the rich prompt, or Copy prompt to review it first.')}</p>
      <p class="run-worktree small">${esc(runWorktreeText(r))}</p>
      <pre class="run-output" tabindex="0" aria-label="Copilot stdout and stderr" ${r || isBusy(key, 'triage') ? '' : 'hidden'}>${esc(r?.output || '')}</pre>
    </div></div>`;
}

function earlierRunCards(key) {
  const [, ...earlier] = runsForPr(key);
  return earlier.map(r => renderRunCard(key, r, true)).join('');
}

// Runs stay inside their PR row. Departed PRs and ignored drafts keep collapsible
// run entries here (unless hidden), only in the unfiltered view.
function renderRunShelf() {
  const shelf = $('#runShelf');
  if (!shelf) return;
  const unfiltered = !S.lane && !S.q.trim() && !S.repo && S.scope === 'all';
  const loaded = !!(S.snap.lastRefresh || S.snap.error);
  const present = new Set(S.snap.prs.filter(p => !ignoredDraft(p)).map(p => p.key));
  // A PR that just left the active list starts collapsed here; later deliberate toggles are kept.
  for (const key of S.activeKeys) if (!present.has(key)) S.expanded.delete(key);
  S.activeKeys = present;
  const hidden = hiddenSet();
  const keys = unfiltered && loaded ? [...new Set([...S.runs.values()].map(r => r.key))]
    .filter(k => !present.has(k) && !hidden.has(k.toLowerCase())) : [];
  shelf.innerHTML = keys.map(key => {
    const r = runForPr(key), open = S.expanded.has(key);
    return `<div class="pr run-only ${open ? 'open' : ''}" data-key="${esc(key)}">
      <div class="run-heading" data-act="toggle">${svg('chev').replace('<svg', '<svg class="chev"')}
        <div class="prmain"><div class="title">${esc(r.title)}</div><div class="meta small"><span class="id">!${esc(r.id)}</span><span class="sep"> · </span><span>${esc(r.repo)}</span><span class="sep"> · </span><span>${findPr(key) && ignoredDraft(findPr(key)) ? 'Ignored draft' : 'PR no longer in the active list'} · ${esc(runSummary(r))}</span></div></div></div>
      ${open ? `<div class="detail"><div class="full stack">${renderRunCard(key, r)}${earlierRunCards(key)}</div></div>` : ''}
    </div>`;
  }).join('');
}

function updateCopilotRun(r) {
  if (!r || typeof r.key !== 'string' || typeof r.runId !== 'string' || typeof r.status !== 'string' || typeof r.output !== 'string')
    throw new Error('Copilot returned an invalid run state.');
  const old = S.runs.get(r.runId);
  if (old?.runId === r.runId && ((old.status !== 'preparing' && r.status === 'preparing') ||
    (['completed', 'failed', 'cancelled'].includes(old.status) && runActive(r)))) return;
  if (old?.runId === r.runId && old.updatedAt && r.updatedAt && Date.parse(r.updatedAt) < Date.parse(old.updatedAt)) return;
  S.runs.set(r.runId, r);
  syncCopilotButton(r.key);
  // Stream only into this panel; never replace rows or move another click target.
  const latest = runForPr(r.key)?.runId === r.runId;
  const cards = [...document.querySelectorAll('.copilot-run')].filter(el => el.dataset.runKey === r.key &&
    (el.dataset.runId === r.runId || (latest && !el.classList.contains('run-earlier'))));
  if (!cards.length) { renderRunShelf(); if (!old) renderRows(true); return; }
  for (const card of cards) {
    card.dataset.runId = r.runId;
    $('.run-status', card).textContent = runSummary(r);
    $('.run-worktree', card).textContent = runWorktreeText(r);
    const output = $('.run-output', card);
    const atBottom = output.scrollHeight - output.clientHeight - output.scrollTop < 30;
    output.hidden = false;
    output.textContent = r.output;
    if (atBottom) output.scrollTop = output.scrollHeight;
    const cancel = $('[data-act="cancelRun"]', card);
    cancel.hidden = !runCancelable(r);
    cancel.disabled = isBusy(r.key, 'cancelRun');
    cancel.dataset.runId = r.runId;
  }
  if (!old) { renderRunShelf(); renderRows(true); }
}

function syncCopilotButton(key) {
  for (const button of document.querySelectorAll('[data-act="triage"]')) {
    if (button.closest('.pr')?.dataset.key !== key) continue;
    const active = runActive(runForPr(key)), busy = isBusy(key, 'triage');
    button.classList.toggle('running', active);
    button.classList.toggle('busy', busy);
    button.disabled = busy;
    button.innerHTML = svg(active || busy ? 'refresh' : 'spark');
    button.title = active ? 'Show the inline Copilot run for this PR' : 'Fix PR with Copilot in an isolated WSL worktree; output stays inline';
    button.setAttribute('aria-label', button.title);
    if (active || busy) button.setAttribute('aria-busy', 'true');
    else button.removeAttribute('aria-busy');
  }
}

function gateCell(p, g) {
  const gate = p.gates?.[g];
  if (!gate) return `<div class="gate"><span class="pill na"><span>—</span></span></div>`;
  let clickable = false;
  if (g === 'pop' && gate.state === 'human') clickable = true;
  if (g === 'autocomplete' && gate.state === 'human') clickable = true;
  if (g === 'reviews' && gate.state === 'human') clickable = true;
  if (['fail', 'warn', 'pending'].includes(gate.state) && ['build', 'merge', 'comments', 'agency', 'checks', 'reviews'].includes(g)) clickable = true;
  const tip = [gate.label, gate.detail].filter(Boolean).join(' — ');
  return `<div class="gate"><span class="pill ${gate.state} ${clickable ? 'click' : ''}" ${clickable ? `data-act="gate" data-gate="${g}"` : ''} title="${esc(tip)}"><i></i><span>${esc(gate.label)}</span></span></div>`;
}

function isBusy(key, a) { return S.busy.has(`${key}:${a}`); }
const autoCompleteBusy = key => isBusy(key, 'autocomplete') || isBusy(key, 'cancelAuto');

function autoCompleteTitle(p) {
  const pending = isBusy(p.key, 'cancelAuto') ? 'Cancelling auto-complete; waiting for ADO confirmation'
    : isBusy(p.key, 'autocomplete') ? 'Setting auto-complete; waiting for ADO confirmation' : '';
  const state = p.autoCompleteBy ? `Auto-complete confirmed by ADO, set by ${p.autoCompleteBy} — click to cancel`
    : `Set auto-complete (${S.settings?.autoComplete?.mergeStrategy || 'squash'}, delete branch, complete work items)`;
  const error = S.autoCompleteErrors.get(p.key);
  return pending || state + (error ? ` — Last attempt: ${error}` : '');
}

function autoCompleteDescription(p) {
  return p.autoCompleteBy ? `${esc(p.mergeStrategy || 'set')} · by ${esc(p.autoCompleteBy)}`
    : '<span style="color:var(--needs)">not set</span>';
}

function syncAutoCompleteButtons(key) {
  const p = findPr(key);
  if (!p) return;
  const set = !!p.autoCompleteBy, busy = autoCompleteBusy(key);
  for (const button of document.querySelectorAll('[data-act="autocomplete"], [data-act="cancelAuto"]')) {
    if (button.closest('.pr')?.dataset.key !== key) continue;
    const rowAction = button.classList.contains('act');
    button.dataset.act = set ? 'cancelAuto' : 'autocomplete';
    button.classList.toggle('auto-set', set);
    button.classList.toggle('hot', rowAction && !set && p.isMine && (p.humanActions || []).includes('autocomplete'));
    button.classList.toggle('busy', busy);
    button.disabled = busy;
    button.title = autoCompleteTitle(p);
    button.setAttribute('aria-label', button.title);
    if (rowAction) button.setAttribute('aria-pressed', String(set));
    if (busy) button.setAttribute('aria-busy', 'true');
    else button.removeAttribute('aria-busy');
    button.innerHTML = svg(busy ? 'refresh' : set ? rowAction ? 'check' : 'x' : 'zap') +
      (rowAction ? '<span>Auto</span>' : set ? ' Cancel auto-complete' : ' Set auto-complete');
  }
  for (const status of document.querySelectorAll('[data-auto-complete-state], [data-auto-complete-error]')) {
    if (status.closest('.pr')?.dataset.key !== key) continue;
    if (status.hasAttribute('data-auto-complete-state')) status.innerHTML = autoCompleteDescription(p);
    else {
      status.textContent = S.autoCompleteErrors.get(key) || '';
      status.hidden = !S.autoCompleteErrors.has(key);
    }
  }
}

function providerName() { return S.snap.approvalProvider?.name || 'Approval provider'; }
function approvalTitle(p) { return p.approval?.title || `Request approval via ${providerName()} for only this PR`; }
function botName() { return S.settings?.reviewBot?.name || 'Review bot'; }

function actionButtons(p) {
  const out = {};
  const human = p.humanActions || [];
  const hot = p.isMine ? 'hot' : '';
  const b = (act, icon, label, cls, title, extra = '') => {
    const busy = ['autocomplete', 'cancelAuto'].includes(act) ? autoCompleteBusy(p.key) : isBusy(p.key, act), running = act === 'triage' && runActive(runForPr(p.key));
    return `<button class="act ${cls} ${busy ? 'busy' : ''} ${running ? 'running' : ''}" data-act="${act}" title="${esc(title)}" aria-label="${esc(title)}" ${busy ? 'disabled' : ''} ${busy || running ? 'aria-busy="true"' : ''} ${extra}>${svg(busy || running ? 'refresh' : icon)}${label ? `<span>${label}</span>` : ''}</button>`;
  };

  if (ignoredDraft(p)) {
    out.open = b('open', 'ext', '', '', 'Open in Azure DevOps');
    return ['approval', 'publish', 'pop', 'auto', 'fix', 'open'].map(slot => `<span class="action-slot" data-slot="${slot}">${out[slot] || ''}</span>`).join('');
  }
  const needsReview = human.includes('reviews') || p.gates?.reviews?.state === 'human';
  if (p.approval) out.approval = b('generateApproval', 'approval', '', needsReview ? 'provider-approval hot review-needed' : 'provider-approval', approvalTitle(p));
  else if (needsReview) out.approval = b('manualReview', 'approval', '', `${hot} review-needed`, 'Human reviewer approval needed. Open this PR to arrange review.');
  if (human.includes('publish')) out.publish = b('publish', 'send', 'Publish', hot, 'Publish draft (mark ready for review)');
  if (human.includes('pop')) out.pop = b('pop', 'key', 'PoP', hot, 'Proof of Presence — opens the inline Edge pane for your security key');
  else if (p.gates?.pop?.state === 'ok') out.pop = b('pop', 'key', '', 'done', 'Proof of Presence satisfied', 'disabled');
  if (p.autoCompleteBy) out.auto = b('cancelAuto', 'check', 'Auto', 'auto-set', autoCompleteTitle(p), 'aria-pressed="true"');
  else out.auto = b('autocomplete', 'zap', 'Auto', human.includes('autocomplete') ? hot : '', autoCompleteTitle(p), 'aria-pressed="false"');
  out.fix = b('triage', 'spark', '', 'copilot', runCancelable(runForPr(p.key)) ? 'Show the inline Copilot run for this PR' : 'Fix PR with Copilot in an isolated WSL worktree; output stays inline');
  out.open = b('open', 'ext', '', '', 'Open in Azure DevOps');
  return ['approval', 'publish', 'pop', 'auto', 'fix', 'open'].map(slot => `<span class="action-slot" data-slot="${slot}">${out[slot] || ''}</span>`).join('');
}

function renderPr(p, meta) {
  const open = S.expanded.has(p.key);
  const branch = `${esc(p.sourceBranch)} → ${esc(p.targetBranch)}`;
  const metaBits = [
    `<span class="row-lane" title="${esc(meta.hint)}">${meta.label}</span>`,
    `<span class="id">!${p.id}</span>`,
    `<span>${esc(p.repo)}</span>`,
    `<span class="br" title="${branch}">${branch}</span>`,
  ];
  const tags = [];
  if (p.isDraft) tags.push('<span class="tag draft">Draft</span>');
  if (!p.isMine) tags.push(`<span class="tag watch" title="${esc(p.author?.uniqueName || '')}">${avatar(p.author?.name)} ${esc(p.author?.name)}</span>`);
  if (p.error) tags.push(`<span class="tag err" title="${esc(p.error)}">load error</span>`);
  return `<div class="pr ${open ? 'open' : ''}" data-key="${esc(p.key)}" style="--c:${meta.c}">
    <div class="row" data-act="toggle">
      <div class="c-pr">${svg('chev').replace('<svg', '<svg class="chev"')}
        <div class="prmain">
          <div class="title" title="${esc(p.title)}">${esc(p.title)}</div>
          <div class="meta">${tags.join('')}${metaBits.join('<span class="sep">·</span>')}</div>
        </div>
      </div>
      ${GATES.map((g) => gateCell(ignoredDraft(p) ? { gates: {} } : p, g)).join('')}
      <div class="c-upd" title="Updated ${esc(new Date(p.updated).toLocaleString())}\nCreated ${esc(new Date(p.created).toLocaleString())}">${ago(p.updated)}<small>age ${ago(p.created)}</small></div>
      <div class="c-act">${actionButtons(p)}</div>
    </div>
    ${open ? renderDetail(p) : ''}
  </div>`;
}

function renderDetail(p) {
  if (ignoredDraft(p)) return `<div class="detail"><div class="full">
    <p>Ignored draft: no attention notifications or automatic merge checks. Publish it in Azure DevOps or turn off Ignore draft PRs in Settings to resume monitoring.</p>
    ${runsForPr(p.key).length ? renderRunCard(p.key) + earlierRunCards(p.key) : ''}
    </div></div>`;
  const left = [];
  const right = [];
  const full = [];
  full.push(renderRunCard(p.key) + earlierRunCards(p.key));

  if (p.approval || S.approvals.has(p.key)) {
    const outcome = S.approvals.get(p.key);
    const busy = isBusy(p.key, 'generateApproval');
    const needed = (p.humanActions || []).includes('reviews') || p.gates?.reviews?.state === 'human';
    const link = S.snap.approvalProvider?.linkUrl;
    full.push(`<div class="card approval"><h3><span class="grow">${esc(providerName())}</span>
      ${p.approval ? `<button class="act provider-approval ${needed ? 'hot review-needed' : ''} ${busy ? 'busy' : ''}" data-act="generateApproval" ${busy ? 'disabled aria-busy="true"' : ''} title="${esc(busy ? 'Requesting approval...' : approvalTitle(p))}" aria-label="${esc(busy ? 'Requesting approval...' : approvalTitle(p))}">${svg(busy ? 'refresh' : 'approval')}</button>` : ''}
      ${link ? `<button class="mini" data-act="url" data-url="${esc(link)}">${svg('ext')} ${esc(S.snap.approvalProvider?.linkLabel || 'Open site')}</button>` : ''}</h3>
      <div class="body">
        <div class="small">Only ${esc(p.repo)}/${p.id}. No sweep, auto-complete or merge.</div>
        ${p.approval?.detail ? `<p class="small">${esc(p.approval.detail)}</p>` : ''}
        ${busy ? '<p role="status">Requesting approval...</p>' : ''}
        ${outcome ? `<div class="approval-outcome ${outcome.kind === 'error' ? 'error' : ''}" role="status"><p>${esc(outcome.message)}</p>
          ${outcome.refreshWarning ? `<p class="error">${esc(outcome.refreshWarning)}</p>` : ''}
          ${outcome.response ? `<pre class="approval-response">${esc(outcome.response)}</pre>` : ''}</div>` : ''}
      </div></div>`);
  }

  if (p.error) full.push(`<div class="card failed"><h3>Load error</h3><div class="body">${esc(p.error)}</div></div>`);

  // Failed gates with log tails
  for (const fb of p.failedBuilds || []) {
    const tasks = (fb.tasks || []).map((t, i) => {
      const lk = logKey(p, fb.buildId, t.logId);
      const log = t.logId != null ? S.logs.get(lk) : undefined;
      if (t.logId != null && i === 0 && log === undefined) queueMicrotask(() => loadLog(p.key, fb.buildId, t.logId));
      let logHtml = '';
      if (log === 'loading') logHtml = `<pre class="log">Loading log tail…</pre>`;
      else if (log instanceof Error) logHtml = `<pre class="log"><span class="e">${esc(log.message)}</span></pre>`;
      else if (typeof log === 'string') logHtml = `<pre class="log" data-log="${esc(lk)}">${formatLog(log, S.showTs.has(lk))}</pre>`;
      const issues = (t.issues || []).length ? `<div class="issues">${t.issues.map((x) => `<div>${esc(x)}</div>`).join('')}</div>` : '';
      return `<div class="task">
        <div class="task-head"><span class="dot fail"></span>
          <div class="grow"><span class="path">${esc(t.path)}${t.path ? ' › ' : ''}</span><span class="name">${esc(t.name)}</span> <span class="small">${esc(t.result)}</span></div>
          ${t.logId != null ? (typeof log === 'string'
            ? `<button class="mini ${S.showTs.has(lk) ? 'on' : ''}" data-act="ts" data-log="${esc(lk)}">${svg('clock')} Timestamps</button>
               <button class="mini" data-act="copyLog" data-log="${esc(lk)}">${svg('copy')} Copy</button>`
            : log === undefined ? `<button class="mini" data-act="loadLog" data-build="${fb.buildId}" data-logid="${t.logId}">Load log tail</button>` : '') : ''}
        </div>${issues}${logHtml}</div>`;
    }).join('') || '<div class="body small">No failed task records found in the timeline.</div>';
    full.push(`<div class="card failed"><h3>✕ Failed gate · ${esc(fb.name)} <span class="small">build ${fb.buildId}</span><span class="grow"></span>
      ${fb.evaluationId ? `<button class="mini" data-act="requeue" data-eval="${esc(fb.evaluationId)}">${svg('play')} Re-queue</button>` : ''}
      <button class="mini" data-act="url" data-url="${esc(fb.url)}">${svg('ext')} Open logs</button></h3>${tasks}</div>`);
  }

  left.push(`<div class="card"><h3>Description</h3><div class="body md clamp">${md(p.description)}</div></div>`);

  const threads = p.threads || [];
  if (threads.length) {
    left.push(`<div class="card"><h3>Unresolved threads <span class="cnt">${threads.length}</span></h3>${threads.map((t) => `
      <div class="thread">
        <div class="thread-head">${avatar(t.author)}<b>${esc(t.author)}</b>${t.isAgency ? `<span class="badge">${esc(botName())} · current</span>` : t.isBot ? '<span class="badge">bot</span>' : ''}
          <span class="small">${esc(t.status)} · ${ago2(t.updated)}${t.replies ? ` · ${t.replies} repl${t.replies === 1 ? 'y' : 'ies'}` : ''}</span><span class="grow"></span>
          <a href="${esc(t.url)}" target="_blank" class="small">open ↗</a></div>
        ${t.filePath ? `<div class="file">${esc(t.filePath)}${t.line ? ':' + t.line : ''}</div>` : ''}
        <div class="md">${md(t.content)}</div>
      </div>`).join('')}</div>`);
  }

  // Right column: what's blocking
  const items = [
    ...(p.blockers || []).map((x) => ['fail', x]),
    ...(p.humanActions || []).map((a) => ['human', { reviews: 'Human reviewer approval needed (+1)', pop: 'Proof of Presence — tap your security key', autocomplete: 'Auto-complete not set', publish: 'Draft — publish when ready' }[a] || a]),
    ...(p.waiting || []).map((x) => ['pending', x]),
  ];
  right.push(`<div class="card"><h3>Status <span class="grow"></span>
      <button class="mini" data-act="triage">${svg('spark')} ${runCancelable(runForPr(p.key)) ? 'Show run' : 'Fix PR'}</button>
      <button class="mini" data-act="copyPrompt">${svg('copy')} Copy prompt</button></h3>
    <div class="body"><ul class="list blockers">${items.length ? items.map(([c, t]) => `<li><span class="dot ${c}"></span><span class="k">${esc(t)}</span></li>`).join('') : '<li><span class="dot ok"></span><span class="k">All gates satisfied</span></li>'}</ul></div></div>`);

  const ac = autoCompleteDescription(p);
  right.push(`<div class="card"><h3>Merge <span class="grow"></span>
      <button class="mini" data-act="restartMerge" title="Ask ADO to recompute the merge against the latest target">${svg('merge')} Re-check merge</button>
      <button class="mini ${p.autoCompleteBy ? 'auto-set' : ''}" data-act="${p.autoCompleteBy ? 'cancelAuto' : 'autocomplete'}" title="${esc(autoCompleteTitle(p))}" aria-label="${esc(autoCompleteTitle(p))}" ${autoCompleteBusy(p.key) ? 'disabled aria-busy="true"' : ''}>${svg(autoCompleteBusy(p.key) ? 'refresh' : p.autoCompleteBy ? 'x' : 'zap')} ${p.autoCompleteBy ? 'Cancel auto-complete' : 'Set auto-complete'}</button></h3>
    <div class="body"><div class="error" role="status" data-auto-complete-error ${S.autoCompleteErrors.has(p.key) ? '' : 'hidden'}>${esc(S.autoCompleteErrors.get(p.key) || '')}</div><dl class="kv">
      <dt>Merge status</dt><dd>${esc(p.mergeStatus || '—')}${p.mergeStale ? ' <span class="small">(preview target is stale or missing)</span>' : ''}${p.mergeFailure ? `<div class="small">${esc(p.mergeFailure)}</div>` : ''}</dd>
      <dt>Merge re-check</dt><dd>${p.mergeRecheckRequestedAt ? `Requested ${esc(new Date(p.mergeRecheckRequestedAt).toLocaleString())}${p.mergeRecheckPending ? ' — awaiting ADO result' : ''}` : 'No restart requested this session'}
        ${p.mergeCheckError ? `<div class="error">${esc(p.mergeCheckError)}</div>` : ''}
        ${p.mergeRecheckError ? `<div class="error">${esc(p.mergeRecheckError)}</div>` : ''}</dd>
      <dt>Branches</dt><dd><code>${esc(p.sourceBranch)}</code> → <code>${esc(p.targetBranch)}</code></dd>
      <dt>Auto-complete</dt><dd data-auto-complete-state>${ac}</dd>
      <dt>${esc(botName())}</dt><dd>${esc(p.agency?.detail || '—')}</dd>
      <dt>Author</dt><dd>${esc(p.author?.name)} <span class="small">${esc(p.author?.uniqueName || '')}</span></dd>
      <dt>Created</dt><dd>${esc(new Date(p.created).toLocaleString())}</dd>
    </dl></div></div>`);

  const pol = p.policies || [];
  if (pol.length) {
    right.push(`<div class="card"><h3>Policies <span class="cnt">${pol.filter((x) => x.status === 'approved').length}/${pol.length}</span></h3><div class="body"><ul class="list">${pol.map((x) => `
      <li><span class="dot ${esc(x.status)}"></span><span class="k">${esc(x.name)} ${x.isBlocking ? '' : '<span class="badge">optional</span>'}${x.isExpired ? ' <span class="badge req">expired</span>' : ''}
        <div class="small">${esc(x.status)}${x.kind !== 'other' ? ' · ' + esc(x.kind) : ''}${x.statusDescription ? '<br>' + esc(x.statusDescription) : ''}</div></span>
        ${x.buildUrl ? `<button class="mini" data-act="url" data-url="${esc(x.buildUrl)}">${svg('ext')}</button>` : ''}
        ${x.kind === 'build' && x.status !== 'running' ? `<button class="mini" data-act="requeue" data-eval="${esc(x.evaluationId)}" title="Re-queue">${svg('play')}</button>` : ''}
      </li>`).join('')}</ul></div></div>`);
  }

  const revs = (p.reviewers || []).slice().sort((a, b) => (b.isRequired - a.isRequired) || (a.vote - b.vote));
  if (revs.length) {
    const vl = { 10: 'Approved', 5: 'Approved w/ suggestions', 0: 'No vote', '-5': 'Waiting for author', '-10': 'Rejected' };
    right.push(`<div class="card"><h3>Reviewers <span class="cnt">${revs.length}</span></h3><div class="body"><ul class="list">${revs.map((r) => `
      <li>${avatar(r.name)}<span class="k">${esc(r.name)} ${r.isRequired ? '<span class="badge req">required</span>' : ''}${r.isContainer ? ' <span class="badge">group</span>' : ''}</span>
      <span class="vote v${r.vote}">${vl[r.vote] ?? r.vote}</span></li>`).join('')}</ul></div></div>`);
  }

  return `<div class="detail">${full.length ? `<div class="full stack">${full.join('')}</div>` : ''}<div class="stack">${left.join('')}</div><div class="stack">${right.join('')}</div></div>`;
}

function formatLog(text, showTs) {
  const lines = text.replace(/\r/g, '').replace(/\n$/, '').split('\n');
  return lines.map((l) => {
    let ts = '';
    const m = l.match(/^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d+Z) (.*)$/);
    if (m) { ts = m[1]; l = m[2]; }
    let cls = '';
    if (/##\[error\]|\berror\b|\bFAIL(ED)?\b|panic:|Exception/i.test(l)) cls = 'e';
    else if (/##\[warning\]|\bwarn(ing)?\b/i.test(l)) cls = 'w';
    else if (/##\[section\]|\bPASS\b|\bok\b/.test(l)) cls = 's';
    const body = cls ? `<span class="${cls}">${esc(l)}</span>` : esc(l);
    return showTs && ts ? `<span class="t">${ts.slice(11, 23)}</span> ${body}` : body;
  }).join('\n');
}

// Build IDs are only unique within an organization/project.
const logKey = (p, buildId, logId) => `${p.org}/${p.project}/${buildId}/${logId}`.toLowerCase();

async function loadLog(key, buildId, logId) {
  const p = findPr(key);
  if (!p) return;
  const lk = logKey(p, buildId, logId);
  if (S.logs.get(lk) === 'loading') return;
  S.logs.set(lk, 'loading');
  try {
    const text = await rpc('logTail', { key, buildId, logId, lines: 200 });
    S.logs.set(lk, text || '(empty log)');
  } catch (e) {
    S.logs.set(lk, e);
  }
  renderRows(true);
}

// ----------------------------------------------------------------- actions
async function undoHide(entry = S.hideUndo.at(-1)) {
  if (!entry || S.hideUndoBusy || !S.hideUndo.includes(entry)) return;
  S.hideUndoBusy = true;
  try {
    S.settings = await rpc('unhide', { key: entry.key });
    S.hideUndo = S.hideUndo.filter((h) => h !== entry);
    render();
    toast(`Restored !${entry.id} ${entry.title}`, 'info');
  } catch (e) {
    toast(e.message || String(e), 'error');
  } finally {
    S.hideUndoBusy = false;
  }
}

function closeRowMenu() {
  S.rowMenu = null;
  const menu = $('#rowMenu');
  if (menu) menu.hidden = true;
}

function openRowMenu(e) {
  if (e.target.closest('.detail, pre, a[href], input, textarea, select, [contenteditable="true"]')) return;
  const row = e.target.closest('.pr[data-key] .row');
  if (!row) return;
  const p = findPr(row.closest('.pr').dataset.key);
  if (!p) return;
  e.preventDefault();
  S.rowMenu = { key: p.key, id: p.id, title: p.title, repo: p.repo };
  const menu = $('#rowMenu');
  menu.hidden = false;
  menu.style.left = `${Math.max(4, Math.min(e.clientX, window.innerWidth - menu.offsetWidth - 4))}px`;
  menu.style.top = `${Math.max(4, Math.min(e.clientY, window.innerHeight - menu.offsetHeight - 4))}px`;
  $('button', menu).focus();
}

async function hidePr(p) {
  if (!p || isBusy(p.key, 'hide') || !confirm(`Hide PR !${p.id} in ${p.repo}?\n\n${p.title}\n\nUndo with Ctrl+Z or the Undo button.`)) return;
  await runAction(p.key, 'hide', async () => {
    S.settings = await rpc('hide', { key: p.key });
    S.expanded.delete(p.key);
    const entry = { key: p.key, id: p.id, title: p.title };
    S.hideUndo.push(entry);
    render();
    toast(`Hid !${entry.id} ${entry.title.slice(0, 50)}`, 'info', { label: 'Undo', run: () => undoHide(entry) });
  });
}

async function runAction(key, act, fn, okText) {
  if (isBusy(key, act)) return;
  S.busy.add(`${key}:${act}`);
  if (act === 'autocomplete' || act === 'cancelAuto') syncAutoCompleteButtons(key);
  else renderRows();
  try {
    const r = await fn();
    if (okText) toast(typeof okText === 'function' ? okText(r) : okText);
    return r;
  } catch (e) {
    toast(e.message || String(e), 'error');
  } finally {
    S.busy.delete(`${key}:${act}`);
    if (act === 'triage') syncCopilotButton(key);
    if (act === 'autocomplete' || act === 'cancelAuto') syncAutoCompleteButtons(key);
    renderRows(true);
  }
}

async function changeAutoComplete(p, enable) {
  if (!p || autoCompleteBusy(p.key)) return;
  S.autoCompleteErrors.delete(p.key);
  await runAction(p.key, enable ? 'autocomplete' : 'cancelAuto', async () => {
    try {
      const updated = await rpc('autoComplete', { key: p.key, enable });
      if (!updated || updated.key !== p.key || !!updated.autoCompleteBy !== enable || !Array.isArray(updated.humanActions) || !updated.gates)
        throw new Error('ADO auto-complete read-back was not confirmed. Refresh the PR before retrying.');
      // Apply only this captured, read-back PR while other row updates stay deferred.
      const replace = prs => prs.flatMap(pr => pr.key !== p.key ? [pr] : updated.isActive === false ? [] : [{
        ...pr, autoCompleteBy: updated.autoCompleteBy, mergeStrategy: updated.mergeStrategy,
        humanActions: (pr.humanActions || []).filter(a => a !== 'autocomplete').concat(updated.autoCompleteBy ? [] : ['autocomplete']),
        gates: { ...pr.gates, autocomplete: updated.gates.autocomplete },
      }]);
      S.snap = { ...S.snap, prs: replace(S.snap.prs) };
      if (S.pendingSnapshot) S.pendingSnapshot = { ...S.pendingSnapshot, prs: replace(S.pendingSnapshot.prs) };
      return updated;
    } catch (e) {
      const message = `Auto-complete change not confirmed: ${e.message || String(e)}. Refresh the PR before retrying.`;
      S.autoCompleteErrors.set(p.key, message);
      throw new Error(message);
    }
  }, `Auto-complete ${enable ? 'set' : 'cancelled'} on !${p.id} (confirmed by ADO)`);
}

async function onAction(act, el, key) {
  const p = key ? findPr(key) : null;
  if (p && ignoredDraft(p) && !['toggle', 'open', 'hide', 'cancelRun'].includes(act))
    throw new Error('Draft PRs are ignored. Publish in Azure DevOps or turn off Ignore draft PRs in Settings first.');
  switch (act) {
    case 'toggle':
      if (window.getSelection()?.toString()) return;
      S.expanded.has(key) ? S.expanded.delete(key) : S.expanded.add(key);
      renderRows();
      break;
    case 'gate': {
      const g = el.dataset.gate;
      if (g === 'pop') return onAction('pop', el, key);
      if (g === 'autocomplete') return onAction('autocomplete', el, key);
      S.expanded.add(key);
      renderRows();
      const target = g === 'build' ? $(`.pr[data-key="${CSS.escape(key)}"] .failed`) : $(`.pr[data-key="${CSS.escape(key)}"] .detail`);
      target?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      break;
    }
    case 'pop':
      await runAction(key, 'pop', async () => {
        const result = await rpc('openPop', { key });
        if (typeof result?.message !== 'string') throw new Error('The inline PoP pane was not confirmed open. No approval is inferred.');
        return result;
      }, result => result.message);
      break;
    case 'open':
    case 'manualReview':
      await rpc('openUrl', { url: p.url });
      break;
    case 'url':
      await rpc('openUrl', { url: el.dataset.url });
      break;
    case 'autocomplete':
      await changeAutoComplete(p, true);
      break;
    case 'cancelAuto':
      if (!p || autoCompleteBusy(key)) return;
      if (!confirm(`Cancel auto-complete on !${p.id}?`)) return;
      await changeAutoComplete(p, false);
      break;
    case 'publish':
      await runAction(key, 'publish', () => rpc('publish', { key }), `!${p.id} published`);
      break;
    case 'generateApproval':
      if (!p?.approval || isBusy(key, act)) return;
      S.expanded.add(key);
      S.approvals.delete(key);
      await runAction(key, act, async () => {
        try {
          const result = await rpc('generateApproval', { key });
          if (!result || !['returned', 'error'].includes(result.kind) || typeof result.message !== 'string')
            throw new Error('The approval provider returned no usable outcome. Approval is not confirmed; check the PR before retrying.');
          S.approvals.set(key, result);
          toast(`${providerName()} response for !${p.id} is shown in the expanded row${result.refreshWarning ? ' (gate refresh failed)' : ''}`, result.kind === 'error' || result.refreshWarning ? 'error' : 'info');
        } catch (e) {
          S.approvals.set(key, { kind: 'error', message: e.message || String(e) });
          throw e;
        }
      });
      break;
    case 'restartMerge':
      await runAction(key, 'restartMerge', async () => {
        const result = await rpc('restartMerge', { key });
        if (typeof result?.requested !== 'boolean' || typeof result.message !== 'string')
          throw new Error('Merge re-check was not confirmed. Refresh the PR before retrying.');
        return result;
      }, (result) => result.message);
      break;
    case 'requeue':
      await runAction(key, 'requeue', () => rpc('requeue', { key, evaluationId: el.dataset.eval }), 'Build re-queued');
      break;
    case 'triage':
      S.expanded.add(key);
      if (runCancelable(runForPr(key))) { renderRows(); break; }
      await runAction(key, 'triage', async () => {
        const r = await rpc('startCopilot', { key });
        updateCopilotRun(r);
      });
      break;
    case 'cancelRun':
      await runAction(key, 'cancelRun', async () => updateCopilotRun(await rpc('cancelCopilot', { key, runId: el.dataset.runId })));
      break;
    case 'copyPrompt':
      await runAction(key, 'copyPrompt', () => rpc('copyPrompt', { key }), 'Triage prompt copied');
      break;
    case 'hide': {
      await hidePr(p);
      break;
    }
    case 'ts':
      S.showTs.has(el.dataset.log) ? S.showTs.delete(el.dataset.log) : S.showTs.add(el.dataset.log);
      renderRows();
      break;
    case 'copyLog': {
      const t = S.logs.get(el.dataset.log);
      await rpc('copy', { text: S.showTs.has(el.dataset.log) ? t : t.replace(/^\d{4}-\d\d-\d\dT\S+Z /gm, '') });
      toast('Log tail copied');
      break;
    }
    case 'loadLog':
      loadLog(key, +el.dataset.build, +el.dataset.logid);
      break;
    case 'refresh':
      await rpc('refresh');
      break;
    case 'settings':
      openDrawer();
      break;
  }
}

// ------------------------------------------------------------------ drawer
async function openDrawer() {
  S.drawer = true; $('#drawer').hidden = false; $('#scrim').hidden = false; renderDrawer();
  await loadApprovalSettings();
}
function closeDrawer() { S.drawer = false; $('#drawer').hidden = true; $('#scrim').hidden = true; }

async function loadApprovalSettings() {
  if (!S.snap.approvalProvider) { S.approvalSettings = null; return; }
  try {
    const view = await rpc('approvalSettings');
    if (view && (typeof view.message !== 'string' || !Array.isArray(view.actions)))
      throw new Error('Could not read approval provider settings.');
    S.approvalSettings = view || null;
  } catch (e) {
    S.approvalSettings = { message: e.message || String(e), actions: [] };
  }
  if (S.drawer) renderDrawer();
}

function renderDrawer() {
  const st = S.settings;
  if (!st) return;
  const titles = new Map(S.snap.prs.map((p) => [p.key.toLowerCase(), p]));
  const disc = S.discovered;
  $('#drawerBody').innerHTML = `
    ${S.snap.approvalProviderError ? `<div class="section"><h4>Approval provider</h4><p class="help error">${esc(S.snap.approvalProviderError)}</p></div>` : ''}
    ${S.snap.approvalProvider && S.approvalSettings ? `<div class="section">
      <h4>${esc(providerName())}</h4>
      <p class="help">${esc(S.approvalSettings.message)}</p>
      ${S.approvalSettings.help ? `<p class="help">${esc(S.approvalSettings.help)}</p>` : ''}
      <div class="field">${S.approvalSettings.actions.map((a) => `<button class="btn" data-dact="approvalSettingsAction" data-id="${esc(a.id)}" ${S.approvalSettingsBusy ? 'disabled' : ''}>${esc(a.label)}</button>`).join('')}</div>
    </div>` : ''}
    <div class="section">
      <h4>Repositories</h4>
      <p class="help">Paste any Azure DevOps repository URL (dev.azure.com or *.visualstudio.com).</p>
      <div class="items">${st.repos.map((r) => `<div class="item"><div class="grow"><div><b>${esc(r.repo)}</b></div><div class="small">${esc(r.org)} / ${esc(r.project)}</div></div>
        <button class="x" data-dact="removeRepo" data-key="${esc(repoKey(r))}" title="Remove">${svg('x')}</button></div>`).join('') || '<div class="small">None yet.</div>'}</div>
      <div class="field"><input type="text" id="repoUrl" placeholder="https://dev.azure.com/org/project/_git/repo" /><button class="btn primary" data-dact="addRepo">${svg('plus')} Add</button></div>
      <div class="field"><button class="btn" data-dact="discover">${svg('search')} Discover repos where I have PRs</button></div>
      ${disc === 'loading' ? '<div class="small" style="margin-top:8px">Searching…</div>' : ''}
      ${Array.isArray(disc) ? (disc.length ? `<div class="items">${disc.map((r, i) => `<div class="item"><div class="grow"><div><b>${esc(r.repo)}</b></div><div class="small">${esc(r.org)} / ${esc(r.project)}</div></div>
          <button class="mini" data-dact="addDiscovered" data-i="${i}">${svg('plus')} Add</button></div>`).join('')}
          <button class="btn" data-dact="addAllDiscovered" style="align-self:flex-start">Add all ${disc.length}</button></div>` : '<div class="small" style="margin-top:8px">No other repositories with your PRs found.</div>') : ''}
    </div>
    <div class="section">
      <h4>Watched authors</h4>
      <p class="help">Also show active PRs created by these people or service identities. Match by alias, UPN or display name.</p>
      <div class="chips">${st.whitelist.map((w, i) => `<span class="chip">${esc(w)}<button data-dact="removeWl" data-i="${i}" title="Remove">${svg('x')}</button></span>`).join('')}</div>
      <div class="field"><input type="text" id="wlInput" placeholder="e.g. jdoe, jdoe@contoso.com or Build Service" /><button class="btn" data-dact="addWl">${svg('plus')} Add</button></div>
    </div>
    <div class="section">
      <h4>Hidden pull requests</h4>
      <p class="help">PRs you removed from the view. They stay hidden until restored.</p>
      <div class="items">${st.hidden.map((h) => { const p = titles.get(h.toLowerCase()); return `<div class="item"><div class="grow"><div>${p ? `<b>!${p.id}</b> ${esc(p.title)}` : esc(h)}</div><div class="small">${p ? esc(p.repo) + ' · ' + esc(p.author?.name) : 'no longer active'}</div></div>
        <button class="mini" data-dact="unhide" data-key="${esc(h)}">${svg('undo')} Restore</button></div>`; }).join('') || '<div class="small">Nothing hidden.</div>'}</div>
    </div>
    <div class="section">
      <h4>Polling</h4>
      <label class="check"><input type="checkbox" id="ignoreDrafts" ${st.ignoreDrafts ? 'checked' : ''}/> Ignore draft PRs</label>
      <p class="help">Exclude drafts from attention counts, notifications and automatic merge checks. Find them with the Ignored drafts filter. Publishing resumes monitoring on the next refresh; existing Fix runs are not stopped.</p>
      <div class="field"><label>Refresh every (seconds)</label><input type="number" id="refreshSeconds" min="30" max="3600" value="${st.refreshSeconds}" /></div>
      <div class="field"><label>Merge-conflict re-check every (minutes)</label><input type="number" id="mergeCheckMinutes" min="1" max="240" value="${st.mergeCheckMinutes}" /></div>
      <label class="check"><input type="checkbox" id="notifications" ${st.notifications ? 'checked' : ''}/> Tray notifications and a distinctive chime for new Needs Human PRs (closing the window keeps it in the tray)</label>
    </div>
    <div class="section">
      <h4>Auto-complete</h4>
      <div class="field"><label>Merge strategy</label><select id="mergeStrategy">${['squash', 'noFastForward', 'rebase', 'rebaseMerge'].map((m) => `<option ${st.autoComplete.mergeStrategy === m ? 'selected' : ''}>${m}</option>`).join('')}</select></div>
      <label class="check"><input type="checkbox" id="deleteSourceBranch" ${st.autoComplete.deleteSourceBranch ? 'checked' : ''}/> Delete source branch</label>
      <label class="check"><input type="checkbox" id="transitionWorkItems" ${st.autoComplete.transitionWorkItems ? 'checked' : ''}/> Complete linked work items</label>
    </div>
    <div class="section">
      <h4>Copilot in WSL</h4>
      <p class="help">Fix PR creates an isolated worktree at the PR source commit and streams its run inline. The source checkout is not switched or edited by the launcher. WSL needs authenticated Copilot, Git and bgtask. Each deliberate run uses --yolo for tool/path/URL permissions without confirmation; global settings are unchanged. Worktree isolation is not a filesystem sandbox. Runs are told to commit validated fixes and ordinary-push them only to the PR's captured source branch (a prompt instruction, not enforced). Merge/PR-update command denies and the prompt's approval/policy boundaries remain.</p>
      <div class="field"><label>WSL distribution (blank = default)</label><input id="wslDistro" type="text" value="${esc(st.wslDistro || '')}" placeholder="Default WSL distribution" /></div>
      ${st.repos.map(r => `<div class="field wsl-repo"><label>${esc(r.repo)}<small>${esc(r.org)} / ${esc(r.project)}</small></label>
        <input type="text" data-wsl-repo="${esc(repoKey(r))}" value="${esc(st.wslRepoDirectories?.[repoKey(r)] || '')}" placeholder="~/clones/${esc(r.repo)}" /></div>`).join('')}
      <p class="help">Use an absolute Linux directory or ~/path. Blank tries ~/clones/&lt;repo&gt;. The directory and exact Azure DevOps remote are checked before any run. Failed/cancelled runs and generated changes are preserved at the displayed worktree path.</p>
    </div>
    <div class="section">
      <h4>Copilot triage instructions</h4>
      <p class="help">The "Ask" appended to every triage prompt. Leave empty for the default (drive the PR to green, no auto-complete).</p>
      <div class="field"><textarea id="triageInstructions" placeholder="Default instructions">${esc(st.triageInstructions || '')}</textarea></div>
    </div>
    <div class="section"><button class="btn primary" data-dact="save">${svg('check')} Save settings</button></div>`;
}

async function saveSettings(patch) {
  const st = { ...S.settings, ...patch };
  S.settings = await rpc('saveSettings', { settings: st });
  render();
}

async function onDrawerAction(act, el) {
  if (act === 'approvalSettingsAction') {
    if (S.approvalSettingsBusy) return;
    S.approvalSettingsBusy = true; renderDrawer();
    try {
      const result = await rpc('approvalSettingsAction', { id: el.dataset.id });
      if (typeof result?.message !== 'string') throw new Error('No settings action outcome was returned.');
      toast(result.message, 'info');
      await loadApprovalSettings();
    } catch (e) { toast(e.message || String(e), 'error'); }
    finally { S.approvalSettingsBusy = false; if (S.drawer) renderDrawer(); }
    return;
  }
  try {
    switch (act) {
      case 'addRepo': {
        const url = $('#repoUrl').value.trim();
        if (!url) return;
        S.settings = await rpc('addRepo', { url });
        toast('Repository added — refreshing');
        break;
      }
      case 'removeRepo':
        S.settings = await rpc('removeRepo', { key: el.dataset.key });
        break;
      case 'discover':
        S.discovered = 'loading'; renderDrawer();
        S.discovered = await rpc('discoverRepos');
        break;
      case 'addDiscovered':
        S.settings = await rpc('addRepos', { repos: [S.discovered[+el.dataset.i]] });
        S.discovered.splice(+el.dataset.i, 1);
        break;
      case 'addAllDiscovered':
        S.settings = await rpc('addRepos', { repos: S.discovered });
        S.discovered = null;
        toast('Repositories added — refreshing');
        break;
      case 'addWl': {
        const v = $('#wlInput').value.trim();
        if (!v) return;
        await saveSettings({ whitelist: [...S.settings.whitelist, v] });
        toast(`Watching PRs by ${v}`);
        break;
      }
      case 'removeWl':
        await saveSettings({ whitelist: S.settings.whitelist.filter((_, i) => i !== +el.dataset.i) });
        break;
      case 'unhide':
        S.settings = await rpc('unhide', { key: el.dataset.key });
        S.hideUndo = S.hideUndo.filter((h) => h.key.toLowerCase() !== el.dataset.key.toLowerCase());
        break;
      case 'save':
        await saveSettings({
          refreshSeconds: +$('#refreshSeconds').value || 120,
          mergeCheckMinutes: +$('#mergeCheckMinutes').value || 10,
          notifications: $('#notifications').checked,
          ignoreDrafts: $('#ignoreDrafts').checked,
          autoComplete: { mergeStrategy: $('#mergeStrategy').value, deleteSourceBranch: $('#deleteSourceBranch').checked, transitionWorkItems: $('#transitionWorkItems').checked },
          triageInstructions: $('#triageInstructions').value,
          wslDistro: $('#wslDistro').value.trim(),
          wslRepoDirectories: Object.fromEntries([...document.querySelectorAll('[data-wsl-repo]')].map(e => [e.dataset.wslRepo, e.value.trim()]).filter(([, value]) => value)),
        });
        toast('Settings saved');
        break;
    }
  } catch (e) {
    if (S.discovered === 'loading') S.discovered = null;
    toast(e.message || String(e), 'error');
  }
  render();
}

// ------------------------------------------------------------------ wiring
function wire() {
  const rows = $('#rows');
  document.addEventListener('contextmenu', openRowMenu);
  rows.addEventListener('pointerenter', () => { S.pointerInRows = true; });
  rows.addEventListener('pointerleave', () => { S.pointerInRows = false; flushRowUpdates(); });
  document.addEventListener('pointerdown', (e) => {
    if (S.rowMenu && !e.target.closest('#rowMenu')) { closeRowMenu(); requestAnimationFrame(flushRowUpdates); }
    if (e.target.closest('.pr')) S.rowPointerDown = true;
  }, true);
  const endPointer = () => {
    S.rowPointerDown = false;
    requestAnimationFrame(flushRowUpdates);
  };
  document.addEventListener('pointerup', endPointer, true);
  document.addEventListener('pointercancel', endPointer, true);
  rows.addEventListener('focusout', () => requestAnimationFrame(flushRowUpdates));
  window.addEventListener('blur', () => {
    closeRowMenu();
    S.pointerInRows = false;
    S.rowPointerDown = false;
    requestAnimationFrame(flushRowUpdates);
  });
  document.addEventListener('click', (e) => {
    if (e.target.closest('[data-menu-hide]')) {
      e.preventDefault();
      const selected = S.rowMenu;
      closeRowMenu();
      hidePr(selected).catch(err => toast(err.message || String(err), 'error'));
      return;
    }
    const d = e.target.closest('[data-dact]');
    if (d) { e.preventDefault(); onDrawerAction(d.dataset.dact, d); return; }
    const lane = e.target.closest('[data-lane]');
    if (lane) { S.lane = S.lane === lane.dataset.lane ? null : lane.dataset.lane; S.laneRetained.clear(); render(); return; }
    const a = e.target.closest('[data-act]');
    if (!a) return;
    if (e.target.closest('a[href]') && a.dataset.act === 'toggle') return;
    if (a.dataset.act === 'toggle' && e.target.closest('.detail')) return;
    e.stopPropagation();
    const key = a.closest('.pr[data-key]')?.dataset.key;
    onAction(a.dataset.act, a, key).catch((err) => toast(err.message || String(err), 'error'));
  });
  $('#search').addEventListener('input', (e) => { S.q = e.target.value; S.laneRetained.clear(); render(); });
  $('#repoFilter').addEventListener('change', (e) => { S.repo = e.target.value; S.laneRetained.clear(); render(); });
  $('#scope').addEventListener('click', (e) => {
    const b = e.target.closest('button[data-scope]');
    if (!b) return;
    if (b.dataset.scope === 'drafts' || S.scope === 'drafts') S.lane = null;
    S.scope = b.dataset.scope;
    S.laneRetained.clear();
    $('#scope').querySelectorAll('button').forEach((x) => x.classList.toggle('on', x === b));
    render();
  });
  $('#refreshBtn').addEventListener('click', () => rpc('refresh'));
  $('#settingsBtn').addEventListener('click', openDrawer);
  $('#closeDrawer').addEventListener('click', closeDrawer);
  $('#scrim').addEventListener('click', closeDrawer);
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && S.rowMenu) { e.preventDefault(); closeRowMenu(); flushRowUpdates(); return; }
    const inInput = /INPUT|TEXTAREA|SELECT/.test(document.activeElement?.tagName) || document.activeElement?.isContentEditable;
    if (!inInput && e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey && e.key.toLowerCase() === 'z' && S.hideUndo.length) {
      e.preventDefault();
      if (!e.repeat) undoHide();
      return;
    }
    if (e.key === 'Escape') { if (S.drawer) closeDrawer(); else if (inInput) document.activeElement.blur(); else { S.expanded.clear(); renderRows(); } }
    if (inInput) {
      if (e.key === 'Enter' && document.activeElement.id === 'wlInput') onDrawerAction('addWl');
      if (e.key === 'Enter' && document.activeElement.id === 'repoUrl') onDrawerAction('addRepo');
      return;
    }
    if (e.key === '/') { e.preventDefault(); $('#search').focus(); }
    if (e.key.toLowerCase() === 'r' && !e.ctrlKey && !e.altKey && !e.metaKey) rpc('refresh');
  });
  setInterval(() => { renderStatus(); }, 15000);
  setInterval(() => {
    if (rowsInUse()) S.rowsPending = true;
    else if (!document.querySelector('.pr.open')) renderRows();
  }, 60000);
}

async function boot() {
  wire();
  if (host) host.addEventListener('message', (e) => onHostMessage(e.data));
  else window.MockHost.onEvent = onHostMessage;
  const init = await rpc('init');
  S.settings = init.settings;
  for (const run of init.runs || []) updateCopilotRun(run);
  setSnapshot(init.snapshot);
  render();
  window.chrome?.webview?.postMessage('ready');
  if (params.has('expand')) {
    const n = +params.get('expand') || 1;
    baseVisible().slice(0, n).forEach((p) => S.expanded.add(p.key));
    renderRows();
  }
  window.__ready = true;
}

boot().catch((e) => { document.body.insertAdjacentHTML('beforeend', `<pre style="color:#f55">${esc(e.stack || e)}</pre>`); });
