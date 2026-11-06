import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';

const source = readFileSync(new URL('../../QuicNet.Shared/wwwroot/speedtest.js', import.meta.url), 'utf8');
const quality = { method: 'QUIC DATAGRAM', sent: 10, received: 10, sendFailures: 0,
  lossPercent: 0, rttMinMs: 0, rttAverageMs: 2, rttMaxMs: 5, jitterMs: 0 };
const stages = [
  { mode: 'Download', uploadMbps: 0, downloadMbps: 100, totalBytes: 200, durationSeconds: 10, quality },
  { mode: 'Upload', uploadMbps: 80, downloadMbps: 0, totalBytes: 180, durationSeconds: 10, quality },
  { mode: 'Both', uploadMbps: 30, downloadMbps: 40, totalBytes: 150, durationSeconds: 10, quality }
];
const tick = () => new Promise(resolve => setTimeout(resolve, 0));
function element() {
  const classes = new Set();
  return { textContent: '', children: [], value: '', style: {}, options: [], attributes: {}, disabled: false,
    classList: { toggle(name, on) { on ? classes.add(name) : classes.delete(name); }, contains(name) { return classes.has(name); } },
    listeners: {}, addEventListener(name, fn) { this.listeners[name] = fn; }, focus() { this.focused = true; },
    setAttribute(name, value) { this.attributes[name] = value; }, removeAttribute(name) { delete this.attributes[name]; },
    appendChild(item) { this.children.push(item); this.options = this.children; }, append(...items) { this.children.push(...items); },
    replaceChildren(...items) { this.children = items; this.options = this.children; }
  };
}
async function fixture(status = {}) {
  const elements = new Map(), handlers = new Map(), requests = [], toasts = [];
  const get = id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); };
  let initialize;
  const document = { addEventListener(event, fn) { if (event === 'DOMContentLoaded') initialize = fn; }, createElement: element, getElementById: get };
  const QP = { on(name, fn) { handlers.set(name, fn); }, fmtBytes: n => n + ' B', toast(...args) { toasts.push(args); },
    api: async (url, options) => { requests.push({url, options}); return url === '/api/speedtest/status' ? status : url === '/api/status' ? {peers:[{id:'a',name:'Ana',isTrusted:true,ping:5.9}]} : {success:true}; } };
  get('speedTestMode').value = 'all'; get('speedTestDuration').value = '10';
  new Script(source).runInContext(createContext({ document, QP, window:{QP}, console }));
  initialize(); await tick();
  return { get, QP, requests, toasts, emit: (name, data) => handlers.get(name)(data), text: id => get(id).textContent,
    state: () => get('speedTestResults').attributes['data-state'],
    async start() { get('speedTestPeerSelect').value = 'a'; return get('startSpeedTestBtn').listeners.click(); } };
}
const f = await fixture();
await f.start();
assert.equal(f.state(), 'connecting');
assert.equal(f.get('speedTestMode').disabled, true);
assert.ok(f.text('speedTestEstimate').includes('30 segundos'));
assert.deepEqual(JSON.parse(f.requests.at(-1).options.body), {peerId:'a',duration:10,mode:'all'});
f.emit('speedtest_phase', {peerId:'a',phase:'Download',baseline:quality});
f.emit('speedtest_progress', {peerId:'a',direction:'Download',currentMbps:100,bytesTransferred:100,elapsedSeconds:5,totalSeconds:10,rttMs:0});
assert.equal(f.text('speedTestMetricUpload'), 'Pendiente', 'Unmeasured upload must not become zero');
assert.equal(f.text('speedTestMetricDownload'), '100.0 Mbps');
assert.equal(f.text('speedTestMetricRtt'), '0.00 ms', 'Measured zero RTT is valid');
assert.equal(f.get('speedTestBarFill').style.width, '50%', 'Progress represents elapsed time, independent of speed');
f.emit('speedtest_progress', {peerId:'b',direction:'Download',currentMbps:999});
assert.equal(f.text('speedTestMetricDownload'), '100.0 Mbps');
f.emit('speedtest_phase_completed', {testId:'one',peerId:'a',...stages[0],state:'running'});
assert.equal(f.get('speedTestHistoryBody').children.length, 1);
assert.equal(f.text('speedTestMetricBytes'), '200 B', 'Completed bytes must not be counted twice');
f.emit('speedtest_phase', {peerId:'a',phase:'Upload'});
f.emit('speedtest_started', {peerId:'a',mode:'All'});
assert.equal(f.get('speedTestPhaseResults').children.length, 1, 'Late started events must preserve completed phases');
f.emit('speedtest_phase_completed', {testId:'one',peerId:'a',...stages[1],state:'running'});
f.emit('speedtest_phase', {peerId:'a',phase:'Both'});
f.emit('speedtest_progress', {peerId:'a',direction:'Upload',currentMbps:2,bytesTransferred:20,elapsedSeconds:3,totalSeconds:10});
f.emit('speedtest_progress', {peerId:'a',direction:'Download',currentMbps:400,bytesTransferred:40,elapsedSeconds:3,totalSeconds:10});
assert.equal(f.text('speedTestMetricUpload'), '2.0 Mbps');
assert.equal(f.text('speedTestMetricDownload'), '400.0 Mbps', 'Asymmetric rates must never collapse into an average');
assert.equal(f.text('speedTestMetricBytes'), '440 B', 'Completed phases and both live directions contribute to transfer total');
f.emit('speedtest_completed', {testId:'one',peerId:'a',peerName:'<img src=x onerror=alert(1)>',mode:'All',stages,baseline:quality,totalBytes:530});
assert.equal(f.state(), 'completed');
assert.equal(f.text('speedTestMetricDownload'), '100.0 Mbps', 'Summary uses labelled isolated measurement; simultaneous result remains in details');
assert.equal(f.text('speedTestMetricUpload'), '80.0 Mbps');
assert.equal(f.text('speedTestMetricBytes'), '530 B');
assert.equal(f.get('speedTestPhaseResults').children.length, 3);
assert.equal(f.get('speedTestHistoryBody').children.length, 1, 'History groups phases into one test');
assert.equal(f.get('speedTestHistoryBody').children[0].children[1].textContent, '<img src=x onerror=alert(1)>', 'Names render as safe text');
for (const cell of f.get('speedTestHistoryBody').children[0].children) assert.ok(cell.attributes['data-label'], 'Narrow history needs visible column labels');
assert.ok(f.text('speedTestBaseline').includes('Pérdida: 0.00%'));
assert.ok(f.text('speedTestBaseline').includes('Jitter: 0.00 ms'));
assert.equal(f.get('speedTestProgress').classList.contains('hidden'), true);

