import {ScreenAudioBuffer} from './screen-audio-buffer.js';

class ScreenAudioPlayback extends AudioWorkletProcessor {
  constructor() {
    super();
    this.queue = new ScreenAudioBuffer(sampleRate);
    this.port.onmessage = event => {
      if (event.data === 'reset') this.queue.reset();
      else if (event.data instanceof Float32Array) this.queue.push(event.data);
    };
  }

  process(inputs, outputs) {
    const channels = outputs[0];
    if (channels?.[0]) {
      this.queue.render(channels[0]);
      for (let i = 1; i < channels.length; i++) channels[i].set(channels[0]);
    }
    return true;
  }
}

registerProcessor('quicnet-screen-audio', ScreenAudioPlayback);
