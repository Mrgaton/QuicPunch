import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Real Web Audio/AudioWorklet rendering of synthetic PCM through the production
// screen-audio WebSocket handler. Offline contexts never play or capture user audio.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage(),errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const bridge=`window.playbackTest={
    entry(){return activeScreenStreams.get('00000000-0000-0000-0000-000000000001');},
    setup(){createOrUpdateScreenTile('00000000-0000-0000-0000-000000000001','Audio sintético',320,180,1,30,false);},
    send(start=0,count=960){const packet=new Uint8Array(17+count*2);packet[0]=4;packet[16]=1;const view=new DataView(packet.buffer);for(let i=0;i<count;i++)view.setInt16(17+i*2,Math.sin(2*Math.PI*440*(start+i)/48000)*16384,true);onWsBinaryReceived(packet.buffer);},
    remove(){removeScreenTile('00000000-0000-0000-0000-000000000001');}
  };})();`;
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,bridge)}));
  await page.addInitScript(()=>{
    window.playbackContexts=[];
    window.AudioContext=class extends OfflineAudioContext {
      constructor(){super(1,24000,48000);this.testClosed=false;playbackContexts.push(this);}
      // Queue packets before starting the offline renderer, as a running realtime
      // context would. Native state/rendering behavior remains inside Web Audio.
      get state(){return this.testClosed?'closed':'running';}
      close(){this.testClosed=true;return Promise.resolve();}
    };
  });
  await page.goto(url+'call.html');await page.waitForFunction(()=>window.playbackTest);
  const rendered=await page.evaluate(async()=>{
    playbackTest.setup();
    playbackTest.send(); // Initialize the lazy player before starting its rendering thread.
    const entry=playbackTest.entry();await entry.audioSetup;
    if(entry.audioFailed)throw Error('Production audio player failed initialization');
    const context=entry.audioContext;
    // Offline rendering runs faster than wall time. Start the synthetic producer
    // only after the worklet has begun processing, then inject the next burst
    // at an audio-clock suspension rather than racing its constructor.
    const started=context.suspend(.02),suspended=context.suspend(.12),rendering=context.startRendering();
    await started;
    for(let i=0;i<10;i++)playbackTest.send(i*960);
    await context.resume();await suspended;
    for(let i=10;i<15;i++)playbackTest.send(i*960);
    await context.resume();const audio=await rendering;
    const samples=audio.getChannelData(0);let power=0,jump=0,zeros=0,longest=0;
    for(let i=2000;i<14000;i++){power+=samples[i]**2;jump=Math.max(jump,Math.abs(samples[i]-samples[i-1]));zeros=Math.abs(samples[i])<.000001?zeros+1:0;longest=Math.max(longest,zeros);}
    return {worklet:entry.audioPlayer.node instanceof AudioWorkletNode,rms:Math.sqrt(power/12000),jump,longest,tail:[...samples.slice(23000)].every(value=>value===0),pending:entry.audioPendingFrames};
  });
  assert.equal(rendered.worklet,true,'Browser did not use the audio rendering thread');
  assert.ok(rendered.rms>.34&&rendered.rms<.36,JSON.stringify(rendered));
  assert.ok(rendered.jump<.06&&rendered.longest<4,JSON.stringify(rendered));
  assert.equal(rendered.tail,true,'Underrun did not fade back to silence');
  assert.equal(rendered.pending,0);
  const cleanup=await page.evaluate(()=>{const entry=playbackTest.entry(),context=entry.audioContext,node=entry.audioPlayer.node;playbackTest.remove();return {closed:context.testClosed,removed:!playbackTest.entry(),player:entry.audioPlayer};});
  assert.deepEqual(cleanup,{closed:true,removed:true,player:null});
  // Test the compatibility player and immediate cleanup during module loading.
  const fallback=await page.evaluate(async()=>{
    const original=window.AudioWorkletNode;window.AudioWorkletNode=undefined;
    playbackTest.setup();playbackTest.send();const entry=playbackTest.entry();await entry.audioSetup;
    const compatible=entry.audioPlayer?.node instanceof ScriptProcessorNode;
    playbackTest.remove();window.AudioWorkletNode=original;
    playbackTest.setup();playbackTest.send();const removed=playbackTest.entry();playbackTest.remove();await removed.audioSetup;
    return {compatible,latePlayer:removed.audioPlayer,closed:removed.audioContext===null};
  });
  assert.deepEqual(fallback,{compatible:true,latePlayer:null,closed:true});
  assert.deepEqual(errors,[]);
  console.log('PASS real browser screen audio: binary PCM delivery, AudioWorklet modules, continuous offline waveform, refill, underrun fade, bounded startup queue, compatibility playback and removal while loading');
} finally {await closeBrowserFixture(browser, server);}
