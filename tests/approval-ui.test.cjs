const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', 'src', 'AdoPrView', 'wwwroot', 'app.js'), 'utf8');

function setup() {
  const elements = new Map();
  const element = () => ({
    innerHTML: '', textContent: '', dataset: {}, scrollTop: 0, children: [],
    classList: { toggle() {} }, addEventListener() {}, querySelectorAll: () => [],
    appendChild(child) { this.children.push(child); }, remove() {},
  });
  const document = {
    querySelector(selector) {
      if (!elements.has(selector)) elements.set(selector, element());
      return elements.get(selector);
    },
    createElement: element, addEventListener() {}, querySelectorAll: () => [],
    body: { insertAdjacentHTML() {} },
  };
  const dialogs = [];
  const window = { MockHost: { rpc: () => new Promise(() => {}) }, addEventListener() {}, getSelection: () => '',
    confirm: text => { dialogs.push(text); return true; } };
  const context = vm.createContext({
    window, document, URLSearchParams, location: { search: '?mock=1' },
    console, setTimeout() {}, setInterval() {}, queueMicrotask, confirm: text => window.confirm(text),
  });
  vm.runInContext(source + '\nglobalThis.hooks = { S, onAction, actionButtons, renderDetail, setSnapshot, flushRowUpdates, renderRows, undoHide, baseVisible, updateCopilotRun, renderRunShelf, runForPr, runActive, onDrawerAction, loadApprovalSettings, renderDrawer };', context);
  const { hooks } = context;
  const pr = {
    key: 'repo:123', id: 123, repo: 'sample-service', title: 'Offline PR',
    org: 'contoso', project: 'Sample', approval: { title: 'Request approval via Sample approval for only this PR' },
    isMine: true, lane: 'needsHuman', humanActions: ['pop', 'autocomplete'], gates: {},
  };
  hooks.S.settings = { repos: [{}], hidden: [], whitelist: [], autoComplete: { mergeStrategy: 'squash' }, refreshSeconds: 120, mergeCheckMinutes: 10 };
  hooks.S.snap = { prs: [pr], lastRefresh: new Date().toISOString(), approvalProvider: { name: 'Sample approval' } };
  return { hooks, pr, window, elements, dialogs };
}

test('settings expose and save independent tray notifications and chime controls in every combination', async () => {
  for (const notifications of [false, true]) for (const sound of [false, true]) {
    const { hooks, window, elements } = setup();
    Object.assign(hooks.S.settings, { notifications, sound, futureSetting: 'KEEP' });
    hooks.renderDrawer();
    const html = elements.get('#drawerBody').innerHTML;
    assert.match(html, /id="notifications"[^>]*\/> Tray notifications \(closing the window keeps it in the tray\)/);
    assert.match(html, /id="sound"[^>]*\/> Play chime for new Needs Human PRs/);
    assert.equal(/id="notifications" checked/.test(html), notifications);
    assert.equal(/id="sound" checked/.test(html), sound);
    for (const id of ['refreshSeconds', 'mergeCheckMinutes', 'triageInstructions', 'wslDistro', 'mergeStrategy',
      'notifications', 'sound', 'deleteSourceBranch', 'transitionWorkItems']) {
      if (!elements.has('#' + id)) elements.set('#' + id, { value: '', checked: false });
    }
    elements.get('#notifications').checked = !notifications;
    elements.get('#sound').checked = !sound;
    let calls = 0;
    window.MockHost.rpc = async (method, p) => {
      assert.equal(method, 'saveSettings');
      assert.equal(p.settings.notifications, !notifications);
      assert.equal(p.settings.sound, !sound);
      assert.equal(p.settings.futureSetting, 'KEEP');
      calls++;
      return p.settings;
    };
    await hooks.onDrawerAction('save', {});
    assert.equal(calls, 1);
    assert.equal(hooks.S.settings.notifications, !notifications);
    assert.equal(hooks.S.settings.sound, !sound);
  }
});

test('only rows with a provider offer expose the approval action; existing actions remain', () => {
  const { hooks, pr } = setup();
  const html = hooks.actionButtons(pr);
  for (const action of ['generateApproval', 'pop', 'autocomplete', 'triage', 'open'])
    assert.ok(html.includes(`data-act="${action}"`));
  assert.ok(!hooks.actionButtons({ ...pr, approval: null }).includes('generateApproval'));
  const approval = html.match(/<button[^>]*data-act="generateApproval"[^>]*>.*?<\/button>/)[0];
  assert.ok(approval.includes('aria-label="Request approval via Sample approval for only this PR"'));
  assert.ok(approval.includes('M3 12h8M7 8v8M15 9l3-2v10M15 17h6'));
  assert.ok(!approval.includes('<span>'), 'Approval should be an icon, not a text button');
  assert.ok(!html.includes('data-act="hide"'), 'Hide must only be available through the row context menu');
  assert.ok(hooks.renderDetail(pr).includes('M3 12h8M7 8v8M15 9l3-2v10M15 17h6'));
});

test('row and expanded action disable together and rapid duplicate sends once', async () => {
  const { hooks, pr, window } = setup();
  let resolve;
  const calls = [];
  window.MockHost.rpc = (method, params) => {
    calls.push({ method, params });
    return new Promise((r) => { resolve = r; });
  };
  const first = hooks.onAction('generateApproval', {}, pr.key);
  await hooks.onAction('generateApproval', {}, pr.key);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].method, 'generateApproval');
  assert.deepEqual(Object.keys(calls[0].params), ['key']);
  assert.equal(calls[0].params.key, pr.key);
  assert.ok(hooks.S.expanded.has(pr.key));
  assert.match(hooks.actionButtons(pr), /data-act="generateApproval"[^>]*disabled aria-busy="true"/);
  assert.match(hooks.renderDetail(pr), /data-act="generateApproval" disabled aria-busy="true"/);
  resolve({ kind: 'returned', message: 'HTTP success does not confirm approval', response: '{"error":"denied"}' });
  await first;
  assert.equal(hooks.S.busy.size, 0);
  assert.equal(hooks.S.approvals.get(pr.key).kind, 'returned');
  assert.match(hooks.renderDetail(pr), /denied/);
});

