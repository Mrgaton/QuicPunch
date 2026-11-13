import {attachApp} from '../Fixtures/WebAppRuntime.mjs';
import {createContext} from 'node:vm';
import assert from 'node:assert/strict';

const events = () => {
  const listeners = new Map();
  return {
    addEventListener(name, callback) { (listeners.get(name) || listeners.set(name, []).get(name)).push(callback); },
    dispatch(name, event = {}) { for (const callback of listeners.get(name) || []) callback(event); }
  };
};
const timers = new Map(), sockets = [], beacons = [], requests = [], diagnostics = [];
let nextTimer = 0;
class Socket {
  static CONNECTING = 0; static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
  constructor(url) { this.url = url; this.readyState = Socket.CONNECTING; this.sent = []; this.bufferedAmount = 0; sockets.push(this); }
  open() { this.readyState = Socket.OPEN; this.onopen?.(); }
  send(value) { assert.equal(this.readyState, Socket.OPEN); this.sent.push(JSON.parse(value)); }
  close() { this.readyState = Socket.CLOSING; }
  message(type, data) { this.onmessage?.({data: JSON.stringify({type, data})}); }
  closed() { this.readyState = Socket.CLOSED; this.onclose?.({code: 1006, wasClean: false}); }
}
const window = {...events(), __qp_csrf: 'test csrf', QuicNetDiagnostics: {report() {}, setTransport(value) { diagnostics.push(value); }}};
const document = {...events(), hidden: false, createElement: () => ({})};
const context = createContext({
  window, document, WebSocket: Socket, ArrayBuffer, console,
  location: {protocol: 'http:', host: 'localhost:8080'},
  navigator: {sendBeacon(path) { beacons.push(path); return false; }},
  fetch: async (path, options) => { requests.push({path, options}); return {}; },
  setTimeout(callback, delay) { const id = ++nextTimer; timers.set(id, {callback, delay}); return id; },
  clearTimeout(id) { timers.delete(id); }
});
attachApp(context);
const qp = window.QP, statuses = [];
qp.on('ws_status', status => statuses.push(status.connected));
document.dispatch('visibilitychange');
const first = sockets[0]; first.open(); first.message('status_updated', {generation: 1});
assert.equal(qp.isWsConnected, true);

// Cancellation of an unload prompt must leave the current connection usable.
window.dispatch('beforeunload');
assert.equal(first.readyState, Socket.OPEN);
assert.equal(qp.requestSync(), true);
assert.equal(beacons.length, 0);

// Visibility can reconnect while the previous socket's close event is still queued.
first.readyState = Socket.CLOSING;
first.closed();
document.dispatch('visibilitychange');
const second = sockets[1]; second.open(); second.message('status_updated', {generation: 2});
const pending = qp.wsRequest('settings.get');
const request = second.sent.at(-1);
first.onopen(); first.message('status_updated', {generation: 0}); first.onerror(); first.closed();
assert.equal(qp.state.status.generation, 2, 'A superseded socket overwrote the current status');
assert.equal(qp.isWsConnected, true, 'A delayed close disconnected the replacement');
assert.equal(second.readyState, Socket.OPEN, 'A stale error closed the replacement socket');
second.onmessage({data: JSON.stringify({type: 'response', id: request.id, success: true, data: {ok: true}})});
assert.equal((await pending).ok, true, 'A stale close rejected a request on the replacement');
assert.equal(timers.size, 0);

// Suspension tears down pending work but does not tell the native host to exit.
const interrupted = qp.wsRequest('settings.get').then(() => assert.fail('Suspended request resolved'), error => error);
window.dispatch('pagehide', {persisted: true});
assert.match((await interrupted).message, /perdió la conexión/);
assert.equal(qp.isWsConnected, false);
assert.equal(timers.size, 0, 'Suspension retained request/reconnect timers');
assert.equal(beacons.length, 0);
second.closed();
window.dispatch('pageshow', {persisted: true});
assert.equal(sockets.length, 3, 'A restored page never reconnected');
sockets[2].open(); sockets[2].message('status_updated', {generation: 3});
assert.equal(qp.state.status.generation, 3);
assert.equal(qp.isWsConnected, true);

// Real departure sends one authenticated close request, including fallback if the beacon queue is full.
window.dispatch('pagehide', {persisted: false});
assert.deepEqual(beacons, ['/api/client-closing?csrf=test%20csrf']);
assert.equal(requests.length, 1);
assert.equal(requests[0].path, beacons[0]);
assert.equal(requests[0].options.keepalive, true);
sockets[2].closed();
assert.equal(timers.size, 0, 'A departed page scheduled reconnection');
assert.equal(diagnostics.at(-1), null);
assert.deepEqual(statuses, [true, false, true, true]);
console.log('PASS WebSocket lifecycle: stale events isolated, cancelled unload usable, pending requests released, cached page restored and authenticated close fallback');
