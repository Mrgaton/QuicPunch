import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';

import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const source = readVoiceSource();
const select = { value: '64000' };
const modal = { classList: { add() {}, remove() {} } };
const requests = [];
const context = createContext({
  document: {
    addEventListener() {},
    getElementById(id) { return id === 'opusBitrateSelect' ? select : id === 'opusModal' ? modal : null; }
  },
  window: {}, navigator: {}, console,
  QP: {
    toast() {},
    async api(path, options) {
      const request = JSON.parse(options.body);
      requests.push({ path, ...request });
      return { bitrate: 56000, requestedBitrate: request.bitrate };
    }
  }
});
new Script(source.replace(/\}\)\(\);\s*$/, `
  globalThis.test = { openOpusModal, saveOpusModal,
    setState(state) { voiceState = state; }, getState() { return voiceState; } };
})();`)).runInContext(context);

context.test.setState({ opusBitrate: 56000, requestedOpusBitrate: 128000 });
context.test.openOpusModal();
assert.equal(select.value, '128000', 'The selector must show the user ceiling even during adaptive throttling');
await context.test.saveOpusModal();
assert.equal(requests.at(-1).bitrate, 128000, 'Saving unchanged settings must preserve the ceiling');
assert.equal(context.test.getState().opusBitrate, 56000, 'Retain the effective bitrate for diagnostics');
context.test.openOpusModal();
assert.equal(select.value, '128000', 'Reopening must not replace the ceiling with a transient QoS bitrate');

select.value = '96000';
await context.test.saveOpusModal();
context.test.openOpusModal();
assert.equal(select.value, '96000', 'New user settings must survive the server effective bitrate response');
context.test.setState({ opusBitrate: 64000 });
context.test.openOpusModal();
assert.equal(select.value, '64000', 'Older state payloads retain their bitrate fallback');
for (const bitrate of [160000, 192000, 256000, 300000]) {
  select.value = String(bitrate);
  await context.test.saveOpusModal();
  context.test.openOpusModal();
  assert.equal(requests.at(-1).bitrate, bitrate);
  assert.equal(select.value, String(bitrate), 'Higher ceiling must survive save and reopen');
}
console.log('PASS voice bitrate settings: requested ceiling, adaptive bitrate, save/reopen and legacy state.');
