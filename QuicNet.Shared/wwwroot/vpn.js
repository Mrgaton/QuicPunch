(() => {
let vpnStatus = null;
let settingsDirty = false;
let vpnConfiguration = null;
let refreshingConfiguration = false;
const connectingPeers = new Set();
const peerPingResults = new Map();

document.addEventListener('DOMContentLoaded', () => {
  document.getElementById('applyLanBtn')?.addEventListener('click', applyLan);
  document.getElementById('connectAllBtn')?.addEventListener('click', connectAll);
  document.getElementById('refreshVpnBtn')?.addEventListener('click', refreshVpn);

  const myIpChip = document.getElementById('myIpChip');
  myIpChip?.addEventListener('click', () => {
    const ip = document.getElementById('virtualIp')?.textContent;
    if (ip && ip !== '—') copyIp(ip, myIpChip);
  });

  const setOptimalMtu = () => {
    const el = document.getElementById('lanMtu');
    if (el) {
      el.value = 1420;
      settingsDirty = true;
      QP.toast('MTU restablecido a 1420');
    }
  };
  document.getElementById('resetMtuBtn')?.addEventListener('click', setOptimalMtu);

  // Direct toggle on switch change
  document.getElementById('lanEnabled')?.addEventListener('change', e => {
    toggleLan(e.target.checked);
  });

  // Auto assign toggle
  document.getElementById('lanAutoAssign')?.addEventListener('change', e => {
    settingsDirty = true;
    updateAutoAssignState(e.target.checked, true);
  });

  document.querySelectorAll('#restartAdminBtn, .btn-restart-admin').forEach(btn => {
    btn.addEventListener('click', restartAsAdmin);
  });

  ['lanIp', 'lanMask', 'lanMtu'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', () => { settingsDirty = true; });
  });

  if (window.QP && typeof QP.on === 'function') {
    QP.on('status_updated', data => {
      vpnStatus = data;
      renderVpn();
    });
    QP.on('peer_connected', () => refreshVpn());
    QP.on('peer_disconnected', () => refreshVpn());
    QP.on('peer_available', () => refreshVpn());
    QP.on('peer_network_disconnected', () => refreshVpn());
    QP.on('view_changed', data => {
      if (data && data.page === 'vpn') {
        refreshVpn();
      }
    });
  }

  refreshVpn();
});

async function refreshVpn() {
  if ((QP.capabilities || window.__qp_capabilities)?.capabilities?.vpn?.available === false) return;
  try {
    vpnStatus = await (QP.getStatus ? QP.getStatus(true) : QP.api('/api/status'));
    vpnConfiguration = normalizeVpnConfiguration(await QP.api('/api/vpn/config'));
    renderVpn();
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

function updateAutoAssignState(autoAssign, focus = false) {
  const lanIp = document.getElementById('lanIp');
  const lanMask = document.getElementById('lanMask');
  if (lanIp) {
    lanIp.disabled = autoAssign;
    if (!autoAssign && focus) lanIp.focus();
  }
  if (lanMask) {
    lanMask.disabled = autoAssign;
  }
}

async function toggleLan(enabled) {
  const switchEl = document.getElementById('lanEnabled');
  const badge = document.getElementById('lanLiveBadge');

  if (badge) {
    badge.className = 'badge';
    badge.textContent = enabled ? 'Iniciando...' : 'Deteniendo...';
  }

  QP.toast(enabled ? 'Iniciando adaptador Wintun...' : 'Deteniendo adaptador...', 'warn');

  try {
    const res = await QP.api('/api/lan-toggle', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled })
    });

    if (res && res.success) {
      settingsDirty = false;
      QP.toast(enabled ? (res.active ? `Adaptador activo (${res.ip})` : 'Preferencia guardada. La VPN se activará cuando tenga permisos.') : 'Red privada desactivada', res.active || !enabled ? 'ok' : 'warn');
    } else {
      if (switchEl) switchEl.checked = !enabled;
      QP.toast(res?.error || 'No se pudo cambiar el estado del adaptador', 'error');
    }
  } catch (e) {
    if (switchEl) switchEl.checked = !enabled;
    const msg = e.message || 'Error al comunicar con el servicio';
    QP.toast(msg, 'error');
    if (msg.toLowerCase().includes('administrador') || msg.toLowerCase().includes('admin')) {
      const banner = document.getElementById('adminRequiredBanner');
      if (banner) {
        banner.classList.remove('hidden');
        banner.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }
    }
  }

  await refreshVpn();
}