test('confirmed Auto result updates captured PR immediately despite deferred snapshot and remains muted through refresh/collapse', async () => {
  const { hooks, pr, window } = setup();
  const neighbor = { ...pr, key: 'other:456', id: 456 };
  hooks.S.snap.prs.push(neighbor);
  hooks.S.pointerInRows = true;
  const confirmed = { ...pr, autoCompleteBy: 'Offline Reviewer', humanActions: ['pop'], gates: { autocomplete: { state: 'ok' } } };
  window.MockHost.rpc = async (method, p) => {
    assert.equal(method, 'autoComplete'); assert.equal(p.key, pr.key); assert.equal(p.enable, true);
    hooks.setSnapshot({ ...hooks.S.snap, prs: [confirmed, { ...neighbor, title: 'Deferred unrelated change' }] });
    return confirmed;
  };
  await hooks.onAction('autocomplete', {}, pr.key);
  assert.equal(hooks.S.snap.prs[0].autoCompleteBy, 'Offline Reviewer');
  assert.equal(hooks.S.snap.prs[1], neighbor);
  assert.match(hooks.actionButtons(hooks.S.snap.prs[0]), /class="act auto-set[^"]*" data-act="cancelAuto"/);
  assert.match(hooks.renderDetail(hooks.S.snap.prs[0]), /Cancel auto-complete/);
  hooks.S.pointerInRows = false; hooks.flushRowUpdates();
  await hooks.onAction('toggle', {}, pr.key);
  await hooks.onAction('toggle', {}, pr.key);
  assert.match(hooks.actionButtons(hooks.S.snap.prs[0]), /aria-pressed="true"/);
  assert.match(hooks.actionButtons(hooks.S.snap.prs[1]), /data-act="autocomplete"/);
});

test('Auto guards row/expanded duplicate set or cancel and enables cancellation only from confirmed state', async () => {
  const { hooks, pr, window } = setup();
  let resolve, calls = 0;
  window.MockHost.rpc = () => { calls++; return new Promise(r => { resolve = r; }); };
  const first = hooks.onAction('autocomplete', {}, pr.key);
  await hooks.onAction('autocomplete', {}, pr.key);
  await hooks.onAction('cancelAuto', {}, pr.key);
  assert.equal(calls, 1);
  assert.match(hooks.renderDetail(pr), /data-act="autocomplete"[^>]*disabled aria-busy="true"/);
  resolve({ ...pr, autoCompleteBy: 'Offline', humanActions: [], gates: { autocomplete: { state: 'ok' } } });
  await first;
  window.MockHost.rpc = async (method, p) => {
    assert.equal(p.enable, false);
    return { ...pr, autoCompleteBy: null };
  };
  await hooks.onAction('cancelAuto', {}, pr.key);
  assert.equal(hooks.S.snap.prs[0].autoCompleteBy, null);
  assert.match(hooks.actionButtons(hooks.S.snap.prs[0]), /class="act hot[^"]*" data-act="autocomplete"/);
});

test('failed or unconfirmed Auto does not mark success, retains escaped error and never changes neighboring PR', async () => {
  for (const result of [null, { key: 'wrong:456', autoCompleteBy: 'Offline' }, { key: 'repo:123', autoCompleteBy: null }]) {
    const { hooks, pr, window } = setup();
    window.MockHost.rpc = async () => result;
    await hooks.onAction('autocomplete', {}, pr.key);
    assert.equal(hooks.S.snap.prs[0], pr);
    assert.doesNotMatch(hooks.actionButtons(pr), /auto-set/);
    assert.match(hooks.renderDetail(pr), /read-back was not confirmed/);
  }
  const { hooks, pr, window } = setup();
  window.MockHost.rpc = async () => { throw new Error('<offline denied>'); };
  await hooks.onAction('autocomplete', {}, pr.key);
  assert.match(hooks.renderDetail(pr), /&lt;offline denied&gt;/);
  assert.doesNotMatch(hooks.actionButtons(pr), /auto-set/);
});

