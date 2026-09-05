let dashboardStatus = null;
let dashboardBusy = false;
let savedPeerEditingHash = null;
let logsClearedAt = 0;
let currentStunData = { exactMappings: [], publicAddresses: [], minPort: 0, maxPort: 0, serverCount: 0 };

const APP_DEFS = [
  { key: 'chat', label: 'Direct Chat', href: '/chat.html' },
  { key: 'voice', label: 'Voice Studio', href: '/call.html' },
  { key: 'lan', label: 'LAN Bridge', href: '/vpn.html' },
  { key: 'files', label: 'RelayDrive', href: '/files.html' }
];

const NETWORK_TYPES = ['Static', 'DynamicPort', 'DynamicAddress', 'DynamicPortAndAddress', 'Tor'];

document.addEventListener('DOMContentLoaded', () => {
  byId('copyWanInline').addEventListener('click', () => dashboardStatus && QP.copy(fullToken(dashboardStatus.node?.wanToken)));
  byId('copyQuickLinkBtn').addEventListener('click', () => dashboardStatus && QP.copy(dashboardStatus.node?.quickUri || dashboardStatus.quickUri || ''));
  byId('copyTorInline').addEventListener('click', () => dashboardStatus && QP.copy(fullToken(dashboardStatus.node?.torToken)));
  byId('connectTokenBtn').addEventListener('click', connectToken);
  byId('connectAllDiscoveredBtn')?.addEventListener('click', connectAllDiscovered);
  byId('saveAllBtn').addEventListener('click', saveAllPeers);
  byId('discoveryToggle').addEventListener('change', e => setDiscovery(e.target.checked, 'wan'));
  byId('torDiscoveryToggle')?.addEventListener('change', e => setDiscovery(e.target.checked, 'tor'));
  byId('wanToggle')?.addEventListener('change', e => setWan(e.target.checked));
  byId('torToggle').addEventListener('change', e => setTor(e.target.checked));
  byId('autoAcceptToggle').addEventListener('change', e => setAutoAccept(e.target.checked));
  byId('torNewNymBtn').addEventListener('click', newTorCircuits);
  byId('applyPortBtn').addEventListener('click', applyListenerPort);
  byId('tokenInput').addEventListener('keydown', e => { if (e.key === 'Enter') connectToken(); });
  byId('refreshBtn').addEventListener('click', refreshDashboard);
  byId('clearLogsBtn').addEventListener('click', clearLogsView);
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
  byId('closeTokenConflictBtn')?.addEventListener('click', closeTokenConflictModal);
  byId('conflictCancelBtn')?.addEventListener('click', closeTokenConflictModal);
  byId('conflictConnectOnlyBtn')?.addEventListener('click', () => submitTokenConflict('connect_only'));
  byId('conflictUpdateConnectBtn')?.addEventListener('click', () => submitTokenConflict('update_and_connect'));
  byId('tokenConflictModal')?.addEventListener('click', e => { if (e.target === byId('tokenConflictModal')) closeTokenConflictModal(); });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      if (!byId('savedPeerModal').classList.contains('hidden')) closeSavedPeerEditor();
      if (!byId('stunModal').classList.contains('hidden')) closeStunModal();
      if (byId('tokenConflictModal') && !byId('tokenConflictModal').classList.contains('hidden')) closeTokenConflictModal();
    }
  });
  refreshDashboard();
  setInterval(refreshDashboard, 1300);
});

function byId(id) { return document.getElementById(id); }
function fullToken(raw) { const value = String(raw || '').trim(); return !value ? '' : (value.toUpperCase().startsWith('QP://') ? value : `QP://${value}`); }
function td(text, className = '') { const node = document.createElement('td'); if (className) node.className = className; node.textContent = text ?? ''; return node; }
function emptyRow(body, cols, text) { const tr = document.createElement('tr'); const cell = td(text, 'empty-cell'); cell.colSpan = cols; tr.appendChild(cell); body.appendChild(tr); }
function formatPortRange(minPort, maxPort) { const min = Number(minPort || 0), max = Number(maxPort || 0); if (!min) return '—'; return !max || min === max ? String(min) : `${min} – ${max}`; }
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
    dashboardStatus = await QP.api('/api/status');
    renderDashboard(dashboardStatus);
  } catch (e) { QP.toast(e.message); }
  finally { dashboardBusy = false; }
}

