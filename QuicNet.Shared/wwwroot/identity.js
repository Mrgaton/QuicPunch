/* Certificate capsules v2. Freeze the domain, byte mapping and pattern/edge order.
   Presentation sizes and avatar/name changes must never change this descriptor. */
(() => {
  const domain = new TextEncoder().encode('QuicNet visual identity v2\0');
  const cache = new Map();
  const capsules = new WeakMap();
  const edges = ['round', 'soft', 'cut'];
  const patterns = ['seda', 'orbita', 'trama', 'pulso', 'estrato', 'mosaico'];

  function normalize(peer) {
    const value = typeof peer?.certHash === 'string' ? peer.certHash.trim() : '';
    if (/^[a-f0-9]{64}$/i.test(value)) return value.toLowerCase();
    if (!/^[A-Za-z0-9+/_-]{43}=?$/.test(value)) return null;
    try {
      const bytes = atob(value.replace(/-/g, '+').replace(/_/g, '/'));
      return bytes.length === 32 ? Array.from(bytes, byte => byte.charCodeAt(0).toString(16).padStart(2, '0')).join('') : null;
    } catch { return null; }
  }

  function describe(peer) {
    const hex = normalize(peer);
    if (!hex || !globalThis.crypto?.subtle) return Promise.resolve(null);
    if (cache.has(hex)) return cache.get(hex);
    const input = new Uint8Array(domain.length + 32);
    input.set(domain);
    input.set(hex.match(/../g).map(byte => parseInt(byte, 16)), domain.length);
    const result = crypto.subtle.digest('SHA-256', input).then(hash => {
      const bytes = new Uint8Array(hash);
      return {hex, version: 2, hue: ((bytes[0] << 8) | bytes[1]) % 360,
        saturation: 24 + bytes[2] % 9, edge: edges[bytes[3] % edges.length],
        pattern: patterns[bytes[4] % patterns.length]};
    }).catch(() => {cache.delete(hex); return null;});
    cache.set(hex, result);
    if (cache.size > 512) cache.delete(cache.keys().next().value);
    return result;
  }

  function createCapsule(peer, options = {}) {
    const interactive = options.interactive !== false;
    const mark = QP.el(interactive ? 'button' : 'span', 'ci' + (interactive ? ' identity-mark' : ''));
    const body = QP.el('span', 'ci-body');
    const avatar = QP.el('span', 'ci-avatar'); avatar.setAttribute('aria-hidden', 'true');
    const name = QP.el('strong', 'ci-name');
    if (options.avatarId) avatar.id = options.avatarId;
    if (options.nameId) name.id = options.nameId;
    body.append(avatar, name); mark.append(body);
    capsules.set(mark, {avatar, name, interactive, hex: undefined, peer: null, options});
    if (interactive) {
      mark.type = 'button'; mark.setAttribute('aria-haspopup', 'dialog');
      mark.addEventListener('click', () => showDialog(capsules.get(mark).peer, mark));
    }
    updateCapsule(mark, peer, options);
    return mark;
  }

  function updateCapsule(mark, peer = {}, options = {}) {
    const state = capsules.get(mark);
    if (!state) return;
    state.peer = {...peer}; state.options = {...state.options, ...options};
    const settings = state.options;
    const label = peer.name || 'Contacto';
    state.name.textContent = label;
    mark.dataset.size = ['compact', 'large'].includes(settings.size) ? settings.size : 'normal';
    if (QP.paintAvatar) QP.paintAvatar(state.avatar, peer, settings.snapshot || QP.state?.status, settings.slot || '');
    else state.avatar.textContent = QP.initials(label);
    // Presence is conveyed by adjacent status text, not by the certificate ornament.
    state.avatar.removeAttribute('data-user-presence');
    const hex = normalize(peer);
    mark.title = hex ? 'Ver huella del certificado' : 'La huella del certificado todavía no está disponible';
    if (state.interactive) {
      mark.disabled = !hex;
      mark.setAttribute('aria-label', `Ver identidad de ${label}${hex ? '' : ' · certificado no disponible'}`);
    }
    if (state.hex === hex) return;
    state.hex = hex;
    delete mark.dataset.identityVersion; delete mark.dataset.pattern; delete mark.dataset.edge;
    mark.style.removeProperty('--ci-hue'); mark.style.removeProperty('--ci-saturation');
    mark.dataset.identityState = 'pending';
    mark.removeAttribute('aria-busy');
    if (!hex) return;
    mark.setAttribute('aria-busy', 'true');
    describe(peer).then(identity => {
      // A late digest must never put the previous caller's identity on this capsule.
      if (state.hex !== hex) return;
      if (identity) {
        mark.style.setProperty('--ci-hue', identity.hue);
        mark.style.setProperty('--ci-saturation', identity.saturation + '%');
        mark.dataset.edge = identity.edge; mark.dataset.pattern = identity.pattern;
        mark.dataset.identityVersion = String(identity.version); mark.dataset.identityState = 'ready';
      }
      mark.removeAttribute('aria-busy');
    });
  }

  function mount(host, peer, options = {}) {
    if (!host) return null;
    let mark = host.firstElementChild;
    if (!capsules.has(mark)) { mark = createCapsule(peer, options); host.replaceChildren(mark); }
    else updateCapsule(mark, peer, options);
    return mark;
  }

  function findPeer(id, snapshot = QP.state?.status) {
    return (snapshot?.peers || []).find(peer => peer.id === id)
      || (snapshot?.savedPeers || []).find(peer => peer.id === id);
  }

  function appendDetails(host, peer) {
    const hex = normalize(peer);
    const visual = QP.el('div', 'identity-preview');
    visual.append(createCapsule(peer, {size: 'large', interactive: false}));
    host.append(visual);
    if (peer.hasNameCollision) host.append(QP.el('p', 'identity-name-warning',
      'El nombre coincide con un contacto guardado, pero su certificado es distinto. Comprueba su identidad antes de confiar.'));
    host.append(QP.el('p', 'identity-explanation',
      'El color, el contorno y la textura permanecen iguales aunque cambien el nombre o la foto. Ayudan a reconocer el dispositivo, pero distintas huellas pueden tener un aspecto parecido. Para confirmar su identidad, compara la huella completa por otro canal.'));
    const label = QP.el('h3', 'identity-fingerprint-label', 'Huella completa del certificado');
    const fingerprint = QP.el('code', 'identity-fingerprint', hex ? hex.toUpperCase().match(/.{1,4}/g).join(' ') : 'Certificado no disponible');
    host.append(label, fingerprint);
    if (hex) {
      const copy = QP.button('Copiar huella', '', async () => {
        if (await QP.copy(hex.toUpperCase())) QP.toast('Huella copiada');
      });
      copy.classList.add('identity-copy'); host.append(copy);
    }
  }

  function showDialog(peer, opener) {
    const dialog = QP.el('dialog', 'identity-dialog');
    dialog.setAttribute('aria-labelledby', 'identityDialogTitle');
    const header = QP.el('div', 'identity-dialog-header');
    const title = QP.el('h2', '', `Identidad de ${peer.name || 'este contacto'}`);
    title.id = 'identityDialogTitle';
    const close = QP.button('Cerrar', '', () => dialog.close());
    header.append(title, close); dialog.append(header);
    appendDetails(dialog, peer);
    document.body.append(dialog);
    dialog.addEventListener('close', () => {dialog.remove(); if (opener?.isConnected) opener.focus({preventScroll: true});}, {once: true});
    dialog.showModal(); close.focus();
  }

  QP.identity = {normalize, describe, createCapsule, updateCapsule, mount, findPeer, appendDetails, showDialog};
})();
