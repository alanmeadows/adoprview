'use strict';

const investigationDrafts = new Map();
let investigationKey = null;
let investigationReturnFocus = null;
let investigationPending = false;
const investigationActive = r => !!r && ['preparing', 'running', 'cancelling', 'posting'].includes(r.status);

function updateInvestigation(r) {
  if (!r || typeof r.key !== 'string' || typeof r.conversationId !== 'string' || typeof r.requestId !== 'string' ||
      !Number.isSafeInteger(r.sequence) || !Array.isArray(r.turns) || !Array.isArray(r.evidence) || !Array.isArray(r.coverage) ||
      !['preparing', 'running', 'cancelling', 'posting', 'completed', 'failed', 'cancelled'].includes(r.status))
    throw new Error('Copilot returned an invalid investigation state.');
  const old = S.investigations.get(r.key);
  if (old && r.sequence <= old.sequence) return;
  S.investigations.set(r.key, r);
  const activity = [...S.investigations.values()].find(x => investigationActive(x));
  $('#openInvestigationActivity').hidden = !activity;
  $('#openInvestigationActivity').dataset.key = activity?.key || '';
  const draft = investigationDrafts.get(r.key);
  if (draft?.sentRequest === r.requestId && r.status === 'completed') {
    if (draft.question.trim() === draft.sentQuestion) {
      draft.question = '';
      if (investigationKey === r.key) $('#investigationQuestion').value = '';
    }
    draft.sentRequest = null;
  }
  if (investigationKey === r.key) renderInvestigation();
}

function openInvestigation(key, kind = 'question') {
  if (!canExplain(key) && !S.investigations.has(key)) return;
  investigationReturnFocus = document.activeElement;
  investigationKey = key;
  const draft = investigationDrafts.get(key) || { kind, question: '' };
  draft.kind = kind;
  investigationDrafts.set(key, draft);
  const p = findPr(key) || S.investigations.get(key)?.revision;
  $('#investigationTitle').textContent = `Investigate ${p?.repo || 'PR'} !${p?.id || ''}`;
  $('#investigationQuestion').value = draft.question;
  $('#investigationKind').value = draft.kind;
  $('#investigationError').textContent = '';
  renderInvestigation();
  const dialog = $('#investigationDialog');
  if (!dialog.open) dialog.showModal();
  $('#investigationQuestion').focus();
}

