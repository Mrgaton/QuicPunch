import assert from 'node:assert/strict';
import {mkdir} from 'node:fs/promises';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

// Overview actions, focus during status updates and long identities use fixture data only.
const server = startPreviewServer();
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1920,height:1080}}), errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let frozen = false;
  await page.routeWebSocket('**/ws?*', socket => {
    const backend = socket.connectToServer();
    socket.onMessage(message => backend.send(message));
    backend.onMessage(message => {
      if (!frozen || typeof message !== 'string' || JSON.parse(message).type !== 'status_updated') socket.send(message);
    });
  });
  await page.goto(url + 'chat.html');
  await page.locator('.chat-idle').waitFor();
  await page.waitForFunction(() => QP.state.status && QP.isWsConnected);
  frozen = true;
  await page.evaluate(() => {
    const snapshot = structuredClone(QP.state.status);
    const names = ['Ana · Portátil', 'Mi ordenador', 'Lucía', 'Marcos · Estudio', 'Equipo de diseño', 'Sofía', 'Un contacto con un nombre especialmente largo para comprobar la lista'];
    snapshot.peers = names.map((name, index) => ({...snapshot.peers[index === 0 ? 0 : 1],
      id:`00000000-0000-0000-0000-${String(index + 1).padStart(12, '0')}`, name,
      certHash:btoa(String.fromCharCode(...new Uint8Array(32).fill(index + 1))), isTrusted:true}));
    snapshot.savedPeers = snapshot.peers;
    window.overviewSnapshot = snapshot;
    QP.state.status = snapshot; QP.emit('status_updated', snapshot);
    ['Te paso las fotos esta tarde.', 'Tú: Archivo: presentación.pdf', '¡Nos vemos mañana!', 'Tú: Nota de voz', 'Todo listo para la reunión.', 'Gracias por el archivo.'].forEach((message, index) => {
      QP.emit('chat_msg', {peerId:snapshot.peers[index].id, msgId:`overview-${index}`, message,
        sender:snapshot.peers[index].name, isMe:false, timestampMs:Date.now() - index * 86400000});
    });
  });
  assert.equal(await page.locator('#chatConversationCount').textContent(), '7');
  for (const selector of ['.conversation-head', '#chatHistoryHelp', '.direct-composer'])
    assert.equal(await page.locator(selector).isVisible(), false, `Inactive control visible: ${selector}`);
  const start = page.locator('.chat-idle-actions button').first();
  await start.focus();
  await page.evaluate(() => QP.emit('status_updated', overviewSnapshot));
  assert.equal(await start.evaluate(node => node === document.activeElement), true, 'Status refresh loses welcome focus');
  await start.press('Enter');
  await page.locator('#chatNewDialog').waitFor({state:'visible'});
  await page.keyboard.press('Escape');
  await page.locator('.chat-idle-actions button').last().click();
  await page.locator('#addContactDialog').waitFor({state:'visible'});
  await page.locator('#closeAddContact').click();
  await page.locator('.nav-link[data-page=chat]').click();
  await page.locator('.chat-idle').waitFor();
  await mkdir('.artifacts', {recursive:true});
  await page.screenshot({path:'.artifacts/chat-overview-desktop.png', animations:'disabled'});

  for (const width of [1920,1280,920,390,320]) {
    await page.setViewportSize({width,height:844});
    await page.locator('#chatPeerSearch').fill('especialmente');
    assert.equal(await page.locator('#chatPeers .dm-peer').count(), 1);
    assert.equal(await page.locator('#chatConversationCount').textContent(), '1');
    assert.ok(await page.locator('#chatPeers .ci-name').evaluate(node => node.getBoundingClientRect().width > 100), 'Long name squeezed');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Overflow at ${width}px`);
    await page.locator('#chatPeerSearch').fill('sin coincidencias');
    assert.match(await page.locator('#chatPeers').textContent(), /No hay contactos/);
    await page.locator('#chatPeerSearch').fill('');
  }
  await page.setViewportSize({width:390,height:844});
  await page.screenshot({path:'.artifacts/chat-overview-mobile.png', animations:'disabled'});
  frozen = false; // Let the preview backend confirm the real connection request.
  await page.locator('#chatPeers .dm-peer').filter({hasText:'Ana · Portátil'}).click();
  await page.waitForFunction(() => document.getElementById('chatPeerMeta').textContent.includes('Conectado'));
  frozen = true;
  assert.equal(await page.locator('.direct-composer').isVisible(), true);
  assert.equal(await page.locator('.chat-idle').count(), 0);
  await page.locator('#messageInput').fill('Hola desde la nueva interfaz');
  await page.locator('#sendBtn').click();
  await page.waitForFunction(() => document.getElementById('messageInput').value === '');
  await page.locator('#chatMobileBackBtn').click();
  await page.locator('#chatPeerSearch').waitFor({state:'visible'});
  await page.setViewportSize({width:1280,height:800});
  await page.locator('#chatOptionsMenu summary').click();
  await page.locator('#chatCloseBtn').click();
  await page.locator('.chat-idle').waitFor();
  assert.equal(await page.locator('.direct-composer').isVisible(), false);
  await page.evaluate(() => {
    const empty = {...overviewSnapshot, peers:[], savedPeers:[], activeChats:[]};
    QP.state.status = empty; QP.emit('status_updated', empty);
  });
  assert.equal(await page.locator('#chatConversationCount').textContent(), '0');
  await page.locator('.chat-idle-actions button').first().click();
  assert.equal(await page.locator('#chatNewDialog .chat-new-peer').count(), 0);
  assert.deepEqual(errors, []);
  console.log('PASS chat overview: welcome actions, refresh focus, hidden idle tools, search/count, long names, mobile navigation, send, close and no contacts');
} finally { await closeBrowserFixture(browser, server); }
