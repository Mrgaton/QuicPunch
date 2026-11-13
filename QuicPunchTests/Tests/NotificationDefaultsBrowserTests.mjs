import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import {mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';

const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  mkdirSync('.artifacts/notification-defaults', {recursive:true});
  const page = await browser.newPage({viewport:{width:1280,height:1000}}), errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const hash = '01'.repeat(32);
  let settings = {startWithSystem:true,runInBackground:true,notifications:{
    enabled:true,desktop:true,inApp:true,sound:true,preview:true,whileFocused:true,
    doNotDisturb:false,quietHours:false,quietStart:1320,quietEnd:480,volume:60,tone:'soft',features:{},contacts:{}
  }};
  let reject = false, saves = 0;
  await page.route('**/api/app/preferences', async route => {
    if (route.request().method() === 'POST') {
      saves++;
      if (reject) { reject = false; return route.fulfill({status:503,json:{error:'Prueba de reintento'}}); }
      settings = route.request().postDataJSON();
    }
    return route.fulfill({json:{success:true,settings,startupSupported:true,platformHelp:'Prueba aislada'}});
  });
  await page.goto(url+'settings.html?section=notifications');
  await page.waitForFunction(()=>!document.getElementById('appBehaviorFields').disabled);
  const applied = () => page.locator('#appBehaviorStatus').filter({hasText:'Todos los cambios aplicados'}).waitFor();
  const contact = page.locator(`[data-contact="${hash}"]`);
  const setting = field => page.locator(`#noticeContact-${hash}-${field}`);
  assert.equal(await contact.locator('.notice-contact-badge').textContent(), 'Predeterminados');
  await page.locator('#noticeContactDefault-enabled').uncheck(); await applied();
  assert.equal(settings.notifications.contactDefaults.enabled, false);
  assert.deepEqual(settings.notifications.contacts, {}, 'Changing defaults must not materialize contact overrides');
  assert.ok((await page.locator('.notice-contact-copy small').allTextContents()).every(text=>text==='Sin avisos'));
  await contact.locator('summary').click();
  await setting('enabled').selectOption('true'); await applied();
  assert.deepEqual(settings.notifications.contacts[hash], {enabled:true}, 'Explicit allow must survive a disabled default');
  assert.equal(await contact.locator('.notice-contact-copy small').textContent(), 'Ventanas y sonido');
  assert.equal(await setting('sound').inputValue(), 'inherit');
  await page.locator('#noticeContactDefault-sound').uncheck(); await applied();
  assert.equal(await contact.getAttribute('open'), '');
  assert.equal(await contact.locator('.notice-contact-copy small').textContent(), 'Solo ventanas');
  await setting('sound').selectOption('true'); await applied();
  await page.reload(); await page.waitForFunction(()=>!document.getElementById('appBehaviorFields').disabled);
  await contact.locator('summary').click();
  assert.equal(await setting('enabled').inputValue(), 'true');
  assert.equal(await setting('sound').inputValue(), 'true');
  assert.equal(await setting('popup').inputValue(), 'inherit');
  assert.equal(await page.locator('#noticeContactDefault-enabled').isChecked(), false);
  const savedBeforeFilter = saves;
  await page.locator('#noticeOnlyCustom').check();
  assert.equal(await page.locator('.notice-contact').count(), 1);
  await page.locator('#noticeContactSearch').fill('ninguna coincidencia');
  assert.match(await page.locator('#noticeContactRules').innerText(), /No hay contactos/);
  await page.locator('#noticeContactSearch').fill('Ana');
  assert.equal(await page.locator('.notice-contact').count(), 1);
  assert.equal(saves, savedBeforeFilter, 'Search and filters must not save preferences');
  await contact.locator('summary').click();
  reject = true; await setting('popup').selectOption('false');
  await page.locator('#retryAppBehavior').waitFor({state:'visible'});
  assert.equal(settings.notifications.contacts[hash].popup, undefined);
  assert.equal(await setting('popup').inputValue(), 'false', 'Keep the pending edit after failure');
  await page.locator('#retryAppBehavior').click(); await applied();
  assert.equal(settings.notifications.contacts[hash].popup, false);
  await setting('popup').selectOption('inherit'); await applied();
  assert.equal(settings.notifications.contacts[hash].popup, undefined);
  await page.locator('#noticeContactSearch').fill('');
  await page.locator('#noticeOnlyCustom').uncheck();
  await page.locator('#noticeDnd').check(); await applied();
  assert.match(await page.locator('#noticeGlobalWarning').innerText(), /No molestar/);
  await page.locator('#noticeDnd').uncheck(); await applied();
  for (const width of [1920,1280,390,320]) {
    await page.setViewportSize({width,height:1000});
    await page.locator('.notice-contacts-card').scrollIntoViewIfNeeded();
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth), 'Page overflows at '+width);
    for (const select of await contact.locator('select').all()) {
      const rect = await select.boundingBox();
      assert.ok(rect.width>120 && rect.x>=0 && rect.x+rect.width<=width, 'Contact selector is clipped at '+width);
    }
    await page.locator('.notice-contacts-card').screenshot({path:`.artifacts/notification-defaults/contacts-${width}.png`});
  }
  await contact.getByRole('button', {name:'Usar todos los predeterminados'}).click(); await applied();
  assert.equal(settings.notifications.contacts[hash], undefined);
  assert.equal(await contact.locator('.notice-contact-copy small').textContent(), 'Sin avisos');
  await setting('enabled').selectOption('true'); await applied();
  await page.locator('#noticeOnlyCustom').check();
  await setting('enabled').focus();
  await setting('enabled').selectOption('inherit'); await applied();
  assert.equal(await page.locator('.notice-contact').count(), 0);
  assert.match(await page.locator('#noticeContactRules').innerText(), /Todos tus contactos usan los predeterminados/);
  assert.equal(await page.locator('#noticeContactSearch').evaluate(node=>node===document.activeElement), true);
  assert.deepEqual(errors, []);
  console.log('PASS notification defaults: one-contact allow list, per-field inheritance, explicit true, persistence, reset, search/filter, save retry, global limits and 320–1920px layouts');
} finally { await closeBrowserFixture(browser, server); }
