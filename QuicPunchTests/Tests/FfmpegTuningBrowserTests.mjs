import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// UI/API fixtures and a decoder spy: no real display, camera or hardware session.
import assert from 'node:assert/strict';
import {mkdirSync} from 'node:fs';

const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 const page=await browser.newPage({viewport:{width:1366,height:900}}),errors=[],starts=[];
 page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
  window.decoderConfigurations=[];
  window.VideoDecoder=class {
   static async isConfigSupported(config){return {supported:!(window.rejectHardware&&config.hardwareAcceleration==='prefer-hardware'),config};}
   constructor(){this.state='unconfigured';this.decodeQueueSize=0;}
   configure(config){decoderConfigurations.push(config);this.state='configured';}
   decode(){} reset(){this.state='unconfigured';} close(){this.state='closed';}
  };
 });
 const source=readVoiceSource();
 const bridge=`window.tuningTest={
  async setup(){
   voiceStatus={...await QP.getStatus(),activeVoiceCalls:[{peerId:'00000000-0000-0000-0000-000000000001',peerName:'Ana'}]};
   voiceState.activeCalls=[{peerId:'00000000-0000-0000-0000-000000000001',screenDecoderCodecMask:63}];
   QP.on('status_updated',()=>{voiceStatus.activeVoiceCalls=[...voiceState.activeCalls];renderVoice();});renderVoice();
  },open(){openScreenSettingsModal('video');},
  decode(){const entry=createOrUpdateScreenTile('00000000-0000-0000-0000-000000000001','Ana',1280,720,1,30,false);handleWebCodecsVideoChunk(entry,1,new Uint8Array([1,11]));}
 };})();`;
 await page.route('**/voice.js',r=>r.fulfill({contentType:'text/javascript',body:source.replace(/\}\)\(\);\s*$/,bridge)}));
 const sources=[{Id:'display:1',Name:'Pantalla de prueba',Width:1920,Height:1080,Kind:'display'},{Id:'hwnd:42',Name:'Editor de prueba',Width:1280,Height:720,Kind:'window'}];
 await page.route('**/api/voice/media-capabilities*',r=>r.fulfill({json:{ffmpeg:{platform:'windows',installed:true,nativeCaptureSupported:true},nativeCaptureAvailable:true,
  nativeEncoders:[{Id:'h264_nvenc',Codec:'h264',Available:true,Hardware:true,Presets:['p1','p2','p3','p4','p5','p6','p7']},{Id:'libx264',Codec:'h264',Available:true,Hardware:false,Presets:['ultrafast','superfast','veryfast','faster','fast','medium','slow']}],sources,displays:[{Id:'1',Width:1920,Height:1080,Primary:true}],frameRates:[30,60],resolutionHeights:[720,1080]}}));
 await page.route('**/api/voice/state',r=>r.fulfill({json:{activeCalls:[{peerId:'00000000-0000-0000-0000-000000000001',screenDecoderCodecMask:63}]}}));
 await page.route('**/api/voice/screen-share/sources',r=>r.fulfill({json:{sources}}));
 await page.route('**/api/voice/screen-share/preview?**',r=>r.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#355566"/></svg>'}));
 await page.route('**/api/voice/screen-share/native-start',r=>{const body=r.request().postDataJSON();starts.push(body);return r.fulfill({json:{profile:{Encoder:body.encoder,Width:1280,Height:720,Fps:body.fps,CodecId:1,CaptureMode:'fixture'}}});});
 await page.goto(url+'call.html');await page.waitForFunction(()=>window.tuningTest);await page.evaluate(()=>tuningTest.setup());await page.evaluate(()=>tuningTest.open());
 await page.waitForFunction(()=>document.getElementById('screenEncoderSelect').value==='h264_nvenc');
 assert.equal(await page.locator('#ffmpegInstallationGuide').isHidden(),true);
 assert.equal(await page.locator('#screenSourceSelect,#screenSourcePickerBtn').count(),0);
 await page.getByText('Afinar la codificación',{exact:true}).click();
 const preset=page.locator('#screenEncoderPresetSelect');
 await preset.selectOption('p6');await page.waitForFunction(()=>document.getElementById('screenPresetSelect').disabled);
 assert.match(await page.locator('#screenEncoderPresetHelp').textContent(),/Preset efectivo: p6/);
 await page.locator('#saveScreenModalBtn').click();
  await page.waitForFunction(()=>JSON.parse(localStorage.getItem('qp_screen_config')||'{}').encoderPreset==='p6');
 assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('qp_screen_config')).encoderPreset),'p6');
 await page.evaluate(()=>tuningTest.open());await page.waitForFunction(()=>document.getElementById('screenEncoderPresetSelect').value==='p6');
 await page.click('#voicePanelScreenBtn');await page.locator('#screenNativeCaptureModal').waitFor({state:'visible'});
 assert.equal(starts.length,0,'Opening the source picker must not start capture');
 await page.locator('.capture-source-card').filter({hasText:'Editor de prueba'}).click();await page.locator('#screenNativeCaptureStart').click();
 await page.waitForFunction(()=>document.querySelector('.is-local-stream'));
 assert.equal(starts[0].source,'hwnd:42');assert.equal(starts[0].encoderPreset,'p6');
 await page.evaluate(()=>tuningTest.open());await page.waitForFunction(()=>document.getElementById('screenEncoderPresetSelect').value==='p6');
 await preset.selectOption('p2');await page.locator('#saveScreenModalBtn').click();
 await page.waitForFunction(()=>document.getElementById('voiceTrayToggle').getAttribute('aria-expanded')==='false');
 assert.equal(starts.length,2);assert.equal(starts[1].source,'hwnd:42');assert.equal(starts[1].encoderPreset,'p2');
 assert.equal(await page.locator('#screenNativeCaptureModal').isHidden(),true,'Applying a preset must keep the active capture source');
 await page.evaluate(()=>tuningTest.open());await page.getByText('Decodificación en este dispositivo',{exact:true}).click();
 await page.evaluate(()=>tuningTest.decode());
 const decoder=page.locator('#screenDecoderAccelerationSelect');
 await decoder.selectOption('prefer-software');await page.waitForFunction(()=>localStorage.getItem('qp_screen_decoder_acceleration')==='prefer-software');
 await page.evaluate(()=>tuningTest.decode());assert.equal(await page.evaluate(()=>decoderConfigurations.at(-1).hardwareAcceleration),'prefer-software');
 await page.evaluate(()=>window.rejectHardware=true);await decoder.selectOption('prefer-hardware');
 await page.waitForFunction(()=>document.getElementById('screenDecoderAccelerationSelect').value==='prefer-software');
 assert.equal(await page.evaluate(()=>localStorage.getItem('qp_screen_decoder_acceleration')),'prefer-software','Unsupported hardware choice must preserve decoding');
 await page.locator('#screenCodecSelect').selectOption('h264:cpu');await page.waitForFunction(()=>document.getElementById('screenEncoderSelect').value==='libx264');
 assert.equal(await preset.inputValue(),'','An encoder switch must clear an incompatible native preset');
 await preset.selectOption('medium');await page.waitForFunction(()=>document.getElementById('screenEncoderPresetHelp').textContent.includes('medium'));
 for(const width of [1366,390]){await page.setViewportSize({width,height:900});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));}
 await page.locator('#saveScreenModalBtn').click();await page.reload();await page.waitForFunction(()=>window.tuningTest);await page.evaluate(()=>tuningTest.setup());await page.evaluate(()=>tuningTest.open());
 await page.waitForFunction(()=>document.getElementById('screenEncoderPresetSelect').value==='medium');
 assert.equal(await page.locator('#screenResolutionSelect option[value="1080"]').count(),1,'An idle settings panel must not keep the resolution limit of the last captured window');
 assert.equal(await decoder.inputValue(),'prefer-software');
 mkdirSync('.artifacts/ffmpeg-tuning',{recursive:true});
 await page.evaluate(()=>document.getElementById('screenEncoderPresetSelect').closest('details').open=true);
 for(const width of [1366,390]){
  await page.setViewportSize({width,height:900});await page.locator('#screenEncoderPresetSelect').scrollIntoViewIfNeeded();
  await page.screenshot({path:`.artifacts/ffmpeg-tuning/presets-${width}.png`,animations:'disabled'});
 }
 assert.deepEqual(errors,[]);
 console.log('PASS FFmpeg tuning: encoder presets, persistence, source selection only when sharing, in-call reconfiguration, decoder preference/rejection and mobile layout');
}finally{await closeBrowserFixture(browser, server);}
