let chatStatus = null;
let selectedPeerId = new URLSearchParams(location.search).get('peer') || '';
const conversationDrafts = new Map();
function selectChatPeer(peerId) {
  closeEmojiPopover();
  if (typeof stopTyping === 'function') stopTyping();
  const input = document.getElementById('messageInput');
  if (input && selectedPeerId) conversationDrafts.set(selectedPeerId, input.value);
  selectedPeerId = peerId;
  const peer = chatTrustedPeers(chatStatus).find(item => item.id === peerId);
  if (peer) reopenConversation(peer);
  followChatLatest = true;
  chatViewportAnchor = null;
  inChatSearchQuery = '';
  const search = document.getElementById('chatSearchInput');
  if (search) search.value = '';
  document.getElementById('chatSearchBar')?.classList.add('hidden');
  for (const id of ['chatOptionsMenu', 'chatHistoryHelp']) {
    const menu = document.getElementById(id);
    if (menu) menu.open = false;
  }
  if (typeof ChatSocial !== 'undefined') ChatSocial.select();
  if (input) { input.value = conversationDrafts.get(peerId) || ''; input.style.height = 'auto'; }
  document.getElementById('chatContainer')?.classList.toggle('in-conversation', !!peerId);
}
let peerFilter = '';
let inChatSearchQuery = '';
let lastRenderedPeerId = '';
let lastRenderedFingerprint = '';
const renderedMessageKeys = new WeakMap();
const connectingPeers = new Map();
const connectionRetryAt = new Map();
const connectionErrors = new Map();
const sendingPeers = new Set();
const sendCompletions = new Map();
const fileTransfers = new Map(); // fileId -> { percent, received, total, isUpload, path, name, status }
const messagePages = new Map(); // peerId -> { loaded, nextBefore, loading }
const pendingSubmissions = new Map();
const deletedChatMessages = new Set();
let typingTimeout = null;
let isLocallyTyping = false;
let typingPeerId = '';
let lastTypingSentAt = 0;
const remoteTyping = new Map();
const historySaves = new Map();
let clearConversationPeerId = '';
const unreadChatMessages = new Map();
let followChatLatest = true;
let chatViewportAnchor = null;
let chatRowObserver = null;
const observedChatRows = new Set();
let chatAliasPeer = null;
let chatAliases = {};
try { const stored = JSON.parse(localStorage.getItem('quicnet.chatAliases') || '{}'); if (stored && typeof stored === 'object' && !Array.isArray(stored)) chatAliases = stored; } catch {}
let closedConversations = {};
try { const stored = JSON.parse(localStorage.getItem('quicnet.closedConversations') || '{}'); if (stored && typeof stored === 'object' && !Array.isArray(stored)) closedConversations = stored; } catch {}

function saveClosedConversations(next) {
  try { localStorage.setItem('quicnet.closedConversations', JSON.stringify(next)); }
  catch { QP.toast('No se ha podido guardar el estado de las conversaciones.', 'error'); return false; }
  closedConversations = next;
  return true;
}

function reopenConversation(peer) {
  const key = chatAliasKey(peer);
  if (!Object.hasOwn(closedConversations, key)) return;
  const next = {...closedConversations}; delete next[key];
  saveClosedConversations(next);
}

function closeCurrentConversation() {
  const peer = chatTrustedPeers(chatStatus).find(item => item.id === selectedPeerId);
  if (!peer) return;
  const closedAt = Math.max(Date.now(), Number(chatStatus?.chatActivity?.[peer.id]) || 0);
  if (!saveClosedConversations({...closedConversations, [chatAliasKey(peer)]: closedAt})) return;
  unreadChatMessages.delete(peer.id);
  selectChatPeer('');
  showConversationList();
  renderChat();
  QP.toast('Conversación cerrada. Puedes recuperarla en Nueva conversación. El historial se conserva.');
}

function chatAliasKey(peer) { return peer.certHash || peer.id; }

function saveChatAlias(event) {
  event.preventDefault();
  if (!chatAliasPeer) return;
  const key = chatAliasKey(chatAliasPeer), name = document.getElementById('chatAliasInput').value.trim();
  const next = {...chatAliases};
  if (name) next[key] = name; else delete next[key];
  try { localStorage.setItem('quicnet.chatAliases', JSON.stringify(next)); }
  catch { QP.setText('chatAliasError', 'No se ha podido guardar el nombre en este dispositivo.'); document.getElementById('chatAliasError').classList.remove('hidden'); return; }
  chatAliases = next;
  document.getElementById('chatAliasDialog').close();
  lastRenderedFingerprint = '';
  renderChat();
}

function chatConversationVisible() {
  return QP.currentPage === 'chat' && !document.hidden && !inChatSearchQuery &&
    !document.querySelector('#view-chat dialog[open]') &&
    (innerWidth > 680 || document.getElementById('chatContainer')?.classList.contains('in-conversation'));
}

function chatAtBottom(box = document.getElementById('messages')) {
  return !!box && box.scrollHeight - box.scrollTop - box.clientHeight <= 24;
}

function setChatScrollTop(box, top) {
  const behavior = box.style.scrollBehavior;
  box.style.scrollBehavior = 'auto'; box.scrollTop = top; box.style.scrollBehavior = behavior;
}

function captureChatAnchor(box = document.getElementById('messages')) {
  if (!box) return null;
  const top = box.getBoundingClientRect().top;
  const row = [...box.children].find(node => node.dataset.messageId && node.getBoundingClientRect().bottom > top + 1);
  return row ? {peerId:selectedPeerId, id:row.dataset.messageId, offset:row.getBoundingClientRect().top - top} : null;
}

function restoreChatAnchor(anchor, box = document.getElementById('messages')) {
  if (!anchor || !box || anchor.peerId !== selectedPeerId) return;
  const row = [...box.children].find(node => node.dataset.messageId === anchor.id);
  if (row) {
    const delta = row.getBoundingClientRect().top - box.getBoundingClientRect().top - anchor.offset;
    if (Math.abs(delta) > .5) setChatScrollTop(box, box.scrollTop + delta);
  }
}

function updateChatUnreadBadges() {
  const list = document.getElementById('chatPeers');
  if (list) for (const row of list.children) {
    const count = unreadChatMessages.get(row.dataset.peerId)?.size || 0;
    let badge = row.querySelector('.chat-unread-count');
    if (!count) { badge?.remove(); continue; }
    if (!badge) { badge = QP.el('span', 'chat-unread-count'); row.append(badge); }
    badge.textContent = count > 99 ? '99+' : String(count);
    badge.setAttribute('aria-label', `${count} mensaje${count === 1 ? '' : 's'} sin leer`);
  }
  const visibleIds = new Set(chatTrustedPeers(chatStatus).map(peer => peer.id));
  if (chatStatus?.chatTransport?.nostrEnabled) visibleIds.add('global');
  const total = [...unreadChatMessages].reduce((sum, [id, ids]) => sum + (visibleIds.has(id) ? ids.size : 0), 0);
  const nav = document.querySelector('.nav-link[data-page="chat"]');
  let badge = document.getElementById('chatUnreadNavCount');
  if (nav && total) {
    if (!badge) { badge = QP.el('span', 'chat-unread-count'); badge.id = 'chatUnreadNavCount'; nav.append(badge); }
    badge.textContent = total > 99 ? '99+' : String(total);
    badge.setAttribute('aria-label', `${total} mensajes sin leer`);
  } else badge?.remove();
}

function markChatRead() {
  if (chatConversationVisible() && chatAtBottom() && unreadChatMessages.delete(selectedPeerId)) {
    QP.setText('chatNewMessageAnnouncement', ''); updateChatUnreadBadges();
  }
}

function updateChatReadingUi() {
  const box = document.getElementById('messages');
  const button = document.getElementById('chatJumpToLatest');
  if (!box || !button) return;
  const count = unreadChatMessages.get(selectedPeerId)?.size || 0;
  button.classList.toggle('hidden', !selectedPeerId || (chatAtBottom(box) && !count));
  button.textContent = count ? `${count} mensaje${count === 1 ? '' : 's'} nuevo${count === 1 ? '' : 's'} · Ir al último ↓` : 'Ir al último mensaje ↓';
  const label = document.getElementById('chatScrollDate');
  const anchor = captureChatAnchor(box);
  const message = chatStatus?.chatMessages?.find(item => item.peerId === selectedPeerId && item.msgId === anchor?.id);
  const date = messageDate(message);
  if (label) { label.classList.toggle('hidden', !date || followChatLatest || box.scrollTop < 16); if (date) label.textContent = chatDayLabel(date); }
}

function observeChatRows(box) {
  if (!chatRowObserver) return;
  const rows = new Set(box.children);
  for (const row of observedChatRows) if (!rows.has(row)) { chatRowObserver.unobserve(row); observedChatRows.delete(row); }
  for (const row of rows) if (!observedChatRows.has(row)) { chatRowObserver.observe(row); observedChatRows.add(row); }
}

function settleChatMediaLayout() {
  const box = document.getElementById('messages');
  if (!box || QP.currentPage !== 'chat') return;
  if (followChatLatest && !inChatSearchQuery) { setChatScrollTop(box, box.scrollHeight); markChatRead(); }
  else restoreChatAnchor(chatViewportAnchor, box);
  chatViewportAnchor = captureChatAnchor(box);
  updateChatReadingUi();
}

function messageDate(message) {
  const timestamp = Number(message?.timestampMs);
  return Number.isFinite(timestamp) && timestamp > 0 ? new Date(timestamp) : null;
}

function chatDayKey(date) {
  return date ? `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}` : '';
}

function chatDayLabel(date) {
  const today = new Date(), yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  return chatDayKey(date) === chatDayKey(today) ? 'Hoy'
    : chatDayKey(date) === chatDayKey(yesterday) ? 'Ayer'
    : date.toLocaleDateString('es-ES', { day: 'numeric', month: 'short', ...(date.getFullYear() !== today.getFullYear() ? {year:'numeric'} : {}) });
}

function chatTime(message, inList = false) {
  const date = messageDate(message);
  if (date) return inList && chatDayKey(date) !== chatDayKey(new Date()) ? chatDayLabel(date)
    : date.toLocaleTimeString('es-ES', {hour:'2-digit',minute:'2-digit'});
  return String(message?.time || '').replace(/(\d{1,2}:\d{2}):\d{2}/, '$1');
}

function openChatPeer(peerId) {
  selectChatPeer(peerId);
  connectionRetryAt.delete(peerId);
  history.replaceState(null, '', `/chat.html?peer=${encodeURIComponent(peerId)}`);
  renderChat(true);
  loadConversation(peerId);
  document.getElementById('messageInput')?.focus();
}

function renderNewChatContacts() {
  const list = document.getElementById('chatNewPeers');
  if (!list) return;
  const query = document.getElementById('chatNewSearch').value.trim().toLocaleLowerCase('es');
  const peers = chatTrustedPeers(chatStatus).filter(peer => `${peer.name} ${peer.id}`.toLocaleLowerCase('es').includes(query));
  list.replaceChildren();
  for (const peer of peers) {
    const button = QP.button('', 'chat-new-peer', () => {
      document.getElementById('chatNewDialog').close();
      openChatPeer(peer.id);
    });
    const copy = QP.el('span');
    copy.append(QP.identity.createCapsule(peer, {snapshot: chatStatus, interactive: false, size: 'compact'}), QP.el('small', 'muted', `ID · ${peer.id}`));
    button.append(copy); list.append(button);
  }
  if (!peers.length) list.append(QP.el('p', 'empty', query ? 'No hay contactos que coincidan.' : 'Añade un contacto con su invitación para empezar.'));
}

