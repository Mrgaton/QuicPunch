import {readFileSync} from 'node:fs';
import {createContext, Script} from 'node:vm';
import assert from 'node:assert/strict';
const elements = new Map();
function element(tag = 'div') {
  return { tag, children: [], listeners: {}, style: {}, checked: false, value: '', type: '', disabled: false,
    classList: { add() {}, remove() {}, contains() { return false; } },
    removeAttribute(key) { delete this[key]; },
    append(...items) { this.children.push(...items); }, appendChild(item) { this.children.push(item); return item; },
    setAttribute(key, value) { this[key] = value; }, addEventListener(event, listener) { this.listeners[event] = listener; }, querySelector() { return null; }, querySelectorAll() { return []; }, focus() {}, showModal() { this.open = true; }, close() { this.open = false; }, remove() {} };
}
const document = { body: element(), addEventListener() {}, querySelectorAll() { return []; },
  createElement: element, createTextNode: text => ({ text }),
  getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); } };
const requests = [];
const config = { revision: 7, mode: 'advanced', autoConnect: true, enabled: true, autoAssign: true,
  localAddress: '10.0.0.1', subnetMask: '255.255.255.0', mtu: 1420, peers: [] };
const status = { lan: { active: true, desiredEnabled: true, localIp: '10.0.0.1', autoAssign: true,
  subnetMask: '255.255.255.0', mtu: 1420, isAdmin: false, canStartWithoutElevation: true, requiresAdmin: false,
  vpnRevision: 7, activePeers: [] }, peers: [], savedPeers: [] };
const QP = { async api(path, options) { requests.push({ path, options }); return path === '/api/vpn/config' ?
  { success: true, configuration: structuredClone(config), path: '/vpn.json', peerDefaults: [] } : { success: true }; },
  async getStatus() { return status; }, clear(el) { el.children = []; }, setText() {}, toast() {}, initials() { return 'P'; }, peerMeta() { return ''; },
  fmtBytes(n) { return String(n); }, el(tag, cls, text) { return { ...element(tag), text, textContent: text, cls }; },
  button(text, cls, action) { return { ...element('button'), text, cls, action, type: 'button' }; } };
let code = readFileSync(new URL('../../QuicNet.Shared/wwwroot/vpn.js', import.meta.url), 'utf8');
// Expose only the actual view entry points to this isolated DOM harness.
code = code.replace('})();', 'globalThis.vpnTest = { refreshVpn, applyLan, toggleLan, editVpnPeer, connectAll, connectLan }; })();');
const context = createContext({ document, QP, console, structuredClone, atob, setTimeout() {}, clearTimeout() {},
  window: { confirm() { throw new Error('A broker-backed VPN must not request elevation.'); } } });
