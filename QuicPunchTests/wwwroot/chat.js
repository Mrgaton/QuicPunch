let chatStatus = null;
let selectedPeerId = new URLSearchParams(location.search).get('peer') || '';
let peerFilter = '';
let lastRenderedPeerId = '';
let lastRenderedFingerprint = '';
const connectingPeers = new Map();

function scrollToBottom(smooth = false) {
  const box = document.getElementById('messages');
  if (!box) return;
  requestAnimationFrame(() => {
    if (smooth) {
      box.scrollTo({ top: box.scrollHeight, behavior: 'smooth' });
    } else {
      box.scrollTop = box.scrollHeight;
    }
  });
}

function getMessagesFingerprint(items) {
  if (!items || !items.length) return 'empty';
  const last = items[items.length - 1];
  return `${items.length}:${last.msgId}:${last.isConfirmed ? '1' : '0'}:${last.time}`;
}

document.addEventListener('DOMContentLoaded', () => {
  document.getElementById('chatConnectBtn').addEventListener('click', connectChat);
  document.getElementById('sendBtn').addEventListener('click', () => sendChatMessage());
  
  const msgInput = document.getElementById('messageInput');
  msgInput.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendChatMessage();
    }
  });
  msgInput.addEventListener('input', () => {
    msgInput.style.height = 'auto';
    msgInput.style.height = Math.min(msgInput.scrollHeight, 140) + 'px';
  });

  document.getElementById('attachBtn').addEventListener('click', () => document.getElementById('fileInput').click());
  document.getElementById('fileInput').addEventListener('change', e => sendAttachment(e.target.files?.[0]));
  document.getElementById('chatPeerSearch').addEventListener('input', e => { peerFilter = e.target.value.trim().toLowerCase(); renderChat(); });
  
  if (new URLSearchParams(location.search).get('connecting') === '1' && selectedPeerId) {
    startConnecting(selectedPeerId);
  }

  // Real-time WebSocket subscriptions
  QP.on('chat_msg', onRealtimeMessage);
  QP.on('chat_ack', onRealtimeAck);
  QP.on('chat_sync', () => refreshChat());
  QP.on('peer_connected', () => refreshChat());
  QP.on('peer_disconnected', () => refreshChat());
  QP.on('peer_available', () => refreshChat());
  QP.on('view_changed', data => {
    if (data.page === 'chat') {
      const p = new URLSearchParams(location.search).get('peer');
      if (p && p !== selectedPeerId) {
        selectedPeerId = p;
        if (new URLSearchParams(location.search).get('connecting') === '1') {
          startConnecting(selectedPeerId);
        }
      }
      refreshChat();
      scrollToBottom();
    }
  });

  refreshChat();
  setInterval(() => {
    // If WS is active, poll less aggressively as a safety backup
    refreshChat();
  }, 4000);
  setInterval(tickConnecting, 250);
});

function onRealtimeMessage(msg) {
  if (!msg) return;
  if (!chatStatus) chatStatus = { chatMessages: [] };
  if (!Array.isArray(chatStatus.chatMessages)) chatStatus.chatMessages = [];
  
  const existing = chatStatus.chatMessages.find(m => m.msgId === msg.msgId);
  if (!existing) {
    chatStatus.chatMessages.push(msg);
  } else {
    Object.assign(existing, msg);
  }

  const allPeers = QP.trusted(chatStatus || {});
  const peer = allPeers.find(p => p.id === selectedPeerId);
  if (peer && msg.peerId === peer.id) {
    renderMessages(peer, !!msg.isMe);
  }
}

function onRealtimeAck(ack) {
  if (!ack || !ack.msgId || !chatStatus?.chatMessages) return;
  const target = chatStatus.chatMessages.find(m => m.msgId === ack.msgId);
  if (target) {
    target.isConfirmed = true;
    const allPeers = QP.trusted(chatStatus || {});
    const peer = allPeers.find(p => p.id === selectedPeerId);
    if (peer && target.peerId === peer.id) {
      renderMessages(peer, false);
    }
  }
}

async function refreshChat() {
  try {
    chatStatus = await QP.api('/api/status?include=chat');
    renderChat();
  } catch(e) {
    QP.toast(e.message, 'error');
  }
}

function startConnecting(peerId, timeoutSeconds = 25, peerName = '') {
  if (!peerName && chatStatus) {
    const p = QP.trusted(chatStatus).find(x => x.id === peerId);
    peerName = p?.name || 'Peer';
  }
  connectingPeers.set(peerId, { expireAt: Date.now() + timeoutSeconds * 1000, timeoutSeconds, peerName: peerName || 'Peer' });
  renderChat();
}