function setupChatWorkspace() {
  const historyMenu = document.getElementById('chatHistoryHelp');
  const historyOptions = document.getElementById('chatHistoryOptions');
  historyOptions?.addEventListener('click', event => {
    const option = event.target.closest('[data-duration]');
    if (!option || option.disabled) return;
    void setDisappearingDuration(Number(option.dataset.duration));
    historyMenu.open = false; historyMenu.querySelector('summary').focus({preventScroll:true});
  });
  historyOptions?.addEventListener('keydown', event => {
    if (!['ArrowUp','ArrowDown','Home','End'].includes(event.key)) return;
    const options = [...historyOptions.querySelectorAll('button:not(:disabled)')];
    if (!options.length) return;
    event.preventDefault(); const index = options.indexOf(document.activeElement);
    options[event.key === 'Home' ? 0 : event.key === 'End' ? options.length-1 : (index + (event.key === 'ArrowUp' ? -1 : 1) + options.length) % options.length].focus();
  });
  historyMenu?.addEventListener('toggle', () => {
    if (historyMenu.open) historyOptions.querySelector('[aria-selected="true"]:not(:disabled)')?.focus({preventScroll:true});
  });
  const box = document.getElementById('messages');
  if (box && typeof ResizeObserver !== 'undefined') {
    chatRowObserver = new ResizeObserver(settleChatMediaLayout);
    chatRowObserver.observe(box);
  }
  document.getElementById('chatJumpToLatest')?.addEventListener('click', () => {
    if (inChatSearchQuery) closeInChatSearch();
    scrollToBottom();
  });
  document.addEventListener('visibilitychange', () => { markChatRead(); updateChatReadingUi(); });
  document.querySelectorAll('#view-chat dialog').forEach(dialog => dialog.addEventListener('close', () => { markChatRead(); updateChatReadingUi(); }));
  document.getElementById('chatLightbox')?.addEventListener('cancel', event => { event.preventDefault(); closeLightbox(); });
  document.getElementById('chatAliasBtn')?.addEventListener('click', () => {
    chatAliasPeer = chatTrustedPeers(chatStatus).find(peer => peer.id === selectedPeerId);
    if (!chatAliasPeer) return;
    document.getElementById('chatOptionsMenu').open = false;
    const input = document.getElementById('chatAliasInput');
    input.value = chatAliases[chatAliasKey(chatAliasPeer)] || '';
    input.placeholder = chatAliasPeer.originalName || chatAliasPeer.name || 'Contacto';
    document.getElementById('chatAliasError').classList.add('hidden');
    document.getElementById('chatAliasDialog').showModal(); input.focus();
  });
  document.getElementById('chatAliasForm')?.addEventListener('submit', saveChatAlias);
  document.getElementById('chatAliasCancel')?.addEventListener('click', () => document.getElementById('chatAliasDialog').close());
  document.getElementById('chatAliasReset')?.addEventListener('click', event => { document.getElementById('chatAliasInput').value = ''; saveChatAlias(event); });
  const handle = document.getElementById('chatListResizer');
  const container = document.getElementById('chatContainer');
  const setWidth = width => {
    width = Math.max(260, Math.min(420, Number(width) || 320));
    container.style.setProperty('--chat-list-width', `${width}px`);
    handle.setAttribute('aria-valuenow', String(width));
    return width;
  };
  if (handle && container) {
    try { setWidth(localStorage.getItem('quicnet.chatListWidth')); } catch { setWidth(320); }
    const saveWidth = () => { try { localStorage.setItem('quicnet.chatListWidth', handle.getAttribute('aria-valuenow')); } catch {} };
    handle.addEventListener('keydown', event => {
      if (!['ArrowLeft','ArrowRight','Home','End'].includes(event.key)) return;
      event.preventDefault();
      setWidth(event.key === 'Home' ? 260 : event.key === 'End' ? 420 : Number(handle.getAttribute('aria-valuenow')) + (event.key === 'ArrowLeft' ? -20 : 20));
      saveWidth();
    });
    handle.addEventListener('pointerdown', event => { handle.setPointerCapture(event.pointerId); event.preventDefault(); });
    handle.addEventListener('pointermove', event => {
      if (handle.hasPointerCapture(event.pointerId)) setWidth(event.clientX - container.getBoundingClientRect().left);
    });
    handle.addEventListener('pointerup', event => { handle.releasePointerCapture(event.pointerId); saveWidth(); });
  }
  document.getElementById('chatNewBtn')?.addEventListener('click', () => {
    document.getElementById('chatNewSearch').value = '';
    renderNewChatContacts();
    document.getElementById('chatNewDialog').showModal();
    document.getElementById('chatNewSearch').focus();
  });
  document.getElementById('chatNewSearch')?.addEventListener('input', renderNewChatContacts);
  document.getElementById('chatNewCloseBtn')?.addEventListener('click', () => document.getElementById('chatNewDialog').close());
  document.getElementById('chatAddContactBtn')?.addEventListener('click', () => {
    document.getElementById('chatNewDialog').close();
    QP.navigate('dashboard');
    document.getElementById('tokenInput')?.focus();
  });
  document.getElementById('chatClearCancelBtn')?.addEventListener('click', () => document.getElementById('chatClearDialog').close());
  document.getElementById('chatClearConfirmBtn')?.addEventListener('click', confirmClearConversation);
  document.addEventListener('click', event => {
    for (const id of ['chatOptionsMenu', 'chatHistoryHelp']) {
      const menu = document.getElementById(id);
      if (menu?.open && !menu.contains(event.target)) menu.open = false;
    }
  });
  document.addEventListener('keydown', event => {
    for (const id of ['chatOptionsMenu', 'chatHistoryHelp']) {
      const menu = document.getElementById(id);
      if (event.key === 'Escape' && menu?.open) { menu.open = false; menu.querySelector('summary').focus(); }
    }
  });
}

// Voice recording state
let mediaRecorder = null;
let voiceRecording = null;
let voiceRecordingPending = false;
let voiceRecordingGeneration = 0;

function scrollToBottom(smooth = false) {
  const box = document.getElementById('messages');
  if (!box) return;
  followChatLatest = true;
  const peerId = selectedPeerId;
  requestAnimationFrame(() => {
    if (peerId !== selectedPeerId) return;
    setChatScrollTop(box, box.scrollHeight);
    chatViewportAnchor = captureChatAnchor(box);
    markChatRead(); updateChatReadingUi();
  });
}

function getMessagesFingerprint(items) {
  if (!items || !items.length) return 'empty';
  const last = items[items.length - 1];
  const confirmations = items.map(m => `${m.isConfirmed ? '1' : '0'}:${m.deliveryState || 'sent'}`).join(',');
  return `${items.length}:${last.msgId}:${confirmations}:${last.time}:${inChatSearchQuery}`;
}

const disappearingLabels = new Map([[0, 'Sin borrado automático local'], [60, '1 minuto'], [3600, '1 hora'],
  [86400, '1 día'], [604800, '1 semana'], [2592000, '30 días']]);

function updateDisappearingControl() {
  const select = document.getElementById('chatExpirySelect');
  const hint = document.getElementById('chatExpiryHint');
  if (!select || !hint) return;
  const seconds = Number(historySaves.get(selectedPeerId) ?? (chatStatus?.disappearingMessages?.[selectedPeerId] || 0));
  select.value = String(seconds);
  const saving = historySaves.has(selectedPeerId);
  select.disabled = !selectedPeerId || saving;
  document.querySelectorAll('#chatHistoryOptions [data-duration]').forEach(option => {
    option.disabled = select.disabled; option.setAttribute('aria-selected', String(Number(option.dataset.duration) === seconds));
  });
  hint.textContent = saving ? 'Guardando preferencia…' : seconds
    ? `Borrar mensajes nuevos tras ${disappearingLabels.get(seconds) || 'el plazo elegido'}`
    : 'Sin borrado automático local';
  document.getElementById('chatHistoryHelp')?.classList.toggle('has-expiry', seconds > 0);
}

async function setDisappearingDuration(seconds) {
  if (!selectedPeerId) return;
  const peerId = selectedPeerId;
  if (historySaves.has(peerId)) return;
  historySaves.set(peerId, seconds);
  updateDisappearingControl();
  try {
    await QP.api('/api/chat/disappearing', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, seconds })
    });
    if (!chatStatus) chatStatus = {};
    if (!chatStatus.disappearingMessages) chatStatus.disappearingMessages = {};
    chatStatus.disappearingMessages[peerId] = seconds;
  } catch (error) {
    QP.toast(error.message, 'error');
  } finally {
    historySaves.delete(peerId);
    updateDisappearingControl();
  }
}

function removeExpiredMessages(ids = null) {
  if (!Array.isArray(chatStatus?.chatMessages)) return;
  const idSet = ids ? new Set(ids) : null;
  const now = Date.now();
  const before = chatStatus.chatMessages.length;
  chatStatus.chatMessages = chatStatus.chatMessages.filter(message =>
    !(idSet?.has(message.msgId) || (message.expiresAtUnixMs && message.expiresAtUnixMs <= now)));
  if (chatStatus.chatMessages.length !== before) {
    const remaining = new Set(chatStatus.chatMessages.map(message => `${message.peerId}:${message.msgId}`));
    for (const [peerId, ids] of unreadChatMessages) {
      for (const id of ids) if (!remaining.has(`${peerId}:${id}`)) ids.delete(id);
      if (!ids.size) unreadChatMessages.delete(peerId);
    }
    renderChat();
  }
}

function formatBytes(bytes) {
  if (!bytes || bytes <= 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + ' ' + sizes[i];
}

function formatAudioDuration(seconds) {
  return Number.isFinite(seconds) && seconds >= 0 ? `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}` : '';
}

const chatAudioDurations = new Map();
let chatAudioDecodeQueue = Promise.resolve();

function measureChatAudioDuration(src) {
  if (chatAudioDurations.has(src)) return Promise.resolve(chatAudioDurations.get(src));
  const operation = chatAudioDecodeQueue.then(async () => {
    const response = await fetch(src, {signal:AbortSignal.timeout(20000)});
    if (!response.ok) throw new Error('No se pudo leer el audio completo.');
    if (Number(response.headers.get('Content-Length')) > 32 * 1024 * 1024) throw new Error('El audio es demasiado grande para analizarlo aquí.');
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength > 32 * 1024 * 1024) throw new Error('El audio es demasiado grande para analizarlo aquí.');
    const Decoder = window.OfflineAudioContext || window.webkitOfflineAudioContext;
    if (!Decoder) throw new Error('El navegador no permite analizar este audio.');
    const buffer = await new Decoder(1, 1, 48000).decodeAudioData(bytes);
    if (!Number.isFinite(buffer.duration) || buffer.duration <= 0) throw new Error('No se pudo calcular la duración.');
    return buffer.duration;
  });
  chatAudioDecodeQueue = operation.catch(() => {});
  chatAudioDurations.set(src, operation);
  operation.then(seconds => { if (chatAudioDurations.get(src) === operation) chatAudioDurations.set(src, seconds); }, () => { if (chatAudioDurations.get(src) === operation) chatAudioDurations.delete(src); });
  while (chatAudioDurations.size > 16) chatAudioDurations.delete(chatAudioDurations.keys().next().value);
  return operation;
}

function chatAudioIcon(kind) {
  const ns = 'http://www.w3.org/2000/svg';
  const icon = document.createElementNS(ns, 'svg');
  icon.setAttribute('viewBox','0 0 24 24'); icon.setAttribute('aria-hidden','true');
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', kind === 'pause' ? 'M8 5v14M16 5v14' : kind === 'volume' ? 'm11 4-6 5H2v6h3l6 5zM16 8a6 6 0 0 1 0 8' : 'm8 5 11 7-11 7z');
  icon.append(path); return icon;
}

