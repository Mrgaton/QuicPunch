import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Synthetic locations only. No database download and no remote IP requests.
import {fileURLToPath} from 'node:url';
import {mkdirSync, readFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const codes=JSON.parse(readFileSync(new URL('../../QuicNet.Shared/wwwroot/flags/codes.json',import.meta.url),'utf8'));
assert.ok(codes.length>=249);
for(const code of codes)assert.match(readFileSync(new URL('../../QuicNet.Shared/wwwroot/flags/'+code+'.svg',import.meta.url),'utf8'),/<svg\b/);
const server = startPreviewServer([]);
let browser;
try{
 const url = await server.ready;
 browser = await launchBrowser();
 const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[],external=[];
 page.on('pageerror',error=>errors.push(error.message));page.on('request',request=>{if(!request.url().startsWith(url)&&!request.url().startsWith('data:'))external.push(request.url());});
 await page.goto(url);await page.waitForFunction(()=>window.QP?.state.status);
 await page.evaluate(()=>{
  const fixture=structuredClone(QP.state.status);
  // Country rendering includes both transports; WAN-only mode deliberately filters the Tor card.
  fixture.tor={...fixture.tor,running:true,isStarted:true,enabled:true,desiredEnabled:true};
  const places=[
   {status:'located',country:'España',countryCode:'ES',city:'Madrid',region:'Comunidad de Madrid'},
   {status:'located',country:'Reino Unido',countryCode:'gb',city:'Londres',region:'Inglaterra',advertised:true},
   {status:'located',country:'Japón',countryCode:'JP'},
   {status:'located',country:'España',countryCode:'ES'},
   {status:'local'}, {status:'missing'},
   {status:'located',country:'País sin bandera',countryCode:'ZZ',city:'Ciudad de prueba'},
   {status:'located',country:'Sin código',countryCode:'../../bad',city:'Otra ciudad'}];
  fixture.peers=places.map((location,i)=>({...fixture.peers[0],id:'10000000-0000-0000-0000-'+String(i+1).padStart(12,'0'),certHash:btoa(String.fromCharCode(i+11).repeat(32)),name:'Contacto '+(i+1),isQuicConnected:true,isTrusted:true,isSaved:true,isTor:i===3,location}));
  fixture.savedPeers=fixture.peers;fixture.discoveredPeers=[];QP.state.status=fixture;QP.emit('status_updated',fixture);
 });
 await page.waitForFunction(()=>document.querySelectorAll('.contact-card').length===8);
 const card=index=>page.locator('.contact-card').filter({has:page.locator('.contact-card-name strong',{hasText:'Contacto '+index})});
 await page.waitForFunction(()=>[...document.querySelectorAll('.country-flag')].every(img=>img.complete&&img.naturalWidth>0));
 assert.equal(await card(1).locator('.country-flag').getAttribute('alt'),'España');
 assert.match(await card(1).locator('.country-flag').getAttribute('src'),/\/flags\/es\.svg$/);
 assert.match(await card(1).locator('.contact-location').innerText(),/Madrid/);
 assert.ok(!(await card(1).locator('.contact-location').innerText()).includes('España'),'Country text was not replaced by its flag');
 assert.match(await card(2).locator('.contact-location').getAttribute('title'),/Reino Unido.*IP anunciada/);
 assert.equal(await card(3).locator('.country-flag').getAttribute('alt'),'Japón');
 assert.equal(await card(4).locator('.country-flag').count(),0,'Tor exposed a country flag');
 assert.match(await card(4).locator('.contact-location').innerText(),/Tor/);
 assert.equal(await card(5).locator('.country-flag').count(),0);
 assert.match(await card(6).locator('.contact-location').innerText(),/Preparando (ubicación|base de localidades)/);
 assert.match(await card(7).locator('.contact-location').innerText(),/País sin bandera/);
 assert.equal(await card(8).locator('.country-flag').count(),0,'Invalid country code generated an asset URL');
 mkdirSync(fileURLToPath(new URL('../../.artifacts/geolocation/',import.meta.url)),{recursive:true});
 for(const width of [1280,390])for(const theme of ['dark']){
  await page.setViewportSize({width,height:900});await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Country flags overflow the viewport');
  await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/geolocation/'+theme+'-'+width+'.png',import.meta.url)),fullPage:true});
 }
 assert.deepEqual(errors,[]);assert.deepEqual(external,[],'Country display requested a remote resource');
 console.log('PASS local country flags: all bundled assets, ES/GB/JP, accessible country names, city/region, unknown/missing code fallback, Tor/local exclusions, offline resources and mobile/dark');
}finally{await closeBrowserFixture(browser, server);}
