import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real browser against fixture HTTP responses: no real contacts, microphone or P2P.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const alice='00000000-0000-0000-0000-000000000001',bob='00000000-0000-0000-0000-000000000002';
  const now=Date.now();
  const message=(id,text,offset=0,peerId=alice)=>({peerId,msgId:id,message:text,sender:'Ana',isMe:false,timestampMs:now-100000+offset,isConfirmed:true});
  const latest=Array.from({length:50},(_,index)=>message('history-'+index,`Mensaje ${index}. Un historial largo permite leer y desplazarse sin perder la posición.`,index*1000));
  const older=Array.from({length:15},(_,index)=>message('older-'+index,`Mensaje anterior ${index}`,index*1000-20000));
  let releaseOlder,olderRequests=0;
  await page.route('**/api/chat/messages?**',async route=>{
    const query=new URL(route.request().url()).searchParams;
    if(query.get('peerId')!==alice)return route.fulfill({json:{messages:[],nextBefore:null}});
    if(query.has('before')){olderRequests++;await new Promise(resolve=>releaseOlder=resolve);return route.fulfill({json:{messages:older,nextBefore:null}});}
    return route.fulfill({json:{messages:latest,nextBefore:'older-page'}});
  });
  await page.goto(url+'chat.html?peer='+alice);
  await page.waitForFunction(()=>document.querySelectorAll('#messages .message-row').length===50&&chatAtBottom());
  const emit=msg=>page.evaluate(msg=>QP.emit('chat_msg',msg),msg);
  const waitForHook=async read=>{const deadline=Date.now()+5000;while(!read()){assert.ok(Date.now()<deadline,'Fixture request did not arrive');await new Promise(resolve=>setTimeout(resolve,10));}};
  const position=()=>page.evaluate(()=>({anchor:captureChatAnchor(),top:document.getElementById('messages').scrollTop}));
  await page.locator('#messages').evaluate(box=>{box.style.scrollBehavior='auto';box.scrollTop=650;});
  await page.waitForFunction(()=>!followChatLatest);
  const before=await position();
  await emit(message('live-1','Un mensaje recibido mientras lees.',110000));
  await emit(message('live-2','Otro mensaje recibido mientras lees.',111000));
  await emit(message('live-2','Otro mensaje recibido mientras lees.',111000));
  const after=await position();
  assert.equal(after.anchor.id,before.anchor.id);assert.ok(Math.abs(after.anchor.offset-before.anchor.offset)<=1);
  assert.ok(Math.abs(after.top-before.top)<=1,'Incoming messages moved the history');
  assert.match(await page.locator('#chatJumpToLatest').textContent(),/2 mensajes nuevos/);
  assert.equal(await page.locator(`#chatPeers [data-peer-id="${alice}"] .chat-unread-count`).textContent(),'2');
  await emit(message('other-peer','Un mensaje de otra conversación.',112000,bob));
  assert.equal(await page.locator('#chatUnreadNavCount').textContent(),'3');
  assert.match(await page.locator('#chatScrollDate').textContent(),/Hoy/);
  // A delayed image inserted above the viewport preserves the visible message,
  // both before and after its intrinsic dimensions become available.
  let releaseImage;
  await page.route('**/api/ux-delayed-image',async route=>{await new Promise(resolve=>releaseImage=resolve);await route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="900"><rect width="1200" height="900" fill="#176b5b"/></svg>'});});
  const image=message('delayed-image',JSON.stringify({type:'image',name:'IMG-20261018-001.jpg',data:'/api/ux-delayed-image'}),500);
  await page.evaluate(msg=>{mergeHistory([msg]);renderChat();},image);
  await page.waitForFunction(()=>document.querySelector('[data-message-id="delayed-image"] img'));
  assert.equal((await position()).anchor.id,before.anchor.id);
  await waitForHook(()=>releaseImage);
  releaseImage();
  await page.waitForFunction(()=>document.querySelector('[data-message-id="delayed-image"] img').naturalHeight===900);
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  const imagePosition=await position();assert.equal(imagePosition.anchor.id,before.anchor.id);assert.ok(Math.abs(imagePosition.anchor.offset-before.anchor.offset)<=1,'Image loading moved the visible message');
  const imageBounds=await page.locator('[data-message-id="delayed-image"] img').boundingBox();
  assert.ok(imageBounds.height<=260&&imageBounds.width<=420);
  assert.equal(await page.locator('[data-message-id="delayed-image"] .message-attachment-title').count(),0);
  const imageButton=page.locator('[data-message-id="delayed-image"] img');
  await imageButton.focus();await page.keyboard.press('Enter');
  assert.equal(await page.locator('#chatLightbox').evaluate(dialog=>dialog.open&&dialog.matches(':modal')),true);
  assert.equal(await page.locator('#lightboxCloseBtn').evaluate(button=>button===document.activeElement),true);
  await page.keyboard.press('Escape');assert.equal(await page.locator('#chatLightbox').isVisible(),false);
  assert.equal(await imageButton.evaluate(img=>img===document.activeElement),true);
  // Keyboard focus can scroll to the image; return to the previous reading area.
  await page.locator('#messages').evaluate(box=>box.scrollTop=650);
  // Move while an older page is still loading: preserve the position at response
  // time rather than using the now-stale position from request time.
  await page.locator('#messages').evaluate(box=>box.scrollTop=40);
  await page.waitForFunction(()=>messagePages.get(selectedPeerId)?.loading);
  await waitForHook(()=>releaseOlder);
  await page.locator('#messages').evaluate(box=>box.scrollTop=450);
  const duringLoad=await position();
  await emit(message('live-3','Recibido durante la paginación.',113000));
  releaseOlder();await page.waitForFunction(()=>document.querySelector('[data-message-id="older-0"]'));
  const loaded=await position();assert.equal(loaded.anchor.id,duringLoad.anchor.id);assert.ok(Math.abs(loaded.anchor.offset-duringLoad.anchor.offset)<=1,'Older history or incoming content moved the reading anchor');
  assert.equal(olderRequests,1);
  await page.locator('#chatJumpToLatest').click();await page.waitForFunction(()=>chatAtBottom()&&!unreadChatMessages.has(selectedPeerId));
  assert.equal(await page.locator('#chatJumpToLatest').isVisible(),false);
  assert.equal(await page.locator('#chatUnreadNavCount').textContent(),'1','Reading one conversation cleared another contact');
  await emit(message('live-4','Recibido al final del chat.',114000));
  await page.waitForFunction(()=>chatAtBottom());
  assert.equal(await page.locator(`#chatPeers [data-peer-id="${alice}"] .chat-unread-count`).count(),0);
  // Searching is not the same as reading incoming content.
  await page.locator('#chatSearchToggleBtn').click();await page.locator('#chatSearchInput').fill('Mensaje 20.');
  await emit(message('live-search','Un mensaje que no coincide con la búsqueda.',115000));
  await page.locator('#chatJumpToLatest').click();
  await page.waitForFunction(()=>!inChatSearchQuery&&chatAtBottom()&&!unreadChatMessages.has(selectedPeerId));
  // Connected peers with missing, partial or invalid presence stay explicitly
  // unknown, and never acquire a duplicate dot or a historical live indicator.
  for(const presence of [null,{}, {IsAfk:false}, {isAfk:'false',acceptsNotifications:true}]){
    await page.evaluate(({id,value})=>{chatStatus.peers.find(peer=>peer.id===id).isQuicConnected=true;chatStatus.peerPresence={[id]:value};renderChat();},{id:alice,value:presence});
    const presenceDot=page.locator(`#chatPeers [data-peer-id="${alice}"] .user-presence-dot`);
    assert.equal(await presenceDot.count(),1);
    assert.equal(await presenceDot.getAttribute('data-user-presence'),'unknown');
    assert.match(await presenceDot.getAttribute('title'),/Estado desconocido/);
    assert.equal(await page.locator('#chatPeers .ci [data-user-presence]').count(),0,'Presence must not alter the certificate capsule');
    assert.equal(await page.locator('#messages [data-user-presence]').count(),0);
  }
  await page.evaluate(id=>{chatStatus.peerPresence={[id]:{IsAfk:false,AcceptsNotifications:false}};renderChat();},alice);
  assert.equal(await page.locator(`#chatPeers [data-peer-id="${alice}"] .user-presence-dot`).getAttribute('data-user-presence'),'dnd');
  // Local names survive reload and do not overwrite the peer's shared name.
  await page.locator('#chatOptionsMenu summary').click();await page.locator('#chatAliasBtn').click();
  await page.locator('#chatAliasInput').fill('Ana · equipo de casa');await page.getByRole('button',{name:'Guardar nombre',exact:true}).click();
  assert.equal(await page.locator('#chatPeerName').textContent(),'Ana · equipo de casa');
  await page.reload();await page.waitForFunction(()=>document.getElementById('chatPeerName').textContent==='Ana · equipo de casa');
  assert.equal(await page.evaluate(id=>QP.state.status.peers.find(peer=>peer.id===id).name,alice),'Ana · Portátil');
  await page.locator('#chatPeers .dm-peer').filter({hasText:'Mi ordenador'}).click();
  // Genuine HTTP failure -> retry -> valid WAV, alongside corrupt and missing audio.
  const wav=Buffer.alloc(16044);wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(8000,24);wav.writeUInt32LE(16000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(16000,40);
  let audioAttempts=0;
  await page.route('**/api/ux-audio-retry',route=>{audioAttempts++;return audioAttempts===1?route.fulfill({status:404,body:'Missing'}):route.fulfill({contentType:'audio/wav',body:wav});});
  await page.route('**/api/ux-audio-corrupt',route=>route.fulfill({contentType:'audio/wav',body:'corrupt audio bytes'}));
  const audioMessage=(id,data)=>message(id,JSON.stringify({type:'audio',name:'Voice note',data}),120000,bob);
  await emit(audioMessage('audio-retry','/api/ux-audio-retry'));
  const retryCard=page.locator('[data-message-id="audio-retry"] .chat-voice-card');
  await page.waitForFunction(()=>document.querySelector('[data-message-id="audio-retry"] .chat-voice-card')?.dataset.audioState==='error');
  assert.match(await retryCard.locator('.chat-audio-status').textContent(),/No se/);
  assert.equal(await retryCard.locator('audio').isVisible(),false,'A broken clip was presented as playable');
  await retryCard.getByRole('button',{name:'Reintentar audio'}).click();
  await page.waitForFunction(()=>document.querySelector('[data-message-id="audio-retry"] .chat-voice-card')?.dataset.audioState==='ready');
  assert.equal(await retryCard.locator('.chat-voice-duration').textContent(),'0:01');assert.ok(audioAttempts>=2);
  await retryCard.locator('audio').evaluate(audio=>audio.dataset.continuity='keep');
  const attemptsBeforeRename=audioAttempts;
  await page.locator('#chatOptionsMenu summary').click();await page.locator('#chatAliasBtn').click();
  await page.locator('#chatAliasInput').fill('Mi otro equipo');await page.getByRole('button',{name:'Guardar nombre',exact:true}).click();
  assert.equal(await retryCard.locator('audio').getAttribute('data-continuity'),'keep','Renaming remounted the audio player');
  assert.equal(audioAttempts,attemptsBeforeRename);
  await emit(audioMessage('audio-corrupt','/api/ux-audio-corrupt'));
  await page.waitForFunction(()=>document.querySelector('[data-message-id="audio-corrupt"] .chat-voice-card')?.dataset.audioState==='error');
  assert.match(await page.locator('[data-message-id="audio-corrupt"] .chat-audio-status').textContent(),/No se puede reproducir/);
  await emit(audioMessage('audio-missing',null));
  assert.match(await page.locator('[data-message-id="audio-missing"] .chat-audio-status').textContent(),/no hay un archivo o enlace válido/);
  assert.equal(await page.locator('[data-message-id="audio-missing"] .chat-audio-retry').isVisible(),false);
  // Once resolved, the total and progress range stay fixed even if the browser
  // later reports an unbounded duration for the same finite recording.
  await retryCard.locator('audio').evaluate(audio=>{Object.defineProperty(audio,'duration',{configurable:true,value:Infinity});audio.dispatchEvent(new Event('durationchange'));});
  assert.equal(await retryCard.locator('.chat-voice-duration').textContent(),'0:01');
  assert.equal(await retryCard.locator('.chat-audio-progress').getAttribute('max'),'1');
  assert.equal(await retryCard.locator('.chat-audio-player').isVisible(),true);
  await retryCard.locator('audio').evaluate(audio=>{delete audio.duration;audio.dispatchEvent(new Event('durationchange'));});
  // A pending response has a visible loading state and offers a retry on stall.
  let blockedAudio;
  await page.route('**/api/ux-audio-pending',async route=>{blockedAudio=route;});
  await emit(audioMessage('audio-pending','/api/ux-audio-pending'));
  const pendingCard=page.locator('[data-message-id="audio-pending"] .chat-voice-card');
  assert.match(await pendingCard.locator('.chat-audio-status').textContent(),/Cargando audio/);
  await pendingCard.locator('audio').evaluate(audio=>audio.dispatchEvent(new Event('stalled')));
  assert.match(await pendingCard.locator('.chat-audio-status').textContent(),/La carga está tardando/);
  assert.equal(await pendingCard.getByRole('button',{name:'Reintentar audio'}).isVisible(),true);
  if(blockedAudio)await blockedAudio.fulfill({contentType:'audio/wav',body:wav});
  // Explicitly verify that arriving messages do not disappear as read when the
  // mobile conversation list is open.
  await page.setViewportSize({width:390,height:844});await page.locator('#chatMobileBackBtn').click();
  await emit(message('mobile-list-arrival','Nuevo mientras miras la lista.',130000,bob));
  assert.equal(await page.locator(`#chatPeers [data-peer-id="${bob}"] .chat-unread-count`).textContent(),'1');
  await page.locator(`#chatPeers [data-peer-id="${bob}"]`).click();await page.waitForFunction(()=>chatAtBottom()&&!unreadChatMessages.has(selectedPeerId));
  await page.screenshot({path:'.artifacts/interface-refinement-mobile.png',animations:'disabled'});
  await page.setViewportSize({width:1600,height:1000});
  await page.screenshot({path:'.artifacts/interface-refinement-desktop.png',animations:'disabled'});
  assert.deepEqual(errors,[]);
  console.log('PASS chat reading/media: stable live/history/image anchors, unread counts/dedup/search/mobile, unknown presence, local names, HTTP audio error/retry, corrupt/missing/stalled audio and unknown duration');
} finally {await closeBrowserFixture(browser, server);}
