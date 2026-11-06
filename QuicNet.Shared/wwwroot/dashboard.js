let dashboardStatus = null;
let dashboardBusy = false;
const networkServiceChanges = new Map();
const discoveryChanges = new Set();
let savedPeerEditingHash = null;
let logsClearMarker = null;
let logsFollowTail = true;
let logsRendering = false;
let lastRenderedLogs = ['\u0000'];
let currentStunData = { exactMappings: [], publicAddresses: [], ports: [], serverCount: 0 };

const APP_DEFS = [
  { key: 'chat', label: 'Mensajes', href: '/chat.html' },
  { key: 'voice', label: 'Llamadas', href: '/call.html' },
  { key: 'lan', label: 'Red privada', href: '/vpn.html' },
  { key: 'speedtest', label: 'Velocidad', href: '/speedtest.html' },
  { key: 'virtual-disk', label: 'Disco virtual', href: '/virtual-disk.html' },
  { key: 'watch', label: 'QuicWatch', href: '/watch.html' }
];

const NETWORK_TYPES = ['Wan', 'Tor'];

document.addEventListener('DOMContentLoaded', () => {
  byId('copyWanInline').addEventListener('click', () => dashboardStatus && QP.copy(fullToken(dashboardStatus.node?.wanToken)));
  byId('copyTorInline').addEventListener('click', () => dashboardStatus && QP.copy(fullToken(dashboardStatus.node?.torToken)));
  byId('connectTokenBtn').addEventListener('click', connectToken);
  byId('activateTokenTransport').addEventListener('click', async () => {
    const button = byId('activateTokenTransport');
    const mode = button.dataset.mode;
    button.disabled = true;
    try { if (await QP.connectionModes.request(mode)) { button.hidden = true; await connectToken(); } }
    finally { button.disabled = false; }
  });
  byId('tokenInput').addEventListener('input', () => { byId('activateTokenTransport').hidden = true; byId('tokenInput').removeAttribute('aria-invalid'); });
  byId('connectAllDiscoveredBtn')?.addEventListener('click', connectAllDiscovered);
  byId('saveAllBtn').addEventListener('click', saveAllPeers);
  byId('discoveryToggle').addEventListener('change', e => setDiscovery(e.target.checked, 'wan'));
  byId('torDiscoveryToggle')?.addEventListener('change', e => setDiscovery(e.target.checked, 'tor'));
  byId('wanToggle')?.addEventListener('change', e => setWan(e.target.checked));
  byId('homeWanToggleBtn')?.addEventListener('click', () => setWan(!(dashboardStatus?.wan?.desiredEnabled ?? dashboardStatus?.wan?.isStarted ?? true)));
  byId('homeTorToggleBtn')?.addEventListener('click', () => setTor(!(dashboardStatus?.tor?.desiredEnabled ?? dashboardStatus?.tor?.isStarted ?? false)));
  byId('torToggle').addEventListener('change', e => setTor(e.target.checked));
  byId('torNewNymBtn').addEventListener('click', newTorCircuits);
  byId('applyPortBtn').addEventListener('click', applyListenerPort);
  byId('tokenInput').addEventListener('keydown', e => { if (e.key === 'Enter') connectToken(); });
  byId('pasteClipboardBtn')?.addEventListener('click', () => detectClipboardToken(true));
  byId('clearLogsBtn').addEventListener('click', clearLogsView);
  byId('openUntrustedBtn')?.addEventListener('click', () => {
    document.querySelector('[data-contact-filter="review"]')?.click();
  });
  byId('logs')?.addEventListener('scroll', () => {
    if (logsRendering) return;
    const box = byId('logs');
    logsFollowTail = box.scrollHeight - box.scrollTop - box.clientHeight < 48;
  });
  document.querySelector('[data-group="diagtabs"][data-subtab="tab-activity-logs"]')?.addEventListener('click', () => {
    if (logsFollowTail) requestAnimationFrame(() => { const box = byId('logs'); box.scrollTop = box.scrollHeight; });
  });
  byId('clearOfflineSavedBtn')?.addEventListener('click', clearOfflineSavedPeers);
  byId('addSavedPeerBtn').addEventListener('click', () => openSavedPeerEditor());
  byId('closeSavedPeerModalBtn').addEventListener('click', closeSavedPeerEditor);
  byId('cancelSavedPeerBtn').addEventListener('click', closeSavedPeerEditor);
  byId('saveSavedPeerBtn').addEventListener('click', saveSavedPeerEditor);
  byId('savedPeerModal').addEventListener('click', e => { if (e.target === byId('savedPeerModal')) closeSavedPeerEditor(); });
  byId('openStunModalBtn')?.addEventListener('click', openStunModal);
  byId('stunMappings')?.addEventListener('click', () => { if (currentStunData.exactMappings?.length) openStunModal(); });
  byId('closeStunModalBtn')?.addEventListener('click', closeStunModal);
  byId('closeStunModalFootBtn')?.addEventListener('click', closeStunModal);
  byId('copyAllStunBtn')?.addEventListener('click', copyAllStunMappings);
  byId('stunModal')?.addEventListener('click', e => { if (e.target === byId('stunModal')) closeStunModal(); });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      if (!byId('savedPeerModal').classList.contains('hidden')) closeSavedPeerEditor();
      if (!byId('stunModal').classList.contains('hidden')) closeStunModal();
    }
  });

  if (window.QP && typeof QP.on === 'function') {
    QP.on('status_updated', status => {
      dashboardStatus = status;
      renderDashboard(dashboardStatus);
    });
    QP.on('log_event', event => {
      if (!event?.entry || !dashboardStatus) return;
      if (dashboardStatus.logs?.at(-1) === event.entry) return;
      dashboardStatus.logs = [...(dashboardStatus.logs || []), event.entry].slice(-80);
      renderLogs(dashboardStatus.logs);
    });
    QP.on('view_changed', data => {
      if (data && data.page === 'dashboard') {
        if (QP.state && QP.state.status) {
          dashboardStatus = QP.state.status;
          renderDashboard(dashboardStatus);
        } else {
          refreshDashboard();
        }
      }
    });
  }

  if (window.QP && QP.state && QP.state.status) {
    dashboardStatus = QP.state.status;
    renderDashboard(dashboardStatus);
  } else {
    refreshDashboard();
  }

});

function byId(id) { return document.getElementById(id); }
function fullToken(raw) { const value = String(raw || '').trim(); return !value ? '' : (value.toUpperCase().startsWith('QP://') ? value : `QP://${value}`); }
function td(text, className = '') { const node = document.createElement('td'); if (className) node.className = className; node.textContent = text ?? ''; return node; }
function emptyRow(body, cols, text) { const tr = document.createElement('tr'); const cell = td(text, 'empty-cell'); cell.colSpan = cols; tr.appendChild(cell); body.appendChild(tr); }
function formatPortRange(minOrItem, maxPortArg, portsArg, portModeArg) {
  let ports = null, portMode = null;
  if (minOrItem && typeof minOrItem === 'object') {
    if (Array.isArray(minOrItem)) {
      ports = minOrItem;
      portMode = maxPortArg || null;
    } else {
      ports = minOrItem.ports || minOrItem.portArray || null;
      portMode = minOrItem.portMode || null;
    }
  } else if (Array.isArray(portsArg)) {
    ports = portsArg;
    portMode = portModeArg || null;
  } else if (minOrItem !== undefined && minOrItem !== null && String(minOrItem).trim() !== '' && String(minOrItem) !== '0') {
    if (maxPortArg && maxPortArg !== minOrItem && String(maxPortArg) !== '0') {
      return `${minOrItem} – ${maxPortArg}`;
    }
    return String(minOrItem);
  }

  if (Array.isArray(ports) && ports.length > 0) {
    const validPorts = ports.map(p => Number(p)).filter(p => !isNaN(p) && p > 0);
    if (validPorts.length === 1) {
      return String(validPorts[0]);
    }
    if (validPorts.length > 0) {
      const sorted = [...new Set(validPorts)].sort((a, b) => a - b);
      if (sorted.length === 1) return String(sorted[0]);

      const minP = sorted[0];
      const maxP = sorted[sorted.length - 1];

      let isContiguous = true;
      for (let i = 1; i < sorted.length; i++) {
        if (sorted[i] !== sorted[i - 1] + 1) {
          isContiguous = false;
          break;
        }
      }

      // Si es un rango de verdad (consecutivos), o son más de 8 puertos, o modo Range:
      if (isContiguous || sorted.length > 8 || portMode === 'Range') {
        return `${minP} – ${maxP}`;
      }

      // Si son múltiples puertos discretos (<= 8 y no contiguos), enumerar separados por coma
      return sorted.join(', ');
    }
  }

  return '—';
}
function formatBytes(bytes) {
  if (bytes === null || bytes === undefined || isNaN(bytes)) return '0 B';
  const num = Number(bytes);
  if (num === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(Math.abs(num)) / Math.log(k));
  const val = (num / Math.pow(k, i)).toFixed(i === 0 ? 0 : 2);
  return `${val} ${sizes[i] || 'B'}`;
}
function isConnectedPeer(peer) { return !!peer && (peer.hasCipher || !!peer.endpoint || Number(peer.lastSeenSecondsAgo || 99999) < 90); }

