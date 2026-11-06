import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Fixture host only: never registers startup or emits real OS notifications.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  let settings = { startWithSystem: false, runInBackground: false, notifications: { enabled:true,desktop:true,inApp:true,sound:true,preview:true,whileFocused:true,doNotDisturb:false,doNotDisturbUntil:null,quietHours:false,quietStart:1320,quietEnd:480,volume:60,tone:'soft',features:{},contacts:{} } };
  let reject = false, saves = 0, slow = false, active = 0, maxActive = 0;
  await page.route('**/api/app/preferences', async route => {
    if (route.request().method() === 'POST') {
      saves++;
      active++; maxActive=Math.max(maxActive,active);
      const next=route.request().postDataJSON();
      if(slow) await new Promise(resolve=>setTimeout(resolve,180));
      active--;
      if (reject) { reject = false; await route.fulfill({status:500,json:{error:'Fallo simulado'}}); return; }
      settings = next;
    }
    await route.fulfill({ json: {success:true,settings,startupSupported:true,platformHelp:'Host de prueba'} });
  });
  let notificationTests=0,ringtoneRequests=[];
  await page.route('**/api/notifications/test',route=>{
    notificationTests++;
    return route.fulfill({json:settings.notifications.doNotDisturb
      ?{success:true,sent:false,reason:'do_not_disturb',message:'Estás en modo No molestar. Los avisos y las llamadas están silenciados.'}
      :{success:true,sent:true,message:'Prueba enviada.'}});
  });
  const wave=Buffer.alloc(44+22050*2*2);wave.write('RIFF');wave.writeUInt32LE(wave.length-8,4);wave.write('WAVEfmt ',8);wave.writeUInt32LE(16,16);wave.writeUInt16LE(1,20);wave.writeUInt16LE(1,22);wave.writeUInt32LE(22050,24);wave.writeUInt32LE(44100,28);wave.writeUInt16LE(2,32);wave.writeUInt16LE(16,34);wave.write('data',36);wave.writeUInt32LE(wave.length-44,40);
  await page.route('**/api/notifications/ringtone?**',route=>{ringtoneRequests.push(new URL(route.request().url()).searchParams.get('volume'));return route.fulfill({contentType:'audio/wav',body:wave});});
  await page.goto(url + 'settings.html?section=startup');
  await page.waitForFunction(()=>!document.getElementById('appBehaviorFields').disabled);
  assert.equal(await page.locator('#saveAppBehavior').count(),0,'Manual save button remains');
  const applied=()=>page.locator('#appBehaviorStatus').filter({hasText:'Todos los cambios aplicados'}).waitFor();
  await page.locator('#startWithSystem').check(); await page.locator('#runInBackground').check();
  await page.locator('#settings-tab-notifications').click();
  await page.getByLabel('Sonido: Mensajes', {exact:true}).uncheck();
  await page.getByLabel('Sonido: Ana · Portátil', {exact:true}).uncheck();
  await applied();
  assert.equal(settings.startWithSystem, true); assert.equal(settings.runInBackground, true);
  assert.equal(settings.notifications.features.messages.sound, false);
  assert.equal(settings.notifications.contacts['01'.repeat(32)].sound, false);
  // Exercise the same controls after a successful save: listeners must keep current rule references.
  await page.getByLabel('Sonido: Mensajes', {exact:true}).check();
  await page.locator('[data-notice-snooze="1"]').click();
  await applied();
  assert.equal(settings.notifications.features.messages.sound, true);
  assert.equal(settings.notifications.doNotDisturb, true);
  assert.ok(new Date(settings.notifications.doNotDisturbUntil).getTime() > Date.now() + 3500000);
  reject = true; await page.locator('#noticePreview').uncheck();
  await page.locator('#appBehaviorStatus').filter({hasText:'Fallo simulado'}).waitFor();
  assert.equal(await page.locator('#noticePreview').isChecked(), false);
  assert.equal(settings.notifications.preview,true,'Failed save was silently accepted');
  await page.locator('#retryAppBehavior').click();await applied();
  slow=true;
  await page.locator('#noticeSound').uncheck();
  await page.locator('#noticeDesktop').uncheck();
  await page.locator('#noticeWhileFocused').uncheck();
  assert.equal(await page.locator('#appBehaviorFields').isDisabled(),false,'Saving blocks other controls');
  await applied();slow=false;
  assert.equal(settings.notifications.sound,false);assert.equal(settings.notifications.desktop,false);assert.equal(settings.notifications.whileFocused,false);
  assert.equal(maxActive,1,'Overlapping saves could overwrite newer settings');
  await page.locator('#noticeQuietHours').check();await page.locator('#noticeQuietStart').fill('21:15');
  await page.locator('#noticeQuietStart').press('Tab');await applied();
  assert.equal(settings.notifications.quietStart,1275);
  await page.locator('#noticeVolume').evaluate(input=>{for(let value=10;value<=70;value+=10){input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));}});
  await applied();assert.equal(settings.notifications.volume,70);
  await page.locator('#noticeTone').selectOption('bell');await applied();assert.equal(settings.notifications.tone,'bell');
  await page.reload(); await page.locator('#noticePreview:not(:checked)').waitFor();
  assert.equal(await page.locator('#runInBackground').isChecked(), true);
  await page.locator('#testAppNotification').click();
  await page.locator('#notificationTestDialog').waitFor({state:'visible'});
  assert.match(await page.locator('#notificationTestTitle').textContent(),/No molestar/);
  assert.equal(notificationTests,1);assert.equal(settings.notifications.doNotDisturb,true);
  await page.screenshot({path:'.artifacts/notification-test-dnd.png'});
  await page.locator('#notificationTestClose').click();
  assert.equal(await page.locator('#testAppNotification').evaluate(node=>node===document.activeElement),true);
  await page.locator('#testCallRingtone').click();
  await page.waitForFunction(()=>!document.getElementById('notificationRingtonePreview').paused);
  assert.equal(await page.locator('#testCallRingtone').getAttribute('aria-pressed'),'true');
  assert.equal(ringtoneRequests.at(-1),'70');
  assert.equal(settings.notifications.doNotDisturb,true,'Ringtone preview changed DND');
  await page.locator('#testCallRingtone').click();
  assert.equal(await page.locator('#notificationRingtonePreview').evaluate(node=>node.paused),true);
  assert.equal(await page.locator('#testCallRingtone').getAttribute('aria-pressed'),'false');
  await page.locator('#noticeVolume').evaluate(input=>{input.value='0';input.dispatchEvent(new Event('input',{bubbles:true}));});
  const beforeSilentPreview=ringtoneRequests.length;
  await page.locator('#testCallRingtone').click();
  await page.locator('#notificationTestDialog').waitFor({state:'visible'});
  assert.match(await page.locator('#notificationTestMessage').textContent(),/volumen está a cero/);
  assert.equal(ringtoneRequests.length,beforeSilentPreview,'Silent preview should explain the zero volume instead of pretending to play');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#testCallRingtone').evaluate(node=>node===document.activeElement),true);
  await page.locator('#noticeVolume').evaluate(input=>{input.value='70';input.dispatchEvent(new Event('input',{bubbles:true}));});
  await applied();
  await page.locator('#noticeDnd').uncheck();await applied();
  await page.locator('#noticeEnabled').check();await page.locator('#noticeWhileFocused').check();
  await page.locator('#noticeQuietHours').uncheck();await applied();
  await page.locator('#testAppNotification').click();
  await page.locator('#appBehaviorStatus').filter({hasText:'Prueba enviada'}).waitFor();
  assert.equal(await page.locator('#notificationTestDialog').isVisible(),false);
  for(const theme of ['dark']) {
    await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
    for(const width of [2048,1280,390,320]) {
      await page.setViewportSize({width,height:900});
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Settings overflow');
      const file=`.artifacts/behavior-${theme}-${width}.png`;
      await page.locator('#appBehaviorForm').screenshot({path:file});
    }
  }
  assert.equal(errors.length, 0, errors.join('\n'));assert.ok(saves>=10);
  console.log('PASS automatic settings: DND test popup, independent ringtone preview/stop, immediate changes, serialized rapid edits, rules, snooze, slider, retry, reload and responsive design');
} finally { await closeBrowserFixture(browser, server); }