function renderDashboard(s) {
  const peers = s.peers || [];
  const connected = peers.filter(p => p.isTrusted);
  const untrusted = peers.filter(p => !p.isTrusted);

  QP.setText('nodeName', s.node?.name || 'Local node');
  QP.setText('nodeNetwork', s.node?.networkType || 'Unknown');
  byId('listenerPortInput').value = String(s.node?.listenerPort || '');
  QP.setText('wanMeta', wanStatusText(s));
  QP.setText('discoveryMeta', discoveryStatusText(s.discovery?.wan || s.discovery));
  QP.setText('torDiscoveryMeta', discoveryStatusText(s.discovery?.tor));
  QP.setText('torMeta', torStatusText(s));
  QP.setText('wanTokenBox', fullToken(s.node?.wanToken) || (s.wan?.isStarted === false ? 'WAN service inactive' : 'WAN token unavailable'));
  QP.setText('torTokenBox', fullToken(s.node?.torToken) || (s.tor?.isStarted === false ? 'Tor service inactive' : 'Tor token unavailable'));
  byId('copyWanInline').disabled = !s.node?.wanToken || s.wan?.isStarted === false;
  byId('copyQuickLinkBtn').disabled = !s.node?.wanToken || s.wan?.isStarted === false;
  byId('copyTorInline').disabled = !s.node?.torToken || s.tor?.isStarted === false;
  byId('torNewNymBtn').disabled = !s.tor?.isStarted;
  if (byId('wanToggle')) byId('wanToggle').checked = !!(s.wan?.desiredEnabled ?? s.wan?.isStarted ?? true);
  byId('discoveryToggle').checked = !!(s.discovery?.wan?.desiredEnabled ?? s.discovery?.desiredEnabled);
  if (byId('torDiscoveryToggle')) byId('torDiscoveryToggle').checked = !!s.discovery?.tor?.desiredEnabled;
  byId('torToggle').checked = !!s.tor?.desiredEnabled;
  byId('autoAcceptToggle').checked = !!s.autoAcceptAll;

  renderStun(s.stun || {}, s.node || {});

  const badge = byId('nodeBadge'); QP.clear(badge);
  badge.append(QP.el('span', `status-dot ${s.node?.listenerPort ? 'on' : ''}`), document.createTextNode(s.node?.name || 'Local node'));

  renderDiscovered(s.discoveredPeers || [], s);
  renderInterrogations(s.activeInterrogations || []);
  renderConnected(connected, s);
  renderUntrusted(untrusted);
  renderSaved(s.savedPeers || [], peers);
  renderActiveApps(s);
  renderQuicTelemetry(s);
  renderRequests(s.pendingPetitions || []);
  renderLogs(s.logs || []);
}

function wanStatusText(s) {
  if (s.wan?.isStarted) return `Running · port ${s.node?.listenerPort || s.wan?.port || '—'}`;
  if (s.wan?.lastError) return `Failed · ${s.wan.lastError}`;
  if (s.wan?.desiredEnabled) return 'Starting…';
  return 'Stopped';
}

function discoveryStatusText(d) {
  if (d?.running) return `Running · ${d.connectedRelays}/${d.relayCount} relays`;
  if (d?.desiredEnabled) return 'Starting…';
  return 'Stopped';
}

function torStatusText(s) {
  if (s.tor?.isStarted) return `Running · SOCKS ${s.tor.socksPort || '—'} · virtual ${s.tor.virtualPort || '—'}`;
  if (s.tor?.lastError) return `Failed · ${s.tor.lastError}`;
  if (s.tor?.desiredEnabled) {
    const status = s.tor.bootstrapStatus || '';
    const isGeneric = !status || status === 'Bootstrapping...' || status === 'Not initialized';
    if (s.tor.bootstrapProgress > 0) {
      if (!isGeneric) {
        if (status.startsWith('Bootstrapped ')) {
          return `Starting · ${status}`;
        }
        return `Starting · ${s.tor.bootstrapProgress}% · ${status}`;
      }
      return `Starting · ${s.tor.bootstrapProgress}%`;
    }
    if (!isGeneric) {
      return `Starting · ${status}`;
    }
    return 'Starting…';
  }
  return 'Stopped';
}