async function refreshDashboard() {
  if (dashboardBusy) return;
  dashboardBusy = true;
  try {
    dashboardStatus = await (QP.getStatus ? QP.getStatus(true) : QP.api('/api/status'));
    renderDashboard(dashboardStatus);
  } catch (e) { QP.toast(e.message); }
  finally { dashboardBusy = false; }
}

function renderDashboard(s) {
  const inMode = peer => QP.peerInConnectionMode(peer, s);
  const peers = (s.peers || []).filter(inMode);
  const isResponsive = p => p.isQuicConnected || (Number(p.unresponsiveSeconds ?? p.lastSeenSecondsAgo ?? 99999) < 60);
  const connected = peers.filter(p => p.isQuicConnected);
  const available = peers.filter(p => p.isTrusted && !p.isQuicConnected && isResponsive(p));
  const untrusted = peers.filter(p => !p.isTrusted);

  QP.setText('nodeName', s.node?.name || 'Este dispositivo');
  QP.setText('nodeNetwork', s.node?.networkType || 'Unknown');
  if (document.activeElement !== byId('listenerPortInput')) byId('listenerPortInput').value = String(s.node?.listenerPort || '');
  const wanMetaEl = byId('wanMeta');
  if (wanMetaEl) {
    wanMetaEl.textContent = wanStatusText(s);
    if (s.wan?.lastError) {
      wanMetaEl.title = s.wan.lastError;
      wanMetaEl.style.cursor = 'pointer';
      wanMetaEl.onclick = () => byId('logs')?.scrollIntoView({ behavior: 'smooth' });
    } else {
      wanMetaEl.removeAttribute('title');
      wanMetaEl.style.cursor = 'default';
      wanMetaEl.onclick = null;
    }
  }
  QP.setText('discoveryMeta', discoveryStatusText(s.discovery?.wan || s.discovery, s.wan));
  QP.setText('torDiscoveryMeta', discoveryStatusText(s.discovery?.tor, s.tor));
  const torMetaEl = byId('torMeta');
  if (torMetaEl) {
    torMetaEl.textContent = torStatusText(s);
    if (s.tor?.lastError) {
      torMetaEl.title = s.tor.lastError;
      torMetaEl.style.cursor = 'pointer';
      torMetaEl.onclick = () => byId('logs')?.scrollIntoView({ behavior: 'smooth' });
    } else {
      torMetaEl.removeAttribute('title');
      torMetaEl.style.cursor = 'default';
      torMetaEl.onclick = null;
    }
  }
  QP.setText('wanTokenBox', fullToken(s.node?.wanToken) || (s.wan?.isStarted === false ? 'Servicio WAN inactivo' : 'Preparando invitación WAN…'));
  QP.setText('torTokenBox', fullToken(s.node?.torToken) || (s.tor?.isStarted === false ? 'Servicio Tor inactivo' : 'Preparando invitación Tor…'));
  byId('copyWanInline').disabled = !s.node?.wanToken || s.wan?.isStarted === false;
  byId('copyTorInline').disabled = !s.node?.torToken || s.tor?.isStarted === false;
  byId('torNewNymBtn').disabled = !s.tor?.isStarted;
  renderHomeServiceControls(s);
  byId('discoveryToggle').checked = !!(s.discovery?.wan?.desiredEnabled ?? s.discovery?.desiredEnabled);
  byId('discoveryToggle').disabled = discoveryChanges.has('wan') || (s.wan?.isStarted === false && !s.wan?.desiredEnabled);
  if (byId('torDiscoveryToggle')) {
    byId('torDiscoveryToggle').checked = !!s.discovery?.tor?.desiredEnabled;
    byId('torDiscoveryToggle').disabled = discoveryChanges.has('tor') || (s.tor?.isStarted === false && !s.tor?.desiredEnabled);
  }

  renderStun(s.stun || {}, s.node || {});

  renderDiscovered((s.discoveredPeers || []).filter(inMode), s);
  renderInterrogations((s.activeInterrogations || []).filter(inMode));
  renderConnected(connected, s);
  renderAvailable(available);
  renderUntrusted(untrusted);
  renderUntrustedAttention(untrusted);
  renderSaved((s.savedPeers || []).filter(inMode), peers);
  renderActiveApps(s);
  renderQuicTelemetry(s);
  renderRequests(s.pendingPetitions || [], s);
  renderLogs(s.logs || []);
}

function cleanInlineError(err, maxLen = 65) {
  if (!err) return '';
  let str = String(err).trim();
  if (str.includes('Last error:')) {
    str = str.substring(str.lastIndexOf('Last error:') + 'Last error:'.length).trim();
  }
  const nl = str.indexOf('\n');
  if (nl > 0) str = str.substring(0, nl).trim();
  return str.length > maxLen ? str.substring(0, maxLen) + '…' : str;
}

function wanStatusText(s) {
  if (s.wan?.isStarted) return `Activo · puerto ${s.node?.listenerPort || s.wan?.port || '—'}`;
  if (s.wan?.lastError) return `Failed · ${cleanInlineError(s.wan.lastError)}`;
  if (s.wan?.desiredEnabled) return 'Iniciando…';
  return 'Detenido';
}

function discoveryStatusText(d, parentService) {
  if (d?.running) return `Activo · ${d.connectedRelays}/${d.relayCount} relays`;
  
  // If parent transport (WAN or Tor) is explicitly stopped/disabled, discovery cannot run
  const isParentInactive = parentService && (!parentService.isStarted && !parentService.desiredEnabled);
  const isServiceDetenido = d?.serviceRunning === false && d?.serviceDesiredEnabled === false;
  if (isParentInactive || isServiceDetenido) return 'Detenido (servicio inactivo)';

  if (d?.desiredEnabled) {
    if (parentService && !parentService.isStarted && parentService.desiredEnabled) {
      return 'Waiting for service…';
    }
    return 'Iniciando…';
  }
  return 'Detenido';
}

function torStatusText(s) {
  if (s.tor?.isStarted) return `Activo · SOCKS ${s.tor.socksPort || '—'} · virtual ${s.tor.virtualPort || '—'}`;
  if (s.tor?.lastError) return `Failed · ${cleanInlineError(s.tor.lastError)}`;
  if (s.tor?.desiredEnabled) {
    const status = s.tor.bootstrapStatus || '';
    const isGeneric = !status || status === 'Bootstrapping...' || status === 'Not initialized';
    if (s.tor.bootstrapProgress > 0) {
      if (!isGeneric) {
        if (status.startsWith('Bootstrapped ')) {
          return `Iniciando · ${status}`;
        }
        return `Iniciando · ${s.tor.bootstrapProgress}% · ${status}`;
      }
      return `Iniciando · ${s.tor.bootstrapProgress}%`;
    }
    if (!isGeneric) {
      return `Iniciando · ${status}`;
    }
    return 'Iniciando…';
  }
  return 'Detenido';
}

function renderStun(stun, node) {
  const publicAddresses = (stun.publicAddresses || []).filter(Boolean);
  const exact = stun.exactMappings || [];
  currentStunData = {
    exactMappings: exact,
    publicAddresses: publicAddresses,
    ports: stun.ports || node.ports || [],
    portMode: stun.portMode || node.portMode || null,
    serverCount: stun.serverCount ?? 0
  };

  QP.setText('stunAddresses', publicAddresses.length ? publicAddresses.join(', ') : 'No exact public address observed yet');
  QP.setText('stunPorts', stun.portDisplay || formatPortRange(currentStunData));

  const stunTextEl = byId('stunMappings');
  const stunDotsBtn = byId('openStunModalBtn');

  if (!exact.length) {
    if (stunTextEl) {
      stunTextEl.textContent = 'No exact public mapping observed yet';
      stunTextEl.style.cursor = 'default';
    }
    if (stunDotsBtn) stunDotsBtn.classList.add('hidden');
  } else {
    const formatted = exact.map(item => {
      if (typeof item === 'string') return item;
      const ep = item.endpoint || `${item.address}:${item.port}`;
      return item.hits && item.hits > 1 ? `${ep} (${item.hits} hits)` : ep;
    });
    if (stunTextEl) {
      stunTextEl.textContent = formatted.join(', ');
      stunTextEl.style.cursor = 'pointer';
    }
    const totalHits = exact.reduce((sum, item) => sum + (typeof item === 'object' && item.hits ? Number(item.hits) : 1), 0);
    if (stunDotsBtn) {
      stunDotsBtn.classList.remove('hidden');
      stunDotsBtn.title = `View all ${exact.length} STUN endpoint${exact.length === 1 ? '' : 's'} (${totalHits} total server hit${totalHits === 1 ? '' : 's'})`;
    }
  }

  QP.setText('localAddresses', (stun.localAddresses || []).join(', ') || '—');
  QP.setText('stunServerCount', String(stun.serverCount ?? 0));

  if (!byId('stunModal')?.classList.contains('hidden')) {
    renderStunModalList();
  }
}

