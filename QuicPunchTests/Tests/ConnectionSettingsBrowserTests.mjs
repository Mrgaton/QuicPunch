import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import {mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready; browser = await launchBrowser();
  mkdirSync('.artifacts/connection-settings', {recursive:true});
  const page = await browser.newPage({viewport:{width:1920,height:1080}}), errors = [], calls = [];
  page.on('pageerror', error => errors.push(error.message));
  let port = 55222, rejectPort = false, releaseStart;
  let tor = {desiredEnabled:false,isStarted:false}, wan = {desiredEnabled:true,isStarted:true};
  const patch = status => ({...status, node:{...status.node,name:'Mi ordenador',networkType:'DynamicAddress',listenerPort:port,
    wanToken:'QP://'+('INVITACION-DE-PRUEBA-'.repeat(15)),torToken:tor.isStarted?'QP://TOR-PRUEBA':''},wan,tor});
  await page.routeWebSocket('**/ws?*', socket => {
    const backend = socket.connectToServer(); socket.onMessage(message=>backend.send(message));
    backend.onMessage(message=>{
      if(typeof message!=='string') return socket.send(message);
      const payload=JSON.parse(message);
      if(payload.data?.node) payload.data=patch(payload.data);
      socket.send(JSON.stringify(payload));
    });
  });
  await page.route('**/api/wan-stop', route=>{calls.push('wan-stop');return route.fulfill({status:503,json:{error:'Prueba: no se pudo detener'}});});
  await page.route('**/api/tor-start', async route=>{
    calls.push('tor-start'); await new Promise(resolve=>releaseStart=resolve);
    tor={desiredEnabled:true,isStarted:false,bootstrapProgress:35};
    return route.fulfill({json:{success:true}});
  });
  await page.route('**/api/change-listener-port', route=>{
    calls.push('port');
    if(rejectPort) return route.fulfill({status:409,json:{error:'Puerto ocupado'}});
    port=route.request().postDataJSON().port;return route.fulfill({json:{success:true,port}});
  });
  await page.goto(url+'settings.html?section=connection');
  await page.waitForFunction(()=>QP.state.status&&QP.isWsConnected);
  assert.equal(await page.locator('#wanToggle,#torToggle').count(),0,'Duplicated service controls remain');
  assert.equal(await page.locator('#wanConnectionDetails').getAttribute('open'),null);
  assert.equal(await page.locator('#copyTorInline').isDisabled(),true);
  assert.equal(await page.locator('#torDiscoveryToggle').isDisabled(),true);
  await page.evaluate(()=>Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async()=>{throw Error('Clipboard unavailable');}}}));
  await page.locator('#copyWanInline').click();
  assert.equal(await page.locator('#wanTokenBox').isVisible(),true);
  await page.evaluate(async()=>{await refreshDashboard();});
  assert.equal(await page.evaluate(()=>getSelection().toString()),'QP://'+'INVITACION-DE-PRUEBA-'.repeat(15));
  await page.locator('#wanConnectionCard .connection-token summary').click();
  await page.locator('#homeWanToggleBtn').click();
  await page.waitForFunction(()=>!document.getElementById('homeWanToggleBtn').disabled);
  assert.equal(await page.locator('#homeWanToggleBtn').getAttribute('aria-pressed'),'true','Failure changed confirmed state');
  await page.locator('#homeTorToggleBtn').click();
  assert.equal(await page.locator('#homeTorToggleBtn').isDisabled(),true);
  assert.equal(await page.locator('#torDiscoveryToggle').isDisabled(),true);
  releaseStart();
  await page.waitForFunction(()=>document.getElementById('homeTorServiceStatus').textContent.includes('35%'));
  assert.equal(await page.locator('#copyTorInline').isDisabled(),true);
  assert.equal(calls.filter(value=>value==='tor-start').length,1);
  tor={desiredEnabled:true,isStarted:false,lastError:'No se pudo iniciar Tor'};
  await page.evaluate(async()=>{await refreshDashboard();});
  assert.equal(await page.locator('#homeTorServiceStatus').getAttribute('data-state'),'error');
  assert.equal(await page.locator('#torErrorLogs').isVisible(),true);
  tor={desiredEnabled:false,isStarted:false}; await page.evaluate(async()=>{await refreshDashboard();});
  await page.locator('#wanConnectionDetails summary').click();
  await page.locator('#listenerPortInput').fill('60000');
  await page.evaluate(async()=>{await refreshDashboard();});
  assert.equal(await page.locator('#listenerPortInput').inputValue(),'60000','Status update discarded port draft');
  rejectPort=true;await page.locator('#applyPortBtn').click();
  await page.locator('#listenerPortFeedback').filter({hasText:'Puerto ocupado'}).waitFor();
  assert.equal(await page.locator('#listenerPortInput').inputValue(),'60000');
  rejectPort=false;await page.locator('#applyPortBtn').click();
  await page.locator('#listenerPortFeedback').filter({hasText:'cambiado a 60000'}).waitFor();
  assert.equal(port,60000);
  await page.locator('#listenerPortInput').fill('70000');await page.locator('#applyPortBtn').click();
  assert.equal(await page.locator('#listenerPortInput').getAttribute('aria-invalid'),'true');
  await page.locator('#discardPortBtn').click();
  assert.equal(await page.locator('#listenerPortInput').inputValue(),'60000');
  await page.locator('#wanConnectionDetails summary').click();
  await page.locator('.toast').evaluateAll(nodes=>nodes.forEach(node=>node.remove()));
  for(const width of [1920,1280,844,390,320]) {
    await page.setViewportSize({width,height:1080});await page.evaluate(()=>scrollTo(0,0));
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Overflow at '+width);
    await page.screenshot({path:`.artifacts/connection-settings/connection-${width}.png`,fullPage:true});
    await page.locator('#wanConnectionCard .connection-token summary').click();
    await page.locator('#wanConnectionDetails summary').click();
    await page.locator('#torConnectionDetails summary').click();
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Expanded content overflows at '+width);
    await page.locator('#wanConnectionCard .connection-token summary').click();
    await page.locator('#wanConnectionDetails summary').click();
    await page.locator('#torConnectionDetails summary').click();
  }
  assert.deepEqual(errors,[]);
  console.log('PASS connection settings: single controls, clipboard fallback, pending/failed service, Tor progress, port draft/retry/validation and 320–1920px layouts');
} finally {await closeBrowserFixture(browser,server);}