function renderStun(stun, node) {
  const publicAddresses = (stun.publicAddresses || []).filter(Boolean);
  const exact = stun.exactMappings || [];
  currentStunData = {
    exactMappings: exact,
    publicAddresses: publicAddresses,
    minPort: stun.minPort ?? node.minPort,
    maxPort: stun.maxPort ?? node.maxPort,
    serverCount: stun.serverCount ?? 0
  };

  QP.setText('stunAddresses', publicAddresses.length ? publicAddresses.join(', ') : 'No exact public address observed yet');
  QP.setText('stunPorts', stun.portDisplay || formatPortRange(currentStunData.minPort, currentStunData.maxPort));

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
    emptyRow(body, 6, isDiscoveryOn ? 'No rendezvous peers discovered via Nostr yet. Keep discovery enabled to receive peer announcements.' : 'Nostr discovery is currently stopped. Turn on Nostr discovery in Local Node settings.');
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
      const verifiedBadge = QP.el('span', 'badge green', 'Cert Verified');
      verifiedBadge.title = 'Cryptographically verified: The Nostr announcement carries the certificate public key, matching the token hash and validating the token signature.';
      tags.appendChild(verifiedBadge);
    }
    if (item.isSaved) tags.appendChild(QP.el('span', 'badge green', 'Saved Contact'));
    if (item.isSaved && item.endpointChanged) tags.appendChild(QP.el('span', 'badge yellow', 'New IP / Endpoint'));
    
    pcell.append(name);
    if (item.isSaved && item.discoveredName && item.discoveredName !== item.name) {
      pcell.append(QP.el('div', 'peer-sub', `Discovered as "${item.discoveredName}"`));
    }
    pcell.append(QP.el('div', 'peer-sub mono', item.canonicalId || QP.canonicalId(item.certHash || item.id)), tags);
    tr.appendChild(pcell);

    const typeCell = document.createElement('td');
    typeCell.appendChild(QP.el('div', 'mono', item.networkType || 'Static'));
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

    tr.appendChild(td(formatPortRange(item.minPort, item.maxPort), 'mono'));

    const timeCell = document.createElement('td');
    const secAgo = Number(item.lastSeenSecondsAgo || 0);
    const timeStr = secAgo < 60 ? `${secAgo}s ago` : `${Math.floor(secAgo / 60)}m ago`;
    timeCell.appendChild(QP.el('div', '', timeStr));
    timeCell.appendChild(QP.el('div', 'peer-sub', item.discoveredAt || ''));
    tr.appendChild(timeCell);

    const actionsCell = document.createElement('td');
    const actions = QP.el('div', 'mini-actions');
    actions.append(
      QP.button('Connect', 'accent', () => connectDiscoveredPeer(item, false))
    );
    if (item.isSaved && item.endpointChanged) {
      actions.appendChild(QP.button('Update & Connect', 'green', () => connectDiscoveredPeer(item, true)));
    }
    actions.append(
      QP.button('Dismiss', 'ghost', () => dismissDiscoveredPeer(item.certHash))
    );
    if (item.token) {
      actions.appendChild(QP.button('Copy token', 'ghost', () => { QP.copy(fullToken(item.token)); QP.toast('Peer token copied'); }));
    }
    actionsCell.appendChild(actions);
    tr.appendChild(actionsCell);

    body.appendChild(tr);
  });
}

async function connectDiscoveredPeer(item, updateSaved = false) {
  try {
    await QP.api('/api/connect-discovered-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: item.token, certHash: item.certHash, updateSavedPeer: updateSaved })
    });
    QP.toast(updateSaved ? `Saved peer updated & connecting to ${item.name || 'peer'}…` : `Connecting to ${item.name || 'discovered peer'}…`);
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
    QP.toast('Discovered peer dismissed');
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
  if (!items.length) { emptyRow(body, 5, 'No active connection attempts.'); return; }
  items.forEach(item => {
    const tr = document.createElement('tr');
    const peer = td(item.peerName || 'Unknown');
    peer.appendChild(QP.el('div', 'peer-sub mono', item.canonicalId || QP.canonicalId(item.certHash || item.peerId || item.id)));
    tr.append(peer, td((item.addresses || []).join(', ') || 'Unknown', 'mono wrap'), td(formatPortRange(item.minPort, item.maxPort), 'mono'), td(item.startTime || '—'));
    const actions = document.createElement('td'); actions.appendChild(QP.button('Cancel', 'danger', () => cancelInterrogation(item.sessionId || item.id))); tr.appendChild(actions);
    body.appendChild(tr);
  });
}

