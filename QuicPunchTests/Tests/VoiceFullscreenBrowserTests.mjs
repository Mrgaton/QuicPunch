import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Synthetic call/video frames; no camera, microphone, screen capture or real peer.
import assert from 'node:assert/strict';
import {mkdirSync} from 'node:fs';

const server = startPreviewServer([]);
const remote='00000000-0000-0000-0000-000000000001';
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 mkdirSync('.artifacts/voice-fullscreen',{recursive:true});
 const source=readVoiceSource();
 const bridge=`window.testVideo={
  setup(){
   for(const id of [...activeScreenStreams.keys()])removeScreenTile(id);
   for(const [id,name,local] of [['${remote}','Ana · Pantalla',false],['local','Tú · Cámara',true]]){
    const e=createOrUpdateScreenTile(id,name,1280,720,1,30,local);
    e.videoEl.classList.add('hidden');e.loadingEl.classList.add('hidden');e.canvasEl.classList.remove('hidden');
    e.canvasEl.width=640;e.canvasEl.height=360;e.canvasCtx.fillStyle='#22aa66';e.canvasCtx.fillRect(0,0,640,360);
   }
  },remove:removeScreenTile,vad:onVoiceVad
 };})();`;
 for(const mode of ['native','rejected','unsupported']){
  const context=await browser.newContext({viewport:{width:390,height:844},isMobile:mode!=='native',hasTouch:mode!=='native'});
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  if(mode!=='native')await page.addInitScript(mode=>{
   if(mode==='rejected'){Element.prototype.requestFullscreen=function(){return Promise.reject(new TypeError('Fixture: native request rejected'));};Object.defineProperty(document,'fullscreenEnabled',{get:()=>true});}
   else {Element.prototype.requestFullscreen=undefined;Element.prototype.webkitRequestFullscreen=undefined;}
  },mode);
  await page.route('**/voice.js',r=>r.fulfill({contentType:'text/javascript',body:source.replace(/\}\)\(\);\s*$/,bridge)}));
  await page.route('**/api/voice/state',r=>r.fulfill({json:{isMuted:false,isDeafened:false,masterVolume:1,isScreenSharing:true,activeCalls:[{peerId:remote,peerName:'Ana',volume:1,screenShareAllowed:true}]}}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.testVideo);await page.evaluate(()=>testVideo.setup());
  const tile=page.locator(`.screen-tile[data-stream-peer-id="${remote}"]`),button=tile.locator('.screen-fullscreen-btn');
  assert.equal(await page.locator('#screenShareGlobalLiveBadge').isVisible(),false);
  assert.equal(await page.locator('.screen-tile-live-badge:visible').count(),0,'Mobile removes remote and local badges');
  const controlCenters=await tile.locator('.screen-tile-actions .btn').evaluateAll(nodes=>nodes.map(n=>{const r=n.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height};}));
  const tileBounds=await tile.boundingBox();
  assert.ok(controlCenters.every(r=>r.w>=44&&r.h>=44&&r.x>=tileBounds.x&&r.x+r.w<=tileBounds.x+tileBounds.width),'Mobile video controls wrap inside the tile and remain touch sized: '+JSON.stringify(controlCenters));
  await tile.evaluate(el=>{window.originalMedia=el.querySelector('canvas');window.originalVideo=el.querySelector('video');});
  await page.evaluate(id=>testVideo.vad({peerId:id,speaking:true}),remote);
  assert.equal(await tile.evaluate(el=>el.classList.contains('is-speaking')),true);
  await button.click();await page.waitForFunction(()=>!document.querySelector('.screen-fullscreen-btn').disabled);
  if(mode==='native')assert.equal(await page.evaluate(()=>document.fullscreenElement?.dataset.streamPeerId),remote,'Browser must enter native fullscreen');
  async function assertExpanded(){
   const b=await tile.boundingBox();const viewport=page.viewportSize();
   assert.ok(Math.abs(b.x)<1&&Math.abs(b.y)<1&&Math.abs(b.width-viewport.width)<1&&Math.abs(b.height-viewport.height)<1,`${mode}: video does not fill viewport ${JSON.stringify(b)}`);
   assert.equal(await button.getAttribute('aria-label'),'Salir de pantalla completa');
   assert.equal(await page.evaluate(()=>originalMedia===document.querySelector('.is-tile-fullscreen canvas')&&originalVideo===document.querySelector('.is-tile-fullscreen video')),true,'Fullscreen remounted media');
   assert.deepEqual(await page.evaluate(()=>[...originalMedia.getContext('2d').getImageData(10,10,1,1).data]),[34,170,102,255]);
   const body=await tile.locator('.screen-tile-body').boundingBox(),exit=await button.boundingBox();
   assert.ok(exit.y>=0&&exit.y+exit.height<=viewport.height,'Exit control leaves the viewport');
   assert.ok(Math.abs(body.x)<1&&Math.abs(body.y)<1&&Math.abs(body.width-viewport.width)<1&&Math.abs(body.height-viewport.height)<1,'Header or safe-area padding steals space from the video');
   assert.equal(await tile.locator('.screen-tile-codec').isVisible(),false,'Codec details remain optional in fullscreen');
   assert.equal(await tile.evaluate(el=>getComputedStyle(el).boxShadow),'none','Speaking must not illuminate fullscreen video');
  }
  await assertExpanded();await page.screenshot({path:`.artifacts/voice-fullscreen/${mode}-portrait.png`});
  await tile.locator('.screen-tile-body').focus();
  await page.waitForFunction(()=>document.getElementById('view-voice').classList.contains('is-media-idle'));
  assert.equal(await tile.locator('.screen-tile-name').isVisible(),false,'Fullscreen names hide after inactivity');
  assert.equal(await tile.locator('.screen-tile-actions').isVisible(),false,'Fullscreen overlays hide after inactivity');
  await tile.locator('.screen-tile-body').hover();
  assert.equal(await tile.locator('.screen-tile-name').isVisible(),true,'Fullscreen identity returns on movement');
  await page.setViewportSize({width:844,height:390});await assertExpanded();
  if(mode!=='native')assert.equal(await page.locator('.screen-tile-live-badge:visible').count(),0,'Landscape touch UI keeps badges hidden');
  await page.screenshot({path:`.artifacts/voice-fullscreen/${mode}-landscape.png`});
  await button.click();await page.waitForFunction(()=>!document.fullscreenElement&&!document.querySelector('.is-tile-fullscreen'));
  assert.equal(await button.getAttribute('aria-pressed'),'false');
  await button.click();await page.waitForFunction(()=>!document.querySelector('.screen-fullscreen-btn').disabled);await page.keyboard.press('Escape');
  await page.waitForFunction(()=>!document.fullscreenElement&&!document.querySelector('.is-tile-fullscreen'));
  await button.click();await page.waitForFunction(()=>!document.querySelector('.screen-fullscreen-btn').disabled);await page.evaluate(id=>testVideo.remove(id),remote);
  await page.waitForFunction(()=>!document.fullscreenElement&&!document.querySelector('.is-tile-fullscreen'));
  await page.evaluate(()=>testVideo.setup());await page.locator('.is-local-stream .screen-fullscreen-btn').click();
  await page.waitForFunction(()=>!document.querySelector('.is-local-stream .screen-fullscreen-btn').disabled);
  await page.evaluate(()=>QP.navigate('dashboard'));await page.waitForFunction(()=>!document.fullscreenElement&&!document.querySelector('.is-tile-fullscreen'));
  assert.equal(await page.locator('#view-dashboard').isVisible(),true);
  assert.deepEqual(errors,[]);await context.close();
 }
 console.log('PASS voice fullscreen: native/rejected/unsupported, edge-to-edge media with overlay controls, hidden identity, media continuity, mobile touch targets, rotation, exit/Escape, stream end and navigation');
}finally{await closeBrowserFixture(browser, server);}
