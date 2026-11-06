import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Shared web UI, two browser clients and fixture history; no real contacts are changed.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1280,height:800}});
  const viewer = await browser.newPage({viewport:{width:390,height:844}});
  const errors = []; for (const client of [page,viewer]) client.on('pageerror',error => errors.push(error.message));
  const alice = '00000000-0000-0000-0000-000000000001', bob = '00000000-0000-0000-0000-000000000002';
  const history = [
    {peerId:alice,msgId:'a',message:'Primero',timestampMs:100,sender:'Ana',time:'23:59',isMe:false},
    {peerId:bob,msgId:'b',message:'Después de medianoche',timestampMs:200,sender:'Tú',time:'00:01',isMe:true}
  ];
  for (const client of [page,viewer]) await client.route('**/api/chat/messages?**', route => route.fulfill({json:{messages:history.filter(m=>m.peerId===new URL(route.request().url()).searchParams.get('peerId')),nextBefore:null}}));
  await page.goto(url+'chat.html?peer='+alice);
  await viewer.goto(url+'chat.html?peer='+alice);
  const rows = () => page.locator('#chatPeers .dm-peer-name .ci-name').allTextContents();
  await page.waitForFunction(() => document.querySelector('#chatPeers .dm-peer-preview')?.textContent.includes('Después de medianoche'));
  assert.deepEqual(await rows(), ['Mi ordenador','Ana · Portátil']);
  const emit = msg => page.evaluate(msg => QP.emit('chat_msg',msg),msg);
  await emit({peerId:alice,msgId:'incoming',message:'Nuevo entrante',timestampMs:300,sender:'Ana',time:'00:02',isMe:false});
  assert.deepEqual(await rows(), ['Ana · Portátil','Mi ordenador']);
  await emit({peerId:bob,msgId:'outgoing',message:'Nuevo saliente',timestampMs:400,sender:'Tú',time:'00:03',isMe:true});
  assert.deepEqual(await rows(), ['Mi ordenador','Ana · Portátil']);
  await emit({peerId:alice,msgId:'old-sync',message:'Historial antiguo',timestampMs:50,sender:'Ana',time:'23:58',isMe:false});
  assert.deepEqual(await rows(), ['Mi ordenador','Ana · Portátil'], 'Old history must not jump to the top');
  const markdown = '# Título\n**Negrita** *cursiva* __subrayado__ ~~tachado~~\n\n> Una cita\n\n- Uno\n- Dos\n\n```js\n**literal**\n<img onerror=alert(1)>\n```\n\n[Enlace](https://example.org/a_b) ||secreto||\n<script>window.injected=true</script>\n[x](javascript:alert)';
  await emit({peerId:alice,msgId:'format',message:markdown,timestampMs:500,sender:'Ana',time:'00:04',isMe:false});
  const formatted = page.locator('#messages .message-text').last();
  assert.equal(await formatted.locator('h1').textContent(), 'Título');
  assert.equal(await formatted.locator('strong').textContent(), 'Negrita');
  assert.equal(await formatted.locator('li').count(), 2);
  assert.equal(await formatted.locator('code').textContent(), '**literal**\n<img onerror=alert(1)>');
  assert.equal(await formatted.locator('script,img').count(), 0);
  assert.equal(await formatted.locator('a').count(), 1);
  const spoiler = formatted.locator('.chat-spoiler'); await spoiler.click();
  assert.equal(await spoiler.getAttribute('aria-expanded'), 'true');
  assert.equal(await page.evaluate(() => window.injected), undefined);
  // Relay the sender's actual typing HTTP requests to the second fixture client's WS handler.
  const requests = []; let dropStop = false;
  await page.route('**/api/chat/typing', async route => {
    const data = route.request().postDataJSON(); requests.push(data);
    if (!dropStop || data.isTyping) await viewer.evaluate(data => QP.emit('chat_typing',{...data,peerName:'Lucía'}),data);
    await route.fulfill({json:{success:true}});
  });
  const input = page.locator('#messageInput'), indicator = viewer.locator('#chatTypingIndicator');
  for (const client of [page,viewer]) await client.waitForFunction(() => document.getElementById('chatPeerMeta').textContent.includes('Conectado'));
  await input.fill('Estoy escribiendo'); await indicator.waitFor({state:'visible'});
  assert.match(await indicator.textContent(), /Lucía está escribiendo/);
  await page.waitForTimeout(1600); await input.press('End');
  const renewal = page.waitForRequest(request => request.url().endsWith('/api/chat/typing') && request.postDataJSON()?.isTyping);
  await input.press('a'); await renewal;
  assert.ok(requests.filter(r=>r.isTyping).length >= 2, 'Continuous writing must renew presence');
  await page.locator('#chatPeers .dm-peer').filter({hasText:'Mi ordenador'}).click();
  await indicator.waitFor({state:'hidden'});
  assert.ok(requests.every(r=>r.peerId===alice), 'A stop event was sent to the newly selected contact');
  await page.locator('#chatPeers .dm-peer').filter({hasText:'Ana · Portátil'}).click();
  await input.fill('Texto para enviar'); await indicator.waitFor({state:'visible'});
  await page.locator('#sendBtn').click(); await indicator.waitFor({state:'hidden'});
  await page.waitForFunction(() => document.getElementById('messageInput').value === '');
  dropStop = true; await input.fill('Conexión perdida'); await indicator.waitFor({state:'visible'});
  await input.fill('');
  await indicator.waitFor({state:'hidden',timeout:7000});
  await page.setViewportSize({width:390,height:844});
  await page.locator('#messages').evaluate(node => {node.scrollTop=0;});
  assert.equal(await page.evaluate(() => scrollY), 0);
  const composer = await input.boundingBox(), nav = await page.locator('.nav-wrap').boundingBox();
  assert.ok(composer.y+composer.height <= nav.y, 'Typing and formatting must not hide the composer');
  await page.screenshot({path:'.artifacts/chat-markdown-mobile.png'});
  assert.deepEqual(errors, []);
  console.log('PASS chat features: incoming/outgoing ordering, old history, safe Markdown, spoilers, two-client typing, refresh, switch, send, expiry and fixed composer');
} finally {await closeBrowserFixture(browser, server);}
