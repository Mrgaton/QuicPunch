(() => {
  let snapshot = null, submission = null, accepted = null, requestError = '';
  let acknowledgement = Promise.resolve();
  let latestIntent = 0;
  const started = state => !!(state?.isStarted ?? state?.running);
  const enabled = (state, fallback) => !!(state?.desiredEnabled ?? state?.enabled ?? fallback);
  const mode = (wan, tor) => wan ? tor ? 'both' : 'wan' : tor ? 'tor' : 'off';
  const actualMode = status => mode(started(status?.wan), started(status?.tor));
  const applying = transition => ['applying', 'canceling-tor'].includes(transition?.phase);
  const currentTransition = () => submission || accepted || snapshot?.connectionMode;
  const names = {
    wan: 'Conexiones directas · WAN', tor: 'Conexiones solo por Tor · WAN desactivada',
    both: 'WAN y Tor activos', off: 'Servicios de conexión desactivados'
  };

  function render(next = snapshot) {
    if (!next) return;
    if (next.connectionMode?.id > (snapshot?.connectionMode?.id ?? 0)) requestError = '';
    snapshot = next;
    if (accepted && next.connectionMode?.id >= accepted.id) accepted = null;
    const transition = currentTransition();
    const wan = started(next.wan), tor = started(next.tor);
    const working = applying(transition);
    const wanted = working ? transition.target : mode(enabled(next.wan, wan), enabled(next.tor, tor));
    const actual = actualMode(next);
    const ready = !working && wanted === actual;
    document.documentElement.dataset.connectionMode = ready ? actual : 'transition';
    for (const button of document.querySelectorAll('button[data-connection-mode]')) {
      const target = button.dataset.connectionMode;
      button.setAttribute('aria-pressed', String(ready && target === actual));
      button.setAttribute('aria-disabled', 'false');
      button.dataset.pending = String(working && target === wanted);
    }

    const error = requestError || (!working && (transition?.error || (!ready && (
      ((wanted === 'tor' || wanted === 'both') && next.tor?.lastError) ||
      ((wanted === 'wan' || wanted === 'both') && next.wan?.lastError)))));
    let text = names[actual];
    if (error) text = `No se pudo completar el cambio: ${error}`;
    else if (transition?.phase === 'canceling-tor') text = 'Cancelando el arranque de Tor…';
    else if (!ready) {
      const progress = next.tor?.bootstrapProgress;
      if (wanted === 'tor' && wan) text = 'Deteniendo WAN…';
      else if ((wanted === 'tor' || wanted === 'both') && !tor) text = `Iniciando Tor${progress > 0 ? ` · ${progress}%` : '…'}`;
      else if (wanted === 'wan') text = tor ? 'Deteniendo Tor…' : 'Activando WAN…';
      else text = 'Actualizando servicios de conexión…';
    }
    for (const id of ['headerNetworkStatus', 'connectionModeStatus', 'settingsConnectionModeStatus']) {
      const node = document.getElementById(id);
      if (!node) continue;
      if (node.textContent !== text) node.textContent = text;
      node.dataset.error = String(!!error);
      if (id === 'headerNetworkStatus') node.classList.toggle('sr-only', ready && !error && (actual === 'wan' || actual === 'tor'));
    }
  }

  async function submit(target) {
    latestIntent++;
    const previous = currentTransition();
    const intent = {target, phase: target === 'wan' && applying(previous) && previous.target !== 'wan' ? 'canceling-tor' : 'applying'};
    submission = intent;
    requestError = '';
    render();
    // Order the acknowledgements from this window without serializing transport startup in the browser.
    const response = acknowledgement.then(() => QP.api('/api/connection-mode', {
      method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({mode: target}),
      signal: AbortSignal.timeout(10000)
    }));
    acknowledgement = response.catch(() => {});
    try {
      const receipt = await response;
      if (submission === intent) accepted = receipt;
      return receipt;
    } catch (error) {
      if (submission === intent) requestError = error.message;
      throw error;
    } finally {
      if (submission === intent) {
        submission = null;
        render();
      }
    }
  }

  function waitForReady(receipt) {
    return new Promise((resolve, reject) => {
      const finish = (result, error) => {
        clearTimeout(timeout);
        unsubscribe();
        if (error) reject(error);
        else resolve(result);
      };
      const check = status => {
        const transition = status?.connectionMode;
        if (!transition || transition.id < receipt.id) return;
        if (transition.id > receipt.id || transition.phase === 'superseded') finish(false);
        else if (transition.phase === 'failed') finish(false, new Error(transition.error || 'No se pudo activar la red.'));
        else if (transition.phase === 'ready') finish(actualMode(status) === receipt.target);
      };
      const unsubscribe = QP.on('status_updated', check);
      const timeout = setTimeout(() => finish(false, new Error('La red aún no está lista. Comprueba el estado en la cabecera.')), 10 * 60 * 1000);
      check(QP.state.status);
    });
  }

  async function request(target) {
    try {
      const response = submit(target);
      const intent = latestIntent;
      const ready = await waitForReady(await response);
      return ready && intent === latestIntent;
    } catch (error) {
      QP.toast(error.message, 'error');
      return false;
    }
  }

  async function select(button) {
    const target = button.dataset.connectionMode;
    const transition = currentTransition();
    if (!snapshot || button.getAttribute('aria-pressed') === 'true' || (applying(transition) && transition.target === target)) return;
    const leaving = target === 'tor' ? 'wan' : 'tor';
    const hasActiveConnections = target !== 'both' && started(snapshot[leaving]) && snapshot.peers?.some(peer => {
      const transport = peer.activeTransport?.toLowerCase() || (peer.isTor || peer.networkType === 'Tor' ? 'tor' : 'wan');
      return peer.isQuicConnected && transport === leaving;
    });
    if (hasActiveConnections) {
      const destination = target === 'tor' ? 'Tor' : 'WAN';
      const source = leaving === 'wan' ? 'WAN' : 'Tor';
      const confirmed = await QP.confirmAction({
        title: `¿Cambiar a ${destination}?`,
        message: `Se cerrarán las conexiones activas por ${source}, incluidas las llamadas y transferencias en curso por esa red.`,
        confirmLabel: `Cambiar a ${destination}`
      });
      if (!confirmed) return;
    }
    try { await submit(target); }
    catch { /* The header shows the error without blocking further selections. */ }
  }

  QP.connectionModes = {request};
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('button[data-connection-mode]').forEach(button => button.addEventListener('click', () => select(button)));
    const topbar = document.querySelector('.topbar');
    new ResizeObserver(() => {
      document.documentElement.style.setProperty('--workspace-topbar-height', `${topbar.offsetHeight}px`);
    }).observe(topbar);
    QP.on('status_updated', render);
    render(QP.state.status);
  });
})();
