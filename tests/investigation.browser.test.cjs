const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm } = require('node:fs/promises');
const { once } = require('node:events');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

test('PR conversation follow-ups, lens selection, posting outcomes, cancellation and PR switching', { timeout: 90000 }, async () => {
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
    const pending = new Map();
    socket.addEventListener('message', e => {
      const m = JSON.parse(e.data), p = pending.get(m.id);
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
    await send('Page.enable');
    await send('Emulation.setDeviceMetricsOverride', { width: 1400, height: 1000, deviceScaleFactor: 1, mobile: false });
    await send('Page.navigate', { url: pathToFileURL(path.resolve(__dirname, '..', 'src', 'AdoPrView', 'wwwroot', 'index.html')).href + '?mock=1' });
    await evaluate(`new Promise((resolve,reject)=>{
      const deadline=Date.now()+10000;
      const check=()=>window.__ready?resolve():Date.now()>deadline?reject(Error('UI did not boot')):setTimeout(check,20);check();
    })`);
    await evaluate(`
      window.prA=S.snap.prs.find(p=>canExplain(p.key));window.prB=S.snap.prs.find(p=>p.key!==prA.key&&canExplain(p.key));
      window.calls=[];window.invSequence=0;window.invRequest=0;window.confirmResult=true;
      window.confirm=()=>window.confirmResult;
      window.MockHost.rpc=async(method,p)=>{
        calls.push({method,...p});
        if(method==='askPr'){
          const previous=S.investigations.get(p.key);
          return {key:p.key,conversationId:previous?.conversationId||'conversation-'+p.key,requestId:'turn-'+(++invRequest),
            sequence:++invSequence,status:'running',message:'Inspecting synthetic source; no comments posted.',
            revision:{sourceCommit:'a'.repeat(40),baseCommit:'b'.repeat(40),targetCommit:'c'.repeat(40),iteration:4},
            worktree:'Ubuntu: /tmp/synthetic/worktree (pinned object reads)',turns:previous?.turns||[],evidence:[],coverage:[]};
        }
        if(method==='cancelInvestigation')return {...S.investigations.get(p.key),sequence:++invSequence,status:'cancelled',message:'Cancelled synthetic inspection.'};
        if(method==='postLensFindings'){
          if(window.postFailure)throw Error('PR revisions changed. Generate a fresh preview.');
          const state=S.investigations.get(p.key),turn=state.turns.at(-1);
          return {...state,sequence:++invSequence,message:'1 comment(s) confirmed on ADO; 1 failed or uncertain. 0 not selected.',
            turns:[...state.turns.slice(0,-1),{...turn,outcomes:[
              {findingId:p.findingIds[0],status:'posted',message:'ADO confirmed this comment.',url:'https://dev.azure.com/offline/thread?discussionId=41'},
              {findingId:p.findingIds[1],status:'uncertain',message:'No automatic retry; check ADO.',url:null}]}]};
        }
        if(method==='openUrl')return null;
        if(method==='resetInvestigation')return {key:p.key,conversationId:'fresh-'+(++invRequest),requestId:'',
          sequence:++invSequence,status:'completed',message:'Fresh conversation; no source captured.',revision:null,worktree:null,turns:[],evidence:[],coverage:[]};
        throw Error('Unexpected mocked RPC '+method);
      };
      window.finishInv=(key,kind,question,findings=[])=>{
        const state=S.investigations.get(key);
        onHostMessage({event:'investigation',data:{...state,sequence:++invSequence,status:'completed',message:kind==='lens'?'Preview only; no comments posted.':'Answer ready.',
          turns:[...state.turns,{id:state.requestId,kind,question,answer:'Plain English with **source evidence**. <img src="https://evil.invalid/" onerror="window.hostile=true">',findings,outcomes:[]}],
          evidence:['Worktree after: /src/code.cs, lines 10-12.'],coverage:['1 uninspected change.']}});
      };
      S.expanded.add(prA.key);renderRows();
      [...document.querySelectorAll('[data-act="askPr"]')].find(x=>x.closest('.pr').dataset.key===prA.key).click();
    `);
    assert.equal(await evaluate(`document.activeElement.id`), 'investigationQuestion');
    await evaluate(`
      $('#investigationQuestion').value='  What genuinely improves?  ';
      $('#investigationQuestion').dispatchEvent(new Event('input',{bubbles:true}));
      $('#investigationSend').click();
      new Promise(resolve=>setTimeout(resolve,30));
    `);
    assert.equal(await evaluate(`calls.at(-1).method`), 'askPr');
    assert.equal(await evaluate(`calls.at(-1).question`), 'What genuinely improves?');
    assert.equal(await evaluate(`$('#investigationSend').disabled`), true);
    assert.match(await evaluate(`$('#investigationSource').textContent`), /pinned object reads/);
    await evaluate(`finishInv(prA.key,'question','What genuinely improves?')`);
    assert.equal(await evaluate(`$('#investigationQuestion').value`), '');
    assert.equal(await evaluate(`investigationDrafts.get(prA.key).question`), '');
    assert.equal(await evaluate(`$('#investigationTranscript img')===null && !window.hostile`), true);
    await evaluate(`
      $('#investigationQuestion').value='Which caller depends on that?';
      $('#investigationQuestion').dispatchEvent(new Event('input',{bubbles:true}));
      $('#investigationSend').click();
      new Promise(resolve=>setTimeout(resolve,30));
    `);
    assert.equal(await evaluate(`calls.at(-1).conversationId`), await evaluate(`S.investigations.get(prA.key).conversationId`));
    await evaluate(`finishInv(prA.key,'question','Which caller depends on that?')`);
    assert.equal(await evaluate(`$('#investigationTranscript').querySelectorAll('article').length`), 2);
    await evaluate(`
      $('#investigationClose').click();openInvestigation(prB.key);
      $('#investigationQuestion').value='Private draft for B';$('#investigationQuestion').dispatchEvent(new Event('input',{bubbles:true}));
      $('#investigationClose').click();openInvestigation(prA.key,'lens');
      $('#investigationQuestion').value='Does the new ARM payload work with ACI?';$('#investigationQuestion').dispatchEvent(new Event('input',{bubbles:true}));
      $('#investigationSend').click();new Promise(resolve=>setTimeout(resolve,30));
    `);
    await evaluate(`finishInv(prA.key,'lens','Does the new ARM payload work with ACI?',[
      {id:'0',title:'Concrete bug A',body:'A required property is dropped. ![hidden](https://example.invalid/image) [label](https://example.invalid/target) <img src="https://example.invalid/html">',path:'/src/code.cs',line:10,quote:'actual line'},
      {id:'1',title:'Concrete bug B',body:'Caller passes a rejected value.',path:'/src/code.cs',line:11,quote:'second line'}
    ])`);
    assert.equal(await evaluate(`calls.filter(c=>c.method==='postLensFindings').length`), 0);
    assert.equal(await evaluate(`document.querySelector('.lens-finding .investigation-question').textContent.includes('https://example.invalid/image') &&
      document.querySelector('.lens-finding .investigation-question').textContent.includes('https://example.invalid/target') &&
      document.querySelector('.lens-finding .investigation-question').textContent.includes('<img') &&
      !document.querySelector('.lens-finding img, .lens-finding a')`), true);
    assert.equal(await evaluate(`document.querySelectorAll('[data-lens-finding]:checked').length`), 0);
    await evaluate(`$('#postLensFindings').click();new Promise(resolve=>setTimeout(resolve,20))`);
    assert.match(await evaluate(`$('#investigationError').textContent`), /Select at least one/);
    await evaluate(`
      document.querySelectorAll('[data-lens-finding]').forEach(x=>x.checked=true);
      window.confirmResult=false;$('#postLensFindings').click();new Promise(resolve=>setTimeout(resolve,20));
    `);
    assert.equal(await evaluate(`calls.filter(c=>c.method==='postLensFindings').length`), 0);
    await evaluate(`window.confirmResult=true;$('#postLensFindings').click();new Promise(resolve=>setTimeout(resolve,30))`);
    const post = await evaluate(`calls.find(c=>c.method==='postLensFindings')`);
    assert.deepEqual(post.findingIds, ['0', '1']);
    assert.equal(post.confirmed, true);
    assert.match(await evaluate(`$('#investigationStatus').textContent`), /1 comment.*1 failed or uncertain/);
    assert.equal(await evaluate(`document.querySelectorAll('[data-investigation-url]').length`), 1);
    assert.match(await evaluate(`$('#investigationTranscript').textContent`), /No automatic retry/);
    await evaluate(`
      const saved=S.investigations.get(prA.key);window.savedSource=saved;
      setSnapshot({...S.snap,prs:[...S.snap.prs].reverse()});
      onHostMessage({event:'investigation',data:{...saved,sequence:saved.sequence-1,message:'stale overwrite'}});
    `);
    assert.equal(await evaluate(`S.investigations.get(prA.key)===savedSource`), true);
    await evaluate(`
      $('#investigationClose').click();openInvestigation(prB.key);
    `);
    assert.equal(await evaluate(`$('#investigationQuestion').value`), 'Private draft for B');
    assert.equal(await evaluate(`$('#investigationTranscript').textContent.trim()`), '');
    await evaluate(`$('#investigationSend').click();new Promise(resolve=>setTimeout(resolve,30))`);
    await evaluate(`
      $('#investigationClose').click();window.beforeRemoval=S.snap;
      setSnapshot({...S.snap,prs:S.snap.prs.filter(p=>p.key!==prB.key)});
      $('#openInvestigationActivity').click();
    `);
    assert.equal(await evaluate(`$('#investigationDialog').open && $('#investigationSend').disabled && !$('#investigationCancel').hidden`), true);
    await evaluate(`$('#investigationCancel').click();new Promise(resolve=>setTimeout(resolve,30))`);
    assert.equal(await evaluate(`S.investigations.get(prB.key).status`), 'cancelled');
    assert.equal(await evaluate(`calls.at(-1).key`), await evaluate(`prB.key`));
    await evaluate(`setSnapshot(window.beforeRemoval)`);
    await evaluate(`
      $('#investigationClose').click();openInvestigation(prA.key,'lens');
      $('#investigationQuestion').value='Is the EOS contract preserved?';$('#investigationSend').click();
      new Promise(resolve=>setTimeout(resolve,30));
    `);
    await evaluate(`finishInv(prA.key,'lens','Is the EOS contract preserved?')`);
    assert.match(await evaluate(`$('#investigationTranscript').textContent`), /No findings established/);
    assert.equal(await evaluate(`$('#postLensFindings')===null`), true);
    await evaluate(`
      $('#investigationQuestion').value='Check compatibility again';$('#investigationSend').click();new Promise(resolve=>setTimeout(resolve,30));
    `);
    await evaluate(`finishInv(prA.key,'lens','Check compatibility again',[{id:'0',title:'A real failure',body:'Concrete.',path:'/src/code.cs',line:10,quote:'actual line'}]);
      $('[data-lens-finding]').checked=true;window.postFailure=true;$('#postLensFindings').click();new Promise(resolve=>setTimeout(resolve,30));`);
    assert.match(await evaluate(`$('#investigationError').textContent`), /revisions changed/);
    assert.equal(await evaluate(`$('#investigationTranscript article:last-child [data-investigation-url]')===null`), true);
    const lightBackground = await evaluate(`getComputedStyle($('#investigationDialog')).backgroundColor`);
    await evaluate(`document.documentElement.dataset.theme='dark'`);
    assert.notEqual(await evaluate(`getComputedStyle($('#investigationDialog')).backgroundColor`), lightBackground);
    assert.notEqual(await evaluate(`getComputedStyle($('#investigationDialog')).color`), await evaluate(`getComputedStyle($('#investigationDialog')).backgroundColor`));
    await evaluate(`$('#investigationReset').click();new Promise(resolve=>setTimeout(resolve,30))`);
    assert.equal(await evaluate(`$('#investigationTranscript').textContent.trim()`), '');
    assert.equal(await evaluate(`S.investigations.get(prA.key).revision`), null);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.close();
    child.kill();
    await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 5000))]);
    await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
