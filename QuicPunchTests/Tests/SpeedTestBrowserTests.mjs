import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real browser coverage of the speed-test UI against isolated API fixtures.

import {mkdir} from 'node:fs/promises';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1440,height:1000}});
  const errors = []; page.on('pageerror',error => errors.push(error.message));
  const peerId = '00000000-0000-0000-0000-000000000001';
  const quality = {method:'QUIC DATAGRAM',sent:10,received:10,sendFailures:0,lossPercent:0,rttMinMs:8.41,rttAverageMs:26.75,rttMaxMs:47.82,jitterMs:17.17};
  const stage = {mode:'Download',uploadMbps:0,downloadMbps:228.1,totalBytes:271900000,durationSeconds:10,quality:{...quality,rttAverageMs:35.63}};
  const partial = {testId:'partial',peerId,peerName:'Ana · Portátil',mode:'All',state:'interrupted',error:'An internal error has occurred. Status code: QUIC_STATUS_INTERNAL_ERROR',baseline:quality,stages:[stage],uploadMbps:0,downloadMbps:228.1};
  let status = {isRunning:false,history:[]};
  let startBody, startFailure = false;
  await page.route('**/api/speedtest/status',route => route.fulfill({json:status}));
  await page.route('**/api/speedtest/start',route => {
    startBody = route.request().postDataJSON();
    return route.fulfill({json:startFailure ? {success:false,error:'QUIC_STATUS_INTERNAL_ERROR'} : {success:true}});
  });
  await page.route('**/api/speedtest/cancel',route => route.fulfill({json:{success:true}}));
  await page.goto(url+'speedtest.html');
  await page.locator('#view-speedtest:not(.hidden)').waitFor();
  await page.locator('#speedTestPeerSelect option[value="'+peerId+'"]').waitFor({state:'attached'});
  assert.equal(await page.locator('#speedTestActiveBadge').textContent(),'Sin iniciar');
  await page.locator('#speedTestMode').selectOption('upload');
  assert.ok((await page.locator('#speedTestEstimate').textContent()).includes('10 segundos'));
  await page.locator('#speedTestMode').selectOption('all');
  await page.locator('#speedTestPeerSelect').selectOption(peerId);
  await page.locator('#startSpeedTestBtn').click();
  await page.waitForFunction(()=>document.getElementById('speedTestResults').dataset.state==='connecting');
  assert.deepEqual(startBody,{peerId,duration:10,mode:'all'});
  const emit = (name,data) => page.evaluate(({name,data})=>QP.emit(name,data),{name,data});
  await emit('speedtest_phase',{peerId,phase:'Download',baseline:quality});
  await emit('speedtest_progress',{peerId,direction:'Download',currentMbps:228.1,bytesTransferred:1000000,elapsedSeconds:5,totalSeconds:10,rttMs:35.63});
  assert.equal(await page.locator('#speedTestMetricUpload').textContent(),'Pendiente');
  assert.equal(await page.locator('#speedTestProgressBar').getAttribute('aria-valuenow'),'50');
  assert.ok((await page.locator('#speedTestGaugeHelp').textContent()).includes('fase 1 de 3'));
  await emit('speedtest_phase_completed',{testId:'partial',peerId,...stage,state:'running'});
  await emit('speedtest_phase',{peerId,phase:'Upload'});
  await emit('speedtest_progress',{peerId,direction:'Upload',currentMbps:44,elapsedSeconds:1,totalSeconds:10,rttMs:50});
  await emit('speedtest_error',{peerId,error:partial.error,result:partial});
  assert.equal(await page.locator('#speedTestResults').getAttribute('data-state'),'interrupted');
  assert.equal(await page.locator('#speedTestMetricUpload').textContent(),'Sin resultado');
  assert.equal(await page.locator('#speedTestMetricDownload').textContent(),'228.1 Mbps');
  assert.equal(await page.locator('#speedTestProgress').isVisible(),false);
  assert.equal(await page.locator('#speedTestError').isVisible(),true);
  assert.equal(await page.locator('#speedTestErrorTechnical').isVisible(),false);
  assert.equal(await page.locator('#speedTestHistoryBody > tr').count(),1);
  await page.locator('#speedTestErrorDetails > summary').click();
  assert.ok((await page.locator('#speedTestErrorTechnical').textContent()).includes('QUIC_STATUS_INTERNAL_ERROR'));
  await page.locator('#speedTestErrorDetails > summary').click();
  await page.locator('.speed-details > summary').click();
  assert.equal(await page.locator('#speedTestPhaseResults .speed-stage-detail').count(),1);
  await page.locator('.speed-details > summary').click();
  await page.locator('.speed-history-details > summary').click();
  assert.ok(await page.locator('.speed-history-details .speed-stage-detail').isVisible());
  await page.locator('.speed-history-details > summary').click();

  const settle = () => page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  for (const theme of ['dark']) {
    for (const viewport of [{width:2560,height:1440},{width:1440,height:1000},{width:1280,height:720},{width:844,height:390},{width:390,height:844},{width:320,height:568}]) {
      await page.setViewportSize(viewport);
      await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme); await settle();
      const geometry = await page.evaluate(()=>({overflow:document.documentElement.scrollWidth-innerWidth,
        labelFont:parseFloat(getComputedStyle(document.querySelector('.speed-setup label')).fontSize),
        setup:document.querySelector('.speed-setup').getBoundingClientRect().toJSON(),result:document.querySelector('.speed-results').getBoundingClientRect().toJSON()}));
      assert.ok(geometry.overflow<=1,`${theme} ${viewport.width}: horizontal overflow ${geometry.overflow}`);
      assert.ok(geometry.labelFont>=14);
      if(viewport.width>=1440) assert.ok(geometry.setup.right<geometry.result.left,'Desktop configuration and results must use distinct columns');
      if(viewport.width<=1100) assert.ok(geometry.result.bottom<=geometry.setup.top,'After a run, narrow layout must put results before configuration');
      await page.locator('.speed-details > summary').click();
      await page.locator('.speed-history-details > summary').click();
      await settle();
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`Expanded details overflow at ${viewport.width}`);
      if(viewport.width<=680) assert.ok(await page.locator('#speedTestHistoryBody td').first().evaluate(cell=>getComputedStyle(cell,'::before').content.includes('Hora')),'Mobile history loses its labels');
      await page.locator('.speed-details > summary').click();
      await page.locator('.speed-history-details > summary').click();
    }
  }
  await mkdir('.artifacts',{recursive:true});
  await page.setViewportSize({width:1440,height:1100});
  await page.evaluate(()=>document.documentElement.dataset.theme='dark');
  await page.evaluate(()=>{document.activeElement?.blur();scrollTo(0,0);});
  await page.screenshot({path:'.artifacts/speedtest-partial-desktop.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});
  await page.evaluate(()=>scrollTo(0,0));
  await page.screenshot({path:'.artifacts/speedtest-partial-mobile.png'});
  await page.setViewportSize({width:1280,height:900});
  await page.evaluate(()=>document.documentElement.style.zoom='2'); await settle();
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'200% scaling must reflow');
  await page.evaluate(()=>document.documentElement.style.zoom='');

  // Hydration uses actual status endpoint, rather than depending on live events.
  status={isRunning:false,history:[partial],lastResult:partial,error:partial.error};
  await page.reload();
  await page.waitForFunction(()=>document.getElementById('speedTestResults').dataset.state==='interrupted');
  assert.equal(await page.locator('#speedTestHistoryBody > tr').count(),1);
  assert.equal(await page.locator('#speedTestMetricUpload').textContent(),'Sin resultado');
  await page.locator('#startSpeedTestBtn').click();
  await page.waitForFunction(()=>document.getElementById('speedTestResults').dataset.state==='connecting');
  assert.equal(await page.locator('#speedTestError').isVisible(),false);
  await page.locator('#cancelSpeedTestBtn').click();
  await page.waitForFunction(()=>document.getElementById('speedTestResults').dataset.state==='cancelled');
  assert.equal(await page.locator('#speedTestError').isVisible(),false);
  startFailure=true;
  await page.locator('#startSpeedTestBtn').click();
  await page.waitForFunction(()=>document.getElementById('speedTestResults').dataset.state==='error');
  assert.equal(await page.locator('#speedTestActiveBadge').textContent(),'No iniciada');
  assert.equal(await page.locator('#speedTestError').isVisible(),true);
  assert.deepEqual(errors,[]);
  console.log('PASS speed test browser: start/cancel/API errors, partial phases, reload, expandable grouped history, 320–2560px, dark, 200% scaling, no page errors');
} finally { await closeBrowserFixture(browser, server); }
