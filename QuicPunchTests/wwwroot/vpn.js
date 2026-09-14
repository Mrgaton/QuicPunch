let vpnStatus = null, settingsDirty = false;

document.addEventListener('DOMContentLoaded', () => {
  document.getElementById('applyLanBtn')?.addEventListener('click', applyLan);
  document.getElementById('connectAllBtn')?.addEventListener('click', connectAll);
  
  const setOptimalMtu = () => {
    const el = document.getElementById('lanMtu');
    if (el) {
      el.value = 1420;
      settingsDirty = true;
      QP.toast('MTU ajustado al valor óptimo (1420)');
    }
  };
  document.getElementById('resetMtuBtn')?.addEventListener('click', setOptimalMtu);
  document.getElementById('optimalMtuBadge')?.addEventListener('click', setOptimalMtu);

  document.getElementById('autoAssign')?.addEventListener('change', () => {
    settingsDirty = true;
    updateManualFields();
  });
  
  document.querySelectorAll('#restartAdminBtn, .btn-restart-admin').forEach(btn => {
    btn.addEventListener('click', restartAsAdmin);
  });

  ['lanEnabled', 'lanIp', 'lanMask', 'lanMtu'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', () => settingsDirty = true);
  });

  if (window.QP && typeof QP.on === 'function') {
    QP.on('status_updated', data => {
      vpnStatus = data;
      renderVpn();
    });
    QP.on('view_changed', data => {
      if (data && data.page === 'vpn') {
        if (QP.state && QP.state.status) {
          vpnStatus = QP.state.status;
          renderVpn();
        } else {
          refreshVpn();
        }
      }
    });
  }

  if (window.QP && QP.state && QP.state.status) {
    vpnStatus = QP.state.status;
    renderVpn();
  } else {
    refreshVpn();
  }
});

async function refreshVpn() {
  try {
    vpnStatus = await (QP.getStatus ? QP.getStatus() : QP.api('/api/status'));
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

  const connectAllBtn = document.getElementById('connectAllBtn');
  if (connectAllBtn) connectAllBtn.disabled = !lan.active;

  QP.setText('virtualIp', lan.localIp || '—');
  QP.setText('addressMode', `${lan.autoAssign ? 'Automatic' : 'Manual'} · ${lan.subnetMask || '255.255.255.0'}`);
  QP.setText('lanPeerCount', (lan.activePeers || []).length);
  QP.setText('lanTraffic', QP.fmtBytes((lan.totalRxBytes || 0) + (lan.totalTxBytes || 0)));
  QP.setText('lanStatusBadge', lan.active ? 'Active' : (lan.adapterStatus || 'Stopped'));
  QP.setText('lanError', lan.lastError || '');

  if (!settingsDirty) {
    const lanEnabled = document.getElementById('lanEnabled');
    if (lanEnabled) lanEnabled.checked = !!lan.desiredEnabled;
    const autoAssign = document.getElementById('autoAssign');
    if (autoAssign) autoAssign.checked = !!lan.autoAssign;
    const lanIp = document.getElementById('lanIp');
    if (lanIp) lanIp.value = lan.localIp || '';
    const lanMask = document.getElementById('lanMask');
    if (lanMask) lanMask.value = lan.subnetMask || '255.255.255.0';
    const lanMtu = document.getElementById('lanMtu');
    if (lanMtu) lanMtu.value = (lan.mtu && lan.mtu !== 1500) ? lan.mtu : 1420;
    updateManualFields();
  }

  renderPeers();
  renderSessions();
}

function updateManualFields() {
  const autoEl = document.getElementById('autoAssign');
  if (!autoEl) return;
  const auto = autoEl.checked;
  const lanIp = document.getElementById('lanIp');
  if (lanIp) lanIp.disabled = auto;
  const lanMask = document.getElementById('lanMask');
  if (lanMask) lanMask.disabled = auto;
}

function renderPeers() {
  const box = document.getElementById('vpnPeers');
  if (!box) return;
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
  if (!body) return;
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
  const lanEnabled = document.getElementById('lanEnabled');
  const autoAssign = document.getElementById('autoAssign');
  const lanIp = document.getElementById('lanIp');
  const lanMask = document.getElementById('lanMask');
  const lanMtu = document.getElementById('lanMtu');
  if (!lanEnabled || !autoAssign || !lanIp || !lanMask || !lanMtu) return;

  // 1. Avisar si estás conectado a alguien de que la conexión se va a restablecer
  let activePeers = vpnStatus?.lan?.activePeers || [];
  if (!activePeers.length && QP?.state?.status?.lan?.activePeers) {
    activePeers = QP.state.status.lan.activePeers;
  }
  if (activePeers.length > 0) {
    const peerNames = activePeers.map(p => p.peerName || 'Peer').join(', ');
    const count = activePeers.length;
    const msg = `Actualmente tienes ${count} peer(s) conectado(s) en la LAN virtual (${peerNames}).\n\nAl aplicar la nueva configuración, el adaptador virtual se reiniciará y la conexión con estos peers se va a restablecer.\n\n¿Estás seguro de que deseas continuar?`;
    if (!window.confirm(msg)) {
      return;
    }
  }

  // 2. Establecer automáticamente el MTU al valor óptimo (1420) si no está definido o es inválido/legacy
  let mtu = Number(lanMtu.value);
  if (!mtu || mtu < 1200 || mtu > 9000 || mtu === 1500) {
    mtu = 1420;
    lanMtu.value = 1420;
  }

  const payload = {
    enabled: lanEnabled.checked,
    autoAssign: autoAssign.checked,
    ip: lanIp.value.trim(),
    subnetMask: lanMask.value.trim(),
    mtu: mtu
  };
  try {
    await QP.api('/api/lan-settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    settingsDirty = false;
    QP.toast('Configuración de LAN aplicada correctamente', 'ok');
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
  const btns = document.querySelectorAll('#restartAdminBtn, .btn-restart-admin');
  btns.forEach(btn => {
    btn.disabled = true;
    btn.dataset.originalText = btn.textContent;
    btn.textContent = '⏳ Solicitando elevación...';
  });

  QP.toast('Solicitando permisos de administrador en el sistema...', 'warn');
  try {
    const res = await QP.api('/api/restart-as-admin', { method: 'POST' });
    if (res && res.success) {
      btns.forEach(btn => btn.textContent = 'Reiniciando como Administrador...');
      QP.toast('Reiniciando aplicación con permisos de Administrador...', 'success');
      setTimeout(() => {
        window.location.reload();
      }, 2500);
    } else {
      btns.forEach(btn => {
        btn.disabled = false;
        btn.textContent = btn.dataset.originalText || 'Reiniciar con permisos de administrador';
      });
      QP.toast('No se pudo elevar los permisos. Confirma la elevación en el aviso del sistema o ejecuta la app como administrador.', 'error');
    }
  } catch (e) {
    btns.forEach(btn => {
      btn.disabled = false;
      btn.textContent = btn.dataset.originalText || 'Reiniciar con permisos de administrador';
    });
    QP.toast(e.message || 'Error al solicitar elevación', 'error');
  }
}