test('native Auto state takes priority over stale attention arrays and synchronizes while rows remain stable', () => {
  const { hooks, pr } = setup();
  hooks.S.pointerInRows = true;
  hooks.setSnapshot({ ...hooks.S.snap, prs: [{ ...pr, autoCompleteBy: 'Offline' }] });
  assert.equal(hooks.S.snap.prs[0].autoCompleteBy, 'Offline');
  assert.match(hooks.actionButtons({ ...pr, autoCompleteBy: 'Offline' }), /data-act="cancelAuto"/);
  assert.doesNotMatch(hooks.actionButtons({ ...pr, autoCompleteBy: 'Offline' }).match(/data-slot="auto">(<button[^>]+)/)[1], /class="act hot/);
});

test('service JSON is escaped; nested failure and refresh warning persist across snapshots', async () => {
  const { hooks, pr, window } = setup();
  window.MockHost.rpc = async () => ({
    kind: 'returned', message: 'Inspect the response; approval is not confirmed',
    response: '{"results":[{"error":"<img src=x onerror=alert(1)>"}]}',
    refreshWarning: 'Gate refresh failed; do not resubmit',
  });
  await hooks.onAction('generateApproval', {}, pr.key);
  hooks.setSnapshot({ ...hooks.S.snap, prs: [{ ...pr }] });
  const html = hooks.renderDetail(pr);
  assert.ok(!html.includes('<img src=x'));
  assert.ok(html.includes('&lt;img'));
  assert.ok(html.includes('Gate refresh failed; do not resubmit'));
  assert.equal(hooks.S.approvals.size, 1);
});

test('authentication/promise errors remain visible and release busy state', async () => {
  const { hooks, pr, window } = setup();
  window.MockHost.rpc = async () => { throw new Error('Provider requires a deliberate human step'); };
  await hooks.onAction('generateApproval', {}, pr.key);
  assert.equal(hooks.S.busy.size, 0);
  assert.equal(hooks.S.approvals.get(pr.key).kind, 'error');
  assert.match(hooks.renderDetail(pr), /Provider requires a deliberate human step/);
});

test('malformed RPC result never produces a success-shaped outcome', async () => {
  const { hooks, pr, window } = setup();
  window.MockHost.rpc = async () => null;
  await hooks.onAction('generateApproval', {}, pr.key);
  assert.equal(hooks.S.approvals.get(pr.key).kind, 'error');
  assert.match(hooks.renderDetail(pr), /Approval is not confirmed/);
});

test('Needs Human label, filter, reviewer gate and status explain the human requirement', () => {
  const { hooks, pr, elements } = setup();
  pr.humanActions = ['reviews'];
  pr.gates.reviews = { state: 'human', label: 'Needs +1', detail: 'Another reviewer is required' };
  pr.gates.agency = { state: 'warn', label: 'Partial', detail: 'Review bot completed with partial coverage' };
  pr.agency = { state: 'partial', detail: 'Review bot completed with partial coverage' };
  pr.policies = [{ name: 'Latest iteration sign-off', kind: 'review', status: 'running', statusDescription: 'Waiting on reviewer(s) to approve', isBlocking: true }];
  hooks.S.lane = 'needsHuman';
  hooks.setSnapshot({ ...hooks.S.snap, prs: [pr] });
  assert.match(elements.get('#laneCounters').innerHTML, /Needs Human/);
  assert.match(elements.get('#laneCounters').innerHTML, /data-lane="needsHuman"/);
  assert.ok(!elements.get('#laneCounters').innerHTML.includes('Needs you'));
  assert.doesNotMatch(elements.get('#rows').innerHTML, /data-act="gate" data-gate="(?:reviews|pop|autocomplete)"/);
  assert.match(elements.get('#rows').innerHTML, /pill warn click[^>]*data-act="gate" data-gate="agency"/);
  assert.match(hooks.renderDetail(pr), /Review bot completed with partial coverage/);
  assert.match(hooks.renderDetail(pr), /Human reviewer approval needed \(\+1\)/);
  assert.match(hooks.renderDetail(pr), /Waiting on reviewer\(s\) to approve/);
});

test('Ready retains partial coverage warning without claiming all gates are green', () => {
  const { hooks, pr, elements } = setup();
  pr.lane = 'ready';
  pr.humanActions = [];
  pr.gates.agency = { state: 'warn', label: 'Partial', detail: 'Review bot completed with partial coverage' };
  hooks.S.lane = 'ready';
  hooks.setSnapshot({ ...hooks.S.snap, prs: [pr] });
  assert.match(elements.get('#rows').innerHTML, /No outstanding actions/);
  assert.match(elements.get('#rows').innerHTML, /Partial/);
  assert.ok(!elements.get('#rows').innerHTML.includes('All gates green'));
});

test('addressed review bot finding leaves only PoP plus human sign-off', () => {
  const { hooks, pr, elements } = setup();
  pr.id = 4242;
  pr.humanActions = ['reviews', 'pop'];
  pr.agency = { state: 'addressed', iteration: 1, currentIteration: 7, detail: 'Review bot findings from iteration #1 resolved; ADO updates do not trigger another bot review' };
  pr.gates.agency = { state: 'ok', label: 'Addressed', detail: pr.agency.detail };
  pr.gates.reviews = { state: 'human', label: 'Needs +1', detail: 'Additional human sign-off is required' };
  pr.gates.pop = { state: 'human', label: 'Tap key' };
  pr.policies = [{ name: 'Latest iteration sign-off', kind: 'review', status: 'rejected', isBlocking: true, statusDescription: 'Additional human sign-off is required' }];
  hooks.S.lane = 'needsHuman';
  hooks.setSnapshot({ ...hooks.S.snap, prs: [pr] });
  assert.match(elements.get('#rows').innerHTML, /Addressed/);
  assert.ok(!elements.get('#rows').innerHTML.includes('Reviewing'));
  const detail = hooks.renderDetail(pr);
  assert.match(detail, /Human reviewer approval needed \(\+1\)/);
  assert.match(detail, /Additional human sign-off is required/);
  assert.match(detail, /ADO updates do not trigger another bot review/);
  assert.match(hooks.actionButtons(pr), /data-act="generateApproval"/);
  assert.match(hooks.actionButtons(pr), /data-act="pop"/);
});

test('provider offer title and detail render as escaped text on both +1 controls and in the expanded row', () => {
  const { hooks, pr } = setup();
  pr.approval = { title: 'Renew <b>stale</b> approval for only this PR', detail: 'Removes one <i>stale</i> reviewer first' };
  hooks.S.snap.approvalProvider = { name: 'Team <provider>', linkUrl: 'https://approval.example/', linkLabel: 'Provider site' };
  for (const html of [hooks.actionButtons(pr), hooks.renderDetail(pr)]) {
    assert.match(html, /Renew &lt;b&gt;stale&lt;\/b&gt; approval for only this PR/);
    assert.doesNotMatch(html, /<b>stale/);
  }
  const detail = hooks.renderDetail(pr);
  assert.match(detail, /Removes one &lt;i&gt;stale&lt;\/i&gt; reviewer first/);
  assert.match(detail, /Team &lt;provider&gt;/);
  assert.match(detail, /data-act="url" data-url="https:\/\/approval\.example\/"[^>]*>.*Provider site/);
  hooks.S.snap.approvalProvider = { name: 'Sample approval' };
  assert.doesNotMatch(hooks.renderDetail(pr), /data-act="url"/);
});

test('reviewer removal remains explicit when a later provider request fails', async () => {
  const { hooks, pr, window } = setup();
  window.MockHost.rpc = async () => ({
    kind: 'error',
    message: 'Removed one stale reviewer entry before the request. The provider returned HTTP 403; approval is not confirmed.',
  });
  await hooks.onAction('generateApproval', {}, pr.key);
  assert.match(hooks.renderDetail(pr), /Removed one stale reviewer entry/);
  assert.match(hooks.renderDetail(pr), /approval is not confirmed/);
  assert.equal(hooks.S.approvals.get(pr.key).kind, 'error');
  assert.equal(hooks.S.busy.size, 0);
});

test('refresh keeps displayed row data and routing together while a pointer targets the table', async () => {
  const { hooks, pr, window, elements } = setup();
  pr.url = 'https://dev.azure.com/org/project/_git/sample-service/pullrequest/123';
  hooks.setSnapshot(hooks.S.snap);
  hooks.S.pointerInRows = true;
  const other = { ...pr, key: 'other:456', id: 456, repo: 'sample-api',
    url: 'https://dev.azure.com/org/project/_git/sample-api/pullrequest/456' };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [other] });
  assert.equal(hooks.S.snap.prs[0], pr);
  let destination;
  window.MockHost.rpc = async (method, p) => { destination = { method, key: p.key }; return { message: 'Offline inline pane' }; };
  await hooks.onAction('pop', {}, pr.key);
  assert.deepEqual(destination, { method: 'openPop', key: pr.key });
  hooks.S.pointerInRows = false;
  hooks.flushRowUpdates();
  assert.equal(hooks.S.snap.prs[0], other);
  assert.equal(hooks.S.pendingSnapshot, null);
  assert.doesNotMatch(elements.get('#statusline').innerHTML, /paused/);
});

