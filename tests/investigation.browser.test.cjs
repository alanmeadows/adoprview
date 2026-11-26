const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm, mkdir, writeFile } = require('node:fs/promises');
const { once } = require('node:events');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

test('task-led assistance keeps exact Explain context, private review intent, selected posting and result continuity', { timeout: 90000 }, async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), 'ado-investigation-browser-'));
  const child = spawn(process.env.ROW_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', [
    '--headless=new', '--remote-debugging-port=0', '--no-first-run', '--disable-extensions',
    '--no-default-browser-check', `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  const exited = once(child, 'exit');
  let socket;
  try {
    const url = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(Error('Browser startup timed out')), 15000);
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.stderr.on('data', chunk => {
        const match = chunk.toString().match(/DevTools listening on (ws:\/\/\S+)/);
        if (match) { clearTimeout(timer); resolve(match[1]); }
      });
    });
    socket = new WebSocket(url);
    await once(socket, 'open');
    let id = 0, session;
    const pending = new Map(), errors = [], network = [];
    socket.addEventListener('message', e => {
      const m = JSON.parse(e.data), p = pending.get(m.id);
      if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails);
      if (m.method === 'Network.requestWillBeSent' && /^https?:/.test(m.params.request.url)) network.push(m.params.request.url);
      if (!p) return;
      pending.delete(m.id);
      m.error ? p.reject(Error(JSON.stringify(m.error))) : p.resolve(m.result);
    });
    const send = (method, params = {}, useSession = true) => new Promise((resolve, reject) => {
      const n = ++id;
      pending.set(n, { resolve, reject });
      socket.send(JSON.stringify({ id: n, method, params, ...(useSession && session ? { sessionId: session } : {}) }));
    });
    const target = await send('Target.createTarget', { url: 'about:blank' }, false);
    session = (await send('Target.attachToTarget', { targetId: target.targetId, flatten: true }, false)).sessionId;
    const evaluate = async expression => {
      const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
      if (result.exceptionDetails) throw Error(JSON.stringify(result.exceptionDetails));
      return result.result.value;
    };
    const click = async selector => {
      const point = await evaluate(`(()=>{const e=${selector.startsWith('#') ? 'document' : 'card()'}.querySelector(${JSON.stringify(selector)});if(!e||e.disabled)throw Error('Missing/disabled control '+${JSON.stringify(selector)});e.scrollIntoView({block:'center'});const b=e.getBoundingClientRect();return{x:b.x+b.width/2,y:b.y+b.height/2};})()`);
      await send('Input.dispatchMouseEvent', { type: 'mouseMoved', ...point });
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, ...point });
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, ...point });
      await evaluate(`new Promise(r=>setTimeout(r,30))`);
    };
    const input = async (role, value) => {
      await click(`[data-inv="${role}"]`);
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'a', code: 'KeyA', modifiers: 2, windowsVirtualKeyCode: 65 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'a', code: 'KeyA', modifiers: 2, windowsVirtualKeyCode: 65 });
      await send('Input.insertText', { text: value });
    };
    const select = id => evaluate(`(()=>{const e=card().querySelector('[data-assistance-result]');e.value=${JSON.stringify(id)};e.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    const proof = process.env.ADOPRVIEW_WORKSPACE_PROOF_DIR;
    const captures = [];
    const screenshot = async (name, note, selector) => {
      if (!proof) return;
      await mkdir(proof, { recursive: true });
      await evaluate(`(()=>{const e=${selector ? `document.querySelector(${JSON.stringify(selector)})` : 'card()'};e.style.scrollMarginTop=((document.querySelector('.thead')?.getBoundingClientRect().height||0)+16)+'px';e.scrollIntoView({block:'start'});})()`);
      const result = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      await writeFile(path.join(proof, name + '.png'), Buffer.from(result.data, 'base64'));
      captures.push({ name, note, width: await evaluate('innerWidth'), height: await evaluate('innerHeight') });
    };
    await send('Page.enable'); await send('Runtime.enable'); await send('Network.enable');
    await send('Network.setBlockedURLs', { urls: ['http://*', 'https://*'] });
    await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1200, deviceScaleFactor: 1, mobile: false });
    await send('Page.navigate', { url: pathToFileURL(path.resolve(__dirname, '..', 'src', 'AdoPrView', 'wwwroot', 'index.html')).href + '?mock=1' });
    await evaluate(`new Promise((resolve,reject)=>{const deadline=Date.now()+10000;const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot')):setTimeout(check,20);check();})`);
    await evaluate(`
      window.prA=S.snap.prs.find(p=>canExplain(p.key));window.prB=S.snap.prs.find(p=>p.key!==prA.key&&canExplain(p.key));
      window.calls=[];window.invSequence=0;window.invRequest=0;window.explainSequence=0;window.confirmResult=true;window.confirmations=[];
      window.card=()=>assistanceCard(prA.key);window.confirm=t=>{confirmations.push(t);return confirmResult;};
      window.revision={key:prA.key,repo:prA.repo,id:prA.id,sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40),targetCommit:'c'.repeat(40),iteration:4};
      window.arm='## What changed\\nSynthetic retry ownership change.\\n## ARM Before / After\\nIllustrative example; not deployment evidence.\\n\\n### Before\\n\\n\`\`\`bicep\\nresource job \\'Example/jobs@2026-01-01\\' = {\\n  name: \\'sample\\'\\n  properties: { retryPolicy: \\'Always\\' }\\n}\\n\`\`\`\\n### After\\n\\n\`\`\`bicep\\nresource job \\'Example/jobs@2026-01-01\\' = {\\n  name: \\'sample\\'\\n  properties: { retryPolicy: \\'OnFailure\\' }\\n  description: \\'A deliberately long synthetic example line to prove code scroll is local and does not push the composer or reference controls outside the assistance workspace.\\'\\n}\\n\`\`\`\\n## Differences\\n\\n\`\`\`diff\\n- retryPolicy: Always\\n+ retryPolicy: OnFailure\\n\`\`\`\\n## Improvement and risks\\nNarrower retry triggering may avoid repeated work. Verify provider semantics, cancellation, and which component owns retries. This example is synthetic.\\n';
      window.MockHost.rpc=async(method,p)=>{
        calls.push({method,...p});
        if(method==='explainPr')return {key:p.key,requestId:'E'+(++explainSequence),sequence:explainSequence,status:'running',message:'Synthetic partial Explain.',output:'Preparing synthetic ARM explanation...',revision,coverage:[]};
        if(method==='askPr'){
          if(window.referenceDenied)throw Error('Reference PR access denied (403). No comparison was generated.');
          if(p.explanationRequestId && !explanationArtifacts.get(p.key)?.has(p.explanationRequestId))throw Error('The selected explanation is unavailable.');
          if(p.explanationRequestId==='E4'&&p.explanationSectionIndex===null)throw Error('The entire explanation exceeds the serialized context limit. Choose an explicit available section; no context-free fallback.');
          const previous=S.investigations.get(p.key);
          const explanationContexts=[...(previous?.explanationContexts||[])];
          const e=explanationArtifacts.get(p.key)?.get(p.explanationRequestId);
          if(e&&!explanationContexts.some(x=>x.requestId===e.requestId))explanationContexts.push({requestId:e.requestId,output:e.requestId==='E4'?'## Safe choice\\nSynthetic selected paragraph.\\n'+arm:e.output,revision:e.revision,evidence:e.evidence||[],coverage:e.coverage||[],sectionIndex:p.explanationSectionIndex,sectionTitle:p.explanationSectionIndex===1?'Safe choice':null,receipt:p.explanationSectionIndex===1?'Explicit Safe choice and entire host ARM block retained. Omitted: Large notes. Original coverage retained.':'Entire completed explanation retained.'});
          const referencePrContexts=[...(previous?.referencePrContexts||[])];
          const url='https://dev.azure.com/offline/project/_git/policy/pullrequest/77';
          if(p.question.includes(url)&&!referencePrContexts.some(x=>x.url===url))referencePrContexts.push({url,revision:{...revision,key:'reference:77',repo:'synthetic-policy',id:77,sourceCommit:'d'.repeat(40)},output:'--- policy.xml @ base\\n+++ policy.xml @ source\\n@@ before line 1, after line 1 @@\\n-  <resource name="old">\\n+  <resource name="new">\\n+  # literal heading *literal* [link](https://evil.invalid/)\\n+  \`\`\`\\n+    indent\\n',evidence:['Reference policy.xml, synthetic fixture only.'],coverage:['Native mocked-source tests separately prove actual retrieval; this UI state is injected.']});
          return {key:p.key,conversationId:previous?.conversationId||'conversation-'+p.key,requestId:'turn-'+(++invRequest),sequence:++invSequence,status:'running',message:'Inspecting synthetic target and canonical ADO reference. No comments posted.',revision,worktree:'Synthetic isolated source',
            turns:previous?.turns||[],evidence:[],coverage:[],explanationContexts,referencePrContexts};
        }
        if(method==='cancelInvestigation')return {...S.investigations.get(p.key),sequence:++invSequence,status:'cancelled',message:'Cancelled synthetic inspection. No draft accepted.'};
        if(method==='cancelExplanation')return {...S.explanations.get(p.key),sequence:++explainSequence,status:'cancelled',message:'Cancelled; partial text is not a completed artifact.'};
        if(method==='postLensFindings'){
          const state=S.investigations.get(p.key),turn=state.turns.at(-1);
          return {...state,sequence:++invSequence,status:'completed',message:'One selected comment confirmed; unselected findings remain private.',turns:[...state.turns.slice(0,-1),{...turn,outcomes:p.findingIds.map(id=>({findingId:id,status:'posted',message:'Synthetic ADO outcome for selected '+id,url:'https://dev.azure.com/offline/thread?discussionId=41'}))}]};
        }
        if(method==='resetInvestigation'){
          if(window.resetDenied)return {...S.investigations.get(p.key),sequence:++invSequence,status:'failed',message:'Worktree cleanup failed; existing conversation retained.'};
          return {key:p.key,conversationId:'fresh-'+(++invRequest),requestId:'',sequence:++invSequence,status:'completed',message:'Fresh conversation; no source captured.',revision:null,worktree:null,turns:[],evidence:[],coverage:[]};
        }
        if(method==='openUrl')return null;
        throw Error('Unexpected mocked RPC '+method);
      };
      window.completeExplain=()=>{
        const state=S.explanations.get(prA.key);
        updateExplanation({...state,sequence:++explainSequence,status:'completed',message:'Synthetic code-grounded layout fixture only.',output:arm,evidence:['Synthetic file: infra/main.bicep'],coverage:['This browser fixture does not run a model.']});
      };
      window.finishInv=(kind,findings=[])=>{
        const state=S.investigations.get(prA.key),call=calls.filter(x=>x.method==='askPr').at(-1);
        updateInvestigation({...state,sequence:++invSequence,status:'completed',message:kind==='lens'?'Private synthetic findings; nothing posted.':'Answer ready about '+call.explanationRequestId,
          turns:[...state.turns,{id:state.requestId,kind,question:call.question,answer:'Synthetic answer: compare retry ownership against the reference, not merely the word OnFailure. <img src="https://evil.invalid/" onerror="window.hostile=true">',findings:findings.map(f=>({...f,outgoingComment:f.title+'\\nReview lens:\\n'+call.question+'\\n'+f.body+'\\nEvidence: '+f.path+':'+f.line+' at source '+revision.sourceCommit+'\\nGenerated by Copilot for a human-selected review lens; source inspected, tests not run.'})),outcomes:[],explanationRequestId:call.explanationRequestId,explanationSectionIndex:call.explanationSectionIndex,explanationRequestIds:state.explanationContexts.map(c=>c.requestId),referencePrUrls:state.referencePrContexts.map(c=>c.url)}],
          evidence:['Target sample-service !105: source '+revision.sourceCommit.slice(0,12), 'Reference synthetic-policy !77: source dddddddddddd; inspected infra/policy.bicep.'],coverage:['Synthetic UI evidence; native mocked-source tests prove actual reference retrieval.']});
      };
      S.expanded.add(prA.key);renderRows();
    `);
    assert.equal(await evaluate(`document.querySelector('#investigationDialog')===null`), true);
    await screenshot('01-workspace-desktop', 'Actual unified assistance; Explain/Ask/Focused review share one inline destination; video is alternate output.');
    await click('[data-act="explainPr"]');
    assert.equal(await evaluate(`card().querySelector('[data-assistance-attach]').disabled`), true);
    await screenshot('02-explain-running', 'Actual running Explain with partial output and exact-request Cancel.');
    await evaluate('completeExplain()');
    await screenshot('03-explain-arm-desktop', 'Actual completed synthetic ARM explanation, code/diff and next-action composer destination.');
    await evaluate(`card().querySelector('.explain-output').scrollTop=170;`);
    await click('[data-assistance-attach]');
    await input('question', 'In explanation E1, why use OnFailure instead of Always?');
    await click('[data-inv="send"]');
    assert.equal(await evaluate(`calls.at(-1).explanationRequestId`), 'E1');
    await evaluate(`finishInv('question')`);
    assert.equal(await evaluate(`card().querySelector('[data-inv="question"]').value`), '');
    assert.equal(await evaluate(`card().querySelector('img')===null&&!window.hostile`), true);
    await screenshot('04-followup-desktop', 'Actual linked Explain follow-up; captured Ask payload names E1. Model answer is synthetic.');
    await click('[data-assistance-intent="lens"]');
    await input('question', 'Is retry ownership aligned with PR https://dev.azure.com/offline/project/_git/policy/pullrequest/77?');
    await input('reference', 'https://dev.azure.com/offline/project/_git/policy/pullrequest/77');
    assert.equal(await evaluate(`card().querySelector('[data-inv="send"]').disabled`), false);
    await screenshot('05-reference-focus-desktop', 'Actual focused-review composer accepts a canonical ADO reference PR; no browser or live ADO read.');
    await click('[data-inv="send"]');
    assert.match(await evaluate(`calls.at(-1).question`), /Reference quote or repository path:\nhttps:\/\/dev\.azure\.com/);
    assert.equal(await evaluate(`calls.at(-1).explanationRequestId`), 'E1');
    await evaluate(`finishInv('lens',[{id:'f1',title:'Duplicate retry owner',body:'Synthetic comparison to reference policy: both caller and job retry.',path:'infra/main.bicep',line:4,quote:'retryPolicy: OnFailure'},{id:'f2',title:'Cancellation deadline',body:'Synthetic second concern.',path:'src/retry.cs',line:12,quote:'deadline'}])`);
    assert.match(await evaluate(`card().querySelector('.reference-context summary').textContent`), /synthetic-policy !77.*dddddddddddd/);
    await evaluate(`card().querySelector('.reference-context').open=true;`);
    assert.equal(await evaluate(`card().querySelector('.reference-output').textContent`),
      await evaluate(`S.investigations.get(prA.key).referencePrContexts[0].output`));
    assert.equal(await evaluate(`card().querySelector('.reference-output').children.length`), 0,
      'Reference source must not be interpreted as Markdown or HTML');
    await screenshot('05b-inspected-reference-result', 'Actual reference-result disclosure separates synthetic target/reference source identities and pinned comparison evidence.');
    await evaluate(`card().querySelector('.reference-context').open=false;`);
    await click('[data-lens-finding="f1"]');
    assert.equal(await evaluate(`card().querySelector('[data-lens-finding="f2"]').checked`), false);
    await screenshot('06-private-selected-desktop', 'Actual two private synthetic findings; only f1 selected; reference attribution shown separately.');
    await evaluate(`card().querySelector('.investigation-transcript').scrollTop=card().querySelector('.investigation-transcript').scrollHeight;`);
    await screenshot('06b-private-post-action', 'Actual selected finding action is reachable by scrolling only the result, not a separate output destination.');
    await evaluate('renderRows()');
    assert.equal(await evaluate(`card().querySelector('[data-lens-finding="f1"]').checked`), true);
    await click('[data-assistance-close]');await click('[data-assistance-close]');
    assert.equal(await evaluate(`card().querySelector('[data-lens-finding="f1"]').checked`), true);
    await click('[data-investigation-action="post"]');
    assert.equal(await evaluate(`document.querySelector('#assistancePostDialog').open`), true);
    assert.match(await evaluate(`document.querySelector('#assistancePostPreview pre').textContent`), /Review focus:.*Reference quote or repository path:.*https:\/\/dev.azure.com\/offline/s);
    assert.match(await evaluate(`document.querySelector('#assistancePostPreview pre').textContent`), /Generated by Copilot/);
    await screenshot('06c-selected-confirmation', 'Actual confirmation dialog shows only selected target-PR comments, source identity, content and Keep private/Confirm actions.', '#assistancePostDialog');
    await evaluate(`confirmations.push(document.querySelector('#assistancePostPreview').textContent)`);
    await click('#assistancePostDialog button[value="cancel"]');
    assert.equal(await evaluate(`calls.filter(x=>x.method==='postLensFindings').length`), 0);
    assert.equal(await evaluate(`document.activeElement.dataset.investigationAction`), 'post');
    await click('[data-investigation-action="post"]');
    await evaluate('renderRows()');
    await send('Input.dispatchKeyEvent', {type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await send('Input.dispatchKeyEvent', {type:'keyUp',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await evaluate(`new Promise(r=>setTimeout(r,30))`);
    assert.equal(await evaluate(`document.querySelector('#assistancePostDialog').open`),false);
    assert.equal(await evaluate(`document.activeElement.dataset.investigationAction`),'post');
    assert.equal(await evaluate(`calls.filter(x=>x.method==='postLensFindings').length`),0);
    await click('[data-investigation-action="post"]');
    await click('#assistancePostDialog button[value="post"]');
    assert.deepEqual(await evaluate(`calls.at(-1).findingIds`), ['f1']);
    assert.equal(await evaluate(`calls.at(-1).key`), await evaluate('prA.key'));
    assert.match(await evaluate('confirmations.at(-1)'), /Duplicate retry owner/);
    assert.equal(await evaluate(`card().querySelector('[data-investigation-action="post"]').disabled`),true);
    assert.match(await evaluate(`card().querySelector('[data-investigation-action="post"]').textContent`),/Post 0 selected/);
    await screenshot('07-selected-outcome-desktop', 'Actual selected-only synthetic publication outcome; no real ADO comment.');
    await click('[data-lens-finding="f2"]');
    assert.match(await evaluate(`card().querySelector('[data-investigation-action="post"]').textContent`),/Post 1 selected/);
    await click('[data-investigation-action="post"]');
    assert.match(await evaluate(`document.querySelector('#assistancePostPreview').textContent`),/Cancellation deadline/);
    assert.doesNotMatch(await evaluate(`document.querySelector('#assistancePostPreview').textContent`),/Duplicate retry owner/);
    await click('#assistancePostDialog button[value="cancel"]');
    await click('[data-lens-finding="f2"]');
    await select('explain:E1');
    await click('[data-act="explainPr"]');
    await click('[data-act="cancelExplanation"]');
    assert.equal(await evaluate(`card().querySelector('[data-assistance-attach]').disabled`), true);
    await select('explain:E1');
    await click('[data-assistance-attach]');
    await input('question', 'Continue from original E1, not cancelled E3.');
    await click('[data-inv="send"]');
    assert.equal(await evaluate('calls.at(-1).explanationRequestId'), 'E1');
    await evaluate(`S.q='no-matching-pr-for-this-test';renderRows();openInvestigation(prA.key);`);
    assert.equal(await evaluate(`card().closest('#explanationShelf')!==null`), true);
    await click('[data-investigation-action="cancel"]');
    assert.equal(await evaluate('S.investigations.get(prA.key).status'), 'cancelled');
    await screenshot('08-cancelled-inspection', 'Actual cancellation retains completed E1 and explicit attached context; no unfinished answer accepted.');
    await evaluate(`S.q='';renderRows();`);
    await select('turn:turn-2');
    assert.equal(await evaluate(`card().querySelector('[data-investigation-action="post"]')===null`), true);
    await click('[data-assistance-intent="lens"]');
    await input('question', 'Compare retry ownership against this PR.');
    await input('reference', 'https://dev.azure.com/offline/project/_git/private/pullrequest/78');
    await evaluate('window.referenceDenied=true');
    await click('[data-inv="send"]');
    assert.match(await evaluate(`card().querySelector('[data-inv="error"]').textContent`), /access denied \(403\)/);
    assert.match(await evaluate(`card().querySelector('[data-inv="reference"]').value`), /pullrequest\/78/);
    await screenshot('09-reference-access-error', 'Actual reference-access error feedback retains focus and URL; no fallback comparison.');
    await evaluate('window.referenceDenied=false');
    await select('explain:E1');
    for (const width of [1000,760]) {
      await send('Emulation.setDeviceMetricsOverride', { width, height: 1100, deviceScaleFactor: 1, mobile: false });
      const metrics = await evaluate(`(()=>{const c=card(),r=c.getBoundingClientRect();return {left:r.left,right:r.right,width:innerWidth,localCode:[...c.querySelectorAll('.explain-output pre')].some(x=>x.scrollWidth>x.clientWidth)};})()`);
      assert.ok(metrics.left >= 0 && metrics.right <= metrics.width + 1, JSON.stringify(metrics));
      assert.equal(metrics.localCode, true);
      await screenshot('10-arm-narrow-'+width, 'Actual narrow ARM/code-heavy explanation with local code overflow and workspace controls.');
    }
    await evaluate(`card().querySelector('.assistance-video').open=true;`);
    await screenshot('11-alternate-video', 'Actual video generator is a secondary disclosure inside PR assistance, with its own provenance.', '.assistance-video');
    await evaluate(`card().querySelector('.assistance-video').open=false;updateCopilotRun({...prA,runId:'workspace-fix',id:91,revision:1,status:'running',message:'Synthetic WSL run; no real execution.',output:'Synthetic bounded output\\n',startedAt:'2026-11-25T00:00:00Z',mayBeRunning:true});`);
    const fix = '#rows .copilot-run[data-run-id="workspace-fix"]';
    await screenshot('12-fix-running', 'Actual separate Fix lifecycle remains co-located; synthetic run only.', fix);
    await evaluate(`updateCopilotRun({...S.runs.get('workspace-fix'),revision:2,status:'completed',mayBeRunning:false,exitCode:0,message:'Process exited; PR fix not verified.'});`);
    await screenshot('13-fix-completed', 'Actual completed Fix status remains honest about unverified success.', fix);
    await evaluate(`updateCopilotRun({...prA,runId:'workspace-recovered',id:92,revision:1,status:'needs-attention',mayBeRunning:true,startedAt:'2026-11-25T00:00:01Z',output:'Synthetic recovered run output\\n',message:'Recovered synthetic run; outcome uncertain.'});renderRows();`);
    assert.match(await evaluate(`document.querySelector('.copilot-run[data-run-id="workspace-recovered"]').textContent`),/Recovered synthetic run; outcome uncertain/);
    await screenshot('14-fix-recovered', 'Actual independently seeded recovered/uncertain Fix retains its exact run and cancellation control.', '#rows .copilot-run[data-run-id="workspace-recovered"]');
    await evaluate(`prA.reviewers=[{name:'Required no vote',vote:0,isRequired:true},{name:'Approved group',vote:10,isRequired:true,isContainer:true},{name:'Rejected',vote:-10,isRequired:true},{name:'Suggestions',vote:5},{name:'Waiting',vote:-5}];renderRows();`);
    await screenshot('15-reviewer-mix', 'Actual Approved-first list still calls out rejected/waiting/no-vote entries.', '#rows .reviewers');
    await click('[data-assistance-intent="question"]');
    await evaluate(`window.resetDenied=true;window.retainedConversation=S.investigations.get(prA.key).conversationId;`);
    await click('[data-investigation-action="detach"]');
    assert.equal(await evaluate('assistanceDraft(prA.key).attachedId'),'E1');
    assert.equal(await evaluate('S.investigations.get(prA.key).conversationId'),await evaluate('window.retainedConversation'));
    assert.match(await evaluate('assistanceDraft(prA.key).error'),/cleanup failed/);
    await evaluate('window.resetDenied=false');
    await click('[data-investigation-action="reset"]');
    assert.equal(await evaluate('S.investigations.get(prA.key).turns.length'),0);
    assert.equal(await evaluate('assistanceDraft(prA.key).attachedId'),'');
    assert.equal(await evaluate('assistanceDraft(prA.key).error'),'');
    await evaluate(`retainExplanation({key:prA.key,requestId:'E4',status:'completed',message:'Synthetic oversized completed source.',output:'## Large notes\\n'+'<'.repeat(18000)+'\\n## Safe choice\\nSynthetic selected paragraph.\\n'+arm,revision,evidence:['Synthetic host ARM evidence'],coverage:['Original complete-artifact coverage'],explanationSections:[{index:0,title:'Large notes',characters:19000,includesArm:true},{index:1,title:'Safe choice',characters:arm.length+50,includesArm:true}]});assistanceDraft(prA.key).selected='explain:E4';openInvestigation(prA.key,'question','E4');`);
    await input('question','Ask about the selected safe section and its preserved ARM risks.');
    await click('[data-inv="send"]');
    assert.match(await evaluate(`card().querySelector('[data-inv="error"]').textContent`), /serialized context limit/);
    await screenshot('16-section-choice', 'Actual overflow error and complete source remain above the chosen package/composer; this overview is not a selector crop.');
    await screenshot('16b-visible-section-choice', 'Actual package selector and preserved draft after whole-context overflow. Error remains recorded above; no context-free retry. Full explanation remains readable.', '[data-inv="form"]');
    await click('[data-assistance-section]');
    await send('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await send('Input.dispatchKeyEvent',{type:'keyUp',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
    await evaluate(`(()=>{const choice=card().querySelector('[data-assistance-section]');choice.focus();choice.value='1';choice.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    assert.equal(await evaluate('document.activeElement.dataset.inv'),'section');
    await click('[data-inv="send"]');
    assert.equal(await evaluate('calls.at(-1).explanationSectionIndex'),1);
    await evaluate(`finishInv('question');card().querySelector('.assistance-context details').open=true;`);
    assert.match(await evaluate(`card().querySelector('.assistance-context details').textContent`),/Omitted: Large notes.*Original coverage/s);
    await screenshot('17-section-receipt', 'Actual accepted package disclosure includes explicit omissions and full ARM/risks; full E4 artifact remains separately selectable.');
    await evaluate(`(()=>{const result=card().querySelector('[data-assistance-result]');result.focus();result.value='explain:E4';result.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    assert.equal(await evaluate('document.activeElement.dataset.inv'),'result');
    await click('[data-assistance-intent="question"]');
    await evaluate(`(()=>{const choice=card().querySelector('[data-assistance-section]');choice.value='';choice.dispatchEvent(new Event('change',{bubbles:true}));})()`);
    await input('question','Try changing the imported package.');
    assert.equal(await evaluate(`card().querySelector('[data-inv="send"]').disabled`),true);
    assert.match(await evaluate(`card().textContent`),/Start a new conversation before changing it/);
    await evaluate(`openInvestigation(prB.key); const d=assistanceDraft(prB.key);d.question='Private B draft';renderInvestigation(prB.key);openInvestigation(prA.key);`);
    assert.notEqual(await evaluate('assistanceDraft(prA.key).question'), 'Private B draft');
    await evaluate(`openInvestigation(prB.key)`);
    assert.equal(await evaluate(`assistanceCard(prB.key).querySelector('[data-inv="question"]').value`), 'Private B draft');
    assert.deepEqual(errors, []); assert.deepEqual(network, []);
    if (proof) await writeFile(path.join(proof,'evidence-manifest.json'),JSON.stringify({
      synthetic:true,networkBlocked:true,cpuCap:'25% hard cap required by runner',browserPid:child.pid,
      captures, requests:await evaluate('calls'), confirmations:await evaluate('confirmations'),
      limits:'UI fixture model responses and posting outcomes are synthetic. Native mocked-source tests establish actual context/reference retrieval; browser captures establish interaction and dispatched identities only.',
    },null,2));
    await send('Browser.close', {}, false);
  } finally {
    socket?.close();
    if (child.exitCode === null) child.kill();
    await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 5000))]);
    await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  }
});
