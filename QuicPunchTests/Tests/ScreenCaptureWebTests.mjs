import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
const context = createContext({ document: { addEventListener() {} }, window: {}, navigator: {}, console });
const source = readVoiceSource();
new Script(source.replace(/\}\)\(\);\s*$/, 'globalThis.getCodecCandidates = getCodecCandidates; globalThis.fitScreenOutput = fitScreenOutput; globalThis.createPacer = createScreenFramePacer; })();')).runInContext(context);
for (const fps of [15, 30, 60, 120, 144]) {
  let now = 0, sequence = 0; const queue = new Map(), frames = [], timestamps = [];
  const timing = { now: () => now, schedule(fn, delay) { const id = ++sequence; queue.set(id, { fn, at: now + delay }); return id; }, cancel(id) { queue.delete(id); } };
  const advance = end => {
    while (true) {
      const next = [...queue].sort((a, b) => a[1].at - b[1].at)[0];
      if (!next || next[1].at > end) break;
      now = next[1].at; queue.delete(next[0]); next[1].fn();
    }
    now = end;
  };
  const pacer = context.createPacer(fps, (frame, timestamp) => { frames.push(frame.id); timestamps.push(timestamp); }, timing);
  let closed = 0;
  pacer.push({ id: 1, close() { closed++; } });
  advance(999);
  assert.ok(Math.abs(frames.length - fps) <= 1, 'A static screen must keep the requested output cadence: ' + fps);
  assert.ok(frames.every(id => id === 1), 'Reuse latest image until a new source frame arrives');
  pacer.push({ id: 2, close() { closed++; } });
  assert.equal(closed, 1, 'Replacing the source closes the retained frame');
  advance(1100);
  assert.equal(frames.at(-1), 2);
  for (let i = 1; i < timestamps.length; i++) assert(timestamps[i] > timestamps[i - 1], 'Monotonic encoder timestamps');
  pacer.stop(); const count = frames.length;
  advance(2000);
  assert.equal(frames.length, count, 'Stopping must release timers and stop transmission');
  assert.equal(closed, 2, 'Stopping must close the latest retained frame');
  pacer.push({ id: 3, close() { closed++; } });
  assert.equal(closed, 3, 'Late frames after cancellation must be released');
}
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
assert(!html.includes('<option value="auto">'), 'Automatic options must not remain in the UI');
for (const id of ['screenCaptureGallery', 'screenCaptureSearch', 'screenCapturePreview', 'screenCaptureWindows', 'screenCaptureDisplays'])
  assert(html.includes('id="' + id + '"'), 'Missing visual source picker control: ' + id);
console.log('PASS screen capture pacing: 15–144 FPS, static source repetition, timestamps, frame ownership, cancellation and source-picker controls');

assert(!html.includes('id="screenSourceSelect"'), 'Settings must not contain a second capture source selector');
assert(!html.includes('id="screenSourcePickerBtn"'), 'Source selection belongs to the share action');
