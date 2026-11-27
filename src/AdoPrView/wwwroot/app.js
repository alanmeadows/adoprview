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
    S.settings = m.data; applySavedTheme(); render();
  } else if (m.event === 'toast') {
    toast(m.data.text, m.data.kind);
  } else if (m.event === 'copilotRun') {
    try { updateCopilotRun(m.data); } catch (e) { toast(e.message || String(e), 'error'); }
    finally {
      if (host && Number.isSafeInteger(m.deliveryId))
        host.postMessage(JSON.stringify({ id: ++seq, method: 'copilotRunAck', params: { deliveryId: m.deliveryId } }));
    }
  } else if (m.event === 'explanation') {
    try { updateExplanation(m.data); } catch (e) { toast(e.message || String(e), 'error'); }
  } else if (m.event === 'investigation') {
    try { updateInvestigation(m.data); } catch (e) { toast(e.message || String(e), 'error'); }
  } else if (m.event === 'prVideo') {
    try { updateVideo(m.data); } catch (e) { toast(e.message || String(e), 'error'); }
  } else if (m.event === 'update') {
    setUpdate(m.data);
  } else if (m.event === 'speechSetupProgress' && S.speechSetup?.busy && m.data?.epoch === S.speechEpoch) {
    S.speechSetup.progress = m.data.message; renderSpeechSetup();
  }
}

// ------------------------------------------------------------------- state
const S = {
  settings: null,
  snap: { prs: [] },
  expanded: new Set(),
  runHistoryOpen: new Set(),
  activeKeys: new Set(),
  lane: null,
  laneRetained: new Set(),
  scope: 'all',
  repo: '',
  q: '',
  logs: new Map(),          // logKey() -> text | Error | 'loading'
  logLoads: new Set(),
  showTs: new Set(),
  busy: new Set(),          // "key:action"
  approvals: new Map(),    // key -> last approval provider outcome (never a credential)
  reviewVotes: new Map(),
  autoCompleteErrors: new Map(),
  lastSig: '',
  lastHistorySig: '',
  pendingSnapshot: null,
  pointerInRows: false,
  rowPointerDown: false,
  rowsPending: false,
  runRelocations: new Set(),
  rowOrder: [],
  orderRevision: null,
  hideUndo: [],
  hideUndoBusy: false,
  approvalSettings: null,
  approvalSettingsBusy: false,
  repoAddBusy: false,
  runs: new Map(),
  runOutputs: new Map(),
  explanations: new Map(),
  explanationPrs: new Map(),
  investigations: new Map(),
  videos: new Map(),
  speechSetup: null,
  speechEpoch: 0,
  rowMenu: null,
  drawer: false,
  drawerFocus: null,
  drawerInert: new Map(),
  drawerDraft: new Map(),
  drawerDraftReset: false,
  drawerEpoch: 0,
  themeBusy: false,
  discovered: null,
  version: null,
  update: null,
  updateBusy: false,      // false | 'check' | 'install'
  updateAnnounced: null,
  updateToast: null,
};

const LANES = {
  needsHuman: { label: 'Needs Human', hint: 'Human actions available; merge preview may still be pending or unverified', short: 'Reviewer · PoP · Auto', c: 'var(--needs)' },
  blocked: { label: 'Blocked', hint: 'Agent-fixable problems', short: 'Fix with agent', c: 'var(--blocked)' },
  waiting: { label: 'Waiting', hint: 'Other automated work is pending, or merge verification remains with no human actions', short: 'Pending', c: 'var(--waiting)' },
  completed: { label: 'Completed', hint: 'PRs Azure DevOps completed (merged) in the last 7 days; read-only and not counted in active totals', short: 'Merged · 7 days', c: 'var(--ready)' },
};
const GATES = ['merge', 'build', 'checks', 'comments', 'agency'];
const SCOPES = ['all', 'mine', 'watched'];
const EMPTY_SCOPE = {
  all: 'No active pull requests in your repositories.',
  mine: 'You have no active pull requests in these repositories.',
  watched: 'No active pull requests from you or your watched authors.',
};

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

function toast(text, kind = 'ok', action, sticky = false) {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  el.innerHTML = `<span class="ic"></span><span class="grow">${esc(text)}</span>`;
  for (const a of [action, sticky && { label: 'Dismiss', run: () => {} }]) {
    if (!a) continue;
    const b = document.createElement('button');
    b.textContent = a.label;
    b.onclick = () => { a.run(); el.remove(); };
    if (S.drawer) { S.drawerInert.set(b, b.inert); b.inert = true; }
    el.appendChild(b);
  }
  $('#toasts').appendChild(el);
  if (!sticky) setTimeout(() => el.remove(), kind === 'error' ? 9000 : 5000);
  return el;
}

function setUpdate(state) {
  S.update = state;
  // A notice is tied to the version and install path it offered; replace it if a later check changes either.
  const key = state?.available ? `${state.latest}|${state.canInstall}` : null;
  if (key !== S.updateAnnounced) {
    S.updateToast?.remove(); S.updateToast = null;
    S.updateAnnounced = key;
    if (key) S.updateToast = toast(`ADO PR View v${state.latest} is available (you have v${state.current}). Upgrade to get the latest version.`, 'info',
      state.canInstall ? { label: 'Upgrade', run: () => void installUpdate() }
        : state.releaseUrl ? { label: 'View release', run: () => void openRelease() } : null, true);
  }
  if (S.drawer) renderDrawer();
}

async function checkForUpdate() {
  if (S.updateBusy) return;
  S.updateBusy = 'check'; if (S.drawer) renderDrawer();
  try { setUpdate(await rpc('checkForUpdate')); }
  catch (e) { toast(e.message || String(e), 'error'); }
  finally { S.updateBusy = false; if (S.drawer) renderDrawer(); }
}

async function installUpdate() {
  const u = S.update;
  if (!u?.canInstall || S.updateBusy) return;
  if (!confirm(`ADO PR View will close, install v${u.latest} from GitHub, and reopen. Running Copilot tasks keep going and reconnect when it reopens. Continue?`)) return;
  S.updateBusy = 'install'; if (S.drawer) renderDrawer();
  toast(`Downloading v${u.latest} from GitHub…`, 'info');
  try { await rpc('installUpdate'); }
  catch (e) { toast(e.message || String(e), 'error'); }
  finally { S.updateBusy = false; if (S.drawer) renderDrawer(); }
}

function openRelease() {
  if (S.update?.releaseUrl) rpc('openUrl', { url: S.update.releaseUrl }).catch(e => toast(e.message || String(e), 'error'));
}

function updateSection() {
  const u = S.update, busy = S.updateBusy;
  const status = busy === 'check' ? 'Checking for updates…' : busy === 'install' ? `Downloading v${u.latest} from GitHub…`
    : u?.error ? u.error : u?.available ? `v${u.latest} is available.`
    : u?.checkedAt ? `You're up to date (checked ${new Date(u.checkedAt).toLocaleString()}).` : 'Not checked yet.';
  return `<div class="section">
      <h4>Updates</h4>
      <p class="help">Version ${esc(u?.current || S.version || 'unknown')}</p>
      <p class="help${u?.error && !busy ? ' error' : ''}" id="updateStatus">${esc(status)}</p>
      ${u?.available && !u.canInstall ? '<p class="help">This copy isn\'t the installed app, so upgrade it manually.</p>' : ''}
      <div class="field"><button class="btn" data-dact="checkUpdate" ${busy ? 'disabled' : ''}>${svg('refresh')} Check for updates</button>
        ${!u?.available ? '' : u.canInstall ? `<button class="btn primary" data-dact="installUpdate" ${busy ? 'disabled' : ''}>Upgrade to v${esc(u.latest)}</button>`
          : u.releaseUrl ? `<button class="btn" data-dact="openRelease">${svg('ext')} Open release page</button>` : ''}</div>
    </div>`;
}

const findPr = (key) => S.snap.prs.find((p) => p.key === key);
// Completed history is display-only: Open and Hide may use it, every other action stays on active PRs.
const findShown = (key) => findPr(key) || (S.snap.completed || []).find((c) => c.key === key);

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
    if (S.scope === 'watched' && !p.isMine && !p.isWatched) return false;
    if (S.repo && p.repo !== S.repo) return false;
    if (q) {
      const hay = `${p.id} !${p.id} ${p.title} ${p.sourceBranch} ${p.repo} ${p.author?.name} ${p.author?.uniqueName}`.toLowerCase();
      if (!q.split(/\s+/).every((t) => hay.includes(t))) return false;
    }
    return true;
  });
}

function completedVisible() {
  const hidden = hiddenSet();
  const q = S.q.trim().toLowerCase();
  if (S.scope === 'drafts') return [];
  return (S.snap.completed || []).filter((c) => {
    if (hidden.has(c.key.toLowerCase())) return false;
    if (S.scope === 'mine' && !c.isMine) return false;
    if (S.scope === 'watched' && !c.isMine && !c.isWatched) return false;
    if (S.repo && c.repo !== S.repo) return false;
    const hay = `${c.id} !${c.id} ${c.title} ${c.repo} ${c.author?.name} ${c.author?.uniqueName}`.toLowerCase();
    return !q || q.split(/\s+/).every((t) => hay.includes(t));
  });
}

// --------------------------------------------------------------- rendering
function rowsInUse() {
  const focused = document.activeElement;
  return !!S.rowMenu || S.pointerInRows || S.rowPointerDown || !!(focused?.closest('.pr') && focused.matches(':focus-visible'));
}

function flushRowUpdates() {
  if (rowsInUse() || crossRunOutputSelection()) return;
  if (S.pendingSnapshot) setSnapshot(S.pendingSnapshot);
  for (const key of [...S.runRelocations]) relocateRunGroup(key);
  // A relocation still queued here is held by a text selection; a full rebuild would detach it. The idle-aware rebuild
  // also stays pending while a crossing selection touches run cards, and selectionchange retries it once cleared.
  if (S.rowsPending && !S.runRelocations.size) renderRows(true);
  renderStatus();
}

function setSnapshot(snap) {
  snap = snap || { prs: [] };
  const currentRevision = (S.pendingSnapshot || S.snap).revision;
  if (Number.isSafeInteger(currentRevision) && Number.isSafeInteger(snap.revision) && snap.revision < currentRevision) return;
  const revision = snap.orderRevision ?? 0;
  const sorted = [...snap.prs].sort((a, b) =>
    Object.keys(LANES).indexOf(a.lane) - Object.keys(LANES).indexOf(b.lane) ||
    (Date.parse(b.updated) || 0) - (Date.parse(a.updated) || 0));
  const byKey = new Map(snap.prs.map(p => [p.key, p]));
  for (const [key, outcome] of S.reviewVotes) {
    if (outcome.kind === 'confirmed' && (byKey.get(key)?.reviewVote?.vote !== 10 ||
        JSON.stringify(byKey.get(key)?.reviewVote?.target) !== JSON.stringify(findPr(key)?.reviewVote?.target)))
      S.reviewVotes.delete(key);
  }
  const oldKeys = new Set(S.rowOrder);
  const order = S.orderRevision === null || revision !== S.orderRevision
    ? sorted.map(p => p.key)
    : [...S.rowOrder.filter(key => byKey.has(key)), ...sorted.filter(p => !oldKeys.has(p.key)).map(p => p.key)];
  snap = { ...snap, prs: order.map(key => byKey.get(key)) };
  // History-only changes rebuild rows only while the Completed lane shows them; elsewhere the header count updates.
  const sig = JSON.stringify(snap.prs), historySig = JSON.stringify([snap.completed || [], snap.completedWarnings || []]);
  const rowsChanged = sig !== S.lastSig || revision !== S.orderRevision || (S.lane === 'completed' && historySig !== S.lastHistorySig);
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
    for (const pr of S.snap.prs) syncReviewVoteControls(pr.key);
    renderStatus();
    return;
  }
  S.pendingSnapshot = null;
  if (revision !== S.orderRevision) S.laneRetained.clear();
  S.snap = snap;
  S.rowOrder = order;
  S.orderRevision = revision;
  S.lastSig = sig;
  S.lastHistorySig = historySig;
  renderHeader();
  if (rowsChanged) renderRows(true);
}

function render() { renderHeader(); renderRows(); if (S.drawer) renderDrawer(); }

function renderHeader() {
  if (!S.settings?.ignoreDrafts && S.scope === 'drafts') S.scope = 'all';
  const vis = baseVisible();
  $('#draftFilter').hidden = !S.settings?.ignoreDrafts;
  document.querySelectorAll('#scope button').forEach(b => b.classList.toggle('on', b.dataset.scope === S.scope));
  $('#laneCounters').innerHTML = Object.entries(LANES).map(([k, m]) => {
    const n = k === 'completed' ? completedVisible().length : vis.filter((p) => !ignoredDraft(p) && p.lane === k).length;
    return `<button class="lane-btn ${S.lane === k ? 'on' : ''} ${n ? '' : 'zero'} ${n && (k === 'needsHuman' || k === 'blocked') ? 'pulse' : ''}" data-lane="${k}" style="--c:${m.c}">
      <span class="bar"></span><span class="n">${n}</span><span class="l"><b>${m.label}</b><span>${m.short}</span></span></button>`;
  }).join('');

  const repos = [...new Set([...S.snap.prs, ...(S.snap.completed || [])].map((p) => p.repo))].sort();
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
  if (s.lastRefresh) parts.push(`<span>${vis.length} active PR${vis.length === 1 ? '' : 's'} across ${repoCount} repo${repoCount === 1 ? '' : 's'}</span>`);
  if (hiddenCount) parts.push(`<span>${hiddenCount} hidden</span>`);
  if (S.settings) parts.push(`<span>auto-refresh ${Math.round(S.settings.refreshSeconds / 60 * 10) / 10}m · merge re-check ${S.settings.mergeCheckMinutes}m</span>`);
  parts.push('<span>Row order stays fixed until Refresh (R)</span>');
  if (s.mergeCheckStatus) parts.push(`<span>${esc(s.mergeCheckStatus)}</span>`);
  if (S.pendingSnapshot || S.rowsPending || S.runRelocations.size) parts.push('<span>Table updates paused while interacting with rows</span>');
  if (S.lane && vis.some(p => S.laneRetained.has(p.key) && p.lane !== S.lane))
    parts.push('<span>Rows that changed lane stay here until Refresh or a filter change</span>');
  if (s.error) parts.push(`<span class="err">⚠ ${esc(s.error)}</span>`);
  for (const e of s.repoErrors || []) parts.push(`<span class="err">⚠ ${esc(e)}</span>`);
  $('#statusline').innerHTML = parts.join('<span class="small">·</span>');
}

