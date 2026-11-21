import assert from 'node:assert/strict';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer(['--interactive']);
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
  const tools = page.getByRole('dialog', {name:'Más',exact:true});
  const more = page.getByRole('button', {name:'Más',exact:true});
  const navIconSize = await page.locator('.nav [data-page=dashboard] .icon').evaluate(icon => {
    const {width, height} = icon.getBoundingClientRect();
    return {width, height};
  });
  assert.ok(await more.locator('.icon').evaluate((icon, size) => {
    const {width, height} = icon.getBoundingClientRect();
    const glyph = icon.getBBox();
    return width === size.width && height === size.height && glyph.width >= 20 && glyph.height >= 5;
  }, navIconSize), 'The ellipsis fills its navigation icon at the same scale as its neighbors');
  await more.focus();
  await page.keyboard.press('Enter');
  assert.ok(await tools.isVisible());
  assert.equal(await page.evaluate(() => document.activeElement.id), 'closeMobileTools');
  assert.equal(await tools.locator('[aria-current=page]').count(), 0);
  assert.deepEqual(await tools.locator('a strong, #mobilePresenceButton strong').allTextContents(),
    ['Ver juntos','Disco virtual','Red privada','Prueba de velocidad','Tu estado']);
  const toolIcons = await tools.locator('.mobile-tools-list > :is(a,button)').evaluateAll(rows => rows.map(row => {
    const icon = row.querySelector('svg[aria-hidden="true"]');
    if (!icon) return null;
    const {width, height} = icon.getBoundingClientRect();
    const glyph = icon.getBBox();
    return {width, height, drawn: glyph.width > 0 && glyph.height > 0, shape: icon.innerHTML};
  }));
  assert.ok(toolIcons.every(icon => icon?.drawn && icon.width === navIconSize.width && icon.height === navIconSize.height),
    'Each tool displays a decorative icon sized like the bottom navigation icons');
  assert.equal(new Set(toolIcons.map(icon => icon.shape)).size, toolIcons.length, 'Each tool has a distinct icon');
  assert.equal(await page.evaluate(() => {
    document.querySelector('.nav a[data-page="dashboard"]').focus();
    return document.activeElement.id;
  }), 'closeMobileTools', 'Background navigation is inert while the panel is open');
  await tools.getByRole('button', {name:/Tu estado/}).click();
  await page.locator('#userPresenceDialog[open]').waitFor();
  await page.locator('#presenceDnd').click();
  await page.waitForFunction(() => document.getElementById('mobilePresenceText').textContent === 'No molestar');
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => document.activeElement.id === 'navMore');

  // Both portrait and short landscape dialogs keep every destination reachable.
  for (const viewport of [{width:320,height:568},{width:667,height:375},{width:320,height:300}]) {
    await page.setViewportSize(viewport);
    await more.click();
    assert.equal(await tools.getByRole('navigation', {name:'Herramientas de QuicNet'}).count(), 1);
    const panel = await tools.boundingBox();
    if (viewport.height > viewport.width) assert.ok(Math.abs(panel.y + panel.height - viewport.height) <= 1, 'Portrait panel is anchored at the bottom');
    else assert.ok(Math.abs(panel.y * 2 + panel.height - viewport.height) <= 2, 'Landscape dialog is centered');
    for (const link of await tools.locator('a, #mobilePresenceButton').all()) {
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
  await page.setViewportSize({width:390,height:844});
  const previousState = () => page.evaluate(() => ({url:location.href,length:history.length,scroll:scrollY}));
  await page.evaluate(() => window.scrollTo(0, 160));
  const beforeBack = await previousState();
  await more.click();
  assert.equal(await page.evaluate(() => QP.dismissMobileTools()), true, 'Android Back hook consumes an open panel');
  assert.equal(await page.evaluate(() => QP.dismissMobileTools()), false, 'The next Back falls through to navigation');
  assert.deepEqual(await previousState(), beforeBack, 'Opening and dismissing the panel preserves the current URL and scroll');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'navMore');

  await more.click();
  await page.setViewportSize({width:844,height:390});
  const desktopPanel = await tools.boundingBox();
  assert.ok(desktopPanel.x>=0 && desktopPanel.x+desktopPanel.width<=844 && desktopPanel.y+desktopPanel.height<=390);
  assert.equal(await more.isVisible(),false);
  await tools.getByRole('button',{name:'Cerrar',exact:true}).click();
  assert.ok(await page.locator('.nav a[data-page=settings]').evaluate(node=>node===document.activeElement),
    'After a rotation, focus returns to the active sidebar link');
  await page.locator('.nav a[data-page=virtual-disk]').click();
  assert.equal(new URL(page.url()).pathname,'/virtual-disk.html');
  await page.setViewportSize({width:390,height:844});
  assert.equal(await more.getAttribute('aria-description'),'Pantalla actual: Disco virtual');

  await more.click();
  const vpn = tools.getByRole('link', {name:/Red privada/});
  await vpn.focus();
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => document.body.dataset.page === 'vpn');
  assert.equal(await page.evaluate(() => window.toolsSessionMarker), 'same-document', 'Tool navigation preserves the running SPA');
  assert.equal(await tools.isVisible(), false);
  assert.ok(await page.locator('#view-vpn h1').evaluate(element => element === document.activeElement),
    'Keyboard navigation moves focus into the destination');
  await more.click();
  assert.equal(await tools.locator('[aria-current=page]').getAttribute('data-page'), 'vpn');
  const before = await page.evaluate(() => history.length);
  await vpn.click();
  assert.equal(await page.evaluate(() => history.length), before, 'Current tool does not navigate again');
  await page.goBack();
  assert.equal(await more.getAttribute('aria-description'),'Pantalla actual: Disco virtual');
  await page.goForward();
  assert.equal(await more.getAttribute('aria-description'),'Pantalla actual: Red privada');

  await page.goto(url + 'virtual-disk.html?share=preview-documents');
  await page.locator('#diskShares [data-selected=true]').waitFor();
  await more.click();
  assert.equal(await tools.locator('[aria-current=page]').getAttribute('data-page'),'virtual-disk');
  const deepLink = await previousState();
  await tools.getByRole('link',{name:/Disco virtual/}).click();
  assert.deepEqual(await previousState(),deepLink,'Selecting the current tool preserves a deep link');

  await more.click();
  await tools.getByRole('link', {name:/Ver juntos/}).click();
  assert.equal(new URL(page.url()).pathname, '/watch.html');
  assert.equal(await more.getAttribute('aria-description'), 'Pantalla actual: Ver juntos');
  await more.click();
  assert.equal(await tools.locator('[aria-current=page]').getAttribute('data-page'), 'watch');
  await page.keyboard.press('Escape');
  await page.goBack();
  assert.equal(await more.getAttribute('aria-description'), 'Pantalla actual: Disco virtual');

  const restricted = await browser.newPage({viewport:{width:390,height:844}});
  await restricted.addInitScript(() => {
    const capabilities = {platform:'android',capabilities:{vpn:{available:false},speedtest:{available:true}}};
    Object.defineProperty(window,'__qp_capabilities', {get:()=>capabilities,set() {}});
  });
  await restricted.goto(url);
  await restricted.locator('#navMore').click();
  assert.equal(await restricted.locator('#mobileToolsDialog a[data-page=vpn]').isVisible(),false);
  assert.equal(await restricted.locator('#mobileToolsDialog a[data-page=speedtest]').isVisible(),true);
  await restricted.keyboard.press('Escape');
  await restricted.setViewportSize({width:844,height:390});
  assert.equal(await restricted.locator('.nav a[data-page=vpn]').isVisible(),false);
  assert.equal(await restricted.locator('.nav a[data-page=speedtest]').isVisible(),true);
  await restricted.close();
  assert.deepEqual(errors, []);
  console.log('PASS accessible contact labels/errors and save retry, skip link, mobile tools keyboard/focus, portrait/landscape and SPA continuity');
} finally {
  await closeBrowserFixture(browser, server);
}
