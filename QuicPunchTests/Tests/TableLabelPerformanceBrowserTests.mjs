import assert from 'node:assert/strict';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer();
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage();
  await page.goto(url);
  await page.waitForFunction(() => QP.state.status);
  const result = await page.evaluate(async () => {
    const settle = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const host = document.createElement('section');
    host.innerHTML = '<p>Estado</p><table class="data-table"><thead><tr><th>Nombre</th><th>Estado</th></tr></thead><tbody><tr><td>Ana</td><td>Lista</td></tr></tbody></table><table class="data-table"><thead><tr><th>Nombre</th><th>Estado</th></tr></thead><tbody><tr><td>Bea</td><td>Lista</td></tr></tbody></table>';
    document.getElementById('spaContainer').append(host);
    await settle();
    const [first, second] = host.querySelectorAll('table');
    let firstScans = 0, secondScans = 0;
    const queryFirst = first.querySelectorAll.bind(first), querySecond = second.querySelectorAll.bind(second);
    first.querySelectorAll = selector => { if (selector === 'tbody tr') firstScans++; return queryFirst(selector); };
    second.querySelectorAll = selector => { if (selector === 'tbody tr') secondScans++; return querySecond(selector); };
    host.querySelector('p').textContent = 'Actualización sin cambios en las tablas';
    await settle();
    const unrelated = [firstScans, secondScans];
    firstScans = secondScans = 0;
    first.tBodies[0].insertAdjacentHTML('beforeend', '<tr><td>Celia</td><td>Lista</td></tr>');
    first.tBodies[0].insertAdjacentHTML('beforeend', '<tr><td>Diana</td><td>Lista</td></tr>');
    await settle();
    const edited = [firstScans, secondScans];
    const labels = [...first.tBodies[0].rows].map(row => [...row.cells].map(cell => cell.dataset.label));
    // A table inserted inside a wrapper is also discovered, including full-width rows.
    const wrapper = document.createElement('div');
    wrapper.innerHTML = '<table class="data-table"><thead><tr><th>Uno</th><th>Dos</th></tr></thead><tbody><tr><td colspan="2">Sin datos</td></tr></tbody></table>';
    host.append(wrapper);
    await settle();
    const fullRow = wrapper.querySelector('td').hasAttribute('data-full-row');
    host.remove();
    return {unrelated, edited, labels, fullRow};
  });
  assert.deepEqual(result.unrelated, [0, 0], 'Unrelated status changes scanned every table');
  assert.deepEqual(result.edited, [1, 0], 'Row changes did not coalesce or scanned an unchanged table');
  assert.deepEqual(result.labels, Array.from({length: 3}, () => ['Nombre', 'Estado']));
  assert.equal(result.fullRow, true);
  console.log('PASS responsive table labels: zero scans for unrelated mutations, one scan per changed table/frame and nested/full-row support');
} finally { await closeBrowserFixture(browser, server); }