function renderVpn() {
  if ((QP.capabilities || window.__qp_capabilities)?.capabilities?.vpn?.available === false) return;
  if (!vpnStatus) return;
  const lan = vpnStatus.lan || {};
  if (vpnConfiguration && lan.vpnRevision !== vpnConfiguration.configuration.revision && !refreshingConfiguration) {
    refreshingConfiguration = true;
    QP.api('/api/vpn/config').then(value => { vpnConfiguration = normalizeVpnConfiguration(value); renderVpn(); })
      .catch(e => QP.toast(e.message, 'error')).finally(() => { refreshingConfiguration = false; });
  }

  // Check admin requirement: if not admin, Wintun always requires elevation on Windows
  const needsAdmin = (!!lan.requiresAdmin && !lan.canStartWithoutElevation) ||
    (lan.adapterStatus && lan.adapterStatus.toLowerCase().includes('administrator')) ||
    (lan.lastError && lan.lastError.toLowerCase().includes('administrator'));

  const banner = document.getElementById('adminRequiredBanner');
  if (banner) {
    if (needsAdmin && !lan.active) banner.classList.remove('hidden');
    else banner.classList.add('hidden');
  }

  // Live badge
  const liveBadge = document.getElementById('lanLiveBadge');
  if (liveBadge) {
    if (lan.active) {
      liveBadge.className = 'badge ok';
      liveBadge.textContent = 'Activo';
    } else if (needsAdmin) {
      liveBadge.className = lan.desiredEnabled ? 'badge warn' : 'badge';
      liveBadge.textContent = lan.desiredEnabled ? 'Esperando permisos' : 'Inactivo';
    } else if (lan.adapterStatus && lan.adapterStatus.toLowerCase().includes('error')) {
      liveBadge.className = 'badge danger';
      liveBadge.textContent = 'Error';
    } else {
      liveBadge.className = 'badge';
      liveBadge.textContent = 'Inactivo';
    }
  }

  const connectAllBtn = document.getElementById('connectAllBtn');
  if (connectAllBtn) connectAllBtn.disabled = !(vpnStatus.peers || []).some(canConnectLan);

  const activeCount = (lan.activePeers || []).length;
  const myIp = lan.localIp || '—';
  QP.setText('virtualIp', myIp);

  const statusSuffix = lan.active ? 'Activo en Wintun' : (needsAdmin ? 'Requiere Administrador' : 'Adaptador detenido');
  QP.setText('addressMode', `${lan.autoAssign ? 'Automática (10.0.0.x/24)' : 'Manual'} · ${statusSuffix}`);
  QP.setText('lanMaskSummary', lan.subnetMask || '255.255.255.0');
  QP.setText('lanMtuSummary', `MTU: ${lan.mtu || 1420}`);
  QP.setText('lanPeerCount', activeCount);
  QP.setText('lanPeerCountSub', `${activeCount} ${activeCount === 1 ? 'sesión activa' : 'sesiones activas'}`);
  QP.setText('lanTraffic', QP.fmtBytes((lan.totalRxBytes || 0) + (lan.totalTxBytes || 0)));
  QP.setText('lanTrafficPackets', `${((lan.totalRxPackets || 0) + (lan.totalTxPackets || 0)).toLocaleString()} paquetes`);
  QP.setText('lanError', lan.lastError || '');

  const switchHint = document.getElementById('lanSwitchHint');
  if (switchHint) {
    if (lan.active) {
      switchHint.textContent = `Adaptador activo en ${myIp} (${lan.adapterName || 'Wintun'})`;
      switchHint.style.color = 'var(--ok)';
    } else if (needsAdmin) {
      switchHint.textContent = lan.desiredEnabled ? 'Activación automática guardada · esperando permisos' : 'Puedes configurar la VPN antes de conceder permisos';
      switchHint.style.color = '#f87171';
    } else {
      switchHint.textContent = lan.desiredEnabled ? 'La red se activará automáticamente al estar disponible' : 'Habilitar ahora y al abrir la aplicación';
      switchHint.style.color = 'var(--muted)';
    }
  }

  // Form fields
  if (!settingsDirty) {
    const lanEnabled = document.getElementById('lanEnabled');
    if (lanEnabled) lanEnabled.checked = !!lan.desiredEnabled;
    if (vpnConfiguration?.configuration) {
      QP.setText('vpnConfigLocation', `Configuración: ${vpnConfiguration.path}`);
      QP.setText('vpnConfigError', vpnConfiguration.error || lan.vpnConfigurationError || '');
    }
    const lanAutoAssign = document.getElementById('lanAutoAssign');
    if (lanAutoAssign) lanAutoAssign.checked = !!lan.autoAssign;
    const lanIp = document.getElementById('lanIp');
    if (lanIp) lanIp.value = lan.localIp || '';
    const lanMask = document.getElementById('lanMask');
    if (lanMask) lanMask.value = lan.subnetMask || '255.255.255.0';
    const lanMtu = document.getElementById('lanMtu');
    if (lanMtu) lanMtu.value = (lan.mtu && lan.mtu !== 1500) ? lan.mtu : 1420;

    updateAutoAssignState(!!lan.autoAssign);
  }

  // Clear connecting peers that are now active
  (lan.activePeers || []).forEach(p => {
    connectingPeers.delete(p.peerId);
  });

  renderPeers();
  renderSessions();
}

