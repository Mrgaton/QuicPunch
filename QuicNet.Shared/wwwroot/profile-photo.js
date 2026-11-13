import { openAvatarMedia, MAX_AVATAR_BYTES } from './avatar-media.js';

/* Crop local stills, GIF/AVIF animations and silent video clips. Upload only the final AVIF. */
document.addEventListener('DOMContentLoaded', () => {
  const $ = id => document.getElementById(id);
  if (!$('profileChoosePhoto')) return;
  const modal = $('avatarCropModal'), canvas = $('avatarCropCanvas'), preview = $('avatarCropPreview');
  const fileInput = $('profilePhotoFile'), zoomInput = $('avatarCropZoom');
  let source = null, image = null, rotation = 0, centerX = 0, centerY = 0, zoom = 1, drag = null;
  let worker = null, encoding = false, reading = false, uploading = false, previousFocus = null;
  let avatarUrl = '', loadVersion = 0, job = 0, playing = false, previewTimer = 0, previewGeneration = 0;
  let clipStart = 0, clipLength = 3;
  const status = message => { $('avatarCropStatus').textContent = message; };
  const background = () => $('avatarAnimationBackground').value;
  function setPhoto(url) {
    avatarUrl = url || ''; const host = $('profilePhotoPreview'); host.replaceChildren();
    if (avatarUrl) { const img = new Image(); img.src = avatarUrl; img.alt = 'Tu avatar'; host.append(img); }
    else host.textContent = QP.initials($('profileDisplayName')?.value || $('profileDeviceName')?.textContent || 'Tú');
    $('profileRemovePhoto').disabled = !avatarUrl || encoding || reading;
  }
  async function load() {
    const revision = ++loadVersion;
    try { const profile = await QP.api('/api/profile'); if (revision === loadVersion) setPhoto(profile.avatarUrl); }
    catch (error) { $('profilePhotoStatus').textContent = error.message; }
  }
  function crop() {
    const width = rotation % 2 ? source.height : source.width, height = rotation % 2 ? source.width : source.height;
    const side = Math.min(width, height) / zoom;
    centerX = Math.max(side / 2, Math.min(width - side / 2, centerX));
    centerY = Math.max(side / 2, Math.min(height - side / 2, centerY));
    return { x: centerX - side / 2, y: centerY - side / 2, side, size: Math.max(1, Math.min(source.animated ? 320 : 512, Math.floor(side))) };
  }
  function orient(drawable) {
    const result = image || document.createElement('canvas');
    const scale = Math.min(1, 1024 / Math.max(source.width, source.height));
    const width = Math.max(1, Math.round((rotation % 2 ? source.height : source.width) * scale));
    const height = Math.max(1, Math.round((rotation % 2 ? source.width : source.height) * scale));
    if (result.width !== width || result.height !== height) { result.width = width; result.height = height; }
    const ctx = result.getContext('2d'); ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, width, height); ctx.translate(result.width / 2, result.height / 2); ctx.rotate(rotation * Math.PI / 2);
    const drawWidth = rotation % 2 ? height : width, drawHeight = rotation % 2 ? width : height;
    ctx.drawImage(drawable, -drawWidth / 2, -drawHeight / 2, drawWidth, drawHeight); return result;
  }
  function draw() {
    if (!image || !source) return;
    const { x, y, side, size } = crop(), ctx = canvas.getContext('2d'); ctx.clearRect(0, 0, 512, 512);
    if (source.animated) { ctx.fillStyle = background(); ctx.fillRect(0, 0, 512, 512); }
    const scaleX = image.width / (rotation % 2 ? source.height : source.width);
    const scaleY = image.height / (rotation % 2 ? source.width : source.height);
    ctx.imageSmoothingQuality = 'high'; ctx.drawImage(image, x * scaleX, y * scaleY, side * scaleX, side * scaleY, 0, 0, 512, 512);
    const small = preview.getContext('2d'); small.clearRect(0, 0, 80, 80); small.drawImage(canvas, 0, 0, 80, 80);
    $('avatarCropSize').textContent = `Hasta ${size} × ${size} · AVIF${source.animated ? ' animado' : ''} · máximo 512 KB`;
    $('avatarCropZoomValue').textContent = `${Math.round(zoom * 100)} %`;
  }
  function reset() { if (!image) return; zoom = 1; zoomInput.value = '1'; centerX = (rotation % 2 ? source.height : source.width) / 2; centerY = (rotation % 2 ? source.width : source.height) / 2; draw(); }
  function stopPreview() { playing = false; previewGeneration++; clearTimeout(previewTimer); $('avatarClipPlay').textContent = 'Reproducir vista previa'; }
  function startPreview() {
    stopPreview(); if (!source?.animated || encoding) return;
    playing = true; $('avatarClipPlay').textContent = 'Pausar vista previa'; const generation = previewGeneration, started = performance.now();
    const tick = async () => {
      if (!playing || generation !== previewGeneration || encoding || !source) return;
      try {
        const frame = await source.frameAt(clipStart + ((performance.now() - started) / 1000) % clipLength);
        try { if (generation === previewGeneration && source && !encoding) { image = orient(frame.drawable); draw(); } }
        finally { frame.release(); }
      } catch (error) { QP.reportError?.(error, 'Vista previa del avatar'); stopPreview(); return; }
      if (playing && generation === previewGeneration) previewTimer = setTimeout(tick, 125);
    };
    tick();
  }
  function syncClip() {
    if (!source?.animated) return;
    clipStart = Math.max(0, Math.min(clipStart, Math.max(0, source.duration - 0.05)));
    const maximum = Math.min(10, source.duration - clipStart);
    clipLength = Math.max(Math.min(0.1, maximum), Math.min(clipLength, maximum));
    $('avatarClipStart').max = String(Math.max(0, source.duration - 0.05)); $('avatarClipStart').value = String(clipStart);
    $('avatarClipLength').min = String(Math.min(0.1, maximum)); $('avatarClipLength').max = String(maximum); $('avatarClipLength').value = String(clipLength);
    $('avatarClipStartValue').textContent = `${clipStart.toFixed(2)} s`; $('avatarClipLengthValue').textContent = `${clipLength.toFixed(2)} s`;
    startPreview();
  }
  function busy(value) {
    encoding = value; if (value) stopPreview(); modal.setAttribute('aria-busy', String(value));
    for (const id of ['avatarCropSave','avatarCropRotate','avatarCropReset','avatarCropZoom','avatarClipStart','avatarClipLength','avatarClipPlay','avatarAnimationFps','avatarAnimationBackground']) $(id).disabled = value;
    $('profileChoosePhoto').disabled = value; $('profileRemovePhoto').disabled = value || !avatarUrl;
    $('avatarCropCancel').textContent = value ? 'Cancelar conversión' : 'Cancelar';
  }
  function close() {
    if (uploading) return;
    job++; ++loadVersion; stopPreview(); worker?.terminate(); worker = null;
    source?.close(); source = null; image = null; drag = null;
    busy(false); modal.classList.add('hidden'); requestAnimationFrame(() => previousFocus?.focus());
  }
  async function choose(file) {
    if (!file || reading || encoding) return;
    reading = true; $('profileChoosePhoto').disabled = true; $('profilePhotoStatus').textContent = 'Abriendo contenido…';
    try {
      const media = await openAvatarMedia(file); stopPreview(); source?.close(); source = media; rotation = 0; job++;
      const frame = await source.frameAt(0); try { image = orient(frame.drawable); } finally { frame.release(); }
      previousFocus = document.activeElement; reset(); clipStart = 0; clipLength = Math.min(3, source.duration || 3);
      $('avatarAnimationControls').classList.toggle('hidden', !source.animated);
      status('El original se queda en tu dispositivo.'); modal.classList.remove('hidden'); canvas.focus();
      if (source.animated) syncClip(); $('profilePhotoStatus').textContent = '';
    } catch (error) { QP.reportError?.(error, 'Abrir avatar'); source?.close(); source = null; image = null; $('profilePhotoStatus').textContent = `No se pudo abrir: ${error.message || String(error)}`; }
    finally { reading = false; $('profileChoosePhoto').disabled = false; fileInput.value = ''; }
  }
  $('profileChoosePhoto').addEventListener('click', () => fileInput.click());
  fileInput.addEventListener('change', () => choose(fileInput.files?.[0]));
  for (const id of ['avatarCropClose','avatarCropCancel']) $(id).addEventListener('click', close);
  modal.addEventListener('click', event => { if (event.target === modal) close(); });
  modal.addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); close(); } });
  zoomInput.addEventListener('input', () => { zoom = Number(zoomInput.value); draw(); });
  $('avatarCropReset').addEventListener('click', reset);
  $('avatarCropRotate').addEventListener('click', () => {
    if (!image || encoding) return;
    const rotated = document.createElement('canvas'); rotated.width = image.height; rotated.height = image.width;
    const ctx = rotated.getContext('2d'); ctx.translate(rotated.width, 0); ctx.rotate(Math.PI / 2); ctx.drawImage(image, 0, 0);
    rotation = (rotation + 1) % 4; image = rotated; reset();
  });
  $('avatarClipPlay').addEventListener('click', () => playing ? stopPreview() : startPreview());
  $('avatarClipStart').addEventListener('input', () => { clipStart = Number($('avatarClipStart').value); syncClip(); });
  $('avatarClipLength').addEventListener('input', () => { clipLength = Number($('avatarClipLength').value); syncClip(); });
  $('avatarAnimationBackground').addEventListener('input', draw);
  canvas.addEventListener('pointerdown', event => {
    if (!image || encoding) return; drag = { pointer: event.pointerId, x: event.clientX, y: event.clientY, cx: centerX, cy: centerY }; canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointermove', event => {
    if (!drag || drag.pointer !== event.pointerId || encoding) return;
    const scale = crop().side / canvas.getBoundingClientRect().width;
    centerX = drag.cx - (event.clientX - drag.x) * scale; centerY = drag.cy - (event.clientY - drag.y) * scale; draw();
  });
  for (const name of ['pointerup','pointercancel','lostpointercapture']) canvas.addEventListener(name, () => { drag = null; });
  canvas.addEventListener('keydown', event => {
    if (!image || encoding || !['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key)) return;
    event.preventDefault(); const step = crop().side / (event.shiftKey ? 10 : 50);
    centerX += event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0;
    centerY += event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0; draw();
  });
  $('avatarCropSave').addEventListener('click', async () => {
    if (!image || !source || encoding) return;
    const currentJob = ++job, region = crop(), frames = [], count = source.animated ? Math.max(2, Math.ceil(clipLength * Number($('avatarAnimationFps').value))) : 1;
    const milliseconds = Math.max(count * 40, Math.round(clipLength * 1000));
    const output = document.createElement('canvas'); output.width = output.height = region.size;
    const ctx = output.getContext('2d'); ctx.imageSmoothingQuality = 'high'; busy(true);
    try {
      for (let index = 0; index < count; index++) {
        status(`Preparando fotograma ${index + 1}/${count}…`);
        const frame = await source.frameAt(source.animated ? clipStart + index * clipLength / count : 0);
        try {
          if (currentJob !== job || !source) return;
          ctx.clearRect(0, 0, region.size, region.size);
          if (source.animated) { ctx.fillStyle = background(); ctx.fillRect(0, 0, region.size, region.size); }
          // Sample the original directly into the small output. The bounded preview
          // must not limit export detail when zooming into a large photograph.
          ctx.save();
          const scale = region.size / region.side;
          ctx.scale(scale, scale); ctx.translate(-region.x, -region.y);
          ctx.translate((rotation % 2 ? source.height : source.width) / 2, (rotation % 2 ? source.width : source.height) / 2);
          ctx.rotate(rotation * Math.PI / 2);
          ctx.drawImage(frame.drawable, -source.width / 2, -source.height / 2, source.width, source.height);
          ctx.restore();
          frames.push({ width: region.size, height: region.size, pixels: ctx.getImageData(0, 0, region.size, region.size).data.buffer,
            duration: Math.round((index + 1) * milliseconds / count) - Math.round(index * milliseconds / count) });
        } finally { frame.release(); }
      }
      if (currentJob !== job) return;
      status('Comprimiendo en AVIF…');
      const currentWorker = new Worker('/avatar-encode-worker.js', { type: 'module' }); worker = currentWorker;
      const fail = message => { if (worker !== currentWorker) return; currentWorker.terminate(); worker = null; busy(false); status(message); };
      currentWorker.onerror = event => { QP.reportError?.(event.error || new Error(event.message), 'Worker AVIF'); fail('No se pudo iniciar el codificador AVIF. Vuelve a intentarlo.'); };
      currentWorker.onmessage = async ({ data }) => {
        if (worker !== currentWorker || currentJob !== job) return;
        if (typeof data.progress === 'number') { status(`Comprimiendo en AVIF: ${Math.round(data.progress * 100)} %${data.attempt > 1 ? ' · ajustando a 512 KB' : ''}`); return; }
        if (data.error) { const error = new Error(data.error); if (data.stack) error.stack = data.stack; QP.reportError?.(error, 'Codificar AVIF'); fail(data.error); return; }
        currentWorker.terminate(); worker = null;
        if (!(data.buffer instanceof ArrayBuffer) || data.buffer.byteLength > MAX_AVATAR_BYTES) { busy(false); status('El resultado supera los 512 KB. Acorta el fragmento o cambia el encuadre.'); return; }
        status('Guardando avatar…'); uploading = true; $('avatarCropCancel').disabled = $('avatarCropClose').disabled = true;
        try {
          const profile = await QP.api('/api/profile/avatar', { method: 'POST', headers: { 'Content-Type': 'image/avif' }, body: data.buffer });
          setPhoto(profile.avatarUrl); $('profilePhotoStatus').textContent = `Avatar guardado · ${(data.buffer.byteLength / 1024).toFixed(1)} KB`;
          uploading = false; close(); await QP.getStatus(true);
        } catch (error) { uploading = false; busy(false); status(error.message); }
        finally { uploading = false; $('avatarCropCancel').disabled = $('avatarCropClose').disabled = false; }
      };
      currentWorker.postMessage({ frames }, frames.map(f => f.pixels));
    } catch (error) { QP.reportError?.(error, 'Preparar avatar'); if (currentJob === job) { busy(false); status(error.message || String(error)); } }
  });
  $('profileRemovePhoto').addEventListener('click', async () => {
    if (encoding || reading || !avatarUrl) return;
    $('profileRemovePhoto').disabled = true; $('profileChoosePhoto').disabled = true;
    try { const profile = await QP.api('/api/profile/avatar/remove', { method: 'POST', body: '{}' }); ++loadVersion; setPhoto(profile.avatarUrl); $('profilePhotoStatus').textContent = 'Avatar eliminado.'; await QP.getStatus(true); }
    catch (error) { $('profilePhotoStatus').textContent = error.message; }
    finally { $('profileChoosePhoto').disabled = false; $('profileRemovePhoto').disabled = !avatarUrl; }
  });
  QP.on('view_changed', () => { if (QP.currentPage === 'settings') load(); });
  load();
});