function renderConnected(peers, s) {
  const body = byId('connectedPeers'); QP.clear(body);
  if (!peers.length) { emptyRow(body, 7, 'No trusted peers are currently available.'); return; }
  peers.forEach(peer => {
    const tr = document.createElement('tr');
    const pcell = document.createElement('td');
    const name = QP.el('div', 'peer-primary', peer.name || 'Peer');
    const tags = QP.el('div', 'peer-inline-tags');
    if (peer.isSaved) tags.appendChild(QP.el('span', 'badge green', 'Saved'));
    if (peer.isAutoAccepted) tags.appendChild(QP.el('span', 'badge blue', 'Auto'));
    if (peer.hasCipher) tags.appendChild(QP.el('span', 'badge green', 'Encrypted'));
    pcell.append(name, QP.el('div', 'peer-sub mono', peer.canonicalId || QP.canonicalId(peer.certHash || peer.id)), tags);
    tr.appendChild(pcell);

    const transport = document.createElement('td');
    const transportName = peer.isTor || peer.activeTransport === 'tor' ? 'Tor' : (peer.activeTransport === 'wan' ? 'WAN' : (peer.networkType || 'WAN'));
    transport.append(QP.el('div', '', transportName), QP.el('div', 'peer-sub mono', peer.activeEndPoint || QP.peerMeta(peer)));
    if (peer.telemetry?.congestionAlgorithm) {
      const isBbr = String(peer.telemetry.congestionAlgorithm).toUpperCase() === 'BBR';
      transport.appendChild(QP.el('span', `badge ${isBbr ? 'badge-cc-bbr' : 'badge-cc-cubic'} space-top-xs`, peer.telemetry.congestionAlgorithm));
    }

    const pingCell = document.createElement('td');
    const unresponsive = Number(peer.unresponsiveSeconds ?? peer.lastSeenSecondsAgo ?? 99999);
    const pingVal = peer.telemetry?.rttMs ?? (peer.ping !== null && peer.ping !== undefined ? Number(peer.ping) : null);
    if (unresponsive > 10) {
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
    else sessionCell.appendChild(QP.el('span', 'muted', 'No app session'));
    tr.appendChild(sessionCell);

    const trustCell = document.createElement('td');
    const trustActions = QP.el('div', 'mini-actions');
    trustActions.append(
      QP.button(peer.isSaved ? 'Unsave' : 'Save', peer.isSaved ? '' : 'green', () => savePeer(peer.id, !peer.isSaved)),
      QP.button(peer.isAutoAccepted ? 'Auto on' : 'Auto off', '', () => autoPeer(peer.id, !peer.isAutoAccepted))
    );
    if (!peer.isSaved) trustActions.appendChild(QP.button('Untrust', 'ghost', () => trustPeer(peer.id, false)));
    trustCell.appendChild(trustActions); tr.appendChild(trustCell);

    const appCell = document.createElement('td');
    const appWrap = QP.el('div', 'protocol-launcher');
    const select = document.createElement('select'); select.className = 'select compact-select'; select.dataset.peerId = peer.id;
    availableProtocols(s).forEach(app => { const o = document.createElement('option'); o.value = app.protocolId || app.key; o.dataset.key = app.key; o.textContent = app.label; select.appendChild(o); });
    const open = QP.button('Open', 'accent', () => launchSelectedApp(peer, select));
    appWrap.append(select, open); appCell.appendChild(appWrap); tr.appendChild(appCell);

    const finalCell = document.createElement('td'); finalCell.appendChild(QP.button('Disconnect', 'ghost', () => disconnectPeer(peer.id))); tr.appendChild(finalCell);
    body.appendChild(tr);
  });
}

function availableProtocols(s) {
  const registered = (s.registeredProtocols || []).filter(p => !/clipboard|clipbridge/i.test(p.name || ''));
  const fixed = APP_DEFS.map(app => {
    const match = registered.find(p => normalizeProtocolName(p.name) === app.key);
    return { ...app, protocolId: match?.id || app.key };
  });
  registered.forEach(p => {
    const key = normalizeProtocolName(p.name);
    if (!fixed.some(x => x.protocolId === p.id || x.key === key) && key !== 'clipboard') fixed.push({ key, label: p.name || 'Application', href: '', protocolId: p.id });
  });
  return fixed;
}

function normalizeProtocolName(name) {
  const n = String(name || '').toLowerCase();
  if (n.includes('chat')) return 'chat';
  if (n.includes('voice') || n.includes('call')) return 'voice';
  if (n.includes('lan') || n.includes('vpn')) return 'lan';
  if (n.includes('relay') || n.includes('file') || n.includes('drive')) return 'files';
  if (n.includes('clip')) return 'clipboard';
  return n.replace(/[^a-z0-9]+/g, '-') || 'protocol';
}

function sessionLabels(peerId, s) {
  const labels = [];
  if ((s.activeChats || []).some(x => x.peerId === peerId)) labels.push('Chat');
  if ((s.activeVoiceCalls || []).some(x => x.peerId === peerId)) labels.push('Voice');
  if ((s.lan?.activePeers || []).some(x => x.peerId === peerId)) labels.push('LAN');
  if ((s.activeRelayDriveSessions || []).some(x => x.peerId === peerId)) labels.push('Files');
  return labels;
}

function renderUntrusted(peers) {
  const body = byId('untrustedPeers'); QP.clear(body);
  if (!peers.length) { emptyRow(body, 6, 'No untrusted peers are waiting for review.'); return; }
  peers.forEach(peer => {
    const tr = document.createElement('tr');
    const p = td(peer.name || 'Peer'); p.appendChild(QP.el('div', 'peer-sub mono', peer.canonicalId || QP.canonicalId(peer.certHash || peer.id))); tr.appendChild(p);
    tr.append(td(peer.isTor ? 'Tor' : (peer.activeTransport || peer.networkType || 'WAN')), td(peer.activeEndPoint || QP.peerMeta(peer), 'mono wrap'), td(peer.lastSeenFormatted || `${peer.lastSeenSecondsAgo || 0}s ago`), td(peer.canonicalId || (peer.certHash ? QP.canonicalId(peer.certHash) : '—'), 'mono'));
    const actions = document.createElement('td'); const wrap = QP.el('div', 'mini-actions'); wrap.append(QP.button('Trust', 'green', () => trustPeer(peer.id, true)), QP.button('Disconnect', 'ghost', () => disconnectPeer(peer.id))); actions.appendChild(wrap); tr.appendChild(actions);
    body.appendChild(tr);
  });
}

function renderSaved(saved, peers) {
  const body = byId('savedPeersBody'); QP.clear(body);
  if (!saved.length) { emptyRow(body, 7, 'No peers are stored in the local database.'); return; }
  saved.forEach(item => {
    const live = peers.find(p => p.certHash && p.certHash === item.certHash);
    const tr = document.createElement('tr');
    const nameCell = td(item.name || 'Saved peer'); if (live) nameCell.appendChild(QP.el('div', 'peer-sub', live.hasCipher ? 'Authenticated session' : 'Peer available')); tr.appendChild(nameCell);
    const certCell = td(item.canonicalId || QP.canonicalId(item.certHash), 'mono'); certCell.appendChild(QP.el('div', 'peer-sub', item.networkType || 'Static')); tr.appendChild(certCell);
    tr.appendChild(td(item.onionAddress || (item.addresses || []).join(', ') || '—', 'mono wrap'));
    tr.appendChild(td(formatPortRange(item.minPort, item.maxPort), 'mono'));

    const autoCell = document.createElement('td');
    const autoLabel = document.createElement('label'); autoLabel.className = 'check-line compact-check';
    const autoInput = document.createElement('input'); autoInput.type = 'checkbox'; autoInput.checked = !!item.autoConnect; autoInput.addEventListener('change', () => toggleSavedAutoConnect(item.certHash, autoInput.checked));
    autoLabel.append(autoInput, document.createTextNode('Enabled')); autoCell.appendChild(autoLabel); tr.appendChild(autoCell);

    const statusCell = document.createElement('td'); statusCell.appendChild(QP.el('span', `badge ${live ? 'green' : ''}`, live ? 'Connected' : 'Stored offline')); tr.appendChild(statusCell);

    const actionsCell = document.createElement('td');
    const actions = QP.el('div', 'mini-actions');
    actions.append(
      QP.button('Reconnect', 'accent', () => reconnectSavedPeer(item.certHash)),
      QP.button('Edit', '', () => openSavedPeerEditor(item)),
      QP.button('Delete', 'danger', () => deleteSavedPeer(item))
    );
    actionsCell.appendChild(actions); tr.appendChild(actionsCell);
    body.appendChild(tr);
  });
}

function renderActiveApps(s) {
  const body = byId('activeAppsBody'); QP.clear(body);
  const rows = [];
  (s.activeChats || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'Direct Chat', detail:'Reliable chat session', href:`/chat.html?peer=${encodeURIComponent(x.peerId)}` }));
  (s.activeVoiceCalls || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'Voice Studio', detail:'Live voice call', href:'/call.html' }));
  (s.lan?.activePeers || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'LAN Bridge', detail:`Virtual IP ${x.virtualIp || '—'}`, href:'/vpn.html' }));
  (s.activeRelayDriveSessions || []).forEach(x => rows.push({ peerId:x.peerId, peerName:x.peerName, app:'RelayDrive', detail:'Mounted virtual shelf', href:'/files.html' }));
  if (!rows.length) { emptyRow(body, 4, 'No application sessions are active.'); return; }
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

