(() => {
  let isRunning = false, phase = '', planMode = 'All', state = 'idle';
  let activePeerId = null, peerName = '', testId = null, baseline = null;
  let stages = [], liveUploadMbps = null, liveDownloadMbps = null;
  let liveUploadedBytes = 0, liveDownloadedBytes = 0, cancelIntent = false, revision = 0;
  let requestVersion = 0, clearingHistory = false;
  let phaseElapsedSeconds = 0;
  const history = [];
  const labels = { All: 'Prueba completa', Both: 'Subida y bajada simultáneas', Upload: 'Solo subida', Download: 'Solo bajada', 'Latencia en reposo': 'Latencia inicial' };
  const stateLabels = { idle: 'Sin iniciar', connecting: 'Conectando', running: 'En curso', completed: 'Completada', interrupted: 'Interrumpida', cancelled: 'Cancelada', error: 'No iniciada' };
  const el = id => document.getElementById(id);
  const setText = (id, text) => { const node = el(id); if (node) node.textContent = text; };
  const hide = (id, hidden) => el(id)?.classList.toggle('hidden', hidden);
  const ms = value => Number.isFinite(value) && value >= 0 ? value.toFixed(2) + ' ms' : 'No disponible';
  const speed = value => Number.isFinite(value) && value >= 0 ? value.toFixed(1) + ' Mbps' : 'Sin resultado';
  const bytes = value => QP.fmtBytes ? QP.fmtBytes(value || 0) : Math.round((value || 0) / 1048576) + ' MB';
  const matches = data => data && (!isRunning || !activePeerId || data.peerId === activePeerId);
  const modes = mode => mode === 'All' ? ['Download', 'Upload', 'Both'] : [mode];
  const selectedMode = () => ({ all: 'All', both: 'Both', upload: 'Upload', download: 'Download' })[el('speedTestMode')?.value] || 'All';
  const cancelled = error => /cancelad|cancelled|canceled/i.test(error || '');
  const make = (tag, text, className) => {
    const node = document.createElement(tag);
    if (text != null) node.textContent = text;
    if (className) node.className = className;
    return node;
  };

  document.addEventListener('DOMContentLoaded', () => {
    el('startSpeedTestBtn')?.addEventListener('click', startTest);
    el('cancelSpeedTestBtn')?.addEventListener('click', cancelTest);
    el('speedTestPeerSelect')?.addEventListener('change', updateReadiness);
    for (const id of ['speedTestDuration', 'speedTestMode']) el(id)?.addEventListener('change', updateEstimate);
    el('clearSpeedTestHistoryBtn')?.addEventListener('click', async () => {
      if (clearingHistory || !history.length) return;
      clearingHistory = true;
      renderHistory();
      try {
        const result = await QP.api('/api/speedtest/history/clear', { method: 'POST' });
        if (result?.success === false) throw new Error('History clear failed');
        history.length = 0;
      } catch { QP.toast('No se pudo limpiar el historial. Vuelve a intentarlo.', 'danger'); }
      finally { clearingHistory = false; renderHistory(); }
    });
    if (window.QP && typeof QP.on === 'function') {
      QP.on('status_updated', updatePeerSelect);
      QP.on('view_changed', data => { if (data?.page === 'speedtest') return syncStatus(); });
      QP.on('speedtest_started', data => {
        if (!matches(data)) return;
        // The backend can deliver this after phase events, even after a very short run ends.
        if (!isRunning && state !== 'idle' && activePeerId === data.peerId) return;
        revision++;
        if (!isRunning) resetResults(data.peerId, data.peerName, data.mode);
        if (data.mode) planMode = data.mode;
        if (data.peerName) peerName = data.peerName;
        setState(phase ? 'running' : 'connecting');
        renderPhaseList();
      });
      QP.on('speedtest_phase', data => {
        if (!matches(data)) return;
        revision++;
        if (!isRunning) resetResults(data.peerId, data.peerName);
        if (data.baseline) baseline = data.baseline;
        startPhase(data.phase);
      });
      QP.on('speedtest_phase_completed', data => {
        if (!matches(data)) return;
        revision++;
        testId = data.testId || testId;
        stages = [...stages.filter(s => s.mode !== data.mode), data];
        if (data.baseline) baseline = data.baseline;
        liveUploadedBytes = liveDownloadedBytes = 0;
        liveUploadMbps = liveDownloadMbps = null;
        renderMeasurements(); renderStages(); renderPhaseList();
        remember({ ...data, mode: planMode, state: 'running', baseline, stages: [...stages] });
      });
      QP.on('speedtest_progress', data => {
        if (isRunning && matches(data)) { revision++; updateTelemetry(data); }
      });
      QP.on('speedtest_completed', data => {
        if (!matches(data)) return;
        revision++;
        const result = { ...data, state: 'completed' };
        showResult(result); remember(result);
        QP.toast('Prueba completada.', 'ok');
      });
      QP.on('speedtest_error', data => {
        if (!matches(data)) return;
        revision++;
        const result = data.result || localResult();
        result.state = cancelIntent || cancelled(data.error || result.error) ? 'cancelled' : 'interrupted';
        result.error = data.error || result.error;
        showResult(result); remember(result);
      });
      QP.on('peer_disconnected', data => {
        if (isRunning && (data?.peerId === activePeerId || data?.id === activePeerId)) {
          revision++;
          const result = { ...localResult(), state: 'interrupted', error: 'El contacto se ha desconectado.' };
          showResult(result); remember(result);
        }
      });
    }
    updateEstimate(); updateReadiness(); renderHistory();
    syncStatus();
  });

  async function syncStatus() {
    const version = revision;
    try {
      updatePeerSelect(await (QP.getStatus ? QP.getStatus() : QP.api('/api/status')));
      const status = await QP.api('/api/speedtest/status');
      // A delayed snapshot must not overwrite newer websocket events or a local action.
      if (version !== revision) return;
      if (Array.isArray(status?.history)) {
        history.length = 0; history.push(...status.history.map(withTime)); renderHistory();
      }
      if (status?.isRunning && status.active) {
        const a = status.active;
        resetResults(a.peerId, a.peerName, a.mode);
        stages = a.stages || []; baseline = a.baseline;
        startPhase(a.phase);
        updateTelemetry({ ...a, direction: a.phase, uploadMbps: a.lastUploadMbps,
          downloadMbps: a.lastDownloadMbps, currentMbps: a.phase === 'Upload' ? a.lastUploadMbps : a.lastDownloadMbps,
          totalSeconds: a.durationSeconds });
        renderStages();
      } else if (status?.lastResult) {
        showResult({ ...status.lastResult, error: status.lastResult.error || status.error });
      } else if (status?.error) {
        showResult({ ...localResult(), state: 'interrupted', error: status.error });
      }
    } catch { QP.toast('No se pudo actualizar el estado de la prueba. Vuelve a abrir esta pantalla para reintentarlo.', 'warn'); }
  }

  function updatePeerSelect(status) {
    const select = el('speedTestPeerSelect');
    if (!select) return;
    const previous = select.value;
    select.replaceChildren();
    const empty = make('option', 'Selecciona un contacto…'); empty.value = ''; select.appendChild(empty);
    for (const peer of (status?.peers || []).filter(p => p.isTrusted)) {
      const option = make('option', (peer.name || 'Contacto') + (Number.isFinite(peer.ping) ? ' (' + peer.ping + ' ms)' : ''));
      option.value = peer.id; select.appendChild(option);
    }
    if ([...select.options].some(o => o.value === previous)) select.value = previous;
    updateReadiness();
  }

  function updateReadiness() {
    const select = el('speedTestPeerSelect');
    const option = [...(select?.options || [])].find(o => o.value && o.value === select.value);
    const hasContacts = [...(select?.options || [])].some(o => o.value);
    if (el('startSpeedTestBtn')) el('startSpeedTestBtn').disabled = isRunning || !option;
    setText('speedTestStartHelp', !hasContacts ? 'Añade un contacto de confianza en Inicio para hacer una prueba.' : !option ? 'Selecciona un contacto para habilitar la prueba.' : 'Todo listo. Conectaremos con el contacto si hace falta.');
    hide('speedTestStartHelp', isRunning);
    if (state === 'idle') {
      setText('speedTestResultContext', option ? 'Este equipo ↔ ' + option.textContent.replace(/ \([\d.]+ ms\)$/, '') : 'Velocidad de transferencia y tiempo de respuesta.');
      setText('speedTestStatusNotice', option ? 'Todo listo. Pulsa «Iniciar prueba» para medir tu conexión.' : !hasContacts ? 'Necesitas un contacto de confianza para medir la conexión.' : 'Selecciona un contacto e inicia tu primera prueba.');
    }
  }

  function updateEstimate() {
    const seconds = Number(el('speedTestDuration')?.value || 10) * modes(selectedMode()).length;
    setText('speedTestEstimate', 'Unos ' + seconds + ' segundos, más la conexión y la medición inicial de latencia.');
    if (state === 'idle') { planMode = selectedMode(); renderPhaseList(); renderMeasurements(); }
  }

  function resetResults(peerId, name, mode) {
    activePeerId = peerId; peerName = name || ''; planMode = mode || selectedMode(); testId = null;
    phase = ''; stages = []; baseline = null; cancelIntent = false;
    phaseElapsedSeconds = 0;
    liveUploadedBytes = liveDownloadedBytes = 0; liveUploadMbps = liveDownloadMbps = null;
    hide('speedTestError', true);
    if (el('speedTestErrorDetails')) el('speedTestErrorDetails').open = false;
    setText('speedTestErrorTechnical', '');
    renderStages();
  }

  function setState(next) {
    const actionHadFocus = document.activeElement === el('startSpeedTestBtn') || document.activeElement === el('cancelSpeedTestBtn');
    state = next; isRunning = next === 'connecting' || next === 'running';
    el('speedTestResults')?.setAttribute('data-state', next);
    el('speedTestLayout')?.setAttribute('data-state', next);
    if (next !== 'idle' && el('speedTestActions')) el('speedTestResultActions')?.appendChild(el('speedTestActions'));
    hide('startSpeedTestBtn', isRunning); hide('cancelSpeedTestBtn', !isRunning);
    for (const id of ['speedTestPeerSelect', 'speedTestDuration', 'speedTestMode']) if (el(id)) el(id).disabled = isRunning;
    if (el('cancelSpeedTestBtn')) el('cancelSpeedTestBtn').disabled = cancelIntent;
    setText('startSpeedTestBtn', next === 'idle' ? 'Iniciar prueba' : 'Repetir prueba');
    setText('cancelSpeedTestBtn', cancelIntent ? 'Cancelando…' : 'Cancelar prueba');
    setText('speedTestActiveBadge', stateLabels[next]);
    setText('speedTestResultTitle', isRunning ? 'Prueba en curso' : 'Resultado de la prueba');
    setText('speedTestResultContext', peerName ? 'Este equipo ↔ ' + peerName : 'Conexión con el contacto seleccionado');
    updateReadiness();
    if (actionHadFocus) el(isRunning ? 'cancelSpeedTestBtn' : 'startSpeedTestBtn')?.focus();
    hide('speedTestProgress', !isRunning);
    if (!isRunning) setProgress(null);
  }

  async function startTest() {
    if (isRunning) return;
    const select = el('speedTestPeerSelect');
    const peerId = select?.value;
    if (!peerId) { QP.toast('Selecciona primero un contacto.', 'warn'); select?.focus(); return; }
    revision++;
    const request = ++requestVersion;
    const name = [...select.options].find(option => option.value === peerId)?.textContent?.replace(/ \([\d.]+ ms\)$/, '');
    resetResults(peerId, name);
    setState('connecting'); renderMeasurements(); renderPhaseList();
    setText('speedTestStatusNotice', 'Conectando con el contacto… Puedes cancelar la prueba.');
    setText('speedTestGaugeHelp', 'Preparando la conexión'); setProgress(null);
    try {
      const result = await QP.api('/api/speedtest/start', { method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, duration: parseInt(el('speedTestDuration')?.value || '10', 10), mode: el('speedTestMode')?.value || 'all' }) });
      if (!result.success) throw new Error(result.error || 'No se pudo iniciar la prueba.');
    } catch (error) {
      if (request !== requestVersion || !isRunning || cancelIntent) return;
      revision++;
      const result = { ...localResult(), state: phase ? 'interrupted' : 'error', error: error.message };
      showResult(result);
      if (phase || stages.length) remember(result);
    }
  }

  async function cancelTest() {
    if (!isRunning || cancelIntent) return;
    const peerId = activePeerId, request = requestVersion;
    cancelIntent = true; revision++; setState(state);
    try {
      const response = await QP.api('/api/speedtest/cancel', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ peerId }) });
      if (response?.success === false) throw new Error(response.error);
      if (request !== requestVersion || !isRunning) return;
      const result = { ...localResult(), state: 'cancelled', error: 'Prueba cancelada' };
      showResult(result); remember(result);
    } catch {
      if (request !== requestVersion || !isRunning) return;
      cancelIntent = false; setState(state);
      QP.toast('No se pudo cancelar la prueba. Vuelve a intentarlo.', 'danger');
    }
  }

  function startPhase(next) {
    if (phase !== next) {
      phase = next; liveUploadedBytes = liveDownloadedBytes = 0;
      phaseElapsedSeconds = 0;
      liveUploadMbps = liveDownloadMbps = null; setProgress(null);
    }
    setState('running');
    setText('speedTestStatusNotice', next === 'Latencia en reposo' ? 'Midiendo la latencia antes de generar tráfico…' : 'Midiendo: ' + (labels[next] || next) + '.');
    setText('speedTestGaugeHelp', labels[next] || next);
    renderMeasurements(); renderStages(); renderPhaseList();
  }

  function setProgress(percent) {
    if (el('speedTestBarFill')) el('speedTestBarFill').style.width = (percent ?? 0) + '%';
    const bar = el('speedTestProgressBar');
    if (percent == null) bar?.removeAttribute('aria-valuenow');
    else bar?.setAttribute('aria-valuenow', String(Math.round(percent)));
  }

  function updateTelemetry(data) {
    const current = Number.isFinite(data.currentMbps) ? data.currentMbps : null;
    if (data.direction === 'Upload') {
      liveUploadedBytes = Number(data.bytesTransferred ?? data.bytesUploaded ?? 0);
      liveUploadMbps = data.uploadMbps ?? current;
    } else if (data.direction === 'Download') {
      liveDownloadedBytes = Number(data.bytesTransferred ?? data.bytesDownloaded ?? 0);
      liveDownloadMbps = data.downloadMbps ?? current;
    } else if (phase === 'Both') {
      liveUploadedBytes = Number(data.bytesUploaded || 0); liveDownloadedBytes = Number(data.bytesDownloaded || 0);
      liveUploadMbps = data.uploadMbps ?? null; liveDownloadMbps = data.downloadMbps ?? null;
    }
    renderMeasurements();
    if (phase !== 'Latencia en reposo') {
      setText('speedTestMetricRtt', ms(data.rttMs));
      setText('speedTestRttHelp', 'Durante: ' + (labels[phase] || phase));
      if (Number.isFinite(data.totalSeconds) && data.totalSeconds > 0 && Number.isFinite(data.elapsedSeconds)) {
        // Upload progress comes from the remote receiver; download progress is
        // local. Their independent clocks and delivery delays must not rewind
        // the phase when both streams report alternately.
        phaseElapsedSeconds = Math.max(phaseElapsedSeconds, data.elapsedSeconds);
        const elapsed = Math.min(data.totalSeconds, phaseElapsedSeconds);
        const index = modes(planMode).indexOf(phase) + 1;
        setText('speedTestGaugeHelp', (labels[phase] || phase) + ' · fase ' + index + ' de ' + modes(planMode).length + ' · ' + elapsed.toFixed(1) + ' / ' + data.totalSeconds + ' s');
        setProgress(100 * elapsed / data.totalSeconds);
      }
    }
  }

  // Only completed stages provide final rates. A plan summary may contain zero placeholders.
  function rateFor(results, direction) {
    return results.find(s => s.mode === direction) || results.find(s => s.mode === 'Both');
  }

  function renderMeasurements() {
    for (const [direction, live] of [['Download', liveDownloadMbps], ['Upload', liveUploadMbps]]) {
      const stage = rateFor(stages, direction);
      const included = modes(planMode).some(mode => mode === direction || mode === 'Both');
      const value = isRunning && live != null ? speed(live) : stage ? speed(direction === 'Download' ? stage.downloadMbps : stage.uploadMbps) : !included ? 'No incluida' : state === 'idle' ? '—' : isRunning ? 'Pendiente' : 'Sin resultado';
      setText('speedTestMetric' + direction, value);
      el('speedTestMetric' + direction)?.setAttribute('data-measured', String(value.endsWith(' Mbps')));
      el('speedTestMetric' + direction)?.setAttribute('aria-label', (direction === 'Download' ? 'Bajada: ' : 'Subida: ') + (value === '—' ? 'sin medir' : value));
      setText('speedTest' + direction + 'Help', isRunning && live != null ? 'En directo · ' + (labels[phase] || phase) : stage ? 'Fase completada · ' + labels[stage.mode] : direction === 'Download' ? 'Recibir en este equipo' : 'Enviar desde este equipo');
    }
    setText('speedTestMetricIdleRtt', baseline ? ms(baseline.rttAverageMs) : state === 'idle' ? '—' : isRunning ? 'Pendiente' : 'Sin resultado');
    const last = stages.at(-1);
    setText('speedTestMetricRtt', last ? ms(last.quality?.rttAverageMs) : state === 'idle' ? '—' : isRunning ? 'Pendiente' : 'Sin resultado');
    setText('speedTestRttHelp', last ? 'Durante: ' + labels[last.mode] : 'Tiempo de ida y vuelta');
    const total = stages.reduce((sum, s) => sum + (s.totalBytes || 0), 0) + (isRunning ? liveUploadedBytes + liveDownloadedBytes : 0);
    setText('speedTestMetricBytes', stages.length || isRunning ? bytes(total) : '—');
    renderQuality('speedTestBaseline', baseline, 'En reposo');
  }

  function renderPhaseList() {
    const host = el('speedTestPhaseList'); if (!host) return;
    host.replaceChildren();
    const list = ['Latencia en reposo', ...modes(planMode)];
    let markedIncomplete = false;
    for (const mode of list) {
      const done = mode === 'Latencia en reposo' ? !!baseline : stages.some(s => s.mode === mode);
      let label = done ? 'Completada' : 'Pendiente', kind = done ? 'completed' : 'pending';
      if (!done && isRunning && phase === mode) { label = 'En curso'; kind = 'running'; }
      else if (!done && !isRunning && state !== 'idle') {
        if (!markedIncomplete) { label = state === 'cancelled' ? 'Cancelada' : state === 'error' ? 'Sin iniciar' : 'Sin completar'; kind = state; }
        markedIncomplete = true;
      }
      const item = make('li', null, 'speed-phase ' + kind);
      if (kind === 'running') item.setAttribute('aria-current', 'step');
      item.append(make('span', labels[mode] || mode), make('strong', label)); host.appendChild(item);
    }
  }

  function qualityText(q, waiting = false) {
    if (!q) return waiting ? 'Pendiente de medir.' : 'Sin resultado.';
    const loss = Number.isFinite(q.lossPercent) ? q.lossPercent.toFixed(2) + '%' : 'No disponible';
    return 'RTT mín./medio/máx.: ' + ms(q.rttMinMs) + ' / ' + ms(q.rttAverageMs) + ' / ' + ms(q.rttMaxMs) +
      ' · Jitter: ' + ms(q.jitterMs) + ' · Pérdida: ' + loss + ' · Sondas respondidas: ' + q.received + '/' + q.sent +
      ' · Fallos de envío: ' + q.sendFailures + ' · ' + q.method;
  }
  function renderQuality(id, quality, label) { setText(id, label + ': ' + qualityText(quality, isRunning)); }

  function stageDetails(result) {
    const section = make('section', null, 'speed-stage-detail');
    section.appendChild(make('h3', labels[result.mode] || result.mode));
    const metrics = make('dl', null, 'speed-detail-metrics');
    const values = [
      ...(result.mode !== 'Upload' ? [['Bajada', speed(result.downloadMbps)]] : []),
      ...(result.mode !== 'Download' ? [['Subida', speed(result.uploadMbps)]] : []),
      ['Datos transferidos', bytes(result.totalBytes)], ['Duración', result.durationSeconds + ' s'],
      ['Latencia con tráfico', ms(result.quality?.rttAverageMs)], ['Jitter', ms(result.quality?.jitterMs)],
      ['Pérdida', Number.isFinite(result.quality?.lossPercent) ? result.quality.lossPercent.toFixed(2) + '%' : 'No disponible'],
      ['RTT mínimo / máximo', ms(result.quality?.rttMinMs) + ' / ' + ms(result.quality?.rttMaxMs)],
      ['Sondas respondidas', result.quality ? result.quality.received + '/' + result.quality.sent : 'No disponible'],
      ['Fallos de envío', result.quality?.sendFailures ?? 'No disponible'], ['Método', result.quality?.method || 'No disponible']
    ];
    for (const [label, value] of values) {
      const pair = make('div'); pair.append(make('dt', label), make('dd', value)); metrics.appendChild(pair);
    }
    section.appendChild(metrics); return section;
  }

  function renderStages() {
    const host = el('speedTestPhaseResults'); if (!host) return;
    host.replaceChildren();
    for (const result of stages) host.appendChild(stageDetails(result));
  }

  function completedStages(data) {
    if (Array.isArray(data.stages)) return data.stages;
    return data.state === 'completed' && data.mode !== 'All' ? [data] : [];
  }

  function localResult() {
    return { testId, peerId: activePeerId, peerName, mode: planMode, baseline, stages: [...stages] };
  }

  function showResult(data) {
    activePeerId = data.peerId; peerName = data.peerName || peerName; testId = data.testId || testId;
    planMode = data.mode || planMode;
    stages = completedStages(data); baseline = data.baseline;
    liveUploadMbps = liveDownloadMbps = null; liveUploadedBytes = liveDownloadedBytes = 0;
    const next = cancelled(data.error) || data.state === 'cancelled' ? 'cancelled' : data.state === 'error' ? 'error' : data.state === 'interrupted' || data.error ? 'interrupted' : 'completed';
    setState(next);
    const partial = next !== 'completed';
    setText('speedTestStatusNotice', partial ? (stages.length ? 'Resultados parciales · ' : 'Sin resultados de velocidad · ') + stages.length + ' de ' + modes(planMode).length + ' fases completadas.' : 'Prueba completada · ' + stages.length + ' de ' + modes(planMode).length + ' fases completadas.');
    hide('speedTestError', !partial || next === 'cancelled');
    if (next === 'cancelled') setText('speedTestStatusNotice', 'Prueba cancelada. ' + (stages.length ? 'Se conservan las fases completadas (' + stages.length + ').' : 'No se ha completado ninguna fase de velocidad.'));
    setText('speedTestErrorTitle', next === 'error' ? 'No se pudo iniciar la prueba' : 'La prueba se ha interrumpido');
    setText('speedTestErrorMessage', (stages.length ? 'Se conservan las fases completadas. ' : 'No hay mediciones de velocidad completas. ') + 'Comprueba que el contacto esté disponible y pulsa «Repetir prueba».');
    setText('speedTestErrorTechnical', data.error || 'No se recibió un diagnóstico técnico.');
    renderMeasurements(); renderStages(); renderPhaseList();
    // Retry must keep the same contact and options, including after restoring a result.
    if (el('speedTestPeerSelect') && [...el('speedTestPeerSelect').options].some(o => o.value === data.peerId)) el('speedTestPeerSelect').value = data.peerId;
    if (el('speedTestMode')) el('speedTestMode').value = planMode.toLowerCase();
    const duration = Math.round(stages[0]?.durationSeconds);
    if (el('speedTestDuration') && [...el('speedTestDuration').options].some(o => Number(o.value) === duration)) el('speedTestDuration').value = String(duration);
    updateEstimate();
    updateReadiness();
  }

  function withTime(data) {
    return { ...data, time: data.time || (data.startedAt ? new Date(data.startedAt) : new Date()).toLocaleTimeString('es-ES', { hour12: false }) };
  }
  function remember(data) {
    const index = data.testId ? history.findIndex(item => item.testId === data.testId) : -1;
    if (index >= 0) history[index] = withTime({ ...history[index], ...data });
    else history.unshift(withTime(data));
    if (history.length > 50) history.length = 50;
    renderHistory();
  }

  function renderHistory() {
    el('speedTestHistory')?.setAttribute('data-empty', String(history.length === 0));
    setText('speedTestHistoryCount', String(history.length));
    if (el('clearSpeedTestHistoryBtn')) el('clearSpeedTestHistoryBtn').disabled = clearingHistory || !history.length;
    setText('clearSpeedTestHistoryBtn', clearingHistory ? 'Limpiando…' : 'Limpiar historial');
    const host = el('speedTestHistoryBody'); if (!host) return;
    host.replaceChildren();
    if (!history.length) {
      const row = make('tr'), cell = make('td', 'Todavía no has realizado ninguna prueba.', 'empty-cell');
      cell.colSpan = 5; row.appendChild(cell); host.appendChild(row); return;
    }
    for (const item of history) {
      const results = completedStages(item), row = make('tr');
      const terminal = item.state !== 'running';
      const values = [['Hora', item.time], ['Contacto', item.peerName || 'Contacto']];
      for (const direction of ['Download', 'Upload']) {
        const result = rateFor(results, direction);
        const value = result ? speed(direction === 'Download' ? result.downloadMbps : result.uploadMbps) : modes(item.mode).some(m => m === direction || m === 'Both') ? terminal ? 'Sin resultado' : 'Pendiente' : 'No incluida';
        values.push([direction === 'Download' ? 'Bajada' : 'Subida', value]);
      }
      for (const [label, value] of values) {
        const cell = make('td', value); cell.setAttribute('data-label', label); row.appendChild(cell);
      }
      const cell = make('td'); cell.setAttribute('data-label', 'Estado y fases');
      const details = make('details', null, 'speed-history-details');
      const label = cancelled(item.error) ? 'Cancelada' : stateLabels[item.state] || 'Completada';
      details.appendChild(make('summary', label + ' · ' + results.length + ' de ' + modes(item.mode).length + ' fases'));
      details.appendChild(make('p', labels[item.mode] || item.mode, 'speed-help'));
      details.appendChild(make('p', 'En reposo: ' + qualityText(item.baseline, !terminal), 'speed-help'));
      for (const result of results) details.appendChild(stageDetails(result));
      if (item.error) details.appendChild(make('pre', item.error));
      cell.appendChild(details); row.appendChild(cell); host.appendChild(row);
    }
  }
})();
