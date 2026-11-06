import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
// Synthetic tracks/PCM and fake Web Audio only: no microphone or system audio is captured.

import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
let contexts = [], requests = [], packets = [], notices = [], failStart = false, holdStart;
const node = () => ({connect(){},disconnect(){}});
class AudioContext {
  constructor(options){this.options=options;this.destination={};contexts.push(this);}
  createMediaStreamSource(stream){this.stream=stream;return node();}
  createScriptProcessor(){return this.processor={...node()};}
  createGain(){return {...node(),gain:{value:1}};}
  close(){this.closed=true;return Promise.resolve();}
}
const runtime=createContext({document:{addEventListener(){}},navigator:{},console,window:{AudioContext},
  MediaStream:class {constructor(tracks){this.tracks=tracks;}getTracks(){return this.tracks;}},
  QP:{sendBinary:packet=>packets.push(new Uint8Array(packet)),toast:message=>notices.push(message),
    async api(path,options){requests.push({path,body:JSON.parse(options.body)});if(path.endsWith('/start')){if(holdStart)await holdStart;if(failStart)throw Error('Native exclusion failed');}return {success:true};}}
});
const source=readVoiceSource();
new Script(source.replace(/\}\)\(\);\s*$/,`globalThis.audioTest={start:startScreenAudioCapture,stop:stopScreenAudioCapture,capabilities(value){nativeCapabilities={systemAudioExcludesCall:value};}};})();`)).runInContext(runtime);
function stream(surface='monitor',audio=true){
  const track={enabled:true,stopped:false,stop(){this.stopped=true;},addEventListener(event,callback){this[event]=callback;}};
  return {track,getAudioTracks:()=>audio?[track]:[],getVideoTracks:()=>[{getSettings:()=>({displaySurface:surface})}]};
}
runtime.audioTest.capabilities(true);
let media=stream();await runtime.audioTest.start(media,'share-1');
assert.equal(contexts.length,0,'System capture must not use unfiltered browser PCM');
assert.equal(media.track.enabled,false,'Raw system audio remained enabled');
assert.equal(requests.at(-1).path,'/api/voice/screen-share/system-audio/start');
assert.equal(requests.at(-1).body.audioSessionId,'share-1');
runtime.audioTest.stop();await Promise.resolve();
assert.equal(requests.at(-1).path,'/api/voice/screen-share/system-audio/stop');
assert.equal(requests.at(-1).body.audioSessionId,'share-1');
requests=[];await runtime.audioTest.start(stream('monitor',false),'silent');
assert.equal(requests.length,0,'Never start native audio unless sharing audio was selected');
media=stream('browser');await runtime.audioTest.start(media,'tab');
assert.equal(contexts.length,1,'Tab audio should keep its selected source');
contexts[0].processor.onaudioprocess({inputBuffer:{getChannelData:()=>new Float32Array([1,-1,.5,-.5,0])}});
assert.equal(packets[0][0],3);
assert.deepEqual([...new Int16Array(packets[0].slice(1).buffer)],[32767,-32768,16383,-16384,0]);
runtime.audioTest.stop();assert.equal(contexts[0].closed,true);assert.equal(media.track.stopped,true);
requests=[];contexts=[];failStart=true;media=stream();await runtime.audioTest.start(media,'failed');
assert.equal(contexts.length,0,'Failed native exclusion must not silently resume the echo-producing source');
assert.equal(media.track.enabled,false);assert.ok(notices.includes('Native exclusion failed'));
failStart=false;let release;holdStart=new Promise(resolve=>release=resolve);
const pending=runtime.audioTest.start(stream(),'old');runtime.audioTest.stop();
holdStart=null;await runtime.audioTest.start(stream(),'new');release();await pending;
runtime.audioTest.stop();await Promise.resolve();
assert.equal(requests.at(-1).body.audioSessionId,'new','Late completion stole the current audio session');
media=stream();await runtime.audioTest.start(media,'revoked');media.track.ended();await Promise.resolve();
assert.equal(requests.at(-1).body.audioSessionId,'revoked');assert.equal(requests.at(-1).path,'/api/voice/screen-share/system-audio/stop');
console.log('PASS screen audio: desktop process exclusion, selected tab PCM, no-audio consent, failure without echo fallback, stop/restart races and permission revocation');
