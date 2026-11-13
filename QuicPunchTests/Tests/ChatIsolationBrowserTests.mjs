import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Fixture-only regression for mode isolation and closing/reopening local conversations.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1440,height:900}}), errors = [];
  page.on('pageerror',error => errors.push(error.message));
  const alice = '00000000-0000-0000-0000-000000000001';
  const torId = '00000000-0000-0000-0000-000000000004';
  const torSavedId = '00000000-0000-0000-0000-000000000005';
  const tor = {id:torId,certHash:'tor-fixture',name:'Contacto Tor',networkType:'Tor',isTor:true,onionAddress:'fixture.onion',isTrusted:true,isSaved:true,addresses:[],ports:[443]};
  const torSaved = {...tor,id:torSavedId,certHash:'tor-saved-fixture',name:'Guardado Tor'};
  const augment = snapshot => ({...snapshot,peers:[...snapshot.peers,tor],savedPeers:[...snapshot.savedPeers,tor,torSaved],discoveredPeers:[{...tor,id:'tor-discovered',certHash:'tor-discovered',name:'Descubierto Tor',isTrusted:false,isSaved:false}]});
  await page.route('**/api/status',async route => {
    const response = await route.fetch(); await route.fulfill({json:augment(await response.json())});
  });
  // Mode changes also deliver live snapshots; use the same mixed-mode fixture there.
  await page.routeWebSocket('**/ws?*',socket => {
    const backend = socket.connectToServer();
    backend.onMessage(message => {
      if (typeof message === 'string') {
        const event = JSON.parse(message);
        if (event.type === 'status_updated' || event.type === 'response' && event.data?.peers) {event.data = augment(event.data); message = JSON.stringify(event);}
      }
      socket.send(message);
    });
    socket.onMessage(message => backend.send(message));
  });
  const oldMessage = {peerId:alice,msgId:'preserved',message:'Este historial se conserva',isMe:false,timestampMs:Date.now()-60000,sender:'Ana'};
  await page.route('**/api/chat/messages?**',route => route.fulfill({json:{messages:new URL(route.request().url()).searchParams.get('peerId') === alice ? [oldMessage] : [],nextBefore:null}}));
  let destructiveRequests = 0;
  page.on('request',request => {if (/\/api\/(?:chat\/clear|remove-saved-peer)/.test(request.url())) destructiveRequests++;});
  await page.goto(url+'chat.html?peer='+alice);
  await page.waitForFunction(() => document.getElementById('messages').textContent.includes('Este historial se conserva'));
  assert.equal(await page.locator('#chatPeers').getByText('Contacto Tor',{exact:true}).count(),0);
  await page.locator('#messageInput').fill('Borrador conservado');
  const close = async () => {await page.locator('#chatOptionsMenu summary').click(); await page.locator('#chatCloseBtn').click();};
  await close();
  assert.equal(await page.locator(`#chatPeers [data-peer-id="${alice}"]`).count(),0);
  assert.equal(new URL(page.url()).searchParams.has('peer'),false);
  assert.equal(await page.locator('#messageInput').isDisabled(),true);
  await page.reload(); await page.waitForFunction(() => QP.state.status && QP.isWsConnected);
  assert.equal(await page.locator(`#chatPeers [data-peer-id="${alice}"]`).count(),0,'Closed chat reopened on reload');
  await page.locator('#chatNewBtn').click();
  assert.equal(await page.locator('#chatNewPeers').getByText('Contacto Tor',{exact:true}).count(),0);
  await page.locator('#chatNewPeers .chat-new-peer').filter({hasText:'Ana · Portátil'}).click();
  await page.waitForFunction(() => document.getElementById('messages').textContent.includes('Este historial se conserva'));
  await page.locator('#messageInput').fill('Borrador conservado');
  await close();
  await page.locator('#chatNewBtn').click(); await page.locator('#chatNewPeers .chat-new-peer').filter({hasText:'Ana · Portátil'}).click();
  assert.equal(await page.locator('#messageInput').inputValue(),'Borrador conservado');
  await close();
  await page.evaluate(id => QP.emit('chat_msg',{peerId:id,msgId:'incoming',message:'Mensaje nuevo',isMe:false,timestampMs:Date.now()+1}),alice);
  await page.locator(`#chatPeers [data-peer-id="${alice}"]`).waitFor();
  await page.locator(`#chatPeers [data-peer-id="${alice}"]`).click();
  await page.locator('.nav-link[data-page=dashboard]').click();
  await page.locator('.contact-options > summary').click();
  await page.locator('button[data-connection-mode=tor]').click();
  await page.waitForFunction(() => document.documentElement.dataset.connectionMode === 'tor');
  await page.locator('.nav-link[data-page=chat]').click();
  assert.equal(await page.locator('#messageInput').isDisabled(),true,'WAN selection survived Tor mode');
  assert.equal(new URL(page.url()).searchParams.has('peer'),false);
  assert.deepEqual((await page.locator('#chatPeers .dm-peer-name .ci-name').allTextContents()).sort(),['Contacto Tor','Guardado Tor']);
  await page.locator('#chatNewBtn').click();
  assert.deepEqual((await page.locator('#chatNewPeers strong').allTextContents()).sort(),['Contacto Tor','Guardado Tor']);
  await page.locator('#chatNewCloseBtn').click();
  await page.locator('.nav-link[data-page=dashboard]').click();
  assert.doesNotMatch(await page.locator('#homeContacts').textContent(),/Ana|Mi ordenador|Dispositivo nuevo/);
  assert.match(await page.locator('#homeContacts').textContent(),/Descubierto Tor/);
  assert.doesNotMatch(await page.locator('#savedPeersBody').textContent(),/Ana|Mi ordenador/);
  await page.locator('button[data-connection-mode=both]').click();
  await page.waitForFunction(() => document.documentElement.dataset.connectionMode === 'both');
  assert.match(await page.locator('#homeContacts').textContent(),/Ana/);
  assert.match(await page.locator('#homeContacts').textContent(),/Contacto Tor/);
  await page.locator('button[data-connection-mode=wan]').click();
  await page.waitForFunction(() => document.documentElement.dataset.connectionMode === 'wan');
  assert.doesNotMatch(await page.locator('#homeContacts').textContent(),/Contacto Tor|Guardado Tor|Descubierto Tor/);
  assert.match(await page.locator('#homeContacts').textContent(),/Ana/);
  assert.equal(destructiveRequests,0,'Closing a conversation deleted data or a contact');
  assert.deepEqual(errors,[]);
  console.log('PASS WAN/Tor isolation across contacts, saved/discovered lists and chat picker; mode clears selection; close persists, preserves history/draft, reopens manually or on a new message without deleting data.');
} finally {await closeBrowserFixture(browser, server);}
