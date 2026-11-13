import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
import {attachApp} from '../Fixtures/WebAppRuntime.mjs';

const source = readFileSync(new URL('../../QuicNet.Shared/wwwroot/chat.js', import.meta.url), 'utf8');
let resolveRequest;
const context = createContext({
  console,
  URLSearchParams, location: {search: ''}, window: {},
  document: {addEventListener() {}, querySelectorAll() {return []; }},
  QP: {toast() {}, api() {return new Promise(resolve => {resolveRequest = resolve; });}}
});
attachApp(context);
new Script(source + `
renderChat = () => {};
globalThis.fileTest = {
  peer(value) {selectedPeerId = value;},
  read(peer, id) {return fileTransfers.get(fileTransferKey(id, peer));},
  downloadChatFile, cancelChatFile, pauseChatFile, onRealtimeFileProgress, onRealtimeFileCancelled, onRealtimeFileComplete, onRealtimeFilePaused, onRealtimeFileVerifying,
  createFileCardDom, loadConversation
};
`).runInContext(context);
const api = context.fileTest;
api.peer('alice');
const download = api.downloadChatFile('same-id', 'file.bin', 3);
api.peer('bob');
resolveRequest({cached: true, path: '/alice/file.bin'});
await download;
assert.equal(api.read('alice', 'same-id').status, 'completed');
assert.equal(api.read('bob', 'same-id'), undefined);
api.onRealtimeFileProgress({peerId: 'bob', fileId: 'same-id', received: 1, total: 3, percent: 33});
api.onRealtimeFileCancelled({peerId: 'bob', fileId: 'same-id'});
assert.equal(api.read('alice', 'same-id').status, 'completed');
assert.equal(api.read('bob', 'same-id').status, 'cancelled');
const cancellation = api.cancelChatFile('same-id');
api.peer('alice');
resolveRequest({success: true});
await cancellation;
assert.equal(api.read('alice', 'same-id').status, 'completed');
assert.equal(api.read('bob', 'same-id').status, 'cancelled');
console.log('PASS file UI isolates peers and keeps requests bound to their original conversation');

api.peer('alice');
api.onRealtimeFilePaused({peerId: 'alice', fileId: 'huge', received: 237 * 1024 ** 3, total: 300 * 1024 ** 3, percent: 79});
const resumed = api.downloadChatFile('huge', 'large.bin', 300 * 1024 ** 3);
assert.equal(api.read('alice', 'huge').received, 237 * 1024 ** 3);
api.onRealtimeFileVerifying({peerId: 'alice', fileId: 'huge', received: 237 * 1024 ** 3, total: 300 * 1024 ** 3, percent: 79});
assert.equal(api.read('alice', 'huge').status, 'verifying');
const finishResume = resolveRequest;
const paused = api.pauseChatFile('huge');
api.peer('bob');
resolveRequest({success: true});
await paused;
finishResume({success: true});
await resumed;
assert.equal(api.read('alice', 'huge').status, 'paused');
assert.equal(api.read('alice', 'huge').received, 237 * 1024 ** 3);
assert.equal(api.read('bob', 'huge'), undefined);
api.onRealtimeFileComplete({peerId: 'alice', fileId: 'huge', path: '/saved/large.bin'});
api.onRealtimeFilePaused({peerId: 'alice', fileId: 'huge', received: 237 * 1024 ** 3});
assert.equal(api.read('alice', 'huge').status, 'completed');
console.log('PASS pause and resume preserve large offsets, conversation identity and completed state');

class Element {
  constructor(tag, cls, text) { this.tagName = tag; this.className = cls; this.textContent = text || ''; this.dataset = {}; this.style = {}; this.children = []; this.classList = {add() {}, remove() {}}; }
  append(...nodes) {this.children.push(...nodes);}
  appendChild(node) {this.children.push(node); return node;}
  addEventListener() {}
}
context.QP.el = (tag, cls, text) => new Element(tag, cls, text);
context.QP.clear = element => {element.children = [];};
api.peer('alice');
api.onRealtimeFilePaused({peerId: 'alice', fileId: 'card', received: 10, total: 20, percent: 50});
const card = api.createFileCardDom({fileId: 'card', name: 'large.bin', size: 20}, {isMe: false});
const actions = card.children[0].children[2];
assert.equal(actions.children[0].textContent, '▶ Reanudar');
assert.equal(actions.children[1].title, 'Descartar descarga');
const loading = api.loadConversation('alice');
resolveRequest({messages: [], downloads: [{peerId: 'alice', fileId: 'saved', name: 'large.bin', received: 237 * 1024 ** 3, total: 300 * 1024 ** 3, percent: 79, status: 'paused'}]});
// Rendering is separate from restoring the durable transfer state.
await loading;
assert.equal(api.read('alice', 'saved').received, 237 * 1024 ** 3);
assert.equal(api.read('alice', 'saved').status, 'paused');
console.log('PASS reopening history restores progress and offers Resume and Discard actions');
