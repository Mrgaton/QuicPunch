// Auxiliary visual QA only. Node/Playwright are not part of NetLocate's runtime.
const { chromium } = require(process.env.NETLOCATE_PLAYWRIGHT);
const { pathToFileURL } = require('node:url');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
(async () => {
  const output = path.resolve(__dirname, '../bin/visual-qa');
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, ...(process.env.NETLOCATE_BROWSER ? { executablePath: process.env.NETLOCATE_BROWSER } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
    const errors = [], external = [];
    page.on('pageerror', e => errors.push(e.message));
    page.on('console', msg => { if (msg.type() === 'error') errors.push(msg.text()); });
    await page.route('**/*', route => {
      if (/^(file:|data:|about:)/.test(route.request().url())) return route.continue();
      external.push(route.request().url()); return route.abort();
    });
    const report = process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(__dirname, '../artifacts/demo-final/report.html');
    await page.goto(pathToFileURL(report).href);
    await page.waitForLoadState('load');
    assert.equal(await page.locator('.frame:visible').count(), 1);
    assert.ok(await page.locator('#frame6').isVisible());
    const box = await page.locator('#frame6 .maps svg').first().evaluate(svg => {
      const b = svg.querySelector('use').getBBox(); return { width: b.width, height: b.height };
    });
    assert.ok(box.width > 300 && box.height > 100, 'Local SVG definitions did not render');
    await page.screenshot({ path: path.join(output, 'demo-desktop.png'), fullPage: true });
    const last = await page.locator('#frame6 .maps').screenshot();
    await page.locator('label[for=r1]').click();
    assert.ok(await page.locator('#frame1').isVisible());
    assert.ok(!(await page.locator('#frame6').isVisible()));
    const first = await page.locator('#frame1 .maps').screenshot();
    assert.ok(!last.equals(first), 'The map did not change between rounds');
    await page.screenshot({ path: path.join(output, 'demo-first-round.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Mobile horizontal overflow');
    await page.locator('label[for=r6]').click();
    await page.screenshot({ path: path.join(output, 'demo-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1440, height: 1100 });
    await page.goto(pathToFileURL(process.argv[3] ? path.resolve(process.argv[3]) : path.resolve(__dirname, '../artifacts/live-validation/report.html')).href);
    if (process.argv[3] && /city-study/.test(process.argv[3])) {
      assert.equal(await page.locator('.frame:visible .conditional').count(), 1, 'Missing separate conditional region');
      assert.match(await page.locator('.frame:visible .conditional').innerText(), /no demuestra precisión de ciudad/);
      const regionalBox = await page.locator('.frame:visible .conditional svg').evaluate(svg => {
        const box = svg.querySelector('use').getBBox(); return { width: box.width, height: box.height };
      });
      assert.ok(regionalBox.width > 0 && regionalBox.height > 0, 'Conditional SVG did not render');
    }
    await page.screenshot({ path: path.join(output, 'live-desktop.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'LIVE mobile horizontal overflow');
    await page.screenshot({ path: path.join(output, 'live-mobile.png'), fullPage: true });
    assert.deepEqual(external, [], 'The local report requested an external resource');
    assert.deepEqual(errors, [], 'Browser/CSP errors');
    console.log('PASS offline maps, SVG fragments, round navigation, changing maps, desktop/mobile layout, live report and zero external requests');
  } finally { await browser.close(); }
})().catch(e => { console.error(e); process.exitCode = 1; });