function laneVisible(prs) {
  return S.lane ? prs.filter(p => !ignoredDraft(p) && (p.lane === S.lane || S.laneRetained.has(p.key))) : prs;
}

function renderRows(whenIdle = false) {
  const assistanceOutputs = captureExplanationOutputs();
  const assistanceFocus = document.activeElement?.closest('.explanation') ? {
    key: document.activeElement.closest('.explanation').dataset.explainKey,
    role: document.activeElement.dataset.inv,
    start: document.activeElement.selectionStart, end: document.activeElement.selectionEnd,
  } : null;
  for (const card of document.querySelectorAll('.explanation')) rememberAssistancePosition(card.dataset.explainKey);
  const focused = expansionControl(document.activeElement);
  const explainFocus = document.activeElement?.closest('.explanation') ? {
    key: document.activeElement.closest('.explanation').dataset.explainKey,
    requestId: document.activeElement.dataset.requestId,
    action: document.activeElement.dataset.act,
    element: document.activeElement,
  } : null;
  const selection = captureRunOutputs();
  const players = new Map([...document.querySelectorAll('video[data-video-media]')].map(v => [v.dataset.videoMedia, v]));
  renderRowsContent(whenIdle);
  renderExplanationShelf();
  restoreExplanationOutputs(assistanceOutputs);
  restoreRunOutputs(selection);
  restoreVideoPlayers(players);
  restoreExpansionFocus(focused);
  for (const card of document.querySelectorAll('.explanation')) {
    const key = card.dataset.explainKey, d = assistanceDraft(key);
    const position = d.positions.get(assistanceSelection(key)?.id || d.selected);
    const output = card.querySelector('.assistance-content > .assistance-explanation > .explain-output, .assistance-content > .investigation-transcript');
    if (output && position) { output.scrollTop = position.top; output.scrollLeft = position.left; }
  }
  if (assistanceFocus?.role) {
    const next = assistanceCard(assistanceFocus.key)?.querySelector(`[data-inv="${assistanceFocus.role}"]`);
    next?.focus({ preventScroll: true });
    if (typeof assistanceFocus.start === 'number' && next?.setSelectionRange) next.setSelectionRange(assistanceFocus.start, assistanceFocus.end);
  }
  if (explainFocus?.action === 'cancelExplanation' && !explainFocus.element.isConnected) {
    const card = [...document.querySelectorAll('.explanation')].find(c => c.dataset.explainKey === explainFocus.key);
    const cancel = card && $('[data-act="cancelExplanation"]', card);
    if (cancel?.dataset.requestId === explainFocus.requestId && !cancel.hidden && !cancel.disabled)
      cancel.focus({ preventScroll: true });
  }
}

function expansionControl(target) {
  const control = target?.closest?.('[role="button"][data-act][aria-expanded]');
  return control && !control.closest('.md') && control.matches(
    '.row > .c-pr[data-act="toggle"], .row > .gate > .pill[data-act="gate"], #runShelf > .pr > .run-heading[data-act="toggle"]')
    ? control : null;
}

function restoreExpansionFocus(control) {
  if (!control || control.isConnected) return;
  const key = control.closest('.pr')?.dataset.key;
  const row = [...document.querySelectorAll('.pr')].find(el => el.dataset.key === key);
  const next = [...(row?.querySelectorAll('[role="button"][data-act][aria-expanded]') || [])]
    .find(el => expansionControl(el) === el && el.dataset.act === control.dataset.act && el.dataset.gate === control.dataset.gate);
  next?.focus({ preventScroll: true });
}