test('refresh metadata does not replace row DOM, and redraw waits for pointer release', () => {
  const { hooks, elements } = setup();
  hooks.setSnapshot(hooks.S.snap);
  const rows = elements.get('#rows');
  rows.innerHTML = 'existing row nodes';
  hooks.setSnapshot({ ...hooks.S.snap, refreshing: true, error: 'Transient status' });
  assert.equal(rows.innerHTML, 'existing row nodes');
  hooks.S.rowPointerDown = true;
  hooks.renderRows();
  assert.equal(rows.innerHTML, 'existing row nodes');
  assert.equal(hooks.S.rowsPending, true);
  hooks.S.rowPointerDown = false;
  hooks.flushRowUpdates();
  assert.notEqual(rows.innerHTML, 'existing row nodes');
});

test('Hide requires confirmation with full title, repo and PR number; Cancel has no effect', async () => {
  const { hooks, pr, window } = setup();
  let confirmation;
  window.confirm = text => { confirmation = text; return false; };
  window.MockHost.rpc = async () => assert.fail('Cancelled Hide must not call native RPC');
  await hooks.onAction('hide', {}, pr.key);
  for (const text of [pr.title, pr.repo, '!123', 'Ctrl+Z']) assert.ok(confirmation.includes(text));
  assert.equal(hooks.S.hideUndo.length, 0);
});

test('only completed hides enter undo history; undo is guarded and restores despite a stale hidden snapshot', async () => {
  const { hooks, pr, window } = setup();
  const calls = [];
  let finishUndo;
  window.MockHost.rpc = async (method, p) => {
    calls.push({ method, key: p.key });
    if (method === 'hide') return { ...hooks.S.settings, hidden: [pr.key] };
    return new Promise(resolve => { finishUndo = () => resolve({ ...hooks.S.settings, hidden: [] }); });
  };
  await hooks.onAction('hide', {}, pr.key);
  assert.equal(hooks.S.hideUndo.length, 1);
  assert.equal(hooks.baseVisible().length, 0);
  pr.isHidden = true;
  const first = hooks.undoHide();
  await hooks.undoHide();
  assert.equal(calls.filter(c => c.method === 'unhide').length, 1);
  finishUndo(); await first;
  assert.equal(hooks.S.hideUndo.length, 0);
  assert.equal(hooks.baseVisible()[0], pr);
  assert.equal(hooks.S.hideUndoBusy, false);
  await hooks.undoHide();
  assert.equal(calls.length, 2);
});

test('failed hide cannot be undone and failed undo remains retryable with explicit feedback', async () => {
  const { hooks, pr, window, elements } = setup();
  window.MockHost.rpc = async () => { throw Error('Hide failed'); };
  await hooks.onAction('hide', {}, pr.key);
  assert.equal(hooks.S.hideUndo.length, 0);
  assert.match(elements.get('#toasts').children.at(-1).innerHTML, /Hide failed/);
  window.MockHost.rpc = async () => ({ ...hooks.S.settings, hidden: [pr.key] });
  await hooks.onAction('hide', {}, pr.key);
  const entry = hooks.S.hideUndo[0];
  window.MockHost.rpc = async () => { throw Error('Undo failed'); };
  await hooks.undoHide();
  assert.equal(hooks.S.hideUndo[0], entry);
  assert.equal(hooks.S.hideUndoBusy, false);
  assert.equal(hooks.baseVisible().length, 0);
  assert.match(elements.get('#toasts').children.at(-1).innerHTML, /Undo failed/);
});

test('confirmed hides undo in reverse order without undoing other row actions', async () => {
  const { hooks, pr, window } = setup();
  const second = { ...pr, key: 'other:456', id: 456, title: 'Second PR' };
  hooks.S.snap.prs.push(second);
  const calls = [];
  window.MockHost.rpc = async (method, p) => {
    calls.push({ method, key: p.key });
    const hidden = hooks.S.settings.hidden.filter(k => k !== p.key);
    if (method === 'hide') hidden.push(p.key);
    return { ...hooks.S.settings, hidden };
  };
  await hooks.onAction('hide', {}, pr.key);
  await hooks.onAction('hide', {}, second.key);
  await hooks.onAction('restartMerge', {}, pr.key);
  await hooks.undoHide(); await hooks.undoHide();
  assert.deepEqual(calls.filter(c => c.method === 'unhide').map(c => c.key), [second.key, pr.key]);
  assert.equal(hooks.S.hideUndo.length, 0);
});

