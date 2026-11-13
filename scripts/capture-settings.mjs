// Reproducible visual review using the real WebUiServer and synthetic account data.
// Build first: dotnet build QuicPunchTests -c Test
// Run: node scripts/capture-settings.mjs before (or after)
import {spawn} from 'node:child_process';
import {mkdir, writeFile} from 'node:fs/promises';
import {resolve} from 'node:path';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../QuicPunchTests/Fixtures/BrowserFixture.mjs';

const phase = process.argv[2] || 'after';
if (!/^[a-z0-9-]+$/i.test(phase)) throw Error('Use a simple directory name.');
const directory = resolve('.artifacts/settings-review', phase);
await mkdir(directory, {recursive: true});
const host = spawn('dotnet', ['run', '--project', 'QuicPunchTests', '-c', 'Test', '--no-build', '--', '--serve-webui-preview'], {stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true});
const fixture = startPreviewServer();
let browser;
const captures = [], errors = [];
try {
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('WebUiServer startup timed out')), 20000);
    host.once('error', reject);
    host.stdout.on('data', data => {
      const match = data.toString().match(/WEBUI-TEST-URL=(http:\/\/127\.0\.0\.1:\d+\/)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
  });
  const preview = await fixture.ready;
  const status = await (await fetch(preview + 'api/status')).json();
  browser = await launchBrowser();
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}, locale: 'es-ES', timezoneId: 'Europe/Madrid'});
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.startsWith('/api/geolocation/')) return route.continue();
    const response = await route.fetch({url: preview.replace(/\/$/, '') + path});
    await route.fulfill({response});
  });
  await page.routeWebSocket(/\/ws(?:\?|$)/, socket => {
    const server = socket.connectToServer();
    server.onMessage(message => {
      const payload = JSON.parse(String(message));
      if (payload.type === 'status_updated') payload.data = status;
      socket.send(JSON.stringify(payload));
    });
  });
  let name = 'Ana';
  await page.route('**/api/profile', route => {
    if (route.request().method() === 'POST') name = route.request().postDataJSON().displayName;
    return route.fulfill({json: {customName: name, displayName: name || 'Mi portátil', deviceName: 'Mi portátil'}});
  });
  let settings = {startWithSystem: true, runInBackground: true, notifications: {enabled: true, desktop: true, inApp: true, sound: true, preview: true, whileFocused: true, doNotDisturb: false, doNotDisturbUntil: null, quietHours: false, quietStart: 1320, quietEnd: 480, volume: 60, tone: 'soft', features: {}, contacts: {}}};
  await page.route('**/api/app/preferences', route => {
    if (route.request().method() === 'POST') settings = route.request().postDataJSON();
    return route.fulfill({json: {success: true, settings, startupSupported: true, platformHelp: 'Al cerrar la ventana, QuicNet continúa en segundo plano.'}});
  });
  await page.goto(url + 'settings.html');
  await page.waitForFunction(() => window.QP?.state.status);
  await page.evaluate(status => {
    QP.state.status = status; QP.emit('status_updated', status);
    QP.emit('update_status', {Stage: 'current', CurrentVersion: '1.0.0', CurrentBuild: 100, CurrentChannel: 'stable', Channel: 'stable', Automatic: true, Downloaded: 0, Total: 0, Platform: 'win-x64', Message: 'Tienes la última versión de QuicNet.', LastCheck: '2026-11-06T12:00:00Z'});
  }, status);
  const sections = await page.locator('[data-settings-section]').evaluateAll(tabs => tabs.map(tab => tab.dataset.settingsSection));
  async function capture(name, locator) {
    if (locator) await locator.scrollIntoViewIfNeeded();
    await page.evaluate(() => scrollTo(0, 0));
    await page.screenshot({path: resolve(directory, name + '.png'), fullPage: true, animations: 'disabled'});
    const dimensions = await page.evaluate(() => ({width: innerWidth, scrollWidth: document.documentElement.scrollWidth}));
    captures.push({name, ...dimensions});
  }
  for (const width of [1440, 390]) {
    await page.setViewportSize({width, height: 1000});
    for (const section of sections) {
      if (width > 1000) await page.locator('#settings-tab-' + section).click();
      else await page.locator('#settingsCategory').selectOption(section);
      await page.locator('#settings-' + section).waitFor();
      if (section === 'contacts') await page.locator('[data-subtab="tab-discovered-peers"]').click();
      if (section === 'diagnostics') await page.locator('[data-subtab="quicTelemetrySection"]').click();
      if (section === 'geolocation') {
        await page.waitForFunction(() => document.querySelector('#geolocationWorkbench').contentDocument?.querySelector('canvas')?.dataset.renderer);
        const frame = page.frameLocator('#geolocationWorkbench');
        await frame.locator('#start:not([disabled])').waitFor();
      }
      await capture(`${width}-${section}`);
      if (section === 'notifications') {
        await page.locator('#noticeDnd').check();
        await page.locator('#noticeQuietHours').check();
        await capture(`${width}-notifications-schedule`);
        await page.locator('#noticeDnd').uncheck();
        await page.locator('#noticeQuietHours').uncheck();
      }
      if (section === 'contacts' || section === 'diagnostics') {
        const group = section === 'contacts' ? 'peerdir' : 'diagtabs';
        for (const tab of await page.locator(`[data-group="${group}"]`).all()) {
          await tab.click();
          const target = await tab.getAttribute('data-subtab');
          await capture(`${width}-${section}-${target}`, page.locator('#' + target));
          if (target === 'tab-saved-peers') {
            const permissions = page.locator('#savedPeersBody .peer-permissions > summary').first();
            await permissions.click();
            await capture(`${width}-contacts-permissions`);
            await permissions.click();
          }
        }
      }
      if (section === 'connection') {
        await page.locator('.token-details > summary').click();
        await capture(`${width}-connection-codes`);
        await page.locator('.token-details > summary').click();
      }
      if (section === 'geolocation') {
        const frame = page.frameLocator('#geolocationWorkbench');
        await frame.locator('#configuration > summary').click();
        await frame.locator('#configuration details > summary').click();
        await capture(`${width}-geolocation-configuration`);
      }
    }
  }
  await writeFile(resolve(directory, 'manifest.json'), JSON.stringify({source: 'Real WebUiServer; synthetic profile, preferences and contacts; offline IP catalogue status.', captures, errors}, null, 2));
  const labels = {profile: 'Mi perfil', notifications: 'Notificaciones', startup: 'Inicio y segundo plano', updates: 'Actualizaciones', connection: 'Conexión y Tor', geolocation: 'Localización', contacts: 'Contactos y permisos', diagnostics: 'Diagnóstico de red'};
  await writeFile(resolve(directory, 'index.html'), `<!doctype html><html lang="es"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Revisión de ajustes · QuicNet</title>
    <style>body{margin:0;background:#151c21;color:#dae4e8;font:16px/1.5 system-ui;padding:28px}h1{margin:0}p{color:#a5b5bd;max-width:900px}nav{display:flex;gap:16px;flex-wrap:wrap;margin:24px 0}select,a{color:#8ad7bc;background:#1b252b;padding:10px;border:1px solid #536b78;border-radius:6px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,540px),1fr));gap:24px}article{border:1px solid #33434c;border-radius:12px;padding:16px}h2{font-size:18px}img{width:100%;height:520px;object-fit:contain;object-position:top;background:#10171b}article>a{display:block;padding:0;border:0}article p{font-size:13px}[hidden]{display:none!important}</style>
    <h1>Revisión de ajustes · ${phase}</h1><p>${captures.length} capturas de los ocho apartados y sus opciones internas. Servidor web real con perfil, preferencias y contactos de ejemplo. Localización usa el mapa local; no se han iniciado sondeos de red. Pulsa una captura para verla completa.</p>
    <nav><label>Ventana <select id="width"><option value="">Todas</option><option>1440</option><option>390</option></select></label><label>Apartado <select id="section"><option value="">Todos</option>${Object.entries(labels).map(([key,label]) => `<option value="${key}">${label}</option>`).join('')}</select></label></nav>
    <main>${captures.map(c => `<article data-width="${c.width}" data-section="${c.name.split('-')[1]}"><h2>${labels[c.name.split('-')[1]]} · ${c.width} px</h2><p>${c.name}</p><a href="${c.name}.png"><img loading="lazy" src="${c.name}.png" alt="${c.name}"></a></article>`).join('')}</main><script>for(const select of document.querySelectorAll('select'))select.onchange=()=>{for(const item of document.querySelectorAll('article'))item.hidden=(width.value&&item.dataset.width!==width.value)||(section.value&&item.dataset.section!==section.value);};</script></html>`);
  console.log(JSON.stringify({directory, captures: captures.length, errors, overflow: captures.filter(c => c.scrollWidth > c.width)}));
} finally {
  host.stdin.end('\n'); host.kill();
  await closeBrowserFixture(browser, fixture);
}
