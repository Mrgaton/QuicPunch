import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real home UI against the isolated fixture host; no real contacts or trust changes.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(url);
  const review = page.locator('[data-contact-filter="review"]');
  const badge = page.locator('#homeReviewPendingCount');
  await page.locator('#homeContactCount').filter({hasText:'3'}).waitFor();
  assert.equal(await page.locator('.connection-overview').count(), 0);
  assert.deepEqual(await page.locator('[data-contact-filter]').evaluateAll(nodes => nodes.map(n => n.dataset.contactFilter)), ['all','saved','review']);
  await review.click();
  assert.equal(await review.getAttribute('aria-pressed'), 'true');
  assert.equal(await badge.textContent(), '1 nuevo');
  assert.equal(await page.locator('#openUntrustedBtn').isVisible(), false, 'Por revisar must expose contact decisions without a second review step');
  await page.screenshot({path:'.artifacts/contact-review-direct-desktop.png',fullPage:true});
  await page.getByRole('button', {name:'Revisar contacto: Dispositivo nuevo',exact:true}).click();
  await page.getByRole('button', {name:'Ahora no',exact:true}).click();
  assert.equal(await badge.isVisible(), true, 'Deferring review must keep the pending indicator');
  // Failed saves must leave the contact pending and the decision retryable.
  let fail = true;
  await page.route('**/api/peer/trust', async route => {
    if (fail) { fail = false; await route.fulfill({status:500,json:{error:'Revisión no guardada'}}); }
    else await route.continue();
  });
  await page.getByRole('button', {name:'No confiar: Dispositivo nuevo',exact:true}).click();
  await page.waitForFunction(() => [...document.querySelectorAll('.toast')].some(node => node.textContent.includes('Revisión no guardada')));
  assert.equal(await page.getByRole('button', {name:'No confiar: Dispositivo nuevo',exact:true}).isEnabled(), true);
  assert.equal(await badge.isVisible(), true);
  await page.getByRole('button', {name:'No confiar: Dispositivo nuevo',exact:true}).click();
  await badge.waitFor({state:'hidden'});
  assert.equal(await page.locator('#homeContacts .contact-card').count(), 1);
  assert.match(await page.locator('#homeContacts').textContent(), /Sin confianza · revisado/);
  assert.equal(await review.evaluate(node => node.classList.contains('needs-attention')), false);
  assert.equal(await page.locator('#untrustedAlert').isVisible(), false);
  await page.reload(); await page.locator('#homeContactCount').filter({hasText:'3'}).waitFor();
  if(!await page.locator('.contact-options').evaluate(e=>e.open)) await page.locator('.contact-options > summary').click();
  await page.locator('#contactConnectionFilter').selectOption('all'); await review.click();
  assert.equal(await badge.isVisible(), false, 'Reload revived a reviewed contact');
  // A new connection ID for the same certificate stays reviewed; a different identity is new.
  await page.evaluate(() => {
    const status = structuredClone(QP.state.status);
    const peer = status.peers.find(p => !p.isTrusted);
    peer.id = '00000000-0000-0000-0000-000000000004';
    status.peers.push({...peer,id:'00000000-0000-0000-0000-000000000005',certHash:btoa('x'.repeat(32)),name:'Otra identidad'});
    status.peers.find(p => p.name === 'Mi ordenador').connectionState = 'connecting';
    QP.emit('status_updated', status);
  });
  assert.equal(await badge.textContent(), '1 nuevo');
  assert.equal(await page.locator('#homeReviewCount').textContent(), '2');
  assert.equal(await page.locator('#homeContacts .needs-review').count(), 1);
  await page.locator('[data-contact-filter=all]').click();
  await page.locator('#contactConnectionFilter').selectOption('connecting');
  assert.equal(await page.locator('#homeContacts .contact-card').count(), 1);
  assert.match(await page.locator('#homeContacts').textContent(), /Mi ordenador/);
  await page.evaluate(() => {
    const status = structuredClone(QP.state.status);
    const reviewed = status.peers.find(p => !p.isTrusted);
    status.savedPeers.push({...reviewed,isSaved:true});
    status.peers = status.peers.filter(p => p.id !== reviewed.id);
    QP.emit('status_updated', status);
  });
  if(!await page.locator('.contact-options').evaluate(e=>e.open)) await page.locator('.contact-options > summary').click();
  await page.locator('#contactConnectionFilter').selectOption('all'); await review.click();
  assert.equal(await page.locator('#homeContacts .contact-card').count(), 1, 'Saved untrusted contacts must remain in Sin confianza while offline');
  assert.match(await page.locator('#homeContacts').textContent(), /Sin confianza · revisado/);
  assert.equal(await badge.isVisible(), false);
  await page.setViewportSize({width:390,height:844}); await page.locator('#contactConnectionFilter').selectOption('all'); await review.click();
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Home overflows on phones');
  await page.screenshot({path:'.artifacts/contact-review-mobile.png',fullPage:true});
  // Acceptance is directly available in Por revisar, with duplicate decisions blocked in flight.
  await page.reload(); await page.locator('#homeContactCount').filter({hasText:'3'}).waitFor(); await review.click();
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Direct review actions overflow on phones');
  await page.screenshot({path:'.artifacts/contact-review-direct-mobile.png',fullPage:true});
  let finishTrust, decisions = [];
  await page.route('**/api/peer/trust', async route => {
    decisions.push(route.request().postDataJSON());
    await new Promise(resolve => finishTrust = resolve);
    await route.continue();
  });
  await page.getByRole('button', {name:'Aceptar contacto: Dispositivo nuevo',exact:true}).click();
  await page.waitForFunction(() => document.querySelector('[data-action-key$=":trust"]').disabled);
  assert.equal(decisions.length, 1);
  assert.equal(await page.locator('dialog.contact-review').count(), 0, 'Acceptance must not require an extra dialog');
  assert.equal(await page.getByRole('button', {name:'Revisar contacto: Dispositivo nuevo',exact:true}).isDisabled(), true);
  finishTrust(); await page.locator('#homeContacts .contact-card').waitFor({state:'detached'});
  assert.equal(decisions[0].trust, true);
  assert.equal(await page.locator('#homeReviewCount').textContent(), '0');
  await page.locator('[data-contact-filter="all"]').click();
  assert.equal(await page.getByRole('button', {name:'Aceptar contacto: Dispositivo nuevo',exact:true}).count(), 0);
  assert.equal(errors.length, 0, errors.join('\n'));
  console.log('PASS home contacts: direct accept/decline, duplicate decision blocking, independent filters, pending attention, optional identity review, save failure/retry, reload, stable identity and mobile layout');
} finally { await closeBrowserFixture(browser, server); }