function renderRequests(items) {
  const panel = byId('requestsPanel'); const list = byId('requestsList'); QP.clear(list); panel.classList.toggle('hidden', !items.length);
  items.forEach(item => {
    const row = QP.el('div', 'petition-row');
    const copy = QP.el('div', 'peer-copy'); copy.append(QP.el('div', 'peer-primary', item.peerName || 'Peer'), QP.el('div', 'peer-meta', item.protocolName || item.protocolId));
    const actions = QP.el('div', 'row-actions'); actions.append(QP.button('Accept & open', 'green', () => respondPetition(item, true)), QP.button('Decline', 'danger', () => respondPetition(item, false)));
    row.append(copy, actions); list.appendChild(row);
  });
}

function renderLogs(logs) {
  const box = byId('logs'); QP.clear(box);
  const visible = logsClearedAt ? [] : logs;
  if (!visible.length) box.appendChild(QP.el('div','muted', logsClearedAt ? 'Log view cleared. New events will appear after the next refresh.' : 'No events yet.'));
  else visible.slice().reverse().forEach(log => box.appendChild(QP.el('div','log-line',log)));
  if (logsClearedAt && logs.length) logsClearedAt = 0;
}

function clearLogsView() { logsClearedAt = Date.now(); QP.clear(byId('logs')); byId('logs').appendChild(QP.el('div', 'muted', 'Log view cleared.')); }

function openSavedPeerEditor(item = null) {
  savedPeerEditingHash = item?.certHash || null;
  byId('savedPeerModalTitle').textContent = item ? 'Edit Saved Peer' : 'Add Saved Peer';
  byId('savedName').value = item?.name || '';
  byId('savedCertHash').value = item?.certHash || '';
  byId('savedCertHash').readOnly = !!item;
  byId('savedAddresses').value = (item?.addresses || []).join('\n');
  byId('savedOnion').value = item?.onionAddress || '';
  byId('savedMinPort').value = String(item?.minPort || 443);
  byId('savedMaxPort').value = String(item?.maxPort || item?.minPort || 443);
  byId('savedNetworkType').value = NETWORK_TYPES.includes(item?.networkType) ? item.networkType : (item?.onionAddress ? 'Tor' : 'Static');
  byId('savedAutoConnect').checked = item ? !!item.autoConnect : true;
  byId('savedPeerModalError').classList.add('hidden');
  byId('savedPeerModalError').textContent = '';
  byId('savedPeerModal').classList.remove('hidden');
  setTimeout(() => (item ? byId('savedName') : byId('savedCertHash')).focus(), 0);
}

function closeSavedPeerEditor() { byId('savedPeerModal').classList.add('hidden'); savedPeerEditingHash = null; }

