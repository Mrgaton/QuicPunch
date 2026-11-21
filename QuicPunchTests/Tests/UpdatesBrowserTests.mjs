import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real shared UI with fixture responses; never downloads or installs a package.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  let installs = 0, callActive = false, confirmedInstalls = 0;
  let rejectChannel = false, socket, connections = 0, statusRequests = 0, holdStatus = false, heldStatus;
  const httpPolls=[], commands=[];
  page.on('request', request => { if (/\/api\/(status|updates)(\/|$|\?)/.test(request.url())) httpPolls.push(request.url()); });
  let status = { CurrentVersion: '1.1.0', CurrentBuild: 2, Platform: 'win-x64', Automatic: true, Channel:'stable', CurrentChannel:'stable',
    Stage: 'ready', LatestVersion: '1.2.0', LatestBuild: 3, Downloaded: 10, Total: 10, Revision:1000 };
  const push=()=>{status={...status,Revision:status.Revision+1};socket.send(JSON.stringify({type:'update_status',data:status}));};
  await page.routeWebSocket(/\/ws\?/, route => {
    socket=route; connections++;
    const backend=route.connectToServer();
    backend.onMessage(message=>route.send(message));
    route.onMessage(message=>{
      const request=JSON.parse(message);
      if(request.command==='status.get'){
        statusRequests++;
        if(holdStatus){heldStatus=request;return;}
      }
      if(request.type!=='request'||!request.command.startsWith('updates.')){backend.send(message);return;}
      commands.push(request.command);
      if(request.command==='updates.channel'){
        if(rejectChannel){rejectChannel=false;route.send(JSON.stringify({type:'response',id:request.id,success:false,error:'Espera a que termine la actualización'}));return;}
        status={...status,Channel:request.data.channel,Stage:'current',LatestBuild:null,LatestVersion:null};
      }
      if(request.command==='updates.automatic')status={...status,Automatic:request.data.enabled};
      if (request.command === 'updates.install') {
        if (callActive && request.data?.endCalls !== true) {
          route.send(JSON.stringify({type:'response', id:request.id, success:true, data:{...status, ConfirmEndCalls:true}}));
          return;
        }
        installs++;
        if (request.data?.endCalls === true) { confirmedInstalls++; callActive = false; }
      }
      push();route.send(JSON.stringify({type:'response',id:request.id,success:true,data:status}));
    });
    push();
  });
  await page.goto(url);
  const menu = page.locator('#menuUpdateNotice'), mobile = page.locator('#mobileUpdateNotice');
  await menu.waitFor({ state: 'visible' });
  assert.equal(await mobile.isVisible(), false);
  await menu.getByRole('button', { name: 'Instalar y reiniciar' }).click();
  assert.equal(installs, 1);
  assert.equal(new URL(page.url()).pathname, '/', 'Install does not navigate to settings');
  for (const width of [920, 680, 390, 320]) {
    await page.setViewportSize({ width, height: 844 });
    await mobile.waitFor({ state: 'visible' });
    assert.equal(await menu.isVisible(), false);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `No horizontal overflow at ${width}px`);
    const notice = await mobile.boundingBox(), nav = await page.locator('.nav-wrap').boundingBox();
    if (width <= 680) assert.ok(notice.y + notice.height < nav.y, 'Update never covers mobile navigation');
    const button = await page.locator('#mobileUpdateInstall').boundingBox();
    assert.ok(button.x >= 0 && button.x + button.width <= width && button.height >= 44, 'Install button fits and is touch sized');
  }
  const refresh = async () => { push(); };
  status = { ...status, Platform: 'android-arm64', Message: 'Confirma la instalación en Android.' };
  await refresh();
  await mobile.getByRole('button', { name: 'Instalar actualización' }).waitFor();
  await mobile.getByRole('button', { name: 'Instalar actualización' }).click();
  assert.equal(installs, 2);
  await page.waitForFunction(() => !document.getElementById('mobileUpdateInstall').disabled);
  callActive = true;
  const installButton = mobile.getByRole('button', { name: 'Instalar actualización' });
  const confirm = page.getByRole('dialog', { name: '¿Actualizar igualmente?' });
  await installButton.click();
  await confirm.waitFor();
  assert.match(await confirm.innerText(), /Hay una llamada en curso/);
  const keepCall = confirm.getByRole('button', { name: 'Seguir en llamada' });
  assert.equal(await keepCall.evaluate(button => button === document.activeElement), true);
  const bounds = await confirm.boundingBox();
  assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= 320, 'Confirmation fits at 320px');
  await page.keyboard.press('Escape');
  await confirm.waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.activeElement.id === 'mobileUpdateInstall');
  assert.equal(installs, 2, 'Dismissing the dialog kept the call');
  await installButton.click();
  await confirm.getByRole('button', { name: 'Sí, actualizar' }).click();
  await page.waitForFunction(() => !document.getElementById('mobileUpdateInstall').disabled);
  assert.equal(confirmedInstalls, 1, 'Explicit consent starts exactly one installation');
  if (process.env.QUICNET_UPDATE_SCREENSHOT) await page.screenshot({ path: process.env.QUICNET_UPDATE_SCREENSHOT });
  await page.locator('.nav-link[data-page=chat]').click();
  assert.equal(await mobile.isVisible(), true, 'Update stays visible after SPA navigation');
  const chat = await page.locator('.direct-chat').boundingBox();
  const navigation = await page.locator('.nav-wrap').boundingBox();
  assert.ok(chat.y + chat.height <= navigation.y + 2, 'Conversation fits above mobile navigation with the notice visible');
  await page.locator('#chatNewBtn').click();
  await page.locator('#chatNewPeers button').filter({ hasText: 'Ana · Portátil' }).click();
  await page.locator('#messageInput').waitFor({ state: 'visible' });
  await page.locator('#messageInput').blur();
  await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 600;
    canvas.height = 800;
    onRealtimeMessage({ peerId: '00000000-0000-0000-0000-000000000001', msgId: 'portrait-preview',
      message: JSON.stringify({ type: 'image', name: 'image-preview.png', data: canvas.toDataURL() }),
      timestampMs: Date.now(), isMe: true, sender: 'Tú' });
  });
  await page.waitForFunction(() => document.querySelector('[data-message-id="portrait-preview"] img')?.naturalHeight === 800);
  for (const viewport of [{ width: 390, height: 844 }, { width: 320, height: 568 }, { width: 667, height: 375 }]) {
    await page.setViewportSize(viewport);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const layout = await page.evaluate(() => {
      const rect = selector => document.querySelector(selector).getBoundingClientRect();
      const history = rect('#messages');
      const image = rect('[data-message-id="portrait-preview"] img');
      return {
        header: rect('.topbar').height, notice: rect('#mobileUpdateNotice').height,
        history: history.height, composer: rect('.direct-composer').bottom,
        imageFits: image.top >= history.top - 1 && image.bottom <= history.bottom + 1,
        nav: rect('.nav-wrap').height ? rect('.nav-wrap').top : innerHeight,
        overflow: document.documentElement.scrollWidth - innerWidth,
      };
    });
    const context = `${viewport.width}×${viewport.height}: ${JSON.stringify(layout)}`;
    assert.ok(layout.notice <= 90 && layout.header - layout.notice <= 58, `Update and network selector take too much chat space at ${context}`);
    assert.ok(layout.history >= viewport.height * 0.25, `Too little room for messages at ${context}`);
    assert.ok(layout.imageFits, `The latest image is cropped at ${context}`);
    assert.ok(layout.composer <= layout.nav + 1 && layout.overflow <= 1, `Chat controls are clipped at ${context}`);
  }
  await page.setViewportSize({ width: 320, height: 300 });
  await page.locator('#messageInput').fill('Probando el teclado');
  assert.ok(await page.locator('#messageInput').evaluate(input => input.getBoundingClientRect().bottom <= innerHeight &&
    document.getElementById('messages').clientHeight > 0), 'The keyboard-sized viewport keeps a visible editor and history');
  await page.locator('#messageInput').blur();
  await page.setViewportSize({ width: 667, height: 375 });
  await page.locator('#mobileUpdateInstall').click({ trial: true });
  await page.locator('[data-message-id="portrait-preview"] img').click();
  await page.locator('#chatLightbox').waitFor({ state: 'visible' });
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#navMore').isVisible(),false);
  await page.locator('#chatOptionsMenu summary').click();
  await page.getByRole('button', { name: 'Herramientas de QuicNet' }).click();
  await page.getByRole('dialog', { name:'Más', exact:true }).waitFor();
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#chatOptionsMenu summary').evaluate(node=>node===document.activeElement), true);
  assert.equal(await page.locator('#messageInput').inputValue(),'Probando el teclado');
  await page.locator('#chatMobileBackBtn').click();
  assert.ok(await page.locator('#navMore').isVisible(), 'Back restores navigation on short screens');
  status = { ...status, Stage: 'downloading', Downloaded: 4 };
  await refresh();
  await page.waitForFunction(() => document.getElementById('mobileUpdateDetail').textContent.includes('40 %'));
  assert.equal(await page.locator('#mobileUpdateInstall').isDisabled(), true);
  status = { ...status, Stage: 'current', LatestBuild: 2 };
  await refresh();
  await mobile.waitFor({ state: 'hidden' });
  await page.locator('.nav-link[data-page=settings]').click();
  await page.locator('#settingsCategory').selectOption('updates');
  const channel=page.locator('#updateChannel');
  await channel.selectOption('beta');
  await page.waitForFunction(()=>!document.getElementById('updateChannel').disabled);
  assert.equal(status.Channel,'beta');assert.equal(await channel.inputValue(),'beta');
  assert.match(await page.locator('#updateChannelHelp').textContent(),/versiones de prueba/);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Channel selector overflows on mobile');
  rejectChannel=true;
  await channel.selectOption('stable');
  await page.locator('#updateMessage').filter({hasText:'Espera a que termine'}).waitFor();
  assert.equal(await channel.inputValue(),'beta','Rejected channel was not rolled back');
  await page.reload();await page.waitForFunction(()=>!document.getElementById('updateChannel').disabled);
  assert.equal(await channel.inputValue(),'beta','Selected channel was lost after reload');
  await channel.selectOption('stable');await page.waitForFunction(()=>!document.getElementById('updateChannel').disabled);
  assert.equal(status.Channel,'stable');
  status={...status,Stage:'downloading',LatestBuild:3};await refresh();
  await page.waitForFunction(()=>document.getElementById('updateChannel').disabled);
  const beforeReconnect=connections;
  socket.close({code:1012,reason:'Test restart'});
  await page.waitForFunction(()=>!QP.isWsConnected);
  await page.waitForFunction(()=>QP.isWsConnected);
  assert.equal(connections,beforeReconnect+1,'Reconnect created duplicate sockets');
  await page.waitForFunction(()=>QP.state.updates?.Stage==='downloading');
  const latestRevision=status.Revision;
  socket.send(JSON.stringify({type:'update_status',data:{...status,Revision:latestRevision-1,Downloaded:0}}));
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
  assert.equal(await page.evaluate(()=>QP.state.updates.Revision),latestRevision,'Stale progress overwrote a newer snapshot');
  const beforeRequests=statusRequests;
  await page.evaluate(()=>Promise.all(Array.from({length:10},()=>QP.getStatus(true))));
  assert.equal(statusRequests,beforeRequests+1,'Concurrent refreshes were not combined');
  holdStatus=true;
  const oldSnapshot=await page.evaluate(()=>QP.state.status);
  await page.evaluate(()=>{window.testStatusResult=QP.getStatus(true);});
  for(let i=0;!heldStatus&&i<100;i++)await new Promise(resolve=>setTimeout(resolve,10));
  assert.ok(heldStatus,'Status request never reached the socket');
  socket.send(JSON.stringify({type:'status_updated',data:{...oldSnapshot,node:{...oldSnapshot.node,name:'Estado más reciente'}}}));
  socket.send(JSON.stringify({type:'response',id:heldStatus.id,success:true,data:oldSnapshot}));
  assert.equal(await page.evaluate(async()=>(await window.testStatusResult).node.name),'Estado más reciente','Old RPC response replaced a newer pushed status');
  await page.waitForTimeout(3500);
  assert.deepEqual(httpPolls,[],'Status and updates must never fall back to HTTP polling');
  assert.deepEqual(errors, [], 'Shared application scripts produced no uncaught errors');
  console.log('PASS WebSocket updates: push/progress, commands, reconnect/replay, stale revision, zero HTTP polling, channel guards and responsive notices');
} finally {
  await closeBrowserFixture(browser, server);
}