function renderDiscovered(items, s) {
  const body = byId('discoveredPeersBody');
  if (!body) return;
  QP.clear(body);
  if (!items.length) {
    const isDiscoveryOn = !!s.discovery?.desiredEnabled || !!s.discovery?.running;
    emptyRow(body, 6, isDiscoveryOn ? 'Todavía no se han descubierto contactos. Aparecerán aquí cuando estén disponibles.' : 'El descubrimiento está detenido. Puedes activarlo en los ajustes de conexión.');
    return;
  }

  items.forEach(item => {
    const tr = document.createElement('tr');

    const pcell = document.createElement('td');
    const name = QP.el('div', 'peer-primary', item.name || 'Discovered Peer');
    const tags = QP.el('div', 'peer-inline-tags');
    tags.appendChild(QP.el('span', 'badge blue', item.source || 'Nostr'));
    if (item.isTor) tags.appendChild(QP.el('span', 'badge purple', 'Tor'));
    else tags.appendChild(QP.el('span', 'badge green', 'WAN'));
    if (item.isCertVerified) {
      const verifiedBadge = QP.el('span', 'badge green', 'Identidad verificada');
      verifiedBadge.title = 'Cryptographically verified: The Nostr announcement carries the certificate public key, matching the token hash and validating the token signature.';
      tags.appendChild(verifiedBadge);
    }
    if (item.isSaved) tags.appendChild(QP.el('span', 'badge green', 'Contacto guardado'));
    if (item.isSaved && item.endpointChanged) tags.appendChild(QP.el('span', 'badge yellow', 'Nueva dirección'));
    
    pcell.append(name);
    if (item.isSaved && item.discoveredName && item.discoveredName !== item.name) {
      pcell.append(QP.el('div', 'peer-sub', `Discovered as "${item.discoveredName}"`));
    }
    pcell.append(QP.el('div', 'peer-sub mono', item.canonicalId || QP.canonicalId(item.certHash || item.id)), tags);
    tr.appendChild(pcell);

    const typeCell = document.createElement('td');
    typeCell.appendChild(QP.el('div', 'mono', item.networkType || 'Wan'));
    typeCell.appendChild(QP.el('div', 'peer-sub', item.source || 'Nostr'));
    tr.appendChild(typeCell);

    const addrCell = document.createElement('td');
    addrCell.className = 'mono wrap';
    if (item.isTor && item.onionAddress) {
      addrCell.appendChild(QP.el('div', 'mono', item.onionAddress));
    } else if ((item.addresses || []).length) {
      addrCell.appendChild(QP.el('div', 'mono wrap', item.addresses.join(', ')));
    } else {
      addrCell.appendChild(QP.el('div', 'muted', 'Token endpoint'));
    }
    tr.appendChild(addrCell);

    tr.appendChild(td(formatPortRange(item), 'mono'));

    const timeCell = document.createElement('td');
    const secAgo = Number(item.lastSeenSecondsAgo || 0);
    const timeStr = secAgo < 60 ? `${secAgo}s ago` : `${Math.floor(secAgo / 60)}m ago`;
    timeCell.appendChild(QP.el('div', '', timeStr));
    timeCell.appendChild(QP.el('div', 'peer-sub', item.discoveredAt || ''));
    tr.appendChild(timeCell);

    const actionsCell = document.createElement('td');
    const actions = QP.el('div', 'mini-actions');
    actions.append(
      QP.button('Conectar', 'accent', () => connectDiscoveredPeer(item))
    );
    actions.append(
      QP.button('Descartar', 'ghost', () => dismissDiscoveredPeer(item.certHash))
    );
    if (item.token) {
      actions.appendChild(QP.button('Copiar invitación', 'ghost', () => { QP.copy(fullToken(item.token)); QP.toast('Invitación copiada'); }));
    }
    actionsCell.appendChild(actions);
    tr.appendChild(actionsCell);

    body.appendChild(tr);
  });
}

async function connectDiscoveredPeer(item) {
  try {
    await QP.api('/api/connect-discovered-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: item.token, certHash: item.certHash })
    });
    QP.toast(`Connecting to ${item.name || 'discovered peer'}…`);
  } catch (e) {
    QP.toast(e.message);
  }
  refreshDashboard();
}

async function dismissDiscoveredPeer(certHash) {
  try {
    await QP.api('/api/dismiss-discovered-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ certHash })
    });
    QP.toast('Contacto descubierto descartado');
  } catch (e) {
    QP.toast(e.message);
  }
  refreshDashboard();
}

async function connectAllDiscovered() {
  try {
    const res = await QP.api('/api/connect-all-discovered', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}'
    });
    QP.toast(`Connecting to ${res.count || 0} discovered peer(s)…`);
  } catch (e) {
    QP.toast(e.message);
  }
  refreshDashboard();
}

function renderInterrogations(items) {
  const body = byId('interrogationsBody'); QP.clear(body);
  if (!items.length) { emptyRow(body, 5, 'No hay conexiones en curso.'); return; }
  items.forEach(item => {
    const tr = document.createElement('tr');
    const peer = td(item.peerName || 'Unknown');
    peer.appendChild(QP.el('div', 'peer-sub mono', item.canonicalId || QP.canonicalId(item.certHash || item.peerId || item.id)));
    tr.append(peer, td((item.addresses || []).join(', ') || 'Unknown', 'mono wrap'), td(formatPortRange(item), 'mono'), td(item.startTime || '—'));
    const actions = document.createElement('td'); actions.appendChild(QP.button('Cancelar', 'danger', () => cancelInterrogation(item.sessionId || item.id))); tr.appendChild(actions);
    body.appendChild(tr);
  });
}