async function saveSavedPeerEditor() {
  const certHash = byId('savedCertHash').value.trim();
  const minPort = Number(byId('savedMinPort').value), maxPort = Number(byId('savedMaxPort').value);
  const error = byId('savedPeerModalError');
  if (!certHash) return showSavedError('Certificate hash is required.');
  if (!Number.isInteger(minPort) || !Number.isInteger(maxPort) || minPort < 1 || maxPort > 65535 || minPort > maxPort) return showSavedError('Enter a valid port or port range.');
  const payload = {
    certHash,
    name: byId('savedName').value.trim(),
    addresses: byId('savedAddresses').value,
    onionAddress: byId('savedOnion').value.trim(),
    minPort,
    maxPort,
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

async function reconnectSavedPeer(certHash) { try { await QP.api('/api/connect-saved-peer',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash})}); QP.toast('Reconnect started'); } catch(e){ QP.toast(e.message); } refreshDashboard(); }
async function toggleSavedAutoConnect(certHash,autoConnect) { try { await QP.api('/api/toggle-saved-peer-autoconnect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash,autoConnect})}); } catch(e){ QP.toast(e.message); } refreshDashboard(); }
async function deleteSavedPeer(item) { if (!confirm(`Delete saved peer "${item.name || 'Saved peer'}"? This removes it from the persistent database.`)) return; try { await QP.api('/api/saved-peer-delete',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({certHash:item.certHash})}); QP.toast('Saved peer deleted'); } catch(e){ QP.toast(e.message); } refreshDashboard(); }

async function launchSelectedApp(peer, select) {
  const option = select.options[select.selectedIndex]; if (!option) return;
  const protocolId = option.value; const key = option.dataset.key || '';
  try {
    await QP.api('/api/connect-peer', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({peerId:peer.id, protocolId}) });
    const app = APP_DEFS.find(x => x.key === key); QP.toast(`Opening ${option.textContent}…`);
    if (app?.href) QP.navigate(key, key === 'chat' ? `${app.href}?peer=${encodeURIComponent(peer.id)}&connecting=1` : app.href);
  } catch (e) { QP.toast(e.message, 'error'); }
}

async function setDiscovery(enabled,type='wan'){try{await QP.api('/api/discovery',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled,type})});}catch(e){QP.toast(e.message);}refreshDashboard();}
async function setWan(enabled){try{await QP.api(enabled?'/api/wan-start':'/api/wan-stop',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});QP.toast(enabled?'WAN enabled':'WAN disabled');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function setTor(enabled){try{await QP.api(enabled?'/api/tor-start':'/api/tor-stop',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});QP.toast(enabled?'Tor enabled':'Tor disabled');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function newTorCircuits(){try{await QP.api('/api/tor-newnym',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});QP.toast('Requested new Tor circuits');}catch(e){QP.toast(e.message);}}
async function setAutoAccept(enabled){try{await QP.api('/api/settings/auto-accept',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({autoAcceptAll:enabled})});}catch(e){QP.toast(e.message);}refreshDashboard();}
async function trustPeer(peerId,trust){try{await QP.api('/api/peer/trust',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId,trust})});QP.toast(trust?'Peer trusted':'Trust removed');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function savePeer(peerId,save){try{await QP.api('/api/save-peer',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId,save,autoConnect:true})});QP.toast(save?'Peer saved':'Peer removed from saved list');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function autoPeer(peerId,autoAccept){try{await QP.api('/api/peer/auto-accept',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId,autoAccept})});}catch(e){QP.toast(e.message);}refreshDashboard();}
async function disconnectPeer(peerId){try{await QP.api('/api/disconnect-peer',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId})});}catch(e){QP.toast(e.message);}refreshDashboard();}
async function saveAllPeers(){try{const r=await QP.api('/api/save-all-peers',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});QP.toast(`${r.savedCount} peer(s) saved`);}catch(e){QP.toast(e.message);}refreshDashboard();}
async function cancelInterrogation(id){try{await QP.api('/api/cancel-interrogation',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id})});QP.toast('Connection attempt cancelled');}catch(e){QP.toast(e.message);}refreshDashboard();}
async function applyListenerPort(){const port=Number(byId('listenerPortInput').value);if(!Number.isInteger(port)||port<1||port>65535){QP.toast('Enter a valid port');return;}try{const r=await QP.api('/api/change-listener-port',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({port})});QP.toast(`Listener moved to ${r.port}`);}catch(e){QP.toast(e.message);}refreshDashboard();}

let pendingConflictData = null;

