import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {attachApp,assertConnectionModes} from '../Fixtures/WebAppRuntime.mjs';

const source=readFileSync(new URL('../../QuicNet.Shared/wwwroot/chat.js',import.meta.url),'utf8');
let now=1000;
const input={value:'',style:{}};
const requests=[];
const alice={id:'alice',name:'Alice',certHash:'alice-hash',isTrusted:true,nostrPubKey:'known',canSendOffline:true};
const bob={id:'bob',name:'Bob',isTrusted:true};
let snapshot={peers:[alice,bob],activeChats:[],mailbox:{connectedRelays:3}};
let implementation=async(path)=>{
  if(path==='/api/status')return snapshot;
  return {success:true};
};
const context=createContext({
  URLSearchParams,location:{search:'?peer=alice'},window:{},console,crypto:{randomUUID},
  Date:class extends Date{static now(){return now;}},
  setTimeout:fn=>{now+=500;queueMicrotask(fn);return 1;},clearTimeout(){},
  document:{addEventListener(){},getElementById:id=>id==='messageInput'?input:null},
  QP:{currentPage:'chat',trusted:s=>(s.peers||[]).filter(p=>p.isTrusted),toast(){},
    async api(path,options){const body=options?.body?JSON.parse(options.body):null;requests.push({path,body});return implementation(path,body);}}
});
assertConnectionModes(attachApp(context));
new Script(source+`
renderChat=()=>{};scrollToBottom=()=>{};
globalThis.test={connectChat,maybeConnectSelectedChat,sendChatMessage,selectChatPeer,
  status:s=>chatStatus=s,select:id=>selectedPeerId=id,
  reset(){connectingPeers.clear();connectionRetryAt.clear();connectionErrors.clear();sendingPeers.clear();},
  pending:()=>connectingPeers.size,failed:id=>connectionErrors.has(id)};
`).runInContext(context);
const t=context.test;
const settle=async()=>{for(let i=0;i<100&&t.pending();i++)await new Promise(r=>setImmediate(r));assert.equal(t.pending(),0);};

// Opening the selected chat requests the existing protocol exactly once, with no manual button.
t.status(snapshot);
let release;
implementation=async(path)=>{
  if(path==='/api/connect-peer')return new Promise(resolve=>release=()=>{snapshot={...snapshot,activeChats:[{peerId:'alice'}]};resolve({success:true});});
  if(path==='/api/status')return snapshot;
  return {success:true};
};
t.maybeConnectSelectedChat();t.maybeConnectSelectedChat();
assert.equal(requests.filter(r=>r.path==='/api/connect-peer').length,1,'Coalesce duplicate connection requests');
release();await settle();t.maybeConnectSelectedChat();
assert.equal(requests.filter(r=>r.path==='/api/connect-peer').length,1,'Reuse an established session');

// An unavailable contact waits before reconnecting and stores explicitly submitted text in the outbox.
t.reset();requests.length=0;snapshot={peers:[alice],activeChats:[],mailbox:{connectedRelays:3}};t.status(snapshot);input.value='No perder';
implementation=async path=>path==='/api/status'?snapshot:{success:true};
await t.connectChat('alice');assert.ok(t.failed('alice'));
const calls=requests.length;t.maybeConnectSelectedChat();assert.equal(requests.length,calls,'Do not loop immediately after timeout');
await t.sendChatMessage();assert.equal(input.value,'');
assert.equal(requests.find(r=>r.path==='/api/chat/outbox').body.message,'No perder');
assert.equal(requests.filter(r=>r.path==='/api/chat-send').length,0,'Known Nostr identity must not bypass the live session requirement');

// Saved contacts are resolved before requesting the existing chat protocol.
t.reset();requests.length=0;snapshot={peers:[],savedPeers:[alice],activeChats:[]};t.status(snapshot);
implementation=async path=>{
  if(path==='/api/connect-saved-peer')snapshot={...snapshot,peers:[alice]};
  if(path==='/api/connect-peer')snapshot={...snapshot,activeChats:[{peerId:'alice'}]};
  return path==='/api/status'?snapshot:{success:true};
};
await t.connectChat('alice');
assert.deepEqual(requests.filter(r=>r.body).map(r=>r.path),['/api/connect-saved-peer','/api/connect-peer']);

// Switching conversations during a send never changes its recipient or clears another draft.
t.reset();requests.length=0;snapshot={peers:[alice,bob],activeChats:[{peerId:'alice'}]};t.status(snapshot);t.select('alice');input.value='Para Alice';
implementation=async()=>new Promise(resolve=>release=()=>resolve({success:true}));
const sending=t.sendChatMessage();t.selectChatPeer('bob');input.value='Borrador de Bob';release();await sending;
assert.equal(requests.find(r=>r.path==='/api/chat/outbox').body.peerId,'alice');
assert.equal(input.value,'Borrador de Bob');t.selectChatPeer('alice');assert.equal(input.value,'');

input.value='Conservar si falla';implementation=async()=>{throw new Error('Disconnected');};
await t.sendChatMessage();assert.equal(input.value,'Conservar si falla');
t.select('global');t.status({chatTransport:{nostrEnabled:false}});requests.length=0;await t.sendChatMessage();assert.equal(requests.length,0,'Paused public chat must not publish');
context.QP.currentPage='dashboard';t.select('alice');t.reset();t.status({peers:[alice],activeChats:[]});t.maybeConnectSelectedChat();assert.equal(requests.length,0,'Do not open chats while browsing other pages');
console.log('PASS automatic chat: single connection, session reuse, saved peers, cooldown, direct-only delivery, draft and recipient safety');
