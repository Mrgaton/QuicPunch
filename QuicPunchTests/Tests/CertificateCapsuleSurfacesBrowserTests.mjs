import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Shared certificate identity across real UI surfaces, using the isolated preview backend.

import {readFileSync, mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer();
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1280,height:900}}), errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  let frozen=false;
  await page.routeWebSocket('**/ws?*', socket=>{
    const backend=socket.connectToServer();socket.onMessage(message=>backend.send(message));
    backend.onMessage(message=>{if(!frozen || typeof message!=='string' || JSON.parse(message).type!=='status_updated')socket.send(message);});
  });
  const id='00000000-0000-0000-0000-000000000001';
  const message={peerId:id,msgId:'identity-message',message:'La misma identidad en todos los lugares.',sender:'Ana',isMe:false,timestampMs:Date.now(),isConfirmed:true};
  await page.route('**/api/chat/messages?**',route=>route.fulfill({json:{messages:[message],nextBefore:null}}));
  await page.route('**/api/profile/avatar/**',route=>route.fulfill({contentType:'image/avif',body:readFileSync(new URL('../Fixtures/profile-avatar.avif',import.meta.url))}));
  await page.goto(url);await page.waitForFunction(()=>QP.state.status && QP.isWsConnected);
  frozen=true;
  await page.evaluate(id=>{
    window.capsuleFixture=structuredClone(QP.state.status);
    capsuleFixture.activeChats=[{peerId:id}];
    window.pushCapsuleFixture=()=>{QP.state.status=capsuleFixture;QP.emit('status_updated',capsuleFixture);};
    pushCapsuleFixture();
  },id);
  const contact=page.locator('#homeContacts .contact-card').filter({has:page.locator('.ci-name',{hasText:'Ana · Portátil'})}).locator('.ci');
  await contact.locator('[class=ci-body]').waitFor();
  await page.waitForFunction(()=>document.querySelector('#homeContacts .ci[data-identity-state=ready]'));
  const signature=locator=>locator.evaluate(node=>JSON.stringify([node.dataset.identityVersion,node.dataset.edge,node.dataset.pattern,node.style.getPropertyValue('--ci-hue'),node.style.getPropertyValue('--ci-saturation')]));
  const original=await signature(contact);
  await page.evaluate(id=>{QP.navigate('chat');openChatPeer(id);pushCapsuleFixture();},id);
  const header=page.locator('#chatIdentity .ci'), author=page.locator('[data-message-id=identity-message] .ci'), list=page.locator(`#chatPeers [data-peer-id="${id}"] .ci`);
  await author.waitFor();
  await page.waitForFunction(()=>document.querySelector('[data-message-id=identity-message] .ci[data-identity-state=ready]'));
  for(const capsule of [header,author,list])assert.equal(await signature(capsule),original);
  assert.equal(await page.locator('button button').count(),0,'List rows must not contain nested buttons');
  await page.evaluate(id=>{
    const petition={requestId:'capsule-call',peerId:id,peerName:'Nombre anunciado',protocolName:'Voice',notify:true};
    capsuleFixture.pendingPetitions=[petition];pushCapsuleFixture();QP.emit('petition_received',petition);
  },id);
  const caller=page.locator('#incomingCallIdentity .ci');
  await page.waitForFunction(()=>document.querySelector('#incomingCallIdentity .ci[data-identity-state=ready]'));
  await page.locator('#incomingCallBanner').waitFor({state:'visible'});
  assert.equal(await signature(caller),original);
  assert.equal(await page.locator('#incomingCallPeerName').textContent(),'Ana · Portátil','Use the known peer rather than an invitation-supplied display name');
  await caller.click();await page.locator('dialog.identity-dialog').waitFor();
  assert.equal((await page.locator('.identity-fingerprint').textContent()).replaceAll(' ',''),'01'.repeat(32));
  await page.keyboard.press('Escape');
  assert.equal(await caller.evaluate(node=>node===document.activeElement),true);

  // Photos, renames, presence and realtime rerenders preserve the visual descriptor and DOM/media.
  await page.evaluate(id=>{capsuleFixture.avatars={[id]:'a'.repeat(64)};pushCapsuleFixture();},id);
  await author.locator('img').waitFor();
  await page.evaluate(()=>{window.capsuleImage=document.querySelector('[data-message-id=identity-message] .ci img');window.capsuleMessage=document.querySelector('[data-message-id=identity-message]');});
  await page.evaluate(id=>{
    capsuleFixture.peers.find(p=>p.id===id).name='Ana · nombre cambiado';
    capsuleFixture.peerPresence={[id]:{IsAfk:true,AcceptsNotifications:false}};pushCapsuleFixture();
  },id);
  for(const capsule of [header,author,list,caller])assert.equal(await signature(capsule),original);
  assert.equal(await page.evaluate(()=>capsuleImage===document.querySelector('[data-message-id=identity-message] .ci img') && capsuleMessage===document.querySelector('[data-message-id=identity-message]')),true);

  const replacement=Buffer.alloc(32,90).toString('base64');
  await page.evaluate(({id,replacement})=>{capsuleFixture.peers.find(p=>p.id===id).certHash=replacement;pushCapsuleFixture();},{id,replacement});
  await page.waitForFunction(()=>!document.querySelector('#chatIdentity [aria-busy],#incomingCallIdentity [aria-busy],[data-message-id=identity-message] [aria-busy]'));
  const changed=await signature(header);assert.notEqual(changed,original);
  for(const capsule of [author,list,caller])assert.equal(await signature(capsule),changed,'A certificate update must refresh every surface');

  // Missing certificate data must not leave the previous person's pattern on an incoming invitation.
  await page.evaluate(()=>{
    const petition={requestId:'late-call',peerId:'late-peer',peerName:'Contacto pendiente',protocolName:'Voice',notify:true};
    capsuleFixture.pendingPetitions=[petition];pushCapsuleFixture();QP.emit('petition_received',petition);
  });
  assert.equal(await caller.getAttribute('data-identity-state'),'pending');
  assert.equal(await caller.getAttribute('data-pattern'),null);
  assert.equal(await caller.isDisabled(),true);
  await page.evaluate(replacement=>{capsuleFixture.peers.push({id:'late-peer',name:'Contacto recuperado',certHash:replacement,isTrusted:true,networkType:'Wan'});pushCapsuleFixture();},replacement);
  await page.waitForFunction(()=>document.querySelector('#incomingCallIdentity .ci[data-identity-state=ready]'));
  assert.equal(await signature(caller),changed);
  assert.equal(await page.locator('#incomingCallPeerName').textContent(),'Contacto recuperado');

  // Resolve outstanding digest promises after clearing a recycled capsule.
  assert.equal(await page.evaluate(async()=>{
    const first={name:'A',certHash:'f1'.repeat(32)},second={name:'B',certHash:'f2'.repeat(32)};
    const capsule=QP.identity.createCapsule(first);
    QP.identity.updateCapsule(capsule,second);QP.identity.updateCapsule(capsule,{name:'Sin certificado'});
    await Promise.all([QP.identity.describe(first),QP.identity.describe(second)]);
    return capsule.dataset.identityState==='pending' && !capsule.dataset.pattern && !capsule.hasAttribute('aria-busy');
  }),true);
  mkdirSync('.artifacts/identity',{recursive:true});
  await page.evaluate(()=>window.scrollTo(0,0));
  assert.equal(await header.evaluate(node=>node.getBoundingClientRect().height),44,'Header must retain the normal capsule geometry');
  await page.screenshot({path:'.artifacts/identity/chat-call.png',animations:'disabled'});
  for(const width of [768,390,320]){
    await page.setViewportSize({width,height:900});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'Overflow at '+width);
    for(const capsule of [header,author,caller]) {
      assert.equal(await capsule.evaluate(node=>node.getBoundingClientRect().right<=innerWidth),true,'Capsule outside viewport at '+width);
      assert.equal(await capsule.locator('.ci-body').evaluate(node=>getComputedStyle(node).display),'flex','Legacy styles must not stack capsule contents');
      assert.ok(await capsule.locator('.ci-name').evaluate(node=>node.getBoundingClientRect().width>=100),'Name squeezed into a vertical column at '+width);
    }
    const composer = await page.locator('.direct-composer').evaluate(node=>({bottom:node.getBoundingClientRect().bottom,viewport:innerHeight}));
    assert.ok(composer.bottom<=composer.viewport,'Incoming call pushes composer outside viewport: '+JSON.stringify(composer));
  }
  await page.evaluate(()=>window.scrollTo(0,0));
  await page.screenshot({path:'.artifacts/identity/chat-call-mobile.png',animations:'disabled'});
  await page.evaluate(()=>{QP.navigate('dashboard');pushCapsuleFixture();});
  await page.locator('#requestsList .ci[data-identity-state=ready]').waitFor();
  assert.equal(await signature(page.locator('#requestsList .ci')),changed,'Pending request panel uses the same capsule');
  assert.deepEqual(errors,[]);
  console.log('PASS shared capsules: contacts/chat/list/call/request identity, rename/photo/presence stability, media reuse, certificate refresh, pending metadata, async race, keyboard, 320–1280px');
} finally {await closeBrowserFixture(browser, server);}
