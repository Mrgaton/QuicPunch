import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import {fileURLToPath} from 'node:url';
import {mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 const page = await browser.newPage({viewport:{width:1280,height:900}}), errors=[], operations=[];
 page.on('pageerror',e=>errors.push(e.message));
 let administrator=true, granted=false, revoked=false;
 const share={id:'00000000-0000-0000-0000-000000000123',name:'Documentos <img src=x>',recipient:'test',root:'C:\\Documentos',writable:true,allowDelete:false};
 await page.route('**/api/virtual-disk/**',async route=>{
   const req=route.request(), body=req.postDataJSON() || {}, operation=body.request?.operation; operations.push(body);
   let result={success:true};
   if(operation==='shares')result={success:true,canExplore:administrator,shares:granted&&!revoked?[share]:[]};
   else if(operation==='explore') result={success:true,canExplore:true,entries:body.request.path?[{name:'Proyecto',path:'C:\\Documentos\\Proyecto',directory:true}]:[{name:'C:\\',path:'C:\\',directory:true}]};
   else if(operation==='grant'){granted=true;result={success:true,shares:[share]};}
   else if(operation==='list')result={success:true,entries:[{name:'Nota <script>alert(1)</script>.txt',path:'nota.txt',directory:false},{name:'Fotos',path:'Fotos',directory:true}]};
   else if(operation==='begin-write')result={success:true,transferId:'00000000-0000-0000-0000-000000000456'};
   else if(operation==='commit')result={success:true,version:'verified'};
   else if(operation==='revoke'){revoked=true;}
   await route.fulfill({contentType:'application/json',body:JSON.stringify(result)});
 });
 await page.goto(url); await page.waitForFunction(()=>window.QP?.state.status);
 await page.getByRole('link',{name:'Disco virtual'}).click();
 await page.locator('#diskExplore').click();
 await page.getByRole('button',{name:'📁 C:\\',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('diskPath').value==='C:\\');
 assert.equal(await page.locator('#diskPath').inputValue(),'C:\\');
 await page.locator('#diskPermission').selectOption('write');
 await page.locator('#diskGrant').click();
 await page.getByRole('button',{name:/Documentos <img/}).click();
 assert.equal(await page.locator('#diskFiles script').count(),0,'Untrusted filename executed markup');
 assert.equal(await page.locator('#diskShares img').count(),0,'Untrusted share name created markup');
 await page.getByRole('link',{name:'Descargar',exact:true}).waitFor();
 assert.equal(await page.getByRole('link',{name:'Descargar',exact:true}).count(),1);
 await page.locator('#diskUpload').setInputFiles({name:'upload.txt',mimeType:'text/plain',buffer:Buffer.from('file contents')});
 await page.waitForFunction(()=>document.getElementById('diskTransfer').textContent.includes('verificado'));
 const writes=operations.filter(o=>o.request?.operation==='write'); assert.equal(Buffer.from(writes[0].request.data,'base64').toString(),'file contents');
 assert.equal(operations.find(o=>o.request?.operation==='grant').request.writable,true);
 assert.equal(operations.find(o=>o.request?.operation==='grant').request.allowDelete,false);
 administrator=false; await page.locator('#diskRefresh').click();
 await page.waitForFunction(()=>document.getElementById('diskExplore').hidden);
 assert.equal(await page.locator('#diskGrantPanel').isVisible(),false);
 mkdirSync(fileURLToPath(new URL('../../.artifacts/virtual-disk/',import.meta.url)),{recursive:true});
 for(const width of [1280,390]){
   await page.setViewportSize({width,height:900});
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Disk view overflowed mobile viewport');
   await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/virtual-disk/'+width+'.png',import.meta.url)),fullPage:true});
 }
 await page.getByRole('button',{name:'Revocar',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('diskFiles').textContent.includes('revocado'));
 assert.equal(await page.locator('#diskUploadLabel').isVisible(),false);
 assert.deepEqual(errors,[]);
 console.log('PASS disk UI: navigation, administrator browsing, grant defaults, upload chunks, escaping, role downgrade, revocation and desktop/mobile layout');
} finally { await closeBrowserFixture(browser, server); }
