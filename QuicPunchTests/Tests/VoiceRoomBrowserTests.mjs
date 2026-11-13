import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Isolated UI fixtures. No microphone, camera, screen or peer connection is opened.
import assert from 'node:assert/strict';
import {readFileSync, mkdirSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const bridge=`window.testRoom={
    async setup(count=2){
      for(const id of [...activeScreenStreams.keys()])removeScreenTile(id);
      const base=await QP.getStatus();
      const peers=['Ana García','Mateo','Sofía · Un nombre de contacto especialmente largo','Daniel'].map((name,i)=>({id:'10000000-0000-0000-0000-00000000000'+i,name,isTrusted:true,isSaved:true,isQuicConnected:true,ping:18+i*7}));
      voiceStatus={...base,node:{...base.node,id:'20000000-0000-0000-0000-000000000000'},peers,savedPeers:peers,activeVoiceCalls:peers.slice(0,count).map(p=>({peerId:p.id,peerName:p.name}))};
      window.roomFixtureStatus=voiceStatus;
      if(!window.roomFixtureHeartbeat){window.roomFixtureHeartbeat=true;QP.on('status_updated',()=>{voiceStatus={...roomFixtureStatus,activeVoiceCalls:[...voiceState.activeCalls]};renderVoice();});}
      voiceState={isMuted:false,isDeafened:false,masterVolume:1,captureRunning:true,playbackRunning:true,backendName:'Audio del dispositivo',activeCalls:voiceStatus.activeVoiceCalls.map(c=>({...c,volume:1,muted:false,screenShareAllowed:true}))};
      participantCards.clear();document.getElementById('activeCalls').replaceChildren();setVoiceDrawer(false,'contacts');
      window.roomRequests=[];QP.toast=()=>{};
      if(!window.roomOriginalApi)window.roomOriginalApi=QP.api;
      QP.api=async(path,options)=>{
        if(path.startsWith('/api/voice')){
          const body=JSON.parse(options?.body||'{}');roomRequests.push({path,body});
          if(path==='/api/voice-hangup'){voiceStatus.activeVoiceCalls=voiceStatus.activeVoiceCalls.filter(c=>c.peerId!==body.peerId);voiceState.activeCalls=voiceState.activeCalls.filter(c=>c.peerId!==body.peerId);}
          if(path==='/api/voice/state')return voiceState;
          if(path.startsWith('/api/voice/native-video')&&(!options||!options.method))return {local:{available:true,engine:'FFmpeg fixture',decoderMask:2,hardwareMask:0,decoders:[]},peers:[],streams:[]};
          if(path==='/api/voice/codec-settings') {
            voiceState.requestedOpusBitrate=body.bitrate;voiceState.opusBitrate=body.bitrate;
            return {success:true,requestedBitrate:body.bitrate,bitrate:body.bitrate};
          }
          if(path.startsWith('/api/voice/media-capabilities'))return {nativeCaptureAvailable:true,nativeEncoders:[{Id:'libx264',Codec:'h264',Available:true,Hardware:false}],displays:[],sources:[]};
          if(path.includes('/mute'))return {success:true,isMuted:body.muted};
          if(path.includes('/deafen'))return {success:true,isDeafened:body.deafened};
          return {success:true,Success:true,Code:'Requested'};
        }
        return roomOriginalApi(path,options);
      };
      renderVoice();
    },render:renderVoice,vad:onVoiceVad,state:patch=>onVoiceStateUpdated({...voiceState,...patch}),settings:()=>openScreenSettingsModal('audio'),
    stream(){createOrUpdateScreenTile('10000000-0000-0000-0000-000000000000','Ana · Pantalla',1280,720,1,30,false);},
    localStream(){createOrUpdateScreenTile('local','Tú · Pantalla',1280,720,1,30,true);},
    photos(version='a',isTor=false){roomFixtureStatus.avatars={[voiceStatus.peers[0].id]:version.repeat(64),[voiceStatus.node.id]:'b'.repeat(64)};voiceStatus.peers[0].isTor=isTor;QP.emit('status_updated',{...voiceStatus,avatars:roomFixtureStatus.avatars});},
    clearStreams(){for(const id of [...activeScreenStreams.keys()])removeScreenTile(id);},
    many(){voiceStatus.activeVoiceCalls=voiceStatus.peers.map(p=>({peerId:p.id,peerName:p.name}));renderVoice();}
  };})();`;
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,bridge)}));
  await page.route('**/api/profile/avatar/**',route=>route.fulfill({contentType:'image/avif',body:readFileSync(new URL('../Fixtures/profile-avatar.avif',import.meta.url))}));
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.testRoom);await page.evaluate(()=>testRoom.setup());
  assert.equal(await page.locator('.voice-participant').count(),3);
  const sizes=await page.locator('.voice-ctrl-btn:not(.hidden)').evaluateAll(buttons=>buttons.map(b=>({w:b.getBoundingClientRect().width,h:b.getBoundingClientRect().height})));
  assert.ok(sizes.every(size=>size.w===sizes[0].w&&size.h===sizes[0].h),'Call controls must have uniform dimensions: '+JSON.stringify(sizes));
  const summary=page.locator('.voice-person-menu > summary').first();await summary.click();
  const menu=page.locator('.voice-person-menu[open]');assert.equal(await menu.count(),1);
  const slider=menu.locator('input[type=range]');await slider.focus();
  await slider.evaluate(node=>{window.roomSlider=node;node.value='135';node.dispatchEvent(new Event('input',{bubbles:true}));});
  await page.evaluate(()=>testRoom.render());
  assert.ok(await page.evaluate(()=>roomSlider===document.querySelector('.voice-person-menu[open] input')),'Status updates replaced the volume control');
  assert.equal(await slider.inputValue(),'135');
  await menu.getByRole('button',{name:'Silenciar a esta persona',exact:true}).click();
  assert.equal(await menu.getByRole('button',{name:'Silenciar a esta persona',exact:true}).getAttribute('aria-pressed'),'true');
  await page.keyboard.press('Escape');assert.equal(await page.locator('.voice-person-menu[open]').count(),0);
  assert.ok(await summary.evaluate(node=>node===document.activeElement));
  await page.click('#voicePanelMicBtn');assert.equal(await page.locator('#voicePanelMicBtn').getAttribute('aria-pressed'),'true');
  await page.evaluate(()=>testRoom.vad({peerId:'10000000-0000-0000-0000-000000000000',speaking:true}));
  assert.equal(await page.locator('.voice-participant.is-speaking').count(),1);
  mkdirSync(fileURLToPath(new URL('../../.artifacts/voice-room/',import.meta.url)),{recursive:true});
  // Local VAD must illuminate the whole self tile, persist through status updates and clear on mute/end.
  await page.click('#voicePanelMicBtn');
  const self=page.locator('.voice-self-tile');
  const localVad=speaking=>page.evaluate(speaking=>QP.emit('voice_vad',{isLocal:true,speaking,level:speaking?.5:0}),speaking);
  await localVad(true);
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),true);
  assert.equal(await page.locator('.voice-participant.is-speaking').count(),2);
  assert.equal(await self.locator('.voice-self-status').textContent(),'Hablando');
  await page.evaluate(()=>testRoom.render());
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),true,'Status refresh lost local speech');
  await page.screenshot({path:'.artifacts/voice-room/local-speaking.png'});
  await localVad(false);
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),false);
  await localVad(true);await page.click('#voicePanelMicBtn');await localVad(true);
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),false,'Muted microphone must not indicate transmitted speech');
  assert.equal(await self.locator('.voice-self-status').textContent(),'Micrófono silenciado');
  await page.click('#voicePanelMicBtn');
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),false,'Unmute revived old speech');
  await localVad(true);await page.evaluate(()=>testRoom.state({captureRunning:false}));
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),false);
  await page.evaluate(()=>testRoom.state({captureRunning:true}));await localVad(true);
  await page.evaluate(()=>testRoom.setup(0));await page.evaluate(()=>testRoom.setup());
  assert.equal(await self.evaluate(node=>node.classList.contains('is-speaking')),false,'New call revived old speech');
  // Stream information uses real profile images and refreshes them without replacing media or restarting avatars.
  await page.evaluate(()=>{testRoom.photos();testRoom.stream();testRoom.localStream();});
  const remoteTile=page.locator('.screen-tile:not(.is-local-stream)'),localTile=page.locator('.screen-tile.is-local-stream');
  assert.equal(await page.locator('.voice-participant:visible').count(),1,'A sharing participant must only have one visible tile');
  assert.equal(await page.locator('#screenShareGrid > .voice-participant:visible').count(),1,'Audio participants share the same grid as video');
  assert.equal(await remoteTile.locator('.screen-tile-name').isVisible(),true,'Identity is independent of codec details');
  assert.equal(await remoteTile.locator('.screen-tile-codec').isVisible(),false);
  await page.evaluate(()=>{testRoom.vad({peerId:'10000000-0000-0000-0000-000000000000',speaking:true});testRoom.vad({isLocal:true,speaking:true,level:.5});});
  assert.equal(await page.locator('.screen-tile.is-speaking').count(),2,'Local and remote speech must highlight their stream');
  await remoteTile.locator('.voice-person-menu > summary').click();
  assert.ok(await remoteTile.getByRole('button',{name:'Silenciar a esta persona',exact:true}).isVisible(),'Participant controls must move into the stream');
  await page.keyboard.press('Escape');
  await page.evaluate(()=>{testRoom.vad({peerId:'10000000-0000-0000-0000-000000000000',speaking:false});testRoom.vad({isLocal:true,speaking:false,level:0});testRoom.render();});
  assert.equal(await page.locator('.screen-tile.is-speaking').count(),0);
  await page.focus('#voiceRoomMedia');
  await page.mouse.move(20,20);
  await page.waitForFunction(()=>document.getElementById('view-voice').classList.contains('is-media-idle'));
  assert.equal(await remoteTile.locator('.screen-tile-name').isVisible(),true,'Native video identity remains visible');
  assert.equal(await remoteTile.locator('.screen-tile-actions').isVisible(),true,'Native controls remain accessible without hover');
  await page.locator('#voiceRoomMedia').hover();
  assert.equal(await remoteTile.locator('.screen-tile-name').isVisible(),true,'Pointer activity restores names');
  await page.keyboard.press('Tab');
  for(const tile of [remoteTile,localTile]){
    await tile.locator('.screen-info-btn').click();
    await tile.locator('.screen-tile-user img').waitFor();
    await page.waitForFunction(()=>[...document.querySelectorAll('.show-stream-info .screen-tile-user img')].every(img=>img.complete&&img.naturalWidth>0));
  }
  assert.match(await remoteTile.locator('.screen-tile-user img').getAttribute('src'),/10000000-0000-0000-0000-000000000000\?v=a{64}$/);
  assert.match(await localTile.locator('.screen-tile-user img').getAttribute('src'),/20000000-0000-0000-0000-000000000000\?v=b{64}$/);
  await page.evaluate(()=>{window.streamPhoto=document.querySelector('.screen-tile:not(.is-local-stream) .screen-tile-user img');window.streamMedia=document.querySelector('.screen-tile:not(.is-local-stream) .screen-tile-body');testRoom.stream();testRoom.render();});
  assert.ok(await page.evaluate(()=>streamPhoto===document.querySelector('.screen-tile:not(.is-local-stream) .screen-tile-user img')),'Stream metadata restarted the profile image');
  await page.screenshot({path:'.artifacts/voice-room/stream-profile-photos.png'});
  await page.evaluate(()=>testRoom.photos('c'));
  assert.match(await remoteTile.locator('.screen-tile-user img').getAttribute('src'),/v=c{64}$/);
  assert.ok(await page.evaluate(()=>streamMedia===document.querySelector('.screen-tile:not(.is-local-stream) .screen-tile-body')),'Profile update replaced video media');
  await page.evaluate(()=>testRoom.photos('c',true));
  assert.equal(await remoteTile.locator('.screen-tile-user img').count(),0,'Tor profile visibility must follow the shared avatar rules');
  await page.evaluate(()=>testRoom.photos('c',false));
  await page.route('**/api/profile/avatar/**',route=>route.fulfill({status:404,body:''}));
  await page.evaluate(()=>testRoom.photos('d'));
  await remoteTile.locator('.screen-tile-user img').waitFor({state:'detached'});
  assert.match(await remoteTile.locator('.screen-tile-user .avatar').textContent(),/AG/);
  await page.unroute('**/api/profile/avatar/**');
  await page.evaluate(()=>{testRoom.clearStreams();});
  assert.equal(await page.locator('.voice-participant:visible').count(),3,'Stopping video must restore participant cards');
  assert.equal(await page.locator('#activeCalls .voice-person-menu').count(),2,'Participant controls return when video ends');
  const settle=()=>page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  async function assertRoomFits(label){
    await settle();
    const bounds=await page.evaluate(()=>{
      const box=selector=>document.querySelector(selector).getBoundingClientRect();
      const dock=box('.voice-dock'),nav=box('.nav-wrap'),media=box('#voiceRoomMedia'),head=box('.voice-stage-head'),controls=box('.voice-quick-controls');
      const drawer=box('#voiceDrawer');
      return {dockBottom:dock.bottom,dockTop:dock.top,dockLeft:dock.left,dockRight:dock.right,dockWidth:dock.width,limit:innerWidth<=680?nav.top:innerHeight,mediaRight:media.right,mediaBottom:media.bottom,mediaHeight:media.height,headTop:head.top,drawerLeft:drawer.left,drawerRight:drawer.right,drawerWidth:drawer.width,open:!document.getElementById('voiceDrawer').hidden,width:innerWidth,rootHeight:document.scrollingElement.scrollHeight,height:innerHeight,scrollY,overflow:document.documentElement.scrollWidth-innerWidth};
    });
    assert.ok(bounds.dockBottom<=bounds.limit+1&&bounds.dockBottom>=bounds.limit-1,label+': controls must meet navigation edge '+JSON.stringify(bounds));
    assert.ok(bounds.dockRight<=bounds.width&&bounds.dockWidth<=80,label+': controls must form a vertical right rail');
    assert.ok(bounds.mediaHeight>0&&bounds.mediaRight<=bounds.dockLeft+1&&bounds.headTop>=0,label+': media overlaps right controls');
    if(bounds.open){assert.ok(bounds.drawerWidth>0&&bounds.drawerLeft>=0&&Math.abs(bounds.drawerRight-bounds.dockLeft)<1,label+': drawer must open beside the right rail '+JSON.stringify(bounds));if(bounds.width>760)assert.ok(bounds.mediaRight<=bounds.drawerLeft+1,label+': desktop sidebar overlaps media');}
    assert.ok(bounds.rootHeight<=bounds.height+1&&bounds.scrollY===0&&bounds.overflow<=0,label+': outer page scroll/overflow '+JSON.stringify(bounds));
  }
  for(const width of [1920,1280,820,390,320]){
    await page.setViewportSize({width,height:width>760?1000:844});
    for(const theme of ['dark']){
      await page.evaluate(async theme=>{document.documentElement.dataset.theme=theme;await testRoom.setup();},theme);
      await assertRoomFits('closed '+width);
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth),'Horizontal overflow at '+width);
      const dock=await page.locator('.voice-dock').boundingBox();assert.ok(dock.x>=0&&dock.x+dock.width<=width,'Dock outside viewport at '+width);
      await page.locator('.voice-person-menu > summary').first().click();
      const popover=await page.locator('.voice-person-menu[open] .voice-person-popover').boundingBox();
      assert.ok(popover.x>=0&&popover.x+popover.width<=width,'Participant menu outside viewport at '+width);
      await page.keyboard.press('Escape');
      if(width===1280||width===390)await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/voice-room/'+theme+'-'+width+'.png',import.meta.url)),fullPage:true});
      await page.evaluate(()=>testRoom.stream());
      await page.evaluate(()=>{window.roomVideo=document.querySelector('.screen-tile');window.roomVideoChild=roomVideo.querySelector('video,canvas');});
      await page.click('#voiceTrayToggle');
      assert.equal(await page.locator('#voiceTrayToggle').getAttribute('aria-expanded'),'true');
      await assertRoomFits('open '+width);
      await page.click('#voiceSettingsTab');
      assert.equal(await page.locator('#screenShareSettingsModal').getAttribute('role'),'dialog');
      assert.ok(await page.locator('#micSelect').isVisible());
      await page.evaluate(()=>testRoom.render());
      assert.ok(await page.evaluate(()=>roomVideo===document.querySelector('.screen-tile')&&roomVideoChild===roomVideo.querySelector('video,canvas')),'Opening drawer or updating status remounted media');
      if((width===1280||width===390)&&theme==='dark')await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/voice-room/settings-'+width+'.png',import.meta.url)),fullPage:true});
      await page.keyboard.press('Escape');await page.click('#voiceTrayToggle');await page.click('#voiceContactsTab');
      assert.equal(await page.locator('#voiceContactsTab').getAttribute('aria-pressed'),'true',JSON.stringify({width,theme,errors,focus:await page.evaluate(()=>document.activeElement?.id)}));
      if((width===1280||width===390)&&theme==='dark')await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/voice-room/contacts-'+width+'.png',import.meta.url)),fullPage:true});
      await page.keyboard.press('Escape');
      assert.equal(await page.locator('#voiceTrayToggle').getAttribute('aria-expanded'),'false');
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=document.documentElement.clientWidth),'Video layout overflows at '+width);
      if(width===1280&&theme==='dark')await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/voice-room/dark-video-1280.png',import.meta.url)),fullPage:true});
      await page.evaluate(()=>testRoom.clearStreams());
    }
  }
  for(const viewport of [{width:320,height:568},{width:390,height:430},{width:844,height:390}]){
    await page.setViewportSize(viewport);await page.evaluate(()=>testRoom.setup());await assertRoomFits('small closed '+JSON.stringify(viewport));
    await page.click('#voiceTrayToggle');await assertRoomFits('small open '+JSON.stringify(viewport));
    await page.click('#voiceSettingsTab');await assertRoomFits('small settings '+JSON.stringify(viewport));await page.keyboard.press('Escape');
  }
  await page.setViewportSize({width:1280,height:900});await page.evaluate(()=>testRoom.setup());
  await page.click('#voiceTrayToggle');await page.click('#voiceDrawerClose');
  assert.equal(await page.locator('#voiceTrayToggle').getAttribute('aria-expanded'),'false');
  assert.ok(await page.locator('#voiceTrayToggle').evaluate(node=>node===document.activeElement));
  await page.locator('#voiceRoomMedia').hover();await page.mouse.wheel(0,120);
  assert.equal(await page.locator('#voiceDrawer').evaluate(node=>node.hidden),true,'Scrolling media must not open settings');
  await page.click('#voiceScreenSettingsBtn');
  await page.evaluate(()=>QP.navigate('dashboard'));
  assert.equal(await page.locator('#screenShareSettingsModal').getAttribute('role'),'dialog');
  assert.equal(await page.locator('#voiceDrawerSettings #screenShareSettingsModal').count(),0,'Global audio settings must remain usable on other pages');
  await page.evaluate(()=>testRoom.settings());
  assert.ok(await page.locator('#screenShareSettingsModal').isVisible());
  await page.keyboard.press('Escape');
  await page.waitForFunction(()=>document.getElementById('screenShareSettingsModal').classList.contains('hidden'));
  await page.evaluate(()=>QP.navigate('voice'));await page.evaluate(()=>testRoom.setup());
  await page.setViewportSize({width:390,height:844});await page.evaluate(()=>testRoom.setup());
  await page.evaluate(()=>{const banner=document.createElement('div');banner.id='roomTestBanner';banner.className='connection-notice';banner.style.height='90px';document.getElementById('spaContainer').before(banner);});
  await assertRoomFits('live banner');await page.evaluate(()=>document.getElementById('roomTestBanner').remove());
  assert.equal(await page.locator('#voiceTrayToggle').getAttribute('aria-expanded'),'false','Contacts must leave all media space available by default');
  await page.click('#voiceInviteBtn');await page.fill('#voiceContactSearch','Sofía');
  assert.equal(await page.locator('.voice-contact').count(),1);
  await page.fill('#voiceContactSearch','No existe');assert.equal(await page.locator('.voice-contact').count(),0);
  await page.evaluate(()=>testRoom.setup());await page.click('#voiceLeaveBtn');
  await page.waitForFunction(()=>!document.getElementById('voiceCallEmpty').classList.contains('hidden'));
  assert.ok(await page.locator('#voiceLeaveBtn').isDisabled());
  await page.evaluate(()=>testRoom.setup(0));assert.equal(await page.locator('.voice-person-menu').count(),0);
  assert.ok(await page.locator('#voicePanelCameraBtn').isDisabled());
  await page.click('#voiceScreenSettingsBtn');
  assert.ok(await page.locator('#mediaVideoTab').isEnabled());
  assert.equal(await page.locator('#mediaAudioTab').getAttribute('aria-selected'),'true');
  await page.focus('#mediaAudioTab');await page.keyboard.press('ArrowLeft');
  assert.equal(await page.locator('#mediaVideoTab').getAttribute('aria-selected'),'true');
  assert.ok(await page.locator('#screenResolutionSelect').isVisible());
  await page.locator('#screenResolutionSelect').selectOption('720');
  await page.locator('#screenFpsSelect').selectOption('30');
  await page.click('#saveScreenModalBtn');
  await page.locator('#screenShareSettingsModal').waitFor({state:'hidden'});
  assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('qp_screen_config')).resolution),'720');
  await page.click('#voiceScreenSettingsBtn');await page.click('#mediaVideoTab');
  assert.equal(await page.locator('#screenResolutionSelect').inputValue(),'720');
  assert.equal(await page.locator('#screenFpsSelect').inputValue(),'30');
  for (const width of [1440,390,320]) {
    await page.setViewportSize({width,height:844});
    await assertRoomFits('video settings before calling '+width);
    await page.screenshot({path:`.artifacts/voice-room/precall-video-${width}.png`});
  }
  assert.ok(!await page.evaluate(()=>roomRequests.some(r=>/screen-share\/start|voice-call|test-loopback/.test(r.path))), 'Saving preferences must not start capture or a call');
  await page.click('#mediaAudioTab');
  for(const bitrate of ['160000','192000','256000','300000']) {
    await page.locator('#opusBitrateSelect').selectOption(bitrate);
    await page.click('#saveOpusModalBtn');
    await page.locator('#closeScreenModalBtn').click();await page.click('#voiceScreenSettingsBtn');
    assert.equal(await page.locator('#opusBitrateSelect').inputValue(),bitrate);
  }
  assert.deepEqual(errors,[]);
  console.log('PASS call room: local/remote speaking, mute/end resets, local/remote stream profile photos, live avatar changes/fallback/Tor, media continuity, layout, drawers, settings, participant controls, 320–1920px');
} finally {await closeBrowserFixture(browser, server);}
