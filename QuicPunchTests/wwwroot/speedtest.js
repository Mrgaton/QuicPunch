(() => {
  let isRunning = false;
  let activePeerId = null;
  const history = [];

  document.addEventListener('DOMContentLoaded', () => {
    const startBtn = document.getElementById('startSpeedTestBtn');
    const cancelBtn = document.getElementById('cancelSpeedTestBtn');
    const clearHistoryBtn = document.getElementById('clearSpeedTestHistoryBtn');

    startBtn?.addEventListener('click', startTest);
    cancelBtn?.addEventListener('click', cancelTest);
    clearHistoryBtn?.addEventListener('click', () => {
      history.length = 0;
      renderHistory();
    });

    if (window.QP && typeof QP.on === 'function') {
      QP.on('status_updated', data => updatePeerSelect(data));
      QP.on('view_changed', data => {
        if (data && data.page === 'speedtest') {
          syncStatus();
        }
      });

      QP.on('speedtest_started', onSpeedTestStarted);
      QP.on('speedtest_progress', onSpeedTestProgress);
      QP.on('speedtest_completed', onSpeedTestCompleted);
      QP.on('speedtest_error', onSpeedTestError);
      QP.on('peer_disconnected', data => {
        if (activePeerId && data && (data.peerId === activePeerId || data.id === activePeerId)) {
          cancelTest();
          QP.toast('El peer se ha desconectado durante la prueba de velocidad.', 'warn');
        }
      });
    }

    syncStatus();
  });

  async function syncStatus() {
    try {
      const net = await (QP.getStatus ? QP.getStatus() : QP.api('/api/status'));
      updatePeerSelect(net);

      const status = await QP.api('/api/speedtest/status');
      if (status && status.isRunning && status.active) {
        setRunningState(true, status.active.peerId);
        updateTelemetry({
          direction: status.active.mode,
          currentMbps: status.active.mode === 'Download' ? status.active.lastDownloadMbps :
                       status.active.mode === 'Upload' ? status.active.lastUploadMbps :
                       Math.max(status.active.lastUploadMbps, status.active.lastDownloadMbps),
          uploadMbps: status.active.lastUploadMbps,
          downloadMbps: status.active.lastDownloadMbps,
          rttMs: status.active.rttMs,
          bytesTransferred: status.active.bytesUploaded + status.active.bytesDownloaded,
          elapsedSeconds: status.active.elapsedSeconds,
          totalSeconds: status.active.durationSeconds
        });
      } else if (!isRunning) {
        setRunningState(false);
      }
    } catch (e) {
      console.error('SpeedTest sync error:', e);
    }
  }

  function updatePeerSelect(netStatus) {
    const sel = document.getElementById('speedTestPeerSelect');
    if (!sel) return;

    const peers = (netStatus?.peers || []).filter(p => p.isTrusted);
    const prevVal = sel.value;

    sel.innerHTML = '<option value="">Seleccionar un peer conectado...</option>';
    peers.forEach(p => {
      const opt = document.createElement('option');
      opt.value = p.id;
      const rttText = p.ping ? `${p.ping} ms` : 'Sin ping';
      opt.textContent = `${p.name || 'Peer'} (${rttText})`;
      sel.appendChild(opt);
    });

    if (prevVal && peers.some(p => p.id === prevVal)) {
      sel.value = prevVal;
    }
  }

  async function startTest() {
    const peerSel = document.getElementById('speedTestPeerSelect');
    const durSel = document.getElementById('speedTestDuration');
    const modeSel = document.getElementById('speedTestMode');
    const notice = document.getElementById('speedTestStatusNotice');

    const peerId = peerSel?.value;
    if (!peerId) {
      QP.toast('Selecciona primero un peer conectado.', 'warn');
      return;
    }

    const duration = parseInt(durSel?.value || '10', 10);
    const mode = modeSel?.value || 'both';

    try {
      setRunningState(true, peerId);
      if (notice) notice.textContent = 'Negociando flujo de prueba de velocidad con el peer...';

      const res = await QP.api('/api/speedtest/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, duration, mode })
      });

      if (!res.success) {
        throw new Error(res.error || 'No se pudo iniciar la prueba de rendimiento');
      }
    } catch (err) {
      setRunningState(false);
      QP.toast(`Error en prueba de velocidad: ${err.message}`, 'danger');
      if (notice) notice.textContent = `Error: ${err.message}`;
    }
  }

  async function cancelTest() {
    if (!activePeerId) return;
    try {
      await QP.api('/api/speedtest/cancel', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId: activePeerId })
      });
      setRunningState(false);
      QP.toast('Prueba de velocidad cancelada.', 'warn');
      const notice = document.getElementById('speedTestStatusNotice');
      if (notice) notice.textContent = 'La prueba de velocidad fue cancelada.';
    } catch (e) {
      console.error(e);
    }
  }

  function setRunningState(running, peerId = null) {
    isRunning = running;
    activePeerId = peerId;

    const startBtn = document.getElementById('startSpeedTestBtn');
    const cancelBtn = document.getElementById('cancelSpeedTestBtn');
    const peerSel = document.getElementById('speedTestPeerSelect');
    const durSel = document.getElementById('speedTestDuration');
    const modeSel = document.getElementById('speedTestMode');
    const badge = document.getElementById('speedTestActiveBadge');

    if (startBtn) startBtn.classList.toggle('hidden', running);
    if (cancelBtn) cancelBtn.classList.toggle('hidden', !running);
    if (peerSel) peerSel.disabled = running;
    if (durSel) durSel.disabled = running;
    if (modeSel) modeSel.disabled = running;

    if (badge) {
      badge.textContent = running ? 'Ejecutando prueba' : 'Inactivo';
      badge.className = running ? 'badge warn pulse' : 'badge';
    }

    if (running) {
      const liveNum = document.getElementById('speedTestLiveNumber');
      const barFill = document.getElementById('speedTestBarFill');
      const upVal = document.getElementById('speedTestMetricUpload');
      const downVal = document.getElementById('speedTestMetricDownload');
      const rttVal = document.getElementById('speedTestMetricRtt');
      const bytesVal = document.getElementById('speedTestMetricBytes');
      if (liveNum) liveNum.textContent = '0.0';
      if (barFill) barFill.style.width = '0%';
      if (upVal) upVal.textContent = '—';
      if (downVal) downVal.textContent = '—';
      if (rttVal) rttVal.textContent = '—';
      if (bytesVal) bytesVal.textContent = '0 MB';
    }
  }

  function onSpeedTestStarted(data) {
    setRunningState(true, data.peerId);
    const notice = document.getElementById('speedTestStatusNotice');
    if (notice) notice.textContent = `Prueba de velocidad en curso contra ${data.peerName} (${data.duration}s, ${data.mode})...`;
  }

  function onSpeedTestProgress(data) {
    updateTelemetry(data);
  }

  function updateTelemetry(data) {
    const liveNum = document.getElementById('speedTestLiveNumber');
    const barFill = document.getElementById('speedTestBarFill');
    const upVal = document.getElementById('speedTestMetricUpload');
    const downVal = document.getElementById('speedTestMetricDownload');
    const rttVal = document.getElementById('speedTestMetricRtt');
    const bytesVal = document.getElementById('speedTestMetricBytes');
    const notice = document.getElementById('speedTestStatusNotice');

    const curMbps = Number(data.currentMbps || 0);
    if (liveNum) liveNum.textContent = curMbps.toFixed(1);

    // Scaling gauge fill up to 1000 Mbps
    const percent = Math.min(100, Math.max(0, (curMbps / 500) * 100));
    if (barFill) barFill.style.width = `${percent}%`;

    if (data.direction === 'Upload') {
      if (upVal) upVal.textContent = `${curMbps.toFixed(1)} Mbps`;
    } else if (data.direction === 'Download') {
      if (downVal) downVal.textContent = `${curMbps.toFixed(1)} Mbps`;
    } else {
      if (upVal) upVal.textContent = `${curMbps.toFixed(1)} Mbps`;
      if (downVal) downVal.textContent = `${curMbps.toFixed(1)} Mbps`;
    }

    if (rttVal) rttVal.textContent = data.rttMs ? `${data.rttMs} ms` : '—';
    if (bytesVal) bytesVal.textContent = QP.fmtBytes ? QP.fmtBytes(data.bytesTransferred || 0) : `${Math.round((data.bytesTransferred || 0) / 1048576)} MB`;

    if (notice && data.elapsedSeconds && data.totalSeconds) {
      notice.textContent = `Progreso: ${data.elapsedSeconds}s / ${data.totalSeconds}s (${Math.round((data.elapsedSeconds / data.totalSeconds) * 100)}%)`;
    }
  }

  function onSpeedTestCompleted(data) {
    setRunningState(false);
    QP.toast(`¡Prueba completada! Subida: ${data.uploadMbps} Mbps, Bajada: ${data.downloadMbps} Mbps`, 'ok');

    const liveNum = document.getElementById('speedTestLiveNumber');
    const barFill = document.getElementById('speedTestBarFill');
    const upVal = document.getElementById('speedTestMetricUpload');
    const downVal = document.getElementById('speedTestMetricDownload');
    const rttVal = document.getElementById('speedTestMetricRtt');
    const notice = document.getElementById('speedTestStatusNotice');

    const maxSpeed = Math.max(data.uploadMbps || 0, data.downloadMbps || 0);
    if (liveNum) liveNum.textContent = maxSpeed.toFixed(1);
    if (barFill) barFill.style.width = '0%';
    if (upVal) upVal.textContent = data.uploadMbps > 0 ? `${data.uploadMbps.toFixed(1)} Mbps` : '—';
    if (downVal) downVal.textContent = data.downloadMbps > 0 ? `${data.downloadMbps.toFixed(1)} Mbps` : '—';
    if (rttVal) rttVal.textContent = data.rttMs ? `${data.rttMs} ms` : '—';

    if (notice) {
      notice.textContent = `Completado en ${data.durationSeconds}s. Transferidos ${QP.fmtBytes ? QP.fmtBytes(data.totalBytes) : `${Math.round(data.totalBytes / 1048576)} MB`}.`;
    }

    // Add to history
    history.unshift({
      time: new Date().toLocaleTimeString(),
      peerName: data.peerName || 'Peer',
      mode: data.mode,
      uploadMbps: data.uploadMbps,
      downloadMbps: data.downloadMbps,
      totalBytes: data.totalBytes,
      duration: data.durationSeconds,
      rttMs: data.rttMs
    });
    renderHistory();
  }

  function onSpeedTestError(data) {
    setRunningState(false);
    QP.toast(`Error en prueba de velocidad: ${data.error}`, 'danger');
    const notice = document.getElementById('speedTestStatusNotice');
    if (notice) notice.textContent = `Prueba fallida: ${data.error}`;
  }

  function renderHistory() {
    const host = document.getElementById('speedTestHistoryBody');
    if (!host) return;

    if (!history.length) {
      host.innerHTML = '<tr><td colspan="8" class="muted text-center">No hay pruebas realizadas aún.</td></tr>';
      return;
    }

    host.innerHTML = '';
    history.forEach(item => {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td class="mono">${item.time}</td>
        <td><strong>${item.peerName}</strong></td>
        <td><span class="badge sm">${item.mode}</span></td>
        <td class="mono text-ok">${item.uploadMbps > 0 ? `${item.uploadMbps.toFixed(1)} Mbps` : '—'}</td>
        <td class="mono text-accent">${item.downloadMbps > 0 ? `${item.downloadMbps.toFixed(1)} Mbps` : '—'}</td>
        <td class="mono">${QP.fmtBytes ? QP.fmtBytes(item.totalBytes) : item.totalBytes}</td>
        <td class="mono">${item.duration}s</td>
        <td class="mono">${item.rttMs ? `${item.rttMs} ms` : '—'}</td>
      `;
      host.appendChild(tr);
    });
  }
})();