function createChatAudioPlayer(src, name, voiceNote = true, metadata = {}) {
  const card = QP.el('div', 'chat-voice-card');
  const heading = QP.el('div', 'chat-voice-heading');
  const duration = QP.el('span', 'chat-voice-duration');
  heading.append(QP.el('strong', '', voiceNote ? 'Nota de voz' : 'Audio'), duration);
  const status = QP.el('span', 'chat-audio-status'); status.setAttribute('role', 'status');
  const audio = document.createElement('audio');
  audio.preload = 'metadata'; audio.className = 'chat-audio-source'; audio.hidden = true;
  const controls = QP.el('div', 'chat-audio-player'); controls.setAttribute('role','group'); controls.hidden = true;
  controls.setAttribute('aria-label', name && !/^voice note$/i.test(name) ? `Reproducir audio: ${name}` : 'Reproducir nota de voz');
  const play = QP.button('', 'chat-audio-play', async () => {
    if (audio.paused) {
      try { if (audio.ended) audio.currentTime = 0; await audio.play(); }
      catch { fail('No se pudo iniciar el audio. Vuelve a intentarlo.'); }
    } else audio.pause();
  });
  play.type = 'button';
  const progress = document.createElement('input');
  progress.type = 'range'; progress.className = 'chat-audio-progress'; progress.min = '0'; progress.max = '0'; progress.step = '0.01'; progress.value = '0';
  progress.setAttribute('aria-label', 'Posición de la nota de voz');
  const time = QP.el('span', 'chat-audio-time', '0:00 / 0:00');
  const volume = QP.el('details', 'chat-audio-volume');
  const volumeButton = QP.el('summary'); volumeButton.setAttribute('aria-label','Volumen del audio'); volumeButton.title = 'Volumen'; volumeButton.append(chatAudioIcon('volume'));
  const volumeInput = document.createElement('input'); volumeInput.type = 'range'; volumeInput.min = '0'; volumeInput.max = '1'; volumeInput.step = '.05'; volumeInput.value = '1'; volumeInput.setAttribute('aria-label','Volumen del audio');
  volumeInput.addEventListener('input', () => { audio.volume = Number(volumeInput.value); });
  volume.append(volumeButton, volumeInput); controls.append(play, progress, time, volume);
  const retry = QP.button('Reintentar audio', 'chat-audio-retry', () => { chatAudioDurations.delete(src); loading(); audio.load(); });
  retry.hidden = true; retry.type = 'button';
  let timer, generation = 0, totalSeconds = 0, pendingDuration = null;
  const cancelTimer = () => clearTimeout(timer);
  const updateProgress = () => {
    if (!totalSeconds) return;
    const current = audio.ended ? totalSeconds : Math.min(totalSeconds, Math.max(0, audio.currentTime || 0));
    progress.value = String(current);
    const totalLabel = formatAudioDuration(Math.ceil(totalSeconds));
    const currentLabel = audio.ended ? totalLabel : formatAudioDuration(current);
    time.textContent = `${currentLabel} / ${totalLabel}`;
    progress.setAttribute('aria-valuetext', `${currentLabel} de ${totalLabel}`);
    const playing = !audio.paused && !audio.ended;
    if (play.dataset.playing !== String(playing)) {
      play.dataset.playing = String(playing); play.replaceChildren(chatAudioIcon(playing ? 'pause' : 'play'));
      play.setAttribute('aria-label', playing ? 'Pausar nota de voz' : 'Reproducir nota de voz');
      play.title = playing ? 'Pausar' : 'Reproducir';
    }
  };
  progress.addEventListener('input', () => {
    try { audio.currentTime = Math.min(totalSeconds, Number(progress.value)); updateProgress(); }
    catch { status.textContent = 'No se pudo cambiar la posición del audio.'; }
  });
  const fail = message => {
    cancelTimer(); ++generation; pendingDuration = null;
    card.dataset.audioState = 'error'; controls.hidden = true; retry.hidden = !src;
    duration.textContent = ''; status.textContent = message;
  };
  const loading = () => {
    cancelTimer(); ++generation; totalSeconds = 0; pendingDuration = null; card.dataset.audioState = 'loading';
    status.textContent = 'Cargando audio…'; duration.textContent = ''; controls.hidden = true; retry.hidden = true;
    timer = setTimeout(() => {
      if (!card.isConnected || !['loading','measuring'].includes(card.dataset.audioState)) return;
      card.dataset.audioState = 'stalled'; status.textContent = 'La carga está tardando. Puedes volver a intentarlo.'; retry.hidden = false;
    }, 15000);
  };
  const ready = async () => {
    if (audio.error) return;
    if (totalSeconds) { updateProgress(); return; }
    if (pendingDuration) return;
    const token = generation;
    const nativeDuration = audio.duration;
    const recorded = /audio\/(webm|ogg)/i.test(metadata.mime || src || '') || /\.(webm|ogg)(?:[?#]|$)/i.test(name || src || '');
    try {
      let seconds = nativeDuration;
      if (recorded || !Number.isFinite(seconds) || seconds <= 0) {
        card.dataset.audioState = 'measuring'; status.textContent = 'Calculando duración del audio…';
        pendingDuration = measureChatAudioDuration(src);
        try { seconds = await pendingDuration; }
        catch (error) {
          const supplied = Number(metadata.durationSeconds);
          if (Number.isFinite(nativeDuration) && nativeDuration > 0) seconds = nativeDuration;
          else if (Number.isFinite(supplied) && supplied > 0) seconds = supplied;
          else throw error;
        }
      }
      if (token !== generation) return;
      totalSeconds = seconds; progress.max = String(seconds);
      cancelTimer(); card.dataset.audioState = 'ready'; controls.hidden = false; retry.hidden = true;
      duration.textContent = formatAudioDuration(Math.ceil(seconds)); status.textContent = 'Audio listo'; updateProgress();
    } catch {
      if (token === generation) fail('No se pudo calcular la duración del audio. Vuelve a intentarlo.');
    } finally { if (token === generation) pendingDuration = null; }
  };
  audio.addEventListener('loadedmetadata', ready);
  audio.addEventListener('canplay', ready);
  audio.addEventListener('durationchange', () => { if (totalSeconds) updateProgress(); else if (audio.readyState >= 1) void ready(); });
  audio.addEventListener('error', () => {
    fail(audio.error?.code === 3 || audio.error?.code === 4
      ? 'No se puede reproducir este audio. El archivo puede no estar disponible o su formato no ser compatible.'
      : 'No se pudo cargar el audio. Comprueba la conexión y vuelve a intentarlo.');
  });
  audio.addEventListener('waiting', () => { status.textContent = 'Cargando audio…'; });
  audio.addEventListener('timeupdate', updateProgress);
  audio.addEventListener('playing', () => { status.textContent = 'Reproduciendo'; updateProgress(); });
  audio.addEventListener('pause', updateProgress);
  audio.addEventListener('ended', () => { status.textContent = 'Reproducción terminada'; updateProgress(); });
  audio.addEventListener('stalled', () => {
    if (card.dataset.audioState !== 'loading') return;
    cancelTimer(); card.dataset.audioState = 'stalled';
    status.textContent = 'La carga está tardando. Puedes volver a intentarlo.'; retry.hidden = false;
  });
  card.append(heading, audio, controls, status, retry);
  if (name && !/^voice note$/i.test(name)) { const label = QP.el('span', 'chat-voice-name', name); label.title = name; card.append(label); }
  if (src) { loading(); audio.src = src; }
  else { card.dataset.audioState = 'error'; status.textContent = 'Audio no disponible: no hay un archivo o enlace válido.'; }
  return card;
}

function configureChatImage(img, src, name) {
  img.title = name || 'Abrir imagen';
  img.alt = name || 'Imagen adjunta';
  img.tabIndex = 0; img.setAttribute('role', 'button');
  img.setAttribute('aria-label', `Abrir imagen${name ? ': ' + name : ''}`);
  img.addEventListener('click', () => openLightbox(src, name));
  img.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); openLightbox(src, name); } });
  img.addEventListener('load', settleChatMediaLayout);
}

function latestConversationMessages() {
  const latest = new Map();
  for (const message of chatStatus?.chatMessages || []) {
    if (message.expiresAtUnixMs && message.expiresAtUnixMs <= Date.now()) continue;
    const previous = latest.get(message.peerId);
    if (!previous || (message.timestampMs || 0) >= (previous.timestampMs || 0)) latest.set(message.peerId, message);
  }
  return latest;
}

function conversationPreview(peerId, latestMessages = latestConversationMessages()) {
  const latest = latestMessages.get(peerId);
  if (!latest) return null;
  let summary = latest.message || '';
  if (typeof ChatSocial !== 'undefined') summary = ChatSocial.decode(summary).text;
  if (summary.startsWith('{')) {
    try {
      const payload = JSON.parse(summary);
      const audioDuration = formatAudioDuration(Number(payload.durationSeconds ?? payload.duration));
      summary = payload.type === 'file_offer' ? (/^voice note(?:\.[a-z0-9]+)?$/i.test(payload.name || '') ? 'Nota de voz' : `📎 ${payload.name || 'Archivo'}`)
        : payload.type === 'audio' ? `Nota de voz${audioDuration ? ' · ' + audioDuration : ''}`
          : payload.type === 'image' ? '🖼️ Imagen'
            : payload.type === 'video' ? '🎬 Vídeo' : summary;
    } catch { }
  }
  summary = summary.replace(/\s+/g, ' ').trim();
  summary = summary.replace(/(Nota de voz\s*:\s*)Voice note\b/gi, 'Nota de voz');
  if (summary.length > 55) summary = summary.slice(0, 52) + '…';
  return { text: `${latest.isMe ? 'Tú: ' : ''}${latest.deliveryState === 'pending' ? '◷ Pendiente · ' : ''}${summary}`, time: chatTime(latest, true) };
}

function chatTrustedPeers(status) {
  const active = QP.trusted(status || {});
  const byId = new Map(active.map(peer => [peer.id, peer]));
  for (const saved of status?.savedPeers || []) {
    if (!saved.id || saved.isTrusted === false || byId.has(saved.id) || !QP.peerInConnectionMode(saved, status)) continue;
    byId.set(saved.id, {
      ...saved,
      endpoint: (saved.addresses || [])[0] || '',
      isTrusted: true,
      isSaved: true,
      isQuicConnected: false
    });
  }
  return Array.from(byId.values()).map(peer => {
    const alias = chatAliases[chatAliasKey(peer)];
    return typeof alias === 'string' && alias.trim() ? {...peer,originalName:peer.name,name:alias.slice(0,80)} : peer;
  });
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
  return QuicNetMarkdown.render(text);
}

function showConversationList() {
  stopTyping();
  document.getElementById('messageInput')?.blur();
  document.getElementById('chatContainer')?.classList.remove('in-conversation');
  // Returning from a direct link must not reopen the conversation on the next visit.
  const url = new URL(location.href);
  if (url.searchParams.has('peer')) {
    url.searchParams.delete('peer');
    history.replaceState(history.state, '', url.pathname + url.search + url.hash);
  }
  const heading = document.querySelector('#view-chat .dm-head strong');
  if (heading) { heading.tabIndex = -1; heading.focus({ preventScroll: true }); }
}

document.addEventListener('DOMContentLoaded', () => {
  setupChatWorkspace();
  // Main chat action buttons
  document.getElementById('chatMobileBackBtn')?.addEventListener('click', showConversationList);
  QP.on('nav_reselected', data => {
    if (data.page === 'chat' && window.matchMedia('(max-width: 680px)').matches)
      showConversationList();
  });
  document.getElementById('messages')?.addEventListener('scroll', event => {
    followChatLatest = chatAtBottom(event.currentTarget);
    chatViewportAnchor = captureChatAnchor(event.currentTarget);
    markChatRead(); updateChatReadingUi();
    if (event.currentTarget.scrollTop < 100) loadOlderMessages(selectedPeerId);
  });
  document.getElementById('messages')?.addEventListener('click', event => {
    const spoiler = event.target.closest('.chat-spoiler');
    if (!spoiler) return;
    const open = spoiler.getAttribute('aria-expanded') !== 'true';
    spoiler.setAttribute('aria-expanded', String(open));
    spoiler.setAttribute('aria-label', open ? 'Ocultar contenido' : 'Mostrar contenido oculto');
  });
  document.addEventListener('click', event => {
    document.querySelectorAll('.message-menu[open]').forEach(menu => { if (!menu.contains(event.target)) menu.open = false; });
  });
  window.addEventListener('resize', () => {
    document.querySelectorAll('.message-menu[open]').forEach(menu => { menu.open = false; });
  });
  document.getElementById('chatConnectBtn')?.addEventListener('click', () => { connectionRetryAt.delete(selectedPeerId); connectChat(selectedPeerId); });
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
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        sendChatMessage();
      }
    });
    msgInput.addEventListener('input', () => {
      msgInput.style.height = 'auto';
      msgInput.style.height = Math.min(msgInput.scrollHeight, 140) + 'px';
      notifyTyping();
    });
    msgInput.addEventListener('blur', stopTyping);
  }
  window.addEventListener('pagehide', () => { stopTyping(); stopVoiceRecording(true); });
  document.addEventListener('visibilitychange', () => { if (document.hidden) stopTyping(); });

  // Header actions: In-chat search & clear
  document.getElementById('chatSearchToggleBtn')?.addEventListener('click', toggleInChatSearch);
  document.getElementById('chatSearchCloseBtn')?.addEventListener('click', closeInChatSearch);
  document.getElementById('chatSearchInput')?.addEventListener('input', onInChatSearchInput);
  document.getElementById('chatClearBtn')?.addEventListener('click', clearCurrentConversation);
  document.getElementById('chatCloseBtn')?.addEventListener('click', closeCurrentConversation);
  document.getElementById('chatExpirySelect')?.addEventListener('change', e =>
    setDisappearingDuration(Number(e.target.value)));

  // Emojis
  document.getElementById('emojiBtn')?.addEventListener('click', toggleEmojiPopover);
  document.getElementById('emojiSearch')?.addEventListener('input', () => { initEmojiGrid(); positionEmojiPopover(); });
  const dismissEmojiOutside = event => {
    if (!document.getElementById('emojiPopover')?.contains(event.target) &&
        !document.getElementById('emojiBtn')?.contains(event.target)) closeEmojiPopover();
  };
  document.addEventListener('pointerdown', dismissEmojiOutside, true);
  document.addEventListener('focusin', dismissEmojiOutside);
  window.addEventListener('blur', () => closeEmojiPopover());
  window.addEventListener('resize', () => closeEmojiPopover());
  document.addEventListener('visibilitychange', () => { if (document.hidden) closeEmojiPopover(); });
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
      document.querySelectorAll('.message-menu[open]').forEach(menu => { menu.open = false; menu.querySelector('summary')?.focus(); });
      closeLightbox();
      closeEmojiPopover(true);
    }
  });

  // Drag and drop support
  setupDragAndDrop();

  // Clipboard paste support
  setupClipboardPaste();


  // Real-time WebSocket subscriptions
  QP.on('status_updated', applyChatStatus);
  QP.on('chat_msg', onRealtimeMessage);
  QP.on('chat_ack', onRealtimeAck);
  QP.on('chat_typing', onRealtimeTyping);
  QP.on('chat_file_progress', onRealtimeFileProgress);
  QP.on('chat_file_complete', onRealtimeFileComplete);
  QP.on('chat_file_cancelled', onRealtimeFileCancelled);
  QP.on('chat_file_paused', onRealtimeFilePaused);
  QP.on('chat_file_verifying', onRealtimeFileVerifying);
  QP.on('chat_file_error', onRealtimeFileError);
  QP.on('chat_file_sent', onRealtimeFileSent);
  QP.on('chat_cleared', onRealtimeChatCleared);
  QP.on('chat_message_deleted', onRealtimeMessageDeleted);
  QP.on('chat_expired', data => removeExpiredMessages(data?.msgIds));
  QP.on('chat_sync', () => refreshChat(true));
  QP.on('peer_connected', () => refreshChat());
  QP.on('peer_disconnected', () => refreshChat());
  QP.on('peer_available', () => refreshChat());
  QP.on('view_changed', data => {
    if (data.page !== 'chat') { closeEmojiPopover(); stopTyping(); stopVoiceRecording(true); }
    renderTypingIndicator();
    if (data.page === 'chat') {
      if (typeof ChatSocial !== 'undefined') ChatSocial.select();
      const p = new URLSearchParams(location.search).get('peer');
      if (p) document.getElementById('chatContainer')?.classList.add('in-conversation');
      if (p && p !== selectedPeerId) {
        selectChatPeer(p);

      }
      connectionRetryAt.delete(selectedPeerId);
      refreshChat();
      scrollToBottom();
    }
  });

  if (typeof ChatSocial !== 'undefined') ChatSocial.configure({peer:()=>selectedPeerId,status:()=>chatStatus,merge:mergeHistory,render:()=>{lastRenderedFingerprint='';renderChatMessagesOnly();}});
  refreshChat();
  setInterval(tickConnecting, 1000);
  setInterval(() => removeExpiredMessages(), 1000);
});

