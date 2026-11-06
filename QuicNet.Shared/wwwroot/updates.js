(() => {
  const app = window.QP;
  if (!app) return;
  let actionPending = false, channelPending = false, lastStatus;
  const notices = ['menuUpdate', 'mobileUpdate'];
  const labels = { idle: 'Pendiente de comprobar', checking: 'Comprobando', current: 'Al día', available: 'Nueva versión',
    downloading: 'Descargando', ready: 'Lista para instalar', installing: 'Instalando', error: 'No se pudo actualizar' };
  const busyStages = ['checking', 'downloading', 'installing'];
  function renderNotices(status) {
    const disconnected = app.state.isWsConnected === false;
    const channelSelect = document.getElementById('updateChannel');
    if (channelSelect) channelSelect.disabled = disconnected || channelPending || actionPending || busyStages.includes(status.Stage);
    const newer = status.LatestBuild > status.CurrentBuild;
    const android = status.Platform === 'android-arm64';
    const percent = status.Total > 0 ? Math.min(100, Math.floor(status.Downloaded * 100 / status.Total)) : null;
    const title = status.Stage === 'downloading' ? 'Descargando actualización'
      : status.Stage === 'installing' ? (android ? 'Confirma la instalación' : 'Reiniciando para actualizar')
      : status.Stage === 'error' ? 'Actualización pendiente' : 'Actualización disponible';
    const detail = status.Stage === 'downloading' ? (percent === null ? 'Preparando descarga…' : percent + ' % descargado')
      : status.Stage === 'installing' ? (android ? 'Continúa en el instalador de Android.' : 'La aplicación se abrirá de nuevo.')
      : status.Stage === 'error' ? status.Message
      : status.Stage === 'checking' ? 'Comprobando la nueva versión…'
      : 'Versión ' + status.LatestVersion + ((status.LatestChannel || status.Channel) === 'beta' ? ' · Beta' : ' · Estable') + (android ? ' · Confirma la instalación en Android.' : ' · Tus datos se conservan.');
    for (const prefix of notices) {
      document.getElementById(prefix + 'Notice').hidden = !newer;
      document.getElementById(prefix + 'Title').textContent = title;
      document.getElementById(prefix + 'Detail').textContent = detail || '';
      const progress = document.getElementById(prefix + 'Progress');
      progress.hidden = status.Stage !== 'downloading';
      progress.value = status.Downloaded; progress.max = status.Total || 1;
      const button = document.getElementById(prefix + 'Install');
      button.disabled = disconnected || actionPending || channelPending || busyStages.includes(status.Stage);
      button.textContent = actionPending ? 'Preparando…' : status.Stage === 'downloading' ? 'Descargando…'
        : status.Stage === 'installing' ? 'Instalando…' : android ? 'Instalar actualización' : 'Instalar y reiniciar';
    }
    document.getElementById('updateInstall').disabled = disconnected || actionPending || channelPending || busyStages.includes(status.Stage);
    document.getElementById('updateInstall').hidden = !['available', 'ready', 'error'].includes(status.Stage) || !newer;
    document.getElementById('updateCheck').disabled = disconnected || actionPending || channelPending || busyStages.includes(status.Stage);
  }
  function showError(error) {
    app.reportError?.(error, 'Actualizaciones');
    document.getElementById('updateMessage').textContent = error.message;
    // Keep the available update visible if the connection or installer fails.
    for (const prefix of notices) document.getElementById(prefix + 'Detail').textContent = error.message;
  }
  function render(status) {
      if (!status) return;
      lastStatus = status;
      document.getElementById('updateVersion').textContent = status.CurrentVersion + ' · compilación ' + status.CurrentBuild + (status.CurrentChannel === 'beta' ? ' · Beta' : '');
      const channelSelect = document.getElementById('updateChannel');
      if (channelSelect && !channelPending) channelSelect.value = status.Channel || 'stable';
      const channelHelp = document.getElementById('updateChannelHelp');
      if (channelHelp) channelHelp.textContent = status.Channel === 'beta'
        ? 'Recibirás versiones de prueba y releases estables, siempre que sean más recientes. Seguirás en Beta tras instalar una estable para recibir las próximas pruebas.'
        : 'Solo versiones estables. Al salir de Beta se conserva la instalación actual hasta que haya una compilación estable superior.';
      document.getElementById('updateStatus').textContent = labels[status.Stage] || status.Stage;
      document.getElementById('updateMessage').textContent = status.Message || 'Se comprueba al abrir y cada 15 minutos.';
      document.getElementById('updateAutomatic').checked = status.Automatic;
      document.getElementById('updateLatest').textContent = status.LatestVersion ? status.LatestVersion + ' · compilación ' + status.LatestBuild : '—';
      document.getElementById('updateLastCheck').textContent = status.LastCheck ? new Date(status.LastCheck).toLocaleString() : 'Aún no comprobada';
      const progress = document.getElementById('updateProgress');
      progress.hidden = status.Stage !== 'downloading';
      progress.value = status.Downloaded; progress.max = status.Total || 1;
      document.getElementById('updateInstall').textContent = status.Platform === 'android-arm64' ? 'Instalar actualización' : 'Instalar y reiniciar';
      document.getElementById('updatePlatformHelp').textContent = status.Platform === 'android-arm64'
        ? 'La descarga es automática. Android te pedirá permitir esta fuente y confirmar la instalación. Tus datos se conservan.'
        : 'El EXE AOT se sustituye y se reinicia automáticamente cuando no hay llamadas. Se conserva una copia anterior.';
      renderNotices(status);
  }
  app.on('update_status', render);
  app.on('ws_status', data => {
    if (lastStatus) renderNotices(lastStatus);
    if (!data.connected) document.getElementById('updateMessage').textContent = 'Reconectando con la aplicación…';
  });
  async function runAction(path) {
    if (actionPending || channelPending || (lastStatus && busyStages.includes(lastStatus.Stage))) return;
    actionPending = true;
    if (lastStatus) renderNotices(lastStatus);
    document.getElementById('updateCheck').disabled = true;
    let failure;
    try {
      await app.wsRequest('updates.' + path);
    } catch (error) { failure = error; }
    finally {
      actionPending = false;
      if (lastStatus) {
        renderNotices(lastStatus);
      }
      if (failure) showError(failure);
    }
  }
  document.addEventListener('DOMContentLoaded', () => {
    // Keep the conversation composer and video inside the remaining viewport.
    const mobileNotice = document.getElementById('mobileUpdateNotice');
    if (window.ResizeObserver) {
      new window.ResizeObserver(() => {
        const height = mobileNotice.getBoundingClientRect().height;
        const margin = parseFloat(window.getComputedStyle(mobileNotice).marginBottom) || 0;
        document.documentElement.style.setProperty('--update-notice-height', (height ? height + margin : 0) + 'px');
      }).observe(mobileNotice);
    }
    for (const [id, path] of [['updateCheck', 'check'], ['updateInstall', 'install'],
      ...notices.map(prefix => [prefix + 'Install', 'install'])])
      document.getElementById(id)?.addEventListener('click', () => runAction(path));
    document.getElementById('updateAutomatic')?.addEventListener('change', async event => {
      try { await app.wsRequest('updates.automatic', { enabled: event.target.checked }); }
      catch (error) { if (lastStatus) render(lastStatus); showError(error); }
    });
    document.getElementById('updateChannel')?.addEventListener('change', async event => {
      const select = event.target;
      if (actionPending || channelPending || (lastStatus && busyStages.includes(lastStatus.Stage))) {
        select.value = lastStatus?.Channel || 'stable'; return;
      }
      channelPending = true;
      if (lastStatus) renderNotices(lastStatus);
      document.getElementById('updateCheck').disabled = true;
      let failure;
      try {
        await app.wsRequest('updates.channel', { channel: select.value });
      } catch (error) { select.value = lastStatus?.Channel || 'stable'; failure = error; }
      finally {
        channelPending = false;
        if (lastStatus) { select.value = lastStatus.Channel || 'stable'; renderNotices(lastStatus); }
      }
      if (failure) showError(failure);
    });
    render(app.state.updates);
  });
})();
