(() => {
  const describe = value => {
    if (!value) return { state: 'unknown', label: 'Conectado · Estado desconocido' };
    const afk = value.isAfk ?? value.IsAfk;
    const accepts = value.acceptsNotifications ?? value.AcceptsNotifications;
    if (typeof afk !== 'boolean' || typeof accepts !== 'boolean') return { state: 'unknown', label: 'Conectado · Estado desconocido' };
    return { state: afk ? accepts ? 'afk' : 'afk-dnd' : accepts ? 'available' : 'dnd',
      label: afk ? accepts ? 'Ausente' : 'Ausente · No molestar' : accepts ? 'Disponible' : 'No molestar' };
  };
  QP.isPeerAvailable = (peer, now = Date.now()) => !!peer?.isQuicConnected ||
    (typeof peer?.discoveryAvailableUntilUtc === 'string' && Date.parse(peer.discoveryAvailableUntilUtc) > now);
  QP.userPresence = (peer, status = QP.state.status) => {
    if (peer?.id && peer.id === status?.node?.id) return describe(status.presence);
    if (!peer?.isQuicConnected) return QP.isPeerAvailable(peer)
      ? { state: 'available', label: 'Disponible' } : { state: 'offline', label: 'Sin conexión' };
    return describe(status?.peerPresence?.[peer.id]);
  };
  document.addEventListener('DOMContentLoaded', () => {
    const $ = id => document.getElementById(id);
    const dialog = $('userPresenceDialog');
    let current = null, busy = false, previousDnd, opener = null;
    function saveStatus(message, state = 'idle') {
      $('presenceSaveStatus').textContent = message;
      $('presenceSaveStatus').dataset.state = state;
    }
    function render(value) {
      if (!value) return;
      current = value;
      const info = describe(value);
      for (const id of ['sidebarPresenceText', 'profilePresenceText', 'userPresenceCurrent']) $(id).textContent = info.label;
      $('sidebarPresenceDot').dataset.userPresence = info.state;
      $('presenceCurrentBadge').dataset.userPresence = info.state;
      $('presenceCurrentIcon').setAttribute('href', `#presence-icon-${info.state === 'unknown' ? 'available' : info.state}`);
      dialog.setAttribute('aria-busy', String(busy));
      $('selfPresenceButton').setAttribute('aria-label', `Configurar estado: ${info.label}`);
      $('selfPresenceButton').title = info.label;
      for (const [id, dnd] of [['presenceAvailable', false], ['presenceDnd', true]]) {
        $(id).setAttribute('aria-pressed', String(value.doNotDisturb === dnd)); $(id).disabled = busy;
      }
      $('presenceIdleMinutes').disabled = busy;
      if (document.activeElement !== $('presenceIdleMinutes')) $('presenceIdleMinutes').value = String(value.idleAfterMinutes);
      $('presenceActivityHelp').textContent = value.activitySource === 'system'
        ? 'Se activa si no usas el ordenador. Al volver, recuperas tu estado, también desde otras aplicaciones.'
        : 'Se activa cuando dejas de usar QuicNet y se quita al volver.';
      if (!value.acceptsNotifications && !value.doNotDisturb) $('presenceActivityHelp').textContent += ' Tus ajustes generales o el horario de silencio mantienen las notificaciones silenciadas.';
      const dndKey = JSON.stringify([value.doNotDisturb, value.doNotDisturbUntil]);
      if (previousDnd !== dndKey) { previousDnd = dndKey; QP.emit('user_presence_changed', value); }
    }
    async function open(event) {
      opener = event.currentTarget;
      try {
        render(await QP.api('/api/profile/presence'));
        saveStatus('Los cambios se guardan automáticamente.');
        dialog.querySelector('.presence-state-guide').open = false;
        dialog.showModal(); $(current.doNotDisturb ? 'presenceDnd' : 'presenceAvailable').focus();
      } catch (error) { QP.toast(error.message, 'error'); }
    }
    async function save(dnd) {
      if (!current || busy) return;
      const minutes = Number($('presenceIdleMinutes').value);
      busy = true; render(current); saveStatus('Guardando…', 'saving');
      try {
        const value = await QP.api('/api/profile/presence', { method: 'POST', body: JSON.stringify({ doNotDisturb: dnd, idleAfterMinutes: minutes }) });
        busy = false; render(value); saveStatus('Estado guardado', 'saved');
      } catch (error) {
        busy = false; render(current);
        $('presenceIdleMinutes').value = String(current.idleAfterMinutes);
        saveStatus(error.message, 'error');
      }
    }
    $('selfPresenceButton').addEventListener('click', open);
    $('profilePresenceButton').addEventListener('click', open);
    $('presenceClose').addEventListener('click', () => dialog.close());
    dialog.addEventListener('click', event => {
      if (event.target !== dialog) return;
      const bounds = dialog.getBoundingClientRect();
      if (event.clientX < bounds.left || event.clientX > bounds.right ||
          event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
    });
    dialog.addEventListener('close', () => opener?.focus({ preventScroll: true }));
    $('presenceAvailable').addEventListener('click', () => save(false));
    $('presenceDnd').addEventListener('click', () => save(true));
    $('presenceIdleMinutes').addEventListener('change', () => save(current.doNotDisturb));
    QP.on('status_updated', status => { if (!busy) render(status.presence); });
    render(QP.state.status?.presence);
    let lastActivitySent = -Infinity;
    const activity = () => {
      if (document.hidden || performance.now() - lastActivitySent < 15000) return;
      lastActivitySent = performance.now();
      QP.api('/api/profile/activity', { method: 'POST', keepalive: true }).catch(() => {});
    };
    for (const name of ['pointerdown', 'pointermove', 'keydown', 'wheel', 'touchstart']) document.addEventListener(name, activity, { passive: true });
    window.addEventListener('focus', activity); activity();
  });
})();
