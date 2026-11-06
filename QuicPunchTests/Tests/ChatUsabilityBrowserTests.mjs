import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Isolated UX regression coverage: fixture identities only; no P2P or audio capture.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1600,height:1000}}), errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const alice='00000000-0000-0000-0000-000000000001', bob='00000000-0000-0000-0000-000000000002';
  const now=Date.now(), yesterday=new Date();yesterday.setDate(yesterday.getDate()-1);
  const messages=[
    {peerId:alice,msgId:'yesterday',message:'El archivo ya está preparado.',timestampMs:yesterday.getTime(),sender:'Ana',time:'12:30:00',isMe:false},
    {peerId:alice,msgId:'today-1',message:'¿Revisamos los detalles esta tarde?',timestampMs:now-10000,sender:'Ana',time:'12:31:00',isMe:false},
    {peerId:alice,msgId:'today-2',message:'También he actualizado las imágenes.',timestampMs:now-5000,sender:'Ana',time:'12:31:05',isMe:false},
    {peerId:alice,msgId:'today-3',message:'Sí, en cuanto termine la llamada.',timestampMs:now,sender:'Tú',time:'12:31:10',isMe:true,isConfirmed:true},
  ];
  const cleared=new Set();
  await page.route('**/api/chat/messages?**',route=>{const peer=new URL(route.request().url()).searchParams.get('peerId');return route.fulfill({json:{messages:peer===alice&&!cleared.has(peer)?messages:[],nextBefore:null}});});
  const durations=new Map(), saves=[]; let releaseSave;
  await page.route('**/api/chat/disappearing',async route=>{
    const body=route.request().postDataJSON();saves.push(body);
    await new Promise(resolve=>releaseSave=resolve);
    durations.set(body.peerId,body.seconds);await route.fulfill({json:{success:true,...body}});
  });
  const voiceSource=readVoiceSource();
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:voiceSource.replace(/\}\)\(\);\s*$/,`window.uxCall=()=>{voiceStatus={...QP.state.status,activeVoiceCalls:[{peerId:'${bob}',peerName:'Mi ordenador'}]};voiceState={...voiceState,isMuted:true,isDeafened:false,activeCalls:[{peerId:'${bob}',startedAtUnixMs:Date.now()-65000}]};renderVoice();};})();`)}));
  await page.goto(url+'chat.html?peer='+alice);
  await page.waitForFunction(()=>document.getElementById('chatPeerMeta').textContent.includes('Conectado'));
  assert.match(await page.locator('#chatHistoryLabel').textContent(),/Historial/);
  assert.match(await page.locator('#chatExpiryHint').textContent(),/Sin borrado automático local/);
  await page.locator('#chatHistoryHelp summary').click();
  assert.match(await page.locator('#chatHistoryScope').textContent(),/No cambia el historial anterior/);
  await page.keyboard.press('Escape');assert.equal(await page.locator('#chatHistoryHelp').evaluate(node=>node.open),false);
  assert.deepEqual(await page.locator('.chat-date-separator').allTextContents(),['Ayer','Hoy']);
  assert.equal(await page.locator('.message-grouped').count(),1);
  assert.doesNotMatch(await page.locator('.message-timestamp').last().textContent(),/\d+:\d+:\d+/);
  assert.equal(await page.locator('#chatClearBtn').isVisible(),false,'Destructive action must be inside options');
  // A slow save stays bound to its original conversation, even after navigation.
  await page.locator('#chatHistoryHelp summary').click();
  await page.locator('#chatHistoryOptions [data-duration="3600"]').click();
  await page.waitForFunction(()=>document.getElementById('chatExpirySelect').disabled);
  await page.locator('#chatPeers .dm-peer').filter({hasText:'Mi ordenador'}).click();
  releaseSave();await page.waitForFunction(()=>!document.getElementById('chatExpirySelect').disabled);
  assert.equal(await page.locator('#chatExpirySelect').inputValue(),'0');
  assert.equal(saves[0].peerId,alice);
  await page.locator('#chatPeers .dm-peer').filter({hasText:'Ana · Portátil'}).click();
  await page.waitForFunction(()=>document.getElementById('chatExpirySelect').value==='3600');
  assert.equal(await page.locator('.toast-host .toast').count(),0,'Retention changes should not stack notifications');
  // Keyboard resize and navigation preference survive a page reload.
  await page.locator('#chatListResizer').focus();await page.keyboard.press('End');
  assert.equal(await page.locator('.dm-pane').evaluate(node=>Math.round(node.getBoundingClientRect().width)),420);
  await page.locator('#navCollapseBtn').click();
  assert.equal(await page.locator('.nav-wrap').evaluate(node=>Math.round(node.getBoundingClientRect().width)),64);
  await page.reload();await page.waitForFunction(()=>!document.getElementById('messageInput').disabled);
  assert.equal(await page.locator('#chatListResizer').getAttribute('aria-valuenow'),'420');
  assert.equal(await page.locator('#navCollapseBtn').getAttribute('aria-expanded'),'false');
  await page.locator('#navCollapseBtn').click();
  // Contact identity and the picker remain usable when display names coincide.
  await page.evaluate(()=>{chatStatus.peers.forEach(peer=>{if(peer.isTrusted)peer.name='Mismo nombre';});renderChat();});
  assert.equal(await page.locator('.dm-peer-device').count(),2);
  await page.locator('#chatNewBtn').click();
  assert.equal(await page.locator('#chatNewDialog .chat-new-peer').count(),2);
  await page.locator('#chatNewSearch').fill(bob);
  assert.equal(await page.locator('#chatNewDialog .chat-new-peer').count(),1);
  await page.locator('#chatNewDialog .chat-new-peer').click();
  await page.waitForFunction(()=>document.getElementById('sendBtn').textContent==='Dejar pendiente');
  assert.match(await page.locator('#chatComposerHint').textContent(),/automáticamente al reconectar/);
  assert.equal(await page.locator('#attachBtn').isDisabled(),true);
  assert.match(await page.locator('#attachBtn').getAttribute('title'),/Disponible al conectar/);
  // Clear is scoped, cancellable, translated and keeps its error inside the dialog.
  let clearCount=0, rejectClear=true;
  await page.route('**/api/chat/clear',route=>{
    clearCount++;
    if(rejectClear)return route.fulfill({status:500,json:{error:'Fallo de escritura simulado'}});
    cleared.add(route.request().postDataJSON().peerId);return route.fulfill({json:{success:true}});
  });
  await page.locator('#chatOptionsMenu summary').click();await page.locator('#chatClearBtn').click();
  assert.equal(await page.locator('#chatClearCancelBtn').evaluate(node=>node===document.activeElement),true);
  assert.match(await page.locator('#chatClearDescription').textContent(),/pendientes de envío/);
  await page.locator('#chatClearCancelBtn').click();assert.equal(clearCount,0);
  await page.locator('#chatOptionsMenu summary').click();await page.locator('#chatClearBtn').click();
  await page.locator('#chatClearConfirmBtn').click();await page.locator('#chatClearError').waitFor({state:'visible'});
  assert.match(await page.locator('#chatClearError').textContent(),/Fallo de escritura simulado/);
  assert.equal(await page.locator('#chatClearDialog').evaluate(node=>node.open),true);
  await page.locator('#chatClearCancelBtn').click();
  // The active call is a separate context and has actual elapsed time and mute state.
  await page.evaluate(()=>uxCall());
  assert.match(await page.locator('#globalCallPeerName').textContent(),/En llamada con Mi ordenador/);
  assert.match(await page.locator('#globalCallMeta').textContent(),/1m.*Micrófono silenciado/);
  assert.equal(await page.locator('#globalCallMicBtn').getAttribute('aria-pressed'),'true');
  assert.equal(await page.locator('#globalCallHangupBtn svg').count(),1);
  const geometry=()=>page.evaluate(()=>{
    const box=id=>document.getElementById(id).getBoundingClientRect();
    const input=box('messageInput'), history=box('messages');
    const limit=innerWidth<=680?document.querySelector('.nav-wrap').getBoundingClientRect().top:innerHeight;
    return {inputTop:input.top,inputBottom:input.bottom,historyHeight:history.height,historyBottom:history.bottom,limit,overflow:document.documentElement.scrollWidth-innerWidth,rootHeight:document.scrollingElement.scrollHeight,height:innerHeight};
  });
  for(const viewport of [{width:2560,height:1440},{width:1280,height:720},{width:920,height:844},{width:390,height:844},{width:320,height:568},{width:844,height:390},{width:640,height:450}]){
    await page.setViewportSize(viewport);
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    const before=await geometry();
    assert.ok(before.inputTop>=0&&before.inputBottom<=before.limit+1&&before.historyHeight>0&&before.historyBottom<=before.inputTop,`Hidden editor/history at ${JSON.stringify(viewport)}: ${JSON.stringify(before)}`);
    assert.ok(before.overflow<=1&&before.rootHeight<=before.height+1,`Page overflow: ${JSON.stringify(before)}`);
    await page.evaluate(()=>{for(let i=0;i<5;i++)QP.toast('Aviso de prueba '+i);});
    const after=await geometry();assert.equal(before.historyHeight,after.historyHeight,'Notifications shrink history');
    assert.ok(await page.locator('.toast-host .toast').count()<=3);
    await page.locator('#messageInput').fill('El cuadro de escritura sigue disponible');
    await page.evaluate(()=>document.querySelector('.toast-host').replaceChildren());
  }
  await page.setViewportSize({width:1600,height:1000});
  await page.evaluate(()=>{document.documentElement.dataset.theme='dark';openChatPeer('00000000-0000-0000-0000-000000000001');uxCall();});
  await page.locator('[data-message-id="today-3"]').waitFor();
  await page.screenshot({path:'.artifacts/interface-chat-dark.png',animations:'disabled'});
  await page.setViewportSize({width:390,height:844});
  await page.screenshot({path:'.artifacts/interface-chat-mobile.png',animations:'disabled'});
  // Native audio retains full controls and exposes a meaningful name/duration.
  const wav=Buffer.alloc(44+16000);
  wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(8000,24);wav.writeUInt32LE(16000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(16000,40);
  await page.evaluate(data=>onRealtimeMessage({peerId:'00000000-0000-0000-0000-000000000001',msgId:'ux-audio',message:JSON.stringify({type:'audio',data,name:'Una nota de voz con un nombre especialmente largo para comprobar el diseño.wav'}),timestampMs:Date.now(),isMe:false,sender:'Ana'}),'data:audio/wav;base64,'+wav.toString('base64'));
  await page.waitForFunction(()=>document.querySelector('.chat-voice-duration')?.textContent==='0:01');
  assert.match(await page.locator('.chat-audio-player').getAttribute('aria-label'),/Reproducir audio/);
  const audioBounds=await page.locator('.chat-audio-player').boundingBox();
  assert.ok(audioBounds.width>=200&&audioBounds.x+audioBounds.width<=390,'Audio controls clipped on mobile');
  // A portrait clip is constrained without changing its aspect ratio.
  const clip=await page.evaluate(async()=>{
    const canvas=document.createElement('canvas');canvas.width=240;canvas.height=480;
    const context=canvas.getContext('2d');context.fillStyle='#176b5b';context.fillRect(0,0,240,480);
    const stream=canvas.captureStream(5),chunks=[];const recorder=new MediaRecorder(stream,{mimeType:'video/webm'});
    const stopped=new Promise(resolve=>recorder.addEventListener('stop',resolve,{once:true}));
    recorder.addEventListener('dataavailable',event=>chunks.push(event.data));recorder.start();
    await new Promise(resolve=>setTimeout(resolve,300));recorder.stop();await stopped;stream.getTracks().forEach(track=>track.stop());
    const reader=new FileReader(),loaded=new Promise(resolve=>reader.addEventListener('load',()=>resolve(reader.result),{once:true}));reader.readAsDataURL(new Blob(chunks,{type:'video/webm'}));return loaded;
  });
  await page.evaluate(data=>onRealtimeMessage({peerId:'00000000-0000-0000-0000-000000000001',msgId:'ux-video',message:JSON.stringify({type:'video',data,name:'Clip vertical.webm'}),timestampMs:Date.now(),isMe:true,sender:'Tú',isConfirmed:true}),clip);
  await page.waitForFunction(()=>document.querySelector('.chat-media-video')?.videoHeight===480);
  const videoBounds=await page.locator('.chat-media-video').boundingBox();
  assert.ok(videoBounds.height<=320&&videoBounds.x+videoBounds.width<=390,'Portrait preview takes over the conversation');
  // A successful clear removes the local view and stays cleared after reload.
  rejectClear=false;
  await page.locator('#chatOptionsMenu summary').click();await page.locator('#chatClearBtn').click();
  await page.locator('#chatClearConfirmBtn').click();
  await page.locator('#chatClearDialog').waitFor({state:'hidden'});
  assert.equal(await page.locator('#messages .message-row').count(),0);
  assert.ok(cleared.has(alice));
  await page.reload();await page.waitForFunction(()=>!document.getElementById('messageInput').disabled);
  assert.equal(await page.locator('#messages .message-row').count(),0);
  assert.deepEqual(errors,[]);
  console.log('PASS chat usability: retention/save race, dates/groups, contact identities, new chat, persisted widths, clear protection/errors, call context/time, notifications and responsive editor');
} finally {await closeBrowserFixture(browser, server);}