function sendTyping(peerId, isTyping) {
  if (!peerId || peerId === 'global') return;
  QP.api('/api/chat/typing', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ peerId, isTyping }) }).catch(() => {});
}

function stopTyping() {
  clearTimeout(typingTimeout);
  if (typingPeerId) sendTyping(typingPeerId, false);
  typingPeerId = ''; isLocallyTyping = false; lastTypingSentAt = 0;
}

function notifyTyping() {
  if (!document.getElementById('messageInput')?.value.trim() || !isChatConnected(selectedPeerId)) { stopTyping(); return; }
  if (typingPeerId && typingPeerId !== selectedPeerId) stopTyping();
  if (!isLocallyTyping || Date.now() - lastTypingSentAt >= 1500) {
    typingPeerId = selectedPeerId; isLocallyTyping = true; lastTypingSentAt = Date.now();
    sendTyping(typingPeerId, true);
  }
  clearTimeout(typingTimeout);
  typingTimeout = setTimeout(stopTyping, 2500);
}

function onRealtimeMessage(msg) {
  if (!msg) return;
  if (deletedChatMessages.has(`${msg.peerId}:${msg.msgId}`)) return;
  if (!chatStatus) chatStatus = { chatMessages: [] };
  if (!Array.isArray(chatStatus.chatMessages)) chatStatus.chatMessages = [];

  const existing = chatStatus.chatMessages.find(m => m.peerId === msg.peerId && m.msgId === msg.msgId);
  const latestTimestamp = Math.max(0, ...chatStatus.chatMessages.filter(item => item.peerId === msg.peerId).map(item => Number(item.timestampMs) || 0));
  if (!existing) {
    msg.timestampMs ||= Date.now();
    chatStatus.chatMessages.push(msg);
    if (!msg.isMe && msg.timestampMs >= latestTimestamp && !(selectedPeerId === msg.peerId && chatConversationVisible() && followChatLatest)) {
      const unread = unreadChatMessages.get(msg.peerId) || new Set();
      unread.add(msg.msgId); unreadChatMessages.set(msg.peerId, unread);
      if (selectedPeerId === msg.peerId) QP.setText('chatNewMessageAnnouncement', `${unread.size} mensaje${unread.size === 1 ? '' : 's'} nuevo${unread.size === 1 ? '' : 's'} en esta conversación.`);
    }
  } else {
    if (existing.isMe && (existing.isConfirmed || existing.deliveryState === 'sent')) { msg.deliveryState = 'sent'; msg.isConfirmed = existing.isConfirmed || msg.isConfirmed; }
    Object.assign(existing, msg);
  }

  if (!msg.isMe) remoteTyping.delete(msg.peerId);

  renderChat();
  if (selectedPeerId === msg.peerId && msg.isMe) scrollToBottom();
}

function onRealtimeAck(ack) {
  if (!ack || !ack.msgId || !chatStatus?.chatMessages) return;
  const target = chatStatus.chatMessages.find(m => m.msgId === ack.msgId);
  if (target) {
    target.isConfirmed = true;
    target.deliveryState = 'sent';
    renderChat();
  }
}

function onRealtimeTyping(data) {
  if (!data?.peerId) return;
  if (data.isTyping) remoteTyping.set(data.peerId, { name: data.peerName || 'El contacto', until: Date.now() + 5000 });
  else remoteTyping.delete(data.peerId);
  renderTypingIndicator();
}

function renderTypingIndicator() {
  for (const [id, state] of remoteTyping) if (state.until <= Date.now() || !isChatConnected(id)) remoteTyping.delete(id);
  const bar = document.getElementById('chatTypingIndicator');
  const text = document.getElementById('chatTypingText');
  if (!bar) return;
  const state = QP.currentPage === 'chat' ? remoteTyping.get(selectedPeerId) : null;
  if (state && text) text.textContent = `${state.name} está escribiendo…`;
  bar.classList.toggle('hidden', !state);
}

function fileTransferKey(fileId, peerId = selectedPeerId) {
  return `${peerId}:${fileId}`;
}

function onRealtimeFileProgress(data) {
  if (!data) return;
  const existing = fileTransfers.get(fileTransferKey(data.fileId, data.peerId)) || {};
  fileTransfers.set(fileTransferKey(data.fileId, data.peerId), {
    ...existing,
    percent: data.percent,
    received: data.received,
    total: data.total,
    isUpload: !!data.isUpload,
    status: 'transferring'
  });
  updateFileCardDom(data.fileId, data.peerId);
}

function onRealtimeFileComplete(data) {
  if (!data) return;
  const existing = fileTransfers.get(fileTransferKey(data.fileId, data.peerId)) || {};
  fileTransfers.set(fileTransferKey(data.fileId, data.peerId), {
    ...existing,
    percent: 100,
    status: 'completed',
    path: data.path,
    name: data.name
  });
  updateFileCardDom(data.fileId, data.peerId);
}

function onRealtimeFileCancelled(data) {
  if (!data) return;
  const existing = fileTransfers.get(fileTransferKey(data.fileId, data.peerId)) || {};
  fileTransfers.set(fileTransferKey(data.fileId, data.peerId), {
    ...existing,
    status: 'cancelled'
  });
  updateFileCardDom(data.fileId, data.peerId);
}

function onRealtimeFilePaused(data) {
  if (!data) return;
  const key = fileTransferKey(data.fileId, data.peerId);
  if (fileTransfers.get(key)?.status === 'completed') return;
  fileTransfers.set(key, { ...(fileTransfers.get(key) || {}), ...data, isUpload: false, status: 'paused' });
  updateFileCardDom(data.fileId, data.peerId);
}

function onRealtimeFileVerifying(data) {
  if (!data) return;
  const key = fileTransferKey(data.fileId, data.peerId);
  fileTransfers.set(key, { ...(fileTransfers.get(key) || {}), ...data, isUpload: false, status: 'verifying' });
  updateFileCardDom(data.fileId, data.peerId);
}

function onRealtimeFileError(data) {
  if (!data) return;
  fileTransfers.set(fileTransferKey(data.fileId, data.peerId), { ...(fileTransfers.get(fileTransferKey(data.fileId, data.peerId)) || {}), status: 'error', error: data.error });
  updateFileCardDom(data.fileId, data.peerId);
  QP.toast(data.error || 'File transfer failed', 'error');
}

function onRealtimeFileSent(data) {
  if (!data) return;
  fileTransfers.set(fileTransferKey(data.fileId, data.peerId), { ...(fileTransfers.get(fileTransferKey(data.fileId, data.peerId)) || {}), isUpload: true, status: 'sent', percent: 100 });
  updateFileCardDom(data.fileId, data.peerId);
}

function onRealtimeChatCleared(data) {
  if (!data || !chatStatus?.chatMessages) return;
  chatStatus.chatMessages = chatStatus.chatMessages.filter(m => m.peerId !== data.peerId);
  if (chatStatus.chatActivity) delete chatStatus.chatActivity[data.peerId];
  messagePages.delete(data.peerId);
  unreadChatMessages.delete(data.peerId);
  if (selectedPeerId === data.peerId) {
    const allPeers = chatTrustedPeers(chatStatus);
    const peer = allPeers.find(p => p.id === selectedPeerId);
    renderMessages(peer, false);
  }
  renderChat();
}

function onRealtimeMessageDeleted(data) {
  if (!data?.peerId || !data.msgId) return;
  deletedChatMessages.add(`${data.peerId}:${data.msgId}`);
  const unread = unreadChatMessages.get(data.peerId);
  unread?.delete(data.msgId);
  if (unread && !unread.size) unreadChatMessages.delete(data.peerId);
  if (chatStatus?.chatMessages) chatStatus.chatMessages = chatStatus.chatMessages.filter(message => message.peerId !== data.peerId || message.msgId !== data.msgId);
  if (pendingSubmissions.get(data.peerId)?.msgId === data.msgId) pendingSubmissions.delete(data.peerId);
  renderChat();
}

async function deleteChatMessage(item, button, edit = false) {
  if (edit && selectedPeerId === item.peerId && document.getElementById('messageInput')?.value) { QP.toast('Conserva o envía tu borrador actual antes de recuperar otro.'); return; }
  button.disabled = true;
  try {
    await QP.api('/api/chat/message/delete', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:item.peerId,msgId:item.msgId,pendingOnly:item.deliveryState==='pending'})});
    onRealtimeMessageDeleted(item);
    if (edit) {
      const input = document.getElementById('messageInput');
      const text = typeof ChatSocial !== 'undefined' ? ChatSocial.restore(item) : item.message;
      if (selectedPeerId === item.peerId && !input.value) { input.value = text; input.dispatchEvent(new Event('input')); input.focus(); }
      else { conversationDrafts.set(item.peerId, text); QP.toast('Mensaje recuperado como borrador.'); }
    }
    await refreshChat();
  } catch (error) { button.disabled = false; QP.toast(error.message, 'error'); }
}

function messageMenu(item) {
  const menu = QP.el('details', 'message-menu');
  const summary = QP.el('summary', '', '⋯');
  summary.setAttribute('aria-label', 'Opciones del mensaje'); summary.title = 'Opciones del mensaje';
  const actions = QP.el('div', 'message-menu-actions');
  const copy = QP.button('Copiar contenido', '', async () => { await QP.copy(typeof ChatSocial !== 'undefined' ? ChatSocial.decode(item.message).text : item.message); menu.open = false; });
  actions.append(copy);
  if (typeof ChatSocial !== 'undefined') actions.append(ChatSocial.menu(item,menu));
  if (item.deliveryState === 'pending') {
    const edit = QP.button('Recuperar como borrador', '', () => deleteChatMessage(item, edit, true));
    actions.append(edit);
  }
  const remove = QP.button(item.deliveryState === 'pending' ? 'Cancelar mensaje pendiente' : 'Eliminar de este dispositivo', 'danger', () => deleteChatMessage(item, remove));
  actions.append(remove); menu.append(summary, actions);
  menu.addEventListener('toggle', () => {
    if (!menu.open) return;
    document.querySelectorAll('.message-menu[open]').forEach(other => { if (other !== menu) other.open = false; });
    const anchor = summary.getBoundingClientRect();
    actions.style.left = `${Math.max(8, Math.min(anchor.right - actions.offsetWidth, innerWidth - actions.offsetWidth - 8))}px`;
    actions.style.top = `${Math.max(8, Math.min(anchor.bottom + 4, innerHeight - actions.offsetHeight - 8))}px`;
  });
  return menu;
}

function mergeHistory(messages) {
  if (!chatStatus) chatStatus = {};
  if (!Array.isArray(chatStatus.chatMessages)) chatStatus.chatMessages = [];
  const known = new Map(chatStatus.chatMessages.map(m => [`${m.peerId}:${m.msgId}`, m]));
  for (const item of messages) {
    if (deletedChatMessages.has(`${item.peerId}:${item.msgId}`)) continue;
    const key = `${item.peerId}:${item.msgId}`;
    const previous = known.get(key);
    if (previous) {
      if (previous.isMe && (previous.isConfirmed || previous.deliveryState === 'sent')) { item.deliveryState = 'sent'; item.isConfirmed = previous.isConfirmed || item.isConfirmed; }
      Object.assign(previous, item);
    }
    else { chatStatus.chatMessages.push(item); known.set(key, item); }
  }
  chatStatus.chatMessages.sort((a, b) => (a.timestampMs || 0) - (b.timestampMs || 0) || String(a.msgId).localeCompare(String(b.msgId)));
}

