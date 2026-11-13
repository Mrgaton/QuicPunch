import createEncoder from './vendor/avif/codec/enc/avif_enc.js';
import { defaultOptions } from './vendor/avif/meta.js';
import { readStillFrame, muxAvifSequence } from './avatar-avif-sequence.js';
import { MAX_AVATAR_BYTES } from './avatar-media.js';

// Stills use speed 5 for compact output; animations use speed 6 because every
// frame and size-budget retry requires a full encode. Avoid archival speed 2.
function resize(frame, size) {
  const source = new OffscreenCanvas(frame.width, frame.height), out = new OffscreenCanvas(size, size);
  source.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(frame.pixels), frame.width, frame.height), 0, 0);
  const ctx = out.getContext('2d'); ctx.imageSmoothingQuality = 'high'; ctx.drawImage(source, 0, 0, size, size);
  return new Uint8Array(ctx.getImageData(0, 0, size, size).data.buffer);
}
self.onmessage = async ({ data }) => {
  try {
    const frames = data.frames || [{ width: data.width, height: data.height, pixels: data.pixels, duration: 0 }];
    if (!frames.length || frames.length > 120 || frames.some(f => !Number.isInteger(f.width) || f.width !== f.height || f.width < 1 || f.width > 512 || f.pixels.byteLength !== f.width * f.height * 4)) throw new Error('Encuadre no válido.');
    const animated = frames.length > 1, encoder = await createEncoder();
    const qualities = animated ? [70, 50, 35, 20] : [75, 60, 45, 30];
    for (let attempt = 0; attempt < qualities.length; attempt++) {
      const factor = attempt < 2 ? 1 : attempt === 2 ? 0.8 : 0.6;
      const size = Math.max(1, Math.floor(frames[0].width * factor));
      const encodedFrames = []; let encodedBytes = 0, overBudget = false;
      for (let index = 0; index < frames.length; index++) {
        const frame = frames[index];
        const rgba = size === frame.width ? new Uint8Array(frame.pixels) : resize(frame, size);
        const encoded = encoder.encode(rgba, size, size, { ...defaultOptions, speed: animated ? 6 : 5, quality: qualities[attempt], qualityAlpha: 100, subsample: animated ? 1 : 3 });
        if (!encoded?.byteLength) throw new Error('No se pudo codificar el AVIF.');
        const buffer = encoded.buffer.slice(encoded.byteOffset, encoded.byteOffset + encoded.byteLength);
        encodedFrames.push(animated ? { ...readStillFrame(buffer), duration: frame.duration } : buffer);
        encodedBytes += animated ? encodedFrames.at(-1).data.byteLength : buffer.byteLength;
        self.postMessage({ progress: (index + 1) / frames.length, attempt: attempt + 1 });
        if (animated && encodedBytes > MAX_AVATAR_BYTES) { overBudget = true; break; }
      }
      if (overBudget) continue;
      const buffer = animated ? muxAvifSequence(encodedFrames) : encodedFrames[0];
      if (buffer.byteLength <= MAX_AVATAR_BYTES) { self.postMessage({ buffer, size, animated }, [buffer]); return; }
    }
    throw new Error('No se ha podido reducir el resultado a 512 KB. Acorta el fragmento o elige otro encuadre.');
  } catch (error) { self.postMessage({ error: error.message || String(error) || 'No se pudo crear el AVIF.', stack: error.stack }); }
};
