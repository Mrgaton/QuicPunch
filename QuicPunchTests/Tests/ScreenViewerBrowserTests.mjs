import {launchBrowser,startPreviewServer,closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';
const server=startPreviewServer([]);let browser;
try{
 const url=await server.ready;browser=await launchBrowser();const page=await browser.newPage({viewport:{width:1366,height:900}}),errors=[];
 let layouts=0;
 page.on('pageerror',e=>errors.push(e.message));
 const isLayout=r=>r.method()==='POST'&&new URL(r.url()).pathname.endsWith('/native-video/layout');
 const nextLayout=async (action,matches=()=>true)=>{
  const response=page.waitForResponse(r=>isLayout(r.request())&&matches(r.request().postDataJSON()),{timeout:5000});
  await action();
  return (await response).request().postDataJSON();
 };
 await page.route(/\/api\/voice\/native-video(?:[/?]|$)/,async route=>{
  if(route.request().method()==='POST'){if(isLayout(route.request()))layouts++;await new Promise(r=>setTimeout(r,30));return route.fulfill({json:{success:true}});}
  return route.fulfill({json:{local:{available:true,engine:'FFmpeg fixture',decoderMask:58,hardwareMask:2,decoders:[{codecId:1,hardware:true,software:true,status:'hardware',reason:'Synthetic fixture',testWidth:640,testHeight:360},{codecId:5,hardware:false,software:true,status:'software',reason:'Fixture',testWidth:640,testHeight:360},{codecId:3,hardware:false,software:false,status:'unsupported',reason:'Fixture',testWidth:640,testHeight:360}]},peers:[{peerId:'10000000-0000-0000-0000-000000000001',name:'<img src=x> Ana',capabilities:null}],streams:[]}});
 });
 await page.goto(url+'call.html');await page.waitForFunction(()=>window.QuicNetNativeVideo);
 const initial=await nextLayout(()=>page.evaluate(async()=>{
  await QuicNetNativeVideo.refresh();
  const parent=document.createElement('div');parent.id='native-clip-test';Object.assign(parent.style,{position:'fixed',top:'80px',left:'10px',width:'200px',height:'100px',overflow:'hidden'});
  const target=document.createElement('div');target.style.width='400px';target.style.height='200px';parent.append(target);document.body.append(parent);
  window.nativeEntry={peerId:'local',isLocal:true,nativeTarget:target,loadingEl:document.createElement('div'),codecBadge:document.createElement('div'),cardEl:parent};
  QuicNetNativeVideo.attach(nativeEntry,target);
 }));
 assert.equal(initial.width,400);assert.equal(initial.clipWidth,200);assert.equal(initial.clipHeight,100);
 const count=layouts;await page.waitForTimeout(100);assert.equal(layouts,count,'Idle geometry must not poll');
 const inFlight=page.waitForRequest(r=>isLayout(r)&&r.postDataJSON().width===420);
 await page.evaluate(()=>{nativeEntry.nativeTarget.style.width='420px';QuicNetNativeVideo.schedule();});
 await inFlight;
 const latest=await nextLayout(()=>page.evaluate(()=>{nativeEntry.nativeTarget.style.width='460px';QuicNetNativeVideo.schedule();}),l=>l.width===460);
 assert.equal(latest.width,460,'Last geometry must win');
 const covered=await nextLayout(()=>page.evaluate(()=>{const d=document.createElement('dialog');d.id='native-dialog-test';document.body.append(d);d.showModal();}));
 assert.equal(covered.visible,false);
 const uncovered=await nextLayout(()=>page.evaluate(()=>document.getElementById('native-dialog-test').close()));
 assert.equal(uncovered.visible,true);
 const fitted=await nextLayout(()=>page.evaluate(()=>QP.emit('native_video_state',{peerId:'local',mode:'hardware',width:640,height:360,presented:7,codecId:5,fps:27,bitrate:2500000})));
 assert.equal(await page.evaluate(()=>nativeEntry.codecBadge.textContent),'Envío · H.265 · 640×360 · 27 FPS · 2,5 Mb/s');
 assert.ok(Math.abs(fitted.width/fitted.height-640/360)<.01,'Native surface stretches the decoded aspect ratio');
 assert.match(await page.locator('#nativeDecoderMatrix').textContent(),/Software✓/);
 assert.match(await page.locator('#nativeDecoderMatrix').textContent(),/No compatible/);
 assert.equal(await page.locator('#nativeDecoderMatrix img').count(),0);
 assert.equal(await page.locator('#nativeDecoderMatrix tr:first-child td[data-label="H.264"]').count(),1,'Mobile rows require codec labels');
 const playback=page.waitForRequest(r=>r.method()==='POST'&&new URL(r.url()).pathname.endsWith('/native-video/playback'));
 await page.evaluate(()=>{document.getElementById('nativePlaybackPreset').value='80';document.getElementById('nativePlaybackPreset').dispatchEvent(new Event('change'));});
 assert.deepEqual((await playback).postDataJSON(),{bufferMs:80,maxQueueFrames:16,decoder:'auto'});
 const detached=await nextLayout(()=>page.evaluate(()=>QuicNetNativeVideo.detach('local')));
 assert.equal(detached.visible,false);
 assert.deepEqual(errors,[]);console.log('PASS native surface bridge: clipped geometry, ordered changes, no idle polling, modal visibility, truthful telemetry, escaped capability matrix and playback buffer.');
}finally{await closeBrowserFixture(browser,server);}
