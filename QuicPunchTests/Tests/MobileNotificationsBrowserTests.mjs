import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';

const server = startPreviewServer(['--interactive']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
  await page.addInitScript(() => { window.__qp_is_android = true; });
  await page.goto(url + 'settings.html');
  await page.locator('#settings-profile').waitFor();
  for (const path of ['settings.html','chat.html','/']) {
    await page.evaluate(path => QP.navigate(path === '/' ? path : '/' + path), path);
    for (const viewport of [{width:390,height:844},{width:320,height:568},{width:640,height:360},{width:390,height:360}]) {
      await page.setViewportSize(viewport);
      const geometry = await page.evaluate(() => {
        document.querySelector('.toast-host').replaceChildren();
        const workspace = document.getElementById('spaContainer');
        const before = workspace.getBoundingClientRect().height;
        QP.toast('Failed to connect Chat: El contacto rechazó la petición.', 'error');
        const host = document.querySelector('.toast-host'), box = host.getBoundingClientRect();
        const close = host.querySelector('button').getBoundingClientRect();
        return {top:box.top,bottom:box.bottom,left:box.left,right:box.right,viewport:innerWidth,
          position:getComputedStyle(host).position,shift:workspace.getBoundingClientRect().height-before,
          closeVisible:document.elementFromPoint(close.x+close.width/2,close.y+close.height/2) === host.querySelector('button')};
      });
      assert.equal(geometry.position, 'fixed');
      assert.equal(geometry.top, 12, `Mobile notification must stay at the top on ${path}: ${JSON.stringify(geometry)}`);
      assert.ok(geometry.left >= 8 && geometry.right <= geometry.viewport - 8);
      assert.equal(geometry.shift, 0, 'Notifications must not resize the workspace');
      assert.equal(geometry.closeVisible, true, 'Dismiss control must be visible and clickable');
      await page.getByRole('button',{name:'Cerrar aviso',exact:true}).click();
      assert.equal(await page.locator('.toast').count(), 0);

      const overflow = await page.evaluate(() => {
        for(let i=0;i<5;i++) QP.toast('Parámetro no válido: ' + 'valor'.repeat(100), 'error');
        const host=document.querySelector('.toast-host');
        return {count:host.children.length,width:host.clientWidth,scrollWidth:host.scrollWidth,
          height:host.getBoundingClientRect().height,limit:innerHeight*0.3,top:host.getBoundingClientRect().top};
      });
      assert.equal(overflow.count,3);
      assert.ok(overflow.scrollWidth <= overflow.width + 1, 'Long parameter errors must wrap within the notification');
      assert.ok(overflow.height <= overflow.limit + 1);
      assert.equal(overflow.top,12);
      await page.evaluate(() => document.querySelector('.toast-host').replaceChildren());
    }
  }
  const peerId = '00000000-0000-0000-0000-000000000001';
  await page.waitForFunction(id => chatStatus?.peers.some(peer => peer.id === id), peerId);
  const receive = (start, end) => page.evaluate(({peerId, start, end}) => {
    for (let i = start; i < end; i++) {
      QP.emit('chat_msg', {peerId, msgId: 'badge-' + i, message: 'Mensaje nuevo',
        sender: 'Ana', isMe: false, timestampMs: Date.now()});
    }
  }, {peerId, start, end});
  const badge = page.locator('#chatUnreadNavCount');
  await receive(0, 1);
  assert.equal(await badge.textContent(), '1');
  const checkBadge = async () => {
    const geometry = await badge.evaluate(node => {
      const box = node.getBoundingClientRect(), link = node.closest('a');
      const icon = link.querySelector('.icon').getBoundingClientRect();
      const label = link.querySelector('.nav-label').getBoundingClientRect();
      const nav = link.closest('.nav-wrap').getBoundingClientRect();
      const tab = link.getBoundingClientRect();
      const overlapX = (Math.max(box.left, icon.left) + Math.min(box.right, icon.right)) / 2;
      const overlapY = (Math.max(box.top, icon.top) + Math.min(box.bottom, icon.bottom)) / 2;
      const style = getComputedStyle(node);
      const luminance = color => {
        const [r,g,b] = color.match(/\d+/g).map(value => {
          const channel = Number(value) / 255;
          return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
        });
        return .2126*r + .7152*g + .0722*b;
      };
      const tones = [luminance(style.color), luminance(style.backgroundColor)];
      return {aboveIcon:box.top < icon.top && box.bottom > icon.top && box.left < icon.right && box.right > icon.right,
        inBar:box.top >= nav.top && box.bottom <= label.top && box.left >= tab.left && box.right <= tab.right,
        height:box.height, fontSize:parseFloat(style.fontSize),
        contrast:(Math.max(...tones) + .05) / (Math.min(...tones) + .05),
        onTop:document.elementFromPoint(overlapX, overlapY) === node};
    });
    assert.ok(geometry.aboveIcon && geometry.inBar && geometry.onTop, JSON.stringify(geometry));
    assert.ok(geometry.height >= 22 && geometry.fontSize >= 12, JSON.stringify(geometry));
    assert.ok(geometry.contrast >= 4.5, JSON.stringify(geometry));
  };
  await checkBadge();
  await page.getByRole('link',{name:'Mensajes, 1 mensaje sin leer',exact:true}).waitFor();
  await receive(1, 100);
  assert.equal(await badge.textContent(), '99+');
  await page.getByRole('link',{name:'Mensajes, 100 mensajes sin leer',exact:true}).waitFor();
  for (const viewport of [{width:320,height:568},{width:390,height:844},{width:667,height:375}]) {
    await page.setViewportSize(viewport);
    await checkBadge();
  }
  await page.getByRole('link',{name:'Mensajes, 100 mensajes sin leer',exact:true}).click();
  await page.locator(`#chatPeers [data-peer-id="${peerId}"]`).click();
  await badge.waitFor({state:'detached'});
  await page.locator('#chatMobileBackBtn').click();
  await page.getByRole('link',{name:'Mensajes',exact:true}).waitFor();
  console.log('PASS mobile notifications: top anchoring, dismissal, stable workspace and visible unread count on the Messages icon');
} finally { await closeBrowserFixture(browser,server); }
