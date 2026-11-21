import {strict as assert} from 'node:assert';
import {closeBrowserFixture, launchBrowser, startPreviewServer} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer();
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const profile = {required: true, saved: false, displayName: '', deviceName: 'Mi equipo',
    torName: 'tor-f82eab', startupSupported: true, startWithSystem: false, runInBackground: false};
  const page = await browser.newPage({viewport: {width: 1180, height: 820}});
  let submitted;
  await page.route('**/api/onboarding', async route => {
    if (route.request().method() === 'POST') {
      submitted = route.request().postDataJSON();
      await route.fulfill({json: {required: false, saved: true}});
    } else {
      await route.fulfill({json: profile});
    }
  });
  await page.goto(url + 'onboarding.html');
  const wan = page.getByRole('radio', {name: 'WAN', exact: true});
  const tor = page.getByRole('radio', {name: 'Solo Tor', exact: true});
  const next = page.getByRole('button', {name: 'Continuar'});
  await page.waitForFunction(() => !document.querySelector('.onboarding-modes').disabled);
  assert.equal(await next.isDisabled(), true);
  assert.equal(await wan.isChecked() || await tor.isChecked(), false);
  await page.keyboard.press('Tab');
  assert.equal(await wan.evaluate(input => document.activeElement === input), true);
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowRight');
  assert.equal(await tor.isChecked(), true);
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  const heading = page.getByRole('heading', {name: 'Configura tu perfil'});
  assert.equal(await heading.evaluate(node => document.activeElement === node), true);
  assert.equal(await page.locator('#onboarding-display-preview').textContent(), 'tor-f82eab');
  const animation = await heading.evaluate(node => getComputedStyle(node.parentElement).animationName);
  assert.equal(animation, 'onboarding-enter');
  await page.getByLabel('Nombre de usuario').fill('Ada');
  await page.getByRole('button', {name: 'Volver'}).click();
  await wan.check();
  await next.click();
  assert.equal(await page.locator('#onboarding-display-preview').textContent(), 'Ada');
  await next.click();
  await page.getByRole('switch', {name: /Continuar en segundo plano/}).check();
  assert.equal(await page.getByRole('switch', {name: /Iniciar con el sistema/}).isChecked(), false);
  assert.equal(submitted, undefined);
  await page.getByRole('button', {name: 'Terminar y conectar'}).click();
  await page.waitForURL(url);
  assert.deepEqual(submitted, {mode: 'wan', displayName: 'Ada', startWithSystem: false, runInBackground: true});

  const phone = await browser.newPage({viewport: {width: 360, height: 690}, reducedMotion: 'reduce', isMobile: true, hasTouch: true});
  await phone.route('**/api/onboarding', route => route.fulfill({json: {...profile, startupSupported: false}}));
  await phone.goto(url + 'onboarding.html');
  await phone.getByRole('radio', {name: 'Solo Tor', exact: true}).check();
  assert.equal(await phone.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await phone.getByRole('button', {name: 'Continuar'}).click();
  assert.equal(await phone.locator('#onboarding-profile').evaluate(node => getComputedStyle(node).animationName), 'none');
  await phone.getByRole('button', {name: 'Continuar'}).click();
  assert.equal(await phone.getByRole('switch', {name: /Iniciar con el sistema/}).isDisabled(), true);
  assert.equal(await phone.getByRole('switch', {name: /Continuar en segundo plano/}).isEnabled(), true);
  console.log('PASS onboarding selection, keyboard, profile, system options and reduced motion');
} finally {
  await closeBrowserFixture(browser, server);
}
