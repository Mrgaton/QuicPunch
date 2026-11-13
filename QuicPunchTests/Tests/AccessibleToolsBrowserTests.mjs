import assert from 'node:assert/strict';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer();
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1280,height:900}});
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(url + 'settings.html?section=contacts');
  await page.locator('#addSavedPeerBtn').waitFor();
  await page.locator('#addSavedPeerBtn').click();
  const editor = page.getByRole('dialog', {name:'Añadir contacto',exact:true});
  for (const [name, id] of [['Nombre','savedName'],['Transporte','savedNetworkType'],
    ['Huella del certificado (Base64)','savedCertHash'],['Direcciones IP','savedAddresses'],
    ['Dirección Onion','savedOnion'],['Puertos','savedPorts']]) {
    assert.equal(await editor.getByLabel(name, {exact:true}).getAttribute('id'), id,
      `The contact field ${name} must have its visible label as its accessible name`);
  }
  await editor.locator('label[for=savedPorts]').click();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'savedPorts');
  assert.match(await editor.locator('#savedPortsHelp').innerText(), /5000-5010/);
  await page.locator('#saveSavedPeerBtn').click();
  assert.ok(await editor.getByRole('alert').isVisible(), 'Validation errors must be announced');
  // A failed save keeps the editable draft and offers a working retry.
  let rejectSave = true, saves = 0;
  await page.route('**/api/saved-peer-update', route => {
    saves++;
    return route.fulfill(rejectSave
      ? {status:503,json:{error:'No se pudo guardar. Vuelve a intentarlo.'}}
      : {json:{success:true}});
  });
  const certificate = Buffer.alloc(32, 1).toString('base64');
  await editor.getByLabel('Nombre', {exact:true}).fill('Mi contacto');
  await editor.getByLabel('Huella del certificado (Base64)', {exact:true}).fill(certificate);
  await page.locator('#saveSavedPeerBtn').click();
  await editor.getByRole('alert').filter({hasText:'No se pudo guardar'}).waitFor();
  await page.waitForFunction(() => !document.getElementById('saveSavedPeerBtn').disabled);
  assert.equal(await editor.getByLabel('Nombre', {exact:true}).inputValue(), 'Mi contacto');
  assert.equal(await editor.getByLabel('Huella del certificado (Base64)', {exact:true}).inputValue(), certificate);
  rejectSave = false;
  await page.locator('#saveSavedPeerBtn').focus();
  await page.keyboard.press('Enter');
  await editor.waitFor({state:'hidden'});
  assert.equal(saves, 2, 'One failed attempt and one explicit retry');
  await page.waitForFunction(() => document.activeElement.id === 'addSavedPeerBtn');

  // Native fragment navigation must not be converted into a SPA page navigation.
  await page.locator('.skip-link').focus();
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.activeElement.id === 'spaContainer');
  assert.equal(new URL(page.url()).hash, '#spaContainer');
  assert.equal(await page.locator('body').getAttribute('data-page'), 'settings');

  await page.setViewportSize({width:390,height:844});
  await page.evaluate(() => { window.toolsSessionMarker = 'same-document'; });
  const tools = page.getByRole('dialog', {name:'Herramientas',exact:true});
  await page.locator('#openMobileTools').focus();
  await page.keyboard.press('Enter');
  assert.ok(await tools.isVisible());
  assert.equal(await page.evaluate(() => document.activeElement.id), 'closeMobileTools');
  assert.equal(await tools.locator('[aria-current=page]').getAttribute('data-page'), 'settings');
  await page.keyboard.press('Escape');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'openMobileTools');

  // Both portrait and short landscape dialogs keep every destination reachable.
  for (const viewport of [{width:320,height:568},{width:667,height:375}]) {
    await page.setViewportSize(viewport);
    await page.locator('#openMobileTools').click();
    assert.equal(await tools.getByRole('navigation', {name:'Herramientas de QuicNet'}).count(), 1);
    for (const link of await tools.getByRole('link').all()) {
      await link.scrollIntoViewIfNeeded();
      const geometry = await link.evaluate(element => {
        const bounds = element.getBoundingClientRect();
        return {height:bounds.height, width:bounds.width, top:bounds.top, bottom:bounds.bottom,
          reachable:element.contains(document.elementFromPoint(bounds.x + bounds.width/2, bounds.y + bounds.height/2))};
      });
      assert.ok(geometry.height >= 44 && geometry.width >= 44 && geometry.reachable,
        `Tool must be touch sized and reachable: ${JSON.stringify(geometry)}`);
    }
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.keyboard.press('Escape');
  }
  await page.locator('#openMobileTools').click();
  const disk = tools.getByRole('link', {name:/Disco virtual/});
  await disk.focus();
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.body.dataset.page === 'virtual-disk');
  assert.equal(await page.evaluate(() => window.toolsSessionMarker), 'same-document', 'Tool navigation preserves the running SPA');
  assert.equal(await tools.isVisible(), false);
  assert.ok(await page.locator('#view-virtual-disk h1').evaluate(element => element === document.activeElement),
    'Keyboard navigation moves focus into the destination');
  await page.locator('#openMobileTools').click();
  assert.equal(await tools.locator('[aria-current=page]').getAttribute('data-page'), 'virtual-disk');
  await page.keyboard.press('Escape');
  assert.deepEqual(errors, []);
  console.log('PASS accessible contact labels/errors and save retry, skip link, mobile tools keyboard/focus, portrait/landscape and SPA continuity');
} finally {
  await closeBrowserFixture(browser, server);
}