function renderConnected(peers, s) {
  const saveAllBtn = byId('saveAllBtn');
  if (saveAllBtn) {
    const unsavedCount = (peers || []).filter(p => !p.isSaved).length;
    saveAllBtn.disabled = unsavedCount === 0;
    saveAllBtn.title = unsavedCount > 0 ? `Guardar ${unsavedCount} peer(s) no guardado(s)` : 'Todos los peers conectados ya están guardados';
  }

  const body = byId('connectedPeers'); QP.clear(body);
  if (!peers.length) { emptyRow(body, 6, 'No hay conexiones QUIC establecidas.'); return; }
  peers.forEach(peer => {
    const tr = document.createElement('tr');
    const pcell = document.createElement('td');
    const name = QP.el('div', 'peer-primary', peer.name || 'Peer');
    const tags = QP.el('div', 'peer-inline-tags');
    if (peer.isQuicConnected) tags.appendChild(QP.el('span', 'badge purple', 'QUIC'));
    if (peer.isSaved) tags.appendChild(QP.el('span', 'badge green', 'Guardado'));
    if (peer.hasCipher) tags.appendChild(QP.el('span', 'badge green', 'Cifrado'));
    pcell.append(name, QP.el('div', 'peer-sub mono', peer.canonicalId || QP.canonicalId(peer.certHash || peer.id)), tags);
    tr.appendChild(pcell);

    const transport = document.createElement('td');
    const transportName = peer.isTor || peer.activeTransport === 'tor' ? 'Tor' : (peer.activeTransport === 'wan' ? 'WAN' : (peer.networkType || 'WAN'));
    transport.append(QP.el('div', '', transportName), QP.el('div', 'peer-sub mono', peer.quicEndpoint || peer.activeEndPoint || QP.peerMeta(peer)));
    if (peer.telemetry?.congestionAlgorithm) {
      const isBbr = String(peer.telemetry.congestionAlgorithm).toUpperCase() === 'BBR';
      transport.appendChild(QP.el('span', `badge ${isBbr ? 'badge-cc-bbr' : 'badge-cc-cubic'} space-top-xs`, peer.telemetry.congestionAlgorithm));
    }
    tr.appendChild(transport);

    const pingCell = document.createElement('td');
    const unresponsive = Number(peer.unresponsiveSeconds ?? peer.lastSeenSecondsAgo ?? 99999);
    const pingVal = peer.telemetry?.rttMs ?? (peer.ping !== null && peer.ping !== undefined ? Number(peer.ping) : null);
    if (peer.isQuicConnected) {
      const pingBadge = QP.el('span', 'badge green', pingVal !== null && !isNaN(pingVal) ? `${pingVal} ms` : '—');
      pingBadge.title = 'Native QUIC smoothed RTT (MsQuic statistics)';
      pingCell.appendChild(pingBadge);
      const sub = QP.el('div', 'peer-sub text-muted', 'QUIC RTT');
      if (peer.telemetry?.packetLossPercentage !== undefined && peer.telemetry.packetLossPercentage > 0) {
        sub.textContent = `QUIC · Loss: ${peer.telemetry.packetLossPercentage}%`;
        sub.className = 'peer-sub text-warn';
      }
      pingCell.appendChild(sub);
    } else if (unresponsive > 10) {
      pingCell.appendChild(QP.el('span', 'badge red', `Sin respuesta (${QP.formatDuration(unresponsive)})`));
    } else if (pingVal !== null && !isNaN(pingVal)) {
      pingCell.appendChild(QP.el('span', 'badge green', `${pingVal} ms`));
      if (peer.telemetry?.packetLossPercentage !== undefined && peer.telemetry.packetLossPercentage > 0) {
        pingCell.appendChild(QP.el('div', 'peer-sub text-warn', `Loss: ${peer.telemetry.packetLossPercentage}%`));
      }
    } else if (unresponsive <= 10) {
      pingCell.appendChild(QP.el('span', 'badge green', '< 1 ms'));
    } else {
      pingCell.textContent = '—';
    }
    tr.appendChild(pingCell);

    const sessions = sessionLabels(peer.id, s);
    const sessionCell = document.createElement('td');
    if (sessions.length) sessions.forEach(label => sessionCell.appendChild(QP.el('span', 'badge blue session-badge', label)));
    else sessionCell.appendChild(QP.el('span', 'muted', peer.isQuicConnected ? 'Conexión lista' : 'Sin sesiones de aplicación'));
    tr.appendChild(sessionCell);

    const trustCell = document.createElement('td');
    const trustActions = QP.el('div', 'mini-actions');
    if (!peer.isQuicConnected) {
      trustActions.appendChild(QP.button('Conectar', 'accent', () => connectPeerDirect(peer.id)));
    }
    trustActions.append(
      QP.button(peer.isSaved ? 'No guardar' : 'Guardar', peer.isSaved ? '' : 'green', () => savePeer(peer.id, !peer.isSaved))
    );
    if (!peer.isSaved) trustActions.appendChild(QP.button('Retirar confianza', 'ghost', () => trustPeer(peer.id, false)));
    trustCell.appendChild(trustActions); tr.appendChild(trustCell);

    const finalCell = document.createElement('td'); finalCell.appendChild(QP.button('Desconectar', 'ghost', () => disconnectPeer(peer.id))); tr.appendChild(finalCell);
    body.appendChild(tr);
  });
}

function renderAvailable(peers) {
  const body = byId('availablePeers'); QP.clear(body);
  if (!peers.length) { emptyRow(body, 4, 'No hay contactos disponibles pendientes de conexión.'); return; }
  peers.forEach(peer => {
    const tr = document.createElement('tr');
    const name = td(peer.name || 'Peer');
    name.appendChild(QP.el('div', 'peer-sub mono', peer.canonicalId || QP.canonicalId(peer.certHash || peer.id)));
    const connecting = peer.connectionState === 'connecting';
    const actions = document.createElement('td');
    const connect = QP.button(connecting ? 'Conectando…' : 'Conectar QUIC', 'accent', () => connectPeerDirect(peer.id));
    connect.disabled = connecting;
    actions.append(connect, QP.button(peer.isSaved ? 'No guardar' : 'Guardar', '', () => savePeer(peer.id, !peer.isSaved)));
    tr.append(name, td(peer.endpoint || QP.peerMeta(peer), 'mono wrap'), td(connecting ? 'Negociando QUIC' : 'Disponible por señalización · QUIC sin conectar'), actions);
    body.appendChild(tr);
  });
}

function normalizeProtocolName(name) {
  const n = String(name || '').toLowerCase();
  if (n.includes('chat')) return 'chat';
  if (n.includes('voice') || n.includes('call')) return 'voice';
  if (n.includes('lan') || n.includes('vpn')) return 'lan';
  if (n.includes('speed') || n.includes('bench')) return 'speedtest';
  if (n.includes('quicwatch')) return 'watch';
  if (n.includes('disco virtual') || n.includes('virtual disk')) return 'virtual-disk';
  if (n.includes('clip')) return 'clipboard';
  return n.replace(/[^a-z0-9]+/g, '-') || 'protocol';
}

function sessionLabels(peerId, s) {
  const labels = [];
  if ((s.activeChats || []).some(x => x.peerId === peerId)) labels.push('Chat');
  if ((s.activeVoiceCalls || []).some(x => x.peerId === peerId)) labels.push('Voice');
  if ((s.lan?.activePeers || []).some(x => x.peerId === peerId)) labels.push('LAN');
  return labels;
}

function renderUntrusted(peers) {
  const body = byId('untrustedPeers'); QP.clear(body);
  if (!peers.length) { emptyRow(body, 6, 'No hay contactos pendientes de revisión.'); return; }
  peers.forEach(peer => {
    const tr = document.createElement('tr');
    const p = td(peer.name || 'Peer'); p.appendChild(QP.el('div', 'peer-sub mono', peer.canonicalId || QP.canonicalId(peer.certHash || peer.id))); p.appendChild(QP.el('div', 'peer-sub', peer.isQuicConnected ? 'QUIC conectado · protocolos bloqueados' : (peer.connectionState === 'connecting' ? 'Conectando QUIC…' : 'Disponible · conexión automática'))); tr.appendChild(p);
    tr.append(td(peer.isTor ? 'Tor' : (peer.activeTransport || peer.networkType || 'WAN')), td(peer.quicEndpoint || peer.activeEndPoint || QP.peerMeta(peer), 'mono wrap'), td(peer.lastSeenFormatted || `${peer.lastSeenSecondsAgo || 0}s ago`), td(peer.canonicalId || (peer.certHash ? QP.canonicalId(peer.certHash) : '—'), 'mono'));
    const actions = document.createElement('td'); const wrap = QP.el('div', 'mini-actions'); wrap.append(QP.button('Confiar', 'green', () => trustPeer(peer.id, true)), QP.button('Desconectar', 'ghost', () => disconnectPeer(peer.id))); actions.appendChild(wrap); tr.appendChild(actions);
    body.appendChild(tr);
  });
}

function renderUntrustedAttention(peers) {
  const reviewed = new Set(dashboardStatus?.preferences?.ReviewedUntrustedPeers || []);
  const count = peers.filter(peer => !reviewed.has(peer.certHash)).length;
  for (const id of ['untrustedTabCount']) {
    const badge = byId(id);
    if (!badge) continue;
    badge.textContent = String(count);
    badge.classList.toggle('hidden', count === 0);
  }
  document.querySelector('[data-group="peerdir"][data-subtab="tab-untrusted-peers"]')?.classList.toggle('needs-attention', count > 0);
}

