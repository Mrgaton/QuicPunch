import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:900}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await page.waitForFunction(()=>QP.state.status&&QP.isWsConnected);
  const hashes=Object.fromEntries(['first','second','third'].map((id,index)=>[id,Buffer.alloc(32,index+1).toString('base64')]));
  await page.evaluate(hashes=>{
    const peer=(id,extra)=>({id,certHash:hashes[id],canonicalId:'identity-'+id,name:'Mismo nombre',isSaved:true,isTrusted:true,isQuicConnected:false,networkType:'Static',...extra});
    window.homeSnapshot={...QP.state.status,tor:{desiredEnabled:true,isStarted:true},peers:[],discoveredPeers:[],savedPeers:[
      peer('first',{name:'Ana',lastConnectedUtc:'2026-10-16T12:00:00Z',lastUsedUtc:'2026-10-18T18:00:00Z',usageCount:9,locationIsHistorical:true,location:{status:'located',city:'Barcelona',country:'España',countryCode:'ES'}}),
      peer('second',{lastConnectedUtc:'2026-10-18T12:00:00Z',usageCount:2}),
      peer('third',{usageCount:0,isTor:true,location:{status:'located',city:'No mostrar esta ciudad'}})]};
    window.pushHome=()=>{QP.state.status=homeSnapshot;QP.emit('status_updated',homeSnapshot);};pushHome();
  },hashes);
  const card=id=>page.locator(`[data-contact-key="${hashes[id]}"]`);
  const keys=async()=>(await page.locator('#homeContacts .contact-card').evaluateAll(cards=>cards.map(c=>c.dataset.contactKey))).map(hash=>Object.keys(hashes).find(id=>hashes[id]===hash));
  assert.deepEqual(await keys(),['first','second','third']);
  assert.match(await card('first').locator('.contact-location').textContent(),/Última IP.*Barcelona/);
  assert.match(await card('first').locator('.contact-last-used').textContent(),/Último uso/);
  assert.equal(await page.getByText('No mostrar esta ciudad').count(),0);
  await page.locator('#contactSort').selectOption('usage');assert.deepEqual(await keys(),['first','second','third']);
  await page.locator('#contactSort').selectOption('name');assert.equal((await keys())[0],'first');
  await page.waitForFunction(()=>[...document.querySelectorAll('.identity-mark')].every(mark=>!mark.hasAttribute('aria-busy')));
  const identityStyle = id => card(id).locator('.ci').evaluate(node => JSON.stringify([node.dataset.edge,node.dataset.pattern,node.getAttribute('style')]));
  assert.notEqual(await identityStyle('second'),await identityStyle('third'));
  await page.locator('#contactSearch').fill('Ana');assert.deepEqual(await keys(),['first']);
  await page.locator('#contactSearch').fill('');
  const add=page.locator('[data-focus-connect]');await add.click();
  assert.equal(await page.locator('#addContactDialog').evaluate(d=>d.open),true);
  assert.equal(await page.locator('#tokenInput').evaluate(e=>e===document.activeElement),true);
  await page.evaluate(()=>Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async()=>{throw new Error('Clipboard denied for fixture');}}}));
  await page.locator('#shareInvitationBtn').click();
  assert.equal(await page.locator('#invitationManualCopy').isVisible(),true);
  assert.match(await page.locator('#invitationCopyValue').inputValue(),/^QP:\/\//);
  await page.locator('#connectTokenBtn').click();assert.match(await page.locator('#connectFeedback').textContent(),/Pega una invitación/);
  await page.locator('#tokenInput').fill('invitation-invalid');await page.locator('#connectTokenBtn').click();
  await page.waitForFunction(()=>document.querySelector('#tokenInput').getAttribute('aria-invalid')==='true'&&!document.querySelector('#connectTokenBtn').disabled);
  assert.ok(await page.locator('#connectFeedback').textContent());
  await page.keyboard.press('Escape');assert.equal(await add.evaluate(e=>e===document.activeElement),true);
  assert.equal(await page.locator('#homeWanToggleBtn').isVisible(),false);
  await page.getByRole('link',{name:'Configurar conexión',exact:true}).click();
  await page.locator('#homeWanToggleBtn').waitFor({state:'visible'});
  await page.evaluate(()=>{QP.navigate('dashboard');pushHome();});
  await page.locator('.toast').evaluateAll(nodes=>nodes.forEach(node=>node.remove()));
  for(const width of [2560,1440,768,390,320]) {
    await page.setViewportSize({width,height:900});
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Home overflow at '+width);
    await page.screenshot({path:'.artifacts/contact-home-'+width+'.png',fullPage:true});
    await add.click();
    assert.ok(await page.locator('#addContactDialog').evaluate(d=>d.scrollWidth<=d.clientWidth),'Invitation overflow at '+width);
    await page.screenshot({path:'.artifacts/contact-invitation-'+width+'.png'});
    await page.locator('#closeAddContact').click();
  }
  await page.reload();await page.waitForFunction(()=>QP.state.status);assert.equal(await page.locator('#contactSort').inputValue(),'name');
  assert.deepEqual(errors,[]);
  console.log('PASS contact home: persisted sorting, history/location, distinct identities, search, invitation validation/focus, settings placement and 320–2560px layouts');
} finally {await closeBrowserFixture(browser, server);}