async function loadConversation(peerId, force = false) {
  if (!peerId) return;
  const state = messagePages.get(peerId) || { loaded: false, nextBefore: null, loading: false };
  if (state.loading || (state.loaded && !force)) return;
  state.loading = true;
  messagePages.set(peerId, state);
  try {
    const transfersBefore = new Map(fileTransfers);
    const page = await QP.api(`/api/chat/messages?peerId=${encodeURIComponent(peerId)}&limit=50`);
    for (const download of page.downloads || []) {
      const key = fileTransferKey(download.fileId, peerId);
      if (fileTransfers.get(key) === transfersBefore.get(key)) {
        fileTransfers.set(key, { ...download, isUpload: false });
        updateFileCardDom(download.fileId, peerId);
      }
    }
    mergeHistory(page.messages || []);
    if (!state.loaded) state.nextBefore = page.nextBefore || null;
    state.loaded = true;
    renderChat();
  } catch (error) {
    console.warn('Could not load local chat history:', error);
  } finally {
    state.loading = false;
  }
}

async function loadOlderMessages(peerId) {
  const state = messagePages.get(peerId);
  if (!state?.nextBefore || state.loading) return;
  const box = document.getElementById('messages');
  state.loading = true;
  try {
    const page = await QP.api(`/api/chat/messages?peerId=${encodeURIComponent(peerId)}&limit=50&before=${encodeURIComponent(state.nextBefore)}`);
    mergeHistory(page.messages || []);
    state.nextBefore = page.nextBefore || null;
    if (selectedPeerId === peerId) {
      const peer = peerId === 'global' ? { id: 'global', name: 'Chat Global' }
        : chatTrustedPeers(chatStatus).find(item => item.id === peerId);
      if (peer) renderMessages(peer, false, true);
    }
  } catch (error) {
    console.warn('Could not load older chat messages:', error);
  } finally {
    state.loading = false;
  }
}

async function refreshChat(forceHistory = false) {
  try {
    const status = await QP.api('/api/status');
    applyChatStatus(status);
    const ids = chatTrustedPeers(chatStatus).map(peer => peer.id);
    if (status.chatTransport?.nostrEnabled || selectedPeerId === 'global') ids.unshift('global');
    await Promise.allSettled(ids.map(id => loadConversation(id, forceHistory && id === selectedPeerId)));
  } catch(e) {
    QP.toast(e.message, 'error');
  }
}

function isChatConnected(peerId) {
  return !!peerId && (chatStatus?.activeChats || []).some(c => c.peerId === peerId);
}

function applyChatStatus(status) {
  if (!status) return;
  chatStatus = { ...status, chatMessages: chatStatus?.chatMessages || [] };
  const selected = [...(status.peers || []), ...(status.savedPeers || [])].find(peer => peer.id === selectedPeerId);
  if (selected && !QP.peerInConnectionMode(selected, status)) {
    selectChatPeer('');
    showConversationList();
    for (const id of ['chatClearDialog', 'chatAliasDialog']) document.getElementById(id)?.close();
  }
  if (document.getElementById('chatNewDialog')?.open) renderNewChatContacts();
  if (typingPeerId && !isChatConnected(typingPeerId)) stopTyping();
  renderChat();
}

function maybeConnectSelectedChat() {
  if (QP.currentPage !== 'chat' || !selectedPeerId || selectedPeerId === 'global') return;
  if (isChatConnected(selectedPeerId) || connectingPeers.has(selectedPeerId)) return;
  if ((connectionRetryAt.get(selectedPeerId) || 0) > Date.now()) return;
  if (!chatTrustedPeers(chatStatus).some(p => p.id === selectedPeerId)) return;
  void connectChat(selectedPeerId);
}

function tickConnecting() {
  maybeConnectSelectedChat();
  renderTypingIndicator();
}

function renderChat(forceScroll = false) {
  const latest = latestConversationMessages();
  const activity = peer => Math.max(Number(chatStatus?.chatActivity?.[peer.id]) || 0, latest.get(peer.id)?.timestampMs || 0);
  const allPeers = chatTrustedPeers(chatStatus).sort((a, b) => activity(b) - activity(a) || String(a.name || '').localeCompare(String(b.name || ''), 'es') || String(a.id).localeCompare(String(b.id)));
  const visiblePeers = allPeers.filter(peer => {
    const closedAt = closedConversations[chatAliasKey(peer)];
    if (closedAt && (selectedPeerId === peer.id || activity(peer) > closedAt)) {
      reopenConversation(peer);
      return true;
    }
    return !closedAt;
  });
  const names = new Map();
  for (const peer of allPeers) names.set(peer.name, (names.get(peer.name) || 0) + 1);
  // A private conversation must never fall back to the public channel.
  updateDisappearingControl();
  renderTypingIndicator();

  const list = document.getElementById('chatPeers'); QP.clear(list);

  // 1. Public channel is paused unless the server explicitly enables it.
  if (chatStatus?.chatTransport?.nostrEnabled) {
  const isGlobalSelected = selectedPeerId === 'global';
  const globalRow = QP.el('button', `dm-peer ${isGlobalSelected ? 'selected' : ''}`);
  globalRow.dataset.peerId = 'global';
  globalRow.type = 'button';
  const globalAvatar = QP.el('div', 'avatar', '🌐');
  const globalCopy = QP.el('div', 'dm-peer-copy');
  const globalTitle = QP.el('div', 'dm-peer-name', 'Canal público');
  const globalTitleLine = QP.el('div', 'dm-peer-title-line');
  const globalPreview = conversationPreview('global', latest);
  globalTitleLine.appendChild(globalTitle);
  if (globalPreview?.time) globalTitleLine.appendChild(QP.el('time', 'dm-peer-time', globalPreview.time));
  const relayCount = chatStatus?.mailbox?.connectedRelays ?? 0;
  const globalMeta = QP.el('div', 'dm-peer-meta', `Red NOSTR · ${relayCount} relé${relayCount === 1 ? '' : 's'}`);
  globalCopy.append(globalTitleLine, globalMeta);
  if (globalPreview) globalCopy.appendChild(QP.el('div', 'dm-peer-preview', globalPreview.text));
  globalRow.append(globalAvatar, globalCopy, QP.el('span', `presence ${relayCount > 0 ? 'online' : ''}`));
  globalRow.addEventListener('click', () => {
    document.getElementById('chatContainer')?.classList.add('in-conversation');
    selectChatPeer('global');
    history.replaceState(null, '', '/chat.html?peer=global');
    renderChat(true);
    scrollToBottom();
    loadConversation('global');
  });
  list.appendChild(globalRow);
  }

  // 2. Direct peer list
  const peers = visiblePeers.filter(p => !peerFilter || `${p.name} ${p.endpoint} ${p.id}`.toLowerCase().includes(peerFilter));
  if (!visiblePeers.length && !peers.length) {
    list.appendChild(QP.el('div', 'empty', 'No hay conversaciones abiertas. Usa Nueva conversación para elegir un contacto.'));
  } else if (!peers.length && peerFilter) {
    list.appendChild(QP.el('div', 'empty', 'No hay contactos que coincidan con tu búsqueda.'));
  }

  peers.forEach(peer => {
    const active = (chatStatus?.activeChats || []).some(c => c.peerId === peer.id);
    const isConnecting = connectingPeers.has(peer.id);

    const row = QP.el('button', `dm-peer ${peer.id === selectedPeerId ? 'selected' : ''}`);
    row.dataset.peerId = peer.id;
    row.type = 'button';
    const copy = QP.el('div', 'dm-peer-copy');
    const title = QP.el('div', 'dm-peer-name');
    title.append(QP.identity.createCapsule(peer, {snapshot: chatStatus, slot: 'chat-peer', size: 'compact', interactive: false}));
    title.title = peer.name || 'Contacto';
    const titleLine = QP.el('div', 'dm-peer-title-line');
    const preview = conversationPreview(peer.id, latest);
    titleLine.appendChild(title);
    if (preview?.time) titleLine.appendChild(QP.el('time', 'dm-peer-time', preview.time));
    const presence = QP.userPresence?.(peer, chatStatus);
    const statusLabel = isConnecting ? 'Conectando…' : presence?.label || (active ? 'Conectado' : peer.isQuicConnected ? 'Disponible' : 'Sin conexión');
    const meta = QP.el('div', 'dm-peer-meta', statusLabel);
    if (presence) {
      const dot = QP.el('span', 'user-presence-dot'); dot.dataset.userPresence = presence.state;
      dot.title = presence.label; dot.setAttribute('aria-hidden', 'true'); meta.prepend(dot);
    }
    copy.append(titleLine, meta);
    if (names.get(peer.name) > 1) {
      const identity = QP.el('div', 'dm-peer-device', `ID · …${peer.id.slice(-8)}`);
      identity.title = `Identidad del contacto: ${peer.id}`;
      copy.append(identity);
    }
    if (preview) copy.appendChild(QP.el('div', 'dm-peer-preview', preview.text));
    row.append(copy);
    row.setAttribute('aria-current', peer.id === selectedPeerId ? 'true' : 'false');
    row.title = `${peer.name || 'Contacto'} · ${statusLabel}${names.get(peer.name) > 1 ? ` · ${peer.id}` : ''}`;
    row.addEventListener('click', () => openChatPeer(peer.id));
    list.appendChild(row);
  });
  updateChatUnreadBadges();
  const aliasButton = document.getElementById('chatAliasBtn');
  if (aliasButton) aliasButton.disabled = !allPeers.some(peer => peer.id === selectedPeerId);
  const closeButton = document.getElementById('chatCloseBtn');
  if (closeButton) closeButton.disabled = !allPeers.some(peer => peer.id === selectedPeerId);

  // 3. Conversation Pane Setup
  if (selectedPeerId === 'global') {
    QP.identity.mount(document.getElementById('chatIdentity'), {name: 'Canal público'}, {snapshot: chatStatus, nameId: 'chatPeerName', avatarId: 'chatAvatar'});
    const enabled = chatStatus?.chatTransport?.nostrEnabled === true;
    const relCount = enabled ? (chatStatus?.mailbox?.connectedRelays ?? 0) : 0;
    QP.setText('chatPeerMeta', enabled ? 'Canal público' : 'Canal público pausado · Tu historial sigue disponible');
    QP.setText('chatAvatar', '🌐');

    const connectBtn = document.getElementById('chatConnectBtn');
    if (connectBtn) connectBtn.classList.add('hidden');

    const inputs = ['messageInput', 'sendBtn', 'emojiBtn', 'voiceRecordBtn'];
    inputs.forEach(id => {
      const el = document.getElementById(id);
      if (el) el.disabled = relCount === 0;
    });
    ['chatSearchToggleBtn', 'chatClearBtn'].forEach(id => {
      const el = document.getElementById(id);
      if (el) el.disabled = false;
    });

    const attachBtn = document.getElementById('attachBtn');
    if (attachBtn) attachBtn.disabled = true;
    QP.setText('sendBtn', 'Enviar');
    QP.setText('chatComposerHint', enabled ? 'Canal público · Enter para enviar. Los adjuntos no están disponibles.' : 'Canal público pausado. Tu historial sigue disponible.');
    document.getElementById('chatComposerHint')?.classList.remove('is-offline');

    const msgInput = document.getElementById('messageInput');
    if (msgInput) {
      msgInput.placeholder = enabled ? 'Mensaje para el canal público…' : 'El canal público está temporalmente desactivado';
    }

    renderMessages({ id: 'global', name: 'Chat Global' }, forceScroll);
    return;
  }

  const peer = allPeers.find(p => p.id === selectedPeerId);
  const connected = !!peer && isChatConnected(peer.id);
  const isConnecting = !!peer && connectingPeers.has(peer.id);
  const failed = !!peer && connectionErrors.has(peer.id);
  QP.identity.mount(document.getElementById('chatIdentity'), peer || {name: 'Tus conversaciones empiezan aquí'},
    {snapshot: chatStatus, slot: 'chat-header', nameId: 'chatPeerName', avatarId: 'chatAvatar'});
  const presence = peer && QP.userPresence?.(peer, chatStatus);
  QP.setText('chatPeerMeta', !peer ? 'Selecciona alguien de la lista'
    : connected ? `Conversación privada · ${presence?.state === 'unknown' ? 'Conectado · Estado desconocido' : presence?.state === 'offline' ? 'Conectado' : presence?.label || 'Conectado'}`
    : isConnecting ? 'Conectando con este contacto…'
    : 'Sin conexión con este contacto');

  const connectBtn = document.getElementById('chatConnectBtn');
  if (connectBtn) {
    connectBtn.className = 'btn ghost sm';
    connectBtn.classList.toggle('hidden', !failed || connected || isConnecting);
    if (!connectBtn.firstElementChild) connectBtn.replaceChildren(QP.el('span','chat-retry-icon','↻'),QP.el('span','chat-retry-label','Reintentar'));
    connectBtn.setAttribute('aria-label', 'Reintentar conexión'); connectBtn.title = 'Reintentar conexión';
    connectBtn.disabled = isConnecting;
  }
  for (const id of ['messageInput', 'emojiBtn']) {
    const control = document.getElementById(id);
    if (control) control.disabled = !peer;
  }
  for (const id of ['attachBtn', 'voiceRecordBtn']) {
    const control = document.getElementById(id);
    if (control) {
      control.disabled = !connected || sendingPeers.has(peer?.id) || (id === 'voiceRecordBtn' && (voiceRecordingPending || !!voiceRecording));
      control.title = !connected ? 'Disponible al conectar con este contacto' : id === 'attachBtn' ? 'Adjuntar archivo o imagen' : 'Grabar mensaje de voz';
    }
  }
  const send = document.getElementById('sendBtn');
  if (send) {
    send.disabled = !peer || sendingPeers.has(peer.id);
    send.textContent = peer && !connected ? 'Dejar pendiente' : 'Enviar';
    send.title = connected ? 'Enviar mensaje' : 'Guardar pendiente y enviar automáticamente al conectar';
  }
  const hint = document.getElementById('chatComposerHint');
  if (hint) {
    hint.classList.toggle('is-offline', !!peer && !connected);
    hint.textContent = !peer ? 'Elige una conversación para escribir.' : connected ? 'Enter para enviar · Mayús + Enter para una nueva línea.'
      : 'Se enviará automáticamente al reconectar. Puedes cancelarlo en las opciones del mensaje. Adjuntos y voz disponibles al conectar.';
  }
  for (const id of ['chatSearchToggleBtn', 'chatClearBtn']) {
    const control = document.getElementById(id);
    if (control) control.disabled = !peer;
  }
  const input = document.getElementById('messageInput');
  if (input) input.placeholder = !peer ? 'Elige una conversación' : 'Escribe un mensaje…';
  renderMessages(peer, forceScroll);
  maybeConnectSelectedChat();
}

