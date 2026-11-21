import assert from 'node:assert/strict';
import {mkdir} from 'node:fs/promises';
import {launchBrowser, startPreviewServer, closeBrowserFixture, mapStatusMessage} from '../Fixtures/BrowserFixture.mjs';

const server = startPreviewServer(['--interactive']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
  page.setDefaultTimeout(10000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => { window.__qp_is_android = true; });
  // Keep the offline-message fixture intact across HTTP and live status refreshes.
  const offlineMessages = status => { status.peers[1].canSendOffline = true; return status; };
  await page.route('**/api/status', async route => {
    const response = await route.fetch();
    await route.fulfill({json:offlineMessages(await response.json())});
  });
  await page.routeWebSocket(/\/ws(?:\?|$)/, socket => {
    const upstream = socket.connectToServer();
    upstream.onMessage(message => socket.send(mapStatusMessage(message, offlineMessages)));
  });
  const filename = 'Fotografías-del-viaje-'.repeat(8) + '.jpg';
  const share = {id:'phone-share',name:'Documentos',root:'content://documents/' + 'carpeta'.repeat(30),writable:true,allowDelete:true};
  await page.route('**/api/virtual-disk/operation', route => route.fulfill({json:{success:true,
    ...(route.request().postDataJSON().request.operation === 'shares'
      ? {shares:[share],canExplore:false}
      : {entries:[{name:filename,path:filename,directory:false},{name:'Fotos',path:'Fotos',directory:true}]})
  }}));
  await page.goto(url);
  await page.waitForFunction(() => QP.state.status && QP.isWsConnected);
  await page.locator('.contact-card').first().waitFor();
  const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await mkdir('.artifacts/phone-layout', {recursive:true});
  const more = page.getByRole('button', { name:'Más', exact:true });
  const tools = page.getByRole('dialog', { name:'Más', exact:true });
  const screenshotSizes = [320,390,667];

  for (const viewport of [{width:320,height:568},{width:360,height:800},{width:390,height:844},{width:430,height:932},{width:667,height:375}]) {
    await page.setViewportSize(viewport);
    for (const view of ['dashboard','chat','voice','watch','virtual-disk','settings','vpn','speedtest']) {
      await page.evaluate(view => QP.navigate(view), view);
      await settle();
      const nav = await page.locator('.nav-wrap').evaluate(host => {
        const rect = host.getBoundingClientRect();
        return {top:rect.top,bottom:rect.bottom,items:[...host.querySelectorAll('.nav-link')].filter(e => e.getBoundingClientRect().width).map(e => {
          const box = e.getBoundingClientRect(), icon = e.querySelector('svg').getBoundingClientRect(), label = e.querySelector('.nav-label').getBoundingClientRect();
          return {page:e.dataset.page || e.id,tag:e.tagName,width:box.width,height:box.height,iconTop:icon.top,labelTop:label.top,labelBottom:label.bottom,
            fits:label.left>=box.left && label.right<=box.right && label.bottom<=box.bottom,
            reachable:e.contains(document.elementFromPoint(box.x+box.width/2,box.y+box.height/2))};
        })};
      });
      const label = `${view} at ${viewport.width}px`;
      assert.deepEqual(nav.items.map(({page,tag})=>[page,tag]),
        [['dashboard','A'],['chat','A'],['voice','A'],['navMore','BUTTON'],['settings','A']],
        label + ': More appears before Settings at the end of the navigation');
      assert.ok(nav.items.every(item => Math.abs(item.iconTop-nav.items[0].iconTop)<1),label + ': navigation icons are misaligned');
      assert.ok(nav.items.every(item => Math.abs(item.labelTop-nav.items[0].labelTop)<1 && item.fits),label + ': navigation labels are clipped or misaligned');
      assert.ok(nav.items.every(item => item.width>=44 && item.height>=44 && item.reachable),label + ': navigation is too small or obstructed');
      assert.ok(nav.bottom<=viewport.height,label + ': navigation escapes the viewport');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth+1),label + ': horizontal overflow');
      assert.ok(await page.locator('#headerNetworkSwitch button').evaluateAll(buttons => buttons.length === 2 && buttons.every(button => {
        const {x,y,width,height} = button.getBoundingClientRect();
        return width>=44 && height>=44 && y>=0 && button.contains(document.elementFromPoint(x+width/2,y+height/2));
      })),label + ': the network selector must be visible and touch-sized');
      if (view === 'watch' && (viewport.width === 320 || viewport.height === 375)) {
        const frame = page.locator('#watchFrame');
        const prepare = frame.contentFrame().locator('#startWatching');
        await prepare.waitFor();
        const frameBox = await frame.boundingBox(), actionBox = await prepare.boundingBox();
        assert.ok(actionBox.y>=frameBox.y && actionBox.y+actionBox.height<=frameBox.y+frameBox.height+1,
          label + ': preparing a room requires scrolling the opening screen');
      }
      if (view === 'voice' && viewport.height === 375) {
        const actions = page.locator('.voice-dock button:visible');
        assert.deepEqual(await actions.evaluateAll(buttons => buttons.map(button => button.id)), ['voiceScreenSettingsBtn']);
        assert.ok(await actions.evaluateAll((buttons, navTop) => buttons.every(button => {
          const {x,y,width,height,bottom} = button.getBoundingClientRect();
          return width>=44 && height>=44 && y>=0 && bottom<=navTop+1 &&
            button.contains(document.elementFromPoint(x+width/2,y+height/2));
        }),nav.top), 'Landscape call controls are clipped or too small');
        await page.locator('#voiceScreenSettingsBtn').click();
        await page.locator('#screenShareSettingsModal').waitFor();
        await page.locator('#closeScreenModalBtn').click();
        await page.locator('#voiceInviteBtn').click();
        const drawer = await page.locator('#voiceDrawer').boundingBox();
        const dock = await page.locator('.voice-dock').boundingBox();
        assert.ok(drawer.y>=0 && drawer.height>0 && drawer.y+drawer.height<=dock.y+1,
          'The landscape contacts drawer covers the call controls');
        await page.locator('#voiceDrawerClose').click();
      }
      if (view === 'settings' && viewport.width === 390) {
        await page.locator('#settingsCategory').selectOption('notifications');
        const master = await page.locator('.notification-master').boundingBox();
        const contacts = await page.locator('.notice-contacts-card').boundingBox();
        assert.ok(master.y+master.height<=Math.min(contacts.y,nav.top), 'The global switch is buried below contact overrides');
      }
      if (view === 'vpn' && viewport.width === 320) {
        const toggle = await page.locator('#lanEnabled').boundingBox();
        assert.ok(toggle.y+toggle.height<=nav.top, 'Private network activation is below the fold');
      }
      if (view === 'speedtest' && viewport.width === 390) {
        await page.locator('#speedTestPeerSelect').selectOption({index:1});
        const start = await page.locator('#startSpeedTestBtn').boundingBox();
        assert.ok(start.y+start.height<=nav.top, 'Start test is below the fold after choosing a contact');
      }
    }
    await page.evaluate(() => QP.navigate('dashboard'));
    await settle();
    if (screenshotSizes.includes(viewport.width)) {
      const capture = name => page.screenshot({path:`.artifacts/phone-layout/more-${viewport.width}-${name}.png`,animations:'disabled'});
      assert.equal(await page.locator('#openMobileTools').isVisible(),false);
      await capture('home');
      await more.tap();
      await tools.waitFor();
      await capture('panel');
      await tools.getByRole('link',{name:/Disco virtual/}).tap();
      await page.locator('#view-virtual-disk').waitFor();
      await page.locator('#diskShares .disk-name').first().waitFor();
      assert.equal(await more.evaluate(button=>button.classList.contains('active')),true);
      await capture('drive');
      await page.evaluate(() => QP.navigate('dashboard'));
    }
    assert.ok(await page.locator('.contact-card .btn, .contact-controls .filter-chip, .workspace-footer a').evaluateAll(elements =>
      elements.filter(e => e.getBoundingClientRect().width).every(e => e.getBoundingClientRect().height>=44)), 'Home actions must have touch-sized targets');
    const offline = page.locator('.contact-card').filter({hasText:'Mi ordenador'});
    const connect = await offline.getByRole('button',{name:/^Conectar:/}).boundingBox();
    const message = await offline.getByRole('button',{name:/^Mensaje:/}).boundingBox();
    assert.ok(Math.abs(connect.y-message.y)<1,'Offline contact actions waste separate rows');
    assert.equal(await page.locator('#contactSearch').evaluate(e => parseFloat(getComputedStyle(e).fontSize)),16,'Contact search must not trigger input zoom');
    await page.locator('.workspace-footer a').last().scrollIntoViewIfNeeded();
    assert.ok(await page.locator('.workspace-footer a').last().evaluate(e => e.getBoundingClientRect().bottom<=document.querySelector('.nav-wrap').getBoundingClientRect().top),'Navigation covers the footer at the end of the page');
    if (viewport.width === 320) {
      assert.ok(await page.locator('.topbar').evaluate(header => window.scrollY > 0 && header.getBoundingClientRect().top === 0),
        'The network selector remains at the top while scrolling');
    }

    await page.evaluate(() => QP.navigate('virtual-disk'));
    if (viewport.width === 320) {
      assert.equal(await page.locator('#diskPathForm').isVisible(), false, 'A path editor requires an open location');
    }
    await page.locator('#diskShares .disk-name').first().click();
    await page.locator('.disk-file').first().waitFor();
    const file = await page.locator('.disk-file').first().evaluate(row => {
      const name = row.querySelector('.disk-name').getBoundingClientRect();
      return {width:row.getBoundingClientRect().width,nameWidth:name.width,actionsBelow:[...row.querySelectorAll('.btn')].every(e => e.getBoundingClientRect().top>=name.bottom)};
    });
    assert.ok(file.nameWidth>=file.width-1 && file.actionsBelow,'Long filenames are squeezed between Android save/delete actions');
    assert.ok(await page.locator('.disk-file button, .disk-share button').evaluateAll(elements => elements.every(e => e.getBoundingClientRect().height>=44)), 'Drive rows need touch-sized folder and action buttons');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth+1),'Long drive names overflow the phone');
    assert.ok(await page.locator('#diskPath').evaluate(e => e.getBoundingClientRect().width>=e.closest('form').getBoundingClientRect().width-1),'Drive path is squeezed beside the Go button');
    if (viewport.width === 390) {
      await page.locator('.disk-file').first().scrollIntoViewIfNeeded();
      await page.screenshot({path:'.artifacts/phone-layout/after-drive.png'});
      await page.evaluate(() => QP.navigate('dashboard'));
      await offline.scrollIntoViewIfNeeded();
      await page.screenshot({path:'.artifacts/phone-layout/after-contacts.png'});
    }
  }

  // A short viewport with the focused composer simulates an open software keyboard.
  await page.setViewportSize({width:390,height:460});
  await page.goto(url + 'chat.html?peer=00000000-0000-0000-0000-000000000001');
  const composer = page.locator('#messageInput');
  const message = 'Mensaje con el teclado abierto';
  await composer.fill(message);
  assert.equal(await page.locator('.nav-wrap').isVisible(),false);
  assert.equal(await page.locator('#headerNetworkSwitch').isVisible(),true);
  const sent = page.waitForResponse(response => response.url().endsWith('/api/chat/outbox') &&
    response.request().method() === 'POST', {timeout:5000});
  await page.locator('#sendBtn').tap();
  const response = await sent;
  assert.ok(response.ok());
  assert.equal(response.request().postDataJSON().message,message);
  await page.getByText(message,{exact:true}).waitFor();
  await page.waitForFunction(() => document.getElementById('messageInput').value === '');
  assert.equal(await composer.evaluate(input => input === document.activeElement),true);

  assert.deepEqual(errors,[]);
  console.log('PASS phone layout: navigation, touch targets, offline actions, long drive filenames and single-tap chat send with the composer focused');
} finally {
  await closeBrowserFixture(browser,server);
}
