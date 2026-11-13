import {launchBrowser,startPreviewServer,closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';
const server=startPreviewServer([]);let browser;
try{
 const url=await server.ready;browser=await launchBrowser();const page=await browser.newPage({viewport:{width:1366,height:900}}),requests=[],errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/api/voice/native-video**',async route=>{
  if(route.request().method()==='POST'){requests.push({path:new URL(route.request().url()).pathname,body:route.request().postDataJSON()});await new Promise(r=>setTimeout(r,30));return route.fulfill({json:{success:true}});}
  return route.fulfill({json:{local:{available:true,engine:'FFmpeg fixture',decoderMask:58,hardwareMask:2,decoders:[{codecId:1,hardware:true,software:true,status:'hardware',reason:'Synthetic fixture',testWidth:640,testHeight:360},{codecId:5,hardware:false,software:true,status:'software',reason:'Fixture',testWidth:640,testHeight:360},{codecId:3,hardware:false,software:false,status:'unsupported',reason:'Fixture',testWidth:640,testHeight:360}]},peers:[{peerId:'10000000-0000-0000-0000-000000000001',name:'<img src=x> Ana',capabilities:null}],streams:[]}});
 });
 await page.goto(url+'call.html');await page.waitForFunction(()=>window.QuicNetNativeVideo);await page.evaluate(async()=>{
  await QuicNetNativeVideo.refresh();
  const parent=document.createElement('div');parent.id='native-clip-test';Object.assign(parent.style,{position:'fixed',top:'80px',left:'10px',width:'200px',height:'100px',overflow:'hidden'});
  const target=document.createElement('div');target.style.width='400px';target.style.height='200px';parent.append(target);document.body.append(parent);
  window.nativeEntry={peerId:'local',nativeTarget:target,loadingEl:document.createElement('div'),codecBadge:document.createElement('div'),cardEl:parent};
  QuicNetNativeVideo.attach(nativeEntry,target);
 });
 await page.waitForTimeout(100);const layouts=()=>requests.filter(r=>r.path.endsWith('/layout'));
 assert.equal(layouts().at(-1).body.width,400);assert.equal(layouts().at(-1).body.clipWidth,200);assert.equal(layouts().at(-1).body.clipHeight,100);
 const count=layouts().length;await page.waitForTimeout(100);assert.equal(layouts().length,count,'Idle geometry must not poll');
 await page.evaluate(()=>{nativeEntry.nativeTarget.style.width='420px';QuicNetNativeVideo.schedule();});
 await page.waitForTimeout(10);await page.evaluate(()=>{nativeEntry.nativeTarget.style.width='460px';QuicNetNativeVideo.schedule();});
 await page.waitForTimeout(140);assert.equal(layouts().at(-1).body.width,460,'Last geometry must win');
 await page.evaluate(()=>{const d=document.createElement('dialog');d.id='native-dialog-test';document.body.append(d);d.showModal();});await page.waitForTimeout(100);assert.equal(layouts().at(-1).body.visible,false);
 await page.evaluate(()=>document.getElementById('native-dialog-test').close());await page.waitForTimeout(100);assert.equal(layouts().at(-1).body.visible,true);
 await page.evaluate(()=>QP.emit('native_video_state',{peerId:'local',mode:'hardware',width:640,height:360,decoded:8,presented:7,dropped:1,decodeMs:.4}));
 assert.match(await page.evaluate(()=>nativeEntry.codecBadge.textContent),/hardware verificado/);
 await page.waitForTimeout(100);const fitted=layouts().at(-1).body;assert.ok(Math.abs(fitted.width/fitted.height-640/360)<.01,'Native surface stretches the decoded aspect ratio');
 assert.match(await page.locator('#nativeDecoderMatrix').textContent(),/Software✓/);
 assert.match(await page.locator('#nativeDecoderMatrix').textContent(),/No compatible/);
 assert.equal(await page.locator('#nativeDecoderMatrix img').count(),0);
 assert.equal(await page.locator('#nativeDecoderMatrix tr:first-child td[data-label="H.264"]').count(),1,'Mobile rows require codec labels');
 await page.evaluate(()=>{document.getElementById('nativePlaybackPreset').value='80';document.getElementById('nativePlaybackPreset').dispatchEvent(new Event('change'));});await page.waitForTimeout(80);
 assert.deepEqual(requests.filter(r=>r.path.endsWith('/playback')).at(-1).body,{bufferMs:80,maxQueueFrames:16,decoder:'auto'});
 await page.evaluate(()=>QuicNetNativeVideo.detach('local'));await page.waitForTimeout(80);assert.equal(layouts().at(-1).body.visible,false);
 assert.deepEqual(errors,[]);console.log('PASS native surface bridge: clipped geometry, ordered changes, no idle polling, modal visibility, truthful telemetry, escaped capability matrix and playback buffer.');
}finally{await closeBrowserFixture(browser,server);}
