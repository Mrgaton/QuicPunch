import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';

const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport: {width: 1280, height: 900}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let fixtureActive = false;
  await page.routeWebSocket('**/ws?*', socket => {
    const backend = socket.connectToServer();
    socket.onMessage(message => backend.send(message));
    backend.onMessage(message => {
      if (!fixtureActive || typeof message !== 'string' || JSON.parse(message).type !== 'status_updated') socket.send(message);
    });
  });
  await page.goto(url);
  await page.waitForFunction(() => QP.state.status && QP.isWsConnected);
  fixtureActive = true;
  await page.evaluate(() => {
    const peer = (id, extra = {}) => ({id, certHash: id, name: id, isSaved: true, isTrusted: true, isQuicConnected: false, networkType: 'Wan', discoveryAvailableUntilUtc: null, ...extra});
    const future = new Date(Date.now() + 60000).toISOString();
    const past = new Date(Date.now() - 60000).toISOString();
    window.availabilityFixture = {...QP.state.status, activeInterrogations: [],
      savedPeers: [peer('Reciente'), peer('Antiguo', {discoveryAvailableUntilUtc: past}), peer('Conectado')],
      discoveredPeers: [peer('Reciente', {discoveryAvailableUntilUtc: future})],
      peers: [peer('Reciente'), peer('Conectado', {isQuicConnected: true, discoveryAvailableUntilUtc: past})]};
    window.pushAvailability = () => {QP.state.status = availabilityFixture; QP.emit('status_updated', availabilityFixture);};
    pushAvailability();
  });
  const groupFor = id => page.locator(`[data-contact-key="${id}"]`).locator('..').locator('..').getAttribute('data-contact-state');
  assert.equal(await groupFor('Reciente'), 'available');
  assert.equal(await groupFor('Antiguo'), 'disconnected');
  assert.equal(await groupFor('Conectado'), 'connected');
  assert.equal(await page.locator('[data-contact-key="Reciente"] .ci [data-user-presence]').count(), 0, 'Availability must not modify the certificate capsule');
  assert.equal(await page.locator('[data-contact-key="Reciente"] .contact-card-connection .badge').textContent(), 'Disponible');
  assert.equal(await page.locator('#homeConnectedFilterCount').textContent(), 'Conectados (1)');
  assert.equal(await page.locator('#homeAvailableFilterCount').textContent(), 'Disponibles (1)');
  await page.locator('.contact-options > summary').click();
  await page.selectOption('#contactConnectionFilter', 'available');
  assert.deepEqual(await page.locator('.contact-card').evaluateAll(cards => cards.map(card => card.dataset.contactKey)), ['Reciente']);
  await page.selectOption('#contactConnectionFilter', 'disconnected');
  assert.deepEqual(await page.locator('.contact-card').evaluateAll(cards => cards.map(card => card.dataset.contactKey)), ['Antiguo']);
  await page.selectOption('#contactConnectionFilter', 'all');
  await page.evaluate(() => {
    availabilityFixture.discoveredPeers[0].discoveryAvailableUntilUtc = new Date(Date.now() + 800).toISOString();
    pushAvailability();
  });
  await page.waitForFunction(() => document.querySelector('[data-contact-key="Reciente"]')?.closest('[data-contact-state]')?.dataset.contactState === 'disconnected');
  assert.equal(await page.locator('[data-contact-key="Reciente"] .contact-card-connection .badge').textContent(), 'Sin conexión');
  assert.equal(await page.locator('#homeAvailableFilterCount').textContent(), 'Disponibles (0)');
  await page.evaluate(() => {
    availabilityFixture.discoveredPeers[0].discoveryAvailableUntilUtc = new Date(Date.now() + 60000).toISOString();
    availabilityFixture.activeInterrogations = [{id: 'attempt', certHash: 'Reciente'}];
    pushAvailability();
  });
  assert.equal(await groupFor('Reciente'), 'connecting');
  assert.equal(await page.locator('[data-contact-key="Reciente"] .ci [data-user-presence]').count(), 0, 'Availability must not modify the certificate capsule');
  assert.deepEqual(errors, []);
  console.log('PASS Nostr availability: saved/discovered/live merge, available group/filter/count, original expiry without refresh, offline indicator, real connection precedence');
} finally {await closeBrowserFixture(browser, server);}