const reliable = {...quality,method:'Stream fiable',lossPercent:null};
const partial = {testId:'partial',peerId:'a',peerName:'Ana',mode:'All',state:'interrupted',error:'QUIC_STATUS_INTERNAL_ERROR',
  uploadMbps:0,downloadMbps:0,baseline:reliable,stages:[stages[0]]};
const p = await fixture({history:[partial],lastResult:partial,error:partial.error});
assert.equal(p.state(), 'interrupted');
assert.equal(p.text('speedTestMetricDownload'), '100.0 Mbps', 'Partial summary placeholders cannot overwrite completed measurements');
assert.equal(p.text('speedTestMetricUpload'), 'Sin resultado');
assert.ok(p.text('speedTestStatusNotice').includes('1 de 3'));
assert.equal(p.get('speedTestError').classList.contains('hidden'), false);
assert.ok(!p.text('speedTestErrorMessage').includes('QUIC_'));
assert.equal(p.text('speedTestErrorTechnical'), partial.error);
assert.ok(p.text('speedTestBaseline').includes('Pérdida: No disponible'));
await p.emit('view_changed', {page:'speedtest'});
assert.equal(p.get('speedTestHistoryBody').children.length, 1, 'Reloading must not duplicate tests');
await p.start();
assert.equal(p.get('speedTestError').classList.contains('hidden'), true, 'Retry clears the old diagnostic');
p.emit('speedtest_phase', {peerId:'a',phase:'Download',baseline:quality});
p.emit('speedtest_phase_completed', {testId:'retry',peerId:'a',...stages[0],state:'running'});
p.emit('speedtest_phase', {peerId:'a',phase:'Upload'});
p.emit('speedtest_error', {peerId:'a',error:'Operation aborted.'});
assert.equal(p.state(), 'interrupted');
assert.equal(p.text('speedTestMetricUpload'), 'Sin resultado', 'Incomplete live measurements cannot become final results');
assert.equal(p.get('speedTestPhaseResults').children.length, 1, 'Errors without a result still preserve completed phases');
assert.equal(p.get('speedTestHistoryBody').children.length, 2);