function renderRowsContent(whenIdle = false) {
  if (S.rowPointerDown || (whenIdle && (rowsInUse() || crossRunOutputSelection() ||
    selectionBlocksMove([...document.querySelectorAll('#rows .copilot-run, #rows .lead-blockers, #runShelf .copilot-run')])))) { S.rowsPending = true; renderStatus(); return; }
  S.rowsPending = false;
  S.runRelocations.clear();
  const vis = baseVisible();
  renderRunShelfContent();
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
  if (S.lane === 'completed') return renderCompletedRows();
  const shown = laneVisible(vis);
  if (S.lane) shown.forEach(p => S.laneRetained.add(p.key));
  if (!shown.length) {
    rows.innerHTML = '';
    empty.hidden = false;
    empty.innerHTML = `<div class="big">✨</div><h2>Nothing here</h2><div>${S.q || S.lane || S.repo ? 'No PRs match the current filters.' : EMPTY_SCOPE[S.scope] || 'No PRs match the current filters.'}</div>`;
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
  syncIdCues();
}

// Read-only rows: no expansion and no launchers, only Open (and Hide from the row menu).
function renderCompletedRows() {
  const rows = $('#rows'), empty = $('#empty');
  const shown = completedVisible();
  const notes = (S.snap.completedWarnings || []).map(w => `<div class="completed-note" role="status">⚠ ${esc(w)}</div>`).join('');
  rows.innerHTML = notes + shown.map(c => `<div class="pr completed" data-key="${esc(c.key)}" data-completed style="--c:var(--ready)">
    <div class="id-cue" aria-hidden="true"><span><b>!${c.id} · </b><i>${esc(c.repo)}</i></span></div>
    <div class="row">
      <div class="c-pr"><div class="prmain">
        <div class="title" title="${esc(c.title)}">${esc(c.title)}</div>
        <div class="meta">${c.isMine ? '' : `<span class="tag watch" title="${esc(c.author?.uniqueName || '')}">${avatar(c.author?.name)} ${esc(c.author?.name)}</span>`}<span class="row-lane" title="${esc(LANES.completed.hint)}">Completed</span><span class="sep">·</span><span class="id">!${c.id}</span><span class="sep">·</span><span>${esc(c.repo)}</span></div>
      </div></div>
      ${GATES.map(() => '<div class="gate"><span class="pill na"><span>—</span></span></div>').join('')}
      <div class="c-upd" title="Completed ${esc(new Date(c.closed).toLocaleString())}${c.closedBy ? ' by ' + esc(c.closedBy) : ''}">${ago(c.closed)}<small>merged</small></div>
      <div class="c-act">${['approval', 'publish', 'pop', 'auto', 'fix'].map(slot => `<span class="action-slot" data-slot="${slot}"></span>`).join('')}<span class="action-slot" data-slot="open"><button class="act" data-act="open" title="Open in Azure DevOps" aria-label="Open in Azure DevOps">${svg('ext')}</button></span></div>
    </div>
  </div>`).join('');
  empty.hidden = !!shown.length;
  if (!shown.length) empty.innerHTML = '<div class="big">✨</div><h2>Nothing here</h2><div>No completed PRs from the last 7 days match the current filters.</div>';
  syncIdCues();
}

// Narrow windows pan the table; once a row's PR cell is fully out of view, show its compact id cue.
function syncIdCues() {
  const scroller = $('main');
  const left = scroller.scrollLeft > 0 ? scroller.getBoundingClientRect().left + scroller.clientLeft : 0;
  for (const row of $('#rows').children) {
    const cell = row.matches('.pr') && scroller.scrollLeft > 0 && $('.row > .c-pr', row);
    const panned = !!cell && cell.getBoundingClientRect().right <= left;
    row.classList.toggle('id-panned', panned);
    // Rows can also be scrolled internally by scrollIntoView, so measure rather than assume main.scrollLeft.
    if (panned) $('.id-cue', row).style.transform = `translateX(${left - row.getBoundingClientRect().left - row.clientLeft + row.scrollLeft}px)`;
  }
}
let idCueFrame = 0;
const queueIdCues = () => { if (!idCueFrame) idCueFrame = requestAnimationFrame(() => { idCueFrame = 0; syncIdCues(); }); };

const runActive = r => !!r && (r.mayBeRunning === true || ['preparing', 'recovering', 'running', 'cancelling'].includes(r.status));
const runCancelable = r => runActive(r) || (r?.status === 'needs-attention' && r.id > 0);
const runsForPr = key => [...S.runs.values()].reverse().filter(r => r.key === key)
  .sort((a, b) => (Date.parse(b.startedAt) || 0) - (Date.parse(a.startedAt) || 0));
// A live run, else an outstanding (cancelable) recovery run, else the newest run.
const runForPr = key => runsForPr(key).find(runActive) || runsForPr(key).find(runCancelable) || runsForPr(key).at(0);
const explanationActive = r => !!r && ['preparing', 'running', 'cancelling'].includes(r.status);
const explanationLabel = r => r?.status === 'failed' || r?.status === 'cancelled' ? 'Retry Explain' : r ? 'Explain again' : 'Explain this PR';
function canExplain(key) {
  const p = S.pendingSnapshot ? S.pendingSnapshot.prs.find(p => p.key === key) : findPr(key);
  return !!p && !p.error && !p.isHidden && !hiddenSet().has(key.toLowerCase()) && !ignoredDraft(p);
}

function explanationRevision(r) {
  const v = r?.revision;
  return v ? `Iteration ${v.iteration} · source ${v.sourceCommit.slice(0, 12)} · merge base ${v.baseCommit.slice(0, 12)}${v.targetCommit ? ` · target ${v.targetCommit.slice(0, 12)}` : ''}` : '';
}

function explanationBody(r) {
  if (r?.status !== 'completed' || !r.output) return '';
  const renderer = window.marked ? new marked.Renderer() : undefined;
  if (renderer) {
    renderer.code = ({ text, lang }) => {
      const label = lang?.split(/\s/)[0].toLowerCase();
      const title = label === 'bicep' ? 'Bicep' : label === 'diff' ? 'Diff (- removed, + added)' : 'Code';
      const body = text.split('\n').map(line => {
        const safe = esc(line);
        return label === 'diff' && /^[+-]/.test(line) && !/^(\+\+\+|---)/.test(line)
          ? (line[0] === '+' ? `<ins>${safe}</ins>` : `<del>${safe}</del>`) : safe;
      }).join('\n');
      return `<p><strong>${title}</strong></p><pre><code>${body}\n</code></pre>`;
    };
  }
  const html = window.marked ? marked.parse(r.output, { gfm: true, breaks: true, renderer }) : esc(r.output);
  // Model output cannot load remote media, navigate links or inject interactive controls.
  return window.DOMPurify ? DOMPurify.sanitize(html, {
    ALLOWED_TAGS: ['p', 'br', 'strong', 'em', 'ul', 'ol', 'li', 'code', 'pre', 'h1', 'h2', 'h3', 'h4',
      'table', 'thead', 'tbody', 'tr', 'th', 'td', 'blockquote', 'hr', 'del', 'ins'],
    ALLOWED_ATTR: [],
  }) : esc(r.output);
}

function renderExplanation(key) {
  const d = assistanceDraft(key), video = S.videos.get(key);
  return `<div class="card explanation" data-explain-key="${esc(key)}">
    <h3><span class="grow">PR assistance</span><button class="mini" data-assistance-close aria-expanded="${!d.closed}">${d.closed ? 'Reopen assistance' : 'Close assistance'}</button></h3>
    <div class="body assistance-body" ${d.closed ? 'hidden' : ''}>
      <div class="assistance-content">${renderAssistanceContent(key)}</div>
      <details class="assistance-video" ${video ? 'open' : ''}><summary>Alternate output: short video explainer, with cats${videoActive(video) ? ' · generating' : ''}</summary>
        <p class="small">Separate video generation and voice pipeline; this does not automatically narrate the selected explanation. Its own source revision is shown below.</p>${renderVideo(key)}</details>
    </div></div>`;
}

function updateExplanation(r) {
  if (!r || typeof r.key !== 'string' || typeof r.requestId !== 'string' || !Number.isSafeInteger(r.sequence) ||
      !['preparing', 'running', 'cancelling', 'completed', 'cancelled', 'failed'].includes(r.status) ||
      typeof r.output !== 'string' || !Array.isArray(r.coverage))
    throw new Error('Copilot returned an invalid explanation state.');
  const old = S.explanations.get(r.key);
  if (old && (r.sequence <= old.sequence || old.requestId === r.requestId && !explanationActive(old) && explanationActive(r))) return;
  S.explanations.set(r.key, r);
  retainExplanation(r);
  const d = assistanceDraft(r.key);
  if (!old || old.requestId !== r.requestId) {
    if (d.kind === 'explain') d.selected = `explain:${r.requestId}`;
    renderInvestigation(r.key);
  } else if (explanationActive(r) !== explanationActive(old) || !explanationActive(r) && old.status !== r.status) {
    renderInvestigation(r.key);
  }
  // Incremental events update status only; completed prose never follows the stream.
  for (const card of document.querySelectorAll('.explanation')) {
    if (card.dataset.explainKey !== r.key) continue;
    const result = $('.assistance-explanation', card);
    if (!result || result.dataset.resultRequest !== r.requestId) continue;
    const active = explanationActive(r);
    const status = $('.explain-status', card);
    status.textContent = r.message;
    status.classList.toggle('error', r.status === 'failed');
    $('.explain-revision', card).textContent = explanationRevision(r);
    const output = $('.explain-output', card);
    output.hidden = r.status !== 'completed' || !r.output;
    const body = explanationBody(r);
    if (output.innerHTML !== body) output.innerHTML = body;
    const waiting = $('.explain-waiting', result);
    if (waiting) waiting.hidden = !active;
    const start = $('[data-act="explainPr"]', card);
    if (start) {
      start.disabled = active || isBusy(r.key, 'explainPr') || !canExplain(r.key);
      start.innerHTML = `${svg('spark')} ${explanationLabel(r)}`;
    }
    const cancel = $('[data-act="cancelExplanation"]', card);
    if (cancel) {
      cancel.hidden = !active;
      cancel.disabled = r.status === 'cancelling';
      cancel.dataset.requestId = r.requestId;
    }
  }
  renderExplanationShelf();
}

function explanationIdentity(r) {
  if (!S.explanationPrs.has(r.key)) {
    const p = findPr(r.key);
    if (p) S.explanationPrs.set(r.key, { id: p.id, repo: p.repo, title: p.title });
  }
  const p = S.explanationPrs.get(r.key) || r.revision;
  return `Assistance for ${p?.repo || r.key}${p?.id ? ` !${p.id}` : ''}${p?.title ? `: ${p.title}` : ''} outside the expanded view`;
}

function renderExplanationShelf() {
  const shelf = $('#explanationShelf');
  if (!shelf) return;
  const players = new Map([...document.querySelectorAll('video[data-video-media]')].map(v => [v.dataset.videoMedia, v]));
  const inline = new Set([...document.querySelectorAll('#rows .explanation')].map(c => c.dataset.explainKey));
  const outside = [...new Map([
    ...[...S.explanations.values()].filter(explanationActive),
    ...[...S.investigations.values()].filter(r => investigationActive(r) || investigationKey === r.key && !assistanceDraft(r.key).closed),
  ].map(r => [r.key, r])).values()].filter(r => !inline.has(r.key));
  const missing = new Set(outside.map(r => r.key));
  for (const entry of shelf.querySelectorAll('.explanation-only')) {
    if (missing.has(entry.dataset.key)) missing.delete(entry.dataset.key);
    else entry.remove();
  }
  shelf.insertAdjacentHTML('beforeend', outside.filter(r => missing.has(r.key)).map(r => `<div class="pr explanation-only" data-key="${esc(r.key)}"><div class="detail"><div class="full">
    <p class="small explanation-identity">${esc(explanationIdentity(r))}</p>
    ${renderExplanation(r.key)}</div></div></div>`).join(''));
  for (const card of shelf.querySelectorAll('.explanation')) {
    const key = card.dataset.explainKey;
    $('.explanation-identity', card.closest('.explanation-only')).textContent = explanationIdentity(S.explanations.get(key) || S.investigations.get(key));
    const start = $('[data-act="explainPr"]', card);
    if (start) start.disabled = !canExplain(key) || explanationActive(S.explanations.get(key)) || isBusy(key, 'explainPr');
  }
  renderVideoShelf();
  restoreVideoPlayers(players);
}

function restoreVideoPlayers(players) {
  for (const placeholder of document.querySelectorAll('video[data-video-media]')) {
    const existing = players.get(placeholder.dataset.videoMedia);
    if (existing && existing !== placeholder) placeholder.replaceWith(existing);
  }
}

const videoActive = r => !!r && ['preparing', 'storyboarding', 'rendering', 'cancelling'].includes(r.status);
const videoLabel = r => r?.status === 'failed' || r?.status === 'cancelled' ? 'Retry video' : r?.status === 'completed' ? 'Generate again' : 'Generate video';
function canVideo(key) {
  const p = S.pendingSnapshot ? S.pendingSnapshot.prs.find(p => p.key === key) : findPr(key);
  return canExplain(key) && p?.isActive !== false;
}

function videoContent(r) {
  if (!r) return '';
  if (r.status !== 'completed') return videoActive(r)
    ? `<div class="video-loading" aria-label="Generating PR video"><span class="video-spinner" aria-hidden="true"></span><strong>Generating...</strong><span class="small">This can take several minutes. You can keep using the dashboard.</span></div>` : '';
  return `<video controls playsinline preload="metadata" width="1280" height="720" data-video-media="${esc(r.requestId)}"
      src="${esc(r.mediaUrl)}" aria-label="AI-generated PR video with narrated captions"></video>
    <p class="video-playback-error error small" role="status" hidden></p>
    <p class="small video-path">${esc(r.path)}</p><button class="mini" data-act="copyVideoPath">${svg('copy')} Copy MP4 path</button>`;
}

function renderVideo(key) {
  const r = S.videos.get(key), active = videoActive(r);
  return `<div class="card pr-video" data-video-key="${esc(key)}">
    <h3><span class="grow">Short PR video explainer, with cats</span>
      <button class="mini" data-act="tiktokPr" ${active || isBusy(key, 'tiktokPr') || !canVideo(key) ? 'disabled' : ''}>${svg('play')} ${videoLabel(r)}</button>
      <button class="mini" data-act="cancelVideo" data-request-id="${esc(r?.requestId || '')}" ${active ? '' : 'hidden'} ${r?.status === 'cancelling' ? 'disabled' : ''}>${svg('x')} Cancel</button></h3>
    <div class="body">
      <p class="video-status small${r?.status === 'failed' ? ' error' : ''}" role="status" aria-live="polite" ${r ? '' : 'hidden'}>${esc(r?.message || '')}</p>
      <div class="video-content" aria-busy="${active}">${videoContent(r)}</div>
      <details class="explain-details"><summary>Source evidence and generation details</summary>
        <p class="small">Opus 5.5 reads pinned PR code; narration uses your selected Azure Speech resource. No PR changes; the video stays local.</p>
        <p class="small">AI-generated; verify important details against the code. Bounded source inspection, not a local checkout or Fix run. Videos are not resumed automatically.</p>
        <p class="video-meta small">${esc([r?.model, r?.voice, r?.durationSeconds && `${Math.round(r.durationSeconds)} seconds`].filter(Boolean).join(' · '))}</p>
        <p class="video-revision small">${esc(explanationRevision(r))}</p>
        <div class="video-evidence small">${(r?.evidence || []).map(x => `<p>${esc(x)}</p>`).join('')}</div>
        <div class="video-coverage small">${(r?.coverage || []).map(x => `<p>${esc(x)}</p>`).join('')}</div>
      </details>
    </div></div>`;
}

function updateVideo(r) {
  if (!r || typeof r.key !== 'string' || typeof r.requestId !== 'string' || !Number.isSafeInteger(r.sequence) ||
      !['preparing', 'storyboarding', 'rendering', 'cancelling', 'completed', 'cancelled', 'failed'].includes(r.status) ||
      typeof r.message !== 'string' || !Array.isArray(r.evidence) || !Array.isArray(r.coverage) ||
      r.status === 'completed' && (!/^[a-f0-9]{32}$/.test(r.requestId) ||
        r.mediaUrl !== `https://pr-video.local/${r.requestId}.mp4` || typeof r.path !== 'string' ||
        !(r.durationSeconds > 0) || r.model !== 'claude-opus-5.5'))
    throw new Error('Copilot returned an invalid video state. No media was opened.');
  const old = S.videos.get(r.key);
  if (old && (r.sequence <= old.sequence || old.requestId === r.requestId && !videoActive(old) && videoActive(r))) return;
  S.videos.set(r.key, r);
  for (const card of document.querySelectorAll('.pr-video')) {
    if (card.dataset.videoKey !== r.key) continue;
    $('.video-status', card).textContent = r.message;
    $('.video-status', card).hidden = false;
    $('.video-status', card).classList.toggle('error', r.status === 'failed');
    $('.video-meta', card).textContent = [r.model, r.voice, r.durationSeconds && `${Math.round(r.durationSeconds)} seconds`].filter(Boolean).join(' · ');
    $('.video-revision', card).textContent = explanationRevision(r);
    $('.video-evidence', card).innerHTML = r.evidence.map(x => `<p>${esc(x)}</p>`).join('');
    $('.video-coverage', card).innerHTML = r.coverage.map(x => `<p>${esc(x)}</p>`).join('');
    const content = $('.video-content', card), active = videoActive(r);
    // Keep an existing player's DOM/audio/seek position while unrelated status events arrive.
    if (!old || old.requestId !== r.requestId || old.status !== r.status) content.innerHTML = videoContent(r);
    content.setAttribute('aria-busy', active);
    const start = $('[data-act="tiktokPr"]', card);
    start.disabled = active || isBusy(r.key, 'tiktokPr') || !canVideo(r.key);
    start.innerHTML = `${svg('play')} ${videoLabel(r)}`;
    const cancel = $('[data-act="cancelVideo"]', card);
    cancel.hidden = !active;
    cancel.disabled = r.status === 'cancelling';
    cancel.dataset.requestId = r.requestId;
  }
  renderVideoShelf();
}

function renderVideoShelf() {
  const shelf = $('#videoShelf');
  if (!shelf) return;
  const inline = new Map([...document.querySelectorAll('.pr-video')]
    .filter(card => !shelf.contains(card)).map(card => [card.dataset.videoKey, card]));
  for (const entry of shelf.querySelectorAll('.video-only')) {
    const owner = inline.get(entry.dataset.key);
    const player = entry.querySelector('video[data-video-media]');
    const placeholder = owner?.querySelector('video[data-video-media]');
    if (player && placeholder?.dataset.videoMedia === player.dataset.videoMedia) placeholder.replaceWith(player);
  }
  const shown = new Set(inline.keys());
  const outside = [...S.videos.values()].filter(r => !shown.has(r.key));
  const missing = new Set(outside.map(r => r.key));
  for (const entry of shelf.querySelectorAll('.video-only')) {
    if (missing.has(entry.dataset.key)) missing.delete(entry.dataset.key);
    else entry.remove();
  }
  shelf.insertAdjacentHTML('beforeend', outside.filter(r => missing.has(r.key)).map(r => `<div class="pr video-only" data-key="${esc(r.key)}"><div class="detail"><div class="full">
    <p class="small">Video for ${esc(r.revision?.repo || 'PR')} !${esc(r.revision?.id || '')} outside the current view</p>${renderVideo(r.key)}</div></div></div>`).join(''));
  for (const card of shelf.querySelectorAll('.pr-video')) {
    const key = card.dataset.videoKey;
    $('[data-act="tiktokPr"]', card).disabled = videoActive(S.videos.get(key)) || isBusy(key, 'tiktokPr') || !canVideo(key);
  }
}

function runSummary(r) {
  return `${r.status}${r.exitCode == null ? '' : ` · exit ${r.exitCode}`}: ${r.message || ''}`;
}

function runWorktreeText(r) {
  if (!r) return '';
  const label = r.worktreeStatus === 'removed-clean' ? 'Clean worktree removed' : r.worktreeRetained && !runActive(r) ? 'Preserved worktree' : 'Worktree';
  return [r.worktreePath && `${label}: ${r.worktreePath}`, r.sessionId && `Copilot session: ${r.sessionId}`,
    r.logPath && `Supervised log: ${r.logPath}`, r.preparationLogPath && `Private preparation log (if created): ${r.preparationLogPath}`].filter(Boolean).join('\n');
}

const runOutputAtBottom = el => el.scrollHeight - el.clientHeight - el.scrollTop <= 2;
function runOutputSelected(el) {
  const selection = window.getSelection();
  return selection?.isCollapsed === false && (el.contains(selection.anchorNode) || el.contains(selection.focusNode));
}

function crossRunOutputSelection() {
  const selection = window.getSelection();
  if (selection?.isCollapsed !== false) return null;
  const anchor = selection.anchorNode;
  const card = (anchor?.nodeType === 1 ? anchor : anchor?.parentElement)?.closest('.copilot-run');
  if (!card?.contains(selection.focusNode)) return null;
  const output = $('.run-output', card);
  if (!output || (output.contains(anchor) && output.contains(selection.focusNode))) return null;
  const range = selection.getRangeAt(0);
  return range.intersectsNode(output) ? { card, output, range } : null;
}

function rememberRunOutputAnchor(view) {
  const el = view.element, node = el.firstChild;
  view.anchor = null;
  if (view.following || node?.nodeType !== 3 || !node.length) return;
  const box = el.getBoundingClientRect(), y = box.top + parseFloat(getComputedStyle(el).paddingTop) + 1;
  const range = document.createRange();
  let low = 0, high = node.length - 1;
  // Range geometry works even outside the dashboard viewport, unlike caret hit testing.
  while (low < high) {
    const middle = Math.floor((low + high) / 2);
    range.setStart(node, middle); range.setEnd(node, middle + 1);
    if (range.getBoundingClientRect().bottom <= y) low = middle + 1;
    else high = middle;
  }
  range.setStart(node, low); range.setEnd(node, low + 1);
  view.anchor = { offset: low, top: range.getBoundingClientRect().top - box.top };
}

function runOutputRetention(before, text, oldOffset, newOffset) {
  const notice = '[Earlier output truncated]\n';
  const oldPrefix = before.startsWith(notice) ? notice.length : 0;
  const newPrefix = text.startsWith(notice) ? notice.length : 0;
  const removed = newOffset - oldOffset + before.length - oldPrefix - (text.length - newPrefix);
  return { oldPrefix, newPrefix, removed, retentionMoved: newPrefix && Number.isSafeInteger(removed) && removed > 0 };
}

function rememberRunOutput(view) {
  const el = view.element;
  if (!el.isConnected || !el.clientHeight) return;
  const selected = runOutputSelected(el);
  if (el.scrollTop !== view.top) {
    view.following = runOutputAtBottom(el);
    view.selectionPaused = selected && view.following;
  }
  if (selected) {
    if (view.following) view.selectionPaused = true;
    view.following = false;
  } else {
    if (view.selectionPaused && runOutputAtBottom(el)) {
      const run = S.runs.get(el.dataset.runId);
      if (!run || !runOutputRetention(el.textContent, run.output, view.offset, run.outputOffset).retentionMoved)
        view.following = true;
    }
    view.selectionPaused = false;
  }
  view.top = el.scrollTop;
  view.left = el.scrollLeft;
  rememberRunOutputAnchor(view);
}

function captureRunOutputs() {
  // Apply queued retention before capturing offsets or detaching selected nodes.
  for (const card of document.querySelectorAll('.copilot-run')) syncRunOutput(card);
  for (const [id, view] of S.runOutputs) {
    rememberRunOutput(view);
    if (view.element.isConnected && (runOutputSelected(view.element) ||
        view.element === document.activeElement || !view.following))
      S.runHistoryOpen.add(id);
  }
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed !== false) return null;
  const anchor = selection.anchorNode;
  const output = (anchor?.nodeType === 1 ? anchor : anchor?.parentElement)?.closest('.run-output');
  if (!output?.contains(selection.focusNode)) return null;
  return { anchor: selection.anchorNode, anchorOffset: selection.anchorOffset,
    focus: selection.focusNode, focusOffset: selection.focusOffset };
}

function restoreRunOutputs(selection) {
  for (const card of document.querySelectorAll('.copilot-run')) syncRunOutput(card);
  for (const [id, view] of S.runOutputs) {
    if (!S.runs.has(id)) S.runOutputs.delete(id);
    // Hiding a panel can clear selection without a deliberate return to the bottom.
    else if (!view.element.isConnected) view.selectionPaused = false;
  }
  restoreRunOutputSelection(selection);
}

function restoreRunOutputSelection(selection) {
  if (selection?.anchor.isConnected && selection.focus.isConnected)
    window.getSelection().setBaseAndExtent(selection.anchor, selection.anchorOffset, selection.focus, selection.focusOffset);
}

