import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real media studio with fixture responses: no installation, microphone or screen capture.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, permissions: ['clipboard-read', 'clipboard-write'] }), errors = [];
  page.on('pageerror', error => errors.push(error.message));
  let installed = false, platform = 'windows', refreshes = 0;
  await page.route('**/voice.js', route => route.fulfill({ contentType: 'text/javascript', body: readVoiceSource().replace(/\}\)\(\);\s*$/, `window.testFfmpeg = { load: loadNativeCapabilities, open() { voiceStatus = { ...(voiceStatus || {}), activeVoiceCalls: [{peerId:'fixture',peerName:'Prueba'}] }; voiceState.activeCalls = [{peerId:'fixture',peerName:'Prueba'}]; openScreenSettingsModal(); } }; })();`) }));
  await page.route('**/api/voice/state', route => route.fulfill({ json: { activeCalls: [{ peerId: 'fixture', peerName: 'Prueba' }], backendName: 'Fixture', masterVolume: 1 } }));
  await page.route('**/api/voice/media-capabilities*', route => {
    if (route.request().url().includes('refresh=1')) refreshes++;
    const windows = platform === 'windows';
    const ffmpeg = { platform, system: windows ? 'Windows' : platform === 'android' ? 'Android' : 'Fedora Linux', installed,
      nativeCaptureSupported: windows, version: installed ? 'ffmpeg version fixture' : null,
      message: platform === 'android' ? 'Android utiliza los códecs del dispositivo; no necesita instalar FFmpeg.' : installed ? 'FFmpeg instalado.' : 'No se ha encontrado FFmpeg. Puedes usar el navegador.',
      options: platform === 'android' || installed ? [] : [{ title: windows ? 'WinGet · yt-dlp.FFmpeg (recomendado)' : 'Fedora', command: windows ? 'winget install --id yt-dlp.FFmpeg --exact --source winget' : 'sudo dnf install ffmpeg-free', url: 'https://ffmpeg.org/download.html', note: 'Instalación externa.' }] };
    return route.fulfill({ json: { ffmpeg, nativeCaptureAvailable: windows && installed,
      nativeEncoders: windows && installed ? [{ Id: 'libx264', Codec: 'h264', Available: true, Hardware: false }] : [], sources: [], displays: [], audioBackend: 'Fixture', frameRates: [30, 60], resolutionHeights: [720, 1080] } });
  });
  await page.goto(url + 'voice.html');
  await page.waitForFunction(() => typeof window.testFfmpeg === 'object');
  await page.evaluate(() => window.testFfmpeg.open());
  const panel = page.locator('#ffmpegInstallationGuide');
  await panel.getByText('Instalar FFmpeg · Windows', { exact: true }).waitFor();
  assert.equal(await panel.locator('textarea').inputValue(), 'winget install --id yt-dlp.FFmpeg --exact --source winget');
  assert.equal(await page.locator('#screenBackendSelect').count(),0);
  await panel.getByRole('button', { name: 'Copiar comando' }).click();
  await panel.getByRole('button', { name: /Copiado|Seleccionado/ }).waitFor();
  if (process.env.QUICNET_FFMPEG_SCREENSHOT) await page.screenshot({ path: process.env.QUICNET_FFMPEG_SCREENSHOT });
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 1), true, 'Commands must fit narrow mobile views.');
  installed = true;
  await panel.getByRole('button', { name: 'Ya lo he instalado · comprobar' }).click();
  await panel.waitFor({state:'hidden'});
  assert.equal(await panel.textContent(), '', 'Installed FFmpeg must not leave a detected banner behind.');
  assert.ok(refreshes > 0, 'The install button must request a fresh backend detection.');
  assert.equal(await panel.locator('textarea').count(), 0);
  assert.equal(await page.locator('#screenBackendSelect').count(),0);
  installed = false; platform = 'linux';
  await page.evaluate(() => window.testFfmpeg.load(true));
  assert.equal(await panel.locator('textarea').inputValue(), 'sudo dnf install ffmpeg-free');
  platform = 'android';
  await page.evaluate(() => window.testFfmpeg.load(true));
  assert.equal(await panel.isHidden(), true, 'Android must not show installation instructions.');
  assert.equal(await panel.locator('textarea').count(), 0);
  assert.deepEqual(errors, []);
  console.log('PASS FFmpeg installation UI: missing → copy → refresh → detected, Fedora command, Android exclusion, mobile layout.');
} finally { await closeBrowserFixture(browser, server); }
