(() => {
  const app = window.QP;
  if (!app) return;
  let actionPending = false, channelPending = false, automaticPending = false, lastStatus;
  const notices = ['menuUpdate', 'mobileUpdate'];
  const labels = { idle: 'Pendiente de comprobar', checking: 'Comprobando', current: 'Al día', available: 'Nueva versión',
    downloading: 'Descargando', ready: 'Lista para instalar', installing: 'Instalando', error: 'No se pudo actualizar' };
  const busyStages = ['checking', 'downloading', 'installing'];
  const verifying = status => status.Stage === 'downloading' && status.DownloadPhase === 'verifying';
  function renderNotices(status) {
    const disconnected = app.state.isWsConnected === false;
    const channelSelect = document.getElementById('updateChannel');
    if (channelSelect) channelSelect.disabled = disconnected || channelPending || actionPending || automaticPending || busyStages.includes(status.Stage);
    const automatic = document.getElementById('updateAutomatic');
    if (automatic) automatic.disabled = disconnected || automaticPending;
    const newer = status.LatestBuild > status.CurrentBuild;
    const android = status.Platform === 'android-arm64';
    const percent = status.Total > 0 ? Math.min(100, Math.floor(status.Downloaded * 100 / status.Total)) : null;
    const title = verifying(status) ? 'Verificando actualización'
      : status.Stage === 'downloading' ? 'Descargando actualización'
      : status.Stage === 'installing' ? (android ? 'Instalando actualización' : 'Reiniciando para actualizar')
      : status.Stage === 'error' ? 'Actualización pendiente' : 'Actualización disponible';
    const version = 'Versión ' + status.LatestVersion + ((status.LatestChannel || status.Channel) === 'beta' ? ' · Beta' : ' · Estable');
    const detail = verifying(status) ? version
      : status.Stage === 'downloading' ? (percent === null ? 'Preparando descarga…' : percent + ' % descargado')
      : ['installing', 'error'].includes(status.Stage) && status.Message ? status.Message
      : status.Stage === 'checking' ? 'Comprobando la nueva versión…'
      : status.Message ? version + ' · ' + status.Message : version;
    for (const prefix of notices) {
      document.getElementById(prefix + 'Notice').hidden = !newer;
      document.getElementById(prefix + 'Title').textContent = title;
      document.getElementById(prefix + 'Detail').textContent = detail || '';
      const progress = document.getElementById(prefix + 'Progress');
      progress.hidden = status.Stage !== 'downloading' || verifying(status);
      progress.value = status.Downloaded; progress.max = status.Total || 1;
      const button = document.getElementById(prefix + 'Install');
      button.disabled = disconnected || actionPending || channelPending || automaticPending || busyStages.includes(status.Stage);
      button.textContent = actionPending ? 'Preparando…' : verifying(status) ? 'Verificando…' : status.Stage === 'downloading' ? 'Descargando…'
        : status.Stage === 'installing' ? 'Instalando…' : android ? 'Instalar actualización' : 'Instalar y reiniciar';
    }
    document.getElementById('updateInstall').disabled = disconnected || actionPending || channelPending || automaticPending || busyStages.includes(status.Stage);
    document.getElementById('updateInstall').hidden = !['available', 'ready', 'error'].includes(status.Stage) || !newer;
    document.getElementById('updateCheck').disabled = disconnected || actionPending || channelPending || automaticPending || busyStages.includes(status.Stage);
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
        ? 'Recibirás versiones de prueba y versiones estables más recientes. Seguirás en Beta tras instalar una estable para recibir las próximas pruebas.'
        : 'Solo versiones estables. Al salir de Beta se conserva la instalación actual hasta que haya una compilación estable superior.';
      document.getElementById('updateStatus').textContent = verifying(status) ? 'Verificando' : labels[status.Stage] || status.Stage;
      document.getElementById('updateMessage').textContent = status.Message || 'Se comprueba al abrir y cada 15 minutos.';
      if (!automaticPending) document.getElementById('updateAutomatic').checked = status.Automatic;
      const publishedChannel = status.LatestChannel || status.Channel;
      document.getElementById('updateLatestLabel').textContent = 'Última publicada en ' + (publishedChannel === 'beta' ? 'Beta' : 'Estable');
      document.getElementById('updateLatest').textContent = status.LatestVersion ? status.LatestVersion + ' · compilación ' + status.LatestBuild : '—';
      const locale = document.documentElement.lang || 'es';
      document.getElementById('updateLastCheck').textContent = status.LastCheck
        ? new Date(status.LastCheck).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'medium' }) : 'Aún no comprobada';
      const progress = document.getElementById('updateProgress');
      progress.hidden = status.Stage !== 'downloading' || verifying(status);
      progress.value = status.Downloaded; progress.max = status.Total || 1;
      document.getElementById('updateInstall').textContent = status.Platform === 'android-arm64' ? 'Instalar actualización' : 'Instalar y reiniciar';
      document.getElementById('updatePlatformHelp').textContent = status.Platform === 'android-arm64'
        ? 'QuicNet espera 15 minutos con el móvil en reposo. Si Android pide permiso o confirmación, pulsa Instalar actualización.'
        : 'QuicNet espera 15 minutos de inactividad y a que terminen las llamadas antes de reiniciar para actualizar.';
      renderNotices(status);
  }
  app.on('update_status', render);
  app.on('ws_status', data => {
    if (lastStatus) renderNotices(lastStatus);
    if (!data.connected) document.getElementById('updateMessage').textContent = 'Reconectando con la aplicación…';
  });
  async function runAction(path, opener) {
    if (actionPending || channelPending || automaticPending || (lastStatus && busyStages.includes(lastStatus.Stage))) return;
    actionPending = true;
    if (lastStatus) renderNotices(lastStatus);
    document.getElementById('updateCheck').disabled = true;
    let failure, prompted = false;
    try {
      const status = await app.wsRequest('updates.' + path);
      if (path === 'install' && status?.ConfirmEndCalls) {
        prompted = true;
        const confirmed = await app.confirmAction({
          title: '¿Actualizar igualmente?',
          message: 'Hay una llamada en curso. Si continúas, QuicNet finalizará las llamadas cuando la actualización esté lista para instalar.',
          cancelLabel: 'Seguir en llamada',
          confirmLabel: 'Sí, actualizar',
          danger: true
        });
        if (confirmed) {
          const result = await app.wsRequest('updates.install', { endCalls: true });
          if (result?.ConfirmEndCalls) failure = new Error('Finaliza las llamadas para poder actualizar.');
        }
      }
    } catch (error) { failure = error; }
    finally {
      actionPending = false;
      if (lastStatus) {
        renderNotices(lastStatus);
      } else document.getElementById('updateCheck').disabled = app.state.isWsConnected === false;
      if (failure) showError(failure);
      if (prompted && opener?.isConnected && !opener.disabled) opener.focus({ preventScroll: true });
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
      document.getElementById(id)?.addEventListener('click', event => runAction(path, event?.currentTarget));
    document.getElementById('updateAutomatic')?.addEventListener('change', async event => {
      const checkbox = event.target, previous = lastStatus?.Automatic ?? !checkbox.checked;
      if (automaticPending) { checkbox.checked = previous; return; }
      const enabled = checkbox.checked;
      const feedback = document.getElementById('updateAutomaticFeedback');
      feedback.textContent = 'Guardando preferencia…';
      automaticPending = true; checkbox.disabled = true;
      if (lastStatus) renderNotices(lastStatus);
      let failure;
      try {
        await app.wsRequest('updates.automatic', { enabled });
        feedback.textContent = lastStatus?.Stage === 'installing'
          ? 'La instalación está en curso. El cambio se aplica a futuras actualizaciones.'
          : enabled ? 'Actualizaciones automáticas activadas.'
            : lastStatus?.Stage === 'downloading'
              ? 'La descarga continuará. Podrás instalar la actualización cuando quieras.'
              : 'Actualizaciones automáticas desactivadas.';
      }
      catch (error) { checkbox.checked = lastStatus?.Automatic ?? previous; failure = error; feedback.textContent = ''; }
      finally {
        automaticPending = false;
        if (lastStatus) renderNotices(lastStatus);
        else checkbox.disabled = true;
      }
      if (failure) showError(failure);
    });
    document.getElementById('updateChannel')?.addEventListener('change', async event => {
      const select = event.target;
      if (actionPending || channelPending || automaticPending || (lastStatus && busyStages.includes(lastStatus.Stage))) {
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
    if (!lastStatus) {
      document.getElementById('updateAutomatic').disabled = true;
      document.getElementById('updateChannel').disabled = true;
    }
  });
})();
