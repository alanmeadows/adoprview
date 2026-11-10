const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { mkdtemp, rm } = require('node:fs/promises');
const { once } = require('node:events');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

// No browser packages required: Node 22+ and a local Chromium browser.
test('real row hit testing and action identity across refreshes', { timeout: 90000 }, async t => {
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
      await release({ x: 10, y: 10 });
      await move(10, 10);
      await evaluate(`document.activeElement?.blur();`);
      await frame();
      await evaluate(`S.expanded.clear();S.lane=null;S.repo='';S.q='';S.scope='all';
        S.rowOrder=[];S.orderRevision=null;S.laneRetained.clear();S.runs.clear();S.busy.clear();closeRowMenu();
        window.holdLog=false;S.settings.hidden=['sample-deploy-id:1001'];S.settings.ignoreDrafts=false;S.hideUndo=[];
        window.confirmations=[];window.confirmResult=true;window.autoFailure=false;S.autoCompleteErrors.clear();
        setSnapshot(structuredClone(window.baseline));${race ? 'window.raceFixture();' : ''}render();window.calls=[];`);
      await frame();
    };
    const point = (key, selector) => evaluate(`window.rowPoint(${JSON.stringify(key)},${JSON.stringify(selector)})`);
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
      assert.deepEqual(await state(), {same:true,act:'cancelAuto',grey:'rgb(180, 186, 200)',pressed:'true',busy:false,detail:'squash · by Offline Reviewer',other:'autocomplete'});
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
            {isMine:false,humanActions:['reviews','pop','autocomplete'],approval:null,repo:'other-repo',gates:{...base.gates,reviews:{state:'human'}}},
            {isMine:true,humanActions:[],approval:null,autoCompleteBy:null,gates:{...base.gates,pop:{state:'na'},reviews:{state:'ok'}}},
            {isMine:true,humanActions:['reviews','publish','pop','autocomplete'],approval:{title:'Request approval for only this PR'},gates:{...base.gates,reviews:{state:'human'}}}
          ];
          const ps=states.map((s,i)=>({...base,...s,key:'grid-'+i,id:100+i,title:'Grid fixture '+i,
            url:'https://dev.azure.com/contoso/Sample/_git/'+(s.repo||base.repo)+'/pullrequest/'+(100+i)}));
          for(const action of ['generateApproval','publish','pop','autocomplete','triage'])S.busy.add('grid-4:'+action);
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

    await t.test('Copilot spinner survives collapse, another PR, refresh/filter and terminal events without losing per-PR state', async () => {
      await reset(true);
      const a = 'sample-service-id:101', b = 'sample-api-id:104';
      const running = key => evaluate(`(()=>{const e=document.querySelector('[data-key="${key}"] .row [data-act="triage"]');return {running:e.classList.contains('running'),busy:e.getAttribute('aria-busy'),disabled:e.disabled,animation:getComputedStyle(e.querySelector('svg')).animationName};})()`);
      await click(await point(a, '.row [data-act="triage"]')); await frame();
      assert.deepEqual(await running(a), { running: true, busy: 'true', disabled: false, animation: 'spin' }, 'Launch reply must not clear the running indicator');
      await click(await point(a, '.row .title')); await frame();
      await click(await point(b, '.row .title')); await frame();
      assert.equal((await running(a)).running, true);
      assert.equal((await running(b)).running, false);
      await move(10, 10); await evaluate('document.activeElement?.blur()'); await frame();
      await evaluate(`setSnapshot({...S.snap,prs:S.snap.prs.map(p=>({...p,updated:'2099-01-01'}))});S.q='no matching PR';render();S.q='';render();`);
      await frame();
      assert.equal((await running(a)).running, true);
      await evaluate(`updateCopilotRun({...S.snap.prs.find(p=>p.key==='${b}'),runId:'independent',status:'running',output:''});`);
      for (const status of ['completed', 'failed', 'cancelled']) {
        await evaluate(`updateCopilotRun({...runForPr('${a}'),runId:'test-${status}',status:'running',output:''});`);
        assert.equal((await running(a)).running, true);
        await evaluate(`updateCopilotRun({...runForPr('${a}'),status:'${status}'});`);
        assert.equal((await running(a)).running, false, 'Collapsed terminal event must update the existing button');
        assert.equal((await running(b)).running, true);
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

    await send('Browser.close', {}, false);
  } finally {
    socket?.close();
    if (child.exitCode === null) await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 3000))]);
    if (child.exitCode === null) { child.kill(); await exited; }
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
});