test('Updated and lane changes preserve surviving positions; only explicit Refresh revisions sort them', () => {
  const { hooks, pr, elements } = setup();
  const second = { ...pr, key: 'repo:456', id: 456, updated: '2026-01-01', lane: 'ready' };
  pr.updated = '2026-01-02';
  hooks.setSnapshot({ prs: [second, pr], lastRefresh: '2026-01-01', orderRevision: 0 });
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [pr.key, second.key]);
  const changed = { ...second, lane: 'blocked', updated: '2099-01-01' };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [changed, pr] });
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [pr.key, second.key]);
  assert.equal(hooks.S.snap.prs[1].lane, 'blocked');
  assert.match(elements.get('#rows').innerHTML, /row-lane[^>]*>Blocked/);
  assert.match(elements.get('#laneCounters').innerHTML, /Blocked/);
  hooks.setSnapshot({ ...hooks.S.snap, prs: [changed, pr], orderRevision: 1 });
  // Needs Human precedes Blocked on an explicit refresh, even if older.
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [pr.key, second.key]);
  const newestHuman = { ...changed, lane: 'needsHuman' };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [newestHuman, pr] });
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [pr.key, second.key]);
  hooks.setSnapshot({ ...hooks.S.snap, orderRevision: 2 });
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [second.key, pr.key]);
});

test('new arrivals append without reordering survivors; removals and filters keep the surviving order', () => {
  const { hooks, pr } = setup();
  const second = { ...pr, key: 'repo:456', id: 456 };
  hooks.setSnapshot({ prs: [pr, second], lastRefresh: '2026-01-01' });
  const newcomer = { ...pr, key: 'repo:789', id: 789, updated: '2099-01-01' };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [newcomer, second, pr] });
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [pr.key, second.key, newcomer.key]);
  hooks.S.q = '456';
  assert.equal(hooks.baseVisible()[0].key, second.key);
  hooks.S.q = '';
  hooks.setSnapshot({ ...hooks.S.snap, prs: [newcomer, pr] });
  assert.deepEqual(Array.from(hooks.S.snap.prs, p => p.key), [pr.key, newcomer.key]);
});

test('merge outcomes are neutral for in-flight skips and explicit on missing confirmations', async () => {
  const { hooks, pr, window, elements } = setup();
  pr.mergeRecheckRequestedAt = '2026-01-01T00:00:00Z';
  pr.mergeRecheckError = 'Restart denied; retry later';
  assert.match(hooks.renderDetail(pr), /Restart denied; retry later/);
  window.MockHost.rpc = async () => ({ requested: false, message: 'Already checking; not restarted' });
  await hooks.onAction('restartMerge', {}, pr.key);
  assert.match(elements.get('#toasts').children.at(-1).innerHTML, /Already checking; not restarted/);
  window.MockHost.rpc = async () => null;
  await hooks.onAction('restartMerge', {}, pr.key);
  assert.match(elements.get('#toasts').children.at(-1).innerHTML, /not confirmed/);
});

test('active lane filters retain a row that changes lane during actions until explicit Refresh', () => {
  const { hooks, pr, elements } = setup();
  hooks.setSnapshot(hooks.S.snap);
  hooks.S.lane = 'needsHuman';
  hooks.renderRows();
  hooks.setSnapshot({ ...hooks.S.snap, prs: [{ ...pr, lane: 'ready' }] });
  assert.match(elements.get('#rows').innerHTML, /data-key="repo:123"/);
  assert.match(elements.get('#rows').innerHTML, /row-lane[^>]*>Ready/);
  assert.match(elements.get('#statusline').innerHTML, /changed lane stay here/);
  hooks.setSnapshot({ ...hooks.S.snap, orderRevision: 1 });
  assert.equal(elements.get('#empty').hidden, false);
  assert.doesNotMatch(elements.get('#rows').innerHTML, /data-key="repo:123"/);
});

test('needed +1 is larger amber on mine or watched PRs; compact after latest-iteration approval, unsupported remains manual', () => {
  const { hooks, pr } = setup();
  for (const isMine of [true, false]) {
    const needy = { ...pr, isMine, humanActions: ['reviews'], gates: { reviews: { state: 'human', label: '1 +more' } } };
    const button = hooks.actionButtons(needy).match(/<button[^>]*data-act="generateApproval"[^>]*>.*?<\/button>/)[0];
    assert.match(button, /provider-approval hot/);
    assert.match(button, /review-needed/);
    assert.doesNotMatch(button, /<span>/, 'The +1 icon must not be duplicated by a second +1 label');
    assert.match(hooks.renderDetail(needy), /act provider-approval hot/);
    const satisfied = hooks.actionButtons({ ...needy, humanActions: [], gates: { reviews: { state: 'ok' } } });
    const compact = satisfied.match(/<button[^>]*data-act="generateApproval"[^>]*>.*?<\/button>/)[0];
    assert.doesNotMatch(compact, /hot|<span>/);
    const unsupported = hooks.actionButtons({ ...needy, approval: null });
    assert.doesNotMatch(unsupported, /generateApproval/);
    assert.match(unsupported, /data-act="manualReview"[^>]*Open this PR to arrange review/);
  }
});