function syncRunOutput(card) {
  const r = S.runs.get(card.dataset.runId);
  if (!r) return;
  let output = $('.run-output', card), view = S.runOutputs.get(r.runId);
  if (view?.element === output) rememberRunOutput(view);
  if (!view) {
    // A different run starts following; never reuse the earlier run's reading state.
    if (output.dataset.runId && output.dataset.runId !== r.runId) {
      const fresh = output.cloneNode(false);
      output.replaceWith(fresh); output = fresh;
    }
    view = { element: output, following: true, top: 0, left: 0 };
    output.dataset.runId = r.runId;
    S.runOutputs.set(r.runId, view);
    output.addEventListener('scroll', () => rememberRunOutput(view));
    output.addEventListener('wheel', e => {
      if (e.deltaY < 0 && output.scrollTop > 0) { view.following = false; view.selectionPaused = false; }
      else if (e.deltaY > 0 && runOutputAtBottom(output)) {
        if (runOutputSelected(output)) view.selectionPaused = true;
        else view.following = true;
      }
    }, { passive: true });
    output.addEventListener('keydown', e => {
      if (output.scrollTop > 0 && ['ArrowUp', 'PageUp', 'Home'].includes(e.key)) { view.following = false; view.selectionPaused = false; }
      else if (!e.shiftKey && ['ArrowDown', 'PageDown', 'End'].includes(e.key) &&
        runOutputAtBottom(output)) {
        if (runOutputSelected(output)) view.selectionPaused = true;
        else view.following = true;
      }
    });
  } else if (view.element !== output) {
    output.replaceWith(view.element);
    output = view.element;
  }
  output.hidden = false;
  const before = output.textContent, text = r.output;
  const { oldPrefix, newPrefix, removed, retentionMoved } = runOutputRetention(before, text, view.offset, r.outputOffset);
  if (crossRunOutputSelection()?.output === output && (retentionMoved || !text.startsWith(before))) {
    S.rowsPending = true; renderStatus(); return;
  }
  const selectionPaused = view.selectionPaused, topBeforeMutation = output.scrollTop;
  if (retentionMoved && !view.following) view.selectionPaused = false;
  if (text !== before || retentionMoved) {
    const node = output.firstChild;
    if (node?.nodeType === 3 && !retentionMoved && text.startsWith(before)) node.appendData(text.slice(before.length));
    else if (node?.nodeType === 3) {
      const retained = before.slice(oldPrefix + removed);
      if (newPrefix && Number.isSafeInteger(removed) && removed >= 0 && removed < before.length - oldPrefix &&
        text.slice(newPrefix).startsWith(retained)) {
        const anchor = view.following ? null : view.anchor;
        const selection = window.getSelection(), cut = oldPrefix + removed;
        const selected = selection?.isCollapsed === false && selection.anchorNode === node && selection.focusNode === node &&
          Math.min(selection.anchorOffset, selection.focusOffset) >= cut
          ? { anchor: selection.anchorOffset - cut + newPrefix, focus: selection.focusOffset - cut + newPrefix } : null;
        node.deleteData(0, oldPrefix + removed);
        node.insertData(0, text.slice(0, newPrefix));
        node.appendData(text.slice(newPrefix + retained.length));
        if (selected) selection.setBaseAndExtent(node, selected.anchor, node, selected.focus);
        if (!view.following) {
          if (anchor && anchor.offset >= oldPrefix + removed) {
            const range = document.createRange(), offset = anchor.offset - oldPrefix - removed + newPrefix;
            range.setStart(node, offset); range.setEnd(node, offset + 1);
            view.top = output.scrollTop + range.getBoundingClientRect().top - output.getBoundingClientRect().top - anchor.top;
            view.selectionPaused = selectionPaused && !!selected && output.scrollTop === topBeforeMutation;
          } else view.top = 0;
        }
      } else {
        node.replaceData(0, node.length, text);
        if (newPrefix && !view.following) view.top = 0;
      }
    } else output.textContent = text;
  }
  view.offset = r.outputOffset;
  if (output.clientHeight) {
    output.scrollTop = view.following ? output.scrollHeight : view.top;
    output.scrollLeft = view.left;
    // Programmatic restoration/clamping must not turn a paused reader into a follower.
    view.top = output.scrollTop;
    view.left = output.scrollLeft;
    if (view.selectionPaused && !runOutputAtBottom(output)) view.selectionPaused = false;
    rememberRunOutputAnchor(view);
  }
}

const runCardTitle = (earlier, run) => `${earlier ? 'Earlier run' : 'Fix PR'} · ${run?.backend === 'wsl' || !run && S.settings?.fixBackend === 'wsl' ? 'Copilot in WSL' : run?.backend === 'legacy-wsl' || run?.backend !== 'windows-native' && run?.distro ? 'Legacy Copilot in WSL' : 'Copilot on Windows'}`;
// One per PR (beside Fix PR, or atop shelf/ignored-draft runs) so card promotion never hides or duplicates it.
let runInfoId = 0;
const runScopeNotice = () => {
  const id = `run-info-${++runInfoId}`;
  return `<div class="small run-scope">
    <p class="run-summary">Fix PR asks Copilot to try to fix merge conflicts, failing checks and review feedback in a separate working copy. Windows is the default; WSL runs only if selected in Settings. New runs are asked to validate, commit and push fixes back to this PR. Follow progress here, then review the changes and results.
      <button type="button" class="mini run-info-button" aria-label="Fix PR permissions and limitations" aria-expanded="false" aria-controls="${id}" aria-describedby="${id}">i</button></p>
    <p id="${id}" class="run-info-details" hidden>Fix PR starts in a separate PR worktree with --yolo: tools, paths and URLs are allowed without confirmation for this run, not globally. This is not a filesystem sandbox. New runs are told to commit validated fixes and ordinary-push them only to this PR's captured source branch; that limit is a prompt instruction, not enforced. Merge/PR-update command denies and the prompt's no-approval/policy-bypass boundaries remain. Legacy WSL runs keep their recorded environment and original permission/publish scope, never silently restart on Windows. Sign-in is required beforehand; this panel is noninteractive. Exit 0 does not verify the PR fix or publication. Inspect the run's output and verify the results.</p>
  </div>`;
};
function setRunInfo(scope, open) {
  $('.run-info-button', scope).setAttribute('aria-expanded', String(open));
  $('.run-info-details', scope).hidden = !open;
}
function renderRunCard(key, r = runForPr(key), earlier = false) {
  return `<div class="card copilot-run${earlier ? ' run-earlier' : ''}" data-run-key="${esc(key)}" data-run-id="${esc(r?.runId || '')}">
    <h3><span class="grow">${runCardTitle(earlier, r)}</span>
      <button class="mini" data-act="cancelRun" data-run-id="${esc(r?.runId || '')}" ${runCancelable(r) ? '' : 'hidden'}>${svg('x')} Cancel run</button></h3>
    <div class="body">
      <p class="run-status" role="status">${esc(r ? runSummary(r) : 'Not started. Choose Fix PR to begin, or Copy prompt to review the instructions first.')}</p>
      <details class="run-history" data-run-id="${esc(r?.runId || '')}" ${!earlier || S.runHistoryOpen.has(r?.runId) ? 'open' : ''}>
        <summary>Show earlier run output</summary>
        <p class="run-worktree small">${esc(runWorktreeText(r))}</p>
        <pre class="run-output" tabindex="0" aria-label="Copilot stdout and stderr" ${r || isBusy(key, 'triage') ? '' : 'hidden'}>${esc(r?.output || '')}</pre>
      </details>
    </div></div>`;
}

function earlierRunCards(key) {
  const latest = runForPr(key);
  const earlier = runsForPr(key).filter(r => r.runId !== latest?.runId);
  return earlier.map(r => renderRunCard(key, r, true)).join('');
}

// Runs stay inside their PR row. Departed PRs and ignored drafts keep collapsible
// run entries here (unless hidden), only in the unfiltered view.
function renderRunShelf(whenIdle = false) {
  if (!$('#runShelf')) return;
  if (whenIdle && (rowsInUse() || crossRunOutputSelection()?.card.closest('#runShelf') ||
    selectionBlocksMove([...document.querySelectorAll('#runShelf .copilot-run')]))) { S.rowsPending = true; renderStatus(); return; }
  const selection = captureRunOutputs();
  renderRunShelfContent();
  restoreRunOutputs(selection);
}

