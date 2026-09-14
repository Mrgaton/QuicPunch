let chatStatus = null;
let selectedPeerId = new URLSearchParams(location.search).get('peer') || '';
let peerFilter = '';
let inChatSearchQuery = '';
let lastRenderedPeerId = '';
let lastRenderedFingerprint = '';
const connectingPeers = new Map();
const fileTransfers = new Map(); // fileId -> { percent, received, total, isUpload, path, name, status }
let typingTimeout = null;
let isLocallyTyping = false;

// Voice recording state
let mediaRecorder = null;
let audioChunks = [];
let recordTimerInterval = null;
let recordSeconds = 0;

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
  return `${items.length}:${last.msgId}:${last.isConfirmed ? '1' : '0'}:${last.time}:${inChatSearchQuery}`;
}

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

function getFileCategory(name) {
  const ext = (name.split('.').pop() || '').toLowerCase();
  if (['pdf'].includes(ext)) return { icon: '📕', label: 'PDF', cls: 'cat-pdf' };
  if (['zip', 'rar', '7z', 'tar', 'gz', 'bz2'].includes(ext)) return { icon: '🗜️', label: 'Archive', cls: 'cat-zip' };
  if (['mp4', 'mkv', 'avi', 'mov', 'webm', 'wmv'].includes(ext)) return { icon: '🎬', label: 'Video', cls: 'cat-video' };
  if (['mp3', 'wav', 'ogg', 'flac', 'm4a', 'aac'].includes(ext)) return { icon: '🎵', label: 'Audio', cls: 'cat-audio' };
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp'].includes(ext)) return { icon: '🖼️', label: 'Image', cls: 'cat-img' };
  if (['cs', 'js', 'ts', 'py', 'json', 'html', 'css', 'cpp', 'c', 'java', 'go', 'rs', 'sh', 'sql'].includes(ext)) return { icon: '💻', label: 'Code', cls: 'cat-code' };
  if (['exe', 'msi', 'apk', 'dmg', 'iso', 'bin'].includes(ext)) return { icon: '⚙️', label: 'App', cls: 'cat-exe' };
  return { icon: '📄', label: 'Document', cls: 'cat-doc' };
}

