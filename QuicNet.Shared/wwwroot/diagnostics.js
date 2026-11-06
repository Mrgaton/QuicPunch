(() => {
  if (window.QuicNetDiagnostics) return;
  const original = { error: console.error.bind(console), warn: console.warn.bind(console) };
  const pending = [], seen = new Map();
  let transport = null, timer = null, dropped = 0;
  const clean = value => String(value ?? '').replace(/(https?:\/\/[^\s?]+)\?[^\s)]+/gi, '$1')
    .replace(/\b(csrf|token|password|authorization|secret)(\s*[:=]\s*)[^\s,;&]+/gi, '$1$2[redacted]');
  function route(value) {
    try { const url = new URL(value, location.href); return ['http:', 'https:', 'ws:', 'wss:'].includes(url.protocol) ? url.origin + url.pathname : url.protocol; }
    catch { return '[URL no válida]'; }
  }
  function flush() {
    clearTimeout(timer); timer = null;
    if (!transport) return;
    for (let i = 0; pending.length && i < 10; i++) {
      try { if (!transport(pending[0])) { timer = setTimeout(flush, 1000); return; } pending.shift(); }
      catch { return; } // Reporting must never cause another error/report loop.
    }
    if (pending.length) timer = setTimeout(flush, 500);
    else if (dropped) { const count = dropped; dropped = 0; record('warn', 'Diagnóstico', `${count} entradas excedieron la cola de diagnóstico.`, ''); }
  }
  function record(level, context, message, stack) {
    const entry = { level, context: clean(context).slice(0,128), message: clean(message).slice(0,1536),
      stack: clean(stack).slice(0,3072), path: location.pathname.slice(0,256) };
    const key = `${level}:${entry.context}:${entry.message}`, now = Date.now();
    if (now - (seen.get(key) ?? -Infinity) < 2000) return;
    seen.set(key, now); if (seen.size > 200) seen.delete(seen.keys().next().value);
    if (pending.length >= 100) { pending.shift(); dropped++; }
    pending.push(entry);
    if (!timer) timer = setTimeout(flush, 100);
  }
  function report(error, context = 'Aplicación', level = 'error') {
    const message = error?.message || String(error ?? 'Error desconocido');
    const stack = (error?.stack || new Error(message).stack) + (error?.cause?.stack ? '\nCausa: ' + error.cause.stack : '');
    original[level === 'warn' ? 'warn' : 'error'](`[QuicNet · ${context}]`, error);
    record(level, context, message, stack);
  }
  for (const level of ['error','warn']) console[level] = (...args) => {
    original[level](...args);
    const error = args.find(value => value?.stack);
    record(level, 'console.' + level, args.map(value => value?.message || (typeof value === 'string' ? value : Object.prototype.toString.call(value))).join(' '), error?.stack || new Error().stack);
  };
  window.addEventListener('error', event => {
    if (event.target && event.target !== window) {
      const target=event.target;
      report(target.error?.message || `No se pudo cargar ${target.tagName || 'un recurso'}: ${route(target.currentSrc || target.src || target.href || '')}`, 'Recurso');
    } else report(event.error || new Error(`${event.message} (${route(event.filename)}:${event.lineno}:${event.colno})`), 'JavaScript');
  }, true);
  window.addEventListener('unhandledrejection', event => report(event.reason, 'Promesa no controlada'));
  const fetchOriginal = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const request = args[0], method = args[1]?.method || request?.method || 'GET';
    const context = `${method} ${route(typeof request === 'string' || request instanceof URL ? request : request?.url)}`;
    try {
      const response = await fetchOriginal(...args);
      if (!response.ok) report(new Error(`HTTP ${response.status} ${response.statusText}`), context);
      return response;
    } catch (error) {
      // Explicit cancellation is expected during teardown/source changes; timeouts remain errors.
      if (error?.name !== 'AbortError') report(error, context);
      throw error;
    }
  };
  window.QuicNetDiagnostics = { report, route,
    setTransport(send) { transport = send; if (send) flush(); }
  };
})();
