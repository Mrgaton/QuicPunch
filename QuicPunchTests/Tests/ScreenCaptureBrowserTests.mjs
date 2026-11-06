import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real Chromium timers, track processing and WebCodecs with a synthetic canvas.
// Never opens a display picker or reads the user's windows/screens.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1280,height:900}}), errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const bridge=`window.testScreenCapture = {
    pacer: createScreenFramePacer,
    async start(fallback, fps) {
      window.capturePackets = []; window.captureNotices = [];
      QP.sendBinary = packet => { capturePackets.push(new Uint8Array(packet)); return true; };
      QP.toast = message => captureNotices.push(message);
      screenShareConfig.acceleration = 'cpu'; screenShareConfig.bitrate = 1000000;
      const canvas=document.createElement('canvas'); canvas.width=320; canvas.height=180;
      const context=canvas.getContext('2d');context.fillStyle='#44bb88';context.fillRect(0,0,320,180);
      screenStream=canvas.captureStream(fps); window.captureTrack=screenStream.getVideoTracks()[0];
      const processor=window.MediaStreamTrackProcessor;
      if(fallback)window.MediaStreamTrackProcessor=undefined;
      let profile;
      // Headless Chromium may omit H.264; still exercise the same real capture
      // pipeline with VP9. Production codec selection is never changed.
      const codecId=await probeVideoEncoderConfig(1,320,180,fps,1000000)?1:3;
      try { profile=await startCanvasFrameExtraction(screenStream,320,180,codecId,fps); }
      finally { window.MediaStreamTrackProcessor=processor; }
      screenTransmissionReady=true;
      createOrUpdateScreenTile('local','Prueba de ventana',320,180,profile.codecId,profile.fps,true);
      return profile;
    },
    stop: stopScreenShare
  };})();`;
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,bridge)}));
  await page.goto(url+'call.html');
  const timerResult=await page.evaluate(async()=>{
    let pacer, emitted=0, closed=0;
    try {
      pacer=testScreenCapture.pacer(144,()=>emitted++);
      pacer.push({close(){closed++;}});
      await new Promise(resolve=>setTimeout(resolve,60));
      pacer.stop();const count=emitted;
      await new Promise(resolve=>setTimeout(resolve,30));
      return {emitted,closed,stopped:emitted===count};
    } catch(error){return {error:error.message};}
  });
  assert.equal(timerResult.error,undefined,'Browser timer receiver is invalid: '+JSON.stringify(timerResult));
  assert.ok(timerResult.emitted>0&&timerResult.closed===1&&timerResult.stopped,'Real browser pacing/cleanup failed');
  for(const fallback of [false,true]) {
    const profile=await page.evaluate(fallback=>testScreenCapture.start(fallback,144),fallback);
    assert.ok([1,3].includes(profile.codecId),'A real WebCodecs encoder must be configured');
    await page.waitForFunction(()=>capturePackets.length>=5);
    const state=await page.evaluate(codecId=>{
      const video=document.querySelector('.is-local-stream video');
      return {frames:capturePackets.length,valid:capturePackets.every(packet=>packet[0]===2&&packet[17]===codecId&&packet.length>21),
        preview:video?.readyState>=2&&video.videoWidth===320&&video.videoHeight===180,
        loading:document.querySelector('.is-local-stream .screen-tile-loading')?.classList.contains('hidden'), notices:captureNotices};
    },profile.codecId);
    assert.ok(state.valid&&state.preview&&state.loading,JSON.stringify(state));
    assert.ok(!state.notices.some(message=>/Illegal invocation|se ha detenido|No se pudo/.test(message)),JSON.stringify(state.notices));
    await page.evaluate(()=>testScreenCapture.stop());
    const count=await page.evaluate(()=>capturePackets.length);
    await page.waitForTimeout(120);
    assert.equal(await page.evaluate(()=>capturePackets.length),count,'Stopping left an encoding timer alive');
    assert.equal(await page.evaluate(()=>captureTrack.readyState),'ended');
    assert.equal(await page.locator('.is-local-stream').count(),0);
  }
  assert.deepEqual(errors,[]);
  console.log('PASS real WebCodecs capture: native timers, 144 FPS target, static window frames, processor/video fallback, local preview and cleanup');
} finally { await closeBrowserFixture(browser, server); }
