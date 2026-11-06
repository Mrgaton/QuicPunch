import {createRequire} from 'node:module';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {once} from 'node:events';
import {existsSync, readdirSync} from 'node:fs';
import {join, delimiter} from 'node:path';

const {chromium} = createRequire(import.meta.url)(process.env.QUICNET_PLAYWRIGHT_MODULE || 'playwright');

// Use the browser pinned by package-lock.json unless an explicit override is supplied.
export async function launchBrowser(options = {}) {
  const browser = process.env.QUICNET_TEST_BROWSER_PATH
    ? {executablePath: process.env.QUICNET_TEST_BROWSER_PATH}
    : {channel: process.env.QUICNET_TEST_BROWSER || 'chromium'};
  const instance = await chromium.launch({headless: true, ...browser, ...options});
  // Intercepted fixture pages still need Chromium's explicit loopback permission.
  // This does not grant camera/microphone access or relax the application CSP.
  for (const method of ['newPage', 'newContext']) {
    const create = instance[method].bind(instance);
    instance[method] = (options = {}) => create({...options,
      permissions: [...new Set(['local-network-access', ...(options.permissions || [])])]});
  }
  return instance;
}

export function startPreviewServer(args = []) {
  const server = spawn(process.execPath,
    [fileURLToPath(new URL('../../scripts/preview-webui.mjs', import.meta.url)), '--port=0', ...args],
    {stdio: ['ignore', 'pipe', 'pipe', 'ipc'], windowsHide: true});
  server.ready = new Promise((resolve, reject) => {
    let output = '', errors = '';
    const timeout = setTimeout(() => finish(new Error('Preview startup timed out: ' + errors)), 10000);
    const onError = error => finish(error);
    const onExit = code => finish(new Error(`Preview exited before listening (${code}): ${errors}`));
    const onData = data => {
      output = (output + data).slice(-8192);
      const match = output.match(/http:\/\/127\.0\.0\.1:\d+\//);
      if (match) finish(null, match[0]);
    };
    const onStderr = data => { errors = (errors + data).slice(-8192); };
    function finish(error, url) {
      clearTimeout(timeout);
      server.off('error', onError); server.off('exit', onExit); server.stdout.off('data', onData);
      if (error) { server.kill(); reject(error); } else resolve(url);
    }
    server.once('error', onError); server.once('exit', onExit);
    server.stdout.on('data', onData); server.stderr.on('data', onStderr);
  });
  return server;
}

export async function closeBrowserFixture(browser, server) {
  try { await browser?.close(); }
  finally {
    if (server?.pid && server.exitCode === null && server.signalCode === null) {
      const stopped = once(server, 'exit');
      server.kill();
      await stopped;
    }
  }
}

export function findFfmpeg() {
  if (process.env.QUICNET_FFMPEG_PATH) return process.env.QUICNET_FFMPEG_PATH;
  const executable = process.platform === 'win32' ? 'ffmpeg.exe' : 'ffmpeg';
  const candidates = (process.env.PATH || '').split(delimiter).map(path => join(path, executable));
  if (process.platform === 'win32') {
    const roots = [process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Microsoft', 'WinGet'),
      process.env.ProgramFiles && join(process.env.ProgramFiles, 'WinGet')].filter(Boolean);
    for (const root of roots) {
      const packages = join(root, 'Packages');
      if (!existsSync(packages)) continue;
      for (const name of readdirSync(packages).filter(name => /ffmpeg/i.test(name)).sort()) {
        const directory = join(packages, name);
        for (const build of readdirSync(directory).filter(name => /^ffmpeg/i.test(name)).sort().reverse())
          candidates.push(join(directory, build, 'bin', executable));
      }
    }
  }
  const path = candidates.find(existsSync);
  if (!path) throw new Error('FFmpeg is required for ScreenViewerBrowserTests. Install it or set QUICNET_FFMPEG_PATH.');
  return path;
}
