(() => {
  // Rates come from counter deltas and elapsed time, not from the number of UI refreshes.
  class VpnTrafficHistory {
    constructor() { this.points = []; this.previous = null; }
    disconnect() { this.previous = null; }
    sample(lan, at = Date.now()) {
      const rx = Number(lan?.totalRxBytes), tx = Number(lan?.totalTxBytes);
      if (![rx, tx, at].every(Number.isFinite) || rx < 0 || tx < 0) return;
      if (at < (this.points.at(-1)?.at ?? at)) { this.points = []; this.previous = null; }
      const previous = this.previous;
      const elapsed = previous ? at - previous.at : 0;
      if (previous && elapsed < 500) return;
      const continuous = previous && elapsed <= 10000 && rx >= previous.rx && tx >= previous.tx && previous.active;
      const point = { at, rx: !lan.active ? 0 : continuous ? (rx - previous.rx) * 1000 / elapsed : null,
        tx: !lan.active ? 0 : continuous ? (tx - previous.tx) * 1000 / elapsed : null };
      this.previous = { at, rx, tx, active: !!lan.active };
      this.points.push(point);
      this.points = this.points.filter(point => point.at >= at - 300000).slice(-601);
    }
    rates(at = Date.now()) {
      const latest = this.points.at(-1);
      return this.previous && latest && at - latest.at <= 5000 ? latest : { rx: null, tx: null };
    }
  }
  window.VpnTrafficHistory = VpnTrafficHistory;
  const history = new VpnTrafficHistory();
  const ranges = [30, 60, 300];
  let range = 60, scheduled = false, connected = true;
  function visible() { return document.body.dataset.page === 'vpn' && !document.hidden; }
  function svgNode(tag, attributes) {
    const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
    for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, value);
    return node;
  }
  function draw() {
    scheduled = false;
    if (!visible()) return;
    const svg = document.getElementById('vpnTrafficChart');
    if (!svg) return;
    const now = Date.now(), start = now - range * 1000;
    const points = history.points.filter(point => point.at >= start);
    const rates = history.rates(now);
    const format = value => value === null || !connected ? '—' : QP.fmtBytes(Math.round(value)) + '/s';
    document.getElementById('vpnRxRate').textContent = format(rates.rx);
    document.getElementById('vpnTxRate').textContent = format(rates.tx);
    const seconds = range === 30 ? '30 segundos' : range === 60 ? '1 minuto' : '5 minutos';
    svg.setAttribute('aria-label', `Tráfico de los últimos ${seconds}. RX recibido: ${format(rates.rx)}. TX enviado: ${format(rates.tx)}.`);
    const width = Math.max(180, svg.clientWidth), height = 100, bottom = 94;
    svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
    svg.replaceChildren();
    for (const y of [24, 59, 94]) svg.appendChild(svgNode('line', { x1: 0, x2: width, y1: y, y2: y, class: 'vpn-traffic-grid' }));
    const peak = Math.max(1, ...points.flatMap(point => [point.rx || 0, point.tx || 0])) * 1.15;
    for (const key of ['rx', 'tx']) {
      let line = '', area = '', segment = [], previous;
      const flush = () => {
        if (!segment.length) return;
        const path = segment.map(([x, y], index) => `${index ? 'L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ');
        line += path + ' ';
        area += `${path} L${segment.at(-1)[0].toFixed(1)},${bottom} L${segment[0][0].toFixed(1)},${bottom} Z `;
        segment = [];
      };
      for (const point of points) {
        if (point[key] === null || (previous && point.at - previous.at > 10000)) flush();
        if (point[key] !== null) segment.push([(point.at - start) / (range * 1000) * width, bottom - point[key] / peak * 88]);
        previous = point;
      }
      flush();
      svg.appendChild(svgNode('path', { d: area.trim(), class: `vpn-traffic-area vpn-traffic-${key}` }));
      svg.appendChild(svgNode('path', { d: line.trim(), class: `vpn-traffic-line vpn-traffic-${key}` }));
    }
  }
  function requestDraw() {
    if (scheduled || !visible()) return;
    scheduled = true; requestAnimationFrame(draw);
  }
  document.addEventListener('DOMContentLoaded', () => {
    const select = document.getElementById('vpnTrafficRange');
    if (!select) return;
    try { const saved = Number(localStorage.getItem('quicnet.vpnTrafficRange')); if (ranges.includes(saved)) range = saved; } catch { }
    select.value = String(range);
    select.addEventListener('change', () => {
      const next = Number(select.value); if (!ranges.includes(next)) return;
      range = next;
      try { localStorage.setItem('quicnet.vpnTrafficRange', String(range)); } catch { }
      requestDraw();
    });
    QP.on('status_updated', status => { if (connected) history.sample(status.lan); requestDraw(); });
    QP.on('ws_status', status => { connected = status.connected; history.disconnect(); requestDraw(); });
    QP.on('view_changed', requestDraw);
    document.addEventListener('visibilitychange', requestDraw);
    if (window.ResizeObserver) new ResizeObserver(requestDraw).observe(document.getElementById('vpnTrafficChart'));
    if (QP.state.status) history.sample(QP.state.status.lan);
    setInterval(requestDraw, 1000);
    requestDraw();
  });
})();