function createIpChip(ip, isLg = false) {
  const btn = document.createElement('button');
  btn.className = isLg ? 'ip-chip lg' : 'ip-chip';
  btn.type = 'button';
  btn.title = 'Clic para copiar IP';
  const iconSize = isLg ? 15 : 12;
  btn.innerHTML = `<span>${ip}</span><span class="ip-icon"><svg width="${iconSize}" height="${iconSize}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path></svg></span>`;
  btn.addEventListener('click', (e) => {
    e.stopPropagation();
    copyIp(ip, btn);
  });
  return btn;
}

function canConnectLan(peer) {
  return !!peer?.isTrusted && peer.isQuicConnected === true && peer.vpnCanStart === true &&
    !connectingPeers.has(peer.id) && !(vpnStatus?.lan?.activePeers || []).some(session => session.peerId === peer.id);
}

function renderPeers() {
  const box = document.getElementById('vpnPeers');
  if (!box) return;
  QP.clear(box);

  const activeSessions = vpnStatus?.lan?.activePeers || [];
  const rawPeers = vpnStatus?.peers || [];
  const savedPeers = vpnStatus?.savedPeers || [];

  // Merge raw peers with active LAN sessions so no active peer is missing
  const peerMap = new Map();
  rawPeers.forEach(p => {
    peerMap.set(p.id, { ...p });
  });

  // Saved peers keep configuration available while discovery establishes transport automatically.
  savedPeers.forEach(saved => {
    if (rawPeers.some(peer => peer.certHash && peer.certHash === saved.certHash)) return;
    peerMap.set(`saved:${saved.certHash}`, {
      id: `saved:${saved.certHash}`,
      certHash: saved.certHash,
      name: saved.name || 'Peer guardado',
      isTrusted: true,
      isSavedOffline: true,
      autoConnectOnStartup: !!saved.autoConnect,
      addresses: saved.addresses || [],
      onionAddress: saved.onionAddress || '',
      isTor: saved.networkType === 'Tor'
    });
  });

  activeSessions.forEach(s => {
    if (!peerMap.has(s.peerId)) {
      peerMap.set(s.peerId, {
        id: s.peerId,
        name: s.peerName || 'Peer',
        isTrusted: true,
        endpoint: '',
        ping: null
      });
    }
  });

  const allPeers = Array.from(peerMap.values());

  if (!allPeers.length) {
    box.appendChild(QP.el('div', 'empty', 'Todavía no hay peers de confianza. Añade un peer desde Red P2P para abrir un túnel LAN.'));
    return;
  }

  // Established QUIC transports first, with active LAN sessions leading within that group.
  allPeers.sort((a, b) => {
    const aActive = activeSessions.some(s => s.peerId === a.id);
    const bActive = activeSessions.some(s => s.peerId === b.id);
    const aReady = !!a.isQuicConnected || aActive;
    const bReady = !!b.isQuicConnected || bActive;
    if (aReady !== bReady) return aReady ? -1 : 1;
    if (aActive !== bActive) return aActive ? -1 : 1;
    if (!!a.isTrusted !== !!b.isTrusted) return a.isTrusted ? -1 : 1;
    return (a.name || '').localeCompare(b.name || '');
  });

  allPeers.forEach(peer => {
    const session = activeSessions.find(p => p.peerId === peer.id);
    const active = !!session;
    const isConnecting = connectingPeers.has(peer.id);
    const isTrusted = !!peer.isTrusted;

    const row = QP.el('div', 'peer-row');
    row.appendChild(QP.el('div', 'avatar', QP.initials(peer.name)));

    const copy = QP.el('div', 'peer-copy');
    const nameWrap = QP.el('div', 'peer-name', peer.name || 'Peer');
    if (active) {
      const badge = QP.el('span', 'badge sm ok', 'Conectado');
      badge.style.marginLeft = '8px';
      nameWrap.appendChild(badge);
    } else if (!isTrusted) {
      const badge = QP.el('span', 'badge sm warn', 'Sin autorizar');
      badge.style.marginLeft = '8px';
      badge.title = 'Requiere autorización para establecer conexión LAN';
      nameWrap.appendChild(badge);
    }
    copy.appendChild(nameWrap);

    const meta = QP.el('div', 'peer-meta');
    meta.style.display = 'flex';
    meta.style.alignItems = 'center';
    meta.style.gap = '8px';
    meta.style.flexWrap = 'wrap';
    meta.style.marginTop = '4px';

    if (active && session?.virtualIp) {
      const ipChip = createIpChip(session.virtualIp);
      meta.appendChild(ipChip);

      if (peerPingResults.has(peer.id)) {
        const pingMeta = QP.el('span', 'muted', `· Latencia: ${peerPingResults.get(peer.id)}`);
        pingMeta.style.fontSize = '11.5px';
        meta.appendChild(pingMeta);
      }
    } else if (isConnecting) {
      meta.textContent = 'Estableciendo túnel...';
    } else if (peer.isSavedOffline) {
      meta.textContent = 'Guardado · conexión automática pendiente';
    } else if (!peer.isQuicConnected) {
      meta.textContent = 'Conexión QUIC automática pendiente';
    } else if (peer.vpnCanStart == null) {
      meta.textContent = 'QUIC conectado · comprobando disponibilidad de LAN';
    } else if (!peer.vpnCanStart) {
      meta.textContent = 'QUIC conectado · LAN remota no disponible';
    } else {
      meta.textContent = 'QUIC conectado · LAN disponible';
    }
    copy.appendChild(meta);
    if (isTrusted && peer.certHash && vpnConfiguration?.configuration) {
      try {
        const identity = vpnIdentity(peer.certHash);
        const policy = vpnConfiguration.configuration.peers.find(p => vpnIdentity(p.certHash) === identity);
        const defaults = (vpnConfiguration.peerDefaults || []).find(p => vpnIdentity(p.certHash) === identity);
        const mode = policy ? (policy.mode || vpnConfiguration.configuration.mode) : 'simple';
        const automatic = policy ? policy.autoConnect && (policy.mode != null || vpnConfiguration.configuration.autoConnect) : defaults?.autoConnect && vpnConfiguration.configuration.autoConnect;
        const connection = policy?.enabled === false ? 'Desactivada' : policy?.paused ? 'En pausa' : automatic ? 'Conectar al abrir' : (policy?.autoAccept ?? defaults?.autoAccept) ? 'Solo aceptar automáticamente' : 'Conexión manual';
        copy.appendChild(QP.el('div', 'vpn-peer-summary', `${mode === 'advanced' ? 'LAN avanzada' : 'LAN automática'} · ${connection}`));
      } catch { /* A persisted VPN policy requires a valid certificate identity. */ }
    }

    const actions = QP.el('div', 'row-actions');
    actions.style.display = 'flex';
    actions.style.alignItems = 'center';
    actions.style.gap = '6px';
    if (isTrusted && peer.certHash && vpnConfiguration?.configuration) {
      actions.appendChild(QP.button('Configurar VPN', 'ghost', () => editVpnPeer(peer)));
    }

    if (active && session?.virtualIp) {
      // Ping button
      const pingBtn = QP.button('Ping', 'ghost', () => pingLanPeer(session.virtualIp, peer.id));
      pingBtn.title = 'Probar latencia ICMP';
      actions.appendChild(pingBtn);

      // Disconnect button
      const discBtn = QP.button('Desconectar', 'danger', () => disconnectLan(peer.id));
      actions.appendChild(discBtn);
    } else if (canConnectLan(peer) || (isConnecting && peer.isQuicConnected && peer.vpnCanStart === true)) {
      const join = QP.button(isConnecting ? 'Conectando...' : 'Conectar', isConnecting ? 'ghost' : 'accent', () => connectLan(peer.id));
      join.disabled = isConnecting;
      actions.appendChild(join);
    }

    row.append(copy, actions);
    box.appendChild(row);
  });
}

