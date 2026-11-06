import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Exercise the actual chat picker against fixture contacts; no native app or real peers.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1600, height:1000}}), errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(url + 'chat.html?peer=00000000-0000-0000-0000-000000000001');
  await page.waitForFunction(() => !document.getElementById('emojiBtn').disabled);
  const button = page.locator('#emojiBtn'), picker = page.locator('#emojiPopover'), search = page.locator('#emojiSearch');
  const input = page.locator('#messageInput');
  await input.fill('Hola mundo');
  await input.evaluate(node => node.setSelectionRange(5, 10));
  await button.click();
  assert.equal(await search.evaluate(node => node === document.activeElement), true);
  assert.equal(await button.getAttribute('aria-expanded'), 'true');
  assert.equal(await picker.locator('.emoji-item').count(), 43);
  await search.fill('corazon rojo');
  assert.deepEqual(await picker.locator('.emoji-item').allTextContents(), ['❤️']);
  await search.fill('café');
  assert.deepEqual(await picker.locator('.emoji-item').allTextContents(), ['☕']);
  await search.fill('coffee');
  assert.deepEqual(await picker.locator('.emoji-item').allTextContents(), ['☕']);
  await search.fill('inexistente');
  assert.equal(await picker.locator('.emoji-item').count(), 0);
  assert.equal(await page.locator('#emojiEmpty').isVisible(), true);
  await search.fill('🔥');
  assert.equal(await picker.isVisible(), true, 'Searching must keep the picker open');
  await picker.getByRole('button', {name:'Fuego', exact:true}).click();
  assert.equal(await input.inputValue(), 'Hola 🔥');
  assert.equal(await input.evaluate(node => node === document.activeElement), true);
  assert.equal(await picker.isVisible(), false);
  assert.equal(await button.getAttribute('aria-expanded'), 'false');
  await button.click();
  assert.equal(await search.inputValue(), '');
  await page.locator('#messages').click({position:{x:100,y:80}});
  assert.equal(await picker.isVisible(), false, 'A click on a non-focusable surface must dismiss');
  await button.click(); await input.focus();
  assert.equal(await picker.isVisible(), false, 'Moving keyboard focus outside must dismiss');
  await button.click(); await page.keyboard.press('Escape');
  assert.equal(await picker.isVisible(), false);
  assert.equal(await button.evaluate(node => node === document.activeElement), true);
  await button.click(); await button.click();
  assert.equal(await picker.isVisible(), false, 'The trigger must still toggle closed');
  await button.click(); await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  assert.equal(await picker.isVisible(), false, 'Losing window focus must dismiss');
  await button.click(); await page.evaluate(() => QP.navigate('dashboard'));
  await picker.waitFor({state:'hidden'});
  await page.evaluate(() => QP.navigate('chat'));
  for (const viewport of [{width:2560,height:1440}, {width:1280,height:720}, {width:720,height:450}]) {
    await page.setViewportSize(viewport); await button.click();
    const popup = await picker.boundingBox(), anchor = await button.boundingBox();
    assert.ok(popup.x >= 0 && popup.x + popup.width <= viewport.width && popup.y >= 0 && popup.y + popup.height <= anchor.y, 'Picker must fit above the trigger');
    assert.equal(await picker.evaluate(node => node.scrollWidth <= node.clientWidth), true, 'Picker must not scroll horizontally');
    await search.fill('heart');
    await page.screenshot({path:'.artifacts/emoji-picker-' + viewport.width + '.png'});
    await button.click();
  }
  assert.deepEqual(errors, []);
  console.log('PASS emoji picker: Spanish/English/accent/symbol search, empty results, caret insertion, outside pointer/focus, Escape, trigger, blur, navigation and viewport placement');
} finally { await closeBrowserFixture(browser, server); }
