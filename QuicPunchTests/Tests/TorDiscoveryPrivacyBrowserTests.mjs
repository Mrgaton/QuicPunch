import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import {mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1280,height:900}}), requests = [], errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let enabled = false, wan = true, rejectEnable = false;
  const patch = status => {
    status.tor = {...status.tor, desiredEnabled:true, isStarted:true, running:true};
    status.discovery = {...status.discovery, enabled:wan, desiredEnabled:wan,
      wan:{enabled:wan, desiredEnabled:wan, running:wan, serviceRunning:true},
      tor:{enabled, desiredEnabled:enabled, running:enabled, serviceRunning:true}};
    return status;
  };
  await page.routeWebSocket('**/ws?*', socket => {
    const backend = socket.connectToServer(); socket.onMessage(message => backend.send(message));
    backend.onMessage(message => {
      if (typeof message !== 'string') return socket.send(message);
      const payload = JSON.parse(message);
      if ((payload.type === 'status_updated' || payload.type === 'response') && payload.data?.node) payload.data = patch(payload.data);
      socket.send(JSON.stringify(payload));
    });
  });
  await page.route('**/api/discovery', async route => {
    const request = route.request().postDataJSON(); requests.push(request);
    if (request.type === 'tor' && request.enabled) {
      assert.equal(request.acknowledgeTorPrivacyRisk, true);
      if (rejectEnable) return route.fulfill({status:503,json:{success:false,error:'No se pudo iniciar el descubrimiento de prueba'}});
    }
    if (request.type === 'tor') enabled = request.enabled; else wan = request.enabled;
    await route.fulfill({json:{success:true,type:request.type,enabled:request.enabled}});
  });
  await page.goto(url); await page.waitForFunction(() => QP.state.status && QP.isWsConnected);
  await page.evaluate(() => QP.navigate('settings')); await page.locator('#settings-tab-connection').click();
  const toggle = page.locator('#torDiscoveryToggle'), dialog = page.locator('dialog.tor-discovery-warning');
  const activation = dialog.getByRole('button', {name:'Activar descubrimiento Tor', exact:true});
  assert.equal(await toggle.isChecked(), false);
  await toggle.click(); await dialog.waitFor({state:'visible'});
  assert.equal(await toggle.isChecked(), false);
  assert.equal(await activation.isDisabled(), true);
  assert.equal(await dialog.getByRole('button', {name:'Mantener desactivado', exact:true}).evaluate(node => node === document.activeElement), true);
  assert.match(await dialog.textContent(), /\.onion.*WAN.*IP pública.*claves distintas.*conservar los anuncios/s);
  assert.equal(requests.length, 0, 'Warning must precede the enabling request');
  mkdirSync('.artifacts', {recursive:true}); await dialog.screenshot({path:'.artifacts/tor-discovery-warning.png'});
  await dialog.getByRole('button', {name:'Mantener desactivado', exact:true}).click();
  await page.waitForFunction(() => !document.querySelector('#torDiscoveryToggle').disabled);
  assert.equal(requests.length, 0);
  await toggle.click(); await dialog.getByRole('checkbox').check();
  assert.equal(await activation.isDisabled(), false);
  await dialog.getByRole('checkbox').uncheck(); assert.equal(await activation.isDisabled(), true);
  await dialog.getByRole('checkbox').check(); await activation.click();
  await page.waitForFunction(() => document.querySelector('#torDiscoveryToggle').checked && !document.querySelector('#torDiscoveryToggle').disabled);
  assert.equal(requests.length, 1);
  await toggle.uncheck(); await page.waitForFunction(() => !document.querySelector('#torDiscoveryToggle').checked && !document.querySelector('#torDiscoveryToggle').disabled);
  assert.equal(await dialog.count(), 0);
  assert.deepEqual(requests[1], {enabled:false,type:'tor'});
  await toggle.click(); assert.equal(await dialog.getByRole('checkbox').isChecked(), false);
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !document.querySelector('#torDiscoveryToggle').disabled);
  assert.equal(requests.length, 2);
  rejectEnable = true;
  await toggle.click(); await dialog.getByRole('checkbox').check(); await activation.click();
  await page.waitForFunction(() => !document.querySelector('#torDiscoveryToggle').disabled);
  assert.equal(await toggle.isChecked(), false, 'Rejected activation left the UI enabled');
  await page.locator('#discoveryToggle').uncheck();
  await page.waitForFunction(() => !document.querySelector('#discoveryToggle').disabled);
  assert.deepEqual(requests.at(-1), {enabled:false,type:'wan'});
  for (const width of [390,320]) {
    await page.setViewportSize({width,height:640});
    await toggle.click(); await dialog.waitFor({state:'visible'});
    assert.ok(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth), 'Privacy warning overflows at ' + width);
    const box = await dialog.boundingBox(); assert.ok(box.x >= 0 && box.y >= 0 && box.x + box.width <= width && box.y + box.height <= 640);
    await page.keyboard.press('Escape'); await page.waitForFunction(() => !document.querySelector('#torDiscoveryToggle').disabled);
  }
  assert.deepEqual(errors, []);
  console.log('PASS Tor discovery privacy: off by default, warning before requests, explicit acknowledgement, cancel/Escape, repeated opt-in, failed activation, immediate disable, independent WAN and mobile layout');
} finally {await closeBrowserFixture(browser, server);}
