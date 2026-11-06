// Continuous 48 kHz mono PCM playout. Packet boundaries never restart the
// waveform; a short jitter buffer absorbs network/main-thread scheduling bursts.
export class ScreenAudioBuffer {
  constructor(outputSampleRate = 48000) {
    this.samples = new Float32Array(24000);
    this.targetFrames = 3840; // 80 ms at the wire sample rate.
    this.maximumFrames = 11520; // Discard stale audio after a long stall (240 ms).
    this.step = 48000 / outputSampleRate;
    this.fadeFrames = Math.max(1, Math.round(outputSampleRate * .005));
    this.read = 0; this.write = 0; this.playing = false;
    this.last = 0; this.tail = 0; this.tailRemaining = 0;
    this.transitionFrom = 0; this.transitionRemaining = 0; this.drift = 0;
  }

  get bufferedFrames() { return Math.max(0, this.write - this.read); }

  push(samples) {
    // Even an oversized packet cannot overwrite audio that is still in use.
    const offset = Math.max(0, samples.length - this.maximumFrames);
    for (let i = offset; i < samples.length; i++) this.samples[this.write++ % this.samples.length] = samples[i];
    if (offset > 0 || this.bufferedFrames > this.maximumFrames) {
      this.read = this.write - this.targetFrames;
      this.transitionFrom = this.last; this.transitionRemaining = this.fadeFrames;
    }
  }

  reset() {
    this.read = this.write = 0; this.playing = false; this.drift = 0;
    this.tail = this.last; this.tailRemaining = this.fadeFrames;
    this.transitionRemaining = 0;
  }

  render(output) {
    if (!this.playing && this.bufferedFrames >= this.targetFrames) {
      this.playing = true;
      this.transitionFrom = this.last; this.transitionRemaining = this.fadeFrames;
      this.tailRemaining = 0;
    }
    // Correct small clock differences gradually, rather than dropping/overlapping
    // whole packets. Keep the correction below 0.2% so pitch stays stable.
    const correction = Math.max(-.002, Math.min(.002, (this.bufferedFrames - this.targetFrames) / 48000 * .02));
    this.drift += (correction - this.drift) * .005;
    const step = this.step * (1 + this.drift);
    for (let i = 0; i < output.length; i++) {
      let value = 0;
      if (this.playing && this.bufferedFrames < Math.max(2, step)) {
        this.playing = false; this.read = this.write;
        this.tail = this.last; this.tailRemaining = this.fadeFrames;
      }
      if (this.playing) {
        const index = Math.floor(this.read), fraction = this.read - index;
        const first = this.samples[index % this.samples.length];
        value = first + (this.samples[(index + 1) % this.samples.length] - first) * fraction;
        this.read += step;
        if (this.transitionRemaining > 0) {
          const mix = 1 - this.transitionRemaining-- / this.fadeFrames;
          value = this.transitionFrom * (1 - mix) + value * mix;
        }
      } else if (this.tailRemaining > 0) value = this.tail * --this.tailRemaining / this.fadeFrames;
      output[i] = this.last = value;
    }
  }
}
