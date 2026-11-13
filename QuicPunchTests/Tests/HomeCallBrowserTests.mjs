import assert from 'node:assert/strict';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(url);
  await page.waitForFunction(() => QP.state.status && QP.isWsConnected);
  const peerId = '10000000-0000-0000-0000-000000000001';
  await page.evaluate(peerId => {
    const peer = {id: peerId, name: 'Contacto de prueba', certHash: btoa('a'.repeat(32)),
      isSaved: true, isTrusted: true, isQuicConnected: true, connectionState: 'connected'};
    const status = {...QP.state.status, peers: [peer], savedPeers: [peer],
      discoveredPeers: [], activeVoiceCalls: []};
    window.callRequests = [];
    const api = QP.api;
    QP.api = async (path, options) => {
      if (path === '/api/connect-peer' || path === '/api/voice-hangup') {
        callRequests.push({path, ...JSON.parse(options.body)});
        return {ok: true};
      }
      if (path === '/api/voice/state') return {activeCalls: [], backendName: 'Native Audio'};
      return api(path, options);
    };
    QP.state.status = status;
    QP.emit('status_updated', status);
  }, peerId);
  // Start on Home without ever opening the calls view first.
  await page.locator('#homeContacts').getByRole('button', {name: 'Llamar: Contacto de prueba', exact: true}).click();
  await page.waitForFunction(() => callRequests.length === 1);
  assert.equal(new URL(page.url()).pathname, '/call.html');
  assert.deepEqual(await page.evaluate(() => callRequests),
    [{path: '/api/connect-peer', peerId, protocol: 'voice'}]);
  await page.evaluate(peerId => {
    QP.navigate('dashboard');
    QP.emit('focus_voice_peer', {peerId});
    QP.emit('focus_voice_peer', {peerId});
  }, peerId);
  assert.equal(await page.evaluate(() => callRequests.length), 1, 'Pending calls must not be duplicated');
  await page.evaluate(peerId => QP.emit('voice_signal', {peerId, signal: 'call-failed'}), peerId);
  await page.locator('#homeContacts').getByRole('button', {name: 'Llamar: Contacto de prueba', exact: true}).click();
  await page.waitForFunction(() => callRequests.length === 2);
  assert.equal(await page.evaluate(() => callRequests[1].peerId), peerId);
  assert.deepEqual(errors, []);
  console.log('PASS first call from Home, pending-call deduplication and retry after failure');
} finally {
  await closeBrowserFixture(browser, server);
}