function openTokenConflictModal(res) {
  pendingConflictData = res;
  byId('conflictPeerName').value = res.savedPeer?.name || res.newTokenPeer?.name || 'Peer';
  byId('conflictCertHash').textContent = res.certHash || '—';
  byId('conflictAutoConnect').checked = res.savedPeer?.autoConnect !== false;

  const savedIps = (res.savedPeer?.addresses || []).join(', ') || 'Ninguna';
  const savedPorts = formatPortRange(res.savedPeer?.minPort, res.savedPeer?.maxPort);
  const savedType = res.savedPeer?.networkType || 'Static';
  const savedTor = res.savedPeer?.onionAddress ? `\nTor: ${res.savedPeer.onionAddress}` : '';
  byId('conflictSavedInfo').textContent = `IP(s): ${savedIps}\nPuertos: ${savedPorts}\nTipo de red: ${savedType}${savedTor}`;

  const newIps = (res.newTokenPeer?.addresses || []).join(', ') || 'Ninguna';
  const newPorts = formatPortRange(res.newTokenPeer?.minPort, res.newTokenPeer?.maxPort);
  const newType = res.newTokenPeer?.networkType || 'Static';
  const newTor = res.newTokenPeer?.onionAddress ? `\nTor: ${res.newTokenPeer.onionAddress}` : '';
  byId('conflictNewInfo').textContent = `IP(s): ${newIps}\nPuertos: ${newPorts}\nTipo de red: ${newType}${newTor}`;

  byId('tokenConflictModal').classList.remove('hidden');
}

function closeTokenConflictModal() {
  byId('tokenConflictModal').classList.add('hidden');
  pendingConflictData = null;
}

async function submitTokenConflict(action) {
  if (!pendingConflictData) return;
  const token = pendingConflictData.token;
  const name = byId('conflictPeerName').value.trim();
  const autoConnect = byId('conflictAutoConnect').checked;
  closeTokenConflictModal();
  try {
    await QP.api('/api/connect-token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token, action, name, autoConnect })
    });
    byId('tokenInput').value = '';
    QP.toast(action === 'update_and_connect' ? 'Información del peer actualizada. Conectando…' : 'Conectando con token…');
  } catch (e) {
    QP.toast(e.message);
  }
  refreshDashboard();
}

async function connectToken() {
  const input = byId('tokenInput');
  const token = input.value.trim();
  if (!token) return;
  try {
    const res = await QP.api('/api/connect-token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token, action: 'check' })
    });
    if (res.requiresConfirmation) {
      openTokenConflictModal(res);
      return;
    }
    input.value = '';
    QP.toast('Token aceptado. Conectando…');
  } catch (e) {
    QP.toast(e.message);
  }
  refreshDashboard();
}
async function respondPetition(item,accept){try{await QP.api('/api/respond-petition',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({requestId:item.requestId,accept})});if(accept){const key=normalizeProtocolName(item.protocolName);const app=APP_DEFS.find(x=>x.key===key);if(app?.href)setTimeout(()=>{QP.navigate(key, key==='chat'?`${app.href}?peer=${encodeURIComponent(item.peerId)}`:app.href);},120);}}catch(e){QP.toast(e.message);}refreshDashboard();}

function openStunModal() {
  if (!currentStunData.exactMappings || !currentStunData.exactMappings.length) {
    QP.toast('No STUN responses available yet');
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
  const ports = endpoints.map(e => Number(e.port)).filter(p => !isNaN(p) && p > 0);
  const minP = ports.length ? Math.min(...ports) : (currentStunData.minPort || '—');
  const maxP = ports.length ? Math.max(...ports) : (currentStunData.maxPort || '—');
  const totalHits = endpoints.reduce((sum, e) => sum + e.hits, 0);

  const stat1 = QP.el('span', '', ''); stat1.innerHTML = `<strong>Total responses:</strong> ${totalHits}`;
  const stat2 = QP.el('span', '', ''); stat2.innerHTML = `<strong>Unique endpoints:</strong> ${endpoints.length}`;
  const stat3 = QP.el('span', '', ''); stat3.innerHTML = `<strong>Unique IPs:</strong> ${uniqueIps.length}`;
  const stat4 = QP.el('span', '', ''); stat4.innerHTML = `<strong>Port range:</strong> ${minP === maxP ? minP : `${minP} – ${maxP}`}`;
  statsBox.append(stat1, stat2, stat3, stat4);

  if (!endpoints.length) {
    emptyRow(listBody, 4, 'No exact STUN mappings recorded.');
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
    actionCell.appendChild(QP.button('Copy', 'ghost', () => QP.copy(item.endpoint)));
    tr.appendChild(actionCell);
    listBody.appendChild(tr);
  });
}

function copyAllStunMappings() {
  const exact = currentStunData.exactMappings || [];
  if (!exact.length) {
    QP.toast('No STUN responses to copy');
    return;
  }
  const endpoints = exact.map(item => typeof item === 'string' ? item : (item.endpoint || `${item.address}:${item.port}`));
  QP.copy(endpoints.join('\n'));
}

