// Media lifetime is scoped to one call room; UI state is supplied explicitly.
(globalThis.QuicNetMedia ??= {}).createScreenVideoDecoder = function({getCodecCandidates, getCodecNameFromId, getAcceleration}) {
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
        const resized = entry.canvasEl.width !== bmp.width || entry.canvasEl.height !== bmp.height;
        if (resized) {
          entry.canvasEl.width = bmp.width;
          entry.canvasEl.height = bmp.height;
        }
        entry.canvasCtx.drawImage(bmp, 0, 0);
        bmp.close();

        entry.canvasEl.classList.remove('hidden');
        if (entry.videoEl && !entry.videoEl.srcObject) {
          entry.videoEl.classList.add('hidden');
        }
        if (resized) entry.viewer?.refresh();
      }).catch(() => {});
    }
  }

  function getDefaultCodecString(codecId, width, height, fps) {
    const candidates = getCodecCandidates(codecId, width || 1920, height || 1080, fps || 60);
    return candidates[0] || 'avc1.64002A';
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
      } catch { return; }
      if (!extraObj || typeof extraObj !== 'object' || Array.isArray(extraObj)) return;
      readOffset = 3 + extraLen;
    }

    const rawChunkData = videoChunk.subarray(readOffset);
    if (rawChunkData.length === 0) return;

    const targetCodecStr = (extraObj && extraObj.c)
      ? extraObj.c
      : entry.currentDecoderCodecId === codecId && entry.currentDecoderCodecString
        || getDefaultCodecString(codecId, entry.width, entry.height, entry.fps);

    if (extraObj?.g != null && entry.decoderGeneration != null && extraObj.g < entry.decoderGeneration) return;
    const sameGeneration = (extraObj?.g == null || extraObj.g === entry.decoderGeneration) && entry.currentDecoderCodecId === codecId;
    const description = (extraObj && Object.hasOwn(extraObj, 'd') ? extraObj.d : sameGeneration ? entry.decoderDescription : '') || '';
    const generation = extraObj?.g ?? entry.decoderGeneration;
    const width = extraObj?.w || entry.width;
    const height = extraObj?.h || entry.height;
    const signature = JSON.stringify([targetCodecStr, description, generation, width, height, getAcceleration()]);
    // Initialize or reconfigure VideoDecoder if necessary
    if (!entry.videoDecoder || entry.videoDecoder.state === 'closed' || entry.currentDecoderCodecId !== codecId || signature !== entry.decoderSignature) {
      if (!isKey) return;
      if (entry.videoDecoder) {
        try { entry.videoDecoder.close(); } catch { }
        entry.videoDecoder = null;
      }
      entry.hasReceivedKeyFrame = false;
      entry.hasDecoderExtraConfigured = false;

      entry.decoderSignature = signature;
      entry.decoderGeneration = generation;
      entry.decoderDescription = description || null;
      entry.dropVideoUntilKeyFrame = false;
      const decoderConfig = {
        codec: targetCodecStr,
        hardwareAcceleration: getAcceleration(),
        optimizeForLatency: true
      };

      if (width > 0 && height > 0) { decoderConfig.codedWidth = width; decoderConfig.codedHeight = height; }
      if (description) {
        try {
          const binaryStr = atob(description);
          const descBuf = new Uint8Array(binaryStr.length);
          for (let i = 0; i < binaryStr.length; i++) {
            descBuf[i] = binaryStr.charCodeAt(i);
          }
          decoderConfig.description = descBuf.buffer;
          entry.hasDecoderExtraConfigured = true;
        } catch { entry.decoderSignature = null; return; }
      }

      try {
        const decoder = new VideoDecoder({
          output: (videoFrame) => {
            if (entry.videoDecoder !== decoder || !entry.canvasEl || !entry.canvasCtx) {
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

            const resized = entry.canvasEl.width !== w || entry.canvasEl.height !== h;
            if (resized) {
              entry.canvasEl.width = w;
              entry.canvasEl.height = h;
            }

            try { entry.canvasCtx.drawImage(videoFrame, 0, 0, w, h); }
            finally { videoFrame.close(); }

            entry.canvasEl.classList.remove('hidden');
            if (entry.videoEl && !entry.videoEl.srcObject && !entry.videoEl.classList.contains('hidden')) {
              entry.videoEl.classList.add('hidden');
            }
            if (resized) entry.viewer?.refresh();
          },
          error: (err) => {
            if (entry.videoDecoder !== decoder) return;
            console.warn(`VideoDecoder error for peer ${entry.peerId}:`, err);
            entry.hasReceivedKeyFrame = false;
            entry.decoderSignature = null;
            if (entry.videoDecoder && entry.videoDecoder.state !== 'closed') {
              try { entry.videoDecoder.reset(); } catch { }
            }
          }
        });

        entry.videoDecoder = decoder;
        entry.decoderConfig = decoderConfig;
        entry.videoDecoder.configure(decoderConfig);
        entry.currentDecoderCodecId = codecId;
        entry.currentDecoderCodecString = targetCodecStr;
        entry.width = width; entry.height = height;
      } catch (err) {
        try { entry.videoDecoder?.close(); } catch { }
        entry.videoDecoder = null;
        entry.decoderSignature = null;
        console.error('Failed to configure VideoDecoder:', err);
        return;
      }
    }

    // Keep decoding near real time. If rendering falls behind, discard dependent
    // frames until the next keyframe instead of playing an ever-growing backlog.
    if (entry.videoDecoder && entry.videoDecoder.decodeQueueSize > 2) {
      entry.dropVideoUntilKeyFrame = true;
    }
    if (entry.dropVideoUntilKeyFrame) {
      if (!isKey) return;
      try { entry.videoDecoder.reset(); entry.videoDecoder.configure(entry.decoderConfig); } catch { entry.decoderSignature = null; return; }
      entry.hasReceivedKeyFrame = false;
      entry.dropVideoUntilKeyFrame = false;
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
        timestamp: Number.isFinite(extraObj?.t) ? extraObj.t : Math.round(performance.now() * 1000),
        data: rawChunkData
      });

      if (entry.videoDecoder && entry.videoDecoder.state === 'configured') {
        entry.videoDecoder.decode(chunk);
      }
    } catch (err) {
      entry.hasReceivedKeyFrame = false;
      entry.dropVideoUntilKeyFrame = true;
      console.warn('VideoDecoder decode chunk error:', err);
    }
  }

  return {jpeg: handleJpegVideoChunk, video: handleWebCodecsVideoChunk};
};
