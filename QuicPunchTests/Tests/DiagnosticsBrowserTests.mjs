import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Inject deliberate failures into the isolated fixture; no user data or P2P calls.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage(), native=[], consoleErrors=[];
  let socketCount=0;
  page.on('console',message=>{if(['error','warning'].includes(message.type()))consoleErrors.push(message.text());});
  page.on('websocket',socket=>{socketCount++;socket.on('framesent',({payload})=>{try{const message=JSON.parse(String(payload));if(message.type==='client_log')native.push(message.data);}catch{}});});
  await page.route('**/__diagnostic/http',route=>route.fulfill({status:503,json:{error:'fixture-http'}}));
  await page.route('**/__diagnostic/network',route=>route.abort('failed'));
  await page.route('**/__diagnostic/json',route=>route.fulfill({contentType:'application/json',body:'this is invalid JSON'}));
  await page.route('**/__diagnostic/logical',route=>route.fulfill({json:{success:false,error:'fixture-logical'}}));
  await page.route('**/__diagnostic/empty',route=>route.fulfill({status:204,body:''}));
  await page.route('**/__diagnostic/image',route=>route.fulfill({status:404,body:'missing'}));
  await page.goto(url);
  await page.waitForFunction(()=>QP.isWsConnected);
  await page.evaluate(async()=>{
    console.error('fixture-console',new Error('fixture-stack'));
    console.warn('fixture-warning');
    for(let i=0;i<30;i++)console.warn('fixture-repeated');
    setTimeout(()=>{throw new Error('fixture-uncaught');},0);
    Promise.reject(new Error('fixture-rejection'));
    await fetch('/__diagnostic/http').catch(()=>{});
    await fetch('/__diagnostic/network').catch(()=>{});
    await QP.api('/__diagnostic/json').catch(()=>{});
    await QP.api('/__diagnostic/logical').catch(()=>{});
    await QP.api('/__diagnostic/empty');
    QP.toast('fixture-toast','error');
    QP.on('fixture-event',()=>{throw new Error('fixture-listener');});QP.emit('fixture-event',{});
    const image=new Image();image.src='/__diagnostic/image';document.body.append(image);
    QuicNetDiagnostics.report(new Error('fixture-secret csrf=very-secret https://localhost/api?token=very-secret'),'Prueba de privacidad');
  });
  const expected=['fixture-console','fixture-warning','fixture-uncaught','fixture-rejection','HTTP 503','fixture-logical','fixture-toast','fixture-listener'];
  for(let i=0;i<100&&!expected.every(message=>native.some(entry=>entry.message.includes(message)));i++)await new Promise(resolve=>setTimeout(resolve,50));
  for(const message of expected)assert.ok(native.some(entry=>entry.message.includes(message)),`Missing native diagnostic: ${message}`);
  assert.ok(native.some(entry=>entry.context.includes('/__diagnostic/network')),'Caught fetch rejection was silent');
  assert.ok(native.some(entry=>entry.context.includes('/__diagnostic/json')&&entry.stack.includes('SyntaxError')),'Invalid JSON lost its cause/stack');
  assert.ok(native.some(entry=>entry.context==='Recurso'&&entry.message.includes('/__diagnostic/image')),'Resource load error was silent');
  assert.equal(native.filter(entry=>entry.message==='fixture-repeated').length,1,'Repeated failures flooded the native console');
  assert.ok(native.find(entry=>entry.message.includes('fixture-console')).stack.includes('Error: fixture-stack'));
  assert.ok(!JSON.stringify(native).includes('very-secret'),'Diagnostics leaked query parameters or credentials');
  assert.ok(!native.some(entry=>entry.context.includes('/__diagnostic/empty')),'Successful empty response was treated as invalid JSON');
  assert.ok(consoleErrors.some(message=>message.includes('fixture-console')),'Browser console lost original errors');
  assert.equal(socketCount,1,'Diagnostics opened an extra socket');
  const watch=await browser.newPage();
  const watchLogs=[];watch.on('websocket',socket=>socket.on('framesent',({payload})=>{try{const message=JSON.parse(String(payload));if(message.type==='client_log')watchLogs.push(message.data);}catch{}}));
  await watch.goto(url+'watch/');
  await watch.waitForFunction(()=>!!window.QuicNetDiagnostics);
  await watch.evaluate(()=>console.error('fixture-watch',new Error('fixture-watch-stack')));
  for(let i=0;i<60&&!watchLogs.some(entry=>entry.message.includes('fixture-watch'));i++)await new Promise(resolve=>setTimeout(resolve,50));
  assert.ok(watchLogs.some(entry=>entry.message.includes('fixture-watch')&&entry.stack.includes('fixture-watch-stack')),'Watch diagnostics were not forwarded');
  console.log('PASS web diagnostics: uncaught/rejected/caught errors, console, HTTP/network/JSON, media resources, stack traces, native WS forwarding, deduplication, redaction and Watch');
} finally {await closeBrowserFixture(browser, server);}
