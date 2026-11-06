import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';

import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
class Element {
  constructor(tag = '') { this.tag = tag; this.children = []; this.value = ''; this.textContent = ''; }
  append(...items) { this.children.push(...items); }
  replaceChildren() { this.children = []; this.value = ''; }
  get options() { return this.children.flatMap(child => child.tag === 'optgroup' ? child.children : [child]); }
  get selectedOptions() { return this.options.filter(option => option.value === this.value); }
}
class Option extends Element {
  constructor(label, value) { super('option'); this.label = label; this.value = value; this.disabled = false; }
}
const elements = new Map(['screenCodecSelect', 'screenEncoderSelect', 'screenCodecCompatibility', 'modalHwAccelStatus'].map(id => [id, new Element()]));
const context = createContext({ document: { addEventListener() {}, getElementById: id => elements.get(id), createElement: tag => new Element(tag) }, window: {}, navigator: {}, console, Option, QP: { api: async () => ({}) } });
const source = readVoiceSource();
new Script(source.replace(/\}\)\(\);\s*$/, `globalThis.test = { updateScreenCodecOptions, resolveCompatibleScreenCodec, supportsVideoEncoderCodec,
 setup(calls = [], remote) {
   localScreenCodecDetails.clear();
   screenShareConfig.backend = 'web'; screenShareConfig.codec = 'h264'; screenShareConfig.acceleration = 'gpu';
   localScreenEncoderCodecMask = codecMaskBit(1);
   localScreenCodecDetails.set(1, { supported: true, gpu: { supported: false }, cpu: { supported: true } });
   voiceState.activeCalls = calls; remoteScreenCodecCapabilities.clear();
   if (remote) remoteScreenCodecCapabilities.set('peer', remote);
 }, config: () => screenShareConfig,
 setProbe(fn) { probeVideoEncoderConfig = fn; }, refreshScreenCodecCapabilities,
 native() {
   browserCaptureSelected = false; screenShareConfig.backend = 'ffmpeg'; screenShareConfig.codec = 'hevc'; screenShareConfig.acceleration = 'gpu';
   nativeCapabilities = { nativeEncoders: [
     { Id: 'hevc_nvenc', Codec: 'hevc', Available: true, Hardware: true },
     { Id: 'libx265', Codec: 'hevc', Available: true, Hardware: false }
   ] };
   publishBrowserScreenState = async () => {};
 }, browserHevc() {
   browserCaptureSelected = false; screenShareConfig.backend = 'web'; screenShareConfig.codec = 'hevc'; screenShareConfig.acceleration = 'gpu';
   publishBrowserScreenState = async () => {};
 }

}; })();`)).runInContext(context);
context.test.setup();
context.test.updateScreenCodecOptions();
assert.equal(elements.get('screenCodecSelect').value, 'h264:cpu', 'Persisted unavailable GPU preference must preselect a tested CPU option');
assert.equal(context.test.config().acceleration, 'cpu');
assert.equal(context.test.config().encoder, 'web-software', 'Encoder label must match the actual selection');
assert.ok(context.test.resolveCompatibleScreenCodec('h264'), 'The offered option must also pass Apply validation');
assert.match(elements.get('screenCodecCompatibility').textContent, /preseleccionado/);
context.test.setup([{ peerId: 'peer', screenDecoderCodecMask: 2 }], { decoderCodecMask: 0 });
context.test.updateScreenCodecOptions();
assert.equal(elements.get('screenCodecSelect').options.find(option => option.value === 'h264:cpu').disabled, false, 'Unknown reception must not disable the local encoder settings');
assert.equal(context.test.resolveCompatibleScreenCodec('h264'), null, 'Unconfirmed receivers must not be treated as compatible');
assert.ok(context.test.resolveCompatibleScreenCodec('h264', false), 'Local settings must remain saveable while receivers are unknown');
context.test.setup([{ peerId: 'peer', screenDecoderCodecMask: 0 }], { decoderCodecMask: 2 });
context.test.updateScreenCodecOptions();
assert.ok(context.test.resolveCompatibleScreenCodec('h264'), 'Fresh confirmed reception must enable both the option and Apply');
let active = 0, peak = 0;
context.test.setProbe(async (...args) => { active++; peak = Math.max(peak, active); await new Promise(resolve => setImmediate(resolve)); active--; return args.at(-1) === 'cpu' ? { codecString: 'avc1' } : null; });
await context.test.supportsVideoEncoderCodec({ id: 1 }, { width: 1280, height: 720, fps: 144 });
assert.equal(peak, 1, 'GPU/CPU driver probes must not compete for encoder sessions');
context.test.native();
context.test.setProbe(async () => { throw new Error('FFmpeg must not probe the browser encoder'); });
await context.test.refreshScreenCodecCapabilities(true);
assert.equal(elements.get('screenCodecSelect').value, 'hevc:gpu');
assert.equal(context.test.config().encoder, 'hevc_nvenc');
assert.equal(elements.get('screenCodecSelect').options.find(o => o.value === 'hevc:cpu').disabled, false);
assert.ok(context.test.resolveCompatibleScreenCodec('hevc', false));
assert.match(elements.get('screenCodecSelect').options.find(o => o.value === 'hevc:gpu').label, /hevc_nvenc/);
context.test.browserHevc();
context.test.setProbe(async (...args) => args.at(-1) === 'browser' ? { codecString: 'hvc1.1.6.L150', hardwarePreference: 'no-preference' } : null);
await context.test.refreshScreenCodecCapabilities(true);
assert.equal(elements.get('screenCodecSelect').value, 'hevc:browser', 'Do not disable HEVC when the browser accepts no-preference only');
assert.equal(context.test.config().encoder, 'web-browser');
assert.equal(elements.get('screenCodecSelect').options.find(o => o.value === 'hevc:gpu').disabled, true, 'Default browser encoding must not masquerade as proven GPU encoding');
// Exercise the actual WebCodecs configuration, including a synthetic encode.
const actualContext = createContext({
 document: { addEventListener() {}, getElementById() { return null; } }, window: {}, navigator: {}, console,
 setTimeout, clearTimeout,
 OffscreenCanvas: class { getContext() { return { fillRect() {} }; } },
 VideoFrame: class { close() {} },
 VideoEncoder: class {
   constructor(handlers) { this.handlers = handlers; this.state = 'unconfigured'; }
   static async isConfigSupported(config) { return { supported: config.codec.startsWith('hvc1.') && config.hardwareAcceleration === 'no-preference', config }; }
   configure(config) { this.config = config; this.state = 'configured'; }
   encode() { this.handlers.output({}); }
   async flush() {}
   close() { this.state = 'closed'; }
 }
});
new Script(source.replace(/\}\)\(\);\s*$/, 'globalThis.probe = probeVideoEncoderConfig; })();')).runInContext(actualContext);
assert.equal(await actualContext.probe(5, 1280, 720, 60, 12000000, 'gpu'), null);
const defaultProbe = await actualContext.probe(5, 1280, 720, 60, 12000000, 'browser');
assert.equal(defaultProbe.config.hardwareAcceleration, 'no-preference');
assert.equal(defaultProbe.verified, true);
console.log('PASS media settings: stale GPU preference, concrete CPU encoder, Apply parity, latest receiver capabilities and serialized probes');

