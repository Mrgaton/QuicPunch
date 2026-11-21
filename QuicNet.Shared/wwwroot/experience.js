/* Task-oriented home and shared interaction behavior. Protocols remain in their modules. */
(() => {
  const $ = id => document.getElementById(id);
  let status = null;
  let filter = 'all';
  let connectionFilter = 'all';
  let sort = 'activity';
  try { sort = localStorage.getItem('quicnet.contactSort.v2') || (['usage','name'].includes(localStorage.getItem('quicnet.contactSort')) ? localStorage.getItem('quicnet.contactSort') : 'activity'); } catch {}
  let contactSignature = '';
  let availabilityTimer;
  const pending = new Set();
  let reviewedHashes = new Set();
  let invitationOrigin;
  const needsReview = p => p.source !== 'pending' && !p.isTrusted && !reviewedHashes.has(p.certHash);
  const latestDiscoveryDeadline = (a, b) => Number.isFinite(Date.parse(a)) &&
    (!Number.isFinite(Date.parse(b)) || Date.parse(a) > Date.parse(b)) ? a : b;

  function contacts(snapshot) {
    const result = new Map();
    const key = p => p.certHash || p.id;
    for (const p of snapshot.savedPeers || []) result.set(key(p), { ...p, source: 'saved', isSaved: true, isTrusted: p.isTrusted !== false });
    for (const p of snapshot.discoveredPeers || []) {
      const previous = result.get(key(p));
      result.set(key(p), { ...p, ...previous, token: p.token, source: previous ? 'saved' : 'discovered',
        discoveryAvailableUntilUtc: latestDiscoveryDeadline(p.discoveryAvailableUntilUtc, previous?.discoveryAvailableUntilUtc) });
    }
    for (const p of snapshot.peers || []) {
      const previous = result.get(key(p));
      result.set(key(p), { ...previous, ...p, source: 'live',
        discoveryAvailableUntilUtc: latestDiscoveryDeadline(p.discoveryAvailableUntilUtc, previous?.discoveryAvailableUntilUtc) });
    }
    for (const attempt of snapshot.activeInterrogations || []) {
      let item = result.get(attempt.certHash);
      if (!item && attempt.certHash) { item = {...attempt, isTrusted:false, isSaved:false, source:'pending'}; result.set(attempt.certHash, item); }
      if (item && !item.isQuicConnected) { item.connectionState = 'connecting'; item.interrogationId = attempt.id; }
    }
    const nameKey = name => String(name || '').normalize('NFKC').trim().toLocaleLowerCase('es');
    const knownNames = new Map();
    for (const peer of result.values()) {
      const hash = QP.identity.normalize(peer);
      if (!peer.isTrusted || !peer.isSaved || !hash || !nameKey(peer.name)) continue;
      const hashes = knownNames.get(nameKey(peer.name)) || new Set();
      hashes.add(hash); knownNames.set(nameKey(peer.name), hashes);
    }
    return [...result.values()].filter(p => QP.peerInConnectionMode(p, snapshot)).map(peer => {
      const hash = QP.identity.normalize(peer);
      return {...peer, hasNameCollision: !peer.isTrusted && !!hash &&
        [...(knownNames.get(nameKey(peer.name)) || [])].some(known => known !== hash)};
    }).sort((a, b) => Number(!!b.isQuicConnected) - Number(!!a.isQuicConnected) || Number(!!b.isSaved) - Number(!!a.isSaved) || String(a.name || '').localeCompare(String(b.name || ''), 'es'));
  }

  function openContactInvitation(opener, { mode = 'receive', restore } = {}) {
    const dialog = $('addContactDialog');
    if (dialog.open) return;
    const share = mode === 'share';
    invitationOrigin = { opener, restore, accepted: false };
    $('invitationShare').hidden = !share;
    $('invitationReceive').hidden = share;
    $('addContactTitle').textContent = share ? 'Compartir mi invitación' : 'Usar una invitación recibida';
    $('returnFromInvitation').hidden = true;
    if (!$('tokenInput').readOnly) {
      $('connectFeedback').textContent = '';
      $('tokenInput').removeAttribute('aria-invalid');
      $('activateTokenTransport').hidden = true;
    }
    dialog.showModal();
    $('connectArea').scrollTop = 0;
    const target = share ? $('shareInvitationBtn') : $('tokenInput');
    (target.disabled ? $('closeAddContact') : target).focus({ preventScroll: true });
  }

  function contactButton(label, action, p, primary = false) {
    const button = QP.button(label, primary ? 'primary' : '', () => performContactAction(p, action, button));
    button.dataset.actionKey = `${p.certHash || p.id}:${action}`;
    button.disabled = pending.has(button.dataset.actionKey);
    if (['trust', 'decline', 'review'].includes(action)) {
      button.disabled ||= !p.certHash || ['trust', 'decline'].some(decision => pending.has(`${p.certHash || p.id}:${decision}`));
    }
    button.setAttribute('aria-label', `${label}: ${p.name || 'contacto'}`);
    return button;
  }

  function matchesContactFilter(p, selected = filter) {
    return selected === 'all' ? true : selected === 'saved' ? !!p.isSaved : p.source !== 'pending' && !p.isTrusted;
  }

  const contactState = p => p.isQuicConnected ? 'connected' : p.connectionState === 'connecting'
    ? 'connecting' : QP.isPeerAvailable?.(p) ? 'available' : 'disconnected';

  function contactSignals(card, peer) {
    const box = card.querySelector('.contact-card-signals');
    const signature = JSON.stringify([peer.lastUsedUtc, peer.lastConnectedUtc, peer.locationIsHistorical, peer.ping, peer.pingSource, peer.isQuicConnected, peer.isTor, peer.networkType, peer.location]);
    if (box.dataset.signature === signature) return;
    box.dataset.signature = signature;
    box.replaceChildren();
    const ping = typeof peer.ping === 'number' && Number.isFinite(peer.ping) && peer.ping > 0 ? peer.ping : null;
    const latency = QP.el('span', 'contact-ping', ping == null ? 'Ping pendiente' : `${peer.isQuicConnected ? '' : 'Último ping · '}${ping.toLocaleString('es', { maximumFractionDigits: 1 })} ms`);
    latency.title = ping == null ? 'Se mostrará al obtener una medición de la conexión; no se estima por distancia.' : peer.pingSource || 'Tiempo de ida y vuelta de la conexión';
    if (!peer.isSaved || peer.isQuicConnected || peer.connectionState === 'connecting' || ping != null) box.append(latency);
    const location = peer.isTor || peer.networkType === 'Tor' ? {status:'tor'} : peer.location;
    const names = [...new Set([location?.municipality || location?.city, location?.region].filter(Boolean))];
    const country = location?.country || location?.countryCode || '';
    const code = /^[a-z]{2}$/i.test(location?.countryCode || '') ? location.countryCode.toLowerCase().replace(/^uk$/, 'gb') : '';
    const located = location?.status === 'located' && (names.length || country || code);
    const text = located ? '≈ ' + (names[0] || country || code.toUpperCase()) :
      location?.status === 'tor' ? 'Tor · ubicación oculta' : location?.status === 'local' ? 'Red local' :
      location?.status === 'missing' ? 'Preparando ubicación…' : '';
    const place = card.querySelector('.contact-location');
    const placeName = QP.el('span', 'contact-location-name', text);
    place.replaceChildren(placeName);
    place.hidden = !text;
    if (located && code) {
      const flag = QP.el('img', 'country-flag');
      flag.src = `/flags/${code}.svg`; flag.alt = country || code.toUpperCase(); flag.title = flag.alt; flag.width = 18; flag.height = 14;
      flag.addEventListener('error', () => {
        if (names.length && country) placeName.append(', ' + country);
        flag.remove();
      }, { once: true });
      place.append(flag);
    } else if (located && names.length && country) {
      placeName.append(', ' + country);
    }
    place.title = located
      ? [...names, country || code.toUpperCase()].filter(Boolean).join(', ') + '. ' +
        (peer.locationIsHistorical ? 'Ubicación de la última IP observada. ' : '') +
        (location?.source ? `Fuente: ${location.source}. ` : '') +
        (location?.advertised ? 'IP anunciada por el contacto. ' : '') +
        'Ubicación aproximada de la IP. Una VPN o red móvil puede situarla en otra región.'
      : text;
    if (peer.lastUsedUtc || (peer.isSaved && peer.lastConnectedUtc)) {
      const date = new Date(peer.lastUsedUtc || peer.lastConnectedUtc);
      if (Number.isFinite(date.getTime())) {
        const dateLabel = date.toLocaleString('es', {
          day: 'numeric', month: 'short', year: date.getFullYear() === new Date().getFullYear() ? undefined : 'numeric',
          hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
        });
        const seen = QP.el('time', peer.lastUsedUtc ? 'contact-last-used' : 'contact-last-connected',
          `${peer.lastUsedUtc ? 'Último uso' : 'Última conexión'} · ${dateLabel}`);
        seen.dateTime = date.toISOString();
        seen.title = date.toLocaleString('es', {dateStyle: 'long', timeStyle: 'short'});
        box.append(seen);
      }
    }
  }

  function renderContacts() {
    if (!status) return;
    const all = contacts(status);
    const query = $('contactSearch').value.trim().toLocaleLowerCase('es');
    const list = all.filter(p => (!query || `${p.name || ''} ${p.canonicalId || ''}`.toLocaleLowerCase('es').includes(query)) &&
      matchesContactFilter(p) && (connectionFilter === 'all' || contactState(p) === connectionFilter));
    const group = p => ['connected','connecting','available','disconnected'].indexOf(contactState(p));
    const time = p => Date.parse((sort === 'recent' ? null : p.lastUsedUtc) || p.lastConnectedUtc || '') || 0;
    list.sort((a, b) => group(a) - group(b) || (sort === 'usage' ? (b.usageCount || 0) - (a.usageCount || 0) || time(b) - time(a) : ['activity','recent'].includes(sort) ? time(b) - time(a) : 0) || String(a.name || '').localeCompare(String(b.name || ''), 'es') || String(a.certHash || a.id).localeCompare(String(b.certHash || b.id)));
    // Ignore rapidly changing telemetry: keep focus and card position stable during live updates.
    const signature = JSON.stringify([query, filter, connectionFilter, sort, list.map(p => [p.id, p.certHash, p.name, p.isTrusted, needsReview(p), p.isSaved, p.isQuicConnected, p.connectionState, p.interrogationId, p.source, p.lastConnectionError, p.hasNameCollision, p.isTor, p.networkType, status.avatars?.[p.id], QP.userPresence?.(p, status)]), [...pending]]);
    if (signature === contactSignature) {
      const byKey = new Map(list.map(p => [p.certHash || p.id, p]));
      for (const card of $('homeContacts').querySelectorAll('[data-contact-key]')) {
        const peer = byKey.get(card.dataset.contactKey);
        if (peer) contactSignals(card, peer);
      }
      return;
    }
    contactSignature = signature;
    const active = document.activeElement?.dataset.actionKey;
    const box = $('homeContacts');
    box.setAttribute('aria-busy', 'false');
    box.replaceChildren();
    $('contactSearchStatus').textContent = `${list.length} contactos`;
    if (!list.length) {
      const empty = QP.el('div', 'empty-state');
      const illustration = QP.el('span', 'empty-illustration');
      illustration.append(document.querySelector('.nav-link[data-page="vpn"] .icon').cloneNode(true));
      empty.append(illustration, QP.el('h3', '', all.length ? 'No hay contactos en esta vista' : 'Todo empieza con alguien'),
        QP.el('p', '', all.length ? 'Prueba otro nombre o cambia el filtro para ver más contactos.' : 'Comparte tu invitación o pega la de un amigo. Tus contactos aparecerán aquí.'));
      const alternative = ['all', 'saved', 'review'].find(value => value !== filter && all.some(p => matchesContactFilter(p, value)));
      const label = query ? 'Limpiar búsqueda' : connectionFilter !== 'all' ? 'Mostrar todos los estados' : alternative === 'all' ? 'Ver todos' : alternative === 'saved' ? 'Ver guardados' : alternative === 'review' ? 'Ver sin confianza' : 'Añadir un contacto';
      const action = QP.button(label, '', () => {
        if (query) { $('contactSearch').value = ''; renderContacts(); }
        else if (connectionFilter !== 'all') { connectionFilter = 'all'; $('contactConnectionFilter').value = 'all'; renderContacts(); }
        else if (alternative) setFilter(alternative);
        else openContactInvitation(action);
      });
      empty.append(action); box.append(empty); return;
    }
    const groups = new Map();
    for (const [value,label] of [[0,'Conectados'],[1,'Conectando'],[2,'Disponibles'],[3,'Sin conexión']]) {
      const count = list.filter(p => group(p) === value).length;
      if (!count) continue;
      const section = QP.el('section', 'contact-state-group'); section.dataset.contactState = ['connected','connecting','available','disconnected'][value];
      const heading = QP.el('h2', 'contact-group-title', label); heading.id = 'contact-group-' + value;
      heading.append(QP.el('span', 'count-label', count));
      section.setAttribute('aria-labelledby', heading.id);
      const rows = QP.el('div', 'contact-state-list'); rows.setAttribute('role','list');
      section.append(heading, rows); box.append(section); groups.set(value, rows);
    }
    for (const p of list) {
      const card = QP.el('div', 'contact-card');
      card.dataset.contactKey = p.certHash || p.id;
      card.setAttribute('role', 'listitem');
      card.classList.toggle('needs-review', needsReview(p));
      const head = QP.el('div', 'contact-card-head');
      const title = QP.el('div', 'contact-card-name');
      const identityLine = QP.el('div', 'contact-card-identity-line');
      identityLine.append(QP.identity.createCapsule(p, {snapshot: status, slot: 'contact'}), QP.el('span', 'contact-location'));
      title.append(identityLine);
      head.append(title);
      const meta = QP.el('div', 'contact-card-meta');
      const stateLabel = p.isQuicConnected ? QP.userPresence?.(p, status)?.label || 'Conectado' : p.connectionState === 'connecting' ? 'Conectando…' : QP.isPeerAvailable?.(p) ? 'Disponible' : 'Sin conexión';
      const connection = QP.el('div', 'contact-card-connection');
      connection.append(QP.el('span', `badge ${QP.isPeerAvailable?.(p) ? 'green' : ''}`, stateLabel));
      const summary = QP.el('div', 'contact-card-summary');
      summary.append(connection, QP.el('div', 'contact-card-signals'));
      const statusBlock = QP.el('div', 'contact-card-status');
      statusBlock.append(summary);
      if (!p.isTrusted) meta.append(QP.el('span', `badge ${needsReview(p) ? 'yellow' : ''}`, needsReview(p) ? 'Nuevo · pendiente de revisión' : 'Sin confianza · revisado'));
      if (p.hasNameCollision) {
        const warning = QP.el('span', 'badge yellow', 'Mismo nombre · otra identidad');
        warning.title = 'Su certificado es distinto al de un contacto guardado con este nombre.';
        meta.append(warning);
      }
      const actions = QP.el('div', 'contact-card-actions');
      if (p.isTrusted) {
        if (p.isQuicConnected || p.canSendOffline) actions.append(contactButton('Mensaje', 'message', p));
        if (p.isQuicConnected) actions.append(contactButton('Llamar', 'call', p));
        if (!p.isQuicConnected && p.connectionState !== 'connecting') {
          const tor = p.isTor || p.networkType === 'Tor';
          const service = tor ? status.tor : status.wan;
          const active = service?.isStarted ?? service?.running ?? false;
          actions.prepend(contactButton(active ? (p.lastConnectionError ? 'Reintentar' : 'Conectar') : (tor ? 'Cambiar a Tor' : 'Cambiar a WAN'), active ? 'connect' : 'change-mode', p, true));
        }
        if (!p.isSaved && p.source === 'live') actions.append(contactButton('Guardar', 'save', p));
      } else if (p.source === 'live') {
        actions.append(contactButton('Aceptar contacto', 'trust', p, true));
        if (needsReview(p)) actions.append(contactButton('No confiar', 'decline', p));
        actions.append(contactButton('Revisar contacto', 'review', p));
      } else if (p.connectionState !== 'connecting') actions.append(contactButton('Conectar y revisar', 'connect', p, true));
      if (p.interrogationId) actions.prepend(contactButton('Cancelar búsqueda', 'cancel', p));
      if (meta.childElementCount) title.append(meta);
      if (p.lastConnectionError && !p.isQuicConnected && p.connectionState !== 'connecting') statusBlock.append(QP.el('p', 'contact-error', 'No se pudo establecer la conexión. ' + p.lastConnectionError));
      card.append(head, statusBlock, actions);
      contactSignals(card, p);
      groups.get(group(p)).append(card);
    }
    if (active) [...box.querySelectorAll('button')].find(b => b.dataset.actionKey === active)?.focus({ preventScroll: true });
  }

  function setFilter(value) {
    filter = ['all', 'saved', 'review'].includes(value) ? value : 'all';
    $('openUntrustedBtn').hidden = filter === 'review';
    document.querySelectorAll('[data-contact-filter]').forEach(b => {
      b.classList.toggle('active', b.dataset.contactFilter === filter);
      b.setAttribute('aria-pressed', String(b.dataset.contactFilter === filter));
    });
    renderContacts();
  }

  async function performContactAction(p, action, button) {
    if (action === 'change-mode') {
      button.disabled = true;
      try { if (await QP.connectionModes.request(p.isTor || p.networkType === 'Tor' ? 'tor' : 'wan')) await performContactAction(p, 'connect', button); }
      finally { button.disabled = false; }
      return;
    }
    if (action === 'cancel') {
      button.disabled = true;
      try { await QP.api('/api/cancel-interrogation', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:p.interrogationId})}); await QP.getStatus(true); }
      catch (error) { QP.toast(error.message, 'error'); button.disabled = false; }
      return;
    }
    if (action === 'message') { QP.navigate('chat', '/chat.html?peer=' + encodeURIComponent(p.id)); return; }
    if (action === 'call') { QP.navigate('voice'); QP.emit('focus_voice_peer', { peerId: p.id }); return; }
    if (action === 'review') { reviewContact(p); return; }
    if (action === 'trust' || action === 'decline') {
      if (!p.certHash || ['trust', 'decline'].some(decision => pending.has(`${p.certHash || p.id}:${decision}`))) return;
      const key = button.dataset.actionKey;
      pending.add(key); renderContacts();
      try {
        await QP.api('/api/peer/trust', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({peerId:p.id, trust:action === 'trust'})});
        QP.toast(action === 'trust' ? 'Contacto aceptado. Ya puedes enviarle mensajes.' : 'Revisión guardada. El contacto permanece sin confianza.');
        await QP.getStatus(true);
      } catch (error) { QP.toast(error.message, 'error'); }
      finally { pending.delete(key); contactSignature = ''; renderContacts(); }
      return;
    }
    const key = button.dataset.actionKey;
    if (pending.has(key)) return;
    pending.add(key); button.disabled = true; button.textContent = action === 'save' ? 'Guardando…' : 'Conectando…';
    try {
      const path = action === 'save' ? '/api/save-peer' : p.isSaved ? '/api/connect-saved-peer' : p.source === 'live' ? '/api/peer/connect' : '/api/connect-discovered-peer';
      const body = action === 'save' ? { peerId: p.id, save: true, autoConnect: true } : p.isSaved ? { certHash:p.certHash } : p.source === 'live' ? { peerId: p.id } : { certHash: p.certHash, token: p.token };
      await QP.api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      QP.toast(action === 'save' ? 'Contacto guardado.' : 'Conexión solicitada. El estado se actualizará aquí.');
      await QP.getStatus(true);
    } catch (error) { QP.toast(error.message, 'error'); }
    finally { pending.delete(key); contactSignature = ''; renderContacts(); }
  }

  function reviewContact(p) {
    const dialog = QP.el('dialog', 'contact-review');
    dialog.setAttribute('aria-labelledby', 'reviewContactTitle');
    const heading = QP.el('h2', '', `Revisar a ${p.name || 'este contacto'}`); heading.id = 'reviewContactTitle';
    dialog.append(heading, QP.el('p', '', 'Compara esta identidad con la que te indique la otra persona. Al confiar en ella, podrá enviarte mensajes. Las llamadas, la pantalla y la red privada mantienen sus propios permisos.'));
    QP.identity.appendDetails(dialog, p);
    const actions = QP.el('div', 'dialog-actions');
    const cancel = QP.button('Ahora no', '', () => dialog.close());
    const decide = async trust => {
      accept.disabled = reject.disabled = cancel.disabled = true;
      try {
        await QP.api('/api/peer/trust', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ peerId: p.id, trust }) });
        dialog.close();
        QP.toast(trust ? 'Contacto de confianza. Ya puedes enviarle un mensaje.' : 'Revisión guardada. El contacto permanece en Sin confianza.');
        await QP.getStatus(true);
      } catch (error) { QP.toast(error.message, 'error'); accept.disabled = reject.disabled = !p.certHash; cancel.disabled = false; }
    };
    const reject = QP.button('No confiar', '', () => decide(false));
    const accept = QP.button('Confiar en este contacto', 'primary', () => decide(true));
    accept.disabled = reject.disabled = !p.certHash;
    dialog.append(QP.el('p', 'muted', 'Si eliges «No confiar», la identidad seguirá sin verificar y se marcará como revisada. «Ahora no» conserva el aviso pendiente.'));
    actions.append(cancel, reject, accept); dialog.append(actions); document.body.append(dialog);
    const previous = document.activeElement;
    dialog.addEventListener('close', () => { dialog.remove(); if (previous?.isConnected) previous.focus(); }, { once: true });
    dialog.showModal(); cancel.focus();
  }

  function availableInvitation(snapshot) {
    const active = service => snapshot?.[service]?.isStarted !== false && snapshot?.[service]?.desiredEnabled !== false;
    return (active('wan') && snapshot?.node?.wanToken) || (active('tor') && snapshot?.node?.torToken) || '';
  }

  function renderHome(next) {
    status = next;
    reviewedHashes = new Set(next.preferences?.ReviewedUntrustedPeers || []);
    const all = contacts(next);
    clearTimeout(availabilityTimer);
    const now = Date.now();
    const expiries = all.filter(p => !p.isQuicConnected).map(p => Date.parse(p.discoveryAvailableUntilUtc)).filter(time => time > now);
    if (expiries.length) availabilityTimer = setTimeout(() => renderHome(status), Math.min(...expiries) - now + 1);
    const connected = all.filter(p => p.isQuicConnected).length;
    const saved = all.filter(p => p.isSaved).length;
    const ready = !!availableInvitation(next);
    QP.setText('homeAllFilterCount', all.length);
    const channels = [next.discovery?.wan || {...next.discovery, serviceRunning:next.wan?.isStarted ?? next.wan?.running}, next.discovery?.tor].filter(c => c?.enabled && c.serviceRunning !== false);
    const relays = channels.reduce((sum,c) => sum + (c.connectedRelays || 0), 0);
    const confirmed = channels.some(c => c.lastAcknowledgedUtc && Date.now() - Date.parse(c.lastAcknowledgedUtc) < 120000);
    QP.setText('homeDiscoveryStatus', !channels.length ? 'Descubrimiento automático desactivado · puedes conectar por código' : relays > 0 ? `Descubrimiento: ${relays} ${relays === 1 ? 'servidor conectado' : 'servidores conectados'} · ${confirmed ? 'anuncio confirmado' : 'esperando confirmación del anuncio'}` : 'Sin servidores de descubrimiento · reintentando');
    QP.setText('homeConnectedFilterCount', `Conectados (${connected})`);
    QP.setText('homeConnectingFilterCount', `Conectando (${all.filter(p => !p.isQuicConnected && p.connectionState === 'connecting').length})`);
    QP.setText('homeAvailableFilterCount', `Disponibles (${all.filter(p => contactState(p) === 'available').length})`);
    QP.setText('homeSavedFilterCount', saved);
    QP.setText('homeContactCount', all.length);
    QP.setText('homeReviewCount', all.filter(p => p.source !== 'pending' && !p.isTrusted).length);
    const unseen = all.filter(needsReview).length;
    const reviewFilter = document.querySelector('[data-contact-filter="review"]');
    reviewFilter.classList.toggle('needs-attention', unseen > 0);
    reviewFilter.setAttribute('aria-label', `Sin confianza: ${all.filter(p => p.source !== 'pending' && !p.isTrusted).length} contactos, ${unseen} pendientes de revisión`);
    QP.setText('homeReviewPendingCount', `${unseen} ${unseen === 1 ? 'nuevo' : 'nuevos'}`);
    $('homeReviewPendingCount').classList.toggle('hidden', unseen === 0);
    $('untrustedAlert').classList.toggle('hidden', unseen === 0);
    QP.setText('untrustedAlertTitle', unseen === 1 ? 'Hay 1 contacto nuevo sin revisar' : `Hay ${unseen} contactos nuevos sin revisar`);
    QP.setText('untrustedNavCount', unseen);
    $('untrustedNavCount').classList.toggle('hidden', unseen === 0);
    QP.setText('sidebarNodeName', next.node?.name || 'Este dispositivo');
    if (QP.paintAvatar) QP.paintAvatar($('sidebarAvatar'), next.node, next);
    else QP.setText('sidebarAvatar', QP.initials(next.node?.name || 'QuicNet'));
    $('shareInvitationBtn').disabled = !ready;
    QP.setText('invitationHint', ready ? 'La otra persona necesitará QuicNet para usarla.' : 'La invitación aparecerá cuando la red esté lista.');
    renderContacts();
  }

  function connectionChanged(connected) {
    $('connectionNotice').classList.toggle('hidden', connected);
  }

  // Retrofitted focus lifecycle for protocol dialogs (which retain their existing close handlers).
  function setupDialogs() {
    let current = null, previous = null, inerted = [];
    const visible = el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
    const focusable = el => [...el.querySelectorAll('button, a[href], input, select, textarea, [tabindex="0"]')].filter(x => !x.disabled && visible(x));
    const sync = () => {
      const overlay = [...document.querySelectorAll('.modal-backdrop:not(.hidden), .chat-lightbox:not(.hidden)')].filter(el => el.tagName !== 'DIALOG' && visible(el)).at(-1) || null;
      if (overlay === current) return;
      inerted.forEach(el => el.inert = false); inerted = [];
      if (current && previous?.isConnected) previous.focus({ preventScroll: true });
      current = overlay;
      if (!current) return;
      previous = document.activeElement;
      const card = current.matches('[role="dialog"]') ? current : current.querySelector('.modal-card, .lightbox-dialog') || current;
      card.setAttribute('role', 'dialog'); card.setAttribute('aria-modal', 'true');
      if (!card.hasAttribute('aria-labelledby') && !card.hasAttribute('aria-label')) card.setAttribute('aria-label', card.querySelector('h2')?.textContent || 'Vista previa');
      let branch = current;
      while (branch.parentElement) {
        [...branch.parentElement.children].filter(el => el !== branch && !el.inert && !['SCRIPT','STYLE','LINK'].includes(el.tagName) && !el.classList.contains('toast-host')).forEach(el => { el.inert = true; inerted.push(el); });
        branch = branch.parentElement;
        if (branch === document.body) break;
      }
      const preferred = card.querySelector('[data-dialog-autofocus]');
      card.tabIndex = -1; (preferred && !preferred.disabled && visible(preferred) ? preferred : focusable(card)[0] || card).focus();
    };
    let queued = false;
    new MutationObserver(mutations => {
      if (queued || !mutations.some(m => m.type === 'childList' || m.target.matches?.('.modal-backdrop, .chat-lightbox'))) return;
      queued = true; queueMicrotask(() => { queued = false; sync(); });
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
    document.addEventListener('keydown', e => {
      if (!current || document.querySelector('dialog[open]')) return;
      if (e.key === 'Escape') {
        const close = current.querySelector('[id^="close"], [id$="CloseBtn"], [id^="cancel"], [id$="Cancel"], [id$="CancelBtn"], #lightboxCloseBtn');
        if (close) { e.preventDefault(); close.click(); }
      }
      if (e.key === 'Tab') {
        const list = focusable(current); const first = list[0], last = list.at(-1);
        if (!first) { e.preventDefault(); return; }
        if (e.shiftKey && (document.activeElement === first || !list.includes(document.activeElement))) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && (document.activeElement === last || !list.includes(document.activeElement))) { e.preventDefault(); first.focus(); }
      }
    });
  }

  document.addEventListener('DOMContentLoaded', async () => {
    const addDialog = $('addContactDialog');
    QP.openContactInvitation = openContactInvitation;
    $('openShareInvitation').addEventListener('click', event => openContactInvitation(event.currentTarget, { mode: 'share' }));
    $('closeAddContact').addEventListener('click', () => addDialog.close());
    $('returnFromInvitation').addEventListener('click', () => addDialog.close());
    addDialog.addEventListener('close', () => {
      const origin = invitationOrigin;
      invitationOrigin = null;
      if (!origin?.opener?.isConnected) return;
      if (origin.restore) origin.restore(origin.accepted);
      else origin.opener.focus({ preventScroll: true });
    });
    QP.on('contact_invitation_accepted', origin => {
      if (!origin || origin !== invitationOrigin || !addDialog.open) return;
      origin.accepted = true;
      $('returnFromInvitation').hidden = !origin.restore;
    });
    QP.contactInvitationOrigin = () => invitationOrigin;
    $('contactSort').value = ['activity','recent','usage','name'].includes(sort) ? sort : (sort = 'activity');
    $('contactConnectionFilter').addEventListener('change', e => { connectionFilter = e.target.value; renderContacts(); });
    $('contactSort').addEventListener('change', e => { sort = e.target.value; try { localStorage.setItem('quicnet.contactSort.v2', sort); } catch {} renderContacts(); });
    document.querySelectorAll('[data-focus-connect]').forEach(b => b.addEventListener('click', event => openContactInvitation(event.currentTarget)));
    document.querySelectorAll('[data-contact-filter]').forEach(b => b.addEventListener('click', () => setFilter(b.dataset.contactFilter)));
    document.querySelectorAll('.nav-link').forEach(a => { a.setAttribute('aria-label', a.querySelector('.nav-label').textContent); a.title = a.getAttribute('aria-label'); });
    document.querySelectorAll('.subtab-btn').forEach(b => b.setAttribute('aria-pressed', String(b.classList.contains('active'))));
    document.querySelectorAll('button[title]').forEach(b => { if (!b.hasAttribute('aria-label')) b.setAttribute('aria-label', b.title); });
    document.querySelectorAll('.field').forEach(field => { const label = field.querySelector('label'); const input = field.querySelector('input[id], textarea[id], select[id]'); if (label && input && !label.htmlFor) label.htmlFor = input.id; });
    $('contactSearch').addEventListener('input', renderContacts);
    if (QP.currentPage === 'chat' && new URLSearchParams(location.search).get('peer')) $('chatContainer').classList.add('in-conversation');
    $('openUntrustedBtn').addEventListener('click', () => { connectionFilter = 'all'; $('contactConnectionFilter').value = 'all'; setFilter('review'); $('homeContacts').scrollIntoView({ block: 'center' }); });
    $('shareInvitationBtn').addEventListener('click', async () => {
      const raw = availableInvitation(status);
      if (!raw) return;
      const invitation = /^qp:\/\//i.test(raw) ? raw : `QP://${raw}`;
      if (await QP.copy(invitation)) QP.setText('invitationHint', 'Invitación copiada. Envíala a la persona que quieras añadir.');
      else { $('invitationManualCopy').hidden = false; $('invitationCopyValue').value = invitation; $('invitationCopyValue').focus(); $('invitationCopyValue').select(); }
    });
    $('retryConnectionBtn').addEventListener('click', async () => {
      const button = $('retryConnectionBtn'); button.disabled = true;
      try { await QP.getStatus(true); QP.setText('connectionNoticeText', 'Estado actualizado. Recuperando las actualizaciones en directo…'); }
      catch { QP.setText('connectionNoticeText', 'No se puede conectar. Comprueba que QuicNet sigue abierto.'); }
      finally { button.disabled = false; }
    });
    QP.on('status_updated', renderHome);
    QP.on('ws_status', data => connectionChanged(data.connected));
    if (QP.state.status) renderHome(QP.state.status);
    if (QP.isWsConnected) connectionChanged(true);
    setTimeout(() => { if (!QP.state.status) { $('homeContacts').setAttribute('aria-busy', 'false'); $('homeContacts').replaceChildren(QP.el('div', 'empty-state', 'No se pudo cargar el estado. Comprueba que QuicNet está abierto y pulsa Reintentar.')); connectionChanged(false); } }, 16000);
    setupDialogs();
  });
})();
