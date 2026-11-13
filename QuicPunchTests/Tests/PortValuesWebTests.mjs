import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const context = createContext({});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/ui-values.js', import.meta.url), 'utf8')).runInContext(context);
const {formatPorts, parsePorts} = context.QuicNetUiValues;
const parse = text => Array.from(parsePorts(text));
for (const text of ['', '0', '65536', '-1', '1.5', '+443', '1e3', '443abc', '443--445', '445–443', '9'.repeat(400)])
  assert.throws(() => parse(text), /puerto/i);
assert.deepEqual(parse('80;443,443 1000 - 1002 65535'), [80, 443, 1000, 1001, 1002, 65535]);
assert.equal(parse('1-65535').length, 65535);
assert.equal(formatPorts(), '—');
assert.equal(formatPorts([0, NaN, Infinity, -1, 1.5, 65536]), '—');
assert.equal(formatPorts([443, '443']), '443');
assert.equal(formatPorts([445, 443, 444]), '443 – 445');
assert.equal(formatPorts([443, 80]), '80, 443');
assert.equal(formatPorts([443, 80], 'Range'), '80 – 443');
// The default display of short discrete lists can be pasted back unchanged.
for (const ports of [[1], [80, 443], [443, 444, 445], [1, 65535]])
  assert.deepEqual(parse(formatPorts(ports)), ports);
console.log('PASS port values without DOM: strict parsing, boundaries, summaries and exact short-list round-trips');
