import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture, findFfmpeg} from '../Fixtures/BrowserFixture.mjs';
// Synthetic FFmpeg VP9 frame -> production WebSocket/WebCodecs decoder -> real browser input.
// Never captures a screen, camera, microphone or real peer. Requires a local FFmpeg installation.
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdirSync} from 'node:fs';

const width = 2560, height = 1440;
const markers = [[0,0,'ee3333'],[2252,0,'33dd66'],[0,1267,'3366ee'],[2252,1267,'eedd33']];
const filter = markers.map(([x,y,color]) => `drawbox=x=${x}:y=${y}:w=308:h=173:color=0x${color}:t=fill`).join(',');
const ivf = execFileSync(findFfmpeg(), ['-hide_banner','-loglevel','error','-nostdin','-f','lavfi','-i',`color=c=0x1a3446:s=${width}x${height}:r=15`,'-vf',filter,'-frames:v','1','-c:v','libvpx-vp9','-deadline','realtime','-cpu-used','8','-lossless','1','-f','ivf','pipe:1'], {windowsHide:true,timeout:30000,maxBuffer:10*1024*1024});
assert.equal(ivf.subarray(0,4).toString(),'DKIF');
const encoded = [...ivf.subarray(44,44+ivf.readUInt32LE(32))];
const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 mkdirSync('.artifacts/screen-viewer',{recursive:true});
 const bridge = `window.viewerTest={
  frame(bytes){
   const entry=createOrUpdateScreenTile('local','Tú · Pantalla FFmpeg',${width},${height},3,15,true);
   const config=new TextEncoder().encode(JSON.stringify({c:'vp09.00.50.08',t:0,g:1}));
   const packet=new Uint8Array(21+config.length+bytes.length);packet[0]=2;packet[17]=3;packet[18]=3;
   packet[19]=config.length>>8;packet[20]=config.length&255;packet.set(config,21);packet.set(bytes,21+config.length);
   onWsBinaryReceived(packet.buffer);
  }
 };})();`;
 const source = readVoiceSource().replace(/\}\)\(\);\s*$/,bridge);
 for (const mobile of [false,true]) {
  const context = await browser.newContext({viewport:mobile?{width:390,height:844}:{width:1280,height:800},isMobile:mobile,hasTouch:mobile,deviceScaleFactor:mobile?2:1});
  const page = await context.newPage(), errors=[];page.on('pageerror',error=>errors.push(error.message));
  await page.addInitScript(() => {Element.prototype.requestFullscreen=undefined;Element.prototype.webkitRequestFullscreen=undefined;});
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:source}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.viewerTest);await page.evaluate(data=>viewerTest.frame(data),encoded);
  await page.waitForFunction(()=>{const c=document.querySelector('.is-local-stream canvas');return c?.width===2560&&c.height===1440&&!c.classList.contains('hidden');});
  const tile=page.locator('.is-local-stream'),body=tile.locator('.screen-tile-body'),media=tile.locator('.screen-tile-media'),info=tile.locator('.screen-tile-codec');
  assert.equal(await body.getAttribute('data-zoom'),'1');
  const baseline=await body.boundingBox(),frame=await tile.locator('canvas').boundingBox();
  assert.ok(Math.abs(frame.width-baseline.width)<1&&Math.abs(frame.height-baseline.height)<1,'Decoded 1440p frame imposed its intrinsic size on the local preview');
  await tile.locator('.screen-fullscreen-btn').click();
  const box=await body.boundingBox(),center={x:box.width/2,y:box.height/2};
  assert.equal(box.y,0); assert.equal(box.height,page.viewportSize().height);
  assert.equal(await info.isVisible(),false);
  await body.click({position:center}); assert.equal(await info.isVisible(),true);
  assert.match(await tile.locator('.screen-tile-name').textContent(),/Pantalla FFmpeg/);assert.match(await info.textContent(),/VP9.*2560.*1440.*FPS/);
  assert.deepEqual(await body.boundingBox(),box,'Information panel changed the picture dimensions');
  await body.click({position:center});assert.equal(await info.isVisible(),false);
  if (mobile) {
   const cdp=await context.newCDPSession(page);
   await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{id:1,x:150,y:420},{id:2,x:240,y:420}]});
   await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{id:1,x:80,y:420},{id:2,x:310,y:420}]});
   await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
   assert.ok(Number(await body.getAttribute('data-zoom'))>2,'Two-finger gesture did not zoom the media');
   assert.equal(await info.isVisible(),false,'Pinch was mistaken for a tap');
   await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{id:1,x:180,y:420}]});
   await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{id:1,x:240,y:450}]});
   await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
   const enlarged=Number(await body.getAttribute('data-zoom'));
   await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{id:1,x:80,y:420},{id:2,x:310,y:420}]});
   await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{id:1,x:140,y:420},{id:2,x:250,y:420}]});
   await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
   assert.ok(Number(await body.getAttribute('data-zoom'))<enlarged,'Closing two fingers did not zoom back out');
   await cdp.detach();
  } else {
   await page.mouse.move(center.x,center.y);await page.mouse.wheel(0,-240);
   await page.waitForFunction(()=>Number(document.querySelector('.screen-tile-body').dataset.zoom)>1.5);
   const before=await media.evaluate(el=>new DOMMatrix(getComputedStyle(el).transform).e);
   await page.mouse.down();await page.mouse.move(center.x+90,center.y+40,{steps:8});await page.mouse.up();
   assert.ok((await media.evaluate(el=>new DOMMatrix(getComputedStyle(el).transform).e))>before,'Dragging did not pan the zoomed picture');
   assert.equal(await info.isVisible(),false,'Dragging was mistaken for a tap');
   await body.focus();await page.keyboard.press('0');assert.equal(await body.getAttribute('data-zoom'),'1');
   await page.keyboard.down('Control');await page.mouse.wheel(0,-240);await page.keyboard.up('Control');
   await page.waitForFunction(()=>Number(document.querySelector('.screen-tile-body').dataset.zoom)>1);
  }
  assert.equal(await page.evaluate(()=>visualViewport.scale),1,'Viewer zoom changed the page zoom');
  assert.equal(await page.evaluate(()=>document.getElementById('voiceDrawer').hidden),true,'Media gesture opened the call drawer');
  await tile.locator('.screen-zoom-reset').click();assert.equal(await body.getAttribute('data-zoom'),'1');
  // Fit checks use rendered pixels, excluding the intentional translucent controls.
  const dpr=mobile?2:1,scale=Math.min(box.width/width,box.height/height),w=width*scale,h=height*scale;
  const left=(box.width-w)/2,top=(box.height-h)/2;
  const points=[[.06,.06],[.94,.06],[.06,.94],[.94,.94]].map(([x,y])=>({x:(left+w*x)*dpr,y:(top+h*y)*dpr}));
  const screenshot=await page.screenshot({style:'.screen-tile-header{visibility:hidden!important}'});
  const pixels=await page.evaluate(async({bytes,points})=>{const bitmap=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:'image/png'}));const c=document.createElement('canvas');c.width=bitmap.width;c.height=bitmap.height;const ctx=c.getContext('2d');ctx.drawImage(bitmap,0,0);bitmap.close();return points.map(p=>[...ctx.getImageData(Math.round(p.x),Math.round(p.y),1,1).data].slice(0,3));},{bytes:[...screenshot],points});
  const expected=[[238,51,51],[51,221,102],[51,102,238],[238,221,51]];
  pixels.forEach((pixel,i)=>assert.ok(pixel.every((value,c)=>Math.abs(value-expected[i][c])<25),`FFmpeg corner ${i} was cropped: ${pixel}`));
  await tile.locator('.screen-info-btn').click();
  await page.screenshot({path:`.artifacts/screen-viewer/${mobile?'mobile':'desktop'}-overlay.png`});
  await tile.locator('.screen-fullscreen-btn').click();assert.equal(await body.getAttribute('data-zoom'),'1');
  assert.deepEqual(errors,[]);await context.close();
 }
 console.log('PASS FFmpeg 1440p local preview: real VP9 decoding, full-frame pixels, overlay metadata without layout loss, wheel/Ctrl-wheel, pinch, drag, reset, no page zoom or drawer gestures; desktop and mobile.');
} finally {await closeBrowserFixture(browser, server);}