function renderRunShelfContent() {
  const shelf = $('#runShelf');
  if (!shelf) return;
  const focused = shelf.contains?.(document.activeElement) ? expansionControl(document.activeElement) : null;
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
      <div class="run-heading" data-act="toggle" role="button" tabindex="0" aria-expanded="${open}">${svg('chev').replace('<svg', '<svg class="chev"')}
        <div class="prmain"><div class="title">${esc(r.title)}</div><div class="meta small"><span class="id">!${esc(r.id)}</span><span class="sep"> · </span><span>${esc(r.repo)}</span><span class="sep"> · </span><span>${findPr(key) && ignoredDraft(findPr(key)) ? 'Ignored draft' : 'PR no longer in the active list'} · ${esc(runSummary(r))}</span></div></div></div>
      ${open ? `<div class="detail"><div class="full stack">${runScopeNotice()}${renderRunCard(key, r)}${earlierRunCards(key)}</div></div>` : ''}
    </div>`;
  }).join('');
  restoreExpansionFocus(focused);
}

function updateCopilotRun(r) {
  if (!r || typeof r.key !== 'string' || typeof r.runId !== 'string' || typeof r.status !== 'string' || typeof r.output !== 'string')
    throw new Error('Copilot returned an invalid run state.');
  const old = S.runs.get(r.runId);
  if (Number.isSafeInteger(old?.revision) && Number.isSafeInteger(r.revision) && r.revision < old.revision) return;
  if (old?.runId === r.runId && ((old.status !== 'preparing' && r.status === 'preparing') ||
    (['completed', 'failed', 'cancelled'].includes(old.status) && !old.mayBeRunning && runActive(r)))) return;
  if (old?.runId === r.runId && old.updatedAt && r.updatedAt && Date.parse(r.updatedAt) < Date.parse(old.updatedAt)) return;
  if (Number.isFinite(old?.outputOffset) && Number.isFinite(r.outputOffset) && r.outputOffset < old.outputOffset) return;
  S.runs.set(r.runId, r);
  const previousSelection = old ? null : captureRunOutputs();
  syncCopilotButton(r.key);
  // Stream only into this PR's panels; preserve controls while rows are in use.
  const latestId = runForPr(r.key)?.runId, latest = latestId === r.runId;
  const runCards = [...document.querySelectorAll('.copilot-run')].filter(el => el.dataset.runKey === r.key);
  const main = runCards.find(el => !el.classList.contains('run-earlier'));
  const promoted = runCards.find(el => el.dataset.runId === latestId);
  if (latest && !promoted && main?.dataset.runId) {
    renderRows(true);
    return;
  }
  const crossSelection = crossRunOutputSelection();
  const deferMain = !!main?.dataset.runId && main.dataset.runId !== latestId &&
    (rowsInUse() || crossSelection?.card.dataset.runKey === r.key);
  if (deferMain) { S.rowsPending = true; renderStatus(); }
  // A selection reaching into a moving card leaves the swap to the queued relocation below.
  else if (main && promoted && main !== promoted && !selectionBlocksMove(runCards)) {
    const selection = captureRunOutputs();
    reorderRunCards(r.key, runCards);
    for (const card of runCards) {
      const view = S.runOutputs.get(card.dataset.runId);
      if (view) {
        view.element.scrollTop = view.top;
        view.element.scrollLeft = view.left;
        view.top = view.element.scrollTop;
      }
    }
    restoreRunOutputSelection(selection);
  }
  const cards = runCards.filter(el =>
    (el.dataset.runId === r.runId || (latest && !promoted && !deferMain && !el.classList.contains('run-earlier'))));
  if (!cards.length) {
    renderRunShelf(true);
    if (!old) { renderRows(true); restoreRunOutputs(previousSelection); }
    return;
  }
  for (const card of cards) {
    const previousView = S.runOutputs.get(card.dataset.runId);
    if (previousView) rememberRunOutput(previousView);
    card.dataset.runId = r.runId;
    for (const [element, text] of [[$('.run-status', card), runSummary(r)], [$('.run-worktree', card), runWorktreeText(r)]]) {
      if (element.textContent === text) continue;
      if (crossSelection?.card === card && crossSelection.range.intersectsNode(element)) {
        S.rowsPending = true; renderStatus();
      } else element.textContent = text;
    }
    syncRunOutput(card);
    const cancel = $('[data-act="cancelRun"]', card);
    const hidden = !runCancelable(r);
    if (cancel.hidden !== hidden && crossSelection?.card === card && crossSelection.range.intersectsNode(cancel)) {
      S.rowsPending = true; renderStatus();
    } else cancel.hidden = hidden;
    cancel.disabled = isBusy(r.key, 'cancelRun');
    cancel.dataset.runId = r.runId;
  }
  if (!old) { renderRunShelf(true); renderRows(true); restoreRunOutputs(previousSelection); return; }
  relocateRunGroup(r.key);
}

// Make the preferred run the main card and keep the rest in chronological order (same nodes).
function reorderRunCards(key, runCards) {
  const latestId = runForPr(key)?.runId;
  const main = runCards.find(el => !el.classList.contains('run-earlier'));
  const promoted = runCards.find(el => el.dataset.runId === latestId);
  if (!main || !promoted || main === promoted) return;
  main.classList.add('run-earlier'); $('h3 .grow', main).textContent = runCardTitle(true, S.runs.get(main.dataset.runId));
  promoted.classList.remove('run-earlier'); $('h3 .grow', promoted).textContent = runCardTitle(false, S.runs.get(promoted.dataset.runId));
  if (runOutputSelected($('.run-output', main)) || main.contains(document.activeElement) ||
      S.runOutputs.get(main.dataset.runId)?.following === false)
    S.runHistoryOpen.add(main.dataset.runId);
  $('.run-history', main).open = S.runHistoryOpen.has(main.dataset.runId);
  $('.run-history', promoted).open = true;
  main.before(promoted);
  let previous = promoted;
  for (const run of runsForPr(key).filter(run => run.runId !== latestId)) {
    const card = runCards.find(card => card.dataset.runId === run.runId);
    if (!card) continue;
    if (previous.nextElementSibling !== card) previous.after(card);
    previous = card;
  }
}

// A non-output text selection touching nodes that would move (detaching its endpoints) holds the move.
function selectionBlocksMove(elements) {
  const selection = window.getSelection(), range = selection?.isCollapsed === false ? selection.getRangeAt(0) : null;
  if (!range) return false;
  const start = range.startContainer, output = (start?.nodeType === 1 ? start : start?.parentElement)?.closest('.run-output');
  if (output?.contains(range.endContainer)) return false;
  return elements.some(el => range.intersectsNode(el));
}

// The Fix home never moves; only a newly preferred run is promoted within it.
function relocateRunGroup(key) {
  const detail = [...document.querySelectorAll('#rows > .pr')].find(el => el.dataset.key === key)?.querySelector(':scope > .detail[data-run-lead]');
  if (!detail) return relocateShelfRuns(key);
  const cards = detail ? [...detail.querySelectorAll('.copilot-run')] : [];
  const latestId = runForPr(key)?.runId;
  const reorder = cards.some(el => el.dataset.runId === latestId) && cards.find(el => !el.classList.contains('run-earlier'))?.dataset.runId !== latestId;
  if (!reorder) { S.runRelocations.delete(key); return; }
  if (!cards.length) { S.runRelocations.delete(key); renderRows(true); return; }
  if (rowsInUse() || crossRunOutputSelection() || selectionBlocksMove(cards)) {
    S.runRelocations.add(key); renderStatus(); return;
  }
  S.runRelocations.delete(key);
  const outputSelection = captureRunOutputs();
  reorderRunCards(key, cards);
  for (const card of cards) {
    const view = S.runOutputs.get(card.dataset.runId);
    if (view) { view.element.scrollTop = view.top; view.element.scrollLeft = view.left; view.top = view.element.scrollTop; }
  }
  restoreRunOutputSelection(outputSelection);
  renderStatus();
}

// Shelf cards have no leading placement; only a held main-run promotion is queued and replayed in place.
function relocateShelfRuns(key) {
  const cards = [...(document.querySelector('#runShelf')?.querySelectorAll('.copilot-run') || [])].filter(el => el.dataset.runKey === key);
  const latestId = runForPr(key)?.runId;
  if (!cards.some(el => el.dataset.runId === latestId) || cards.find(el => !el.classList.contains('run-earlier'))?.dataset.runId === latestId) {
    S.runRelocations.delete(key); return;
  }
  if (rowsInUse() || crossRunOutputSelection() || selectionBlocksMove(cards)) { S.runRelocations.add(key); renderStatus(); return; }
  S.runRelocations.delete(key);
  const outputSelection = captureRunOutputs();
  reorderRunCards(key, cards);
  for (const card of cards) {
    const view = S.runOutputs.get(card.dataset.runId);
    if (view) { view.element.scrollTop = view.top; view.element.scrollLeft = view.left; view.top = view.element.scrollTop; }
  }
  restoreRunOutputSelection(outputSelection);
  renderStatus();
}

const humanText = a => ({ reviews: 'Human reviewer approval needed (+1)', pop: 'Proof of Presence — tap your security key', autocomplete: 'Auto-complete not set', publish: 'Draft — publish when ready' }[a] || a);

function syncCopilotButton(key) {
  for (const button of document.querySelectorAll('[data-act="triage"]')) {
    if (button.closest('.pr')?.dataset.key !== key) continue;
    const active = runActive(runForPr(key)), cancelable = runCancelable(runForPr(key)), busy = isBusy(key, 'triage');
    const expanded = button.classList.contains('mini');
    button.classList.toggle('running', active);
    button.classList.toggle('busy', busy);
    button.disabled = busy;
    button.innerHTML = svg(active || busy ? 'refresh' : 'spark') + (expanded ? ` ${cancelable ? 'Show run' : 'Fix PR'}` : '');
    button.title = cancelable ? runForPr(key)?.status === 'needs-attention'
      ? 'Copilot task may still be active; show status and cancellation controls'
      : 'Show the active inline Copilot run for this PR' : 'Fix PR with Copilot in an isolated Windows worktree; output stays inline';
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
  const inspection = clickable && !['pop', 'autocomplete'].includes(g);
  const tip = [gate.label, gate.detail].filter(Boolean).join(' — ');
  return `<div class="gate"><span class="pill ${gate.state} ${clickable ? 'click' : ''}" ${clickable ? `data-act="gate" data-gate="${g}"` : ''} ${inspection ? `role="button" tabindex="0" aria-expanded="${S.expanded.has(p.key)}"` : ''} title="${esc(tip)}"><i></i><span>${esc(gate.label)}</span></span></div>`;
}

function isBusy(key, a) { return S.busy.has(`${key}:${a}`); }
const autoCompleteBusy = key => isBusy(key, 'autocomplete') || isBusy(key, 'cancelAuto');

function autoCompleteTitle(p) {
  const pending = isBusy(p.key, 'cancelAuto') ? 'Cancelling auto-complete; waiting for ADO confirmation'
    : isBusy(p.key, 'autocomplete') ? 'Setting auto-complete; waiting for ADO confirmation' : '';
  const options = S.settings?.autoComplete;
  const effects = [options?.mergeStrategy || 'squash', options?.deleteSourceBranch && 'delete branch',
    options?.transitionWorkItems && 'complete work items'].filter(Boolean).join(', ');
  const state = p.autoCompleteBy ? `Auto-complete confirmed by ADO, set by ${p.autoCompleteBy} — click to cancel`
    : `Set auto-complete (${effects})`;
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

function reviewVoteFor(p) {
  const state = p.reviewVote;
  if (!state) return null;
  const queued = S.pendingSnapshot?.prs.find(pr => pr.key === p.key);
  if (S.pendingSnapshot && (!queued || queued.error || queued.isActive === false || queued.isHidden ||
      JSON.stringify(queued.reviewVote?.target) !== JSON.stringify(state.target)))
    return { ...state, vote: queued?.reviewVote?.vote ?? null, canApprove: false,
      detail: 'The PR revision or your review state changed. Finish this interaction, then review the refreshed PR before approving.' };
  if (p.isActive === false || p.isHidden || p.error ||
      hiddenSet().has(p.key.toLowerCase()))
    return { ...state, canApprove: false, detail: 'This PR is inactive, hidden or could not be refreshed.' };
  return state;
}

function reviewVoteTitle(p) {
  const state = reviewVoteFor(p);
  return isBusy(p.key, 'approve') ? 'Submitting your ordinary ADO reviewer vote...' : state?.canApprove
    ? `Approve as ${state.reviewerName}. ADO checks your permissions; other review requirements may remain.`
    : `${state?.vote === 10 ? `Approved as ${state.reviewerName}.` : 'Approve unavailable.'} ${state?.detail || 'Refresh to identify your ADO reviewer and the PR revision.'}`;
}

function reviewVoteButton(p, mini = false) {
  const state = reviewVoteFor(p), busy = isBusy(p.key, 'approve'), title = reviewVoteTitle(p);
  return `<button class="${mini ? 'mini' : 'act reviewer-vote'} ${busy ? 'busy' : ''} ${state?.vote === 10 ? 'done' : ''}" data-act="approve"
    title="${esc(title)}" aria-label="${esc(title)}" ${busy || !state?.canApprove ? 'disabled' : ''} ${busy ? 'aria-busy="true"' : ''}>${svg(busy ? 'refresh' : 'check')}<span>${state?.vote === 10 ? 'Approved' : 'Approve'}</span></button>`;
}

function reviewVoteDescription(p) {
  const state = reviewVoteFor(p);
  const labels = { 10: 'Approved', 5: 'Approved with suggestions', 0: 'No vote', '-5': 'Waiting for author', '-10': 'Rejected' };
  return state ? `<b>${esc(state.reviewerName)}</b> &mdash; your recorded vote: <b>${esc(labels[state.vote] || 'Unknown')}</b>.
    <p class="small">${esc(state.detail)}</p>` : 'Refresh to identify your ADO reviewer and the PR revision.';
}

function syncReviewVoteControls(key) {
  const p = findPr(key);
  if (!p) return;
  for (const button of document.querySelectorAll('[data-act="approve"]')) {
    if (button.closest('.pr')?.dataset.key !== key) continue;
    const state = reviewVoteFor(p), busy = isBusy(key, 'approve');
    button.disabled = busy || !state?.canApprove;
    button.title = reviewVoteTitle(p);
    button.setAttribute('aria-label', button.title);
    button.classList.toggle('busy', busy);
    button.classList.toggle('done', state?.vote === 10);
    if (busy) button.setAttribute('aria-busy', 'true');
    else button.removeAttribute('aria-busy');
    button.innerHTML = svg(busy ? 'refresh' : 'check') + `<span>${state?.vote === 10 ? 'Approved' : 'Approve'}</span>`;
  }
  for (const node of document.querySelectorAll('[data-review-vote-state], [data-review-vote-outcome]')) {
    if (node.closest('.pr')?.dataset.key !== key) continue;
    if (node.hasAttribute('data-review-vote-state')) node.innerHTML = reviewVoteDescription(p);
    else {
      const outcome = S.reviewVotes.get(key);
      node.textContent = outcome?.message || '';
      node.hidden = !outcome;
      node.classList.toggle('error', outcome?.kind === 'error');
    }
  }
}

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
  else if (p.reviewVote) out.approval = reviewVoteButton(p);
  else if (needsReview) out.approval = b('manualReview', 'approval', '', `${hot} review-needed`, 'Human reviewer approval needed. Open this PR to arrange review.');
  if (human.includes('publish')) out.publish = b('publish', 'send', 'Publish', hot, 'Publish draft (mark ready for review)');
  if (human.includes('pop')) out.pop = b('pop', 'key', 'PoP', hot, 'Proof of Presence — opens the inline Edge pane for your security key');
  else if (p.gates?.pop?.state === 'ok') out.pop = b('pop', 'key', '', 'done', 'Proof of Presence satisfied', 'disabled');
  if (p.autoCompleteBy) out.auto = b('cancelAuto', 'check', 'Auto', 'auto-set', autoCompleteTitle(p), 'aria-pressed="true"');
  else out.auto = b('autocomplete', 'zap', 'Auto', human.includes('autocomplete') ? hot : '', autoCompleteTitle(p), 'aria-pressed="false"');
  out.fix = b('triage', 'spark', '', 'copilot', runCancelable(runForPr(p.key)) ? 'Show the inline Copilot run for this PR' : 'Fix PR with Copilot in an isolated Windows worktree; output stays inline');
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
    <div class="id-cue" aria-hidden="true"><span><b>!${p.id} · </b><i>${esc(p.repo)}</i></span></div>
    <div class="row" data-act="toggle">
      <div class="c-pr" data-act="toggle" role="button" tabindex="0" aria-expanded="${open}">${svg('chev').replace('<svg', '<svg class="chev"')}
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
    ${runsForPr(p.key).length ? runScopeNotice() + renderRunCard(p.key) + earlierRunCards(p.key) : ''}
    ${S.videos.has(p.key) ? renderVideo(p.key) : ''}
    </div></div>`;
  const left = [];
  const right = [];
  const full = [];
  const runGroup = renderRunCard(p.key) + earlierRunCards(p.key);

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

  const loadError = p.error ? `<div class="card failed"><h3>Load error</h3><div class="body">${esc(p.error)}</div></div>` : '';

  // Failed gates with log tails
  for (const fb of (p.failedBuilds || []).slice().sort((a, b) =>
    Number(b.isBlocking === true) - Number(a.isBlocking === true) || Number(a.isBlocking === false) - Number(b.isBlocking === false))) {
    const optional = fb.isBlocking === false;
    const tasks = (fb.tasks || []).map((t, i) => {
      const lk = logKey(p, fb.buildId, t.logId);
      const log = t.logId != null ? S.logs.get(lk) : undefined;
      if (!optional && t.logId != null && i === 0 && log === undefined) queueMicrotask(() => loadLog(p.key, fb.buildId, t.logId));
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
            : log === undefined || log instanceof Error ? `<button class="mini" data-act="loadLog" data-build="${fb.buildId}" data-logid="${t.logId}" ${S.logLoads.has(lk) ? 'disabled aria-busy="true"' : ''}>${log instanceof Error ? 'Retry log tail' : 'Load log tail'}</button>` : '') : ''}
        </div>${issues}${logHtml}</div>`;
    }).join('') || '<div class="body small">No failed task records found in the timeline.</div>';
    const card = `<div class="card ${optional ? 'optional-build' : 'failed'}"><h3>${optional ? 'Optional failed build (not a merge blocker)' : '✕ Failed gate'} · ${esc(fb.name)} <span class="small">build ${fb.buildId}${fb.isBlocking == null ? ' · requiredness unknown' : ''}</span><span class="grow"></span>
      ${fb.evaluationId ? `<button class="mini" data-act="requeue" data-eval="${esc(fb.evaluationId)}">${svg('play')} Re-queue</button>` : ''}
      <button class="mini" data-act="url" data-url="${esc(fb.url)}">${svg('ext')} Open logs</button></h3>${tasks}</div>`;
    if (optional) right.push(card);
    else full.push(card);
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
    ...(p.humanActions || []).map((a) => ['human', humanText(a)]),
    ...(p.waiting || []).map((x) => ['pending', x]),
  ];
  const fixActive = runActive(runForPr(p.key)), fixBusy = isBusy(p.key, 'triage');
  const status = `<div class="card pr-status"><h3>Status</h3>
    <div class="body"><ul class="list blockers">${items.length ? items.map(([c, t]) => `<li><span class="dot ${c}"></span><span class="k">${esc(t)}</span></li>`).join('') : '<li><span class="dot ok"></span><span class="k">All gates satisfied</span></li>'}</ul></div></div>`;
  const fixHome = `<section class="full stack fix-home" aria-label="Fix PR">
    <div class="fix-toolbar"><strong>Fix with Copilot</strong>
      <button class="mini ${fixActive ? 'running' : ''} ${fixBusy ? 'busy' : ''}" data-act="triage" ${fixBusy ? 'disabled' : ''} ${fixActive || fixBusy ? 'aria-busy="true"' : ''}>${svg(fixActive || fixBusy ? 'refresh' : 'spark')} ${runCancelable(runForPr(p.key)) ? 'Show run' : 'Fix PR'}</button>
      <button class="mini" data-act="copyPrompt">${svg('copy')} Copy prompt</button></div>
    ${runScopeNotice()}${runGroup}</section>`;

  const ac = autoCompleteDescription(p);
  right.push(`<div class="card"><h3>Merge <span class="grow"></span>
      <button class="mini" data-act="restartMerge" title="Ask ADO to recompute the merge against the latest target">${svg('merge')} Re-check merge</button>
      <button class="mini ${p.autoCompleteBy ? 'auto-set' : ''}" data-act="${p.autoCompleteBy ? 'cancelAuto' : 'autocomplete'}" title="${esc(autoCompleteTitle(p))}" aria-label="${esc(autoCompleteTitle(p))}" ${autoCompleteBusy(p.key) ? 'disabled aria-busy="true"' : ''}>${svg(autoCompleteBusy(p.key) ? 'refresh' : p.autoCompleteBy ? 'x' : 'zap')} ${p.autoCompleteBy ? 'Cancel auto-complete' : 'Set auto-complete'}</button></h3>
    <div class="body"><div class="error" role="status" data-auto-complete-error ${S.autoCompleteErrors.has(p.key) ? '' : 'hidden'}>${esc(S.autoCompleteErrors.get(p.key) || '')}</div><dl class="kv">
      <dt>Merge status</dt><dd>${esc(p.mergeStatus || '—')}${p.mergeStale ? ' <span class="small">(preview target is stale or missing)</span>' : ''}${p.mergeFailure ? `<div class="small">${esc(p.mergeFailure)}</div>` : ''}</dd>
      <dt>Merge re-check</dt><dd>${p.mergeRecheckRequestedAt ? `Triggered ${esc(new Date(p.mergeRecheckRequestedAt).toLocaleString())}; merge status comes from the PR read` : 'No restart requested this session'}
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

  if (p.reviewVote) {
    const outcome = S.reviewVotes.get(p.key);
    right.push(`<div class="card"><h3><span class="grow">Your ADO review</span>${reviewVoteButton(p, true)}
      <button class="mini" data-act="open" title="Open in Azure DevOps">${svg('ext')} Open in ADO</button></h3><div class="body">
      <div data-review-vote-state>${reviewVoteDescription(p)}</div>
      <p data-review-vote-outcome role="status" class="${outcome?.kind === 'error' ? 'error' : ''}" ${outcome ? '' : 'hidden'}>${esc(outcome?.message || '')}</p>
      </div></div>`);
  }
  const voteOrder = [10, 5, -10, -5, 0];
  const rank = vote => { const index = voteOrder.indexOf(vote); return index < 0 ? voteOrder.length : index; };
  const revs = (p.reviewers || []).slice().sort((a, b) => (rank(a.vote) - rank(b.vote)) || (b.isRequired - a.isRequired));
  if (revs.length) {
    const vl = { 10: 'Approved', 5: 'Approved w/ suggestions', 0: 'No vote', '-5': 'Waiting for author', '-10': 'Rejected' };
    const outstanding = [[-10, 'rejected'], [-5, 'waiting for author'], [0, 'no vote']]
      .map(([vote, label]) => { const count = revs.filter(r => r.vote === vote).length; return count ? `${count} ${label}` : ''; }).filter(Boolean);
    right.push(`<div class="card reviewers"><h3>Reviewers <span class="cnt">${revs.length}</span></h3><div class="body">
      ${outstanding.length ? `<p class="reviewer-summary">${esc(outstanding.join(' · '))}</p>` : ''}
      <ul class="list">${revs.map((r) => `
      <li>${avatar(r.name)}<span class="k">${esc(r.name)}${r.id?.toLowerCase() === p.reviewVote?.reviewerId?.toLowerCase() ? ' (you)' : ''} ${r.isRequired ? '<span class="badge req">required</span>' : ''}${r.isContainer ? ' <span class="badge">group</span>' : ''}</span>
      <span class="vote v${r.vote}">${vl[r.vote] ?? r.vote}</span></li>`).join('')}</ul></div></div>`);
  }

  return `<div class="detail" data-run-lead="1"><div class="full stack">${loadError}${status}
    <section class="stack ai-assistance" aria-label="PR assistance">${renderExplanation(p.key)}</section></div>
    ${fixHome}${full.length ? `<div class="full stack">${full.join('')}</div>` : ''}<div class="stack">${left.join('')}</div><div class="stack">${right.join('')}</div></div>`;
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
  if (S.logLoads.has(lk)) return;
  S.logLoads.add(lk);
  if (S.logs.get(lk) instanceof Error) renderRows(true);
  else S.logs.set(lk, 'loading');
  try {
    const text = await rpc('logTail', { key, buildId, logId, lines: 200 });
    S.logs.set(lk, text || '(empty log)');
  } catch (e) {
    S.logs.set(lk, e);
  } finally {
    S.logLoads.delete(lk);
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
  const p = findShown(row.closest('.pr').dataset.key);
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
  else if (act === 'approve') syncReviewVoteControls(key);
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
    if (act === 'approve') syncReviewVoteControls(key);
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
  const p = key ? (['open', 'hide'].includes(act) ? findShown(key) : findPr(key)) : null;
  if (p && ignoredDraft(p) && !['toggle', 'open', 'hide', 'cancelRun', 'cancelExplanation', 'cancelVideo', 'copyVideoPath'].includes(act))
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
    case 'approve': {
      const state = p && reviewVoteFor(p);
      if (!state?.canApprove || !state.target || isBusy(key, act)) return;
      const expected = { ...state.target };
      S.reviewVotes.delete(key);
      await runAction(key, act, async () => {
        try {
          const result = await rpc('approve', { key, expected });
          const updated = result?.pr, target = updated?.reviewVote?.target;
          if (result?.confirmed !== true || typeof result.message !== 'string' || updated?.key !== key ||
              updated.id !== p.id || updated.repoId !== p.repoId || updated.org !== p.org || updated.project !== p.project ||
              updated.reviewVote?.vote !== 10 || target?.vote !== 10 ||
              Object.keys(expected).some(k => k !== 'vote' && expected[k] !== target[k]) ||
              !Array.isArray(updated.reviewers) || !Array.isArray(updated.humanActions) || !updated.gates)
            throw new Error('ADO approval read-back was not confirmed. ADO may have recorded your vote; refresh and check the PR before retrying.');
          S.snap = { ...S.snap, prs: S.snap.prs.map(pr => pr.key === key ? updated : pr) };
          if (S.pendingSnapshot) S.pendingSnapshot = { ...S.pendingSnapshot, prs: S.pendingSnapshot.prs.map(pr =>
            pr.key === key && JSON.stringify(pr.reviewVote?.target) === JSON.stringify(expected) ? updated : pr) };
          S.reviewVotes.set(key, { kind: 'confirmed', message: result.message });
          toast(result.message);
        } catch (e) {
          S.reviewVotes.set(key, { kind: 'error', message: e.message || String(e) });
          throw e;
        }
      });
      break;
    }
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
      // Launch only for an active PR; Cancel is a separate action and stays available for owned runs.
      if (!p) return;
      S.expanded.add(key);
      if (runCancelable(runForPr(key))) {
        renderRows();
        // Reveal the exact live/uncertain run (status, Cancel, output) past any tall failed-gate logs.
        const card = [...document.querySelectorAll('#rows .copilot-run:not(.run-earlier)')].find(c => c.dataset.runKey === key);
        if (card) {
          // Clear the sticky column header so the run status and Cancel stay visible and clickable.
          card.style.scrollMarginTop = `${($('.thead')?.getBoundingClientRect().height || 0) + 8}px`;
          card.scrollIntoView({ block: 'start' }); $('.run-output', card)?.focus({ preventScroll: true });
        }
        break;
      }
      await runAction(key, 'triage', async () => {
        const r = await rpc('startCopilot', { key });
        updateCopilotRun(r);
      });
      break;
    case 'askPr':
    case 'lensPr':
      openInvestigation(key, act === 'lensPr' ? 'lens' : 'question');
      break;
    case 'explainPr':
      if (!canExplain(key) || isBusy(key, act) || explanationActive(S.explanations.get(key))) return;
      S.explanationPrs.set(key, { id: p.id, repo: p.repo, title: p.title });
      S.busy.add(`${key}:${act}`);
      renderInvestigation(key);
      for (const card of document.querySelectorAll('.explanation')) {
        if (card.dataset.explainKey === key && $('[data-act="explainPr"]', card)) $('[data-act="explainPr"]', card).disabled = true;
      }
      try { updateExplanation(await rpc('explainPr', { key })); }
      catch (e) { toast(e.message || String(e), 'error'); }
      finally {
        S.busy.delete(`${key}:${act}`);
        renderInvestigation(key);
        const state = S.explanations.get(key);
        for (const card of document.querySelectorAll('.explanation')) {
          if (card.dataset.explainKey === key && $('[data-act="explainPr"]', card)) $('[data-act="explainPr"]', card).disabled = explanationActive(state) || !canExplain(key);
        }
      }
      break;
    case 'cancelExplanation': {
      const r = S.explanations.get(key);
      if (!explanationActive(r) || r.status === 'cancelling' || r.requestId !== el.dataset.requestId || isBusy(key, act)) return;
      S.busy.add(`${key}:${act}`);
      try { updateExplanation(await rpc('cancelExplanation', { key, requestId: r.requestId })); }
      catch (e) { toast(e.message || String(e), 'error'); }
      finally { S.busy.delete(`${key}:${act}`); }
      break;
    }
    case 'tiktokPr':
      if (!canVideo(key) || isBusy(key, act) || videoActive(S.videos.get(key))) return;
      if (!S.settings.videoSpeech) { await openSpeechSetup(key); return; }
      S.busy.add(`${key}:${act}`);
      for (const card of document.querySelectorAll('.pr-video')) {
        if (card.dataset.videoKey === key) $('[data-act="tiktokPr"]', card).disabled = true;
      }
      try { updateVideo(await rpc('tiktokPr', { key })); }
      catch (e) { toast(e.message || String(e), 'error'); }
      finally {
        S.busy.delete(`${key}:${act}`);
        for (const card of document.querySelectorAll('.pr-video')) {
          if (card.dataset.videoKey === key) $('[data-act="tiktokPr"]', card).disabled = videoActive(S.videos.get(key)) || !canVideo(key);
        }
      }
      break;
    case 'cancelVideo':
      if (isBusy(key, act)) return;
      S.busy.add(`${key}:${act}`);
      try { updateVideo(await rpc('cancelVideo', { key, requestId: el.dataset.requestId })); }
      catch (e) { toast(e.message || String(e), 'error'); }
      finally { S.busy.delete(`${key}:${act}`); }
      break;
    case 'copyVideoPath':
      if (S.videos.get(key)?.status === 'completed') {
        await rpc('copy', { text: S.videos.get(key).path });
        toast('MP4 path copied');
      }
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
async function openSpeechSetup(key = null) {
  if ($('#speechDialog').open) return;
  const epoch = ++S.speechEpoch;
  S.speechSetup = { key, busy: true, progress: 'Checking video dependencies and Azure CLI...', subscriptions: [], subscription: '', resources: null, resource: '', voice: '', error: '',
    exactId: '', exactVoice: 'en-US-Ava:DragonHDLatestNeural', exactOpen: false };
  renderSpeechSetup(); $('#speechDialog').showModal();
  try {
    const subscriptions = await rpc('listSpeechSubscriptions', { epoch });
    if (epoch !== S.speechEpoch) return;
    if (!Array.isArray(subscriptions)) throw new Error('Azure CLI returned no subscription list.');
    S.speechSetup.subscriptions = subscriptions;
    if (!subscriptions.length) S.speechSetup.error = 'No enabled public Azure subscriptions found. Run az login in the correct tenant.';
  } catch (e) { if (epoch === S.speechEpoch) S.speechSetup.error = e.message || String(e); }
  finally { if (epoch === S.speechEpoch) { S.speechSetup.busy = false; renderSpeechSetup(); } }
}

function renderSpeechSetup() {
  const s = S.speechSetup;
  if (!s) return;
  const selected = (s.resources || []).find(r => r.resourceId === s.resource);
  $('#speechBody').innerHTML = `
    <p>Use your Azure CLI sign-in to find a Speech-capable Cognitive Services resource.
      No API keys, model deployments or Azure changes are needed. Discovery needs resource read access;
      narration needs Cognitive Services Speech User RBAC on the account, not just its Foundry project.</p>
    <p class="help">Windows rendering, the bundled Copilot runtime and real-cat assets are checked once and reused.
      No FFmpeg installation or voice-model download is needed. Missing components show repair and retry instructions.</p>
    <p class="help">Narration text will be sent to Azure Speech and billed to the selected resource.
      Selection synthesizes a short generic test phrase to verify your Speech RBAC.
      PR source inspection still uses Copilot. Video rendering and cat photos stay local.</p>
    <label>Azure subscription<select id="speechSubscription" ${s.busy ? 'disabled' : ''}>
      <option value="">Select a subscription</option>${s.subscriptions.map(x => `<option value="${esc(x.id)}" ${x.id === s.subscription ? 'selected' : ''}>${esc(x.name)} (${esc(x.id)})</option>`).join('')}</select></label>
    <div class="field"><button class="btn" data-sact="find" ${s.busy || !s.subscription ? 'disabled' : ''}>Find compatible resources</button>
      <button class="mini" data-sact="reload" ${s.busy ? 'disabled' : ''}>Reload sign-in</button></div>
    ${s.resources ? `<label>Resource<select id="speechResource" ${s.busy ? 'disabled' : ''}><option value="">Select a verified resource</option>
      ${s.resources.filter(r => r.compatible).map(r => `<option value="${esc(r.resourceId)}" ${r.resourceId === s.resource ? 'selected' : ''}>${esc(r.name)} / ${esc(r.region)}</option>`).join('')}</select></label>
      ${!s.resources.some(r => r.compatible) ? '<p class="error">No compatible resources with verified Speech voice access were found. Check RBAC, network access and subscription.</p>' : ''}
      ${s.resources.filter(r => !r.compatible).map(r => `<p class="speech-result small">${esc(r.name)}: ${esc(r.message)}</p>`).join('')}` : ''}
    ${selected ? `<p class="speech-result small">${esc(selected.resourceId)}<br>${esc(selected.message)}</p>
      <label>Neural voice<select id="speechVoice" ${s.busy ? 'disabled' : ''}>${selected.voices.map(v => `<option ${v === s.voice ? 'selected' : ''}>${esc(v)}</option>`).join('')}</select></label>` : ''}
    <details id="speechExactDetails" ${s.exactOpen ? 'open' : ''}><summary>Have an exact resource ID?</summary><p class="help">Use an account ID ending in Microsoft.CognitiveServices/accounts/name, not a project URL.
      This also works when your account can read a resource but cannot list its subscription.</p>
      <label>Resource ID<input id="speechExactId" value="${esc(s.exactId)}" placeholder="/subscriptions/.../resourceGroups/.../providers/Microsoft.CognitiveServices/accounts/..." ${s.busy ? 'disabled' : ''}></label>
      <label>Voice name<input id="speechExactVoice" value="${esc(s.exactVoice)}" ${s.busy ? 'disabled' : ''}></label>
      <button class="mini" data-sact="exact" ${s.busy ? 'disabled' : ''}>Verify and use this resource${s.key ? ', then generate' : ''}</button></details>
    <p role="status" aria-live="polite" class="${s.error ? 'error' : 'small'}">${s.busy ? '<span class="video-spinner" aria-hidden="true"></span> ' : ''}${esc(s.error || (s.busy ? s.progress || 'Checking Azure access... You can cancel this setup.' : 'Only compatible resources with verified voice access can be selected.'))}</p>
    <div class="field"><button class="btn primary" data-sact="use" ${s.busy || !selected || !s.voice ? 'disabled' : ''}>
      ${s.key ? 'Use resource and generate video' : 'Use this resource'}</button>
      <button class="btn" data-sact="cancel">Cancel</button></div>`;
}

async function closeSpeechSetup() {
  ++S.speechEpoch;
  S.speechSetup = null; $('#speechDialog').close();
  try { await rpc('cancelSpeechSetup'); } catch (e) { toast(e.message || String(e), 'error'); }
}

async function onSpeechAction(action) {
  const s = S.speechSetup;
  if (!s) return;
  if (action === 'cancel') { await closeSpeechSetup(); return; }
  if (s.busy) return;
  if (action === 'reload') { $('#speechDialog').close(); await openSpeechSetup(s.key); return; }
  const epoch = S.speechEpoch;
  const exactId = action === 'exact' ? $('#speechExactId').value.trim() : null;
  const exactVoice = action === 'exact' ? $('#speechExactVoice').value.trim() : null;
  s.busy = true; s.error = ''; s.progress = action === 'find'
    ? 'Checking compatible Azure Speech resources...' : 'Verifying Azure Speech synthesis permissions...'; renderSpeechSetup();
  try {
    if (action === 'find') {
      s.resources = await rpc('findSpeechResources', { subscription: s.subscription, epoch });
      if (!Array.isArray(s.resources)) throw new Error('Azure returned no resource list.');
      s.resource = ''; s.voice = '';
    } else {
      const settings = await rpc('selectSpeechResource', { resourceId: exactId || s.resource, voice: exactVoice || s.voice, epoch });
      if (epoch !== S.speechEpoch) return;
      if (!settings?.videoSpeech) throw new Error('Azure Speech setup was not confirmed.');
      S.settings = settings;
      const key = s.key;
      ++S.speechEpoch; S.speechSetup = null; $('#speechDialog').close(); render();
      if (key && canVideo(key)) { S.expanded.add(key); renderRows(); await onAction('tiktokPr', {}, key); }
      else if (key) toast('Azure Speech was saved, but this PR is no longer eligible. No video was started; refresh and choose an active monitored PR.', 'info');
      else toast('Azure Speech resource and voice saved.', 'info');
      return;
    }
  } catch (e) { if (epoch === S.speechEpoch) s.error = e.message || String(e); }
  finally { if (epoch === S.speechEpoch) { s.busy = false; renderSpeechSetup(); } }
}

async function openDrawer() {
  if (S.drawer) return;
  S.drawerFocus = document.activeElement;
  for (const el of document.body.children || []) {
    if (['drawer', 'scrim', 'toasts'].includes(el.id)) continue;
    S.drawerInert.set(el, el.inert); el.inert = true;
  }
  for (const el of $('#toasts').querySelectorAll('button')) {
    S.drawerInert.set(el, el.inert); el.inert = true;
  }
  ++S.drawerEpoch;
  S.drawerDraft.clear(); S.drawerDraftReset = true;
  S.drawer = true; $('#drawer').hidden = false; $('#scrim').hidden = false; renderDrawer();
  $('#closeDrawer').focus?.({ preventScroll: true });
  await loadApprovalSettings();
  if (S.settings.fixBackend === 'wsl') await loadWslDistros();
}
function closeDrawer() {
  S.drawer = false; $('#drawer').hidden = true; $('#scrim').hidden = true;
  for (const [el, inert] of S.drawerInert) el.inert = inert;
  S.drawerInert.clear();
  const saved = S.drawerFocus;
  const opener = saved && saved !== document.body && saved.isConnected && !saved.disabled && saved.getClientRects().length ? saved : $('#settingsBtn');
  S.drawerFocus = null;
  opener.focus?.({ preventScroll: true });
}

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

const drawerFieldKey = el => el.id || (el.dataset.wslRepo ? `wsl:${el.dataset.wslRepo}` : `native:${el.dataset.nativeRepo}`);
const drawerFields = () => [...$('#drawerBody').querySelectorAll('input[id], select[id], textarea[id], input[data-native-repo], input[data-wsl-repo]')];
function checkoutMappings(backend) {
  const mappings = { ...(backend === 'native' ? S.settings.nativeRepoDirectories : S.settings.wslRepoDirectories) };
  for (const el of $('#drawerBody').querySelectorAll(`[data-${backend}-repo]`)) {
    const key = backend === 'native' ? el.dataset.nativeRepo : el.dataset.wslRepo;
    if (el.value.trim()) mappings[key] = el.value.trim();
    else delete mappings[key];
  }
  return mappings;
}

async function loadWslDistros() {
  S.wslDistrosError = null;
  try { S.wslDistros = await rpc('listWslDistros'); }
  catch (e) { S.wslDistrosError = e.message || String(e); }
  if (S.drawer) renderDrawer();
}

function clearSubmittedDrawerInput(id, value, epoch) {
  const current = $('#' + id);
  if (S.drawer && epoch === S.drawerEpoch && current?.value === value) {
    current.value = '';
    S.drawerDraft.delete(id);
  }
}

function renderDrawer() {
  const st = S.settings;
  if (!st) return;
  const focusBody = $('#drawerBody');
  const active = document.activeElement;
  const focusFields = drawerFields();
  const focused = focusFields.find(el => el === active);
  const focusSelection = focused && { start: focused.selectionStart, end: focused.selectionEnd, direction: focused.selectionDirection };
  const focusTop = focusBody.scrollTop;
  const titles = new Map(S.snap.prs.map((p) => [p.key.toLowerCase(), p]));
  const disc = S.discovered;
  const body = $('#drawerBody');
  const fields = drawerFields();
  if (S.drawerDraftReset) { S.drawerDraft.clear(); S.drawerDraftReset = false; }
  else for (const el of fields) {
    if (el.id === 'theme') continue;
    const initial = el.tagName === 'SELECT' ? [...el.options].find(o => o.defaultSelected)?.value ?? el.options[0]?.value : el.defaultValue;
    const dirty = el.type === 'checkbox' ? el.checked !== el.defaultChecked : el.value !== initial;
    if (dirty) S.drawerDraft.set(drawerFieldKey(el), { value: el.value, checked: el.checked });
    else S.drawerDraft.delete(drawerFieldKey(el));
  }
  body.innerHTML = `
    <div class="section">
      <h4>Appearance</h4>
      <p class="help" id="themeHelp">Changes apply and save immediately. Light Mode is the default.</p>
      <div class="field"><label for="theme">Theme</label><select id="theme" aria-describedby="themeHelp" ${S.themeBusy ? 'disabled' : ''}>
        ${[['light', 'Light Mode'], ['dark', 'Dark Mode']].map(([value, label]) => `<option value="${value}" ${AppTheme.effective(st.theme) === value ? 'selected' : ''}>${label}</option>`).join('')}
      </select></div>
    </div>
    ${updateSection()}
    <div class="section"><h4>TikTok narration</h4>
      <p class="help">${st.videoSpeech ? `Azure Speech / ${esc(st.videoSpeech.region)} / ${esc(st.videoSpeech.voice)}` : 'Choose an Azure Speech resource on your first video. Azure CLI (az) and az login are required.'}</p>
      <button class="btn" data-dact="speechSetup">Choose Azure Speech resource / voice</button></div>
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
      <div class="field"><input type="text" id="repoUrl" placeholder="https://dev.azure.com/org/project/_git/repo" /><button class="btn primary" data-dact="addRepo" ${S.repoAddBusy ? 'disabled' : ''}>${svg('plus')} ${S.repoAddBusy ? 'Checking…' : 'Add'}</button></div>
      <div class="field"><button class="btn" data-dact="discover">${svg('search')} Discover repos where I have PRs</button></div>
      ${disc === 'loading' ? '<div class="small" style="margin-top:8px">Searching…</div>' : ''}
      ${Array.isArray(disc) ? (disc.length ? `<div class="items">${disc.map((r, i) => `<div class="item"><div class="grow"><div><b>${esc(r.repo)}</b></div><div class="small">${esc(r.org)} / ${esc(r.project)}</div></div>
          <button class="mini" data-dact="addDiscovered" data-i="${i}">${svg('plus')} Add</button></div>`).join('')}
          <button class="btn" data-dact="addAllDiscovered" style="align-self:flex-start">Add all ${disc.length}</button></div>` : '<div class="small" style="margin-top:8px">No other repositories with your PRs found.</div>') : ''}
    </div>
    <div class="section">
      <h4>Watched authors</h4>
      <p class="help">Your PRs and PRs by these people or service identities appear under Mine + watched and trigger notifications. All always shows every PR in your repositories. Match by alias, UPN or display name.</p>
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
      <label class="check"><input type="checkbox" id="notifications" ${st.notifications ? 'checked' : ''}/> Tray notifications (closing the window keeps it in the tray)</label>
      <label class="check"><input type="checkbox" id="sound" ${st.sound ? 'checked' : ''}/> Play chime for new Needs Human PRs</label>
    </div>
    <div class="section">
      <h4>Auto-complete</h4>
      <div class="field"><label>Merge strategy</label><select id="mergeStrategy">${['squash', 'noFastForward', 'rebase', 'rebaseMerge'].map((m) => `<option ${st.autoComplete.mergeStrategy === m ? 'selected' : ''}>${m}</option>`).join('')}</select></div>
      <label class="check"><input type="checkbox" id="deleteSourceBranch" ${st.autoComplete.deleteSourceBranch ? 'checked' : ''}/> Delete source branch</label>
      <label class="check"><input type="checkbox" id="transitionWorkItems" ${st.autoComplete.transitionWorkItems ? 'checked' : ''}/> Complete linked work items</label>
    </div>
    <div class="section">
      <h4>Copilot Fix</h4>
      <div class="field"><label for="fixBackend">Run new Fix tasks using</label><select id="fixBackend">
        <option value="windows-native" ${(st.fixBackend || 'windows-native') === 'windows-native' ? 'selected' : ''}>Windows (default)</option>
        <option value="wsl" ${st.fixBackend === 'wsl' ? 'selected' : ''}>WSL (opt in)</option></select></div>
      <p class="help">Choose one backend; there is no automatic fallback. Save to apply to new runs only. Active runs keep their captured backend, distro and paths. Ask and focused reviews always use Windows independently.</p>
      <p class="help">Both backends use an isolated worktree at the captured PR source commit. The source checkout is not switched or edited by the launcher. Each deliberate run uses --yolo for tool/path/URL permissions without confirmation; global settings are unchanged. Worktree isolation is not a filesystem sandbox. Runs are told to commit validated fixes and ordinary-push them only to the PR's captured source branch (a prompt instruction, not enforced). Merge/PR-update command denies and the prompt's approval/policy boundaries remain.</p>
      <div data-fix-settings="windows-native">
      ${st.repos.map(r => `<div class="field native-repo"><label>${esc(r.repo)}<small>${esc(r.org)} / ${esc(r.project)}</small></label>
        <input type="text" data-native-repo="${esc(repoKey(r))}" value="${esc(st.nativeRepoDirectories?.[repoKey(r)] || '')}" placeholder="C:\\repos\\${esc(r.repo)}" /></div>`).join('')}
      <p class="help">Required: an existing absolute local Windows Git checkout for each repository you Fix, Git for Windows and the full authenticated Windows Copilot CLI. Windows Fix registers one on-demand per-user Task Scheduler task to launch its separate immutable supervisor outside the app's lifetime job. It uses your current interactive token, no password, administrator rights or service installation. The supervisor removes its task registration on exit; exact Cancel stops the run's owned job, not other tasks. Task Scheduler policy failures are reported, never retried on an uncertain dispatch.</p></div>
      <div data-fix-settings="wsl">
      <div class="field"><label for="wslDistro">WSL distro (required)</label><select id="wslDistro">
        <option value="">Select an installed distro</option>
        ${[...new Set([st.wslDistro, ...(S.wslDistros || [])].filter(Boolean))].map(d => `<option value="${esc(d)}" ${d === st.wslDistro ? 'selected' : ''}>${esc(d)}</option>`).join('')}</select>
        <button class="btn" data-dact="wslDistros">Refresh distros</button></div>
      ${S.wslDistrosError ? `<p class="help" role="alert">${esc(S.wslDistrosError)}</p>` : ''}
      ${st.repos.map(r => `<div class="field"><label>${esc(r.repo)}<small>${esc(r.org)} / ${esc(r.project)}</small></label>
        <input type="text" data-wsl-repo="${esc(repoKey(r))}" value="${esc(st.wslRepoDirectories?.[repoKey(r)] || '')}" placeholder="/home/user/repos/${esc(r.repo)}" /></div>`).join('')}
      <p class="help">Required: an explicit installed distro and absolute Linux Git checkout path for each repository you Fix. Git, authenticated full Copilot CLI, bgtask and ownership prerequisites are checked inside that distro. No default distro or ~/clones path is inferred for new WSL runs.</p></div>
      <p class="help">Switching retains both backends' settings. Legacy WSL runs keep their original host and work. Use each run's exact Cancel control; unavailable tools leave a recovery warning, never a replay on a different backend.</p>
    </div>
    <div class="section">
      <h4>Copilot triage instructions</h4>
      <p class="help">Override the "Ask" in new Fix prompts. Leave empty to keep the default (drive the PR to green, no auto-complete). Required gates and execution boundaries still apply.</p>
      <div class="field"><label for="triageInstructions">Custom Ask (override)</label><textarea id="triageInstructions" placeholder="Default instructions">${esc(st.triageInstructions || '')}</textarea></div>
      <p class="help">Additional instructions are appended once at the very end of Copy prompt and new Fix launches, after the generated gates and verified launch/publish context. They do not replace the default or custom Ask, or change existing sessions.</p>
      <div class="field"><label for="additionalTriageInstructions">Additional instructions (append)</label><textarea id="additionalTriageInstructions" placeholder="Optional additional instructions">
${esc(st.additionalTriageInstructions || '')}</textarea></div>
    </div>
    <div class="section"><button class="btn primary" data-dact="save">${svg('check')} Save settings</button></div>`;

  const current = drawerFields();
  for (const el of current) {
    const draft = S.drawerDraft.get(drawerFieldKey(el));
    if (draft) { el.value = draft.value; if (el.type === 'checkbox') el.checked = draft.checked; }
  }
  for (const group of body.querySelectorAll('[data-fix-settings]'))
    group.hidden = group.dataset.fixSettings !== $('#fixBackend').value;
  focusBody.scrollTop = focusTop;
  const next = focused && current.find(el => drawerFieldKey(el) === drawerFieldKey(focused));
  if (S.drawer && next && !next.disabled) {
    next.focus({ preventScroll: true });
    if (typeof focusSelection.start === 'number') next.setSelectionRange(focusSelection.start, focusSelection.end, focusSelection.direction);
  } else if (S.drawer && !(S.themeBusy && (active === document.body || active?.id === 'theme')) &&
      !document.activeElement?.closest('#drawer')) $('#closeDrawer').focus?.({ preventScroll: true });
}

async function saveSettings(patch, commitDraft = false) {
  const epoch = S.drawerEpoch;
  const submitted = commitDraft ? new Map(drawerFields()
    .filter(el => !['theme', 'repoUrl', 'wlInput'].includes(el.id))
    .map(el => [drawerFieldKey(el), { value: el.value, checked: el.checked }])) : null;
  const st = { ...S.settings, ...patch };
  S.settings = await rpc('saveSettings', { settings: st });
  if (submitted && S.drawer && epoch === S.drawerEpoch) {
    // Rebase capture to the submission so newer edits reverted to old defaults stay dirty.
    for (const el of drawerFields()) {
      const value = submitted.get(drawerFieldKey(el));
      if (!value) continue;
      const current = { value: el.value, checked: el.checked };
      if (current.value === value.value && current.checked === value.checked) S.drawerDraft.delete(drawerFieldKey(el));
      else S.drawerDraft.set(drawerFieldKey(el), current);
      if (el.type === 'checkbox') el.defaultChecked = value.checked;
      else if (el.tagName === 'SELECT') for (const option of el.options) option.defaultSelected = option.value === value.value;
      else el.defaultValue = value.value;
      el.value = current.value;
      if (el.type === 'checkbox') el.checked = current.checked;
    }
  }
  render();
}

function applySavedTheme() {
  AppTheme.apply(S.settings?.theme);
  AppTheme.remember(S.settings?.theme);
}

async function changeTheme(select) {
  if (S.themeBusy) return;
  const previous = S.settings?.theme;
  const selected = select.value;
  const hadFocus = document.activeElement === select;
  S.themeBusy = true;
  select.disabled = true;
  AppTheme.apply(selected);
  try {
    const saved = await rpc('setTheme', { theme: selected });
    if (saved?.theme !== selected) throw new Error('The host did not confirm the selected theme.');
    S.settings = saved;
    applySavedTheme();
  } catch (e) {
    AppTheme.apply(previous);
    select.value = AppTheme.effective(previous);
    toast('Theme was not saved: ' + (e.message || String(e)), 'error');
  } finally {
    S.themeBusy = false;
    const current = $('#theme');
    if (current) {
      current.value = AppTheme.effective(S.settings?.theme);
      current.disabled = false;
      if (hadFocus && S.drawer && document.activeElement === document.body)
        current.focus({ preventScroll: true });
    }
  }
}

async function onDrawerAction(act, el) {
  if (act === 'speechSetup') { closeDrawer(); await openSpeechSetup(); return; }
  if (act === 'checkUpdate') { await checkForUpdate(); return; }
  if (act === 'installUpdate') { await installUpdate(); return; }
  if (act === 'openRelease') { openRelease(); return; }
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
        if (S.repoAddBusy) return;
        const value = $('#repoUrl').value, epoch = S.drawerEpoch;
        const url = value.trim();
        if (!url) return;
        S.repoAddBusy = true; renderDrawer();
        try {
          S.settings = await rpc('addRepo', { url });
          clearSubmittedDrawerInput('repoUrl', value, epoch);
          toast('Repository added — refreshing');
        } finally { S.repoAddBusy = false; }
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
        const value = $('#wlInput').value, epoch = S.drawerEpoch;
        const v = value.trim();
        if (!v) return;
        await saveSettings({ whitelist: [...S.settings.whitelist, v] });
        clearSubmittedDrawerInput('wlInput', value, epoch);
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
      case 'save': {
        const additionalInstructions = $('#additionalTriageInstructions').value;
        const storedAdditionalInstructions = S.settings.additionalTriageInstructions;
        await saveSettings({
          refreshSeconds: +$('#refreshSeconds').value || 120,
          mergeCheckMinutes: +$('#mergeCheckMinutes').value || 10,
          notifications: $('#notifications').checked,
          sound: $('#sound').checked,
          ignoreDrafts: $('#ignoreDrafts').checked,
          autoComplete: { ...S.settings.autoComplete, mergeStrategy: $('#mergeStrategy').value, deleteSourceBranch: $('#deleteSourceBranch').checked, transitionWorkItems: $('#transitionWorkItems').checked },
          triageInstructions: $('#triageInstructions').value,
          additionalTriageInstructions: typeof storedAdditionalInstructions === 'string' &&
            additionalInstructions === storedAdditionalInstructions.replace(/\r\n?/g, '\n')
            ? storedAdditionalInstructions : additionalInstructions,
          nativeRepoDirectories: checkoutMappings('native'),
          fixBackend: $('#fixBackend').value,
          wslDistro: $('#wslDistro').value,
          wslRepoDirectories: checkoutMappings('wsl'),
        }, true);
        toast('Settings saved');
        break;
      }
      case 'wslDistros': await loadWslDistros(); break;
    }
  } catch (e) {
    if (act === 'discover' && S.discovered === 'loading') S.discovered = null;
    toast(e.message || String(e), 'error');
  }
  render();
}

// ------------------------------------------------------------------ wiring
function wire() {
  wireInvestigation();
  const rows = $('#rows');
  $('#speechDialog').addEventListener('cancel', e => { e.preventDefault(); void closeSpeechSetup(); });
  $('#speechDialog').addEventListener('click', e => {
    const button = e.target.closest('[data-sact]');
    if (button && !button.disabled) void onSpeechAction(button.dataset.sact);
  });
  $('#speechDialog').addEventListener('input', e => {
    const s = S.speechSetup;
    if (!s || s.busy) return;
    if (e.target.id === 'speechExactId') s.exactId = e.target.value;
    if (e.target.id === 'speechExactVoice') s.exactVoice = e.target.value;
  });
  $('#speechDialog').addEventListener('toggle', e => {
    if (S.speechSetup && e.target.id === 'speechExactDetails') S.speechSetup.exactOpen = e.target.open;
  }, true);
  $('#speechDialog').addEventListener('change', e => {
    const s = S.speechSetup;
    if (!s || s.busy || !['speechSubscription', 'speechResource', 'speechVoice'].includes(e.target.id)) return;
    if (e.target.id === 'speechSubscription') { s.subscription = e.target.value; s.resources = null; s.resource = ''; s.voice = ''; }
    if (e.target.id === 'speechResource') {
      s.resource = e.target.value;
      const voices = (s.resources || []).find(r => r.resourceId === s.resource)?.voices || [];
      s.voice = voices.find(v => v === 'en-US-Ava:DragonHDLatestNeural') || voices.find(v => v === 'en-US-AvaMultilingualNeural') || voices[0] || '';
    }
    if (e.target.id === 'speechVoice') s.voice = e.target.value;
    renderSpeechSetup();
  });
  document.addEventListener('error', e => {
    const video = e.target;
    if (!video.matches?.('video[data-video-media]')) return;
    const message = $('.video-playback-error', video.closest('.pr-video'));
    message.textContent = 'The MP4 could not be played. Retry generation or use the displayed local path in a media player; no upload was attempted.';
    message.hidden = false;
  }, true);
  document.addEventListener('contextmenu', openRowMenu);
  document.addEventListener('toggle', e => {
    if (!e.target.matches?.('.run-history') || !e.target.closest('.run-earlier')) return;
    const id = e.target.dataset.runId;
    if (e.target.open) S.runHistoryOpen.add(id);
    else S.runHistoryOpen.delete(id);
  }, true);
  for (const type of ['pointerover', 'focusin', 'pointerout', 'focusout']) {
    document.addEventListener(type, e => {
      const scope = e.target.closest('.run-scope');
      if (!scope) return;
      if (type === 'pointerover' && e.pointerType === 'touch') return;
      if (type === 'pointerover' || type === 'focusin') {
        if (e.target.closest('.run-info-button, .run-info-details')) setRunInfo(scope, true);
      } else if (!scope.contains(e.relatedTarget) && !scope.dataset.pinned &&
        !scope.matches(':hover') && !scope.contains(document.activeElement))
        setRunInfo(scope, false);
    });
  }
  $('main').addEventListener('scroll', queueIdCues, { capture: true, passive: true });
  window.addEventListener('resize', queueIdCues);
  const rowRegions = [rows, $('#runShelf'), $('#explanationShelf')];
  for (const region of rowRegions) {
    region.addEventListener('pointerenter', () => { S.pointerInRows = true; });
    region.addEventListener('pointerleave', e => {
      S.pointerInRows = !!e.relatedTarget?.nodeType && rowRegions.some(root => root.contains(e.relatedTarget));
      flushRowUpdates();
    });
    region.addEventListener('focusout', () => requestAnimationFrame(flushRowUpdates));
  }
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
  document.addEventListener('selectionchange', () => requestAnimationFrame(flushRowUpdates));
  window.addEventListener('blur', () => {
    closeRowMenu();
    S.pointerInRows = false;
    S.rowPointerDown = false;
    requestAnimationFrame(flushRowUpdates);
  });
  document.addEventListener('click', (e) => {
    const info = e.target.closest('.run-info-button');
    for (const scope of document.querySelectorAll('.run-scope')) {
      if (scope.contains(e.target)) continue;
      delete scope.dataset.pinned;
      setRunInfo(scope, false);
    }
    if (info) {
      const scope = info.closest('.run-scope'), pinned = !scope.dataset.pinned;
      if (pinned) scope.dataset.pinned = 'true';
      else delete scope.dataset.pinned;
      setRunInfo(scope, pinned);
      return;
    }
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
    if (SCOPES.includes(S.scope))
      rpc('setScope', { scope: S.scope }).then((saved) => { S.settings = saved; })
        .catch((err) => toast(`The view could not be saved: ${err.message || err}`, 'error'));
  });
  $('#refreshBtn').addEventListener('click', () => rpc('refresh'));
  $('#settingsBtn').addEventListener('click', openDrawer);
  $('#closeDrawer').addEventListener('click', closeDrawer);
  $('#scrim').addEventListener('click', closeDrawer);
  $('#drawerBody').addEventListener('change', (e) => {
    if (e.target.id === 'theme') changeTheme(e.target);
    if (e.target.id === 'fixBackend') {
      renderDrawer();
      if (e.target.value === 'wsl') loadWslDistros();
    }
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      const open = [...document.querySelectorAll('.run-scope')].filter(scope => !$('.run-info-details', scope).hidden);
      if (open.length) {
        e.preventDefault();
        for (const scope of open) {
          delete scope.dataset.pinned;
          setRunInfo(scope, false);
        }
        return;
      }
    }
    if (S.drawer) {
      if (e.key === 'Escape') { e.preventDefault(); closeDrawer(); }
      else if (e.key === 'Tab') {
        const drawer = $('#drawer');
        const controls = [...drawer.querySelectorAll('button, input, select, textarea, a[href], [tabindex]')]
          .filter(el => !el.disabled && el.tabIndex >= 0 && el.getClientRects().length);
        const first = controls[0], last = controls.at(-1);
        if (!drawer.contains(document.activeElement) || (e.shiftKey ? document.activeElement === first : document.activeElement === last)) {
          e.preventDefault(); (e.shiftKey ? last : first)?.focus();
        }
      } else if (e.key === 'Enter' && ['wlInput', 'repoUrl'].includes(document.activeElement?.id)) {
        e.preventDefault(); void onDrawerAction(document.activeElement.id === 'wlInput' ? 'addWl' : 'addRepo');
      }
      return;
    }
    if (e.key === 'Escape' && S.rowMenu) { e.preventDefault(); closeRowMenu(); flushRowUpdates(); return; }
    const control = expansionControl(e.target);
    if (control && ['Enter', ' '].includes(e.key)) {
      e.preventDefault();
      if (e.repeat || control.getAttribute('aria-disabled') === 'true') return;
      control.click();
      return;
    }
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
  if (SCOPES.includes(S.settings.scope)) S.scope = S.settings.scope;
  S.version = init.version;
  applySavedTheme();
  if (S.settings.theme != null && !AppTheme.supported.includes(S.settings.theme))
    toast('The saved theme is not supported by this version. Showing Light Mode without changing your preference.', 'info');
  for (const run of init.runs || []) updateCopilotRun(run);
  for (const artifact of init.explanationArtifacts || []) retainExplanation(artifact);
  for (const explanation of init.explanations || []) updateExplanation(explanation);
  for (const investigation of init.investigations || []) updateInvestigation(investigation);
  for (const video of init.videos || []) updateVideo(video);
  setSnapshot(init.snapshot);
  render();
  if (init.update) setUpdate(init.update);
  window.chrome?.webview?.postMessage('ready');
  if (params.has('expand')) {
    const n = +params.get('expand') || 1;
    baseVisible().slice(0, n).forEach((p) => S.expanded.add(p.key));
    renderRows();
  }
  window.__ready = true;
}

boot().catch((e) => { document.body.insertAdjacentHTML('beforeend', `<pre class="boot-error" role="alert">${esc(e.stack || e)}</pre>`); });