new Script(code).runInContext(context);
await context.vpnTest.refreshVpn();
assert.equal(elements.get('lanEnabled').checked, true);
status.lan.mtu = 1500;
await context.vpnTest.refreshVpn();
assert.equal(elements.get('lanMtu').value, 1500, 'A valid MTU of 1500 is displayed unchanged');
await context.vpnTest.toggleLan(true);
assert.ok(requests.some(r => r.path === '/api/lan-toggle'), 'broker-backed toggle reaches the API without an elevation prompt');
await context.vpnTest.applyLan();
const saved = JSON.parse(requests.findLast(r => r.path === '/api/vpn/config' && r.options?.method === 'POST').options.body);
assert.equal(saved.expectedRevision, 7, 'optimistic concurrency revision sent');
assert.equal(saved.configuration.autoConnect, true, 'Adapter edits preserve existing reconnect choices');
assert.equal(saved.configuration.mode, 'advanced', 'Adapter edits preserve the legacy mode for unmigrated peers');
assert.equal(saved.configuration.mtu, 1500, 'Saving preserves a valid MTU of 1500');
for (const invalid of ['', '1199', '9001', '1420.5']) {
  elements.get('lanMtu').value = invalid;
  const before = requests.length;
  await context.vpnTest.applyLan();
  assert.equal(requests.length, before, 'Invalid MTU must not be silently replaced or submitted');
}
status.lan.active = false; status.lan.requiresAdmin = true; status.lan.canStartWithoutElevation = false;
config.enabled = false; config.autoConnect = false;
await context.vpnTest.refreshVpn();
context.vpnTest.editVpnPeer({ certHash: Buffer.alloc(32, 170).toString('base64'), name: '<script>untrusted name</script>' });
const dialog = document.body.children.at(-1);
assert.equal(dialog.children[0].textContent, 'VPN de <script>untrusted name</script>', 'peer names rendered as text');
assert.equal(dialog.open, true, 'configuration opens without adapter or administrator privileges');
const form = dialog.children[2];
const descendants = node => (node.children || []).flatMap(child => [child, ...descendants(child)]);
const inputs = descendants(form).filter(input => input.tag === 'input');
descendants(form).find(input => input.name === 'mode').value = 'advanced';
form.children.flatMap(row => row.children || []).find(input => input.name === 'connectionMode').value = 'maintain';
inputs.find(input => input.placeholder === '192.168.50.0/24').value = '192.168.50.0/24, 10.99.0.0/16';
await form.listeners.submit({ preventDefault() {} });
const peerSave = JSON.parse(requests.findLast(r => r.path === '/api/vpn/config' && r.options?.method === 'POST').options.body);
assert.equal(peerSave.configuration.peers[0].autoAccept, true);
assert.equal(peerSave.configuration.peers[0].mode, 'advanced');
assert.equal(peerSave.configuration.peers[0].autoConnect, true);
assert.equal(peerSave.configuration.peers[0].certHash, 'A'.repeat(64));
assert.equal(peerSave.configuration.enabled, true);
assert.equal(peerSave.configuration.autoConnect, false, 'Per-contact startup does not change the legacy master setting for other peers');
assert.deepEqual(peerSave.configuration.peers[0].allowedSources, ['192.168.50.0/24', '10.99.0.0/16']);
status.peers = [
  { id: 'pending', name: 'A pendiente', certHash: 'pending', isTrusted: true, isQuicConnected: false, vpnCanStart: true },
  { id: 'ready', name: 'Z disponible', certHash: 'ready', isTrusted: true, isQuicConnected: true, vpnCanStart: true },
  { id: 'unknown', name: 'B desconocido', certHash: 'unknown', isTrusted: true, isQuicConnected: true, vpnCanStart: null },
  { id: 'denied', name: 'C sin permisos', certHash: 'denied', isTrusted: true, isQuicConnected: true, vpnCanStart: false },
  { id: 'untrusted', name: 'D sin autorizar', certHash: 'untrusted', isTrusted: false, isQuicConnected: true, vpnCanStart: true }
];
status.savedPeers = [{ certHash: 'saved', name: 'Y guardado' }];
await context.vpnTest.refreshVpn();
assert.equal(elements.get('connectAllBtn').disabled, true, 'Remote readiness cannot override missing local permissions');
const beforeLocalPermissions = requests.length;
await context.vpnTest.connectLan('ready');
assert.equal(requests.length, beforeLocalPermissions, 'Local permission gate also protects the connection action');
status.lan.canStartWithoutElevation = true;
await context.vpnTest.refreshVpn();
const rows = elements.get('vpnPeers').children;
assert.deepEqual(rows.map(row => row.children[1].children[0].text),
  ['B desconocido', 'C sin permisos', 'Z disponible', 'D sin autorizar', 'A pendiente', 'Y guardado'], 'established QUIC transports precede pending peers');
const buttons = row => row.children[2].children.map(button => button.text);
assert.deepEqual(buttons(rows[0]), ['Configurar VPN'], 'unknown capability keeps configuration only');
assert.deepEqual(buttons(rows[1]), ['Configurar VPN'], 'remote without privileges has no connect action');
assert.deepEqual(buttons(rows[2]), ['Configurar VPN', 'Conectar'], 'confirmed remote capability enables LAN connect');
assert.deepEqual(buttons(rows[5]), ['Configurar VPN'], 'saved offline peer remains configurable without manual location');
assert.ok(!code.includes('Localizar y conectar') && !code.includes('/api/connect-saved-peer'), 'VPN UI does not duplicate transport discovery');
const previous = requests.length;
await context.vpnTest.connectLan('unknown');
assert.equal(requests.length, previous, 'unavailable peer cannot be connected by calling the view action directly');
await context.vpnTest.connectAll();
const joins = requests.slice(previous).filter(r => r.path === '/api/connect-peer');
assert.equal(joins.length, 1, 'bulk connection only includes eligible peers');
assert.equal(JSON.parse(joins[0].options.body).peerId, 'ready');
await context.vpnTest.refreshVpn();
assert.equal(elements.get('connectAllBtn').disabled, true, 'bulk button is disabled while the only eligible peer is connecting');
console.log('PASS VPN UI: broker activation, mode, autoconnection, revision, ACLs, safe names, transport ordering and remote capability gates');

const beforeUnavailable = requests.length;
QP.capabilities = { capabilities: { vpn: { available: false } } };
await context.vpnTest.refreshVpn();
assert.equal(requests.length, beforeUnavailable, 'Disabled VPN must not query a missing endpoint and show startup errors');

