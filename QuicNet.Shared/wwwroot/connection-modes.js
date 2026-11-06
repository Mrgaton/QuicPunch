(() => {
  let snapshot = null, pending = null, requestError = '';
  const started = s => !!(s?.isStarted ?? s?.running);
  const enabled = (s, fallback) => !!(s?.desiredEnabled ?? s?.enabled ?? fallback);
  const mode = (wan, tor) => wan ? tor ? 'both' : 'wan' : tor ? 'tor' : 'off';
  function render(next = snapshot) {
    if (!next) return;
    snapshot = next;
    const wan = started(next.wan), tor = started(next.tor);
    const wanted = pending || mode(enabled(next.wan, wan), enabled(next.tor, tor));
    const actual = mode(wan, tor);
    const ready = !pending && wanted === actual;
    document.documentElement.dataset.connectionMode = ready ? actual : 'transition';
    document.querySelectorAll('button[data-connection-mode]').forEach(button => {
      button.setAttribute('aria-pressed', String(button.dataset.connectionMode === wanted));
      button.disabled = !!pending;
    });
    const names = {wan:'Conexiones directas · WAN', tor:'Conexiones solo por Tor · WAN desactivada', both:'WAN y Tor activos', off:'Servicios de conexión desactivados'};
    if (ready) requestError = '';
    const error = requestError || (!ready && ((wanted !== 'wan' && next.tor?.lastError) || (wanted !== 'tor' && next.wan?.lastError)));
    const progress = next.tor?.bootstrapProgress;
    const text = error ? `No se pudo completar el cambio: ${error}` : ready ? names[actual]
      : wanted === 'tor' && !tor ? `Iniciando Tor${progress > 0 ? ` · ${progress}%` : '…'}${wan ? ' · deteniendo WAN' : ' · WAN desactivada'}`
      : wanted === 'wan' ? 'Activando WAN · deteniendo Tor…' : 'Actualizando servicios de conexión…';
    QP.setText('connectionModeStatus', text);
    const node = document.getElementById('connectionModeStatus');
    if (node) node.dataset.error = String(!!error);
  }
  async function request(target) {
    if (pending) return false;
    pending = target; requestError = ''; render();
    try {
      await QP.api('/api/connection-mode', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mode:target})});
      return true;
    } catch (error) { requestError = error.message; QP.toast(error.message, 'error'); return false; }
    finally {
      try { const next = await QP.getStatus(true); if (next) snapshot = next; }
      catch (error) { requestError ||= error.message; }
      pending = null; render();
    }
  }
  QP.connectionModes = {request};
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('button[data-connection-mode]').forEach(button => button.addEventListener('click', () => request(button.dataset.connectionMode)));
    QP.on('status_updated', next => render(next));
    render(QP.state.status);
  });
})();
