async function openNativeScreenSourcePicker({
  initialSource, getSources, onSources, updateEncodingSummary, setCancel, onCancel, onEncodingSettings
}) {
  const modal = document.getElementById('screenNativeCaptureModal');
  const grid = document.getElementById('screenCaptureGallery');
  const search = document.getElementById('screenCaptureSearch');
  const preview = document.getElementById('screenCapturePreview');
  if (!modal || !grid || !search || !preview) throw new Error('No se pudo abrir el selector de captura.');
  let selected = initialSource, sources = [], matches = [], category = 'all', previewTimer = null, closed = false, refreshVersion = 0;
  const thumbnails = new Map();
  const failedThumbnails = new Set();
  let cancelPreview = null, cancelThumbnail = null, activeThumbnail = null, thumbnailQueue = [], previewLoading = false, filterTimer = null;
  const status = document.getElementById('screenCaptureSelection');
  const start = document.getElementById('screenNativeCaptureStart');
  start.disabled = true;
  start.textContent = 'Compartir fuente seleccionada';
  document.getElementById('screenNativeCaptureWeb').classList.add('hidden');
  const loadPreviewImage = (image, source, completed) => {
    let settled = false;
    const finish = (success, timedOut = false) => {
      if (settled) return;
      settled = true; clearTimeout(deadline);
      image.onload = image.onerror = null;
      if (!success) image.removeAttribute('src');
      completed(success, timedOut);
    };
    const deadline = setTimeout(() => finish(false, true), 8000);
    image.onload = () => finish(image.naturalWidth > 0);
    image.onerror = () => finish(false);
    image.src = '/api/voice/screen-share/preview?source=' + encodeURIComponent(source) + '&t=' + Date.now();
    return () => {
      if (settled) return;
      settled = true; clearTimeout(deadline);
      image.onload = image.onerror = null; image.removeAttribute('src');
    };
  };
  const stopPreview = () => {
    clearTimeout(previewTimer); previewTimer = null;
    cancelPreview?.(); cancelPreview = null; previewLoading = false;
  };
  const stopThumbnail = (requeue = false) => {
    cancelThumbnail?.(); cancelThumbnail = null;
    if (requeue && activeThumbnail) thumbnailQueue.unshift(activeThumbnail);
    activeThumbnail = null;
  };
  const loadNextThumbnail = () => {
    if (closed || previewLoading || activeThumbnail || !thumbnailQueue.length) return;
    const item = activeThumbnail = thumbnailQueue.shift();
    item.detail.textContent = item.dimensions + ' · cargando vista previa…';
    cancelThumbnail = loadPreviewImage(item.image, item.id, success => {
      activeThumbnail = null; cancelThumbnail = null;
      if (!success) failedThumbnails.add(item.id);
      item.detail.textContent = item.dimensions + (success ? '' : ' · vista previa no disponible');
      loadNextThumbnail();
    });
  };
  const showPreview = () => {
    if (closed || !selected) return;
    stopPreview();
    const source = sources.find(item => item.Id === selected);
    previewLoading = true;
    if (status) status.textContent = (source?.Name || 'Fuente') + ' · cargando vista previa…';
    cancelPreview = loadPreviewImage(preview, selected, (success, timedOut) => {
      cancelPreview = null; previewLoading = false;
      if (status) status.textContent = success
        ? source.Name + ' · fuente completa, sin recortar'
        : timedOut ? 'La vista previa no ha respondido en 8 segundos. Pulsa Actualizar para reintentar o selecciona otra fuente.'
          : 'No se puede previsualizar esta fuente. Restaura la ventana o selecciona otra pantalla.';
      // Failed sources retry only through a deliberate selection or refresh.
      if (success && !closed) previewTimer = setTimeout(showPreview, 2500);
      loadNextThumbnail();
    });
  };
  const select = (id, focus = false) => {
    const previous = selected;
    selected = matches.some(source => source.Id === id) ? id : null;
    for (const button of grid.querySelectorAll('.capture-source-card')) {
      const active = button.dataset.sourceId === selected;
      button.classList.toggle('selected', active);
      button.setAttribute('aria-selected', String(active)); button.tabIndex = active ? 0 : -1;
      if (active && focus) { button.focus({preventScroll:true}); button.scrollIntoView({block:'nearest',inline:'nearest'}); }
    }
    const source = matches.find(item => item.Id === selected);
    if (status) status.textContent = source ? source.Name + ' · se compartirá completa, sin recortar' : 'Selecciona una pantalla o ventana.';
    start.disabled = !source;
    if (!source) {
      stopPreview(); preview.removeAttribute('src');
    } else if (previous !== selected || !preview.getAttribute('src')) {
      stopThumbnail(true); showPreview();
    }
  };
  const render = () => {
    clearTimeout(filterTimer); filterTimer = null;
    stopPreview(); stopThumbnail(); thumbnailQueue = [];
    preview.removeAttribute('src');
    for (const [id, kind] of [['screenCaptureAll','all'], ['screenCaptureDisplays','display'], ['screenCaptureWindows','window']]) {
      const button = document.getElementById(id);
      button.classList.toggle('active', category === kind); button.setAttribute('aria-pressed', String(category === kind));
    }
    grid.replaceChildren();
    const query = search.value.trim().toLocaleLowerCase();
    matches = sources.filter(source => (category === 'all' || (source.Kind || 'display') === category) &&
      source.Name.toLocaleLowerCase().includes(query));
    for (const source of matches) {
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'capture-source-card' + (source.Id === selected ? ' selected' : '');
      button.dataset.sourceId = source.Id; button.setAttribute('role', 'option'); button.title = source.Name;
      let image = thumbnails.get(source.Id);
      if (!image) {
        image = document.createElement('img');
        image.alt = 'Vista previa de ' + source.Name;
        thumbnails.set(source.Id, image);
      }
      const name = document.createElement('strong'); name.textContent = source.Name;
      const detail = document.createElement('span');
      detail.textContent = (source.Width ? source.Width + '×' + source.Height : 'Tamaño al iniciar') +
        ((source.Kind || 'display') === 'window' ? ' · ventana' : ' · pantalla');
      const dimensions = detail.textContent;
      button.setAttribute('aria-label', source.Name + ' · ' + dimensions);
      const loaded = image.complete && image.naturalWidth > 0;
      detail.textContent = dimensions + (loaded ? '' : failedThumbnails.has(source.Id) ? ' · vista previa no disponible' : ' · vista previa pendiente');
      if (!loaded && !failedThumbnails.has(source.Id)) thumbnailQueue.push({ id: source.Id, image, detail, dimensions });
      button.append(image, name, detail);
      button.onclick = () => select(source.Id);
      grid.append(button);
    }
    select(matches.some(source => source.Id === selected) ? selected : matches[0]?.Id);
    loadNextThumbnail();
    document.getElementById('screenCaptureResults').textContent = matches.length === 1 ? '1 fuente' : matches.length + ' fuentes';
    if (!matches.length) grid.textContent = 'No hay fuentes que coincidan. Cambia la búsqueda o actualiza las ventanas.';
  };
  const refresh = async () => {
    const version = ++refreshVersion;
    if (status) status.textContent = 'Buscando pantallas y ventanas…';
    try {
      const data = await getSources();
      if (closed || version !== refreshVersion) return;
      sources = data.sources || [];
      onSources(sources);
      if (!sources.some(source => source.Id === selected)) selected = sources.find(source => source.Kind !== 'window')?.Id || sources[0]?.Id;
      render();
    } catch (error) { if (!closed && version === refreshVersion && status) status.textContent = 'No se pudieron consultar las fuentes: ' + error.message; }
  };
  updateEncodingSummary();
  modal.classList.remove('hidden');
  search.value = ''; search.oninput = () => {
    stopPreview(); stopThumbnail(); thumbnailQueue = [];
    clearTimeout(filterTimer); start.disabled = true;
    filterTimer = setTimeout(render, 180);
  };
  return new Promise(resolve => {
    const keydown = event => {
      if (closed || modal.classList.contains('hidden') || event.isComposing || !modal.contains(event.target)) return;
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); done('cancel'); return; }
      if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey && event.key.length > 1) return;
      if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) {
        event.preventDefault(); event.stopPropagation();
        const buttons = [...grid.querySelectorAll('.capture-source-card')];
        const current = buttons.find(button => button.dataset.sourceId === selected);
        if (!current) return;
        const origin = current.getBoundingClientRect();
        const horizontal = event.key === 'ArrowLeft' || event.key === 'ArrowRight';
        const forward = event.key === 'ArrowRight' || event.key === 'ArrowDown';
        const candidates = buttons.filter(button => button !== current).map(button => ({button,rect:button.getBoundingClientRect()}))
          .filter(({rect}) => horizontal
            ? Math.abs(rect.top - origin.top) < 2 && (forward ? rect.left > origin.left : rect.left < origin.left)
            : forward ? rect.top > origin.top + 2 : rect.top < origin.top - 2)
          .sort((a,b) => horizontal
            ? Math.abs(a.rect.left-origin.left)-Math.abs(b.rect.left-origin.left)
            : Math.abs(a.rect.top-origin.top)-Math.abs(b.rect.top-origin.top) || Math.abs(a.rect.left-origin.left)-Math.abs(b.rect.left-origin.left));
        select(candidates[0]?.button.dataset.sourceId || selected, true); return;
      }
      if (event.key === 'Enter') {
        // Tabbed-to actions retain their normal Enter activation.
        const button = event.target.closest('button');
        if (button && !grid.contains(button) && button !== start) return;
        event.preventDefault(); event.stopPropagation();
        if (!start.disabled && selected) done(selected);
        return;
      }
      if (event.key.length === 1 && !event.target.closest('input,textarea,select,[contenteditable]')) {
        event.preventDefault(); event.stopPropagation(); search.focus({preventScroll:true});
        search.setRangeText(event.key, search.selectionStart ?? search.value.length, search.selectionEnd ?? search.value.length, 'end');
        search.dispatchEvent(new Event('input', {bubbles:true}));
      }
    };
    const done = choice => {
      if (closed) return;
      closed = true;
      setCancel(null);
      if (choice === 'cancel') onCancel();
      clearTimeout(filterTimer);
      stopPreview(); stopThumbnail(); thumbnailQueue = [];
      preview.removeAttribute('src');
      for (const image of thumbnails.values()) image.removeAttribute('src');
      thumbnails.clear(); grid.replaceChildren(); modal.classList.add('hidden');
      document.removeEventListener('keydown', keydown, true);
      search.oninput = null;
      for (const id of ['screenNativeCaptureStart', 'screenNativeCaptureWeb', 'screenNativeCaptureCancel', 'screenCaptureRefresh', 'screenCaptureAll', 'screenCaptureDisplays', 'screenCaptureWindows', 'screenCaptureEncodingBtn'])
        document.getElementById(id).onclick = null;
      resolve(choice);
    };
    setCancel(() => done('cancel'));
    document.addEventListener('keydown', keydown, true);
    document.getElementById('screenNativeCaptureStart').onclick = () => { if (!start.disabled && selected) done(selected); };
    document.getElementById('screenNativeCaptureWeb').onclick = () => done('web');
    document.getElementById('screenCaptureEncodingBtn').onclick = () => { modal.classList.add('hidden'); onEncodingSettings(); };
    document.getElementById('screenNativeCaptureCancel').onclick = () => done('cancel');
    document.getElementById('screenCaptureRefresh').onclick = () => {
      stopPreview(); stopThumbnail(); thumbnailQueue = [];
      failedThumbnails.clear(); refresh();
    };
    for (const [id, kind] of [['screenCaptureAll','all'], ['screenCaptureDisplays','display'], ['screenCaptureWindows','window']])
      document.getElementById(id).onclick = () => { category = kind; render(); };
    refresh();
  });
}
