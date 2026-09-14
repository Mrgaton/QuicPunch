(() => {
  const toastHost = document.createElement('div');
  toastHost.className = 'toast-host';

  const ROUTES = {
    '/': 'dashboard',
    '/index.html': 'dashboard',
    '/chat.html': 'chat',
    '/call.html': 'voice',
    '/vpn.html': 'vpn',
    '/speedtest.html': 'speedtest'
  };

  const PAGE_TITLES = {
    dashboard: 'Network · QuicPunch',
    chat: 'Direct Chat · QuicPunch',
    voice: 'Voice Studio · QuicPunch',
    vpn: 'LAN Bridge · QuicPunch',
    speedtest: 'Speed Test · QuicPunch'
  };

  const PAGE_SUBTITLES = {
    dashboard: 'Private peer network',
    chat: 'Direct Chat',
    voice: 'Voice Studio',
    vpn: 'LAN Bridge',
    speedtest: 'Bandwidth & Latency Benchmark'
  };

  const PAGE_URLS = {
    dashboard: '/',
    chat: '/chat.html',
    voice: '/call.html',
    vpn: '/vpn.html',
    speedtest: '/speedtest.html'
  };

  let currentPage = 'dashboard';
  const seenNotificationIds = new Set();
  let notificationsBootstrapped = false;
  const eventListeners = new Map();
  let activeSocket = null;
  let isConnected = false;
  let fallbackPollTimer = null;
  const state = {
    status: null,
    get isWsConnected() { return isConnected; }
  };

  function on(event, callback) {
    if (!eventListeners.has(event)) eventListeners.set(event, new Set());
    eventListeners.get(event).add(callback);
    return () => off(event, callback);
  }

  function off(event, callback) {
    if (eventListeners.has(event)) {
      eventListeners.get(event).delete(callback);
    }
  }

  function emit(event, data) {
    if (eventListeners.has(event)) {
      for (const cb of eventListeners.get(event)) {
        try { cb(data); } catch (err) { console.error('Error in event listener', err); }
      }
    }
  }

  function getPageFromUrl(url = location.pathname) {
    const clean = String(url || '').split('?')[0].toLowerCase();
    return ROUTES[clean] || 'dashboard';
  }

  function navigate(targetPageOrUrl, targetUrl = null, pushState = true) {
    let page = targetPageOrUrl;
    let url = targetUrl;

    if (typeof targetPageOrUrl === 'string' && targetPageOrUrl.startsWith('/')) {
      page = getPageFromUrl(targetPageOrUrl);
      url = targetPageOrUrl;
    } else if (!url) {
      url = PAGE_URLS[page] || '/';
    }

    if (pushState && (location.pathname + location.search) !== url) {
      try {
        history.pushState({ page }, '', url);
      } catch (e) { }
    }

    currentPage = page;
    document.body.dataset.page = page;
    document.title = PAGE_TITLES[page] || 'QuicPunch';

    const sub = document.getElementById('topbarSubtitle');
    if (sub) sub.textContent = PAGE_SUBTITLES[page] || 'Private peer network';

    // Update nav links active state
    document.querySelectorAll('.nav-link').forEach(link => {
      if (link.dataset.page === page) link.classList.add('active');
      else link.classList.remove('active');
    });

    // Toggle SPA views visibility
    document.querySelectorAll('.spa-view').forEach(view => {
      const viewPage = view.id.replace('view-', '');
      if (viewPage === page) {
        view.classList.remove('hidden');
      } else {
        view.classList.add('hidden');
      }
    });

    emit('view_changed', { page, url });
  }

  document.addEventListener('DOMContentLoaded', () => {
    document.body.appendChild(toastHost);

    // Intercept navigation clicks
    document.addEventListener('click', e => {
      const a = e.target.closest('a');
      if (!a) return;

      const rawHref = (a.getAttribute('href') || '').trim();
      if (rawHref.startsWith('file:') || rawHref.startsWith('file:///')) {
        e.preventDefault();
        toast('El navegador no permite abrir enlaces locales file:/// directamente. Usa los botones de carpeta.', 'warn');
        return;
      }

      let href = '';
      try {
        href = a.href || rawHref;
      } catch {
        href = rawHref;
      }

      if (!href || href.startsWith('file:')) return;

      try {
        const url = new URL(href, location.origin);
        if (url.origin === location.origin) {
          const path = url.pathname.toLowerCase();
          if (ROUTES[path] || a.dataset.page) {
            e.preventDefault();
            const targetPage = a.dataset.page || ROUTES[path];
            navigate(targetPage, url.pathname + url.search);
          }
        }
      } catch { }
    });

    window.addEventListener('popstate', () => {
      const page = getPageFromUrl(location.pathname);
      navigate(page, location.pathname + location.search, false);
    });

    // Initial SPA route setup
    const initialPage = getPageFromUrl(location.pathname);
    navigate(initialPage, location.pathname + location.search, false);

    initWebSocket();
  });

  window.addEventListener('pagehide', () => {
    try {
      if (navigator.sendBeacon) {
        navigator.sendBeacon('/api/client-closing');
      } else {
        fetch('/api/client-closing', { method: 'POST', keepalive: true }).catch(() => {});
      }
    } catch (e) { }
  });

  window.addEventListener('beforeunload', () => {
    try {
      if (activeSocket) {
        try { activeSocket.close(); } catch (e) { }
      }
      if (navigator.sendBeacon) {
        const csrfParam = window.__qp_csrf ? `?csrf=${encodeURIComponent(window.__qp_csrf)}` : '';
        navigator.sendBeacon('/api/client-closing' + csrfParam);
      }
    } catch (e) { }
  });

  function startFallbackPoll() {
    if (fallbackPollTimer) return;
    fallbackPollTimer = setInterval(async () => {
      if (isConnected) {
        stopFallbackPoll();
        return;
      }
      try {
        const s = await api('/api/status');
        if (s) {
          state.status = s;
          processNotifications(s);
          emit('status_updated', s);
        }
      } catch (e) { }
    }, 45000);
  }

  function stopFallbackPoll() {
    if (fallbackPollTimer) {
      clearInterval(fallbackPollTimer);
      fallbackPollTimer = null;
    }
  }

  function requestSync() {
    if (activeSocket && activeSocket.readyState === WebSocket.OPEN) {
      try {
        activeSocket.send(JSON.stringify({ type: 'sync' }));
        return true;
      } catch { }
    }
    return false;
  }

  async function getStatus(force = false) {
    if (!force && state.status) return state.status;
    const s = await api('/api/status');
    if (s) {
      state.status = s;
      processNotifications(s);
      emit('status_updated', s);
    }
    return s;
  }

  function initWebSocket() {
    try {
      const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
      const csrfParam = window.__qp_csrf ? `?csrf=${encodeURIComponent(window.__qp_csrf)}` : '';
      const wsUrl = `${protocol}//${location.host}/ws${csrfParam}`;
      activeSocket = new WebSocket(wsUrl);
      activeSocket.binaryType = 'arraybuffer';

      activeSocket.onopen = () => {
        isConnected = true;
        stopFallbackPoll();
        emit('ws_status', { connected: true });
        requestSync();
      };

      activeSocket.onmessage = (evt) => {
        try {
          if (evt.data instanceof ArrayBuffer) {
            emit('ws_binary', evt.data);
            return;
          }
          const payload = JSON.parse(evt.data);
          if (payload && payload.type) {
            if (payload.type === 'status_updated' && payload.data) {
              state.status = payload.data;
              processNotifications(payload.data);
            }
            emit(payload.type, payload.data);
            if (payload.type === 'notification' && payload.data) {
              const n = payload.data;
              if (n.id && !seenNotificationIds.has(n.id)) {
                seenNotificationIds.add(n.id);
                toast(n.message, n.type || 'info');
              }
            }
          }
        } catch (e) { }
      };

      activeSocket.onclose = () => {
        isConnected = false;
        startFallbackPoll();
        emit('ws_status', { connected: false });
        setTimeout(initWebSocket, 2000);
      };

      activeSocket.onerror = () => {
        try { activeSocket.close(); } catch (e) { }
      };
    } catch (e) {
      startFallbackPoll();
      setTimeout(initWebSocket, 3000);
    }
  }

  function processNotifications(status) {
    if (!status || !Array.isArray(status.notifications)) return;
    if (!notificationsBootstrapped) {
      notificationsBootstrapped = true;
      status.notifications.forEach(n => seenNotificationIds.add(n.id));
      return;
    }
    status.notifications.forEach(n => {
      if (!seenNotificationIds.has(n.id)) {
        seenNotificationIds.add(n.id);
        toast(n.message, n.type || 'info');
      }
    });
  }

  async function api(path, options = {}) {
    const response = await fetch(path, options);
    let data = null;
    try { data = await response.json(); } catch { data = {}; }
    if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
    if (data && data.notifications) processNotifications(data);
    return data;
  }

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

  function canonicalId(value) {
    if (!value) return '—';
    const s = String(value).trim();
    if (/^[0-9a-fA-F]{16,64}$/.test(s)) {
      return s.slice(0, 16).toLowerCase();
    }
    if (/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(s)) {
      return s.replace(/-/g, '').slice(0, 16).toLowerCase();
    }
    try {
      const b64 = s.replace(/-/g, '+').replace(/_/g, '/');
      const bin = atob(b64);
      let hex = '';
      for (let i = 0; i < Math.min(bin.length, 8); i++) {
        hex += bin.charCodeAt(i).toString(16).padStart(2, '0');
      }
      if (hex.length >= 8) return hex.slice(0, 16).toLowerCase();
    } catch { }
    return s.slice(0, 16).toLowerCase();
  }

  function shortId(value, size = 16) { return canonicalId(value); }
  function initials(name) {
    const parts = String(name || 'P').trim().split(/\s+/).filter(Boolean);
    return (parts.length > 1 ? parts[0][0] + parts[1][0] : parts[0]?.slice(0, 2) || 'P').toUpperCase();
  }
  function fmtBytes(bytes) {
    const n = Number(bytes || 0);
    if (n < 1024) return `${n} B`;
    if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} KB`;
    if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`;
    return `${(n / 1024 ** 3).toFixed(1)} GB`;
  }
  function peerMeta(peer) {
    if (peer.isTor) return peer.onionAddress || 'Tor peer';
    return peer.endpoint || (peer.addresses || []).join(', ') || shortId(peer.id, 14);
  }
  function trusted(status) { return (status.peers || []).filter(p => p.isTrusted); }
  function findPeer(status, id) { return (status.peers || []).find(p => p.id === id); }
  function button(label, style = '', handler) {
    const node = el('button', `btn sm ${style}`.trim(), label);
    if (handler) node.addEventListener('click', handler);
    return node;
  }
  function toast(message, kind = '') {
    const node = el('div', `toast ${kind}`.trim(), message);
    toastHost.appendChild(node);
    setTimeout(() => node.remove(), kind === 'error' ? 4500 : 2800);
  }
  async function copy(text) {
    try { await navigator.clipboard.writeText(text); toast('Copied to clipboard'); }
    catch { toast('Clipboard permission was denied'); }
  }
  function setText(id, value) { const node = document.getElementById(id); if (node) node.textContent = value ?? ''; }
  function safeDataUrl(value, kind) {
    const data = typeof value === 'string' ? value : '';
    if (!data.startsWith('data:') || data.length > 35 * 1024 * 1024) return '';

    const commaIdx = data.indexOf(',');
    if (commaIdx === -1) return '';

    const header = data.slice(0, commaIdx);
    const colonIdx = header.indexOf(':');
    if (colonIdx === -1) return '';

    // Must be base64 data url
    if (!/;base64\s*$/i.test(header)) return '';

    // Extract mime type (e.g. "audio/webm;codecs=opus" -> "audio/webm", or "image/png")
    const mimeSection = header.slice(colonIdx + 1, header.length - 7); // strip ;base64 from the end
    const mime = mimeSection.split(';')[0].trim().toLowerCase();

    // Disallow dangerous executable web content
    if (['text/html', 'application/xhtml+xml', 'image/svg+xml', 'application/javascript', 'text/javascript'].includes(mime)) {
      return '';
    }

    if (kind === 'image') return /^(image\/(png|jpeg|jpg|gif|webp|bmp|avif|x-icon))$/.test(mime) ? data : '';
    if (kind === 'audio') return mime.startsWith('audio/') ? data : '';
    if (kind === 'video') return mime.startsWith('video/') ? data : '';
    if (kind === 'file') return data;
    return data;
  }

  function formatDuration(sec) {
    const s = Math.max(0, Math.floor(sec || 0));
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m ${s % 60}s`;
    const h = Math.floor(m / 60);
    return `${h}h ${m % 60}m`;
  }

  function formatPingStatus(peer) {
    if (!peer) return '—';
    const unresponsive = Number(peer.unresponsiveSeconds ?? peer.lastSeenSecondsAgo ?? 99999);
    const ping = peer.ping !== null && peer.ping !== undefined ? Number(peer.ping) : null;

    if (unresponsive > 10) {
      return `Sin respuesta (${formatDuration(unresponsive)})`;
    }
    if (ping !== null && !isNaN(ping) && ping >= 0) {
      return `${ping} ms`;
    }
    if (unresponsive <= 10) {
      return ping !== null ? `${ping} ms` : `< 1 ms`;
    }
    return '—';
  }

  function sendBinary(data) {
    if (activeSocket && activeSocket.readyState === WebSocket.OPEN) {
      activeSocket.send(data);
      return true;
    }
    return false;
  }

  window.QP = {
    api, el, clear, canonicalId, shortId, initials, fmtBytes, peerMeta, trusted, findPeer,
    button, toast, copy, setText, safeDataUrl, on, off, emit, formatDuration, formatPingStatus,
    sendBinary, navigate, requestSync, getStatus, state,
    get currentPage() { return currentPage; },
    get isWsConnected() { return isConnected; }
  };
})();