function renderMessages(peer, forceScroll = false, preserveScroll = false) {
  const box = document.getElementById('messages');
  if (!box) return;

  if (!peer) {
    lastRenderedPeerId = '';
    lastRenderedFingerprint = '';
    QP.clear(box);
    const welcome = QP.el('div', 'empty-state');
    welcome.append(QP.el('h3', '', 'Un mensaje puede acercarte'), QP.el('p', '', 'Elige un contacto para hablar en privado o añade uno desde Inicio.'));
    const link = QP.el('a', 'btn', 'Añadir un contacto'); link.href = '/'; welcome.append(link);
    box.appendChild(welcome);
    return;
  }

  let items = (chatStatus?.chatMessages || []).filter(m =>
    m.peerId === peer.id && (!m.expiresAtUnixMs || m.expiresAtUnixMs > Date.now()));
  if (inChatSearchQuery) {
    items = items.filter(m => m.message && m.message.toLowerCase().includes(inChatSearchQuery.toLowerCase()));
    const badge = document.getElementById('chatSearchCount');
    if (badge) badge.textContent = `${items.length} coincidencia${items.length === 1 ? '' : 's'}`;
  }

  const identityKey = JSON.stringify([peer.name, peer.certHash, peer.isTor, peer.networkType, peer.activeTransport, peer.onionAddress]);
  const currentFingerprint = `${getMessagesFingerprint(items)}:${identityKey}:${chatDayKey(new Date())}:${chatStatus?.avatars?.[peer.id] || ''}:${typeof ChatSocial !== 'undefined' ? ChatSocial.signature(peer.id) : 0}`;

  if (peer.id === lastRenderedPeerId && currentFingerprint === lastRenderedFingerprint && !forceScroll) {
    markChatRead(); updateChatReadingUi();
    return;
  }

  const isPeerChanged = peer.id !== lastRenderedPeerId;
  const shouldFollow = !preserveScroll && !inChatSearchQuery && (forceScroll || isPeerChanged || followChatLatest);
  const anchor = shouldFollow ? null : captureChatAnchor(box);

  lastRenderedPeerId = peer.id;
  lastRenderedFingerprint = currentFingerprint;

  if (!items.length) {
    QP.clear(box);
    const welcome = QP.el('div','chat-welcome');
    if (inChatSearchQuery) {
      welcome.appendChild(QP.el('strong', '', `No hay mensajes que coincidan con «${inChatSearchQuery}».`));
    } else {
      welcome.appendChild(QP.el('strong', '', peer.id === 'global'
        ? 'Estás en el canal público' : `Esta es tu conversación con ${peer.name || 'este contacto'}.`));
      welcome.appendChild(document.createElement('br'));
      welcome.appendChild(QP.el('small', 'muted', peer.id === 'global'
        ? 'Canal público descentralizado mediante Nostr.'
        : 'Los mensajes se intercambian de forma privada con este contacto.'));
    }
    box.appendChild(welcome);
    return;
  }

  const existingRows = new Map(isPeerChanged ? [] : [...box.children].filter(row => row.dataset.messageId).map(row => [row.dataset.messageId,row]));
  const rows = [], restoreFocus = [];
  items.forEach(item => {
    const previous = existingRows.get(item.msgId);
    const key = JSON.stringify([item,chatStatus?.avatars?.[peer.id],typeof ChatSocial !== 'undefined' ? ChatSocial.rowSignature(peer.id,item.msgId) : '']);
    if (previous && renderedMessageKeys.get(previous) === key) {
      const author = previous.querySelector('.message-author');
      if (author?.classList.contains('ci')) QP.identity.updateCapsule(author, peer, {snapshot: chatStatus});
      else if (author) author.textContent = peer.id === 'global' ? item.sender : peer.name;
      const avatar = previous.querySelector('.msg-avatar');
      if (avatar) { avatar.title = peer.name || item.sender || 'Contacto'; if (!avatar.querySelector('img')) avatar.textContent = QP.initials(peer.name); }
      rows.push(previous); return;
    }
    const wrap = QP.el('div', `message-row ${item.isMe ? 'me' : ''}`);
    wrap.dataset.messageId = item.msgId;
    if (!item.isMe && peer.id === 'global') {
      const avatar = QP.el('div','avatar msg-avatar',QP.initials(item.sender || peer.name));
      QP.paintAvatar?.(avatar, peer, chatStatus); avatar.removeAttribute('data-user-presence'); avatar.title = peer.name || item.sender || 'Contacto'; wrap.appendChild(avatar);
    }
    const body = QP.el('div','message-body');
    const head = QP.el('div','message-meta');
    const time = QP.el('time', 'message-timestamp', chatTime(item));
    const date = messageDate(item);
    if (date) { time.dateTime = date.toISOString(); time.title = date.toLocaleString('es-ES'); }
    if (!item.isMe) {
      if (peer.id === 'global') head.appendChild(QP.el('span', 'message-author', item.sender));
      else {
        const identity = QP.identity.createCapsule(peer, {snapshot: chatStatus, size: 'compact'});
        identity.classList.add('message-author'); head.append(identity); wrap.classList.add('has-identity');
      }
    }
    head.appendChild(time);
    if (item.viaNostr) head.appendChild(QP.el('span', 'message-route', ' · 📬'));
    if (item.isGlobal) head.appendChild(QP.el('span', 'message-route', ' · 🌐'));
    if (item.expiresAtUnixMs) {
      const expiry = new Date(item.expiresAtUnixMs).toLocaleString('es-ES', { dateStyle: 'short', timeStyle: 'short' });
      head.appendChild(QP.el('span', 'message-expiry', ` · ◷ ${expiry}`));
    }
    const pending = item.isMe && ['pending', 'sending'].includes(item.deliveryState);
    if (item.isMe) {
      const label = pending ? item.deliveryState === 'sending' ? ' · Enviando…' : ' · ◷ Pendiente' : item.isConfirmed ? ' · ✓✓ Entregado' : ' · ✓ Enviado';
      const delivery = QP.el('span', `chat-check ${pending ? 'pending-label' : item.isConfirmed ? 'confirmed' : ''}`, label);
      if (item.deliveryState === 'pending') delivery.title = 'Esperando conexión. Se enviará automáticamente al conectar.';
      head.appendChild(delivery);
    }
    const bubble = QP.el('div',`message ${item.isMe ? 'me' : ''}${pending ? ' pending' : ''}`);
    appendMessageContent(bubble, item.message, item);
    bubble.appendChild(messageMenu(item));
    body.append(head, bubble);
    if (typeof ChatSocial !== 'undefined') ChatSocial.decorate(item,body);
    wrap.appendChild(body);
    renderedMessageKeys.set(wrap,key);
    const oldMenu = previous?.querySelector('.message-menu');
    if (oldMenu?.open) {
      const menu = wrap.querySelector('.message-menu'); menu.open = true;
      if (!oldMenu.querySelector('.message-reaction-picker')?.classList.contains('hidden')) menu.querySelector('.message-reaction-picker')?.classList.remove('hidden');
      if (oldMenu.contains(document.activeElement)) {
        const focused = document.activeElement, label = focused.getAttribute('aria-label') || focused.textContent;
        restoreFocus.push(() => [...menu.querySelectorAll('summary,button')].find(button => (button.getAttribute('aria-label') || button.textContent) === label)?.focus({preventScroll:true}));
      }
    }
    rows.push(wrap);
  });
  // Reuse rows and media while adding date boundaries and grouping nearby messages.
  const timeline = [];
  const separators = new Map([...box.querySelectorAll('.chat-date-separator')].map(node => [node.dataset.day, node]));
  rows.forEach((row, index) => {
    const item = items[index], previous = items[index - 1];
    const date = messageDate(item), day = chatDayKey(date);
    const sameDay = day === chatDayKey(messageDate(previous));
    if (date && (!index || !sameDay)) {
      const separator = separators.get(day) || QP.el('div', 'chat-date-separator');
      separator.dataset.day = day; separator.textContent = chatDayLabel(date);
      timeline.push(separator);
    }
    const grouped = !!previous && sameDay && date && messageDate(previous) && item.isMe === previous.isMe && item.sender === previous.sender
      && Number(item.timestampMs) >= Number(previous.timestampMs) && Number(item.timestampMs) - Number(previous.timestampMs) < 300000;
    row.classList.toggle('message-grouped', !!grouped);
    timeline.push(row);
  });
  // Keep unchanged rows attached: delivery acknowledgements must not close menus or restart media.
  const retained = new Set(timeline);
  for (const child of [...box.children]) if (!retained.has(child)) child.remove();
  let cursor = box.firstChild;
  for (const row of timeline) { if (row === cursor) cursor = cursor.nextSibling; else box.insertBefore(row,cursor); }
  restoreFocus.forEach(restore => restore());

  if (shouldFollow) scrollToBottom();
  else { restoreChatAnchor(anchor, box); chatViewportAnchor = captureChatAnchor(box); }
  observeChatRows(box);
  updateChatReadingUi();
}