function renderFormattedText(text) {
  if (!text) return '';
  const div = document.createElement('div');
  div.textContent = text;
  let safe = div.innerHTML;

  // Code blocks ```code```
  safe = safe.replace(/```([\s\S]*?)```/g, (match, code) => {
    return `<pre class="chat-code-block"><code>${code.trim()}</code></pre>`;
  });

  // Inline code `code`
  safe = safe.replace(/`([^`\n]+)`/g, (match, code) => {
    return `<code class="chat-inline-code">${code}</code>`;
  });

  // Bold **bold**
  safe = safe.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

  // Italic *italic*
  safe = safe.replace(/(^|[^*])\*([^*]+)\*([^*]|$)/g, '$1<em>$2</em>$3');

  // URLs to clickable links
  safe = safe.replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener noreferrer" class="chat-link">$1</a>');

  // Line breaks
  safe = safe.replace(/\n/g, '<br>');

  return safe;
}

document.addEventListener('DOMContentLoaded', () => {
  // Main chat action buttons
  document.getElementById('chatConnectBtn')?.addEventListener('click', connectChat);
  document.getElementById('sendBtn')?.addEventListener('click', () => sendChatMessage());
  document.getElementById('attachBtn')?.addEventListener('click', () => document.getElementById('fileInput').click());
  document.getElementById('fileInput')?.addEventListener('change', e => {
    const files = Array.from(e.target.files || []);
    files.forEach(f => sendAttachment(f));
  });
  document.getElementById('chatPeerSearch')?.addEventListener('input', e => {
    peerFilter = e.target.value.trim().toLowerCase();
    renderChat();
  });

  // Textarea input & typing notification
  const msgInput = document.getElementById('messageInput');
  if (msgInput) {
    msgInput.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendChatMessage();
      }
    });
    msgInput.addEventListener('input', () => {
      msgInput.style.height = 'auto';
      msgInput.style.height = Math.min(msgInput.scrollHeight, 140) + 'px';
      notifyTyping();
    });
  }

  // Header actions: In-chat search & clear
  document.getElementById('chatSearchToggleBtn')?.addEventListener('click', toggleInChatSearch);
  document.getElementById('chatSearchCloseBtn')?.addEventListener('click', closeInChatSearch);
  document.getElementById('chatSearchInput')?.addEventListener('input', onInChatSearchInput);
  document.getElementById('chatClearBtn')?.addEventListener('click', clearCurrentConversation);

  // Emojis
  document.getElementById('emojiBtn')?.addEventListener('click', toggleEmojiPopover);
  initEmojiGrid();

  // Voice recording
  document.getElementById('voiceRecordBtn')?.addEventListener('click', startVoiceRecording);
  document.getElementById('voiceCancelBtn')?.addEventListener('click', () => stopVoiceRecording(true));
  document.getElementById('voiceSendBtn')?.addEventListener('click', () => stopVoiceRecording(false));

  // Lightbox
  document.getElementById('lightboxCloseBtn')?.addEventListener('click', closeLightbox);
  document.getElementById('lightboxOverlay')?.addEventListener('click', closeLightbox);
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      closeLightbox();
      closeEmojiPopover();
    }
  });

  // Drag and drop support
  setupDragAndDrop();

  // Clipboard paste support
  setupClipboardPaste();

  if (new URLSearchParams(location.search).get('connecting') === '1' && selectedPeerId) {
    startConnecting(selectedPeerId);
  }

  // Real-time WebSocket subscriptions
  QP.on('chat_msg', onRealtimeMessage);
  QP.on('chat_ack', onRealtimeAck);
  QP.on('chat_typing', onRealtimeTyping);
  QP.on('chat_file_progress', onRealtimeFileProgress);
  QP.on('chat_file_complete', onRealtimeFileComplete);
  QP.on('chat_file_cancelled', onRealtimeFileCancelled);
  QP.on('chat_cleared', onRealtimeChatCleared);
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
  setInterval(tickConnecting, 250);
});

function notifyTyping() {
  if (!selectedPeerId) return;
  if (!isLocallyTyping) {
    isLocallyTyping = true;
    QP.api('/api/chat/typing', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, isTyping: true })
    }).catch(() => {});
  }
  clearTimeout(typingTimeout);
  typingTimeout = setTimeout(() => {
    isLocallyTyping = false;
    QP.api('/api/chat/typing', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, isTyping: false })
    }).catch(() => {});
  }, 2500);
}

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

function onRealtimeTyping(data) {
  if (!data || data.peerId !== selectedPeerId) return;
  const bar = document.getElementById('chatTypingIndicator');
  const text = document.getElementById('chatTypingText');
  if (!bar) return;
  if (data.isTyping) {
    if (text) text.textContent = `${data.peerName || 'Peer'} is typing...`;
    bar.classList.remove('hidden');
  } else {
    bar.classList.add('hidden');
  }
}

function onRealtimeFileProgress(data) {
  if (!data) return;
  const existing = fileTransfers.get(data.fileId) || {};
  fileTransfers.set(data.fileId, {
    ...existing,
    percent: data.percent,
    received: data.received,
    total: data.total,
    isUpload: !!data.isUpload,
    status: 'transferring'
  });
  updateFileCardDom(data.fileId);
}

function onRealtimeFileComplete(data) {
  if (!data) return;
  const existing = fileTransfers.get(data.fileId) || {};
  fileTransfers.set(data.fileId, {
    ...existing,
    percent: 100,
    status: 'completed',
    path: data.path,
    name: data.name
  });
  updateFileCardDom(data.fileId);
  QP.toast(`File downloaded: ${data.name || 'file'}`, 'success');
}

function onRealtimeFileCancelled(data) {
  if (!data) return;
  const existing = fileTransfers.get(data.fileId) || {};
  fileTransfers.set(data.fileId, {
    ...existing,
    status: 'cancelled'
  });
  updateFileCardDom(data.fileId);
}

function onRealtimeChatCleared(data) {
  if (!data || !chatStatus?.chatMessages) return;
  chatStatus.chatMessages = chatStatus.chatMessages.filter(m => m.peerId !== data.peerId);
  if (selectedPeerId === data.peerId) {
    const allPeers = QP.trusted(chatStatus || {});
    const peer = allPeers.find(p => p.id === selectedPeerId);
    renderMessages(peer, false);
    QP.toast('Conversation history cleared', 'info');
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
      QP.toast(`Could not connect to ${info.peerName}: connection timed out.`, 'error');
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
  QP.setText('chatAvatar', peer ? QP.initials(peer.name) : '—');

  const connectBtn = document.getElementById('chatConnectBtn');
  if (connectBtn) {
    if (connected) {
      connectBtn.classList.add('hidden');
    } else if (isConnecting) {
      connectBtn.classList.remove('hidden');
      connectBtn.textContent = `Conectando… (${remaining}s)`;
      connectBtn.className = 'btn connecting';
      connectBtn.disabled = true;
    } else {
      connectBtn.classList.toggle('hidden', !peer);
      connectBtn.textContent = 'Conectar';
      connectBtn.className = 'btn ghost sm';
      connectBtn.disabled = !peer;
    }
  }

  // Discord / Telegram style: allow composing immediately if a trusted peer is selected
  const canCompose = !!peer;
  const inputs = ['messageInput', 'sendBtn', 'attachBtn', 'voiceRecordBtn', 'emojiBtn', 'chatSearchToggleBtn', 'chatClearBtn'];
  inputs.forEach(id => {
    const el = document.getElementById(id);
    if (el) el.disabled = !canCompose;
  });

  const msgInput = document.getElementById('messageInput');
  if (msgInput && peer) {
    msgInput.placeholder = connected
      ? `Enviar mensaje a ${peer.name} (Enter para enviar)`
      : `Escribe un mensaje a ${peer.name} (se conectará automáticamente al enviar)…`;
  }

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

  let items = (chatStatus?.chatMessages || []).filter(m => m.peerId === peer.id);
  if (inChatSearchQuery) {
    items = items.filter(m => m.message && m.message.toLowerCase().includes(inChatSearchQuery.toLowerCase()));
    const badge = document.getElementById('chatSearchCount');
    if (badge) badge.textContent = `${items.length} match${items.length === 1 ? '' : 'es'}`;
  }

  const currentFingerprint = getMessagesFingerprint(items);

  if (peer.id === lastRenderedPeerId && currentFingerprint === lastRenderedFingerprint && !forceScroll) {
    return;
  }

  const isNearBottom = (box.scrollHeight - box.scrollTop - box.clientHeight) < 140;
  const isPeerChanged = peer.id !== lastRenderedPeerId;

  lastRenderedPeerId = peer.id;
  lastRenderedFingerprint = currentFingerprint;

  QP.clear(box);

  if (!items.length) {
    const welcome = QP.el('div','chat-welcome');
    welcome.innerHTML = inChatSearchQuery
      ? `No messages matched <strong>"${inChatSearchQuery}"</strong>.`
      : `<strong>This is the beginning of your conversation with ${peer.name || 'this peer'}.</strong><br><small style="color:var(--muted)">Messages and files are sent directly peer-to-peer via QUIC.</small>`;
    box.appendChild(welcome);
    return;
  }

  items.forEach(item => {
    const wrap = QP.el('div', `message-row ${item.isMe ? 'me' : ''}`);
    if (!item.isMe) wrap.appendChild(QP.el('div','avatar msg-avatar',QP.initials(item.sender || peer.name)));
    const body = QP.el('div','message-body');
    const statusIcon = item.isMe ? (item.isConfirmed ? ' <span class="chat-check confirmed" title="Delivered">✓✓</span>' : ' <span class="chat-check" title="Sending">✓</span>') : '';
    const head = QP.el('div','message-meta');
    head.innerHTML = `${item.isMe ? 'You' : item.sender} · ${item.time}${statusIcon}`;
    const bubble = QP.el('div',`message ${item.isMe ? 'me' : ''}`);
    appendMessageContent(bubble, item.message, item);
    body.append(head, bubble);
    wrap.appendChild(body);
    box.appendChild(wrap);
  });

  if (forceScroll || isPeerChanged || isNearBottom) {
    scrollToBottom();
  }
}

function renderChatMessagesOnly() {
  const allPeers = QP.trusted(chatStatus || {});
  const peer = allPeers.find(p => p.id === selectedPeerId);
  if (peer) renderMessages(peer, false);
}

function buildMediaPreviewDom(fileId, fileName, catCls) {
  const preview = QP.el('div', 'chat-file-preview');
  const viewUrl = `/api/chat/view-file?fileId=${encodeURIComponent(fileId)}`;

  if (catCls === 'cat-img') {
    const img = document.createElement('img');
    img.src = viewUrl;
    img.alt = fileName || 'Image';
    img.className = 'chat-media-img';
    img.addEventListener('click', () => openLightbox(viewUrl, fileName));
    img.onload = () => {
      const box = document.getElementById('messages');
      if (box && (box.scrollHeight - box.scrollTop - box.clientHeight < 300)) {
        scrollToBottom();
      }
    };
    preview.appendChild(img);
  } else if (catCls === 'cat-video') {
    const video = document.createElement('video');
    video.controls = true;
    video.preload = 'metadata';
    video.src = viewUrl;
    video.className = 'chat-media-video';
    video.onloadeddata = () => {
      const box = document.getElementById('messages');
      if (box && (box.scrollHeight - box.scrollTop - box.clientHeight < 300)) {
        scrollToBottom();
      }
    };
    preview.appendChild(video);
  } else if (catCls === 'cat-audio') {
    const audio = document.createElement('audio');
    audio.controls = true;
    audio.preload = 'metadata';
    audio.src = viewUrl;
    audio.className = 'chat-audio-player';
    preview.appendChild(audio);
  }

  return preview;
}

function appendMessageContent(container, raw, item) {
  let obj = null;
  if (typeof raw === 'string' && (raw.startsWith('{') || raw.startsWith('['))) {
    try {
      obj = JSON.parse(raw);
    } catch {
      obj = null;
    }
  }

  if (obj && typeof obj === 'object') {
    // 1. INLINE IMAGE
    if (obj.type === 'image') {
      let src = QP.safeDataUrl(obj.data, 'image');
      if (!src && typeof obj.data === 'string' && (obj.data.startsWith('http://') || obj.data.startsWith('https://') || obj.data.startsWith('/api/'))) {
        src = obj.data;
      }
      if (!src && obj.fileId) {
        src = `/api/chat/view-file?fileId=${encodeURIComponent(obj.fileId)}`;
      }
      if (!src && typeof obj.data === 'string' && obj.data.startsWith('data:image/')) {
        src = obj.data;
      }

      if (src) {
        if (obj.name) container.appendChild(QP.el('div', 'message-attachment-title', obj.name));
        const img = document.createElement('img');
        img.src = src;
        img.alt = obj.name || 'Image';
        img.className = 'chat-media-img';
        img.addEventListener('click', () => openLightbox(src, obj.name));
        img.onload = () => {
          const box = document.getElementById('messages');
          if (box && (box.scrollHeight - box.scrollTop - box.clientHeight < 300)) {
            scrollToBottom();
          }
        };
        img.onerror = () => {
          img.style.display = 'none';
          const errDiv = QP.el('div', 'chat-media-error', `⚠️ Image failed to load (${obj.name || 'image'})`);
          container.appendChild(errDiv);
        };
        container.appendChild(img);
      } else {
        const errDiv = QP.el('div', 'chat-media-error', `⚠️ Image format not supported (${obj.name || 'image'})`);
        container.appendChild(errDiv);
      }
      return;
    }

    // 2. INLINE AUDIO / VOICE NOTE
    if (obj.type === 'audio') {
      let src = QP.safeDataUrl(obj.data, 'audio');
      if (!src && typeof obj.data === 'string' && (obj.data.startsWith('http://') || obj.data.startsWith('https://') || obj.data.startsWith('/api/'))) {
        src = obj.data;
      }
      if (!src && obj.fileId) {
        src = `/api/chat/view-file?fileId=${encodeURIComponent(obj.fileId)}`;
      }
      if (!src && typeof obj.data === 'string' && obj.data.startsWith('data:audio/')) {
        src = obj.data;
      }

      const card = QP.el('div', 'chat-voice-card');
      const icon = QP.el('span', 'chat-voice-icon', '🎙️');
      card.appendChild(icon);

      if (src) {
        const audio = document.createElement('audio');
        audio.controls = true;
        audio.src = src;
        audio.className = 'chat-audio-player';
        audio.preload = 'metadata';
        audio.onerror = () => {
          console.warn('Audio element error loading src');
        };
        card.appendChild(audio);
        if (obj.name && obj.name !== 'Voice note') {
          const label = QP.el('span', 'chat-voice-name', obj.name);
          card.appendChild(label);
        }
      } else {
        const label = QP.el('span', 'chat-voice-label', obj.name || 'Audio message (unavailable)');
        card.appendChild(label);
      }

      container.appendChild(card);
      return;
    }

    // 3. INLINE VIDEO
    if (obj.type === 'video') {
      let src = QP.safeDataUrl(obj.data, 'video');
      if (!src && typeof obj.data === 'string' && (obj.data.startsWith('http://') || obj.data.startsWith('https://') || obj.data.startsWith('/api/'))) {
        src = obj.data;
      }
      if (!src && obj.fileId) {
        src = `/api/chat/view-file?fileId=${encodeURIComponent(obj.fileId)}`;
      }
      if (!src && typeof obj.data === 'string' && obj.data.startsWith('data:video/')) {
        src = obj.data;
      }

      if (obj.name) container.appendChild(QP.el('div', 'message-attachment-title', obj.name));

      if (src) {
        const video = document.createElement('video');
        video.controls = true;
        video.preload = 'metadata';
        video.src = src;
        video.className = 'chat-media-video';
        video.onloadeddata = () => {
          const box = document.getElementById('messages');
          if (box && (box.scrollHeight - box.scrollTop - box.clientHeight < 300)) {
            scrollToBottom();
          }
        };
        video.onerror = () => {
          video.style.display = 'none';
          const errDiv = QP.el('div', 'chat-media-error', `⚠️ Video could not be played (${obj.name || 'video'})`);
          container.appendChild(errDiv);
        };
        container.appendChild(video);
      } else {
        const errDiv = QP.el('div', 'chat-media-error', `⚠️ Video unavailable (${obj.name || 'video'})`);
        container.appendChild(errDiv);
      }
      return;
    }

    // 4. ON-DEMAND FILE OFFER (Modern File Card)
    if (obj.type === 'file_offer') {
      const card = createFileCardDom(obj, item);
      container.appendChild(card);
      return;
    }

    // 5. LEGACY INLINE FILE
    if (obj.type === 'file') {
      const src = QP.safeDataUrl(obj.data, 'file') || obj.data;
      if (src) {
        const link = QP.el('a', 'btn sm accent', `Download ${obj.name || 'file'}`);
        link.href = src;
        link.download = String(obj.name || 'file').replace(/[\\/:*?"<>|]/g, '_');
        container.appendChild(link);
        return;
      }
    }
  }

  // Plain Text / Markdown
  const textContainer = QP.el('div', 'message-text');
  textContainer.innerHTML = renderFormattedText(raw);
  container.appendChild(textContainer);
}

function createFileCardDom(offer, item) {
  const fileId = offer.fileId;
  const fileName = offer.name || 'file';
  const fileSize = offer.size || 0;
  const cat = getFileCategory(fileName);
  const transfer = fileTransfers.get(fileId) || {};

  const wrapper = QP.el('div', 'chat-file-wrapper');
  wrapper.dataset.fileId = fileId;

  const isAvailable = transfer.status === 'completed' || (item.isMe && offer.path);
  if (isAvailable && ['cat-img', 'cat-video', 'cat-audio'].includes(cat.cls)) {
    wrapper.appendChild(buildMediaPreviewDom(fileId, fileName, cat.cls));
  }

  const card = QP.el('div', `chat-file-card ${cat.cls}`);
  card.dataset.fileId = fileId;

  // Icon column
  const iconCol = QP.el('div', 'file-card-icon');
  iconCol.textContent = cat.icon;

  // Info column
  const infoCol = QP.el('div', 'file-card-info');
  const nameEl = QP.el('div', 'file-card-name', fileName);
  nameEl.title = fileName;

  let statusLabel = `${cat.label} · ${formatBytes(fileSize)}`;
  if (transfer.status === 'downloading' || transfer.status === 'transferring') {
    const pct = transfer.percent || 0;
    const recStr = formatBytes(transfer.received || 0);
    const totStr = formatBytes(fileSize);
    statusLabel = `${transfer.isUpload ? 'Uploading' : 'Downloading'} ${pct}% (${recStr} / ${totStr})`;
  } else if (transfer.status === 'completed') {
    statusLabel = `Downloaded · ${formatBytes(fileSize)}`;
  } else if (transfer.status === 'cancelled') {
    statusLabel = `Cancelled · ${formatBytes(fileSize)}`;
  }

  const metaEl = QP.el('div', 'file-card-meta', statusLabel);
  metaEl.className = 'file-card-meta';

  // Progress Bar
  const progressTrack = QP.el('div', 'file-card-progress');
  const progressFill = QP.el('div', 'file-card-progress-fill');
  const pct = Math.min(100, Math.max(0, transfer.percent || 0));
  progressFill.style.width = `${pct}%`;
  progressTrack.appendChild(progressFill);

  if (transfer.status === 'downloading' || transfer.status === 'transferring') {
    progressTrack.classList.add('active');
  } else {
    progressTrack.classList.remove('active');
  }

  infoCol.append(nameEl, metaEl, progressTrack);

  // Action Buttons column
  const actionCol = QP.el('div', 'file-card-actions');

  if (transfer.status === 'completed' || (item.isMe && offer.path)) {
    const targetPath = transfer.path || offer.path;
    const openBtn = QP.el('button', 'btn sm', 'Open');
    openBtn.type = 'button';
    openBtn.addEventListener('click', () => openChatFile(fileId, targetPath, false));

    const folderBtn = QP.el('button', 'btn sm icon-btn', '📂');
    folderBtn.type = 'button';
    folderBtn.title = 'Reveal in folder';
    folderBtn.addEventListener('click', () => openChatFile(fileId, targetPath, true));

    actionCol.append(openBtn, folderBtn);
  } else if (transfer.status === 'downloading' || transfer.status === 'transferring') {
    const cancelBtn = QP.el('button', 'btn sm danger', '✕');
    cancelBtn.type = 'button';
    cancelBtn.title = 'Cancel transfer';
    cancelBtn.addEventListener('click', () => cancelChatFile(fileId));
    actionCol.appendChild(cancelBtn);
  } else if (item.isMe) {
    const label = QP.el('span', 'file-card-tag', 'Sent');
    actionCol.appendChild(label);
  } else {
    const dlBtn = QP.el('button', 'btn sm primary', '⬇️ Download');
    dlBtn.type = 'button';
    dlBtn.addEventListener('click', () => downloadChatFile(fileId, fileName, fileSize));
    actionCol.appendChild(dlBtn);
  }

  card.append(iconCol, infoCol, actionCol);
  wrapper.appendChild(card);
  return wrapper;
}

function updateFileCardDom(fileId) {
  const wrappers = document.querySelectorAll(`.chat-file-wrapper[data-file-id="${fileId}"]`);
  wrappers.forEach(wrapper => {
    const card = wrapper.querySelector(`.chat-file-card[data-file-id="${fileId}"]`);
    if (!card) return;
    const transfer = fileTransfers.get(fileId) || {};
    const metaEl = card.querySelector('.file-card-meta');
    const fillEl = card.querySelector('.file-card-progress-fill');
    const trackEl = card.querySelector('.file-card-progress');
    const actionsEl = card.querySelector('.file-card-actions');
    const nameEl = card.querySelector('.file-card-name');
    const fileName = nameEl ? nameEl.textContent : (transfer.name || 'file');
    const cat = getFileCategory(fileName);

    const pct = Math.min(100, Math.max(0, transfer.percent || 0));
    if (fillEl) fillEl.style.width = `${pct}%`;

    if (transfer.status === 'downloading' || transfer.status === 'transferring') {
      if (trackEl) trackEl.classList.add('active');
      if (metaEl) {
        const recStr = formatBytes(transfer.received || 0);
        const totStr = formatBytes(transfer.total || 0);
        metaEl.textContent = `${transfer.isUpload ? 'Uploading' : 'Downloading'} ${pct}% (${recStr} / ${totStr})`;
      }
    } else if (transfer.status === 'completed') {
      if (trackEl) trackEl.classList.remove('active');
      if (metaEl) metaEl.textContent = `Downloaded · ${formatBytes(transfer.total || 0)}`;

      // Attach media preview if not present
      if (['cat-img', 'cat-video', 'cat-audio'].includes(cat.cls) && !wrapper.querySelector('.chat-file-preview')) {
        const preview = buildMediaPreviewDom(fileId, fileName, cat.cls);
        wrapper.insertBefore(preview, card);
      }

      if (actionsEl) {
        QP.clear(actionsEl);
        const openBtn = QP.el('button', 'btn sm', 'Open');
        openBtn.type = 'button';
        openBtn.addEventListener('click', () => openChatFile(fileId, transfer.path, false));
        const folderBtn = QP.el('button', 'btn sm icon-btn', '📂');
        folderBtn.type = 'button';
        folderBtn.title = 'Reveal in folder';
        folderBtn.addEventListener('click', () => openChatFile(fileId, transfer.path, true));
        actionsEl.append(openBtn, folderBtn);
      }
    } else if (transfer.status === 'cancelled') {
      if (trackEl) trackEl.classList.remove('active');
      if (metaEl) metaEl.textContent = 'Transfer cancelled';
    }
  });
}

async function downloadChatFile(fileId, name, size) {
  if (!selectedPeerId) return;
  fileTransfers.set(fileId, { percent: 0, received: 0, total: size, isUpload: false, status: 'downloading', name });
  updateFileCardDom(fileId);
  try {
    const res = await QP.api('/api/chat/download-file', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, fileId, name, size })
    });
    if (res && res.cached && res.path) {
      fileTransfers.set(fileId, { percent: 100, received: size, total: size, isUpload: false, status: 'completed', path: res.path, name });
      updateFileCardDom(fileId);
      QP.toast(`File ready: ${name}`, 'success');
    }
  } catch (e) {
    fileTransfers.set(fileId, { status: 'error', error: e.message });
    updateFileCardDom(fileId);
    QP.toast(e.message, 'error');
  }
}

async function openChatFile(fileId, path, openFolder = false) {
  try {
    await QP.api('/api/chat/open-file', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fileId, path, openFolder })
    });
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

async function cancelChatFile(fileId) {
  if (!selectedPeerId) return;
  try {
    await QP.api('/api/chat/cancel-file', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, fileId })
    });
    fileTransfers.set(fileId, { status: 'cancelled' });
    updateFileCardDom(fileId);
  } catch (e) {
    QP.toast(e.message, 'error');
  }
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

async function sendChatMessage(payload) {
  const input = document.getElementById('messageInput');
  const message = payload ?? input.value.trim();
  if (!message || !selectedPeerId) return;

  const connected = !!chatStatus && (chatStatus.activeChats || []).some(c => c.peerId === selectedPeerId);
  if (!connected) {
    QP.toast('Iniciando conexión directa con el peer…', 'info');
    connectChat();
    // Wait briefly for handshake if already in progress or newly requested
    let waitLoops = 0;
    while (waitLoops < 40) {
      await new Promise(r => setTimeout(r, 250));
      waitLoops++;
      const isNowConnected = !!chatStatus && (chatStatus.activeChats || []).some(c => c.peerId === selectedPeerId);
      if (isNowConnected) break;
    }
  }

  try {
    await QP.api('/api/chat-send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId, message })
    });
    if (payload === undefined) {
      input.value = '';
      input.style.height = '';
      if (isLocallyTyping) {
        isLocallyTyping = false;
        clearTimeout(typingTimeout);
        QP.api('/api/chat/typing', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ peerId: selectedPeerId, isTyping: false })
        }).catch(() => {});
      }
    }
    scrollToBottom(true);
  } catch(e) {
    QP.toast(e.message, 'error');
  }
}

async function sendAttachment(file) {
  if (!file || !selectedPeerId) return;

  // 1. If image, audio, or video clip (<= 10 MB), send inline for instant display in memory
  if ((file.type.startsWith('image/') || file.type.startsWith('audio/') || file.type.startsWith('video/')) && file.size <= 10 * 1024 * 1024) {
    try {
      const data = await readDataUrl(file);
      let type = 'file';
      if (file.type.startsWith('image/')) type = 'image';
      else if (file.type.startsWith('audio/')) type = 'audio';
      else if (file.type.startsWith('video/')) type = 'video';
      await sendChatMessage(JSON.stringify({ type, name: file.name, mime: file.type || 'application/octet-stream', size: file.size, data }));
      document.getElementById('fileInput').value = '';
    } catch(e) {
      QP.toast(e.message, 'error');
    }
    return;
  }

  // 2. Large files, documents, archives, videos: On-demand streaming from disk to disk
  QP.toast(`Sharing "${file.name}" (${formatBytes(file.size)})...`, 'info');
  try {
    const csrf = (window.QP_CSRF || (window.QP && window.QP.csrf) || '');
    const b64Name = btoa(unescape(encodeURIComponent(file.name)));
    const res = await fetch(`/api/chat/share-file?peerId=${encodeURIComponent(selectedPeerId)}`, {
      method: 'POST',
      headers: {
        'X-Peer-Id': selectedPeerId,
        'X-File-Name-B64': b64Name,
        'X-QuicPunch-CSRF': csrf,
        'Content-Type': file.type || 'application/octet-stream'
      },
      body: file
    });
    const data = await res.json();
    if (!res.ok || !data.success) {
      throw new Error(data.error || 'Failed to share file.');
    }
    QP.toast(`Offered "${file.name}" to peer`, 'success');
    document.getElementById('fileInput').value = '';
    scrollToBottom(true);
  } catch (e) {
    QP.toast(`File share error: ${e.message}`, 'error');
  }
}

function readDataUrl(file) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result);
    r.onerror = reject;
    r.readAsDataURL(file);
  });
}

// Voice Recording Handlers
async function startVoiceRecording() {
  if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
    QP.toast('Microphone access is not available in this environment.', 'error');
    return;
  }
  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    audioChunks = [];
    recordSeconds = 0;
    mediaRecorder = new MediaRecorder(stream);
    mediaRecorder.ondataavailable = e => {
      if (e.data && e.data.size > 0) audioChunks.push(e.data);
    };
    mediaRecorder.onstop = async () => {
      stream.getTracks().forEach(t => t.stop());
    };
    mediaRecorder.start(200);

    document.getElementById('voiceRecordingBar')?.classList.remove('hidden');
    document.getElementById('voiceRecordingTimer').textContent = '00:00';
    recordTimerInterval = setInterval(() => {
      recordSeconds++;
      const mins = String(Math.floor(recordSeconds / 60)).padStart(2, '0');
      const secs = String(recordSeconds % 60).padStart(2, '0');
      const timerEl = document.getElementById('voiceRecordingTimer');
      if (timerEl) timerEl.textContent = `${mins}:${secs}`;
    }, 1000);
  } catch (err) {
    QP.toast(`Microphone error: ${err.message}`, 'error');
  }
}

function stopVoiceRecording(discard = false) {
  if (recordTimerInterval) {
    clearInterval(recordTimerInterval);
    recordTimerInterval = null;
  }
  document.getElementById('voiceRecordingBar')?.classList.add('hidden');

  if (mediaRecorder && mediaRecorder.state !== 'inactive') {
    mediaRecorder.onstop = async () => {
      if (!discard && audioChunks.length > 0) {
        const mime = mediaRecorder.mimeType || 'audio/webm';
        const blob = new Blob(audioChunks, { type: mime });
        if (blob.size > 0) {
          const dataUrl = await readDataUrl(blob);
          await sendChatMessage(JSON.stringify({
            type: 'audio',
            name: `Voice note`,
            mime,
            size: blob.size,
            data: dataUrl
          }));
        }
      }
      audioChunks = [];
    };
    mediaRecorder.stop();
  }
}

// Emoji Popover
function initEmojiGrid() {
  const grid = document.getElementById('emojiGrid');
  if (!grid) return;
  const emojis = ['😀','😃','😄','😁','😆','😅','😂','🤣','😊','😇','🙂','😉','😍','🥰','😘','😋','😎','🤔','🤫','🤐','😴','🥳','🤯','😱','🔥','👍','👎','👏','🙌','🤝','❤️','💖','💯','🚀','🎉','👀','⚡','☕','🎮','📌','📎','🔒','📦'];
  QP.clear(grid);
  emojis.forEach(emo => {
    const btn = QP.el('button', 'emoji-item', emo);
    btn.type = 'button';
    btn.addEventListener('click', () => {
      insertEmoji(emo);
    });
    grid.appendChild(btn);
  });
}

function toggleEmojiPopover() {
  const popover = document.getElementById('emojiPopover');
  if (popover) popover.classList.toggle('hidden');
}

function closeEmojiPopover() {
  document.getElementById('emojiPopover')?.classList.add('hidden');
}

function insertEmoji(emo) {
  const input = document.getElementById('messageInput');
  if (!input) return;
  const start = input.selectionStart || 0;
  const end = input.selectionEnd || 0;
  const val = input.value;
  input.value = val.slice(0, start) + emo + val.slice(end);
  input.selectionStart = input.selectionEnd = start + emo.length;
  input.focus();
  notifyTyping();
}

// In-chat Search
function toggleInChatSearch() {
  const bar = document.getElementById('chatSearchBar');
  if (!bar) return;
  bar.classList.toggle('hidden');
  if (!bar.classList.contains('hidden')) {
    document.getElementById('chatSearchInput')?.focus();
  } else {
    inChatSearchQuery = '';
    renderChatMessagesOnly();
  }
}

function closeInChatSearch() {
  document.getElementById('chatSearchBar')?.classList.add('hidden');
  inChatSearchQuery = '';
  const searchInput = document.getElementById('chatSearchInput');
  if (searchInput) searchInput.value = '';
  renderChatMessagesOnly();
}

function onInChatSearchInput(e) {
  inChatSearchQuery = e.target.value.trim();
  renderChatMessagesOnly();
}

// Clear conversation
async function clearCurrentConversation() {
  if (!selectedPeerId) return;
  if (!confirm('Are you sure you want to clear this conversation history?')) return;
  try {
    await QP.api('/api/chat/clear', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId: selectedPeerId })
    });
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

// Lightbox
function openLightbox(src, name) {
  const box = document.getElementById('chatLightbox');
  const img = document.getElementById('lightboxImg');
  const cap = document.getElementById('lightboxCaption');
  const dl = document.getElementById('lightboxDownloadBtn');
  if (!box || !img) return;
  img.src = src;
  if (cap) cap.textContent = name || 'Image';
  if (dl) {
    dl.href = src;
    dl.download = (name || 'image').replace(/[\\/:*?"<>|]/g, '_');
  }
  box.classList.remove('hidden');
}

function closeLightbox() {
  document.getElementById('chatLightbox')?.classList.add('hidden');
}

// Drag & Drop
function setupDragAndDrop() {
  const container = document.getElementById('chatContainer');
  const overlay = document.getElementById('chatDropZone');
  if (!container || !overlay) return;

  let dragCounter = 0;

  container.addEventListener('dragenter', e => {
    e.preventDefault();
    dragCounter++;
    overlay.classList.remove('hidden');
  });

  container.addEventListener('dragleave', e => {
    e.preventDefault();
    dragCounter--;
    if (dragCounter <= 0) {
      dragCounter = 0;
      overlay.classList.add('hidden');
    }
  });

  container.addEventListener('dragover', e => {
    e.preventDefault();
  });

  container.addEventListener('drop', e => {
    e.preventDefault();
    dragCounter = 0;
    overlay.classList.add('hidden');
    const files = Array.from(e.dataTransfer?.files || []);
    if (files.length) {
      files.forEach(f => sendAttachment(f));
    }
  });
}

// Clipboard Paste
function setupClipboardPaste() {
  window.addEventListener('paste', e => {
    const chatView = document.getElementById('view-chat');
    if (chatView && chatView.classList.contains('hidden')) return;
    if (!selectedPeerId) return;

    const files = Array.from(e.clipboardData?.files || []);
    if (files.length > 0) {
      e.preventDefault();
      files.forEach(f => sendAttachment(f));
    }
  });
}

