import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real DOM regression: npm install playwright, then QUICNET_PLAYWRIGHT_MODULE may override its module path.

import assert from 'node:assert/strict';
const server = startPreviewServer(['--vpn-no-admin', '--vpn-legacy', '--vpn-reject-first']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1100,height:850}});
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  let prompts = 0; page.on('dialog', async dialog => { prompts++; await dialog.dismiss(); });
  await page.goto(url + 'vpn.html');
  assert.equal(await page.locator('#vpnMode, #vpnAutoConnect').count(), 0, 'Mode and startup choices are per contact');
  await page.getByRole('button', {name:'Configurar VPN'}).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.waitFor({state:'visible'});
  assert.match(await dialog.innerText(), /sin permisos de administrador/);
  await dialog.getByLabel('Conexión con este contacto').selectOption('maintain');
  if (process.env.QUICNET_VPN_SCREENSHOT) await page.screenshot({path:process.env.QUICNET_VPN_SCREENSHOT});
  await dialog.getByRole('button', {name:'Guardar',exact:true}).click();
  await dialog.getByRole('alert').filter({hasText:'Fallo de escritura simulado'}).waitFor();
  assert.equal(await dialog.isVisible(), true, 'rejected save keeps the dialog open');
  await dialog.getByRole('button', {name:'Guardar',exact:true}).click();
  await dialog.waitFor({state:'detached'});
  let response = await (await page.request.get(url+'api/vpn/config')).json();
  assert.equal(response.configuration.Peers.length, 1, 'retry does not append duplicate peers');
  assert.equal(response.configuration.Peers[0].CertHash, '01'.repeat(32));
  assert.equal(response.configuration.Peers[0].AutoConnect, true);
  assert.equal(response.configuration.Peers[0].AutoAccept, true);
  assert.equal(response.configuration.Peers[0].Mode, 'simple');
  assert.equal(response.configuration.Enabled, true, 'maintain mode enables the network persistently');
  await page.reload();
  await page.getByRole('button', {name:'Configurar VPN'}).first().click();
  await dialog.waitFor({state:'visible'});
  assert.equal(await dialog.getByLabel('Conexión con este contacto').inputValue(), 'maintain', 'Base64 peer finds its saved hexadecimal policy');
  await dialog.getByLabel('Modo de red de este contacto').selectOption('advanced');
  await dialog.getByLabel('Destinos de mi tráfico que van hacia este contacto').fill('192.168.50.0/24');
  await dialog.getByRole('button', {name:'Guardar',exact:true}).click();
  await dialog.waitFor({state:'detached'});
  await page.locator('#vpnPeers .peer-row').filter({hasText:'Mi ordenador'}).getByRole('button', {name:'Configurar VPN'}).click();
  await dialog.getByLabel('Conexión con este contacto').selectOption('manual');
  await dialog.getByRole('button', {name:'Guardar',exact:true}).click();
  await dialog.waitFor({state:'detached'});
  response = await (await page.request.get(url+'api/vpn/config')).json();
  const advanced = response.configuration.Peers.find(peer => peer.CertHash === '01'.repeat(32));
  const simple = response.configuration.Peers.find(peer => peer.CertHash === '02'.repeat(32));
  assert.equal(advanced.Mode, 'advanced'); assert.equal(advanced.AutoConnect, true);
  assert.deepEqual(advanced.Routes, ['192.168.50.0/24']);
  assert.equal(simple.Mode, 'simple'); assert.equal(simple.AutoConnect, false);
  await page.reload();
  await page.locator('#vpnPeers .peer-row').filter({hasText:'Ana'}).getByRole('button', {name:'Configurar VPN'}).click();
  assert.equal(await dialog.getByLabel('Modo de red de este contacto').inputValue(), 'advanced');
  assert.equal(await dialog.getByLabel('Destinos de mi tráfico que van hacia este contacto').inputValue(), '192.168.50.0/24');
  for (const width of [390, 320]) {
    await page.setViewportSize({width,height:844});
    const bounds = await dialog.boundingBox();
    assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width, 'Peer settings fit on a phone');
    await dialog.getByLabel('Modo de red de este contacto').selectOption('simple');
    assert.equal(await dialog.locator('.vpn-peer-advanced').isVisible(), false);
    await dialog.getByLabel('Modo de red de este contacto').selectOption('advanced');
    assert.equal(await dialog.getByLabel('Destinos de mi tráfico que van hacia este contacto').inputValue(), '192.168.50.0/24', 'Switching modes retains advanced fields');
  }
  await dialog.getByRole('button', {name:'Cancelar',exact:true}).click();
  await page.locator('#lanEnabled').uncheck();
  await page.waitForFunction(() => document.getElementById('lanLiveBadge').textContent !== 'Deteniendo...');
  await page.locator('#lanEnabled').check();
  await page.waitForFunction(() => document.getElementById('lanSwitchHint').textContent.includes('esperando permisos'));
  await page.evaluate(() => {
    QP.emit('ws_status', {connected:false}); QP.emit('ws_status', {connected:true});
    const original = Date.now, start = original() - 30000;
    let rx = 0, tx = 0;
    try {
      for (let i = 0; i <= 30; i++) {
        rx += i === 30 ? 4096 : Math.round(4096 * (1.1 + Math.sin(i * .5)));
        tx += i === 30 ? 2048 : Math.round(2048 * (1.1 + Math.cos(i * .7)));
        Date.now = () => start + i * 1000;
        QP.emit('status_updated', {...QP.state.status, lan:{...QP.state.status.lan, active:true, totalRxBytes:rx, totalTxBytes:tx}});
      }
    } finally { Date.now = original; }
  });
  await page.waitForFunction(() => document.getElementById('vpnRxRate').textContent === '4.0 KB/s');
  assert.equal(await page.locator('#vpnTxRate').textContent(), '2.0 KB/s');
  assert.ok((await page.locator('#vpnTrafficChart .vpn-traffic-line').first().getAttribute('d')).includes('L'), 'Counters draw real RX/TX history');
  for (const range of ['30','60','300']) {
    await page.locator('#vpnTrafficRange').selectOption(range);
    await page.waitForFunction(range => localStorage.getItem('quicnet.vpnTrafficRange') === range, range);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Traffic card does not overflow on mobile');
  }
  await page.locator('#vpnTrafficRange').selectOption('30');
  if (process.env.QUICNET_VPN_SCREENSHOT) {
    await page.evaluate(() => { document.querySelector('.toast-host')?.replaceChildren(); document.activeElement?.blur(); window.scrollTo(0,0); });
    await page.setViewportSize({width:1280,height:900});
    await page.screenshot({path:process.env.QUICNET_VPN_SCREENSHOT.replace('.png','-desktop.png')});
    await page.setViewportSize({width:390,height:844});
    await page.screenshot({path:process.env.QUICNET_VPN_SCREENSHOT,fullPage:true});
  }
  await page.reload();
  assert.equal(await page.locator('#vpnTrafficRange').inputValue(), '30', 'The chart interval survives reopening');
  assert.equal(prompts, 0, 'saving desired activation never forces an elevation prompt');
  assert.deepEqual(errors, [], 'actual application scripts produced no uncaught errors');
  console.log('PASS VPN browser: mixed peer modes, independent startup, legacy casing, failed-save retry, responsive editor and live RX/TX graph with persistent interval');
} finally {
  await closeBrowserFixture(browser, server);
}
