import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Reproduce a real MediaRecorder WebM without duration metadata; no microphone.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 const page=await browser.newPage({viewport:{width:2560,height:1200}}),errors=[];
 page.on('pageerror',error=>errors.push(error.message));
 const peerId='00000000-0000-0000-0000-000000000001';
 await page.goto(url+'chat.html?peer='+peerId);
 await page.waitForFunction(()=>!document.getElementById('messageInput').disabled);
 // The generator uses an oscillator connected only to a MediaStreamDestination.
 // It never connects to speakers or requests permission for a microphone.
 const recording=await page.evaluate(async()=>{
  const context=new AudioContext();await context.resume();
  const stream=context.createMediaStreamDestination(),oscillator=context.createOscillator();
  oscillator.connect(stream);oscillator.start();
  const recorder=new MediaRecorder(stream.stream,{mimeType:'audio/webm;codecs=opus'}),chunks=[];
  const stopped=new Promise(resolve=>recorder.addEventListener('stop',resolve,{once:true}));
  recorder.addEventListener('dataavailable',event=>{if(event.data.size)chunks.push(event.data);});recorder.start(100);
  await new Promise(resolve=>setTimeout(resolve,1300));recorder.stop();await stopped;
  oscillator.stop();stream.stream.getTracks().forEach(track=>track.stop());await context.close();
  const blob=new Blob(chunks,{type:recorder.mimeType}),reader=new FileReader();
  const data=await new Promise(resolve=>{reader.onload=()=>resolve(reader.result);reader.readAsDataURL(blob);});
  const probe=new Audio();probe.preload='metadata';probe.src=data;
  await new Promise((resolve,reject)=>{probe.onloadedmetadata=resolve;probe.onerror=reject;});
  const nativeInfinite=probe.duration===Infinity;
  probe.removeAttribute('src');probe.load();
  return {data,mime:recorder.mimeType,nativeInfinite};
 });
 assert.equal(recording.nativeInfinite,true,'Fixture did not reproduce a durationless MediaRecorder file');
 await page.evaluate(({peerId,recording})=>onRealtimeMessage({peerId,msgId:'legacy-webm',message:JSON.stringify({type:'audio',name:'Voice note',mime:recording.mime,data:recording.data}),timestampMs:Date.now(),sender:'Ana',isMe:false}),{peerId,recording});
 const card=page.locator('[data-message-id="legacy-webm"] .chat-voice-card');
 await page.waitForFunction(()=>document.querySelector('[data-message-id="legacy-webm"] .chat-voice-card')?.dataset.audioState==='ready');
 const total=Number(await card.locator('.chat-audio-progress').getAttribute('max'));
 assert.ok(total>1&&total<3,`Decoded duration is not the actual recording length: ${total}`);
 assert.equal(await card.locator('.chat-voice-duration').textContent(),'0:02');
 assert.equal(await card.locator('.chat-audio-time').textContent(),'0:00 / 0:02');
 assert.equal(await card.locator('audio').evaluate(audio=>audio.duration===Infinity),true,'Test must exercise the custom finite range over an infinite native duration');
 await card.getByRole('button',{name:'Reproducir nota de voz',exact:true}).click();
 await page.waitForFunction(()=>document.querySelector('[data-message-id="legacy-webm"] audio').currentTime>.15);
 assert.equal(Number(await card.locator('.chat-audio-progress').getAttribute('max')),total,'The progress endpoint grew during playback');
 await card.getByRole('button',{name:'Pausar nota de voz',exact:true}).click();
 await card.locator('.chat-audio-progress').evaluate((input,total)=>{input.value=String(total*.65);input.dispatchEvent(new Event('input',{bubbles:true}));},total);
 await page.waitForFunction(()=>document.querySelector('[data-message-id="legacy-webm"] audio').currentTime>.6);
 assert.equal(Number(await card.locator('.chat-audio-progress').getAttribute('max')),total);
 await card.getByRole('button',{name:'Reproducir nota de voz',exact:true}).click();
 await page.waitForFunction(()=>document.querySelector('[data-message-id="legacy-webm"] audio').ended);
 assert.equal(await card.locator('.chat-audio-time').textContent(),'0:02 / 0:02');
 assert.equal(await card.getByRole('button',{name:'Reproducir nota de voz',exact:true}).count(),1);
 await card.locator('.chat-audio-volume summary').click();
 await card.locator('.chat-audio-volume input').fill('0.5');
 assert.equal(await card.locator('audio').evaluate(audio=>audio.volume),.5);
 await card.locator('.chat-audio-volume summary').click();
 // Equal actual outer dimensions, including their box model and focusable hit area.
 for(const viewport of [{width:2560,height:1200},{width:1440,height:900},{width:390,height:844},{width:320,height:568}]){
  await page.setViewportSize(viewport);
  const size=await page.evaluate(()=>{
   const search=document.getElementById('chatSearchToggleBtn').getBoundingClientRect();
   const more=document.querySelector('#chatOptionsMenu>summary').getBoundingClientRect();
   const messages=document.getElementById('messages'),composer=document.querySelector('.direct-composer');
   return {search:[search.width,search.height],more:[more.width,more.height],padding:parseFloat(getComputedStyle(messages).paddingLeft),composerPadding:parseFloat(getComputedStyle(composer).paddingLeft),overflow:document.documentElement.scrollWidth-innerWidth};
  });
  assert.deepEqual(size.search,[44,44]);assert.deepEqual(size.more,size.search);
  assert.ok(size.padding<=24&&size.composerPadding<=24,`Wide chat margins returned at ${viewport.width}px`);
  assert.ok(size.overflow<=1);
 }
 await page.setViewportSize({width:1440,height:900});
 let saveCount=0, rejectSave=false;
 await page.route('**/api/chat/disappearing',async route=>{
  saveCount++;
  if(rejectSave)return route.fulfill({status:500,json:{error:'No se pudo guardar la preferencia'}});
  return route.fulfill({json:{success:true,...route.request().postDataJSON()}});
 });
 // One click opens all the actual choices; there is no nested select or reflow.
 const heightBefore=await page.locator('#messages').evaluate(box=>box.clientHeight);
 await page.locator('#chatHistoryHelp>summary').click();
 assert.equal(await page.getByRole('option',{name:'Borrar tras 1 día',exact:true}).isVisible(),true);
 assert.equal(await page.locator('#messages').evaluate(box=>box.clientHeight),heightBefore,'Opening the choice popup shrank the conversation');
 assert.equal(await page.locator('#chatHistoryHelp select').count(),0);
 await page.getByRole('option',{name:'Borrar tras 1 día',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('chatExpirySelect').value==='86400'&&!document.getElementById('chatExpirySelect').disabled);
 assert.equal(saveCount,1);assert.equal(await page.locator('#chatHistoryHelp').evaluate(menu=>menu.open),false);
 await page.locator('#chatHistoryHelp>summary').click();
 assert.equal(await page.getByRole('option',{name:'Borrar tras 1 día',exact:true}).getAttribute('aria-selected'),'true');
 await page.keyboard.press('Home');await page.keyboard.press('Enter');
 await page.waitForFunction(()=>document.getElementById('chatExpirySelect').value==='0'&&!document.getElementById('chatExpirySelect').disabled);
 rejectSave=true;await page.locator('#chatHistoryHelp>summary').click();
 await page.getByRole('option',{name:'Borrar tras 1 hora',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('chatExpirySelect').value==='0'&&!document.getElementById('chatExpirySelect').disabled);
 assert.match(await page.locator('.toast-host').textContent(),/No se pudo guardar/);
 await page.evaluate(()=>document.querySelector('.toast-host').replaceChildren());
 await page.setViewportSize({width:2560,height:1200});
 await page.screenshot({path:'.artifacts/chat-audio-fixed-duration-desktop.png',animations:'disabled'});
 await page.setViewportSize({width:390,height:844});
 await page.locator('#chatHistoryHelp>summary').click();
 await page.screenshot({path:'.artifacts/chat-history-direct-menu-mobile.png',animations:'disabled'});
 assert.deepEqual(errors,[]);
 console.log('PASS audio controls: real durationless WebM, decoded total, fixed range, play/pause/seek/end/volume; equal header buttons, full-width chat, direct history options, keyboard and failed saves');
} finally {await closeBrowserFixture(browser, server);}
