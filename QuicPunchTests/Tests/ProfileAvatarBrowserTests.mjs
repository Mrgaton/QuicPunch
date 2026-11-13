import {launchBrowser, startPreviewServer, closeBrowserFixture, findFfmpeg} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

const directory = fileURLToPath(new URL('../obj/avatar-media/', import.meta.url));
await mkdir(directory, {recursive:true});
const ffmpeg = findFfmpeg();
for (const extension of ['gif', 'webm']) {
  execFileSync(ffmpeg, ['-hide_banner','-loglevel','error','-y','-f','lavfi','-i',
    'testsrc2=size=320x240:rate=8:duration=3', ...(extension === 'gif' ? [] : ['-c:v','libvpx-vp9']),
    directory + 'motion.' + extension], {windowsHide:true});
}
const server = startPreviewServer(['--interactive']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:1440,height:1000}}), errors = [], uploads = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/profile/avatar', async route => {
    assert.equal(route.request().headers()['content-type'], 'image/avif');
    uploads.push(Buffer.from(await page.evaluate(() => window.avatarUploadBytes)));
    await route.fulfill({json:{avatarUrl:''}});
  });
  await page.goto(url + 'settings.html');
  await page.locator('#profileChoosePhoto').waitFor();
  await page.evaluate(() => {
    const api = QP.api;
    QP.api = (path, options) => {
      if (path === '/api/profile/avatar') window.avatarUploadBytes = Array.from(new Uint8Array(options.body));
      return api(path, options);
    };
  });
  const png = await page.evaluate(async () => {
    const canvas = document.createElement('canvas'); canvas.width = 4096; canvas.height = 3072;
    const ctx = canvas.getContext('2d');
    for (let y = 0; y < canvas.height; y += 32) for (let x = 0; x < canvas.width; x += 32) {
      ctx.fillStyle = `rgb(${x / 16 % 256},${y / 12 % 256},${(x * 7 + y * 11) % 256})`;
      ctx.fillRect(x, y, 32, 32);
    }
    const blob = await new Promise(resolve => canvas.toBlob(resolve));
    // Track application drawing surfaces after fixture generation.
    const getContext = HTMLCanvasElement.prototype.getContext;
    window.avatarCanvasSizes = [];
    HTMLCanvasElement.prototype.getContext = function(...args) {
      window.avatarCanvasSizes.push([this.width, this.height]);
      return getContext.apply(this, args);
    };
    return Array.from(new Uint8Array(await blob.arrayBuffer()));
  });
  async function select(file) {
    const started = performance.now();
    await page.locator('#profilePhotoFile').setInputFiles(file);
    await page.locator('#avatarCropModal:not(.hidden)').waitFor();
    return Math.round(performance.now() - started);
  }
  async function save() {
    const count = uploads.length, started = performance.now();
    await page.locator('#avatarCropSave').click();
    await page.locator('#avatarCropModal').waitFor({state:'hidden',timeout:60000});
    assert.equal(uploads.length, count + 1);
    assert.ok(uploads.at(-1).length <= 512 * 1024);
    return Math.round(performance.now() - started);
  }
  async function inspect(bytes) {
    await writeFile(directory + 'latest.avif', bytes);
    // FFmpeg also validates animated AVIF on Chromium builds without AVIF ImageDecoder.
    const probe = JSON.parse(execFileSync(ffmpeg.replace(/ffmpeg(\.exe)?$/i,'ffprobe$1'),
      ['-v','error','-count_frames','-show_streams','-of','json',directory+'latest.avif'], {windowsHide:true}));
    const stream = probe.streams.at(-1), count = Number(stream.nb_read_frames);
    const raw = execFileSync(ffmpeg, ['-v','error','-i',directory+'latest.avif','-map',`0:${stream.index}`,'-vf','scale=80:80','-pix_fmt','rgba','-f','rawvideo','-'], {windowsHide:true,maxBuffer:16*1024*1024});
    const frameBytes = 80*80*4;
    return {count, duration:Number(stream.duration), frames:[...new Set([0,count-1])].map(index=>({width:stream.width,height:stream.height,
      pixels:Array.from(raw.subarray(index*frameBytes,(index+1)*frameBytes))}))};
  }
  const openMs = await select({name:'large.png',mimeType:'image/png',buffer:Buffer.from(png)});
  await page.locator('#avatarCropRotate').click();
  await page.locator('#avatarCropZoom').fill('3');
  await page.locator('#avatarCropCanvas').press('ArrowRight');
  const preview = await page.locator('#avatarCropPreview').evaluate(canvas => Array.from(canvas.getContext('2d').getImageData(0,0,80,80).data));
  const saveMs = await save(), still = await inspect(uploads.at(-1));
  assert.equal(still.count, 1); assert.equal(still.frames[0].width, 512);
  const difference = still.frames[0].pixels.reduce((sum, value, index) => sum + Math.abs(value - preview[index]), 0) / preview.length;
  assert.ok(difference < 12, `Export must match rotated, zoomed, panned preview: ${difference}`);
  assert.ok(await page.evaluate(() => window.avatarCanvasSizes.every(([w,h]) => w <= 1024 && h <= 1024)), 'Do not allocate full-resolution crop canvases');
  console.log(`PASS large photo: opening ${openMs} ms, encoding/upload ${saveMs} ms, ${uploads.at(-1).length} bytes; bounded preview and original-resolution crop`);
  for (const extension of ['gif','webm']) {
    const opened = await select(directory + 'motion.' + extension);
    assert.equal(await page.locator('#avatarAnimationControls').isVisible(), true);
    const saved = await save(), result = await inspect(uploads.at(-1));
    assert.equal(result.count, 24, 'Preserve three seconds at eight frames per second');
    assert.equal(result.duration, 3);
    assert.equal(result.frames[0].width, 240);
    assert.notDeepEqual(result.frames[0].pixels, result.frames[1].pixels, 'Motion must survive AVIF conversion');
    console.log(`PASS ${extension}: opening ${opened} ms, encoding/upload ${saved} ms, ${uploads.at(-1).length} bytes; ${result.count} moving AVIF frames`);
  }
  await select(directory + 'motion.gif');
  await page.locator('#avatarCropSave').click();
  await page.locator('#avatarCropCancel').click();
  assert.equal(await page.locator('#avatarCropModal').isVisible(), false);
  assert.equal(await page.locator('#profileChoosePhoto').isEnabled(), true);
  assert.equal(uploads.length, 3, 'Cancelling conversion must not upload an avatar');
  // Optional same-input comparison against the previous archival encoder setting.
  if (process.env.QUICNET_AVATAR_BENCHMARK) {
    const source = await readFile(new URL('../../QuicNet.Shared/wwwroot/avatar-encode-worker.js',import.meta.url),'utf8');
    await page.route('**/avatar-encode-baseline.js', route => route.fulfill({contentType:'text/javascript',body:source.replace('speed: animated ? 6 : 5', 'speed: 2')}));
    for (const speed of [4,6]) await page.route(`**/avatar-encode-speed${speed}.js`, route => route.fulfill({contentType:'text/javascript',body:source.replace('speed: animated ? 6 : 5', `speed: ${speed}`)}));
    const results = await page.evaluate(async () => {
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = 512;
      const ctx = canvas.getContext('2d');
      for (let y=0;y<512;y+=4) for (let x=0;x<512;x+=4) {
        ctx.fillStyle=`rgb(${x%256},${y%256},${(x*7+y*11)%256})`; ctx.fillRect(x,y,4,4);
      }
      const results = [];
      for (const script of ['/avatar-encode-baseline.js','/avatar-encode-speed4.js','/avatar-encode-speed6.js','/avatar-encode-worker.js']) {
        const pixels=ctx.getImageData(0,0,512,512).data.buffer, start=performance.now();
        const bytes=await new Promise((resolve,reject) => {
          const worker=new Worker(script,{type:'module'});
          worker.onerror=event=>{worker.terminate();reject(new Error(event.message));};
          worker.onmessage=({data})=>{if(data.error){worker.terminate();reject(new Error(data.error));} else if(data.buffer){worker.terminate();resolve(data.buffer.byteLength);}};
          worker.postMessage({width:512,height:512,pixels},[pixels]);
        });
        results.push({script,ms:Math.round(performance.now()-start),bytes});
      }
      return results;
    });
    console.log('Same-input AVIF benchmark:', JSON.stringify(results));
  }
  assert.deepEqual(errors, []);
} finally { await closeBrowserFixture(browser, server); }
