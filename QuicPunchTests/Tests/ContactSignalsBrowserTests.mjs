import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  let installRequests=0;
  await page.routeWebSocket('**/ws?*',socket=>{
    const backend=socket.connectToServer();
    socket.onMessage(message=>{
      if(typeof message==='string') {
        const request=JSON.parse(message);
        if(request.command==='geolocation.install') {
          installRequests++;
          socket.send(JSON.stringify({type:'response',id:request.id,success:true,data:{ready:false,downloading:true,downloadedBytes:0}}));
          return;
        }
      }
      backend.send(message);
    });
    backend.onMessage(message=>socket.send(message));
  });
  await page.goto(url);await page.waitForFunction(()=>QP.state.status&&QP.isWsConnected);
  await page.evaluate(()=>{
    const peer=(id,extra)=>({id,name:id,certHash:id,isTrusted:true,isSaved:false,isQuicConnected:false,networkType:'Wan',...extra});
    window.signalSnapshot={...QP.state.status,wan:{desiredEnabled:true},tor:{desiredEnabled:true},savedPeers:[peer('Barcelona',{isSaved:true})],discoveredPeers:[],peers:[
      peer('Barcelona',{isSaved:true,isQuicConnected:true,ping:12.4,pingSource:'QUIC RTT',location:{status:'located',city:'Barcelona',region:'Cataluña',country:'España',countryCode:'ES'}}),
      peer('Conectando',{connectionState:'connecting',ping:null,location:{status:'missing'}}),
      peer('Bogotá',{isTrusted:false,ping:89,location:{status:'located',city:'Bogotá',region:'Bogotá',country:'Colombia',countryCode:'CO',advertised:true}}),
      peer('Privado',{isTrusted:false,isTor:true,location:{status:'located',city:'Ciudad filtrada',country:'España'}})
    ],geolocation:{ready:false,downloading:false,downloadedBytes:0}};
    window.pushSignals=()=>{QP.state.status=window.signalSnapshot;QP.emit('status_updated',window.signalSnapshot);};
    window.pushSignals();
  });
  await page.locator('.contact-options > summary').click();
  await page.locator('#contactConnectionFilter').selectOption('connected');
  const live=page.locator('#homeContacts .contact-card');
  assert.match(await live.locator('.contact-ping').textContent(),/12,4 ms/);
  assert.equal(await live.locator('.contact-location').textContent(),'≈ Barcelona, Cataluña');
  assert.equal(await live.locator('.country-flag').getAttribute('alt'),'España');
  await page.evaluate(()=>{signalSnapshot.peers[0].location={...signalSnapshot.peers[0].location,municipality:'Benillup',region:'Comunitat Valenciana',source:'Catálogo local'};pushSignals();});
  assert.equal(await live.locator('.contact-location').textContent(),'≈ Benillup, Comunitat Valenciana');
  assert.match(await live.locator('.contact-location').getAttribute('title'),/Catálogo local/);
  const call=live.getByRole('button',{name:'Llamar: Barcelona',exact:true});
  await call.focus();await call.evaluate(button=>button.dataset.sameElement='yes');
  await page.evaluate(()=>{signalSnapshot.peers[0].ping=21.8;pushSignals();});
  assert.match(await live.locator('.contact-ping').textContent(),/21,8 ms/);
  assert.equal(await call.getAttribute('data-same-element'),'yes');assert.equal(await call.evaluate(button=>button===document.activeElement),true);
  await page.evaluate(()=>{signalSnapshot.peers[0].isTor=true;pushSignals();});
  assert.equal(await live.locator('.contact-location').textContent(),'Tor · ubicación oculta','Transport change retained an old city');
  await page.evaluate(()=>{signalSnapshot.peers[0].isTor=false;pushSignals();});
  await page.locator('#contactConnectionFilter').selectOption('connecting');
  assert.equal(await page.locator('.contact-ping').textContent(),'Ping pendiente');
  await page.locator('#contactConnectionFilter').selectOption('all'); await page.locator('[data-contact-filter=review]').click();
  const colombia=page.locator('[data-contact-key="Bogotá"]');
  assert.equal(await colombia.locator('.contact-location').textContent(),'≈ Bogotá');
  assert.equal(await colombia.locator('.country-flag').getAttribute('alt'),'Colombia');
  assert.match(await colombia.locator('.contact-location').getAttribute('title'),/IP anunciada/);
  assert.match(await colombia.locator('.contact-ping').textContent(),/Último ping/);
  assert.equal(await page.locator('[data-contact-key=Privado] .contact-location').textContent(),'Tor · ubicación oculta');
  assert.equal(await page.getByText('Ciudad filtrada').count(),0);
  await page.locator('[data-contact-filter=saved]').click();assert.equal(await page.locator('.contact-card-signals').count(),1);
  await page.evaluate(()=>QP.navigate('settings','/settings.html?section=geolocation'));
  assert.equal(await page.locator('#geolocationWorkbench').count(),0,'Archived laboratory is still present');
  assert.equal(await page.locator('#connectionGeolocationStatus').count(),0,'Automatic measurements are still present');
  assert.equal(await page.locator('#installGeolocation').count(),0,'Obsolete database download control is still available');
  assert.equal(installRequests,0,'Embedded catalogue triggered a database download');
  await page.evaluate(()=>{signalSnapshot.geolocation={ready:true,embeddedReady:true,downloading:false,downloadedBytes:0,databaseDate:'2026-10-22'};pushSignals();});
  await page.locator('#geolocationStatus').filter({hasText:'IPTrust integrado listo · edición 2026-10-22'}).waitFor();
  await page.evaluate(()=>{signalSnapshot.geolocation.error='Error de catálogo simulado';pushSignals();});
  assert.match(await page.locator('#geolocationStatus').textContent(),/Error de catálogo/);
  for(const theme of ['dark']) for(const width of [1280,390,320]) {
    await page.setViewportSize({width,height:900});await page.evaluate(theme=>{document.documentElement.dataset.theme=theme;QP.navigate('dashboard');pushSignals();},theme);
    await page.locator('#contactConnectionFilter').selectOption('connected');
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Contact metadata overflows');
    await page.screenshot({path:`.artifacts/contact-signals-${theme}-${width}.png`});
  }
  assert.deepEqual(errors,[]);
  console.log('PASS contact location/ping: live updates preserve focus, pending/recent RTT, saved location, Tor privacy, advertised source, embedded IPTrust status without downloads and responsive dark layouts');
} finally {await closeBrowserFixture(browser, server);}
