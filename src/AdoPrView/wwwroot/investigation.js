'use strict';

const investigationDrafts = new Map();
const explanationArtifacts = new Map();
const investigationPending = new Set();
let investigationKey = null;
const investigationActive = r => !!r && ['preparing', 'running', 'cancelling', 'posting'].includes(r.status);

function assistanceDraft(key) {
  if (!investigationDrafts.has(key)) investigationDrafts.set(key, {
    kind: 'explain', question: '', focus: '', reference: '', attachedId: '', selected: '',
    closed: false, selections: new Map(), positions: new Map(), error: '', sectionIndex: null,
  });
  return investigationDrafts.get(key);
}

function assistanceCard(key) {
  return [...document.querySelectorAll('.explanation')].find(c => c.dataset.explainKey === key);
}

function retainExplanation(r) {
  if (r.status !== 'completed' || !r.revision || !r.output) return;
  const artifacts = explanationArtifacts.get(r.key) || new Map();
  artifacts.set(r.requestId, r);
  while (artifacts.size > 12) artifacts.delete(artifacts.keys().next().value);
  explanationArtifacts.set(r.key, artifacts);
}

function selectedExplanation(key) {
  const d = assistanceDraft(key), current = S.explanations.get(key);
  if (d.selected.startsWith('explain:')) {
    const id = d.selected.slice(8);
    return explanationArtifacts.get(key)?.get(id) || (current?.requestId === id ? current : null);
  }
  return current;
}

function assistanceResultOptions(key) {
  const current = S.explanations.get(key);
  const artifacts = [...(explanationArtifacts.get(key)?.values() || [])].map(r => r.requestId === current?.requestId ? current : r);
  if (current && !artifacts.some(x => x.requestId === current.requestId)) artifacts.push(current);
  return [
    ...artifacts.map(r => ({ id: `explain:${r.requestId}`, label: `Explanation ${r.requestId.slice(0, 12)} · ${r.status}`, explanation: r })),
    ...(S.investigations.get(key)?.turns || []).map(t => ({
      id: `turn:${t.id}`, label: `${t.kind === 'lens' ? 'Review' : 'Answer'} ${t.id.slice(0, 12)}`, turn: t,
    })),
  ];
}

function assistanceSelection(key) {
  const d = assistanceDraft(key), results = assistanceResultOptions(key);
  return results.find(x => x.id === d.selected) || (!d.selected ? results.at(-1) : null);
}

function attachedExplanation(key, id) {
  const context = S.investigations.get(key)?.explanationContexts?.find(c => c.requestId === id);
  return context && (context.sectionIndex ?? null) === assistanceDraft(key).sectionIndex ? context :
    explanationArtifacts.get(key)?.get(id) || context;
}

function renderContextReceipt(r, turn) {
  const ids = turn.explanationRequestIds || [], urls = turn.referencePrUrls || [];
  const contexts = (r.explanationContexts || []).filter(c => ids.includes(c.requestId));
  const references = (r.referencePrContexts || []).filter(c => urls.includes(c.url));
  return `<p class="small">Target PR: ${esc(r.revision?.repo || r.key)} !${esc(r.revision?.id || '')} · ${esc(explanationRevision(r))}. Posting is only to this target.</p>
    ${contexts.length ? `<p class="small">Explanation context used for this result: ${contexts.map(c => `${esc(c.requestId.slice(0, 12))} · source ${esc(c.revision.sourceCommit.slice(0, 12))} · ${esc(c.sectionTitle || 'Entire explanation')}`).join('; ')}.</p>
      ${contexts.map(c => `<details><summary>View actual imported explanation ${esc(c.requestId.slice(0, 12))}</summary><p class="small">${esc(c.receipt || 'Entire completed explanation retained.')}</p>
        <div class="md explain-output">${explanationBody({ output: c.output, status: 'completed' })}</div>
        ${(c.coverage || []).map(x => `<p class="small">${esc(x)}</p>`).join('')}</details>`).join('')}` : ''}
    ${references.map(c => `<details class="reference-context"><summary>Reference PR: ${esc(c.revision.repo)} !${esc(c.revision.id)} · source ${esc(c.revision.sourceCommit.slice(0, 12))}</summary>
      <p class="small">${esc(c.url)}<br>${esc(explanationRevision(c))}</p>
      <pre class="reference-output" tabindex="0" aria-label="Pinned reference PR source diff">${esc(c.output)}</pre>
      <p class="small">Pinned reference comparison evidence; separate from the target. Inspection/coverage reflects this conversation, not proof of additional reads by this historical answer.</p>
      ${(c.evidence || []).concat(c.coverage || []).map(x => `<p class="small">${esc(x)}</p>`).join('')}</details>`).join('')}`;
}

