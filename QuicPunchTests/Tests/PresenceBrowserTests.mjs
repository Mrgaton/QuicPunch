import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Isolated fixtures: no input hooks, actual peers, webcam or microphone.
import {fileURLToPath} from 'node:url';
import {mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try{
 const url = await server.ready;
 browser = await launchBrowser();
 mkdirSync(fileURLToPath(new URL('../../.artifacts/presence/',import.meta.url)),{recursive:true});
 for(const width of [1280,390,320]){
  const page=await browser.newPage({viewport:{width,height:900}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  let presence={isAfk:false,acceptsNotifications:true,doNotDisturb:false,idleAfterMinutes:5,activitySource:'system',doNotDisturbUntil:null};
  let rejectSave=false,activity=0;
  await page.route('**/api/app/preferences',route=>route.fulfill({json:{success:true,startupSupported:true,platformHelp:'Fixture',settings:{startWithSystem:true,runInBackground:true,notifications:{enabled:true,desktop:true,inApp:true,sound:true,preview:true,whileFocused:true,doNotDisturb:presence.doNotDisturb,quietHours:false,quietStart:1320,quietEnd:480,volume:60,tone:'soft',features:{},contacts:{}}}}}));
  await page.route('**/api/profile/activity',route=>{activity++;return route.fulfill({status:204});});
  await page.route('**/api/profile/presence',async route=>{
   if(route.request().method()==='POST'){
    if(rejectSave)return route.fulfill({status:503,json:{error:'Fallo de prueba al guardar'}});
    const change=route.request().postDataJSON();presence={...presence,...change,acceptsNotifications:!change.doNotDisturb,doNotDisturbUntil:null};
   }
   return route.fulfill({json:presence});
  });
  await page.goto(url);await page.waitForFunction(()=>window.QP?.userPresence);
  await page.waitForFunction(()=>!document.getElementById('appBehaviorFields').disabled);
  await page.evaluate(async value=>{window.presenceFixture={...await QP.getStatus(),presence:value};window.updatePresenceFixture=()=>{QP.state.status=presenceFixture;QP.emit('status_updated',presenceFixture);};updatePresenceFixture();},presence);
  if(width>920)await page.click('#selfPresenceButton');
  else {await page.evaluate(()=>QP.navigate('settings'));await page.click('#profilePresenceButton');}
  await page.locator('#userPresenceDialog').waitFor({state:'visible'});
  assert.equal(await page.locator('#presenceAvailable').getAttribute('aria-pressed'),'true');
  assert.equal(await page.locator('.presence-state-guide').getAttribute('open'),null,'Help should be collapsed initially');
  assert.ok(await page.locator('.presence-choice-icon svg').evaluateAll(nodes=>nodes.every(svg=>{const box=svg.getBBox();return box.width>15&&box.height>15&&svg.getAttribute('viewBox')==='0 0 24 24';})),'Status SVGs must render at a consistent size');
  await page.click('#presenceDnd');
  await page.waitForFunction(()=>document.getElementById('presenceDnd').getAttribute('aria-pressed')==='true'&&!document.getElementById('presenceDnd').disabled);
  assert.equal(presence.doNotDisturb,true);
  await page.waitForFunction(()=>{
   const ctx=document.createElement('canvas').getContext('2d');ctx.fillStyle=getComputedStyle(document.getElementById('presenceDnd')).borderTopColor;ctx.fillRect(0,0,1,1);
   const [r,g,b]=ctx.getImageData(0,0,1,1).data;return r>g&&r>b;
  });
  assert.equal(await page.locator('#sidebarPresenceDot').getAttribute('data-user-presence'),'dnd');
  assert.ok(await page.locator('#noticeDnd').isChecked(),'Quick status must synchronize notification settings');
  await page.selectOption('#presenceIdleMinutes','2');await page.waitForFunction(()=>!document.getElementById('presenceIdleMinutes').disabled);
  assert.equal(presence.idleAfterMinutes,2);
  presence={...presence,isAfk:true};
  await page.evaluate(value=>{presenceFixture.presence=value;updatePresenceFixture();},presence);
  assert.equal(await page.locator('#sidebarPresenceDot').getAttribute('data-user-presence'),'afk-dnd');
  assert.equal(await page.locator('#userPresenceCurrent').innerText(),'Ausente · No molestar');
  assert.match(await page.locator('#sidebarPresenceDot').evaluate(n=>getComputedStyle(n).backgroundImage),/data:image\/svg\+xml/,'Combined state should use a crisp SVG instead of a gradient and text glyph');
  assert.equal(await page.locator('#presenceCurrentIcon').getAttribute('href'),'#presence-icon-afk-dnd');
  for(const theme of ['dark']){
   await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
   const box=await page.locator('#userPresenceDialog').boundingBox();assert.ok(box.x>=0&&box.y>=0&&box.x+box.width<=width&&box.y+box.height<=900);
   await page.screenshot({animations:'disabled',path:fileURLToPath(new URL('../../.artifacts/presence/'+theme+'-'+width+'.png',import.meta.url))});
   if(width===1280)await page.locator('#userPresenceDialog').screenshot({animations:'disabled',path:fileURLToPath(new URL('../../.artifacts/presence/status-panel.png',import.meta.url))});
  }
  await page.locator('.presence-state-guide summary').click();
  await page.setViewportSize({width,height:480});
  const compact=await page.locator('#userPresenceDialog').boundingBox();
  assert.ok(compact.y>=0&&compact.y+compact.height<=480,'Status panel escapes a short viewport');
  await page.setViewportSize({width,height:900});
  assert.equal(await page.locator('.user-presence-legend > div:visible').count(),4);
  await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/presence/guide-'+width+'.png',import.meta.url))});
  await page.locator('.presence-state-guide summary').click();
  presence={...presence,isAfk:false};
  await page.evaluate(value=>{presenceFixture.presence=value;updatePresenceFixture();},presence);
  assert.equal(await page.locator('#sidebarPresenceDot').getAttribute('data-user-presence'),'dnd','Activity must not turn off DND');
  rejectSave=true;await page.click('#presenceAvailable');await page.waitForFunction(()=>document.getElementById('presenceSaveStatus').textContent.includes('Fallo'));
  assert.equal(await page.locator('#presenceDnd').getAttribute('aria-pressed'),'true','Failed save changed the selected state');
  assert.equal(await page.locator('#presenceSaveStatus').getAttribute('data-state'),'error');
  await page.selectOption('#presenceIdleMinutes','30');
  await page.waitForFunction(()=>!document.getElementById('presenceIdleMinutes').disabled);
  assert.equal(await page.locator('#presenceIdleMinutes').inputValue(),'2','Failed save left an unsaved idle threshold on screen');
  rejectSave=false;await page.click('#presenceAvailable');await page.waitForFunction(()=>document.getElementById('presenceAvailable').getAttribute('aria-pressed')==='true'&&!document.getElementById('presenceAvailable').disabled);
  assert.equal(await page.locator('#sidebarPresenceDot').getAttribute('data-user-presence'),'available');
  await page.keyboard.press('Escape');assert.ok(!await page.locator('#userPresenceDialog').isVisible());
  assert.ok(await page.evaluate(()=>['selfPresenceButton','profilePresenceButton'].includes(document.activeElement.id)));
  await page.evaluate(()=>{
   presenceFixture.peerPresence={[presenceFixture.peers[0].id]:{IsAfk:true,AcceptsNotifications:false}};
   updatePresenceFixture();QP.navigate('chat');
  });
  await page.waitForFunction(()=>document.querySelector('#chatPeers [data-user-presence="afk-dnd"]'));
  assert.ok(await page.locator('#chatPeers .dm-peer-meta').filter({hasText:'Ausente · No molestar'}).count());
  await page.evaluate(()=>{presenceFixture.peers[0].isQuicConnected=false;updatePresenceFixture();});
  assert.equal(await page.locator('#chatPeers [data-user-presence="afk-dnd"]').count(),0,'Offline peer retained its old active presence');
  assert.ok(activity>0&&activity<20,'Activity reporting was not throttled');
  assert.deepEqual(errors,[]);await page.close();
 }
 console.log('PASS presence: consistent SVGs, compact dialog at 320/390/1280px, collapsible state guide, four states, DND settings sync, auto AFK, failed save/idle rollback, peer/offline display and keyboard');
}finally{await closeBrowserFixture(browser, server);}
