// Pure UI values: no DOM, network, timers or application state.
(() => {
  const byteUnits = ['B', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB'];
  const validPort = port => Number.isInteger(port) && port >= 1 && port <= 65535;

  function fmtBytes(bytes) {
    const n = Number(bytes);
    if (!Number.isFinite(n) || n <= 0) return '0 B';
    if (n < 1024) return `${n} B`;
    const unit = Math.min(Math.floor(Math.log(n) / Math.log(1024)), byteUnits.length - 1);
    return `${(n / 1024 ** unit).toFixed(1)} ${byteUnits[unit]}`;
  }

  function formatDuration(sec) {
    const s = Math.max(0, Math.floor(sec || 0));
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m ${s % 60}s`;
    return `${Math.floor(m / 60)}h ${m % 60}m`;
  }

  // Display summaries may span gaps. Editable values must use the exact list.
  function formatPorts(ports, mode) {
    const sorted = [...new Set((ports || []).map(Number).filter(validPort))].sort((a, b) => a - b);
    if (!sorted.length) return '—';
    if (sorted.length === 1) return String(sorted[0]);
    const consecutive = sorted.every((port, index) => index === 0 || port === sorted[index - 1] + 1);
    return consecutive || sorted.length > 8 || mode === 'Range'
      ? `${sorted[0]} – ${sorted.at(-1)}` : sorted.join(', ');
  }

  function parsePorts(value) {
    const ports = new Set();
    const parts = String(value ?? '').trim().replace(/\s*([-–])\s*/g, '$1').split(/[\s,;]+/).filter(Boolean);
    for (const part of parts) {
      const match = /^(\d+)(?:[-–](\d+))?$/.exec(part);
      if (!match) throw new Error('Puerto o rango no válido: ' + part + '. Usa números enteros del 1 al 65535.');
      const start = Number(match[1]), end = Number(match[2] ?? match[1]);
      if (!validPort(start) || !validPort(end) || end < start)
        throw new Error('Puerto o rango no válido: ' + part + '. Usa números del 1 al 65535, en orden ascendente.');
      for (let port = start; port <= end; port++) ports.add(port);
    }
    if (!ports.size) throw new Error('Introduce al menos un puerto válido, del 1 al 65535.');
    return [...ports].sort((a, b) => a - b);
  }

  globalThis.QuicNetUiValues = Object.freeze({fmtBytes, formatDuration, formatPorts, parsePorts});
})();
