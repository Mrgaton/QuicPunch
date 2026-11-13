(() => {
  const $ = id => document.getElementById(id);
  const features = { messages: 'Mensajes', calls: 'Llamadas', files: 'Archivos', screen: 'Pantalla compartida', system: 'Avisos de la aplicación' };
  const contactFields = { enabled: 'Avisos', popup: 'Ventanas', sound: 'Sonido' };
  const flags = { Enabled: 'enabled', Desktop: 'desktop', InApp: 'inApp', Sound: 'sound', Preview: 'preview', WhileFocused: 'whileFocused', Dnd: 'doNotDisturb', QuietHours: 'quietHours' };
  let settings, loading = false, revision = 0, savedRevision = 0, saving, saveTimer;
  let latestPresence, presenceRevision = 0, testNoticeOpener = null;
  let systemControlsSound = false;
  QP.on('user_presence_changed', value => {
    latestPresence = value; presenceRevision++;
    if (!settings) return;
    settings.notifications.doNotDisturb = value.doNotDisturb;
    settings.notifications.doNotDisturbUntil = value.doNotDisturbUntil || null;
    $('noticeDnd').checked = value.doNotDisturb;
    $('noticeDndUntil').value = value.doNotDisturbUntil ? localDate(new Date(value.doNotDisturbUntil)) : '';
    updatePanels();
  });
  function status(text, state = '') { if (state === 'error') QP.reportError?.(new Error(text), 'Ajustes'); $('appBehaviorStatus').textContent = text; $('appBehaviorStatus').dataset.state = state; }
  function showTestNotice(message, title = 'Prueba de notificaciones', opener = document.activeElement) {
    $('notificationTestTitle').textContent = title; $('notificationTestMessage').textContent = message;
    const dialog = $('notificationTestDialog');
    if (!dialog.open) { testNoticeOpener = opener; dialog.showModal(); }
    $('notificationTestClose').focus();
  }
  function updatePanels() {
    $('noticeDndOptions').hidden = !$('noticeDnd').checked;
    $('noticeQuietOptions').hidden = !$('noticeQuietHours').checked;
    $('notificationBackgroundHelp').hidden = $('runInBackground').checked;
    const nativeSound = systemControlsSound && $('noticeDesktop').checked;
    $('noticeTone').closest('.field').hidden = nativeSound;
    $('noticeVolumeLabel').textContent = nativeSound ? 'Volumen del timbre' : 'Volumen';
    const warning = $('noticeGlobalWarning');
    const limits = [];
    if (!$('noticeEnabled').checked) limits.push('Las notificaciones están desactivadas para todos.');
    if ($('noticeDnd').checked) limits.push('No molestar silencia también a los contactos personalizados mientras esté activo.');
    if ($('noticeQuietHours').checked) limits.push('Durante el horario silencioso no recibirás avisos de ningún contacto.');
    if (!$('noticeSound').checked) limits.push('El sonido general está desactivado.');
    if (!$('noticeDesktop').checked && !$('noticeInApp').checked) limits.push('Las ventanas de aviso están desactivadas en todas las salidas.');
    if (!$('noticeWhileFocused').checked) limits.push('Los avisos están silenciados mientras usas QuicNet.');
    warning.textContent = limits.join(' '); warning.hidden = !limits.length;
  }
  function renderSystemStatus(value) {
    const box = $('notificationSystemStatus');
    systemControlsSound = !!value?.systemControlsSound;
    updatePanels();
    box.hidden = !value?.canOpenSettings;
    if (box.hidden) return;
    $('notificationSystemMessage').textContent = value.message;
    box.dataset.state = !value.enabled || !value.soundEnabled ? 'warning' : 'ready';
    $('notificationSystemSoundHelp').hidden = !value.systemControlsSound;
  }
  async function refreshSystemStatus() {
    if (document.hidden || !$('settings-notifications')?.checkVisibility()) return;
    try { renderSystemStatus(await QP.api('/api/notifications/status')); }
    catch { /* Keep the last known state; the connection banner reports outages. */ }
  }
  const labels = new Map();
  const rule = () => ({ enabled: true, popup: true, sound: true });
  function hash(value) {
    if (/^[a-f0-9]{64}$/i.test(value || '')) return value.toUpperCase();
    try { const bytes = atob(value || ''); return bytes.length === 32 ? [...bytes].map(c => c.charCodeAt(0).toString(16).padStart(2, '0')).join('').toUpperCase() : ''; } catch { return ''; }
  }
  function time(minutes) { return String(Math.floor(minutes / 60)).padStart(2, '0') + ':' + String(minutes % 60).padStart(2, '0'); }
  function localDate(date) { return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16); }
  function ruleRow(name, value, changed) {
    const row = QP.el('div', 'notice-rule'); row.append(QP.el('strong', '', name));
    for (const [key, text] of [['enabled', 'Avisos'], ['popup', 'Ventanas'], ['sound', 'Sonido']]) {
      const label = QP.el('label', 'check-line'); const input = document.createElement('input'); input.type = 'checkbox'; input.checked = value[key];
      input.setAttribute('aria-label', text + ': ' + name); input.setAttribute('role', 'switch');
      input.addEventListener('change', () => { value[key] = input.checked; changed?.(); });
      label.append(input, QP.el('span', 'sr-only', text)); row.append(label);
    }
    return row;
  }
  function renderContacts(status = QP.state.status) {
    if (!settings) return;
    for (const peer of [...(status?.savedPeers || []), ...(status?.peers || [])]) {
      const key = hash(peer.certHash); if (key) labels.set(key, peer.name || 'Contacto ' + key.slice(0, 8));
    }
    const box = $('noticeContactRules');
    const expanded = new Set([...box.querySelectorAll('details[open]')].map(row => row.dataset.contact));
    const focusedId = box.contains(document.activeElement) ? document.activeElement.id : null;
    const scrollTop = box.scrollTop;
    box.replaceChildren();
    const query = $('noticeContactSearch').value.trim().toLocaleLowerCase();
    const n = settings.notifications;
    const customized = key => Object.keys(contactFields).some(field => typeof n.contacts[key]?.[field] === 'boolean');
    const keys = [...new Set([...labels.keys(), ...Object.keys(n.contacts)])]
      .sort((a, b) => (labels.get(a) || a).localeCompare(labels.get(b) || b, 'es', {sensitivity: 'base'}));
    const updateCount = () => {
      const count = keys.filter(customized).length;
      $('noticeContactCount').textContent = `${keys.length} ${keys.length === 1 ? 'contacto' : 'contactos'} · ${count} ${count === 1 ? 'personalizado' : 'personalizados'}`;
    };
    updateCount();
    for (const key of keys) {
      const name = labels.get(key) || 'Contacto ' + key.slice(0, 8);
      if (query && !name.toLocaleLowerCase().includes(query)) continue;
      if ($('noticeOnlyCustom').checked && !customized(key)) continue;
      const row = QP.el('details', 'notice-contact'); row.dataset.contact = key; row.open = expanded.has(key);
      const summary = QP.el('summary', ''); summary.id = 'noticeContact-' + key;
      const copy = QP.el('span', 'notice-contact-copy'), state = QP.el('small', '');
      copy.append(QP.el('strong', '', name), state);
      const badge = QP.el('span', 'notice-contact-badge'); summary.append(copy, badge);
      const controls = QP.el('div', 'notice-contact-controls');
      const selects = new Map();
      for (const [field, text] of Object.entries(contactFields)) {
        const label = QP.el('label', 'field'); label.append(QP.el('span', '', text));
        const select = QP.el('select', 'select'); select.id = `noticeContact-${key}-${field}`;
        select.setAttribute('aria-label', text + ': ' + name);
        for (const [value, caption] of [['inherit', ''], ['true', 'Activado'], ['false', 'Desactivado']]) {
          const option = QP.el('option', '', caption); option.value = value; select.append(option);
        }
        select.addEventListener('change', () => {
          const value = {...n.contacts[key]};
          if (select.value === 'inherit') delete value[field]; else value[field] = select.value === 'true';
          if (Object.values(value).some(value => typeof value === 'boolean')) n.contacts[key] = value;
          else delete n.contacts[key];
          refresh(); updateCount();
          if ($('noticeOnlyCustom').checked && !customized(key)) queueMicrotask(() => renderContacts());
        });
        selects.set(field, select); label.append(select); controls.append(label);
      }
      const reset = QP.el('button', 'btn ghost sm', 'Usar todos los predeterminados'); reset.type = 'button';
      reset.addEventListener('click', () => {
        delete n.contacts[key]; refresh(); updateCount(); changed();
        if ($('noticeOnlyCustom').checked) { renderContacts(); $('noticeContactSearch').focus(); }
        else selects.get('enabled').focus();
      });
      const foot = QP.el('div', 'notice-contact-foot');
      foot.append(QP.el('small', '', 'Solo los valores personalizados dejan de seguir los predeterminados.'), reset);
      function refresh() {
        const value = n.contacts[key] || {}, effective = {};
        for (const [field, select] of selects) {
          effective[field] = value[field] ?? n.contactDefaults[field];
          select.options[0].textContent = 'Predeterminado: ' + (n.contactDefaults[field] ? 'sí' : 'no');
          select.value = typeof value[field] === 'boolean' ? String(value[field]) : 'inherit';
        }
        const custom = customized(key);
        badge.textContent = custom ? 'Personalizado' : 'Predeterminados';
        row.dataset.custom = String(custom); reset.disabled = !custom;
        state.textContent = !effective.enabled ? 'Sin avisos' : effective.popup && effective.sound ? 'Ventanas y sonido' :
          effective.popup ? 'Solo ventanas' : effective.sound ? 'Solo sonido' : 'Sin ventanas ni sonido';
      }
      refresh(); row.append(summary, controls, foot); box.append(row);
    }
    if (!box.childElementCount) box.append(QP.el('p', 'muted notice-contact-empty', keys.length ?
      ($('noticeOnlyCustom').checked && !query ? 'Todos tus contactos usan los predeterminados.' : 'No hay contactos que coincidan con estos filtros.') : 'Tus contactos aparecerán aquí cuando los añadas.'));
    if (focusedId) ($(focusedId) || $('noticeContactSearch')).focus({preventScroll: true});
    box.scrollTop = scrollTop;
  }
  function renderContactDefaults() {
    const n = settings.notifications;
    const descriptions = {enabled: 'Permitir avisos de los contactos.', popup: 'Mostrar avisos del sistema y dentro de QuicNet.', sound: 'Reproducir avisos y timbres de llamada.'};
    const box = $('noticeContactDefaults'); box.replaceChildren();
    for (const [key, text] of Object.entries(contactFields)) {
      const label = QP.el('label', 'check-line'), input = document.createElement('input');
      input.type = 'checkbox'; input.id = 'noticeContactDefault-' + key; input.checked = n.contactDefaults[key];
      input.setAttribute('role', 'switch'); input.setAttribute('aria-label', text + ': predeterminado de contactos');
      const copy = QP.el('span', ''); copy.append(QP.el('strong', '', text), QP.el('small', '', descriptions[key]));
      label.append(input, copy); box.append(label);
      input.addEventListener('change', () => { n.contactDefaults[key] = input.checked; renderContacts(); });
    }
  }
  async function load() {
    if (loading || saving || revision !== savedRevision) return;
    loading = true; $('retryAppBehavior').classList.add('hidden');
    const presenceAtStart = presenceRevision;
    try {
      const response = await QP.api('/api/app/preferences'); settings = response.settings;
      settings.notifications.contactDefaults = {...rule(), ...settings.notifications.contactDefaults};
      settings.notifications.contacts ||= {};
      if (presenceRevision > presenceAtStart && latestPresence) {
        settings.notifications.doNotDisturb = latestPresence.doNotDisturb;
        settings.notifications.doNotDisturbUntil = latestPresence.doNotDisturbUntil || null;
      }
      $('startWithSystem').checked = settings.startWithSystem; $('runInBackground').checked = settings.runInBackground;
      $('appPlatformHelp').textContent = response.platformHelp;
      renderSystemStatus(response.notificationStatus);
      for (const [suffix, key] of Object.entries(flags)) $('notice' + suffix).checked = settings.notifications[key];
      const n = settings.notifications;
      $('noticeTone').value = n.tone; $('noticeVolume').value = n.volume; $('noticeVolumeValue').textContent = n.volume + ' %';
      $('noticeQuietStart').value = time(n.quietStart); $('noticeQuietEnd').value = time(n.quietEnd);
      $('noticeDndUntil').value = n.doNotDisturbUntil ? localDate(new Date(n.doNotDisturbUntil)) : '';
      $('noticeFeatureRules').replaceChildren();
      for (const [key, name] of Object.entries(features)) {
        n.features[key] ||= rule(); $('noticeFeatureRules').append(ruleRow(name, n.features[key]));
      }
      renderContactDefaults(); renderContacts(); updatePanels(); $('appBehaviorFields').disabled = false; $('startWithSystem').disabled = !response.startupSupported;
      status('Cambios automáticos');
    } catch (error) { status(error.message, 'error'); $('retryAppBehavior').classList.remove('hidden'); }
    finally { loading = false; }
  }
  function collect() {
    if (!settings) return false;
    for (const id of ['noticeDndUntil', 'noticeQuietStart', 'noticeQuietEnd']) {
      const active = id === 'noticeDndUntil' ? $('noticeDnd').checked : $('noticeQuietHours').checked;
      if (active && !$(id).checkValidity()) { status('Completa la fecha o la hora para aplicar el cambio.', 'error'); return false; }
    }
    settings.startWithSystem = $('startWithSystem').checked; settings.runInBackground = $('runInBackground').checked;
    for (const [suffix, key] of Object.entries(flags)) settings.notifications[key] = $('notice' + suffix).checked;
    const n = settings.notifications;
    n.volume = Number($('noticeVolume').value); n.tone = $('noticeTone').value;
    const minutes = id => { const [h, m] = $(id).value.split(':').map(Number); return h * 60 + m; };
    if ($('noticeQuietStart').validity.valid) n.quietStart = minutes('noticeQuietStart');
    if ($('noticeQuietEnd').validity.valid) n.quietEnd = minutes('noticeQuietEnd');
    n.doNotDisturbUntil = $('noticeDndUntil').value ? new Date($('noticeDndUntil').value).toISOString() : null;
    return true;
  }
  async function flush() {
    clearTimeout(saveTimer);
    if (!settings) return false;
    if (saving) return saving;
    saving = (async () => {
      while (savedRevision < revision) {
        const submitted = revision, payload = JSON.stringify(settings);
        status('Aplicando cambios…'); $('retryAppBehavior').classList.add('hidden');
        try {
          await QP.api('/api/app/preferences', { method: 'POST', headers: {'Content-Type':'application/json'}, body: payload, keepalive: new Blob([payload]).size < 60000 });
          savedRevision = submitted;
        } catch (error) {
          if (revision > submitted) continue;
          status('No se ha aplicado el cambio: ' + error.message, 'error');
          $('retryAppBehavior').classList.remove('hidden'); return false;
        }
      }
      status('Todos los cambios aplicados', 'saved'); return true;
    })();
    try { return await saving; } finally { saving = null; }
  }
  function changed(delay = 0) {
    updatePanels();
    if (!collect()) return;
    revision++; clearTimeout(saveTimer);
    if (delay) { status('Aplicando cambios…'); saveTimer = setTimeout(flush, delay); }
    else flush();
  }
  document.addEventListener('DOMContentLoaded', () => {
    if (!$('appBehaviorForm')) return;
    document.querySelectorAll('#appBehaviorFields input[type=checkbox]').forEach(input => input.setAttribute('role', 'switch'));
    $('appBehaviorForm').addEventListener('submit', event => { event.preventDefault(); changed(); });
    $('appBehaviorForm').addEventListener('change', event => { if (!['noticeContactSearch', 'noticeOnlyCustom'].includes(event.target.id)) changed(); });
    $('testAppNotification').addEventListener('click', async () => {
      const button = $('testAppNotification'); button.disabled = true;
      try {
        if (!collect() || !await flush()) return;
        const result = await QP.api('/api/notifications/test', {method:'POST'});
        if (result?.sent === false) {
          showTestNotice(result.message, result.reason === 'do_not_disturb' ? 'Estás en modo No molestar' : 'Prueba de notificaciones', button);
          status(result.message, 'warning'); return;
        }
        status('Prueba enviada.', 'saved');
      } catch (error) { status(error.message, 'error'); }
      finally { button.disabled = false; }
    });
    $('notificationTestClose').addEventListener('click', () => $('notificationTestDialog').close());
    $('openNotificationSettings').addEventListener('click', async () => {
      try { await QP.api('/api/notifications/system-settings', {method:'POST'}); }
      catch (error) { showTestNotice(error.message); }
    });
    window.addEventListener('focus', refreshSystemStatus);
    document.addEventListener('visibilitychange', refreshSystemStatus);
    QP.on('settings_section_changed', ({section}) => { if (section === 'notifications') refreshSystemStatus(); });
    $('notificationTestDialog').addEventListener('close', () => { if (testNoticeOpener?.isConnected && !testNoticeOpener.disabled) testNoticeOpener.focus({preventScroll:true}); });
    const ringtone = $('notificationRingtonePreview'), ringButton = $('testCallRingtone');
    const stopRingtone = () => {
      ringtone.pause(); ringtone.removeAttribute('src'); ringtone.load();
      ringButton.textContent = 'Probar tono de llamada'; ringButton.setAttribute('aria-pressed', 'false');
    };
    ringButton.addEventListener('click', async () => {
      if (ringButton.getAttribute('aria-pressed') === 'true') { stopRingtone(); return; }
      const volume = Number($('noticeVolume').value);
      if (volume === 0) { showTestNotice('El volumen está a cero. Súbelo para escuchar el tono de llamada.', 'Volumen silenciado'); return; }
      ringtone.src = '/api/notifications/ringtone?volume=' + volume;
      ringButton.textContent = 'Detener tono'; ringButton.setAttribute('aria-pressed', 'true');
      try { await ringtone.play(); }
      catch (error) { if (error.name !== 'AbortError') { stopRingtone(); showTestNotice('No se pudo reproducir el tono de llamada: ' + error.message); } }
    });
    ringtone.addEventListener('ended', stopRingtone);
    ringtone.addEventListener('error', () => {
      if (!ringtone.hasAttribute('src')) return;
      stopRingtone(); showTestNotice('No se pudo cargar el tono de llamada. Vuelve a intentarlo.');
    });
    QP.on('view_changed', () => { if (!document.getElementById('settings-notifications')?.checkVisibility()) stopRingtone(); });
    QP.on('settings_section_changed', ({section}) => { if (section !== 'notifications') stopRingtone(); });
    window.addEventListener('pagehide', stopRingtone);
    $('retryAppBehavior').addEventListener('click', () => settings ? changed() : load());
    $('noticeContactSearch').addEventListener('input', () => renderContacts());
    $('noticeOnlyCustom').addEventListener('change', () => renderContacts());
    $('noticeVolume').addEventListener('input', () => { $('noticeVolumeValue').textContent = $('noticeVolume').value + ' %'; changed(150); });
    document.querySelectorAll('[data-notice-snooze]').forEach(button => button.addEventListener('click', () => {
      $('noticeDnd').checked = true; $('noticeDndUntil').value = localDate(new Date(Date.now() + Number(button.dataset.noticeSnooze) * 3600000)); changed();
    }));
    $('quitAppBtn').addEventListener('click', async () => {
      if (!confirm('¿Salir de QuicNet y desconectar las sesiones activas?')) return;
      try { await QP.api('/api/shutdown', { method: 'POST' }); $('appBehaviorStatus').textContent = 'QuicNet se está cerrando.'; }
    catch (error) { $('appBehaviorStatus').textContent = error.message; }
    });
    let contactsKey = '';
    QP.on('status_updated', status => {
      const signature = JSON.stringify([...(status.savedPeers || []), ...(status.peers || [])].map(p => [p.certHash, p.name]));
      if (signature !== contactsKey) { contactsKey = signature; renderContacts(status); }
    });
    QP.on('app_notice', notice => {
      const category = notice.Category || notice.category;
      const label = {messages:'Abrir conversación',calls:'Ver llamada',files:'Ver archivo',screen:'Ver pantalla'}[category] || 'Abrir';
      QP.toast((notice.Title || notice.title) + ': ' + (notice.Message || notice.message), '',
        {label, href:notice.Url || notice.url});
    });
    const client = window.crypto?.randomUUID?.() || String(Date.now()) + Math.random();
    const presence = () => QP.api('/api/notifications/presence', { method: 'POST', keepalive: true,
      body: JSON.stringify({ client, focused: !document.hidden && document.hasFocus() }) }).catch(() => {});
    window.addEventListener('focus', presence); window.addEventListener('blur', presence); document.addEventListener('visibilitychange', presence);
    window.addEventListener('pagehide', () => {
      if (settings && revision !== savedRevision) flush();
      QP.api('/api/notifications/presence', { method: 'POST', keepalive: true, body: JSON.stringify({ client, focused: false }) }).catch(() => {});
    });
    setInterval(presence, 15000); presence(); load();
  });
})();
