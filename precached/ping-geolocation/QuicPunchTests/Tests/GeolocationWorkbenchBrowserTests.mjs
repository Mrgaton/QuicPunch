import {launchBrowser} from '../Fixtures/BrowserFixture.mjs';
import {spawn} from 'node:child_process';
import {mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server=spawn('dotnet',['run','--project','QuicPunchTests','-c',process.env.QUICNET_TEST_CONFIGURATION||'Test','--no-build','--','--serve-geolocation-workbench'],{stdio:['pipe','pipe','pipe'],windowsHide:true});
let browser;
try{
 const url=await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(Error('Workbench host timeout')),15000);server.once('error',reject);server.stdout.on('data',data=>{const match=data.toString().match(/GEOLOCATION-TEST-URL=(http:\/\/127\.0\.0\.1:\d+\/)/);if(match){clearTimeout(timeout);resolve(match[1]);}});});
 browser = await launchBrowser();
 const page=await browser.newPage({viewport:{width:1280,height:1000}}),errors=[],requests=[],tiles=[];
 page.on('pageerror',error=>errors.push(error.message));page.on('request',request=>requests.push(request.url()));
 await page.route('**/api/speedtest/status',route=>route.fulfill({json:{running:false,state:'idle'}}));
 // Test map gestures/tiles without downloading tiles from the public provider.
 await page.route('https://tile.openstreetmap.org/**',route=>{
   tiles.push({url:route.request().url(),headers:route.request().headers()});
   return route.fulfill({contentType:'image/svg+xml',headers:{'Cache-Control':'public, max-age=604800'},body:'<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256"><rect width="256" height="256" fill="#e9e7e0"/><path d="M0 70H256M0 190H256M80 0V256M200 0V256" stroke="#fff" stroke-width="14"/><text x="90" y="65" font-family="sans-serif" font-size="10" fill="#666">Calle de prueba</text></svg>'});
 });
 // An offline lookup is safe even though this node-free fixture has no public IP.
 await page.route('**/api/geolocation/workbench/open',async route=>{const response=await route.fetch();const data=await response.json();data.addresses=['213.242.114.213'];await route.fulfill({response,json:data});});
 await page.goto(url);await page.waitForFunction(()=>window.QP?.state.status);
 await page.evaluate(()=>QP.navigate('settings'));
 await page.locator('#settings-profile').waitFor();
 assert.equal(requests.filter(u=>/\/geolocation\/|\/api\/geolocation\/workbench\//.test(u)).length,0,'The ordinary settings page eagerly loaded the laboratory');
 await page.locator('#settings-tab-geolocation').click();
 const panel=()=>page.frames().find(frame=>frame.url().includes('/geolocation/workbench.html'));
 await page.waitForFunction(()=>document.querySelector('#geolocationWorkbench').contentDocument?.querySelector('canvas')?.dataset.renderer);
 let frame=panel();await frame.locator('#start').waitFor({state:'visible'});await frame.waitForFunction(()=>!document.querySelector('#start').disabled);
 assert.equal(tiles.length,0,'Local map fetched street tiles before activation');
 await frame.locator('#configuration > summary').click();
 await frame.locator('[name=enablePathMtu]').check();await frame.locator('[name=enableTcp]').check();
 assert.equal(await frame.locator('#profile').inputValue(),'custom');
 await frame.locator('[name=maximumProbes]').fill('0');await frame.locator('#start').click();
 assert.equal(requests.filter(u=>u.endsWith('/api/geolocation/workbench/start')).length,0,'Invalid settings reached the engine');
 await frame.locator('#profile').selectOption('balanced');
 assert.equal(await frame.locator('[name=enablePathMtu]').isChecked(),false,'Preset retained stale optional probes');
 await frame.locator('#profile').selectOption('offline');
 assert.equal(await frame.locator('[name=enableTcp]').isDisabled(),true,'Offline mode left network options enabled');
 await frame.locator('#publicIp').selectOption('213.242.114.213');
 const startRequest=page.waitForRequest('**/api/geolocation/workbench/start');
 await frame.locator('#start').click();
 const submitted=(await startRequest).postDataJSON().settings;
 assert.equal(submitted.maximumProbes,4000);assert.equal(submitted.traceRepetitions,3);assert.equal(submitted.maximumTraceRoutes,16);assert.equal(submitted.enableTopology,true);assert.equal(submitted.offline,true);
 await frame.locator('#logs summary').click();
 await frame.locator('#logOutput').filter({hasText:'Revisión'}).waitFor();
 await frame.locator('#resultTitle').filter({hasText:'Referencia publicada'}).waitFor();
 assert.equal(await frame.locator('#localityStatus').textContent(),'Solo catálogo de IP');
 assert.equal(await frame.locator('#analysisStatus').textContent(),'Consulta local · sin sondeos');
 assert.equal(await frame.locator('#conditional').isVisible(),false,'Offline lookup claimed conditional measurements');
 const villages=await frame.evaluate(async()=>await (await fetch('/geolocation/localities?south=41.2&west=1.8&north=41.6&east=2.4')).json());
 assert.ok(villages.some(place=>place.name==='Barcelona'),'Real server cannot resolve bundled infrastructure labels offline');
 await frame.locator('#localityDetails > summary').click();
 if(await frame.locator('#localityCandidates button').count())await frame.locator('#localityCandidates button').first().click();
 assert.ok(!requests.some(url=>/geonames\.org|db-ip\.com|ipmap.*ripe|nominatim/.test(url)),'Locality lookup called an external geolocation API');
 await frame.locator('#configuration > summary').click();
 assert.equal(tiles.length,0,'Running an offline analysis fetched street tiles');
 await frame.locator('[data-layer=streets]').check();
 await frame.waitForFunction(()=>Number(document.querySelector('canvas').dataset.fullDraws)>0);
 await frame.evaluate(()=>{for(let i=0;i<20;i++)document.querySelector('[data-map=in]').click();});
 await frame.waitForFunction(()=>Number(document.querySelector('canvas').dataset.zoom)>=18);
 await page.waitForFunction(()=>document.querySelector('#geolocationWorkbench').contentDocument.querySelector('.map-status').textContent.includes('Calles en línea'));
 await new Promise(resolve=>setTimeout(resolve,500));
 assert.ok(tiles.some(tile=>/\/1[89]\//.test(tile.url)),'Zoom did not reach street-level tiles');
 assert.ok(tiles.every(tile=>tile.headers.referer===url),'Street tiles lost the required origin-only referrer');
 assert.ok(tiles.length<50,'Map prefetched unrelated zoom levels/areas');
 await frame.locator('[data-layer=streets]').uncheck();
 await frame.waitForFunction(()=>document.querySelector('.map-status').textContent.includes('Mapa local'));
 mkdirSync('.artifacts/geolocation-workbench',{recursive:true});
 for(const width of [1280,390,320]){
   await page.setViewportSize({width,height:1000});
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Settings overflowed');
   assert.ok(await frame.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Workbench overflowed');
   await page.locator('#geolocationWorkbench').scrollIntoViewIfNeeded();
   const draws=await frame.locator('canvas').getAttribute('data-full-draws');
   await frame.locator('[data-map=focus]').click();
   await frame.waitForFunction(previous=>Number(document.querySelector('canvas').dataset.fullDraws)>Number(previous),draws);
   await frame.waitForFunction(()=>Number(document.querySelector('canvas').dataset.zoom)>8);
   await page.screenshot({path:`.artifacts/geolocation-workbench/${width}.png`,fullPage:true});
 }
 const closes=()=>requests.filter(u=>u.includes('/api/geolocation/workbench/close')).length;
 const closed=page.waitForRequest('**/api/geolocation/workbench/close');
 await page.evaluate(()=>QP.navigate('settings','/settings.html?section=profile'));await closed;
 assert.equal(await page.locator('#geolocationWorkbench').getAttribute('src'),null,'Leaving settings retained the iframe');
 assert.equal(panel(),undefined,'Leaving settings retained the workbench document');
 const polls=requests.filter(u=>u.includes('/api/geolocation/workbench/state')).length;
 await new Promise(resolve=>setTimeout(resolve,1200));
 assert.equal(requests.filter(u=>u.includes('/api/geolocation/workbench/state')).length,polls,'Polling continued after leaving the panel');
 assert.ok(closes()>0);
 await page.evaluate(()=>QP.navigate('settings','/settings.html?section=geolocation'));
 await page.waitForFunction(()=>document.querySelector('#geolocationWorkbench').contentDocument?.querySelector('#start')?.disabled===false);
 frame=panel();assert.equal(await frame.locator('#logOutput').textContent(),'Sin registros todavía.','Closing the panel retained its packet history');
 assert.deepEqual(errors,[]);
 console.log('PASS real-server workbench: offline infrastructure labels, new engine status, map labels without external APIs, lazy loading, logs, optional tiles, responsive layouts, unload/stop and reopen.');
}finally{
 await browser?.close();server.stdin.end('stop\n');
 await new Promise(resolve=>{const timer=setTimeout(()=>{server.kill();resolve();},5000);server.once('exit',()=>{clearTimeout(timer);resolve();});});
}