test('Fix PR starts once with captured identity, never clipboard/focus; Copy prompt stays separate', async () => {
  const { hooks, pr, window } = setup();
  let resolve;
  const calls = [];
  window.MockHost.rpc = async (method, p) => {
    calls.push({ method, ...p });
    if (method === 'copyPrompt') return { prompt: 'offline' };
    return new Promise(r => resolve = r);
  };
  const first = hooks.onAction('triage', {}, pr.key);
  await hooks.onAction('triage', {}, pr.key);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].method, 'startCopilot');
  assert.equal(calls[0].key, pr.key);
  const r = { key: pr.key, id: pr.id, repo: pr.repo, title: pr.title, runId: 'offline-1', status: 'running', message: 'Started fake CLI', output: '<script>not executable</script>', worktreePath: '/tmp/offline-worktree' };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [{ ...pr, updated: '2099-01-01', lane: 'ready' }] });
  resolve(r); await first;
  assert.equal(hooks.runForPr(pr.key).runId, r.runId);
  assert.match(hooks.actionButtons(pr).match(/<button[^>]*data-act="triage"[^>]*>/)[0], /running/);
  assert.match(hooks.renderDetail(pr), /&lt;script&gt;/);
  assert.doesNotMatch(hooks.renderDetail(pr), /<script>not executable/);
  await hooks.onAction('triage', {}, pr.key);
  assert.equal(calls.length, 1, 'Active run must be shown rather than resubmitted');
  await hooks.onAction('copyPrompt', {}, pr.key);
  assert.equal(calls.at(-1).method, 'copyPrompt');
  assert.ok(calls.every(c => !['copy', 'triage', 'openUrl'].includes(c.method)));
});

test('Copilot indicator follows per-PR run state through collapse, another PR, refresh/filter and terminal events', async () => {
  const { hooks, pr, window } = setup();
  const b = { ...pr, key: 'other:456', id: 456 };
  window.getSelection = () => ({ toString: () => '' });
  hooks.setSnapshot({ ...hooks.S.snap, prs: [pr, b] });
  const button = p => hooks.actionButtons(p).match(/<button[^>]*data-act="triage"[^>]*>/)[0];
  const run = { key: pr.key, runId: 'offline-active', status: 'running', output: '', id: pr.id, repo: pr.repo, title: pr.title };
  hooks.updateCopilotRun(run);
  hooks.S.expanded.add(pr.key);
  await hooks.onAction('toggle', {}, pr.key);
  await hooks.onAction('toggle', {}, b.key);
  hooks.renderRows();
  hooks.setSnapshot({ ...hooks.S.snap, prs: [{ ...b, updated: '2099-01-01' }, pr], orderRevision: 1 });
  hooks.S.q = 'not-found'; hooks.renderRows();
  hooks.S.q = ''; hooks.renderRows();
  assert.match(button(pr), /running/);
  assert.match(button(pr), /aria-busy="true"/);
  assert.doesNotMatch(button(pr), /disabled/);
  assert.doesNotMatch(button(b), /running|aria-busy/);
  hooks.updateCopilotRun({ ...run, key: b.key, runId: 'independent', status: 'cancelling' });
  for (const status of ['completed', 'failed', 'cancelled']) {
    hooks.S.expanded.delete(pr.key);
    hooks.updateCopilotRun({ ...run, runId: 'offline-' + status, status });
    assert.doesNotMatch(button(pr), /running|aria-busy/);
    assert.match(button(b), /running/);
  }
});

test('PoP requests only the captured native key and never opens an external tab or infers approval', async () => {
  const { hooks, pr, window } = setup();
  let resolve;
  const calls = [];
  window.MockHost.rpc = (method, p) => { calls.push({ method, ...p }); return new Promise(r => resolve = r); };
  const action = hooks.onAction('pop', {}, pr.key);
  hooks.setSnapshot({ ...hooks.S.snap, prs: [{ ...pr, id: 999, key: 'other:999' }] });
  resolve({ message: 'Inline PoP opened; not approved.' }); await action;
  assert.deepEqual(calls, [{ method: 'openPop', key: pr.key }]);
  assert.ok(calls.every(c => c.method !== 'openUrl'));
  assert.deepEqual(pr.humanActions, ['pop', 'autocomplete']);
});

test('run output remains attached after removal/filtering and cancellation includes exact run identity', async () => {
  const { hooks, pr, window, elements } = setup();
  const run = { key: pr.key, id: pr.id, repo: pr.repo, title: pr.title, runId: 'offline-1', status: 'running', message: 'Running', output: 'Fake stdout\nFake stderr', worktreePath: '/tmp/offline-worktree' };
  hooks.updateCopilotRun(run);
  hooks.setSnapshot({ ...hooks.S.snap, prs: [] });
  await hooks.onAction('toggle', {}, pr.key);
  assert.match(elements.get('#runShelf').innerHTML, /!123/);
  assert.match(elements.get('#runShelf').innerHTML, /sample-service/);
  assert.match(elements.get('#runShelf').innerHTML, /Fake stdout/);
  window.MockHost.rpc = async (method, p) => {
    assert.equal(method, 'cancelCopilot'); assert.equal(p.key, pr.key); assert.equal(p.runId, run.runId);
    return { ...run, status: 'cancelled', message: 'Preserved worktree', worktreeRetained: true };
  };
  await hooks.onAction('cancelRun', { dataset: { runId: run.runId } }, pr.key);
  assert.equal(hooks.runForPr(pr.key).status, 'cancelled');
  assert.match(elements.get('#runShelf').innerHTML, /Preserved worktree/);
  hooks.updateCopilotRun({ ...run, status: 'completed', exitCode: 0, message: 'CLI exited normally; not verified fixed' });
  assert.match(elements.get('#runShelf').innerHTML, /exit 0/);
  assert.match(elements.get('#runShelf').innerHTML, /not verified fixed/);
});

