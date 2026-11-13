import assert from 'node:assert/strict';
import {mkdir} from 'node:fs/promises';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';

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
    upstream.onMessage(message => {
      const packet = JSON.parse(message.toString());
      if (packet.data?.peers) offlineMessages(packet.data);
      socket.send(JSON.stringify(packet));
    });
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

  for (const viewport of [{width:320,height:568},{width:360,height:800},{width:390,height:844},{width:430,height:932},{width:667,height:375}]) {
    await page.setViewportSize(viewport);
    for (const view of ['dashboard','chat','voice','watch','virtual-disk','settings','vpn','speedtest']) {
      await page.evaluate(view => QP.navigate(view), view);
      await settle();
      const nav = await page.locator('.nav-wrap').evaluate(host => {
        const rect = host.getBoundingClientRect();
        return {top:rect.top,bottom:rect.bottom,items:[...host.querySelectorAll('.nav-link')].filter(e => e.getBoundingClientRect().width).map(e => {
          const box = e.getBoundingClientRect(), icon = e.querySelector('svg').getBoundingClientRect(), label = e.querySelector('.nav-label').getBoundingClientRect();
          return {page:e.dataset.page,width:box.width,height:box.height,iconTop:icon.top,labelTop:label.top,labelBottom:label.bottom,
            fits:label.left>=box.left && label.right<=box.right && label.bottom<=box.bottom,
            reachable:e.contains(document.elementFromPoint(box.x+box.width/2,box.y+box.height/2))};
        })};
      });
      const label = `${view} at ${viewport.width}px`;
      assert.equal(nav.items.length,6,label + ': all six primary tabs are visible');
      assert.ok(nav.items.every(item => Math.abs(item.iconTop-nav.items[0].iconTop)<1),label + ': navigation icons are misaligned');
      assert.ok(nav.items.every(item => Math.abs(item.labelTop-nav.items[0].labelTop)<1 && item.fits),label + ': navigation labels are clipped or misaligned');
      assert.ok(nav.items.every(item => item.width>=44 && item.height>=44 && item.reachable),label + ': navigation is too small or obstructed');
      assert.ok(nav.bottom<=viewport.height,label + ': navigation escapes the viewport');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth+1),label + ': horizontal overflow');
    }
    await page.evaluate(() => QP.navigate('dashboard'));
    await settle();
    assert.ok(await page.locator('.contact-card .btn, .contact-controls .filter-chip, .workspace-footer a').evaluateAll(elements =>
      elements.filter(e => e.getBoundingClientRect().width).every(e => e.getBoundingClientRect().height>=44)), 'Home actions must have touch-sized targets');
    const offline = page.locator('.contact-card').filter({hasText:'Mi ordenador'});
    const connect = await offline.getByRole('button',{name:/^Conectar:/}).boundingBox();
    const message = await offline.getByRole('button',{name:/^Mensaje:/}).boundingBox();
    assert.ok(Math.abs(connect.y-message.y)<1,'Offline contact actions waste separate rows');
    assert.equal(await page.locator('#contactSearch').evaluate(e => parseFloat(getComputedStyle(e).fontSize)),16,'Contact search must not trigger input zoom');
    await page.locator('.workspace-footer a').last().scrollIntoViewIfNeeded();
    assert.ok(await page.locator('.workspace-footer a').last().evaluate(e => e.getBoundingClientRect().bottom<=document.querySelector('.nav-wrap').getBoundingClientRect().top),'Navigation covers the footer at the end of the page');

    await page.evaluate(() => QP.navigate('virtual-disk'));
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
  assert.deepEqual(errors,[]);
  console.log('PASS phone layout: aligned navigation, all workspaces, touch targets, offline actions and long Android drive filenames at five viewport sizes');
} finally {
  await closeBrowserFixture(browser,server);
}