const e = await fixture();
e.QP.api = async () => ({success:false,error:'Internal error: test failure'});
await e.start();
assert.equal(e.state(), 'error');
assert.equal(e.text('speedTestActiveBadge'), 'No iniciada');
assert.equal(e.text('speedTestMetricDownload'), 'Sin resultado');
assert.equal(e.get('startSpeedTestBtn').disabled, false);

const c = await fixture();
let rejectStart;
c.QP.api = url => url.endsWith('/start') ? new Promise((_,reject) => {rejectStart=reject;}) : Promise.resolve({success:true});
const pendingStart = c.start();
await c.get('cancelSpeedTestBtn').listeners.click();
assert.equal(c.state(), 'cancelled');
rejectStart(new Error('Prueba cancelada.')); await pendingStart;
assert.equal(c.state(), 'cancelled', 'A delayed start response must not replace cancellation with an error');
assert.equal(c.text('speedTestMetricDownload'), 'Sin resultado');

const disconnect = await fixture(); await disconnect.start();
disconnect.emit('speedtest_phase', {peerId:'a',phase:'Download'});
disconnect.emit('peer_disconnected', {peerId:'a'});
assert.equal(disconnect.state(), 'interrupted', 'A disconnected peer is not a user cancellation');

const zero = await fixture();
zero.emit('speedtest_completed', {peerId:'a',mode:'Upload',uploadMbps:0,downloadMbps:0,totalBytes:0,durationSeconds:10,quality,baseline:quality});
assert.equal(zero.text('speedTestMetricUpload'), '0.0 Mbps', 'Measured zero remains distinct from absent data');
assert.equal(zero.text('speedTestMetricDownload'), 'No incluida');
await zero.get('clearSpeedTestHistoryBtn').listeners.click();
assert.equal(zero.get('speedTestHistoryBody').children[0].children[0].colSpan, 5);

// A status response requested before live events must not roll the UI back.
const race = await fixture(); let snapshot;
race.QP.api = async url => url.endsWith('/status') ? new Promise(resolve => {snapshot=resolve;}) : {success:true};
race.QP.getStatus = async () => ({peers:[]});
const refresh = race.emit('view_changed', {page:'speedtest'}); await tick();
race.emit('speedtest_phase', {peerId:'a',phase:'Download'});
snapshot({lastResult:partial,history:[partial]}); await refresh;
assert.equal(race.state(), 'running');

const html = readFileSync(new URL('../../QuicNet.Shared/wwwroot/views/speedtest.html', import.meta.url), 'utf8');
for (const mode of ['upload','download','both','all']) assert.ok(html.includes('value="'+mode+'"'));
assert.ok(html.includes('role="status"') && html.includes('role="alert"') && html.includes('role="progressbar"'));
assert.ok(!html.includes('speedTestLiveNumber'), 'There is no ambiguous combined gauge');
console.log('PASS speed test UI: lifecycle, partial results, error recovery, cancellation races, peer disconnect, real zero, temporal progress, grouped safe history and status hydration');
