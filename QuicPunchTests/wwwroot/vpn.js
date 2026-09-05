let vpnStatus = null, settingsDirty = false;

document.addEventListener('DOMContentLoaded', () => {
  document.getElementById('applyLanBtn').addEventListener('click', applyLan);
  document.getElementById('connectAllBtn').addEventListener('click', connectAll);
  document.getElementById('autoAssign').addEventListener('change', () => {
    settingsDirty = true;
    updateManualFields();
  });
  
  const restartBtn = document.getElementById('restartAdminBtn');
  if (restartBtn) restartBtn.addEventListener('click', restartAsAdmin);

  ['lanEnabled', 'lanIp', 'lanMask', 'lanMtu'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', () => settingsDirty = true);
  });

  refreshVpn();
  setInterval(refreshVpn, 1200);
});

async function refreshVpn() {
  try {
    vpnStatus = await QP.api('/api/status');
    renderVpn();
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

function renderVpn() {
  if (!vpnStatus || !vpnStatus.lan) return;
  const lan = vpnStatus.lan;

  // Check admin requirement
  const needsAdmin = lan.requiresAdmin ||
    (lan.adapterStatus && lan.adapterStatus.toLowerCase().includes('administrator')) ||
    (lan.lastError && lan.lastError.toLowerCase().includes('administrator'));

  const banner = document.getElementById('adminRequiredBanner');
  if (banner) {
    if (needsAdmin) banner.classList.remove('hidden');
    else banner.classList.add('hidden');
  }

  document.getElementById('connectAllBtn').disabled = !lan.active;
  QP.setText('virtualIp', lan.localIp || '—');
  QP.setText('addressMode', `${lan.autoAssign ? 'Automatic' : 'Manual'} · ${lan.subnetMask || '255.255.255.0'}`);
  QP.setText('lanPeerCount', (lan.activePeers || []).length);
  QP.setText('lanTraffic', QP.fmtBytes((lan.totalRxBytes || 0) + (lan.totalTxBytes || 0)));
  QP.setText('lanStatusBadge', lan.active ? 'Active' : (lan.adapterStatus || 'Stopped'));
  QP.setText('lanError', lan.lastError || '');

  if (!settingsDirty) {
    document.getElementById('lanEnabled').checked = !!lan.desiredEnabled;
    document.getElementById('autoAssign').checked = !!lan.autoAssign;
    document.getElementById('lanIp').value = lan.localIp || '';
    document.getElementById('lanMask').value = lan.subnetMask || '255.255.255.0';
    document.getElementById('lanMtu').value = lan.mtu || 1500;
    updateManualFields();
  }

  renderPeers();
  renderSessions();
}

function updateManualFields() {
  const auto = document.getElementById('autoAssign').checked;
  document.getElementById('lanIp').disabled = auto;
  document.getElementById('lanMask').disabled = auto;
}

function renderPeers() {
  const box = document.getElementById('vpnPeers');
  QP.clear(box);
  const peers = QP.trusted(vpnStatus);
  if (!peers.length) {
    box.appendChild(QP.el('div', 'empty', 'No trusted peers available.'));
    return;
  }
  peers.forEach(peer => {
    const active = (vpnStatus.lan.activePeers || []).some(p => p.peerId === peer.id);
    const row = QP.el('div', 'peer-row');
    row.appendChild(QP.el('div', 'avatar', QP.initials(peer.name)));
    const copy = QP.el('div', 'peer-copy');
    copy.append(
      QP.el('div', 'peer-name', peer.name),
      QP.el('div', 'peer-meta', active ? 'Joined to LAN' : QP.peerMeta(peer))
    );
    const actions = QP.el('div', 'row-actions');
    const join = QP.button(
      active ? 'Joined' : (vpnStatus.lan.active ? 'Join LAN' : 'Enable LAN'),
      active ? 'green' : 'accent',
      () => connectLan(peer.id)
    );
    join.disabled = active || !vpnStatus.lan.active;
    actions.append(join);
    row.append(copy, actions);
    box.appendChild(row);
  });
}

function renderSessions() {
  const body = document.getElementById('lanSessions');
  QP.clear(body);
  const items = vpnStatus.lan.activePeers || [];
  if (!items.length) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.colSpan = 5;
    td.textContent = 'No active LAN sessions.';
    td.className = 'muted';
    tr.appendChild(td);
    body.appendChild(tr);
    return;
  }
  items.forEach(item => {
    const tr = document.createElement('tr');
    [
      item.peerName,
      item.virtualIp,
      item.connectedAt,
      QP.fmtBytes(item.rxBytes),
      QP.fmtBytes(item.txBytes)
    ].forEach(v => {
      const td = document.createElement('td');
      td.textContent = v;
      tr.appendChild(td);
    });
    body.appendChild(tr);
  });
}

async function applyLan() {
  const payload = {
    enabled: document.getElementById('lanEnabled').checked,
    autoAssign: document.getElementById('autoAssign').checked,
    ip: document.getElementById('lanIp').value.trim(),
    subnetMask: document.getElementById('lanMask').value.trim(),
    mtu: Number(document.getElementById('lanMtu').value)
  };
  try {
    await QP.api('/api/lan-settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    settingsDirty = false;
    QP.toast('LAN settings saved', 'success');
    refreshVpn();
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

async function connectLan(peerId) {
  try {
    await QP.api('/api/connect-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, protocol: 'lan' })
    });
    QP.toast('Joining peer to LAN…');
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

async function connectAll() {
  for (const peer of QP.trusted(vpnStatus)) {
    if (!(vpnStatus.lan.activePeers || []).some(p => p.peerId === peer.id)) {
      try {
        await QP.api('/api/connect-peer', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ peerId: peer.id, protocol: 'lan' })
        });
      } catch { }
    }
  }
  QP.toast('LAN connection attempts started');
}

async function restartAsAdmin() {
  const btn = document.getElementById('restartAdminBtn');
  if (btn) {
    btn.disabled = true;
    btn.textContent = '⏳ Solicitando permisos (UAC)...';
  }
  QP.toast('Solicitando permisos de administrador en el sistema...', 'warn');
  try {
    const res = await QP.api('/api/restart-as-admin', { method: 'POST' });
    if (res && res.success) {
      if (btn) btn.textContent = 'Reiniciando como Administrador...';
      QP.toast('Reiniciando aplicación con permisos de Administrador...', 'success');
      setTimeout(() => {
        window.location.reload();
      }, 2500);
    } else {
      if (btn) {
        btn.disabled = false;
        btn.textContent = 'Reiniciar con permisos de administrador';
      }
      QP.toast('No se pudo elevar los permisos. Intenta ejecutar la app como Administrador manualmente.', 'error');
    }
  } catch (e) {
    if (btn) {
      btn.disabled = false;
      btn.textContent = 'Reiniciar con permisos de administrador';
    }
    QP.toast(e.message || 'Error al solicitar elevación', 'error');
  }
}
