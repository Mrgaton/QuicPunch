import assert from 'node:assert/strict';
import {mkdirSync,readFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import {launchBrowser, startPreviewServer, closeBrowserFixture, findFfmpeg} from '../Fixtures/BrowserFixture.mjs';

const server=startPreviewServer([]);
let browser;
try {
  const url=await server.ready;
  browser=await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:1050}});
  const errors=[],requests=[];
  mkdirSync('.artifacts/ui-review',{recursive:true});
  const video='.artifacts/ui-review/watch-fixture.mp4';
  const encoded=spawnSync(findFfmpeg(),['-y','-loglevel','error','-f','lavfi','-i','color=c=black:s=160x90:r=10','-t','3','-c:v','libx264','-pix_fmt','yuv420p','-movflags','+faststart',video],{windowsHide:true});
  assert.equal(encoded.status,0,encoded.stderr?.toString());
  await page.route('**/watch/media/**',route=>{
    const bytes=readFileSync(video),range=route.request().headers().range?.match(/bytes=(\d+)-(\d*)/);
    const start=range?Number(range[1]):0,end=range&&range[2]?Number(range[2]):bytes.length-1;
    return route.fulfill({status:range?206:200,contentType:'video/mp4',headers:{'Accept-Ranges':'bytes',...(range?{'Content-Range':`bytes ${start}-${end}/${bytes.length}`}:{})},body:bytes.subarray(start,end+1)});
  });
  page.on('pageerror',e=>errors.push(e.message));
  let snapshot={local:{peerId:'local',name:'Mi dispositivo',token:'INVITACION-DE-PRUEBA'},peers:[{peerId:'friend',name:'Ana · Una amiga con un nombre largo',connected:true,inRoom:false}],room:null,canPickMedia:true,canSelectPath:false};
  let cancelPicker=true, releasePicker;
  await page.routeWebSocket(/\/watch\/ws/,ws=>{ ws.onMessage(()=>{}); });
  await page.route('**/watch/api/**',async route=>{
    const path=new URL(route.request().url()).pathname;
    const body=route.request().postDataJSON();
    requests.push({path,body});
    let result={success:true};
    if(path.endsWith('/status'))result=snapshot;
    if(path.endsWith('/room/create')){
      snapshot.room={roomId:'room',name:body.name,isOwner:true,myRole:'Owner',participants:[{peerId:'local'},{peerId:'friend'}],media:null};
      snapshot.peers[0].inRoom=true;snapshot.peers[0].role='Viewer';result=snapshot;
    }
    if(path.endsWith('/media/pick')){
      await new Promise(resolve=>{releasePicker=resolve;});
      result=cancelPicker?{success:false,cancelled:true}:{success:false,error:'No hay espacio libre suficiente.'};
    }
    if(path.endsWith('/room/leave')){snapshot.room=null;snapshot.peers[0].inRoom=false;result=snapshot;}
    await route.fulfill({contentType:'application/json',body:JSON.stringify(result)});
  });
  await page.goto(url+'watch.html');
  const frame=page.frameLocator('#watchFrame');
  await frame.locator('#connectionText').filter({hasText:'Listo para compartir'}).waitFor();
  await frame.locator('#startWatching').click();
  assert.equal(await frame.locator('#roomName').evaluate(el=>el===document.activeElement),true);
  await frame.locator('.peer-check').check();
  await frame.locator('#roomName').fill('Viernes de cine');
  await frame.locator('#roomName').press('Enter');
  await frame.locator('#roomTitle').filter({hasText:'Viernes de cine'}).waitFor();
  assert.deepEqual(requests.find(r=>r.path.endsWith('/room/create')).body,{name:'Viernes de cine',peerIds:['friend']});
  assert.equal(await frame.locator('#pathDetails').isVisible(),false,'Android must not ask for a filesystem path');
  await frame.locator('#pickMedia').click();
  await frame.locator('#pickMedia[aria-busy="true"]').waitFor();
  assert.equal(await frame.locator('#pickMedia').isDisabled(),true);
  while(!releasePicker)await new Promise(resolve=>setTimeout(resolve,20));
  releasePicker();
  await frame.locator('#pickMedia[aria-busy="false"]').waitFor();
  assert.equal(await frame.locator('#toast.error').count(),0,'Cancelling the Android picker is not an error');
  cancelPicker=false;releasePicker=null;
  await frame.locator('#pickMedia').click();
  while(!releasePicker)await new Promise(resolve=>setTimeout(resolve,20));
  releasePicker();
  await frame.locator('#toast.error').filter({hasText:'No hay espacio'}).waitFor();
  assert.equal(await frame.locator('#pickMedia').isDisabled(),false);

  snapshot.room.media={mediaId:'movie',name:'Película de prueba.mp4',size:readFileSync(video).length};
  snapshot.room.playback={roomId:'room',mediaId:'movie',paused:true,positionSeconds:.5,playbackRate:1,revision:1,issuedAtUnixMs:Date.now(),applyAtUnixMs:Date.now()};
  await frame.locator('body').evaluate(()=>refresh());
  const watch=page.frames().find(f=>f.url().includes('/watch/'));
  await watch.waitForFunction(()=>player.readyState>=1&&Math.abs(player.currentTime-.5)<.1,null,{timeout:5000}).catch(async error=>{
    throw new Error(error.message+' '+JSON.stringify(await watch.evaluate(()=>({ready:player.readyState,time:player.currentTime,duration:player.duration,seekable:player.seekable.length,src:player.src,error:player.error?.message,state,latestPlayback}))));
  });
  assert.equal(await frame.locator('#emptyPlayer').isVisible(),false);
  assert.equal(await frame.locator('#fullscreenBtn').isDisabled(),false);
  // Autoplay policies must leave an explicit, usable recovery action.
  await frame.locator('#player').evaluate(el=>{window.nativeWatchPlay=el.play;el.play=()=>Promise.reject(new DOMException('User gesture required','NotAllowedError'));});
  snapshot.room.playback={...snapshot.room.playback,paused:false,revision:2,issuedAtUnixMs:Date.now(),applyAtUnixMs:Date.now()};
  await frame.locator('body').evaluate(()=>refresh());
  await frame.locator('#resumePlayback').waitFor({state:'visible'});
  await frame.locator('#player').evaluate(el=>{el.play=window.nativeWatchPlay;});
  await frame.locator('#resumePlayback').click();
  await watch.waitForFunction(()=>!player.paused);
  snapshot.room.playback={...snapshot.room.playback,paused:true,revision:3};
  snapshot.room.isOwner=false;snapshot.room.myRole='Viewer';
  await frame.locator('body').evaluate(()=>refresh());
  assert.equal(await frame.locator('#pickMedia').isDisabled(),true);
  await watch.waitForFunction(()=>!applying);
  const previous=requests.filter(r=>r.path.endsWith('/playback')).length;
  await frame.locator('#player').evaluate(async el=>{el.dispatchEvent(new Event('play'));el.dispatchEvent(new Event('pause'));await new Promise(resolve=>setTimeout(resolve,200));});
  assert.equal(requests.filter(r=>r.path.endsWith('/playback')).length,previous,'Viewers cannot send control commands');
  snapshot.room.myRole='Controller';
  await frame.locator('body').evaluate(()=>refresh());
  await watch.waitForFunction(()=>!applying);
  await watch.evaluate(()=>sendPlayback(true));
  assert.equal(requests.filter(r=>r.path.endsWith('/playback')).length,previous+1,'Controllers can send control commands');
  await frame.locator('#leaveRoom').click();
  await frame.locator('#createRoom').waitFor();
  await frame.locator('#toast').evaluate(el=>el.className='toast');
  for(const [width,height] of [[1440,1050],[1920,1080],[768,1024],[390,844],[320,740]]){
    await page.setViewportSize({width,height});
    const overflow=await frame.locator('body').evaluate(()=>document.documentElement.scrollWidth>innerWidth+1);
    assert.equal(overflow,false,`Watch overflows at ${width}px`);
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),false,`Shell overflows at ${width}px`);
    const button=await frame.locator('#startWatching').boundingBox();
    assert.ok(button.height>=44,'Touch targets must be at least 44px high');
    if(width===1440||width===390)await page.screenshot({path:`.artifacts/ui-review/watch-${width}.png`,fullPage:true});
  }
  await page.setViewportSize({width:1440,height:1050});
  await page.locator('.nav-link[data-page=dashboard]').click();
  await page.screenshot({path:'.artifacts/ui-review/home-1440.png',fullPage:true});
  await page.locator('.nav-link[data-page=watch]').click();
  assert.equal(await frame.locator('#connectionText').textContent(),'Listo para compartir');
  assert.deepEqual(errors,[]);
  console.log('OK Watch room: Android capabilities, creation, selection, cancellation, errors, viewer permissions, responsive shell and navigation.');
} finally { await closeBrowserFixture(browser,server); }
