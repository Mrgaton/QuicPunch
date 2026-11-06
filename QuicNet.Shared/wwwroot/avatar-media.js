/* Local-only media decoding. ImageDecoder preserves GIF/WebP/AVIF animation and disposal. */
const rasterTypes = ['image/jpeg','image/png','image/webp','image/avif','image/gif'];
export const MAX_AVATAR_BYTES = 512 * 1024;
export function checkDimensions(width, height) {
  if (!width || !height || width * height > 64 * 1024 * 1024 || width > 16384 || height > 16384) throw new Error('El contenido tiene una resolución demasiado grande. Usa una copia de menor resolución.');
}
function serialFrames(source) {
  let pending = Promise.resolve();
  const get = source.frameAt;
  source.frameAt = time => { const next = pending.then(() => get(time)); pending = next.catch(() => {}); return next; };
  return source;
}
function videoEvent(video, name) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => finish(new Error('No se pudo leer el vídeo. Prueba un MP4 o WebM compatible.')), 15000);
    const success = () => finish(); const error = () => finish(new Error('El navegador no puede decodificar este vídeo. Prueba un MP4 o WebM compatible.'));
    function finish(error) { clearTimeout(timer); video.removeEventListener(name, success); video.removeEventListener('error', error); error ? reject(error) : resolve(); }
    video.addEventListener(name, success, { once: true }); video.addEventListener('error', error, { once: true });
  });
}
export async function openAvatarMedia(file) {
  if (!file || file.size > 50 * 1024 * 1024) throw new Error('Escoge una imagen, GIF, AVIF animado o vídeo de hasta 50 MB.');
  const isVideo = file.type.startsWith('video/') || /\.(mp4|webm|mov|m4v|mkv)$/i.test(file.name);
  if (isVideo) {
    const video = document.createElement('video'), url = URL.createObjectURL(file);
    video.muted = true; video.playsInline = true; video.preload = 'auto';
    const ready = videoEvent(video, 'loadeddata'); video.src = url; video.load();
    try {
      await ready; checkDimensions(video.videoWidth, video.videoHeight);
      if (!Number.isFinite(video.duration) || video.duration <= 0) throw new Error('El vídeo no tiene una duración válida.');
      return serialFrames({ width: video.videoWidth, height: video.videoHeight, duration: video.duration, animated: true,
        async frameAt(time) {
          const target = Math.max(0, Math.min(video.duration - 0.001, time));
          if (Math.abs(video.currentTime - target) > 0.001 || video.readyState < 2) { const seeked = videoEvent(video, 'seeked'); video.currentTime = target; await seeked; }
          return { drawable: video, release() {} };
        }, close() { video.pause(); video.removeAttribute('src'); video.load(); URL.revokeObjectURL(url); }
      });
    } catch (error) { video.removeAttribute('src'); video.load(); URL.revokeObjectURL(url); throw error; }
  }
  if (!rasterTypes.includes(file.type)) throw new Error('Formato no compatible. Escoge JPG, PNG, WebP, GIF, AVIF o un vídeo.');
  if (typeof ImageDecoder !== 'undefined' && file.type !== 'image/jpeg' && await ImageDecoder.isTypeSupported(file.type)) {
    const decoder = new ImageDecoder({ data: await file.arrayBuffer(), type: file.type, preferAnimation: true });
    try {
      await decoder.tracks.ready; await decoder.completed;
      const track = decoder.tracks.selectedTrack;
      if (!track || track.frameCount > 600) throw new Error('Usa una animación de hasta 600 fotogramas.');
      const first = (await decoder.decode({ frameIndex: 0 })).image;
      const width = first.displayWidth, height = first.displayHeight; checkDimensions(width, height);
      const animated = track.animated && track.frameCount > 1;
      if (!animated) {
        const bitmap = await createImageBitmap(first); first.close(); decoder.close();
        return { width, height, animated: false, duration: 0, async frameAt() { return { drawable: bitmap, release() {} }; }, close() { bitmap.close(); } };
      }
      const ends = []; let duration = 0;
      for (let index = 0; index < track.frameCount; index++) {
        const frame = index === 0 ? first : (await decoder.decode({ frameIndex: index })).image;
        checkDimensions(frame.displayWidth, frame.displayHeight);
        if (frame.displayWidth !== width || frame.displayHeight !== height) { frame.close(); throw new Error('Los fotogramas deben mantener la misma resolución.'); }
        duration += Math.max(0.01, Math.min(10, (frame.duration || 100000) / 1000000)); ends.push(duration); frame.close();
      }
      return serialFrames({ width, height, duration, animated: true,
        async frameAt(time) { let index = ends.findIndex(end => end > time); if (index < 0) index = ends.length - 1; const { image } = await decoder.decode({ frameIndex: index }); return { drawable: image, release() { image.close(); } }; },
        close() { decoder.close(); }
      });
    } catch (error) { decoder.close(); throw error; }
  }
  // Never flatten an animation silently on a browser without an animation decoder.
  let animationNeedsDecoder = file.type === 'image/gif';
  if (file.type === 'image/avif' || file.type === 'image/webp') {
    const header = new Uint8Array(await file.slice(0, 128).arrayBuffer());
    if (file.type === 'image/avif') {
      animationNeedsDecoder = false;
      const type = new TextDecoder('ascii');
      if (header.length >= 16 && type.decode(header.subarray(4, 8)) === 'ftyp') {
        const length = Math.min(header.length, new DataView(header.buffer).getUint32(0));
        for (let at = 8; at + 4 <= length; at += 4) if (at !== 12 && type.decode(header.subarray(at, at + 4)) === 'avis') animationNeedsDecoder = true;
      }
    } else animationNeedsDecoder = header.length > 20 && new TextDecoder('ascii').decode(header.subarray(12, 16)) === 'VP8X' && !!(header[20] & 2);
  }
  if (animationNeedsDecoder) throw new Error('Este navegador no puede leer animaciones de este formato. Actualiza el navegador o Android WebView.');
  const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  try { checkDimensions(bitmap.width, bitmap.height); } catch (error) { bitmap.close(); throw error; }
  return { width: bitmap.width, height: bitmap.height, duration: 0, animated: false, async frameAt() { return { drawable: bitmap, release() {} }; }, close() { bitmap.close(); } };
}
