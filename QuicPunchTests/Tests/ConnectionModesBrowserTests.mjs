import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';
const server = startPreviewServer(['--mode-fail-first-tor']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await page.waitForFunction(()=>QP.state.status&&QP.isWsConnected);
  const tor=page.locator('button[data-connection-mode=tor]'),wan=page.locator('button[data-connection-mode=wan]'),both=page.locator('button[data-connection-mode=both]');
  await tor.click();
  await page.waitForFunction(()=>document.querySelector('#connectionModeStatus').textContent.includes('no disponible'));
  assert.equal(await page.evaluate(()=>QP.state.status.wan.isStarted),false,'Failed Tor activation restored WAN');
  assert.notEqual(await page.locator('html').getAttribute('data-connection-mode'),'tor','Failure applied a misleading Tor palette');
  await page.waitForFunction(()=>!document.querySelector('button[data-connection-mode=tor]').disabled);
  await tor.click();
  await page.waitForFunction(()=>document.documentElement.dataset.connectionMode==='tor');
  assert.equal(await page.evaluate(()=>QP.state.status.wan.desiredEnabled),false);
  const purple=await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--accent').trim());
  await both.click();await page.waitForFunction(()=>document.documentElement.dataset.connectionMode==='both');
  assert.equal(await page.evaluate(()=>QP.state.status.wan.isStarted&&QP.state.status.tor.isStarted),true);
  await wan.click();await page.waitForFunction(()=>document.documentElement.dataset.connectionMode==='wan');
  assert.notEqual(await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--accent').trim()),purple);
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
  assert.deepEqual(errors,[]);
  console.log('PASS connection modes: failed Tor stays offline, exclusive/both states, real-state palette, token without discovery, explicit transport activation, grouped activity sorting and compact list at 320–2560px');
} finally {await closeBrowserFixture(browser, server);}
