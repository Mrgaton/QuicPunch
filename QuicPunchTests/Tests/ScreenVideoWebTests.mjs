import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';

import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const source = readVoiceSource();
const decoders = [];
class Decoder {
  constructor(handlers) { this.state = 'unconfigured'; this.decodeQueueSize = 0; this.handlers = handlers; this.decoded = []; decoders.push(this); }
  configure(config) { this.config = config; this.state = 'configured'; }
  decode(chunk) { assert.equal(this.state, 'configured'); this.decoded.push(chunk); }
  reset() { this.state = 'unconfigured'; this.resets = (this.resets || 0) + 1; }
  close() { this.state = 'closed'; }
  static async isConfigSupported(config) { return { supported: config.codec.startsWith('avc1.'), config }; }
}
const replies = [];
const context = createContext({
  document: { addEventListener() {} }, window: {}, navigator: {},
  VideoDecoder: Decoder, EncodedVideoChunk: class { constructor(options) { Object.assign(this, options); } },
  TextEncoder, TextDecoder, Uint8Array, atob, btoa, console, performance,
  QP: { api: async (path, options) => replies.push({ path, ...JSON.parse(options.body) }) }
});
new Script(source.replace(/\}\)\(\);\s*$/, 'globalThis.test = { handleWebCodecsVideoChunk, answerDecoderProbe }; })();')).runInContext(context);
const entry = { peerId: 'test', width: 320, height: 180, fps: 30 };
function packet({ key = true, codec = 'avc1.64001F', description = 'AQID', generation = 1, timestamp = 12345 } = {}) {
  const metadata = new TextEncoder().encode(JSON.stringify({ c: codec, d: description, g: generation, t: timestamp }));
  const data = new Uint8Array(3 + metadata.length + 5);
  data[0] = 2 | Number(key); data[1] = metadata.length >> 8; data[2] = metadata.length & 255;
  data.set(metadata, 3); data.set([0, 0, 0, 1, 5], 3 + metadata.length);
  return data;
}
const handle = context.test.handleWebCodecsVideoChunk;
handle(entry, 1, packet({ key: false })); assert.equal(decoders.length, 0, 'Must wait for keyframe before configuring');
handle(entry, 1, packet());
assert.equal(decoders.length, 1); assert.equal(decoders[0].decoded[0].timestamp, 12345);
assert.deepEqual(Array.from(new Uint8Array(decoders[0].config.description)), [1, 2, 3]);
handle(entry, 1, packet({ key: false, timestamp: 22345 })); assert.equal(decoders.length, 1, 'Do not reconfigure every delta');
decoders[0].decodeQueueSize = 3;
handle(entry, 1, packet({ key: false })); assert.equal(decoders[0].decoded.length, 2, 'Drop backlog-dependent delta');
handle(entry, 1, packet()); assert.equal(decoders[0].state, 'configured', 'reset must be followed by configure');
assert.equal(decoders[0].resets, 1);
handle(entry, 1, packet({ generation: 2, description: 'BAUG' }));
assert.equal(decoders.length, 2, 'New configuration must create a fresh decoder');
handle(entry, 1, packet({ generation: 1 })); assert.equal(decoders.length, 2, 'Ignore stale producer generation');
handle(entry, 1, packet({ generation: 3, description: null }));
assert.equal(decoders.length, 3); assert.equal(decoders[2].config.description, undefined, 'Annex B must not inherit old avcC');
await context.test.answerDecoderProbe({ peerId: 'peer', config: { requestId: 'request', codec: 'avc1.64001F', width: 320, height: 180, description: 'AQID' } });
assert.equal(replies.at(-1).supported, true);
await context.test.answerDecoderProbe({ peerId: 'peer', config: { requestId: 'request', codec: 'unsupported', width: 320, height: 180 } });
assert.equal(replies.at(-1).supported, false);
console.log('Screen web tests passed: timestamps, codec metadata, keyframes, backlog recovery, generations and decoder negotiation.');
