const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm, mkdir, writeFile } = require('node:fs/promises');
const { once } = require('node:events');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

// No browser packages required: Node 22+ and a local Chromium browser.
test('real row hit testing and action identity across refreshes', { timeout: 90000 }, async t => {
  if (process.env.ROW_TEST_FILTER) {
    const run = t.test.bind(t), names = process.env.ROW_TEST_FILTER.split('|');
    t.test = (name, ...args) => names.some(part => name.includes(part)) ? run(name, ...args) : Promise.resolve();
  }
  const profile = await mkdtemp(path.join(os.tmpdir(), 'ado-row-tests-'));
  const browser = process.env.ROW_TEST_BROWSER || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
  const child = spawn(browser, [
    '--headless=new', '--remote-debugging-port=0', '--no-first-run', '--disable-extensions',
    '--no-default-browser-check', `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  const exited = once(child, 'exit');
  let socket;
  try {
    const url = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Browser startup timed out')), 15000);
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.stderr.on('data', chunk => {
        const match = chunk.toString().match(/DevTools listening on (ws:\/\/\S+)/);
        if (match) { clearTimeout(timer); resolve(match[1]); }
      });
    });
    socket = new WebSocket(url);
    await once(socket, 'open');
    let id = 0, session;
    const pending = new Map();
    socket.addEventListener('message', e => {
      const m = JSON.parse(e.data), p = pending.get(m.id);
      if (!p) return;
      pending.delete(m.id);
      m.error ? p.reject(new Error(JSON.stringify(m.error))) : p.resolve(m.result);
    });
    const send = (method, params = {}, useSession = true) => new Promise((resolve, reject) => {
      const n = ++id;
      pending.set(n, { resolve, reject });
      socket.send(JSON.stringify({ id: n, method, params, ...(useSession && session ? { sessionId: session } : {}) }));
    });
    const target = await send('Target.createTarget', { url: 'about:blank' }, false);
    session = (await send('Target.attachToTarget', { targetId: target.targetId, flatten: true }, false)).sessionId;
    const evaluate = async expression => {
      const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
      if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails));
      return r.result.value;
    };
    const frame = () => evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    const move = (x, y) => send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
    const press = p => send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'left', clickCount: 1, x: p.x, y: p.y });
    const release = p => send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'left', clickCount: 1, x: p.x, y: p.y });
    const click = async p => { await move(p.x, p.y); await press(p); await release(p); };
    await send('Page.enable');
    await send('Page.bringToFront');
    await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'no-preference' }] });
    await send('Page.addScriptToEvaluateOnNewDocument', { source: `
      window.rowIntervals=[];
      const interval=window.setInterval.bind(window);
      window.setInterval=(fn,ms)=>{window.rowIntervals.push({fn,ms});return interval(fn,ms);};
    ` });
    await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    const root = path.resolve(__dirname, '..', 'src', 'AdoPrView', 'wwwroot', 'index.html');
    await send('Page.navigate', { url: pathToFileURL(root).href + '?mock=1' });
    await evaluate(`new Promise((resolve,reject)=>{
      const deadline=Date.now()+10000;
      const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot')):setTimeout(check,20);check();
    })`);
    await evaluate(`
      window.baseline=structuredClone(S.snap);
      window.calls=[];window.confirmations=[];window.confirmResult=true;
      window.confirm=text=>{window.confirmations.push(text);return window.confirmResult;};
      window.MockHost.rpc=async(method,p)=>{
        window.calls.push({method,...p});
        if(method==='hide')return {...S.settings,hidden:[...S.settings.hidden,p.key]};
        if(method==='unhide')return {...S.settings,hidden:S.settings.hidden.filter(k=>k!==p.key)};
        if(method==='copyPrompt')return {prompt:'Intercepted'};
        if(method==='openPop')return {message:'Intercepted native PoP; not approved'};
        if(method==='startCopilot'){
          const pr=S.snap.prs.find(x=>x.key===p.key);
          return {...pr,runId:'offline-'+pr.id,status:'running',message:'Fake CLI running; no live fix',output:'Fake stdout\\n',worktreePath:'/tmp/offline-'+pr.id};
        }
        if(method==='cancelCopilot')return {...S.runs.get(p.runId),status:'cancelled',message:'Cancelled fake run; worktree retained',worktreeRetained:true};
        if(method==='restartMerge')return {requested:true,message:'Intercepted merge re-check'};
        if(method==='explainPr')return {key:p.key,requestId:'offline-explain-'+(++window.explainSequence),
          sequence:window.explainSequence,status:'running',message:'Reading synthetic pinned context',
          output:'',coverage:[],revision:{iteration:4,sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40)}};
        if(method==='cancelExplanation')return {...S.explanations.get(p.key),sequence:++window.explainSequence,
          status:'cancelled',message:'Explanation cancelled. Partial text is not a finished explanation.'};
        if(method==='tiktokPr')return {key:p.key,requestId:(++window.videoSequence).toString(16).padStart(32,'0'),
          sequence:window.videoSequence,status:'preparing',message:'Synthetic UI job only; no model or renderer.',
          evidence:[],coverage:[],model:'claude-opus-5.5'};
        if(method==='cancelVideo')return {...S.videos.get(p.key),sequence:++window.videoSequence,
          status:'cancelled',message:'Synthetic video cancelled. No MP4 was generated.'};
        if(method==='listSpeechSubscriptions'){
          if(window.speechMissingAz)throw Error('Azure CLI (az) is not available. Install Azure CLI and run az login.');
          if(window.speechListDenied)throw Error('Subscription listing denied. Use an exact resource ID if permitted.');
          return [{id:'00000000-0000-4000-8000-000000000001',name:'Offline subscription'}];
        }
        if(method==='findSpeechResources')return [
          {resourceId:'offline-verified',name:'Verified fixture',region:'eastus',compatible:true,
            voices:['en-US-Ava:DragonHDLatestNeural'],message:'Synthetic catalog; no live Azure call'},
          {resourceId:'offline-denied',name:'Denied fixture',region:'eastus',compatible:false,voices:[],message:'Missing Speech User RBAC'}];
        if(method==='selectSpeechResource'){
          if(window.holdSpeechSelection)await new Promise(resolve=>window.completeSpeechSelection=resolve);
          if(window.speechDenied)throw Error('Synthesis denied: Cognitive Services Speech User RBAC is required.');
          return {...S.settings,videoSpeech:{resourceId:p.resourceId,voice:p.voice,region:'eastus'}};
        }
        if(method==='cancelSpeechSetup')return {cancelled:true};
        if(method==='approve'){
          const pr=structuredClone(S.snap.prs.find(x=>x.key===p.key));
          if(window.holdApproval)await new Promise(resolve=>window.completeApproval=resolve);
          if(window.approvalFailure)throw Error('Offline approval denied; refresh before retrying.');
          pr.reviewVote={...pr.reviewVote,vote:10,canApprove:false,target:{...pr.reviewVote.target,vote:10}};
          pr.reviewers=[{id:'offline-reviewer',name:'Offline Reviewer',vote:10}];
          return {confirmed:true,message:'Your ordinary ADO vote is Approved. Other reviewers remain required.',pr};
        }
        if(method==='autoComplete'){
          if(window.autoFailure)throw Error('Offline auto-complete denied');
          const pr=structuredClone(S.snap.prs.find(x=>x.key===p.key));
          pr.autoCompleteBy=p.enable?'Offline Reviewer':null;
          pr.mergeStrategy=p.enable?S.settings.autoComplete.mergeStrategy:null;
          pr.humanActions=pr.humanActions.filter(a=>a!=='autocomplete').concat(p.enable?[]:['autocomplete']);
          pr.gates.autocomplete={state:p.enable?'ok':'human',label:p.enable?'Set':'Not set'};
          setSnapshot({...S.snap,prs:S.snap.prs.map(x=>x.key===p.key?pr:x)});
          return pr;
        }
        if(method==='refresh'){
          setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1});
          return null;
        }
        if(method==='generateApproval')return {kind:'returned',message:'Intercepted; no approval submitted'};
        if(method==='logTail')return window.holdLog?new Promise(resolve=>window.completeLog=resolve):'Intercepted log';
        return null;
      };
      document.addEventListener('click',e=>{
        const a=e.target.closest('a[href]');
        if(a){e.preventDefault();window.calls.push({method:'anchor',url:a.href});}
      },true);
      window.rowPoint=(key,selector)=>{
        document.querySelector('#toasts').replaceChildren();
        const row=[...document.querySelectorAll('.pr')].find(e=>e.dataset.key===key);
        const b=row.querySelector(selector);
        if(!b)throw Error('Missing control '+key+' '+selector);
        b.scrollIntoView({block:'center',inline:'nearest'});
        const r=b.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;
        const hit=document.elementFromPoint(x,y);
        if(hit?.closest('.pr')!==row || hit.closest('[data-act]')!==b.closest('[data-act]'))
          throw Error('Hit testing disagrees with visible row '+key);
        const label=row.querySelector('.id');
        return {x,y,key,id:label.textContent.slice(1),repo:label.nextElementSibling.nextElementSibling.textContent};
      };
      window.swapRows=()=>{
        const snap=structuredClone(S.snap),ps=snap.prs;
        const a=ps.findIndex(p=>p.id===101),b=ps.findIndex(p=>p.id===104);
        [ps[a],ps[b]]=[ps[b],ps[a]];
        snap.orderRevision=(S.snap.orderRevision||0)+1;setSnapshot(snap);
      };
      window.raceFixture=()=>{
        const snap=structuredClone(window.baseline),a=snap.prs.find(p=>p.id===101);
        const b={...structuredClone(a),id:104,key:'sample-api-id:104',repo:'sample-api',
          title:'Different repository and PR',url:'https://dev.azure.com/contoso/Sample/_git/sample-api/pullrequest/104'};
        snap.prs=[a,b];setSnapshot(snap);
      };
    `);
    const reset = async (race = false) => {
      await evaluate('S.speechSetup ? closeSpeechSetup() : null');
      await release({ x: 10, y: 10 });
      await move(10, 10);
      await evaluate(`document.activeElement?.blur();window.getSelection().removeAllRanges();`);
      await frame();
      await evaluate(`S.expanded.clear();S.lane=null;S.repo='';S.q='';S.scope='all';
        S.rowOrder=[];S.orderRevision=null;S.laneRetained.clear();S.runs.clear();S.busy.clear();closeRowMenu();
        window.holdLog=false;S.settings.hidden=['sample-deploy-id:1001'];S.settings.ignoreDrafts=false;S.hideUndo=[];
        window.confirmations=[];window.confirmResult=true;window.autoFailure=false;S.autoCompleteErrors.clear();
        window.explainSequence=0;S.explanations.clear();window.videoSequence=0;S.videos.clear();
        S.settings.videoSpeech={voice:'offline-configured-voice'};
        window.speechMissingAz=false;window.speechDenied=false;window.speechListDenied=false;window.holdSpeechSelection=false;
        window.holdApproval=false;window.approvalFailure=false;S.reviewVotes.clear();
        setSnapshot(structuredClone(window.baseline));${race ? 'window.raceFixture();' : ''}render();window.calls=[];`);
      await frame();
    };
    const point = (key, selector) => evaluate(`window.rowPoint(${JSON.stringify(key)},${JSON.stringify(selector)})`);
    const elementPoint = selector => evaluate(`(()=>{
      const el=document.querySelector(${JSON.stringify(selector)});el.scrollIntoView({block:'center'});
      const r=el.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};
    })()`);
    const expectedUrl = p => `https://dev.azure.com/contoso/Sample/_git/${encodeURIComponent(p.repo)}/pullrequest/${p.id}`;
    const calls = () => evaluate('window.calls');
    const hidePoint = async key => {
      const p = await point(key, '.row .title');
      await move(p.x, p.y);
      await send('Input.dispatchMouseEvent', { type: 'mousePressed', button: 'right', clickCount: 1, x: p.x, y: p.y });
      await send('Input.dispatchMouseEvent', { type: 'mouseReleased', button: 'right', clickCount: 1, x: p.x, y: p.y });
      await frame();
      const menu = await evaluate(`(()=>{const b=document.querySelector('#rowMenu button'),r=b.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()`);
      return { ...p, x: menu.x, y: menu.y };
    };

    await t.test('Explain preserves row targets, sanitizes output, discloses pinned evidence and supports cancel/retry at wide and narrow widths', async () => {
      const key='sample-service-id:101';
      for (const width of [1600,900]) {
        await send('Emulation.setDeviceMetricsOverride', {width,height:1000,deviceScaleFactor:1,mobile:false});
        await reset(true);
        await evaluate(`S.expanded.add('${key}');renderRows();
          window.originalFix=document.querySelector('[data-key="${key}"] .row [data-act="triage"]');`);
        await click(await point(key,'.detail [data-act="explainPr"]')); await frame();
        assert.deepEqual((await calls()).map(c=>c.method),['explainPr']);
        assert.equal(await evaluate(`(()=>{
          const button=document.querySelector('[data-key="${key}"] [data-act="explainPr"]');
          button.click();return button.disabled;
        })()`),true);
        assert.equal((await calls()).length,1);
        await evaluate(`updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,
          output:'Partial <script>window.explainInjected=true</script>',coverage:['Binary payload omitted'],
          evidence:['after: /README.md, lines 1-12.']});`);
        assert.equal(await evaluate(`window.originalFix===document.querySelector('[data-key="${key}"] .row [data-act="triage"]')`),true);
        assert.equal(await evaluate('window.explainInjected===true'),false);
        assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .explain-details').open`),false);
        await click(await point(key,'.detail [data-act="cancelExplanation"]')); await frame();
        const cancel=(await calls()).find(c=>c.method==='cancelExplanation');
        assert.equal(cancel.key,key);
        assert.equal(cancel.requestId,'offline-explain-1');
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] [data-act="explainPr"]').textContent`),/Retry Explain/);
        await click(await point(key,'.detail [data-act="explainPr"]')); await frame();
        await evaluate(`window.currentExplain=S.explanations.get('${key}');
          updateExplanation({...window.currentExplain,sequence:++window.explainSequence,status:'completed',
            message:'Summary ready.',output:'The fixture generator is a developer utility. Its dependency changes. <img src="https://evil.invalid/image"><a href="https://evil.invalid/">link</a><button>run</button>',
            coverage:['Binary payload omitted'],evidence:['after: /README.md, lines 1-12.']});
          updateExplanation({...window.currentExplain,sequence:1,output:'late cancelled response'});`);
        assert.equal(await evaluate(`(()=>{
          const card=document.querySelector('[data-key="${key}"] .explanation');
          return !card.querySelector('.explain-output img, .explain-output a, .explain-output button, .explain-output script')
            && card.querySelector('.explain-output').textContent.includes('developer utility')
            && !card.querySelector('.explain-details').open
            && card.scrollWidth<=card.clientWidth+1;
        })()`),true,`Sanitized brief panel fits at ${width}px`);
        const summary=await point(key,'.explain-details summary');
        await click(summary); await frame();
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .explain-revision').textContent`),/source aaaaaaaaaaaa.*merge base bbbbbbbbbbbb/);
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .explain-evidence').textContent`),/README.md/);
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .explain-coverage').textContent`),/Binary payload omitted/);
        await evaluate(`document.querySelector('[data-key="${key}"] .explain-details summary').focus();`);
        await send('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',windowsVirtualKeyCode:13,text:'\r'});
        await send('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
        await frame();
        assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .explain-details').open`),false);
        await evaluate(`updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,
          status:'failed',message:'The PR revisions changed. Retry Explain.',output:'partial response'});`);
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .explain-status').textContent`),/revisions changed/);
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] [data-act="explainPr"]').textContent`),/Retry Explain/);
        assert.deepEqual((await calls()).map(c=>c.method),['explainPr','cancelExplanation','explainPr']);
      }
      await send('Emulation.setDeviceMetricsOverride', {width:1600,height:1000,deviceScaleFactor:1,mobile:false});
    });

    await t.test('first video selects an Azure resource and voice explicitly, missing az and denied synthesis cannot generate', async () => {
      const key='sample-service-id:101';
      await reset(true);
      await evaluate(`S.settings.videoSpeech=null;window.speechMissingAz=true;S.expanded.add('${key}');renderRows();`);
      await click(await point(key,'.detail [data-act="tiktokPr"]')); await frame();
      assert.equal(await evaluate(`document.querySelector('#speechDialog').open`),true);
      assert.match(await evaluate(`document.querySelector('#speechBody').textContent`),/az.*not available/);
      assert.deepEqual((await calls()).map(c=>c.method),['listSpeechSubscriptions']);
      await evaluate(`window.speechMissingAz=false;document.querySelector('[data-sact="reload"]').click();`); await frame();
      await evaluate(`{
        const select=document.querySelector('#speechSubscription');select.value=select.options[1].value;
        select.dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('[data-sact="find"]').click();
      }`); await frame();
      assert.match(await evaluate(`document.querySelector('#speechBody').textContent`),/Missing Speech User RBAC/);
      assert.equal(await evaluate(`document.querySelector('#speechResource option[value="offline-denied"]')===null`),true);
      await evaluate(`{
        const select=document.querySelector('#speechResource');select.value='offline-verified';
        select.dispatchEvent(new Event('change',{bubbles:true}));window.speechDenied=true;
        document.querySelector('[data-sact="use"]').click();
      }`); await frame();
      assert.match(await evaluate(`document.querySelector('#speechBody').textContent`),/Synthesis denied/);
      assert.equal((await calls()).some(c=>c.method==='tiktokPr'),false);
      await evaluate(`window.speechDenied=false;document.querySelector('[data-sact="use"]').click();`); await frame();
      assert.equal(await evaluate(`document.querySelector('#speechDialog').open`),false);
      assert.equal((await calls()).filter(c=>c.method==='tiktokPr').length,1);
      assert.equal(await evaluate('S.settings.videoSpeech.voice'),'en-US-Ava:DragonHDLatestNeural');
      assert.equal((await calls()).find(c=>c.method==='tiktokPr').key,key);
    });

    await t.test('exact-resource setup retains typed values and open state through progress, denied verification and retry', async () => {
      const key='sample-service-id:101';
      const resource='/subscriptions/00000000-0000-4000-8000-000000000001/resourceGroups/offline/providers/Microsoft.CognitiveServices/accounts/offline-exact';
      const voice='en-US-AvaMultilingualNeural';
      await reset(true);
      await evaluate(`S.settings.videoSpeech=null;window.speechListDenied=true;S.expanded.add('${key}');renderRows();`);
      await click(await point(key,'.detail [data-act="tiktokPr"]')); await frame();
      assert.match(await evaluate(`document.querySelector('#speechBody').textContent`),/Subscription listing denied/);
      await evaluate(`document.querySelector('#speechBody details').open=true;document.querySelector('#speechExactId').focus();`);
      await send('Input.insertText',{text:resource});
      await send('Input.dispatchKeyEvent',{type:'keyDown',key:'Tab',code:'Tab',windowsVirtualKeyCode:9});
      await send('Input.dispatchKeyEvent',{type:'keyUp',key:'Tab',code:'Tab',windowsVirtualKeyCode:9});
      assert.equal(await evaluate(`document.querySelector('#speechExactId').value`),resource);
      await evaluate(`document.querySelector('#speechExactVoice').value='';document.querySelector('#speechExactVoice').focus();`);
      await send('Input.insertText',{text:voice});
      await evaluate(`window.holdSpeechSelection=true;window.speechDenied=true;`);
      await click(await elementPoint('[data-sact="exact"]')); await frame();
      await evaluate(`onHostMessage({event:'speechSetupProgress',data:{epoch:S.speechEpoch,message:'Checking offline synthesis permissions'}});`);
      const exactState=()=>evaluate(`({
        resource:document.querySelector('#speechExactId').value,voice:document.querySelector('#speechExactVoice').value,
        open:document.querySelector('#speechBody details').open
      })`);
      assert.deepEqual(await exactState(),{resource,voice,open:true});
      await evaluate(`window.completeSpeechSelection();`); await frame();
      assert.match(await evaluate(`document.querySelector('#speechBody').textContent`),/Synthesis denied/);
      assert.deepEqual(await exactState(),{resource,voice,open:true});
      assert.equal((await calls()).some(c=>c.method==='tiktokPr'),false);
      await evaluate(`window.holdSpeechSelection=false;window.speechDenied=false;`);
      await click(await elementPoint('[data-sact="exact"]')); await frame();
      const selected=(await calls()).filter(c=>c.method==='selectSpeechResource');
      assert.equal(selected.length,2);
      assert.equal(selected.every(c=>c.resourceId===resource&&c.voice===voice),true);
      assert.equal((await calls()).some(c=>c.method==='findSpeechResources'),false);
      assert.equal((await calls()).filter(c=>c.method==='tiktokPr').length,1);
      assert.equal(await evaluate(`document.querySelector('#speechDialog').open`),false);
    });

    await t.test('video loading stays responsive, cancellation survives filtering, retry is exact-job scoped and ignored context cannot restart', async () => {
      const key='sample-service-id:101';
      for (const width of [1600,900]) {
        await send('Emulation.setDeviceMetricsOverride', {width,height:1000,deviceScaleFactor:1,mobile:false});
        await reset(true);
        await evaluate(`S.expanded.add('${key}');renderRows();
          window.originalFix=document.querySelector('[data-key="${key}"] .row [data-act="triage"]');`);
        await click(await point(key,'.detail [data-act="tiktokPr"]')); await frame();
        assert.deepEqual((await calls()).map(c=>c.method),['tiktokPr']);
        assert.equal(await evaluate(`(()=>{
          const button=document.querySelector('[data-key="${key}"] [data-act="tiktokPr"]');
          button.click();return button.disabled;
        })()`),true);
        assert.equal((await calls()).length,1,'Duplicate start is blocked');
        await evaluate(`window.firstVideo=S.videos.get('${key}');
          updateVideo({...window.firstVideo,sequence:++window.videoSequence,status:'rendering',
            message:'<img src=x onerror="window.videoInjected=true"> Generating narration',
            evidence:['Diff: /README.md'],coverage:['Only the public fixture is synthetic']});`);
        assert.equal(await evaluate(`(()=>{
          const card=document.querySelector('[data-key="${key}"] .pr-video');
          return card.scrollWidth<=card.clientWidth+1&&getComputedStyle(card.querySelector('.video-spinner')).animationName!=='none'
            &&!window.videoInjected&&!card.querySelector('.video-status img')
            &&window.originalFix===document.querySelector('[data-key="${key}"] .row [data-act="triage"]');
        })()`),true,`Responsive safe loading card at ${width}px`);
        await evaluate(`S.q='no matching PR';renderRows();`);
        assert.equal(await evaluate(`!!document.querySelector('#videoShelf [data-act="cancelVideo"]:not([hidden])')`),true);
        const cancelPoint=await evaluate(`(()=>{
          const button=document.querySelector('#videoShelf [data-act="cancelVideo"]');button.scrollIntoView({block:'center'});
          const r=button.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};
        })()`);
        await click(cancelPoint); await frame();
        const cancelled=(await calls()).find(c=>c.method==='cancelVideo');
        assert.equal(cancelled.key,key);
        assert.equal(cancelled.requestId,'1'.padStart(32,'0'));
        await evaluate(`S.q='';renderRows();`);
        await click(await point(key,'.detail [data-act="tiktokPr"]')); await frame();
        await evaluate(`window.retryVideo=S.videos.get('${key}');
          updateVideo({...window.firstVideo,sequence:1,message:'stale progress'});
          updateVideo({...window.retryVideo,sequence:++window.videoSequence,status:'failed',message:'Voice prerequisite unavailable. No media published.'});`);
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .video-status').textContent`),/prerequisite unavailable/);
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] [data-act="tiktokPr"]').textContent`),/Retry video/);
        assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"] video')`),false);
        await evaluate(`S.q='no matching PR';renderRows();S.settings.hidden.push('${key}');renderRows();`);
        assert.equal(await evaluate(`document.querySelector('#videoShelf [data-act="tiktokPr"]').disabled`),true);
        await evaluate(`document.querySelector('#videoShelf [data-act="tiktokPr"]').click();`);
        assert.deepEqual((await calls()).map(c=>c.method),['tiktokPr','cancelVideo','tiktokPr']);
      }
      await send('Emulation.setDeviceMetricsOverride', {width:1600,height:1000,deviceScaleFactor:1,mobile:false});
    });

    await t.test('lane-filtered video jobs keep a visible exact-job cancellation control', async () => {
      const key='sample-service-id:101';
      await reset(true);
      await evaluate(`S.expanded.add('${key}');renderRows();`);
      await click(await point(key,'.detail [data-act="tiktokPr"]')); await frame();
      await click(await elementPoint('[data-lane="blocked"]')); await frame();
      assert.equal(await evaluate(`!!document.querySelector('#rows [data-key="${key}"]')`),false);
      const cancel=`#videoShelf [data-video-key="${key}"] [data-act="cancelVideo"]`;
      assert.equal(await evaluate(`!!document.querySelector('${cancel}:not([hidden])')`),true);
      await click(await elementPoint(cancel)); await frame();
      const cancelled=(await calls()).filter(c=>c.method==='cancelVideo');
      assert.equal(cancelled.length,1);
      assert.equal(cancelled[0].key,key);
      assert.equal(cancelled[0].requestId,'1'.padStart(32,'0'));
    });

    await t.test('shelf retries and membership changes retain unrelated player nodes, seek and playback settings', async () => {
      const playing='sample-service-id:101', retry='sample-api-id:104';
      await reset(true);
      const state=await evaluate(`(async()=>{
          S.q='no matching PR';renderRows();
          updateVideo({key:'${playing}',requestId:'a'.repeat(32),sequence:++window.videoSequence,status:'completed',
            message:'Offline playback fixture',evidence:[],coverage:[],model:'claude-opus-5.5',durationSeconds:1,
            path:'offline.mp4',mediaUrl:'https://pr-video.local/'+'a'.repeat(32)+'.mp4'});
          updateVideo({key:'${retry}',requestId:'b'.repeat(32),sequence:++window.videoSequence,status:'failed',
            message:'Synthetic failure',evidence:[],coverage:[]});
          const player=document.querySelector('#videoShelf [data-video-key="${playing}"] video');
          player.currentTime=17;player.volume=.35;player.playbackRate=.75;
          const snapshot=()=>{
            const current=document.querySelector('#videoShelf [data-video-key="${playing}"] video');
            return {same:current===player,connected:player.isConnected,position:current.currentTime,
              volume:current.volume,rate:current.playbackRate};
          };
          await onAction('tiktokPr',{},'${retry}');
          const afterRetry=snapshot();
          updateVideo({key:'departed:105',requestId:'c'.repeat(32),sequence:++window.videoSequence,
            status:'rendering',message:'Another retained job',evidence:[],coverage:[]});
          const afterAddition=snapshot();
          S.videos.delete('departed:105');renderVideoShelf();
          return [afterRetry,afterAddition,snapshot()];
        })()`);
      assert.deepEqual(state,Array.from({length:3},()=>({same:true,connected:true,position:17,volume:.35,rate:.75})));
      assert.equal((await calls()).find(c=>c.method==='tiktokPr').key,retry);
    });

    await t.test('ordinary Approve preserves collapsed/expanded rows through pending, result and refresh without dialogs or duplicate votes', async () => {
      const key='sample-service-id:101';
      for (const [surface, expanded] of [['row',false],['row',true],['detail',true]]) for (const failure of [false,true]) {
        await reset(true);
        await evaluate(`{
          const p=S.snap.prs.find(p=>p.key==='${key}');
          p.isActive=true;p.repoId='sample-service-id';p.reviewers=[];
          p.reviewVote={reviewerId:'offline-reviewer',reviewerName:'Offline Reviewer',vote:0,canApprove:true,
            detail:'Only your ordinary vote. Other reviews remain separate.',
            target:{reviewerId:'offline-reviewer',sourceCommit:'a'.repeat(40),targetCommit:'b'.repeat(40),
              sourceRef:'refs/heads/feature',targetRef:'refs/heads/main',iterationId:4,vote:0}};
          ${surface === 'row' ? 'delete p.approval;' : "p.approval={title:'Offline provider remains separate'};"}
          ${expanded ? `S.expanded.add('${key}');` : ''}renderRows();
          window.holdApproval=true;window.approvalFailure=${failure};window.confirmResult=false;
        }`);
        const assertExpansion = async phase => {
          assert.deepEqual(await evaluate(`(()=>{
            const row=document.querySelector('[data-key="${key}"]');
            return {state:S.expanded.has('${key}'),open:row.classList.contains('open'),detail:!!row.querySelector('.detail')};
          })()`), {state:expanded,open:expanded,detail:expanded}, `${surface}/${expanded}/${failure}: ${phase}`);
        };
        await assertExpansion('before click');
        await click(await point(key, `.${surface} [data-act="approve"]`)); await frame();
        await assertExpansion('pending');
        const approvalCalls=()=>calls().then(cs=>cs.filter(c=>c.method==='approve'));
        const submitted=await approvalCalls();
        assert.equal(submitted.length,1,`${surface} ${failure}: one RPC for deliberate click`);
        assert.equal(submitted[0].key,key);
        assert.deepEqual(submitted[0].expected,{
          reviewerId:'offline-reviewer',sourceCommit:'a'.repeat(40),targetCommit:'b'.repeat(40),
          sourceRef:'refs/heads/feature',targetRef:'refs/heads/main',iterationId:4,vote:0,
        });
        assert.deepEqual(await evaluate('window.confirmations'),[]);
        assert.equal(await evaluate(`(()=>{
          const buttons=[...document.querySelectorAll('[data-key="${key}"] [data-act="approve"]')];
          const locked=buttons.length>0&&buttons.every(b=>b.disabled&&b.getAttribute('aria-busy')==='true');
          buttons.forEach(b=>b.click());return locked;
        })()`),true);
        assert.equal((await approvalCalls()).length,1,'Duplicate controls cannot submit another RPC');
        await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1});`);
        await frame();
        await assertExpansion('refresh while pending');
        await evaluate('window.completeApproval()'); await frame();
        await assertExpansion('result');
        assert.equal((await approvalCalls()).length,1,'Completion/failure does not retry the vote');
        assert.deepEqual(await evaluate('window.confirmations'),[]);
        assert.equal(await evaluate(`S.snap.prs.find(p=>p.key==='${key}').reviewVote.vote`),failure?0:10);
        assert.equal(await evaluate(`S.reviewVotes.get('${key}').kind`),failure?'error':'confirmed');
        if(failure)assert.match(await evaluate(`document.querySelector('#toasts').textContent`),/Offline approval denied/);
        if(surface==='detail')assert.ok(await evaluate(`!!document.querySelector('[data-key="${key}"] .row [data-act="generateApproval"]')`));
        assert.deepEqual((await calls()).map(c=>c.method),['approve']);
        await move(10,10);
        await evaluate(`document.activeElement?.blur();`);
        await frame();
        await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1});flushRowUpdates();`);
        await frame();
        await assertExpansion('refresh after result');
        await click(await point(key, '.row .title')); await frame();
        assert.equal(await evaluate(`S.expanded.has('${key}')`),!expanded,'Ordinary row click still toggles expansion');
      }
    });

    await t.test('Auto turns grey in-place under the pointer only after confirmed set; cancel, refresh, collapse and failure remain truthful', async () => {
      await reset(true);
      const key='sample-service-id:101', neighbor='sample-api-id:104';
      await evaluate(`S.expanded.add('${key}');renderRows();`);
      const p=await point(key, '.row [data-act="autocomplete"]');
      await evaluate(`window.autoButton=document.querySelector('[data-key="${key}"] .row [data-act="autocomplete"]');`);
      await click(p); await frame();
      await evaluate(`Promise.all(window.autoButton.getAnimations().map(a=>a.finished.catch(()=>{})))`);
      const state=()=>evaluate(`(()=>{const row=document.querySelector('[data-key="${key}"]'),b=row.querySelector('.row [data-slot="auto"] button');return{
        same:b===window.autoButton,act:b.dataset.act,grey:getComputedStyle(b).color,pressed:b.getAttribute('aria-pressed'),
        busy:b.disabled,detail:row.querySelector('[data-auto-complete-state]')?.textContent,
        other:document.querySelector('[data-key="${neighbor}"] .row [data-slot="auto"] button').dataset.act
      };})()`);
      const neutralText = await evaluate(`getComputedStyle(document.documentElement).getPropertyValue('--text2').trim()`);
      const neutralRgb = await evaluate(`(()=>{const e=document.createElement('span');e.style.color=${JSON.stringify(neutralText)};document.body.appendChild(e);const c=getComputedStyle(e).color;e.remove();return c;})()`);
      assert.deepEqual(await state(), {same:true,act:'cancelAuto',grey:neutralRgb,pressed:'true',busy:false,detail:'squash · by Offline Reviewer',other:'autocomplete'});
      assert.equal(await evaluate('!!S.pendingSnapshot'),true,'Unrelated table updates should still be deferred');
      await move(10,10); await frame();
      await click(await point(key,'.row .title')); await frame();
      await click(await point(key,'.row .title')); await frame();
      assert.equal((await state()).act,'cancelAuto');
      await click(await point(key,'.row [data-act="cancelAuto"]')); await frame();
      assert.equal((await state()).act,'autocomplete');
      await evaluate('window.autoFailure=true;');
      await click(await point(key,'.row [data-act="autocomplete"]')); await frame();
      assert.equal((await state()).act,'autocomplete');
      assert.equal((await state()).pressed,'false');
      assert.match(await evaluate(`document.querySelector('[data-key="${key}"] [data-auto-complete-error]').textContent`),/Offline auto-complete denied/);
      assert.deepEqual((await calls()).filter(c=>c.method==='autoComplete').map(c=>({key:c.key,enable:c.enable})),
        [{key,enable:true},{key,enable:false},{key,enable:true}]);
    });

    await t.test('visible PR ID/repo match Open and PoP destinations at multiple sizes and scales', async () => {
      let checked = 0;
      for (const [width, scale] of [[1000, 1], [1280, 1.25], [1600, 1.5], [1920, 1]]) {
        await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: scale, mobile: false });
        await reset();
        const keys = await evaluate(`[...document.querySelectorAll('.pr')].map(e=>e.dataset.key)`);
        for (const key of keys) {
          for (const selector of ['.row [data-act="open"]', '.row button[data-act="pop"]:not([disabled])', '.row [data-act="gate"][data-gate="pop"]']) {
            if (!await evaluate(`document.querySelector('[data-key="${key}"]').querySelector(${JSON.stringify(selector)})!==null`)) continue;
            const p = await point(key, selector);
            await click(p);
            assert.deepEqual((await calls()).at(-1), selector.includes('pop') ? { method: 'openPop', key: p.key } : { method: 'openUrl', url: expectedUrl(p) });
            checked++;
          }
        }
      }
      t.diagnostic(`${checked} coordinate-click destinations matched displayed PR ID and repo`);
    });

    for (const action of ['open', 'pop']) {
      await t.test(`${action}: refresh cannot substitute a different repo/PR under the pointer`, async () => {
        await reset(true);
        const p = await point('sample-service-id:101', `.row button[data-act="${action}"]`);
        await move(p.x, p.y);
        await evaluate('window.swapRows()');
        await frame();
        await press(p); await release(p);
        assert.deepEqual((await calls()).at(-1), action === 'pop' ? { method: 'openPop', key: p.key } : { method: 'openUrl', url: expectedUrl(p) });
        assert.equal(await evaluate(`document.querySelector('.pr').dataset.key`), p.key);
        assert.equal(await evaluate('!!S.pendingSnapshot'), true);
        await move(10, 10); await frame();
        assert.equal(await evaluate(`document.querySelector('.pr').dataset.key`), 'sample-api-id:104');
        assert.equal(await evaluate('!!S.pendingSnapshot'), false, 'Mouse focus must not pin updates after pointer exit');
      });
    }

    await t.test('refresh and periodic redraw between press/release do not drop or retarget clicks', async () => {
      await reset(true);
      const p = await point('sample-service-id:101', '.row button[data-act="open"]');
      await move(p.x, p.y); await press(p);
      await evaluate(`window.swapRows();window.rowIntervals.find(t=>t.ms===60000).fn();renderRows();`);
      await release(p);
      assert.deepEqual(await calls(), [{ method: 'openUrl', url: expectedUrl(p) }]);
    });

    await t.test('long refresh/error status metadata cannot shift table links under the pointer', async () => {
      await reset(true);
      const p = await point('sample-service-id:101', '.row [data-act="open"]');
      await move(p.x, p.y);
      await evaluate(`setSnapshot({...S.snap,refreshing:true,mergeCheckStatus:'Merge re-check: 10 checking',
        repoErrors:Array(5).fill('A long repository refresh failure with actionable retry guidance')})`);
      await frame();
      const same = await point(p.key, '.row [data-act="open"]');
      assert.equal(same.y, p.y);
      await press(p); await release(p);
      assert.deepEqual((await calls()).at(-1), { method: 'openUrl', url: expectedUrl(p) });
    });

    await t.test('keyboard-focused controls retain their PR until focus leaves rows', async () => {
      await reset(true);
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9 });
      await evaluate(`document.querySelector('.pr .row [data-act="open"]').focus();window.swapRows();`);
      assert.equal(await evaluate('!!S.pendingSnapshot'), true);
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
      await frame();
      assert.match((await calls()).at(-1).url, /sample-service\/pullrequest\/101$/);
      await evaluate(`document.querySelector('#search').focus()`); await frame();
      assert.equal(await evaluate(`document.querySelector('.pr').dataset.key`), 'sample-api-id:104');
    });

    await t.test('filters, updated order and expanded-row commands use displayed identity, never an index', async () => {
      await reset();
      await evaluate(`S.snap.prs.reverse();render();S.repo='sample-service';render();`);
      const key = 'sample-service-id:105';
      await click(await point(key, '.row .title'));
      for (const [selector, method] of [
        ['.detail [data-act="generateApproval"]', 'generateApproval'],
        ['.detail [data-act="restartMerge"]', 'restartMerge'],
        ['.detail [data-act="requeue"]', 'requeue'],
        ['.detail [data-act="copyPrompt"]', 'copyPrompt'],
      ]) {
        const p = await point(key, selector);
        await click(p);
        await frame();
        const call = (await calls()).filter(c => c.method === method).at(-1);
        assert.equal(call.key, p.key);
        assert.equal(p.id, '105'); assert.equal(p.repo, 'sample-service');
      }
      const p = await point(key, '.row [data-act="open"]');
      await click(p);
      assert.deepEqual((await calls()).at(-1), { method: 'openUrl', url: expectedUrl(p) });
    });

    await t.test('latest pending snapshot wins and a removed row never redirects to its replacement', async () => {
      await reset(true);
      const p = await point('sample-service-id:101', '.row button[data-act="pop"]');
      await move(p.x, p.y);
      await evaluate(`window.swapRows();setSnapshot({...S.pendingSnapshot,prs:S.pendingSnapshot.prs.filter(p=>p.id!==101)});`);
      await press(p); await release(p);
      assert.deepEqual((await calls()).at(-1), { method: 'openPop', key: p.key });
      await move(10, 10); await frame();
      assert.equal(await evaluate(`document.querySelectorAll('.pr').length`), 1);
      assert.equal(await evaluate(`document.querySelector('.pr').dataset.key`), 'sample-api-id:104');
    });

    await t.test('automatic log loading above another targeted row cannot move its link under the pointer', async () => {
      await reset();
      await evaluate(`(()=>{
        const snap=structuredClone(window.baseline),a=snap.prs.find(p=>p.id===105),b=snap.prs.find(p=>p.id===101);
        b.lane=a.lane;snap.prs=[a,b];S.logs.clear();window.holdLog=true;setSnapshot(snap);
      })()`);
      await click(await point('sample-service-id:105', '.row .title'));
      await frame();
      const p = await point('sample-service-id:101', '.row [data-act="open"]');
      await move(p.x, p.y);
      await evaluate(`window.completeLog(Array(200).fill('Expanded asynchronous log output').join('\\n'))`);
      await frame();
      await press(p); await release(p);
      assert.deepEqual((await calls()).filter(c => c.method === 'openUrl').at(-1), { method: 'openUrl', url: expectedUrl(p) });
      assert.equal(await evaluate('S.rowsPending'), true);
      await move(10, 10); await frame();
      assert.equal(await evaluate('S.rowsPending'), false);
      assert.match(await evaluate(`document.querySelector('pre.log').textContent`), /Expanded asynchronous log output/);
    });

    await t.test('Hide confirmation identifies the displayed PR, Cancel is inert, and Ctrl+Z undoes only hides', async () => {
      await reset(true);
      const key = 'sample-service-id:101';
      await evaluate('window.confirmResult=false');
      assert.equal(await evaluate(`document.querySelectorAll('.row [data-act="hide"]').length`), 0);
      const p = await hidePoint(key);
      const title = await evaluate(`document.querySelector('.pr .title').textContent`);
      await click(p);
      assert.equal((await calls()).length, 0);
      const question = (await evaluate('window.confirmations')).at(-1);
      for (const text of [p.id, p.repo, title]) assert.ok(question.includes(text));
      await evaluate('window.confirmResult=true');
      await click(await hidePoint(key));
      await frame();
      assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"]')`), false);
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'z', code: 'KeyZ', windowsVirtualKeyCode: 90, modifiers: 2 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'z', code: 'KeyZ', windowsVirtualKeyCode: 90, modifiers: 2 });
      await frame();
      assert.deepEqual((await calls()).filter(c => ['hide', 'unhide'].includes(c.method)), [
        { method: 'hide', key }, { method: 'unhide', key },
      ]);
      assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"]')`), true);
      await click(await hidePoint(key)); await frame();
      const before = (await calls()).filter(c => c.method === 'unhide').length;
      await evaluate(`document.querySelector('#search').focus()`);
      await send('Input.insertText', { text: 'typed search' });
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'z', code: 'KeyZ', windowsVirtualKeyCode: 90, modifiers: 2 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'z', code: 'KeyZ', windowsVirtualKeyCode: 90, modifiers: 2 });
      await frame();
      assert.equal((await calls()).filter(c => c.method === 'unhide').length, before, 'Text editing must retain native Undo');
      assert.equal(await evaluate(`document.querySelector('#search').value`), '');
      const undo = await evaluate(`(()=>{
        const b=document.querySelector('#toasts .toast button'),r=b.getBoundingClientRect();
        return {x:r.x+r.width/2,y:r.y+r.height/2};
      })()`);
      await click(undo); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'unhide').at(-1), { method: 'unhide', key });
      assert.equal(await evaluate('S.hideUndo.length'), 0);
    });

    await t.test('a multi-action sequence and lane changes keep row order until Refresh or R explicitly reorders', async () => {
      await reset(true);
      const target = 'sample-api-id:104';
      const before = await evaluate(`[...document.querySelectorAll('.pr')].map(e=>e.dataset.key)`);
      // The target starts second. Auto updates its time and lane, as a native
      // single-PR refresh would, then a later full refresh arrives sorted.
      await evaluate(`(()=>{
        window.originalRpc=window.MockHost.rpc;
        window.MockHost.rpc=async(method,p)=>{
          const result=await window.originalRpc(method,p);
          if(method==='autoComplete'){
            const snap=structuredClone(S.snap),pr=snap.prs.find(x=>x.key===p.key);
            pr.updated='2099-01-01T00:00:00Z';pr.lane='blocked';
            pr.autoCompleteBy='Offline';pr.humanActions=['pop'];
            pr.gates.merge={state:'fail',label:'Conflicts'};
            snap.prs.reverse();setSnapshot(snap);
          }
          return result;
        };
        S.lane='needsHuman';S.expanded.add('${target}');renderRows();
      })()`);
      await click(await point(target, '.row [data-act="autocomplete"]'));
      await move(10, 10); await frame();
      const keys = () => evaluate(`[...document.querySelectorAll('.pr')].map(e=>e.dataset.key)`);
      assert.deepEqual(await keys(), before);
      assert.equal(await evaluate(`document.querySelector('[data-key="${target}"] .row-lane').textContent`), 'Blocked');
      assert.equal(await evaluate(`S.snap.prs.find(p=>p.key==='${target}').updated`), '2099-01-01T00:00:00Z');
      assert.equal(await evaluate(`document.querySelector('[data-key="${target}"]').classList.contains('open')`), true);
      assert.match(await evaluate(`document.querySelector('#statusline').textContent`), /changed lane stay here/);
      const p = await point(target, '.row [data-act="pop"]');
      await click(p);
      assert.deepEqual((await calls()).filter(c => c.method === 'openPop').at(-1), { method: 'openPop', key: p.key });
      await move(10, 10); await frame();
      await evaluate(`setSnapshot({...S.snap,prs:[...S.snap.prs].reverse(),mergeCheckStatus:'Merge re-check: automatic sweep'})`);
      assert.deepEqual(await keys(), before, 'Full refresh/merge sweep must not restore Updated sorting');
      await evaluate(`S.lane=null;S.laneRetained.clear();render()`);
      await evaluate(`document.querySelector('#refreshBtn').click()`);
      await frame();
      const sorted = await keys(); // Needs Human remains above Blocked on explicit refresh.
      assert.deepEqual(sorted, before);
      await evaluate(`(()=>{
        const snap=structuredClone(S.snap);snap.prs.find(p=>p.key==='${target}').lane='needsHuman';
        setSnapshot(snap);
      })()`);
      assert.deepEqual(await keys(), before);
      await evaluate(`document.querySelector('#refreshBtn').click()`); await frame();
      assert.deepEqual(await keys(), [...before].reverse(), 'Main Refresh must restore Updated ordering within a lane');
      await evaluate(`(()=>{
        const snap=structuredClone(S.snap);snap.prs.find(p=>p.key==='${target}').lane='ready';setSnapshot(snap);
      })()`);
      assert.deepEqual(await keys(), [...before].reverse(), 'Background lane change must stay in place');
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'r', code: 'KeyR', windowsVirtualKeyCode: 82, text: 'r' });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'r', code: 'KeyR', windowsVirtualKeyCode: 82 });
      await frame();
      assert.deepEqual(await keys(), before, 'Keyboard refresh must restore lane priority');
      await evaluate(`window.MockHost.rpc=window.originalRpc;delete window.originalRpc;`);
    });

    await t.test('compact table columns align and needed +1 expands without overlapping other row targets', async () => {
      for (const width of [1000, 1280, 1600]) {
        await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: false });
        await reset();
        const layout = await evaluate(`(()=>{
          const headers=[...document.querySelectorAll('.thead>div')].map(e=>e.textContent.trim());
          const row=document.querySelector('[data-key="sample-service-id:104"] .row');
          const buttons=[...row.querySelectorAll('button:not([disabled])')];
          return {headers,cells:row.children.length,needed:row.querySelector('[data-act="generateApproval"]').outerHTML,
            overlaps:buttons.some((b,i)=>buttons.slice(i+1).some(c=>{
              const a=b.getBoundingClientRect(),d=c.getBoundingClientRect();
              return Math.min(a.right,d.right)-Math.max(a.left,d.left)>.5 && Math.min(a.bottom,d.bottom)-Math.max(a.top,d.top)>.5;
            }))};
        })()`);
        assert.deepEqual(layout.headers, ['Pull request', 'Merge', 'Build', 'Checks', 'Comments', 'Review Bot', 'Updated', 'Actions']);
        assert.equal(layout.cells, layout.headers.length);
        assert.equal(layout.overlaps, false);
        assert.match(layout.needed, /provider-approval hot/);
        assert.match(layout.needed, /review-needed/);
      }
    });

    await t.test('semantic action slots stay aligned across expanded/compact/missing/disabled/draft/watched/busy combinations', async () => {
      for (const width of [1000, 1280, 1600]) {
        await send('Emulation.setDeviceMetricsOverride', { width, height: 1100, deviceScaleFactor: 1, mobile: false });
        await reset();
        await evaluate(`(()=>{
          const base=structuredClone(window.baseline.prs[0]);
          const states=[
            {isMine:true,isDraft:true,humanActions:['reviews','publish','pop','autocomplete'],approval:{title:'Request approval for only this PR'},gates:{...base.gates,reviews:{state:'human'}}},
            {isMine:false,isDraft:false,humanActions:[],approval:{title:'Request approval for only this PR'},autoCompleteBy:'Offline',gates:{...base.gates,pop:{state:'ok'},reviews:{state:'ok'}}},
            {isMine:false,humanActions:['reviews','pop','autocomplete'],approval:null,reviewVote:null,repo:'other-repo',gates:{...base.gates,reviews:{state:'human'}}},
            {isMine:true,humanActions:[],approval:null,reviewVote:null,autoCompleteBy:null,gates:{...base.gates,pop:{state:'na'},reviews:{state:'ok'}}},
            {isMine:true,humanActions:['reviews','publish','pop','autocomplete'],approval:{title:'Request approval for only this PR'},gates:{...base.gates,reviews:{state:'human'}}},
            {isMine:false,approval:null,reviewVote:{...base.reviewVote,vote:0,canApprove:true}},
            {isMine:true,approval:null,reviewVote:{...base.reviewVote,vote:10,canApprove:false,target:{...base.reviewVote.target,vote:10}}},
            {isMine:false,approval:null,reviewVote:{...base.reviewVote,canApprove:false,detail:'Refresh before approving.'}},
            {isMine:true,approval:null,reviewVote:{...base.reviewVote}}
          ];
          const ps=states.map((s,i)=>({...base,...s,key:'grid-'+i,id:100+i,title:'Grid fixture '+i,
            url:'https://dev.azure.com/contoso/Sample/_git/'+(s.repo||base.repo)+'/pullrequest/'+(100+i)}));
          for(const action of ['generateApproval','publish','pop','autocomplete','triage'])S.busy.add('grid-4:'+action);
          S.busy.add('grid-8:approve');
          setSnapshot({...S.snap,prs:ps,orderRevision:(S.snap.orderRevision||0)+1});renderRows();
          document.querySelector('main').scrollLeft=9999;
        })()`);
        const layout = await evaluate(`[...document.querySelectorAll('#rows .pr')].map(row=>[...row.querySelectorAll('.action-slot')].map(slot=>{
          const s=slot.getBoundingClientRect(),b=slot.querySelector('button'),r=b?.getBoundingClientRect();
          return {slot:slot.dataset.slot,left:s.left,width:s.width,button:r?{left:r.left,right:r.right,center:r.left+r.width/2}:null};
        }))`);
        for (const row of layout) {
          assert.deepEqual(row.map(s => s.slot), ['approval', 'publish', 'pop', 'auto', 'fix', 'open']);
          row.forEach((s, i) => {
            assert.ok(Math.abs(s.left - layout[0][i].left) < .1, `Slot ${s.slot} moved between rows at ${width}`);
            assert.equal(s.width, layout[0][i].width);
            if (s.button) {
              assert.ok(s.button.left >= s.left - .1 && s.button.right <= s.left + s.width + .1, `${s.slot} clipped outside its slot`);
              assert.ok(Math.abs(s.button.center - (s.left + s.width / 2)) < .1, `${s.slot} center is not aligned`);
            }
          });
        }
        assert.equal(layout[3][0].button, null, 'Absent review action still needs its empty slot');
        assert.deepEqual(await evaluate(`['grid-5','grid-6','grid-7','grid-8'].map(key=>{
          const b=document.querySelector('[data-key="'+key+'"] .row [data-act="approve"]');
          return {label:b.textContent.trim(),disabled:b.disabled,busy:b.getAttribute('aria-busy')};
        })`), [
          {label:'Approve',disabled:false,busy:null},
          {label:'Approved',disabled:true,busy:null},
          {label:'Approve',disabled:true,busy:null},
          {label:'Approve',disabled:true,busy:'true'},
        ]);
        for (const key of ['grid-0', 'grid-1', 'grid-2', 'grid-3', 'grid-4']) {
          const p = await point(key, '.row [data-act="open"]'); await click(p);
          assert.deepEqual((await calls()).filter(c => c.method === 'openUrl').at(-1), { method: 'openUrl', url: expectedUrl(p) });
        }
        const review = await point('grid-2', '.row [data-act="manualReview"]'); await click(review);
        assert.deepEqual((await calls()).filter(c => c.method === 'openUrl').at(-1), { method: 'openUrl', url: expectedUrl(review) });
      }
    });

    await t.test('context menu Hide stays bound to its menu-open PR through a reorder snapshot; Escape and outside click dismiss', async () => {
      await reset(true);
      const key = 'sample-service-id:101';
      const menu = await hidePoint(key);
      await evaluate('window.swapRows()'); await frame();
      assert.equal(await evaluate('S.rowMenu.key'), key);
      await click(menu); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'hide'), [{ method: 'hide', key }]);
      await evaluate(`undoHide()`);
      await frame();
      await hidePoint(key);
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
      assert.equal(await evaluate('S.rowMenu'), null);
      await hidePoint(key);
      await click({ x: 10, y: 10 });
      assert.equal(await evaluate('S.rowMenu'), null);
      assert.equal((await calls()).filter(c => c.method === 'hide').length, 1);
    });

    for (const motion of ['no-preference', 'reduce']) {
      await t.test(`Copilot busy state and refresh survive redraws and terminal events with motion=${motion}`, async () => {
        await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: motion }] });
        try {
          await reset(true);
          assert.equal(await evaluate("matchMedia('(prefers-reduced-motion: reduce)').matches"), motion === 'reduce');
          const a = 'sample-service-id:101', b = 'sample-api-id:104';
          const active = { running: true, busy: 'true', disabled: false, animation: motion === 'reduce' ? 'none' : 'spin' };
          const idle = { running: false, busy: null, disabled: false, animation: 'none' };
          const running = key => evaluate(`(()=>{const e=document.querySelector('[data-key="${key}"] .row [data-act="triage"]');return {running:e.classList.contains('running'),busy:e.getAttribute('aria-busy'),disabled:e.disabled,animation:getComputedStyle(e.querySelector('svg')).animationName};})()`);
          await click(await point(a, '.row [data-act="triage"]')); await frame();
          assert.deepEqual(await running(a), active, 'Launch reply must not clear the busy state, regardless of motion preference');
          await click(await point(a, '.row .title')); await frame();
          await click(await point(b, '.row .title')); await frame();
          assert.deepEqual(await running(a), active);
          assert.deepEqual(await running(b), idle);
          await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
          await evaluate(`setSnapshot({...S.snap,refreshing:true,prs:S.snap.prs.map(p=>({...p,updated:'2099-01-01'}))});S.q='no matching PR';render();S.q='';render();`);
          await frame();
          assert.deepEqual(await running(a), active);
          const refresh = await evaluate(`(()=>{
            const button=document.querySelector('#refreshBtn'),dot=document.querySelector('.statusline .dot');
            return {spin:button.classList.contains('spin'),disabled:button.disabled,busy:dot.classList.contains('busy'),
              label:dot.parentElement.textContent,animation:getComputedStyle(button.querySelector('svg')).animationName,
              dotAnimation:getComputedStyle(dot).animationName};
          })()`);
          assert.deepEqual(refresh, { spin: true, disabled: false, busy: true, label: 'Refreshing…',
            animation: motion === 'reduce' ? 'none' : 'spin', dotAnimation: motion === 'reduce' ? 'none' : 'blink' });
          await evaluate(`document.querySelector('#refreshBtn').click();setSnapshot({...S.snap,refreshing:false});`);
          assert.equal((await calls()).filter(c => c.method === 'refresh').length, 1, 'Refresh stays usable without animation');
          assert.equal(await evaluate("document.querySelector('#refreshBtn').classList.contains('spin')"), false);
          await evaluate(`updateCopilotRun({...S.snap.prs.find(p=>p.key==='${b}'),runId:'independent',status:'running',output:''});`);
          for (const status of ['completed', 'failed', 'cancelled']) {
            assert.deepEqual(await running(a), active);
            await evaluate(`updateCopilotRun({...runForPr('${a}'),status:'needs-attention',mayBeRunning:true,message:'Task status temporarily unavailable'});`);
            assert.deepEqual(await running(a), active, 'Uncertain process liveness must not appear idle');
            await evaluate(`updateCopilotRun({...runForPr('${a}'),status:'${status}',mayBeRunning:false});`);
            assert.deepEqual(await running(a), idle, 'Collapsed terminal event must clear only its own busy state');
            assert.deepEqual(await running(b), active);
            await evaluate(`updateCopilotRun({...runForPr('${a}'),runId:'test-after-${status}',status:'running',output:''});`);
          }
        } finally {
          await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'no-preference' }] });
        }
      });
    }

    await t.test('failed browser-open and Fix setup actions remain visible errors without hiding dashboard or sticking busy', async () => {
      await reset(true);
      const key = 'sample-service-id:101';
      await evaluate(`
        window.actionRpc=MockHost.rpc;
        MockHost.rpc=async(method,p)=>{
          if(method==='openUrl')throw Error('Windows could not open the default browser (Windows error 1155). Check the default HTTPS app.');
          if(method==='startCopilot')throw Error('Could not set up the isolated worktree at PR source fetch (exit 128). Git authentication was refused. No Copilot task was launched.');
          return window.actionRpc(method,p);
        };`);
      try {
        await click(await point(key, '.row [data-act="open"]')); await frame();
        assert.match(await evaluate("document.querySelector('#toasts').textContent"), /default HTTPS app/);
        await click(await point(key, '.row [data-act="triage"]')); await frame();
        assert.match(await evaluate("document.querySelector('#toasts').textContent"), /PR source fetch.*exit 128/);
        assert.equal(await evaluate(`(()=>{const e=document.querySelector('[data-key="${key}"] .row [data-act="triage"]');return e.disabled||e.hasAttribute('aria-busy');})()`), false);
        assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .row').getBoundingClientRect().height>0`), true);
        await click(await point(key, '.row .title')); await frame();
        assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"]')`), true);
      } finally { await evaluate('MockHost.rpc=window.actionRpc;'); }
    });

    await t.test('running Copilot panel stays inside its owning row across lane/search/repo views and collapse/reopen', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await reset();
      const key = 'sample-service-id:101';
      await click(await point(key, '.row [data-act="triage"]')); await frame();
      const where = () => evaluate(`(()=>{const cards=[...document.querySelectorAll('.copilot-run')];return {
        shelf:document.querySelector('#runShelf').children.length,cards:cards.length,
        owners:cards.map(c=>c.closest('.pr')?.dataset.key),rowShown:!!document.querySelector('#rows [data-key="${key}"]')};})()`);
      assert.deepEqual(await where(), { shelf: 0, cards: 1, owners: [key], rowShown: true });
      for (const view of [`S.lane='blocked'`, `S.lane='waiting'`, `S.lane='ready'`, `S.lane='needsHuman'`, `S.lane=null;S.q='no matching PR'`,
        `S.q='';S.repo='sample-api'`, `S.repo='';S.scope='watched'`, `S.scope='all'`]) {
        await evaluate(`S.laneRetained.clear();${view};render();`); await frame();
        const w = await where();
        assert.equal(w.shelf, 0, `${view}: a loaded PR's run must never render below the view`);
        assert.deepEqual(w.owners, w.rowShown ? [key] : [], `${view}: run panel must follow its own row`);
      }
      await click(await point(key, '.row .title')); await frame();
      assert.deepEqual(await where(), { shelf: 0, cards: 0, owners: [], rowShown: true }, 'Collapsing the owner hides its run panel');
      assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .row [data-act="triage"]').classList.contains('running')`), true);
      await click(await point(key, '.row .title')); await frame();
      assert.deepEqual(await where(), { shelf: 0, cards: 1, owners: [key], rowShown: true }, 'Reopening shows the same run');
      assert.deepEqual((await calls()).filter(c => c.method === 'cancelCopilot'), [], 'Switching views must never cancel a run');
    });

    await t.test('Fix metadata remains separately readable without clipping at wide and narrow panel widths', async () => {
      const key = 'sample-service-id:101';
      try {
        for (const width of [1600, 900]) {
          await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: false });
          await reset(true);
          await click(await point(key, '.row [data-act="triage"]')); await frame();
          await evaluate(`window.metadataRow=document.querySelector('[data-key="${key}"]');
            window.metadataOutput=window.metadataRow.querySelector('.run-output');
            updateCopilotRun({...runForPr('${key}'),
              worktreePath:'/tmp/synthetic/'+'long-worktree-segment-'.repeat(12),
              sessionId:'synthetic-session-'+'0123456789'.repeat(4),
              logPath:'/tmp/synthetic/'+'long-supervised-log-segment-'.repeat(12)+'/output.jsonl',
              preparationLogPath:'/tmp/synthetic/'+'long-preparation-log-segment-'.repeat(12)+'/prepare.stderr',
              output:'Updated synthetic stdout\\n'});`);
          await frame();
          const layout = await evaluate(`(()=>{
            const row=document.querySelector('[data-key="${key}"]'),card=row.querySelector('.copilot-run');
            const metadata=card.querySelector('.run-worktree'),text=metadata.textContent,node=metadata.firstChild;
            let offset=0;
            const lines=text.split('\\n').map(line=>{
              const range=document.createRange();range.setStart(node,offset);range.setEnd(node,offset+line.length);
              offset+=line.length+1;
              const rects=[...range.getClientRects()];
              return {text:line,top:Math.min(...rects.map(r=>r.top)),bottom:Math.max(...rects.map(r=>r.bottom))};
            });
            return {lines,height:metadata.clientHeight,scrollHeight:metadata.scrollHeight,
              fits:metadata.scrollWidth<=metadata.clientWidth+1 && card.scrollWidth<=card.clientWidth+1,
              rowPreserved:row===window.metadataRow,outputPreserved:card.querySelector('.run-output')===window.metadataOutput,
              outputHeight:window.metadataOutput.clientHeight,output:window.metadataOutput.textContent};
          })()`);
          assert.deepEqual(layout.lines.map(line => line.text.split(': ')[0]),
            ['Worktree', 'Copilot session', 'Supervised log', 'Private preparation log (if created)']);
          assert.ok(layout.lines.every((line, i) => i === 0 || line.top >= layout.lines[i - 1].bottom - 1),
            `Metadata fields must start on separate lines at ${width}px`);
          assert.ok(layout.scrollHeight <= layout.height + 1 && layout.fits,
            `All metadata must fit without internal scrolling or clipping at ${width}px`);
          assert.equal(layout.rowPreserved && layout.outputPreserved, true, 'Metadata updates must not replace the owning row or stream');
          assert.equal(layout.outputHeight, 280, 'The streaming output area keeps its fixed height');
          assert.equal(layout.output, 'Updated synthetic stdout\n');
        }
      } finally {
        await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      }
    });

    await t.test('Fix output follows the tail by default, respects wheel and keyboard reading, and resumes at the bottom', async () => {
      const key = 'sample-service-id:101';
      try {
        for (const width of [1600, 900]) {
          await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: false });
          await reset();
          await evaluate(`window.tailLines=Array.from({length:160},(_,n)=>'Synthetic line '+n+' for scrolling only.\\n').join('');
            window.tailRun={...S.snap.prs.find(p=>p.key==='${key}'),runId:'tail-follow',status:'running',
              output:window.tailLines,outputOffset:window.tailLines.length};
            S.runs.set(window.tailRun.runId,window.tailRun);S.expanded.add('${key}');render();
            window.tailElement=document.querySelector('[data-key="${key}"] .run-output');
            window.tailNode=window.tailElement.firstChild;
            window.tailState=()=>({top:window.tailElement.scrollTop,
              bottom:window.tailElement.scrollHeight-window.tailElement.clientHeight-window.tailElement.scrollTop,
              height:window.tailElement.clientHeight,same:window.tailElement===document.querySelector('[data-key="${key}"] .run-output'),
              sameText:window.tailNode===window.tailElement.firstChild});
            window.appendTail=()=>{const run=runForPr('${key}'),extra='Synthetic appended line\\n'.repeat(12);
              updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});};`);
          await frame();
          assert.ok((await evaluate('window.tailState()')).bottom <= 2, `${width}: a newly opened existing log starts at the tail`);
          await evaluate('window.appendTail()'); await frame();
          assert.ok((await evaluate('window.tailState()')).bottom <= 2, `${width}: coalesced output follows the tail`);
          await evaluate(`S.expanded.delete('${key}');renderRows();window.appendTail();
            S.expanded.add('${key}');renderRows();S.q='no matching PR';render();window.appendTail();S.q='';render();`);
          await frame();
          assert.ok((await evaluate('window.tailState()')).bottom <= 2,
            'Following intent survives collapse and filter return with hidden output');
          const p = await elementPoint(`[data-key="${key}"] .run-output`);
          await move(p.x, p.y);
          await send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: p.x, y: p.y, deltaX: 0, deltaY: -600 });
          await frame();
          await evaluate(`new Promise(resolve=>setTimeout(resolve,250))`);
          const reading = await evaluate('window.tailState()');
          assert.ok(reading.bottom > 100, 'Trusted wheel input leaves the tail');
          await evaluate('window.appendTail()'); await frame();
          assert.ok(Math.abs((await evaluate('window.tailState()')).top - reading.top) <= 1, 'New output does not fight wheel reading');
          await evaluate('window.tailElement.focus()');
          await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'End', code: 'End', windowsVirtualKeyCode: 35 });
          await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'End', code: 'End', windowsVirtualKeyCode: 35 });
          await evaluate(`new Promise(resolve=>setTimeout(resolve,250))`); await frame();
          await evaluate('window.appendTail()'); await frame();
          assert.ok((await evaluate('window.tailState()')).bottom <= 2, 'Reaching the bottom automatically resumes following');
          await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Home', code: 'Home', windowsVirtualKeyCode: 36 });
          await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Home', code: 'Home', windowsVirtualKeyCode: 36 });
          await evaluate(`new Promise(resolve=>setTimeout(resolve,250))`); await frame();
          await evaluate('window.appendTail()'); await frame();
          const home = await evaluate('window.tailState()');
          assert.ok(home.top <= 1 && home.bottom > 100, 'Keyboard reading at the beginning stays there');
          assert.equal(home.same && home.sameText, true, 'Streaming preserves the output and text node identities');
          assert.equal(home.height, 280, 'Output geometry is unchanged');
        }
      } finally {
        await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      }
    });

    await t.test('Fix output preserves per-run position and selected text through rerenders, collapse, filters and refresh', async () => {
      await reset();
      const key = 'sample-service-id:101';
      await evaluate(`{window.readingText=Array.from({length:160},(_,n)=>'Read selected synthetic line '+n+'\\n').join('');
        const pr=S.snap.prs.find(p=>p.key==='${key}');
        S.runs.set('reading-run',{...pr,runId:'reading-run',status:'running',output:window.readingText,outputOffset:window.readingText.length});
        S.expanded.add('${key}');render();
        window.readingElement=document.querySelector('[data-key="${key}"] .run-output');
        window.readingNode=window.readingElement.firstChild;window.readingElement.scrollTop=400;}`);
      await frame();
      await evaluate(`window.getSelection().setBaseAndExtent(window.readingNode,600,window.readingNode,626);
        window.selectedText=window.getSelection().toString();
        updateCopilotRun({...runForPr('${key}'),output:window.readingText+'Appended synthetic text\\n',
          outputOffset:window.readingText.length+24});renderRows();`);
      await frame();
      assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.selectedText'), 'Append and shared rerender retain selected text');
      assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .run-output')===window.readingElement`), true);
      assert.equal(await evaluate('window.readingElement.firstChild===window.readingNode'), true);
      assert.ok(Math.abs(await evaluate('window.readingElement.scrollTop') - 400) <= 1);
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1});`);
      await frame();
      assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.selectedText'),
        'A same-run snapshot refresh preserves selected text');
      await evaluate(`window.getSelection().removeAllRanges();S.expanded.delete('${key}');renderRows();
        updateCopilotRun({...runForPr('${key}'),output:runForPr('${key}').output+'Hidden append\\n',
          outputOffset:runForPr('${key}').outputOffset+14});S.expanded.add('${key}');renderRows();`);
      await frame();
      assert.ok(Math.abs(await evaluate('window.readingElement.scrollTop') - 400) <= 1, 'Collapse and hidden output keep reading position');
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      await evaluate(`S.q='no matching PR';render();S.q='';render();
        setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1});`);
      await frame();
      assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .run-output')===window.readingElement`), true);
      assert.ok(Math.abs(await evaluate('window.readingElement.scrollTop') - 400) <= 1, 'Filter return and refresh keep the same run position');
      await evaluate(`{const run=runForPr('${key}'),extra='After filter return\\n';
        updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.ok(Math.abs(await evaluate('window.readingElement.scrollTop') - 400) <= 1,
        'The paused intent survives filter return and refresh, including the next update');
      await evaluate(`{const old=runForPr('${key}');
        updateCopilotRun({...old,runId:'different-run',startedAt:new Date().toISOString(),
          output:window.readingText,outputOffset:window.readingText.length});}`);
      await frame();
      const newState = await evaluate(`(()=>{const output=document.querySelector('[data-key="${key}"] .copilot-run:not(.run-earlier) .run-output');
        return {different:output!==window.readingElement,bottom:output.scrollHeight-output.clientHeight-output.scrollTop,
          earlier:document.querySelector('.run-earlier .run-output')===window.readingElement,
          earlierTop:window.readingElement.scrollTop};})()`);
      assert.equal(newState.different && newState.earlier, true, 'A different run never inherits another run output node or intent');
      assert.ok(newState.bottom <= 2 && Math.abs(newState.earlierTop - 400) <= 1);
    });

    await t.test('Fix output keeps retained reading anchors during truncation and never mistakes clamping for following', async () => {
      await reset();
      const key = 'sample-service-id:101';
      await evaluate(`window.truncText=Array.from({length:160},(_,n)=>'Retained synthetic line '+n+'\\n').join('');
        S.runs.set('truncated-run',{...S.snap.prs.find(p=>p.key==='${key}'),runId:'truncated-run',status:'running',
          output:window.truncText,outputOffset:window.truncText.length});
        S.expanded.add('${key}');render();
        window.truncElement=document.querySelector('[data-key="${key}"] .run-output');window.truncElement.scrollTop=600;`);
      const p = await elementPoint(`[data-key="${key}"] .run-output`); await move(p.x, p.y);
      await frame();
      await evaluate(`{const box=window.truncElement.getBoundingClientRect(),style=getComputedStyle(window.truncElement);
        const range=document.caretRangeFromPoint(box.left+parseFloat(style.paddingLeft)+1,box.top+parseFloat(style.paddingTop)+1);
        window.anchorText=window.truncElement.textContent.slice(range.startOffset,range.startOffset+30);
        const run=runForPr('${key}'),cut=run.output.indexOf('\\n',run.output.indexOf('\\n')+1)+1,extra='New tail line\\n';
        updateCopilotRun({...run,output:'[Earlier output truncated]\\n'+run.output.slice(cut)+extra,
          outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.equal(await evaluate(`(()=>{const box=window.truncElement.getBoundingClientRect(),style=getComputedStyle(window.truncElement);
        const range=document.caretRangeFromPoint(box.left+parseFloat(style.paddingLeft)+1,box.top+parseFloat(style.paddingTop)+1);
        return window.truncElement.textContent.slice(range.startOffset,range.startOffset+30);})()`),
        await evaluate('window.anchorText'), 'A retained visible anchor does not shift when the tail drops earlier lines');
      await evaluate(`{const run=runForPr('${key}'),prefix='[Earlier output truncated]\\n';
        const body=run.output.slice(prefix.length),cut=body.indexOf('Retained synthetic line 80\\n'),extra='New tail line\\n';
        updateCopilotRun({...run,output:prefix+body.slice(cut)+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.ok(await evaluate('window.truncElement.scrollTop') <= 1,
        'A removed anchor moves to the retained beginning even when later old output survives');
      await evaluate(`{const run=runForPr('${key}');
        updateCopilotRun({...run,output:'[Earlier output truncated]\\nOnly the new retained tail\\n',outputOffset:run.outputOffset+10000});}`);
      await frame();
      await evaluate(`{const run=runForPr('${key}'),extra='More new output\\n'.repeat(80);
        updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.ok(await evaluate('window.truncElement.scrollTop') <= 1, 'Lost historical position stays at the disclosed retained beginning, not the new tail');
      assert.match(await evaluate('window.truncElement.textContent'), /^\[Earlier output truncated\]/);
      await evaluate(`window.truncElement.scrollTop=window.truncElement.scrollHeight-window.truncElement.clientHeight-1.25;`); await frame();
      await evaluate(`{const run=runForPr('${key}'),extra='Fractional-scroll tail\\n';
        updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.ok(await evaluate('window.truncElement.scrollHeight-window.truncElement.clientHeight-window.truncElement.scrollTop') <= 2,
        'The small bottom tolerance resumes tail following');
    });

    const readingAnchorFixture = async (identical = false, nearBottom = false) => {
      await reset();
      await evaluate(`{const key='sample-service-id:101',prefix='[Earlier output truncated]\\n';
        window.anchorBody=${identical} ? 'Repeated synthetic retained line\\n'.repeat(5000).slice(-(128*1024-prefix.length))
          : Array.from({length:160},(_,n)=>'Anchor synthetic line '+n+'\\n').join('');
        const output=${identical} ? prefix+window.anchorBody : window.anchorBody;
        S.runs.set('anchor-edge-run',{...S.snap.prs.find(p=>p.key===key),runId:'anchor-edge-run',status:'running',
          output,outputOffset:output.length+4096,logPath:'/tmp/synthetic-supervised.log'});
        S.expanded.add(key);render();
        window.anchorElement=document.querySelector('[data-key="'+key+'"] .run-output');
        window.anchorElement.scrollTop=${nearBottom}
          ? window.anchorElement.scrollHeight-window.anchorElement.clientHeight-180 : 600;
        window.anchorSample=()=>{
          const box=window.anchorElement.getBoundingClientRect(),style=getComputedStyle(window.anchorElement);
          const range=document.caretRangeFromPoint(box.left+parseFloat(style.paddingLeft)+1,box.top+parseFloat(style.paddingTop)+1);
          if(range?.startContainer!==window.anchorElement.firstChild)throw Error('Fixture anchor is not in output');
          return {text:window.anchorElement.textContent.slice(range.startOffset,range.startOffset+30),
            top:window.anchorElement.scrollTop,bottom:window.anchorElement.scrollHeight-window.anchorElement.clientHeight-window.anchorElement.scrollTop};
        };}`);
      await elementPoint('[data-key="sample-service-id:101"] .run-output'); await frame();
      await evaluate('window.anchorBefore=window.anchorSample()');
    };

    await t.test('Fix output handles retention advancing with an identical 128 Ki-character displayed tail', async () => {
      await readingAnchorFixture(true);
      await evaluate(`{const run=runForPr('sample-service-id:101');
        updateCopilotRun({...run,outputOffset:run.outputOffset+window.anchorBody.length});}`);
      await frame();
      assert.ok(await evaluate('window.anchorElement.scrollTop') <= 1,
        'Identical text does not hide that the entire old retained body and reading anchor were replaced');
      assert.equal(await evaluate('window.anchorElement.textContent.length'), 128 * 1024);
      assert.match(await evaluate('document.querySelector(".run-worktree").textContent'), /Supervised log: \/tmp\/synthetic-supervised\.log/);
    });

    await t.test('Fix output preserves or honestly loses its logical reading anchor while the panel is offscreen', async () => {
      for (const removed of [false, true]) {
        await readingAnchorFixture();
        await evaluate(`window.anchorSpacer=document.createElement('div');window.anchorSpacer.style.height='2000px';
          document.querySelector('main').prepend(window.anchorSpacer);document.querySelector('main').scrollTop=0;`);
        await frame();
        assert.ok(await evaluate('window.anchorElement.getBoundingClientRect().top') > 1000, 'The update occurs outside the browser viewport');
        await evaluate(`{const run=runForPr('sample-service-id:101'),cut=${removed}
            ? run.output.indexOf('Anchor synthetic line 80\\n') : run.output.indexOf('Anchor synthetic line 2\\n');
          const extra='Offscreen appended line\\n';
          updateCopilotRun({...run,output:'[Earlier output truncated]\\n'+run.output.slice(cut)+extra,
            outputOffset:run.outputOffset+extra.length});
          window.anchorSpacer.remove();window.anchorElement.scrollIntoView({block:'center'});}`);
        await frame();
        if (removed) assert.ok(await evaluate('window.anchorElement.scrollTop') <= 1, 'A removed offscreen anchor shows the retained beginning');
        else assert.equal(await evaluate('window.anchorSample().text'), await evaluate('window.anchorBefore.text'),
          'A retained offscreen anchor still shows the same text on return');
      }
    });

    await t.test('Fix output preserves a surviving anchor when retained-tail shortening clamps browser scrollTop', async () => {
      await readingAnchorFixture(false, true);
      await evaluate(`{const run=runForPr('sample-service-id:101'),cut=run.output.indexOf('Anchor synthetic line 80\\n'),extra='After clamp\\n';
        updateCopilotRun({...run,output:'[Earlier output truncated]\\n'+run.output.slice(cut)+extra,
          outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      const after = await evaluate('window.anchorSample()'), before = await evaluate('window.anchorBefore');
      assert.equal(after.text, before.text, 'Browser clamping must not be counted twice when restoring the retained anchor');
      assert.ok(after.top < before.top && after.bottom > 100, 'The shorter tail keeps the paused reader away from the new bottom');
      await evaluate(`{const run=runForPr('sample-service-id:101'),extra='More after clamp\\n'.repeat(12);
        updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.ok(Math.abs(await evaluate('window.anchorElement.scrollTop') - after.top) <= 1, 'Clamping does not resume following');
    });

    await t.test('Fix output keeps following when wheel or upward keys cannot scroll a short log', async () => {
      for (const input of ['wheel', 'Home', 'ArrowUp', 'PageUp']) {
        await reset();
        await evaluate(`{const key='sample-service-id:101',output='Short synthetic output\\n';
          S.runs.set('short-follow-run',{...S.snap.prs.find(p=>p.key===key),runId:'short-follow-run',status:'running',
            output,outputOffset:output.length});S.expanded.add(key);render();
          window.shortElement=document.querySelector('[data-key="'+key+'"] .run-output');
          window.shortWheelTrusted=false;window.shortElement.addEventListener('wheel',e=>window.shortWheelTrusted=e.isTrusted,{once:true});}`);
        const p = await elementPoint('[data-key="sample-service-id:101"] .run-output');
        if (input === 'wheel') {
          await move(p.x, p.y);
          await send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: p.x, y: p.y, deltaX: 0, deltaY: -600 });
          await evaluate(`new Promise((resolve,reject)=>{
            const deadline=Date.now()+2000;
            const check=()=>window.shortWheelTrusted ? resolve() : Date.now()>deadline
              ? reject(Error('Trusted wheel did not reach the short output panel')) : setTimeout(check,10);check();
          })`);
        } else {
          await evaluate('window.shortElement.focus()');
          const code = { Home: 36, ArrowUp: 38, PageUp: 33 }[input];
          await send('Input.dispatchKeyEvent', { type: 'keyDown', key: input, code: input, windowsVirtualKeyCode: code });
          await send('Input.dispatchKeyEvent', { type: 'keyUp', key: input, code: input, windowsVirtualKeyCode: code });
        }
        await frame();
        await evaluate(`{const run=runForPr('sample-service-id:101'),extra='Now overflowing synthetic output\\n'.repeat(160);
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.ok(await evaluate('window.shortElement.scrollHeight-window.shortElement.clientHeight-window.shortElement.scrollTop') <= 2,
          `${input}: input that never left the bottom must not disable following`);
      }
    });

    await t.test('Fix output preserves forward and backward selection at the exact retained truncation boundary', async () => {
      for (const alreadyTruncated of [false, true]) for (const backward of [false, true]) {
        await reset();
        await evaluate(`{const key='sample-service-id:101',prefix='[Earlier output truncated]\\n';
          const body=Array.from({length:160},(_,n)=>'Selection boundary synthetic line '+n+'\\n').join('');
          const output=${alreadyTruncated} ? prefix+body : body,cut=body.indexOf('Selection boundary synthetic line 2\\n');
          S.runs.set('selection-boundary-run',{...S.snap.prs.find(p=>p.key===key),runId:'selection-boundary-run',
            status:'running',output,outputOffset:output.length});S.expanded.add(key);render();
          window.boundaryElement=document.querySelector('[data-key="'+key+'"] .run-output');
          window.boundaryElement.scrollTop=400;
          const node=window.boundaryElement.firstChild,start=(${alreadyTruncated} ? prefix.length : 0)+cut,end=start+30;
          window.getSelection().setBaseAndExtent(node,${backward} ? end : start,node,${backward} ? start : end);
          window.boundarySelected=window.getSelection().toString();
          const run=runForPr(key),extra='After selection boundary\\n';
          updateCopilotRun({...run,output:prefix+body.slice(cut)+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.boundarySelected'),
          'Every selected character survived; prefix insertion must not add the truncation notice to copied text');
        await evaluate('renderRows()'); await frame();
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.boundarySelected'));
      }
    });

    await t.test('Fix output resumes after clearing a bottom selection but not after retention clamps it there', async () => {
      await reset();
      await evaluate(`{const key='sample-service-id:101',output='Selected bottom synthetic output\\n'.repeat(160);
        S.runs.set('selection-resume-run',{...S.snap.prs.find(p=>p.key===key),runId:'selection-resume-run',
          status:'running',output,outputOffset:output.length});S.expanded.add(key);render();
        window.resumeElement=document.querySelector('[data-key="'+key+'"] .run-output');
        window.resumeElement.scrollTop=400;
        const node=window.resumeElement.firstChild;
        window.getSelection().setBaseAndExtent(node,700,node,730);
        window.resumeSelected=window.getSelection().toString();}`);
      await frame();
      await evaluate('window.resumeElement.scrollTop=window.resumeElement.scrollHeight-window.resumeElement.clientHeight');
      await frame();
      assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.resumeSelected'));
      await evaluate(`window.getSelection().removeAllRanges();
        {const run=runForPr('sample-service-id:101'),extra='After clearing bottom selection\\n'.repeat(40);
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.ok(await evaluate('window.resumeElement.scrollHeight-window.resumeElement.clientHeight-window.resumeElement.scrollTop') <= 2,
        'Clearing the selection after deliberately returning to bottom resumes following on the next append');
      await evaluate(`{const node=window.resumeElement.firstChild;
        window.getSelection().setBaseAndExtent(node,node.length-60,node,node.length-30);renderRows();}`);
      await frame();
      const beforeFilter = await evaluate('window.resumeElement.scrollTop');
      await evaluate(`S.q='No matching synthetic PR';renderRows();S.q='';renderRows();`);
      await frame();
      await evaluate(`{const run=runForPr('sample-service-id:101'),extra='After hiding selected output\\n'.repeat(40);
        updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.equal(await evaluate('window.resumeElement.scrollTop'), beforeFilter,
        'A filter clearing the browser selection preserves the paused reading intent');
      await evaluate('window.resumeElement.scrollTop=window.resumeElement.scrollHeight-window.resumeElement.clientHeight');
      await frame();
      await evaluate(`{const node=window.resumeElement.firstChild;
        window.getSelection().setBaseAndExtent(node,node.length-60,node,node.length-30);renderRows();}`);
      await frame();
      await evaluate(`{const run=runForPr('sample-service-id:101'),extra='Only a new retained beginning\\n';
        updateCopilotRun({...run,output:'[Earlier output truncated]\\n'+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.equal(await evaluate('window.resumeElement.scrollTop'), 0);
      await evaluate(`window.getSelection().removeAllRanges();
        {const run=runForPr('sample-service-id:101'),extra='After selected anchor was lost\\n'.repeat(160);
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
      await frame();
      assert.equal(await evaluate('window.resumeElement.scrollTop'), 0,
        'Losing the selected anchor and clamping to bottom must not count as a deliberate return to the tail');
    });

    await t.test('Fix output preserves a surviving bottom selection resume intent unless retention clamps the viewport', async () => {
      for (const clamped of [false, true]) {
        await reset();
        await evaluate(`{const key='sample-service-id:101',prefix='[Earlier output truncated]\\n';
          const body=Array.from({length:160},(_,n)=>'Surviving selection synthetic line '+n+'\\n').join('')+'Final selected suffix';
          const output=prefix+body;
          S.runs.set('surviving-selection-run',{...S.snap.prs.find(p=>p.key===key),runId:'surviving-selection-run',
            status:'running',output,outputOffset:output.length+50000});S.expanded.add(key);render();
          window.survivingElement=document.querySelector('[data-key="'+key+'"] .run-output');
          const node=window.survivingElement.firstChild;
          window.getSelection().setBaseAndExtent(node,node.length-60,node,node.length-30);
          window.survivingSelected=window.getSelection().toString();renderRows();}`);
        await frame();
        const before = await evaluate('window.survivingElement.scrollTop');
        await evaluate(`{const run=runForPr('sample-service-id:101'),prefix='[Earlier output truncated]\\n';
          const body=run.output.slice(prefix.length),cut=${clamped} ? body.indexOf('\\n')+1 : 1;
          updateCopilotRun({...run,output:prefix+body.slice(cut)+'Z',outputOffset:run.outputOffset+1});}`);
        await frame();
        const retained = await evaluate('window.survivingElement.scrollTop');
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.survivingSelected'));
        if (clamped) assert.ok(retained < before, 'Removing an earlier line forces the bottom viewport to clamp');
        else assert.equal(retained, before, 'Removing and appending a character leaves line geometry and the bottom unchanged');
        await evaluate(`window.getSelection().removeAllRanges();
          {const run=runForPr('sample-service-id:101'),extra='After surviving selection cleared\\n'.repeat(160);
            updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        if (clamped) assert.equal(await evaluate('window.survivingElement.scrollTop'), retained,
          'Clamping is not a deliberate return even when the selected text and reading anchor survive');
        else assert.ok(await evaluate('window.survivingElement.scrollHeight-window.survivingElement.clientHeight-window.survivingElement.scrollTop') <= 2,
          'Clearing a still-retained selection at the unchanged bottom resumes the next append');
      }
    });

    await t.test('Fix output reconciles recovered run promotion and demotion before moving cached output', async () => {
      for (const interacting of [false, true]) {
        await reset();
        await evaluate(`{const key='sample-service-id:101',pr=S.snap.prs.find(p=>p.key===key);
          const older=Array.from({length:160},(_,n)=>'Older recovered synthetic line '+n+'\\n').join('');
          const newer='Newer completed synthetic output\\n'.repeat(160);
          S.runs.set('older-recovered-run',{...pr,runId:'older-recovered-run',status:'needs-attention',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output:older,outputOffset:older.length});
          S.runs.set('newer-completed-run',{...pr,runId:'newer-completed-run',status:'completed',mayBeRunning:false,
            startedAt:'2026-01-02T00:00:00Z',output:newer,outputOffset:newer.length});
          S.runs.set('middle-completed-run',{...pr,runId:'middle-completed-run',status:'completed',mayBeRunning:false,
            startedAt:'2026-01-01T12:00:00Z',output:newer,outputOffset:newer.length});
          S.expanded.add(key);render();
          window.promotionRow=document.querySelector('#rows [data-key="'+key+'"]');
          window.promotionFix=window.promotionRow.querySelector('.row [data-act="triage"]');
          window.olderOutput=window.promotionRow.querySelector('[data-run-id="older-recovered-run"] .run-output');
          window.newerOutput=window.promotionRow.querySelector('[data-run-id="newer-completed-run"] .run-output');
          window.middleOutput=window.promotionRow.querySelector('[data-run-id="middle-completed-run"] .run-output');
          window.olderOutput.scrollTop=400;window.newerOutput.scrollTop=700;
          window.middleOutput.scrollTop=500;
          window.olderNode=window.olderOutput.firstChild;
          window.olderReadingY=()=>{const range=document.createRange(),node=window.olderOutput.firstChild;
            const start=node.data.indexOf('Older recovered synthetic line 30\\n');
            range.setStart(node,start);range.setEnd(node,start+1);
            return range.getBoundingClientRect().top-window.olderOutput.getBoundingClientRect().top;};
          const start=window.olderNode.data.indexOf('Older recovered synthetic line 30\\n');
          window.getSelection().setBaseAndExtent(window.olderNode,start,window.olderNode,start+25);
          window.promotionSelected=window.getSelection().toString();}`);
        await frame();
        const readingY = await evaluate('window.olderReadingY()');
        if (interacting) {
          const p = await point('sample-service-id:101', '.row [data-act="triage"]');
          await move(p.x, p.y);
        }
        await evaluate(`{const run=S.runs.get('older-recovered-run'),extra='Recovered run is now active\\n';
          const cut=run.output.indexOf('Older recovered synthetic line 2\\n');
          updateCopilotRun({...run,status:'recovering',mayBeRunning:true,output:'[Earlier output truncated]\\n'+run.output.slice(cut)+extra,
            outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        const roles = () => evaluate(`[...window.promotionRow.querySelectorAll('.copilot-run')].map(e=>[e.dataset.runId,e.classList.contains('run-earlier')])`);
        if (interacting) {
          assert.deepEqual(await roles(), [['newer-completed-run', false], ['middle-completed-run', true], ['older-recovered-run', true]],
            'While interacting, update the existing recovered card without moving controls or duplicating output');
          assert.equal(await evaluate('window.promotionRow===document.querySelector("#rows [data-key=\\"sample-service-id:101\\"]")'), true);
          assert.equal(await evaluate('window.promotionRow.querySelector(".row [data-act=\\"triage\\"]")===window.promotionFix'), true);
          await move(10, 10); await frame();
          await evaluate('window.promotionRow=document.querySelector("#rows [data-key=\\"sample-service-id:101\\"]")');
        }
        assert.deepEqual(await roles(), [['older-recovered-run', false], ['newer-completed-run', true], ['middle-completed-run', true]]);
        assert.equal(await evaluate('window.promotionRow.querySelector(".copilot-run:not(.run-earlier) .run-output")===window.olderOutput'), true);
        assert.equal(await evaluate('window.promotionRow.querySelector(".run-earlier .run-output")===window.newerOutput'), true);
        assert.equal(await evaluate('window.olderOutput.firstChild===window.olderNode'), true);
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.promotionSelected'));
        assert.ok(Math.abs(await evaluate('window.olderReadingY()') - readingY) <= 1, 'Promotion preserves the retained reading anchor');
        assert.equal(await evaluate('window.newerOutput.scrollTop'), 700);
        assert.equal(await evaluate('window.middleOutput.scrollTop'), 500);
        if (!interacting) {
          assert.equal(await evaluate('window.promotionRow===document.querySelector("#rows [data-key=\\"sample-service-id:101\\"]")'), true);
          assert.equal(await evaluate('window.promotionRow.querySelector(".row [data-act=\\"triage\\"]")===window.promotionFix'), true,
            'Reconciliation does not replace the PR row or its action target');
        }
        assert.equal(await evaluate('window.promotionRow.querySelector(".copilot-run:not(.run-earlier) [data-act=\\"cancelRun\\"]").dataset.runId'), 'older-recovered-run');
        await evaluate(`updateCopilotRun({...S.runs.get('older-recovered-run'),status:'completed',mayBeRunning:false})`);
        await frame();
        assert.deepEqual(await roles(), [['newer-completed-run', false], ['middle-completed-run', true], ['older-recovered-run', true]],
          'Demoting an older recovered run preserves the existing chronological history order');
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.promotionSelected'));
        assert.ok(Math.abs(await evaluate('window.olderReadingY()') - readingY) <= 1, 'Demotion preserves the same retained reading anchor');
        assert.equal(await evaluate('window.newerOutput.scrollTop'), 700);
        assert.equal(await evaluate('window.middleOutput.scrollTop'), 500);
      }
    });

    await t.test('Fix output shelf defers background card changes while hovering or pressing captured cancellation', async () => {
      for (const pressed of [false, true]) {
        await reset(true);
        await evaluate(`{const key='sample-service-id:101',pr=S.snap.prs.find(p=>p.key===key),other=S.snap.prs.find(p=>p.id===104);
          const output='Shelf reading synthetic output\\n'.repeat(160);
          S.runs.set('shelf-older-run',{...pr,runId:'shelf-older-run',status:'needs-attention',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output,outputOffset:output.length});
          S.runs.set('shelf-newer-run',{...pr,runId:'shelf-newer-run',status:'completed',mayBeRunning:false,
            startedAt:'2026-01-02T00:00:00Z',output,outputOffset:output.length});
          S.runs.set('shelf-collapsed-run',{...other,runId:'shelf-collapsed-run',status:'running',output:'Collapsed output\\n',
            outputOffset:'Collapsed output\\n'.length});
          render();setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:[]});}`);
        await frame();
        await click(await point('sample-service-id:101', '.run-heading')); await frame();
        const captured = await point('sample-service-id:101', '.run-earlier [data-act="cancelRun"]');
        await move(captured.x, captured.y);
        if (pressed) await press(captured);
        await evaluate(`window.shelfRow=document.querySelector('#runShelf [data-key="sample-service-id:101"]');
          window.shelfCancel=window.shelfRow.querySelector('.run-earlier [data-act="cancelRun"]');
          window.shelfOutput=window.shelfRow.querySelector('.run-earlier .run-output');
          window.shelfOutput.scrollTop=400;
          const node=window.shelfOutput.firstChild;window.getSelection().setBaseAndExtent(node,700,node,725);
          window.shelfSelected=window.getSelection().toString();`);
        await frame();
        await evaluate(`{const run=S.runs.get('shelf-collapsed-run'),extra='Collapsed background append\\n';
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('window.shelfCancel.isConnected'), true, 'A collapsed run update must not rebuild another captured shelf card');
        await evaluate(`{const run=S.runs.get('shelf-older-run'),extra='Recovery while interacting\\n';
          updateCopilotRun({...run,status:'recovering',output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('window.shelfRow.querySelector(".run-earlier [data-act=\\"cancelRun\\"]")===window.shelfCancel'), true);
        assert.equal(await evaluate('window.shelfCancel.dataset.runId'), 'shelf-older-run');
        assert.equal(await evaluate('window.shelfOutput.scrollTop'), 400);
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.shelfSelected'));
        const hit = await evaluate(`(()=>{const r=window.shelfCancel.getBoundingClientRect();
          return {x:r.x+r.width/2,y:r.y+r.height/2,hit:document.elementFromPoint(${captured.x},${captured.y})?.closest('[data-act]')===window.shelfCancel};})()`);
        assert.ok(Math.abs(hit.x-captured.x)<=1 && Math.abs(hit.y-captured.y)<=1 && hit.hit,
          'The same captured cancellation target remains under the pointer');
        if (!pressed) await press(captured);
        await release(captured); await frame();
        assert.deepEqual((await calls()).filter(c=>c.method==='cancelCopilot'),
          [{method:'cancelCopilot',key:'sample-service-id:101',runId:'shelf-older-run'}]);
        await move(10,10); await evaluate('document.activeElement?.blur()'); await frame();
        assert.equal(await evaluate('S.rowsPending'), false, 'Deferred shelf changes flush after interaction ends');
        assert.equal(await evaluate('document.querySelector("#runShelf [data-run-id=\\"shelf-older-run\\"] .run-output")===window.shelfOutput'), true);
        assert.equal(await evaluate('window.shelfOutput.scrollTop'), 400);
      }
    });

    await t.test('inline output streams without replacing rows, remains on captured PR through refresh/removal, and cancels that run', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await reset(true);
      const key = 'sample-service-id:101';
      const p = await point(key, '.row [data-act="triage"]');
      await click(p); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'startCopilot'), [{ method: 'startCopilot', key }]);
      assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"] .copilot-run .run-output')`), true);
      await evaluate(`window.runRow=document.querySelector('[data-key="${key}"]');
        updateCopilotRun({...runForPr('${key}'),output:'stdout line\\nstderr line\\n<img src=x onerror=alert(1)>',message:'Fake run in progress'});`);
      assert.equal(await evaluate(`window.runRow===document.querySelector('[data-key="${key}"]')`), true, 'Streaming must not replace row DOM');
      assert.equal(await evaluate(`document.querySelector('.run-output img')`), null);
      assert.match(await evaluate(`document.querySelector('.run-output').textContent`), /<img/);
      const contextPreserved = await evaluate(`(()=>{
        const output=document.querySelector('.run-output');
        const e=new MouseEvent('contextmenu',{bubbles:true,cancelable:true});
        output.dispatchEvent(e);return !e.defaultPrevented && S.rowMenu===null;
      })()`);
      assert.equal(contextPreserved, true, 'Inline output must keep its normal copy context menu');
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:S.snap.prs.filter(p=>p.key!=='${key}')});`);
      await frame();
      assert.match(await evaluate(`document.querySelector('#runShelf').textContent`), /!101/);
      assert.equal(await evaluate(`!!document.querySelector('#runShelf .copilot-run')`), false, 'A departed expanded PR starts collapsed on the shelf');
      await click(await point(key, '.run-heading')); await frame();
      const cancel = await point(key, '.copilot-run [data-act="cancelRun"]');
      await click(cancel); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'cancelCopilot'), [{ method: 'cancelCopilot', key, runId: 'offline-101' }]);
      assert.match(await evaluate(`document.querySelector('#runShelf').textContent`), /worktree retained/);
      assert.ok((await calls()).every(c => !['copy', 'triage', 'openUrl'].includes(c.method)), 'Fix PR must not launch/focus/copy another UI');
    });

    await t.test('ignored drafts stay outside attention while their existing Fix controls remain accessible', async () => {
      await reset(true);
      const key = 'sample-service-id:101';
      await click(await point(key, '.row [data-act="triage"]')); await frame();
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      await evaluate(`S.settings.ignoreDrafts=true;
        setSnapshot({...S.snap,prs:S.snap.prs.map(p=>p.key==='${key}'?{...p,isDraft:true,isIgnoredDraft:true}:p)});
        render();`);
      await frame();
      assert.equal(await evaluate(`!!document.querySelector('#rows [data-key="${key}"]')`), false);
      assert.match(await evaluate(`document.querySelector('#runShelf').textContent`), /Ignored draft/);
      assert.equal(await evaluate(`document.querySelector('#draftFilter').hidden`), false);
      await click(await point(key, '.run-heading')); await frame();
      assert.equal(await evaluate(`document.querySelector('#runShelf [data-act="cancelRun"]').hidden`), false);
      const filterPoint = await evaluate(`(()=>{
        const r=document.querySelector('#draftFilter').getBoundingClientRect();
        return {x:r.x+r.width/2,y:r.y+r.height/2};
      })()`);
      await click(filterPoint); await frame();
      assert.equal(await evaluate('S.scope'), 'drafts');
      assert.equal(await evaluate('S.lane'), null);
      assert.deepEqual(await evaluate(`[...document.querySelectorAll('#laneCounters .n')].map(e=>e.textContent)`), ['0', '0', '0', '0']);
      assert.deepEqual(await evaluate(`[...document.querySelectorAll('#rows [data-key="${key}"] .row [data-act]')].map(e=>e.dataset.act)`), ['open']);
      await click(await point(key, '.row [data-act="open"]')); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'openUrl'),
        [{ method: 'openUrl', url: 'https://dev.azure.com/contoso/Sample/_git/sample-service/pullrequest/101' }]);
      await click(await point(key, '.copilot-run [data-act="cancelRun"]')); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'cancelCopilot'), [{ method: 'cancelCopilot', key, runId: 'offline-101' }]);
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      await evaluate(`S.settings.ignoreDrafts=false;S.snap.prs.forEach(p=>p.isIgnoredDraft=false);render();`);
      assert.equal(await evaluate('S.scope'), 'all');
      assert.equal(await evaluate('document.querySelector("#draftFilter").hidden'), true);
    });

    await t.test('hostile PR description and thread comment markdown cannot run script or reach the native bridge', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await reset();
      const key = 'sample-service-id:101';
      const evil = where => {
        const mark = `__xss.push('${where}:`;
        const bridge = `chrome.webview.postMessage(JSON.stringify({id:1,method:'openUrl',params:{url:'https://evil.example/${where}'}}));`
          + `MockHost.rpc('openUrl',{url:'https://evil.example/${where}'});rpc('startCopilot',{key:'${key}'});`;
        return [
          `**${where} bold** with a [benign ${where} link](https://example.com/${where}).`,
          `<script>${mark}script');${bridge}</script>`,
          `<img src="missing-${where}.png" onerror="${mark}img');${bridge}">`,
          `<details open ontoggle="${mark}details');${bridge}"><summary>s</summary>x</details>`,
          `<svg><animate onbegin="${mark}svg');${bridge}" attributeName="x" dur="1s"></animate></svg>`,
          `<iframe srcdoc="<script>parent.${mark}iframe');</script>"></iframe>`,
          `<form action="javascript:${mark}form')"><button formaction="javascript:${mark}formaction')">go</button></form>`,
          `<a href="javascript:${mark}href')">raw js link</a>`,
          `[markdown js link](javascript:${mark}mdlink'))`,
          `<a href="data:text/html,<script>alert(1)</script>">data link</a>`,
        ].join('\n\n');
      };
      const prepared = await evaluate(`(()=>{
        const libs={marked:typeof window.marked?.parse,purify:typeof window.DOMPurify?.sanitize,supported:window.DOMPurify?.isSupported};
        if(libs.marked!=='function'||libs.purify!=='function')return libs;
        window.__xss=[];window.bridge=[];window.sanitized=[];
        window.chrome=window.chrome||{};
        window.chrome.webview={postMessage:m=>window.bridge.push(m),addEventListener(){}};
        if(!window.realSanitize)window.realSanitize=DOMPurify.sanitize;
        DOMPurify.sanitize=(html,cfg)=>{window.sanitized.push(String(html));return window.realSanitize.call(DOMPurify,html,cfg);};
        const snap=structuredClone(S.snap),pr=snap.prs.find(p=>p.key==='${key}');
        pr.description=${JSON.stringify(evil('desc'))};
        pr.threads=[{id:1,status:'active',author:'Hostile Commenter',isBot:false,isAgency:false,
          content:${JSON.stringify(evil('thread'))},replies:0,filePath:null,line:null,updated:pr.updated,url:'#'}];
        setSnapshot({...snap,orderRevision:(S.snap.orderRevision||0)+1});
        window.calls=[];
        return libs;
      })()`);
      assert.deepEqual(prepared, { marked: 'function', purify: 'function', supported: true }, 'Vendored marked and DOMPurify must be loaded');
      await click(await point(key, '.row .title'));
      await frame();
      await evaluate('new Promise(resolve=>setTimeout(resolve,750))');
      await frame();
      const result = await evaluate(`(()=>{
        const row=document.querySelector('[data-key="${key}"]');
        const bodies={desc:row.querySelector('.detail .body.md'),thread:row.querySelector('.detail .thread .md')};
        const inspect=el=>({
          strong:el.querySelector('strong')?.textContent,
          literalMarkdown:el.textContent.includes('**'),
          links:[...el.querySelectorAll('a')].map(a=>({text:a.textContent,href:a.getAttribute('href'),target:a.getAttribute('target'),rel:a.getAttribute('rel')})),
          hazards:[...el.querySelectorAll('script,iframe,object,embed,frame,frameset,base,meta,link,style')].map(n=>n.tagName),
          badAttrs:[...el.querySelectorAll('*')].flatMap(n=>[...n.attributes].filter(a=>/^on/i.test(a.name)||/^\\s*(javascript|data|vbscript):/i.test(a.value)||a.name==='srcdoc'||a.name==='formaction').map(a=>n.tagName+' '+a.name)),
        });
        return {xss:window.__xss,bridge:window.bridge,calls:window.calls,
          sanitizedHostile:window.sanitized.filter(h=>/onerror=/.test(h)&&/<script>/.test(h)).length,
          desc:inspect(bodies.desc),thread:inspect(bodies.thread)};
      })()`);
      assert.deepEqual(result.xss, [], 'No hostile script, handler or URL may execute');
      assert.deepEqual(result.bridge, [], 'Hostile content must not post native-bridge messages');
      assert.deepEqual(result.calls, [], 'Rendering details must not issue privileged host requests');
      assert.ok(result.sanitizedHostile >= 2, 'Both hostile markdown bodies must reach the real DOMPurify sanitizer');
      for (const where of ['desc', 'thread']) {
        const r = result[where];
        assert.equal(r.strong, `${where} bold`, `${where}: real marked formatting must still render`);
        assert.equal(r.literalMarkdown, false, `${where}: escape-only fallback must not be used`);
        assert.deepEqual(r.hazards, [], `${where}: hazardous elements must be removed`);
        assert.deepEqual(r.badAttrs, [], `${where}: event handlers and script URLs must be removed`);
        assert.deepEqual(r.links.find(l => l.text === `benign ${where} link`),
          { text: `benign ${where} link`, href: `https://example.com/${where}`, target: '_blank', rel: 'noopener' });
        for (const l of r.links) assert.ok(l.href === null || /^https:\/\/example\.com\//.test(l.href), `${where}: unexpected link ${l.href}`);
      }
      await evaluate(`DOMPurify.sanitize=window.realSanitize;delete window.chrome.webview;`);
    });

    await t.test('both themes apply to real components, states, contrast and saved reloads', async () => {
      const navigate = async url => {
        await evaluate('delete window.__ready');
        await send('Page.navigate', { url });
        await evaluate(`new Promise((resolve,reject)=>{
          const deadline=Date.now()+10000;
          const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot')):setTimeout(check,20);check();
        })`);
        await frame();
      };
      for (const hint of ['', '&theme=github']) {
        await navigate(pathToFileURL(root).href + '?mock=1' + hint);
        assert.deepEqual(await evaluate('({applied:document.documentElement.dataset.theme,saved:S.settings.theme})'),
          {applied:'light',saved:'light'},'Missing preference and the legacy GitHub hint use canonical Light Mode');
      }
      await evaluate(`openDrawer();document.querySelector('#theme').focus();`);
      assert.deepEqual(await evaluate("[...document.querySelector('#theme').options].map(o=>({value:o.value,label:o.textContent}))"),
        [{value:'light',label:'Light Mode'},{value:'dark',label:'Dark Mode'}]);
      for (const [expected,key,code] of [['dark','ArrowDown',40],['light','ArrowUp',38],['dark','ArrowDown',40]]) {
        await send('Input.dispatchKeyEvent',{type:'keyDown',key,code:key,windowsVirtualKeyCode:code});
        await send('Input.dispatchKeyEvent',{type:'keyUp',key,code:key,windowsVirtualKeyCode:code});
        await evaluate(`new Promise((resolve,reject)=>{
          const deadline=Date.now()+5000;
          const check=()=>!S.themeBusy?resolve():Date.now()>deadline?reject(Error('Theme save did not settle')):requestAnimationFrame(check);check();
        })`);
        assert.deepEqual(await evaluate(`({theme:S.settings.theme,focus:document.activeElement.id})`),
          {theme:expected,focus:'theme'},'Keyboard theme selection preserves focus for the next arrow key');
      }
      await evaluate('closeDrawer()');
      const screenshotDir = process.env.ADOPRVIEW_THEME_SCREENSHOTS;
      if (screenshotDir) await mkdir(screenshotDir, { recursive: true });
      const screenshot = async name => {
        if (!screenshotDir) return;
        const { data } = await send('Page.captureScreenshot', { format: 'png' });
        await writeFile(path.join(screenshotDir, name + '.png'), Buffer.from(data, 'base64'));
      };
      for (const [theme, background] of [['light','rgb(255, 255, 255)'],['dark','rgb(14, 16, 20)']]) {
        await evaluate('openDrawer()');
        await evaluate(`new Promise((resolve,reject)=>{
          const select=document.querySelector('#theme');
          const old=window.MockHost.rpc.bind(window.MockHost);
          window.themeCalls=[];
          window.MockHost.rpc=async(method,p)=>{
            window.themeCalls.push({method,...p});
            if(method==='setTheme')try{return await old(method,p);}finally{window.MockHost.rpc=old;}
            throw Error('Theme change must not invoke '+method);
          };
          document.querySelector('#refreshSeconds').value='777';
          select.value='${theme}';
          select.dispatchEvent(new Event('change',{bubbles:true}));
          const check=()=>S.themeBusy?requestAnimationFrame(check):resolve();
          check();
        })`);
        await frame();
        assert.deepEqual(await evaluate('window.themeCalls'), [{method:'setTheme',theme}]);
        assert.equal(await evaluate("document.querySelector('#refreshSeconds').value"),'777','Switching must preserve unfinished drawer edits');
        assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'),background);
        assert.equal(await evaluate('getComputedStyle(document.documentElement).colorScheme'),theme === 'dark' ? 'dark' : 'light');
        assert.equal(await evaluate('document.activeElement?.id==="theme" || !!document.querySelector("#theme").focus'),true);
        await evaluate(`document.querySelector('#theme').focus();`);
        await frame();
        assert.equal(await evaluate('getComputedStyle(document.activeElement).outlineStyle'),'solid');
        await screenshot(theme + '-settings');
        await evaluate('closeDrawer()');
        await frame();
        await screenshot(theme + '-overview');

        await evaluate(`closeDrawer();
          window.themeFixture=structuredClone(S.snap);
          S.expanded.add('sample-service-id:105');
          const pr=S.snap.prs.find(p=>p.id===105);
          pr.gates.checks={state:'human',label:'Needs review'};
          pr.description+='\\n\\n> Review before merging.\\n\\n\`\`\`text\\nSynthetic code block\\n\`\`\`\\n\\n[A documentation link](https://example.com)';
          pr.isDraft=true;
          S.busy.add(pr.key+':approve');
          for(const build of pr.failedBuilds)for(const task of build.tasks){
            const key=logKey(pr,build.buildId,task.logId);
            S.logs.set(key,'2026-01-01T10:00:00.000Z ##[error] Synthetic error\\n2026-01-01T10:00:01.000Z warning: Synthetic warning\\n2026-01-01T10:00:02.000Z --- PASS: Synthetic pass');
            S.showTs.add(key);
          }
          S.runs.set('theme-run',{key:pr.key,runId:'theme-run',status:'failed',message:'Synthetic failed Fix; no process was started',output:'Synthetic CLI output\\nNo live commands',worktreePath:'/tmp/synthetic',worktreeRetained:true});
          updateExplanation({key:pr.key,requestId:'theme-explain',sequence:1,status:'completed',message:'Synthetic summary',
            output:'## Context\\nThe **offline component** renders \`code\` and [link](https://example.com).',coverage:['Synthetic fixture only']});
          S.approvals.set(pr.key,{kind:'returned',message:'Synthetic response',response:'Synthetic provider output'});
          renderRows();
          document.querySelector('[data-key="sample-service-id:105"]').scrollIntoView({block:'start'});
          toast('Synthetic error notification','error');`);
        await frame();
        const results = await evaluate(`(()=>{
          const canvas=document.createElement('canvas');canvas.width=canvas.height=1;
          const ctx=canvas.getContext('2d',{willReadFrequently:true});
          const rgba=value=>{ctx.clearRect(0,0,1,1);ctx.fillStyle=value;ctx.fillRect(0,0,1,1);return [...ctx.getImageData(0,0,1,1).data].map((v,i)=>i===3?v/255:v);};
          const blend=(a,b)=>a.slice(0,3).map((v,i)=>v*a[3]+b[i]*(1-a[3])).concat(1);
          const bg=el=>{const chain=[];for(let n=el;n;n=n.parentElement)chain.unshift(n);return chain.reduce((c,n)=>blend(rgba(getComputedStyle(n).backgroundColor),c),[255,255,255,1]);};
          const lum=c=>c.slice(0,3).map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4;}).reduce((n,v,i)=>n+v*[.2126,.7152,.0722][i],0);
          window.themeContrast=el=>{const b=bg(el),f=blend(rgba(getComputedStyle(el).color),b),l=[lum(b),lum(f)].sort((a,b)=>b-a);return (l[0]+.05)/(l[1]+.05);};
          const scope=document.querySelector('[data-key="sample-service-id:105"]');
          const selectors=['.title','.meta .id','.row-lane','.pill.ok','.pill.fail','.pill.human','.tag.draft','.card h3','.md','.md code','.md pre code','.md blockquote','.md a','.failed h3','.issues','.log','.log .e','.log .w','.log .s','.log .t','.run-output','.explain-output','.approval-response','.small'];
          return selectors.map(selector=>{
            const el=scope.querySelector(selector);
            if(!el)return {selector,missing:true};
            const cs=getComputedStyle(el);
            return {selector,ratio:window.themeContrast(el),color:cs.color,background:cs.backgroundColor};
          });
        })()`);
        for (const result of results) {
          assert.ok(!result.missing, `${theme}: missing representative ${result.selector}`);
          assert.ok(result.ratio >= 4.5, `${theme} ${result.selector}: contrast ${result.ratio.toFixed(2)} (${result.color} / ${result.background})`);
        }
        await screenshot(theme + '-details');
        await evaluate(`openDrawer();`);
        await frame();
        for (const selector of ['.drawer h2','.help','.field label','.field input','.field select','.btn','.btn.primary']) {
          const ratio=await evaluate(`window.themeContrast(document.querySelector(${JSON.stringify(selector)}))`);
          assert.ok(ratio>=4.5,`${theme} ${selector}: contrast ${ratio.toFixed(2)}`);
        }
        for (const selector of ['#repoUrl','#refreshSeconds','#triageInstructions']) {
          const focus=await evaluate(`(()=>{
            const e=document.querySelector(${JSON.stringify(selector)});e.focus({preventScroll:true});
            const c=getComputedStyle(e);return {active:document.activeElement===e,style:c.outlineStyle,width:c.outlineWidth};
          })()`);
          assert.deepEqual(focus,{active:true,style:'solid',width:'2px'},`${theme}: form focus remains visible on ${selector}`);
        }
        const primary = await evaluate(`(()=>{const b=document.querySelector('.btn.primary');const r=b.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2,background:getComputedStyle(b).backgroundColor};})()`);
        await move(primary.x,primary.y); await frame();
        const hovered = await evaluate(`({background:getComputedStyle(document.querySelector('.btn.primary')).backgroundColor,ratio:window.themeContrast(document.querySelector('.btn.primary'))})`);
        assert.notEqual(hovered.background,primary.background,`${theme}: primary hover is visible`);
        assert.ok(hovered.ratio>=4.5,`${theme}: primary hover contrast ${hovered.ratio}`);
        await evaluate(`closeDrawer();`);
        for (const width of [1600,900]) {
          await send('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:false});
          await frame();
          assert.ok(await evaluate(`document.documentElement.scrollWidth<=innerWidth`), `${theme}: page itself should not overflow at ${width}`);
          const tableScroll=await evaluate(`(()=>{const m=document.querySelector('main');m.scrollLeft=m.scrollWidth;return m.scrollLeft>0||m.scrollWidth<=m.clientWidth;})()`);
          assert.equal(tableScroll,true,'Wide tables stay locally scrollable');
          await screenshot(theme + '-' + width);
        }
        await evaluate(`openRowMenu({target:document.querySelector('[data-key="sample-service-id:105"] .title'),clientX:250,clientY:250,preventDefault(){}});`);
        await frame();
        assert.ok(await evaluate(`window.themeContrast(document.querySelector('#rowMenu button'))`)>=4.5,`${theme}: menu text contrast`);
        await evaluate('closeRowMenu()');
        const savedUrl=await evaluate('location.href');
        await navigate(savedUrl);
        assert.equal(await evaluate('S.settings.theme'),theme,'Saved mock hint survives reload (native persistence is covered separately)');
        assert.equal(await evaluate('getComputedStyle(document.body).backgroundColor'),background);
        await send('Emulation.setDeviceMetricsOverride',{width:1600,height:1000,deviceScaleFactor:1,mobile:false});
      }
    });

    await send('Browser.close', {}, false);
  } finally {
    socket?.close();
    if (child.exitCode === null) await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 3000))]);
    if (child.exitCode === null) { child.kill(); await exited; }
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
});