function renderSessions() {
  const body = document.getElementById('lanSessions');
  if (!body) return;
  QP.clear(body);
  const items = vpnStatus.lan?.activePeers || [];
  if (!items.length) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 6;
    td.textContent = 'No hay sesiones activas en la subred.';
    td.className = 'muted';
    td.style.textAlign = 'center';
    td.style.padding = '20px';
    tr.appendChild(td);
    body.appendChild(tr);
    return;
  }

  items.forEach(item => {
    const tr = document.createElement('tr');

    // Peer
    const tdPeer = document.createElement('td');
    tdPeer.appendChild(QP.el('strong', '', item.peerName || 'Peer'));
    tr.appendChild(tdPeer);

    // Virtual IP as interactive chip
    const tdIp = document.createElement('td');
    const ipChip = createIpChip(item.virtualIp);
    tdIp.appendChild(ipChip);
    tr.appendChild(tdIp);

    // Connected At
    const tdTime = document.createElement('td');
    tdTime.textContent = item.connectedAt || '—';
    tr.appendChild(tdTime);

    // RX
    const tdRx = document.createElement('td');
    tdRx.textContent = QP.fmtBytes(item.rxBytes || 0);
    tr.appendChild(tdRx);

    // TX
    const tdTx = document.createElement('td');
    tdTx.textContent = QP.fmtBytes(item.txBytes || 0);
    tr.appendChild(tdTx);

    // Actions
    const tdActions = document.createElement('td');
    const wrap = document.createElement('div');
    wrap.style.display = 'flex';
    wrap.style.gap = '6px';

    const pingBtn = QP.button('Ping', 'sm ghost', () => pingLanPeer(item.virtualIp, item.peerId));
    const discBtn = QP.button('Desconectar', 'sm danger', () => disconnectLan(item.peerId));

    wrap.append(pingBtn, discBtn);
    tdActions.appendChild(wrap);
    tr.appendChild(tdActions);

    body.appendChild(tr);
  });
}

