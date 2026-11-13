import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
import {attachApp} from '../Fixtures/WebAppRuntime.mjs';

const ids = ['savedCertHash', 'savedPorts', 'savedPeerModalError', 'savedName', 'savedAddresses', 'savedOnion', 'savedNetworkType', 'savedAutoConnect', 'saveSavedPeerBtn', 'savedPeerModal', 'savedPeerModalTitle'];
const nodes = new Map(ids.map(id => {
  const classes = new Set();
  return [id, {value: '', textContent: '', disabled: false,
    classList: {add: name => classes.add(name), remove: name => classes.delete(name), contains: name => classes.has(name)}, focus() {}}];
}));
const card = {setAttribute() {}, removeAttribute() {}, focus() {}};
nodes.get('savedPeerModal').querySelector = () => card;
nodes.get('savedPeerModal').querySelectorAll = () => ids.filter(id => id !== 'savedPeerModal').map(id => nodes.get(id));
const requests = [];
nodes.get('savedCertHash').value = 'ab'.repeat(32);
const context = createContext({setTimeout() {}, document: {addEventListener() {}, getElementById: id => nodes.get(id)}, QP: {
  async api(path, options) { requests.push({path, body: JSON.parse(options.body)}); }, toast() {}
}});
attachApp(context);
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/dashboard.js', import.meta.url), 'utf8') + '\nrefreshDashboard = () => {};').runInContext(context);

for (const invalid of ['443abc', '1.5', '+443', '-443', '443-', '443--445', '1e3', '0', '65536', '450–443', '443-445abc', '', '9'.repeat(400)]) {
  nodes.get('savedPorts').value = invalid;
  nodes.get('savedPeerModalError').textContent = '';
  await context.saveSavedPeerEditor();
  assert.equal(requests.length, 0, 'Invalid ports submitted: ' + invalid);
  assert.match(nodes.get('savedPeerModalError').textContent, /puerto|Puerto/);
}
for (const [text, expected] of [
  ['443 – 445', [443, 444, 445]],
  ['80, 443; 443 1000 - 1002', [80, 443, 1000, 1001, 1002]],
  ['00080 65535', [80, 65535]],
  [context.QP.formatPorts([443, 444, 445], 'Range'), [443, 444, 445]]
]) {
  nodes.get('savedPorts').value = text;
  await context.saveSavedPeerEditor();
  assert.deepEqual(requests.at(-1).body.ports, expected);
  assert.equal(nodes.get('saveSavedPeerBtn').disabled, false);
}
// A display summary is deliberately lossy; editing must keep every actual port.
const sparsePorts = [80, 443, 1000, 2000, 3000, 4000, 5000, 6000, 9000];
context.openSavedPeerEditor({certHash: 'ab'.repeat(32), ports: sparsePorts, portMode: 'Range'});
await context.saveSavedPeerEditor();
assert.deepEqual(requests.at(-1).body.ports, sparsePorts, 'Saving a contact must not fill the gaps in a display range');
console.log('PASS saved contact ports: exact integers, boundaries, invalid forms, deduplication and displayed range round-trip');
