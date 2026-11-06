import {readFileSync} from 'node:fs';
import assert from 'node:assert/strict';
import {Script, createContext} from 'node:vm';
import { readStillFrame, muxAvifSequence } from '../../QuicNet.Shared/wwwroot/avatar-avif-sequence.js';
import { MAX_AVATAR_BYTES, checkDimensions } from '../../QuicNet.Shared/wwwroot/avatar-media.js';
const still = readFileSync(new URL('../Fixtures/profile-avatar.avif', import.meta.url));
const frame = readStillFrame(still);
assert.equal(frame.width, 512);
assert.equal(MAX_AVATAR_BYTES, 512 * 1024);
const storeSource = readFileSync(new URL('../../QuicNet.Shared/Settings/ProfileAvatarStore.cs',import.meta.url),'utf8');
assert.match(storeSource,/MaxBytes\s*=\s*512\s*\*\s*1024/,'Frontend and server must agree on the byte budget');
const sequence = new Uint8Array(muxAvifSequence([0,1,2,3].map(() => ({...frame,duration:250}))));
assert.equal(new TextDecoder().decode(sequence.subarray(8,12)), 'avis', 'Animation must use the sequence brand');
const text = new TextDecoder('latin1').decode(sequence);
assert.ok(text.includes('moov') && text.includes('stts') && text.includes('elst'), 'Track timing and repeat edits must survive muxing');
assert.ok(sequence.length < MAX_AVATAR_BYTES);
assert.throws(() => muxAvifSequence([{...frame,duration:100}]));
assert.throws(() => muxAvifSequence(Array.from({length:121},()=>({...frame,duration:100}))));
assert.throws(() => muxAvifSequence([{...frame,duration:6000},{...frame,duration:6000}]), /10 segundos/);
assert.throws(() => muxAvifSequence([{...frame,duration:0},{...frame,duration:100}]));
assert.throws(() => muxAvifSequence([{...frame,duration:100},{...frame,width:256,duration:100}]), /tamaño/);
assert.throws(() => readStillFrame(new Uint8Array([0,0,0,10])));
assert.throws(() => checkDimensions(20000,20000));
assert.throws(() => checkDimensions(0,100));
checkDimensions(1920,1080);
console.log('PASS profile media: AVIF sequence, square dimensions, duration/frame bounds, loop metadata and 512 KiB policy');

// Rebuilding telemetry rows must keep the same image element, preserving animation playback.
class Element {
  constructor(tag='div') { this.tag=tag; this.children=[]; this.parentElement=null; this.value=''; }
  set textContent(value) { this.replaceChildren(); this.value=value; }
  get textContent() { return this.value; }
  replaceChildren(...items) { for(const old of this.children)old.parentElement=null;this.children=[];for(const item of items){if(item.parentElement)item.parentElement.children=item.parentElement.children.filter(v=>v!==item);item.parentElement=this;this.children.push(item);} }
  getAttribute(name) { return this[name] || null; }
  addEventListener() {}
  querySelector(tag) { return this.children.find(v=>v.tag===tag) || null; }
}
const runtime=createContext({document:{createElement:tag=>new Element(tag),addEventListener(){}},window:{addEventListener(){}},navigator:{},location:{pathname:'/settings.html'}});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/app.js',import.meta.url),'utf8')).runInContext(runtime);
const peer={id:'4f3a5a1b-d319-476d-ad5d-561491689369',name:'Test'};
const snapshot={avatars:{[peer.id]:'a'.repeat(64)}};
const sidebarHost=new Element();
runtime.window.QP.paintAvatar(sidebarHost,peer,snapshot);
const sidebarImage=sidebarHost.children[0];
runtime.window.QP.paintAvatar(sidebarHost,peer,snapshot);
assert.equal(sidebarHost.children[0],sidebarImage,'Default slots must preserve the current image without a cache key');
const firstHost=new Element(), secondHost=new Element();
runtime.window.QP.paintAvatar(firstHost,peer,snapshot,'chat-peer');
const firstImage=firstHost.children[0];
runtime.window.QP.paintAvatar(secondHost,peer,snapshot,'chat-peer');
assert.equal(secondHost.children[0],firstImage,'Telemetry must reuse the existing animated image');
runtime.window.QP.paintAvatar(secondHost,{...peer,isTor:true},snapshot,'chat-peer');
assert.equal(secondHost.children.length,0,'Tor must never show the public avatar');
console.log('PASS animated avatar rendering: stable playback across row updates and Tor isolation');
