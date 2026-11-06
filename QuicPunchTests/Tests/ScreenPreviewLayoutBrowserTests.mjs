import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// A synthetic desktop with four corner markers goes through the real local JPEG
// decode path. Pixel checks use browser screenshots, not just canvas contents.
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
   const c=document.createElement('canvas');c.width=width;c.height=height;const x=c.getContext('2d');
   x.fillStyle='#1a3446';x.fillRect(0,0,width,height);
   for(const [dx,dy,color] of [[0,0,'#ee3333'],[.88,0,'#33dd66'],[0,.88,'#3366ee'],[.88,.88,'#eedd33']]){x.fillStyle=color;x.fillRect(dx*width,dy*height,.12*width,.12*height);}
   x.fillStyle='white';x.font=Math.floor(height/14)+'px sans-serif';x.fillText('PANTALLA COMPLETA',width*.16,height*.5);
   createOrUpdateScreenTile('local','Tú · Pantalla',width,height,0,60,true);
   const blob=await new Promise(r=>c.toBlob(r,'image/jpeg',.95)),bytes=new Uint8Array(await blob.arrayBuffer()),wire=new Uint8Array(18+bytes.length);
   wire[0]=2;wire[17]=0;wire.set(bytes,18);onWsBinaryReceived(wire.buffer);
  }
 };})();`;
 const source=readVoiceSource().replace(/\}\)\(\);\s*$/,bridge);
 const colors=[[238,51,51],[51,221,102],[51,102,238],[238,221,51]];
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
    await page.waitForFunction(({width,height})=>{const c=document.querySelector('.is-local-stream canvas');return c?.width===width&&c.height===height&&!c.classList.contains('hidden');},{width,height});
    await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
    const bounds=await page.evaluate(()=>{
     const box=s=>{const r=document.querySelector(s).getBoundingClientRect();return {left:r.left,top:r.top,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
     return {media:box('.is-local-stream canvas'),body:box('.is-local-stream .screen-tile-body'),room:box('#voiceRoomMedia'),dock:box('.voice-dock'),overflow:document.documentElement.scrollWidth-innerWidth};
    });
    assert.ok(bounds.media.top>=bounds.room.top&&bounds.media.bottom<=bounds.room.bottom+1&&bounds.media.right<=bounds.dock.left+1,`Preview clipped by the call room at ${JSON.stringify({dpr,viewport,width,height,bounds})}`);
    assert.ok(bounds.media.left>=bounds.body.left-1&&bounds.media.right<=bounds.body.right+1&&bounds.media.height>0&&bounds.overflow<=1,'Preview exceeds its viewport');
    const scale=Math.min(bounds.media.width/width,bounds.media.height/height),w=width*scale,h=height*scale;
    const left=bounds.media.left+(bounds.media.width-w)/2,top=bounds.media.top+(bounds.media.height-h)/2;
    const points=[[.06,.06],[.94,.06],[.06,.94],[.94,.94]].map(([x,y])=>({x:(left+w*x)*dpr,y:(top+h*y)*dpr}));
    // Pixel-fit checks exclude the deliberately translucent controls over the picture.
    const shot=await page.screenshot({style:'.screen-tile-header { visibility:hidden!important; }',...(viewport.width===1600&&width===2560?{path:`.artifacts/screen-preview/dpr-${dpr}.png`}:{})});
    const pixels=await page.evaluate(async({bytes,points})=>{const b=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:'image/png'})),c=document.createElement('canvas');c.width=b.width;c.height=b.height;const ctx=c.getContext('2d');ctx.drawImage(b,0,0);b.close();return points.map(p=>[...ctx.getImageData(Math.round(p.x),Math.round(p.y),1,1).data].slice(0,3));},{bytes:[...shot],points});
    pixels.forEach((pixel,i)=>assert.ok(pixel.every((v,c)=>Math.abs(v-colors[i][c])<25),`Corner ${i} is hidden or cropped: ${JSON.stringify({pixel,dpr,viewport,width,height})}`));
   }
  }
  assert.deepEqual(errors,[]);await context.close();
 }
 console.log('PASS local preview: full-frame corner pixels, JPEG decode, 1440p/4K/portrait, 1024–2560px, DPI/zoom layouts and no clipping behind call controls');
}finally{await closeBrowserFixture(browser, server);}
