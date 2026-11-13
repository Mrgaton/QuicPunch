import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Regressions from the web UI audit. Real DOM/media, isolated contacts and HTTP fixtures.

import {readFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const selected=process.argv.find(arg=>arg.startsWith('--case='))?.slice(7);
const run=name=>!selected || selected===name;
const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser({args:['--autoplay-policy=no-user-gesture-required']});
 const csp=[...readFileSync(new URL('../../QuicNet.Shared/WebUi/WebUiServer.cs',import.meta.url),'utf8').matchAll(/resp.Headers\["Content-Security-Policy"\] = "([^"]+)"/g)].at(-1)[1];
 async function chatPage() {
  const page=await browser.newPage();
  await page.route('**/chat.html',async route=>{const response=await route.fetch();await route.fulfill({response,headers:{...response.headers(),'Content-Security-Policy':csp}});});
  await page.goto(url+'chat.html');await page.waitForFunction(()=>QP.state.status);await page.waitForTimeout(50);
  await page.evaluate(()=>{
   const a='10000000-0000-0000-0000-000000000001',b='10000000-0000-0000-0000-000000000002';window.testPeers={a,b};
   const peers=[{id:a,name:'Ana',isTrusted:true,isSaved:true,isQuicConnected:true},{id:b,name:'Bea',isTrusted:true,isSaved:true,isQuicConnected:true}];
   window.testStatus={...QP.state.status,peers,savedPeers:peers,activeChats:[{peerId:a},{peerId:b}],chatMessages:[]};
   QP.state.status=testStatus;chatStatus=testStatus;QP.getStatus=async()=>testStatus;window.testRequests=[];window.testNotices=[];QP.toast=message=>testNotices.push(message);
   QP.api=async(path,options={})=>{const body=JSON.parse(options.body||'{}');testRequests.push({path,body});if(path==='/api/status')return testStatus;return {success:true,messages:[],states:[],pins:[]};};
   selectChatPeer(a);renderChat();
  });return page;
 }
 if(run('downloads')) {
  const page=await chatPage();
  const result=await page.evaluate(()=>{
   window.testInjected=false;
   const invalid=['javascript:window.testInjected=true;void(0)','data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==','data:image/svg+xml;base64,PHN2Zy8+','file:///C:/Windows/win.ini'];
   const rejected=invalid.map(data=>{const box=document.createElement('div');appendMessageContent(box,JSON.stringify({type:'file',name:'archivo',data}),{});return {links:box.querySelectorAll('a').length,error:!!box.querySelector('.chat-media-error')};});
   const good=document.createElement('div');appendMessageContent(good,JSON.stringify({type:'file',name:'seguro.txt',data:'data:text/plain;base64,aG9sYQ=='}),{});
   const svg=document.createElement('div');appendMessageContent(svg,JSON.stringify({type:'image',data:invalid[2]}),{});
   return {rejected,validHref:good.querySelector('a')?.href,svgImages:svg.querySelectorAll('img').length,injected:testInjected};
  });
  assert.ok(result.rejected.every(item=>item.links===0&&item.error));assert.equal(result.injected,false);assert.equal(result.validHref,'data:text/plain;base64,aG9sYQ==');assert.equal(result.svgImages,0);
  await page.close();console.log('PASS downloads: executable/file/HTML/SVG URLs rejected under production CSP; safe legacy download preserved');
 }
 if(run('recipient')) {
  const page=await chatPage();
  const attachment=await page.evaluate(async()=>{
   const original=FileReader.prototype.readAsDataURL;
   FileReader.prototype.readAsDataURL=function(){const reader=this;window.finishRead=()=>{Object.defineProperty(reader,'result',{value:'data:image/png;base64,aG9sYQ=='});reader.onload?.(new Event('load'));};};
   try {const pending=sendAttachment(new File(['image'],'privada.png',{type:'image/png'}));selectChatPeer(testPeers.b);finishRead();await pending;return testRequests.find(item=>item.path==='/api/chat-send')?.body.peerId;}
   finally {FileReader.prototype.readAsDataURL=original;}
  });assert.equal(attachment,'10000000-0000-0000-0000-000000000001');
  const note=await page.evaluate(async()=>{
   selectChatPeer(testPeers.a);testRequests.length=0;
   const context=new AudioContext(),destination=context.createMediaStreamDestination(),oscillator=context.createOscillator();oscillator.connect(destination);oscillator.start();await context.resume();
   Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:async()=>destination.stream});
   try {
    await startVoiceRecording();await new Promise(resolve=>setTimeout(resolve,250));selectChatPeer(testPeers.b);
    const stopped=new Promise(resolve=>mediaRecorder.addEventListener('stop',resolve,{once:true}));stopVoiceRecording();await stopped;
    for(let attempt=0;attempt<50&&!testRequests.some(item=>item.path==='/api/chat-send');attempt++)await new Promise(resolve=>setTimeout(resolve,20));
    return testRequests.find(item=>item.path==='/api/chat-send')?.body.peerId;
   }finally {destination.stream.getTracks().forEach(track=>track.stop());await context.close();}
  });assert.equal(note,'10000000-0000-0000-0000-000000000001');
  await page.close();console.log('PASS recipient: changing conversations during file read or voice recording preserves the original private recipient');
 }
 if(run('microphone')) {
  const page=await chatPage();
  const outcomes=await page.evaluate(async()=>{
   const context=new AudioContext();await context.resume();const outcomes=[];
   try {
    for(const action of ['send','cancel','leave']) {
     const destination=context.createMediaStreamDestination(),oscillator=context.createOscillator();oscillator.connect(destination);oscillator.start();
     Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:async()=>destination.stream});
     testRequests.length=0;await startVoiceRecording();await new Promise(resolve=>setTimeout(resolve,250));
     const recorder=mediaRecorder,stopped=new Promise(resolve=>recorder.addEventListener('stop',resolve,{once:true}));
     if(action==='leave')QP.emit('view_changed',{page:'settings'});else stopVoiceRecording(action==='cancel');
     const closedImmediately=destination.stream.getTracks().every(track=>track.readyState==='ended');
     await stopped;await new Promise(resolve=>setTimeout(resolve,100));
     outcomes.push({action,closedImmediately,inactive:recorder.state==='inactive',sent:testRequests.filter(item=>item.path==='/api/chat-send').length});oscillator.stop();
    }
    let release,requests=0;const lateStream=context.createMediaStreamDestination().stream;
    Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:()=>{requests++;return new Promise(resolve=>release=resolve);}});
    const pending=startVoiceRecording();await startVoiceRecording();stopVoiceRecording(true);release(lateStream);await pending;
    outcomes.push({action:'cancel-pending-permission',closedImmediately:lateStream.getTracks().every(track=>track.readyState==='ended'),requests});
    const failed=context.createMediaStreamDestination().stream,original=MediaRecorder;
    Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:async()=>failed});
    window.MediaRecorder=class {constructor(){throw Error('Unsupported recording format');}};
    try {await startVoiceRecording();}finally {window.MediaRecorder=original;}
    outcomes.push({action:'constructor-failure',closedImmediately:failed.getTracks().every(track=>track.readyState==='ended')});
   }finally {await context.close();}return outcomes;
  });
  assert.ok(outcomes.every(item=>item.closedImmediately),JSON.stringify(outcomes));assert.equal(outcomes.find(item=>item.action==='send').sent,1);
  assert.ok(outcomes.filter(item=>['cancel','leave'].includes(item.action)).every(item=>item.sent===0&&item.inactive));
  assert.equal(outcomes.find(item=>item.action==='cancel-pending-permission').requests,1);
  await page.close();console.log('PASS microphone: send/cancel/navigation release tracks, late permission is cancelled, duplicate starts blocked and recorder failure cleaned up');
 }
 if(run('markdown')) {
  const page=await chatPage();
  const result=await page.evaluate(()=>{
   const message='texto '+String.fromCharCode(96).repeat(60000),start=performance.now();
   onRealtimeMessage({peerId:testPeers.a,msgId:'markdown-regression',sender:'Ana',message,isMe:false,isConfirmed:true,time:'10:00',timestampMs:Date.now()});
   return {elapsed:performance.now()-start,rendered:!!document.querySelector('[data-message-id="markdown-regression"]')};
  });assert.ok(result.rendered);assert.ok(result.elapsed<1000,JSON.stringify(result));
  await page.close();console.log('PASS Markdown UI: the 60 KB audit message renders without blocking ('+Math.round(result.elapsed)+' ms)');
 }
 if(run('calls')) {
  const page=await browser.newPage();
  const bridge=`window.testCalls={setup(){
   const id='10000000-0000-0000-0000-000000000001',peer={id,name:'Ana',isTrusted:true,isSaved:true,isQuicConnected:true};
   voiceStatus={...QP.state.status,peers:[peer],savedPeers:[peer],activeVoiceCalls:[]};voiceState={...voiceState,activeCalls:[]};QP.state.status=voiceStatus;QP.getStatus=async()=>voiceStatus;
   window.callRequests=[];QP.toast=()=>{};QP.api=async(path,options={})=>{callRequests.push({path,body:JSON.parse(options.body||'{}')});if(path==='/api/voice/state')return voiceState;return {success:true};};renderVoice();
  },start:()=>startCall('10000000-0000-0000-0000-000000000001'),hangup:hangupAll,pending:()=>callingPeers.size,
  fail:()=>onVoiceSignal({peerId:'10000000-0000-0000-0000-000000000001',signal:'call-failed'}),
  delayPermission(){ensureMicrophonePermission=()=>new Promise(resolve=>window.finishCallPermission=resolve);}
 };})();`;
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,bridge)}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.testCalls&&QP.state.status);await page.evaluate(()=>testCalls.setup());
  await page.clock.install();await page.evaluate(()=>testCalls.start());await page.clock.fastForward(11000);
  assert.equal(await page.evaluate(()=>testCalls.pending()),1);assert.equal(await page.locator('#voiceLeaveBtn').isDisabled(),false);
  await page.evaluate(()=>{
   const api=QP.api;let fail=true;
   QP.api=async(path,options)=>{if(path==='/api/voice-hangup'&&fail){fail=false;throw Error('Hangup transport failure');}return api(path,options);};
  });
  await page.locator('#voiceInviteBtn').click();
  await page.getByRole('button',{name:'Cancelar llamada a Ana',exact:true}).click();
  assert.equal(await page.evaluate(()=>testCalls.pending()),1,'A rejected hangup hid the still-pending call');
  await page.getByRole('button',{name:'Cancelar llamada a Ana',exact:true}).click();
  assert.ok(await page.evaluate(()=>callRequests.some(request=>request.path==='/api/voice-hangup'&&request.body.peerId==='10000000-0000-0000-0000-000000000001')));
  await page.evaluate(()=>testCalls.start());await page.evaluate(()=>testCalls.fail());assert.equal(await page.evaluate(()=>testCalls.pending()),0);
  await page.evaluate(async()=>{
   await testCalls.start();const api=QP.api;
   QP.api=async(path,options)=>{if(path==='/api/voice-hangup'){testCalls.fail();throw Error('Response lost after call ended');}return api(path,options);};
   await testCalls.hangup();QP.api=api;
  });assert.equal(await page.evaluate(()=>testCalls.pending()),0,'A late HTTP failure resurrected an ended call');
  await page.evaluate(()=>{testCalls.setup();testCalls.delayPermission();window.waitingCall=testCalls.start();});
  await page.waitForFunction(()=>window.finishCallPermission);await page.evaluate(()=>testCalls.hangup());
  await page.evaluate(async()=>{finishCallPermission();await waitingCall;});
  assert.ok(await page.evaluate(()=>!callRequests.some(request=>request.path==='/api/connect-peer')),'A cancelled permission request started a call later');
  await page.close();console.log('PASS outgoing calls: cancellation survives 10 seconds, sends hangup, server failure clears state and late permission cannot resurrect the call');
 }
 if(run('attachments')) {
  const page=await chatPage();
  const result=await page.evaluate(async()=>{
   const requests=[];let release,active=0,maxActive=0;
   const gate=new Promise(resolve=>release=resolve);
   const settled=async()=>{for(let i=0;i<250&&(preparingAttachments||[...attachmentUploads.values()].some(job=>job.state==='queued'));i++)await new Promise(resolve=>setTimeout(resolve,20));if(preparingAttachments)throw Error('Attachment queue did not settle');};
   QP.api=async(path,options={})=>{
    if(path==='/api/status')return testStatus;
    if(path==='/api/chat-send'){active++;maxActive=Math.max(maxActive,active);requests.push(JSON.parse(options.body));if(requests.length===1)await gate;active--;}
    return {success:true,messages:[],states:[],pins:[]};
   };
   const pending=['uno','dos','tres'].map(name=>sendAttachment(new File([name],name+'.png',{type:'image/png'})));
   await new Promise(resolve=>setTimeout(resolve,100));const whileBlocked=requests.length;release();await Promise.all(pending);await settled();
   // A failed first request must not discard or deadlock the next attachment.
   let attempts=0;
   QP.api=async(path)=>{if(path==='/api/chat-send'&&++attempts===1)throw Error('Simulated send failure');return {success:true,messages:[],states:[],pins:[]};};
   await Promise.all(['a','b'].map(name=>sendAttachment(new File([name],name+'.png',{type:'image/png'}))));await settled();
   return {whileBlocked,maxActive,names:requests.map(request=>JSON.parse(request.message).name),recipients:requests.map(request=>request.peerId),attempts,busy:sendingPeers.size,tails:sendCompletions.size,notices:testNotices};
  });
  assert.equal(result.whileBlocked,1);assert.equal(result.maxActive,1);assert.deepEqual([...result.names].sort(),['dos.png','tres.png','uno.png']);
  assert.ok(result.recipients.every(id=>id==='10000000-0000-0000-0000-000000000001'));assert.equal(result.attempts,2);assert.equal(result.busy,0);assert.equal(result.tails,0);assert.ok(result.notices.includes('Simulated send failure'));
  await page.close();console.log('PASS multiple attachments: all files sent serially per recipient; errors do not discard the following file or leave a stuck queue');
 }
 if(run('watch')) {
  const page=await browser.newPage({viewport:{width:390,height:844}});
  const state={local:{peerId:'10000000-0000-0000-0000-000000000001',name:'Prueba',token:'fixture'},peers:[],room:null};
  let version=0,rejectLeave=true;
  await page.routeWebSocket('**/watch/ws?**',ws=>ws.send(JSON.stringify({type:'status',data:state})));
  await page.route('**/watch/api/status',route=>route.fulfill({json:state}));
  await page.route('**/watch/api/room/create',route=>{state.room={roomId:'room-'+(++version),name:'Sala de prueba',ownerPeerId:state.local.peerId,isOwner:true,myRole:'Owner',media:null,playback:null};return route.fulfill({json:state});});
  await page.route('**/watch/api/room/leave',route=>{if(rejectLeave){rejectLeave=false;return route.fulfill({status:500,json:{success:false,error:'Fallo de salida simulado'}});}state.room=null;return route.fulfill({json:state});});
  await page.goto(url+'watch/');await page.locator('#createRoom').click();await page.getByRole('button',{name:'Cerrar sala',exact:true}).waitFor();
  await page.getByRole('button',{name:'Cerrar sala',exact:true}).click();await page.locator('#toast').filter({hasText:'Fallo de salida simulado'}).waitFor();
  await page.waitForFunction(()=>!document.getElementById('leaveRoom').disabled);assert.equal(await page.locator('#createRoom').isDisabled(),true);
  await page.getByRole('button',{name:'Cerrar sala',exact:true}).click();await page.waitForFunction(()=>!document.getElementById('createRoom').disabled);
  assert.equal(await page.locator('#leaveRoom').isVisible(),false);assert.equal(await page.locator('#player').getAttribute('src'),null);
  await page.reload();await page.waitForFunction(()=>!document.getElementById('createRoom').disabled);await page.locator('#createRoom').click();
  assert.equal(version,2);state.room.isOwner=false;state.room.myRole='Viewer';await page.reload();
  await page.getByRole('button',{name:'Salir de la sala',exact:true}).click();await page.waitForFunction(()=>!document.getElementById('createRoom').disabled);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
  await page.close();console.log('PASS watch rooms: owner close, viewer leave, retry on error, reload, recreation, player cleanup and mobile fit');
 }
 if(run('menus')) {
  const page=await chatPage();
  const result=await page.evaluate(()=>{
   const original={peerId:testPeers.a,msgId:'stable-menu',sender:'Tú',message:'Mensaje con menú',isMe:true,isConfirmed:false,time:'10:00',timestampMs:Date.now()};
   onRealtimeMessage(original);
   const row=document.querySelector('[data-message-id="stable-menu"]'),menu=row.querySelector('.message-menu');menu.open=true;
   const copy=[...menu.querySelectorAll('button')].find(button=>button.textContent==='Copiar contenido');copy.focus();
   onRealtimeMessage({peerId:testPeers.a,msgId:'another-message',sender:'Ana',message:'Nuevo mensaje',isMe:false,isConfirmed:true,time:'10:01',timestampMs:Date.now()+1});
   const unchanged=row===document.querySelector('[data-message-id="stable-menu"]')&&menu.open&&document.activeElement===copy;
   onRealtimeAck({msgId:'stable-menu'});
   const changed=document.querySelector('[data-message-id="stable-menu"]');
   return {unchanged,ackMenuOpen:changed.querySelector('.message-menu').open,focused:document.activeElement?.textContent};
  });assert.ok(result.unchanged);assert.ok(result.ackMenuOpen);assert.equal(result.focused,'Copiar contenido');
  await page.close();console.log('PASS chat stability: new messages retain row/menu/focus; delivery changes preserve the open menu and action focus');
 }
} finally {await closeBrowserFixture(browser, server);}
