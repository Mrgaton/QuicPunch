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
 const bridge=`const testCall={peerId:'${remote}',peerName:'Ana'};
 QP.on('status_updated',status=>{
  status.activeVoiceCalls=[testCall];
  voiceStatus=status;
  renderVoice();
 });
 window.testVideo={
  setup(){
   for(const id of [...activeScreenStreams.keys()])removeScreenTile(id);
   renderActiveCalls([testCall],[]);
   for(const [id,name,local] of [['${remote}','Ana · Pantalla',false],['local','Tú · Cámara',true]]){
    const e=createOrUpdateScreenTile(id,name,1280,720,1,30,local);
    e.loadingEl.classList.add('hidden');
   }
  },remove:removeScreenTile,vad:onVoiceVad
 };})();`;
 for(const mode of ['native','rejected','unsupported']){
  const context=await browser.newContext({viewport:{width:390,height:844},isMobile:mode!=='native',hasTouch:mode!=='native'});
  const page=await context.newPage(),errors=[],layouts=new Map();page.on('pageerror',e=>errors.push(e.message));
  const requests=[];
  const current={Codec:'h264',Width:1920,Height:1080,Fps:60,Bitrate:12000000,Encoder:'libx264',
   Preset:'balanced',RateControl:'cbr',Quality:23,KeyFrameIntervalMs:1000,BufferMs:50};
  const capabilities={Version:1,Revision:7,Backend:'native',Sharing:true,CanConfigure:true,Codecs:['h264'],
   Encoders:['libx264'],Heights:[720,1080],FrameRates:[30,60],MinBitrate:500000,MaxBitrate:30000000,
   Presets:['balanced'],RateControls:['cbr'],MinQuality:0,MaxQuality:51,MinKeyFrameIntervalMs:250,
   MaxKeyFrameIntervalMs:5000,MinBufferMs:16,MaxBufferMs:250,Current:current};
  await page.route('**/api/voice/screen-share/remote',route=>{
   const request=route.request().postDataJSON();requests.push(request);
   return route.fulfill({json:{Success:true,Capabilities:{...capabilities,Current:request.settings||current}}});
  });
  await page.route('**/api/voice/native-video/layout',route=>{
   const layout=route.request().postDataJSON();layouts.set(layout.peerId,layout);
   return route.fulfill({json:{success:true}});
  });
  const waitForLayout=async (id, check)=>{
   for(let attempt=0;attempt<100&&!check(layouts.get(id));attempt++) await new Promise(resolve=>setTimeout(resolve,25));
   assert.ok(check(layouts.get(id)),'Expected native geometry was not sent: '+JSON.stringify([...layouts]));
   return layouts.get(id);
  };
  if(mode!=='native')await page.addInitScript(mode=>{
   if(mode==='rejected'){Element.prototype.requestFullscreen=function(){return Promise.reject(new TypeError('Fixture: native request rejected'));};Object.defineProperty(document,'fullscreenEnabled',{get:()=>true});}
   else {Element.prototype.requestFullscreen=undefined;Element.prototype.webkitRequestFullscreen=undefined;}
  },mode);
  await page.route('**/voice.js',r=>r.fulfill({contentType:'text/javascript',body:source.replace(/\}\)\(\);\s*$/,bridge)}));
  await page.route('**/api/voice/state',r=>r.fulfill({json:{isMuted:false,isDeafened:false,masterVolume:1,isScreenSharing:true,activeCalls:[{peerId:remote,peerName:'Ana',volume:1,screenShareAllowed:true}]}}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.testVideo);await page.evaluate(()=>testVideo.setup());
  const tile=page.locator(`.screen-tile[data-stream-peer-id="${remote}"]`),button=tile.locator('.screen-fullscreen-btn');
  const media=tile.locator('.screen-tile-body'),header=tile.locator('.screen-tile-header'),reset=tile.locator('.screen-zoom-reset');
  const tapVideo=async()=>{
   if(mode!=='native') return media.tap();
   await page.mouse.down(); await page.mouse.up();
  };
  const wakeControls=async()=>{
   if(await header.isVisible()) return;
   if(mode==='native') await page.mouse.move(20, page.viewportSize().height / 2);
   else await media.tap();
  };
  await waitForLayout(remote,l=>l?.visible);
  assert.equal(await page.locator('#screenShareGlobalLiveBadge').isVisible(),false);
  assert.equal(await page.locator('.screen-tile-live-badge:visible').count(),0,'Mobile removes remote and local badges');
  const controlCenters=await tile.locator('.screen-tile-actions .btn:visible').evaluateAll(nodes=>nodes.map(n=>{const r=n.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height};}));
  const tileBounds=await tile.boundingBox();
  assert.ok(controlCenters.every(r=>r.w>=44&&r.h>=44&&r.x>=tileBounds.x&&r.x+r.w<=tileBounds.x+tileBounds.width),'Mobile video controls wrap inside the tile and remain touch sized: '+JSON.stringify(controlCenters));
  await tile.evaluate(el=>{window.originalMedia=el.querySelector('.screen-tile-body');});
  await page.evaluate(id=>testVideo.vad({peerId:id,speaking:true}),remote);
  assert.equal(await tile.evaluate(el=>el.classList.contains('is-speaking')),true);
  await button.click();await page.waitForFunction(()=>!document.querySelector('.screen-fullscreen-btn').disabled);
  if(mode==='native')assert.equal(await page.evaluate(()=>document.fullscreenElement?.dataset.streamPeerId),remote,'Browser must enter native fullscreen');
  async function assertExpanded(){
   const b=await tile.boundingBox();const viewport=page.viewportSize();
   assert.ok(Math.abs(b.x)<1&&Math.abs(b.y)<1&&Math.abs(b.width-viewport.width)<1&&Math.abs(b.height-viewport.height)<1,`${mode}: video does not fill viewport ${JSON.stringify(b)}`);
   assert.equal(await button.getAttribute('aria-label'),'Salir de pantalla completa');
   assert.equal(await page.evaluate(()=>originalMedia===document.querySelector('.is-tile-fullscreen .screen-tile-body')),true,'Fullscreen remounted media');
   assert.equal(await tile.locator('canvas,video').count(),0,'Decoded media must not enter the WebView');
   const body=await media.boundingBox(),exit=await button.boundingBox();
   assert.ok(exit.y>=0&&exit.y+exit.height<=viewport.height,'Exit control leaves the viewport');
   assert.ok(Math.abs(body.x)<1&&Math.abs(body.y)<1&&Math.abs(body.width-viewport.width)<1&&Math.abs(body.height-viewport.height)<1,'The video viewport must stay fixed as controls hide');
   assert.equal(await tile.locator('.screen-tile-codec').isVisible(),false,'Codec details remain optional in fullscreen');
   assert.equal(await tile.evaluate(el=>getComputedStyle(el).boxShadow),'none','Speaking must not illuminate fullscreen video');
  }
  await assertExpanded();
  if(mode==='native') await media.hover();
  await tapVideo();
  assert.equal(await header.isVisible(),false,'A stationary click or tap immediately hides the controls');
  await tapVideo();
  assert.equal(await header.isVisible(),true,'A second click or tap shows the controls');
  await media.press('Space');
  assert.equal(await header.isVisible(),false,'The focused viewer can hide the controls with Space');
  await media.press('Space');
  assert.equal(await header.isVisible(),true,'Space can also show the controls again');
  const audio=tile.getByLabel('Audio de la transmisión');
  const volume=tile.getByRole('slider',{name:'Volumen del vídeo',includeHidden:true});
  assert.equal(await volume.isVisible(),false,'Volume starts collapsed');
  const toolbarBefore=await header.boundingBox();
  await audio.click();
  assert.equal(await volume.isVisible(),true);
  assert.deepEqual(await header.boundingBox(),toolbarBefore,'Opening the slider must not resize the header');
  await volume.fill('55');
  assert.equal(await volume.inputValue(),'55');
  await tile.getByRole('button',{name:'Silenciar audio del vídeo'}).click();
  assert.equal(await tile.getByRole('button',{name:'Reactivar audio del vídeo'}).isVisible(),true);
  await page.keyboard.press('Escape');
  assert.equal(await volume.isVisible(),false);
  assert.equal(await button.getAttribute('aria-pressed'),'true','Escape closes volume before fullscreen');
  await page.screenshot({path:`.artifacts/voice-fullscreen/${mode}-portrait.png`});
  const portrait=await waitForLayout(remote,l=>l?.visible&&l.width===390&&l.y>250&&l.clipWidth===390);
  assert.equal(portrait.clipHeight,portrait.height,'Maximized video must escape the inline scroll container');
  await waitForLayout('local',l=>l?.visible===false);
  await media.focus();
  await page.waitForFunction(()=>document.getElementById('view-voice').classList.contains('is-media-idle'));
  assert.equal(await header.isVisible(),false,'The identity and actions must collapse on idle');
  await wakeControls();
  assert.equal(await tile.locator('.screen-tile-name').isVisible(),true,'Fullscreen identity returns on movement');
  await page.setViewportSize({width:844,height:390});await assertExpanded();
  const fitted=await waitForLayout(remote,l=>l?.visible&&l.width>600&&l.height===390&&l.clipY>0);

  if(mode==='native'){
   await page.mouse.move(422,195);await page.mouse.wheel(0,-350);
   const zoomed=await waitForLayout(remote,l=>l?.zoom>1.5&&l.width>fitted.width);
   await page.mouse.down();await page.mouse.move(482,225,{steps:5});await page.mouse.up();
   await waitForLayout(remote,l=>l?.x>zoomed.x+40&&l.y>zoomed.y+20);
  }else{
   const cdp=await context.newCDPSession(page);
   const send=(type,...points)=>cdp.send('Input.dispatchTouchEvent',{type,touchPoints:points.map(([id,x,y])=>({id,x,y}))});
   await send('touchStart',[0,322,195]);
   await send('touchStart',[0,322,195],[1,522,195]);
   await send('touchMove',[0,252,195],[1,592,195]);
   const zoomed=await waitForLayout(remote,l=>l?.zoom>1.5&&l.width>fitted.width);
   await send('touchEnd');
   await send('touchStart',[0,252,195]);
   await send('touchMove',[0,302,225]);
   await send('touchEnd');
   await waitForLayout(remote,l=>l?.x>zoomed.x+30&&l.y>zoomed.y+20);
   await cdp.detach();
  }
  assert.match(await reset.textContent(),/^[12]\d\d%$/);
  assert.equal(await header.isVisible(),true,'Panning and pinching keep the controls visible');
  await page.setViewportSize({width:360,height:844});
  const rows=await tile.locator('.screen-tile-actions > :visible').evaluateAll(nodes=>nodes.map(el=>Math.round(el.getBoundingClientRect().top)));
  assert.equal(new Set(rows).size,1,'The fullscreen toolbar fits on one line with zoom and Solicitar control');
  const options=tile.getByLabel('Opciones de Ana');
  assert.equal(await options.isVisible(),true,'The participant menu fits on the same row');
  assert.equal(await tile.locator('.screen-transmission-btn').isVisible(),false,'Compact fullscreen uses the participant menu for settings');
  await page.screenshot({path:`.artifacts/voice-fullscreen/${mode}-compact.png`});
  await options.click();
  assert.equal(await tile.getByRole('button',{name:'Ajustes de su transmisión'}).isVisible(),true);
  await options.click();
  await page.setViewportSize({width:844,height:390});
  const activeLayout=await waitForLayout(remote,l=>l?.zoom>1.5&&l.clipWidth===844&&l.clipY>0&&l.clipHeight<390);
  await page.waitForFunction(()=>!document.querySelector('.is-tile-fullscreen .screen-tile-header').checkVisibility({visibilityProperty:true}));
  const idleLayout=await waitForLayout(remote,l=>l?.clipHeight>activeLayout.clipHeight);
  for(const key of ['x','y','width','height','zoom']) assert.equal(idleLayout[key],activeLayout[key],'Auto-hide must not shift the zoomed video');
  assert.ok(idleLayout.y+idleLayout.clipY>=0&&idleLayout.y+idleLayout.clipY+idleLayout.clipHeight<=390,'Zoomed video must be clipped to the viewport');
  await wakeControls();
  const restoredLayout=await waitForLayout(remote,l=>l?.clipHeight===activeLayout.clipHeight);
  for(const key of ['x','y','width','height','zoom']) assert.equal(restoredLayout[key],idleLayout[key],'Revealing controls must only change the clip');
  await reset.click();
  await waitForLayout(remote,l=>l?.zoom===1&&l.width===fitted.width);
  assert.equal(await reset.isVisible(),false);
  if(mode==='native'){
   await page.mouse.move(422,195);
   await page.mouse.wheel(0,-1100);
   await waitForLayout(remote,l=>l?.zoom>8&&l.clipWidth<=844&&l.clipHeight<=390);
  }
  await wakeControls();
  await tile.getByRole('button',{name:'Ajustes de su transmisión'}).click();
  const settings=tile.locator('#remoteScreenSettingsModal');
  await settings.waitFor({state:'visible'});
  await waitForLayout(remote,l=>l?.visible===false);
  await settings.getByLabel('Fotogramas por segundo').selectOption('30');
  await settings.getByRole('button',{name:'Aplicar en el emisor'}).click();
  await settings.waitFor({state:'detached'});
  assert.equal(requests.find(request=>request.action==='configure')?.settings.Fps,30);
  assert.equal(await tile.evaluate(el=>el.classList.contains('is-tile-fullscreen')),true);
  await waitForLayout(remote,l=>l?.visible===true);
  if(mode!=='native')assert.equal(await page.locator('.screen-tile-live-badge:visible').count(),0,'Landscape touch UI keeps badges hidden');
  await page.screenshot({path:`.artifacts/voice-fullscreen/${mode}-landscape.png`});
  await wakeControls();
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
 console.log('PASS voice fullscreen: native geometry, wheel/drag, pinch/pan, idle controls, rotation, reset, exit/Escape, stream end and navigation');
}finally{await closeBrowserFixture(browser, server);}
