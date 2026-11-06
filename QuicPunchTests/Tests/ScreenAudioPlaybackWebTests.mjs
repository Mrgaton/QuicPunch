// Render production PCM buffering with deterministic packet jitter. No audio device is opened.
import assert from 'node:assert/strict';
import {ScreenAudioBuffer} from '../../QuicNet.Shared/wwwroot/screen-audio-buffer.js';

function signal(start,length){return Float32Array.from({length},(_,i)=>.5*Math.sin(2*Math.PI*440*(start+i)/48000));}
function simulate(outputRate=48000,stall=false){
  const queue=new ScreenAudioBuffer(outputRate), packets=[];
  let sent=0,arrival=0,index=0;
  while(sent<480000){
    const size=[960,1024,480,1536][index%4],jitter=[0,35,0,10,45,0,20,0][index%8];
    const time=sent/48;
    arrival=Math.max(arrival,time+jitter+(stall&&time>=1000&&time<1300?300:0));
    packets.push({at:arrival,samples:signal(sent,size)});sent+=size;index++;
  }
  let next=0,maxBuffered=0;
  const output=new Float32Array(Math.ceil(outputRate*10.6/128)*128);
  for(let frame=0;frame<output.length;frame+=128){
    while(next<packets.length&&packets[next].at<=frame*1000/outputRate){queue.push(packets[next++].samples);maxBuffered=Math.max(maxBuffered,queue.bufferedFrames);}
    queue.render(output.subarray(frame,frame+128));
  }
  assert.ok(maxBuffered<=queue.maximumFrames,'Network bursts accumulated unbounded delay');
  assert.ok(output.every(Number.isFinite),'Invalid playback samples');
  let maxJump=0,zeroRun=0,longestSilence=0,power=0,crossings=0;
  const from=Math.round(outputRate*.25),to=Math.round(outputRate*9.9);
  for(let i=from;i<to;i++){
    maxJump=Math.max(maxJump,Math.abs(output[i]-output[i-1]));
    zeroRun=Math.abs(output[i])<.000001?zeroRun+1:0;longestSilence=Math.max(longestSilence,zeroRun);
    power+=output[i]*output[i];if(output[i]>=0&&output[i-1]<0)crossings++;
  }
  assert.ok(maxJump<.06,`Audible discontinuity between packets: ${maxJump}`);
  if(!stall){
    assert.ok(longestSilence<4,`Routine packet jitter caused an audible gap: ${longestSilence} samples`);
    assert.ok(Math.abs(Math.sqrt(power/(to-from))-Math.SQRT1_2*.5)<.01,'Waveform was attenuated or clipped');
    assert.ok(Math.abs(crossings/((to-from)/outputRate)-440)<2,'Clock handling changed the pitch');
  }
  else assert.ok(longestSilence>outputRate*.05,'The long outage was not exercised');
  return {maxJump,maxBuffered,longestSilence};
}
for(const rate of [44100,48000,96000])simulate(rate);
simulate(48000,true);
const queue=new ScreenAudioBuffer();queue.push(new Float32Array(100000).fill(.5));
assert.equal(queue.bufferedFrames,queue.targetFrames,'Oversized burst was not trimmed to fresh audio');
const block=new Float32Array(128);queue.render(block);queue.reset();
for(let i=0;i<5;i++)queue.render(block);
assert.ok(block.every(value=>value===0),'Reset retained old playback');
assert.equal(queue.bufferedFrames,0);
console.log('PASS screen audio playout: mixed packet sizes, 45ms jitter, 300ms outage/rebuffer, bounded bursts, smooth transitions, stable pitch/volume, 44.1/48/96kHz and reset');
