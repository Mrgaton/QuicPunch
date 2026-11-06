import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Synthetic camera only: never reads the user's webcam, microphone or screen.

import {readFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const avcFixture=JSON.parse(readFileSync(new URL('../../.artifacts/android-video-fixture.json',import.meta.url),'utf8')).packet;
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  for (const android of [false,true]) {
    const page=await browser.newPage({viewport:android?{width:390,height:844}:{width:1280,height:900},...(android?{userAgent:'Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/140.0 Mobile Safari/537.36'}:{})});
    const errors=[],logs=[];page.on('pageerror',e=>errors.push(e.message));page.on('console',message=>{if(['warning','error'].includes(message.type()))logs.push(message.text());});
    const bridge=`window.testVideo={
      async prepare(){
        voiceStatus={...await QP.getStatus(),activeVoiceCalls:[{peerId:'12345678-1234-1234-1234-123456789012',peerName:'Prueba'}]};
        voiceState.activeCalls=[{peerId:'12345678-1234-1234-1234-123456789012',screenDecoderCodecMask:63}];
        // The preview broadcasts its idle status every ten seconds. Keep this
        // test's synthetic call authoritative across that unrelated heartbeat.
        QP.on('status_updated',()=>{voiceStatus.activeVoiceCalls=[...voiceState.activeCalls];renderVoice();});
        window.videoPackets=[];window.videoRequests=[];window.videoStreams=[];window.videoConstraints=[];window.videoNotices=[];
        QP.sendBinary=p=>{videoPackets.push(new Uint8Array(p));return true;}; QP.toast=m=>videoNotices.push(m);
        const api=QP.api;QP.api=async(path,options)=>{
          if(path.includes('/api/voice/screen-share/')){
            const body=JSON.parse(options?.body||'{}');videoRequests.push({path,body});
            if(path.endsWith('/decoder-check'))return {supported:true};
            if(path.endsWith('/platform-start'))return {profile:{width:720,height:1280,codecId:1,fps:30}};
            return {success:true};
          }
          if(path.includes('/media-capabilities'))return {nativeEncoders:[],displays:[],sources:[],nativeCaptureAvailable:false};
          return api(path,options);
        };
        nativeCapabilities={nativeEncoders:[],displays:[],sources:[],nativeCaptureAvailable:false};
        screenShareConfig.acceleration='cpu';
        Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:async constraints=>{
          videoConstraints.push(constraints);
          if(window.denyCamera)throw new DOMException('Permiso de cámara denegado','NotAllowedError');
          if(window.cameraBusyAttempts>0){window.cameraBusyAttempts--;throw new DOMException('Could not start video source','NotReadableError');}
          if(performance.now()<(window.cameraReleasedAt||0))throw new DOMException('Camera driver still releasing','NotReadableError');
          if(videoStreams.some(s=>s.getVideoTracks().some(t=>t.readyState==='live')))throw new DOMException('Previous capture still open','NotReadableError');
          const c=document.createElement('canvas');c.width=320;c.height=180;const ctx=c.getContext('2d');
          let step=0;const paint=()=>{ctx.fillStyle=++step%2?'#69b9a1':'#184d42';ctx.fillRect(0,0,320,180);};paint();
          const stream=c.captureStream(30),track=stream.getVideoTracks()[0],stop=track.stop.bind(track),timer=setInterval(paint,30);
          track.stop=()=>{if(track.readyState==='live')window.cameraReleasedAt=performance.now()+120;clearInterval(timer);stop();};videoStreams.push(stream);return stream;
        }});
        Object.defineProperty(navigator.mediaDevices,'enumerateDevices',{configurable:true,value:async()=>[
          {kind:'videoinput',deviceId:'front',label:'Frontal'},{kind:'videoinput',deviceId:'rear',label:'Trasera'}]});
        renderVoice();
      },ensureCall(){voiceStatus.activeVoiceCalls=[{peerId:'12345678-1234-1234-1234-123456789012',peerName:'Prueba'}];voiceState.activeCalls=[{peerId:'12345678-1234-1234-1234-123456789012',screenDecoderCodecMask:63}];renderVoice();},start:startCamera,stop:stopScreenShare,screen:startScreenShare,hangup:hangupAll,remoteEnd:()=>onVoiceSignal({signal:'call-ended',peerId:'12345678-1234-1234-1234-123456789012'}),loseStatus:()=>{voiceStatus.activeVoiceCalls=[];voiceState.activeCalls=[];},
      decode(base64){const bytes=Uint8Array.from(atob(base64),c=>c.charCodeAt(0));const wire=new Uint8Array(18+bytes.length);wire[0]=2;wire[17]=1;wire.set(bytes,18);onWsBinaryReceived(wire.buffer);},
      stopped:onScreenShareDetenido,
      state:()=>({kind:localVideoKind,active:!!screenStream,pending:videoStartPending,stopping:!!videoStopPending,calls:activeCallsList().length,device:cameraDeviceId,config:{...screenShareConfig}})
    };})();`;
    await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,bridge)}));
    await page.goto(url+'call.html');await page.waitForFunction(()=>window.testVideo);await page.evaluate(()=>testVideo.prepare());
    if(android){
      await page.evaluate(packet=>testVideo.decode(packet),avcFixture);
      let decoded=true;
      try { await page.waitForFunction(()=>{const c=document.querySelector('.is-local-stream canvas');return c?.width===320&&c.height===180&&!c.classList.contains('hidden');},{},{timeout:5000}); }
      catch(error){
        if(logs.some(log=>/NotSupportedError: H\.264 decoding is not supported/.test(log))&&!process.env.QUICNET_REQUIRE_H264){decoded=false;console.log('SKIP native H264 preview: this Chromium build omits the H264 decoder (managed FFmpeg roundtrip still required).');}
        else throw new Error('Native H264 preview failed: '+logs.join('\n'),{cause:error});
      }
      if(decoded){
        const pixel=await page.locator('.is-local-stream canvas').evaluate(c=>[...c.getContext('2d').getImageData(100,100,1,1).data]);
        assert.ok(pixel[1]>pixel[0]+30&&pixel[1]>pixel[2]+30,'Android H264 preview did not decode the synthetic green frame: '+pixel);
      }
      await page.evaluate(()=>testVideo.stop());
    }
    await page.evaluate(()=>testVideo.ensureCall());
    await page.click('#voicePanelCameraBtn');
    await page.waitForFunction(()=>document.getElementById('voiceCameraPicker').open);
    assert.equal(await page.evaluate(()=>videoConstraints.length),0,'Opening the picker must not acquire a camera');
    await page.click('#voiceCameraPickerCancel');
    assert.equal(await page.evaluate(()=>videoConstraints.length),0,'Cancelling must not acquire a camera');
    await page.click('#voicePanelCameraBtn');
    await page.selectOption('#voiceCameraPickerSelect','rear');
    if(!android)await page.screenshot({path:'.artifacts/camera-picker-desktop.png'});
    else await page.screenshot({path:'.artifacts/camera-picker-mobile.png'});
    await page.click('#voiceCameraPickerForm button[type=submit]');
    await page.waitForFunction(()=>testVideo.state().active&&!testVideo.state().pending);
    assert.equal(await page.evaluate(()=>videoConstraints[0].video.deviceId.exact),'rear','The first acquisition must use the selected camera');
    assert.equal((await page.evaluate(()=>testVideo.state())).kind,'camera',JSON.stringify(await page.evaluate(()=>videoNotices)));
    await page.waitForFunction(()=>videoPackets.length>=3);
    assert.equal(await page.locator('#voicePanelCameraBtn').getAttribute('aria-pressed'),'true');
    assert.equal(await page.locator('#voicePanelScreenBtn').getAttribute('aria-pressed'),'false');
    assert.equal(await page.locator('#voiceCameraSelect option').count(),2);
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth),'Camera controls overflow the viewport');
    const preview=await page.locator('.is-local-stream video').evaluate(v=>({ready:v.readyState,width:v.videoWidth,muted:v.muted}));
    assert.ok(preview.ready>=2&&preview.width===320&&preview.muted,JSON.stringify(preview));
    assert.ok(await page.evaluate(()=>videoRequests.some(r=>r.path.endsWith('/start')&&r.body.kind==='camera')));
    assert.equal(await page.evaluate(()=>videoConstraints[0].audio),false,'Camera must not acquire a second microphone');
    await page.click('#voiceScreenSettingsBtn');
    assert.ok(await page.locator('#voiceDrawerSettings #voiceCameraSelect').isVisible(),'Camera device controls belong in the right settings panel');
    await page.selectOption('#voiceCameraSelect','front');
    try { await page.waitForFunction(()=>videoConstraints.some(c=>c.video.deviceId?.exact==='front')&&!testVideo.state().pending,{},{timeout:5000}); }
    catch(error){throw new Error('Camera switch failed: '+JSON.stringify(await page.evaluate(()=>({state:testVideo.state(),constraints:videoConstraints,notices:videoNotices,requests:videoRequests}))),{cause:error});}
    assert.equal(await page.evaluate(()=>videoStreams[0].getVideoTracks()[0].readyState),'ended');
    if(android){
      await page.click('#voiceCameraFlipBtn');
      try { await page.waitForFunction(()=>videoConstraints.some(c=>c.video.facingMode?.ideal==='environment')&&!testVideo.state().pending,{},{timeout:5000}); }
      catch(error){throw new Error('Camera flip failed: '+JSON.stringify(await page.evaluate(()=>({state:testVideo.state(),constraints:videoConstraints,notices:videoNotices}))),{cause:error});}
      await page.evaluate(()=>testVideo.stop());await page.evaluate(()=>testVideo.screen());
      assert.ok(await page.evaluate(()=>videoRequests.some(r=>r.path.endsWith('/platform-start'))),'Android must use MediaProjection, not getDisplayMedia');
    }
    await page.evaluate(()=>testVideo.stop());
    assert.ok(await page.evaluate(()=>videoStreams.every(s=>s.getTracks().every(t=>t.readyState==='ended'))));
    const packets=await page.evaluate(()=>videoPackets.length);await page.waitForTimeout(150);
    assert.equal(await page.evaluate(()=>videoPackets.length),packets);
    // Repeat starts/stops with an asynchronously releasing driver. Old and duplicate
    // stop acknowledgements must never terminate the replacement stream.
    for(let cycle=0;cycle<3;cycle++){
      await page.evaluate(()=>testVideo.start());
      assert.equal((await page.evaluate(()=>testVideo.state())).active,true,'Camera failed to restart');
      await page.evaluate(()=>{
        const request=videoRequests.filter(r=>r.path.endsWith('/stop')).at(-1);
        testVideo.stopped({peerId:'local',requestId:request.body.requestId});
        testVideo.stopped({peerId:'local',requestId:request.body.requestId});
      });
      assert.equal((await page.evaluate(()=>testVideo.state())).active,true,'Late stop acknowledgement killed the replacement camera');
      await page.evaluate(()=>testVideo.stop());
    }
    await page.evaluate(()=>{window.cameraBusyAttempts=20;window.beforeBusy=videoConstraints.length;return testVideo.start();});
    assert.equal(await page.evaluate(()=>videoConstraints.length-beforeBusy),4,'Busy retry must be bounded');
    assert.equal((await page.evaluate(()=>testVideo.state())).active,false);
    assert.equal((await page.evaluate(()=>testVideo.state())).pending,false);
    assert.ok(await page.evaluate(()=>videoNotices.some(m=>m.includes('ocupada'))));
    await page.evaluate(()=>{window.cameraBusyAttempts=0;return testVideo.start();});
    assert.equal((await page.evaluate(()=>testVideo.state())).active,true,'A busy failure must not lock out future attempts');
    await page.evaluate(()=>testVideo.stop());
    for(const ending of ['local','remote','status']) {
      await page.evaluate(()=>{
        testVideo.ensureCall();window.cameraBeforePending=videoRequests.filter(r=>r.path.endsWith('/start')).length;
        const acquire=navigator.mediaDevices.getUserMedia;
        window.cameraPermissionSeen=false;
        Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:constraints=>new Promise(resolve=>{
          window.cameraPermissionSeen=true;window.releaseCameraPermission=()=>resolve(acquire(constraints));
        })});
        window.restoreCameraPermission=()=>Object.defineProperty(navigator.mediaDevices,'getUserMedia',{configurable:true,value:acquire});
        window.pendingCamera=testVideo.start();
      });
      await page.waitForFunction(()=>window.cameraPermissionSeen);
      await page.evaluate(ending=>ending==='remote'?testVideo.remoteEnd():ending==='status'?testVideo.loseStatus():testVideo.hangup(),ending);
      await page.evaluate(async()=>{releaseCameraPermission();await pendingCamera;restoreCameraPermission();});
      assert.equal((await page.evaluate(()=>testVideo.state())).active,false,'Camera opened after the last call ended');
      assert.equal(await page.evaluate(()=>videoStreams.at(-1).getTracks()[0].readyState),'ended');
      assert.ok(await page.evaluate(()=>videoRequests.filter(r=>r.path.endsWith('/start')).length===cameraBeforePending),'Cancelled camera announced a new broadcast');
    }
    await page.evaluate(()=>{testVideo.ensureCall();window.denyCamera=true;return testVideo.start();});
    assert.equal((await page.evaluate(()=>testVideo.state())).active,false);
    assert.equal((await page.evaluate(()=>testVideo.state())).pending,false);
    assert.equal(await page.locator('.is-local-stream').count(),0);
    assert.ok(await page.evaluate(()=>videoNotices.some(m=>m.includes('denegado'))));
    assert.deepEqual(errors,[]);await page.close();
  }
  console.log('PASS Windows/Android camera: explicit picker, repeat restart, busy driver recovery, stale stop acknowledgements, WebCodecs, preview, device switch, cancellation and cleanup');
} finally {await closeBrowserFixture(browser, server);}
