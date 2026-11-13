import assert from 'node:assert/strict';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

const server=startPreviewServer([]);
let browser;
try {
  const url=await server.ready;
  browser=await launchBrowser();
  const page=await browser.newPage();
  const errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  let snapshot={local:{peerId:'local',name:'Este dispositivo',token:'fixture'},peers:[],room:null};
  let socket, holdStatus=false, heldStatus, heldToken, tokenRequests=0, statusRequests=0, rejectToken=false;
  await page.routeWebSocket(/\/watch\/ws/,ws=>{socket=ws;ws.onMessage(()=>{});});
  await page.route('**/watch/api/**',async route=>{
    const path=new URL(route.request().url()).pathname;
    if(path.endsWith('/status')){
      statusRequests++;
      if(holdStatus){holdStatus=false;heldStatus=route;return;}
      return route.fulfill({json:snapshot});
    }
    if(path.endsWith('/token/connect')){
      tokenRequests++;
      if(rejectToken)return route.fulfill({status:400,json:{success:false,error:'La invitación no es válida.'}});
      heldToken=route;return;
    }
    return route.fulfill({json:{success:true}});
  });
  await page.goto(url+'watch.html');
  const frame=page.frames().find(f=>f.url().includes('/watch/'));
  assert.ok(frame);
  await frame.locator('#connectionText').filter({hasText:'Listo para compartir'}).waitFor();
  await frame.waitForFunction(()=>refreshPromise===null);

  // A delayed poll cannot replace a more recent room delivered by WebSocket.
  const old=structuredClone(snapshot);
  holdStatus=true;
  await frame.evaluate(()=>{window.pendingWatchRefresh=refresh();});
  for(let i=0;!heldStatus&&i<100;i++)await new Promise(resolve=>setTimeout(resolve,10));
  assert.ok(heldStatus,'Status request must be intercepted');
  snapshot={...snapshot,room:{roomId:'new-room',name:'Sala actualizada',isOwner:true,participants:[],media:null}};
  socket.send(JSON.stringify({type:'status',data:snapshot}));
  await frame.locator('#roomTitle').filter({hasText:'Sala actualizada'}).waitFor();
  await heldStatus.fulfill({json:old});heldStatus=null;
  await frame.evaluate(()=>window.pendingWatchRefresh);
  assert.equal(await frame.locator('#roomTitle').textContent(),'Sala actualizada');
  assert.equal(await frame.locator('#roomForm').isVisible(),false);

  // A failed obsolete poll must not report a disconnection after a fresh update.
  holdStatus=true;
  await frame.evaluate(()=>{window.pendingWatchRefresh=refresh();});
  for(let i=0;!heldStatus&&i<100;i++)await new Promise(resolve=>setTimeout(resolve,10));
  assert.ok(heldStatus);
  socket.send(JSON.stringify({type:'status',data:{...snapshot,room:{...snapshot.room,name:'Seguimos conectados'}}}));
  await frame.locator('#roomTitle').filter({hasText:'Seguimos conectados'}).waitFor();
  await heldStatus.fulfill({status:503,json:{error:'Unavailable'}});heldStatus=null;
  await frame.evaluate(()=>window.pendingWatchRefresh);
  assert.equal(await frame.locator('#connectionText').textContent(),'Listo para compartir');
  assert.equal(await frame.locator('#retryConnection').isVisible(),false);

  // Notifications without a full snapshot also require a post-event request.
  for(const notification of [{type:'media',data:{}},{type:'playback',data:{roomId:'other-room',mediaId:'other-media'}}]){
    holdStatus=true;
    await frame.evaluate(()=>{window.pendingWatchRefresh=refresh();});
    for(let i=0;!heldStatus&&i<100;i++)await new Promise(resolve=>setTimeout(resolve,10));
    assert.ok(heldStatus);
    const previous=structuredClone(snapshot);
    snapshot={...snapshot,room:{...snapshot.room,name:`Actualizada por ${notification.type}`}};
    const revision=await frame.evaluate(()=>statusRevision);
    socket.send(JSON.stringify(notification));
    // A round trip through the browser ensures the notification was dispatched
    // before the old response is released.
    await frame.waitForFunction(previousRevision=>statusRevision>previousRevision,revision,{timeout:1000});
    await heldStatus.fulfill({json:previous});heldStatus=null;
    await frame.locator('#roomTitle').filter({hasText:snapshot.room.name}).waitFor({timeout:1500});
  }

  // Repeated submissions keep one connection operation and retain failed input.
  // Its completion must request fresh state even if a previous poll is pending.
  holdStatus=true;
  await frame.evaluate(()=>{window.pendingWatchRefresh=refresh();});
  for(let i=0;!heldStatus&&i<100;i++)await new Promise(resolve=>setTimeout(resolve,10));
  assert.ok(heldStatus);
  const beforeConnect=structuredClone(snapshot),beforeStatusRequests=statusRequests;
  await frame.locator('#joinDetails summary').click();
  await frame.locator('#tokenInput').fill('test-invitation');
  await frame.locator('#tokenForm button').click();
  await frame.locator('#tokenForm[aria-busy="true"]').waitFor();
  await frame.evaluate(()=>{
    document.getElementById('tokenForm').dispatchEvent(new Event('submit',{cancelable:true}));
    document.getElementById('tokenForm').dispatchEvent(new Event('submit',{cancelable:true}));
  });
  for(let i=0;!heldToken&&i<100;i++)await new Promise(resolve=>setTimeout(resolve,10));
  assert.ok(heldToken);
  assert.equal(tokenRequests,1);
  assert.equal(await frame.locator('#tokenInput').evaluate(el=>el.readOnly),true);
  snapshot={...snapshot,peers:[{peerId:'friend',name:'Contacto conectado',connected:true,inRoom:false}]};
  await heldToken.fulfill({json:{success:true}});
  await frame.locator('#toast').filter({hasText:'Dispositivo conectado.'}).waitFor();
  await heldStatus.fulfill({json:beforeConnect});heldStatus=null;
  await frame.locator('#tokenForm[aria-busy="false"]').waitFor();
  await frame.locator('.peer-name').filter({hasText:'Contacto conectado'}).waitFor();
  assert.ok(statusRequests>beforeStatusRequests,'An action must request state newer than an existing poll');
  assert.equal(await frame.locator('#tokenInput').inputValue(),'');
  rejectToken=true;
  await frame.locator('#tokenInput').fill('invalid-invitation');
  await frame.locator('#tokenForm button').click();
  await frame.locator('#toast.error').filter({hasText:'La invitación no es válida.'}).waitFor();
  await frame.locator('#tokenForm[aria-busy="false"]').waitFor();
  assert.equal(await frame.locator('#tokenInput').inputValue(),'invalid-invitation');
  assert.equal(await frame.locator('#tokenInput').evaluate(el=>el.readOnly),false);
  assert.equal(await frame.locator('#tokenForm button').isEnabled(),true);
  assert.deepEqual(errors,[]);
  console.log('OK Watch state: stale snapshots and failures, single connection submission, recoverable invitation errors.');
} finally { await closeBrowserFixture(browser,server); }