function renderInvestigation() {
  const key = investigationKey;
  if (!key) return;
  const r = S.investigations.get(key);
  const busy = investigationPending || investigationActive(r);
  $('#investigationStatus').textContent = r?.message || 'Ask about the code or enter a concrete review lens. Preparing source uses your configured WSL checkout. Generation is read-only.';
  $('#investigationStatus').classList.toggle('error', r?.status === 'failed');
  $('#investigationSource').textContent = r?.revision
    ? `${explanationRevision(r)}\n${r.worktree || ''}`
    : 'No source worktree captured yet. No repository code, checkout filters or model shell will run.';
  const transcript = $('#investigationTranscript');
  const bottom = transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight < 40;
  const oldCount = transcript.dataset.count;
  transcript.innerHTML = (r?.turns || []).map((turn, index) => {
    const last = index === r.turns.length - 1;
    const findings = turn.findings.map(f => {
      const outcome = turn.outcomes.find(o => o.findingId === f.id);
      const posted = outcome?.status === 'posted';
      const selectable = last && turn.kind === 'lens' && r.status === 'completed' && !outcome;
      return `<div class="lens-finding">
        <label><input type="checkbox" data-lens-finding="${esc(f.id)}" ${selectable ? '' : 'disabled'}>
          <strong>${esc(f.title)}</strong></label>
        <p class="small">${esc(f.path)}:${esc(f.line)} · pinned source ${esc(r.revision.sourceCommit.slice(0, 12))}</p>
        <div class="investigation-question">${esc(f.body)}</div>
        <pre>${esc(f.quote)}</pre>
        ${outcome ? `<p class="${posted ? '' : 'error'}">${esc(outcome.message)}
          ${posted && /^https:\/\/dev\.azure\.com\//i.test(outcome.url || '') ? `<button type="button" class="mini" data-investigation-url="${esc(outcome.url)}">Open confirmed comment</button>` : ''}</p>` : ''}
      </div>`;
    }).join('');
    return `<article class="investigation-turn"><h3>${turn.kind === 'lens' ? 'Review lens' : 'Question'}</h3>
      <p class="investigation-question">${esc(turn.question)}</p>
      <div class="md">${explanationBody({ output: turn.answer, status: 'completed' })}</div>
      ${turn.kind === 'lens' && !turn.findings.length ? '<p class="small">No findings established in the inspected scope. No comments posted; this is not proof the PR is bug-free.</p>' : ''}
      ${findings}
      ${last && turn.kind === 'lens' && turn.findings.some(f => !turn.outcomes.some(o => o.findingId === f.id)) ?
        `<button type="button" class="btn" id="postLensFindings" data-turn-id="${esc(turn.id)}" ${busy || r.status !== 'completed' ? 'disabled' : ''}>Post selected findings...</button>
        <p class="small">Only selected findings are posted, after confirmation and a fresh revision check. Nothing is selected by default.</p>` : ''}
    </article>`;
  }).join('');
  transcript.dataset.count = String(r?.turns.length || 0);
  if (oldCount !== transcript.dataset.count || bottom) transcript.scrollTop = transcript.scrollHeight;
  $('#investigationEvidence').innerHTML = [...(r?.evidence || []), ...(r?.coverage || [])].map(x => `<p>${esc(x)}</p>`).join('');
  $('#investigationSend').disabled = busy || !canExplain(key);
  $('#investigationQuestion').disabled = busy;
  $('#investigationKind').disabled = busy;
  $('#investigationReset').disabled = busy || !r;
  $('#investigationCancel').hidden = !investigationActive(r) || r.status === 'posting';
  $('#investigationCancel').disabled = investigationPending || r?.status === 'cancelling';
}

async function investigationAction(action) {
  if (!investigationKey || investigationPending) return;
  const key = investigationKey, r = S.investigations.get(key);
  const identity = { key, conversationId: r?.conversationId || '', requestId: r?.requestId || '' };
  const draft = investigationDrafts.get(key);
  let method, payload;
  if (action === 'send') {
    const question = $('#investigationQuestion').value.trim();
    if (!question) { $('#investigationQuestion').focus(); return; }
    method = 'askPr'; payload = { ...identity, kind: $('#investigationKind').value, question };
  } else if (action === 'cancel') {
    method = 'cancelInvestigation'; payload = identity;
  } else if (action === 'reset') {
    if (!confirm('Start a new conversation with fresh PR revisions? The current conversation will be cleared and its owned source worktree closed.')) return;
    method = 'resetInvestigation'; payload = identity;
  } else if (action === 'post') {
    const findingIds = [...document.querySelectorAll('#investigationTranscript [data-lens-finding]:checked')].map(e => e.dataset.lensFinding);
    if (!findingIds.length) { $('#investigationError').textContent = 'Select at least one finding. No comments were posted.'; return; }
    if (!confirm(`Post exactly ${findingIds.length} selected finding(s) as ADO comments on ${findPr(key)?.repo || 'this PR'} !${findPr(key)?.id || ''}? Unselected findings will not be posted.`)) return;
    method = 'postLensFindings'; payload = { ...identity, requestId: $('#postLensFindings').dataset.turnId, findingIds, confirmed: true };
  } else return;
  investigationPending = true;
  $('#investigationError').textContent = '';
  renderInvestigation();
  try {
    const result = await rpc(method, payload);
    updateInvestigation(result);
    if (method === 'askPr') {
      draft.sentRequest = result.requestId;
      draft.sentQuestion = payload.question;
    }
  } catch (e) {
    if (investigationKey === key) $('#investigationError').textContent = e.message || String(e);
    else toast(e.message || String(e), 'error');
  } finally {
    investigationPending = false;
    renderInvestigation();
  }
}

function wireInvestigation() {
  const dialog = $('#investigationDialog');
  $('#investigationClose').addEventListener('click', () => dialog.close());
  $('#openInvestigationActivity').addEventListener('click', e => openInvestigation(e.currentTarget.dataset.key));
  dialog.addEventListener('close', () => {
    if (dialog.open) return;
    investigationKey = null;
    if (investigationReturnFocus?.isConnected) investigationReturnFocus.focus();
  });
  $('#investigationForm').addEventListener('submit', e => { e.preventDefault(); void investigationAction('send'); });
  $('#investigationQuestion').addEventListener('input', e => { if (investigationKey) investigationDrafts.get(investigationKey).question = e.target.value; });
  $('#investigationKind').addEventListener('change', e => { if (investigationKey) investigationDrafts.get(investigationKey).kind = e.target.value; });
  $('#investigationReset').addEventListener('click', () => void investigationAction('reset'));
  $('#investigationCancel').addEventListener('click', () => void investigationAction('cancel'));
  $('#investigationTranscript').addEventListener('click', e => {
    if (e.target.closest('#postLensFindings')) void investigationAction('post');
    const button = e.target.closest('[data-investigation-url]');
    if (button) void rpc('openUrl', { url: button.dataset.investigationUrl }).catch(e => toast(e.message || String(e), 'error'));
  });
}
