import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
const context = createContext({ document: { addEventListener() {} }, window: {}, navigator: {}, console });
const source = readVoiceSource();
new Script(source.replace(/\}\)\(\);\s*$/, 'globalThis.getCodecCandidates = getCodecCandidates; globalThis.fitScreenOutput = fitScreenOutput; })();')).runInContext(context);
for (const [width, height, expectedWidth, expectedHeight] of [[2560, 1440, 1920, 1080], [801, 601, 800, 600], [3440, 1440, 1920, 802], [720, 1280, 606, 1080]]) {
  const result = context.fitScreenOutput(width, height, { width: 1920, height: 1080 });
  assert.equal(result.width, expectedWidth);
  assert.equal(result.height, expectedHeight);
}
assert.match(context.getCodecCandidates(5, 2560, 1440, 30)[0], /L150$/, '1440p requires HEVC level 5 despite its moderate sample rate');
assert.match(context.getCodecCandidates(5, 3840, 2160, 15)[0], /L150$/, '4K at low FPS must still fit a whole HEVC picture');
assert.equal(context.getCodecCandidates(1, 3840, 2160, 15)[0], 'avc1.640033', '4K AVC must not use a 1080p-only level');
assert.match(context.getCodecCandidates(3, 2560, 1440, 15)[0], /^vp09.00.50/, 'VP9 must fit full frame size');
assert.match(context.getCodecCandidates(4, 2560, 1440, 15)[0], /^av01.0.12/, 'AV1 must fit full frame size');
assert.equal(context.getCodecCandidates(1, 1920, 1080, 30)[0], 'avc1.640028', '1080p30 uses an interoperable AVC level');
const html = readFileSync(new URL('../../QuicNet.Shared/wwwroot/index.html', import.meta.url), 'utf8');
assert(!html.includes('value="webcodecs"'), 'Browser codecs must not remain selectable');
for (const id of ['screenCaptureGallery', 'screenCaptureSearch', 'screenCapturePreview', 'screenCaptureWindows', 'screenCaptureDisplays'])
  assert(html.includes('id="' + id + '"'), 'Missing visual source picker control: ' + id);
console.log('PASS native capture profile dimensions, codec levels and source-picker controls');

assert(!html.includes('id="screenSourceSelect"'), 'Settings must not contain a second capture source selector');
assert(!html.includes('id="screenSourcePickerBtn"'), 'Source selection belongs to the share action');
