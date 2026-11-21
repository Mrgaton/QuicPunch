import assert from 'node:assert/strict';
import { readVoiceSource } from '../Fixtures/VoiceSource.mjs';
import { closeBrowserFixture, launchBrowser, startPreviewServer } from '../Fixtures/BrowserFixture.mjs';

const peerId = '00000000-0000-0000-0000-000000000001';
const id = '00000000-0000-0000-0000-000000000099';
const server = startPreviewServer([]);
let browser;
try {
  browser = await launchBrowser();
  const page = await browser.newPage({ viewport: { width: 430, height: 860 }, isMobile: true, hasTouch: true });
  const errors = [], posts = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    window.testClipboard = '';
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
      readText: async () => window.testClipboard, writeText: async text => { window.testClipboard = text; }
    } });
  });
  let state = { revision: 0, session: null };
  const setup = `window.showTestScreen = () => {
    const entry = createOrUpdateScreenTile('${peerId}', 'Ana', 1280, 720, 1, 30, false);
    entry.loadingEl.classList.add('hidden');
  };})();`;
  await page.route('**/voice.js', route => route.fulfill({
    contentType: 'text/javascript', body: readVoiceSource().replace(/\}\)\(\);\s*$/, setup)
  }));
  await page.route('**/api/voice/remote-input', async route => {
    if (route.request().method() === 'POST') {
      const data = route.request().postDataJSON();
      posts.push(data);
      if (data.action === 'request' || data.action === 'request-with-capture') state = {
        revision: state.revision + 1, session: { id, peerId, peerName: 'Ana', active: false, incoming: false }
      };
      if (data.action === 'accept') state = { revision: state.revision + 1, session: { ...state.session, active: true } };
      if (data.action === 'clipboard-toggle') state = {
        revision: state.revision + 1, session: { ...state.session, clipboardEnabled: data.enabled }
      };
      if (data.action === 'stop') state = { revision: state.revision + 1, session: null };
    }
    await route.fulfill({ json: state });
  });
  const emit = async session => {
    state = { revision: state.revision + 1, session };
    await page.evaluate(value => QP.emit('remote_input_state', value), state);
  };
  const notice = (incoming, active, administrator = false) => ({ id, peerId, peerName: 'Ana', incoming, active, administrator });
  const inputRequest = async kind => (await page.waitForResponse(response => {
    const request = response.request();
    return request.method() === 'POST' &&
    request.url().endsWith('/api/voice/remote-input') &&
      request.postDataJSON().inputs?.some(event => event.kind === kind);
  })).request().postDataJSON();
  const url = await server.ready;
  await page.goto(url + 'call.html');
  await page.waitForFunction(() => window.showTestScreen);
  const dialog = page.locator('#remoteInputConsent'), allow = page.locator('#remoteInputAccept'), stop = page.locator('#remoteInputStop');

  await emit(notice(true, false));
  assert.equal(await dialog.isVisible(), false);
  await page.locator('#remoteInputReview').click();
  assert.equal(await allow.isDisabled(), true);
  assert.equal(await page.locator('#remoteInputClose').evaluate(button => button === document.activeElement), true);
  assert.deepEqual(posts, []);
  await page.locator('#remoteInputPermission').check();
  await allow.click();
  await dialog.waitFor({ state: 'hidden' });
  assert.deepEqual(posts.at(-1), { action: 'accept', id, confirmed: true });
  assert.match(await page.locator('#remoteInputStatus').textContent(), /Ctrl \+ Alt \+ F12/);
  await stop.click();
  await stop.waitFor({ state: 'hidden' });

  await emit(notice(true, true, true));
  assert.equal(await dialog.isVisible(), false);
  assert.equal(await page.locator('#remoteInputReview').isVisible(), false);
  assert.match(await page.locator('#remoteInputStatus').textContent(), /Ana controla tu equipo/);
  assert.equal(posts.filter(post => post.action === 'accept').length, 1);
  await stop.click();
  await stop.waitFor({ state: 'hidden' });

  await page.waitForFunction(() => QP.state.status);
  await page.evaluate(peerId => {
    QP.state.status.activeVoiceCalls = [{ peerId, peerName: 'Ana' }];
    QP.navigate('voice');
    QP.emit('status_updated', QP.state.status);
  }, peerId);
  await page.locator(`#activeCalls [data-call-peer-id="${peerId}"] summary`).click();
  await page.getByRole('button', { name: 'Solicitar control remoto', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#remoteInputStatus').textContent.includes('Esperando'));
  assert.deepEqual(posts.at(-1), { action: 'request-with-capture', peerId });
  await stop.click();
  await stop.waitFor({ state: 'hidden' });
  await page.locator('.toast').filter({ hasText: 'Solicitando control remoto' }).waitFor({ state: 'hidden', timeout: 7000 });

  await page.evaluate(() => showTestScreen());
  const tile = page.locator('.screen-tile[data-stream-peer-id="' + peerId + '"]'), body = tile.locator('.screen-tile-body');
  await tile.getByRole('button', { name: 'Solicitar control' }).click();
  await tile.getByRole('button', { name: 'Esperando permiso…' }).waitFor();
  assert.equal(posts.some(post => post.action === 'input'), false);
  await emit({ ...notice(false, true), fullKeyboard: true, clipboardAllowed: true, monitors: [
    { name: 'Pantalla 1', x: 0, y: 0, width: 0.5, height: 1 },
    { name: 'Pantalla 2', x: 0.5, y: 0, width: 0.5, height: 1 }
  ] });
  await tile.getByRole('button', { name: 'Controlar', exact: true }).click();
  await tile.getByRole('button', { name: 'Vista y zoom' }).waitFor();
  const downRequest = inputRequest('down'), clickRequest = inputRequest('up');
  await body.tap();
  const click = await clickRequest;
  const up = click.inputs.find(event => event.kind === 'up');
  assert.ok(Math.abs(up.x - 0.5) < 0.02 && Math.abs(up.y - 0.5) < 0.02, 'Touch must be normalized to the displayed video');
  assert.equal(up.button, 0);
  assert.equal((await downRequest).inputs.find(event => event.kind === 'down').button, 0);
  await tile.getByRole('button', { name: 'Clic derecho', exact: true }).tap();
  const rightRequest = inputRequest('down');
  await body.tap();
  assert.equal((await rightRequest).inputs.find(event => event.kind === 'down').button, 2);
  const keyRequest = inputRequest('text');
  await body.press('a');
  assert.equal((await keyRequest).inputs.find(event => event.kind === 'text').text, 'a');
  await tile.getByRole('button', { name: 'Teclado', exact: true }).click();
  const textRequest = inputRequest('text');
  await tile.getByRole('textbox', { name: 'Escribir en el equipo remoto' }).fill('¡Hola! ☃');
  assert.equal((await textRequest).inputs.find(event => event.kind === 'text').text, '¡Hola! ☃');
  await tile.getByRole('combobox', { name: 'Pantalla remota' }).selectOption({ label: 'Pantalla 2' });
  const selectedMonitor = inputRequest('up');
  await body.tap();
  const monitorPoint = (await selectedMonitor).inputs.find(event => event.kind === 'up');
  assert.ok(Math.abs(monitorPoint.x - 0.75) < 0.02, 'The selected monitor must map into the original desktop');

  await tile.getByRole('button', { name: 'Teclado completo', exact: true }).click();
  const physicalDown = inputRequest('keydown'), physicalUp = inputRequest('keyup');
  await page.keyboard.press('Control+a');
  assert.ok((await physicalDown).inputs.some(event => event.kind === 'keydown' && event.key === 'ControlLeft'));
  assert.ok((await physicalUp).inputs.some(event => event.kind === 'keyup' && event.key === 'KeyA'));
  await tile.getByRole('button', { name: 'Portapapeles', exact: true }).click();
  await page.evaluate(() => { window.testClipboard = 'texto local'; });
  const clipboardSent = page.waitForResponse(response => response.request().method() === 'POST' &&
    response.url().endsWith('/api/voice/remote-input') && response.request().postDataJSON().action === 'clipboard');
  await tile.getByRole('button', { name: 'Enviar texto copiado' }).click();
  assert.equal((await clipboardSent).request().postDataJSON().text, 'texto local');
  await page.evaluate(requestId => QP.emit('remote_input_clipboard', { id: requestId, text: 'texto remoto' }), id);
  await page.waitForFunction(() => window.testClipboard === 'texto remoto');

  await emit(null);
  await tile.getByRole('button', { name: 'Vista y zoom' }).waitFor({ state: 'hidden' });
  const inputCount = posts.filter(post => post.action === 'input').length;
  await body.tap();
  await page.waitForTimeout(100);
  assert.equal(posts.filter(post => post.action === 'input').length, inputCount, 'Revocation must stop sending input');
  assert.deepEqual(errors, []);
  console.log('PASS remote-input consent, automatic administrator grant, call action, tap projection, mobile keyboard, revocation');
} finally {
  await closeBrowserFixture(browser, server);
}
