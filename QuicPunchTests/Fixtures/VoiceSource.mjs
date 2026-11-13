import {readFileSync} from 'node:fs';

// VM tests and instrumented browser routes use the same script order as index.html.
export function readVoiceSource() {
  return ['screen-audio-capture.js', 'screen-audio-playback.js', /* Native surface bridge loads separately from index.html. */ 'voice.js']
    .map(file => readFileSync(new URL('../../QuicNet.Shared/wwwroot/' + file, import.meta.url), 'utf8')).join('\n');
}
