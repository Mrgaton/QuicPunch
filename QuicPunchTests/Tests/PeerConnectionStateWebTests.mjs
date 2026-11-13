import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
import {attachApp,assertConnectionModes} from '../Fixtures/WebAppRuntime.mjs';
const elements = new Map();
function element() {
  return { children: [], style: {}, dataset: {}, classList: { toggle() {} }, append(...items) { this.children.push(...items); }, appendChild(item) { this.children.push(item); }, removeAttribute() {}, setAttribute() {} };
}
const document = { addEventListener() {}, createElement: element, getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); } };
const QP = { el(tag, cls, text) { return { ...element(), tag, cls, text }; }, clear(el) { el.children = []; }, setText() {}, peerMeta(p) { return p.endpoint || ''; }, canonicalId(id) { return id; }, button(text, cls, action) { return { ...element(), text, action }; } };
const context = createContext({ document, QP, console });
assertConnectionModes(attachApp(context));
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/dashboard.js', import.meta.url), 'utf8')).runInContext(context);
// Exercise the real rendering filters while isolating unrelated dashboard sections.
new Script(`
for (const name of ['renderStun','renderDiscovered','renderInterrogations','renderUntrusted','renderUntrustedAttention','renderSaved','renderActiveApps','renderQuicTelemetry','renderRequests','renderLogs']) globalThis[name] = () => {};
wanStatusText = torStatusText = discoveryStatusText = () => '';
fullToken = () => '';
sessionLabels = () => [];
`).runInContext(context);
context.status = { peers: [
  { id: 'udp', name: 'UDP only', isTrusted: true, isQuicConnected: false, unresponsiveSeconds: 0, endpoint: 'udp:1000' },
  { id: 'quic', name: 'QUIC active', isTrusted: true, isQuicConnected: true, quicEndpoint: 'quic:2000', endpoint: 'control:1000' },
  { id: 'pending', name: 'Pending', isTrusted: true, isQuicConnected: false, connectionState: 'connecting', unresponsiveSeconds: 1 },
  { id: 'stale', name: 'Stale', isTrusted: true, isQuicConnected: false, unresponsiveSeconds: 90 }
] };
new Script('renderDashboard(status)').runInContext(context);
assert.equal(elements.get('connectedPeers').children.length, 1, 'UDP responsiveness must never imply QUIC connection');
assert.equal(elements.get('availablePeers').children.length, 2, 'available and connecting peers remain accessible separately');
const connected = JSON.stringify(elements.get('connectedPeers').children);
assert.ok(connected.includes('quic:2000'));
assert.ok(!connected.includes('control:1000'));
assert.ok(!connected.includes('< 1 ms'), 'missing telemetry must not fabricate RTT');
const pendingButton = elements.get('availablePeers').children[1].children[3].children[0];
assert.equal(pendingButton.disabled, true);
new Script('status.peers[1].isQuicConnected = false; status.peers[1].unresponsiveSeconds = 0; renderDashboard(status)').runInContext(context);
assert.equal(elements.get('connectedPeers').children[0].children[0].colSpan, 6, 'disconnect removes the connected row');
assert.equal(elements.get('availablePeers').children.length, 3, 'disconnected responsive peer returns to availability');
console.log('PASS peer state dashboard: discovery, negotiation, connection, disconnect, endpoint and RTT');