function tickConnecting() {
  if (!connectingPeers.size) return;
  let needsRender = false;
  for (const [peerId, info] of Array.from(connectingPeers.entries())) {
    const isConnected = !!chatStatus && (chatStatus.activeChats || []).some(c => c.peerId === peerId);
    if (isConnected) {
      connectingPeers.delete(peerId);
      QP.toast(`Chat connected with ${info.peerName}`, 'success');
      needsRender = true;
      continue;
    }
    const remainingSec = Math.max(0, Math.ceil((info.expireAt - Date.now()) / 1000));
    if (remainingSec <= 0) {
      connectingPeers.delete(peerId);
      QP.toast(`Could not connect to ${info.peerName}: connection timed out (peer may be offline or unreachable).`, 'error');
      needsRender = true;
      continue;
    }
    if (peerId === selectedPeerId) {
      const btn = document.getElementById('chatConnectBtn');
      if (btn) {
        btn.textContent = `Connecting… (${remainingSec}s)`;
        btn.className = 'btn connecting';
        btn.disabled = true;
      }
    }
  }
  if (needsRender) renderChat();
}

function renderChat() {
  const allPeers = QP.trusted(chatStatus || {});
  if (selectedPeerId && !allPeers.some(p => p.id === selectedPeerId)) selectedPeerId = '';
  if (!selectedPeerId && allPeers.length) selectedPeerId = allPeers[0].id;
  const peers = allPeers.filter(p => !peerFilter || `${p.name} ${p.endpoint} ${p.id}`.toLowerCase().includes(peerFilter));
  const list = document.getElementById('chatPeers'); QP.clear(list);
  if (!allPeers.length) list.appendChild(QP.el('div','empty','Trust a peer from Network first.'));
  else if (!peers.length) list.appendChild(QP.el('div','empty','No peers match your search.'));
  peers.forEach(peer => {
    const active = (chatStatus?.activeChats || []).some(c => c.peerId === peer.id);
    const isConnecting = connectingPeers.has(peer.id);
    const remaining = isConnecting ? Math.max(0, Math.ceil((connectingPeers.get(peer.id).expireAt - Date.now()) / 1000)) : 0;
    const row = QP.el('button', `dm-peer ${peer.id === selectedPeerId ? 'selected' : ''}`);
    row.type = 'button';
    const avatar = QP.el('div','avatar',QP.initials(peer.name));
    const copy = QP.el('div','dm-peer-copy');
    const title = QP.el('div','dm-peer-name',peer.name || 'Peer');
    const statusLabel = active ? 'Connected' : isConnecting ? `Connecting (${remaining}s)…` : 'Ready';
    const meta = QP.el('div','dm-peer-meta',`${statusLabel} · ${QP.formatPingStatus(peer)}`);
    copy.append(title, meta);
    row.append(avatar, copy, QP.el('span', `presence ${active ? 'online' : isConnecting ? 'connecting' : ''}`));
    row.addEventListener('click', () => {
      if (selectedPeerId !== peer.id) {
        selectedPeerId = peer.id;
        history.replaceState(null,'',`/chat.html?peer=${encodeURIComponent(peer.id)}`);
        renderChat();
        scrollToBottom();
      }
    });
    list.appendChild(row);
  });

  const peer = allPeers.find(p => p.id === selectedPeerId);
  const connected = !!peer && (chatStatus?.activeChats || []).some(c => c.peerId === peer.id);
  const isConnecting = !!peer && connectingPeers.has(peer.id);
  const remaining = isConnecting ? Math.max(0, Math.ceil((connectingPeers.get(peer.id).expireAt - Date.now()) / 1000)) : 0;

  QP.setText('chatPeerName', peer?.name || 'Choose a peer');
  QP.setText('chatPeerMeta', peer ? `${connected ? 'Chat connected' : isConnecting ? `Connecting (${remaining}s)…` : 'Trusted peer · not connected'} · ${QP.formatPingStatus(peer)} · ${QP.peerMeta(peer)}` : 'Select someone from the left');
  QP.setText('chatStatusBadge', connected ? 'Connected' : isConnecting ? `Connecting (${remaining}s)` : 'Not connected');
  QP.setText('chatAvatar', peer ? QP.initials(peer.name) : '—');

  const connectBtn = document.getElementById('chatConnectBtn');
  if (connected) {
    connectBtn.textContent = 'Chat connected';
    connectBtn.className = 'btn';
    connectBtn.disabled = true;
  } else if (isConnecting) {
    connectBtn.textContent = `Connecting… (${remaining}s)`;
    connectBtn.className = 'btn connecting';
    connectBtn.disabled = true;
  } else {
    connectBtn.textContent = 'Connect chat';
    connectBtn.className = 'btn primary';
    connectBtn.disabled = !peer;
  }

  document.getElementById('messageInput').disabled = !connected;
  document.getElementById('sendBtn').disabled = !connected;
  document.getElementById('attachBtn').disabled = !connected;
  renderMessages(peer);
}

