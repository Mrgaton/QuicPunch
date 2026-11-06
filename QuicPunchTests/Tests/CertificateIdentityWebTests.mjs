import {readFileSync} from 'node:fs';
import {webcrypto} from 'node:crypto';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const context = createContext({QP: {}, TextEncoder, crypto: webcrypto, atob, Uint8Array});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/identity.js', import.meta.url), 'utf8')).runInContext(context);
const identity = context.QP.identity;
const certificate = Buffer.from(Array.from({length: 32}, (_, index) => index));
const peer = {certHash: certificate.toString('base64'), id: 'one', name: 'Ana'};
const expected = {version:2,hue:297,saturation:25,edge:'cut',pattern:'mosaico'};
const pattern = async value => {
  const {hex, ...visual} = await identity.describe(value);
  return JSON.parse(JSON.stringify(visual));
};
assert.deepEqual(await pattern(peer), expected, 'Changing capsule v2 would invalidate familiar identities');
assert.deepEqual(await pattern({...peer, id: 'two', name: 'Otra foto y otro nombre'}), expected);
assert.deepEqual(await pattern({certHash: certificate.toString('hex').toUpperCase()}), expected);
assert.deepEqual(await pattern({certHash: certificate.toString('base64url')}), expected);
for (let index = 0; index < certificate.length; index++) {
  const changed = Buffer.from(certificate); changed[index] ^= 1;
  assert.notDeepEqual(await pattern({certHash: changed.toString('base64')}), expected, `Certificate byte ${index} was ignored`);
}
assert.equal(identity.normalize({id: 'one', name: 'Ana', canonicalId: '0123456789abcdef'}), null);
for (const certHash of ['', 'abcd', '<img src=x onerror=alert(1)>', 'a'.repeat(16), 'x'.repeat(60)]) {
  assert.equal(identity.normalize({certHash}), null);
  assert.equal(await identity.describe({certHash}), null);
}
assert.equal(identity.normalize(peer), certificate.toString('hex'));
const noCrypto = createContext({QP:{},TextEncoder,atob,Uint8Array});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/identity.js', import.meta.url), 'utf8')).runInContext(noCrypto);
assert.equal(await noCrypto.QP.identity.describe(peer), null, 'Unsupported crypto leaves a neutral capsule');
console.log('PASS certificate capsules: frozen v2 vector, hex/base64/base64url equivalence, all certificate bytes, name-independent appearance, neutral fallback');