function renderChatMessagesOnly() {
  if (selectedPeerId === 'global') {
    renderMessages({ id: 'global', name: 'Chat Global' }, false);
    return;
  }
  const allPeers = chatTrustedPeers(chatStatus);
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
    configureChatImage(img, viewUrl, fileName);
    preview.appendChild(img);
  } else if (catCls === 'cat-video') {
    const video = document.createElement('video');
    video.controls = true;
    video.preload = 'metadata';
    video.src = viewUrl;
    video.className = 'chat-media-video';
    video.addEventListener('loadeddata', settleChatMediaLayout);
    preview.appendChild(video);
  } else if (catCls === 'cat-audio') {
    preview.appendChild(createChatAudioPlayer(viewUrl, fileName, false));
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
    if (obj.type === 'text' && typeof obj.text === 'string') {
      const content = typeof ChatSocial !== 'undefined' ? ChatSocial.decode(raw) : {text:obj.text,mentions:[]};
      const text = QP.el('div','message-text');text.innerHTML = QuicNetMarkdown.render(content.text,{mentions:content.mentions});container.append(text);return;
    }
    // 1. INLINE IMAGE
    if (obj.type === 'image') {
      let src = QP.safeDataUrl(obj.data, 'image');
      if (!src && typeof obj.data === 'string' && (obj.data.startsWith('http://') || obj.data.startsWith('https://') || obj.data.startsWith('/api/'))) {
        src = obj.data;
      }
      if (!src && obj.fileId) {
        src = `/api/chat/view-file?fileId=${encodeURIComponent(obj.fileId)}`;
      }

      if (src) {
        if (obj.name && !/^(?:IMG[-_]|image[-_]|Screenshot[-_])/i.test(obj.name)) container.appendChild(QP.el('div', 'message-attachment-title', obj.name));
        const img = document.createElement('img');
        img.src = src;
        img.alt = obj.name || 'Image';
        img.className = 'chat-media-img';
        configureChatImage(img, src, obj.name);
        img.onerror = () => {
          img.style.display = 'none';
          const errDiv = QP.el('div', 'chat-media-error', 'No se pudo cargar la imagen.');
          container.appendChild(errDiv);
        };
        container.appendChild(img);
      } else {
        const errDiv = QP.el('div', 'chat-media-error', 'Imagen no disponible: formato o enlace no permitido.');
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

      container.appendChild(createChatAudioPlayer(src, obj.name, true, obj));
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

      if (obj.name) container.appendChild(QP.el('div', 'message-attachment-title', obj.name));

      if (src) {
        const video = document.createElement('video');
        video.controls = true;
        video.preload = 'metadata';
        video.src = src;
        video.className = 'chat-media-video';
        video.addEventListener('loadeddata', settleChatMediaLayout);
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
      const src = QP.safeDataUrl(obj.data, 'file');
      if (src) {
        const link = QP.el('a', 'btn sm accent', `Download ${obj.name || 'file'}`);
        link.href = src;
        link.download = String(obj.name || 'file').replace(/[\\/:*?"<>|]/g, '_');
        container.appendChild(link);
        return;
      }
      container.appendChild(QP.el('div', 'chat-media-error', 'Archivo no disponible: formato o enlace no permitido.'));
      return;
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
  const transfer = fileTransfers.get(fileTransferKey(fileId)) || {};

  const wrapper = QP.el('div', 'chat-file-wrapper');
  wrapper.dataset.fileId = fileId;
  wrapper.dataset.isMe = String(!!item.isMe);

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
  } else if (transfer.status === 'paused') {
    statusLabel = `Paused · ${formatBytes(transfer.received || 0)} / ${formatBytes(fileSize)}`;
  } else if (transfer.status === 'verifying') {
    statusLabel = `Checking saved data · ${formatBytes(transfer.received || 0)}`;
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
  } else if (['downloading', 'transferring', 'verifying'].includes(transfer.status)) {
    if (!transfer.isUpload) {
      const pauseBtn = QP.el('button', 'btn sm', 'Pause');
      pauseBtn.type = 'button';
      pauseBtn.addEventListener('click', () => pauseChatFile(fileId));
      actionCol.appendChild(pauseBtn);
    }
    const cancelBtn = QP.el('button', 'btn sm danger', '✕');
    cancelBtn.type = 'button';
    cancelBtn.title = 'Cancel transfer';
    cancelBtn.addEventListener('click', () => cancelChatFile(fileId));
    actionCol.appendChild(cancelBtn);
  } else if (item.isMe) {
    const label = QP.el('span', 'file-card-tag', 'Sent');
    actionCol.appendChild(label);
  } else {
    const dlBtn = QP.el('button', 'btn sm primary', transfer.received > 0 ? '▶ Resume' : '⬇️ Download');
    dlBtn.type = 'button';
    dlBtn.addEventListener('click', () => downloadChatFile(fileId, fileName, fileSize));
    actionCol.appendChild(dlBtn);
    if (transfer.received > 0) {
      const discardBtn = QP.el('button', 'btn sm danger', '✕');
      discardBtn.type = 'button';
      discardBtn.title = 'Discard download';
      discardBtn.addEventListener('click', () => cancelChatFile(fileId));
      actionCol.appendChild(discardBtn);
    }
  }

  card.append(iconCol, infoCol, actionCol);
  wrapper.appendChild(card);
  return wrapper;
}

function updateFileCardDom(fileId, peerId = selectedPeerId) {
  if (peerId !== selectedPeerId) return;
  const wrappers = document.querySelectorAll(`.chat-file-wrapper[data-file-id="${fileId}"]`);
  wrappers.forEach(wrapper => {
    const card = wrapper.querySelector(`.chat-file-card[data-file-id="${fileId}"]`);
    if (!card) return;
    const transfer = fileTransfers.get(fileTransferKey(fileId)) || {};
    const metaEl = card.querySelector('.file-card-meta');
    const fillEl = card.querySelector('.file-card-progress-fill');
    const trackEl = card.querySelector('.file-card-progress');
    const actionsEl = card.querySelector('.file-card-actions');
    const nameEl = card.querySelector('.file-card-name');
    const fileName = nameEl ? nameEl.textContent : (transfer.name || 'file');
    const cat = getFileCategory(fileName);

    const pct = Math.min(100, Math.max(0, transfer.percent || 0));
    if (fillEl) fillEl.style.width = `${pct}%`;

    if (['downloading', 'transferring', 'verifying'].includes(transfer.status)) {
      if (actionsEl && !transfer.isUpload && wrapper.dataset.isMe !== 'true') {
        QP.clear(actionsEl);
        const pauseBtn = QP.el('button', 'btn sm', 'Pause');
        pauseBtn.type = 'button';
        pauseBtn.addEventListener('click', () => pauseChatFile(fileId));
        const cancelBtn = QP.el('button', 'btn sm danger', '✕');
        cancelBtn.type = 'button';
        cancelBtn.title = 'Discard download';
        cancelBtn.addEventListener('click', () => cancelChatFile(fileId));
        actionsEl.append(pauseBtn, cancelBtn);
      }
      if (trackEl) trackEl.classList.add('active');
      if (metaEl) {
        const recStr = formatBytes(transfer.received || 0);
        const totStr = formatBytes(transfer.total || 0);
        metaEl.textContent = `${transfer.isUpload ? 'Uploading' : 'Downloading'} ${pct}% (${recStr} / ${totStr})`;
        if (transfer.status === 'verifying') metaEl.textContent = `Checking saved data · ${recStr}`;
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
    } else if (transfer.status === 'error') {
      if (trackEl) trackEl.classList.remove('active');
      if (metaEl) metaEl.textContent = transfer.error || 'Transfer failed';
    } else if (transfer.status === 'sent') {
      if (trackEl) trackEl.classList.remove('active');
      if (metaEl) metaEl.textContent = 'Received and verified by peer';
    }
    if (['paused', 'error', 'cancelled'].includes(transfer.status) && !transfer.isUpload && wrapper.dataset.isMe !== 'true') {
      if (trackEl) trackEl.classList.remove('active');
      if (metaEl && transfer.status === 'paused') metaEl.textContent = `Paused · ${formatBytes(transfer.received || 0)} / ${formatBytes(transfer.total || 0)}`;
      if (actionsEl) {
        QP.clear(actionsEl);
        const resumeBtn = QP.el('button', 'btn sm primary', transfer.received > 0 ? '▶ Resume' : '⬇️ Download');
        resumeBtn.type = 'button';
        resumeBtn.addEventListener('click', () => downloadChatFile(fileId, fileName, transfer.total || 0));
        actionsEl.appendChild(resumeBtn);
        if (transfer.received > 0) {
          const discardBtn = QP.el('button', 'btn sm danger', '✕');
          discardBtn.type = 'button';
          discardBtn.title = 'Discard download';
          discardBtn.addEventListener('click', () => cancelChatFile(fileId));
          actionsEl.appendChild(discardBtn);
        }
      }
    }
  });
}

async function downloadChatFile(fileId, name, size) {
  if (!selectedPeerId) return;
  const peerId = selectedPeerId;
  const key = fileTransferKey(fileId, peerId);
  fileTransfers.set(key, { ...(fileTransfers.get(key) || { percent: 0, received: 0 }), total: size, isUpload: false, status: 'downloading', name });
  updateFileCardDom(fileId, peerId);
  try {
    const res = await QP.api('/api/chat/download-file', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, fileId, name, size })
    });
    if (res && res.cached && res.path) {
      fileTransfers.set(fileTransferKey(fileId, peerId), { percent: 100, received: size, total: size, isUpload: false, status: 'completed', path: res.path, name });
      updateFileCardDom(fileId, peerId);
      QP.toast(`File ready: ${name}`, 'success');
    } else if (res?.download && fileTransfers.get(key)?.status === 'downloading') {
      fileTransfers.set(key, { ...fileTransfers.get(key), ...res.download, isUpload: false });
      updateFileCardDom(fileId, peerId);
    }
  } catch (e) {
    if (['paused', 'completed', 'cancelled'].includes(fileTransfers.get(key)?.status)) return;
    fileTransfers.set(key, { ...fileTransfers.get(key), status: 'error', error: e.message });
    updateFileCardDom(fileId, peerId);
    QP.toast(e.message, 'error');
  }
}

async function pauseChatFile(fileId) {
  const peerId = selectedPeerId;
  if (!peerId) return;
  try {
    await QP.api('/api/chat/pause-file', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({peerId, fileId})});
    const key = fileTransferKey(fileId, peerId);
    if (fileTransfers.get(key)?.status !== 'completed') {
      fileTransfers.set(key, { ...fileTransfers.get(key), status: 'paused' });
      updateFileCardDom(fileId, peerId);
    }
  } catch (e) { QP.toast(e.message, 'error'); }
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
  const peerId = selectedPeerId;
  try {
    await QP.api('/api/chat/cancel-file', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId, fileId })
    });
    fileTransfers.set(fileTransferKey(fileId, peerId), { status: 'cancelled' });
    updateFileCardDom(fileId, peerId);
  } catch (e) {
    QP.toast(e.message, 'error');
  }
}

async function connectChat(peerId = selectedPeerId) {
  if (!peerId || peerId === 'global' || isChatConnected(peerId) || connectingPeers.has(peerId)) return;
  const peer = chatTrustedPeers(chatStatus).find(p => p.id === peerId);
  if (!peer) return;
  const attempt = { expireAt: Date.now() + 25000 };
  connectingPeers.set(peerId, attempt);
  connectionErrors.delete(peerId);
  renderChat();
  const post = (path, body) => QP.api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  try {
    // Saved contacts may need to be discovered before the existing protocol endpoint can open chat.
    if (!(chatStatus?.peers || []).some(p => p.id === peerId)) {
      await post('/api/connect-saved-peer', { certHash: peer.certHash });
      while (!(chatStatus?.peers || []).some(p => p.id === peerId)) {
        if (Date.now() >= attempt.expireAt) throw new Error('El contacto no está disponible.');
        await new Promise(resolve => setTimeout(resolve, 500));
        applyChatStatus(await QP.api('/api/status'));
      }
    }
    if (!isChatConnected(peerId)) await post('/api/connect-peer', { peerId, protocol: 'chat' });
    while (!isChatConnected(peerId)) {
      if (Date.now() >= attempt.expireAt) throw new Error('El contacto no ha respondido.');
      await new Promise(resolve => setTimeout(resolve, 500));
      applyChatStatus(await QP.api('/api/status'));
    }
    connectionRetryAt.delete(peerId);
  } catch (error) {
    connectionErrors.set(peerId, error.message);
    connectionRetryAt.set(peerId, Date.now() + 30000);
  } finally {
    connectingPeers.delete(peerId);
    renderChat();
  }
}

async function sendChatMessage(payload, peerId = selectedPeerId) {
  const input = document.getElementById('messageInput');
  const originalDraft = input?.value || '';
  const message = payload ?? originalDraft.trim();
  if (!message || !peerId || (payload === undefined && sendingPeers.has(peerId))) return false;
  const queueText = peerId !== 'global' && payload === undefined;
  const replyTo = queueText && typeof ChatSocial !== 'undefined' ? ChatSocial.reply(peerId) : null;
  const wireMessage = queueText && typeof ChatSocial !== 'undefined' ? ChatSocial.encode(message,peerId,replyTo) : message;
  if (queueText && !chatTrustedPeers(chatStatus).some(peer => peer.id === peerId)) return false;
  const previous = sendCompletions.get(peerId);
  let complete;
  const completion = new Promise(resolve => { complete = resolve; });
  sendCompletions.set(peerId, completion);
  sendingPeers.add(peerId);
  renderChat();
  try {
    if (previous) await previous;
    if (!queueText && (peerId === 'global' ? !chatStatus?.chatTransport?.nostrEnabled : !isChatConnected(peerId))) {
      if (selectedPeerId === peerId) maybeConnectSelectedChat();
      throw new Error('No se pudo enviar el adjunto: el contacto ya no está conectado. Vuelve a intentarlo al conectar.');
    }
    if (queueText) {
      let submission = pendingSubmissions.get(peerId);
      if (!submission || submission.message !== wireMessage) {
        submission = {peerId,message:wireMessage,msgId:crypto.randomUUID()}; pendingSubmissions.set(peerId, submission);
      }
      const response = await QP.api('/api/chat/outbox', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(submission)});
      // WS may already carry a newer delivery state than the HTTP response.
      if (response.message && !chatStatus.chatMessages?.some(item => item.peerId === peerId && item.msgId === response.message.msgId)) onRealtimeMessage(response.message);
      pendingSubmissions.delete(peerId);
      if (typeof ChatSocial !== 'undefined') ChatSocial.sent(peerId,replyTo);
      maybeConnectSelectedChat();
    } else {
      await QP.api('/api/chat-send', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId,message})});
    }
    if (payload === undefined) {
      if (selectedPeerId === peerId && input.value === originalDraft) {
        input.value = ''; input.style.height = ''; conversationDrafts.delete(peerId);
      } else if (conversationDrafts.get(peerId) === originalDraft) conversationDrafts.delete(peerId);
      if (typingPeerId === peerId) stopTyping();
    }
    if (selectedPeerId === peerId) scrollToBottom(true);
    return true;
  } catch (error) {
    QP.toast(error.message, 'error');
    return false;
  } finally {
    if (sendCompletions.get(peerId) === completion) { sendCompletions.delete(peerId); sendingPeers.delete(peerId); }
    complete();
    renderChat();
  }
}