function renderMessages(peer, forceScroll = false) {
  const box = document.getElementById('messages');
  if (!box) return;

  if (!peer) {
    lastRenderedPeerId = '';
    lastRenderedFingerprint = '';
    QP.clear(box);
    box.appendChild(QP.el('div','empty','Choose a trusted peer from the left.'));
    return;
  }

  const items = (chatStatus?.chatMessages || []).filter(m => m.peerId === peer.id);
  const currentFingerprint = getMessagesFingerprint(items);

  // Avoid unnecessary DOM rebuilds if nothing changed (prevents scroll jumps on background polling)
  if (peer.id === lastRenderedPeerId && currentFingerprint === lastRenderedFingerprint && !forceScroll) {
    return;
  }

  // Check if user was near the bottom before we update the DOM
  const isNearBottom = (box.scrollHeight - box.scrollTop - box.clientHeight) < 120;
  const isPeerChanged = peer.id !== lastRenderedPeerId;

  lastRenderedPeerId = peer.id;
  lastRenderedFingerprint = currentFingerprint;

  QP.clear(box);

  if (!items.length) {
    box.appendChild(QP.el('div','chat-welcome',`This is the beginning of your direct conversation with ${peer.name || 'this peer'}.`));
    return;
  }

  items.forEach(item => {
    const wrap = QP.el('div', `message-row ${item.isMe ? 'me' : ''}`);
    if (!item.isMe) wrap.appendChild(QP.el('div','avatar msg-avatar',QP.initials(item.sender || peer.name)));
    const body = QP.el('div','message-body');
    const head = QP.el('div','message-meta',`${item.isMe ? 'You' : item.sender} · ${item.time}${item.isMe ? (item.isConfirmed ? ' · delivered' : ' · sending') : ''}`);
    const bubble = QP.el('div',`message ${item.isMe ? 'me' : ''}`);
    appendMessageContent(bubble, item.message);
    body.append(head, bubble);
    wrap.appendChild(body);
    box.appendChild(wrap);
  });

  if (forceScroll || isPeerChanged || isNearBottom) {
    scrollToBottom();
  }
}

function appendMessageContent(container, raw) {
  try {
    const obj = JSON.parse(raw);
    if (obj && obj.type === 'image') {
      const src = QP.safeDataUrl(obj.data,'image');
      if(!src) throw new Error();
      if(obj.name) container.appendChild(QP.el('div','message-text',obj.name));
      const img = document.createElement('img');
      img.src = src;
      img.alt = obj.name || 'Image';
      img.onload = () => {
        const box = document.getElementById('messages');
        if (box && (box.scrollHeight - box.scrollTop - box.clientHeight < 250)) {
          scrollToBottom();
        }
      };
      container.appendChild(img);
      return;
    }
    if (obj && obj.type === 'audio') {
      const src = QP.safeDataUrl(obj.data,'audio');
      if(!src) throw new Error();
      const audio = document.createElement('audio');
      audio.controls = true;
      audio.src = src;
      container.appendChild(audio);
      return;
    }
    if (obj && obj.type === 'file') {
      const src = QP.safeDataUrl(obj.data,'file');
      if(!src) throw new Error();
      const link = QP.el('a','btn sm accent',`Download ${obj.name||'file'}`);
      link.href = src;
      link.download = String(obj.name||'file').replace(/[\\/:*?"<>|]/g,'_');
      container.appendChild(link);
      return;
    }
  } catch {}
  container.appendChild(QP.el('div','message-text',raw));
}

async function connectChat() {
  if (!selectedPeerId) return;
  const allPeers = QP.trusted(chatStatus || {});
  const peer = allPeers.find(p => p.id === selectedPeerId);
  const peerName = peer?.name || 'Peer';
  startConnecting(selectedPeerId, 25, peerName);
  try {
    await QP.api('/api/connect-peer', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, protocol: 'chat' })
    });
  } catch(e) {
    connectingPeers.delete(selectedPeerId);
    renderChat();
    QP.toast(e.message, 'error');
  }
}

async function sendChatMessage(payload){
  const input = document.getElementById('messageInput');
  const message = payload ?? input.value.trim();
  if(!message || !selectedPeerId) return;
  try {
    await QP.api('/api/chat-send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, message })
    });
    if(payload === undefined) {
      input.value = '';
      input.style.height = '';
    }
    scrollToBottom(true);
  } catch(e) {
    QP.toast(e.message, 'error');
  }
}

async function sendAttachment(file){
  if(!file) return;
  if(file.size > 6 * 1024 * 1024){
    QP.toast('Attachment limit is 6 MB', 'error');
    return;
  }
  try {
    const data = await readDataUrl(file);
    let type = 'file';
    if(file.type.startsWith('image/')) type = 'image';
    else if(file.type.startsWith('audio/')) type = 'audio';
    await sendChatMessage(JSON.stringify({ type, name: file.name, mime: file.type || 'application/octet-stream', size: file.size, data }));
    document.getElementById('fileInput').value = '';
  } catch(e) {
    QP.toast(e.message, 'error');
  }
}

function readDataUrl(file){
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result);
    r.onerror = reject;
    r.readAsDataURL(file);
  });
}
