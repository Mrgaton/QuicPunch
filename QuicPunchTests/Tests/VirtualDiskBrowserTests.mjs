import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import {fileURLToPath} from 'node:url';
import {mkdirSync, writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
 const url = await server.ready;
 browser = await launchBrowser();
 const page = await browser.newPage({viewport:{width:1280,height:900}}), errors=[], operations=[];
 page.on('pageerror',e=>errors.push(e.message));
 let administrator=true, granted=false, revoked=false, slowWrite=false, failWrite=false;
 const share={id:'00000000-0000-0000-0000-000000000123',name:'Documentos <img src=x>',recipient:'test',root:'C:\\Documentos',writable:true,allowDelete:false};
 await page.route('**/api/virtual-disk/**',async route=>{
   const req=route.request(), body=req.postDataJSON() || {}, operation=body.request?.operation; operations.push(body);
   let result={success:true};
   if(operation==='shares')result={success:true,canExplore:administrator,shares:granted&&!revoked?[share]:[]};
   else if(operation==='explore') result={success:true,canExplore:true,entries:body.request.path?[{name:'Proyecto',path:'C:\\Documentos\\Proyecto',directory:true}]:[{name:'C:\\',path:'C:\\',directory:true}]};
   else if(operation==='grant'){granted=true;result={success:true,shares:[share]};}
   else if(operation==='outbox' || operation==='inbox'){granted=true;share.writable=operation==='inbox';result={success:true,shares:[share]};}
   else if(operation==='list')result={success:true,entries:[{name:'Nota <script>alert(1)</script>.txt',path:'nota.txt',directory:false},{name:'Fotos',path:'Fotos',directory:true}]};
   else if(operation==='begin-write')result={success:true,transferId:'00000000-0000-0000-0000-000000000456'};
   else if(operation==='commit')result={success:true,version:'verified'};
   else if(operation==='revoke'){revoked=true;}
   if (operation==='write' && slowWrite) await new Promise(resolve=>setTimeout(resolve,400));
   if (operation==='write' && failWrite) result={success:false,code:'Denied',error:'Permiso revocado durante el envío'};
   if (new URL(req.url()).pathname.endsWith('/pick-folder')) result={success:true,nativePath:'content://documents/tree/primary%3ATest'};
   await route.fulfill({contentType:'application/json',body:JSON.stringify(result)});
 });
 await page.goto(url); await page.waitForFunction(()=>window.QP?.state.status);
 await page.getByRole('link',{name:'Disco virtual'}).click();
 await page.locator('#diskExplore').click();
 await page.getByRole('button',{name:'📁 C:\\',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('diskPath').value==='C:\\');
 assert.equal(await page.locator('#diskPath').inputValue(),'C:\\');
 // Local sharing requires an explicit recipient before the grant can be submitted.
 await page.locator('#diskRecipient').selectOption({index:1});
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
 const beforeMultiple=operations.length;
 await page.locator('#diskUpload').setInputFiles([
   {name:'one.bin',mimeType:'application/octet-stream',buffer:Buffer.alloc(150000,42)},
   {name:'empty.txt',mimeType:'text/plain',buffer:Buffer.alloc(0)},
   {name:'two.txt',mimeType:'text/plain',buffer:Buffer.from('second')}
 ]);
 await page.waitForFunction(()=>document.getElementById('diskTransfer').textContent.includes('3 de 3'));
 const multiple=operations.slice(beforeMultiple).filter(o=>o.request?.operation==='begin-write');
 assert.deepEqual(multiple.map(o=>o.request.length),[150000,0,6]);
 assert.equal(await page.locator('#diskQueue [data-state=done]').count(),3);
 const folder=fileURLToPath(new URL('../../.artifacts/virtual-disk/upload-folder/',import.meta.url));
 mkdirSync(folder+'/nested',{recursive:true});
 writeFileSync(folder+'/root.txt','root'); writeFileSync(folder+'/nested/empty.txt','');
 const beforeFolder=operations.length;
 await page.locator('#diskFolderUpload').setInputFiles(folder);
 await page.waitForFunction(()=>document.getElementById('diskTransfer').textContent.includes('2 de 2'));
 const folderOps=operations.slice(beforeFolder);
 assert.ok(folderOps.some(o=>o.request?.operation==='mkdir' && o.request.path==='upload-folder/nested'),'Directory structure was lost');
 assert.ok(folderOps.some(o=>o.request?.operation==='begin-write' && o.request.path==='upload-folder/nested/empty.txt'),'Relative file path was lost');
 slowWrite=true;
 const beforeCancel=operations.length;
 await page.locator('#diskUpload').setInputFiles([
   {name:'cancel.bin',mimeType:'application/octet-stream',buffer:Buffer.alloc(500000,1)},
   {name:'never.txt',mimeType:'text/plain',buffer:Buffer.from('never')}
 ]);
 await page.waitForFunction(()=>document.querySelector('#diskQueue [data-state=active]'));
 assert.equal(await page.locator('#diskDevice').isDisabled(),true,'Destination changed while uploading');
 await page.locator('#diskCancel').click();
 await page.waitForFunction(()=>document.getElementById('diskCancel').hidden);
 assert.equal(operations.slice(beforeCancel).filter(o=>o.request?.operation==='commit').length,0,'Cancelled upload was committed');
 assert.ok(operations.slice(beforeCancel).some(o=>o.request?.operation==='close'),'Cancellation did not close staging transfer');
 assert.equal(await page.locator('#diskQueue [data-state=cancelled]').count(),2);
 slowWrite=false;failWrite=true;
 await page.locator('#diskUpload').setInputFiles({name:'denied.txt',mimeType:'text/plain',buffer:Buffer.from('denied')});
 await page.waitForFunction(()=>document.getElementById('diskTransfer').textContent.includes('con error'));
 assert.equal(await page.locator('#diskQueue [data-state=error]').count(),1);
 failWrite=false;
 await page.locator('#diskSendFiles').setInputFiles({name:'send-from-phone.pdf',mimeType:'application/pdf',buffer:Buffer.from('portable document')});
 await page.waitForFunction(()=>document.querySelector('#diskQueue [data-state=done]')?.textContent.includes('send-from-phone.pdf'));
 assert.ok(operations.some(o=>o.request?.operation==='outbox' && o.request.recipient),'Quick send did not create a recipient-scoped outbox');
 assert.equal(share.writable,false,'Quick send gave recipient write access');
 await page.locator('#diskReceive').click();
 await page.waitForFunction(()=>document.getElementById('diskStatus').textContent.includes('Recepción activada'));
 assert.ok(operations.some(o=>o.request?.operation==='inbox' && o.request.expiresUtc),'Reception has no expiry');
 await page.evaluate(()=>{window.__qp_is_android=true;});
 await page.locator('#diskRefresh').click();
 await page.locator('#diskPickFolder').click();
 await page.waitForFunction(()=>document.getElementById('diskPath').value.startsWith('content://'));
 await page.locator('#diskGrant').click();
 assert.ok(operations.some(o=>o.request?.operation==='grant' && o.request.path.startsWith('content://')),'Android tree was not shared');
 await page.getByRole('button',{name:/Documentos <img/}).click();
 await page.getByRole('button',{name:'Guardar en el móvil',exact:true}).click();
 await page.waitForFunction(()=>document.getElementById('diskStatus').textContent.includes('guardado y verificado'));
 assert.ok(operations.some(o=>o.shareId===share.id && o.path==='nota.txt'),'Native save lost the selected file');
 assert.equal(await page.getByRole('button',{name:'Montar unidad',exact:true}).count(),0,'Android offered Windows drive mounting');
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
 console.log('PASS disk UI: batch and empty uploads, cancellation, failures, quick send/receive, Android tree picker/save, escaping, revocation and desktop/mobile layout');
} finally { await closeBrowserFixture(browser, server); }
