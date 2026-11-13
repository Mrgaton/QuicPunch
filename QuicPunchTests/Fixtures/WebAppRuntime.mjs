import {readFileSync} from 'node:fs';
import {Script} from 'node:vm';
import assert from 'node:assert/strict';

export function loadApp(context) {
  for (const file of ['ui-values.js', 'app.js']) {
    const url = new URL('../../QuicNet.Shared/wwwroot/' + file, import.meta.url);
    new Script(readFileSync(url, 'utf8'), {filename: file}).runInContext(context);
  }
  return context.window.QP;
}

// Load the actual QP contract; retain only the IO/DOM doubles supplied by each test.
export function attachApp(context) {
  const doubles = context.QP || {};
  context.window ||= {};
  context.window.addEventListener ||= () => {};
  context.navigator ||= {};
  context.setTimeout ||= setTimeout;
  context.clearTimeout ||= clearTimeout;
  context.location ||= {pathname:'/chat.html',search:''};
  context.document.addEventListener ||= () => {};
  context.document.createElement ||= () => ({});
  const actual = loadApp(context);
  const {peerInConnectionMode, trusted} = actual;
  // Preserve getters such as isWsConnected; spreading the object freezes their values.
  Object.defineProperties(actual, Object.getOwnPropertyDescriptors(doubles));
  context.QP = Object.assign(actual, {peerInConnectionMode, trusted});
  context.window.QP = context.QP;
  return context.QP;
}

export function assertConnectionModes(qp) {
  const wan = {id:'wan',isTrusted:true}, tor = {id:'tor',isTrusted:true,isTor:true};
  assert.equal(qp.peerInConnectionMode(wan, {}), true);
  assert.equal(qp.peerInConnectionMode(tor, {}), false);
  for (const enabled of [false,true]) {
    const status = {peers:[wan,tor],wan:{desiredEnabled:enabled,enabled:!enabled},tor:{desiredEnabled:!enabled,enabled}};
    assert.equal(qp.peerInConnectionMode(wan,status),enabled,'Desired WAN mode takes precedence');
    assert.equal(qp.peerInConnectionMode(tor,status),!enabled,'Desired Tor mode takes precedence');
    assert.deepEqual(Array.from(qp.trusted(status),p=>p.id),[enabled?'wan':'tor']);
  }
}
