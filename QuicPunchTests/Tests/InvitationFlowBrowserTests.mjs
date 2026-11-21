import assert from 'node:assert/strict';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer(['--interactive','--empty']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:320,height:350}});
  const invitation = page.getByRole('dialog',{name:'Usar una invitación recibida'});
  for (const [path, start, picker, add, search] of [
    ['chat.html','chatFirstContactBtn',null,'chatFirstContactBtn',null],
    ['call.html','voiceStartCallBtn','voiceCallPicker','voiceAddContactBtn','voiceCallPickerSearch']
  ]) {
    await page.goto(url + path);
    await page.waitForFunction(() => QP.state.status);
    await page.locator('#' + start).click();
    if (picker) {
      await page.waitForFunction(id => document.activeElement?.id === id, add);
      assert.equal(await page.locator('#' + search).isVisible(), false);
      await page.locator('#' + add).click();
    }
    await invitation.waitFor();
    if (picker) assert.equal(await page.locator('#' + picker).isVisible(), false);
    assert.equal(await page.locator('#invitationShare').isVisible(), false);
    assert.equal(new URL(page.url()).pathname, '/' + path);
    await invitation.locator('#connectTokenBtn').click();
    assert.match(await invitation.locator('#connectFeedback').innerText(), /Pega una invitación/);
    await page.keyboard.press('Escape');
    await page.waitForFunction(id => document.activeElement?.id === id, add);
    if (picker) assert.equal(await page.locator('#' + picker).isVisible(), true);
  }
  await page.route('**/api/connect-token', route => route.fulfill({json:{success:true,connecting:true}}));
  await page.locator('#voiceAddContactBtn').click();
  await invitation.locator('#tokenInput').fill('identidad-ficticia');
  await invitation.locator('#connectTokenBtn').click();
  await invitation.locator('#returnFromInvitation').click();
  await page.locator('#voiceCallPicker[open]').waitFor();
  assert.match(await page.url(), /call\.html$/);
  await page.goto(url + 'vpn.html');
  const layout = await page.locator('#lanMaskSummary').evaluate(mask => {
    const range = new Range();
    range.selectNodeContents(mask);
    return {lines:range.getClientRects().length, overflow:document.documentElement.scrollWidth > innerWidth};
  });
  assert.deepEqual(layout,{lines:1,overflow:false});
  console.log('PASS invitation modes, empty selectors, validation, return focus and narrow VPN layout');
} finally {
  await closeBrowserFixture(browser, server);
}
