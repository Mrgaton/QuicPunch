(() => {
  const windowMs = 10 * 60 * 1000, maxPoints = 600;
  const number = value => Number.isFinite(Number(value)) && value !== null && value !== '' ? Math.max(0, Number(value)) : null;
  function identity(item) {
    const t = item.telemetry || {};
    return JSON.stringify([item.peerId, item.transportType,
      t.nativeLocalEndpoint || '', t.nativeRemoteEndpoint || item.protocolId || item.protocolName || '', t.handshakeDurationMs ?? '']);
  }
  function unique(items) {
    const connections = new Map();
    for (const item of items) {
      const key = identity(item), previous = connections.get(key);
      if (!previous || number(item.telemetry?.sendTotalBytes) >= number(previous.telemetry?.sendTotalBytes)) connections.set(key, item);
    }
    return [...connections.values()];
  }
  class History {
    constructor() { this.points = []; this.previous = new Map(); }
    sample(items, at = Date.now()) {
      const last = this.points.at(-1);
      if (last && at < last.at) { this.points = []; this.previous.clear(); }
      else if (last && at - last.at < 1000) return null;
      items = unique(items);
      const point = {at, connections:items.length, rtt:null, loss:null, cwnd:null, tx:null, rx:null};
      let rtt = 0, rtts = 0, cwnd = 0, cwnds = 0, tx = 0, rx = 0, rates = 0, lost = 0, sent = 0;
      const next = new Map();
      for (const item of items) {
        const t = item.telemetry || {}, key = identity(item);
        const r = number(t.rttMs), c = number(t.sendCongestionWindow);
        if (r !== null) { rtt += r; rtts++; } if (c !== null) { cwnd += c; cwnds++; }
        const current = {at, tx:number(t.sendTotalBytes), rx:number(t.recvTotalBytes), sent:number(t.sendTotalPackets), lost:number(t.sendSuspectedLostPackets)};
        const previous = this.previous.get(key), seconds = previous ? (at - previous.at) / 1000 : 0;
        // New/reset connections establish a baseline; never graph their lifetime total as a burst.
        if (previous && seconds > 0 && seconds <= 15 && [current.tx, current.rx, previous.tx, previous.rx].every(v => v !== null) && current.tx >= previous.tx && current.rx >= previous.rx) {
          tx += (current.tx - previous.tx) / seconds; rx += (current.rx - previous.rx) / seconds; rates++;
          if ([current.sent,current.lost,previous.sent,previous.lost].every(v => v !== null) && current.sent >= previous.sent && current.lost >= previous.lost) {
            sent += current.sent - previous.sent; lost += current.lost - previous.lost;
          }
        }
        next.set(key, current);
      }
      point.rtt = rtts ? rtt / rtts : null; point.cwnd = cwnds ? cwnd : null;
      point.loss = sent > 0 ? Math.min(100, lost * 100 / sent) : null;
      if (rates || !items.length) { point.tx = tx; point.rx = rx; }
      this.previous = next;
      this.points.push(point);
      while (this.points.length > maxPoints || this.points[0]?.at < at - windowMs) this.points.shift();
      return point;
    }
  }
  const definitions = {
    connections:{title:'Conexiones activas',keys:['connections'],format:v => String(Math.round(v))},
    rtt:{title:'Latencia RTT media',keys:['rtt'],format:v => v.toFixed(1) + ' ms'},
    loss:{title:'Pérdida por intervalo',keys:['loss'],format:v => v.toFixed(2) + ' %'},
    cwnd:{title:'Ventana de congestión',keys:['cwnd'],format:v => bytes(v)},
    traffic:{title:'Tráfico de red',keys:['tx','rx'],format:v => bytes(v) + '/s'}
  };
  function bytes(value) {
    const units = ['B','KB','MB','GB']; let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return value.toFixed(unit ? 1 : 0) + ' ' + units[unit];
  }
  const history = new History();
  let range = 5 * 60 * 1000, scheduled = false, host, charts = [];
  const visible = () => host && !document.hidden && document.body.dataset.page === 'settings' &&
    document.getElementById('networkDiagnostics').open && !host.classList.contains('hidden') && host.getClientRects().length;
  const svgNode = (name, attrs = {}, text) => {
    const element = document.createElementNS('http://www.w3.org/2000/svg', name);
    for (const [key,value] of Object.entries(attrs)) element.setAttribute(key, value);
    if (text !== undefined) element.textContent = text;
    return element;
  };
  function inspect(chart, index) {
    const point = chart.points[index]; if (!point) return;
    const time = new Date(point.at).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'});
    chart.tip.textContent = time + ' · ' + chart.definition.keys.map(key => (key === 'tx' ? '↑ ' : key === 'rx' ? '↓ ' : '') + (point[key] === null ? 'Sin datos' : chart.definition.format(point[key]))).join(' · ');
    chart.tip.hidden = false;
    const x = chart.x(point.at); chart.cursor.setAttribute('x1',x); chart.cursor.setAttribute('x2',x);
    chart.cursor.removeAttribute('visibility'); chart.index = index;
  }
  function hide(chart) { chart.tip.hidden = true; chart.cursor?.setAttribute('visibility','hidden'); }
  function draw() {
    scheduled = false;
    if (!visible()) return;
    const end = history.points.at(-1)?.at ?? Date.now(), start = end - range;
    const points = history.points.filter(point => point.at >= start);
    for (const chart of charts) {
      const {svg,definition} = chart;
      chart.points = points; chart.index = points.length - 1; hide(chart);
      const width = Math.max(180,svg.clientWidth), height = 132, left = 58, right = width-8, top=12, bottom=104;
      const values = points.flatMap(point => definition.keys.map(key => point[key])).filter(v => v !== null);
      const peak = Math.max(0,...values);
      const max = definition.keys[0] === 'connections' ? Math.max(2,Math.ceil(peak)) : Math.max(1,peak*1.1);
      const y = value => bottom - value / max * (bottom-top);
      chart.x = time => left + (time-start)/range*(right-left);
      svg.setAttribute('viewBox',`0 0 ${width} ${height}`); svg.replaceChildren();
      svg.setAttribute('aria-label',definition.title + '. Historial de los últimos ' + range/60000 + ' minutos. Usa las flechas para consultar muestras.');
      for (const fraction of [0,.5,1]) {
        const lineY = top+(bottom-top)*fraction;
        svg.append(svgNode('line',{x1:left,x2:right,y1:lineY,y2:lineY,class:'net-chart-grid'}));
        svg.append(svgNode('text',{x:left-6,y:lineY+4,'text-anchor':'end',class:'net-chart-axis'},definition.format(max*(1-fraction))));
      }
      svg.append(svgNode('text',{x:left,y:125,class:'net-chart-axis'},'−'+range/60000+' min'),svgNode('text',{x:right,y:125,'text-anchor':'end',class:'net-chart-axis'},'Ahora'));
      definition.keys.forEach((key,series) => {
        let path = '', previous = null;
        for (const point of points) {
          if (point[key] === null) { previous=null; continue; }
          const x = chart.x(point.at), valueY = y(point[key]);
          const continues = previous && point.at - previous.at <= 15000;
          if (continues && key === 'connections') path += ` H${x.toFixed(1)} V${valueY.toFixed(1)}`;
          else path += ` ${continues ? 'L':'M'}${x.toFixed(1)},${valueY.toFixed(1)}`;
          previous = point;
        }
        svg.append(svgNode('path',{d:path.trim(),class:'net-chart-line net-chart-series-'+series}));
      });
      if (points.length < 2 || !values.length) svg.append(svgNode('text',{x:(left+right)/2,y:58,'text-anchor':'middle',class:'net-chart-empty'},'Recogiendo muestras…'));
      chart.cursor = svgNode('line',{y1:top,y2:bottom,class:'net-chart-cursor',visibility:'hidden'}); svg.append(chart.cursor);
    }
    const current = history.points.at(-1);
    if (current) {
      const rate = value => value === null ? '—' : bytes(value)+'/s';
      document.getElementById('kpiQuicThroughput').textContent = '↑ '+rate(current.tx)+' · ↓ '+rate(current.rx);
      document.getElementById('kpiQuicLoss').textContent = current.loss === null ? '—' : current.loss.toFixed(2)+' %';
    }
  }
  function requestDraw() {
    if (scheduled || !visible()) return;
    scheduled=true; requestAnimationFrame(draw);
  }
  window.NetworkTelemetry = {History, unique, update(items, at) {history.sample(items,at); requestDraw();}};
  document.addEventListener('DOMContentLoaded', () => {
    host=document.getElementById('quicTelemetrySection'); if (!host) return;
    charts=[...host.querySelectorAll('[data-net-chart]')].map(svg => {
      const chart={svg,definition:definitions[svg.dataset.netChart],tip:svg.parentElement.querySelector('.net-chart-tip'),points:[],index:0};
      svg.addEventListener('pointermove',event => {
        const box=svg.getBoundingClientRect(), time=(history.points.at(-1)?.at ?? 0)-range+Math.max(0,Math.min(1,(event.clientX-box.left-58)/(box.width-66)))*range;
        const nearest=chart.points.reduce((best,point,index)=>Math.abs(point.at-time)<Math.abs(chart.points[best].at-time)?index:best,0);
        inspect(chart,nearest);
      });
      svg.addEventListener('pointerleave',()=>hide(chart));
      svg.addEventListener('focus',()=>inspect(chart,chart.points.length-1));
      svg.addEventListener('blur',()=>hide(chart));
      svg.addEventListener('keydown',event => {
        if (!['ArrowLeft','ArrowRight','Home','End','Escape'].includes(event.key)) return;
        event.preventDefault();
        if(event.key==='Escape') {hide(chart);return;}
        const index=event.key==='Home'?0:event.key==='End'?chart.points.length-1:chart.index+(event.key==='ArrowLeft'?-1:1);
        inspect(chart,Math.max(0,Math.min(chart.points.length-1,index)));
      });
      return chart;
    });
    document.getElementById('telemetryRange').addEventListener('change',event=>{range=Number(event.target.value)*60000;requestDraw();});
    document.getElementById('networkDiagnostics').addEventListener('toggle',requestDraw);
    QP.on('settings_section_changed',requestDraw);
    QP.on('view_changed',requestDraw);
    new ResizeObserver(requestDraw).observe(host);
    document.addEventListener('visibilitychange',requestDraw);
    requestDraw();
  });
})();