function renderQuicTelemetry(s) {
  const badge = byId('quicTelemetryStatusBadge');
  const body = byId('quicTelemetryBody');
  if (!body) return;
  QP.clear(body);

  let sessions = s.quicTelemetry || [];
  if (!sessions.length && s.peers) {
    sessions = s.peers.filter(p => p.telemetry).map(p => ({
      peerId: p.id,
      peerName: p.name || 'Peer',
      protocolId: '',
      protocolName: 'Peer Direct',
      transportType: p.isTor ? 'Tor' : (p.activeTransport || 'WAN'),
      telemetry: p.telemetry,
      sampleTime: p.telemetry?.timestamp
    }));
  }

  const activeCount = sessions.length;
  if (badge) {
    QP.clear(badge);
    const dot = QP.el('span', `status-dot ${activeCount > 0 ? 'on' : ''}`);
    badge.append(dot, document.createTextNode(`${activeCount} active connection${activeCount === 1 ? '' : 's'}`));
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
    emptyRow(body, 9, 'No active QUIC protocol sessions available. Connect to a peer or open an application to inspect native telemetry.');
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
  QP.setText('kpiQuicAlgorithm', `Algorithms: ${Array.from(algos).join(', ') || 'BBR / CUBIC'}`);
  QP.setText('kpiQuicThroughput', `Tx: ${formatBytes(totalTxBytes)} · Rx: ${formatBytes(totalRxBytes)}`);
  QP.setText('kpiQuicPackets', `Tx: ${totalTxPackets.toLocaleString()} pkts · Rx: ${totalRxPackets.toLocaleString()} pkts`);

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

    // 3. Congestion Control
    const ccCell = document.createElement('td');
    const algo = t.congestionAlgorithm || 'CUBIC';
    const isBbr = String(algo).toUpperCase() === 'BBR';
    const ccBadge = QP.el('span', `badge ${isBbr ? 'badge-cc-bbr' : 'badge-cc-cubic'}`, algo);
    ccCell.appendChild(ccBadge);
    tr.appendChild(ccCell);

    // 4. RTT (Min / Avg / Max)
    const rttCell = document.createElement('td');
    const rttVal = t.rttMs !== undefined ? `${t.rttMs} ms` : '—';
    rttCell.appendChild(QP.el('div', 'mono strong', rttVal));
    rttCell.appendChild(QP.el('div', 'peer-sub mono', `Min: ${t.minRttMs ?? '—'} · Max: ${t.maxRttMs ?? '—'} ms`));
    tr.appendChild(rttCell);

    // 5. Loss %
    const lossCell = document.createElement('td');
    const lossPct = t.packetLossPercentage !== undefined ? `${t.packetLossPercentage}%` : '0.00%';
    const lossBadge = QP.el('span', `badge ${t.packetLossPercentage > 1 ? 'red' : 'green'}`, lossPct);
    lossCell.appendChild(lossBadge);
    lossCell.appendChild(QP.el('div', 'peer-sub mono', `Lost: ${t.sendSuspectedLostPackets ?? 0} · Retrans: ${t.sendRetransmittablePackets ?? 0}`));
    tr.appendChild(lossCell);

    // 6. CWND / Path MTU
    const cwndCell = document.createElement('td');
    cwndCell.appendChild(QP.el('div', 'mono strong', formatBytes(t.sendCongestionWindow || 0)));
    cwndCell.appendChild(QP.el('div', 'peer-sub mono', `MTU: ${t.pathMtu || '—'} B`));
    tr.appendChild(cwndCell);

    // 7. Wire Transfer (Tx / Rx)
    const wireCell = document.createElement('td');
    wireCell.appendChild(QP.el('div', 'mono', `Tx: ${formatBytes(t.sendTotalBytes || 0)}`));
    wireCell.appendChild(QP.el('div', 'mono text-muted', `Rx: ${formatBytes(t.recvTotalBytes || 0)}`));
    wireCell.appendChild(QP.el('div', 'peer-sub mono', `${t.sendTotalPackets ?? 0} tx · ${t.recvTotalPackets ?? 0} rx pkts`));
    tr.appendChild(wireCell);

    // 8. Security / ALPN
    const secCell = document.createElement('td');
    secCell.appendChild(QP.el('div', 'mono text-xs', t.cipherSuite || 'TLS 1.3'));
    secCell.appendChild(QP.el('div', 'peer-sub mono', `ALPN: ${t.alpn || '—'}`));
    tr.appendChild(secCell);

    // 9. Actions
    const actionCell = document.createElement('td');
    const switchBtn = QP.button(
      isBbr ? 'Set CUBIC' : 'Set BBR',
      isBbr ? '' : 'accent',
      () => switchCongestionControl(item.peerId, item.protocolId, algo)
    );
    actionCell.appendChild(switchBtn);
    tr.appendChild(actionCell);

    body.appendChild(tr);
  });
}

async function switchCongestionControl(peerId, protocolId, currentAlgo) {
  const targetAlgo = (String(currentAlgo || '').toUpperCase() === 'BBR') ? 'cubic' : 'bbr';
  try {
    const res = await QP.api('/api/quic/congestion-control', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, protocolId: protocolId || undefined, algorithm: targetAlgo })
    });
    if (res.success) {
      QP.toast(`Switched congestion control to ${targetAlgo.toUpperCase()}`);
    } else {
      QP.toast(`Failed to switch congestion control to ${targetAlgo.toUpperCase()}`);
    }
  } catch (err) {
    QP.toast(`Congestion control update failed: ${err.message}`);
  }
  refreshDashboard();
}

