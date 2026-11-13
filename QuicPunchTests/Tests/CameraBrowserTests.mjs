import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser,startPreviewServer,closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';
const server=startPreviewServer([]);let browser;
try{
 const url=await server.ready;browser=await launchBrowser();
 for(const android of [false,true]){
  const page=await browser.newPage({viewport:{width:android?390:1366,height:900},...(android?{userAgent:'Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/130 Mobile Safari/537.36'}:{})});const errors=[];page.on('pageerror',e=>errors.push(e.message));
  const bridge=`window.nativeCameraTest={
   async setup(){
    const base=await QP.getStatus();voiceStatus={...base,activeVoiceCalls:[{peerId:'00000000-0000-0000-0000-000000000001',peerName:'Ana'}]};voiceState.activeCalls=[...voiceStatus.activeVoiceCalls];
    QP.on('status_updated',()=>{voiceStatus.activeVoiceCalls=[...voiceState.activeCalls];renderVoice();});
    window.nativeRequests=[];window.nativeStarts=[];window.browserCaptureRequests=0;
    navigator.mediaDevices.getUserMedia=async()=>{browserCaptureRequests++;throw new Error('Web capture forbidden');};
    const original=QP.api;QP.api=async(path,options)=>{
     const body=JSON.parse(options?.body||'{}');if(path.startsWith('/api/voice'))nativeRequests.push({path,body});
     if(path==='/api/voice/cameras')return [{id:'camera:front',name:'Frontal'},{id:'camera:rear',name:'Trasera'}];
     if(path==='/api/voice/native-video')return {local:{available:true,decoderMask:2,hardwareMask:2,decoders:[{codecId:1,hardware:true,software:true,status:'hardware',reason:'Fixture',testWidth:640,testHeight:360}]},peers:[],streams:[]};
     if(path.endsWith('/native-start')||path.endsWith('/platform-start')){nativeStarts.push(body);if(window.delayNativeStart)await new Promise(resolve=>window.releaseNativeStart=resolve);return {profile:{Encoder:'fixture',Width:640,Height:360,Fps:30,CodecId:1}};}
     if(path==='/api/voice/screen-share/stop'||path.startsWith('/api/voice/native-video/'))return {success:true};
     if(path==='/api/voice/state')return voiceState;
     return original(path,options);
    };
    screenShareConfig={...screenShareConfig,backend:'ffmpeg',codec:'h264',encoder:'auto',encoderPreset:'',resolution:'720',fps:30};renderVoice();
   },start:startCamera,stop:stopScreenShare,state:()=>({sharing:nativeSharing,pending:videoStartPending})
  };})();`;
  await page.route('**/voice.js',r=>r.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,bridge)}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.nativeCameraTest);await page.evaluate(()=>nativeCameraTest.setup());
  await page.click('#voicePanelCameraBtn');await page.waitForFunction(()=>document.getElementById('voiceCameraPicker').open);
  assert.equal(await page.evaluate(()=>nativeStarts.length),0,'Picker must not start capture');
  await page.locator('#voiceCameraPickerSelect').selectOption('camera:rear');await page.click('#voiceCameraPickerForm button[type=submit]');
  await page.waitForFunction(()=>nativeCameraTest.state().sharing);
  assert.equal(await page.evaluate(()=>nativeStarts[0].source),'camera:rear');
  assert.equal(await page.evaluate(()=>browserCaptureRequests),0);
  assert.equal(await page.locator('.screen-tile video,.screen-tile canvas').count(),0,'No browser pixel surface');
  await page.waitForFunction(()=>nativeRequests.some(r=>r.path.endsWith('/layout')&&r.body.visible));
  await page.evaluate(()=>QP.emit('native_video_state',{peerId:'local',mode:'hardware',width:640,height:360,decoded:8,presented:8,dropped:0,decodeMs:.5}));
  assert.match(await page.locator('.is-local-stream .screen-tile-codec').textContent(),/hardware verificado/);
  await page.evaluate(()=>nativeCameraTest.stop());assert.equal(await page.locator('.is-local-stream').count(),0);
  await page.evaluate(()=>{delayNativeStart=true;void nativeCameraTest.start();});await page.waitForFunction(()=>window.releaseNativeStart);
  await page.evaluate(()=>nativeCameraTest.stop());await page.evaluate(()=>releaseNativeStart());await page.waitForFunction(()=>!nativeCameraTest.state().pending);
  assert.equal(await page.evaluate(()=>nativeCameraTest.state().sharing),false,'Late start response must be stopped');
  assert.equal(await page.locator('.is-local-stream').count(),0);
  assert.deepEqual(errors,[]);await page.close();
 }
 console.log('PASS native Windows/Android camera UI: explicit selection, native APIs, no browser capture or pixel surfaces, hardware telemetry, stop and late-start cancellation.');
}finally{await closeBrowserFixture(browser,server);}
