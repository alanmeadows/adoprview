const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm, mkdir, writeFile, readdir, rmdir } = require('node:fs/promises');
const { existsSync } = require('node:fs');
const { once } = require('node:events');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

test('Additional Fix fields are independently editable and restored in the real source GUI', { timeout: 60000 }, async () => {
  const profile = await mkdtemp(path.join(os.tmpdir(), 'ado-fix-fields-'));
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
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data), request = pending.get(message.id);
      if (!request) return;
      pending.delete(message.id);
      message.error ? request.reject(new Error(JSON.stringify(message.error))) : request.resolve(message.result);
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
      if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
      return result.result.value;
    };
    const click = async selector => {
      const point = await evaluate(`(()=>{
        document.querySelector('#toasts').replaceChildren();
        const el=document.querySelector(${JSON.stringify(selector)});
        if(!el)throw Error('Missing control');
        el.scrollIntoView({block:'center'});
        const r=el.getBoundingClientRect(),x=r.x+r.width/2,y=r.y+r.height/2;
        const hit=document.elementFromPoint(x,y);
        if(!el.contains(hit))throw Error('Control is not hit-testable: '+${JSON.stringify(selector)}+' covered by '+(hit?.className||hit?.tagName||'none'));
        return {x,y};
      })()`);
      for (const type of ['mousePressed', 'mouseReleased'])
        await send('Input.dispatchMouseEvent', { type, ...point, button: 'left', clickCount: 1 });
    };
    await send('Page.enable');
    await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    const root = path.resolve(__dirname, '..', 'src', 'AdoPrView', 'wwwroot', 'index.html');
    await send('Page.navigate', { url: pathToFileURL(root).href + '?mock=1' });
    await evaluate(`new Promise((resolve,reject)=>{
      const deadline=Date.now()+10000;
      const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot: '+document.querySelector('.boot-error')?.textContent)):setTimeout(check,20);check();
    })`);
    await evaluate(`
      S.snap.approvalProvider=null;
      S.settings.triageInstructions=null;S.settings.additionalTriageInstructions=null;
      S.settings.futureSetting='SYNTHETIC-KEEP';
      S.settings.wslDistro='Legacy-Ubuntu';S.settings.wslRepoDirectories={'offline/project/repo':'/legacy/source'};
      S.settings.nativeRepoDirectories={};
      window.fixSaved=[];
      window.fixExisting=['running','recovering','completed'].map((status,i)=>({
        key:'offline:'+i,runId:'existing-'+i,status,output:'Captured session '+i
      }));
      for(const run of window.fixExisting)S.runs.set(run.runId,run);
      window.MockHost.rpc=async(method,p)=>{
        if(method!=='saveSettings')throw Error('Unexpected host call '+method);
        window.fixSaved.push(structuredClone(p.settings));
        return structuredClone(p.settings);
      };
    `);
    await click('#settingsBtn');
    assert.equal(await evaluate(`!!document.querySelector('[data-native-repo]')&&!document.querySelector('[data-wsl-repo]')&&!document.querySelector('#wslDistro')`), true);
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('.native-repo label small')).display`), 'block');
    const nativeKey = await evaluate(`document.querySelector('[data-native-repo]').dataset.nativeRepo`);
    const nativeDirectory = 'C:\\synthetic\\checkout with spaces';
    await click('[data-native-repo]');
    await send('Input.insertText', { text: nativeDirectory });
    const labels = await evaluate(`['triageInstructions','additionalTriageInstructions'].map(id=>
      document.querySelector('label[for="'+id+'"]').textContent)`);
    assert.deepEqual(labels, ['Custom Ask (override)', 'Additional instructions (append)']);
    const ask = 'CUSTOM ASK: fix only the synthetic issue';
    const extra = '\n  EXTRA GUIDANCE\nKeep the final newline.\n';
    await click('#triageInstructions');
    await send('Input.insertText', { text: ask });
    await click('#additionalTriageInstructions');
    await send('Input.insertText', { text: extra });
    await click('[data-dact="save"]');
    assert.equal(await evaluate('window.fixSaved.length'), 1);
    const saved = await evaluate('window.fixSaved[0]');
    assert.equal(saved.triageInstructions, ask);
    assert.equal(saved.additionalTriageInstructions, extra);
    assert.equal(saved.futureSetting, 'SYNTHETIC-KEEP');
    assert.equal(saved.nativeRepoDirectories[nativeKey], nativeDirectory);
    assert.equal(saved.wslDistro, 'Legacy-Ubuntu');
    assert.equal(saved.wslRepoDirectories['offline/project/repo'], '/legacy/source');
    await evaluate('closeDrawer();S.settings=structuredClone(window.fixSaved[0]);');
    await click('#settingsBtn');
    assert.equal(await evaluate(`document.querySelector('[data-native-repo]').value`), nativeDirectory);
    assert.deepEqual(await evaluate(`['triageInstructions','additionalTriageInstructions'].map(id=>
      document.getElementById(id).value)`), [ask, extra]);
    await click('[data-dact="save"]');
    assert.equal(await evaluate('window.fixSaved[1].additionalTriageInstructions'), extra);
    await evaluate(`document.querySelector('#triageInstructions').closest('.section').scrollIntoView({block:'center'});`);
    if (process.env.ADOPRVIEW_FIX_SCREENSHOT) {
      await evaluate('new Promise(resolve=>setTimeout(resolve,250))');
      const screenshot = await send('Page.captureScreenshot', { format: 'png' });
      await writeFile(process.env.ADOPRVIEW_FIX_SCREENSHOT, Buffer.from(screenshot.data, 'base64'));
    }
    await click('#additionalTriageInstructions');
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2 });
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8 });
    await click('[data-dact="save"]');
    assert.equal(await evaluate('window.fixSaved[2].additionalTriageInstructions'), '');
    assert.equal(await evaluate('window.fixSaved[2].triageInstructions'), ask);
    for (const original of ['\n  CRLF guidance\r\nverbatim\n', '\r  CR guidance\rverbatim\r']) {
      const normalized = original.replace(/\r\n?/g, '\n');
      await evaluate(`S.settings.additionalTriageInstructions=${JSON.stringify(original)};renderDrawer();`);
      assert.equal(await evaluate("document.querySelector('#additionalTriageInstructions').value"), normalized);
      await click('[data-dact="save"]');
      assert.equal(await evaluate('window.fixSaved.at(-1).additionalTriageInstructions'), original);
      await evaluate('closeDrawer();S.settings=structuredClone(window.fixSaved.at(-1));');
      await click('#settingsBtn');
      assert.equal(await evaluate("document.querySelector('#additionalTriageInstructions').value"), normalized);
      await click('#additionalTriageInstructions');
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'a', code: 'KeyA', windowsVirtualKeyCode: 65, modifiers: 2 });
      const edited = normalized + 'EDITED';
      await send('Input.insertText', { text: edited });
      await click('[data-dact="save"]');
      assert.equal(await evaluate('window.fixSaved.at(-1).additionalTriageInstructions'), edited);
      assert.equal(await evaluate('window.fixSaved.at(-1).triageInstructions'), ask);
    }
    assert.equal(await evaluate('window.fixExisting.every(run=>S.runs.get(run.runId)===run)'), true);
    await send('Browser.close', {}, false);
  } finally {
    socket?.close();
    if (child.exitCode === null) child.kill();
    await exited;
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
});

// No browser packages required: Node 22+ and a local Chromium browser.
test('real row hit testing and action identity across refreshes', { timeout: 90000 }, async t => {
  if (process.env.ROW_TEST_FILTER) {
    const run = t.test.bind(t), names = process.env.ROW_TEST_FILTER.split('|');
    t.test = (name, ...args) => names.some(part => name.includes(part)) ? run(name, ...args) : Promise.resolve();
  }
  const proofDir = process.env.ADOPRVIEW_EXPLAIN_PROOF_DIR;
  const profile = proofDir ? path.join(proofDir, 'edge-profile') : await mkdtemp(path.join(os.tmpdir(), 'ado-row-tests-'));
  if (proofDir) {
    assert.equal(existsSync(profile), false, 'Proof profile must not preexist');
    await mkdir(profile);
  }
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
    const keyboard = async (key, windowsVirtualKeyCode, modifiers = 0) => {
      const code = key === ' ' ? 'Space' : key;
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode, modifiers,
        ...(key === 'Enter' ? { text: '\r' } : {}) });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode, modifiers });
      await frame();
    };
    await send('Page.enable');
    if (proofDir) {
      await send('Network.enable');
      await send('Network.setBlockedURLs', { urls: ['http://*', 'https://*'] });
      await writeFile(path.join(proofDir, 'browser.json'), JSON.stringify({ pid: child.pid, browser, profile, profilePreabsent: true }));
    }
    await send('Page.bringToFront');
    await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'no-preference' }] });
    await send('Page.addScriptToEvaluateOnNewDocument', { source: `
      window.rowIntervals=[];
      const interval=window.setInterval.bind(window);
      window.setInterval=(fn,ms)=>{window.rowIntervals.push({fn,ms});return interval(fn,ms);};
    ` });
    await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    const root = process.env.ROW_TEST_GUI_ROOT || path.resolve(__dirname, '..', 'src', 'AdoPrView', 'wwwroot', 'index.html');
    await send('Page.navigate', { url: pathToFileURL(root).href + '?mock=1' });
    await evaluate(`new Promise((resolve,reject)=>{
      const deadline=Date.now()+10000;
      const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot: '+document.querySelector('.boot-error')?.textContent)):setTimeout(check,20);check();
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
        document.querySelector('#search').value='';
        S.rowOrder=[];S.orderRevision=null;S.laneRetained.clear();S.runs.clear();S.runHistoryOpen.clear();S.busy.clear();closeRowMenu();
        window.holdLog=false;S.settings.hidden=['sample-deploy-id:1001'];S.settings.ignoreDrafts=false;S.hideUndo=[];
        window.confirmations=[];window.confirmResult=true;window.autoFailure=false;S.autoCompleteErrors.clear();
        window.explainSequence=0;S.explanations.clear();S.explanationPrs?.clear();explanationArtifacts.clear();investigationDrafts.clear();window.videoSequence=0;S.videos.clear();
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

    await t.test('optional pipelines never become primary failed gates and keep exact secondary actions', async () => {
      const key = 'sample-service-id:101';
      for (const requiredFailed of [false, true]) {
        await reset(true);
        await evaluate(`(()=>{
          const snap=structuredClone(S.snap),pr=snap.prs.find(p=>p.key==='${key}');
          pr.approval=null;pr.error=null;pr.blockers=${JSON.stringify(requiredFailed ? ['Build failed: Required CI'] : [])};
          pr.waiting=[];pr.humanActions=['pop'];pr.lane='${requiredFailed ? 'blocked' : 'needsHuman'}';
          pr.gates.build={state:'${requiredFailed ? 'fail' : 'ok'}',label:'${requiredFailed ? 'Failed' : 'Passed'}',detail:'Required CI'};
          pr.policies=[
            {evaluationId:'required',kind:'build',name:'Required CI',isBlocking:true,status:'${requiredFailed ? 'rejected' : 'approved'}'},
            {evaluationId:'optional-running',kind:'build',name:'Optional running',isBlocking:false,status:'running'},
            {evaluationId:'optional-failed',kind:'build',name:'Optional failed',isBlocking:false,status:'rejected'}
          ];
          pr.failedBuilds=[
            {buildId:222,evaluationId:'optional-failed',name:'Optional failed',isBlocking:false,url:'#',
             tasks:[{name:'Optional task',issues:['Optional issue'],logId:22}]}
          ];
          ${requiredFailed ? `pr.failedBuilds.push({buildId:111,evaluationId:'required',name:'Required CI',isBlocking:true,url:'#',
             tasks:[{name:'Required task',issues:['Required issue'],logId:11}]});` : ''}
          S.logs.clear();setSnapshot(snap);S.expanded.add('${key}');renderRows();
        })()`);
        await frame();
        const details = await evaluate(`(()=>{
          const row=document.querySelector('[data-key="${key}"]');
          return {primary:[...row.querySelectorAll('.card.failed h3')].map(e=>e.textContent),
            secondary:row.querySelector('.optional-build')?.textContent,
            status:row.querySelector('.pr-status')?.textContent,
            optionalLabel:row.querySelector('.optional-build')?.closest('.full')!==null};
        })()`);
        assert.equal(details.primary.length, requiredFailed ? 1 : 0);
        if (requiredFailed) assert.match(details.primary[0], /Failed gate.*Required CI/);
        assert.match(details.secondary, /Optional failed build \(not a merge blocker\).*Optional failed/s);
        assert.match(details.secondary, /Optional issue/);
        assert.equal(details.optionalLabel, false, 'Optional failure must not enter full-width primary diagnostics');
        assert.doesNotMatch(details.status, /Optional/);
        const logReads = (await calls()).filter(c => c.method === 'logTail');
        assert.deepEqual(logReads.map(c => c.buildId), requiredFailed ? [111] : []);
        await click(await point(key, '.optional-build [data-act="loadLog"]'));
        await frame();
        assert.equal((await calls()).filter(c => c.method === 'logTail').at(-1).buildId, 222);
        assert.equal((await calls()).filter(c => c.method === 'logTail').at(-1).logId, 22);
        await click(await point(key, '.optional-build [data-act="requeue"]'));
        await frame();
        assert.equal((await calls()).filter(c => c.method === 'requeue').at(-1).evaluationId, 'optional-failed');
      }
    });

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
        assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"] .explain-waiting:not([hidden]) .video-spinner')`),true);
        await evaluate(`window.explainWaiting=document.querySelector('[data-key="${key}"] .explain-waiting');
          window.explainOutput=document.querySelector('[data-key="${key}"] .explain-output');
          window.explainViewport=document.querySelector('main').scrollTop;`);
        await evaluate(`updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,
          output:'Partial <script>window.explainInjected=true</script>',coverage:['Binary payload omitted'],
          evidence:['after: /README.md, lines 1-12.']});`);
        assert.equal(await evaluate(`window.originalFix===document.querySelector('[data-key="${key}"] .row [data-act="triage"]')`),true);
        assert.equal(await evaluate('window.explainInjected===true'),false);
        assert.equal(await evaluate(`window.explainOutput===document.querySelector('[data-key="${key}"] .explain-output') &&
          window.explainWaiting===document.querySelector('[data-key="${key}"] .explain-waiting') &&
          window.explainOutput.hidden && window.explainOutput.textContent==='' &&
          document.querySelector('main').scrollTop===window.explainViewport`),true);
        assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .explain-details').open`),false);
        await click(await point(key,'.detail [data-act="cancelExplanation"]')); await frame();
        const cancel=(await calls()).find(c=>c.method==='cancelExplanation');
        assert.equal(cancel.key,key);
        assert.equal(cancel.requestId,'offline-explain-1');
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] [data-act="explainPr"]').textContent`),/Retry Explain/);
        assert.equal(await evaluate(`!document.querySelector('[data-key="${key}"] .explain-waiting:not([hidden])') &&
          document.querySelector('[data-key="${key}"] .explain-output').textContent===''`),true);
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
        assert.equal(await evaluate(`!document.querySelector('[data-key="${key}"] .explain-waiting:not([hidden])') &&
          document.querySelector('[data-key="${key}"] .explain-output').hidden &&
          !document.querySelector('[data-key="${key}"] .explanation').textContent.includes('partial response')`),true);
        assert.deepEqual((await calls()).map(c=>c.method),['explainPr','cancelExplanation','explainPr']);
      }
      await send('Emulation.setDeviceMetricsOverride', {width:1600,height:1000,deviceScaleFactor:1,mobile:false});
    });

    await t.test('Explain completed reading position selection and output identity survive updates and rerenders', async () => {
      const key='sample-service-id:101';
      await reset(true);
      await evaluate(`S.expanded.add('${key}');renderRows();
        updateExplanation({key:'${key}',requestId:'reading-final',sequence:++window.explainSequence,status:'completed',
          message:'Ready',output:Array.from({length:80},(_,i)=>'Paragraph '+i+': completed prose.').join('\\n\\n'),
          coverage:[],revision:{iteration:4,sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40)}});
        window.readingOutput=document.querySelector('[data-key="${key}"] .explain-output');
        window.readingOutput.scrollTop=200;
        window.readingNode=window.readingOutput.querySelector('p').firstChild;
        window.getSelection().setBaseAndExtent(window.readingNode,0,window.readingNode,11);
        window.readingSelection=window.getSelection().toString();
        window.readingTop=window.readingOutput.scrollTop;
        window.readingViewport=document.querySelector('main').scrollTop;`);
      for (const mutation of [
        `updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,message:'Still ready'});`,
        `renderInvestigation('${key}');`,
        'renderRows();',
      ]) {
        await evaluate(mutation);
        assert.equal(await evaluate(`window.readingOutput===document.querySelector('[data-key="${key}"] .explain-output') &&
          window.readingOutput.scrollTop===window.readingTop &&
          window.getSelection().toString()===window.readingSelection &&
          window.getSelection().anchorNode===window.readingNode &&
          document.querySelector('main').scrollTop===window.readingViewport`),true,mutation);
      }
      await evaluate(`window.getSelection().removeAllRanges();`);
      await evaluate(`S.explanations.get('${key}').evidence=Array.from({length:80},(_,i)=>'Evidence paragraph '+i);
        S.snap.prs=S.snap.prs.filter(p=>p.key==='${key}');renderRows();
        renderInvestigation('${key}');
        document.querySelector('[data-key="${key}"] .assistance-explanation .explain-details').open=true;
        document.querySelector('main').scrollTop=document.querySelector('main').scrollHeight;
        window.expandedEvidenceTop=document.querySelector('main').scrollTop;
        renderInvestigation('${key}');`);
      assert.equal(await evaluate(`document.querySelector('main').scrollTop`),
        await evaluate('window.expandedEvidenceTop'), 'Reopening evidence precedes output scroll restoration so the viewport is not clamped');
    });

    await t.test('Explain optional Mermaid diagrams render offline and retain completed reading state across async rerenders', async sub => {
      const key = 'sample-service-id:101';
      const output = 'The reader receives pinned evidence before producing the explanation.\n\n```mermaid\nflowchart LR\n  accTitle: Evidence flow\n  accDescr: The reader and pinned evidence exchange context, which produces the explanation.\n  Class["PR reader $& $$"]:::explain-diagram <--> Link["Pinned evidence"]:::explain-diagram-work\n  Link -.-> Result["Completed explanation"]\n```\n\n```mermaid\nsequenceDiagram\n  participant Reader\n  participant Host\n  Reader->>Host: Read pinned source\n  Host<<-->>Reader: Evidence\n```';
      await reset(true);
      sub.after(async () => {
        await evaluate('mermaid.render=window.realDiagramRender;window.releaseDiagram?.();');
        await evaluate('window.removeEventListener("unhandledrejection",window.diagramRejectHandler);');
        await send('Network.setBlockedURLs', { urls: [] });
      });
      await send('Network.enable');
      await send('Network.setBlockedURLs', { urls: ['http://*', 'https://*'] });
      await evaluate(`S.expanded.add('${key}');renderRows();
        window.realDiagramRender=mermaid.render.bind(mermaid);window.diagramCalls=0;
        window.diagramUnhandled=[];window.diagramRejectHandler=e=>{window.diagramUnhandled.push(e.reason.message);e.preventDefault();};
        window.addEventListener('unhandledrejection',window.diagramRejectHandler);
        window.diagramGate=new Promise(resolve=>window.releaseDiagram=resolve);
        mermaid.render=(render=>async(...args)=>{window.diagramCalls++;await window.diagramGate;return render(...args);})(window.realDiagramRender);
        updateExplanation({key:'${key}',requestId:'diagram-final',sequence:++window.explainSequence,status:'running',
          message:'Reading',output:${JSON.stringify(output)},coverage:[]});`);
      assert.equal(await evaluate(`!document.querySelector('[data-key="${key}"] .explain-diagram') && window.diagramCalls===0`), true);
      await evaluate(`updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,status:'completed',message:'Ready',
          revision:{iteration:4,sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40)}});
        window.diagramOutput=document.querySelector('[data-key="${key}"] .explain-output');
        window.diagramParagraph=window.diagramOutput.querySelector('p').firstChild;
        window.getSelection().setBaseAndExtent(window.diagramParagraph,0,window.diagramParagraph,10);
        window.diagramSelection=window.getSelection().toString();
        renderInvestigation('${key}');renderRows();`);
      const pendingDiagrams = await evaluate(`({same:window.diagramOutput===document.querySelector('[data-key="${key}"] .explain-output'),
        calls:window.diagramCalls,states:[...window.diagramOutput.querySelectorAll('.explain-diagram')].map(x=>x.dataset.diagramState)})`);
      await evaluate('window.releaseDiagram();');
      assert.deepEqual(pendingDiagrams, {same:true,calls:2,states:['rendering','rendering']});
      await evaluate(`window.diagramViewport=document.querySelector('main').scrollTop;window.releaseDiagram();`);
      await evaluate(`new Promise((resolve,reject)=>{
        const deadline=Date.now()+15000;
        const check=()=>document.querySelectorAll('.explain-diagram[data-diagram-state="rendering"]').length===0?resolve():
          Date.now()>deadline?reject(Error('Diagram rendering timed out')):setTimeout(check,20);check();
      })`);
      const rendered = await evaluate(`(()=>{
        const out=window.diagramOutput,svg=[...out.querySelectorAll('.explain-diagram-drawing svg')];
        return {count:svg.length,labels:svg.map(x=>x.textContent),errors:[...out.querySelectorAll('.explain-diagram-status:not([hidden])')].map(x=>x.textContent),
          unsafe:!!out.querySelector('svg script,svg foreignObject,svg image,svg a,svg style,svg [style],svg [href],svg [onload]'),
          sizes:svg.map(x=>({width:x.getBoundingClientRect().width,height:x.getBoundingClientRect().height})),
          localMarkers:[...out.querySelectorAll('svg [marker-end]')].every(x=>/^url\\(#[\\w-]+\\)$/.test(x.getAttribute('marker-end')))};
      })()`);
      assert.deepEqual(rendered.errors, []);
      assert.equal(rendered.count, 2);
      assert.match(rendered.labels[0], /PR reader.*Pinned evidence.*Completed explanation/s);
      assert.ok(rendered.labels[0].includes('$& $$'), 'Dollar tokens remain literal diagram labels');
      assert.match(rendered.labels[1], /Read pinned source.*Evidence/s);
      assert.ok(rendered.sizes.every(x=>x.width>100&&x.height>30), JSON.stringify(rendered));
      const labelsFit = await evaluate(`([...window.diagramOutput.querySelectorAll('svg .node')].map(node=>{
        const label=node.querySelector('text'),box=node.querySelector('rect');
        if(!label||!box)return null;
        const a=label.getBoundingClientRect(),b=box.getBoundingClientRect();
        return {fits:a.left>=b.left-1&&a.right<=b.right+1,label:label.textContent};
      }).filter(Boolean))`);
      assert.ok(labelsFit.every(x=>x.fits), JSON.stringify(labelsFit));
      assert.equal(rendered.unsafe, false);
      assert.equal(rendered.localMarkers, true);
      assert.equal(await evaluate(`([...window.diagramOutput.querySelectorAll('svg .node text')]
        .every(label=>getComputedStyle(label).visibility==='visible'))`), true, 'Diagram classes cannot hide evidence labels');
      assert.deepEqual(await evaluate(`(()=>{
        const [flow,sequence]=window.diagramOutput.querySelectorAll('.explain-diagram-drawing svg');
        const title=flow.querySelector('title'),description=flow.querySelector('desc');
        return {title:title?.textContent,description:description?.textContent,
          owned:flow.getAttribute('aria-labelledby')===title?.id && flow.getAttribute('aria-describedby')===description?.id &&
            title?.id.startsWith('explanation-svg-') && description?.id.startsWith('explanation-svg-'),
          sequence:sequence.getAttribute('aria-label')};
      })()`), {title:'Evidence flow',description:'The reader and pinned evidence exchange context, which produces the explanation.',
        owned:true,sequence:'PR explanation diagram. Reader sends Read pinned source to Host. Host exchanges Evidence with Reader.'});
      const documentNode = (await send('DOM.getDocument')).root.nodeId;
      const diagramNode = await send('DOM.querySelector', {nodeId:documentNode,selector:`[data-key="${key}"] .explain-diagram-drawing svg`});
      const accessible = (await send('Accessibility.getPartialAXTree', {nodeId:diagramNode.nodeId,fetchRelatives:false})).nodes[0];
      assert.equal(accessible.name.value, 'Evidence flow');
      assert.equal(accessible.description.value, 'The reader and pinned evidence exchange context, which produces the explanation.');
      assert.deepEqual(await evaluate(`(()=>{
        const out=window.diagramOutput;
        return {solid:getComputedStyle(out.querySelector('.messageLine0')).strokeDasharray,
          dashed:getComputedStyle(out.querySelector('.messageLine1')).strokeDasharray,
          dotted:getComputedStyle(out.querySelector('.edge-pattern-dotted')).strokeDasharray};
      })()`), {solid:'none',dashed:'2px, 2px',dotted:'2px, 2px'}, 'Connection types retain their visual distinction');
      assert.equal(await evaluate(`window.getSelection().anchorNode===window.diagramParagraph &&
        window.getSelection().toString()===window.diagramSelection &&
        document.querySelector('main').scrollTop===window.diagramViewport`), true);
      await evaluate(`window.diagramSvg=window.diagramOutput.querySelector('svg');
        window.diagramSource=window.diagramOutput.querySelector('.explain-diagram-source');window.diagramSource.open=true;
        updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,message:'Still ready'});
        renderInvestigation('${key}');renderRows();`);
      await frame();
      assert.deepEqual(await evaluate('window.diagramUnhandled'), [], 'Rendered SVG classes never become hydration placeholders');
      assert.equal(await evaluate(`!!window.diagramOutput.querySelector('svg .explain-diagram') &&
        !window.diagramOutput.querySelector('svg .explain-diagram').dataset.diagramState`), true);
      assert.equal(await evaluate(`window.diagramOutput===document.querySelector('[data-key="${key}"] .explain-output') &&
        window.diagramSvg===window.diagramOutput.querySelector('svg') && window.diagramCalls===2 &&
        window.getSelection().anchorNode===window.diagramParagraph &&
        window.diagramSource===window.diagramOutput.querySelector('.explain-diagram-source') && window.diagramSource.open`), true);
      await send('Emulation.setDeviceMetricsOverride', {width:900,height:1000,deviceScaleFactor:1,mobile:false});
      await evaluate("S.settings.theme='dark';applySavedTheme();");
      await frame();
      assert.equal(await evaluate(`(()=>{
        const drawing=window.diagramOutput.querySelector('.explain-diagram-drawing');
        const labels=[...drawing.querySelectorAll('.node text')];
        return drawing.scrollWidth<=drawing.clientWidth+1 && labels.every(label=>{
          const a=label.getBoundingClientRect(),b=label.closest('.node').querySelector('rect').getBoundingClientRect();
          return a.left>=b.left-1&&a.right<=b.right+1;
        }) && getComputedStyle(labels[0]).fill===getComputedStyle(document.body).color;
      })()`), true, 'Narrow dark-mode diagrams fit and use the app text color');
      await send('Emulation.setDeviceMetricsOverride', {width:1600,height:1000,deviceScaleFactor:1,mobile:false});
      await evaluate("S.settings.theme='light';applySavedTheme();");
      if (process.env.ADOPRVIEW_DIAGRAM_SCREENSHOT) {
        await evaluate('window.diagramOutput.scrollIntoView({block:"start"});');
        const screenshot = await send('Page.captureScreenshot', { format: 'png' });
        await writeFile(process.env.ADOPRVIEW_DIAGRAM_SCREENSHOT, Buffer.from(screenshot.data, 'base64'));
      }
      await evaluate(`window.getSelection().removeAllRanges();mermaid.render=window.realDiagramRender;
        const draft=assistanceDraft('${key}');draft.kind='question';draft.attachedId='diagram-final';renderInvestigation('${key}');
        const preview=[...document.querySelectorAll('[data-key="${key}"] details')].find(d=>d.querySelector('summary')?.textContent==='View attached context');
        preview.open=true;`);
      await evaluate(`new Promise((resolve,reject)=>{
        const deadline=Date.now()+15000;
        const check=()=>[...document.querySelectorAll('[data-key="${key}"] details')].find(d=>d.querySelector('summary')?.textContent==='View attached context')
          ?.querySelectorAll('.explain-diagram[data-diagram-state="completed"]').length===2?resolve():
          Date.now()>deadline?reject(Error('Attached diagram preview timed out')):setTimeout(check,20);check();
      })`);
      assert.deepEqual(await calls(), [], 'Diagram rendering never calls the native bridge');
      await send('Network.setBlockedURLs', { urls: [] });
    });

    await t.test('Explain diagrams completed in closed assistance hydrate immediately on both reopen paths', async () => {
      const key = 'sample-service-id:101';
      for (const reopen of ['button', 'openInvestigation']) {
        const output = 'Completed evidence.\n\n```mermaid\nflowchart LR\n' +
          (reopen === 'openInvestigation' ? ' accTitle: Evidence connection\n' : '') + ' A --> B\n```';
        await reset(true);
        await evaluate(`S.expanded.add('${key}');renderRows();
          updateExplanation({key:'${key}',requestId:'closed-diagram',sequence:++window.explainSequence,
            status:'running',message:'Reading',output:'',coverage:[]});
          document.querySelector('[data-key="${key}"] [data-assistance-close]').click();
          updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,
            status:'completed',message:'Ready',output:${JSON.stringify(output)}});
          window.closedDiagram=document.querySelector('[data-key="${key}"] .explain-diagram');`);
        assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .assistance-body').hidden &&
          !window.closedDiagram.dataset.diagramState && !window.closedDiagram.querySelector('svg')`), true);
        await evaluate(reopen === 'button'
          ? `document.querySelector('[data-key="${key}"] [data-assistance-close]').click();`
          : `openInvestigation('${key}', 'explain');`);
        await evaluate(`new Promise((resolve,reject)=>{
          const deadline=Date.now()+15000;
          const check=()=>window.closedDiagram.dataset.diagramState==='completed'?resolve():
            Date.now()>deadline?reject(Error('Reopened diagram stayed pending')):setTimeout(check,20);check();
        })`);
        assert.equal(await evaluate(`window.closedDiagram===document.querySelector('[data-key="${key}"] .explain-diagram') &&
          !document.querySelector('[data-key="${key}"] .assistance-body').hidden &&
          !!window.closedDiagram.querySelector('.explain-diagram-drawing svg')`), true, reopen);
        if (reopen === 'button')
          assert.equal(await evaluate(`window.closedDiagram.querySelector('svg').getAttribute('aria-label')`), 'PR explanation diagram. A connects to B.');
        else assert.equal(await evaluate(`(()=>{
          const diagram=window.closedDiagram.querySelector('svg'),description=diagram.querySelector('desc');
          return description.textContent==='A connects to B.' && diagram.getAttribute('aria-describedby')===description.id &&
            description.id.startsWith('explanation-svg-');
        })()`), true, 'Missing authored description receives an owned connection summary');
      }
      assert.deepEqual(await calls(), []);
    });

    await t.test('Explain diagram failures expose safe source and prose while plain explanations never invoke Mermaid', async sub => {
      const key = 'sample-service-id:101';
      await reset(true);
      sub.after(() => evaluate('mermaid.render=window.realDiagramRender;'));
      await evaluate(`S.expanded.add('${key}');renderRows();window.diagramCalls=0;
        window.realDiagramRender=mermaid.render.bind(mermaid);
        mermaid.render=(render=>(...args)=>{window.diagramCalls++;return render(...args);})(window.realDiagramRender);
        updateExplanation({key:'${key}',requestId:'plain-final',sequence:++window.explainSequence,status:'completed',
          message:'Ready',output:'A small dependency update needs only prose.',coverage:[]});`);
      assert.equal(await evaluate(`window.diagramCalls===0 && !document.querySelector('.explain-diagram') &&
        document.querySelector('[data-key="${key}"] .explain-output').textContent.includes('only prose')`), true);
      for (const [source, permitted] of [
        ['flowchart LR\n A -->', false],
        ['flowchart LR\n A --> B\n click A "https://evil.invalid/"', false],
        ['%%{init: {"securityLevel":"loose"}}%%\nflowchart LR\n A --> B', false],
        ['flowchart LR\n A["<img src=x onerror=window.diagramInjected=true>"] --> B', false],
      ]) {
        const output = `Completed prose remains readable.\n\n\`\`\`mermaid\n${source}\n\`\`\``;
        const before = await evaluate('window.diagramCalls');
        await evaluate(`updateExplanation({key:'${key}',requestId:'invalid-'+window.explainSequence,sequence:++window.explainSequence,
          status:'completed',message:'Ready',output:${JSON.stringify(output)},coverage:[]});`);
        await evaluate(`new Promise((resolve,reject)=>{
          const deadline=Date.now()+10000;
          const check=()=>document.querySelector('[data-key="${key}"] .explain-diagram')?.dataset.diagramState==='failed'?resolve():
            Date.now()>deadline?reject(Error('Diagram failure stayed pending')):setTimeout(check,20);check();
        })`);
        assert.equal(await evaluate('window.diagramCalls'), before + (permitted ? 1 : 0));
        assert.deepEqual(await evaluate(`(()=>{
          const out=document.querySelector('[data-key="${key}"] .explain-output'),figure=out.querySelector('.explain-diagram');
          return {source:figure.querySelector('code').textContent,open:figure.querySelector('details').open,
            prose:out.textContent.includes('Completed prose remains readable.'),
            unsafe:!!out.querySelector('script,img,a,iframe')||window.diagramInjected===true};
        })()`), {source,open:true,prose:true,unsafe:false});
        assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .explain-diagram-status').textContent`), /Diagram could not be rendered/);
        await evaluate(`window.failedDiagram=document.querySelector('[data-key="${key}"] figure.explain-diagram');
          window.failedDiagramCalls=window.diagramCalls;renderInvestigation('${key}');renderRows();`);
        await frame();
        assert.equal(await evaluate(`window.failedDiagram===document.querySelector('[data-key="${key}"] figure.explain-diagram') &&
          window.failedDiagram.querySelector('details').open && window.diagramCalls===window.failedDiagramCalls`), true,
        'Failed diagrams retain their opened source without restarting');
      }
      await evaluate(`mermaid.render=window.realDiagramRender;
        { const safeSvg=safeExplanationDiagram('<svg xmlns="http://www.w3.org/2000/svg"><style>body{display:none}</style><script>window.diagramInjected=true</script><foreignObject><img src="https://evil.invalid/"></foreignObject><image href="https://evil.invalid/"/><a href="javascript:alert(1)"><text onload="alert(1)">Safe label</text></a><path marker-end="url(https://evil.invalid/x)"/></svg>');
        document.querySelector('[data-key="${key}"] .explain-diagram-drawing').replaceChildren(safeSvg); }`);
      assert.deepEqual(await evaluate(`(()=>{
        const diagram=document.querySelector('[data-key="${key}"] .explain-diagram-drawing svg');
        return {unsafe:!!diagram.querySelector('script,style,foreignObject,image,a,[href],[onload],[marker-end]'),
          injected:window.diagramInjected===true,text:diagram.textContent};
      })()`), {unsafe:false,injected:false,text:'Safe label'});
      assert.deepEqual(await calls(), []);
    });

    await t.test('Explain sequence diagrams enforce the 80-connection limit before rendering, not counting notes or activation', async sub => {
      const key = 'sample-service-id:101';
      await reset(true);
      sub.after(() => evaluate('mermaid.render=window.realDiagramRender;'));
      await evaluate(`S.expanded.add('${key}');renderRows();window.diagramCalls=0;
        window.realDiagramRender=mermaid.render.bind(mermaid);
        mermaid.render=async()=>{window.diagramCalls++;return {svg:'<svg><text>Budgeted sequence</text></svg>'};};`);
      const atLimit = 'sequenceDiagram\nloop Evidence\nNote over A,B: An arrow ->> in a note is not a connection\n' +
        Array.from({length:40},()=> 'A->>+B: Read\nB-->>-A: Evidence').join('\n') + '\nend';
      const overLimit = 'sequenceDiagram\n' + Array.from({length:81},()=> 'A->>B: Evidence').join(';');
      for (const [source, state] of [[atLimit, 'completed'], [overLimit, 'failed']]) {
        const output = 'Completed prose.\n\n```mermaid\n' + source + '\n```';
        const before = await evaluate('window.diagramCalls');
        await evaluate(`updateExplanation({key:'${key}',requestId:'budget-'+window.explainSequence,
          sequence:++window.explainSequence,status:'completed',message:'Ready',output:${JSON.stringify(output)},coverage:[]});`);
        await evaluate(`new Promise((resolve,reject)=>{
          const deadline=Date.now()+15000;
          const check=()=>document.querySelector('[data-key="${key}"] figure.explain-diagram')?.dataset.diagramState===${JSON.stringify(state)}?resolve():
            Date.now()>deadline?reject(Error('Sequence budget did not reach '+${JSON.stringify(state)})):setTimeout(check,20);check();
        })`);
        assert.equal(await evaluate('window.diagramCalls'), before + (state === 'completed' ? 1 : 0));
        if (state === 'failed') {
          assert.match(await evaluate(`document.querySelector('[data-key="${key}"] .explain-diagram-status').textContent`), /80-connection/);
          assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] figure.explain-diagram code').textContent`), source);
        }
      }
      assert.deepEqual(await calls(), []);
    });

    await t.test('Explain discarded async diagram results cannot replace a cancelled or newer explanation', async sub => {
      const key = 'sample-service-id:101';
      const output = '```mermaid\nflowchart LR\n A --> B\n```';
      await reset(true);
      sub.after(() => evaluate('mermaid.render=window.realDiagramRender;'));
      await evaluate(`S.expanded.add('${key}');renderRows();
        window.realDiagramRender=mermaid.render.bind(mermaid);
        mermaid.render=()=>new Promise(resolve=>window.resolveStaleDiagram=resolve);
        updateExplanation({key:'${key}',requestId:'old-diagram',sequence:++window.explainSequence,status:'completed',
          message:'Ready',output:${JSON.stringify(output)},coverage:[]});
        window.oldDiagram=document.querySelector('[data-key="${key}"] .explain-diagram');`);
      await evaluate(`new Promise((resolve,reject)=>{
        const deadline=Date.now()+10000;
        const check=()=>window.resolveStaleDiagram?resolve():
          Date.now()>deadline?reject(Error('Diagram did not reach async renderer')):setTimeout(check,20);check();
      })`);
      await evaluate(`
        updateExplanation({key:'${key}',requestId:'new-request',sequence:++window.explainSequence,status:'running',
          message:'Reading',output:${JSON.stringify(output)},coverage:[]});
        updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,status:'cancelled',
          message:'Cancelled',output:${JSON.stringify(output)}});
        window.resolveStaleDiagram({svg:'<svg><text>Stale result</text></svg>'});`);
      await frame();
      assert.equal(await evaluate(`!window.oldDiagram.isConnected &&
        !document.querySelector('[data-key="${key}"] .explain-output svg') &&
        document.querySelector('[data-key="${key}"] .explain-output').hidden &&
        !document.querySelector('[data-key="${key}"] .explanation').textContent.includes('Stale result')`), true);
      await evaluate('mermaid.render=window.realDiagramRender;');
    });

    await t.test('Explain ARM Bicep preserves indentation, safe labels and highlighted differences through retained output', async () => {
      const key = 'sample-service-id:101';
      const before = "resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'sample'\n  properties: {\n    allowBlobPublicAccess: true\n  }\n}";
      const after = before.replace('true', 'false');
      const output = process.env.ADOPRVIEW_ARM_EXAMPLE
        ? require('node:fs').readFileSync(process.env.ADOPRVIEW_ARM_EXAMPLE, 'utf8')
        : `## Azure Resource Manager changes\n\n### /deploy.bicep\n\n#### Before\n\nExact source excerpt (not deployment-validated):\n\n\`\`\`bicep\n${before}\n\`\`\`\n\n#### After\n\nExact source excerpt (not deployment-validated):\n\n\`\`\`bicep\n${after}\n\`\`\`\n\n#### Changed lines (- removed, + added)\n\n\`\`\`diff\n-    allowBlobPublicAccess: true\n+    allowBlobPublicAccess: false\n\`\`\`\n\n**Improvement:** Disables public blob access.\n\n**Risks and uncertainty:** Anonymous consumers may fail; no deployment was run.`;
      for (const [width, theme] of [[1600, 'light'], [900, 'dark']]) {
        await send('Emulation.setDeviceMetricsOverride', { width, height: 1100, deviceScaleFactor: 1, mobile: false });
        await reset(true);
        await evaluate(`S.settings.theme=${JSON.stringify(theme)};applySavedTheme();S.expanded.add('${key}');renderRows();
          updateExplanation({key:'${key}',requestId:'arm-example',sequence:++window.explainSequence,status:'completed',
            message:'Synthetic ARM explanation ready.',output:${JSON.stringify(output)},coverage:[]});`);
        const check = () => evaluate(`(()=>{
          const card=document.querySelector('[data-key="${key}"] .explanation'),out=card.querySelector('.explain-output');
          const codes=[...out.querySelectorAll('pre code')];
          return {
            before:codes[0]?.textContent, after:codes[1]?.textContent,
            whitespace:getComputedStyle(codes[0].parentElement).whiteSpace,
            labels:[...out.querySelectorAll('p > strong')].map(n=>n.textContent),
            removed:out.querySelector('pre del')?.textContent, added:out.querySelector('pre ins')?.textContent,
            removedColor:getComputedStyle(out.querySelector('pre del')).color,
            addedColor:getComputedStyle(out.querySelector('pre ins')).color,
            safe:!out.querySelector('script,img,a,button,iframe,style,[onerror]'),
            fits:card.scrollWidth<=card.clientWidth+1
          };
        })()`);
        let actual = await check();
        assert.equal(actual.before, before + '\n');
        assert.equal(actual.after, after + '\n');
        assert.equal(actual.whitespace, 'pre');
        assert.ok(actual.labels.includes('Bicep'));
        assert.equal(actual.removed, '-    allowBlobPublicAccess: true');
        assert.equal(actual.added, '+    allowBlobPublicAccess: false');
        assert.notEqual(actual.removedColor, actual.addedColor);
        assert.ok(actual.safe && actual.fits);
        if (width === 1600 && process.env.ADOPRVIEW_ARM_SCREENSHOT) {
          await evaluate(`document.querySelector('[data-key="${key}"] .explanation').scrollIntoView({block:'start'});`);
          const shot = await send('Page.captureScreenshot', { format: 'png' });
          await writeFile(process.env.ADOPRVIEW_ARM_SCREENSHOT, Buffer.from(shot.data, 'base64'));
          await evaluate(`const output=document.querySelector('[data-key="${key}"] .explain-output');output.scrollTop=output.scrollHeight;`);
          const diffShot = await send('Page.captureScreenshot', { format: 'png' });
          await writeFile(process.env.ADOPRVIEW_ARM_SCREENSHOT.replace(/\.png$/i, '-diff.png'), Buffer.from(diffShot.data, 'base64'));
        }
        await evaluate(`S.expanded.delete('${key}');renderRows();S.expanded.add('${key}');renderRows();`);
        assert.deepEqual(await check(), actual, 'Retained output changed code or lost safe diff treatment');
        const hostile = '```bicep onclick=evil\n  name: \'<script>window.armInjected=true</script>&<img src=x onerror=evil()>\'\n```\n\n```diff\n-<button onclick=evil()>old</button>\n+<img src=x onerror=evil()>\n```';
        await evaluate(`updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,output:${JSON.stringify(hostile)}});`);
        assert.equal(await evaluate(`(()=>{
          const out=document.querySelector('[data-key="${key}"] .explain-output');
          return !window.armInjected&&!out.querySelector('script,img,button,[onclick],[onerror]')&&
            out.querySelector('pre code').textContent.includes('<script>window.armInjected=true</script>&<img')&&
            out.querySelector('pre ins').textContent==='+<img src=x onerror=evil()>';
        })()`), true);
      }
      await send('Emulation.setDeviceMetricsOverride', { width:1600,height:1000,deviceScaleFactor:1,mobile:false });
    });

    await t.test('Explain retained activity meets missing, filtered, hidden, cancellation, replacement and return contracts', async t => {
      const key = 'sample-service-id:101', other = 'sample-api-id:104';
      const metrics = [];
      await evaluate('window.explainRpc=window.MockHost.rpc;');
      const setup = async () => {
        await reset(true);
        await evaluate(`S.expanded.add('${key}');renderRows();
          window.explainSlot=null;window.explainNext=0;window.explainCancelHeld=false;
          window.MockHost.rpc=async(method,p)=>{
            window.calls.push({method,...p});
            if(method==='explainPr'){
              if(!S.snap.prs.some(pr=>pr.key===p.key))throw Error('Removed PR cannot launch');
              if(window.explainSlot)throw Error('Another PR explanation is running. Cancel it or wait for it to finish.');
              return window.explainSlot={key:p.key,requestId:'synthetic-explain-'+(++window.explainNext),
                sequence:++window.explainSequence,status:'running',message:'Reading synthetic pinned context',
                output:'Synthetic partial explanation',coverage:[]};
            }
            if(method==='cancelExplanation'){
              if(!window.explainSlot || window.explainSlot.key!==p.key || window.explainSlot.requestId!==p.requestId)
                throw Error('This explanation is no longer current.');
              const owned=window.explainSlot;
              updateExplanation({...owned,sequence:++window.explainSequence,status:'cancelling',message:'Cancelling synthetic explanation'});
              if(window.explainCancelHeld)await new Promise(resolve=>window.explainCancelDone=resolve);
              if(window.explainSlot!==owned)throw Error('Replacement operation must not be cancelled');
              window.explainSlot=null;
              return {...owned,sequence:++window.explainSequence,status:'cancelled',message:'Synthetic cancellation confirmed'};
            }
            throw Error('Unexpected host call '+method);
          };`);
        await click(await point(key, '.detail [data-act="explainPr"]'));
        await frame();
        await move(10, 10);
        await evaluate('document.activeElement?.blur();');
        await frame();
      };
      const measure = async scene => {
        const result = await evaluate(`(()=>{
          const cards=[...document.querySelectorAll('.explanation')].filter(c=>c.dataset.explainKey==='${key}');
          cards[0]?.querySelector('h3').scrollIntoView({block:'center'});
          const cancels=cards.flatMap(c=>[...c.querySelectorAll('[data-act="cancelExplanation"]')]).filter(b=>!b.hidden);
          return {cards:cards.length,visibleCancel:cancels.length,
            status:cards.map(c=>c.querySelector('.explain-status').textContent),
            currentKeys:S.snap.prs.map(p=>p.key),
            pendingKeys:S.pendingSnapshot?.prs.map(p=>p.key)||null,
            caption:document.querySelector('#explanationShelf')?.textContent||'',
            slot:window.explainSlot?.requestId||null,
            cancelCalls:window.calls.filter(c=>c.method==='cancelExplanation')};
        })()`);
        metrics.push({ scene, ...result });
        if (proofDir) {
          await writeFile(path.join(proofDir, 'scenes.json'), JSON.stringify(metrics, null, 2));
          const data = (await send('Page.captureScreenshot', { format: 'png' })).data;
          await writeFile(path.join(proofDir, scene + '.png'), Buffer.from(data, 'base64'));
        }
        return result;
      };
      try {
        await t.test('AC1 and AC2: departed identity, status and keyboard Cancel release the exact slot', async () => {
          await setup();
          const before = await measure('active');
          assert.equal(before.cards, 1);
          assert.equal(before.visibleCancel, 1);
          await evaluate(`window.departedCancel=document.querySelector('[data-key="${key}"] [data-act="cancelExplanation"]');
            setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();`);
          const departed = await measure('departed');
          assert.equal(departed.currentKeys.includes(key), false, 'Departure must be in the accepted current snapshot');
          if (process.env.ADOPRVIEW_EXPLAIN_BASELINE === '1') {
            await evaluate(`onAction('cancelExplanation',window.departedCancel,'${key}')`);
            await evaluate(`onAction('explainPr',{dataset:{}},'${other}')`);
            const failure = await measure('departed-no-rpc');
            assert.equal(failure.visibleCancel, 1, 'Baseline loses reachable Cancel and cannot release the single slot');
            return;
          }
          assert.equal(departed.cards, 1);
          assert.equal(departed.visibleCancel, 1);
          assert.match(departed.caption, /sample-service.*!101.*Add per-cluster health probe/);
          assert.deepEqual(departed.status, ['Reading synthetic pinned context']);
          await evaluate(`window.explainCancelHeld=true;document.querySelector('#search').focus();`);
          for (let i = 0; i < 40 && await evaluate('document.activeElement.dataset.act') !== 'cancelExplanation'; i++)
            await keyboard('Tab', 9);
          assert.equal(await evaluate('document.activeElement.dataset.act'), 'cancelExplanation');
          await keyboard('Enter', 13);
          assert.equal(await evaluate('window.explainSlot.requestId'), 'synthetic-explain-1');
          assert.equal(await evaluate("document.querySelector('#explanationShelf [data-act=\"cancelExplanation\"]').disabled"), true);
          await measure('cancelling');
          assert.deepEqual((await calls()).filter(c => c.method === 'cancelExplanation'),
            [{ method: 'cancelExplanation', key, requestId: 'synthetic-explain-1' }]);
          await evaluate('window.explainCancelDone()'); await frame();
          assert.equal(await evaluate('window.explainSlot'), null);
          assert.equal(await evaluate("document.querySelectorAll('#explanationShelf .explanation').length"), 0);
        });
        if (process.env.ADOPRVIEW_EXPLAIN_BASELINE === '1') return;
        await t.test('AC1: search, repository, scope, lane, hidden and collapsed targets retain one accessible card', async () => {
          for (const [scene, mutation] of [
            ['search', "const e=document.querySelector('#search');e.value='Different repository';e.dispatchEvent(new Event('input',{bubbles:true}));"],
            ['repository', "const e=document.querySelector('#repoFilter');e.value='sample-api';e.dispatchEvent(new Event('change',{bubbles:true}));"],
            ['scope', `S.snap.prs.find(p=>p.key==='${key}').isMine=false;document.querySelector('[data-scope="watched"]').click();`],
            ['lane', "document.querySelector('[data-lane=\"completed\"]').click();"],
            ['hidden', `S.settings.hidden.push('${key}');S.expanded.delete('${key}');render();`],
            ['collapsed', `onAction('toggle',{dataset:{}},'${key}');`],
          ]) {
            await setup();
            await evaluate(`{${mutation}}`); await frame();
            const state = await measure(scene);
            assert.equal(state.cards, 1, scene);
            assert.equal(state.visibleCancel, 1, scene);
            assert.match(state.caption, /sample-service.*!101/, scene);
            await evaluate("document.querySelector('#explanationShelf [data-act=\"cancelExplanation\"]').focus()");
            await keyboard('Enter', 13);
            assert.equal(await evaluate('window.explainSlot'), null, scene);
          }
        });
        await t.test('AC2 and AC3: removed launch blocked, confirmed cancel permits another PR, obsolete events/actions cannot cancel replacement', async () => {
          await setup();
          await evaluate(`window.oldExplain=structuredClone(window.explainSlot);
            window.oldCancel=document.querySelector('[data-key="${key}"] [data-act="cancelExplanation"]');
            setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();
            onAction('explainPr',{dataset:{}},'${key}');`);
          assert.equal((await calls()).filter(c => c.method === 'explainPr' && c.key === key).length, 1);
          await evaluate("document.querySelector('#explanationShelf [data-act=\"cancelExplanation\"]').focus()");
          await keyboard('Enter', 13);
          await evaluate(`onAction('explainPr',{dataset:{}},'${other}')`);
          assert.equal(await evaluate('window.explainSlot.key'), other);
          await evaluate(`(async()=>{
            updateExplanation({...window.oldExplain,status:'running',output:'Obsolete text'});
            await onAction('cancelExplanation',window.oldCancel,'${key}');
          })()`);
          assert.equal(await evaluate('window.explainSlot.key'), other);
          assert.equal((await calls()).filter(c => c.method === 'cancelExplanation').length, 1);
          await evaluate(`(async()=>{
            await onAction('cancelExplanation',{dataset:{requestId:window.explainSlot.requestId}},'${other}');
            setSnapshot(structuredClone(window.baseline));renderRows();
            await onAction('explainPr',{dataset:{}},'${key}');
            updateExplanation({...window.oldExplain,status:'completed',output:'Obsolete terminal text'});
            await onAction('cancelExplanation',window.oldCancel,'${key}');
          })()`);
          assert.equal(await evaluate(`S.explanations.get('${key}').requestId`), 'synthetic-explain-3');
          assert.equal(await evaluate('window.explainSlot.requestId'), 'synthetic-explain-3');
          assert.equal((await calls()).filter(c => c.method === 'cancelExplanation').length, 2);
          await measure('replacement');
        });
        await t.test('restored preparing Explain hydrates identity without replacing its Cancel or output', async () => {
          await reset(true);
          await evaluate(`S.snap={prs:[]};S.lastSig='';
            updateExplanation({key:'${key}',requestId:'synthetic-restored',sequence:1,status:'preparing',
              message:'Restored synthetic preparation',output:'Restored partial output',coverage:[]});renderRows();
            window.restoredCancel=document.querySelector('#explanationShelf [data-act="cancelExplanation"]');
            window.restoredOutput=document.querySelector('#explanationShelf .explain-output');`);
          assert.equal(await evaluate(`window.restoredOutput.hidden && window.restoredOutput.textContent==='' &&
            !!document.querySelector('#explanationShelf .explain-waiting:not([hidden]) .video-spinner')`), true);
          assert.match(await evaluate("document.querySelector('.explanation-identity').textContent"), /sample-service-id:101/);
          await evaluate(`updateExplanation({...S.explanations.get('${key}'),sequence:2,status:'running',
            revision:{repo:'sample-service',id:101,iteration:4,sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40)}});`);
          assert.match(await evaluate("document.querySelector('.explanation-identity').textContent"), /sample-service !101/);
          await evaluate('setSnapshot(structuredClone(window.baseline));renderRows();');
          assert.match(await evaluate("document.querySelector('.explanation-identity').textContent"),
            /sample-service !101: Add per-cluster health probe/);
          await evaluate(`window.restoredCancel.focus();
            updateExplanation({...S.explanations.get('${key}'),sequence:3,status:'running',
              revision:{repo:'sample-service',id:101,iteration:4,sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40)}});`);
          assert.equal(await evaluate("window.restoredCancel===document.querySelector('#explanationShelf [data-act=\"cancelExplanation\"]')"), true);
          assert.equal(await evaluate("window.restoredOutput===document.querySelector('#explanationShelf .explain-output')"), true);
          assert.equal(await evaluate('document.activeElement===window.restoredCancel'), true);
          await measure('restored-identity');
        });
        await t.test('retained Explain focusout flushes the latest queued snapshot for Cancel and output', async () => {
          for (const selector of ['[data-act="cancelExplanation"]', '.explain-details summary']) {
            await setup();
            await evaluate(`setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();
              document.querySelector('#search').focus();`);
            const focused = () => evaluate(`document.activeElement?.matches(${JSON.stringify('#explanationShelf ' + selector)})`);
            for (let i = 0; i < 40 && !await focused(); i++) await keyboard('Tab', 9);
            assert.equal(await focused(), true);
            assert.equal(await evaluate("document.activeElement.matches(':focus-visible')"), true);
            await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,
                prs:S.snap.prs.map(p=>({...p,title:'New synthetic current title'}))});`);
            assert.equal(await evaluate('!!S.pendingSnapshot'), true);
            assert.notEqual(await evaluate('S.snap.prs[0].title'), 'New synthetic current title');
            await evaluate("document.querySelector('#search').focus()");
            await frame();
            assert.equal(await evaluate('S.pendingSnapshot'), null);
            assert.equal(await evaluate('S.snap.prs[0].title'), 'New synthetic current title');
          }
        });
        await t.test('AC4: return has no duplicate, exact Cancel focus transfers, terminal cleanup preserves inline output', async () => {
          for (const status of ['completed', 'cancelled', 'failed']) {
            await setup();
            await evaluate(`document.querySelector('[data-explain-key="${key}"] [data-act="cancelExplanation"]').focus();
              S.expanded.delete('${key}');renderRows();`);
            assert.equal(await evaluate("document.activeElement.closest('.explanation-only')?.dataset.key"), key);
            await evaluate(`S.expanded.add('${key}');renderRows();`);
            assert.equal(await evaluate("document.activeElement.dataset.act"), 'cancelExplanation');
            await move(10, 10); await frame();
            await evaluate(`document.activeElement.blur();window.returnSnap=structuredClone(S.snap);
              setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();`);
            await frame();
            await evaluate('setSnapshot(window.returnSnap);renderRows();');
            await measure('return-' + status);
            assert.equal(await evaluate(`document.querySelectorAll('[data-explain-key="${key}"]').length`), 1);
            await click(await point(key, '.row .title')); await frame();
            assert.equal(await evaluate(`document.querySelectorAll('[data-explain-key="${key}"]').length`), 1);
            assert.equal(await evaluate("document.querySelectorAll('#explanationShelf .explanation').length"), 0);
            await move(10, 10);
            await evaluate('document.activeElement?.blur();');
            await frame();
            await evaluate(`document.activeElement.blur();
              setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();
              updateExplanation({...S.explanations.get('${key}'),sequence:++window.explainSequence,
                status:'${status}',message:'Synthetic ${status}',output:'Retained synthetic ${status} output'});
              window.explainSlot=null;`);
            assert.equal(await evaluate(`S.snap.prs.some(p=>p.key==='${key}')`), false);
            assert.equal(await evaluate("document.querySelectorAll('#explanationShelf .explanation').length"), 0);
            await evaluate('setSnapshot(window.returnSnap);renderRows();');
            await click(await point(key, '.row .title')); await frame();
            assert.equal(await evaluate(`document.querySelector('[data-explain-key="${key}"] .explain-output').textContent.trimEnd()`),
              status === 'completed' ? 'Retained synthetic completed output' : '');
            assert.equal(await evaluate(`document.querySelector('[data-explain-key="${key}"] .explain-output').hidden`), status !== 'completed');
            assert.equal(await evaluate(`document.querySelector('[data-explain-key="${key}"] [data-act="cancelExplanation"]').hidden`), true);
          }
          await measure('returned-terminal');
        });
      } finally {
        await evaluate('if(window.explainRpc)window.MockHost.rpc=window.explainRpc;');
      }
    });

    await t.test('first video selects an Azure resource and voice explicitly, missing az and denied synthesis cannot generate', async () => {
      const key='sample-service-id:101';
      await reset(true);
      await evaluate(`S.settings.videoSpeech=null;window.speechMissingAz=true;S.expanded.add('${key}');renderRows();`);
      await click(await point(key,'.detail .assistance-video > summary')); await frame();
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
      await click(await point(key,'.detail .assistance-video > summary')); await frame();
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
        await click(await point(key,'.detail .assistance-video > summary')); await frame();
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
      await click(await point(key,'.detail .assistance-video > summary')); await frame();
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

    await t.test('video has one owner across expanded, retained assistance and standalone shelves without losing playback', async () => {
      const key='sample-service-id:101';
      await reset(true);
      const states=await evaluate(`(()=>{
        S.expanded.add('${key}');renderRows();
        updateVideo({key:'${key}',requestId:'d'.repeat(32),sequence:++window.videoSequence,status:'completed',
          message:'Offline playback fixture',evidence:[],coverage:[],model:'claude-opus-5.5',durationSeconds:1,
          path:'offline.mp4',mediaUrl:'https://pr-video.local/'+'d'.repeat(32)+'.mp4'});
        const player=document.querySelector('[data-video-key="${key}"] video');
        player.currentTime=17;player.volume=.35;player.playbackRate=.75;
        const snapshot=()=>{
          const cards=document.querySelectorAll('[data-video-key="${key}"]');
          const current=cards[0]?.querySelector('video');
          return {count:cards.length,same:current===player,connected:player.isConnected,
            position:current?.currentTime,volume:current?.volume,rate:current?.playbackRate};
        };
        const states=[snapshot()];
        updateExplanation({key:'${key}',requestId:'video-owner-explain',sequence:1,status:'running',
          message:'Synthetic explanation',output:'Reading source',coverage:[]});
        S.q='no matching PR';renderRows();states.push(snapshot());
        S.q='';renderRows();states.push(snapshot());
        S.expanded.delete('${key}');renderRows();states.push(snapshot());
        updateExplanation({...S.explanations.get('${key}'),sequence:2,status:'failed',
          message:'Synthetic failure'});states.push(snapshot());
        S.expanded.add('${key}');renderRows();states.push(snapshot());
        return states;
      })()`);
      assert.deepEqual(states,Array.from({length:6},()=>({count:1,same:true,connected:true,position:17,volume:.35,rate:.75})));
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

    await t.test('Auto effects follow saved settings in both views without changing keyboard actions or pending labels', async t => {
      await reset(true);
      const key = 'sample-service-id:101';
      const savedSettings = await evaluate('structuredClone(S.settings)');
      const labels = () => evaluate(`([...document.querySelectorAll('[data-key="${key}"] button')]
        .filter(b=>['autocomplete','cancelAuto'].includes(b.dataset.act))
        .map(b=>({view:b.closest('.row')?'row':'expanded',title:b.title,aria:b.getAttribute('aria-label'),
          act:b.dataset.act,disabled:b.disabled,busy:b.getAttribute('aria-busy')})))`);
      const assertLabels = async (expected, act = 'autocomplete', busy = false) => {
        assert.deepEqual(await labels(), ['row', 'expanded'].map(view => ({
          view, title: expected, aria: expected, act, disabled: busy, busy: busy ? 'true' : null,
        })));
      };
      const clickControl = async selector => {
        await evaluate(`document.querySelector('#toasts').replaceChildren();
          document.querySelector(${JSON.stringify(selector)}).scrollIntoView({block:'center'});`);
        await frame();
        const p = await evaluate(`(()=>{
          const b=document.querySelector(${JSON.stringify(selector)}),r=b.getBoundingClientRect();
          const x=r.x+r.width/2,y=r.y+r.height/2,hit=document.elementFromPoint(x,y);
          if(!b.contains(hit))throw Error('Auto fixture control is covered: '+${JSON.stringify(selector)});
          return {x,y};
        })()`);
        await click(p); await frame();
      };
      const evidence = [];
      await evaluate(`S.snap.approvalProvider=null;S.approvalSettings=null;
        S.expanded.add('${key}');renderRows();window.autoEffectsRpc=MockHost.rpc;
        window.autoEffectsCalls=[];window.autoEffectsHold=false;
        MockHost.rpc=async(method,p)=>{
          if(!['saveSettings','autoComplete','refresh'].includes(method))throw Error('Unexpected host call '+method);
          window.autoEffectsCalls.push({method,...structuredClone(p)});
          if(method==='saveSettings')return structuredClone(p.settings);
          if(method==='refresh')return null;
          if(window.autoEffectsHold)await new Promise(resolve=>window.autoEffectsComplete=resolve);
          return {...structuredClone(findPr(p.key)),autoCompleteBy:p.enable?'Offline Reviewer':null,
            mergeStrategy:p.enable?S.settings.autoComplete.mergeStrategy:null};
        };`);
      try {
        for (const mergeStrategy of ['squash', 'noFastForward', 'rebase', 'rebaseMerge']) {
          for (const deleteSourceBranch of [false, true]) for (const transitionWorkItems of [false, true]) {
            const previousLabels = await labels();
            await clickControl('#settingsBtn');
            await evaluate(`document.querySelector('#mergeStrategy').value=${JSON.stringify(mergeStrategy)};
              document.querySelector('#deleteSourceBranch').checked=${deleteSourceBranch};
              document.querySelector('#transitionWorkItems').checked=${transitionWorkItems};`);
            assert.deepEqual(await labels(), previousLabels, 'Unsaved effects do not change action labels');
            await clickControl('[data-dact="save"]');
            assert.equal(await evaluate('window.autoEffectsCalls.at(-1)?.method'), 'saveSettings',
              await evaluate("document.querySelector('#toasts').textContent"));
            const expected = `Set auto-complete (${[mergeStrategy,
              ...(deleteSourceBranch ? ['delete branch'] : []),
              ...(transitionWorkItems ? ['complete work items'] : [])].join(', ')})`;
            const committed = await evaluate('S.settings.autoComplete');
            assert.deepEqual(committed, { ...savedSettings.autoComplete, mergeStrategy, deleteSourceBranch, transitionWorkItems });
            await assertLabels(expected);
            evidence.push({ mergeStrategy, deleteSourceBranch, transitionWorkItems, saved: await labels() });
            await keyboard('Escape', 27);
            assert.equal(await evaluate('document.activeElement.id'), 'settingsBtn');
            await click(await point(key, '.row .title')); await frame();
            assert.deepEqual(await labels(), [{
              view: 'row', title: expected, aria: expected, act: 'autocomplete', disabled: false, busy: null,
            }]);
            await click(await point(key, '.row .title')); await frame();
            await assertLabels(expected);
            if (mergeStrategy === 'squash' && !deleteSourceBranch && !transitionWorkItems &&
              process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS) {
              const shot = await send('Page.captureScreenshot', { format: 'png' });
              await writeFile(path.join(process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS, 'auto-effects-disabled.png'),
                Buffer.from(shot.data, 'base64'));
            }
          }
        }
        assert.equal(await evaluate(`window.autoEffectsCalls.filter(c=>c.method==='saveSettings').length`), 16);
        assert.equal(await evaluate(`window.autoEffectsCalls.some(c=>c.method==='autoComplete')`), false);
        assert.deepEqual(await evaluate('window.confirmations'), []);
        await evaluate(`S.settings.autoComplete={mergeStrategy:'squash',deleteSourceBranch:false,transitionWorkItems:false};
          renderRows();window.autoEffectsHold=true;
          document.querySelector('[data-key="${key}"] .row [data-act="autocomplete"]').focus();`);
        assert.equal(await evaluate(`document.activeElement===document.querySelector('[data-key="${key}"] .row [data-act="autocomplete"]')`), true);
        await keyboard('Enter', 13);
        await assertLabels('Setting auto-complete; waiting for ADO confirmation', 'autocomplete', true);
        evidence.push({ state: 'setting', labels: await labels() });
        await evaluate('window.autoEffectsComplete()'); await frame();
        const confirmed = 'Auto-complete confirmed by ADO, set by Offline Reviewer — click to cancel';
        await assertLabels(confirmed, 'cancelAuto');
        evidence.push({ state: 'confirmed', labels: await labels() });
        assert.deepEqual(await evaluate('window.confirmations'), [], 'Setting Auto still has no confirmation');
        await evaluate(`document.querySelector('[data-key="${key}"] .detail [data-act="cancelAuto"]').focus();`);
        assert.equal(await evaluate(`document.activeElement===document.querySelector('[data-key="${key}"] .detail [data-act="cancelAuto"]')`), true);
        await keyboard(' ', 32);
        await assertLabels('Cancelling auto-complete; waiting for ADO confirmation', 'cancelAuto', true);
        evidence.push({ state: 'cancelling', labels: await labels() });
        await evaluate('window.autoEffectsComplete()'); await frame();
        await assertLabels('Set auto-complete (squash)');
        evidence.push({ state: 'cancelled', labels: await labels() });
        assert.deepEqual(await evaluate(`window.autoEffectsCalls.filter(c=>c.method==='autoComplete')`),
          [{ method: 'autoComplete', key, enable: true }, { method: 'autoComplete', key, enable: false }]);
        assert.deepEqual(await evaluate('window.confirmations'), ['Cancel auto-complete on !101?']);
      } finally {
        t.diagnostic(JSON.stringify({ scenarios: evidence, rpc: await evaluate(`window.autoEffectsCalls.filter(c=>c.method==='autoComplete')`),
          confirmations: await evaluate('window.confirmations') }));
        await evaluate(`MockHost.rpc=window.autoEffectsRpc;delete window.autoEffectsRpc;
          if(S.drawer)closeDrawer();S.settings=${JSON.stringify(savedSettings)};`);
        await reset();
      }
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

    await t.test('GUI keyboard expands rows, build gates and retained run headings without launching an action', async () => {
      await reset(true);
      const key = 'sample-service-id:101';
      await evaluate(`S.logs.clear();S.snap.prs=S.snap.prs.map(p=>p.key==='${key}'?{...p,gates:{...p.gates,
        build:{state:'fail',label:'Synthetic build'},comments:{state:'fail',label:'Open thread'}},
        failedBuilds:[{buildId:9001,name:'Synthetic build',tasks:[{logId:101,name:'Synthetic failed task',issues:[]}]}]}:p);render();
        window.inspectionClicks=[];window.inspectionClick=e=>{
          const c=e.target.closest('[role="button"][data-act][aria-expanded]');
          if(c)window.inspectionClicks.push({key:c.closest('.pr').dataset.key,act:c.dataset.act,gate:c.dataset.gate||null});
        };document.addEventListener('click',window.inspectionClick,true);`);
      const tabTo = async selector => {
        await evaluate(`document.querySelector('#settingsBtn').focus()`);
        for (let n = 0; n < 65; n++) {
          await keyboard('Tab', 9);
          if (await evaluate(`document.activeElement.matches(${JSON.stringify(selector)})`)) return;
        }
        assert.fail(`Tab never reached ${selector}`);
      };
      const state = () => evaluate(`({key:document.activeElement.closest('.pr')?.dataset.key,
        expanded:document.activeElement.getAttribute('aria-expanded'),open:S.expanded.has('${key}')})`);
      const row = `[data-key="${key}"] .c-pr`;
      await tabTo(row);
      assert.equal(await evaluate(`getComputedStyle(document.activeElement).outlineStyle`), 'solid');
      assert.equal(await evaluate(`document.querySelector('[data-key="${key}"] .detail')`), null);
      assert.equal(await evaluate(`!!document.querySelector('button button,[role="button"] button')`), false);
      for (const [keyName, code] of [['Enter', 13], [' ', 32]]) {
        const scroll = await evaluate(`({page:scrollY,main:document.querySelector('main').scrollTop})`);
        const clicks = await evaluate('window.inspectionClicks.length');
        await keyboard(keyName, code);
        assert.deepEqual(await state(), {key,expanded:'true',open:true});
        assert.equal(await evaluate('window.inspectionClicks.length'), clicks + 1, 'One activation per key press');
        if (keyName === ' ') assert.deepEqual(await evaluate(`({page:scrollY,main:document.querySelector('main').scrollTop})`), scroll);
        await send('Input.dispatchKeyEvent', {type:'keyDown',key:keyName,code:keyName===' '?'Space':keyName,
          windowsVirtualKeyCode:code,autoRepeat:true});
        await send('Input.dispatchKeyEvent', {type:'keyUp',key:keyName,windowsVirtualKeyCode:code});
        assert.deepEqual(await state(), {key,expanded:'true',open:true});
        assert.equal(await evaluate('window.inspectionClicks.length'), clicks + 1, 'Held keys must not toggle again');
        await keyboard(keyName, code);
        assert.deepEqual(await state(), {key,expanded:'false',open:false});
      }
      for (const gate of ['build', 'comments']) {
        for (const [keyName, code] of [['Enter', 13], [' ', 32]]) {
          await tabTo(`[data-key="${key}"] [data-act="gate"][data-gate="${gate}"]`);
          assert.deepEqual(await state(), {key,expanded:'false',open:false});
          await keyboard(keyName, code);
          assert.deepEqual(await state(), {key,expanded:'true',open:true});
          assert.equal(await evaluate('document.activeElement.dataset.gate'), gate);
          await keyboard('Escape', 27);
          assert.deepEqual(await state(), {key,expanded:'false',open:false});
        }
      }
      const inactiveGates = await evaluate(`[...document.querySelectorAll('[data-key="${key}"] .pill:not([data-act])')].map(c=>({
        role:c.getAttribute('role'),tab:c.getAttribute('tabindex'),expanded:c.getAttribute('aria-expanded')}))`);
      assert.ok(inactiveGates.length > 0);
      assert.ok(inactiveGates.every(c => c.role === null && c.tab === null && c.expanded === null));
      await evaluate(`document.querySelector('#search').focus();
        {const pr=S.snap.prs.find(p=>p.key==='${key}');
        S.runs.set('keyboard-shelf',{...pr,runId:'keyboard-shelf',status:'running',output:'Synthetic retained output'});
        setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();}`);
      for (const [keyName, code] of [['Enter', 13], [' ', 32]]) {
        await tabTo('#runShelf .run-heading');
        assert.deepEqual(await state(), {key,expanded:'false',open:false});
        const scroll = await evaluate(`({page:scrollY,main:document.querySelector('main').scrollTop})`);
        await keyboard(keyName, code);
        assert.deepEqual(await state(), {key,expanded:'true',open:true});
        assert.equal(await evaluate(`document.querySelector('#runShelf .run-output').textContent`), 'Synthetic retained output');
        if (keyName === ' ') assert.deepEqual(await evaluate(`({page:scrollY,main:document.querySelector('main').scrollTop})`), scroll);
        await keyboard(keyName, code);
        assert.deepEqual(await state(), {key,expanded:'false',open:false});
        assert.equal(await evaluate(`document.querySelector('#runShelf .run-output')`), null);
      }
      const requests = await calls();
      assert.ok(requests.every(c => c.method === 'logTail'), JSON.stringify(requests));
      assert.ok(requests.every(c => c.key === key && c.buildId === 9001 && c.logId === 101));
      assert.deepEqual(await evaluate('window.confirmations'), []);
      await evaluate('document.removeEventListener("click",window.inspectionClick,true)');
    });

    await t.test('GUI keyboard inspection focus survives refresh, Escape and collapsed shelf streaming without hidden targets', async () => {
      await reset(true);
      const key = 'sample-service-id:101';
      await keyboard('Tab', 9);
      await evaluate(`document.querySelector('[data-key="${key}"] .c-pr').focus();window.swapRows()`);
      assert.equal(await evaluate('!!S.pendingSnapshot'), true);
      await keyboard('Enter', 13);
      assert.equal(await evaluate(`document.activeElement.closest('.pr').dataset.key`), key);
      assert.equal(await evaluate(`document.activeElement.getAttribute('aria-expanded')`), 'true');
      await evaluate('renderRows()');
      assert.equal(await evaluate(`document.activeElement.closest('.pr').dataset.key`), key);
      await keyboard('Escape', 27);
      assert.equal(await evaluate(`document.activeElement.getAttribute('aria-expanded')`), 'false');
      await keyboard(' ', 32);
      assert.equal(await evaluate(`document.activeElement.getAttribute('aria-expanded')`), 'true');
      await evaluate(`document.querySelector('#search').focus()`); await frame();
      assert.equal(await evaluate(`document.querySelector('.pr').dataset.key`), 'sample-api-id:104');
      await evaluate(`{const pr=S.snap.prs.find(p=>p.key==='${key}');
        S.runs.set('keyboard-stream',{...pr,runId:'keyboard-stream',status:'running',output:'Initial output'});
        setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!=='${key}')});renderRows();
        document.querySelector('#runShelf .run-heading').focus();
        updateCopilotRun({...S.runs.get('keyboard-stream'),output:'Streamed output'});}`);
      assert.equal(await evaluate(`document.activeElement.matches('#runShelf .run-heading')`), true);
      await keyboard(' ', 32);
      assert.equal(await evaluate(`document.querySelector('#runShelf .run-output').textContent`), 'Streamed output');
      await keyboard('Escape', 27);
      assert.equal(await evaluate(`document.activeElement.getAttribute('aria-expanded')`), 'false');
      await evaluate(`setSnapshot({...S.snap,prs:S.snap.prs.map(p=>({...p,title:'Updated after shelf focus'}))})`);
      assert.equal(await evaluate('!!S.pendingSnapshot'), true);
      await keyboard('Tab', 9);
      if (!await evaluate(`!!document.activeElement.closest('header')`)) await keyboard('Tab', 9);
      assert.equal(await evaluate(`!!document.activeElement.closest('header')`), true);
      assert.equal(await evaluate('S.pendingSnapshot'), null);
      assert.equal(await evaluate(`document.querySelector('#rows .title').textContent`), 'Updated after shelf focus');
      await evaluate(`S.q='no matching inspection target';render()`);
      assert.equal(await evaluate(`document.querySelector('[data-key="${key}"]')`), null);
      assert.equal(await evaluate(`document.activeElement.closest('.pr')`), null);
      await evaluate(`S.q='';S.settings.hidden.push('${key}');render()`);
      assert.equal(await evaluate(`document.querySelector('[data-key="${key}"]')`), null);
      assert.deepEqual(await calls(), []);
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

    await t.test('GUI failed log Retry preserves errors, repeated failure, deduplication and task/PR isolation after refresh/reopen', async () => {
      await reset(true);
      const key = 'sample-service-id:101', other = 'sample-api-id:104';
      const row = `[data-key="${key}"]`, otherRow = `[data-key="${other}"]`;
      const selector = `${row} [data-act="loadLog"][data-build="9002"][data-logid="102"]`;
      const task = `${row} .task:first-of-type`, additional = `${row} .task:last-of-type`;
      await evaluate(`{
        const snap=structuredClone(S.snap);
        for(const pr of snap.prs){
          pr.lane='blocked';pr.project=pr.key==='${key}'?'Sample':'Other';
          pr.failedBuilds=[{buildId:9002,name:'Synthetic retry build',url:'https://example.invalid/build/9002',
            tasks:[{logId:102,name:'Synthetic failed task',issues:[]},{logId:103,name:'Synthetic additional task',issues:[]}]}];
        }
        window.retryPrior=new Map();
        for(const pr of snap.prs)for(const task of pr.failedBuilds[0].tasks){
          const cache=logKey(pr,9002,task.logId);window.retryPrior.set(cache,S.logs.get(cache));S.logs.delete(cache);
        }
        window.logRpc=MockHost.rpc;window.retryCalls=[];window.releaseLogRetry=null;
        MockHost.rpc=async(method,p)=>{
          if(method==='logTail'){
            window.retryCalls.push({method,...p});
            if(p.key==='${key}'&&p.buildId===9002&&p.logId===102){
              const attempt=window.retryCalls.filter(c=>c.key==='${key}'&&c.logId===102).length;
              if(attempt===1)throw Error('Synthetic log HTTP 503 <transient>');
              await new Promise(resolve=>window.releaseLogRetry=resolve);
              window.releaseLogRetry=null;
              if(attempt===2)throw Error('Synthetic log HTTP 503 again');
              return '2026-11-21T10:00:00.123Z Recovered synthetic log tail\\n';
            }
            return 'Independent '+p.key+'/'+p.logId;
          }
          return window.logRpc(method,p);
        };
        setSnapshot(snap);
      }`);
      const idle = async () => {
        await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      };
      const text = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).textContent`);
      const reads = () => evaluate('window.retryCalls');
      const reopen = async () => {
        await click(await point(key, '.row .title')); await idle();
        await click(await point(key, '.row .title')); await idle();
      };
      try {
        await click(await point(key, '.row .title')); await idle();
        await click(await point(other, '.row .title')); await idle();
        assert.equal(await text(selector), 'Retry log tail');
        assert.match(await text(task), /Synthetic log HTTP 503 <transient>/);
        assert.match(await text(otherRow), /Independent sample-api-id:104\/102/);
        assert.equal(await text(`${additional} [data-act="loadLog"]`), 'Load log tail');
        await click(await elementPoint('#refreshBtn')); await idle();
        await reopen();
        assert.equal((await reads()).length, 2, 'Refresh/reopen does not automatically retry or reset other logs');
        assert.equal(await text(selector), 'Retry log tail');

        await click(await elementPoint(selector)); await frame();
        await click(await elementPoint(selector)); await frame();
        assert.equal((await reads()).filter(c => c.key === key && c.logId === 102).length, 2);
        assert.match(await text(task), /Synthetic log HTTP 503 <transient>/);
        await idle();
        assert.equal(await evaluate(`document.querySelector(${JSON.stringify(selector)}).disabled`), true);
        assert.equal(await evaluate(`document.querySelector(${JSON.stringify(selector)}).getAttribute('aria-busy')`), 'true');
        await click(await elementPoint('#refreshBtn')); await idle();
        await reopen();
        assert.match(await text(task), /Synthetic log HTTP 503 <transient>/);
        assert.equal(await evaluate(`document.querySelector(${JSON.stringify(selector)}).disabled`), true);
        await click(await elementPoint(selector)); await idle();
        assert.equal((await reads()).filter(c => c.key === key && c.logId === 102).length, 2);
        await click(await elementPoint(`${additional} [data-act="loadLog"]`)); await idle();
        assert.match(await text(additional), /Independent sample-service-id:101\/103/);
        assert.match(await text(otherRow), /Independent sample-api-id:104\/102/);
        await evaluate('window.releaseLogRetry()'); await idle();
        assert.match(await text(task), /Synthetic log HTTP 503 again/);
        assert.equal(await text(selector), 'Retry log tail');
        assert.equal(await evaluate(`document.querySelector(${JSON.stringify(selector)}).disabled`), false);

        await evaluate(`document.querySelector(${JSON.stringify(selector)}).focus()`);
        await keyboard('Enter', 13); await idle();
        assert.match(await text(task), /Synthetic log HTTP 503 again/);
        await evaluate('window.releaseLogRetry()'); await idle();
        assert.match(await text(task), /Recovered synthetic log tail/);
        assert.doesNotMatch(await text(task), /Synthetic log HTTP 503|Retry log tail/);
        assert.match(await text(additional), /Independent sample-service-id:101\/103/);
        assert.match(await text(otherRow), /Independent sample-api-id:104\/102/);
        await click(await elementPoint(`${task} [data-act="copyLog"]`)); await idle();
        assert.equal((await calls()).filter(c => c.method === 'copy').at(-1).text, 'Recovered synthetic log tail\n');
        await click(await elementPoint(`${task} [data-act="ts"]`)); await idle();
        assert.match(await text(`${task} pre.log`), /10:00:00\.123/);
        await click(await elementPoint(`${task} [data-act="copyLog"]`)); await idle();
        assert.equal((await calls()).filter(c => c.method === 'copy').at(-1).text,
          '2026-11-21T10:00:00.123Z Recovered synthetic log tail\n');
        await click(await elementPoint(`${row} .card.failed [data-act="url"]`)); await idle();
        assert.deepEqual((await calls()).filter(c => c.method === 'openUrl').at(-1),
          {method:'openUrl',url:'https://example.invalid/build/9002'});
        await click(await elementPoint('#refreshBtn')); await idle(); await reopen();
        assert.match(await text(task), /Recovered synthetic log tail/);
        assert.deepEqual(await reads(), [
          {method:'logTail',key,buildId:9002,logId:102,lines:200},
          {method:'logTail',key:other,buildId:9002,logId:102,lines:200},
          {method:'logTail',key,buildId:9002,logId:102,lines:200},
          {method:'logTail',key,buildId:9002,logId:103,lines:200},
          {method:'logTail',key,buildId:9002,logId:102,lines:200},
        ]);
        t.diagnostic('Exact logTail counts: target 102=3 (503,503,success), target 103=1, other-project 102=1; lines=200 throughout. Mouse and keyboard Retry, Refresh/reopen, visible errors, busy state, timestamps/copy/open all asserted.');
      } finally {
        await evaluate('window.releaseLogRetry?.()'); await idle();
        assert.equal(await evaluate('S.logLoads.size'), 0, 'No owned log request remains in flight');
        await evaluate(`MockHost.rpc=window.logRpc;
          for(const [cache,prior] of window.retryPrior)if(prior===undefined)S.logs.delete(cache);else S.logs.set(cache,prior);`);
      }
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
        const snap=structuredClone(S.snap);snap.prs.find(p=>p.key==='${target}').lane='waiting';setSnapshot(snap);
      })()`);
      assert.deepEqual(await keys(), [...before].reverse(), 'Background lane change must stay in place');
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'r', code: 'KeyR', windowsVirtualKeyCode: 82, text: 'r' });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'r', code: 'KeyR', windowsVirtualKeyCode: 82 });
      await frame();
      assert.deepEqual(await keys(), before, 'Keyboard refresh must restore lane priority');
      await evaluate(`window.MockHost.rpc=window.originalRpc;delete window.originalRpc;`);
    });

    await t.test('layout primary AI and Fix controls fit desktop and narrow detail independently of the panning table', async () => {
      for (const width of [1600, 1000, 760]) {
        await send('Emulation.setDeviceMetricsOverride', { width, height: 1000, deviceScaleFactor: 1, mobile: false });
        await reset();
        const key = 'sample-service-id:105';
        await evaluate(`S.expanded.add('${key}');renderRows();`);
        const fitting = await evaluate(`(()=>{const row=document.querySelector('#rows [data-key="${key}"]'),main=document.querySelector('main');
          main.scrollLeft=0;
          return [...row.querySelectorAll('.detail [data-act="explainPr"],.detail [data-assistance-intent],.detail [data-act="triage"],.detail [data-act="copyPrompt"]')]
            .every(el=>{const r=el.getBoundingClientRect();return r.width>0&&r.left>=0&&r.right<=main.clientWidth+main.getBoundingClientRect().left;});})()`);
        assert.equal(fitting, true, 'Primary controls fit width '+width);
        assert.equal(await evaluate(`document.querySelector('#rows [data-key="${key}"] .run-output').hidden`), true, 'No idle log well');
        await evaluate(`{const pr=S.snap.prs.find(p=>p.key==='${key}');
          S.runs.set('fit-earlier',{...pr,runId:'fit-earlier',status:'completed',output:'Earlier\\n',startedAt:'2026-01-01T00:00:00Z'});
          S.runs.set('fit-main',{...pr,runId:'fit-main',status:'completed',output:'Short output\\n',startedAt:'2026-01-02T00:00:00Z'});renderRows();}`);
        assert.equal(await evaluate(`document.querySelector('#rows [data-key="${key}"] .run-earlier .run-history').open`), false);
        await click(await point(key, '.run-earlier .run-history > summary')); await move(0, 0); await frame();
        assert.equal(await evaluate(`document.querySelector('#rows [data-key="${key}"] .run-earlier .run-history').open`), true, 'History opens by real input');
        assert.ok(await evaluate(`document.querySelector('#rows [data-key="${key}"] .copilot-run:not(.run-earlier) .run-output').clientHeight`) < 100, 'Short output has content-driven height');
      }
    });

    const layoutCards = key => evaluate(`(()=>{const row=document.querySelector('#rows [data-key="${key}"]'),detail=row.querySelector('.detail');
      const box=e=>{const r=e.getBoundingClientRect();return {top:r.top,bottom:r.bottom};};
      return {cards:[...detail.querySelectorAll('.card')].map(c=>({head:c.querySelector('h3').textContent.trim().replace(/\\s+/g,' '),
          run:c.classList.contains('copilot-run'),earlier:c.classList.contains('run-earlier'),...box(c)})),
        columns:[...detail.querySelectorAll(':scope > .stack:not(.full)')].map(box),trailing:box(detail.lastElementChild),
        scopes:[...row.querySelectorAll('.run-scope')].map(p=>({inStatus:!!p.closest('.fix-home')?.querySelector('[data-act="triage"]'),
          inRun:!!p.closest('.copilot-run'),visible:p.getClientRects().length>0,text:p.textContent}))};})()`);
    const cardIndex = (layout, head) => {
      const i = layout.cards.findIndex(c => c.head.startsWith(head));
      assert.ok(i >= 0, `Missing card ${head}: ${layout.cards.map(c => c.head).join(' | ')}`);
      return i;
    };
    const assertOneStatusScope = layout => {
      assert.equal(layout.scopes.length, 1, 'One Fix PR execution notice per expanded PR');
      assert.deepEqual([layout.scopes[0].inStatus, layout.scopes[0].inRun, layout.scopes[0].visible], [true, false, true]);
      assert.match(layout.scopes[0].text, /By default, Fix PR asks Copilot on Windows to try to fix merge conflicts, failing checks and review feedback/);
    };

    await t.test('Fix PR plain-language summary and accessible permission details survive panel updates', async () => {
      await reset();
      const key = 'sample-service-id:105', root = `#rows [data-key="${key}"]`;
      await evaluate(`S.expanded.add('${key}');renderRows();`);
      await move(0, 0); await frame();
      const state = () => evaluate(`(()=>{
        const scope=document.querySelector('${root} .run-scope'),button=scope.querySelector('button');
        const detail=document.getElementById(button.getAttribute('aria-controls'));
        return {summary:scope.querySelector('.run-summary').textContent,hidden:detail.hidden,
          expanded:button.getAttribute('aria-expanded'),name:button.getAttribute('aria-label'),
          associated:button.getAttribute('aria-describedby')===detail.id,details:detail.textContent,
          visible:detail.getClientRects().length>0};
      })()`);
      const info = await state();
      assert.match(info.summary, /try to fix merge conflicts, failing checks and review feedback/);
      assert.match(info.summary, /New runs are asked to validate, commit and push fixes back to this PR/);
      assert.doesNotMatch(info.summary, /--yolo|sandbox|confirmation/);
      assert.deepEqual([info.hidden, info.expanded, info.visible, info.associated], [true, 'false', false, true]);
      assert.equal(info.name, 'Fix PR permissions and limitations');
      for (const text of ['--yolo', 'not globally', 'not a filesystem sandbox', 'prompt instruction, not enforced',
        'no-approval/policy-bypass', 'Legacy WSL runs keep their recorded environment and original permission/publish scope', 'Sign-in is required',
        'noninteractive', 'Exit 0 does not verify', 'verify the results'])
        assert.ok(info.details.includes(text), `Missing boundary: ${text}`);

      const control = await point(key, '.run-info-button');
      await move(control.x, control.y); await frame();
      assert.equal((await state()).visible, true, 'Mouse hover reveals details');
      await keyboard('Escape', 27);
      assert.equal((await state()).hidden, true, 'Escape dismisses hover content without focusing the icon');
      await move(0, 0); await move(control.x, control.y); await frame();
      const detailPoint = await point(key, '.run-info-details');
      await move(detailPoint.x, detailPoint.y); await frame();
      assert.equal((await state()).visible, true, 'Pointer can move onto the details to read them');
      await move(0, 0); await frame();
      assert.equal((await state()).hidden, true, 'Leaving a transient preview closes it');
      await evaluate(`document.querySelector('${root} [data-act="copyPrompt"]').focus();`);
      await keyboard('Tab', 9);
      assert.equal(await evaluate(`document.activeElement.matches('${root} .run-info-button')`), true);
      assert.equal((await state()).expanded, 'true', 'Keyboard focus reveals associated details');
      await keyboard('Escape', 27);
      assert.equal((await state()).hidden, true);
      assert.equal(await evaluate(`document.querySelector('${root}').classList.contains('open')`), true,
        'Dismissing details must not collapse the PR');
      await keyboard('Enter', 13);
      await keyboard('Tab', 9);
      assert.equal((await state()).visible, true, 'Keyboard activation pins details after focus leaves');
      await click(await point(key, '.run-info-button'));
      assert.equal((await state()).hidden, true, 'Second activation closes pinned details');
      await move(0, 0);
      await evaluate('document.activeElement.blur();');
      const touch = await point(key, '.run-info-button');
      await send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: touch.x, y: touch.y }] });
      await send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      await frame();
      assert.equal((await state()).visible, true, 'Touch opens details without hover');

      // Re-rendered PRs get fresh disclosure state; recovered output keeps its captured notice.
      await evaluate(`{
        const pr=S.snap.prs.find(p=>p.key==='${key}');
        S.runs.set('legacy-copy-run',{...pr,runId:'legacy-copy-run',status:'running',
          message:'Earlier run: no push permitted',output:'Original no-push scope',revision:1});
        renderRows();
      }`);
      await move(0, 0); await evaluate('document.activeElement.blur();'); await frame();
      assert.equal((await state()).hidden, true, 'Panel replacement resets pinned state');
      assert.match(await evaluate(`document.querySelector('${root} .run-status').textContent`), /Earlier run: no push permitted/);
      await evaluate(`updateCopilotRun({...S.runs.get('legacy-copy-run'),status:'completed',revision:2,
        message:'Earlier run: no push permitted; inspect results',exitCode:0});`);
      assert.match(await evaluate(`document.querySelector('${root} .run-status').textContent`),
        /exit 0: Earlier run: no push permitted; inspect results/);
      assert.equal((await state()).hidden, true);
      assert.deepEqual((await calls()).filter(c=>c.method==='startCopilot'), [], 'Info never starts a run');
    });

    await t.test('layout hierarchy leads with status and AI; stable Fix home precedes full diagnostic logs and context', async () => {
      const key = 'sample-service-id:105';
      for (const loadError of [false, true]) {
        await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
        await reset();
        await evaluate(`{const key='${key}';
          ${loadError ? "setSnapshot({...S.snap,prs:S.snap.prs.map(p=>p.key===key?{...p,error:'Synthetic load error'}:p)});" : ''}
          const pr=S.snap.prs.find(p=>p.key===key);
          S.runs.set('layout-done-run',{...pr,runId:'layout-done-run',status:'completed',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output:'Finished synthetic output\\n',outputOffset:26});
          S.expanded.add(key);renderRows();}`);
        await frame();
        const layout = await layoutCards(key);
        const gate = cardIndex(layout, '✕ Failed gate'), description = cardIndex(layout, 'Description'), status = cardIndex(layout, 'Status');
        const explain = cardIndex(layout, 'PR assistance'), video = cardIndex(layout, 'Short PR video explainer');
        const run = layout.cards.findIndex(c => c.run);
        if (loadError) assert.ok(cardIndex(layout, 'Load error') < status, 'Load error leads the tools');
        assert.ok(status < explain && explain < video && video < run && run < gate && gate < description,
          `Unexpected detail order: ${layout.cards.map(c => c.head).join(' | ')}`);
        assert.ok(layout.cards[run].bottom <= layout.cards[gate].top, 'Completed Fix remains before full logs');
        assert.equal(await evaluate(`!!document.querySelector('#rows [data-key="${key}"] .explanation > .assistance-body > .assistance-video .pr-video')`), true, 'Video is an alternate output inside assistance, not a peer card');
        assertOneStatusScope(layout);
        if (loadError) {
          assert.equal(await evaluate(`document.querySelector('#rows [data-key="${key}"] .explanation [data-act="explainPr"]').disabled`), true);
          continue;
        }
        await click(await point(key, '.explanation [data-act="explainPr"]'));
        await frame();
        assert.equal((await calls()).filter(c => c.method === 'explainPr').length, 1, 'Explain launches exactly once');
        await evaluate(`new Promise((resolve,reject)=>{const deadline=Date.now()+3000;
          const check=()=>document.querySelector('#rows [data-key="${key}"] pre.log[data-log]')?resolve():
            Date.now()>deadline?reject(Error('Failed log tail did not load')):setTimeout(check,20);check();})`);
        await evaluate(`{const row=document.querySelector('#rows [data-key="${key}"]');window.layoutRow=row;
          window.layoutLog=row.querySelector('pre.log[data-log]');const range=document.createRange();range.selectNodeContents(window.layoutLog);
          window.getSelection().removeAllRanges();window.getSelection().addRange(range);window.layoutSelected=window.getSelection().toString();
          const r=S.explanations.get('${key}');
          updateExplanation({...r,sequence:r.sequence+1,output:'Streaming synthetic explanation',message:'Still reading'});}`);
        await frame();
        assert.equal(await evaluate(`window.layoutRow===document.querySelector('#rows [data-key="${key}"]') &&
          window.layoutLog===window.layoutRow.querySelector('pre.log[data-log]') && window.layoutLog.isConnected`), true,
          'Explanation streaming updates in place without rebuilding the failed log');
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.layoutSelected'));
        assert.equal(await evaluate(`window.layoutRow.querySelector('.explain-output').hidden &&
          window.layoutRow.querySelector('.explain-output').textContent==='' &&
          !!window.layoutRow.querySelector('.explain-waiting:not([hidden]) .video-spinner')`), true);
        if (process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS) {
          await evaluate(`window.getSelection().removeAllRanges();document.querySelector('#rows [data-key="${key}"]').scrollIntoView({block:'start'});`);
          await frame();
          const shot = await send('Page.captureScreenshot', { format: 'png' });
          await writeFile(path.join(process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS, 'after-expanded-1600.png'), Buffer.from(shot.data, 'base64'));
        }
      }
    });

    await t.test('layout actionable and uncertain Fix runs lead after blockers and Show run reveals that exact run', async () => {
      const key = 'sample-service-id:105';
      for (const live of [{ status: 'running' }, { status: 'needs-attention', mayBeRunning: true }]) {
        await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 600, deviceScaleFactor: 1, mobile: false });
        await reset();
        await evaluate(`{const key='${key}',pr=S.snap.prs.find(p=>p.key===key);
          S.runs.set('layout-earlier-run',{...pr,runId:'layout-earlier-run',status:'completed',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output:'Earlier synthetic output\\n',outputOffset:25});
          const output='Live synthetic output line\\n'.repeat(400);
          S.runs.set('layout-live-run',{...pr,runId:'layout-live-run',...${JSON.stringify(live)},
            startedAt:'2026-01-02T00:00:00Z',output,outputOffset:output.length});
          S.expanded.add(key);renderRows();}`);
        await frame();
        const layout = await layoutCards(key);
        const gate = cardIndex(layout, '✕ Failed gate'), description = cardIndex(layout, 'Description');
        const runs = layout.cards.map((c, i) => c.run ? i : -1).filter(i => i >= 0);
        assert.deepEqual(runs.map(i => layout.cards[i].earlier), [false, true]);
        assert.ok(cardIndex(layout, 'Status') < runs[0] && runs[1] < gate && gate < description, 'The live run group follows compact blockers and AI, before full diagnostics');
        assert.ok(cardIndex(layout, 'PR assistance') < runs[0]);
        assertOneStatusScope(layout);
        assert.equal(await evaluate(`(()=>{const c=document.querySelector('#rows [data-key="${key}"] .copilot-run:not(.run-earlier)');
          return c.dataset.runId==='layout-live-run' && !c.querySelector('[data-act="cancelRun"]').hidden &&
            c.nextElementSibling?.dataset.runId==='layout-earlier-run';})()`), true, 'Cancel stays visible and run cards stay adjacent');
        for (const via of ['pointer', 'keyboard']) {
          await evaluate(`document.querySelector('main').scrollTop=0`);
          await frame();
          if (via === 'pointer') await click(await point(key, '.detail [data-act="triage"]'));
          else {
            await evaluate(`document.querySelector('#rows [data-key="${key}"] .detail [data-act="triage"]').focus({preventScroll:true})`);
            await keyboard('Enter', 13);
          }
          await frame();
          // Below the sticky header and actually hit-testable, not merely inside main.
          const shown = await evaluate(`(()=>{const main=document.querySelector('main').getBoundingClientRect(),head=document.querySelector('.thead').getBoundingClientRect();
            const card=document.querySelector('#rows [data-key="${key}"] .copilot-run:not(.run-earlier)');
            const clear=e=>{const r=e.getBoundingClientRect();return r.top>=head.bottom-.5&&r.bottom<=main.bottom+.5;};
            const cancel=card.querySelector('[data-act="cancelRun"]'),r=cancel.getBoundingClientRect(),hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
            return {run:card.dataset.runId,status:clear(card.querySelector('.run-status')),cancel:clear(cancel),hit:!!hit&&cancel.contains(hit),
              focused:document.activeElement===card.querySelector('.run-output')};})()`);
          assert.deepEqual(shown, { run: 'layout-live-run', status: true, cancel: true, hit: true, focused: true }, `Show run via ${via}`);
        }
        assert.equal((await calls()).filter(c => c.method === 'startCopilot').length, 0, 'Show run never launches another run');
      }
    });

    await t.test('layout run lifecycle keeps its home stable and defers exact-run promotion while rows are in use', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await reset();
      const key = 'sample-service-id:105';
      await evaluate(`{const key='${key}',pr=S.snap.prs.find(p=>p.key===key);
        S.runs.set('lc-new',{...pr,runId:'lc-new',id:0,status:'completed',mayBeRunning:false,startedAt:'2026-01-02T00:00:00Z',output:'New\\n',outputOffset:4});
        S.runs.set('lc-old',{...pr,runId:'lc-old',id:0,status:'needs-attention',mayBeRunning:false,startedAt:'2026-01-01T00:00:00Z',output:'Old\\n',outputOffset:4});
        S.expanded.add(key);renderRows();}`);
      await frame();
      const place = async () => {
        const layout = await layoutCards(key);
        const runs = layout.cards.map((c, i) => c.run ? i : -1).filter(i => i >= 0);
        return { lead: await evaluate(`document.querySelector('#rows [data-key="${key}"] .detail').dataset.runLead`),
          ids: await evaluate(`[...document.querySelectorAll('#rows [data-key="${key}"] .copilot-run')].map(c=>c.dataset.runId+(c.classList.contains('run-earlier')?':earlier':''))`),
          beforeContext: runs.every(i => i < cardIndex(layout, 'Description')), adjacent: runs[1] === runs[0] + 1,
          label: await evaluate(`document.querySelector('#rows [data-key="${key}"] .detail [data-act="triage"]').textContent.trim()`) };
      };
      assert.deepEqual(await place(), { lead: '1', ids: ['lc-new', 'lc-old:earlier'], beforeContext: true, adjacent: true, label: 'Fix PR' });
      await evaluate(`window.fixHome=document.querySelector('#rows [data-key="${key}"] .fix-home');`);
      await evaluate(`updateCopilotRun({...S.runs.get('lc-old'),status:'running',mayBeRunning:true})`);
      await frame();
      assert.deepEqual(await place(), { lead: '1', ids: ['lc-old', 'lc-new:earlier'], beforeContext: true, adjacent: true, label: 'Show run' },
        'Promoted earlier run leads with its sibling and no manual render');
      const inRow = await point(key, '.detail .copilot-run:not(.run-earlier) .run-output');
      await move(inRow.x, inRow.y);
      await evaluate(`window.lcCard=document.querySelector('#rows [data-key="${key}"] .copilot-run:not(.run-earlier)');
        updateCopilotRun({...S.runs.get('lc-old'),status:'completed',mayBeRunning:false,id:0})`);
      await frame();
      assert.deepEqual([(await place()).lead, await evaluate(`window.lcCard.isConnected && S.runRelocations.has('${key}')`)], ['1', true], 'Placement waits while rows are in use');
      assert.equal((await place()).label, 'Fix PR', 'Label still updates in place while deferred');
      await move(10, 10);
      await frame();
      assert.deepEqual(await place(), { lead: '1', ids: ['lc-new', 'lc-old:earlier'], beforeContext: true, adjacent: true, label: 'Fix PR' },
        'Completed group keeps its early home while preferred run updates');
      assert.equal(await evaluate(`document.querySelector('#rows [data-key="${key}"] .fix-home').previousElementSibling.querySelector('.ai-assistance')!==null`), true);
      for (let i = 0; i < 3; i++) await evaluate(`updateCopilotRun({...S.runs.get('lc-new'),output:S.runs.get('lc-new').output+'tick\\n',outputOffset:S.runs.get('lc-new').outputOffset+5,revision:${i + 1}})`);
      assert.equal(await evaluate(`document.querySelector('#rows [data-key="${key}"] .copilot-run').isConnected && !S.rowsPending && !S.runRelocations.size`), true);
      // Latest run uncertain but not maybe-running is still cancelable: it leads and the label is Show run.
      await evaluate(`updateCopilotRun({...S.runs.get('lc-new'),status:'needs-attention',mayBeRunning:false,id:7,revision:9})`);
      await frame();
      assert.deepEqual(await place(), { lead: '1', ids: ['lc-new', 'lc-old:earlier'], beforeContext: true, adjacent: true, label: 'Show run' });
    });

    await t.test('layout compact Status keeps all conflicts and required-review work visible above AI and Fix', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      for (const c of [{ blockers: ['Merge conflicts'], humanActions: [], expect: ['Merge conflicts'] },
        { blockers: [], humanActions: ['reviews'], cached: true, expect: ['Human reviewer approval needed (+1)'] },
        { mixed: true, blockers: ['Merge conflicts with main'], humanActions: ['reviews'],
          expect: ['Merge conflicts with main', 'Human reviewer approval needed (+1)'] }]) {
        await reset();
        const key = 'sample-service-id:105';
        await evaluate(`{const key='${key}',pr=S.snap.prs.find(p=>p.key===key);
          const fbs=${!!c.mixed}?[{buildId:9105,name:'Synthetic mixed build',tasks:[{logId:105,name:'Synthetic mixed task',issues:[]}]}]:[];
          Object.assign(pr,{failedBuilds:fbs,error:null,approval:null,humanActions:${JSON.stringify(c.humanActions)},
            blockers:[...fbs.map(f=>'Build failed: '+f.name),...${JSON.stringify(c.blockers)}]});
          if(${!!c.cached})S.approvals.set(key,{kind:'error',message:'Synthetic cached approval outcome'});else S.approvals.delete(key);
          S.runs.set('lead-live',{...pr,runId:'lead-live',status:'running',mayBeRunning:true,startedAt:'2026-01-02T00:00:00Z',output:'Live\\n'.repeat(200),outputOffset:1000});
          S.expanded.add(key);renderRows();}`);
        await frame();
        const layout = await layoutCards(key);
        const blocking = cardIndex(layout, 'Status'), run = layout.cards.findIndex(x => x.run);
        assert.ok(blocking < run && run < cardIndex(layout, 'Description'), layout.cards.map(x => x.head).join(' | '));
        const blockingText = await evaluate(`document.querySelector('#rows [data-key="${key}"] .pr-status').textContent`);
        for (const reason of c.expect) assert.ok(blockingText.includes(reason), `Blocking card misses ${reason}: ${blockingText}`);
        if (c.mixed) {
          assert.ok(cardIndex(layout, '✕ Failed gate') > run, 'Full diagnostics follow primary tools');
          assert.ok(blockingText.includes('Build failed'), 'The compact summary retains the failed build before full logs');
        }
        assertOneStatusScope(layout);
      }
      // Status never moves or disappears when a run completes.
      const key = 'sample-service-id:105';
      await move(10, 10);
      await evaluate(`{const el=document.querySelector('#rows [data-key="${key}"] .pr-status li .k'),r=document.createRange();r.selectNodeContents(el);
        window.getSelection().removeAllRanges();window.getSelection().addRange(r);window.blockNode=el.firstChild;window.blockText=window.getSelection().toString();}`);
      await evaluate(`updateCopilotRun({...S.runs.get('lead-live'),status:'completed',mayBeRunning:false,revision:5})`);
      await frame();
      assert.deepEqual(await evaluate(`[window.blockNode.isConnected,window.getSelection().toString()===window.blockText,S.runRelocations.has('${key}')]`), [true, true, false],
        'A selected status reason stays attached without needing relocation');
      await evaluate('window.getSelection().removeAllRanges()');
      await frame(); await frame();
      assert.deepEqual(await evaluate(`[!!document.querySelector('#rows [data-key="${key}"] .pr-status'),S.runRelocations.size]`), [true, 0],
        'Status remains present after completion');
    });

    await t.test('layout run relocation leaves native text selections in failed logs and Explain prose attached', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await reset();
      const key = 'sample-service-id:105', other = 'sample-service-id:101';
      await evaluate(`{const key='${key}',pr=S.snap.prs.find(p=>p.key===key);
        for(const k of [key,'${other}'])S.explanations.set(k,{key:k,requestId:'sel-'+k,sequence:1,status:'completed',output:'Synthetic selectable prose for '+k});
        S.runs.set('sel-new',{...pr,runId:'sel-new',id:0,status:'completed',mayBeRunning:false,startedAt:'2026-01-02T00:00:00Z',output:'New\\n',outputOffset:4});
        S.runs.set('sel-old',{...pr,runId:'sel-old',id:0,status:'needs-attention',mayBeRunning:false,startedAt:'2026-01-01T00:00:00Z',output:'Old\\n',outputOffset:4});
        S.expanded.add(key);S.expanded.add('${other}');renderRows();}`);
      await evaluate(`new Promise((resolve,reject)=>{const deadline=Date.now()+3000;
        const check=()=>document.querySelector('#rows [data-key="${key}"] pre.log[data-log]')?resolve():
          Date.now()>deadline?reject(Error('Failed log tail did not load')):setTimeout(check,20);check();})`);
      await move(10, 10);
      const lead = () => evaluate(`document.querySelector('#rows [data-key="${key}"] .detail').dataset.runLead`);
      const runs = () => evaluate(`[...document.querySelectorAll('#rows [data-key="${key}"] .copilot-run')].map(c=>c.dataset.runId)`);
      const select = (from, to) => evaluate(`{const a=document.querySelector(${JSON.stringify(from)}),b=document.querySelector(${JSON.stringify(to)}),r=document.createRange();
        const w=e=>document.createTreeWalker(e,NodeFilter.SHOW_TEXT),s=w(a).nextNode(),bw=w(b);let t=null,n;while((n=bw.nextNode()))t=n;r.setStart(s,0);r.setEnd(t,t.length);
        window.getSelection().removeAllRanges();window.getSelection().addRange(r);
        window.selNodes=[r.startContainer,r.endContainer];window.selText=window.getSelection().toString();window.selRow=document.querySelector('#rows [data-key="${key}"]');}`);
      const intact = () => evaluate(`window.selNodes.every(n=>n.isConnected) && window.getSelection().toString()===window.selText &&
        window.selRow===document.querySelector('#rows [data-key="${key}"]')`);
      for (const [name, from, status] of [['failed log', `#rows [data-key="${key}"] pre.log[data-log]`, 'running'],
        ['another PR Explain prose', `#rows [data-key="${other}"] .explain-output`, 'completed']]) {
        await select(from, from);
        await evaluate(`updateCopilotRun({...S.runs.get('sel-old'),status:'${status}',mayBeRunning:${status === 'running'},revision:${status === 'running' ? 1 : 2}})`);
        await frame();
        assert.equal(await intact(), true, `${name} selection stays attached across run relocation`);
        assert.deepEqual([await lead(), await runs()], status === 'running' ? ['1', ['sel-old', 'sel-new']] : ['1', ['sel-new', 'sel-old']],
          `${name} selection outside the run cards does not delay relocation`);
      }
      // A selection spanning into a moving card defers the move until the selection clears.
      await select(`#rows [data-key="${key}"] .explain-output`, `#rows [data-key="${key}"] .copilot-run .run-output`);
      assert.equal(await intact(), true, 'The crossing selection baseline is intact before the lifecycle event');
      await evaluate(`updateCopilotRun({...S.runs.get('sel-new'),status:'needs-attention',mayBeRunning:false,id:7,revision:3})`);
      await frame();
      // The truthful status label and Cancel inside this range update in place; assert range identity, not exact selected text.
      const attached = () => evaluate(`{const s=window.getSelection(),r=s.rangeCount&&s.getRangeAt(0);
        !!r && r.startContainer===window.selNodes[0] && r.endContainer===window.selNodes[1] && window.selNodes.every(n=>n.isConnected) &&
        window.selRow===document.querySelector('#rows [data-key="${key}"]') &&
        s.toString().startsWith('Synthetic selectable prose for ${key}') && s.toString().trimEnd().endsWith('New')}`);
      assert.deepEqual([await attached(), await lead(), await evaluate(`S.runRelocations.has('${key}')`)], [true, '1', false],
        'Same preferred run requires no placement change and keeps crossing selections attached');
      await evaluate('window.getSelection().removeAllRanges()');
      await frame(); await frame();
      assert.deepEqual([await lead(), await runs(), await evaluate('S.runRelocations.size')], ['1', ['sel-new', 'sel-old'], 0],
        'The group moves once the selection clears');
      // A history-only snapshot (identical active PRs) must not rebuild active rows under a held failed-log selection.
      await select(`#rows [data-key="${key}"] pre.log[data-log]`, `#rows [data-key="${key}"] pre.log[data-log]`);
      const chip = () => evaluate(`document.querySelector('[data-lane="completed"] .n').textContent`);
      const before = +(await chip());
      await evaluate(`window.selLog=document.querySelector('#rows [data-key="${key}"] pre.log[data-log]');
        setSnapshot({...S.snap,revision:(S.snap.revision||0)+1,completedWarnings:['Synthetic history warning'],
          completed:[{key:'history-only:1',id:1,title:'History only',url:'https://example.invalid/1',org:'o',project:'p',repo:S.snap.prs[0].repo,
            repoId:'r',author:'a',isMine:true,isHidden:false,closed:new Date().toISOString(),closedBy:null},...(S.snap.completed||[])]});
        updateCopilotRun({...S.runs.get('sel-new'),output:S.runs.get('sel-new').output+'live\\n',outputOffset:S.runs.get('sel-new').outputOffset+5,revision:4});`);
      await frame();
      assert.deepEqual([await intact(), await evaluate('window.selLog.isConnected && !S.pendingSnapshot && !S.rowsPending'), +(await chip())],
        [true, true, before + 1], 'History-only snapshot updates the Completed count without rebuilding the selected active row');
      assert.match(await evaluate(`document.querySelector('#rows [data-key="${key}"] .copilot-run[data-run-id="sel-new"] .run-output').textContent`), /live/,
        'Run output keeps streaming while the unrelated selection is held');
      await evaluate('window.getSelection().removeAllRanges()');
      await click(await elementPoint('[data-lane="completed"]')); await frame();
      assert.ok((await evaluate(`[...document.querySelectorAll('#rows > .pr')].map(r=>r.dataset.key)`)).includes('history-only:1'),
        'The Completed lane shows the history change');
      // An older run validly recovering becomes the main card; a selection from Explain into its output holds that swap.
      await move(10, 10);
      await evaluate(`{const pr=S.snap.prs.find(p=>p.key==='${key}');S.lane=null;
        S.runs.set('sel-rec',{...pr,runId:'sel-rec',id:0,status:'needs-attention',mayBeRunning:false,startedAt:'2025-12-31T00:00:00Z',output:'Recovered\\n',outputOffset:10});
        S.runs.set('sel-new',{...S.runs.get('sel-new'),status:'completed',mayBeRunning:false,revision:5});render();}`);
      await frame();
      assert.deepEqual([await lead(), await runs()], ['1', ['sel-new', 'sel-old', 'sel-rec']]);
      await evaluate(`document.querySelector('#rows [data-key="${key}"] .copilot-run[data-run-id="sel-rec"] .run-history').open=true;`);
      await select(`#rows [data-key="${key}"] .explain-output`, `#rows [data-key="${key}"] .copilot-run[data-run-id="sel-rec"] .run-output`);
      assert.equal(await intact(), true, 'The recovery-crossing selection baseline is intact before promotion');
      await evaluate(`window.recCard=document.querySelector('#rows [data-key="${key}"] .copilot-run[data-run-id="sel-rec"]');
        updateCopilotRun({...S.runs.get('sel-rec'),status:'recovering',mayBeRunning:true,revision:1})`);
      await frame();
      assert.deepEqual([await evaluate(`{const s=window.getSelection(),r=s.rangeCount&&s.getRangeAt(0);
        !!r && r.startContainer===window.selNodes[0] && r.endContainer===window.selNodes[1] && window.selNodes.every(n=>n.isConnected) &&
        window.selRow===document.querySelector('#rows [data-key="${key}"]') && s.toString().startsWith('Synthetic selectable prose for ${key}') && s.toString().trimEnd().endsWith('Recovered')}`),
        await lead(), await runs(), await evaluate(`S.runRelocations.has('${key}')`)], [true, '1', ['sel-new', 'sel-old', 'sel-rec'], true],
        'Promotion of a recovered run waits while a selection endpoint sits in its card');
      // A queued full-row render (idle render, then flush) must not rebuild under the held relocation.
      assert.deepEqual(await evaluate(`renderRows(true); flushRowUpdates();
        [S.rowsPending, S.runRelocations.has('${key}'), window.selNodes.every(n=>n.isConnected), window.recCard.isConnected,
          window.getSelection().getRangeAt(0).endContainer===window.selNodes[1]]`), [true, true, true, true, true],
        'Full row render stays deferred while a selection holds a run relocation');
      await evaluate('window.getSelection().removeAllRanges()');
      await frame(); await frame();
      assert.deepEqual([await lead(), await runs(), await evaluate('[S.runRelocations.size, S.rowsPending]'),
        await evaluate(`[...document.querySelectorAll('#rows [data-key="${key}"] .copilot-run:not(.run-earlier)')].map(c=>c.dataset.runId)`)],
        ['1', ['sel-rec', 'sel-new', 'sel-old'], [0, false], ['sel-rec']], 'The recovered run leads and the deferred render applies once the selection clears');
      // On the shelf, a selection from the execution notice into an older run's output holds that run's recovery promotion,
      // which is queued and replayed once the selection clears.
      await evaluate(`{const pr=S.snap.prs.find(p=>p.key==='${key}');
        for (const id of ['sel-rec','sel-new','sel-old']) S.runs.delete(id);
        S.runs.set('shelf-new',{...pr,runId:'shelf-new',status:'completed',mayBeRunning:false,startedAt:'2026-01-02T00:00:00Z',output:'Shelf newer\\n',outputOffset:12});
        S.runs.set('shelf-old',{...pr,runId:'shelf-old',id:0,status:'needs-attention',mayBeRunning:false,startedAt:'2026-01-01T00:00:00Z',output:'Shelf older\\n',outputOffset:12});
        render();setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:S.snap.prs.filter(p=>p.key!=='${key}')});
        S.expanded.add('${key}');renderRunShelf();}`);
      await frame();
      const shelfMain = () => evaluate(`[...document.querySelectorAll('#runShelf [data-key="${key}"] .copilot-run')].map(c=>[c.dataset.runId,c.classList.contains('run-earlier')])`);
      assert.deepEqual(await shelfMain(), [['shelf-new', false], ['shelf-old', true]]);
      await evaluate(`{const row=document.querySelector('#runShelf [data-key="${key}"]'),a=row.querySelector('.run-scope'),
          b=row.querySelector('.copilot-run[data-run-id="shelf-old"] .run-output'),r=document.createRange();
        b.closest('.run-history').open=true;
        const w=e=>document.createTreeWalker(e,NodeFilter.SHOW_TEXT),s=w(a).nextNode(),bw=w(b);let t=null,n;while((n=bw.nextNode()))t=n;
        r.setStart(s,0);r.setEnd(t,t.length);window.getSelection().removeAllRanges();window.getSelection().addRange(r);
        window.selNodes=[r.startContainer,r.endContainer];window.shelfOld=b.closest('.copilot-run');}`);
      const shelfAttached = () => evaluate(`{const s=window.getSelection(),r=s.rangeCount&&s.getRangeAt(0);
        !!r && r.startContainer===window.selNodes[0] && r.endContainer===window.selNodes[1] && window.selNodes.every(n=>n.isConnected) &&
        window.shelfOld.isConnected && s.toString().trimEnd().endsWith('Shelf older')}`);
      assert.equal(await shelfAttached(), true, 'The shelf crossing selection baseline is intact before promotion');
      assert.deepEqual(await evaluate('S.rowsPending=true; flushRowUpdates(); [window.shelfOld.isConnected, S.rowsPending, S.runRelocations.size]'), [true, true, 0],
        'A deferred rebuild with no queued relocation stays pending instead of detaching the selected shelf cards');
      await evaluate(`updateCopilotRun({...S.runs.get('shelf-old'),status:'recovering',mayBeRunning:true,revision:1})`);
      await frame();
      assert.deepEqual([await shelfAttached(), await shelfMain(), await evaluate(`S.runRelocations.has('${key}')`)],
        [true, [['shelf-new', false], ['shelf-old', true]], true], 'A selection-held shelf promotion is queued, not dropped');
      assert.deepEqual(await evaluate(`renderRows(true); flushRowUpdates(); [S.runRelocations.has('${key}'), window.shelfOld.isConnected]`), [true, true],
        'Deferred renders do not rebuild the selected shelf cards');
      assert.equal(await shelfAttached(), true);
      await evaluate('window.getSelection().removeAllRanges()');
      await frame(); await frame();
      assert.deepEqual([await shelfMain(), await evaluate('[S.runRelocations.size, S.rowsPending]')],
        [[['shelf-old', false], ['shelf-new', true]], [0, false]], 'The recovered shelf run becomes the main card once the selection clears');    });

    await t.test('layout narrow-window identity cue names the panned row without covering gates or actions', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 900, height: 500, deviceScaleFactor: 1, mobile: false });
      await reset();
      await evaluate(`S.snap.prs.find(p=>p.key!=='sample-service-id:101'&&p.key!=='sample-service-id:105').repo='an-exceptionally-long-repository-name-for-the-compact-identity-cue';
        S.expanded.add('sample-service-id:105');renderRows();document.querySelector('main').scrollTop=0;`);
      const cues = () => evaluate(`(()=>{document.querySelector('#toasts').replaceChildren();const main=document.querySelector('main'),m=main.getBoundingClientRect();
        const left=m.left+main.clientLeft,right=left+main.clientWidth,top=m.top+main.clientTop,bottom=top+main.clientHeight;
        const byKey=new Map(S.snap.prs.map(p=>[p.key,p])),headBottom=Math.max(top,document.querySelector('.thead').getBoundingClientRect().bottom);
        return [...document.querySelectorAll('#rows > .pr')].map(row=>{
          const chip=row.querySelector('.id-cue span'),c=chip.getBoundingClientRect(),shown=getComputedStyle(chip).display!=='none';
          const visible=r=>r.width>0&&r.right>left&&r.left<right&&r.bottom>top&&r.top<bottom;
          const targets=[...row.querySelectorAll('.row .title,.row .meta,.row .pill,.row .act')].filter(e=>visible(e.getBoundingClientRect()));
          const overlap=shown&&targets.some(e=>{const r=e.getBoundingClientRect();
            return Math.min(r.right,c.right)-Math.max(r.left,c.left)>0&&Math.min(r.bottom,c.bottom)-Math.max(r.top,c.top)>0;});
          // Disabled/busy actions are intentionally pointer-events:none, so they cannot hit themselves.
          const misses=targets.filter(e=>getComputedStyle(e).pointerEvents!=='none').filter(e=>{const r=e.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
            if(x<left||x>right||y<=headBottom||y>bottom)return false;const hit=document.elementFromPoint(x,y);
            return !hit||(hit!==e&&!e.contains(hit));}).map(e=>{const r=e.getBoundingClientRect(),hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);
            return e.className+'@'+Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2)+'->'+(hit?(hit.id||hit.className||hit.tagName):'none');});
          const p=byKey.get(row.dataset.key);
          const id=chip.querySelector('b').getBoundingClientRect(),repo=chip.querySelector('i');
          const bounded=!shown||(c.width>0&&c.width<=200.5&&c.height>0&&id.width>0&&id.left>=c.left-.5&&id.right<=c.right+.5&&repo.getBoundingClientRect().right<=c.right+.5&&
            (repo.scrollWidth<=repo.clientWidth||getComputedStyle(repo).textOverflow==='ellipsis'));
          return {key:row.dataset.key,shown,bounded,text:chip.textContent,expected:'!'+p.id+' · '+p.repo,
            inside:!shown||(c.left>=left-.5&&c.right<=right+.5),overlap,misses,onScreen:visible(row.getBoundingClientRect()),
            prRight:row.querySelector('.row > .c-pr').getBoundingClientRect().right-left};});})()`);
      const geometry = await evaluate(`(()=>{const main=document.querySelector('main');
        return {client:main.clientWidth,content:main.scrollWidth,max:main.scrollWidth-main.clientWidth};})()`);
      assert.ok(geometry.max > 0, `900px must pan the table (${JSON.stringify(geometry)})`);
      const pan = async x => { await evaluate(`document.querySelector('main').scrollLeft=${x}`); await frame(); return cues(); };
      const initial = await pan(0);
      assert.ok(initial.every(c => !c.shown), 'No cue while the PR cell is visible');
      const threshold = Math.ceil(Math.max(...initial.map(c => c.prRight))) + 1;
      for (const x of [threshold - 2, threshold, geometry.max]) {
        const state = await pan(x);
        for (const c of state.filter(r => r.onScreen)) {
          assert.deepEqual(c.misses, [], `Hit testing changed in ${c.key} at ${x}`);
          if (x < threshold) { assert.equal(c.shown, c.prRight <= 0, `Cue before PR cell left ${c.key}`); continue; }
          assert.deepEqual([c.shown, c.bounded, c.text, c.inside, c.overlap], [true, true, c.expected, true, false], `Cue for ${c.key} at ${x}`);
        }
        if (x === geometry.max && process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS) {
          const shot = await send('Page.captureScreenshot', { format: 'png' });
          await writeFile(path.join(process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS, 'after-panned-900.png'), Buffer.from(shot.data, 'base64'));
        }
      }
      assert.equal(await evaluate(`document.querySelector('[data-key="sample-service-id:101"] .id-cue').getAttribute('aria-hidden')`), 'true');
      const open = await point('sample-service-id:101', '.row [data-act="open"]');
      await click(open); await frame();
      assert.deepEqual((await calls()).at(-1), { method: 'openUrl', url: expectedUrl(open) }, 'Panned actions keep their row target');
      assert.equal(await evaluate(`document.querySelector('[data-key="sample-service-id:101"]').classList.contains('id-panned')`),
        await evaluate(`document.querySelector('main').scrollLeft>0`), 'Cue state follows the scroll after pointer actions');
      // Resizing while panned re-measures; long repositories stay ellipsized inside the bounded chip.
      await send('Emulation.setDeviceMetricsOverride', { width: 860, height: 560, deviceScaleFactor: 1, mobile: false });
      await frame();
      const resized = await pan(await evaluate(`(()=>{const m=document.querySelector('main');return m.scrollWidth-m.clientWidth;})()`));
      assert.ok(resized.some(c => c.onScreen && c.shown), 'Cue still shows after resizing while panned');
      for (const c of resized.filter(r => r.onScreen && r.shown))
        assert.deepEqual([c.bounded, c.text, c.inside, c.overlap, c.misses], [true, c.expected, true, false, []], `Resized cue for ${c.key}`);
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await frame(); await frame();
      const wide = await cues();
      assert.ok(wide.every(c => !c.shown), 'Widening the window clears every cue');
    });

    await t.test('Completed history is read-only; a PR completing during a live Fix keeps owned output and Stop but loses launchers', async () => {
      await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
      await reset(true);
      const key = 'sample-service-id:101';
      await click(await point(key, '.row [data-act="triage"]')); await frame();
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      const chip = () => evaluate(`document.querySelector('[data-lane="completed"] .n').textContent`);
      assert.equal(await chip(), '2');
      assert.doesNotMatch(await evaluate(`document.querySelector('#rows').innerHTML`), /data-completed/, 'All stays active-only');
      await evaluate(`(()=>{const p=S.snap.prs.find(p=>p.key==='${key}');
        setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:S.snap.prs.filter(x=>x!==p),
          completed:[{key:p.key,id:p.id,title:p.title,url:p.url,org:p.org,project:p.project,repo:p.repo,repoId:p.repoId,author:p.author,isMine:p.isMine,isHidden:false,closed:new Date().toISOString(),closedBy:null},...S.snap.completed]});})()`);
      await frame();
      assert.equal(await chip(), '3', 'History-only change updates the chip');
      assert.match(await evaluate(`document.querySelector('#runShelf').textContent`), /!101/);
      await click(await point(key, '.run-heading')); await frame();
      const launchers = await evaluate(`[...document.querySelectorAll('[data-key="${key}"] [data-act]')].map(e=>e.dataset.act).filter(a=>['triage','pop','autocomplete','approve','explain','tiktokPr'].includes(a))`);
      assert.deepEqual(launchers, [], 'Completed PR loses launch controls');
      await click(await point(key, '.copilot-run [data-act="cancelRun"]')); await frame();
      assert.deepEqual((await calls()).filter(c => c.method === 'cancelCopilot'), [{ method: 'cancelCopilot', key, runId: 'offline-101' }]);

      await click(await elementPoint('[data-lane="completed"]')); await frame();
      const rows = await evaluate(`[...document.querySelectorAll('#rows > .pr')].map(r=>[r.dataset.key,r.hasAttribute('data-completed'),[...r.querySelectorAll('[data-act]')].map(e=>e.dataset.act).join()])`);
      assert.deepEqual(rows.map(r => r[0]), [key, 'sample-api-id:96', 'sample-operator-id:94']);
      assert.ok(rows.every(r => r[1] && r[2] === 'open'), JSON.stringify(rows));
      assert.match(await evaluate(`document.querySelector('#rows').textContent`), /sample-service: Checked 100 completions in this repository; some PRs may be missing\./);
      assert.equal(await evaluate(`document.querySelector('#runShelf').hidden || !document.querySelector('#runShelf').textContent.trim()`), true);
      await evaluate(`document.querySelector('[data-key="sample-api-id:96"] [data-act="open"]').focus()`);
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 }); await frame();
      assert.deepEqual((await calls()).at(-1), { method: 'openUrl', url: await evaluate(`S.snap.completed.find(c=>c.id===96).url`) });
      if (process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS) {
        const shot = await send('Page.captureScreenshot', { format: 'png' });
        await writeFile(path.join(process.env.ADOPRVIEW_LAYOUT_SCREENSHOTS, 'completed-lane-1600.png'), Buffer.from(shot.data, 'base64'));
      }
      await evaluate(`document.activeElement?.blur();S.lane=null;render()`);
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

    await t.test('GUI settings drawer contains keyboard focus, blocks background actions and returns to its opener', async () => {
      const key = 'sample-service-id:101';
      for (const provider of [false, true]) {
        const surface = provider ? 'detail' : 'row';
        await reset();
        try {
          await evaluate(`(()=>{
            const snap=structuredClone(S.snap),pr=snap.prs.find(p=>p.key==='${key}');
            pr.approval=${provider ? "{title:'Synthetic optional provider'}" : 'null'};
            snap.approvalProvider=${provider ? "{name:'Synthetic optional provider'}" : 'null'};
            ${provider ? `S.expanded.add('${key}');` : ''}
            setSnapshot(snap);render();window.calls=[];
          })()`);
          assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"] .${surface} [data-act="approve"]:not([disabled])')`), true);
          assert.equal(await evaluate(`!!document.querySelector('[data-key="${key}"] .row [data-act="generateApproval"]')`), provider);
          for (const dismissal of ['close', 'escape', 'scrim']) {
            await evaluate(`document.querySelector('#settingsBtn').focus()`);
            await keyboard('Enter', 13);
            assert.equal(await evaluate(`document.activeElement.id`), 'closeDrawer');
            assert.equal(await evaluate(`document.querySelector('main').inert && document.querySelector('header').inert`), true);
            const count = await evaluate(`[...document.querySelector('#drawer').querySelectorAll('button,input,select,textarea,a[href],[tabindex]')]
              .filter(el=>!el.disabled && el.tabIndex>=0 && el.getClientRects().length).length`);
            for (const modifiers of [0, 8]) {
              for (let i = 0; i < count; i++) {
                await keyboard('Tab', 9, modifiers);
                assert.equal(await evaluate(`!!document.activeElement.closest('#drawer')`), true, `provider=${provider}, traversal=${modifiers}, step=${i}`);
              }
              assert.equal(await evaluate(`document.activeElement.id`), 'closeDrawer', 'Traversal wraps to its starting control');
            }
            for (const action of ['approve', ...(provider ? ['generateApproval'] : [])]) {
              await evaluate(`document.querySelector('[data-key="${key}"] .${action === 'approve' ? surface : 'row'} [data-act="${action}"]').focus()`);
              assert.equal(await evaluate(`document.activeElement.id`), 'closeDrawer', 'Inert review buttons cannot take focus');
            }
            await evaluate(`document.querySelector('#wlInput').focus()`);
            await keyboard('Enter', 13);
            await keyboard('Enter', 13);
            assert.equal(await evaluate(`!!document.activeElement.closest('#drawer')`), true, 'Tab/Enter stays in Settings rather than approving');
            await evaluate(`document.querySelector('#closeDrawer').focus()`);
            if (dismissal === 'close') await keyboard('Enter', 13);
            else if (dismissal === 'escape') await keyboard('Escape', 27);
            else await click({ x: 10, y: 500 });
            assert.deepEqual(await evaluate(`({
              focus:document.activeElement.id,hidden:document.querySelector('#drawer').hidden,
              inert:document.querySelector('main').inert || document.querySelector('header').inert
            })`), { focus: 'settingsBtn', hidden: true, inert: false });
            await keyboard('Tab', 9);
            assert.equal(await evaluate(`!!document.activeElement.closest('main')`), true, 'Background keyboard navigation resumes after dismissal');
          }
          assert.ok((await calls()).every(c => !['approve', 'generateApproval', 'startCopilot', 'autoComplete'].includes(c.method)));
          assert.deepEqual(await evaluate(`window.confirmations`), [], 'Focus containment adds no approval confirmation');
          await click(await point(key, `.${surface} [data-act="approve"]`)); await frame();
          assert.equal((await calls()).filter(c => c.method === 'approve').length, 1, 'Visible ordinary approval still dispatches once');
          if (provider) {
            await click(await point(key, '.row [data-act="generateApproval"]')); await frame();
            assert.equal((await calls()).filter(c => c.method === 'generateApproval').length, 1, 'Visible provider action still dispatches once');
          }
        } finally { await evaluate(`if(S.drawer)closeDrawer()`); }
      }
    });

    await t.test('GUI settings drawer preserves rerender and theme focus without stealing later focus or leaving inert controls', async () => {
      await reset();
      const settleTheme = () => evaluate(`new Promise((resolve,reject)=>{
        const deadline=Date.now()+5000;
        const check=()=>!S.themeBusy?resolve():Date.now()>deadline?reject(Error('Theme save did not settle')):requestAnimationFrame(check);check();
      })`);
      await evaluate(`window.savedTriageInstructions=S.settings.triageInstructions;
        S.settings.triageInstructions='Synthetic saved instructions';
        window.settingsRpc=window.MockHost.rpc;
        window.MockHost.rpc=async(method,p)=>{
          if(method==='approvalSettings')return {message:'Synthetic provider settings',actions:[]};
          if(method==='discoverRepos'){
            window.calls.push({method,...p});
            return [];
          }
          if(method==='setTheme'){
            window.calls.push({method,...p});
            await new Promise(resolve=>window.completeTheme=resolve);
            if(window.failTheme)throw Error('Synthetic theme save denied');
            return {...S.settings,theme:p.theme};
          }
          return window.settingsRpc(method,p);
        };
        document.querySelector('#settingsBtn').focus();`);
      try {
        await keyboard('Enter', 13);
        await evaluate(`document.querySelector('#triageInstructions').focus();
          document.querySelector('#triageInstructions').setSelectionRange(2,5);
          render();`);
        assert.deepEqual(await evaluate(`({
          id:document.activeElement.id,start:document.activeElement.selectionStart,end:document.activeElement.selectionEnd
        })`), { id: 'triageInstructions', start: 2, end: 5 });
        await evaluate(`document.querySelector('#triageInstructions').value='Synthetic saved instructions with an unsaved longer draft';
          document.querySelector('#triageInstructions').setSelectionRange(40,47);renderDrawer()`);
        assert.deepEqual(await evaluate(`({
          id:document.activeElement.id,value:document.activeElement.value,
          start:document.activeElement.selectionStart,end:document.activeElement.selectionEnd
        })`), { id: 'triageInstructions', value: 'Synthetic saved instructions with an unsaved longer draft', start: 40, end: 47 },
        'Draft text must be restored before its focused selection');
        await evaluate(`document.querySelector('[data-native-repo]').focus();renderDrawer()`);
        assert.equal(await evaluate(`document.activeElement.dataset.nativeRepo===document.querySelector('[data-native-repo]').dataset.nativeRepo`), true);
        await evaluate(`document.querySelector('[data-dact="discover"]').focus();renderDrawer()`);
        assert.equal(await evaluate(`document.activeElement.id`), 'closeDrawer', 'Removed button focus falls back inside the drawer');
        for (const outcome of ['saved', 'failed', 'moved', 'discover', 'closed']) {
          await evaluate(`document.querySelector('#theme').focus();
            window.previousTheme=S.settings.theme;window.failTheme=${outcome === 'failed'};
            window.selectedTheme=S.settings.theme==='dark'?'light':'dark';`);
          const dark = await evaluate(`window.previousTheme==='dark'`);
          await keyboard(dark ? 'ArrowUp' : 'ArrowDown', dark ? 38 : 40);
          assert.equal(await evaluate(`S.themeBusy && document.querySelector('#theme').disabled`), true);
          assert.equal(await evaluate(`document.documentElement.dataset.theme===window.selectedTheme`), true, 'Theme applies immediately');
          await evaluate(`renderDrawer()`);
          if (outcome === 'moved') await evaluate(`document.querySelector('#repoUrl').focus()`);
          if (outcome === 'discover') {
            await evaluate(`document.querySelector('[data-dact="discover"]').focus()`);
            await keyboard('Enter', 13);
            assert.equal(await evaluate(`document.activeElement.id`), 'closeDrawer', 'Removed Discover button falls back inside Settings during a pending theme save');
          }
          if (outcome === 'closed') await keyboard('Escape', 27);
          await evaluate(`window.completeTheme()`);
          await settleTheme();
          assert.equal(await evaluate(`document.activeElement.id`), outcome === 'moved' ? 'repoUrl' : outcome === 'closed' ? 'settingsBtn' : outcome === 'discover' ? 'closeDrawer' : 'theme');
          assert.equal(await evaluate(`S.settings.theme===(window.failTheme?window.previousTheme:window.selectedTheme)
            && document.documentElement.dataset.theme===S.settings.theme && !document.querySelector('#theme').disabled`), true);
          if (outcome === 'failed') assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Theme was not saved.*Synthetic theme save denied/);
        }
        assert.equal(await evaluate(`[...S.drawerInert].length===0 && !document.querySelector('main').inert`), true);
        await keyboard('Tab', 9);
        assert.equal(await evaluate(`!!document.activeElement.closest('main')`), true);
        assert.equal((await calls()).filter(c => c.method === 'setTheme').length, 5);
        assert.equal((await calls()).some(c => c.method === 'saveSettings'), false, 'Theme saves remain separate from all other Settings');
      } finally {
        await evaluate(`if(S.themeBusy && typeof window.completeTheme==='function')window.completeTheme()`);
        await settleTheme();
        await evaluate(`if(S.drawer)closeDrawer();window.MockHost.rpc=window.settingsRpc;delete window.settingsRpc;
          S.settings.triageInstructions=window.savedTriageInstructions;delete window.savedTriageInstructions;`);
      }
    });

    await t.test('GUI settings drawer falls back from hidden or unfocused openers and restores preexisting inertness', async () => {
      await reset();
      const savedSettings = await evaluate(`structuredClone(S.settings)`);
      try {
        await evaluate(`S.settings.repos=[];render()`);
        await click(await elementPoint('#empty [data-act="settings"]')); await frame();
        assert.equal(await evaluate(`document.activeElement.id`), 'closeDrawer');
        await evaluate(`S.settings=${JSON.stringify(savedSettings)};render()`);
        assert.equal(await evaluate(`document.querySelector('#empty').hidden`), true);
        await keyboard('Escape', 27);
        assert.equal(await evaluate(`document.activeElement.id`), 'settingsBtn', 'A still-connected hidden opener is not a safe focus target');
        await evaluate(`document.activeElement.blur();document.querySelector('#statusline').inert=true;openDrawer()`);
        await keyboard('Escape', 27);
        assert.equal(await evaluate(`document.activeElement.id`), 'settingsBtn', 'Unfocused opening returns to the visible Settings button');
        assert.equal(await evaluate(`document.querySelector('#statusline').inert`), true, 'Existing inertness is restored, not cleared');
        assert.equal(await evaluate(`document.querySelector('main').inert`), false);
        await evaluate(`(async()=>{
          toast('Synthetic preexisting inert action','info',{label:'Already inert',run:()=>{}});
          window.preInertToast=document.querySelector('#toasts .toast:last-child button');window.preInertToast.inert=true;
          await hidePr(findPr('sample-service-id:101'));
          window.existingUndo=document.querySelector('#toasts .toast:last-child button');window.existingUndo.id='existingUndoFixture';
        })()`);
        await keyboard('Enter', 13);
        await evaluate(`toast('Synthetic late Undo','info',{label:'Undo',run:()=>undoHide()});
          window.lateUndo=document.querySelector('#toasts .toast:last-child button');window.lateUndo.id='lateUndoFixture';`);
        assert.equal(await evaluate(`window.existingUndo.inert && window.lateUndo.inert && !document.querySelector('#toasts').inert`), true, 'Existing and new actions are inert while passive live announcements remain available');
        await click(await elementPoint('#existingUndoFixture'));
        await click(await elementPoint('#lateUndoFixture'));
        assert.equal((await calls()).some(c => c.method === 'unhide'), false, 'Undo toast mouse actions cannot bypass Settings modal isolation');
        await keyboard('Tab', 9);
        assert.equal(await evaluate(`!!document.activeElement.closest('#drawer')`), true);
        await keyboard('Escape', 27);
        assert.equal(await evaluate(`!window.existingUndo.inert && !window.lateUndo.inert && window.preInertToast.inert`), true, 'Dismissal restores each toast action state');
        await click(await elementPoint('#lateUndoFixture')); await frame();
        assert.equal((await calls()).filter(c => c.method === 'unhide').length, 1, 'Undo is usable again after dismissal');
      } finally {
        await evaluate(`if(S.drawer)closeDrawer();document.querySelector('#statusline').inert=false;
          document.querySelector('#toasts').replaceChildren();S.settings=${JSON.stringify(savedSettings)};render()`);
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

    await t.test('update notice is a sticky Upgrade toast and Settings can upgrade or check for updates', async () => {
      await reset();
      try {
        const notice = await evaluate(`(()=>{
          document.querySelector('#toasts').replaceChildren();
          const set=window.setTimeout;let timers=0;window.setTimeout=(...a)=>{timers++;return set(...a);};
          try {
            for(let i=0;i<2;i++)onHostMessage({event:'update',data:{current:'0.1.3',latest:'0.2.0',available:true,canInstall:true,
              releaseUrl:'https://github.com/alanmeadows/adoprview/releases/tag/v0.2.0',checkedAt:new Date().toISOString(),error:null}});
          } finally { window.setTimeout=set; }
          const toasts=[...document.querySelectorAll('#toasts .toast')];
          return {timers,count:toasts.length,text:toasts[0].textContent,buttons:[...toasts[0].querySelectorAll('button')].map(b=>b.textContent)};
        })()`);
        assert.equal(notice.count, 1, 'The same version is announced once');
        assert.equal(notice.timers, 0, 'Update notice must not auto-dismiss');
        assert.match(notice.text, /ADO PR View v0\.2\.0 is available \(you have v0\.1\.3\)/);
        assert.deepEqual(notice.buttons, ['Upgrade', 'Dismiss']);
        await evaluate(`openDrawer()`); await frame();
        assert.equal(await evaluate(`document.querySelector('[data-dact="installUpdate"]').textContent`), 'Upgrade to v0.2.0');
        await evaluate(`window.confirmResult=false;document.querySelector('[data-dact="installUpdate"]').click()`); await frame();
        assert.match((await evaluate('window.confirmations')).at(-1), /close, install v0\.2\.0 from GitHub, and reopen/);
        await evaluate(`document.querySelector('[data-dact="checkUpdate"]').click()`); await frame();
        assert.deepEqual((await calls()).map(c => c.method).filter(m => /Update/.test(m)), ['checkForUpdate'], 'Declined upgrade must not install');
        await evaluate(`new Promise(r=>{const t=setInterval(()=>{if(!S.updateBusy){clearInterval(t);r();}},20);})`);
        assert.equal(await evaluate(`document.querySelectorAll('#toasts .toast').length`), 0, 'A check without an available update must retire the notice');
        await evaluate(`onHostMessage({event:'update',data:{current:'0.1.3',latest:'0.2.0',available:true,canInstall:true,releaseUrl:null,checkedAt:null,error:null}})`);
        assert.equal(await evaluate(`document.querySelectorAll('#toasts .toast').length`), 1, 'The same version is re-announced after the notice was retired');
      } finally {
        await evaluate(`if(S.drawer)closeDrawer();S.update=null;S.updateAnnounced=null;S.updateToast=null;S.updateBusy=false;document.querySelector('#toasts').replaceChildren();`);
      }
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
      for (const view of [`S.lane='blocked'`, `S.lane='waiting'`, `S.lane='completed'`, `S.lane='needsHuman'`, `S.lane=null;S.q='no matching PR'`,
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
          assert.ok(layout.outputHeight > 0 && layout.outputHeight <= 280, 'Short streaming output remains compact within the 280px scrolling bound');
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
          // id:0 keeps the older run non-cancelable history until it actually recovers.
          S.runs.set('older-recovered-run',{...pr,runId:'older-recovered-run',id:0,status:'needs-attention',mayBeRunning:false,
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
          window.olderOutput.closest('.run-history').open=true;window.middleOutput.closest('.run-history').open=true;
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
        assert.equal(await evaluate(`window.promotionRow.querySelectorAll('.copilot-run .run-scope').length===0 &&
          [...window.promotionRow.querySelectorAll('.run-scope')].filter(p=>p.getClientRects().length).length===1`), true,
          'Promotion keeps one visible execution notice outside the run cards without a full render');
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

    await t.test('Fix output preserves selected reading and bottom resume when a shelf run returns to the table', async () => {
      for (const bottom of [true, false]) {
        await reset(true);
        await evaluate(`{const key='sample-service-id:101';window.returningPr=S.snap.prs.find(p=>p.key===key);
          const output=Array.from({length:160},(_,n)=>'Returning synthetic line '+n+'\\n').join('');
          S.runs.set('returning-run',{...window.returningPr,runId:'returning-run',status:'needs-attention',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output,outputOffset:output.length});
          render();setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:[]});}`);
        await frame();
        await click(await point('sample-service-id:101', '.run-heading'));
        await move(10,10); await evaluate('document.activeElement?.blur()'); await frame();
        await evaluate(`{window.returningOutput=document.querySelector('#runShelf [data-run-id="returning-run"] .run-output');
          window.returningNode=window.returningOutput.firstChild;
          window.returningOutput.scrollTop=${bottom ? 'window.returningOutput.scrollHeight' : '400'};
          const start=${bottom ? 'window.returningNode.length-26' : 'window.returningNode.data.indexOf("Returning synthetic line 30\\n")'};
          window.getSelection().setBaseAndExtent(window.returningNode,start,window.returningNode,start+25);
          window.returningSelected=window.getSelection().toString();window.returningTop=window.returningOutput.scrollTop;}`);
        await frame();
        assert.equal(await evaluate('rowsInUse()'), false, 'Refresh returns the shelf run without interaction deferral');
        await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:[window.returningPr]})`);
        await frame();
        assert.equal(await evaluate('document.querySelector("#rows [data-run-id=\\"returning-run\\"] .run-output")===window.returningOutput'), true);
        assert.equal(await evaluate('document.querySelector("#runShelf [data-run-id=\\"returning-run\\"]")'), null);
        assert.equal(await evaluate('window.returningOutput.firstChild===window.returningNode'), true);
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.returningSelected'));
        assert.equal(await evaluate('window.returningOutput.scrollTop'), await evaluate('window.returningTop'));
        await evaluate(`{window.getSelection().removeAllRanges();const run=S.runs.get('returning-run'),extra='After shelf return\\n'.repeat(160);
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        if (bottom) assert.ok(await evaluate('window.returningOutput.scrollHeight-window.returningOutput.clientHeight-window.returningOutput.scrollTop') <= 2,
          'Clearing a retained bottom selection resumes after returning from the shelf');
        else assert.equal(await evaluate('window.returningOutput.scrollTop'), await evaluate('window.returningTop'),
          'A paused reader stays paused after returning from the shelf');
      }
    });

    await t.test('Fix output preserves bottom selection resume across idle new-run retargeting', async () => {
      for (const shelf of [false, true]) {
        await reset(true);
        await evaluate(`{const key='sample-service-id:101',pr=S.snap.prs.find(p=>p.key===key);
          const output='Idle selected synthetic output\\n'.repeat(160);
          S.runs.set('idle-selected-run',{...pr,runId:'idle-selected-run',status:'needs-attention',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output,outputOffset:output.length});
          S.expanded.add(key);render();}`);
        if (shelf) {
          await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:[]})`);
          await frame();
          await click(await point('sample-service-id:101', '.run-heading'));
        }
        await move(10,10); await evaluate('document.activeElement?.blur()'); await frame();
        await evaluate(`{window.idleOutput=document.querySelector('[data-run-id="idle-selected-run"] .run-output');
          window.idleNode=window.idleOutput.firstChild;window.idleOutput.scrollTop=window.idleOutput.scrollHeight;
          window.getSelection().setBaseAndExtent(window.idleNode,window.idleNode.length-25,window.idleNode,window.idleNode.length-1);
          window.idleSelected=window.getSelection().toString();}`);
        await frame();
        assert.equal(await evaluate('rowsInUse()'), false, 'Exercise retargeting without pointer/focus deferral');
        await evaluate(`{const old=S.runs.get('idle-selected-run'),output='A different idle run output\\n'.repeat(160);
          updateCopilotRun({...old,runId:'idle-replacement-run',status:'recovering',
            startedAt:'2026-01-02T00:00:00Z',output,outputOffset:output.length});}`);
        await frame();
        assert.equal(await evaluate('document.querySelector(".run-earlier .run-output")===window.idleOutput'), true);
        assert.equal(await evaluate('window.idleOutput.firstChild===window.idleNode'), true);
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.idleSelected'));
        assert.ok(await evaluate('window.idleOutput.scrollHeight-window.idleOutput.clientHeight-window.idleOutput.scrollTop') <= 2);
        assert.ok(await evaluate(`(()=>{const output=document.querySelector('[data-run-id="idle-replacement-run"] .run-output');
          return output!==window.idleOutput && output.scrollHeight-output.clientHeight-output.scrollTop<=2;})()`),
        'The different run gets its own output node and default following intent');
        await evaluate(`{window.getSelection().removeAllRanges();
          const old=S.runs.get('idle-selected-run'),extra='After idle bottom selection cleared\\n'.repeat(160);
          updateCopilotRun({...old,output:old.output+extra,outputOffset:old.outputOffset+extra.length});}`);
        await frame();
        assert.ok(await evaluate('window.idleOutput.scrollHeight-window.idleOutput.clientHeight-window.idleOutput.scrollTop') <= 2,
          'Clearing the surviving bottom selection resumes the earlier run on its next append');
      }
    });

    await t.test('Fix output shelf defers background card changes while hovering or pressing captured cancellation', async () => {
      for (const pressed of [false, true]) {
        await reset(true);
        await evaluate(`{const key='sample-service-id:101',pr=S.snap.prs.find(p=>p.key===key),other=S.snap.prs.find(p=>p.id===104);
          const output='Shelf reading synthetic output\\n'.repeat(160);
          S.runs.set('shelf-older-run',{...pr,runId:'shelf-older-run',status:'needs-attention',mayBeRunning:false,
            startedAt:'2026-01-01T00:00:00Z',output,outputOffset:output.length});
          S.runs.set('shelf-newer-run',{...pr,runId:'shelf-newer-run',status:'needs-attention',mayBeRunning:false,
            startedAt:'2026-01-02T00:00:00Z',output,outputOffset:output.length});
          S.runs.set('shelf-collapsed-run',{...other,runId:'shelf-collapsed-run',status:'running',output:'Collapsed output\\n',
            outputOffset:'Collapsed output\\n'.length});
          render();setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,prs:[]});}`);
        await frame();
        await click(await point('sample-service-id:101', '.run-heading')); await frame();
        assert.equal(await evaluate(`(()=>{const row=document.querySelector('#runShelf [data-key="sample-service-id:101"]');
          const scopes=row.querySelectorAll('.run-scope');return scopes.length===1&&!scopes[0].closest('.copilot-run')&&
            !!(scopes[0].compareDocumentPosition(row.querySelector('.copilot-run'))&Node.DOCUMENT_POSITION_FOLLOWING);})()`), true,
          'An open shelf run shows the execution notice once before its run cards');
        const captured = await point('sample-service-id:101', '.copilot-run:not(.run-earlier) [data-act="cancelRun"]');
        await move(captured.x, captured.y);
        if (pressed) await press(captured);
        await evaluate(`window.shelfRow=document.querySelector('#runShelf [data-key="sample-service-id:101"]');
          window.shelfCancel=window.shelfRow.querySelector('.copilot-run:not(.run-earlier) [data-act="cancelRun"]');
          window.shelfOutput=window.shelfRow.querySelector('.copilot-run:not(.run-earlier) .run-output');
          window.shelfOutput.scrollTop=400;
          {const node=window.shelfOutput.firstChild;window.getSelection().setBaseAndExtent(node,700,node,725);}
          window.shelfSelected=window.getSelection().toString();`);
        await frame();
        await evaluate(`{const run=S.runs.get('shelf-collapsed-run'),extra='Collapsed background append\\n';
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('window.shelfCancel.isConnected'), true, 'A collapsed run update must not rebuild another captured shelf card');
        if (pressed) {
          await evaluate(`{const run=S.runs.get('shelf-newer-run');
            updateCopilotRun({...run,runId:'shelf-unseen-run',status:'recovering',startedAt:'2026-01-03T00:00:00Z'});}`);
          await frame();
        }
        await evaluate(`{const run=S.runs.get('shelf-older-run'),extra='Recovery while interacting\\n';
          updateCopilotRun({...run,status:'recovering',output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('window.shelfRow.querySelector(".copilot-run:not(.run-earlier) [data-act=\\"cancelRun\\"]")===window.shelfCancel'), true);
        assert.equal(await evaluate('window.shelfCancel.dataset.runId'), 'shelf-newer-run');
        assert.equal(await evaluate('window.shelfOutput.scrollTop'), 400);
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.shelfSelected'));
        const hit = await evaluate(`(()=>{const r=window.shelfCancel.getBoundingClientRect();
          return {x:r.x+r.width/2,y:r.y+r.height/2,hit:document.elementFromPoint(${captured.x},${captured.y})?.closest('[data-act]')===window.shelfCancel};})()`);
        assert.ok(Math.abs(hit.x-captured.x)<=1 && Math.abs(hit.y-captured.y)<=1 && hit.hit,
          'The same captured cancellation target remains under the pointer');
        if (!pressed) await press(captured);
        await release(captured); await frame();
        assert.deepEqual((await calls()).filter(c=>c.method==='cancelCopilot'),
          [{method:'cancelCopilot',key:'sample-service-id:101',runId:'shelf-newer-run'}]);
        await move(10,10); await evaluate('document.activeElement?.blur()'); await frame();
        assert.equal(await evaluate('S.rowsPending'), false, 'Deferred shelf changes flush after interaction ends');
        assert.equal(await evaluate('document.querySelector("#runShelf [data-run-id=\\"shelf-newer-run\\"] .run-output")===window.shelfOutput'), true);
        assert.equal(await evaluate('window.shelfOutput.scrollTop'), 400);
        const latest = pressed ? 'shelf-unseen-run' : 'shelf-older-run';
        assert.equal(await evaluate('document.querySelector("#runShelf .copilot-run:not(.run-earlier)").dataset.runId'), latest);
      }
    });

    const crossLogSelectionFixture = async mode => {
      await reset(true);
      await evaluate(`{const key='sample-service-id:101',pr=S.snap.prs.find(p=>p.key===key),other=S.snap.prs.find(p=>p.id===104);
        const output=Array.from({length:160},(_,n)=>'Cross-log synthetic line '+n+'\\n').join('');
        S.runs.set('cross-selected-run',{...pr,runId:'cross-selected-run',status:'running',mayBeRunning:true,
          startedAt:'2026-01-01T00:00:00Z',output,outputOffset:output.length,
          worktreePath:'C:\\\\Synthetic\\\\selected',logPath:'C:\\\\Synthetic\\\\original.log',message:'Original run metadata'});
        const independent='Independent run output\\n'.repeat(160);
        S.runs.set('cross-independent-run',{...other,runId:'cross-independent-run',status:'running',mayBeRunning:true,
          startedAt:'2026-01-01T00:00:00Z',output:independent,outputOffset:independent.length,message:'Original independent metadata'});
        S.expanded.add(key);S.expanded.add(other.key);render();
        window.crossRow=document.querySelector('#rows [data-key="'+key+'"]');
        window.crossCard=window.crossRow.querySelector('.copilot-run');
        window.crossOutput=window.crossCard.querySelector('.run-output');
        window.crossText=window.crossOutput.firstChild;
        window.crossWorktree=window.crossCard.querySelector('.run-worktree');
        window.crossMetadataNode=window.crossWorktree.firstChild;
        window.crossCancel=window.crossCard.querySelector('[data-act="cancelRun"]');
        window.crossOutput.scrollTop=400;}`);
      await move(10,10); await evaluate('document.activeElement?.blur()'); await frame();
      const endpoints = mode === 'contained' ? 'window.crossText,600,window.crossText,626'
        : mode === 'forward' ? 'window.crossMetadataNode,0,window.crossText,1000'
          : 'window.crossText,1000,window.crossMetadataNode,0';
      await evaluate(`window.getSelection().setBaseAndExtent(${endpoints});
        window.crossSelected=window.getSelection().toString();window.crossTop=window.crossOutput.scrollTop;`);
      await frame();
      assert.equal(await evaluate('rowsInUse()'), false, 'Selection is not protected by pointer or keyboard-focus interaction');
    };

    await t.test('Fix output preserves cross-log selection during metadata updates and flushes only deferred mutations', async () => {
      for (const mode of ['contained', 'forward', 'backward']) {
        await crossLogSelectionFixture(mode);
        await evaluate(`{const run=S.runs.get('cross-selected-run'),extra='Selection-safe append\\n';
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length,
            logPath:'C:\\\\Synthetic\\\\changed.log',message:'Current progress outside the selected range'});}`);
        await frame();
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.crossSelected'));
        assert.equal(await evaluate('window.crossOutput.firstChild===window.crossText'), true);
        assert.equal(await evaluate('window.crossOutput.scrollTop'), await evaluate('window.crossTop'));
        assert.match(await evaluate('window.crossOutput.textContent'), /Selection-safe append/);
        assert.match(await evaluate('window.crossCard.querySelector(".run-status").textContent'), /Current progress outside/,
          'Metadata outside the selected range still updates in place');
        if (mode === 'contained') {
          assert.match(await evaluate('window.crossWorktree.textContent'), /changed\.log/);
          assert.equal(await evaluate('S.rowsPending'), false, 'Contained log selection does not defer unrelated metadata');
        } else {
          assert.equal(await evaluate('window.crossWorktree.firstChild===window.crossMetadataNode'), true);
          assert.match(await evaluate('window.crossWorktree.textContent'), /original\.log/);
          assert.equal(await evaluate('S.rowsPending'), true);
        }
        await evaluate(`{const run=S.runs.get('cross-independent-run'),extra='Independent live append\\n';
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length,message:'Independent live metadata'});}`);
        await frame();
        assert.match(await evaluate('document.querySelector("[data-run-id=\\"cross-independent-run\\"] .run-output").textContent'), /Independent live append/);
        assert.match(await evaluate('document.querySelector("[data-run-id=\\"cross-independent-run\\"] .run-status").textContent'), /Independent live metadata/);
        assert.equal(await evaluate('window.crossCancel.isConnected && !window.crossCancel.disabled'), true);
        assert.equal(await evaluate('window.crossCancel.dataset.runId'), 'cross-selected-run');
        await evaluate('window.getSelection().removeAllRanges()'); await frame();
        assert.equal(await evaluate('S.rowsPending'), false, 'Native selectionchange flushes the latest queued state');
        assert.match(await evaluate('document.querySelector("[data-run-id=\\"cross-selected-run\\"] .run-worktree").textContent'), /changed\.log/);
        assert.equal(await evaluate('document.querySelector("[data-run-id=\\"cross-selected-run\\"] .run-output")===window.crossOutput'), true);
        assert.equal(await evaluate('window.crossOutput.scrollTop'), await evaluate('window.crossTop'));
      }
    });

    await t.test('Fix output preserves cross-log selection through queued background row rerenders', async () => {
      for (const mode of ['forward', 'backward']) {
        await crossLogSelectionFixture(mode);
        await evaluate(`setSnapshot({...S.snap,orderRevision:(S.snap.orderRevision||0)+1,
          prs:S.snap.prs.map(p=>p.id===101?{...p,title:'Latest queued selected-row title'}:p)})`);
        await frame();
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.crossSelected'));
        assert.equal(await evaluate('document.querySelector("#rows [data-key=\\"sample-service-id:101\\"]")===window.crossRow'), true);
        assert.equal(await evaluate('window.crossWorktree.firstChild===window.crossMetadataNode'), true);
        assert.equal(await evaluate('window.crossOutput.scrollTop'), await evaluate('window.crossTop'));
        assert.equal(await evaluate('S.rowsPending'), true);
        await evaluate('window.getSelection().removeAllRanges()'); await frame();
        assert.equal(await evaluate('S.rowsPending'), false);
        assert.match(await evaluate('document.querySelector("#rows [data-key=\\"sample-service-id:101\\"] .title").textContent'), /Latest queued selected-row title/);
        assert.equal(await evaluate('document.querySelector("[data-run-id=\\"cross-selected-run\\"] .run-output")===window.crossOutput'), true);
        assert.equal(await evaluate('window.crossOutput.scrollTop'), await evaluate('window.crossTop'));
      }
    });

    await t.test('Fix output applies deferred retention before resuming a cleared cross-log selection', async () => {
      const results = [];
      for (const mode of ['forward', 'backward']) for (const lost of [false, true]) {
        await crossLogSelectionFixture(mode);
        await evaluate('window.crossOutput.scrollTop=window.crossOutput.scrollHeight-window.crossOutput.clientHeight');
        await elementPoint('[data-run-id="cross-selected-run"] .run-output');
        await frame();
        await evaluate(`{const box=window.crossOutput.getBoundingClientRect();
          const range=document.caretRangeFromPoint(box.left+12,box.top+9);
          if(range?.startContainer!==window.crossText)throw Error('Expected a visible synthetic log anchor');
          window.deferredAnchorText=window.crossText.data.slice(range.startOffset).split('\\n')[0];
          const run=S.runs.get('cross-selected-run'),extra='Deferred retained append\\n'.repeat(100);
          const retained=${lost}?'':run.output.slice(run.output.indexOf('Cross-log synthetic line 20\\n'));
          updateCopilotRun({...run,output:'[Earlier output truncated]\\n'+retained+extra,
            outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('window.getSelection().toString()'), await evaluate('window.crossSelected'));
        assert.equal(await evaluate('S.rowsPending'), true);
        await evaluate('window.getSelection().removeAllRanges()'); await frame();
        const result = await evaluate(`(()=>{const el=window.crossOutput,box=el.getBoundingClientRect();
          const range=document.caretRangeFromPoint(box.left+12,box.top+9);
          return {top:el.scrollTop,bottom:el.scrollHeight-el.clientHeight-el.scrollTop,
            anchor:range?.startContainer===el.firstChild?range.startContainer.data.slice(range.startOffset).split('\\n')[0]:null,
            before:window.deferredAnchorText,notice:el.textContent.startsWith('[Earlier output truncated]\\n')};})()`);
        await evaluate(`{const run=S.runs.get('cross-selected-run'),extra='After deferred retention\\n'.repeat(40);
          updateCopilotRun({...run,output:run.output+extra,outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        results.push({ ...result, mode, lost, nextTop: await evaluate('window.crossOutput.scrollTop') });
      }
      for (const result of results) {
        assert.equal(result.notice, true);
        if (result.lost) assert.equal(result.top, 0, `${result.mode}: a lost deferred anchor shows the retained beginning`);
        else {
          assert.equal(result.anchor, result.before, `${result.mode}: clearing selection preserves the still-retained visible anchor`);
          assert.ok(result.bottom > 100, 'Applying deferred retention must not resume against the old displayed bottom');
        }
        assert.equal(result.nextTop, result.top, 'A later append remains paused after deferred anchor reconciliation');
      }
    });

    await t.test('Fix output remaps a narrowed log selection before restoring deferred retention', async () => {
      const results = [];
      for (const mode of ['forward', 'backward']) for (const shortened of [false, true]) {
        await crossLogSelectionFixture(mode);
        await evaluate(`window.getSelection().setBaseAndExtent(${mode === 'forward'
          ? 'window.crossMetadataNode,0,window.crossText,window.crossText.length'
          : 'window.crossText,window.crossText.length,window.crossMetadataNode,0'});
          window.narrowStart=window.crossText.data.indexOf('Cross-log synthetic line 140\\n');
          window.narrowEnd=window.narrowStart+24;
          window.narrowExpected=window.crossText.data.slice(window.narrowStart,window.narrowEnd);
          {const run=S.runs.get('cross-selected-run'),cut=run.output.indexOf('Cross-log synthetic line ${shortened ? 80 : 20}\\n');
          const extra='Narrow selection retained append\\n'.repeat(${shortened ? 1 : 100});
          updateCopilotRun({...run,output:'[Earlier output truncated]\\n'+run.output.slice(cut)+extra,
            outputOffset:run.outputOffset+extra.length});}`);
        await frame();
        assert.equal(await evaluate('S.rowsPending'), true);
        await evaluate(`window.deferredSelectionError=null;
          window.deferredErrorHandler=e=>window.deferredSelectionError=e.message;
          window.addEventListener('error',window.deferredErrorHandler);
          window.getSelection().setBaseAndExtent(window.crossText,${mode === 'backward' ? 'window.narrowEnd' : 'window.narrowStart'},
            window.crossText,${mode === 'backward' ? 'window.narrowStart' : 'window.narrowEnd'});`);
        await frame();
        results.push({ mode, shortened, ...await evaluate(`(()=>{window.removeEventListener('error',window.deferredErrorHandler);
          return {selected:window.getSelection().toString(),expected:window.narrowExpected,error:window.deferredSelectionError,
            sameNode:window.crossOutput.firstChild===window.crossText,pending:S.rowsPending};})()`) });
      }
      for (const result of results) {
        assert.equal(result.error, null, `${result.mode}/${result.shortened}: shortened text must not restore an invalid old offset`);
        assert.equal(result.selected, result.expected, `${result.mode}/${result.shortened}: every narrowed selected character survived retention`);
        assert.equal(result.sameNode, true);
        assert.equal(result.pending, false, 'Native selectionchange applies the queued retention after narrowing');
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
      assert.deepEqual(await evaluate(`[...document.querySelectorAll('#rows [data-key="${key}"] .row [data-act]')].map(e=>e.dataset.act)`), ['toggle', 'open']);
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
          `<span class="fake-inspection" role="button" tabindex="0" aria-expanded="false" data-act="open">Open trap</span>`,
          `<div class="row"><div class="gate"><span class="pill fake-inspection" role="button" tabindex="0" aria-expanded="false" data-act="gate" data-gate="pop">PoP trap</span></div></div>`,
          `<div class="row"><div class="c-pr fake-inspection" role="button" tabindex="0" aria-expanded="false" data-act="toggle">Toggle trap</div></div>`,
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
      const traps = await evaluate(`document.querySelectorAll('[data-key="${key}"] .md .fake-inspection').length`);
      assert.equal(traps, 6, 'Description and thread fixtures retain the hostile keyboard attributes');
      for (let n = 0; n < traps; n++) {
        await evaluate(`document.querySelectorAll('[data-key="${key}"] .md .fake-inspection')[${n}].focus()`);
        for (const [keyName, code] of [['Enter', 13], [' ', 32]]) {
          await keyboard(keyName, code);
          assert.equal(await evaluate(`S.expanded.has('${key}')`), true, 'Markdown must not dispatch inspection or row actions');
          assert.deepEqual(await calls(), [], 'Markdown keyboard attributes must not reach the host');
          assert.deepEqual(await evaluate('window.bridge'), []);
        }
      }
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

    const withSettingsDraftFixture = async run => {
      await reset();
      await evaluate(`window.draftRpc=MockHost.rpc;window.originalDraftSettings=structuredClone(S.settings);
        S.settings.futureProvider={preserve:'synthetic unknown field'};
        S.settings.autoComplete.futureOption='synthetic nested option';
        S.settings.wslDistro='Legacy-Ubuntu';
        S.settings.wslRepoDirectories={'legacy/project/repo':'/legacy/checkout'};
        S.settings.nativeRepoDirectories={};
        window.committedDraftSettings=structuredClone(S.settings);
        window.draftWrites=[];window.denyDraftMethod='';window.holdDraftMethod='';
        MockHost.rpc=async(method,p)=>{
          if(['saveSettings','setTheme','addRepo','removeRepo'].includes(method)){
            window.draftWrites.push({method,params:structuredClone(p)});
            if(window.holdDraftMethod===method)await new Promise(resolve=>window.completeDraftWrite=resolve);
            if(window.denyDraftMethod===method)throw Error('Synthetic '+method+' write failed');
            const next=structuredClone(window.committedDraftSettings);
            if(method==='saveSettings'){
              Object.assign(next,p.settings,{theme:next.theme});
            }
            if(method==='setTheme')next.theme=p.theme;
            if(method==='addRepo'){
              if(p.url!=='https://dev.azure.com/contoso/Sample/_git/synthetic-draft-repo')
                throw Error('Synthetic invalid repository URL');
              next.repos.push({org:'contoso',project:'Sample',repo:'synthetic-draft-repo'});
            }
            if(method==='removeRepo')next.repos=next.repos.filter(r=>repoKey(r)!==p.key);
            window.committedDraftSettings=next;
            return structuredClone(next);
          }
          return window.draftRpc(method,p);
        };`);
      try {
        await click(await elementPoint('#settingsBtn')); await frame();
        await evaluate(`window.readDraftFields=()=>({
          ...Object.fromEntries(['triageInstructions','refreshSeconds','mergeCheckMinutes','mergeStrategy',
            ...(document.querySelector('#additionalTriageInstructions')?['additionalTriageInstructions']:[])].map(id=>[id,document.getElementById(id).value])),
          ...Object.fromEntries(['notifications','sound','ignoreDrafts','deleteSourceBranch','transitionWorkItems'].map(id=>[id,document.getElementById(id).checked])),
          nativeRepoDirectories:Object.fromEntries([...document.querySelectorAll('[data-native-repo]')].map(e=>[e.dataset.nativeRepo,e.value]))
        });
        document.querySelector('#triageInstructions').value='Unsaved Custom Ask\\n';
        if(document.querySelector('#additionalTriageInstructions'))document.querySelector('#additionalTriageInstructions').value='Unsaved additive instructions\\n';
        document.querySelector('#refreshSeconds').value='333';
        document.querySelector('#mergeCheckMinutes').value='23';
        document.querySelector('#mergeStrategy').value='rebase';
        for(const id of ['notifications','sound','deleteSourceBranch','transitionWorkItems'])document.getElementById(id).checked=false;
        document.querySelector('#ignoreDrafts').checked=true;
        [...document.querySelectorAll('[data-native-repo]')].forEach((e,i)=>e.value='C:\\\\synthetic\\\\checkout-'+i);
        window.draftNativeKey=document.querySelector('[data-native-repo]').dataset.nativeRepo;
        window.expectedDraftFields=window.readDraftFields();`);
        const saved = await evaluate('structuredClone(window.committedDraftSettings)');
        const assertDraft = async () => assert.deepEqual(await evaluate('window.readDraftFields()'), await evaluate('window.expectedDraftFields'));
        const assertCommitted = async expected => {
          assert.deepEqual(await evaluate('S.settings'), expected, 'UI committed state matches only confirmed writes');
          assert.deepEqual(await evaluate('window.committedDraftSettings'), expected, 'Synthetic host state does not contain unfinished edits');
        };
        await run({ saved, assertDraft, assertCommitted });
      } finally {
        await evaluate(`MockHost.rpc=window.draftRpc;if(S.drawer)closeDrawer();S.settings=window.originalDraftSettings;render()`);
      }
    };

    await t.test('GUI settings failed Save preserves every available draft and only successful explicit Save survives reload', async () => {
      await withSettingsDraftFixture(async ({ saved, assertDraft, assertCommitted }) => {
        await evaluate(`window.denyDraftMethod='saveSettings';document.querySelector('#repoUrl').value='invalid draft URL';
          document.querySelector('#wlInput').value='Unsubmitted author';`);
        await click(await elementPoint('[data-dact="save"]')); await frame();
        await assertDraft(); await assertCommitted(saved);
        assert.equal(await evaluate(`document.querySelector('#repoUrl').value`), 'invalid draft URL');
        assert.equal(await evaluate(`document.querySelector('#wlInput').value`), 'Unsubmitted author');
        assert.equal(await evaluate(`document.querySelectorAll('#toasts .toast.error').length`), 1);
        assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Synthetic saveSettings write failed/);
        assert.doesNotMatch(await evaluate(`document.querySelector('#toasts').textContent`), /Settings saved/);
        await evaluate(`document.querySelector('#refreshSeconds').value='334';window.expectedDraftFields=window.readDraftFields();
          window.denyDraftMethod='';document.querySelector('#toasts').replaceChildren();`);
        await click(await elementPoint('[data-dact="save"]')); await frame();
        const expected = await evaluate('window.expectedDraftFields');
        const committed = { ...saved, ...expected, refreshSeconds: 334, mergeCheckMinutes: 23,
          autoComplete: { ...saved.autoComplete, mergeStrategy: expected.mergeStrategy,
            deleteSourceBranch: expected.deleteSourceBranch, transitionWorkItems: expected.transitionWorkItems } };
        for (const name of ['mergeStrategy','deleteSourceBranch','transitionWorkItems']) delete committed[name];
        await assertDraft(); await assertCommitted(committed);
        assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Settings saved/);
        assert.equal(await evaluate(`window.draftWrites[1].params.settings.refreshSeconds`), 334, 'Retry uses corrected preserved input');

        const injection = await send('Page.addScriptToEvaluateOnNewDocument', { source: `
          Object.defineProperty(window,'MockHost',{configurable:true,set(host){
            Object.defineProperty(window,'MockHost',{configurable:true,writable:true,value:host});
            const original=host.rpc.bind(host);
            host.rpc=async(method,p)=>{
              const result=await original(method,p);
              return method==='init'?{...result,settings:${JSON.stringify(committed)}}:result;
            };
          }});` });
        try {
          await evaluate('delete window.__ready');
          await send('Page.reload');
          await evaluate(`new Promise((resolve,reject)=>{
            const deadline=Date.now()+10000;
            const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('Reload did not boot')):setTimeout(check,20);check();
          })`);
          await evaluate('openDrawer()'); await frame();
          assert.deepEqual(await evaluate('S.settings'), committed, 'Fresh boot reads only confirmed synthetic host settings');
          assert.equal(await evaluate(`document.querySelector('#triageInstructions').value`), 'Unsaved Custom Ask\n');
          assert.equal(await evaluate(`document.querySelector('#refreshSeconds').value`), '334');
          assert.equal(await evaluate(`document.querySelector('[data-native-repo]').value`), Object.values(committed.nativeRepoDirectories)[0]);
          assert.equal(await evaluate(`S.settings.wslDistro`), 'Legacy-Ubuntu');
          assert.deepEqual(await evaluate(`S.settings.wslRepoDirectories`), { 'legacy/project/repo': '/legacy/checkout' });
          assert.equal(await evaluate(`document.querySelector('#repoUrl').value`), '');
          assert.equal(await evaluate(`document.querySelector('#wlInput').value`), '');
        } finally {
          await send('Page.removeScriptToEvaluateOnNewDocument', { identifier: injection.identifier });
          await evaluate(`window.draftRpc=MockHost.rpc;window.originalDraftSettings=${JSON.stringify(saved)};
            window.baseline=structuredClone(S.snap);`);
        }
      });
    });

    await t.test('GUI settings unrelated repository and author failures preserve input for correction and success commits only their own state', async () => {
      await withSettingsDraftFixture(async ({ saved, assertDraft, assertCommitted }) => {
        await evaluate(`document.querySelector('#repoUrl').value='invalid draft URL';`);
        await click(await elementPoint('[data-dact="addRepo"]')); await frame();
        await assertDraft(); await assertCommitted(saved);
        assert.equal(await evaluate(`document.querySelector('#repoUrl').value`), 'invalid draft URL');
        assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Synthetic invalid repository URL/);
        await evaluate(`document.querySelector('#repoUrl').value='https://dev.azure.com/contoso/Sample/_git/synthetic-draft-repo';
          window.denyDraftMethod='addRepo';document.querySelector('#toasts').replaceChildren();`);
        await click(await elementPoint('[data-dact="addRepo"]')); await frame();
        await assertDraft(); await assertCommitted(saved);
        assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Synthetic addRepo write failed/);
        assert.equal(await evaluate(`document.querySelector('#repoUrl').value`), 'https://dev.azure.com/contoso/Sample/_git/synthetic-draft-repo');
        await evaluate(`window.denyDraftMethod='';`);
        await click(await elementPoint('[data-dact="addRepo"]')); await frame();
        const withRepo = { ...saved, repos: [...saved.repos, { org:'contoso', project:'Sample', repo:'synthetic-draft-repo' }] };
        await assertCommitted(withRepo);
        const fields = await evaluate('window.readDraftFields()'), expected = await evaluate('window.expectedDraftFields');
        assert.deepEqual(fields, { ...expected, nativeRepoDirectories: { ...expected.nativeRepoDirectories, 'contoso/sample/synthetic-draft-repo': '' } });
        assert.equal(await evaluate(`document.querySelector('#repoUrl').value`), '');
        await evaluate(`window.expectedDraftFields=window.readDraftFields();window.denyDraftMethod='saveSettings';
          document.querySelector('#wlInput').value='Unfinished author';document.querySelector('#toasts').replaceChildren();`);
        await click(await elementPoint('[data-dact="addWl"]')); await frame();
        await assertDraft(); await assertCommitted(withRepo);
        assert.equal(await evaluate(`document.querySelector('#wlInput').value`), 'Unfinished author');
        assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Synthetic saveSettings write failed/);
        assert.doesNotMatch(await evaluate(`document.querySelector('#toasts').textContent`), /Watching PRs by/);
        await evaluate(`window.denyDraftMethod='';document.querySelector('#wlInput').value='Corrected author';`);
        await click(await elementPoint('[data-dact="addWl"]')); await frame();
        const withAuthor = { ...withRepo, whitelist: [...saved.whitelist, 'Corrected author'] };
        await assertDraft(); await assertCommitted(withAuthor);
        assert.equal(await evaluate(`document.querySelector('#wlInput').value`), '');
        assert.equal(await evaluate(`window.draftWrites.at(-1).params.settings.whitelist.at(-1)`), 'Corrected author');
        await click(await elementPoint(`[data-dact="removeWl"][data-i="${withAuthor.whitelist.length - 1}"]`)); await frame();
        await assertDraft(); await assertCommitted(withRepo);
        await click(await elementPoint('[data-dact="removeRepo"][data-key="contoso/sample/synthetic-draft-repo"]')); await frame();
        await assertCommitted(saved);
        assert.deepEqual(await evaluate('window.readDraftFields()'), expected);
        await evaluate('closeDrawer();openDrawer()'); await frame();
        assert.equal(await evaluate(`document.querySelector('#triageInstructions').value`), saved.triageInstructions || '');
        assert.equal(await evaluate(`document.querySelector('#refreshSeconds').value`), '120', 'Unrelated successes never committed polling drafts');
      });
    });

    await t.test('GUI settings immediate theme saves and rollback preserve drafts and unknown integration fields', async () => {
      await withSettingsDraftFixture(async ({ saved, assertDraft, assertCommitted }) => {
        await evaluate(`document.querySelector('#theme').value='dark';
          document.querySelector('#theme').dispatchEvent(new Event('change',{bubbles:true}));`);
        await frame();
        await assertDraft(); await assertCommitted({ ...saved, theme:'dark' });
        assert.equal(await evaluate('document.documentElement.dataset.theme'), 'dark');
        assert.deepEqual(await evaluate('window.draftWrites'), [{method:'setTheme',params:{theme:'dark'}}]);
        await evaluate(`window.denyDraftMethod='setTheme';document.querySelector('#theme').value='light';
          document.querySelector('#theme').dispatchEvent(new Event('change',{bubbles:true}));`);
        await frame();
        await assertDraft(); await assertCommitted({ ...saved, theme:'dark' });
        assert.equal(await evaluate('document.documentElement.dataset.theme'), 'dark');
        assert.equal(await evaluate(`document.querySelector('#theme').value`), 'dark');
        assert.match(await evaluate(`document.querySelector('#toasts').textContent`), /Theme was not saved: Synthetic setTheme write failed/);
        await evaluate('renderDrawer()'); await frame();
        await assertDraft(); await assertCommitted({ ...saved, theme:'dark' });
        await evaluate(`window.denyDraftMethod='';document.querySelector('#theme').value='light';
          document.querySelector('#theme').dispatchEvent(new Event('change',{bubbles:true}));`);
        await frame();
        await assertDraft(); await assertCommitted({ ...saved, theme:'light' });
        assert.equal(await evaluate('document.documentElement.dataset.theme'), 'light');
      });
    });

    await t.test('GUI settings delayed Save and Add preserve independent, newer and reopened-drawer drafts', async () => {
      await withSettingsDraftFixture(async () => {
        const finishWrite = async () => {
          assert.equal(await evaluate(`typeof window.completeDraftWrite`), 'function', 'Synthetic write must be pending');
          await evaluate(`window.holdDraftMethod='';window.completeDraftWrite();delete window.completeDraftWrite;`);
          await frame();
        };
        await evaluate(`window.holdDraftMethod='saveSettings';
          document.querySelector('#repoUrl').value='Unsubmitted repository URL';
          document.querySelector('#wlInput').value='Unsubmitted author';`);
        await click(await elementPoint('[data-dact="save"]')); await frame();
        await evaluate(`document.querySelector('#triageInstructions').value='Newer Custom Ask';
          document.querySelector('#refreshSeconds').value='334';
          document.querySelector('#mergeStrategy').value='squash';
          document.querySelector('#notifications').checked=true;
          renderDrawer();`);
        await finishWrite();
        assert.equal(await evaluate('S.settings.triageInstructions'), 'Unsaved Custom Ask\n');
        assert.equal(await evaluate('S.settings.refreshSeconds'), 333);
        assert.equal(await evaluate(`document.querySelector('#triageInstructions').value`), 'Newer Custom Ask');
        assert.equal(await evaluate(`document.querySelector('#refreshSeconds').value`), '334');
        assert.equal(await evaluate(`document.querySelector('#mergeStrategy').value`), 'squash');
        assert.equal(await evaluate(`document.querySelector('#notifications').checked`), true);
        assert.equal(await evaluate(`document.querySelector('#repoUrl').value`), 'Unsubmitted repository URL');
        assert.equal(await evaluate(`document.querySelector('#wlInput').value`), 'Unsubmitted author');
        assert.equal(await evaluate(`document.querySelector('[data-native-repo]').value`), 'C:\\synthetic\\checkout-0');

        await evaluate(`window.holdDraftMethod='saveSettings';
          window.submittedNativePath='  C:\\\\synthetic\\\\submitted  ';
          document.querySelector('[data-native-repo]').value=window.submittedNativePath;`);
        await click(await elementPoint('[data-dact="save"]')); await frame();
        await evaluate(`(async()=>{closeDrawer();await openDrawer();
          document.querySelector('#triageInstructions').value='Newer Custom Ask';
          document.querySelector('[data-native-repo]').value=window.submittedNativePath;
          document.querySelector('#wlInput').value='Reopened author';renderDrawer();})()`);
        await finishWrite();
        assert.equal(await evaluate('S.settings.triageInstructions'), 'Newer Custom Ask');
        assert.equal(await evaluate(`document.querySelector('#triageInstructions').value`), 'Newer Custom Ask');
        assert.equal(await evaluate('S.settings.nativeRepoDirectories[window.draftNativeKey]'), 'C:\\synthetic\\submitted');
        assert.equal(await evaluate(`document.querySelector('[data-native-repo]').value`), '  C:\\synthetic\\submitted  ', 'Same-valued reopened input is not normalized by the previous drawer Save');
        assert.equal(await evaluate('S.settings.wslDistro'), 'Legacy-Ubuntu');
        assert.equal(await evaluate(`document.querySelector('#wlInput').value`), 'Reopened author');

        for (const action of ['addRepo','addWl']) {
          const id = action === 'addRepo' ? 'repoUrl' : 'wlInput';
          const method = action === 'addRepo' ? 'addRepo' : 'saveSettings';
          const submitted = action === 'addRepo' ? 'https://dev.azure.com/contoso/Sample/_git/synthetic-draft-repo' : 'Submitted author';
          await evaluate(`window.holdDraftMethod=${JSON.stringify(method)};
            document.querySelector('#${id}').value=${JSON.stringify(submitted)};`);
          await click(await elementPoint(`[data-dact="${action}"]`)); await frame();
          if (action === 'addRepo') {
            assert.deepEqual(await evaluate(`(b=>({disabled:b.disabled,text:b.textContent.trim()}))(document.querySelector('[data-dact="addRepo"]'))`),
              { disabled: true, text: 'Checking…' }, 'addRepo shows a busy state while the repository is validated');
          }
          await evaluate(`document.querySelector('#${id}').value='Next unsubmitted value';renderDrawer();`);
          await finishWrite();
          assert.equal(await evaluate(`document.querySelector('#${id}').value`), 'Next unsubmitted value', `${action} must preserve newer text after rerender`);
          if (action === 'addRepo') {
            await click(await elementPoint('[data-dact="removeRepo"][data-key="contoso/sample/synthetic-draft-repo"]')); await frame();
          }

          await evaluate(`window.holdDraftMethod=${JSON.stringify(method)};
            document.querySelector('#${id}').value=${JSON.stringify(submitted)};`);
          await click(await elementPoint(`[data-dact="${action}"]`)); await frame();
          await evaluate(`(async()=>{closeDrawer();await openDrawer();document.querySelector('#${id}').value=${JSON.stringify(submitted)};renderDrawer();})()`);
          await finishWrite();
          assert.equal(await evaluate(`document.querySelector('#${id}').value`), submitted, `${action} must not clear same-valued text in a reopened drawer`);
        }
      });
    });

    await t.test('both themes apply to real components, states, contrast and saved reloads', async () => {
      const navigate = async url => {
        await evaluate('delete window.__ready');
        await send('Page.navigate', { url });
        await evaluate(`new Promise((resolve,reject)=>{
          const deadline=Date.now()+10000;
          const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot')):setTimeout(check,20);check();
        })`);
        await send('Page.bringToFront');
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
    if (proofDir) {
      const removeOwnedProfile = async directory => {
        for (const entry of await readdir(directory, { withFileTypes: true })) {
          const owned = path.join(directory, entry.name);
          if (entry.isDirectory()) await removeOwnedProfile(owned);
          else await rm(owned);
        }
        await rmdir(directory);
      };
      try {
        await removeOwnedProfile(profile);
        await writeFile(path.join(proofDir, 'cleanup.json'), JSON.stringify({ browserPid: child.pid,
          exitCode: child.exitCode, signal: child.signalCode, profileRemoved: !existsSync(profile) }));
      } catch (error) {
        await writeFile(path.join(proofDir, 'cleanup.json'), JSON.stringify({ browserPid: child.pid,
          exitCode: child.exitCode, signal: child.signalCode, cleanupError: error.message }));
        throw error;
      }
    } else await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
});
