import assert from 'node:assert/strict';
import {mkdir} from 'node:fs/promises';
import {launchBrowser,startPreviewServer,closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server=startPreviewServer(['--interactive']);let browser;
try {
  const url=await server.ready;browser=await launchBrowser();
  const page=await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
  await page.addInitScript(()=>{window.__qp_is_android=true;});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  let enabled=false,opened=0;
  const status=()=>({enabled,soundEnabled:enabled,canOpenSettings:true,systemControlsSound:true,
    message:enabled?'Android permite estos avisos y su sonido.':'Android tiene bloqueadas estas notificaciones.'});
  await page.route('**/api/app/preferences',async route=>{
    const response=await route.fetch(),data=await response.json();
    data.notificationStatus=status();data.settings.notifications.whileFocused=true;
    await route.fulfill({json:data});
  });
  await page.route('**/api/notifications/status',route=>route.fulfill({json:status()}));
  await page.route('**/api/notifications/system-settings',route=>{opened++;return route.fulfill({status:204});});
  await page.route('**/api/notifications/test',route=>route.fulfill({json:{success:true,sent:enabled,reason:enabled?null:'system_blocked',message:status().message}}));
  await page.goto(url+'settings.html?section=notifications');
  await page.locator('#notificationSystemStatus').waitFor();
  assert.equal(await page.locator('#noticeTone').isVisible(),false,'Android channel sounds must be edited through Android');
  assert.equal(await page.locator('#noticeVolumeLabel').textContent(),'Volumen del timbre');
  assert.match(await page.locator('#notificationSystemMessage').textContent(),/bloqueadas/);
  await page.locator('#testAppNotification').click();
  await page.getByRole('alertdialog',{name:'Prueba de notificaciones'}).waitFor();
  assert.match(await page.locator('#notificationTestMessage').textContent(),/bloqueadas/);
  await page.locator('#notificationTestClose').click();
  await page.locator('#openNotificationSettings').click();assert.equal(opened,1);
  enabled=true;await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
  await page.waitForFunction(()=>document.getElementById('notificationSystemStatus').dataset.state==='ready');
  await mkdir('.artifacts/mobile-notifications',{recursive:true});
  await page.locator('#notificationSystemStatus').scrollIntoViewIfNeeded();
  await page.screenshot({path:'.artifacts/mobile-notifications/system-settings.png'});
  // Every settings category must remain usable on narrow, normal and landscape phones.
  const sections=await page.locator('#settingsCategory option').evaluateAll(es=>es.map(e=>e.value));
  for(const viewport of [{width:320,height:568},{width:390,height:844},{width:667,height:375}]){
    await page.setViewportSize(viewport);
    for(const section of sections){
      await page.evaluate(section=>QP.navigate('/settings.html?section='+section),section);
      await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
      assert.equal(await page.locator('[data-settings-panel]:visible').getAttribute('id'),'settings-'+section);
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),section+' overflows '+viewport.width);
    }
    await page.evaluate(()=>QP.navigate('/'));
    await page.evaluate(()=>QP.emit('app_notice',{category:'messages',title:'Ana',message:'Te he enviado las fotos.',url:'/chat.html?peer=00000000-0000-0000-0000-000000000001'}));
    const action=page.getByRole('button',{name:'Abrir conversación',exact:true});await action.waitFor();
    assert.ok((await action.boundingBox()).height>=44);
    await action.click();await page.locator('#messageInput').waitFor();
    assert.match(page.url(),/chat\.html\?peer=00000000-0000-0000-0000-000000000001/);
    assert.equal(await page.locator('.toast').count(),0);
  }
  // A peer-provided notification link cannot leave the local application.
  await page.evaluate(()=>QP.emit('app_notice',{title:'Aviso',message:'No abrir fuera',url:'https://example.invalid/'}));
  assert.equal(await page.locator('.toast-action').count(),0);
  await page.evaluate(()=>document.querySelector('.toast-host').replaceChildren());
  await page.setViewportSize({width:390,height:360});await page.locator('#messageInput').fill('El teclado deja espacio para leer');
  const bounds=await page.evaluate(()=>({nav:document.querySelector('.nav-wrap').checkVisibility(),
    input:document.getElementById('messageInput').getBoundingClientRect().bottom,history:document.getElementById('messages').getBoundingClientRect().height}));
  assert.equal(bounds.nav,false);assert.ok(bounds.input<=360&&bounds.history>=100,JSON.stringify(bounds));
  await page.screenshot({path:'.artifacts/mobile-notifications/keyboard.png'});
  await page.setViewportSize({width:390,height:844});assert.ok(await page.locator('.nav-wrap').isVisible());
  assert.deepEqual(errors,[]);
  console.log('PASS mobile notification experience: Android status/blocked test/settings/refresh, actionable safe links, all settings categories at 3 sizes, keyboard reading space');
}finally{await closeBrowserFixture(browser,server);}