function renderSaved(saved, peers) {
  const body = byId('savedPeersBody'); QP.clear(body);
  if (!saved.length) { emptyRow(body, 7, 'Todavía no has guardado contactos.'); return; }
  saved.forEach(item => {
    const live = peers.find(p => p.certHash && p.certHash === item.certHash);
    const tr = document.createElement('tr');
    const nameCell = td(item.name || 'Saved peer'); if (live) nameCell.appendChild(QP.el('div', 'peer-sub', live.hasCipher ? 'Authenticated session' : 'Peer available'));
    const profile = document.createElement('select'); profile.className = 'select';
    profile.setAttribute('aria-label', 'Permisos de este peer sobre mi dispositivo');
    profile.append(new Option('Amigo', 'friend'), new Option('Administrador de este dispositivo', 'administrator'));
    profile.value = item.profile || 'friend';
    profile.addEventListener('change', async () => {
      profile.disabled = true;
      try {
        await QP.api('/api/peer/profile', { method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ certHash: item.certHash, profile: profile.value }) });
        QP.toast(profile.value === 'administrator' ? 'Este peer puede iniciar aplicaciones, controlar mi pantalla y explorar y compartir mis discos sin nuevas peticiones.' : 'Perfil de amigo aplicado');
      } catch (e) { QP.toast(e.message, 'error'); }
      refreshDashboard();
    });
    nameCell.append(profile);
    const permissions = document.createElement('details'); permissions.className = 'peer-permissions';
    const summary = document.createElement('summary'); summary.textContent = 'Permisos automáticos'; permissions.appendChild(summary);
    const permissionOptions = [
      ['chat', 'Chat directo (sin aceptación)'], ['voice', 'Llamadas'], ['vpn', 'VPN / LAN'],
      ['speedtest', 'Prueba de velocidad'], ['watch', 'QuicWatch'], ['virtual-disk', 'Conectar disco virtual (solo carpetas compartidas)'], ['screen-share-request', 'Pedir mi pantalla'], ['screen-settings', 'Cambiar ajustes de mi transmisión']
    ];
    permissionOptions.forEach(([key, label]) => {
      if (key === 'vpn' && QP.capabilities?.capabilities?.vpn?.available === false) return;
      if (key === 'watch' && QP.capabilities?.capabilities?.watch?.available === false) return;
      const control = document.createElement('label'); control.className = 'peer-permission-choice';
      const input = document.createElement('input'); input.type = 'checkbox';
      input.checked = (item.automaticPermissions || []).includes(key);
      if (key === 'chat' || item.profile === 'administrator') { input.checked = true; input.disabled = true; }
      input.addEventListener('change', () => setSavedPeerPermission(item.certHash, key, input.checked));
      control.append(input, document.createTextNode(label)); permissions.appendChild(control);
    });
    nameCell.appendChild(permissions); tr.appendChild(nameCell);
    const certCell = td(item.canonicalId || QP.canonicalId(item.certHash), 'mono'); certCell.appendChild(QP.el('div', 'peer-sub', item.networkType || 'Wan')); tr.appendChild(certCell);
    tr.appendChild(td(item.onionAddress || (item.addresses || []).join(', ') || '—', 'mono wrap'));
    tr.appendChild(td(formatPortRange(item), 'mono'));

    const autoCell = document.createElement('td');
    const autoLabel = document.createElement('label'); autoLabel.className = 'check-line compact-check';
    const autoInput = document.createElement('input'); autoInput.type = 'checkbox'; autoInput.checked = !!item.autoConnect; autoInput.addEventListener('change', () => toggleSavedAutoConnect(item.certHash, autoInput.checked));
    autoLabel.append(autoInput, document.createTextNode('Enabled')); autoCell.appendChild(autoLabel); tr.appendChild(autoCell);

    const statusCell = document.createElement('td'); statusCell.appendChild(QP.el('span', `badge ${live ? 'green' : ''}`, live ? 'Connected' : 'Stored offline')); tr.appendChild(statusCell);

    const actionsCell = document.createElement('td');
    const actions = QP.el('div', 'mini-actions');
    actions.append(
      QP.button('Reconectar', 'accent', () => reconnectSavedPeer(item.certHash)),
      QP.button('Editar', '', () => openSavedPeerEditor(item)),
      QP.button('Eliminar', 'danger', () => deleteSavedPeer(item))
    );
    actionsCell.appendChild(actions); tr.appendChild(actionsCell);
    body.appendChild(tr);
  });
}

function renderActiveApps(s) {
  const body = byId('activeAppsBody'); QP.clear(body);
  const rows = [];
  (s.activeChats || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'Mensajes', detail:'Reliable chat session', href:`/chat.html?peer=${encodeURIComponent(x.peerId)}` }));
  (s.activeVoiceCalls || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'Llamadas', detail:'Live voice call', href:'/call.html' }));
  (s.lan?.activePeers || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'Red privada', detail:`Virtual IP ${x.virtualIp || '—'}`, href:'/vpn.html' }));
  if (!rows.length) { emptyRow(body, 4, 'No hay sesiones de aplicaciones activas.'); return; }
  rows.forEach(row => {
    const tr = document.createElement('tr');
    const sessionTelem = (s.quicTelemetry || []).find(qt => qt.peerId === row.peerId && qt.protocolName?.toLowerCase()?.includes(row.app.toLowerCase()));
    let detailText = row.detail;
    if (sessionTelem?.telemetry) {
      detailText += ` · RTT ${sessionTelem.telemetry.rttMs} ms · CWND ${formatBytes(sessionTelem.telemetry.sendCongestionWindow)}`;
    }
    tr.append(td(row.peerName || row.canonicalId || QP.canonicalId(row.peerId)), td(row.app), td(detailText));
    const action = document.createElement('td');
    const a = document.createElement('a');
    a.className = 'btn sm accent';
    a.href = row.href;
    a.textContent = 'Open';
    action.appendChild(a);
    tr.appendChild(action);
    body.appendChild(tr);
  });
}

function renderRequests(items, snapshot) {
  const panel = byId('requestsPanel'); const list = byId('requestsList'); QP.clear(list); panel.classList.toggle('hidden', !items.length);
  items.forEach(item => {
    const row = QP.el('div', 'petition-row');
    const peer = QP.identity.findPeer(item.peerId, snapshot) || {id: item.peerId, name: item.peerName || 'Contacto'};
    const copy = QP.el('div', 'peer-copy'); copy.append(QP.identity.createCapsule(peer, {snapshot, size: 'compact'}), QP.el('div', 'peer-meta', item.protocolName || item.protocolId));
    const actions = QP.el('div', 'row-actions'); actions.append(QP.button('Aceptar y abrir', 'green', () => respondPetition(item, true)), QP.button('Rechazar', 'danger', () => respondPetition(item, false)));
    row.append(copy, actions); list.appendChild(row);
  });
}

function renderLogs(logs) {
  const box = byId('logs');
  if (!box) return;
  const markerIndex = logsClearMarker ? logs.lastIndexOf(logsClearMarker) : -1;
  const visible = markerIndex >= 0 ? logs.slice(markerIndex + 1) : logs;
  if (visible.length === lastRenderedLogs.length && visible.every((line, index) => line === lastRenderedLogs[index])) return;
  const followTail = logsFollowTail;
  const previousTop = box.scrollTop;
  let overlap = 0;
  const maxOverlap = Math.min(lastRenderedLogs.length, visible.length);
  for (let length = maxOverlap; length > 0; length--) {
    if (lastRenderedLogs.slice(-length).every((line, index) => line === visible[index])) {
      overlap = length;
      break;
    }
  }
  let removedHeight = 0;
  logsRendering = true;
  if (overlap > 0 && box.querySelectorAll('.log-line').length === lastRenderedLogs.length) {
    for (let index = 0; index < lastRenderedLogs.length - overlap; index++) {
      removedHeight += box.firstElementChild?.getBoundingClientRect().height || 0;
      box.firstElementChild?.remove();
    }
    visible.slice(overlap).forEach(log => box.appendChild(QP.el('div', 'log-line', log)));
  } else {
    QP.clear(box);
    if (!visible.length) box.appendChild(QP.el('div', 'muted', logsClearMarker ? 'La vista está limpia. Los nuevos eventos aparecerán aquí.' : 'Aún no hay eventos.'));
    else visible.forEach(log => box.appendChild(QP.el('div', 'log-line', log)));
  }
  lastRenderedLogs = visible.slice();
  if (followTail) box.scrollTop = box.scrollHeight;
  else box.scrollTop = Math.max(0, previousTop - removedHeight);
  requestAnimationFrame(() => { logsRendering = false; });
}

function clearLogsView() {
  logsClearMarker = dashboardStatus?.logs?.at(-1) || null;
  logsFollowTail = true;
  lastRenderedLogs = ['\u0000'];
  renderLogs(dashboardStatus?.logs || []);
}

function openSavedPeerEditor(item = null) {
  savedPeerEditingHash = item?.certHash || null;
  byId('savedPeerModalTitle').textContent = item ? 'Editar contacto' : 'Añadir contacto';
  byId('savedName').value = item?.name || '';
  byId('savedCertHash').value = item?.certHash || '';
  byId('savedCertHash').readOnly = !!item;
  byId('savedAddresses').value = (item?.addresses || []).join('\n');
  byId('savedOnion').value = item?.onionAddress || '';
  byId('savedPorts').value = item ? formatPortRange(item) : '443';
  byId('savedNetworkType').value = NETWORK_TYPES.includes(item?.networkType) ? item.networkType : (item?.onionAddress ? 'Tor' : 'Wan');
  byId('savedAutoConnect').checked = item ? !!item.autoConnect : true;
  byId('savedPeerModalError').classList.add('hidden');
  byId('savedPeerModalError').textContent = '';
  byId('savedPeerModal').classList.remove('hidden');
  setTimeout(() => (item ? byId('savedName') : byId('savedCertHash')).focus(), 0);
}

