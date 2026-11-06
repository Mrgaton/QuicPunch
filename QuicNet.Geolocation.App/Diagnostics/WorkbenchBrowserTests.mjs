// Synthetic saved evidence exercises the real Blazor host; no Internet probes or map APIs.
import {createRequire} from 'node:module';
import {spawn} from 'node:child_process';
import {mkdirSync,writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import assert from 'node:assert/strict';
const {chromium}=createRequire(import.meta.url)(process.env.QUICNET_PLAYWRIGHT_MODULE||'playwright');
const directory=resolve('.artifacts/geolocation-web-ui');mkdirSync(directory,{recursive:true});
const point={latitude:40.44,longitude:-3.62};
const hypothesis={id:'synthetic-region',level:'regional-area',label:'Región de prueba',countryCode:'ES',position:point,relativeScore:.6,searchRadiusKm:150,independentSites:4,basis:'synthetic-fixture'};
const fixture={revision:2,level:'Unknown',phase:'time-budget-exhausted',isFinal:true,databasePrior:null,asn:null,region:null,candidates:[],routers:[],measuredLandmarks:4,sentPackets:12,change:'Synthetic UI fixture',notes:[],
 enhanced:{hierarchy:{stage:'regional-candidates',hypotheses:[hypothesis],nearbyPlaces:[],explanation:'Synthetic alternatives'},fingerprints:{status:'mixed-network-context',matches:[],explanation:'Synthetic mixed routes'},topology:{sharedRouters:[],routePairs:[],distinctRoutes:2,explanation:'Synthetic routes'},tunnel:{state:'LocalTunnelIdentified',routes:[{destination:'8.8.8.8',sourceAddress:'192.0.2.2',interfaceId:'synthetic',interfaceName:'Interfaz de prueba',interfaceDescription:'Synthetic',interfaceIndex:1,mtu:1400,interfaceType:'Tunnel',usesLocalTunnel:true,method:'synthetic',error:null}],pathMtu:[{destination:'8.8.8.8',addressFamily:'ipv4',status:'bounded',lowerBoundBytes:1380,upperBoundBytes:1400,attempts:[],explanation:'Synthetic bounds'}],reasons:[],mixedRoutes:true},supplemental:[{landmarkId:'synthetic',protocol:'tcp',statistics:{attempts:2,successes:1,minimumMs:15,medianMs:15,madMs:0,lossFraction:.5},measuredUtc:new Date().toISOString()}]}};
// Regression of the reported failure: no verified primary region, conditional heat,
// a mathematical optimum in the Bay of Biscay, and actual reference rows.
const sea={latitude:43.8394,longitude:-4.8953},barcelona={latitude:41.3874,longitude:2.1686};
const values=Buffer.alloc(64*64);
for(let y=0;y<64;y++)for(let x=0;x<64;x++){
 const lon=-10+(x+.5)/64*15,lat=Math.atan(Math.sinh(Math.PI*(1-2*((1-Math.asinh(Math.tan(45*Math.PI/180))/Math.PI)/2+(y+.5)/64*((1-Math.asinh(Math.tan(35*Math.PI/180))/Math.PI)/2-(1-Math.asinh(Math.tan(45*Math.PI/180))/Math.PI)/2)))))*180/Math.PI;
 values[y*64+x]=1+Math.round(254*Math.exp(-((lon-2.17)**2+(lat-41.39)**2)/10));
}
const heat={west:-10,north:45,east:5,south:35,width:64,height:64,values:values.toString('base64'),hints:[],peaks:[{position:barcelona,scaleKm:160,label:'Barcelona',inferred:true}],explanation:'Synthetic conditional heat'};
fixture.region={status:'insufficient-data',representative:null,coveringRadiusKm:null,areaKm2:0,cells:[],constraints:[]};
fixture.regionalRefinement={region:{status:'candidate-region',representative:sea,coveringRadiusKm:900,areaKm2:300000,cells:[{south:35,west:-10,north:45,east:5}],constraints:[]},support:heat,respondingSites:4,networkGroups:3,minimumRttMs:3.4,explanation:'Synthetic operator declarations'};
fixture.localities={status:'regional-estimate',summary:'Zona estimada a partir de varias referencias',precision:'estimate',evidence:'CONDITIONAL fixture',position:sea,modelSpreadKm:81,fit:{position:sea,modelSpreadKm:81,residualMs:6,accessDelayMs:3,independentSites:4,diverseGeometry:true,alternatives:[sea]},candidates:[],publishedPlace:null,limitations:[]};
fixture.enhanced.measurements=[{id:'bcn',address:'192.0.2.1',siteId:'bcn',name:'Barcelona',countryCode:'ES',position:barcelona,minimumMs:3.4,medianMs:4.2,successes:3,attempts:4,coordinatesVerified:false,source:'synthetic-fixture'},{id:'mad',address:'192.0.2.2',siteId:'mad',name:'Madrid',countryCode:'ES',position:{latitude:40.42,longitude:-3.7},minimumMs:12.8,medianMs:15,successes:4,attempts:4,coordinatesVerified:false,source:'synthetic-fixture'}];
fixture.enhanced.routeLocation={status:'route-informed-affinity',usableRoutes:3,identifiedBranches:0,contrasts:[{firstLandmarkId:'bcn',secondLandmarkId:'mad',deltaRttMs:-9.4,noiseMs:4,sharedRouters:2,sharedEdges:1,weight:1}],zones:[{countryCode:'ES',region:'Cataluña',label:'Cataluña',position:barcelona,relativeScore:.6,latencyLoss:.2,routeLoss:.1,sites:2,tracedSites:2,branches:0,reasons:['Referencia más rápida: Barcelona, 3.40 ms.','Ruta sintética compartida: dos routers estables.','La salida del operador no confirma la ciudad del dispositivo.']}],limitations:['Datos sintéticos de interfaz; no son mediciones reales.']};
fixture.enhanced.topology.traces=[{landmarkId:'bcn',stablePublicHops:2,stablePathHops:5,unstableHops:1,missingHops:3,reached:true,destinationTtl:12,branch:'unresolved:bcn',stability:.7}];
writeFileSync(`${directory}/result.json`,JSON.stringify(fixture));
writeFileSync(`${directory}/settings.json`,JSON.stringify({enablePathMtu:true,enableTcp:true}));
const server=spawn('dotnet',['run','--project','QuicNet.Geolocation.App','-c',process.env.QUICNET_TEST_CONFIGURATION||'Release','--no-build','--','--no-browser','--session',directory],{stdio:['pipe','pipe','pipe'],windowsHide:true});
let browser;let output='';server.stderr.on('data',data=>output+=data);
try{
 const url=await new Promise((resolve,reject)=>{const timeout=setTimeout(()=>reject(Error('Host timeout: '+output)),20000);server.once('error',reject);server.stdout.on('data',data=>{for(const line of data.toString().split('\n')){try{const ready=JSON.parse(line);if(ready.kind==='ready'){clearTimeout(timeout);resolve(ready.url);}}catch{}}});});
 browser=await chromium.launch({headless:true,...(process.env.QUICNET_TEST_BROWSER_PATH?{executablePath:process.env.QUICNET_TEST_BROWSER_PATH}:{channel:'chrome'})});
 const page=await browser.newPage({viewport:{width:1280,height:1000}}),errors=[],external=[],tiles=[];
 await page.route('https://tile.openstreetmap.org/**',route=>{tiles.push(route.request().url());return route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256"><rect width="256" height="256" fill="#dbe5de"/><path d="M0 80H256M70 0V256" stroke="#fff" stroke-width="10"/></svg>'});});
 page.on('pageerror',e=>errors.push(e.message));page.on('request',r=>{if(!r.url().startsWith(new URL(url).origin)&&!r.url().startsWith('data:'))external.push(r.url());});
 await page.goto(url);await page.waitForFunction(()=>document.querySelector('canvas')?.dataset.renderer);
 await page.getByText('Tiempo agotado · resultado parcial',{exact:true}).first().waitFor();
 assert.match(await page.locator('.result-meta').textContent(),/12 sondeos · 4 referencias/);
 await page.waitForFunction(()=>document.querySelector('canvas').dataset.heatSource==='conditional'&&Number(document.querySelector('canvas').dataset.heatCells)>0);
 assert.equal(await page.locator('select[data-community] option').count(),20);
 assert.match(await page.locator('.result h2').textContent(),/Ubicación sin resolver/);
 assert.ok(!(await page.locator('.result').textContent()).includes('43.8394'),'Maritime fit was presented as the location');
 assert.match(await page.locator('#reasoning').textContent(),/Barcelona: 3[.,]40 ms/);
 assert.match(await page.locator('#reasoning').textContent(),/Mayor afinidad de red del modelo: Cataluña/);
 await page.locator('#alternatives > summary').click();
 assert.match(await page.locator('#alternatives').textContent(),/Ruta sintética compartida: dos routers estables/);
 await page.locator('#alternatives > summary').click();
 await page.waitForFunction(()=>document.querySelector('canvas').dataset.modelVisible==='false');
 await page.locator('[data-model-warning]').waitFor({state:'visible'});
 await page.locator('select[data-community]').selectOption('CT');
 await page.waitForFunction(()=>document.querySelector('canvas').dataset.community==='CT');
 assert.match(await page.locator('[data-community-status]').textContent(),/Cataluña/);
 await page.locator('.community-comparison > summary').click();
 assert.equal(await page.locator('[data-community-comparison] button').count(),19);
 await page.locator('[data-region=GA]').click();assert.match(await page.locator('[data-community-status]').textContent(),/Galicia/);
 await page.locator('[data-region=CT]').click();await page.locator('.community-comparison > summary').click();
 await page.locator('[data-layer=heat]').uncheck();await page.waitForFunction(()=>document.querySelector('canvas').dataset.heatVisible==='false');
 await page.locator('[data-layer=heat]').check();await page.waitForFunction(()=>document.querySelector('canvas').dataset.heatVisible==='true');
 await page.locator('select[data-heat-source]').selectOption('primary');await page.waitForFunction(()=>document.querySelector('canvas').dataset.heatSource==='none');
 await page.locator('select[data-heat-source]').selectOption('conditional');await page.waitForFunction(()=>document.querySelector('canvas').dataset.heatSource==='conditional');
 await page.locator('#measurements > summary').click();assert.match(await page.locator('#measurements table').textContent(),/3 \/ 4/);
 const exported=await page.request.get(new URL('analysis.json',url).href);assert.equal(exported.status(),200);assert.equal((await exported.json()).regionalRefinement.respondingSites,4);
 await page.locator('#measurements > summary').click();
 assert.equal(tiles.length,0,'Detailed tiles were fetched before explicit activation');
 await page.locator('[data-layer=streets]').check();await page.waitForFunction(()=>document.querySelector('.map-status').textContent.includes('Calles en línea'));
 assert.ok(tiles.length>0);await page.locator('[data-layer=streets]').uncheck();
 assert.equal(await page.locator('#alternatives').getAttribute('open'),null,'Alternatives expanded on arrival');
 await page.locator('#alternatives > summary').click();
 assert.match(await page.locator('#alternatives').textContent(),/Región de prueba/);
 await page.locator('#alternatives').getByRole('button',{name:'Ver zona',exact:true}).click();await page.waitForFunction(()=>Number(document.querySelector('canvas').dataset.zoom)>5);
 await page.locator('#network > summary').click();
 assert.match(await page.locator('#network').textContent(),/Túnel local identificado/);
 assert.match(await page.locator('#network').textContent(),/suspendida/);
 assert.match(await page.locator('#network table').last().textContent(),/Interfaz de prueba/);
 assert.match(await page.locator('#network').textContent(),/Calidad de los traceroutes/);
 assert.match(await page.locator('#network').textContent(),/1380/);
 await page.locator('#network > summary').click();await page.locator('#alternatives > summary').click();
 for(const width of [1280,390,320]){
  await page.setViewportSize({width,height:1000});
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Standalone page overflow');
  await page.locator('select[data-community]').selectOption('CT');
  await page.waitForFunction(()=>document.querySelector('canvas').dataset.heatVisible==='true'&&document.querySelector('canvas').dataset.community==='CT');
  await page.locator('.location-map').screenshot({path:`${directory}/map-${width}.png`});
  await page.screenshot({path:`${directory}/standalone-${width}.png`,fullPage:true});
 }
 await page.locator('#configuration > summary').click();
 assert.equal(await page.getByLabel('Medir Path MTU',{exact:true}).isChecked(),true);
 assert.equal(await page.getByLabel('Medir TCP en destinos autorizados').isChecked(),true);
 await page.getByLabel('Límite de sondeos',{exact:true}).fill('0');await page.locator('#start').click();
 await page.getByRole('alert').filter({hasText:'Parámetros fuera'}).waitFor();
 await page.locator('#profile').selectOption({label:'Sin red'});await page.locator('#start').click();
 await page.getByText('Consulta local · sin sondeos',{exact:true}).first().waitFor();
 assert.equal(await page.locator('#network').count(),0,'Offline restart retained old tunnel evidence');
 assert.equal(await page.locator('#alternatives').count(),0,'Offline restart retained old hypotheses');
 assert.match(await page.locator('.result-meta').textContent(),/0 sondeos · 0 referencias/);
 assert.deepEqual(errors,[]);assert.ok(external.every(url=>url.startsWith('https://tile.openstreetmap.org/')),'An external geolocation or training API was called');
 console.log('PASS real Blazor: conditional heat pixels, 19 community boundaries/navigation/comparison, maritime-model regression, reference RTT explanation, JSON export, partial/tunnel panels, offline restart, no remote requests and responsive layouts.');
}finally{
 await browser?.close();server.stdin.end('stop\n');
 await new Promise(resolve=>{const timer=setTimeout(()=>{server.kill();resolve();},5000);server.once('exit',()=>{clearTimeout(timer);resolve();});});
}
