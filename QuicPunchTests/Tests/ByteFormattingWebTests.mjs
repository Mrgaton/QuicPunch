import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const context = createContext({});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/ui-values.js', import.meta.url), 'utf8')).runInContext(context);
const format = context.QuicNetUiValues.fmtBytes;
for (const input of [undefined, null, '', 'invalid', NaN, Infinity, -Infinity, -1, 0]) {
  assert.equal(format(input), '0 B', `Invalid byte count: ${input}`);
}
for (const [input, expected] of [[1, '1 B'], [1023, '1023 B'], [1024, '1.0 KB'],
  [1536, '1.5 KB'], ['2048', '2.0 KB'], [1024 ** 2, '1.0 MB'],
  [1024 ** 3, '1.0 GB'], [1024 ** 4, '1.0 TB'], [1024 ** 5, '1.0 PB'], [1024 ** 6, '1.0 EB']]) {
  assert.equal(format(input), expected);
}
assert.ok(!/undefined|NaN|Infinity/.test(format(Number.MAX_VALUE)));
console.log('PASS shared byte formatting: unit boundaries, large files and invalid counters');

for (const [seconds, text] of [[null, '0s'], [-1, '0s'], [59.9, '59s'], [60, '1m 0s'], [3599, '59m 59s'], [3600, '1h 0m']])
  assert.equal(context.QuicNetUiValues.formatDuration(seconds), text);