async function sendAttachment(file) {
  const peerId = selectedPeerId;
  if (!file || !peerId) return;

  // 1. If image, audio, or video clip (<= 10 MB), send inline for instant display in memory
  if ((file.type.startsWith('image/') || file.type.startsWith('audio/') || file.type.startsWith('video/')) && file.size <= 10 * 1024 * 1024) {
    try {
      const data = await readDataUrl(file);
      let type = 'file';
      if (file.type.startsWith('image/')) type = 'image';
      else if (file.type.startsWith('audio/')) type = 'audio';
      else if (file.type.startsWith('video/')) type = 'video';
      const sent = await sendChatMessage(JSON.stringify({ type, name: file.name, mime: file.type || 'application/octet-stream', size: file.size, data }), peerId);
      if (sent) document.getElementById('fileInput').value = '';
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
    const res = await fetch(`/api/chat/share-file?peerId=${encodeURIComponent(peerId)}`, {
      method: 'POST',
      headers: {
        'X-Peer-Id': peerId,
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
    r.onerror = () => reject(new Error(r.error?.message || 'No se pudo leer el archivo adjunto.'));
    r.readAsDataURL(file);
  });
}

// Voice Recording Handlers
async function startVoiceRecording() {
  const peerId = selectedPeerId;
  if (!peerId || voiceRecordingPending || voiceRecording) return;
  if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
    QP.toast('Microphone access is not available in this environment.', 'error');
    return;
  }
  const generation = ++voiceRecordingGeneration;
  voiceRecordingPending = true;
  document.getElementById('voiceRecordingBar')?.classList.remove('hidden');
  document.getElementById('voiceRecordingTimer').textContent = '00:00';
  document.getElementById('voiceSendBtn').disabled = true;
  const name = chatTrustedPeers(chatStatus).find(peer => peer.id === peerId)?.name || 'este contacto';
  document.querySelector('.voice-recording-label').textContent = `Nota para ${name}`;
  renderChat();
  let stream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    if (generation !== voiceRecordingGeneration) { stream.getTracks().forEach(track => track.stop()); return; }
    const recorder = new MediaRecorder(stream);
    const session = {recorder,stream,peerId,chunks:[],discard:true,timer:null,startedAt:Date.now()};
    voiceRecording = session; mediaRecorder = recorder;
    recorder.ondataavailable = event => { if (event.data?.size > 0) session.chunks.push(event.data); };
    recorder.onstop = async () => {
      clearInterval(session.timer);
      stream.getTracks().forEach(track => track.stop());
      if (voiceRecording === session) { voiceRecording = null; mediaRecorder = null; document.getElementById('voiceRecordingBar')?.classList.add('hidden'); renderChat(); }
      try {
        if (!session.discard && session.chunks.length) {
          const mime = recorder.mimeType || 'audio/webm';
          const blob = new Blob(session.chunks, {type:mime});
          if (blob.size) await sendChatMessage(JSON.stringify({type:'audio',name:'Voice note',mime,size:blob.size,durationSeconds:Math.max(0,(Date.now()-session.startedAt)/1000),data:await readDataUrl(blob)}), peerId);
        }
      } catch (error) { QP.toast(error.message, 'error'); }
      finally { session.chunks.length = 0; }
    };
    recorder.onerror = event => { if (voiceRecording === session) stopVoiceRecording(true); QP.toast(event.error?.message || 'No se pudo grabar la nota.', 'error'); };
    recorder.start(200);
    document.getElementById('voiceSendBtn').disabled = false;
    const started = Date.now();
    session.timer = setInterval(() => {
      const seconds = Math.floor((Date.now() - started) / 1000);
      document.getElementById('voiceRecordingTimer').textContent = `${String(Math.floor(seconds / 60)).padStart(2,'0')}:${String(seconds % 60).padStart(2,'0')}`;
    }, 1000);
  } catch (err) {
    stream?.getTracks().forEach(track => track.stop());
    if (generation === voiceRecordingGeneration) { voiceRecording = null; mediaRecorder = null; document.getElementById('voiceRecordingBar')?.classList.add('hidden'); }
    QP.toast(`Microphone error: ${err.message}`, 'error');
  } finally { if (generation === voiceRecordingGeneration) voiceRecordingPending = false; renderChat(); }
}

function stopVoiceRecording(discard = false) {
  ++voiceRecordingGeneration;
  voiceRecordingPending = false;
  document.getElementById('voiceRecordingBar')?.classList.add('hidden');
  const session = voiceRecording;
  voiceRecording = null; mediaRecorder = null;
  if (session) {
    session.discard = discard; clearInterval(session.timer);
    try { if (session.recorder.state !== 'inactive') session.recorder.stop(); }
    finally { session.stream.getTracks().forEach(track => track.stop()); }
  }
  renderChat();
}

// Emoji Popover
const chatEmojis = [
  ['😀', 'Sonrisa', 'grinning smile feliz happy cara face'],
  ['😃', 'Sonrisa con ojos grandes', 'smiley happy feliz'],
  ['😄', 'Sonrisa con ojos sonrientes', 'smile happy feliz'],
  ['😁', 'Sonrisa radiante', 'grin beaming dientes teeth feliz'],
  ['😆', 'Risa con ojos cerrados', 'laughing squinting reir'],
  ['😅', 'Sonrisa con sudor', 'sweat smile nervios'],
  ['😂', 'Lágrimas de risa', 'joy tears laugh reir lol'],
  ['🤣', 'Rodando de risa', 'rofl rolling laughing reir lol'],
  ['😊', 'Sonrisa sonrojada', 'blush smile feliz happy'],
  ['😇', 'Ángel', 'angel innocent halo inocente'],
  ['🙂', 'Sonrisa leve', 'slightly smiling happy feliz'],
  ['😉', 'Guiño', 'wink winking'],
  ['😍', 'Ojos de corazón', 'heart eyes amor love enamorado'],
  ['🥰', 'Cara con corazones', 'smiling hearts amor love enamorado'],
  ['😘', 'Beso', 'kiss kissing amor love'],
  ['😋', 'Saboreando comida', 'yum delicious rico lengua tongue'],
  ['😎', 'Gafas de sol', 'sunglasses cool genial'],
  ['🤔', 'Pensativo', 'thinking pensar duda'],
  ['🤫', 'Silencio', 'shushing quiet secreto'],
  ['🤐', 'Boca cerrada', 'zipper mouth secreto quiet'],
  ['😴', 'Dormido', 'sleeping sleep sueño dormir'],
  ['🥳', 'Cara de fiesta', 'partying party celebrar'],
  ['🤯', 'Cabeza explotando', 'exploding head asombro sorpresa'],
  ['😱', 'Grito de miedo', 'scream fear susto sorpresa'],
  ['🔥', 'Fuego', 'fire hot calor llama'],
  ['👍', 'Pulgar arriba', 'thumbs up like bien ok si'],
  ['👎', 'Pulgar abajo', 'thumbs down dislike mal no'],
  ['👏', 'Aplausos', 'clap clapping bravo'],
  ['🙌', 'Manos levantadas', 'raised hands celebrar hurra'],
  ['🤝', 'Apretón de manos', 'handshake acuerdo trato'],
  ['❤️', 'Corazón rojo', 'heart red amor love'],
  ['💖', 'Corazón brillante', 'sparkling heart amor love'],
  ['💯', 'Cien puntos', 'hundred points perfecto'],
  ['🚀', 'Cohete', 'rocket espacio lanzamiento'],
  ['🎉', 'Confeti', 'party popper fiesta celebrar'],
  ['👀', 'Ojos', 'eyes mirar ver'],
  ['⚡', 'Rayo', 'zap lightning electricidad energia'],
  ['☕', 'Café', 'coffee tea te bebida hot drink'],
  ['🎮', 'Videojuegos', 'video game gaming mando jugar'],
  ['📌', 'Chincheta', 'pushpin fijar pin'],
  ['📎', 'Clip', 'paperclip adjunto attachment'],
  ['🔒', 'Candado', 'lock locked seguridad privado'],
  ['📦', 'Paquete', 'package box caja envio']
];

function normalizeEmojiSearch(value) {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim();
}

function initEmojiGrid() {
  const grid = document.getElementById('emojiGrid');
  if (!grid) return;
  const terms = normalizeEmojiSearch(document.getElementById('emojiSearch')?.value || '').split(/\s+/).filter(Boolean);
  const emojis = chatEmojis.filter(emoji => terms.every(term => normalizeEmojiSearch(emoji.join(' ')).includes(term)));
  QP.clear(grid);
  emojis.forEach(([emo, name]) => {
    const btn = QP.el('button', 'emoji-item', emo);
    btn.type = 'button';
    btn.title = name;
    btn.setAttribute('aria-label', name);
    btn.addEventListener('click', () => {
      closeEmojiPopover();
      insertEmoji(emo);
    });
    grid.appendChild(btn);
  });
  document.getElementById('emojiEmpty')?.classList.toggle('hidden', emojis.length > 0);
}

function toggleEmojiPopover() {
  const popover = document.getElementById('emojiPopover');
  if (!popover) return;
  if (!popover.classList.contains('hidden')) { closeEmojiPopover(); return; }
  const search = document.getElementById('emojiSearch');
  if (search) search.value = '';
  initEmojiGrid();
  popover.classList.remove('hidden');
  document.getElementById('emojiBtn')?.setAttribute('aria-expanded', 'true');
  positionEmojiPopover();
  search?.focus({preventScroll: true});
}

function positionEmojiPopover() {
  const popover = document.getElementById('emojiPopover');
  const button = document.getElementById('emojiBtn');
  if (!popover || !button || popover.classList.contains('hidden')) return;
  const anchor = button.getBoundingClientRect();
  popover.style.left = `${Math.max(12, Math.min(anchor.left, window.innerWidth - popover.offsetWidth - 12))}px`;
  popover.style.top = `${Math.max(12, anchor.top - popover.offsetHeight - 8)}px`;
}

function closeEmojiPopover(restoreFocus = false) {
  const popover = document.getElementById('emojiPopover');
  const wasOpen = popover && !popover.classList.contains('hidden');
  popover?.classList.add('hidden');
  const button = document.getElementById('emojiBtn');
  button?.setAttribute('aria-expanded', 'false');
  if (restoreFocus && wasOpen) button?.focus({preventScroll: true});
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
  clearConversationPeerId = selectedPeerId;
  const name = selectedPeerId === 'global' ? 'Canal público' : chatTrustedPeers(chatStatus).find(peer => peer.id === selectedPeerId)?.name || 'este contacto';
  document.getElementById('chatOptionsMenu').open = false;
  QP.setText('chatClearDescription', `Se borrarán el historial local con ${name} y sus mensajes pendientes de envío. Las copias de tu contacto no se eliminarán. Esta acción no se puede deshacer.`);
  document.getElementById('chatClearError').classList.add('hidden');
  document.getElementById('chatClearDialog').showModal();
  document.getElementById('chatClearCancelBtn').focus();
}

async function confirmClearConversation() {
  const peerId = clearConversationPeerId;
  if (!peerId) return;
  const confirmButton = document.getElementById('chatClearConfirmBtn');
  const cancelButton = document.getElementById('chatClearCancelBtn');
  confirmButton.disabled = true;
  cancelButton.disabled = true;
  try {
    await QP.api('/api/chat/clear', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ peerId })
    });
    onRealtimeChatCleared({ peerId });
    document.getElementById('chatClearDialog').close();
    QP.toast('Historial eliminado de este dispositivo');
  } catch (e) {
    QP.setText('chatClearError', `No se ha podido borrar el historial: ${e.message}`);
    document.getElementById('chatClearError').classList.remove('hidden');
  } finally {
    confirmButton.disabled = false;
    cancelButton.disabled = false;
    if (!document.getElementById('chatClearDialog').open) document.getElementById('chatOptionsMenu').querySelector('summary').focus();
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
  if (!box.open) box.showModal();
  document.getElementById('lightboxCloseBtn')?.focus();
}

function closeLightbox() {
  const box = document.getElementById('chatLightbox');
  if (box?.open) box.close();
  box?.classList.add('hidden');
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