test('active run panel stays with its own row across lanes, search, repo, scope and collapse; never orphaned below a view', async () => {
  const { hooks, pr, elements } = setup();
  const blocked = { ...pr, key: 'other:456', id: 456, repo: 'sample-api', lane: 'blocked', isMine: false };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [pr, blocked] });
  hooks.updateCopilotRun({ key: pr.key, id: pr.id, repo: pr.repo, title: pr.title, runId: 'live-1', status: 'running', output: 'live output' });
  hooks.S.expanded.add(pr.key);
  const rows = () => elements.get('#rows').innerHTML;
  const shelf = () => elements.get('#runShelf').innerHTML;
  const ownRunCard = () => (rows().split('<div class="pr ').find(chunk => chunk.includes(`data-key="${pr.key}"`)) || '').includes('data-run-id="live-1"');
  const views = [
    () => { hooks.S.lane = 'needsHuman'; }, () => { hooks.S.lane = 'blocked'; }, () => { hooks.S.lane = 'waiting'; },
    () => { hooks.S.lane = 'ready'; }, () => { hooks.S.lane = null; hooks.S.q = 'no-such-pr'; },
    () => { hooks.S.q = ''; hooks.S.repo = 'sample-api'; }, () => { hooks.S.repo = ''; hooks.S.scope = 'watched'; },
    () => { hooks.S.scope = 'all'; },
  ];
  for (const apply of views) {
    hooks.S.laneRetained.clear();
    apply();
    hooks.renderRows();
    assert.equal(shelf(), '', 'A run whose PR is loaded must never render outside its row');
    const visible = rows().includes(`data-key="${pr.key}"`);
    assert.equal(ownRunCard(), visible, 'The run card renders exactly when its own expanded row is visible');
    assert.equal((rows().match(/class="card copilot-run" data-run-key/g) || []).length, visible ? 1 : 0);
  }
  await hooks.onAction('toggle', {}, pr.key);
  assert.equal(ownRunCard(), false, 'Collapsing the owning row hides its run panel');
  assert.match(rows().split('<div class="pr ').find(chunk => chunk.includes(`data-key="${pr.key}"`)), /class="act copilot\s+running" data-act="triage"/, 'The collapsed row still shows the running indicator');
  assert.equal(shelf(), '');
  await hooks.onAction('toggle', {}, pr.key);
  assert.equal(ownRunCard(), true, 'Reopening the owning row shows the same run');
  hooks.S.lane = null;
  const runsBefore = JSON.stringify([...hooks.S.runs]);
  hooks.setSnapshot({ ...hooks.S.snap, prs: [blocked] });
  assert.match(shelf(), /PR no longer in the active list/);
  assert.doesNotMatch(shelf(), /live output/, 'A PR expanded while active starts collapsed once it leaves the active list');
  await hooks.onAction('toggle', {}, pr.key);
  assert.match(shelf(), /live output/, 'The departed entry can be deliberately expanded');
  assert.match(shelf(), /data-act="cancelRun" data-run-id="live-1" >/, 'The running run keeps its cancel control');
  hooks.setSnapshot({ ...hooks.S.snap, prs: [blocked] });
  assert.match(shelf(), /live output/, 'A later refresh without the PR keeps the deliberate expansion');
  await hooks.onAction('toggle', {}, pr.key);
  assert.doesNotMatch(shelf(), /live output/, 'The departed entry can be collapsed again');
  assert.equal(JSON.stringify([...hooks.S.runs]), runsBefore, 'Leaving the active list never changes run or cancel state');
  hooks.S.lane = 'blocked'; hooks.renderRows();
  assert.equal(shelf(), '', 'Filtered views never show runs for PRs outside the loaded data');
});

test('a hidden PR with a retained run does not reappear on the shelf after leaving the active list', () => {
  const { hooks, pr, elements } = setup();
  hooks.updateCopilotRun({ key: pr.key, id: pr.id, repo: pr.repo, title: 'Hidden sample title', runId: 'hidden-1', status: 'running', output: 'hidden output' });
  hooks.S.settings = { ...hooks.S.settings, hidden: [pr.key.toUpperCase()] };
  hooks.setSnapshot({ ...hooks.S.snap, prs: [] });
  assert.equal(elements.get('#runShelf').innerHTML, '', 'Hidden PRs never render a shelf card');
  assert.doesNotMatch(elements.get('#rows').innerHTML, /Hidden sample title/);
  assert.equal(hooks.S.runs.get('hidden-1')?.status, 'running', 'The retained run is not deleted or cancelled');
  hooks.S.settings = { ...hooks.S.settings, hidden: [] };
  hooks.renderRows();
  assert.match(elements.get('#runShelf').innerHTML, /Hidden sample title/, 'Unhiding shows the retained run again');
});

test('approval provider settings exchange only allow-listed action ids and stay silent without a provider', async () => {
  const { hooks, window, elements } = setup();
  const calls = [];
  window.MockHost.rpc = async (method, p) => {
    calls.push({ method, p });
    return method === 'approvalSettings' ? { message: 'Credential <saved>', help: null, actions: [{ id: 'replace', label: 'Replace <credential>' }] } : { message: 'Native dialog cancelled' };
  };
  hooks.S.drawer = true;
  await hooks.loadApprovalSettings();
  assert.match(elements.get('#drawerBody').innerHTML, /Credential &lt;saved&gt;/);
  assert.match(elements.get('#drawerBody').innerHTML, /data-dact="approvalSettingsAction" data-id="replace"[^>]*>Replace &lt;credential&gt;/);
  await hooks.onDrawerAction('approvalSettingsAction', { dataset: { id: 'replace' } });
  assert.deepEqual(calls.map(c => c.method), ['approvalSettings', 'approvalSettingsAction', 'approvalSettings']);
  assert.equal(JSON.stringify(calls[1].p), JSON.stringify({ id: 'replace' }));
  calls.length = 0;
  hooks.S.snap = { ...hooks.S.snap, approvalProvider: null, approvalProviderError: 'The configured approval provider was not loaded: <missing>' };
  await hooks.loadApprovalSettings();
  hooks.renderDrawer();
  assert.equal(calls.length, 0);
  assert.equal(hooks.S.approvalSettings, null);
  assert.doesNotMatch(elements.get('#drawerBody').innerHTML, /approvalSettingsAction/);
  assert.match(elements.get('#drawerBody').innerHTML, /not loaded: &lt;missing&gt;/);
});

