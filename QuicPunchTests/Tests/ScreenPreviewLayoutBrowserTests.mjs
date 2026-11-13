import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Native viewport geometry only; actual decoding and presentation are tested through the C ABI.
import assert from 'node:assert/strict';
import {mkdirSync} from 'node:fs';

const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 mkdirSync('.artifacts/screen-preview',{recursive:true});
 const bridge=`window.previewTest={
  async setup(){
   voiceStatus={...await QP.getStatus(),activeVoiceCalls:[{peerId:'00000000-0000-0000-0000-000000000001',peerName:'Ana'}]};
   voiceState={...voiceState,activeCalls:[{peerId:'00000000-0000-0000-0000-000000000001',peerName:'Ana',screenShareAllowed:true}],isScreenSharing:true};
   QP.on('status_updated',()=>{voiceStatus.activeVoiceCalls=[...voiceState.activeCalls];renderVoice();});renderVoice();
  },
  async frame(width,height){
   createOrUpdateScreenTile('local','Tú · Pantalla',width,height,1,60,true);
  }
 };})();`;
 const source=readVoiceSource().replace(/\}\)\(\);\s*$/,bridge);
 for(const dpr of [1,1.25,2]){
  const context=await browser.newContext({viewport:{width:1600,height:900},deviceScaleFactor:dpr});const page=await context.newPage(),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/voice.js',r=>r.fulfill({contentType:'text/javascript',body:source}));
  await page.route('**/api/voice/state',r=>r.fulfill({json:{activeCalls:[{peerId:'00000000-0000-0000-0000-000000000001',peerName:'Ana'}],isScreenSharing:true}}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.previewTest);await page.evaluate(()=>previewTest.setup());
  for(const viewport of [{width:1600,height:900},{width:1280,height:720},{width:2560,height:1440},{width:1024,height:768}]){
   await page.setViewportSize(viewport);
   for(const [width,height] of [[2560,1440],[3840,2160],[720,1280]]){
    await page.evaluate(({width,height})=>previewTest.frame(width,height),{width,height});
    await page.locator('.is-local-stream .screen-tile-body').waitFor();
    await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
    const bounds=await page.evaluate(()=>{
     const box=s=>{const r=document.querySelector(s).getBoundingClientRect();return {left:r.left,top:r.top,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
     return {media:box('.is-local-stream .screen-tile-body'),body:box('.is-local-stream'),header:box('.is-local-stream .screen-tile-header'),room:box('#voiceRoomMedia'),dock:box('.voice-dock'),overflow:document.documentElement.scrollWidth-innerWidth};
    });
    assert.ok(bounds.media.top>=bounds.room.top&&bounds.media.bottom<=bounds.room.bottom+1&&bounds.media.right<=bounds.dock.left+1,`Preview clipped by the call room at ${JSON.stringify({dpr,viewport,width,height,bounds})}`);
    assert.ok(bounds.media.left>=bounds.body.left-1&&bounds.media.right<=bounds.body.right+1&&bounds.media.height>0&&bounds.overflow<=1,'Preview exceeds its viewport');
    assert.ok(bounds.media.top>=bounds.header.bottom-1,'Native surface covers the web controls');
    assert.equal(await page.locator('.is-local-stream canvas,.is-local-stream video').count(),0,'Browser media decoder returned');
    if(viewport.width===1600&&width===2560)await page.screenshot({path:`.artifacts/screen-preview/dpr-${dpr}.png`});
   }
  }
  assert.deepEqual(errors,[]);await context.close();
 }
 console.log('PASS native preview viewport: 1440p/4K/portrait metadata, 1024–2560px, DPR 1–2, no browser media and no overlap with call controls');
}finally{await closeBrowserFixture(browser, server);}