function copyIp(ip, element = null) {
  if (!ip || ip === '—') return;
  QP.copy(ip);
  QP.toast(`IP copiada: ${ip}`, 'ok');

  if (element) {
    element.classList.add('copied');
    const iconSpan = element.querySelector('.ip-icon');
    const oldHtml = iconSpan ? iconSpan.innerHTML : null;
    if (iconSpan) {
      const size = element.classList.contains('lg') ? 15 : 12;
      iconSpan.innerHTML = `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>`;
    }
    setTimeout(() => {
      element.classList.remove('copied');
      if (iconSpan && oldHtml) iconSpan.innerHTML = oldHtml;
    }, 1200);
  }
}

async function pingLanPeer(ip, peerId) {
  if (!ip || ip === '—') return;
  QP.toast(`Enviando ping a ${ip}...`);
  try {
    const res = await QP.api('/api/lan-ping-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ip })
    });
    if (res && res.success) {
      const pingText = `${res.rttMs} ms`;
      QP.toast(`Respuesta de ${ip}: ${pingText}`, 'ok');
      if (peerId) {
        peerPingResults.set(peerId, pingText);
        renderPeers();
      }
    } else {
      QP.toast(`Sin respuesta de ${ip} (${res?.error || 'Timeout'})`, 'warn');
    }
  } catch (e) {
    QP.toast(`Error en ping: ${e.message}`, 'error');
  }
}

async function connectLan(peerId) {
  const peer = (vpnStatus?.peers || []).find(p => p.id === peerId);
  if (!canConnectLan(peer)) return;
  connectingPeers.add(peerId);
  renderPeers();

  try {
    QP.toast('Conectando túnel LAN con el peer...');
    const res = await QP.api('/api/connect-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, protocol: 'lan' })
    });

    if (res && res.success) {
      QP.toast('Túnel LAN iniciado.', 'ok');
      setTimeout(refreshVpn, 1000);
      setTimeout(refreshVpn, 3000);
      setTimeout(() => {
        if (connectingPeers.delete(peerId)) refreshVpn();
      }, 20000);
    } else {
      connectingPeers.delete(peerId);
      renderPeers();
      QP.toast(res?.error || 'No se pudo conectar con el peer', 'error');
    }
  } catch (e) {
    connectingPeers.delete(peerId);
    renderPeers();
    QP.toast(e.message || 'Error de conexión LAN', 'error');
  }
}