test('review bot labels come from settings and fall back to a generic name', () => {
  const { hooks, pr } = setup();
  pr.agency = { state: 'findings', detail: '1 open finding' };
  pr.threads = [{ id: 1, status: 'active', author: 'Bot identity', isBot: true, isAgency: true, content: 'Finding', replies: 0 }];
  assert.match(hooks.renderDetail(pr), /<dt>Review bot<\/dt>/);
  assert.match(hooks.renderDetail(pr), /Review bot · current/);
  hooks.S.settings.reviewBot = { name: 'Team <Bot>', authorId: '00000000-0000-4000-8000-0000000000b0' };
  assert.match(hooks.renderDetail(pr), /<dt>Team &lt;Bot&gt;<\/dt>/);
  assert.match(hooks.renderDetail(pr), /Team &lt;Bot&gt; · current/);
});

test('late start replies and old run events cannot overwrite completed or newer inline runs', () => {
  const { hooks, pr } = setup();
  const first = { key: pr.key, id: pr.id, repo: pr.repo, title: pr.title, runId: 'offline-1',
    status: 'running', message: 'Running', output: 'Partial', startedAt: '2026-01-01T00:00:00Z' };
  hooks.updateCopilotRun({ ...first, status: 'completed', output: 'Final', exitCode: 0 });
  hooks.updateCopilotRun({ ...first, status: 'preparing', output: '' });
  hooks.updateCopilotRun(first);
  assert.equal(hooks.runForPr(pr.key).output, 'Final');
  const second = { ...first, runId: 'offline-2', startedAt: '2026-01-01T00:01:00Z', output: 'New run' };
  hooks.updateCopilotRun(second);
  hooks.updateCopilotRun({ ...first, status: 'completed', output: 'Old final' });
  assert.equal(hooks.runForPr(pr.key).runId, second.runId);
  assert.equal(hooks.runForPr(pr.key).output, 'New run');
  assert.equal(hooks.S.runs.get(first.runId).output, 'Old final');
});

test('recovered sessions keep independent history, recovered spinner and original run cancellation identity', async () => {
  const { hooks, pr, elements, window } = setup();
  const old = { key: pr.key, id: pr.id, repo: pr.repo, title: pr.title, runId: 'old-completed',
    status: 'completed', output: 'old work', startedAt: '2026-01-01T00:00:00Z' };
  const recovered = { ...old, runId: 'recovered-session', status: 'recovering', sessionId: 'exact-session',
    logPath: '/private/log', output: 'replayed output', startedAt: '2026-01-02T00:00:00Z' };
  hooks.updateCopilotRun(recovered);
  hooks.updateCopilotRun(old);
  assert.match(hooks.actionButtons(pr).match(/<button[^>]*data-act="triage"[^>]*>/)[0], /running/);
  assert.match(hooks.renderDetail(pr), /exact-session/);
  assert.match(hooks.renderDetail(pr), /data-run-id="old-completed"/);
  assert.doesNotMatch(elements.get('#runShelf').innerHTML, /old-completed/);
  hooks.setSnapshot({ ...hooks.S.snap, prs: [] });
  await hooks.onAction('toggle', {}, pr.key);
  assert.match(elements.get('#runShelf').innerHTML, /old-completed/);
  assert.match(elements.get('#runShelf').innerHTML, /recovered-session/);
  hooks.updateCopilotRun({ ...recovered, status: 'needs-attention', message: 'Session unavailable' });
  hooks.updateCopilotRun({ ...recovered, status: 'running', message: 'Reattached authoritative live state' });
  assert.equal(hooks.runForPr(pr.key).status, 'running');
  hooks.updateCopilotRun({ ...recovered, status: 'needs-attention', message: 'Session unavailable' });
  assert.match(hooks.renderDetail(pr), /data-act="cancelRun"[^>]*>/);
  window.MockHost.rpc = async (method, p) => {
    assert.equal(method, 'cancelCopilot'); assert.equal(p.runId, recovered.runId);
    return { ...recovered, status: 'cancelled' };
  };
  await hooks.onAction('cancelRun', { dataset: { runId: recovered.runId } }, pr.key);
  assert.equal(hooks.runForPr(pr.key).status, 'cancelled');
  assert.equal(hooks.S.runs.get(old.runId).status, 'completed');
});

test('failed-build log tails with equal build and log ids stay separate per organization and project', async () => {
  const { hooks, pr, window } = setup();
  const fb = { buildId: 123, name: 'CI', url: 'u', tasks: [{ name: 'Task', path: '', result: 'failed', logId: 7, issues: [] }] };
  const a = { ...pr, failedBuilds: [fb] };
  const b = { ...pr, key: 'other:123', org: 'fabrikam', failedBuilds: [fb] };
  const c = { ...pr, key: 'third:123', project: 'Other', failedBuilds: [fb] };
  hooks.S.snap.prs = [a, b, c];
  const calls = [];
  window.MockHost.rpc = async (method, p) => {
    calls.push([method, p]);
    if (method === 'logTail') return `tail for ${p.key}`;
  };
  const settle = () => new Promise((resolve) => setImmediate(resolve));
  for (const p of [a, b, c]) {
    assert.ok(!hooks.renderDetail(p).includes('tail for'));
    await settle();
  }
  assert.deepEqual(calls.filter(([m]) => m === 'logTail').map(([, p]) => [p.key, p.buildId, p.logId]),
    [[a.key, 123, 7], [b.key, 123, 7], [c.key, 123, 7]]);
  for (const p of [a, b, c]) {
    const html = hooks.renderDetail(p);
    assert.ok(html.includes(`tail for ${p.key}`));
    assert.equal((html.match(/tail for/g) || []).length, 1);
    const log = html.match(/data-act="copyLog" data-log="([^"]+)"/)[1];
    await hooks.onAction('copyLog', { dataset: { log } }, p.key);
    assert.equal(calls.at(-1)[1].text, `tail for ${p.key}`);
  }
  assert.equal(calls.filter(([m]) => m === 'logTail').length, 3);
});
