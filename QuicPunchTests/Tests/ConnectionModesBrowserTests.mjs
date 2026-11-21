import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';
const server = startPreviewServer(['--mode-fail-first-tor', '--mode-tor-delay=3500']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await page.waitForFunction(()=>QP.state.status&&QP.isWsConnected);
  const modes = page.locator('#headerNetworkSwitch');
  const tor = modes.getByRole('button', {name:'Solo Tor'}), wan = modes.getByRole('button', {name:'Solo WAN'});
  const enableTor = async () => {
    await tor.click();
    await page.getByRole('dialog', {name:'¿Cambiar a Tor?'}).getByRole('button', {name:'Cambiar a Tor'}).click();
  };
  assert.equal(await modes.isVisible(), true);
  assert.equal(await page.locator('#view-dashboard button[data-connection-mode]').count(), 0);
  await enableTor();
  await page.waitForFunction(()=>document.querySelector('#connectionModeStatus').textContent.includes('no disponible'));
  assert.equal(await page.evaluate(()=>QP.state.status.wan.isStarted),false,'Failed Tor activation restored WAN');
  assert.notEqual(await page.locator('html').getAttribute('data-connection-mode'),'tor','Failure applied a misleading Tor palette');
  // Accept Tor promptly, then navigate and reload while its simulated bootstrap is still pending.
  const response = page.waitForResponse(reply => reply.url().endsWith('/api/connection-mode') && reply.status() === 202);
  await tor.click();
  const pending = await (await response).json();
  assert.equal(pending.phase, 'applying');
  await page.locator('#headerNetworkStatus').filter({hasText:/Iniciando Tor.*30%/}).waitFor();
  await page.locator('.nav-link[data-page=chat]').click();
  assert.equal(await page.locator('#view-chat').isVisible(), true);
  assert.equal(await page.evaluate(() => QP.getStatus(true).then(s => s.connectionMode.id)), pending.id);
  await page.reload();
  await page.waitForFunction(id => QP.state.status?.connectionMode?.id === id && QP.state.status.connectionMode.phase === 'applying', pending.id, {timeout:2500});
  assert.equal(await wan.isEnabled(), true);
  await wan.click();
  await page.waitForFunction(() => document.documentElement.dataset.connectionMode === 'wan', {timeout:2000});
  assert.equal(await page.evaluate(() => QP.state.status.tor.isStarted), false);

  await page.locator('.nav-link[data-page=dashboard]').click();
  await enableTor();
  await page.waitForFunction(()=>document.documentElement.dataset.connectionMode==='tor');
  assert.equal(await page.evaluate(()=>QP.state.status.wan.desiredEnabled),false);
  const purple=await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--accent').trim());
  await page.evaluate(() => QP.navigate('/settings.html?section=connection'));
  await page.locator('#view-settings button[data-connection-mode=both]').click();
  await page.waitForFunction(()=>document.documentElement.dataset.connectionMode==='both');
  assert.equal(await page.evaluate(()=>QP.state.status.wan.isStarted&&QP.state.status.tor.isStarted),true);
  await wan.click();await page.waitForFunction(()=>document.documentElement.dataset.connectionMode==='wan');
  assert.notEqual(await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()),purple);
  await page.locator('.nav-link[data-page=dashboard]').click();
  // Token connection remains visible and usable with automatic discovery disabled.
  await page.evaluate(()=>{const s=structuredClone(QP.state.status);s.discovery={enabled:false,running:false};QP.state.status=s;QP.emit('status_updated',s);});
  await page.getByRole('button',{name:'Conectar por código',exact:true}).click();
  let requests=0;
  await page.route('**/api/connect-token',async route=>{requests++;await route.fulfill({status:requests===1?409:200,contentType:'application/json',body:JSON.stringify(requests===1?{success:false,error:'Este código utiliza Tor.',requiredTransport:'tor'}:{success:true})});});
  await page.locator('#tokenInput').fill('fixture-tor-token');await page.locator('#connectTokenBtn').click();
  await page.locator('#activateTokenTransport').waitFor({state:'visible'});
  assert.equal(await page.locator('#tokenInput').inputValue(),'fixture-tor-token');
  await page.locator('#activateTokenTransport').click();
  await page.waitForFunction(()=>document.querySelector('#tokenInput').value==='');
  assert.equal(requests,2);assert.equal(await page.locator('html').getAttribute('data-connection-mode'),'tor');
  await page.locator('#closeAddContact').click();
  await page.evaluate(()=>{
    const base={isSaved:true,isTrusted:true,networkType:'Tor',isTor:true};
    const peer=(id,extra)=>({...base,id,certHash:id,canonicalId:id,name:id,...extra});
    window.groupSnapshot={...QP.state.status,savedPeers:[],discoveredPeers:[],peers:[
      peer('Offline reciente',{lastUsedUtc:'2026-10-18T18:00:00Z'}),
      peer('Conectando',{connectionState:'connecting',lastUsedUtc:'2026-10-18T17:00:00Z'}),
      peer('Conectado antiguo',{isQuicConnected:true,lastUsedUtc:'2026-10-16T12:00:00Z'}),
      peer('Conectado reciente',{isQuicConnected:true,lastUsedUtc:'2026-10-18T12:00:00Z'}),
      peer('Offline antiguo',{lastUsedUtc:'2026-10-15T12:00:00Z'})]};
    QP.state.status=groupSnapshot;QP.emit('status_updated',groupSnapshot);
  });
  await page.getByText('Filtros y conexión', {exact:true}).click();
  await page.locator('#contactSort').selectOption('activity');
  assert.deepEqual(await page.locator('.contact-state-group').evaluateAll(g=>g.map(n=>n.dataset.contactState)),['connected','connecting','disconnected']);
  assert.deepEqual(await page.locator('#homeContacts .contact-card').evaluateAll(g=>g.map(n=>n.dataset.contactKey)),['Conectado reciente','Conectado antiguo','Conectando','Offline reciente','Offline antiguo']);
  assert.equal(await page.locator('#homeContacts').evaluate(n=>getComputedStyle(n).display),'block');
  await page.locator('.toast').evaluateAll(nodes=>nodes.forEach(n=>n.remove()));
  for(const width of [2560,1440,768,390,320]) {
    await page.setViewportSize({width,height:900});
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Compact list overflows at '+width);
    await page.screenshot({path:'.artifacts/contact-list-tor-'+width+'.png',fullPage:true});
  }
  await page.setViewportSize({width:1440,height:900});
  await page.goto(url + 'settings.html?section=connection');
  await page.waitForFunction(()=>QP.state.status && QP.isWsConnected);
  const settingsMode = page.getByRole('tabpanel', {name:'Conexión y Tor'}).getByRole('button', {name:'Solo WAN',exact:true});
  await settingsMode.click();
  await page.waitForFunction(()=>document.querySelector('#settingsConnectionModeStatus').textContent.includes('directas'));
  assert.equal(await settingsMode.getAttribute('aria-pressed'),'true');
  assert.deepEqual(errors,[]);

  const phone = await browser.newPage({viewport:{width:320,height:568},isMobile:true,hasTouch:true});
  await phone.goto(url + 'chat.html');
  await phone.waitForFunction(() => QP.isWsConnected && QP.state.status?.wan?.isStarted);
  const header = phone.locator('#headerNetworkSwitch');
  const mobileWan = header.getByRole('button', {name:'Solo WAN',exact:true});
  const mobileTor = header.getByRole('button', {name:'Solo Tor',exact:true});
  const mobileStatus = phone.locator('#headerNetworkStatus');
  const confirmTor = phone.getByRole('dialog', {name:'¿Cambiar a Tor?'});
  const posts = [];
  let failRequest;
  const firstRequestHeld = new Promise(resolve => { failRequest = resolve; });
  await phone.route('**/api/connection-mode', async route => {
    posts.push(route.request().postDataJSON().mode);
    if (posts.length === 1) {
      await firstRequestHeld;
      await route.fulfill({status:503,json:{success:false,error:'Tor no disponible en esta prueba móvil'}});
    } else {
      await route.continue();
    }
  });
  assert.equal(await mobileWan.getAttribute('aria-pressed'), 'true');
  await mobileWan.tap();
  await mobileTor.tap();
  await confirmTor.getByRole('button', {name:'Cancelar'}).tap();
  await confirmTor.waitFor({state:'hidden'});
  assert.deepEqual(posts, [], 'Selecting the current mode or cancelling must leave the connection alone');

  await mobileTor.tap();
  await confirmTor.getByRole('button', {name:'Cambiar a Tor'}).tap();
  await mobileStatus.filter({hasText:/Deteniendo WAN|Iniciando Tor/}).waitFor();
  assert.equal(await mobileWan.isEnabled(), true);
  assert.equal(await mobileTor.getAttribute('aria-pressed'), 'false');
  failRequest();
  await mobileStatus.filter({hasText:/No se pudo completar el cambio/}).waitFor();
  await header.locator('button[data-connection-mode=wan][aria-pressed=true]').waitFor();
  assert.deepEqual(posts, ['tor'], 'A failed acknowledgement must not change modes');

  await mobileTor.tap();
  await confirmTor.getByRole('button', {name:'Cambiar a Tor'}).tap();
  await header.locator('button[data-connection-mode=tor][aria-pressed=true]').waitFor();

  // The live transport determines which peers are affected, independent of their stored network type.
  await phone.evaluate(() => {
    const status = structuredClone(QP.state.status);
    status.peers.find(peer => peer.isQuicConnected).activeTransport = 'tor';
    QP.emit('status_updated', status);
  });
  await mobileWan.tap();
  const confirmWan = phone.getByRole('dialog', {name:'¿Cambiar a WAN?'});
  assert.match(await confirmWan.innerText(), /conexiones activas por Tor/);
  await confirmWan.getByRole('button', {name:'Cancelar'}).tap();
  await confirmWan.waitFor({state:'hidden'});
  await phone.evaluate(() => QP.getStatus(true));

  await mobileWan.tap();
  await header.locator('button[data-connection-mode=wan][aria-pressed=true]').waitFor();
  assert.deepEqual(posts, ['tor','tor','wan'], 'Switching from Tor without Tor connections takes one tap');
  console.log('PASS connection modes: exclusive/both states, Tor failure, token activation, contact sorting, mobile confirmation, progress, retry and one-tap switching');
} finally {await closeBrowserFixture(browser, server);}
