import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {attachApp,loadChatDrafts} from '../Fixtures/WebAppRuntime.mjs';
const source=readFileSync(new URL('../../QuicNet.Shared/wwwroot/chat.js',import.meta.url),'utf8');
const input={value:'',style:{}};
const state=new Set();
const document={getElementById:id=>id==='messageInput'?input:id==='chatContainer'?{classList:{toggle:(name,on)=>on?state.add(name):state.delete(name)}}:null};
const context=createContext({document,URLSearchParams,clearTimeout,crypto:{randomUUID},location:{search:'?peer=alice'}});
// Include selection's real helpers/state; do not cut the script at an unrelated declaration.
attachApp(context);
const storedDrafts = new Map();
let releaseAlice;
let aliceSaving;
const pendingAlice = new Promise(resolve => { aliceSaving = resolve; });
context.QP.api = async (path, options) => {
  const peerId = new URL(path, 'http://localhost').searchParams.get('peerId');
  if (!options) return storedDrafts.get(peerId) ?? {text:'', revision:null};
  const {text, revision, nextRevision} = JSON.parse(options.body);
  if (peerId === 'alice' && !releaseAlice) {
    await new Promise(resolve => { releaseAlice = resolve; aliceSaving(); });
  }
  assert.equal(revision, storedDrafts.get(peerId)?.revision ?? null);
  const result = {text, revision:nextRevision};
  storedDrafts.set(peerId, result);
  return result;
};
loadChatDrafts(context);
new Script(source+'\nglobalThis.select = selectChatPeer;').runInContext(context);
input.value='Borrador privado para Alice';
context.select('bob');
assert.equal(input.value,'','A draft for Alice must not appear in Bob’s composer');
input.value='Borrador para Bob';context.select('global');
assert.equal(input.value,'','A private draft must not appear in the public channel');
context.select('alice');assert.equal(input.value,'Borrador privado para Alice');
context.select('bob');assert.equal(input.value,'Borrador para Bob');
input.value='';context.select('alice');context.select('bob');
assert.equal(input.value,'','A sent or cleared draft must not reappear');
await pendingAlice;
assert.equal(input.value, '', 'Alice’s pending save must leave Bob’s composer untouched');
releaseAlice();
await new Promise(resolve => setImmediate(resolve));
assert.equal(storedDrafts.get('alice').text, 'Borrador privado para Alice');
assert.equal(storedDrafts.get('bob').text, '');
assert.equal(input.value, '', 'Alice’s response must not populate Bob’s composer');
const renderPrelude=source.slice(source.indexOf('function renderChat('),source.indexOf('// 1. Public channel'));
assert.ok(!/selectedPeerId\s*=\s*'global'/.test(renderPrelude),'Unavailable contacts must not silently select a public channel');
console.log('PASS conversation privacy: drafts remain per-contact, cleared drafts stay empty, no fallback to public chat');
