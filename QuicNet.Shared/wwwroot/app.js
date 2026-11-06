(() => {
  const toastHost = document.createElement('div');
  toastHost.className = 'toast-host';

  const ROUTES = {
    '/': 'dashboard',
    '/index.html': 'dashboard',
    '/chat.html': 'chat',
    '/call.html': 'voice',
    '/voice.html': 'voice',
    '/vpn.html': 'vpn',
    '/speedtest.html': 'speedtest',
    '/watch.html': 'watch',
    '/virtual-disk.html': 'virtual-disk',
    '/settings.html': 'settings'
  };

  const PAGE_TITLES = {
    dashboard: 'Inicio · QuicNet',
    chat: 'Mensajes · QuicNet',
    voice: 'Llamadas · QuicNet',
    vpn: 'Red privada · QuicNet',
    speedtest: 'Velocidad · QuicNet',
    watch: 'Ver juntos · QuicNet',
    'virtual-disk': 'Disco virtual · QuicNet',
    settings: 'Ajustes · QuicNet'
  };

  const PAGE_URLS = {
    dashboard: '/',
    chat: '/chat.html',
    voice: '/call.html',
    vpn: '/vpn.html',
    speedtest: '/speedtest.html',
    watch: '/watch.html',
    'virtual-disk': '/virtual-disk.html',
    settings: '/settings.html'
  };

  let currentPage = 'dashboard';
  const seenNotificationIds = new Set();
  let notificationsBootstrapped = false;
  const eventListeners = new Map();
  let activeSocket = null;
  let isConnected = false;
  let reconnectTimer = null, reconnectAttempt = 0, requestId = 0, statusRequest = null, syncPending = false, leaving = false;
  const pendingRequests = new Map();
  let statusVersion = 0;
  const state = {
    status: null,
    updates: null,
    get isWsConnected() { return isConnected; }
  };

  function reportError(error, context = 'Aplicación', level = 'error') {
    if (window.QuicNetDiagnostics) window.QuicNetDiagnostics.report(error, context, level);
    else console[level === 'warn' ? 'warn' : 'error'](context, error);
  }

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
        try { cb(data); } catch (err) { reportError(err, 'Evento ' + event); }
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

    // Update nav links active state
    document.querySelectorAll('.nav-link').forEach(link => {
      if (link.dataset.page === page) link.classList.add('active');
      else link.classList.remove('active');
      if (link.dataset.page === page) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
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

    if (page === 'watch') {
      const frame = document.getElementById('watchFrame');
      const isWatchAvail = capabilities?.capabilities?.watch?.available !== false;
      if (frame) {
        if (isWatchAvail) {
          const peerId = new URLSearchParams(new URL(url, location.origin).search).get('peer');
          const wanted = peerId ? `/watch/?peer=${encodeURIComponent(peerId)}` : '/watch/';
          if (frame.getAttribute('src') !== wanted) frame.setAttribute('src', wanted);
        } else {
          frame.style.display = 'none';
        }
      }
    }

    emit('view_changed', { page, url });
    if (pushState) {
      window.scrollTo({ top: 0, behavior: 'instant' });
      const heading = document.querySelector(`#view-${page} h1, #view-${page} .dm-head strong`);
      if (heading) { heading.tabIndex = -1; heading.focus({ preventScroll: true }); }
    }
  }

  // On narrow screens data tables render as stacked cards (app.css). Each cell
  // carries its column header in data-label so the card can show "Label: value".
  // Rows are rebuilt by every view's renderer, so label them as they appear.
  function labelTableCells(table) {
    const headers = [...table.querySelectorAll('thead th')].map(th => th.textContent.trim());
    if (!headers.length) return;
    for (const row of table.querySelectorAll('tbody tr')) {
      let column = 0;
      for (const cell of row.cells) {
        const span = cell.colSpan || 1;
        if (span >= headers.length) cell.dataset.fullRow = '';
        else if (!cell.dataset.label && headers[column]) cell.dataset.label = headers[column];
        column += span;
      }
    }
  }

  function observeTableLabels() {
    let queued = false;
    const relabel = () => {
      queued = false;
      document.querySelectorAll('table.data-table').forEach(labelTableCells);
    };
    relabel();
    new MutationObserver(() => {
      if (!queued) { queued = true; requestAnimationFrame(relabel); }
    }).observe(document.getElementById('spaContainer') || document.body, { childList: true, subtree: true });
  }

  document.addEventListener('DOMContentLoaded', () => {
    document.body.appendChild(toastHost);
    const navigation = document.querySelector('.nav-wrap');
    if (navigation) {
      const toggle = button('', 'nav-collapse-toggle', () => {
        document.body.classList.toggle('nav-collapsed');
        updateNavigation();
        try { localStorage.setItem('quicnet.navCollapsed', String(document.body.classList.contains('nav-collapsed'))); } catch {}
      });
      toggle.id = 'navCollapseBtn'; toggle.type = 'button';
      const updateNavigation = () => {
        const collapsed = document.body.classList.contains('nav-collapsed');
        toggle.textContent = collapsed ? '»' : '«';
        toggle.setAttribute('aria-label', collapsed ? 'Ampliar menú de navegación' : 'Contraer menú de navegación');
        toggle.setAttribute('aria-expanded', String(!collapsed));
        toggle.title = collapsed ? 'Ampliar menú' : 'Contraer menú';
      };
      try { document.body.classList.toggle('nav-collapsed', localStorage.getItem('quicnet.navCollapsed') === 'true'); } catch {}
      updateNavigation();
      navigation.querySelector('.brand')?.after(toggle);
      navigation.querySelectorAll('.nav-link').forEach(link => { link.title = link.querySelector('.nav-label')?.textContent || ''; link.setAttribute('aria-label', link.title); });
    }
    observeTableLabels();

    // Intercept navigation clicks
    document.addEventListener('click', e => {
      const a = e.target.closest('a');
      if (!a) return;
      if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || a.target === '_blank' || a.hasAttribute('download')) return;

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
            if (targetPage === currentPage && a.classList.contains('nav-link'))
              emit('nav_reselected', { page: targetPage });
            navigate(targetPage, url.pathname + url.search);
          }
        }
      } catch { }
    });

    window.addEventListener('popstate', () => {
      const page = getPageFromUrl(location.pathname);
      navigate(page, location.pathname + location.search, false);
    });

    // Generic subtab click handler for progressive disclosure
    document.addEventListener('click', e => {
      const btn = e.target.closest('.subtab-btn[data-subtab]');
      if (!btn) return;
      const group = btn.dataset.group;
      const targetId = btn.dataset.subtab;
      if (!group || !targetId) return;

      document.querySelectorAll(`.subtab-btn[data-group="${group}"]`).forEach(b => {
        b.classList.toggle('active', b === btn);
        b.setAttribute('aria-pressed', String(b === btn));
      });

      document.querySelectorAll(`[data-group-target="${group}"]`).forEach(target => {
        if (target.id === targetId) {
          target.classList.remove('hidden');
        } else {
          target.classList.add('hidden');
        }
      });
    });

    // Initial SPA route setup
    const initialPage = getPageFromUrl(location.pathname);
    navigate(initialPage, location.pathname + location.search, false);

    initWebSocket();
    loadCapabilities();
  });

  let capabilities = window.__qp_capabilities || null;

  async function loadCapabilities() {
    try {
      if (!capabilities) {
        const res = await api('/api/capabilities');
        if (res && res.capabilities) capabilities = res;
      }
      applyCapabilities(capabilities);
    } catch (e) {
      console.warn('Could not load capabilities:', e);
    }
  }

  function applyCapabilities(caps) {
    if (!caps || !caps.capabilities) return;
    const isAndroid = caps.platform === 'android' || /Android/i.test(navigator.userAgent);
    document.documentElement.classList.toggle('platform-android', isAndroid);
    document.documentElement.classList.toggle('platform-desktop', !isAndroid);
    document.body.classList.toggle('platform-android', isAndroid);
    document.body.classList.toggle('platform-desktop', !isAndroid);

    const c = caps.capabilities;
    // Puente LAN / VPN
    if (c.vpn && !c.vpn.available) {
      document.querySelectorAll('.nav-link[data-page="vpn"]').forEach(el => el.classList.add('hidden'));
    }
    // QuicWatch unavailable guard
    if (c.watch && !c.watch.available) {
      const frame = document.getElementById('watchFrame');
      if (frame) frame.style.display = 'none';
    }
    // Render status banners on views with available === false
    ['dashboard', 'chat', 'voice', 'vpn', 'speedtest', 'watch'].forEach(page => {
      const capKey = page === 'dashboard' ? 'peers' : page;
      const cap = c[capKey];
      const view = document.getElementById(`view-${page}`);
      if (view && cap && !cap.available) {
        let banner = view.querySelector('.capability-pending-banner');
        if (!banner) {
          banner = document.createElement('div');
          banner.className = 'capability-pending-banner';
          const target = view.querySelector('.workspace') || view.firstElementChild;
          if (target) target.insertBefore(banner, target.firstElementChild);
        }
        banner.replaceChildren(el('strong', 'cap-banner-badge', 'Función no disponible'), el('p', 'cap-banner-msg', cap.reason || 'Esta función no está disponible en este dispositivo.'));
      }
    });
  }

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
    leaving = true;
    clearTimeout(reconnectTimer);
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

  function wsRequest(command, data = {}) {
    const rejected = message => { const error = new Error(message); reportError(error, 'WebSocket ' + command, 'warn'); return Promise.reject(error); };
    if (!isConnected || activeSocket?.readyState !== WebSocket.OPEN) return rejected('Reconectando con QuicNet. Inténtalo de nuevo en unos segundos.');
    if (pendingRequests.size >= 64) return rejected('Hay demasiadas operaciones pendientes.');
    const id = String(++requestId);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pendingRequests.delete(id); const error = new Error('La aplicación no respondió a tiempo.'); reportError(error, 'WebSocket ' + command); reject(error); }, 15000);
      pendingRequests.set(id, { resolve, reject, timer, command });
      try { activeSocket.send(JSON.stringify({ type: 'request', id, command, data })); }
      catch (error) { clearTimeout(timer); pendingRequests.delete(id); reportError(error, 'Enviar WebSocket ' + command); reject(error); }
    });
  }

  function receiveStatus(status) {
    statusVersion++;
    state.status = status; syncPending = false;
    processNotifications(status); emit('status_updated', status);
  }

  function receiveUpdate(status) {
    if (!status || !Number.isFinite(status.CurrentBuild)) return;
    if (state.updates && (status.Revision ?? 0) < (state.updates.Revision ?? 0)) return;
    state.updates = status; emit('update_status', status);
  }

  function requestSync() {
    if (activeSocket && activeSocket.readyState === WebSocket.OPEN) {
      try {
        if (!syncPending) { activeSocket.send(JSON.stringify({ type: 'sync' })); syncPending = true; }
        return true;
      } catch (error) { reportError(error, 'Sincronizar estado'); }
    }
    return false;
  }

  async function getStatus(force = false) {
    if (!force && state.status) return state.status;
    const version = statusVersion;
    if (!statusRequest) statusRequest = (isConnected ? wsRequest('status.get').then(status => {
      if (version === statusVersion) receiveStatus(status);
      return state.status;
    })
      : new Promise((resolve, reject) => {
        const stop = on('status_updated', status => { clearTimeout(timer); stop(); resolve(status); });
        const timer = setTimeout(() => { stop(); reject(new Error('No se pudo conectar con QuicNet.')); }, 15000);
      })).finally(() => { statusRequest = null; });
    return statusRequest;
  }

  function scheduleReconnect() {
    if (leaving || reconnectTimer) return;
    reconnectTimer = setTimeout(() => { reconnectTimer = null; initWebSocket(); }, Math.min(10000, 500 * 2 ** Math.min(reconnectAttempt++, 5)));
  }

  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) {
      if (isConnected) requestSync();
      else { clearTimeout(reconnectTimer); reconnectTimer = null; initWebSocket(); }
    }
  });

  function initWebSocket() {
    if (leaving || activeSocket && [WebSocket.OPEN, WebSocket.CONNECTING].includes(activeSocket.readyState)) return;
    try {
      const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
      const csrfParam = window.__qp_csrf ? `?csrf=${encodeURIComponent(window.__qp_csrf)}` : '';
      const wsUrl = `${protocol}//${location.host}/ws${csrfParam}`;
      activeSocket = new WebSocket(wsUrl);
      activeSocket.binaryType = 'arraybuffer';

      activeSocket.onopen = () => {
        isConnected = true;
        reconnectAttempt = 0; syncPending = false;
        state.updates = null;
        window.QuicNetDiagnostics?.setTransport(entry => {
          if (activeSocket?.readyState !== WebSocket.OPEN || activeSocket.bufferedAmount > 256 * 1024) return false;
          activeSocket.send(JSON.stringify({ type: 'client_log', data: entry })); return true;
        });
        emit('ws_status', { connected: true });
      };

      activeSocket.onmessage = (evt) => {
        try {
          if (evt.data instanceof ArrayBuffer) {
            emit('ws_binary', evt.data);
            return;
          }
          const payload = JSON.parse(evt.data);
          if (payload && payload.type) {
            if (payload.type === 'response') {
              const pending = pendingRequests.get(payload.id);
              if (!pending) return;
              clearTimeout(pending.timer); pendingRequests.delete(payload.id);
              if (payload.success) {
                if (pending.command.startsWith('updates.')) receiveUpdate(payload.data);
                pending.resolve(payload.data);
              } else {
                const error = new Error(payload.error || 'No se pudo completar la operación.');
                reportError(error, 'WebSocket ' + pending.command); pending.reject(error);
              }
              return;
            }
            if (payload.type === 'status_updated' && payload.data) {
              receiveStatus(payload.data); return;
            }
            if (payload.type === 'update_status') { receiveUpdate(payload.data); return; }
            emit(payload.type, payload.data);
            if (payload.type === 'notification' && payload.data) {
              const n = payload.data;
              if (n.id && !seenNotificationIds.has(n.id)) {
                seenNotificationIds.add(n.id);
                // App notices are filtered by the shared notification policy.
              }
            }
          }
        } catch (error) { reportError(error, 'Procesar mensaje WebSocket'); }
      };

      activeSocket.onclose = event => {
        window.QuicNetDiagnostics?.setTransport(null);
        if (!leaving && (event.code !== 1000 || pendingRequests.size))
          reportError(new Error(`Conexión cerrada (código ${event.code}, ${event.wasClean ? 'cierre normal' : 'interrumpida'}). ${event.reason || ''}`), 'WebSocket', 'warn');
        isConnected = false;
        syncPending = false;
        for (const pending of pendingRequests.values()) { clearTimeout(pending.timer); pending.reject(new Error('Se perdió la conexión con QuicNet. Comprueba el estado antes de reintentar.')); }
        pendingRequests.clear();
        emit('ws_status', { connected: false });
        scheduleReconnect();
      };

      activeSocket.onerror = () => {
        reportError(new Error('Error de conexión WebSocket.'), 'WebSocket');
        try { activeSocket.close(); } catch (e) { }
      };
    } catch (e) {
      reportError(e, 'Abrir WebSocket');
      scheduleReconnect();
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
        // Historical notifications are retained without replaying native alerts.
      }
    });
  }

  async function api(path, options = {}) {
    const isRead = !options.method || options.method === 'GET';
    if (isRead && path === '/api/status') return getStatus();
    const response = await fetch(path, { ...options, signal: options.signal || (isRead ? AbortSignal.timeout(15000) : undefined) });
    let data = {};
    try {
      const body = response.status === 204 || response.status === 205 ? '' : await response.text();
      if (body.trim()) data = JSON.parse(body);
    } catch (cause) {
      const error = new Error('La aplicación devolvió una respuesta JSON no válida.', { cause });
      reportError(error, 'Respuesta API ' + path.split('?')[0]); throw error;
    }
    if (!response.ok || data?.success === false) {
      const error = new Error(data?.error || `No se pudo completar la solicitud (${response.status}).`);
      error.details = data;
      reportError(error, 'API ' + path.split('?')[0]); throw error;
    }
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
  const avatarImages = new Map();
  function paintAvatar(host, peer, snapshot = state.status, slot = '') {
    if (!host) return;
    const presence = window.QP?.userPresence?.(peer, snapshot);
    if (presence) { host.dataset.userPresence = presence.state; host.title = `${peer?.name || 'Contacto'} · ${presence.label}`; }
    const version = snapshot?.avatars?.[peer?.id];
    const privatePeer = peer?.isTor || peer?.networkType === 'Tor' || peer?.activeTransport === 'Tor' || peer?.onionAddress;
    const url = !privatePeer && /^[0-9a-f-]{36}$/i.test(peer?.id || '') && /^[0-9a-f]{64}$/i.test(version || '')
      ? `/api/profile/avatar/${peer.id}?v=${version}` : '';
    const key = slot ? `${slot}:${peer?.id || ''}` : '';
    if (key && !url) avatarImages.delete(key);
    const cached = key ? avatarImages.get(key) : null;
    if (url && cached?.getAttribute('src') === url) { if (cached.parentElement !== host) host.replaceChildren(cached); return; }
    const previous = host.querySelector('img');
    if (url && previous?.getAttribute('src') === url) return;
    host.textContent = initials(peer?.name || 'QuicNet');
    if (!url) return;
    const image = document.createElement('img'); image.src = url; image.alt = ''; image.loading = 'lazy'; image.decoding = 'async';
    image.addEventListener('error', () => {
      if (key && avatarImages.get(key) === image) avatarImages.delete(key);
      if (image.parentElement) image.parentElement.textContent = initials(peer?.name || 'QuicNet');
    }, { once: true });
    // Preserve the image element when telemetry rebuilds a row, so animation does not restart.
    if (key) {
      avatarImages.delete(key); avatarImages.set(key, image);
      if (avatarImages.size > 512) avatarImages.delete(avatarImages.keys().next().value);
    }
    host.replaceChildren(image);
  }

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
  function peerInConnectionMode(peer, status = state.status) {
    const tor = !!(peer.isTor || peer.networkType === 'Tor' || peer.onionAddress);
    const service = status?.[tor ? 'tor' : 'wan'];
    return !!(service?.desiredEnabled ?? service?.enabled ?? service?.isStarted ?? service?.running ?? !tor);
  }
  function trusted(status) { return (status.peers || []).filter(p => p.isTrusted && peerInConnectionMode(p, status)); }
  function findPeer(status, id) { return (status.peers || []).find(p => p.id === id); }
  function button(label, style = '', handler) {
    const node = el('button', `btn sm ${style}`.trim(), label);
    if (handler) node.addEventListener('click', handler);
    return node;
  }
  function toast(message, kind = '') {
    if (kind === 'error' || kind === 'warn' || kind === 'warning') reportError(new Error(message), 'Aviso de interfaz', kind === 'error' ? 'error' : 'warn');
    const node = el('div', `toast ${kind}`.trim());
    node.setAttribute('role', kind === 'error' ? 'alert' : 'status');
    node.appendChild(el('span', '', message));
    const close = el('button', '', '×');
    close.setAttribute('aria-label', 'Cerrar aviso');
    close.onclick = () => node.remove();
    node.appendChild(close);
    toastHost.appendChild(node);
    // Bound transient feedback without taking space from the workspace.
    while (toastHost.children.length > 3) toastHost.firstElementChild.remove();
    // Errors stay available until dismissed. Resume expiry after hover/focus ends.
    if (kind !== 'error') {
      let timer;
      const pause = () => clearTimeout(timer);
      const resume = () => {
        pause();
        timer = setTimeout(() => { if (!node.matches(':hover') && !node.contains(document.activeElement)) node.remove(); }, 5000);
      };
      node.addEventListener('mouseenter', pause); node.addEventListener('mouseleave', resume);
      node.addEventListener('focusin', pause); node.addEventListener('focusout', resume);
      resume();
    }
  }
  async function copy(text) {
    if (!text) { toast('El código todavía no está disponible.', 'warn'); return false; }
    try { await navigator.clipboard.writeText(text); toast('Copiado al portapapeles'); return true; }
    catch { toast('No se pudo copiar. Selecciona el código y cópialo manualmente.', 'error'); return false; }
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

  function sendBinary(data, maxBufferedAmount) {
    if (activeSocket && activeSocket.readyState === WebSocket.OPEN) {
      if (Number.isFinite(maxBufferedAmount) && activeSocket.bufferedAmount > maxBufferedAmount) {
        return false;
      }
      activeSocket.send(data);
      return true;
    }
    return false;
  }

  window.QP = {
    api, el, clear, canonicalId, shortId, initials, paintAvatar, fmtBytes, peerMeta, peerInConnectionMode, trusted, findPeer,
    button, toast, copy, setText, safeDataUrl, on, off, emit, formatDuration, formatPingStatus,
    sendBinary, navigate, requestSync, getStatus, wsRequest, reportError, state,
    get capabilities() { return capabilities; },
    get currentPage() { return currentPage; },
    get isWsConnected() { return isConnected; }
  };
})();
