import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[],telemetryRequests=[];
  page.on('pageerror',error=>errors.push(error.message));page.on('request',request=>{if(new URL(request.url()).pathname==='/api/telemetry')telemetryRequests.push(request.url());});
  await page.goto(url+'settings.html');await page.waitForFunction(()=>window.NetworkTelemetry&&typeof renderQuicTelemetry==='function');
  await page.evaluate(()=>{
    window.seedNetworkCharts=()=>{
      const start=Date.now()-300000;
      let sessions;
      for(let i=0;i<=120;i++) {
        sessions=Array.from({length:i<30?1:i<80?2:3},(_,n)=>({peerId:'chart-peer-'+n,peerName:['Portátil','Móvil','Servidor'][n],protocolId:'direct',protocolName:'Direct QUIC',transportType:'Wan',telemetry:{
          nativeLocalEndpoint:'192.0.2.1:5000',nativeRemoteEndpoint:'192.0.2.'+(n+2)+':5000',rttMs:6+n*8+Math.sin(i/8)*2,minRttMs:4,maxRttMs:32,rttVarianceMs:1,
          sendTotalBytes:i*4096*(n+1),recvTotalBytes:i*16384*(n+1),sendTotalPackets:i*100*(n+1),sendSuspectedLostPackets:Math.floor(i/17)*(n+1),
          sendCongestionWindow:(10000+Math.sin(i/6)*4000)*(n+1),congestionAlgorithm:n===0?'CUBIC':'BBR'
        }}));
        NetworkTelemetry.update(sessions,start+i*2500);
      }
      renderQuicTelemetry({quicTelemetry:[...sessions,{...sessions[0],protocolId:'voice',protocolName:'Llamadas'}]});
    };
    window.seedNetworkCharts();
  });
  assert.equal(await page.locator('[data-net-chart] path').count(),0,'Collapsed diagnostics still draws charts');
  await page.locator('#settings-tab-diagnostics').click();
  await page.locator('#networkDiagnostics').evaluate(details=>details.open=true);
  await page.locator('#quicTelemetrySection').scrollIntoViewIfNeeded();
  await page.waitForFunction(()=>document.querySelectorAll('[data-net-chart] path').length===6);
  assert.equal(await page.locator('#kpiQuicConnections').textContent(),'3','Protocols counted as physical connections');
  assert.equal(await page.locator('#quicTelemetryBody tr').count(),3);
  assert.match(await page.locator('#kpiQuicThroughput').textContent(),/9\.6 KB\/s.*38\.4 KB\/s/);
  assert.match(await page.locator('#kpiQuicAlgorithm').textContent(),/CUBIC.*BBR/);
  assert.ok(await page.locator('[data-net-chart] path').evaluateAll(paths=>paths.every(path=>!/[IN]aN|Infinity/.test(path.getAttribute('d')))),'Invalid graph coordinates');
  const graph=page.locator('[data-net-chart=traffic]');await graph.focus();await page.keyboard.press('Home');
  const tip=graph.locator('..').locator('.net-chart-tip');assert.equal(await tip.isVisible(),true);assert.match(await tip.textContent(),/↑.*↓/);
  await page.keyboard.press('ArrowRight');assert.ok((await tip.textContent()).includes('KB/s'));
  await page.keyboard.press('Escape');assert.equal(await tip.isVisible(),false);
  await page.locator('#telemetryRange').selectOption('1');
  await page.waitForFunction(()=>document.querySelector('[data-net-chart] text').closest('svg').textContent.includes('−1 min'));
  await page.locator('#telemetryRange').selectOption('10');
  await page.waitForFunction(()=>document.querySelector('[data-net-chart]').textContent.includes('−10 min'));
  for(const theme of ['dark']) {
    await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
    for(const width of [2048,1280,390,320]) {
      await page.setViewportSize({width,height:1000});
      await page.evaluate(()=>window.seedNetworkCharts());
      await page.locator('#quicTelemetrySection').scrollIntoViewIfNeeded();
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Diagnostics overflow horizontally');
      assert.ok(await page.locator('[data-net-chart]').evaluateAll(graphs=>graphs.every(graph=>graph.getBoundingClientRect().width>160)),'Graphs too narrow');
      await page.screenshot({path:`.artifacts/network-telemetry-${theme}-${width}.png`});
    }
  }
  const before=await page.locator('[data-net-chart=connections] path').getAttribute('d');
  await page.locator('#networkDiagnostics').evaluate(details=>details.open=false);
  await page.evaluate(()=>NetworkTelemetry.update([],Date.now()+3000));
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
  assert.equal(await page.locator('[data-net-chart=connections] path').getAttribute('d'),before,'Closed panel keeps drawing');
  assert.deepEqual(telemetryRequests,[],'Charts added telemetry polling');assert.deepEqual(errors,[]);
  console.log('PASS network charts: lazy rendering, physical connections, two rate series, real algorithms, range selection, keyboard inspection, themes and 320–2048px layouts');
} finally {await closeBrowserFixture(browser, server);}
