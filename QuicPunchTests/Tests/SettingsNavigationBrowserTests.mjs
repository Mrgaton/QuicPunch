import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  let customName = 'Ana';
  await page.route('**/api/profile', async route => {
    if (route.request().method() === 'POST') customName = route.request().postDataJSON().displayName;
    await route.fulfill({json:{customName,deviceName:'Mi portátil',displayName:customName||'Mi portátil'}});
  });
  let settings={startWithSystem:true,runInBackground:true,notifications:{enabled:true,desktop:true,inApp:true,sound:true,preview:true,whileFocused:true,doNotDisturb:false,doNotDisturbUntil:null,quietHours:false,quietStart:1320,quietEnd:480,volume:60,tone:'soft',features:{},contacts:{}}};
  await page.route('**/api/app/preferences', async route => {
    if (route.request().method() === 'POST') settings=route.request().postDataJSON();
    await route.fulfill({json:{success:true,settings,startupSupported:true,platformHelp:'Al cerrar la ventana, QuicNet continúa en segundo plano.'}});
  });
  await page.goto(url+'settings.html');
  await page.locator('#settings-profile').waitFor();
  assert.equal(await page.locator('[data-settings-panel]:visible').count(),1);
  assert.equal(await page.locator('#appBehaviorForm').isVisible(),false);
  await page.locator('#profileDisplayName').fill('Mi nuevo nombre');
  await page.locator('#settings-tab-notifications').click();
  await page.locator('#settings-tab-profile').click();
  assert.equal(await page.locator('#profileDisplayName').inputValue(),'Mi nuevo nombre','Changing categories discarded a draft');
  await page.locator('#profileSave').click();
  await page.locator('#profileSaveStatus').filter({hasText:'Nombre guardado'}).waitFor();
  assert.equal(customName,'Mi nuevo nombre');
  const sections=await page.locator('[data-settings-section]').evaluateAll(tabs=>tabs.map(tab=>tab.dataset.settingsSection));
  assert.deepEqual(sections,['profile','notifications','startup','updates','connection','geolocation','contacts','diagnostics']);
  for(const section of sections) {
    await page.locator('#settings-tab-'+section).click();
    assert.equal(await page.locator('[data-settings-panel]:visible').getAttribute('id'),'settings-'+section);
    assert.equal(await page.locator('#settings-tab-'+section).getAttribute('aria-selected'),'true');
  }
  await page.goBack();await page.locator('#settings-contacts').waitFor();
  await page.goForward();await page.locator('#settings-diagnostics').waitFor();
  await page.reload();await page.locator('#settings-diagnostics').waitFor();
  await page.locator('#settings-tab-profile').focus();await page.keyboard.press('ArrowDown');
  await page.locator('#settings-notifications').waitFor();
  assert.equal(await page.locator('#settings-tab-notifications').evaluate(tab=>tab===document.activeElement),true);
  await page.keyboard.press('End');await page.locator('#settings-diagnostics').waitFor();
  await page.keyboard.press('Home');await page.locator('#settings-profile').waitFor();
  for(const theme of ['dark']) {
    await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
    for(const width of [2048,1280,844,390,320]) {
      await page.setViewportSize({width,height:1000});
      for(const section of sections) {
        if(width>1000) await page.locator('#settings-tab-'+section).click();
        else await page.locator('#settingsCategory').selectOption(section);
        await page.locator('#settings-'+section).waitFor();
        assert.equal(await page.locator('[data-settings-panel]:visible').count(),1);
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${section} overflows at ${width}`);
        if(['profile','notifications'].includes(section)&&[1280,390].includes(width))
          await page.screenshot({path:`.artifacts/settings-${section}-${theme}-${width}.png`});
      }
    }
  }
  await page.evaluate(()=>QP.navigate('dashboard'));
  await page.evaluate(()=>QP.navigate('settings'));
  await page.locator('#settings-profile').waitFor();
  await page.goto(url+'settings.html?section=unknown');await page.locator('#settings-profile').waitFor();
  assert.deepEqual(errors,[]);
  console.log('PASS settings categories: profile default, all sections, keyboard, history, reload, safe fallback and 320–2048px dark layouts');
} finally {await closeBrowserFixture(browser, server);}
