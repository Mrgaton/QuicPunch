import {readFileSync} from 'node:fs';
import {createHash, webcrypto} from 'node:crypto';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const context = createContext({QP: {}, TextEncoder, crypto: webcrypto, atob, Uint8Array});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/identity.js', import.meta.url), 'utf8')).runInContext(context);
const identity = context.QP.identity;
const certificate = Buffer.from(Array.from({length: 32}, (_, index) => index));
const peer = {certHash: certificate.toString('base64'), id: 'one', name: 'Ana'};
const expected = {version:3,hue:28,secondaryHue:178,saturation:64,edge:'bevel',pattern:'escamas',pitch:33,angle:120,phaseX:14,phaseY:9};
const pattern = async value => {
  const {hex, ...visual} = await identity.describe(value);
  return JSON.parse(JSON.stringify(visual));
};
assert.deepEqual(await pattern(peer), expected, 'Changing capsule v3 would invalidate familiar identities');
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
// Check a broad, reproducible population rather than judging a handful of contacts.
const population = await Promise.all(Array.from({length:4096}, (_, index) => pattern({
  certHash:createHash('sha256').update('identity diversity fixture ' + index).digest('hex')
})));
const hues = new Map();
for (const visual of population) {
  hues.set(visual.hue, (hues.get(visual.hue) || 0) + 1);
  assert.ok(visual.saturation >= 62 && visual.saturation <= 80);
  assert.notEqual(visual.hue, visual.secondaryHue);
}
assert.equal(hues.size, 16, 'All warm and cool palette families must be reachable');
assert.ok([...hues.values()].every(count => count >= 180 && count <= 340), 'Palette distribution is skewed');
assert.equal(new Set(population.map(visual => visual.pattern)).size, 12);
assert.equal(new Set(population.map(visual => visual.edge)).size, 8);
assert.equal(new Set(population.map(visual => `${visual.hue}:${visual.pattern}`)).size, 192, 'Colors and motifs must vary independently');
assert.equal(new Set(population.map(JSON.stringify)).size, population.length, 'Sample certificates repeat the full visual descriptor');
assert.deepEqual(await pattern(peer), expected, 'Cache eviction must not change the descriptor');
const noCrypto = createContext({QP:{},TextEncoder,atob,Uint8Array});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/identity.js', import.meta.url), 'utf8')).runInContext(noCrypto);
assert.equal(await noCrypto.QP.identity.describe(peer), null, 'Unsupported crypto leaves a neutral capsule');
console.log('PASS certificate capsules: frozen v3 vector, encoding equivalence, all certificate bytes, stable identity, neutral fallback and diversity across 4096 certificates');
