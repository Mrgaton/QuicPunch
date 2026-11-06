import {launchBrowser} from '../Fixtures/BrowserFixture.mjs';
import {createServer} from 'node:http';
import {readFileSync, writeFileSync, mkdirSync} from 'node:fs';
import {resolve, extname} from 'node:path';
import {createHash, generateKeyPairSync, sign} from 'node:crypto';
import {spawnSync} from 'node:child_process';
import assert from 'node:assert/strict';
const directory=resolve('.artifacts/download-page-test');mkdirSync(directory,{recursive:true});
const raw=Buffer.from('QuicNet actualización firmada · '.repeat(8000));
writeFileSync(resolve(directory,'app.exe'),raw);
const tool=spawnSync('dotnet',['run','--project','QuicNet.ReleaseTools/QuicNet.ReleaseTools.csproj','-c','Release','--','compress',resolve(directory,'app.exe'),resolve(directory,'app.exe.zst')],{encoding:'utf8',windowsHide:true,env:{...process.env,DOTNET_CLI_TELEMETRY_OPTOUT:'1'}});
assert.equal(tool.status,0,tool.stdout+tool.stderr);
const packed=readFileSync(resolve(directory,'app.exe.zst')),hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const {privateKey,publicKey}=generateKeyPairSync('rsa',{modulusLength:2048});
let corrupt=false,badRaw=false,badSignature=false,unsafe=false,challenge=false;
function manifest(channel){const artifacts={};for(const [platform,extension] of [['win-x64','.exe'],['linux-x64','.bin'],['android-arm64','.apk']])artifacts[platform]={url:'releases/test/app'+extension,sha256:badRaw?'0'.repeat(64):hash(raw),size:raw.length,compressed:{algorithm:'zstd',url:unsafe?'../evil'+extension+'.zst':'releases/test/app'+extension+'.zst',sha256:hash(packed),size:packed.length,windowLog:27}};const payload=Buffer.from(JSON.stringify({schema:1,channel,version:'1.2.3',build:channel==='stable'?14:15,artifacts}));return JSON.stringify({payload:payload.toString('base64'),signature:(badSignature?Buffer.alloc(256):sign('sha256',payload,privateKey)).toString('base64')});}
const server=createServer((req,res)=>{const path=new URL(req.url,'http://localhost').pathname.replace(/^\/QuicNet\//,'');res.setHeader('Cache-Control','no-store');if(path==='release-public.pem')return res.end(publicKey.export({type:'spki',format:'pem'}));if(path==='latest.json'||path==='latest-beta.json'){if(challenge){res.writeHead(403,{'cf-mitigated':'challenge'});return res.end('Browser verification required');}res.setHeader('Content-Type','application/json');return res.end(manifest(path==='latest.json'?'stable':'beta'));}if(path.endsWith('.zst')){const bytes=Buffer.from(packed);if(corrupt)bytes[bytes.length-1]^=1;res.setHeader('Content-Length',bytes.length);return res.end(bytes);}const file=resolve('scripts/download-page',path||'index.html');if(!file.startsWith(resolve('scripts/download-page'))){res.writeHead(404);return res.end();}try{res.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.mjs':'application/javascript','.css':'text/css'})[extname(file)]||'text/plain');res.end(extname(file)==='.html'?readFileSync(file,'utf8').replaceAll('__ASSET_PREFIX__','.'):readFileSync(file));}catch{res.writeHead(404);res.end();}});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
try{
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];page.on('pageerror',error=>errors.push(error.message));
  const url=`http://127.0.0.1:${server.address().port}/QuicNet/`;
  await page.goto(url);await page.waitForFunction(()=>!document.getElementById('download').disabled);
  assert.match(await page.locator('#release').textContent(),/Build 14.*Release/);
  await page.locator('#channel').selectOption('beta');assert.match(await page.locator('#release').textContent(),/Build 15.*Beta/);
  for(const platform of ['win-x64','android-arm64','linux-x64']){
    await page.locator('#platform').selectOption(platform);
    const pending=page.waitForEvent('download');await page.locator('#download').click();const download=await pending;
    const stream=await download.createReadStream(),chunks=[];for await(const chunk of stream)chunks.push(chunk);
    assert.deepEqual(Buffer.concat(chunks),raw,'Browser did not save the verified decompressed bytes');
  }
  corrupt=true;await page.locator('#download').click();await page.locator('#status.error').filter({hasText:'integridad'}).waitFor();corrupt=false;
  badRaw=true;await page.reload();await page.waitForFunction(()=>!document.getElementById('download').disabled);await page.locator('#download').click();await page.locator('#status.error').filter({hasText:'descomprimido'}).waitFor();badRaw=false;
  unsafe=true;await page.reload();await page.waitForFunction(()=>!document.getElementById('download').disabled);await page.locator('#download').click();await page.locator('#status.error').filter({hasText:'CDN'}).waitFor();unsafe=false;
  badSignature=true;await page.reload();await page.locator('#status.error').filter({hasText:'Firma'}).waitFor();assert.equal(await page.locator('#download').isDisabled(),true);badSignature=false;
  challenge=true;await page.reload();await page.locator('#verification').waitFor();assert.match(await page.locator('#verifyBrowser').getAttribute('href'),/latest\.json/);challenge=false;await page.locator('#retry').click();await page.waitForFunction(()=>!document.getElementById('download').disabled);
  for(const colorScheme of ['dark'])for(const width of [1280,390,320]){await page.emulateMedia({colorScheme});await page.setViewportSize({width,height:900});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await page.screenshot({path:`.artifacts/cdn-download-${colorScheme}-${width}.png`});}
  assert.deepEqual(errors,[]);console.log('PASS CDN download page: signed stable/beta, all platforms, real WASM Zstandard, both hashes, tamper/path rejection and responsive dark layouts');
}finally{await browser?.close();await new Promise(resolve=>server.close(resolve));}