function closeSavedPeerEditor() { byId('savedPeerModal').classList.add('hidden'); savedPeerEditingHash = null; }

async function saveSavedPeerEditor() {
  const certHash = byId('savedCertHash').value.trim();
  const portsRaw = byId('savedPorts').value.trim();
  const error = byId('savedPeerModalError');
  if (!certHash) return showSavedError('Certificate hash is required.');
  
  const ports = [];
  const parts = portsRaw.split(/[\s,;]+/).filter(Boolean);
  for (const part of parts) {
    if (part.includes('-') || part.includes('–')) {
      const rp = part.split(/[-–]/).filter(Boolean);
      if (rp.length === 2) {
        const start = parseInt(rp[0], 10);
        const end = parseInt(rp[1], 10);
        if (!isNaN(start) && !isNaN(end) && start >= 1 && end <= 65535 && end >= start) {
          for (let p = start; p <= end; p++) ports.push(p);
        } else {
          return showSavedError('Invalid port range: ' + part);
        }
      } else {
        return showSavedError('Invalid port range format: ' + part);
      }
    } else {
      const p = parseInt(part, 10);
      if (!isNaN(p) && p >= 1 && p <= 65535) {
        ports.push(p);
      } else {
        return showSavedError('Invalid port: ' + part);
      }
    }
  }

  const uniquePorts = [...new Set(ports)].sort((a, b) => a - b);
  if (!uniquePorts.length) return showSavedError('Enter at least one valid port (1-65535).');

  const payload = {
    certHash,
    name: byId('savedName').value.trim(),
    addresses: byId('savedAddresses').value,
    onionAddress: byId('savedOnion').value.trim(),
    ports: uniquePorts,
    networkType: byId('savedNetworkType').value,
    autoConnect: byId('savedAutoConnect').checked
  };
  try {
    byId('saveSavedPeerBtn').disabled = true;
    await QP.api('/api/saved-peer-update', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(payload) });
    QP.toast(savedPeerEditingHash ? 'Saved peer updated' : 'Saved peer added');
    closeSavedPeerEditor();
    refreshDashboard();
  } catch (e) { showSavedError(e.message); }
  finally { byId('saveSavedPeerBtn').disabled = false; }
  function showSavedError(message) { error.textContent = message; error.classList.remove('hidden'); }
}

function showSavedError(message) { const error = byId('savedPeerModalError'); error.textContent = message; error.classList.remove('hidden'); }

