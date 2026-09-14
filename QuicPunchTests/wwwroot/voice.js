(() => {
  let voiceStatus = null;
  let voiceState = {
    isMuted: false,
    isDeafened: false,
    masterVolume: 1.0,
    isTestLoopback: false,
    currentInputDevice: 'default',
    currentOutputDevice: 'default',
    backendName: 'Native Audio',
    activeCalls: []
  };

  const callingPeers = new Set();
  const localPeerVolumes = new Map();
  const localPeerMutes = new Map();
  const localOutboundMutes = new Map();
  const localScreenAllowed = new Map();
  const talkingPeers = new Set();

  // Screen Sharing State & Multi-Stream Grid Manager
  let screenStream = null;
  let screenAudioStream = null;
  let screenAudioContext = null;
  let screenAudioProcessor = null;
  let screenAudioSource = null;
  let screenCaptureInterval = null;
  let screenCaptureActive = false;
  let screenCaptureVideo = null;
  let screenCaptureAnimId = null;
  let screenVideoEncoder = null;
  let screenVideoReader = null;
  const activeScreenStreams = new Map(); // peerId.toLowerCase() -> entry
  let screenOffscreenCanvas = null;

  let screenShareConfig = {
    codec: 'h264',
    resolution: '1080',
    fps: 60,
    bitrate: 12000000
  };

  let currentIncomingCall = null;

  function normalizePeerId(id) {
    if (!id) return '';
    return String(id).toLowerCase().trim();
  }

  function resolvePeerName(peerId) {
    if (!peerId) return 'Peer';
    const norm = normalizePeerId(peerId);
    if (norm === 'local' || norm === 'me') return 'You';

    const active = (voiceState.activeCalls || []).find(c => normalizePeerId(c.peerId) === norm);
    if (active && active.peerName) return active.peerName;

    const netCalls = (voiceStatus?.activeVoiceCalls || []).find(c => normalizePeerId(c.peerId) === norm);
    if (netCalls && netCalls.peerName) return netCalls.peerName;

    const trusted = (voiceStatus?.peers || []).find(p => normalizePeerId(p.id) === norm);
    if (trusted && trusted.name) return trusted.name;

    return 'Peer';
  }

  function parseGuidFromBytes(bytes, offset = 0) {
    if (!bytes || bytes.length < offset + 16) return '';
    const d1 = (bytes[offset + 3] << 24 | bytes[offset + 2] << 16 | bytes[offset + 1] << 8 | bytes[offset]) >>> 0;
    const d2 = (bytes[offset + 5] << 8 | bytes[offset + 4]) >>> 0;
    const d3 = (bytes[offset + 7] << 8 | bytes[offset + 6]) >>> 0;

    const p1 = d1.toString(16).padStart(8, '0');
    const p2 = d2.toString(16).padStart(4, '0');
    const p3 = d3.toString(16).padStart(4, '0');

    let p4 = '';
    for (let i = 8; i < 10; i++) p4 += bytes[offset + i].toString(16).padStart(2, '0');
    let p5 = '';
    for (let i = 10; i < 16; i++) p5 += bytes[offset + i].toString(16).padStart(2, '0');

    return `${p1}-${p2}-${p3}-${p4}-${p5}`.toLowerCase();
  }

  function showIncomingCall(petition) {
    currentIncomingCall = petition;
    const banner = document.getElementById('incomingCallBanner');
    if (!banner) return;
    QP.setText('incomingCallPeerName', petition.peerName || 'Peer');
    const avatar = document.getElementById('incomingCallAvatar');
    if (avatar) avatar.textContent = '📞';
    banner.classList.remove('hidden');
  }

  function hideIncomingCall() {
    currentIncomingCall = null;
    const banner = document.getElementById('incomingCallBanner');
    if (banner) banner.classList.add('hidden');
  }

  async function acceptIncomingCall() {
    if (!currentIncomingCall) return;
    const petition = currentIncomingCall;
    hideIncomingCall();
    try {
      await QP.api('/api/respond-petition', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestId: petition.requestId, accept: true })
      });
      QP.toast(`Call answered with ${petition.peerName || 'peer'}`, 'success');
      QP.navigate('voice');
      refreshVoice();
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function declineIncomingCall() {
    if (!currentIncomingCall) return;
    const petition = currentIncomingCall;
    hideIncomingCall();
    try {
      await QP.api('/api/respond-petition', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ requestId: petition.requestId, accept: false })
      });
      QP.toast('Call declined');
    } catch { }
  }

  document.addEventListener('DOMContentLoaded', () => {
    // Global Topbar & Call Bar Buttons
    const micBtn = document.getElementById('globalMicBtn');
    if (micBtn) micBtn.addEventListener('click', toggleMute);

    const deafenBtn = document.getElementById('globalDeafenBtn');
    if (deafenBtn) deafenBtn.addEventListener('click', toggleDeafen);

    const callMicBtn = document.getElementById('globalCallMicBtn');
    if (callMicBtn) callMicBtn.addEventListener('click', toggleMute);

    const callDeafenBtn = document.getElementById('globalCallDeafenBtn');
    if (callDeafenBtn) callDeafenBtn.addEventListener('click', toggleDeafen);

    const callHangupBtn = document.getElementById('globalCallHangupBtn');
    if (callHangupBtn) callHangupBtn.addEventListener('click', hangupAll);

    const callGoToVoiceBtn = document.getElementById('globalCallGoToVoiceBtn');
    if (callGoToVoiceBtn) callGoToVoiceBtn.addEventListener('click', () => QP.navigate('voice'));

    // Voice Studio "My Audio & Streaming" Panel Buttons
    const panelMicBtn = document.getElementById('voicePanelMicBtn');
    if (panelMicBtn) panelMicBtn.addEventListener('click', toggleMute);

    const panelDeafenBtn = document.getElementById('voicePanelDeafenBtn');
    if (panelDeafenBtn) panelDeafenBtn.addEventListener('click', toggleDeafen);

    const panelScreenBtn = document.getElementById('voicePanelScreenBtn');
    if (panelScreenBtn) panelScreenBtn.addEventListener('click', toggleScreenShare);

    // Incoming Call Banner Buttons
    const acceptCallBtn = document.getElementById('acceptIncomingCallBtn');
    if (acceptCallBtn) acceptCallBtn.addEventListener('click', acceptIncomingCall);

    const declineCallBtn = document.getElementById('declineIncomingCallBtn');
    if (declineCallBtn) declineCallBtn.addEventListener('click', declineIncomingCall);

    // Device Selects
    const micSelect = document.getElementById('micSelect');
    if (micSelect) {
      micSelect.addEventListener('change', () => {
        setDevice('input', micSelect.value);
      });
    }

    const speakerSelect = document.getElementById('speakerSelect');
    if (speakerSelect) {
      speakerSelect.addEventListener('change', () => {
        setDevice('output', speakerSelect.value);
      });
    }

    // Master Volume
    const masterSlider = document.getElementById('masterVolumeSlider');
    if (masterSlider) {
      masterSlider.addEventListener('input', () => {
        const val = Number(masterSlider.value) / 100;
        setMasterVolume(val);
        const label = document.getElementById('masterVolumeLabel');
        if (label) label.textContent = `${masterSlider.value}%`;
      });
    }

    // Test Mic Button
    const testBtn = document.getElementById('testMicBtn');
    if (testBtn) testBtn.addEventListener('click', toggleTestMic);

    // Advanced Opus Modal
    const advBtn = document.getElementById('advancedOpusBtn');
    if (advBtn) advBtn.addEventListener('click', openOpusModal);

    const closeAdvBtn = document.getElementById('closeOpusModalBtn');
    if (closeAdvBtn) closeAdvBtn.addEventListener('click', closeOpusModal);

    // Screen Settings Modal & Buttons
    const screenSettingsBtn = document.getElementById('voiceScreenSettingsBtn');
    if (screenSettingsBtn) screenSettingsBtn.addEventListener('click', openScreenSettingsModal);

    const screenGlobalSettingsBtn = document.getElementById('screenShareGlobalSettingsBtn');
    if (screenGlobalSettingsBtn) screenGlobalSettingsBtn.addEventListener('click', openScreenSettingsModal);

    const closeScreenBtn = document.getElementById('closeScreenModalBtn');
    if (closeScreenBtn) closeScreenBtn.addEventListener('click', closeScreenSettingsModal);

    const saveScreenBtn = document.getElementById('saveScreenModalBtn');
    if (saveScreenBtn) saveScreenBtn.addEventListener('click', saveScreenSettingsModal);

    loadScreenShareSettings();

    // WebSocket Events
    if (window.QP && typeof QP.on === 'function') {
      QP.on('voice_vad', onVoiceVad);
      QP.on('voice_state', onVoiceStateUpdated);
      QP.on('voice_signal', onVoiceSignal);
      QP.on('screen_share_started', onScreenShareStarted);
      QP.on('screen_share_stopped', onScreenShareStopped);
      QP.on('screen_frame', onScreenFrameReceived);
      QP.on('ws_binary', onWsBinaryReceived);
      QP.on('petition_received', petition => {
        if (!petition) return;
        const proto = (petition.protocolName || '').toLowerCase();
        if (proto.includes('voice') || proto.includes('call')) {
          showIncomingCall(petition);
        }
      });
      QP.on('status_updated', status => {
        voiceStatus = status;
        if (currentIncomingCall) {
          const stillPending = (status?.pendingPetitions || []).some(
            p => p.requestId === currentIncomingCall.requestId || p.id === currentIncomingCall.id
          );
          if (!stillPending) {
            hideIncomingCall();
          }
        }
        renderVoice();
      });
      QP.on('peer_connected', () => refreshVoice());
      QP.on('peer_disconnected', () => refreshVoice());
      QP.on('view_changed', data => {
        if (data && data.page === 'voice') refreshVoice();
      });
    }

    loadAudioDevices();
    refreshVoice();
  });

  async function loadAudioDevices() {
    try {
      const res = await QP.api('/api/voice/devices');
      if (!res) return;

      const micSelect = document.getElementById('micSelect');
      if (micSelect && Array.isArray(res.inputs)) {
        QP.clear(micSelect);
        res.inputs.forEach(dev => {
          const opt = document.createElement('option');
          opt.value = dev.id;
          opt.textContent = dev.name;
          if (dev.id === res.currentInput) opt.selected = true;
          micSelect.appendChild(opt);
        });
      }

      const speakerSelect = document.getElementById('speakerSelect');
      if (speakerSelect && Array.isArray(res.outputs)) {
        QP.clear(speakerSelect);
        res.outputs.forEach(dev => {
          const opt = document.createElement('option');
          opt.value = dev.id;
          opt.textContent = dev.name;
          if (dev.id === res.currentOutput) opt.selected = true;
          speakerSelect.appendChild(opt);
        });
      }
    } catch (e) {
      console.warn('Could not load native audio devices', e);
    }
  }

  async function setDevice(type, deviceId) {
    try {
      await QP.api('/api/voice/device', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ type, deviceId })
      });
      QP.toast(`${type === 'input' ? 'Input' : 'Output'} audio device updated`);
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function refreshVoice() {
    try {
      const netPromise = (QP.state && QP.state.status) ? Promise.resolve(QP.state.status) : (QP.getStatus ? QP.getStatus() : QP.api('/api/status'));
      const [status, state] = await Promise.all([
        netPromise,
        QP.api('/api/voice/state').catch(() => null)
      ]);
      voiceStatus = status;
      if (state) {
        voiceState = state;
        if (Array.isArray(state.activeCalls)) {
          state.activeCalls.forEach(c => {
            localPeerVolumes.set(c.peerId, c.volume);
            localPeerMutes.set(c.peerId, c.muted);
            localOutboundMutes.set(c.peerId, !!c.outboundMuted);
            localScreenAllowed.set(c.peerId, c.screenShareAllowed !== false);
          });
        }
      }
      renderVoice();
    } catch (e) {
      console.warn('Error refreshing voice status', e);
    }
  }

  function onVoiceStateUpdated(state) {
    if (!state) return;
    voiceState = state;
    if (Array.isArray(state.activeCalls)) {
      state.activeCalls.forEach(c => {
        localPeerVolumes.set(c.peerId, c.volume);
        localPeerMutes.set(c.peerId, c.muted);
        localOutboundMutes.set(c.peerId, !!c.outboundMuted);
        localScreenAllowed.set(c.peerId, c.screenShareAllowed !== false);
      });
    }
    renderVoice();
  }

  function onVoiceSignal(data) {
    if (!data) return;
    if (data.signal === 'call-established') {
      callingPeers.delete(data.peerId);
      hideIncomingCall();
      QP.toast('Voice call established', 'success');
      refreshVoice();
    } else if (data.signal === 'call-ended') {
      callingPeers.delete(data.peerId);
      talkingPeers.delete(data.peerId);
      hideIncomingCall();
      removeScreenTile(data.peerId);
      refreshVoice();
    }
  }

  function onVoiceVad(data) {
    if (!data) return;
    if (data.isLocal) {
      const meter = document.getElementById('micMeter');
      if (meter) {
        const pct = Math.min(100, Math.round(data.level * 100));
        meter.style.width = `${pct}%`;
      }
      const localAvatar = document.querySelector('.local-avatar');
      if (localAvatar) {
        if (data.speaking) localAvatar.classList.add('talking');
        else localAvatar.classList.remove('talking');
      }
    } else if (data.peerId) {
      const peerCard = document.querySelector(`[data-call-peer-id="${data.peerId}"]`);
      if (peerCard) {
        const avatar = peerCard.querySelector('.avatar');
        const badge = peerCard.querySelector('.live-badge');
        if (data.speaking) {
          avatar?.classList.add('talking');
          if (badge) {
            badge.className = 'badge green live-badge talking-badge';
            badge.textContent = 'Speaking';
          }
          talkingPeers.add(data.peerId);
        } else {
          avatar?.classList.remove('talking');
          if (badge) {
            badge.className = 'badge green live-badge';
            badge.textContent = 'Live';
          }
          talkingPeers.delete(data.peerId);
        }
      }
    }
  }

  function activeCallsList() {
    return voiceStatus?.activeVoiceCalls || [];
  }

  function renderVoice() {
    const calls = activeCallsList();
    const peers = QP.trusted(voiceStatus);

    // Badges & Top Actions
    QP.setText('voiceBadge', `${calls.length} active`);
    const voiceBadge = document.getElementById('voiceBadge');
    if (voiceBadge) {
      if (calls.length > 0) voiceBadge.classList.remove('hidden');
      else voiceBadge.classList.add('hidden');
    }

    const backendBadge = document.getElementById('audioBackendBadge');
    if (backendBadge && voiceState.backendName) {
      backendBadge.textContent = voiceState.backendName;
    }

    // 1. Global Topbar Buttons
    const micBtn = document.getElementById('globalMicBtn');
    if (micBtn) {
      micBtn.textContent = voiceState.isMuted ? '🔇' : '🎙️';
      micBtn.className = `btn sm ${voiceState.isMuted ? 'danger' : 'green'}`;
      micBtn.title = voiceState.isMuted ? 'Microphone muted (Peers cannot hear you)' : 'Microphone active (Peers can hear you)';
    }

    const deafenBtn = document.getElementById('globalDeafenBtn');
    if (deafenBtn) {
      deafenBtn.textContent = voiceState.isDeafened ? '🔇' : '🎧';
      deafenBtn.className = `btn sm ${voiceState.isDeafened ? 'danger' : 'green'}`;
      deafenBtn.title = voiceState.isDeafened ? 'Audio deafened (You cannot hear anyone)' : 'Audio active (Listening)';
    }

    // 2. Voice Studio "My Audio & Streaming" Panel Buttons
    const panelMicBtn = document.getElementById('voicePanelMicBtn');
    if (panelMicBtn) {
      panelMicBtn.className = `btn ${voiceState.isMuted ? 'danger' : 'green'} voice-ctrl-btn`;
      QP.setText('voicePanelMicIcon', voiceState.isMuted ? '🔇' : '🎙️');
      QP.setText('voicePanelMicText', voiceState.isMuted ? 'Mic Muted' : 'Mic Active');
      QP.setText('voicePanelMicSub', voiceState.isMuted ? 'Peers cannot hear you' : 'Peers can hear you');
    }

    const panelDeafenBtn = document.getElementById('voicePanelDeafenBtn');
    if (panelDeafenBtn) {
      panelDeafenBtn.className = `btn ${voiceState.isDeafened ? 'danger' : 'green'} voice-ctrl-btn`;
      QP.setText('voicePanelDeafenIcon', voiceState.isDeafened ? '🔇' : '🎧');
      QP.setText('voicePanelDeafenText', voiceState.isDeafened ? 'Audio Deafened' : 'Audio Active');
      QP.setText('voicePanelDeafenSub', voiceState.isDeafened ? 'You cannot hear anyone' : 'Listening to participants');
    }

    const panelScreenBtn = document.getElementById('voicePanelScreenBtn');
    if (panelScreenBtn) {
      const isSharing = !!screenStream || !!voiceState.isScreenSharing || !!voiceState.isNativeScreenSharing;
      panelScreenBtn.className = `btn ${isSharing ? 'danger sharing-screen' : 'ghost'} voice-ctrl-btn`;
      QP.setText('voicePanelScreenIcon', isSharing ? '⏹️' : '🖥️');
      QP.setText('voicePanelScreenText', isSharing ? 'Stop Sharing' : 'Share Screen');
      QP.setText('voicePanelScreenSub', isSharing ? 'Broadcasting your screen' : 'Stream monitor or window');
    }

    // 3. Global Floating Call Bar
    const callBar = document.getElementById('globalActiveCallBar') || document.getElementById('globalCallBar');
    if (callBar) {
      const isVoicePage = QP.currentPage === 'voice' || document.body.dataset.page === 'voice';
      if (calls.length > 0 && !isVoicePage) {
        callBar.classList.remove('hidden');
        const names = calls.map(c => c.peerName || 'Peer').join(', ');
        QP.setText('globalCallPeerName', names);
        QP.setText('globalCallMeta', `${calls.length} participant${calls.length === 1 ? '' : 's'} · Native ${voiceState.backendName}`);
        
        const callMic = document.getElementById('globalCallMicBtn');
        if (callMic) {
          callMic.textContent = voiceState.isMuted ? '🔇' : '🎙️';
          callMic.className = `btn sm ${voiceState.isMuted ? 'danger' : 'ghost'}`;
          callMic.title = voiceState.isMuted ? 'Microphone muted' : 'Microphone active';
        }
        const callDeafen = document.getElementById('globalCallDeafenBtn');
        if (callDeafen) {
          callDeafen.textContent = voiceState.isDeafened ? '🔇' : '🎧';
          callDeafen.className = `btn sm ${voiceState.isDeafened ? 'danger' : 'ghost'}`;
          callDeafen.title = voiceState.isDeafened ? 'Audio deafened' : 'Audio active';
        }
      } else {
        callBar.classList.add('hidden');
      }
    }

    // Test Mic Button text
    const testBtn = document.getElementById('testMicBtn');
    if (testBtn) {
      testBtn.textContent = voiceState.isTestLoopback ? 'Stop Test' : 'Test Mic';
      testBtn.className = voiceState.isTestLoopback ? 'btn danger' : 'btn';
    }

    // Master Volume Slider
    const masterSlider = document.getElementById('masterVolumeSlider');
    if (masterSlider && document.activeElement !== masterSlider) {
      const pct = Math.round((voiceState.masterVolume || 1.0) * 100);
      masterSlider.value = String(pct);
      const label = document.getElementById('masterVolumeLabel');
      if (label) label.textContent = `${pct}%`;
    }

    renderActiveCalls(calls, peers);
    renderVoicePeers(peers, calls);
  }

  function renderActiveCalls(calls, peers) {
    const host = document.getElementById('activeCalls');
    if (!host) return;
    QP.clear(host);

    if (!calls.length) {
      host.appendChild(QP.el('div', 'empty', 'No active calls. Start a call below; audio is played directly through your system audio device.'));
      return;
    }

    calls.forEach(call => {
      const peer = peers.find(p => p.id === call.peerId) || { id: call.peerId, name: call.peerName || 'Peer' };
      const card = QP.el('div', 'call-card voice-call-row');
      card.dataset.callPeerId = peer.id;

      // 1. Head (Avatar + Peer Info)
      const head = QP.el('div', 'call-card-head');
      const avatar = QP.el('div', 'avatar', QP.initials(peer.name));
      if (talkingPeers.has(peer.id)) avatar.classList.add('talking');
      
      const copy = QP.el('div', 'peer-copy');
      copy.append(
        QP.el('div', 'peer-name', peer.name),
        QP.el('div', 'peer-meta', `Opus VoIP 48kHz · ${QP.formatPingStatus(peer)}`)
      );
      const liveBadge = QP.el('span', 'badge green live-badge', talkingPeers.has(peer.id) ? 'Speaking' : 'Live');
      head.append(avatar, copy, liveBadge);

      // 2. Individual Peer Controls:
      // - mutePeerBtn (🎧/🔇): Mute hearing this peer
      // - outboundMuteBtn (🎙️/🎙️🚫): Selective outbound mute (my mic to this peer only)
      // - hangupBtn (🛑): End call with this peer
      const controls = QP.el('div', 'call-card-controls');
      const isMuted = localPeerMutes.get(peer.id) || false;
      const isOutboundMuted = localOutboundMutes.get(peer.id) || false;

      const mutePeerBtn = document.createElement('button');
      mutePeerBtn.className = `call-action-btn ${isMuted ? 'muted-red' : 'active-green'}`;
      mutePeerBtn.textContent = isMuted ? '🔇' : '🎧';
      mutePeerBtn.title = isMuted ? `Unmute audio from ${peer.name} (hear them again)` : `Mute audio from ${peer.name} for you (deafen them)`;
      mutePeerBtn.addEventListener('click', () => togglePeerMute(peer.id));

      const outboundMuteBtn = document.createElement('button');
      outboundMuteBtn.className = `call-action-btn ${isOutboundMuted ? 'muted-red' : 'active-green'}`;
      outboundMuteBtn.textContent = isOutboundMuted ? '🎙️🚫' : '🎙️';
      outboundMuteBtn.title = isOutboundMuted
        ? `Unmute your voice for ${peer.name} (${peer.name} will hear you again)`
        : `Mute your voice for ${peer.name} only (others still hear you, but ${peer.name} will not)`;
      outboundMuteBtn.addEventListener('click', () => toggleOutboundMute(peer.id));

      const isScreenAllowed = localScreenAllowed.get(peer.id) !== false;
      const screenSharePeerBtn = document.createElement('button');
      screenSharePeerBtn.className = `call-action-btn ${isScreenAllowed ? 'screenshare-active' : 'screenshare-blocked'}`;
      screenSharePeerBtn.textContent = isScreenAllowed ? '🖥️' : '🖥️🚫';
      screenSharePeerBtn.title = isScreenAllowed
        ? `Sharing screen allowed with ${peer.name} (click to block them from seeing your screen)`
        : `Screen sharing blocked for ${peer.name} (click to allow them to see your screen)`;
      screenSharePeerBtn.addEventListener('click', () => togglePeerScreenShare(peer.id));

      const hangupBtn = document.createElement('button');
      hangupBtn.className = 'call-action-btn hangup-red';
      hangupBtn.textContent = '🛑';
      hangupBtn.title = `Hang up call with ${peer.name}`;
      hangupBtn.addEventListener('click', () => hangup(peer.id));

      controls.append(mutePeerBtn, outboundMuteBtn, screenSharePeerBtn, hangupBtn);

      // 3. Individual Volume Slider (0% - 200%)
      const volContainer = QP.el('div', 'volume-row');
      volContainer.append(QP.el('span', 'muted', 'Vol'));
      const slider = document.createElement('input');
      slider.type = 'range';
      slider.min = '0';
      slider.max = '200';
      slider.step = '5';
      const curVol = Math.round((localPeerVolumes.get(peer.id) ?? 1.0) * 100);
      slider.value = String(curVol);

      const volLabel = QP.el('span', 'mono volume-value', `${curVol}%`);
      slider.addEventListener('input', () => {
        const val = Number(slider.value) / 100;
        volLabel.textContent = `${slider.value}%`;
        setPeerVolume(peer.id, val);
      });
      volContainer.append(slider, volLabel);

      card.append(head, controls, volContainer);
      host.appendChild(card);
    });
  }

  function renderVoicePeers(peers, calls) {
    const box = document.getElementById('voicePeers');
    if (!box) return;
    QP.clear(box);

    if (!peers.length) {
      box.appendChild(QP.el('div', 'empty', 'No trusted peers available to call. Connect to a peer in Network first.'));
      return;
    }

    peers.forEach(peer => {
      const isCalling = callingPeers.has(peer.id);
      const inCall = calls.some(c => c.peerId === peer.id);

      const row = QP.el('div', 'peer-row voice-peer-row');
      row.appendChild(QP.el('div', 'avatar', QP.initials(peer.name)));

      const copy = QP.el('div', 'peer-copy');
      copy.append(
        QP.el('div', 'peer-name', peer.name),
        QP.el('div', 'peer-meta', inCall ? 'Active call' : (isCalling ? 'Calling…' : QP.peerMeta(peer)))
      );

      const actions = QP.el('div', 'row-actions');
      if (inCall) {
        actions.append(QP.button('Hang Up', 'danger', () => hangup(peer.id)));
      } else {
        const btn = QP.button(isCalling ? 'Calling…' : 'Call', 'accent', () => startCall(peer.id));
        btn.disabled = isCalling;
        actions.append(btn);
      }

      row.append(copy, actions);
      box.appendChild(row);
    });
  }

  async function startCall(peerId) {
    callingPeers.add(peerId);
    renderVoice();
    try {
      await QP.api('/api/connect-peer', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, protocol: 'voice' })
      });
      QP.toast('Initiating voice call…');
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

  async function hangup(peerId) {
    callingPeers.delete(peerId);
    talkingPeers.delete(peerId);
    if (Array.isArray(voiceState.activeCalls)) {
      voiceState.activeCalls = voiceState.activeCalls.filter(c => c.peerId !== peerId);
    }
    if (voiceStatus && Array.isArray(voiceStatus.activeVoiceCalls)) {
      voiceStatus.activeVoiceCalls = voiceStatus.activeVoiceCalls.filter(c => c.peerId !== peerId);
    }
    removeScreenTile(peerId);

    // If no active calls remain, stop screen broadcast
    if (activeCallsList().length === 0 && (screenStream || voiceState.isScreenSharing)) {
      stopScreenShare();
    }

    renderVoice();

    try {
      await QP.api('/api/voice-hangup', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId })
      });
      QP.toast('Call ended');
    } catch (e) {
      console.warn('Error hanging up', e);
    }
    refreshVoice();
  }

  async function hangupAll() {
    const calls = activeCallsList();
    const ids = new Set([
      ...calls.map(c => c.peerId),
      ...Array.from(callingPeers.values()),
      ...(voiceState.activeCalls || []).map(c => c.peerId)
    ]);
    if (ids.size === 0) return;
    for (const peerId of ids) {
      await hangup(peerId);
    }
    if (screenStream || voiceState.isScreenSharing) {
      stopScreenShare();
    }
  }

  async function toggleMute() {
    try {
      const res = await QP.api('/api/voice/mute', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ muted: !voiceState.isMuted })
      });
      voiceState.isMuted = res.isMuted;
      renderVoice();
      QP.toast(voiceState.isMuted ? 'Microphone muted' : 'Microphone unmuted');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function toggleDeafen() {
    try {
      const res = await QP.api('/api/voice/deafen', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deafened: !voiceState.isDeafened })
      });
      voiceState.isDeafened = res.isDeafened;
      renderVoice();
      QP.toast(voiceState.isDeafened ? 'Audio deafened (muted all sound)' : 'Audio restored');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function setMasterVolume(val) {
    voiceState.masterVolume = val;
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ masterVolume: val })
      });
    } catch { }
  }

  async function setPeerVolume(peerId, volume) {
    localPeerVolumes.set(peerId, volume);
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, volume })
      });
    } catch { }
  }

  async function togglePeerMute(peerId) {
    const current = localPeerMutes.get(peerId) || false;
    const next = !current;
    localPeerMutes.set(peerId, next);
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, muted: next })
      });
      renderVoice();
      QP.toast(next ? 'Peer audio muted' : 'Peer audio unmuted');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function toggleOutboundMute(peerId) {
    const current = localOutboundMutes.get(peerId) || false;
    const next = !current;
    localOutboundMutes.set(peerId, next);
    try {
      await QP.api('/api/voice/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, outboundMuted: next })
      });
      renderVoice();
      const peerName = resolvePeerName(peerId);
      QP.toast(next ? `Your mic is now muted for ${peerName}` : `Your mic is now unmuted for ${peerName}`);
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  async function togglePeerScreenShare(peerId) {
    const current = localScreenAllowed.get(peerId) !== false;
    const next = !current;
    localScreenAllowed.set(peerId, next);
    try {
      await QP.api('/api/voice/screen-share/peer-toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ peerId, allowed: next })
      });
      renderVoice();
      const peerName = resolvePeerName(peerId);
      QP.toast(next ? `Screen sharing enabled for ${peerName}` : `Screen sharing blocked for ${peerName}`);
    } catch (e) {
      localScreenAllowed.set(peerId, current);
      renderVoice();
      QP.toast(e.message, 'error');
    }
  }

  async function toggleTestMic() {
    try {
      const res = await QP.api('/api/voice/test-mic', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: !voiceState.isTestLoopback })
      });
      voiceState.isTestLoopback = res.isTestLoopback;
      renderVoice();
      QP.toast(voiceState.isTestLoopback ? 'Mic test started: speak to hear your voice through your speakers' : 'Mic test stopped');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  function openOpusModal() {
    const modal = document.getElementById('opusModal');
    if (!modal) return;
    const select = document.getElementById('opusBitrateSelect');
    if (select && voiceState.opusBitrate) {
      select.value = String(voiceState.opusBitrate);
    }
    modal.classList.remove('hidden');
  }

  function closeOpusModal() {
    const modal = document.getElementById('opusModal');
    if (modal) modal.classList.add('hidden');
  }

  async function saveOpusModal() {
    const select = document.getElementById('opusBitrateSelect');
    const bitrate = select ? parseInt(select.value, 10) : 96000;
    try {
      const res = await QP.api('/api/voice/codec-settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bitrate })
      });
      voiceState.opusBitrate = res.bitrate;
      closeOpusModal();
      QP.toast(`VoIP bitrate updated to ${Math.round(bitrate / 1000)} kbps`, 'success');
    } catch (e) {
      QP.toast(e.message, 'error');
    }
  }

  // --- Screen Sharing Settings Modal & Persistence ---

  function updateScreenSettingsSubtitle() {
    const subEl = document.getElementById('voiceScreenSettingsSub');
    if (subEl) {
      const resStr = screenShareConfig.resolution === 'native' ? 'Native' : screenShareConfig.resolution + 'p';
      subEl.textContent = `${screenShareConfig.fps} FPS · ${resStr} · ${(screenShareConfig.codec || 'H264').toUpperCase()}`;
    }
  }

  function loadScreenShareSettings() {
    try {
      const saved = localStorage.getItem('qp_screen_config');
      if (saved) {
        const parsed = JSON.parse(saved);
        if (parsed && typeof parsed === 'object') {
          screenShareConfig = Object.assign(screenShareConfig, parsed);
        }
      }
    } catch { }

    const codecSelect = document.getElementById('screenCodecSelect');
    if (codecSelect && screenShareConfig.codec) codecSelect.value = screenShareConfig.codec;

    const resSelect = document.getElementById('screenResolutionSelect');
    if (resSelect && screenShareConfig.resolution) resSelect.value = screenShareConfig.resolution;

    const fpsSelect = document.getElementById('screenFpsSelect');
    if (fpsSelect && screenShareConfig.fps) fpsSelect.value = String(screenShareConfig.fps);

    const brSelect = document.getElementById('screenBitrateSelect');
    if (brSelect && screenShareConfig.bitrate) brSelect.value = String(screenShareConfig.bitrate);

    updateScreenSettingsSubtitle();
  }

  function openScreenSettingsModal() {
    loadScreenShareSettings();
    const modal = document.getElementById('screenShareSettingsModal');
    if (modal) modal.classList.remove('hidden');
  }

  function closeScreenSettingsModal() {
    const modal = document.getElementById('screenShareSettingsModal');
    if (modal) modal.classList.add('hidden');
  }

  async function saveScreenSettingsModal() {
    const codecSelect = document.getElementById('screenCodecSelect');
    const resSelect = document.getElementById('screenResolutionSelect');
    const fpsSelect = document.getElementById('screenFpsSelect');
    const brSelect = document.getElementById('screenBitrateSelect');

    if (codecSelect) screenShareConfig.codec = codecSelect.value;
    if (resSelect) screenShareConfig.resolution = resSelect.value;
    if (fpsSelect) screenShareConfig.fps = parseInt(fpsSelect.value, 10) || 60;
    if (brSelect) screenShareConfig.bitrate = parseInt(brSelect.value, 10) || 12000000;

    try {
      localStorage.setItem('qp_screen_config', JSON.stringify(screenShareConfig));
    } catch { }

    updateScreenSettingsSubtitle();
    closeScreenSettingsModal();

    // If screen transmission is currently active, apply changes immediately in real-time!
    if (screenStream && voiceState.isScreenSharing) {
      const fps = screenShareConfig.fps || 60;
      let idealW = 1920;
      let idealH = 1080;
      if (screenShareConfig.resolution === '720') { idealW = 1280; idealH = 720; }
      else if (screenShareConfig.resolution === '1440') { idealW = 2560; idealH = 1440; }
      else if (screenShareConfig.resolution === '2160') { idealW = 3840; idealH = 2160; }
      else if (screenShareConfig.resolution === 'native') { idealW = window.screen.width; idealH = window.screen.height; }

      const codecId = getCodecId(screenShareConfig.codec);

      const videoTrack = screenStream.getVideoTracks()[0];
      if (videoTrack) {
        try {
          await videoTrack.applyConstraints({
            frameRate: { ideal: fps, max: fps },
            width: { ideal: idealW },
            height: { ideal: idealH }
          }).catch(() => {});
        } catch {}
      }

      const settings = videoTrack ? videoTrack.getSettings() : {};
      const width = settings.width || idealW;
      const height = settings.height || idealH;

      // Re-initialize frame extraction with new target parameters
      startCanvasFrameExtraction(screenStream, width, height, codecId, fps);

      // Notify backend and peers of new stream parameters
      QP.api('/api/voice/screen-share/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ width, height, codecId, fps })
      }).catch(() => {});

      createOrUpdateScreenTile('local', 'You (Your Screen)', width, height, codecId, fps, true);
      QP.toast(`Live screen settings updated: ${fps} FPS · ${width}x${height} · ${(screenShareConfig.codec || 'H264').toUpperCase()}`, 'success');
      return;
    }

    if (voiceState.isNativeScreenSharing) {
      const fps = screenShareConfig.fps || 60;
      let idealW = 1920;
      let idealH = 1080;
      if (screenShareConfig.resolution === '720') { idealW = 1280; idealH = 720; }
      else if (screenShareConfig.resolution === '1440') { idealW = 2560; idealH = 1440; }
      else if (screenShareConfig.resolution === '2160') { idealW = 3840; idealH = 2160; }

      QP.api('/api/voice/screen-share/native-toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enable: true, fps, width: idealW, height: idealH })
      }).catch(() => {});

      createOrUpdateScreenTile('local', 'You (Native Desktop Capture)', idealW, idealH, 0, fps, true);
      QP.toast(`Live native screen settings updated: ${fps} FPS · ${idealW}x${idealH}`, 'success');
      return;
    }

    QP.toast(`Screen settings saved: ${(screenShareConfig.codec || 'H264').toUpperCase()} · ${screenShareConfig.fps} FPS · ${Math.round(screenShareConfig.bitrate / 1000)} kbps`, 'success');
  }

  function getCodecId(codecName) {
    switch ((codecName || '').toLowerCase()) {
      case 'h264': return 1;
      case 'vp9': return 3;
      case 'av1': return 4;
      case 'hevc': return 5;
      default: return 0; // 0 = JPEG / fallback
    }
  }

  function getCodecNameFromId(codecId) {
    switch (codecId) {
      case 1: return 'H.264 / AVC';
      case 3: return 'VP9';
      case 4: return 'AV1';
      case 5: return 'H.265 / HEVC';
      default: return 'JPEG';
    }
  }

  // --- High-Quality Screen Sharing Implementation ---

  async function toggleScreenShare() {
    if (screenStream || voiceState.isScreenSharing || voiceState.isNativeScreenSharing) {
      await stopScreenShare();
    } else {
      await startScreenShare();
    }
  }

  async function startScreenShare() {
    // 1. Try Browser / Chromium getDisplayMedia API
    if (navigator.mediaDevices && typeof navigator.mediaDevices.getDisplayMedia === 'function') {
      try {
        const fps = screenShareConfig.fps || 60;
        let idealW = 1920;
        let idealH = 1080;
        if (screenShareConfig.resolution === '720') { idealW = 1280; idealH = 720; }
        else if (screenShareConfig.resolution === '1440') { idealW = 2560; idealH = 1440; }
        else if (screenShareConfig.resolution === '2160') { idealW = 3840; idealH = 2160; }
        else if (screenShareConfig.resolution === 'native') { idealW = window.screen.width; idealH = window.screen.height; }

        const stream = await navigator.mediaDevices.getDisplayMedia({
          video: {
            cursor: 'always',
            frameRate: { ideal: fps, max: fps },
            width: { ideal: idealW },
            height: { ideal: idealH }
          },
          audio: {
            echoCancellation: false,
            noiseSuppression: false,
            autoGainControl: false
          }
        });

        screenStream = stream;
        const videoTrack = stream.getVideoTracks()[0];
        if (videoTrack) {
          videoTrack.onended = () => {
            stopScreenShare();
          };
        }

        // Start screen audio capture if system/tab audio was shared
        startScreenAudioCapture(stream);

        const settings = videoTrack ? videoTrack.getSettings() : {};
        const width = settings.width || idealW;
        const height = settings.height || idealH;
        const codecId = getCodecId(screenShareConfig.codec);

        await QP.api('/api/voice/screen-share/start', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ width, height, codecId, fps })
        });

        // Use binary canvas frame extraction for rock-solid 0-latency streaming without MSE/WebM black screen bugs
        startCanvasFrameExtraction(stream, width, height, codecId, fps);

        voiceState.isScreenSharing = true;
        renderVoice();
        createOrUpdateScreenTile('local', 'You (Your Screen)', width, height, codecId, fps, true);
        QP.toast(`Screen broadcasting live (${getCodecNameFromId(codecId)} · ${fps} FPS)`, 'success');
        return;
      } catch (err) {
        if (err.name === 'NotAllowedError') {
          QP.toast('Screen share selection cancelled');
          return;
        }
        console.warn('getDisplayMedia error, falling back to native capture:', err);
      }
    }

    // 2. Fallback to native host screen capture via backend
    await startNativeScreenShare();
  }

  function startScreenAudioCapture(stream) {
    stopScreenAudioCapture();
    try {
      const audioTracks = stream.getAudioTracks();
      if (!audioTracks || audioTracks.length === 0) return;

      screenAudioStream = new MediaStream([audioTracks[0]]);
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;

      screenAudioContext = new AudioCtx({ sampleRate: 48000 });
      screenAudioSource = screenAudioContext.createMediaStreamSource(screenAudioStream);

      // Buffer size 1024 (approx 21ms at 48kHz)
      screenAudioProcessor = screenAudioContext.createScriptProcessor(1024, 1, 1);
      screenAudioProcessor.onaudioprocess = (e) => {
        if (!screenAudioProcessor) return;
        const inputFloat = e.inputBuffer.getChannelData(0);
        const pcmInt16 = new Int16Array(inputFloat.length);
        for (let i = 0; i < inputFloat.length; i++) {
          const s = Math.max(-1, Math.min(1, inputFloat[i]));
          pcmInt16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }

        // Tag 0x03: Screen Share Audio PCM Int16 samples
        const packet = new Uint8Array(1 + pcmInt16.byteLength);
        packet[0] = 0x03;
        packet.set(new Uint8Array(pcmInt16.buffer), 1);
        QP.sendBinary(packet.buffer);
      };

      screenAudioSource.connect(screenAudioProcessor);
      const silentGain = screenAudioContext.createGain();
      silentGain.gain.value = 0;
      screenAudioProcessor.connect(silentGain);
      silentGain.connect(screenAudioContext.destination);
      QP.toast('System & tab audio sharing enabled', 'info');
    } catch (err) {
      console.warn('Could not initialize screen audio capture:', err);
    }
  }

  function stopScreenAudioCapture() {
    if (screenAudioProcessor) {
      try { screenAudioProcessor.disconnect(); } catch { }
      screenAudioProcessor = null;
    }
    if (screenAudioSource) {
      try { screenAudioSource.disconnect(); } catch { }
      screenAudioSource = null;
    }
    if (screenAudioContext) {
      try { screenAudioContext.close(); } catch { }
      screenAudioContext = null;
    }
    if (screenAudioStream) {
      try {
        screenAudioStream.getTracks().forEach(t => t.stop());
      } catch { }
      screenAudioStream = null;
    }
  }

  async function probeVideoEncoderConfig(codecId, width, height, fps, bitrate) {
    if (typeof VideoEncoder === 'undefined') return null;

    const targetFps = Math.max(15, Math.min(144, fps || screenShareConfig.fps || 60));
    const targetBitrate = bitrate || screenShareConfig.bitrate || 12000000;

    const candidates = [];
    if (codecId === 5) {
      candidates.push({ codec: 'hev1.1.6.L93.B0', codecId: 5 });
      candidates.push({ codec: 'hvc1.1.6.L93.B0', codecId: 5 });
      candidates.push({ codec: 'hev1.1.6.L120.B0', codecId: 5 });
      candidates.push({ codec: 'avc1.42002A', codecId: 1 });
      candidates.push({ codec: 'avc1.64002A', codecId: 1 });
    } else if (codecId === 3) {
      candidates.push({ codec: 'vp09.00.10.08', codecId: 3 });
      candidates.push({ codec: 'vp9', codecId: 3 });
      candidates.push({ codec: 'avc1.42002A', codecId: 1 });
    } else if (codecId === 4) {
      candidates.push({ codec: 'av01.0.08M.10', codecId: 4 });
      candidates.push({ codec: 'av1', codecId: 4 });
      candidates.push({ codec: 'avc1.42002A', codecId: 1 });
    } else {
      candidates.push({ codec: 'avc1.42002A', codecId: 1 });
      candidates.push({ codec: 'avc1.64002A', codecId: 1 });
      candidates.push({ codec: 'avc1.4D002A', codecId: 1 });
      candidates.push({ codec: 'vp09.00.10.08', codecId: 3 });
    }

    for (const item of candidates) {
      const isAvc = item.codec.startsWith('avc');
      for (const hw of ['prefer-hardware', 'no-preference']) {
        const cfg = {
          codec: item.codec,
          width,
          height,
          bitrate: targetBitrate,
          framerate: targetFps,
          hardwareAcceleration: hw,
          latencyMode: 'realtime'
        };
        if (isAvc) {
          cfg.avc = { format: 'annexb' };
        }
        try {
          const support = await VideoEncoder.isConfigSupported(cfg);
          if (support && support.supported) {
            return {
              config: support.config || cfg,
              effectiveCodecId: item.codecId,
              codecString: item.codec
            };
          }
        } catch { }

        if (isAvc) {
          const cfgNoAvc = Object.assign({}, cfg);
          delete cfgNoAvc.avc;
          try {
            const support = await VideoEncoder.isConfigSupported(cfgNoAvc);
            if (support && support.supported) {
              return {
                config: support.config || cfgNoAvc,
                effectiveCodecId: item.codecId,
                codecString: item.codec
              };
            }
          } catch { }
        }
      }
    }
    return null;
  }

  async function startWebCodecsExtraction(stream, width, height, codecId, fps) {
    stopCanvasFrameExtraction();
    screenCaptureActive = true;

    const targetW = width || 1920;
    const targetH = height || 1080;
    const targetFps = Math.max(15, Math.min(144, fps || screenShareConfig.fps || 60));
    const targetBitrate = screenShareConfig.bitrate || 12000000;

    const encoderProbe = await probeVideoEncoderConfig(codecId, targetW, targetH, targetFps, targetBitrate);
    if (!encoderProbe || !screenCaptureActive) {
      console.warn('Hardware WebCodecs encoder not available, falling back to JPEG extraction.');
      return startCanvasJpegExtraction(stream, targetW, targetH, targetFps);
    }

    const { config: encoderConfig, effectiveCodecId, codecString } = encoderProbe;

    if (effectiveCodecId !== codecId) {
      QP.api('/api/voice/screen-share/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ width: targetW, height: targetH, codecId: effectiveCodecId, fps: targetFps })
      }).catch(() => {});
    }

    const localEntry = activeScreenStreams.get('local');
    if (localEntry) {
      localEntry.stats = {
        framesCount: 0,
        bytesCount: 0,
        lastTick: performance.now(),
        fps: 0,
        bitrateKbps: 0,
        realWidth: targetW,
        realHeight: targetH,
        codecName: getCodecNameFromId(effectiveCodecId)
      };
    }

    let lastEncoderKeyFrameTime = 0;
    const keyFrameIntervalMs = 1500; // I-frame every 1.5s for instant receiver synchronization

    try {
      screenVideoEncoder = new VideoEncoder({
        output: (chunk, metadata) => {
          if (!screenCaptureActive) return;

          let extraBytes = null;
          if (metadata && metadata.decoderConfig) {
            const extraObj = {};
            if (metadata.decoderConfig.codec) {
              extraObj.c = metadata.decoderConfig.codec;
            } else {
              extraObj.c = codecString;
            }
            if (metadata.decoderConfig.description) {
              const descArr = new Uint8Array(metadata.decoderConfig.description);
              let binary = '';
              for (let i = 0; i < descArr.byteLength; i++) {
                binary += String.fromCharCode(descArr[i]);
              }
              extraObj.d = btoa(binary);
            }
            try {
              extraBytes = new TextEncoder().encode(JSON.stringify(extraObj));
            } catch { }
          }

          const isKey = chunk.type === 'key';
          const hasExtra = extraBytes && extraBytes.length > 0;
          const flags = (isKey ? 0x01 : 0x00) | (hasExtra ? 0x02 : 0x00);

          const extraLen = hasExtra ? extraBytes.length : 0;
          const headerLen = hasExtra ? 3 : 1;
          const packetLen = 18 + headerLen + extraLen + chunk.byteLength;

          const packet = new Uint8Array(packetLen);
          packet[0] = 0x02; // Tag 0x02: screen video stream
          // bytes 1-16: TargetPeerId (Guid.Empty = broadcast)
          packet[17] = effectiveCodecId;
          packet[18] = flags;

          let writeOffset = 19;
          if (hasExtra) {
            packet[19] = (extraLen >> 8) & 0xFF;
            packet[20] = extraLen & 0xFF;
            packet.set(extraBytes, 21);
            writeOffset = 21 + extraLen;
          }

          chunk.copyTo(packet.subarray(writeOffset));
          QP.sendBinary(packet.buffer);

          // Update broadcaster real-time telemetry
          const entry = activeScreenStreams.get('local');
          if (entry) {
            if (!entry.stats) {
              entry.stats = {
                framesCount: 0,
                bytesCount: 0,
                lastTick: performance.now(),
                fps: 0,
                bitrateKbps: 0,
                realWidth: targetW,
                realHeight: targetH,
                codecName: getCodecNameFromId(effectiveCodecId)
              };
            }
            entry.stats.framesCount++;
            entry.stats.bytesCount += packet.byteLength;
            entry.stats.realWidth = targetW;
            entry.stats.realHeight = targetH;

            const tNow = performance.now();
            const elapsed = (tNow - entry.stats.lastTick) / 1000;
            if (elapsed >= 1.0) {
              entry.stats.fps = Math.round((entry.stats.framesCount / elapsed) * 10) / 10;
              entry.stats.bitrateKbps = Math.round((entry.stats.bytesCount * 8) / (elapsed * 1000));
              entry.stats.framesCount = 0;
              entry.stats.bytesCount = 0;
              entry.stats.lastTick = tNow;

              const brStr = entry.stats.bitrateKbps >= 1000
                ? (entry.stats.bitrateKbps / 1000).toFixed(1) + ' Mbps'
                : entry.stats.bitrateKbps + ' kbps';

              if (entry.codecBadge) {
                entry.codecBadge.textContent = `Broadcasting · ${entry.stats.codecName || getCodecNameFromId(effectiveCodecId)} · ${entry.stats.realWidth}x${entry.stats.realHeight} · ${entry.stats.fps} FPS · ${brStr}`;
              }
            }
          }
        },
        error: (err) => {
          console.error('VideoEncoder error:', err);
        }
      });

      screenVideoEncoder.configure(encoderConfig);
    } catch (err) {
      console.warn('VideoEncoder configuration failed, falling back to JPEG:', err);
      return startCanvasJpegExtraction(stream, targetW, targetH, targetFps);
    }

    const minFrameIntervalMs = (1000 / targetFps) - 1.5;
    const videoTrack = stream.getVideoTracks()[0];

    // Pipeline A: MediaStreamTrackProcessor (Zero-copy hardware frame access)
    if (typeof MediaStreamTrackProcessor !== 'undefined' && videoTrack) {
      try {
        const processor = new MediaStreamTrackProcessor({ track: videoTrack });
        const reader = processor.readable.getReader();
        screenVideoReader = reader;

        let lastSendTime = 0;
        (async () => {
          while (screenCaptureActive) {
            try {
              const { done, value: frame } = await reader.read();
              if (done || !screenCaptureActive) {
                if (frame) frame.close();
                break;
              }

              const now = performance.now();
              if (now - lastSendTime < minFrameIntervalMs) {
                frame.close();
                continue;
              }

              if (screenVideoEncoder && screenVideoEncoder.state === 'configured') {
                if (screenVideoEncoder.encodeQueueSize > 2) {
                  // Maintain < 5ms latency by dropping frames if GPU encoder queue is busy
                  frame.close();
                  continue;
                }

                const forceKey = (now - lastEncoderKeyFrameTime >= keyFrameIntervalMs);
                if (forceKey) lastEncoderKeyFrameTime = now;

                screenVideoEncoder.encode(frame, { keyFrame: forceKey });
                lastSendTime = now;
              }
              frame.close();
            } catch {
              break;
            }
          }
        })();
        return;
      } catch (err) {
        console.warn('MediaStreamTrackProcessor failed, using video callback:', err);
      }
    }

    // Pipeline B: Video element + requestVideoFrameCallback
    const video = document.createElement('video');
    screenCaptureVideo = video;
    video.srcObject = stream;
    video.muted = true;
    video.playsInline = true;
    video.play().catch(() => {});

    let lastVideoSendTime = 0;
    const captureFrame = (now) => {
      if (!screenCaptureActive || !screenStream) return;

      const tNow = now || performance.now();
      if (tNow - lastVideoSendTime >= minFrameIntervalMs) {
        if (video.videoWidth > 0 && video.videoHeight > 0 && screenVideoEncoder && screenVideoEncoder.state === 'configured') {
          if (screenVideoEncoder.encodeQueueSize <= 2) {
            try {
              const frame = new VideoFrame(video, { timestamp: Math.round(tNow * 1000) });
              const forceKey = (tNow - lastEncoderKeyFrameTime >= keyFrameIntervalMs);
              if (forceKey) lastEncoderKeyFrameTime = tNow;

              screenVideoEncoder.encode(frame, { keyFrame: forceKey });
              frame.close();
              lastVideoSendTime = tNow;
            } catch (err) {
              console.warn('VideoFrame capture error:', err);
            }
          }
        }
      }
      scheduleNext();
    };

    function scheduleNext() {
      if (!screenCaptureActive) return;
      if ('requestVideoFrameCallback' in video) {
        video.requestVideoFrameCallback(captureFrame);
      } else {
        screenCaptureAnimId = requestAnimationFrame(captureFrame);
      }
    }

    scheduleNext();
  }

  function startCanvasJpegExtraction(stream, width, height, fps) {
    stopCanvasFrameExtraction();
    screenCaptureActive = true;

    const video = document.createElement('video');
    screenCaptureVideo = video;
    video.srcObject = stream;
    video.muted = true;
    video.playsInline = true;
    video.play().catch(() => {});

    const targetW = width || 1920;
    const targetH = height || 1080;

    let canvas = null;
    let ctx = null;
    if (typeof OffscreenCanvas !== 'undefined') {
      canvas = new OffscreenCanvas(targetW, targetH);
      ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    } else {
      if (!screenOffscreenCanvas) screenOffscreenCanvas = document.createElement('canvas');
      canvas = screenOffscreenCanvas;
      canvas.width = targetW;
      canvas.height = targetH;
      ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    }

    const targetFps = Math.max(15, Math.min(144, fps || screenShareConfig.fps || 60));
    const minFrameIntervalMs = (1000 / targetFps) - 1.5;
    const jpegQuality = targetFps >= 60 ? 0.70 : 0.78;

    let inFlightEncodes = 0;
    const maxInFlight = 2;
    let lastSendTimestamp = 0;

    const localEntry = activeScreenStreams.get('local');
    if (localEntry) {
      localEntry.stats = {
        framesCount: 0,
        bytesCount: 0,
        lastTick: performance.now(),
        fps: 0,
        bitrateKbps: 0,
        realWidth: targetW,
        realHeight: targetH,
        codecName: 'JPEG'
      };
    }

    const captureFrame = async (timestamp) => {
      if (!screenCaptureActive || !screenStream) return;

      const now = timestamp || performance.now();
      if (now - lastSendTimestamp < minFrameIntervalMs) {
        scheduleNext();
        return;
      }

      if (!video.videoWidth || !video.videoHeight) {
        scheduleNext();
        return;
      }

      if (inFlightEncodes >= maxInFlight) {
        scheduleNext();
        return;
      }

      const curW = Math.min(targetW, video.videoWidth);
      const curH = Math.min(targetH, video.videoHeight);

      if (canvas.width !== curW || canvas.height !== curH) {
        canvas.width = curW;
        canvas.height = curH;
      }

      ctx.drawImage(video, 0, 0, curW, curH);
      lastSendTimestamp = now;
      inFlightEncodes++;

      try {
        let blob = null;
        if (canvas.convertToBlob) {
          blob = await canvas.convertToBlob({ type: 'image/jpeg', quality: jpegQuality });
        } else {
          blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', jpegQuality));
        }

        if (blob && screenCaptureActive && screenStream) {
          const buffer = await blob.arrayBuffer();
          // WebSocket binary Tag 0x02: [Tag 0x02 (1B)][TargetPeerId 16B][CodecId 1B = 0][Video Bytes]
          const targetGuidEmpty = new Uint8Array(16);
          const packet = new Uint8Array(18 + buffer.byteLength);
          packet[0] = 0x02;
          packet.set(targetGuidEmpty, 1);
          packet[17] = 0; // 0 = JPEG
          packet.set(new Uint8Array(buffer), 18);

          QP.sendBinary(packet.buffer);

          const entry = activeScreenStreams.get('local');
          if (entry) {
            if (!entry.stats) {
              entry.stats = {
                framesCount: 0,
                bytesCount: 0,
                lastTick: performance.now(),
                fps: 0,
                bitrateKbps: 0,
                realWidth: curW,
                realHeight: curH,
                codecName: 'JPEG'
              };
            }
            entry.stats.framesCount++;
            entry.stats.bytesCount += packet.byteLength;
            entry.stats.realWidth = curW;
            entry.stats.realHeight = curH;

            const tNow = performance.now();
            const elapsed = (tNow - entry.stats.lastTick) / 1000;
            if (elapsed >= 1.0) {
              entry.stats.fps = Math.round((entry.stats.framesCount / elapsed) * 10) / 10;
              entry.stats.bitrateKbps = Math.round((entry.stats.bytesCount * 8) / (elapsed * 1000));
              entry.stats.framesCount = 0;
              entry.stats.bytesCount = 0;
              entry.stats.lastTick = tNow;

              const brStr = entry.stats.bitrateKbps >= 1000
                ? (entry.stats.bitrateKbps / 1000).toFixed(1) + ' Mbps'
                : entry.stats.bitrateKbps + ' kbps';

              if (entry.codecBadge) {
                entry.codecBadge.textContent = `Broadcasting · JPEG · ${entry.stats.realWidth}x${entry.stats.realHeight} · ${entry.stats.fps} FPS · ${brStr}`;
              }
            }
          }
        }
      } catch (err) {
        console.warn('Screen capture frame encode error:', err);
      } finally {
        inFlightEncodes = Math.max(0, inFlightEncodes - 1);
      }

      scheduleNext();
    };

    function scheduleNext() {
      if (!screenCaptureActive) return;
      if ('requestVideoFrameCallback' in video) {
        video.requestVideoFrameCallback(captureFrame);
      } else {
        screenCaptureAnimId = requestAnimationFrame(captureFrame);
      }
    }

    scheduleNext();
  }

  function startCanvasFrameExtraction(stream, width, height, codecId, fps) {
    if (typeof VideoEncoder !== 'undefined' && codecId !== 0) {
      return startWebCodecsExtraction(stream, width, height, codecId, fps);
    }
    return startCanvasJpegExtraction(stream, width, height, fps);
  }

  function stopCanvasFrameExtraction() {
    screenCaptureActive = false;
    if (screenVideoReader) {
      try { screenVideoReader.cancel(); } catch { }
      screenVideoReader = null;
    }
    if (screenVideoEncoder) {
      try { screenVideoEncoder.close(); } catch { }
      screenVideoEncoder = null;
    }
    if (screenCaptureAnimId) {
      cancelAnimationFrame(screenCaptureAnimId);
      screenCaptureAnimId = null;
    }
    if (screenCaptureInterval) {
      clearInterval(screenCaptureInterval);
      screenCaptureInterval = null;
    }
    if (screenCaptureVideo) {
      try {
        screenCaptureVideo.pause();
        screenCaptureVideo.srcObject = null;
      } catch { }
      screenCaptureVideo = null;
    }
  }

  async function startNativeScreenShare() {
    try {
      const res = await QP.api('/api/voice/screen-share/native-toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enable: true, fps: screenShareConfig.fps || 60, width: 1920, height: 1080 })
      });
      if (res.isNativeRunning) {
        voiceState.isNativeScreenSharing = true;
        renderVoice();
        createOrUpdateScreenTile('local', 'You (Native Desktop Capture)', 1920, 1080, 0, screenShareConfig.fps || 60, true);
        QP.toast('Native desktop screen capture active', 'success');
      }
    } catch (e) {
      QP.toast('Could not start screen capture: ' + e.message, 'error');
    }
  }

  async function stopScreenShare() {
    stopScreenAudioCapture();
    stopCanvasFrameExtraction();

    if (screenStream) {
      screenStream.getTracks().forEach(t => {
        try { t.stop(); } catch { }
      });
      screenStream = null;
    }

    try {
      await QP.api('/api/voice/screen-share/stop', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' }
      });
    } catch { }

    voiceState.isScreenSharing = false;
    voiceState.isNativeScreenSharing = false;
    renderVoice();

    removeScreenTile('local');
    QP.toast('You stopped sharing your screen');
  }

  // --- Dynamic Multi-User Stream Grid Manager ---

  function getOrInitScreenGrid() {
    const section = document.getElementById('screenShareGridSection');
    const grid = document.getElementById('screenShareGrid');
    return { section, grid };
  }

  function updateGridState() {
    const { section, grid } = getOrInitScreenGrid();
    if (!section || !grid) return;

    const count = activeScreenStreams.size;
    if (count === 0) {
      section.classList.add('hidden');
      grid.classList.remove('single-stream', 'theater-mode');
    } else {
      section.classList.remove('hidden');
      if (count === 1) grid.className = 'screen-share-grid grid-1';
      else if (count === 2) grid.className = 'screen-share-grid grid-2';
      else if (count <= 4) grid.className = 'screen-share-grid grid-4';
      else grid.className = 'screen-share-grid grid-many';
    }
  }

  function createOrUpdateScreenTile(peerId, peerName, width, height, codecId, fps, isLocal) {
    const { grid } = getOrInitScreenGrid();
    if (!grid) return null;

    const normId = normalizePeerId(peerId);
    let entry = activeScreenStreams.get(normId);
    const resolvedName = peerName || resolvePeerName(normId);

    if (!entry) {
      const card = QP.el('div', 'screen-tile');
      card.dataset.streamPeerId = normId;
      if (isLocal) card.classList.add('is-local-stream');

      // 1. Header (User Info + Badges + Controls)
      const header = QP.el('div', 'screen-tile-header');
      const userBox = QP.el('div', 'screen-tile-user');

      const avatar = QP.el('div', 'avatar', isLocal ? 'YOU' : QP.initials(resolvedName));
      const metaBox = QP.el('div', 'screen-tile-meta');
      const nameEl = QP.el('span', 'screen-tile-name', resolvedName);
      const codecBadge = QP.el('span', 'screen-tile-codec', isLocal
        ? `Broadcasting · ${width}x${height} · Target ${fps} FPS`
        : `Connecting · ${getCodecNameFromId(codecId)}…`);
      metaBox.append(nameEl, codecBadge);
      userBox.append(avatar, metaBox);

      const actions = QP.el('div', 'screen-tile-actions');
      const liveBadge = QP.el('span', 'badge green', isLocal ? 'Broadcasting' : 'Live');

      let muteBtn = null;
      let volSlider = null;
      let volGroup = null;

      if (!isLocal) {
        volGroup = QP.el('div', 'screen-tile-vol-group');
        muteBtn = document.createElement('button');
        muteBtn.className = 'btn ghost sm icon-only-btn screen-mute-btn';
        muteBtn.textContent = '🔊';
        muteBtn.title = 'Mute stream audio';

        volSlider = document.createElement('input');
        volSlider.type = 'range';
        volSlider.min = '0';
        volSlider.max = '100';
        volSlider.value = '100';
        volSlider.className = 'screen-tile-volume';
        volSlider.title = 'Stream audio volume';

        volGroup.append(muteBtn, volSlider);
      }

      const theaterBtn = document.createElement('button');
      theaterBtn.className = 'btn ghost sm icon-only-btn';
      theaterBtn.textContent = '⛶';
      theaterBtn.title = 'Focus stream (Theater mode)';
      theaterBtn.addEventListener('click', () => toggleTheaterStream(normId));

      const fullscreenBtn = document.createElement('button');
      fullscreenBtn.className = 'btn ghost sm icon-only-btn';
      fullscreenBtn.textContent = '🗖';
      fullscreenBtn.title = 'Fullscreen viewport';
      fullscreenBtn.addEventListener('click', () => toggleTileFullscreen(card));

      if (volGroup) {
        actions.append(liveBadge, volGroup, theaterBtn, fullscreenBtn);
      } else {
        actions.append(liveBadge, theaterBtn, fullscreenBtn);
      }

      if (isLocal) {
        const stopBtn = document.createElement('button');
        stopBtn.className = 'btn sm danger';
        stopBtn.textContent = '⏹️ Stop';
        stopBtn.title = 'Stop screen broadcast';
        stopBtn.addEventListener('click', stopScreenShare);
        actions.append(stopBtn);
      }

      header.append(userBox, actions);

      // 2. Body (Video or Canvas viewport)
      const body = QP.el('div', 'screen-tile-body');
      const videoEl = document.createElement('video');
      videoEl.autoplay = true;
      videoEl.playsInline = true;
      videoEl.muted = true; // Muted for instant autoplay

      // Use a permanent double-buffered canvas instead of an <img> tag to eliminate torn image icon & black flicker
      const canvasEl = document.createElement('canvas');
      canvasEl.className = 'hidden';
      const canvasCtx = canvasEl.getContext('2d', { alpha: false });

      const loadingEl = QP.el('div', 'screen-tile-loading', 'Connecting live video stream…');

      body.append(videoEl, canvasEl, loadingEl);
      card.append(header, body);
      grid.appendChild(card);

      // Setup live video playback
      let mediaSource = null;
      let sourceBuffer = null;
      const bufferQueue = [];
      let isAppending = false;

      if (isLocal && screenStream) {
        videoEl.srcObject = screenStream;
        videoEl.play().catch(() => {});
        videoEl.classList.remove('hidden');
        canvasEl.classList.add('hidden');
        loadingEl.classList.add('hidden');
      } else if (!isLocal && typeof window.MediaSource !== 'undefined') {
        try {
          mediaSource = new MediaSource();
          videoEl.src = URL.createObjectURL(mediaSource);

          mediaSource.addEventListener('sourceopen', () => {
            try {
              let mime = 'video/webm; codecs="vp8"';
              if (codecId === 1 && MediaSource.isTypeSupported('video/webm; codecs="h264"')) {
                mime = 'video/webm; codecs="h264"';
              } else if (codecId === 5 && MediaSource.isTypeSupported('video/mp4; codecs="hvc1"')) {
                mime = 'video/mp4; codecs="hvc1"';
              } else if (codecId === 3 && MediaSource.isTypeSupported('video/webm; codecs="vp9"')) {
                mime = 'video/webm; codecs="vp9"';
              } else if (codecId === 4 && MediaSource.isTypeSupported('video/webm; codecs="av01"')) {
                mime = 'video/webm; codecs="av01"';
              } else if (codecId === 2 && MediaSource.isTypeSupported('video/webm; codecs="vp8"')) {
                mime = 'video/webm; codecs="vp8"';
              } else if (MediaSource.isTypeSupported('video/webm; codecs="h264"')) {
                mime = 'video/webm; codecs="h264"';
              } else if (MediaSource.isTypeSupported('video/webm; codecs="vp8"')) {
                mime = 'video/webm; codecs="vp8"';
              }
              sourceBuffer = mediaSource.addSourceBuffer(mime);
              sourceBuffer.mode = 'sequence';

              sourceBuffer.addEventListener('updateend', () => {
                isAppending = false;
                if (bufferQueue.length > 0 && sourceBuffer && !sourceBuffer.updating) {
                  isAppending = true;
                  const next = bufferQueue.shift();
                  try { sourceBuffer.appendBuffer(next); } catch { isAppending = false; }
                }
              });

              // Flush early chunks that arrived before sourceopen fired
              if (bufferQueue.length > 0 && !sourceBuffer.updating) {
                isAppending = true;
                const next = bufferQueue.shift();
                try { sourceBuffer.appendBuffer(next); } catch { isAppending = false; }
              }
            } catch (err) {
              console.warn('Could not add SourceBuffer:', err);
            }
          });
        } catch (err) {
          console.warn('MediaSource setup failed:', err);
        }
      }

      entry = {
        peerId: normId,
        peerName: resolvedName,
        width,
        height,
        codecId,
        fps,
        mediaSource,
        getSourceBuffer: () => sourceBuffer,
        bufferQueue,
        isAppendingRef: () => isAppending,
        setIsAppending: (val) => { isAppending = val; },
        videoEl,
        canvasEl,
        canvasCtx,
        loadingEl,
        cardEl: card,
        codecBadge,
        nameEl,
        avatarEl: avatar,
        isLocal,
        audioContext: null,
        gainNode: null,
        audioVolume: 1.0,
        audioMuted: false,
        nextPlayTime: 0,
        volSlider,
        muteBtn
      };

      if (muteBtn && volSlider) {
        muteBtn.addEventListener('click', (e) => {
          e.stopPropagation();
          entry.audioMuted = !entry.audioMuted;
          muteBtn.textContent = entry.audioMuted ? '🔇' : '🔊';
          muteBtn.title = entry.audioMuted ? 'Unmute stream audio' : 'Mute stream audio';
          muteBtn.classList.toggle('is-muted', entry.audioMuted);
          if (entry.gainNode) {
            entry.gainNode.gain.value = entry.audioMuted ? 0 : (entry.audioVolume ?? 1.0);
          }
          if (entry.audioContext && entry.audioContext.state === 'suspended') {
            entry.audioContext.resume().catch(() => {});
          }
        });

        volSlider.addEventListener('input', (e) => {
          e.stopPropagation();
          const val = parseFloat(volSlider.value) / 100;
          entry.audioVolume = val;
          if (!entry.audioMuted && entry.gainNode) {
            entry.gainNode.gain.value = val;
          }
          if (val === 0) {
            muteBtn.textContent = '🔇';
          } else if (!entry.audioMuted) {
            muteBtn.textContent = '🔊';
          }
          if (entry.audioContext && entry.audioContext.state === 'suspended') {
            entry.audioContext.resume().catch(() => {});
          }
        });
      }

      card.addEventListener('click', () => {
        if (entry && entry.audioContext && entry.audioContext.state === 'suspended') {
          entry.audioContext.resume().catch(() => {});
        }
      });

      activeScreenStreams.set(normId, entry);
      updateGridState();
    } else {
      // Update metadata
      entry.width = width;
      entry.height = height;
      entry.codecId = codecId;
      entry.fps = fps;
      if (entry.nameEl && resolvedName) {
        entry.peerName = resolvedName;
        entry.nameEl.textContent = resolvedName;
        if (!isLocal && entry.avatarEl) {
          entry.avatarEl.textContent = QP.initials(resolvedName);
        }
      }
      if (entry.codecBadge && (!entry.stats || entry.stats.fps === 0)) {
        entry.codecBadge.textContent = isLocal
          ? `Broadcasting · ${width}x${height} · Target ${fps} FPS`
          : `Connecting · ${getCodecNameFromId(codecId)}…`;
      }
      if (isLocal && screenStream && entry.videoEl && !entry.videoEl.srcObject) {
        entry.videoEl.srcObject = screenStream;
        entry.videoEl.play().catch(() => {});
        entry.videoEl.classList.remove('hidden');
        if (entry.canvasEl) entry.canvasEl.classList.add('hidden');
        if (entry.loadingEl) entry.loadingEl.classList.add('hidden');
      }
    }

    return entry;
  }

  function removeScreenTile(peerId) {
    if (!peerId) return;
    const normId = normalizePeerId(peerId);
    let entry = activeScreenStreams.get(normId);

    if (!entry) {
      for (const [k, v] of activeScreenStreams.entries()) {
        if (k.includes(normId) || normId.includes(k)) {
          entry = v;
          break;
        }
      }
    }

    if (!entry) return;

    if (entry.cardEl && entry.cardEl.parentNode) {
      entry.cardEl.parentNode.removeChild(entry.cardEl);
    }
    if (entry.videoEl) {
      try {
        entry.videoEl.pause();
        entry.videoEl.src = '';
        entry.videoEl.srcObject = null;
      } catch { }
    }
    if (entry.videoDecoder) {
      try { entry.videoDecoder.close(); } catch { }
      entry.videoDecoder = null;
    }
    if (entry.audioContext) {
      try { entry.audioContext.close(); } catch { }
      entry.audioContext = null;
      entry.gainNode = null;
    }
    activeScreenStreams.delete(entry.peerId);
    updateGridState();
  }

  function toggleTheaterStream(peerId) {
    const { grid } = getOrInitScreenGrid();
    if (!grid) return;

    const normId = normalizePeerId(peerId);
    const isTheater = grid.classList.contains('theater-mode');
    if (isTheater) {
      grid.classList.remove('theater-mode');
      grid.querySelectorAll('.screen-tile').forEach(t => t.classList.remove('theater-focused'));
    } else {
      grid.classList.add('theater-mode');
      grid.querySelectorAll('.screen-tile').forEach(t => {
        if (normalizePeerId(t.dataset.streamPeerId) === normId) {
          t.classList.add('theater-focused');
          grid.prepend(t);
        } else {
          t.classList.remove('theater-focused');
        }
      });
    }
  }

  function toggleTileFullscreen(tileElement) {
    const body = tileElement.querySelector('.screen-tile-body');
    if (!body) return;

    if (!document.fullscreenElement) {
      if (body.requestFullscreen) {
        body.requestFullscreen().catch(() => {});
      } else if (body.webkitRequestFullscreen) {
        body.webkitRequestFullscreen();
      }
    } else {
      if (document.exitFullscreen) {
        document.exitFullscreen().catch(() => {});
      }
    }
  }

  // --- WebSocket Stream Chunk Delivery ---

  function playScreenAudioChunk(peerId, pcmBytes) {
    if (!pcmBytes || pcmBytes.byteLength < 2) return;
    const normId = normalizePeerId(peerId);
    let entry = activeScreenStreams.get(normId);

    if (!entry) {
      for (const [k, v] of activeScreenStreams.entries()) {
        if (k.includes(normId) || normId.includes(k)) {
          entry = v;
          break;
        }
      }
    }

    if (!entry || entry.isLocal) return;
    if (entry.audioMuted || entry.audioVolume === 0) return;

    if (!entry.audioContext) {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return;
      entry.audioContext = new AudioCtx({ sampleRate: 48000 });
      entry.gainNode = entry.audioContext.createGain();
      entry.gainNode.gain.value = (typeof entry.audioVolume === 'number') ? entry.audioVolume : 1.0;
      entry.gainNode.connect(entry.audioContext.destination);
      entry.nextPlayTime = 0;
    }

    if (entry.audioContext.state === 'suspended') {
      entry.audioContext.resume().catch(() => {});
    }

    const sampleCount = Math.floor(pcmBytes.byteLength / 2);
    if (sampleCount === 0) return;

    const audioBuf = entry.audioContext.createBuffer(1, sampleCount, 48000);
    const channel = audioBuf.getChannelData(0);
    const dv = new DataView(pcmBytes.buffer, pcmBytes.byteOffset, pcmBytes.byteLength);

    for (let i = 0; i < sampleCount; i++) {
      const s = dv.getInt16(i * 2, true);
      channel[i] = s / 32768.0;
    }

    const source = entry.audioContext.createBufferSource();
    source.buffer = audioBuf;
    source.connect(entry.gainNode);

    const curTime = entry.audioContext.currentTime;
    if (!entry.nextPlayTime || entry.nextPlayTime < curTime) {
      entry.nextPlayTime = curTime + 0.02; // 20ms lead
    } else if (entry.nextPlayTime > curTime + 0.35) {
      // Catch up if drift is excessive (>350ms)
      entry.nextPlayTime = curTime + 0.04;
    }

    source.start(entry.nextPlayTime);
    entry.nextPlayTime += audioBuf.duration;
  }

  function onWsBinaryReceived(arrayBuffer) {
    if (!arrayBuffer || arrayBuffer.byteLength < 17) return;
    const view = new Uint8Array(arrayBuffer);
    const tag = view[0];

    // Screen Audio Chunk (Tag 0x04): [Tag 0x04 (1B)][PeerId 16B][PCM Int16 Bytes]
    if (tag === 0x04) {
      const peerId = parseGuidFromBytes(view, 1);
      const pcmBytes = view.subarray(17);
      playScreenAudioChunk(peerId, pcmBytes);
      return;
    }

    // Video Chunk (Tag 0x02): [Tag 0x02 (1B)][PeerId 16B][CodecId 1B][Video Bytes]
    if (tag === 0x02 && arrayBuffer.byteLength >= 18) {
      const peerId = parseGuidFromBytes(view, 1);
      const codecId = view[17];
      const videoChunk = view.subarray(18);

      let entry = activeScreenStreams.get(peerId);
      if (!entry) {
        const peerName = resolvePeerName(peerId);
        entry = createOrUpdateScreenTile(peerId, peerName, 1920, 1080, codecId, 30, false);
      }
      if (!entry) return;

      // Real-time telemetry tracking for actual received video
      if (!entry.stats) {
        entry.stats = {
          frameCount: 0,
          byteCount: 0,
          lastTick: performance.now(),
          fps: 0,
          bitrateKbps: 0,
          realWidth: 0,
          realHeight: 0,
          codecName: getCodecNameFromId(codecId)
        };
      }
      entry.stats.frameCount++;
      entry.stats.byteCount += videoChunk.byteLength;

      const now = performance.now();
      const elapsed = (now - entry.stats.lastTick) / 1000;
      if (elapsed >= 1.0) {
        entry.stats.fps = Math.round((entry.stats.frameCount / elapsed) * 10) / 10;
        entry.stats.bitrateKbps = Math.round((entry.stats.byteCount * 8) / (elapsed * 1000));
        entry.stats.frameCount = 0;
        entry.stats.byteCount = 0;
        entry.stats.lastTick = now;

        const brStr = entry.stats.bitrateKbps >= 1000
          ? (entry.stats.bitrateKbps / 1000).toFixed(1) + ' Mbps'
          : entry.stats.bitrateKbps + ' kbps';

        const codecName = entry.stats.codecName || getCodecNameFromId(codecId);
        const resW = entry.stats.realWidth || entry.width || 1280;
        const resH = entry.stats.realHeight || entry.height || 720;

        if (entry.codecBadge) {
          entry.codecBadge.textContent = `${codecName} · ${resW}x${resH} · ${entry.stats.fps} FPS · ${brStr}`;
          entry.codecBadge.title = `Actual live incoming telemetry: ${entry.stats.fps} FPS, ${resW}x${resH}, ${brStr}`;
        }
      }

      if (entry.loadingEl && !entry.loadingEl.classList.contains('hidden')) {
        entry.loadingEl.classList.add('hidden');
      }

      // 1. JPEG Check: codecId === 0 or payload starting with JPEG SOI (0xFF 0xD8)
      if (codecId === 0 || (videoChunk.length > 3 && videoChunk[0] === 0xFF && videoChunk[1] === 0xD8)) {
        handleJpegVideoChunk(entry, videoChunk);
        return;
      }

      // 2. Hardware-accelerated GPU WebCodecs decoding pipeline
      if (typeof VideoDecoder !== 'undefined') {
        handleWebCodecsVideoChunk(entry, codecId, videoChunk);
        return;
      }

      // 3. Fallback: Feed continuous video chunk to MediaSource SourceBuffer if supported
      const sb = entry.getSourceBuffer ? entry.getSourceBuffer() : entry.sourceBuffer;
      if (sb) {
        try {
          if (entry.stats && entry.videoEl && entry.videoEl.videoWidth > 0) {
            entry.stats.realWidth = entry.videoEl.videoWidth;
            entry.stats.realHeight = entry.videoEl.videoHeight;
          }
          if (!sb.updating && entry.bufferQueue.length === 0) {
            entry.setIsAppending(true);
            sb.appendBuffer(videoChunk);
          } else {
            entry.bufferQueue.push(videoChunk);
            if (entry.bufferQueue.length > 60) {
              entry.bufferQueue.shift();
            }
          }
          if (entry.videoEl) {
            entry.videoEl.classList.remove('hidden');
            if (entry.videoEl.paused) entry.videoEl.play().catch(() => {});
          }
          if (entry.canvasEl) entry.canvasEl.classList.add('hidden');
        } catch { }
      } else {
        entry.bufferQueue.push(videoChunk);
        if (entry.bufferQueue.length > 60) {
          entry.bufferQueue.shift();
        }
      }
    }
  }

  function handleJpegVideoChunk(entry, videoChunk) {
    if (!entry) return;
    let jpegBytes = videoChunk;
    if (videoChunk.length > 2 && videoChunk[0] !== 0xFF && videoChunk[1] === 0xFF && videoChunk[2] === 0xD8) {
      jpegBytes = videoChunk.subarray(1);
    }
    const blob = new Blob([jpegBytes], { type: 'image/jpeg' });
    if (typeof createImageBitmap !== 'undefined') {
      createImageBitmap(blob).then(bmp => {
        if (!entry || !entry.canvasEl || !entry.canvasCtx) {
          bmp.close();
          return;
        }
        if (entry.stats) {
          entry.stats.realWidth = bmp.width;
          entry.stats.realHeight = bmp.height;
          entry.stats.codecName = 'JPEG';
        }
        if (entry.canvasEl.width !== bmp.width || entry.canvasEl.height !== bmp.height) {
          entry.canvasEl.width = bmp.width;
          entry.canvasEl.height = bmp.height;
        }
        entry.canvasCtx.drawImage(bmp, 0, 0);
        bmp.close();

        entry.canvasEl.classList.remove('hidden');
        if (entry.videoEl && !entry.videoEl.srcObject) {
          entry.videoEl.classList.add('hidden');
        }
      }).catch(() => {});
    }
  }

  function getDefaultCodecString(codecId) {
    switch (codecId) {
      case 1: return 'avc1.42002A';
      case 3: return 'vp09.00.10.08';
      case 4: return 'av01.0.08M.10';
      case 5: return 'hev1.1.6.L93.B0';
      default: return 'avc1.42002A';
    }
  }

  function handleWebCodecsVideoChunk(entry, codecId, videoChunk) {
    if (!entry || videoChunk.length < 2) return;

    const flags = videoChunk[0];
    const isKey = (flags & 0x01) !== 0;
    const hasExtra = (flags & 0x02) !== 0;

    let readOffset = 1;
    let extraObj = null;

    if (hasExtra) {
      if (videoChunk.length < 3) return;
      const extraLen = (videoChunk[1] << 8) | videoChunk[2];
      if (videoChunk.length < 3 + extraLen) return;
      try {
        const jsonStr = new TextDecoder().decode(videoChunk.subarray(3, 3 + extraLen));
        extraObj = JSON.parse(jsonStr);
      } catch { }
      readOffset = 3 + extraLen;
    }

    const rawChunkData = videoChunk.subarray(readOffset);
    if (rawChunkData.length === 0) return;

    const targetCodecStr = (extraObj && extraObj.c) ? extraObj.c : getDefaultCodecString(codecId);

    // Initialize or reconfigure VideoDecoder if necessary
    if (!entry.videoDecoder || entry.videoDecoder.state === 'closed' || entry.currentDecoderCodecId !== codecId || (hasExtra && !entry.hasDecoderExtraConfigured)) {
      if (entry.videoDecoder) {
        try { entry.videoDecoder.close(); } catch { }
        entry.videoDecoder = null;
      }
      entry.hasReceivedKeyFrame = false;
      entry.hasDecoderExtraConfigured = false;

      const decoderConfig = {
        codec: targetCodecStr,
        hardwareAcceleration: 'prefer-hardware',
        optimizeForLatency: true
      };

      if (extraObj && extraObj.d) {
        try {
          const binaryStr = atob(extraObj.d);
          const descBuf = new Uint8Array(binaryStr.length);
          for (let i = 0; i < binaryStr.length; i++) {
            descBuf[i] = binaryStr.charCodeAt(i);
          }
          decoderConfig.description = descBuf.buffer;
          entry.hasDecoderExtraConfigured = true;
        } catch { }
      }

      try {
        entry.videoDecoder = new VideoDecoder({
          output: (videoFrame) => {
            if (!entry || !entry.canvasEl || !entry.canvasCtx) {
              videoFrame.close();
              return;
            }
            const w = videoFrame.displayWidth || videoFrame.codedWidth;
            const h = videoFrame.displayHeight || videoFrame.codedHeight;

            if (entry.stats) {
              entry.stats.realWidth = w;
              entry.stats.realHeight = h;
              entry.stats.codecName = getCodecNameFromId(codecId);
            }

            if (entry.canvasEl.width !== w || entry.canvasEl.height !== h) {
              entry.canvasEl.width = w;
              entry.canvasEl.height = h;
            }

            entry.canvasCtx.drawImage(videoFrame, 0, 0, w, h);
            videoFrame.close();

            entry.canvasEl.classList.remove('hidden');
            if (entry.videoEl && !entry.videoEl.srcObject && !entry.videoEl.classList.contains('hidden')) {
              entry.videoEl.classList.add('hidden');
            }
          },
          error: (err) => {
            console.warn(`VideoDecoder error for peer ${entry.peerId}:`, err);
            entry.hasReceivedKeyFrame = false;
            if (entry.videoDecoder && entry.videoDecoder.state !== 'closed') {
              try { entry.videoDecoder.reset(); } catch { }
            }
          }
        });

        entry.videoDecoder.configure(decoderConfig);
        entry.currentDecoderCodecId = codecId;
        entry.currentDecoderCodecString = targetCodecStr;
      } catch (err) {
        console.error('Failed to configure VideoDecoder:', err);
        return;
      }
    }

    // WebCodecs requires a keyframe as the first chunk following configure() or reset()
    if (!entry.hasReceivedKeyFrame) {
      if (!isKey) {
        // Drop delta frames until the first keyframe arrives
        return;
      }
      entry.hasReceivedKeyFrame = true;
    }

    try {
      const chunk = new EncodedVideoChunk({
        type: isKey ? 'key' : 'delta',
        timestamp: Math.round(performance.now() * 1000),
        data: rawChunkData
      });

      if (entry.videoDecoder && entry.videoDecoder.state === 'configured') {
        entry.videoDecoder.decode(chunk);
      }
    } catch (err) {
      console.warn('VideoDecoder decode chunk error:', err);
    }
  }

  function onScreenShareStarted(data) {
    if (!data) return;
    const isLocal = data.peerId === 'local' || data.peerId === 'me';
    const normId = normalizePeerId(data.peerId);
    let peerName = data.peerName;
    if (!peerName || peerName === 'Contacto' || peerName === 'Peer') {
      peerName = resolvePeerName(normId);
    }
    createOrUpdateScreenTile(normId, peerName, data.width || 1280, data.height || 720, data.codecId || 1, data.fps || 30, isLocal);
    if (!isLocal) {
      QP.toast(`${peerName} started sharing their screen`, 'info');
    }
  }

  function onScreenShareStopped(data) {
    if (!data) return;
    const normId = normalizePeerId(data.peerId);
    const peerName = resolvePeerName(normId);
    removeScreenTile(normId);
    QP.toast(`${peerName || 'A participant'} stopped screen sharing`);
  }

  function onScreenFrameReceived(data) {
    // Handled primarily via high-efficiency WebSocket binary stream
  }
})();