async function disconnectLan(peerId) {
  try {
    QP.toast('Desconectando sesión...');
    const res = await QP.api('/api/lan-disconnect-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId })
    });
    if (res && res.success) {
      QP.toast('Peer desconectado');
    }
  } catch (e) {
    QP.toast(e.message || 'Error al desconectar', 'error');
  }
  refreshVpn();
}

async function connectAll() {
  const peers = (vpnStatus?.peers || []).filter(canConnectLan);
  for (const peer of peers) await connectLan(peer.id);
  QP.toast(peers.length ? `Iniciando conexión con ${peers.length} peer(s)...` : 'No hay peers disponibles para conectar a la LAN');
}

async function applyLan() {
  const lanEnabled = document.getElementById('lanEnabled');
  const lanAutoAssign = document.getElementById('lanAutoAssign');
  const lanIp = document.getElementById('lanIp');
  const lanMask = document.getElementById('lanMask');
  const lanMtu = document.getElementById('lanMtu');
  if (!lanEnabled || !lanAutoAssign || !lanIp || !lanMask || !lanMtu) return;

  const autoAssign = lanAutoAssign.checked;

  let activePeers = vpnStatus?.lan?.activePeers || [];
  if (activePeers.length > 0) {
    const peerNames = activePeers.map(p => p.peerName || 'Peer').join(', ');
    const msg = `Actualmente hay ${activePeers.length} peer(s) conectado(s) (${peerNames}).\n\nAl aplicar los cambios, el adaptador se reiniciará.\n\n¿Deseas continuar?`;
    if (!window.confirm(msg)) {
      return;
    }
  }

  let mtu = Number(lanMtu.value);
  if (!mtu || mtu < 1200 || mtu > 9000 || mtu === 1500) {
    mtu = 1420;
    lanMtu.value = 1420;
  }

  const payload = {
    enabled: lanEnabled.checked,
    autoAssign: autoAssign,
    ip: lanIp.value.trim(),
    subnetMask: lanMask.value.trim(),
    mtu: mtu
  };

  try {
    const configuration = JSON.parse(JSON.stringify(vpnConfiguration.configuration));
    Object.assign(configuration, { enabled: payload.enabled, autoAssign: payload.autoAssign,
      localAddress: payload.ip, subnetMask: payload.subnetMask, mtu: payload.mtu });
    const res = await QP.api('/api/vpn/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ configuration, expectedRevision: configuration.revision })
    });

    if (res && res.success) {
      settingsDirty = false;
      QP.toast('Configuración guardada correctamente', 'ok');
      await refreshVpn();
    } else {
      QP.toast(res?.error || 'Error al guardar configuración', 'error');
    }
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

function normalizeVpnConfiguration(response) {
  // Accept older desktop binaries that serialized typed configuration with PascalCase.
  const camel = value => Array.isArray(value) ? value.map(camel) : value && typeof value === 'object'
    ? Object.fromEntries(Object.entries(value).map(([key, item]) => [key[0].toLowerCase() + key.slice(1), camel(item)])) : value;
  const normalized = camel(response);
  if (!normalized?.configuration || !Array.isArray(normalized.configuration.peers))
    throw new Error('No se pudo cargar la configuración VPN. Actualiza la aplicación y vuelve a intentarlo.');
  return normalized;
}

function vpnIdentity(value) {
  if (/^[0-9a-f]{64}$/i.test(value || '')) return value.toUpperCase();
  try {
    const bytes = atob(value || '');
    if (bytes.length === 32) return Array.from(bytes, c => c.charCodeAt(0).toString(16).padStart(2, '0')).join('').toUpperCase();
  } catch { }
  throw new Error('La identidad de este contacto no es válida para configurar su VPN.');
}

function editVpnPeer(peer) {
  let dialog;
  try {
    if (!vpnConfiguration?.configuration) throw new Error('La configuración VPN todavía no está disponible. Pulsa Refrescar.');
    const configuration = JSON.parse(JSON.stringify(vpnConfiguration.configuration));
    const identity = vpnIdentity(peer.certHash);
    const existing = configuration.peers.find(p => vpnIdentity(p.certHash) === identity);
    const defaults = (vpnConfiguration.peerDefaults || []).find(p => vpnIdentity(p.certHash) === identity) || {};
    const policy = existing || { certHash: identity, mode: 'simple', enabled: true, autoConnect: defaults.autoConnect ?? (!!peer.autoConnectOnStartup && !!defaults.autoAccept),
      autoAccept: !!defaults.autoAccept, paused: false, virtualAddress: '', allowedSources: [],
      accessibleDestinations: defaults.accessibleDestinations || [], routes: [], allowBroadcast: false, allowForwarding: false };
    dialog = document.createElement('dialog'); dialog.className = 'vpn-peer-dialog';
    dialog.setAttribute('aria-labelledby', 'vpnPeerDialogTitle');
    const title = QP.el('h2', '', `VPN de ${peer.name || 'contacto'}`); title.id = 'vpnPeerDialogTitle'; dialog.appendChild(title);
    dialog.appendChild(QP.el('p', 'muted', 'Puedes guardar esta configuración con el adaptador apagado y sin permisos de administrador.'));
    const form = document.createElement('form');
    const fields = new Map();
    let fieldHost = form;
    function checkbox(key, label) {
      const row = document.createElement('label'); row.className = 'field space-top';
      const input = document.createElement('input'); input.type = 'checkbox'; input.name = key; input.checked = !!policy[key];
      row.append(input, document.createTextNode(` ${label}`)); fieldHost.appendChild(row); fields.set(key, input);
    }
    function text(key, label, placeholder) {
      const row = document.createElement('label'); row.className = 'field space-top'; row.appendChild(document.createTextNode(label));
      const input = document.createElement('input'); input.name = key; input.className = 'input mono'; input.placeholder = placeholder || '';
      input.value = Array.isArray(policy[key]) ? policy[key].join(', ') : policy[key] || '';
      row.appendChild(input); fieldHost.appendChild(row); fields.set(key, input);
    }
    const modeRow = QP.el('label', 'field space-top'); modeRow.appendChild(document.createTextNode('Modo de red de este contacto'));
    const mode = document.createElement('select'); mode.name = 'mode'; mode.className = 'input';
    for (const [value, label] of [['simple', 'LAN automática'], ['advanced', 'LAN avanzada']]) {
      const option = document.createElement('option'); option.value = value; option.textContent = label; mode.appendChild(option);
    }
    mode.value = policy.mode || configuration.mode; modeRow.appendChild(mode); form.appendChild(modeRow);
    form.appendChild(QP.el('p', 'muted', 'LAN automática asigna la IP y permite la comunicación local. LAN avanzada añade direcciones, accesos y rutas propios para este contacto.'));
    checkbox('enabled', 'Permitir VPN para este contacto');
    const connectionRow = QP.el('label', 'field space-top'); connectionRow.appendChild(document.createTextNode('Conexión con este contacto'));
    const connection = document.createElement('select'); connection.name = 'connectionMode'; connection.className = 'input';
    const modes = [['manual', 'Conectar manualmente'], ['maintain', 'Conectar al abrir y mantener conectada'], ['receive', 'Solo aceptar automáticamente']];
    if (policy.autoConnect && !policy.autoAccept) modes.push(['outgoing', 'Reconectar salidas y confirmar entradas']);
    for (const [value, label] of modes) { const option = document.createElement('option'); option.value = value; option.textContent = label; connection.appendChild(option); }
    connection.value = policy.autoConnect ? (policy.autoAccept ? 'maintain' : 'outgoing') : (policy.autoAccept ? 'receive' : 'manual');
    connectionRow.appendChild(connection); form.appendChild(connectionRow);
    form.appendChild(QP.el('p', 'muted', 'Mantener conectada activa la red al abrir la aplicación, reconecta este contacto y acepta su VPN sin preguntar. Configura también el otro equipo para conectar sin intervención.'));
    checkbox('paused', 'Pausar temporalmente este contacto');
    connection.addEventListener('change', () => {
      if (connection.value !== 'manual') { fields.get('enabled').checked = true; fields.get('paused').checked = false; }
    });
    const advanced = QP.el('div', 'vpn-peer-advanced'); form.appendChild(advanced); fieldHost = advanced;
    {
      text('virtualAddress', 'IP virtual autorizada (vacío: automática)', defaults.virtualAddress);
      text('allowedSources', 'Otros orígenes autorizados (CIDR separados por comas)', '192.168.50.0/24');
      text('accessibleDestinations', 'Destinos accesibles desde este contacto', `${vpnStatus?.lan?.localIp || '10.0.0.2'}/32`);
      text('routes', 'Destinos de mi tráfico que van hacia este contacto', '192.168.50.0/24');
      checkbox('allowBroadcast', 'Permitir broadcast y multicast');
      checkbox('allowForwarding', 'Permitir reenvío a otros contactos y redes compartidas');
      advanced.appendChild(QP.el('p', 'muted', 'La IP virtual siempre se autoriza como origen y ruta /32. Para compartir una LAN física, configura también sus rutas de retorno o NAT.'));
    }
    fieldHost = form;
    const updateMode = () => {
      advanced.hidden = mode.value !== 'advanced';
      advanced.querySelectorAll('input').forEach(input => { input.disabled = advanced.hidden; });
    };
    mode.addEventListener('change', updateMode); updateMode();
    const error = QP.el('p', 'inline-help'); error.setAttribute('role', 'alert'); form.appendChild(error);
    const toolbar = QP.el('div', 'toolbar space-top');
    let saving = false;
    const save = QP.button('Guardar', 'primary'); save.type = 'submit';
    const cancel = QP.button('Cancelar', 'ghost', () => dialog.close()); cancel.type = 'button';
    form.addEventListener('submit', async e => {
      e.preventDefault(); if (saving) return; saving = true; save.disabled = true; cancel.disabled = true; error.textContent = '';
      try {
        const next = { ...policy, certHash: identity, mode: mode.value };
        for (const [key, input] of fields) next[key] = input.type === 'checkbox' ? input.checked :
          Array.isArray(policy[key]) ? input.value.split(/[\n,]+/).map(s => s.trim()).filter(Boolean) : input.value.trim();
        next.autoConnect = connection.value === 'maintain' || connection.value === 'outgoing';
        next.autoAccept = connection.value === 'maintain' || connection.value === 'receive';
        // A rejected save must not append a duplicate on the next attempt.
        const updated = JSON.parse(JSON.stringify(configuration));
        updated.peers = updated.peers.filter(p => vpnIdentity(p.certHash) !== identity); updated.peers.push(next);
        if (next.enabled && !next.paused && (next.autoConnect || next.autoAccept)) {
          updated.enabled = true;
        }
        const result = await QP.api('/api/vpn/config', { method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ configuration: updated, expectedRevision: configuration.revision }) });
        if (result?.success === false) throw new Error(result.error || 'No se pudo guardar la configuración.');
        dialog.close();
        QP.toast(updated.enabled && vpnStatus?.lan?.requiresAdmin && !vpnStatus.lan.canStartWithoutElevation
          ? 'Configuración guardada. Se conectará automáticamente cuando la aplicación tenga permisos.' : 'Configuración VPN guardada', 'ok');
        await refreshVpn();
      } catch (e) { error.textContent = e.message || 'No se pudo guardar la configuración.'; }
      finally { saving = false; save.disabled = false; cancel.disabled = false; }
    });
    toolbar.append(save, cancel); form.appendChild(toolbar); dialog.appendChild(form); document.body.appendChild(dialog);
    const previous = document.activeElement;
    dialog.addEventListener('cancel', e => { if (saving) e.preventDefault(); });
    dialog.addEventListener('close', () => { dialog.remove(); if (previous?.isConnected) previous.focus(); }, { once: true });
    dialog.showModal(); connection.focus();
  } catch (e) { dialog?.remove(); QP.toast(e.message || 'No se pudo abrir la configuración VPN.', 'error'); }
}

async function restartAsAdmin() {
  const btns = document.querySelectorAll('#restartAdminBtn, .btn-restart-admin');
  btns.forEach(btn => {
    btn.disabled = true;
    btn.dataset.originalText = btn.textContent;
    btn.textContent = 'Solicitando elevación...';
  });

  QP.toast('Solicitando permisos de administrador...', 'warn');
  try {
    const res = await QP.api('/api/restart-as-admin', { method: 'POST' });
    if (res && res.success) {
      btns.forEach(btn => { btn.textContent = 'Reiniciando...'; });
      QP.toast('Reiniciando aplicación con permisos de Administrador...', 'ok');
      setTimeout(() => {
        window.location.reload();
      }, 2500);
    } else {
      btns.forEach(btn => {
        btn.disabled = false;
        btn.textContent = btn.dataset.originalText || 'Reiniciar como Administrador';
      });
      QP.toast('No se pudo elevar los permisos.', 'error');
    }
  } catch (e) {
    btns.forEach(btn => {
      btn.disabled = false;
      btn.textContent = btn.dataset.originalText || 'Reiniciar como Administrador';
    });
    QP.toast(e.message || 'Error al solicitar elevación', 'error');
  }
}
})();