async function reconnectSavedPeer(certHash) { try { await QP.api('/api/connect-saved-peer',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash})}); QP.toast('Reconexión solicitada'); } catch(e){ QP.toast(e.message); } refreshDashboard(); }
async function toggleSavedAutoConnect(certHash,autoConnect) { try { await QP.api('/api/toggle-saved-peer-autoconnect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash,autoConnect})}); } catch(e){ QP.toast(e.message); } refreshDashboard(); }
async function setSavedPeerPermission(certHash,permission,allowed) { try { await QP.api('/api/peer/automatic-permission',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash,permission,allowed})}); QP.toast(allowed?'Permiso automático concedido':'Permiso automático revocado'); } catch(e){ QP.toast(e.message,'error'); } refreshDashboard(); }
async function deleteSavedPeer(item) { if (!confirm(`Delete saved peer "${item.name || 'Saved peer'}"? This removes it from the persistent database.`)) return; try { await QP.api('/api/saved-peer-delete',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash:item.certHash})}); QP.toast('Contacto eliminado'); } catch(e){ QP.toast(e.message); } refreshDashboard(); }
async function clearOfflineSavedPeers() { if (!confirm('Are you sure you want to remove all offline saved peers from the database?')) return; try { const r = await QP.api('/api/saved-peers-clear-offline',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'}); QP.toast(`Cleared ${r.removedCount || 0} offline saved peer(s)`); } catch(e){ QP.toast(e.message); } refreshDashboard(); }

async function setDiscovery(enabled, type = 'wan') {
  if (discoveryChanges.has(type)) return;
  discoveryChanges.add(type);
  const toggle = byId(type === 'tor' ? 'torDiscoveryToggle' : 'discoveryToggle');
  if (toggle) {toggle.disabled = true; if (type === 'tor' && enabled) toggle.checked = false;}
  try {
    const request = {enabled, type};
    if (type === 'tor' && enabled) {
      if (!await QP.confirmTorDiscoveryRisk()) return;
      request.acknowledgeTorPrivacyRisk = true;
    }
    await QP.api('/api/discovery', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(request)});
  } catch (error) { QP.toast(error.message, 'error'); }
  finally {
    discoveryChanges.delete(type);
    if (toggle) toggle.disabled = false;
    refreshDashboard();
  }
}
function renderHomeServiceControls(snapshot) {
  for (const [service, label, prefix] of [['wan', 'WAN', 'homeWan'], ['tor', 'Tor', 'homeTor']]) {
    const state = snapshot?.[service];
    const enabled = !!(state?.desiredEnabled ?? state?.isStarted ?? service === 'wan');
    const busy = networkServiceChanges.has(service);
    const target = busy ? networkServiceChanges.get(service) : enabled;
    const button = byId(prefix + 'ToggleBtn');
    if (button) {
      button.disabled = !snapshot || busy;
      button.textContent = busy ? `${target ? 'Activando' : 'Desactivando'} ${label}…` : `${enabled ? 'Desactivar' : 'Activar'} ${label}`;
      button.setAttribute('aria-pressed', String(enabled));
    }
    const setting = byId(service + 'Toggle');
    if (setting) { setting.checked = enabled; setting.disabled = busy; }
    let detail = !snapshot ? 'Consultando…' : busy ? (target ? 'Activando…' : 'Desactivando…')
      : state?.isStarted ? (enabled ? 'Activo' : 'Deteniéndose…')
      : enabled ? (state?.lastError ? 'No se pudo iniciar' : 'Iniciando…') : 'Inactivo';
    if (!busy && enabled && !state?.isStarted && !state?.lastError && service === 'tor' && state?.bootstrapProgress > 0)
      detail = `Iniciando · ${state.bootstrapProgress}%`;
    QP.setText(prefix + 'ServiceStatus', detail);
    const status = byId(prefix + 'ServiceStatus');
    if (status) status.title = enabled ? (state?.lastError || '') : '';
  }
}

async function setNetworkService(service, enabled) {
  if (networkServiceChanges.has(service)) return;
  networkServiceChanges.set(service, enabled);
  renderHomeServiceControls(dashboardStatus);
  try {
    await QP.api(`/api/${service}-${enabled ? 'start' : 'stop'}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    QP.toast(service === 'wan' ? (enabled ? 'Conexión directa activada' : 'Conexión directa detenida') : (enabled ? 'Tor activado' : 'Tor detenido'));
  } catch (error) { QP.toast(error.message, 'error'); }
  finally {
    await refreshDashboard();
    networkServiceChanges.delete(service);
    renderHomeServiceControls(dashboardStatus);
  }
}
async function setWan(enabled) { await setNetworkService('wan', enabled); }
async function setTor(enabled) { await setNetworkService('tor', enabled); }
async function newTorCircuits(){try{await QP.api('/api/tor-newnym',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});QP.toast('Se han solicitado nuevos circuitos Tor');}catch(e){QP.toast(e.message);}}
async function trustPeer(peerId,trust){try{await QP.api('/api/peer/trust',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId,trust})});QP.toast(trust?'Contacto de confianza':'Confianza retirada');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function savePeer(peerId,save){try{await QP.api('/api/save-peer',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId,save,autoConnect:true})});QP.toast(save?'Contacto guardado':'Contacto retirado de la libreta');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function connectPeerDirect(peerId){try{const r=await QP.api('/api/peer/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId})});if(r.success){QP.toast('Estableciendo conexión…','success');}else{QP.toast(r.error||'No se pudo conectar','error');}}catch(e){QP.toast(e.message,'error');}refreshDashboard();}
async function disconnectPeer(peerId){try{await QP.api('/api/disconnect-peer',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId})});}catch(e){QP.toast(e.message);}refreshDashboard();}
async function saveAllPeers(){try{const r=await QP.api('/api/save-all-peers',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});QP.toast(`${r.savedCount} peer(s) saved`);}catch(e){QP.toast(e.message);}refreshDashboard();}
async function cancelInterrogation(id){try{await QP.api('/api/cancel-interrogation',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id})});QP.toast('Intento de conexión cancelado');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function applyListenerPort(){const port=Number(byId('listenerPortInput').value);if(!Number.isInteger(port)||port<1||port>65535){QP.toast('Introduce un puerto válido entre 1 y 65535');return;}try{const r=await QP.api('/api/change-listener-port',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({port})});QP.toast(`Listener moved to ${r.port}`);}catch(e){QP.toast(e.message);}refreshDashboard();}

async function connectToken() {
  const input = byId('tokenInput');
  const button = byId('connectTokenBtn');
  if (button.disabled) return;
  const token = input.value.trim();
  if (!token) {
    QP.setText('connectFeedback', 'Pega una invitación para continuar.');
    input.setAttribute('aria-invalid', 'true'); input.focus(); return;
  }
  input.removeAttribute('aria-invalid');
  byId('activateTokenTransport').hidden = true;
  button.disabled = true;
  button.textContent = 'Conectando…';
  QP.setText('connectFeedback', 'Comprobando invitación…');
  try {
    await QP.api('/api/connect-token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token })
    });
    input.value = '';
    QP.setText('connectFeedback', 'Invitación aceptada. Buscando el dispositivo…');
    QP.toast('Token aceptado. Conectando…');
  } catch (e) {
    QP.setText('connectFeedback', e.message);
    const required = e.details?.requiredTransport;
    if (required === 'tor' || required === 'wan') {
      const activate = byId('activateTokenTransport'); activate.hidden = false; activate.dataset.mode = required;
      activate.textContent = required === 'tor' ? 'Activar Solo Tor y conectar' : 'Cambiar a WAN y conectar';
    } else input.setAttribute('aria-invalid', 'true');
  } finally {
    button.disabled = false;
    button.textContent = 'Buscar contacto';
  }
  refreshDashboard();
}

async function detectClipboardToken(explicit = false) {
  if (!navigator.clipboard || typeof navigator.clipboard.readText !== 'function') {
    if (explicit) QP.toast('El navegador no permite acceso directo al portapapeles.', 'warn');
    return;
  }

  try {
    const rawText = (await navigator.clipboard.readText() || '').trim();
    if (!rawText) {
      if (explicit) QP.toast('El portapapeles está vacío.', 'info');
      return;
    }

    const match = rawText.match(/(?:qp|qphp):\/\/[^\s"'<>]+/i);
    let candidate = match ? match[0] : null;

    if (!candidate) {
      if (rawText.toLowerCase().includes('.onion') || (/^[A-Za-z0-9+/=_-]{30,}$/.test(rawText) && !rawText.includes(' '))) {
        candidate = rawText;
      }
    }

    if (candidate) {
      const input = byId('tokenInput');
      if (input) {
        if (input.value.trim() !== candidate) {
          input.value = candidate;
          QP.toast('Token de conexión detectado en portapapeles y cargado.', 'info');
        } else if (explicit) {
          QP.toast('El token ya está cargado en el campo.', 'info');
        }
      }
    } else if (explicit) {
      QP.toast('No se encontró un token QP válido en el portapapeles.', 'warn');
    }
  } catch (err) {
    if (explicit) {
      QP.toast('No se pudo acceder al portapapeles: ' + (err.message || 'Permiso denegado'), 'warn');
    }
  }
}
async function respondPetition(item,accept){try{await QP.api('/api/respond-petition',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({requestId:item.requestId,accept})});if(accept){const key=normalizeProtocolName(item.protocolName);const app=APP_DEFS.find(x=>x.key===key);if(app?.href)setTimeout(()=>{QP.navigate(key, key==='virtual-disk'?`${app.href}?shareWith=${encodeURIComponent(item.peerId)}`:key==='chat'||key==='watch'?`${app.href}?peer=${encodeURIComponent(item.peerId)}`:app.href);},120);}}catch(e){QP.toast(e.message);}refreshDashboard();}

function openStunModal() {
  if (!currentStunData.exactMappings || !currentStunData.exactMappings.length) {
    QP.toast('Todavía no hay respuestas STUN disponibles');
    return;
  }
  renderStunModalList();
  byId('stunModal').classList.remove('hidden');
}

function closeStunModal() {
  byId('stunModal').classList.add('hidden');
}

function renderStunModalList() {
  const exact = currentStunData.exactMappings || [];
  const listBody = byId('stunModalList');
  const statsBox = byId('stunModalStats');
  QP.clear(listBody);
  QP.clear(statsBox);

  const endpoints = exact.map(item => {
    if (typeof item === 'string') {
      const parts = item.split(':');
      return { endpoint: item, address: parts[0] || item, port: parts[1] || '', hits: 1 };
    }
    return {
      endpoint: item.endpoint || `${item.address}:${item.port}`,
      address: item.address || '—',
      port: item.port ? String(item.port) : '—',
      hits: Number(item.hits) || 1
    };
  });

  const uniqueIps = [...new Set(endpoints.map(e => e.address).filter(Boolean))];
  const ports = [...new Set(endpoints.map(e => Number(e.port)).filter(p => !isNaN(p) && p > 0))].sort((a, b) => a - b);
  const totalHits = endpoints.reduce((sum, e) => sum + e.hits, 0);

  const stat1 = QP.el('span', '', ''); stat1.innerHTML = `<strong>Total responses:</strong> ${totalHits}`;
  const stat2 = QP.el('span', '', ''); stat2.innerHTML = `<strong>Unique endpoints:</strong> ${endpoints.length}`;
  const stat3 = QP.el('span', '', ''); stat3.innerHTML = `<strong>Unique IPs:</strong> ${uniqueIps.length}`;
  const stat4 = QP.el('span', '', ''); stat4.innerHTML = `<strong>Port(s):</strong> ${formatPortRange(ports.length ? ports : currentStunData.ports, currentStunData.portMode)}`;
  statsBox.append(stat1, stat2, stat3, stat4);

  if (!endpoints.length) {
    emptyRow(listBody, 4, 'Aún no se han detectado direcciones públicas.');
    return;
  }

  endpoints.forEach((item, index) => {
    const tr = document.createElement('tr');
    tr.appendChild(td(String(index + 1), 'muted'));
    tr.appendChild(td(item.endpoint, 'mono'));

    const hitsCell = document.createElement('td');
    const hitsBadge = QP.el('span', 'badge blue', `${item.hits} hit${item.hits === 1 ? '' : 's'}`);
    hitsCell.appendChild(hitsBadge);
    tr.appendChild(hitsCell);

    const actionCell = document.createElement('td');
    actionCell.appendChild(QP.button('Copiar', 'ghost', () => QP.copy(item.endpoint)));
    tr.appendChild(actionCell);
    listBody.appendChild(tr);
  });
}

function copyAllStunMappings() {
  const exact = currentStunData.exactMappings || [];
  if (!exact.length) {
    QP.toast('No hay direcciones públicas para copiar');
    return;
  }
  const endpoints = exact.map(item => typeof item === 'string' ? item : (item.endpoint || `${item.address}:${item.port}`));
  QP.copy(endpoints.join('\n'));
}

function renderQuicTelemetry(s) {
  const badge = byId('quicTelemetryStatusBadge');
  const body = byId('quicTelemetryBody');
  if (!body || !s) return;

  let sessions = [];
  if (Array.isArray(s)) {
    sessions = s;
  } else if (Array.isArray(s.quicTelemetry) && s.quicTelemetry.length > 0) {
    sessions = s.quicTelemetry;
  } else if (Array.isArray(s.sessions) && s.sessions.length > 0) {
    sessions = s.sessions;
  } else if (Array.isArray(s.peers) && s.peers.length > 0) {
    sessions = s.peers.filter(p => p.telemetry).map(p => ({
      peerId: p.id || p.peerId,
      peerName: p.name || p.peerName || 'Peer',
      protocolId: '',
      protocolName: 'Peer Direct',
      transportType: p.isTor ? 'Tor' : (p.activeTransport || 'WAN'),
      telemetry: p.telemetry,
      sampleTime: p.telemetry?.timestamp
    }));
  }

  if (window.NetworkTelemetry) {
    sessions = window.NetworkTelemetry.unique(sessions);
    window.NetworkTelemetry.update(sessions);
  }
  const activeCount = sessions.length;
  if (badge) {
    QP.clear(badge);
    const dot = QP.el('span', `status-dot ${activeCount > 0 ? 'on' : ''}`);
    badge.append(dot, document.createTextNode(`${activeCount} ${activeCount === 1 ? 'conexión activa' : 'conexiones activas'}`));
  }

  QP.setText('kpiQuicConnections', String(activeCount));

  if (activeCount === 0) {
    QP.setText('kpiQuicTransports', 'WAN / Tor');
    QP.setText('kpiQuicRtt', '—');
    QP.setText('kpiQuicRttRange', 'Min: — · Max: — · Var: —');
    QP.setText('kpiQuicLoss', '0.00%');
    QP.setText('kpiQuicLossDetails', '0 lost · 0 retrans');
    QP.setText('kpiQuicCwnd', '—');
    QP.setText('kpiQuicAlgorithm', 'Algorithm: BBR / CUBIC · MTU: —');
    QP.setText('kpiQuicThroughput', '—');
    QP.setText('kpiQuicPackets', 'Tx: 0 pkts · Rx: 0 pkts');

    const fragment = document.createDocumentFragment();
    emptyRow(fragment, 8, 'Las métricas aparecerán cuando haya una sesión de aplicación activa.');
    if (typeof body.replaceChildren === 'function') {
      body.replaceChildren(fragment);
    } else {
      QP.clear(body);
      body.appendChild(fragment);
    }
    return;
  }

  let totalRtt = 0;
  let minRttGlobal = Infinity;
  let maxRttGlobal = 0;
  let totalVar = 0;
  let totalLost = 0;
  let totalRetrans = 0;
  let totalCwnd = 0;
  let totalTxBytes = 0;
  let totalRxBytes = 0;
  let totalTxPackets = 0;
  let totalRxPackets = 0;
  let wanCount = 0;
  let torCount = 0;
  const algos = new Set();
  let validRttCount = 0;

  sessions.forEach(item => {
    const t = item.telemetry || {};
    if (String(item.transportType || '').toLowerCase() === 'tor') torCount++; else wanCount++;
    if (t.congestionAlgorithm) algos.add(t.congestionAlgorithm);

    if (t.rttMs !== undefined && t.rttMs > 0) {
      totalRtt += t.rttMs;
      validRttCount++;
      if (t.minRttMs !== undefined && t.minRttMs < minRttGlobal) minRttGlobal = t.minRttMs;
      if (t.maxRttMs !== undefined && t.maxRttMs > maxRttGlobal) maxRttGlobal = t.maxRttMs;
      if (t.rttVarianceMs !== undefined) totalVar += t.rttVarianceMs;
    }

    totalLost += Number(t.sendSuspectedLostPackets || 0);
    totalRetrans += Number(t.sendRetransmittablePackets || 0);
    totalCwnd += Number(t.sendCongestionWindow || 0);
    totalTxBytes += Number(t.sendTotalBytes || 0);
    totalRxBytes += Number(t.recvTotalBytes || 0);
    totalTxPackets += Number(t.sendTotalPackets || 0);
    totalRxPackets += Number(t.recvTotalPackets || 0);
  });

  const avgRtt = validRttCount > 0 ? (totalRtt / validRttCount) : 0;
  const avgVar = validRttCount > 0 ? (totalVar / validRttCount) : 0;
  const totalSent = totalTxPackets;
  const overallLossRatio = totalSent > 0 ? (totalLost / totalSent) * 100 : 0;

  QP.setText('kpiQuicTransports', `WAN: ${wanCount} · Tor: ${torCount}`);
  QP.setText('kpiQuicRtt', avgRtt > 0 ? `${avgRtt.toFixed(1)} ms` : '< 1 ms');
  QP.setText('kpiQuicRttRange', `Min: ${minRttGlobal === Infinity ? '—' : minRttGlobal.toFixed(1) + ' ms'} · Max: ${maxRttGlobal ? maxRttGlobal.toFixed(1) + ' ms' : '—'} · Var: ${avgVar.toFixed(1)} ms`);
  QP.setText('kpiQuicLoss', `${overallLossRatio.toFixed(2)}%`);
  QP.setText('kpiQuicLossDetails', `${totalLost.toLocaleString()} lost · ${totalRetrans.toLocaleString()} retrans`);
  QP.setText('kpiQuicCwnd', formatBytes(totalCwnd));
  QP.setText('kpiQuicAlgorithm', algos.size ? [...algos].join(' / ') : 'Algoritmo no disponible');
  QP.setText('kpiQuicThroughput', `Tx: ${formatBytes(totalTxBytes)} · Rx: ${formatBytes(totalRxBytes)}`);
  QP.setText('kpiQuicPackets', `Tx: ${totalTxPackets.toLocaleString()} pkts · Rx: ${totalRxPackets.toLocaleString()} pkts`);

  const fragment = document.createDocumentFragment();

  sessions.forEach(item => {
    const t = item.telemetry || {};
    const tr = document.createElement('tr');

    // 1. Peer / Identity
    const pcell = document.createElement('td');
    pcell.appendChild(QP.el('div', 'peer-primary', item.peerName || 'Peer'));
    pcell.appendChild(QP.el('div', 'peer-sub mono', QP.canonicalId(item.peerId)));
    tr.appendChild(pcell);

    // 2. Protocol / Transport
    const protoCell = document.createElement('td');
    protoCell.appendChild(QP.el('div', '', item.protocolName || 'Unknown'));
    const isTor = String(item.transportType || '').toLowerCase() === 'tor';
    const transBadge = QP.el('span', `badge ${isTor ? 'purple' : 'green'} space-top-xs`, item.transportType || 'WAN');
    protoCell.appendChild(transBadge);
    if (t.nativeRemoteEndpoint) {
      protoCell.appendChild(QP.el('div', 'peer-sub mono', t.nativeRemoteEndpoint));
    }
    tr.appendChild(protoCell);

    // 3. RTT (Min / Avg / Max)
    const rttCell = document.createElement('td');
    const rttVal = t.rttMs !== undefined ? `${t.rttMs} ms` : '—';
    rttCell.appendChild(QP.el('div', 'mono strong', rttVal));
    rttCell.appendChild(QP.el('div', 'peer-sub mono', `Min: ${t.minRttMs ?? '—'} · Max: ${t.maxRttMs ?? '—'} ms · Var: ${t.rttVarianceMs ?? '—'} ms`));
    tr.appendChild(rttCell);

    // 4. Loss %
    const lossCell = document.createElement('td');
    const lossPct = t.packetLossPercentage !== undefined ? `${t.packetLossPercentage}%` : '0.00%';
    const lossBadge = QP.el('span', `badge ${t.packetLossPercentage > 1 ? 'red' : 'green'}`, lossPct);
    lossCell.appendChild(lossBadge);
    lossCell.appendChild(QP.el('div', 'peer-sub mono', `Lost: ${t.sendSuspectedLostPackets ?? 0} · Retrans: ${t.sendRetransmittablePackets ?? 0}`));
    tr.appendChild(lossCell);

    // 5. CWND / Path MTU
    const cwndCell = document.createElement('td');
    cwndCell.appendChild(QP.el('div', 'mono strong', formatBytes(t.sendCongestionWindow || 0)));
    cwndCell.appendChild(QP.el('div', 'peer-sub mono', `MTU: ${t.pathMtu || '—'} B`));
    tr.appendChild(cwndCell);

    // 6. Wire Transfer (Tx / Rx)
    const wireCell = document.createElement('td');
    wireCell.appendChild(QP.el('div', 'mono', `Tx: ${formatBytes(t.sendTotalBytes || 0)}`));
    wireCell.appendChild(QP.el('div', 'mono text-muted', `Rx: ${formatBytes(t.recvTotalBytes || 0)}`));
    wireCell.appendChild(QP.el('div', 'peer-sub mono', `${t.sendTotalPackets ?? 0} tx · ${t.recvTotalPackets ?? 0} rx pkts`));
    tr.appendChild(wireCell);

    // 7. Security / ALPN
    const secCell = document.createElement('td');
    secCell.appendChild(QP.el('div', 'mono text-xs', t.cipherSuite || 'TLS 1.3'));
    secCell.appendChild(QP.el('div', 'peer-sub mono', `ALPN: ${t.alpn || '—'}`));
    tr.appendChild(secCell);

    // 8. Diagnostics / Handshake
    const diagCell = document.createElement('td');
    const hs = t.handshakeDurationMs !== undefined ? `${t.handshakeDurationMs} ms` : '—';
    diagCell.appendChild(QP.el('div', 'mono strong', `Handshake: ${hs}`));
    const drops = t.recvDroppedPackets ?? 0;
    const dupes = t.recvDuplicatePackets ?? 0;
    diagCell.appendChild(QP.el('div', 'peer-sub mono', `Drops: ${drops} · Dupes: ${dupes}`));
    tr.appendChild(diagCell);

    fragment.appendChild(tr);
  });

  if (typeof body.replaceChildren === 'function') {
    body.replaceChildren(fragment);
  } else {
    QP.clear(body);
    body.appendChild(fragment);
  }
}
