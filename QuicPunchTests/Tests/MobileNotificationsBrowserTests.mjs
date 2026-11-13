import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import assert from 'node:assert/strict';

const server = startPreviewServer(['--interactive']);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
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
  console.log('PASS mobile notifications: top anchoring, narrow/landscape/keyboard-sized viewports, dismissal, long errors and stable workspace');
} finally { await closeBrowserFixture(browser,server); }