function privateFindingSelections(turn, ids) {
  return new Set(turn.findings.filter(f => ids.has(f.id) && !turn.outcomes.some(o => o.findingId === f.id)).map(f => f.id));
}

function renderInvestigationTurn(key, turn) {
  const r = S.investigations.get(key), d = assistanceDraft(key);
  const latest = turn.id === r.turns.at(-1)?.id;
  const eligible = latest && turn.kind === 'lens' && r.status === 'completed';
  const selections = privateFindingSelections(turn, d.selections.get(turn.id) || new Set());
  const busy = investigationPending.has(key) || investigationActive(r);
  const findings = turn.findings.map(f => {
    const outcome = turn.outcomes.find(o => o.findingId === f.id);
    const posted = outcome?.status === 'posted';
    const selectable = eligible && !busy && !outcome;
    return `<div class="lens-finding">
      <label><input type="checkbox" data-lens-finding="${esc(f.id)}" data-turn-id="${esc(turn.id)}" ${selectable ? '' : 'disabled'} ${selections.has(f.id) && !outcome ? 'checked' : ''}>
        <strong>${esc(f.title)}</strong></label>
      <p class="small">${esc(f.path)}:${esc(f.line)} · pinned source ${esc(r.revision?.sourceCommit?.slice(0, 12) || 'not available')}</p>
      <div class="investigation-question">${esc(f.body)}</div>
      <pre>${esc(f.quote)}</pre>
      ${outcome ? `<p class="${posted ? '' : 'error'}">${esc(outcome.message)}
        ${posted && /^https:\/\/dev\.azure\.com\//i.test(outcome.url || '') ? `<button type="button" class="mini" data-investigation-url="${esc(outcome.url)}">Open confirmed comment</button>` : ''}</p>` : ''}
    </div>`;
  }).join('');
  return `<article class="investigation-turn" data-turn-id="${esc(turn.id)}"><h4>${turn.kind === 'lens' ? 'Focused review' : 'Answer'} · ${esc(turn.id.slice(0, 12))}</h4>
    ${renderContextReceipt(r, turn)}
    ${turn.kind === 'lens' ? `<p class="investigation-privacy">${turn.outcomes.length ? 'AI findings — see each selected finding for its ADO outcome.' : 'Private AI findings — nothing posted to ADO.'}</p>` : ''}
    <p class="investigation-question">${esc(turn.question)}</p>
    <div class="md">${explanationBody({ output: turn.answer, status: 'completed' })}</div>
    ${findings}
    ${turn.kind === 'lens' && !turn.findings.length ? '<p class="small">No findings established in the inspected scope. This is not proof the PR is bug-free.</p>' : ''}
    ${turn.kind === 'lens' && !eligible ? '<p class="small assistance-notice">Historical preview: only the latest completed review is eligible to post. Generate updated private findings; a later question does not keep an older preview postable.</p>' : ''}
    ${eligible && turn.findings.some(f => !turn.outcomes.some(o => o.findingId === f.id)) ?
      `<button type="button" class="btn" data-investigation-action="post" data-turn-id="${esc(turn.id)}" ${busy || !selections.size ? 'disabled' : ''}>Post ${selections.size} selected finding(s) to ADO...</button>
      <p class="small">Selected findings only, after confirmation and a fresh revision check. Nothing is selected by default.</p>` : ''}
  </article>`;
}

function renderAssistanceContent(key) {
  const d = assistanceDraft(key), r = S.investigations.get(key);
  const result = assistanceSelection(key), explanation = result?.explanation;
  const options = assistanceResultOptions(key);
  const latestExplanation = S.explanations.get(key);
  const busy = investigationPending.has(key) || investigationActive(r);
  const activeExplain = explanationActive(latestExplanation);
  const available = canExplain(key);
  const attached = attachedExplanation(key, d.attachedId);
  const imported = r?.explanationContexts?.find(c => c.requestId === d.attachedId);
  const sectionConflict = !!imported && (imported.sectionIndex ?? null) !== d.sectionIndex;
  const sections = explanationArtifacts.get(key)?.get(d.attachedId)?.explanationSections || [];
  const selected = result?.id || d.selected;
  const inputId = `assistance-question-${encodeURIComponent(key)}`;
  const referenceId = `assistance-reference-${encodeURIComponent(key)}`;
  const combined = investigationQuestion(d);
  const invalid = d.kind === 'lens'
    ? !d.focus.trim() || !d.reference.trim() || combined.length > 4000
    : !d.question.trim() || d.question.length > 4000;
  return `<div class="assistance-tabs" role="group" aria-label="PR assistance intent">
    ${[['explain', 'Explain'], ['question', 'Ask'], ['lens', 'Focused review']].map(([kind, label]) =>
      `<button type="button" class="mini" data-assistance-intent="${kind}" aria-pressed="${d.kind === kind}" ${busy ? 'disabled' : ''}>${label}</button>`).join('')}</div>
    <p class="small">Read-only understanding and private review. Results and drafts last while this app is running; they are not saved across app exit. Fix is a separate code-changing run.</p>
    ${options.length ? `<label class="small">Working result <select data-assistance-result data-inv="result">${options.map(o => `<option value="${esc(o.id)}" ${o.id === selected ? 'selected' : ''}>${esc(o.label)}</option>`).join('')}${d.selected && !result ? '<option selected value="">Selected result expired</option>' : ''}</select></label>` : ''}
    ${isBusy(key, 'explainPr') && !activeExplain ? '<p class="explain-waiting small" role="status"><span class="video-spinner" aria-hidden="true"></span>Requesting explanation...</p>' : ''}
    ${activeExplain && result?.explanation?.requestId !== latestExplanation.requestId ? '<p class="small" role="status">A new explanation is running; the selected result remains unchanged.</p>' : ''}
    ${activeExplain && d.kind !== 'explain' ? `<p class="small" role="status">${esc(latestExplanation.message)} <button class="mini" data-act="cancelExplanation" data-request-id="${esc(latestExplanation.requestId)}" ${latestExplanation.status === 'cancelling' ? 'disabled' : ''}>Cancel Explain</button></p>` : ''}
    ${investigationActive(r) && d.kind === 'explain' ? `<p class="small" role="status">${esc(r.message)}
      ${r.status !== 'posting' ? `<button type="button" class="mini" data-investigation-action="cancel" ${investigationPending.has(key) || r.status === 'cancelling' ? 'disabled' : ''}>Cancel inspection</button>` : 'Posting cannot be cancelled mid-request; wait for its exact outcome.'}</p>` : ''}
    <p class="error" data-inv="error" role="alert">${d.error ? `${esc(d.errorAction || 'Assistance action')}: ${esc(d.error)}` : ''}</p>
    ${d.kind === 'explain' ? `<p><button class="mini" data-act="explainPr" ${activeExplain || isBusy(key, 'explainPr') || !available ? 'disabled' : ''}>${svg('spark')} ${explanationLabel(latestExplanation)}</button>
      <button class="mini" data-act="cancelExplanation" data-request-id="${esc(latestExplanation?.requestId || '')}" ${activeExplain ? '' : 'hidden'} ${latestExplanation?.status === 'cancelling' ? 'disabled' : ''}>Cancel Explain</button></p>` : ''}
    ${explanation ? `<div class="assistance-explanation" data-result-request="${esc(explanation.requestId)}">
      <p class="explain-status small${explanation.status === 'failed' ? ' error' : ''}" role="status">${esc(explanation.message)}</p>
      <p class="explain-revision small">${esc(explanationRevision(explanation))} · result ${esc(explanation.requestId.slice(0, 12))}</p>
      <p class="explain-waiting small" role="status" ${explanationActive(explanation) ? '' : 'hidden'}><span class="video-spinner" aria-hidden="true"></span>Waiting for the completed explanation...</p>
      <div class="explain-output md" data-explanation-result="${explanation.status === 'completed' ? esc(explanation.requestId) : ''}" tabindex="0" aria-label="PR explanation" ${explanation.status === 'completed' && explanation.output ? '' : 'hidden'}>${explanationBody(explanation)}</div>
      <details class="explain-details"><summary>Evidence and coverage</summary><p class="small">AI-generated; verify against the code. This does not start Fix or change ADO.</p>
        <div class="explain-evidence">${(explanation.evidence || []).map(x => `<p class="small">${esc(x)}</p>`).join('')}</div>
        <div class="explain-coverage">${(explanation.coverage || []).map(x => `<p class="small">${esc(x)}</p>`).join('')}</div></details>
      <p><button type="button" class="mini" data-assistance-attach="${esc(explanation.requestId)}" ${explanation.status !== 'completed' || !explanation.revision || busy ? 'disabled' : ''}>Ask about this explanation</button></p>
      ${explanation.status !== 'completed' ? '<p class="small">Only a completed explanation is displayed and available as a context attachment.</p>' : ''}
    </div>` : result?.turn ? `<div class="explain-output investigation-transcript" data-inv="transcript" tabindex="0" aria-label="Selected assistance result">${renderInvestigationTurn(key, result.turn)}</div>` : d.selected ? '<p class="error">This selected result is no longer available. Select an available result; no substitute context has been attached.</p>' : ''}
    ${d.kind !== 'explain' ? `<form data-inv="form">
      <div class="assistance-context small">
        ${d.attachedId ? `<strong>Using explanation ${esc(d.attachedId.slice(0, 12))}</strong>
          ${attached ? ` · ${esc(explanationRevision(attached))}` : '<span class="error"> · unavailable; submission is blocked until you explicitly choose another context.</span>'}
          ${sections.length ? `<label>Context package <select data-assistance-section data-inv="section" ${busy ? 'disabled' : ''}><option value="" ${d.sectionIndex === null ? 'selected' : ''}>Entire explanation</option>
            ${sections.map(s => `<option value="${s.index}" ${d.sectionIndex === s.index ? 'selected' : ''}>${esc(s.title)} · ${s.characters.toLocaleString()} characters${s.includesArm ? ' + complete ARM block' : ''}</option>`).join('')}</select></label>
            <p>Explicit sections retain their complete heading subtree plus the full host ARM Before/After, diff, risks and original coverage. Character estimates exclude JSON/history overhead; a package may still exceed the safe limit.</p>` : ''}
          ${sectionConflict ? '<p class="error">This explanation already has a different package in the conversation. Start a new conversation before changing it; prior context will not be silently replaced.</p>' : ''}
          <details><summary>View attached context</summary>${attached ? `<p>${esc(imported && !sectionConflict ? imported.receipt || 'Entire completed explanation retained.' : d.sectionIndex === null ? 'Entire completed explanation will be resolved by server-owned reference.' : 'Full source preview below. The server resolves the chosen heading and complete ARM block; the accepted result shows the actual package and omissions.')}</p>
            <div class="md explain-output">${explanationBody({ ...attached, status: 'completed' })}</div><p>Source identity, evidence and coverage are retained. Later completed investigation turns are also replayed.</p>` : '<p>This explanation expired or is unavailable. It will not be silently replaced.</p>'}</details>
          <button class="mini" type="button" data-investigation-action="detach" ${busy ? 'disabled' : ''}>Ask/review without this explanation</button>`
        : '<p>No explanation attached. This request uses the investigation’s pinned source and completed history; a shared PR label is not an Explain handoff.</p>'}
        ${r?.explanationContexts?.length ? `<p>Conversation retains ${r.explanationContexts.map(c => esc(c.requestId.slice(0, 12))).join(', ')}. Changing the primary selection does not remove earlier context. Start a new conversation to clear its history and attachments.</p>` : ''}
      </div>
      <p data-inv="status" class="small${r?.status === 'failed' ? ' error' : ''}" role="status" aria-live="polite">${esc(r?.message || 'Ask/review use authenticated Azure DevOps source and the bundled Windows Copilot runtime. No WSL or local checkout is needed. Enter a question or a concrete focus and reference; no comments are posted by generation.')}</p>
      <p data-inv="source" class="small">${esc(r?.revision ? explanationRevision(r) : 'No investigation source captured yet.')}</p>
      <label for="${inputId}">${d.kind === 'lens' ? 'Review focus' : d.attachedId ? 'Question about this explanation' : 'Question or follow-up'}</label>
      <textarea id="${inputId}" data-inv="question" rows="3" maxlength="4000" required ${busy ? 'disabled' : ''}>${esc(d.kind === 'lens' ? d.focus : d.question)}</textarea>
      ${d.kind === 'lens' ? `<label for="${referenceId}">Reference: rule quote, repository path or Azure DevOps PR URL</label>
        <textarea id="${referenceId}" data-inv="reference" rows="3" maxlength="4000" required ${busy ? 'disabled' : ''}>${esc(d.reference)}</textarea>
        <p class="small" data-inv="budget">Combined focus and reference: ${combined.length} / 4,000 characters. Supported canonical ADO PR URLs are inspected read-only with separate source identity; other URLs return an explicit error. A supplied quote is human context, not independently inspected policy.</p>` : ''}
      ${result?.turn?.kind === 'lens' && r?.turns.at(-1)?.id === result.turn.id && d.kind === 'question' ? '<p class="small assistance-notice">A new completed question will make this review historical and no longer postable. Generate an updated review before posting.</p>' : ''}
      <div class="investigation-actions">
        <button class="btn primary" data-inv="send" type="submit" ${busy || !available || invalid || sectionConflict || d.attachedId && !attached ? 'disabled' : ''}>${d.kind === 'lens' ? 'Generate private findings' : 'Ask'}</button>
        <button class="btn" data-investigation-action="cancel" type="button" ${investigationActive(r) && r.status !== 'posting' ? '' : 'hidden'} ${investigationPending.has(key) || r?.status === 'cancelling' ? 'disabled' : ''}>Cancel inspection</button>
        <button class="btn" data-investigation-action="reset" type="button" ${busy || !r ? 'disabled' : ''}>New conversation</button>
      </div>
      ${r?.status === 'posting' ? '<p class="small">Posting cannot be cancelled mid-request. Close/reopen does not withdraw the write; wait for its exact outcome.</p>' : ''}
    </form>` : ''}
    ${r && d.kind !== 'explain' ? `<details><summary>Investigation evidence and unread areas</summary><div class="small">${[...r.evidence, ...r.coverage].map(x => `<p>${esc(x)}</p>`).join('')}</div></details>` : ''}`;
}

function investigationQuestion(d) {
  return d.kind === 'lens' ? `Review focus:\n${d.focus.trim()}\n\nReference quote or repository path:\n${d.reference.trim()}` : d.question.trim();
}

function confirmLensPost(key, r, selected) {
  const dialog = $('#assistancePostDialog');
  if (dialog.open) return Promise.resolve(false);
  const focus = document.activeElement, p = findPr(key) || r.revision;
  $('#assistancePostPreview').innerHTML = `<p>Post exactly ${selected.length} selected finding(s) on ${esc(p?.repo || key)} !${esc(p?.id || '')}?</p>
    <p class="small">${esc(explanationRevision(r))}<br>Unselected findings will not be posted.</p>
    ${selected.map(f => `<article class="lens-finding"><strong>${esc(f.title)}</strong>
      <p>${esc(f.path)}:${esc(f.line)}</p><p>Complete outgoing comment, including the review focus/reference and attribution:</p>
      <pre>${esc(f.outgoingComment)}</pre><details><summary>Inspected source excerpt (not added to comment)</summary><pre>${esc(f.quote)}</pre></details></article>`).join('')}
    <p>These become visible ADO comments on the target PR, not the reference PR. A fresh revision check runs before posting.</p>`;
  dialog.returnValue = '';
  return new Promise(resolve => {
    dialog.addEventListener('close', () => {
      const confirmed = dialog.returnValue === 'post';
      const returnFocus = focus?.isConnected ? focus :
        [...(assistanceCard(key)?.querySelectorAll('[data-investigation-action="post"]') || [])].find(b => b.dataset.turnId === r.requestId);
      returnFocus?.focus({ preventScroll: true });
      resolve(confirmed);
    }, { once: true });
    dialog.showModal();
  });
}

function rememberAssistancePosition(key) {
  const card = assistanceCard(key), d = assistanceDraft(key);
  const output = card?.querySelector('.assistance-content > .assistance-explanation > .explain-output, .assistance-content > .investigation-transcript');
  if (output) {
    const id = output.parentElement.dataset.resultRequest
      ? `explain:${output.parentElement.dataset.resultRequest}` : `turn:${output.querySelector('[data-turn-id]')?.dataset.turnId || ''}`;
    d.positions.set(id, { top: output.scrollTop, left: output.scrollLeft });
  }
}

function renderInvestigation(key = investigationKey) {
  if (!key) return;
  const card = assistanceCard(key), content = card?.querySelector('.assistance-content');
  if (!content) return;
  const outputs = captureExplanationOutputs();
  rememberAssistancePosition(key);
  const focused = content.contains(document.activeElement) ? document.activeElement : null;
  const role = focused?.dataset.inv;
  const start = focused?.selectionStart, end = focused?.selectionEnd;
  const openDetails = [...content.querySelectorAll('details')].filter(x => x.open).map(x => x.querySelector('summary')?.textContent);
  content.innerHTML = renderAssistanceContent(key);
  restoreExplanationOutputs(outputs);
  for (const details of content.querySelectorAll('details')) if (openDetails.includes(details.querySelector('summary')?.textContent)) details.open = true;
  const position = assistanceDraft(key).positions.get(assistanceSelection(key)?.id || assistanceDraft(key).selected);
  const output = content.querySelector(':scope > .assistance-explanation > .explain-output, :scope > .investigation-transcript');
  if (output && position) { output.scrollTop = position.top; output.scrollLeft = position.left; }
  if (role) {
    const next = content.querySelector(`[data-inv="${role}"]`);
    next?.focus({ preventScroll: true });
    if (typeof start === 'number' && next?.setSelectionRange) next.setSelectionRange(start, end);
  }
}

function captureExplanationOutputs() {
  const outputs = [...document.querySelectorAll('.explain-output[data-explanation-result]')]
    .filter(el => el.dataset.explanationResult)
    .map(el => ({ el, key: el.closest('.explanation').dataset.explainKey,
      id: el.dataset.explanationResult, html: el.innerHTML, top: el.scrollTop, left: el.scrollLeft }));
  const selection = window.getSelection();
  const selected = selection?.isCollapsed === false && outputs.some(({ el }) =>
    el.contains(selection.anchorNode) && el.contains(selection.focusNode));
  return { outputs, focus: document.activeElement, selection: selected ? {
    anchor: selection.anchorNode, anchorOffset: selection.anchorOffset,
    focus: selection.focusNode, focusOffset: selection.focusOffset,
  } : null };
}

function restoreExplanationOutputs(saved) {
  for (const placeholder of document.querySelectorAll('.explain-output[data-explanation-result]')) {
    const existing = saved.outputs.find(x => x.key === placeholder.closest('.explanation').dataset.explainKey &&
      x.id === placeholder.dataset.explanationResult && x.html === placeholder.innerHTML);
    if (!existing) continue;
    if (existing.el !== placeholder) placeholder.replaceWith(existing.el);
    existing.el.scrollTop = existing.top;
    existing.el.scrollLeft = existing.left;
    if (saved.focus === existing.el) existing.el.focus({ preventScroll: true });
  }
  restoreRunOutputSelection(saved.selection);
}

function updateInvestigation(r) {
  if (!r || typeof r.key !== 'string' || typeof r.conversationId !== 'string' || typeof r.requestId !== 'string' ||
      !Number.isSafeInteger(r.sequence) || !Array.isArray(r.turns) || !Array.isArray(r.evidence) || !Array.isArray(r.coverage) ||
      !['preparing', 'running', 'cancelling', 'posting', 'completed', 'failed', 'cancelled'].includes(r.status))
    throw new Error('Copilot returned an invalid investigation state.');
  const old = S.investigations.get(r.key);
  if (old && r.sequence <= old.sequence) return;
  const d = assistanceDraft(r.key);
  if (old?.conversationId !== r.conversationId) {
    d.selections.clear();
    if (d.selected.startsWith('turn:')) d.selected = '';
  }
  S.investigations.set(r.key, r);
  if (!d.attachedId && r.turns.at(-1)?.explanationRequestId) {
    d.attachedId = r.turns.at(-1).explanationRequestId;
    d.sectionIndex = r.turns.at(-1).explanationSectionIndex ?? null;
  }
  const activity = [...S.investigations.values()].find(investigationActive);
  $('#openInvestigationActivity').hidden = !activity;
  $('#openInvestigationActivity').dataset.key = activity?.key || '';
  if (r.status === 'completed' && r.turns.length && r.turns.at(-1).id !== old?.turns.at(-1)?.id) {
    d.selected = `turn:${r.turns.at(-1).id}`;
    if (d.sentRequest === r.requestId) {
      if (investigationQuestion(d) === d.sentQuestion) {
        if (d.kind === 'lens') d.focus = '';
        else d.question = '';
      }
      d.sentRequest = null;
    }
  }
  renderExplanationShelf();
  renderInvestigation(r.key);
}

function openInvestigation(key, kind = 'question', attachedId) {
  if (!canExplain(key) && !S.investigations.has(key)) return;
  const d = assistanceDraft(key);
  rememberAssistancePosition(key);
  d.kind = kind; d.closed = false;
  if (attachedId !== undefined) {
    d.attachedId = attachedId;
    d.sectionIndex = S.investigations.get(key)?.explanationContexts?.find(c => c.requestId === attachedId)?.sectionIndex ?? null;
  }
  investigationKey = key;
  const p = findPr(key);
  if (p) S.explanationPrs.set(key, { id: p.id, repo: p.repo, title: p.title });
  S.expanded.add(key);
  if (!assistanceCard(key)) renderRows();
  else renderInvestigation(key);
  const card = assistanceCard(key);
  card?.querySelector('.assistance-body')?.removeAttribute('hidden');
  card?.querySelector('[data-assistance-close]')?.setAttribute('aria-expanded', 'true');
  if (card?.querySelector('[data-assistance-close]')) card.querySelector('[data-assistance-close]').textContent = 'Close assistance';
  card?.scrollIntoView({ block: 'nearest' });
  card?.querySelector('[data-inv="question"]')?.focus({ preventScroll: true });
}

async function investigationAction(action, key = investigationKey, turnId) {
  if (!key || investigationPending.has(key)) return;
  const r = S.investigations.get(key), d = assistanceDraft(key);
  const identity = { key, conversationId: r?.conversationId || '', requestId: r?.requestId || '' };
  let method, payload;
  if (action === 'send') {
    const question = investigationQuestion(d);
    if (!question || question.length > 4000 || d.kind === 'lens' && (!d.focus.trim() || !d.reference.trim())) {
      d.error = 'Enter a question or a concrete focus and reference within the combined 4,000-character limit.';
      renderInvestigation(key); return;
    }
    if (d.attachedId && !attachedExplanation(key, d.attachedId)) {
      d.error = 'The selected explanation is unavailable. Choose an available completed result or explicitly remove the attachment.';
      renderInvestigation(key); return;
    }
    method = 'askPr'; payload = { ...identity, kind: d.kind, question, explanationRequestId: d.attachedId || null, explanationSectionIndex: d.attachedId ? d.sectionIndex : null };
    d.sentQuestion = question;
  } else if (action === 'cancel') {
    method = 'cancelInvestigation'; payload = identity;
  } else if (action === 'reset') {
    if (!confirm('Start a new conversation with fresh PR revisions? Completed investigation history and its pinned source attachments will be cleared. Explain results remain readable.')) return false;
    method = 'resetInvestigation'; payload = identity;
  } else if (action === 'post') {
    const turn = r?.turns.find(t => t.id === turnId);
    const findingIds = [...(d.selections.get(turnId) || [])].filter(id => !turn?.outcomes.some(o => o.findingId === id));
    if (!turn || turn !== r.turns.at(-1) || turn.kind !== 'lens' || r.status !== 'completed' || !findingIds.length) {
      d.error = 'Select findings from the latest completed review. Nothing was posted.'; renderInvestigation(key); return;
    }
    const selected = turn.findings.filter(f => findingIds.includes(f.id));
    if (selected.some(f => typeof f.outgoingComment !== 'string' || !f.outgoingComment)) {
      d.error = 'The complete outgoing comment preview is unavailable. Generate a new private review before posting.';
      renderInvestigation(key); return;
    }
    if (!await confirmLensPost(key, r, selected)) return;
    method = 'postLensFindings'; payload = { ...identity, requestId: turnId, findingIds, confirmed: true };
  } else if (action === 'detach') {
    if (r && !await investigationAction('reset', key)) return;
    d.attachedId = ''; d.sectionIndex = null; renderInvestigation(key); return;
  } else return;
  investigationPending.add(key); d.error = '';
  d.errorAction = method === 'askPr' ? d.kind === 'lens' ? 'Focused review' : 'Ask' : method === 'postLensFindings' ? 'ADO posting' : 'Conversation';
  renderInvestigation(key);
  try {
    const result = await rpc(method, payload);
    if (method === 'askPr') d.sentRequest = result.requestId;
    if (method === 'resetInvestigation') {
      if (result.status !== 'completed' || result.conversationId === identity.conversationId || result.turns?.length !== 0) {
        updateInvestigation(result);
        d.error = result.message || 'The conversation reset was not confirmed. Existing context was retained.';
        return false;
      }
      d.attachedId = ''; d.sectionIndex = null; d.selected = '';
    }
    updateInvestigation(result);
    return true;
  } catch (e) {
    d.error = e.message || String(e);
    return false;
  } finally {
    investigationPending.delete(key); renderInvestigation(key);
  }
}

function wireInvestigation() {
  $('#openInvestigationActivity').addEventListener('click', e => openInvestigation(e.currentTarget.dataset.key));
  document.addEventListener('submit', e => {
    if (!e.target.matches('[data-inv="form"]')) return;
    e.preventDefault();
    void investigationAction('send', e.target.closest('.explanation').dataset.explainKey);
  });
  document.addEventListener('input', e => {
    if (!['question', 'reference'].includes(e.target.dataset.inv)) return;
    const key = e.target.closest('.explanation').dataset.explainKey, d = assistanceDraft(key);
    if (e.target.dataset.inv === 'reference') d.reference = e.target.value;
    else if (d.kind === 'lens') d.focus = e.target.value;
    else d.question = e.target.value;
    const card = assistanceCard(key), combined = investigationQuestion(d);
    const invalid = d.kind === 'lens'
      ? !d.focus.trim() || !d.reference.trim() || combined.length > 4000
      : !d.question.trim() || combined.length > 4000;
    const send = card.querySelector('[data-inv="send"]');
    const imported = S.investigations.get(key)?.explanationContexts?.find(c => c.requestId === d.attachedId);
    send.disabled = invalid || investigationPending.has(key) || investigationActive(S.investigations.get(key)) || !canExplain(key) || d.attachedId && !attachedExplanation(key, d.attachedId) || imported && (imported.sectionIndex ?? null) !== d.sectionIndex;
    const budget = card.querySelector('[data-inv="budget"]');
    if (budget) budget.textContent = `Combined focus and reference: ${combined.length} / 4,000 characters. Supported canonical ADO PR URLs are read-only references; other URLs return an explicit error. Human text is context, not inspected policy.`;
  });
  document.addEventListener('change', e => {
    const card = e.target.closest('.explanation'); if (!card) return;
    const key = card.dataset.explainKey, d = assistanceDraft(key);
    if (e.target.matches('[data-assistance-result]')) {
      rememberAssistancePosition(key);
      d.selected = e.target.value;
      const selected = assistanceSelection(key);
      d.kind = selected?.turn ? selected.turn.kind : 'explain';
      renderInvestigation(key);
    } else if (e.target.matches('[data-assistance-section]')) {
      d.sectionIndex = e.target.value === '' ? null : Number(e.target.value);
      renderInvestigation(key);
    } else if (e.target.matches('[data-lens-finding]')) {
      const turn = S.investigations.get(key)?.turns.find(t => t.id === e.target.dataset.turnId);
      if (!turn) return;
      const ids = privateFindingSelections(turn, d.selections.get(turn.id) || new Set());
      if (e.target.checked) ids.add(e.target.dataset.lensFinding); else ids.delete(e.target.dataset.lensFinding);
      d.selections.set(e.target.dataset.turnId, ids);
      const button = card.querySelector('[data-investigation-action="post"]');
      if (button) { button.textContent = `Post ${ids.size} selected finding(s) to ADO...`; button.disabled = !ids.size; }
    }
  });
  document.addEventListener('click', e => {
    const card = e.target.closest('.explanation'); if (!card) return;
    const key = card.dataset.explainKey, d = assistanceDraft(key);
    const intent = e.target.closest('[data-assistance-intent]');
    if (intent) {
      rememberAssistancePosition(key); d.kind = intent.dataset.assistanceIntent; d.closed = false;
      investigationKey = key;
      if (d.kind === 'explain') d.selected = S.explanations.has(key) ? `explain:${S.explanations.get(key).requestId}` : '';
      renderInvestigation(key); card.querySelector('[data-inv="question"]')?.focus(); return;
    }
    const attach = e.target.closest('[data-assistance-attach]');
    if (attach) { openInvestigation(key, 'question', attach.dataset.assistanceAttach); return; }
    const close = e.target.closest('[data-assistance-close]');
    if (close) {
      rememberAssistancePosition(key); d.closed = !d.closed;
      card.querySelector('.assistance-body').hidden = d.closed; close.textContent = d.closed ? 'Reopen assistance' : 'Close assistance';
      close.setAttribute('aria-expanded', String(!d.closed)); renderExplanationShelf(); return;
    }
    const action = e.target.closest('[data-investigation-action]');
    if (action) { void investigationAction(action.dataset.investigationAction, key, action.dataset.turnId); return; }
    const url = e.target.closest('[data-investigation-url]');
    if (url) void rpc('openUrl', { url: url.dataset.investigationUrl }).catch(e => toast(e.message || String(e), 'error'));
  });
}
