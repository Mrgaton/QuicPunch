(() => {
  const SAMPLE_RATE = 48000;
  const FRAME_DURATION_MS = 60;
  const FRAME_SIZE = (SAMPLE_RATE * FRAME_DURATION_MS) / 1000; // 2880 samples
  const FRAME_DURATION_US = FRAME_DURATION_MS * 1000; // 60000 us

  const CODEC_OPUS = 0x01;
  const CODEC_PCM = 0x00;

  let voiceStatus = null;
  let mediaStream = null;
  let inputCtx = null;
  let inputSource = null;
  let inputProcessor = null;
  let analyser = null;
  let testOnly = false;
  let globalMicMuted = false;
  let globalDeafened = false;
  let playbackCtx = null;

  let encoder = null;
  let encodeTimestampUs = 0;
  let inputSampleAccumulator = [];

  const callingPeers = new Set();
  const callSettings = new Map();
  const outputs = new Map();

  function getOpusBitrate() {
    const saved = localStorage.getItem('qp.voice.opus_bitrate');
    const val = saved ? parseInt(saved, 10) : 96000;
    return isNaN(val) || val < 8000 || val > 128000 ? 96000 : val;
  }

  function setOpusBitrate(val) {
    localStorage.setItem('qp.voice.opus_bitrate', String(val));
    updateTopOpusBadge();
    if (encoder && encoder.state === 'configured') {
      try {
        encoder.configure({
          codec: 'opus',
          sampleRate: SAMPLE_RATE,
          numberOfChannels: 1,
          bitrate: val,
          opus: { application: 'voip', frameDuration: FRAME_DURATION_US }
        });
      } catch (e) {
        console.warn('Could not reconfigure active encoder', e);
      }
    }
  }

  function isJitterBufferEnabled() {
    const saved = localStorage.getItem('qp.voice.jitter_buffer_60ms');
    return saved === null ? true : saved === 'true';
  }

  function setJitterBufferEnabled(enabled) {
    localStorage.setItem('qp.voice.jitter_buffer_60ms', enabled ? 'true' : 'false');
    updateTopOpusBadge();
  }

  function updateTopOpusBadge() {
    const br = getOpusBitrate() / 1000;
    const jb = isJitterBufferEnabled();
    const badge = document.getElementById('topOpusBadge');
    if (badge) badge.textContent = `Opus VoIP · ${br} kbps · ${jb ? 'Buffer 60ms' : 'Sin buffer'}`;
    const sel = document.getElementById('opusBitrateSelect');
    if (sel) sel.value = String(getOpusBitrate());
    const jbToggle = document.getElementById('jitterBufferToggle');
    if (jbToggle) jbToggle.checked = jb;
    const jbStatus = document.getElementById('modalJitterStatus');
    if (jbStatus) jbStatus.textContent = jb ? '60 ms (1 frame)' : 'Desactivado (0 ms)';
  }
  function createOpusHead(sampleRate = 48000, channels = 1) {
    const head = new Uint8Array(19);
    head.set([0x4F, 0x70, 0x75, 0x73, 0x48, 0x65, 0x61, 0x64], 0); // "OpusHead"
    head[8] = 1; // version
    head[9] = channels; // channels
    head[10] = 0; head[11] = 0; // pre-skip
    head[12] = sampleRate & 0xFF;
    head[13] = (sampleRate >> 8) & 0xFF;
    head[14] = (sampleRate >> 16) & 0xFF;
    head[15] = (sampleRate >> 24) & 0xFF;
    head[16] = 0; head[17] = 0; // gain
    head[18] = 0; // mapping family
    return head;
  }

  function guidToBytes(uuidStr) {
    const hex = String(uuidStr || '').replace(/-/g, '');
    const bytes = new Uint8Array(16);
    if (hex.length !== 32) return bytes;
    bytes[0] = parseInt(hex.substr(6, 2), 16);
    bytes[1] = parseInt(hex.substr(4, 2), 16);
    bytes[2] = parseInt(hex.substr(2, 2), 16);
    bytes[3] = parseInt(hex.substr(0, 2), 16);
    bytes[4] = parseInt(hex.substr(10, 2), 16);
    bytes[5] = parseInt(hex.substr(8, 2), 16);
    bytes[6] = parseInt(hex.substr(14, 2), 16);
    bytes[7] = parseInt(hex.substr(12, 2), 16);
    for (let i = 8; i < 16; i++) {
      bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
    }
    return bytes;
  }

  function bytesToGuid(bytes, offset = 0) {
    const b = new Uint8Array(bytes.buffer || bytes, (bytes.byteOffset || 0) + offset, 16);
    const hex = n => n.toString(16).padStart(2, '0');
    const d1 = hex(b[3]) + hex(b[2]) + hex(b[1]) + hex(b[0]);
    const d2 = hex(b[5]) + hex(b[4]);
    const d3 = hex(b[7]) + hex(b[6]);
    const d4 = hex(b[8]) + hex(b[9]);
    const d5 = hex(b[10]) + hex(b[11]) + hex(b[12]) + hex(b[13]) + hex(b[14]) + hex(b[15]);
    return `${d1}-${d2}-${d3}-${d4}-${d5}`.toLowerCase();
  }

  function getPlaybackContext() {
    if (!playbackCtx) {
      playbackCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: SAMPLE_RATE });
    }
    if (playbackCtx.state === 'suspended') {
      playbackCtx.resume().catch(() => {});
    }
    return playbackCtx;
  }

  function unlockAudio() {
    if (playbackCtx && playbackCtx.state === 'suspended') playbackCtx.resume().catch(() => {});
    if (inputCtx && inputCtx.state === 'suspended') inputCtx.resume().catch(() => {});
    const banner = document.getElementById('audioUnlockBanner');
    if (banner) banner.classList.add('hidden');
  }
  window.addEventListener('click', unlockAudio, { passive: true });
  window.addEventListener('touchstart', unlockAudio, { passive: true });
  window.addEventListener('keydown', unlockAudio, { passive: true });

  class PeerAudioOutput {
    constructor(peerId) {
      this.peerId = peerId;
      this.gainNode = null;
      this.decoder = null;
      this.nextPlayTime = 0;
      this.decodeTimestampUs = 0;
      this.initDecoder();
    }

    ensureGain() {
      const ctx = getPlaybackContext();
      if (!this.gainNode) {
        this.gainNode = ctx.createGain();
        this.gainNode.connect(ctx.destination);
        this.applySettings();
      }
      return this.gainNode;
    }

    initDecoder() {
      if (typeof AudioDecoder !== 'undefined') {
        try {
          this.decoder = new AudioDecoder({
            output: audioData => {
              const frames = audioData.numberOfFrames;
              const samples = new Float32Array(frames);
              audioData.copyTo(samples, { planeIndex: 0 });
              audioData.close();
              this.playFloatSamples(samples, audioData.sampleRate || SAMPLE_RATE);
            },
            error: err => {
              console.warn(`Opus decoder error for peer ${this.peerId}:`, err);
              this.decoder = null;
            }
          });
          this.decoder.configure({
            codec: 'opus',
            sampleRate: SAMPLE_RATE,
            numberOfChannels: 1,
            description: createOpusHead(SAMPLE_RATE, 1)
          });
        } catch (e) {
          console.warn('AudioDecoder init failed, using PCM fallback', e);
          this.decoder = null;
        }
      }
    }

    applySettings() {
      const s = settings(this.peerId);
      if (this.gainNode) {
        const isMuted = s.receiveMuted || globalDeafened;
        this.gainNode.gain.value = isMuted ? 0 : Math.max(0, Math.min(2, s.volume / 100));
      }
    }

    pushDatagram(bytes) {
      if (!bytes || bytes.length === 0) return;
      const s = settings(this.peerId);
      if (s.receiveMuted || globalDeafened) return;

      const format = bytes[0];
      const payload = bytes.subarray(1);

      if (format === CODEC_OPUS && this.decoder && this.decoder.state === 'configured') {
        try {
          const chunk = new EncodedAudioChunk({
            type: 'key',
            timestamp: this.decodeTimestampUs,
            data: payload
          });
          this.decodeTimestampUs += FRAME_DURATION_US;
          this.decoder.decode(chunk);
          return;
        } catch (err) {
          console.warn('Error decoding Opus chunk, falling back to PCM', err);
        }
      }

      // Safe PCM Decoding with DataView (immune to unaligned byteOffset issues)
      if (payload.byteLength >= 2) {
        try {
          const numSamples = Math.floor(payload.byteLength / 2);
          const samples = new Float32Array(numSamples);
          const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
          for (let i = 0; i < numSamples; i++) {
            samples[i] = view.getInt16(i * 2, true) / 32768.0;
          }
          this.playFloatSamples(samples, SAMPLE_RATE);
        } catch (e) {
          console.warn('PCM playback error', e);
        }
      }
    }

    playFloatSamples(samples, rate) {
      if (!samples || samples.length === 0) return;
      const ctx = getPlaybackContext();
      const gain = this.ensureGain();
      const buffer = ctx.createBuffer(1, samples.length, rate);
      buffer.copyToChannel(samples, 0);

      const source = ctx.createBufferSource();
      source.buffer = buffer;
      source.connect(gain);

      const now = ctx.currentTime;
      const useJitterBuffer = isJitterBufferEnabled();
      const leadTime = useJitterBuffer ? 0.060 : 0.005; // 60ms cushion (1 full frame buffer) vs 5ms ultra-low latency

      if (this.nextPlayTime < now || this.nextPlayTime > now + 0.30) {
        this.nextPlayTime = now + leadTime;
      }

      source.start(this.nextPlayTime);
      this.nextPlayTime += buffer.duration;
    }

    dispose() {
      try { this.decoder?.close(); } catch { }
      try { this.gainNode?.disconnect(); } catch { }
      this.decoder = null;
      this.gainNode = null;
      this.nextPlayTime = 0;
    }
  }

  function activeCalls() {
    return voiceStatus?.activeVoiceCalls || [];
  }

  function settings(peerId) {
    if (callSettings.has(peerId)) return callSettings.get(peerId);
    let value = { volume: 100, receiveMuted: false, sendMuted: false };
    try {
      value = { ...value, ...JSON.parse(localStorage.getItem(`qp.voice.${peerId}`) || '{}') };
    } catch { }
    callSettings.set(peerId, value);
    return value;
  }

  function saveSettings(peerId) {
    try {
      localStorage.setItem(`qp.voice.${peerId}`, JSON.stringify(settings(peerId)));
    } catch { }
  }

  function output(peerId) {
    if (!outputs.has(peerId)) outputs.set(peerId, new PeerAudioOutput(peerId));
    return outputs.get(peerId);
  }

  function checkAudioState() {
    const banner = document.getElementById('audioUnlockBanner');
    if (!banner) return;
    if (activeCalls().length > 0 && playbackCtx && playbackCtx.state === 'suspended') {
      banner.classList.remove('hidden');
    } else {
      banner.classList.add('hidden');
    }
  }

  document.addEventListener('DOMContentLoaded', () => {
    const unlockBtn = document.getElementById('audioUnlockBanner');
    if (unlockBtn) unlockBtn.addEventListener('click', unlockAudio);

    document.getElementById('globalMicBtn')?.addEventListener('click', toggleGlobalMic);
    document.getElementById('globalDeafenBtn')?.addEventListener('click', toggleGlobalDeafen);
    document.getElementById('testMicBtn')?.addEventListener('click', testMic);
    document.getElementById('advancedOpusBtn')?.addEventListener('click', openOpusModal);
    document.getElementById('closeOpusModalBtn')?.addEventListener('click', closeOpusModal);
    document.getElementById('saveOpusModalBtn')?.addEventListener('click', saveOpusModal);
    document.getElementById('opusModal')?.addEventListener('click', e => {
      if (e.target.id === 'opusModal') closeOpusModal();
    });

    // Global Active Call Bar controls
    document.getElementById('globalCallMicBtn')?.addEventListener('click', toggleGlobalMic);
    document.getElementById('globalCallDeafenBtn')?.addEventListener('click', toggleGlobalDeafen);
    document.getElementById('globalCallHangupBtn')?.addEventListener('click', () => {
      const calls = activeCalls();
      if (calls.length) hangup(calls[0].peerId);
    });
    document.getElementById('globalCallGoToVoiceBtn')?.addEventListener('click', () => {
      QP.navigate('voice', '/call.html');
    });
    document.getElementById('globalCallBarInfo')?.addEventListener('click', () => {
      QP.navigate('voice', '/call.html');
    });

    document.getElementById('micSelect')?.addEventListener('change', () => {
      if (activeCalls().length || testOnly) ensureAudio(testOnly).catch(e => QP.toast(e.message, 'error'));
    });

    document.getElementById('processingToggle')?.addEventListener('change', () => {
      if (mediaStream) ensureAudio(testOnly).catch(e => QP.toast(e.message, 'error'));
    });

    // Binary WebSocket Audio Handler
    QP.on('ws_binary', data => {
      if (!(data instanceof ArrayBuffer) || data.byteLength < 17) return;
      const bytes = new Uint8Array(data);
      const peerId = bytesToGuid(bytes, 0);
      const payload = bytes.subarray(16);
      output(peerId).pushDatagram(payload);
      checkAudioState();
    });

    // WebSocket Call Signals
    QP.on('voice_signal', msg => {
      if (!msg) return;
      if (msg.signal === 'call-established') {
        callingPeers.delete(msg.peerId);
        unlockAudio();
        refreshVoice();
      } else if (msg.signal === 'call-ended') {
        callingPeers.delete(msg.peerId);
        outputs.get(msg.peerId)?.dispose();
        outputs.delete(msg.peerId);
        refreshVoice();
      }
    });

    QP.on('view_changed', () => {
      renderVoice();
      checkAudioState();
    });

    updateTopOpusBadge();
    loadDevices();
    refreshVoice();
    setInterval(refreshVoice, 1200);
    setInterval(checkAudioState, 1500);
    setInterval(pollVoiceFallback, 100);
  });

  async function loadDevices() {
    try {
      const temp = await navigator.mediaDevices.getUserMedia({ audio: true });
      temp.getTracks().forEach(t => t.stop());
      const devices = await navigator.mediaDevices.enumerateDevices();
      const sel = document.getElementById('micSelect');
      if (!sel) return;
      QP.clear(sel);
      devices.filter(d => d.kind === 'audioinput').forEach((d, i) => {
        const o = document.createElement('option');
        o.value = d.deviceId;
        o.textContent = d.label || `Micrófono ${i + 1}`;
        sel.appendChild(o);
      });
    } catch {
      QP.toast('Se requiere permiso de micrófono para las llamadas de voz', 'warn');
    }
  }

  async function refreshVoice() {
    try {
      voiceStatus = await QP.api('/api/status');
      renderVoice();
      if (activeCalls().length && !mediaStream && !testOnly) {
        ensureAudio(false).catch(e => console.warn('Audio capture start error', e));
      }
      if (!activeCalls().length && !testOnly && mediaStream) {
        stopCapture();
      }
    } catch { }
  }

  function renderGlobalCallBar(calls, peers) {
    const bar = document.getElementById('globalActiveCallBar');
    if (!bar) return;

    if (!calls.length) {
      bar.classList.add('hidden');
      return;
    }

    if (QP.currentPage === 'voice') {
      bar.classList.add('hidden');
      return;
    }

    bar.classList.remove('hidden');

    const firstCall = calls[0];
    const peer = peers.find(p => p.id === firstCall.peerId) || { id: firstCall.peerId, name: firstCall.peerName || 'Peer' };

    QP.setText('globalCallPeerName', calls.length > 1 ? `${peer.name} (+${calls.length - 1} más)` : peer.name);
    QP.setText('globalCallAvatar', QP.initials(peer.name));
    QP.setText('globalCallMeta', `En llamada VoIP · Opus ${getOpusBitrate() / 1000} kbps · ${isJitterBufferEnabled() ? 'Buffer 60ms' : '0ms'}`);

    const micBtn = document.getElementById('globalCallMicBtn');
    if (micBtn) {
      micBtn.innerHTML = globalMicMuted ? 'Micro off' : 'Micro on';
      micBtn.className = `btn sm ${globalMicMuted ? 'danger' : 'ghost'}`;
    }

    const deafenBtn = document.getElementById('globalCallDeafenBtn');
    if (deafenBtn) {
      deafenBtn.innerHTML = globalDeafened ? 'Ensordecido' : 'Audio on';
      deafenBtn.className = `btn sm ${globalDeafened ? 'danger' : 'ghost'}`;
    }
  }

  function renderVoice() {
    const peers = QP.trusted(voiceStatus);
    const calls = activeCalls();
    const ids = new Set(calls.map(c => c.peerId));

    for (const [id, out] of outputs) {
      if (!ids.has(id)) {
        out.dispose();
        outputs.delete(id);
      }
    }

    QP.setText('voiceBadge', `${calls.length} activa${calls.length === 1 ? '' : 's'}`);
    renderActiveCalls(calls, peers);
    renderGlobalCallBar(calls, peers);

    // Render Start a Call list
    const list = document.getElementById('voicePeers');
    if (list) {
      QP.clear(list);
      if (!peers.length) {
        list.appendChild(QP.el('div', 'empty', 'No hay contactos de confianza disponibles.'));
      } else {
        peers.forEach(peer => {
          const row = QP.el('div', 'peer-row voice-peer-row');
          row.appendChild(QP.el('div', 'avatar', QP.initials(peer.name)));
          const copy = QP.el('div', 'peer-copy');
          copy.append(
            QP.el('div', 'peer-name', peer.name),
            QP.el('div', 'peer-meta', `${QP.formatPingStatus(peer)} · ${QP.peerMeta(peer)}`)
          );
          const actions = QP.el('div', 'row-actions');
          const active = ids.has(peer.id);
          const calling = callingPeers.has(peer.id);

          const btn = QP.button(
            active ? 'En llamada' : calling ? 'Llamando…' : 'Llamar',
            active ? 'green' : calling ? 'accent' : 'accent',
            () => startCall(peer.id)
          );
          btn.disabled = active || calling;
          actions.append(btn);
          row.append(copy, actions);
          list.appendChild(row);
        });
      }
    }

    // Render Global Mic & Deafen buttons
    const micBtn = document.getElementById('globalMicBtn');
    if (micBtn) {
      if (globalMicMuted) {
        micBtn.textContent = 'Micro silenciado';
        micBtn.className = 'btn sm danger';
      } else {
        micBtn.textContent = 'Micro activo';
        micBtn.className = 'btn sm green';
      }
    }

    const deafenBtn = document.getElementById('globalDeafenBtn');
    if (deafenBtn) {
      if (globalDeafened) {
        deafenBtn.textContent = 'Ensordecido';
        deafenBtn.className = 'btn sm danger';
      } else {
        deafenBtn.textContent = 'Audio activo';
        deafenBtn.className = 'btn sm green';
      }
    }
  }

  function renderActiveCalls(calls, peers) {
    const host = document.getElementById('activeCalls');
    QP.clear(host);
    if (!calls.length) {
      host.appendChild(QP.el('div', 'empty', 'No hay llamadas activas. Inicia una llamada abajo; puedes mantener varias activas simultáneamente.'));
      return;
    }

    calls.forEach(call => {
      const peer = peers.find(p => p.id === call.peerId) || { id: call.peerId, name: call.peerName || 'Peer' };
      const s = settings(peer.id);
      const card = QP.el('div', 'call-card voice-call-row');

      // Head
      const head = QP.el('div', 'call-card-head');
      head.append(QP.el('div', 'avatar', QP.initials(peer.name)));
      const copy = QP.el('div', 'peer-copy');
      copy.append(
        QP.el('div', 'peer-name', peer.name),
        QP.el('div', 'peer-meta', `Opus 60ms · ${QP.formatPingStatus(peer)}`)
      );
      head.append(copy, QP.el('span', 'badge green', 'En vivo'));

      // Discord-style Circular Controls
      const controls = QP.el('div', 'call-card-controls discord-call-controls');

      // 1. Mic Send Toggle (/ )
      const micSendBtn = document.createElement('button');
      micSendBtn.className = `call-btn-round ${s.sendMuted ? 'muted-red' : 'active-green'}`;
      micSendBtn.innerHTML = s.sendMuted ? 'Muted' : 'Mic';
      micSendBtn.title = s.sendMuted ? `Desmutear mi micrófono para ${peer.name}` : `Mutear mi micrófono para ${peer.name}`;
      micSendBtn.addEventListener('click', () => {
        s.sendMuted = !s.sendMuted;
        saveSettings(peer.id);
        renderVoice();
      });

      // 2. Hear Peer Toggle (/ )
      const listenBtn = document.createElement('button');
      listenBtn.className = `call-btn-round ${s.receiveMuted ? 'muted-red' : 'active-green'}`;
      listenBtn.innerHTML = s.receiveMuted ? 'Muted' : 'Audio';
      listenBtn.title = s.receiveMuted ? `Escuchar audio de ${peer.name}` : `Silenciar audio de ${peer.name} para mí`;
      listenBtn.addEventListener('click', () => {
        s.receiveMuted = !s.receiveMuted;
        saveSettings(peer.id);
        output(peer.id).applySettings();
        renderVoice();
      });

      // 3. Hangup Button (White Handset SVG)
      const hangupBtn = document.createElement('button');
      hangupBtn.className = 'call-btn-round hangup-red';
      hangupBtn.innerHTML = '<svg viewBox="0 0 24 24" width="18" height="18" fill="white" style="transform: rotate(135deg); display: block;"><path d="M6.62 10.79a15.05 15.05 0 006.59 6.59l2.2-2.2a1 1 0 011.02-.24c1.12.37 2.33.57 3.57.57a1 1 0 011 1V20a1 1 0 01-1 1A17 17 0 013 4a1 1 0 011-1h3.5a1 1 0 011 1c0 1.25.2 2.45.57 3.57a1 1 0 01-.25 1.02l-2.2 2.2z"/></svg>';
      hangupBtn.title = `Colgar llamada con ${peer.name}`;
      hangupBtn.addEventListener('click', () => hangup(peer.id));

      controls.append(micSendBtn, listenBtn, hangupBtn);

      // Volume slider
      const volume = QP.el('div', 'volume-row');
      volume.append(QP.el('span', 'muted', 'Vol'));
      const slider = document.createElement('input');
      slider.type = 'range';
      slider.min = '0';
      slider.max = '200';
      slider.step = '5';
      slider.value = String(s.volume);
      const label = QP.el('span', 'mono volume-value', `${s.volume}%`);
      slider.addEventListener('input', () => {
        s.volume = Number(slider.value);
        label.textContent = `${s.volume}%`;
        saveSettings(peer.id);
        output(peer.id).applySettings();
      });
      volume.append(slider, label);

      card.append(head, controls, volume);
      host.appendChild(card);
    });
  }

  async function startCall(peerId) {
    unlockAudio();
    callingPeers.add(peerId);
    renderVoice();
    try {
      await ensureAudio(false);
      await QP.api('/api/connect-peer', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, protocol: 'voice' })
      });
      QP.toast('Llamando…');
      setTimeout(() => {
        callingPeers.delete(peerId);
        renderVoice();
      }, 10000);
    } catch (e) {
      callingPeers.delete(peerId);
      renderVoice();
      QP.toast(e.message, 'error');
    }
  }

  function initEncoder() {
    if (typeof AudioEncoder !== 'undefined') {
      try {
        encoder = new AudioEncoder({
          output: (chunk, metadata) => {
            const buffer = new Uint8Array(chunk.byteLength + 1);
            buffer[0] = CODEC_OPUS;
            chunk.copyTo(buffer.subarray(1));
            dispatchVoiceFrame(buffer);
          },
          error: e => {
            console.warn('Opus encoder error, falling back to PCM', e);
            encoder = null;
          }
        });
        encoder.configure({
          codec: 'opus',
          sampleRate: SAMPLE_RATE,
          numberOfChannels: 1,
          bitrate: getOpusBitrate(),
          opus: {
            application: 'voip',
            frameDuration: FRAME_DURATION_US
          }
        });
      } catch (e) {
        console.warn('AudioEncoder not supported or failed to initialize, using PCM fallback', e);
        encoder = null;
      }
    } else {
      encoder = null;
    }
  }

  function dispatchVoiceFrame(payloadUint8) {
    if (testOnly || globalMicMuted) return;
    const calls = activeCalls();
    if (!calls.length) return;

    const targets = calls.map(c => c.peerId).filter(id => !settings(id).sendMuted);
    if (!targets.length) return;

    // Fast binary WebSocket transmission
    if (QP.isWsConnected) {
      if (targets.length === calls.length) {
        // Broadcast packet (16 bytes zeros + payload)
        const packet = new Uint8Array(16 + payloadUint8.byteLength);
        packet.set(payloadUint8, 16);
        QP.sendBinary(packet.buffer);
      } else {
        // Send to each specific unmuted peer
        targets.forEach(targetId => {
          const packet = new Uint8Array(16 + payloadUint8.byteLength);
          packet.set(guidToBytes(targetId), 0);
          packet.set(payloadUint8, 16);
          QP.sendBinary(packet.buffer);
        });
      }
      return;
    }

    // Fallback HTTP
    fetch('/api/voice-send', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/octet-stream',
        'X-Peer-Ids': targets.join(',')
      },
      body: payloadUint8.buffer
    }).catch(() => {});
  }

  async function ensureAudio(test = false) {
    if (mediaStream && testOnly === test) return;
    stopCapture();
    unlockAudio();

    const deviceId = document.getElementById('micSelect').value;
    const processing = document.getElementById('processingToggle').checked;

    mediaStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        ...(deviceId ? { deviceId: { exact: deviceId } } : {}),
        echoCancellation: processing,
        noiseSuppression: processing,
        autoGainControl: processing,
        sampleRate: SAMPLE_RATE,
        channelCount: 1
      }
    });

    inputCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: SAMPLE_RATE });
    if (inputCtx.state === 'suspended') await inputCtx.resume();

    inputSource = inputCtx.createMediaStreamSource(mediaStream);
    analyser = inputCtx.createAnalyser();
    analyser.fftSize = 64;
    inputSource.connect(analyser);

    testOnly = test;
    inputSampleAccumulator = [];
    encodeTimestampUs = 0;
    initEncoder();

    inputProcessor = inputCtx.createScriptProcessor(1024, 1, 1);
    inputSource.connect(inputProcessor);
    const silent = inputCtx.createGain();
    silent.gain.value = 0;
    inputProcessor.connect(silent);
    silent.connect(inputCtx.destination);

    inputProcessor.onaudioprocess = e => {
      drawMeter();
      if (testOnly || globalMicMuted) return;

      const src = e.inputBuffer.getChannelData(0);
      for (let i = 0; i < src.length; i++) {
        inputSampleAccumulator.push(src[i]);
      }

      while (inputSampleAccumulator.length >= FRAME_SIZE) {
        const frame = new Float32Array(inputSampleAccumulator.splice(0, FRAME_SIZE));
        if (encoder && encoder.state === 'configured') {
          try {
            const audioData = new AudioData({
              format: 'f32-planar',
              sampleRate: SAMPLE_RATE,
              numberOfFrames: FRAME_SIZE,
              numberOfChannels: 1,
              timestamp: encodeTimestampUs,
              data: frame
            });
            encodeTimestampUs += FRAME_DURATION_US;
            encoder.encode(audioData);
            audioData.close();
          } catch (err) {
            console.warn('Encoding frame error, switching to PCM fallback', err);
            encoder = null;
          }
        }

        if (!encoder || encoder.state !== 'configured') {
          // Robust PCM fallback
          const pcm = new Int16Array(frame.length);
          for (let j = 0; j < frame.length; j++) {
            const v = Math.max(-1, Math.min(1, frame[j]));
            pcm[j] = v < 0 ? v * 32768 : v * 32767;
          }
          const pcmBytes = new Uint8Array(pcm.buffer, pcm.byteOffset, pcm.byteLength);
          const buf = new Uint8Array(1 + pcmBytes.length);
          buf[0] = CODEC_PCM;
          buf.set(pcmBytes, 1);
          dispatchVoiceFrame(buf);
        }
      }
    };
  }

  function drawMeter() {
    if (!analyser) return;
    const a = new Uint8Array(analyser.frequencyBinCount);
    analyser.getByteFrequencyData(a);
    const avg = a.reduce((x, y) => x + y, 0) / Math.max(1, a.length);
    const meter = document.getElementById('micMeter');
    if (meter) meter.style.width = `${Math.min(100, avg * 1.6)}%`;
  }

  async function testMic() {
    try {
      if (activeCalls().length) {
        QP.toast('El medidor ya está activo durante las llamadas');
        return;
      }
      if (testOnly && mediaStream) {
        stopCapture();
        return;
      }
      await ensureAudio(true);
      document.getElementById('testMicBtn').textContent = 'Detener prueba';
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function pollVoiceFallback() {
    // Only used as fallback if WebSocket is not streaming
    if (QP.isWsConnected) return;
    try {
      const data = await QP.api('/api/voice-poll');
      for (const chunk of data.chunks || []) {
        const bin = atob(chunk.data);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        output(chunk.peerId).pushDatagram(bytes);
      }
    } catch { }
  }

  function toggleGlobalMic() {
    globalMicMuted = !globalMicMuted;
    renderVoice();
    QP.toast(globalMicMuted ? 'Micrófono silenciado para todos' : 'Micrófono activado');
  }

  function toggleGlobalDeafen() {
    globalDeafened = !globalDeafened;
    for (const out of outputs.values()) {
      out.applySettings();
    }
    renderVoice();
    QP.toast(globalDeafened ? 'Audio ensordecido (no escucharás a nadie)' : 'Audio restaurado');
  }

  async function hangup(peerId) {
    try {
      await QP.api('/api/voice-hangup', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId })
      });
    } catch { }
    outputs.get(peerId)?.dispose();
    outputs.delete(peerId);
    if (activeCalls().length <= 1) stopCapture();
    refreshVoice();
  }

  function stopCapture() {
    try { encoder?.close(); } catch { }
    try { inputProcessor?.disconnect(); } catch { }
    try { inputSource?.disconnect(); } catch { }
    try { inputCtx?.close(); } catch { }
    mediaStream?.getTracks().forEach(t => t.stop());
    mediaStream = null;
    inputCtx = null;
    inputSource = null;
    inputProcessor = null;
    analyser = null;
    encoder = null;
    testOnly = false;
    inputSampleAccumulator = [];

    const meter = document.getElementById('micMeter');
    if (meter) meter.style.width = '0%';
    const button = document.getElementById('testMicBtn');
    if (button) button.textContent = 'Probar micrófono';
  }

  function openOpusModal() {
    const modal = document.getElementById('opusModal');
    if (modal) {
      document.getElementById('opusBitrateSelect').value = String(getOpusBitrate());
      const jbToggle = document.getElementById('jitterBufferToggle');
      if (jbToggle) jbToggle.checked = isJitterBufferEnabled();
      const jbStatus = document.getElementById('modalJitterStatus');
      if (jbStatus) jbStatus.textContent = isJitterBufferEnabled() ? '60 ms (1 frame)' : 'Desactivado (0 ms)';
      modal.classList.remove('hidden');
    }
  }

  function closeOpusModal() {
    const modal = document.getElementById('opusModal');
    if (modal) modal.classList.add('hidden');
  }

  function saveOpusModal() {
    const sel = document.getElementById('opusBitrateSelect');
    const val = parseInt(sel.value, 10);
    setOpusBitrate(val);

    const jbToggle = document.getElementById('jitterBufferToggle');
    const jb = jbToggle ? jbToggle.checked : true;
    setJitterBufferEnabled(jb);

    closeOpusModal();
    QP.toast(`Configuración guardada (${val / 1000} kbps · ${jb ? 'Buffer 60ms activo' : 'Buffer desactivado'})`, 'success');
  }
})();
