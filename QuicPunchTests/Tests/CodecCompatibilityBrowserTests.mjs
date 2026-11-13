import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser,startPreviewServer,closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';
import {mkdirSync} from 'node:fs';
const server=startPreviewServer([]);let browser;
try{
 const url=await server.ready;browser=await launchBrowser();const page=await browser.newPage({viewport:{width:1366,height:960}}),errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 const caps={nativeCaptureAvailable:true,graphicsAdapterCount:1,ffmpeg:{installed:true,platform:'windows'},nativeEncoders:[{Id:'h264_nvenc',Codec:'h264',Available:true,Hardware:true,Presets:['p1','p3','p5','p7']},{Id:'libx264',Codec:'h264',Available:true,Hardware:false,Presets:['ultrafast','veryfast','medium']},{Id:'libx265',Codec:'hevc',Available:true,Hardware:false}],displays:[{Id:'display:1',Name:'Monitor',Width:1920,Height:1080,Primary:true}],sources:[],frameRates:[30,60],resolutionHeights:[720,1080]};
 const decoder=(codecId,hardware,software)=>({codecId,hardware,software,status:hardware?'hardware':software?'software':'unsupported',reason:'Muestra local en caché',testWidth:640,testHeight:360});
 let peers=[{peerId:'10000000-0000-0000-0000-000000000001',name:'Ana <img src=x>',capabilities:{decoders:[decoder(1,true,true),decoder(5,false,true),decoder(3,false,false)]}}];
 await page.route('**/api/voice/media-capabilities*',r=>r.fulfill({json:caps}));
 await page.route('**/api/voice/native-video**',r=>r.fulfill({json:{local:{available:true,decoderMask:58,decoders:[decoder(1,true,true),decoder(5,false,true),decoder(3,false,false)]},peers,streams:[]}}));
 await page.route('**/voice.js',r=>r.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,'window.openCodecSettings=()=>openScreenSettingsModal("video");})();')}));
 await page.goto(url+'call.html');await page.waitForFunction(()=>window.openCodecSettings);await page.evaluate(()=>openCodecSettings());
 await page.waitForFunction(()=>document.querySelectorAll('.codec-choice').length===4);
 assert.equal(await page.locator('#screenBackendSelect,#screenCodecTrigger,#screenCaptureReceivers,#screenCodecReceivers').count(),0);
 assert.equal(await page.locator('.codec-choice-button').count(),8);
 const gpu=page.locator('[data-value="h264:gpu"]'),cpu=page.locator('[data-value="h264:cpu"]');
 assert.equal(await gpu.getAttribute('aria-pressed'),'true');assert.equal(await page.locator('[data-value="hevc:gpu"]').isDisabled(),true);
 await gpu.scrollIntoViewIfNeeded();const before=await page.locator('#screenCodecOptions').boundingBox();await gpu.hover();const after=await page.locator('#screenCodecOptions').boundingBox();assert.deepEqual(after,before,'Hover changed the selector layout');
 await cpu.click();await page.waitForFunction(()=>document.getElementById('screenCodecSelect').value==='h264:cpu');assert.equal(await cpu.getAttribute('aria-pressed'),'true');
 await cpu.press('ArrowRight');assert.equal(await page.evaluate(()=>document.activeElement.dataset.value),'hevc:cpu');await page.keyboard.press('Space');await page.waitForFunction(()=>document.getElementById('screenCodecSelect').value==='hevc:cpu');
 await cpu.click();await page.locator('#videoEncodingDetails > summary').click();
 assert.equal(await page.locator('#screenEncoderPresetSelect').evaluate(e=>e.closest('details').id),'videoEncodingDetails');
 await page.locator('#screenEncoderPresetSelect').selectOption('medium');await page.waitForFunction(()=>document.getElementById('screenTuningSummary').textContent.includes('preset medium'));
 assert.match(await page.locator('#screenTuningSummary').textContent(),/libx264.*FPS.*Mbps/);
 const matrix=page.locator('#nativeDecoderMatrix');assert.match(await matrix.textContent(),/Hardware★/);assert.match(await matrix.textContent(),/Software✓/);assert.match(await matrix.textContent(),/No compatible×/);assert.match(await matrix.textContent(),/Pendiente·/);
 assert.equal(await matrix.locator('img').count(),0);assert.match(await matrix.textContent(),/Ana <img src=x>/);
 peers=[];await page.evaluate(()=>QuicNetNativeVideo.refresh());assert.equal(await matrix.locator('tr').count(),1,'Departed participant remained in inventory');
 mkdirSync('.artifacts/codec-settings',{recursive:true});
 for(const width of [1366,390,320]){await page.setViewportSize({width,height:960});await page.locator('#videoFormatDetails').scrollIntoViewIfNeeded();assert.ok(await page.locator('.media-studio').evaluate(e=>e.scrollWidth<=e.clientWidth+1));await page.screenshot({path:`.artifacts/codec-settings/${width}.png`});}
 assert.deepEqual(errors,[]);console.log('PASS codec settings: cached matrix symbols, escaped peer names, fixed GPU/CPU choices, no hover expansion, disabled encoders, keyboard, tuning presets, live values and mobile.');
}finally{await closeBrowserFixture(browser,server);}
