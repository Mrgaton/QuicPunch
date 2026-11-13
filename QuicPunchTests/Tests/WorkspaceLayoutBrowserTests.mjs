import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Exercise actual page geometry and scrolling against the isolated web fixture.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({ viewport: {width:2560,height:1080} });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  const peerId = '00000000-0000-0000-0000-000000000001';
  const messages = Array.from({length:100}, (_,i) => ({peerId,msgId:`layout-${i}`,sender:i%2?'Tú':'Ana',message:`Mensaje ${i}: Este historial largo permite comprobar el desplazamiento sin perder el cuadro de escritura.`,isMe:!!(i%2),isConfirmed:true,time:'12:00'}));
  await page.route('**/api/chat/messages?**', route => route.fulfill({json:{messages,nextBefore:null}}));
  await page.goto(url);
  await page.locator('#homeConnectedFilterCount').filter({hasText:'1'}).waitFor({state:'attached'});
  assert.equal(await page.locator('#chatMarkdownHint, .topbar-inner, .breadcrumb, #connectionIndicator').count(), 0);
  const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  // Pages use the available width; Speed keeps its intentional 1500px reading limit.
  for (const width of [2560,1280,920,390,320]) {
    await page.setViewportSize({width,height:900});
    for (const view of ['dashboard','voice','vpn','speedtest','settings']) {
      await page.evaluate(view => QP.navigate(view), view); await settle();
      const bounds = await page.evaluate(view => {
        const host = document.getElementById('spaContainer').getBoundingClientRect();
        const main = document.querySelector(`#view-${view} .workspace`).getBoundingClientRect();
        const style = getComputedStyle(document.querySelector(`#view-${view} .workspace`));
        return {gap:host.width-main.width,hostWidth:host.width,padding:parseFloat(style.paddingLeft),overflow:document.documentElement.scrollWidth-innerWidth};
      }, view);
      const expectedGap = view === 'speedtest' ? Math.max(0, bounds.hostWidth - 1500) : view === 'vpn' ? Math.max(0, bounds.hostWidth - 1600) : 0;
      assert.ok(Math.abs(bounds.gap - expectedGap) <= 1 && bounds.padding <= 40, `${view} wastes horizontal space at ${width}px: ${JSON.stringify(bounds)}`);
      assert.ok(bounds.overflow <= 1, `${view} overflows horizontally at ${width}px`);
      assert.ok(await page.locator('.app-location').isVisible(), 'The workspace header must identify the current page');
    }
  }
  await page.goto(url + 'chat.html?peer=' + peerId);
  await page.waitForFunction(() => !document.getElementById('messageInput').disabled);
  await page.waitForFunction(() => document.querySelectorAll('#messages .message-row').length === 100);
  async function assertChatFits(label) {
    await settle();
    const geometry = await page.evaluate(() => {
      const box = id => document.getElementById(id).getBoundingClientRect();
      const chat = box('chatContainer'), input = box('messageInput'), history = box('messages');
      const nav = document.querySelector('.nav-wrap').getBoundingClientRect();
      const limit = innerWidth <= 680 && nav.height > 0 ? nav.top : innerHeight;
      return {chatBottom:chat.bottom,inputTop:input.top,inputBottom:input.bottom,historyBottom:history.bottom,historyHeight:history.height,limit,rootHeight:document.scrollingElement.scrollHeight,viewport:innerHeight,scrollY,overflow:document.documentElement.scrollWidth-innerWidth};
    });
    assert.ok(geometry.inputTop >= 0 && geometry.inputBottom <= geometry.limit + 1, `${label}: composer hidden: ${JSON.stringify(geometry)}`);
    assert.ok(geometry.historyBottom <= geometry.inputTop && geometry.historyHeight > 0, `${label}: history overlaps composer`);
    assert.ok(geometry.chatBottom <= geometry.limit + 1 && geometry.rootHeight <= geometry.viewport + 1 && geometry.scrollY === 0, `${label}: nested page scroll: ${JSON.stringify(geometry)}`);
    assert.ok(geometry.overflow <= 1, `${label}: horizontal overflow`);
  }
  for (const viewport of [{width:2560,height:1080},{width:1280,height:720},{width:920,height:844},{width:390,height:844},{width:320,height:568},{width:390,height:430},{width:844,height:390}]) {
    await page.setViewportSize(viewport);
    await assertChatFits(JSON.stringify(viewport));
    await page.locator('#messageInput').fill('Un borrador que permanece visible\nIncluso con varias líneas\nY mientras leo mensajes anteriores');
    await page.locator('#messages').evaluate(node => { node.style.scrollBehavior = 'auto'; node.scrollTop = 0; });
    await assertChatFits('history top / multiline');
    // Scrolling beyond either end must never move the outer workspace.
    await page.locator('#messages').hover(); await page.mouse.wheel(0,-2000);
    await page.locator('#messages').evaluate(node => { node.scrollTop = node.scrollHeight; });
    await page.mouse.wheel(0,2000);
    await assertChatFits('history bottom / wheel');
  }
  await page.setViewportSize({width:390,height:844});
  const back = page.getByRole('button', {name:'Volver a la lista de conversaciones'});
  const backBounds = await back.boundingBox();
  assert.ok(backBounds.y >= 0 && backBounds.width >= 44 && backBounds.height >= 44, 'Back button is visible and touch sized');
  await page.locator('#chatMobileBackBtn').click();
  assert.equal(await page.locator('.dm-pane').isVisible(), true);
  assert.equal(new URL(page.url()).searchParams.has('peer'), false, 'Back clears the conversation deep link');
  await page.locator('.nav-link[data-page=dashboard]').click();
  await page.locator('.nav-link[data-page=chat]').click();
  assert.equal(await page.locator('.dm-pane').isVisible(), true, 'Returning to Messages keeps the conversation list open');
  const conversation = page.locator('#chatPeers .dm-peer').filter({hasText:'Ana'});
  await conversation.click();
  const draft = 'Este borrador permanece al volver a la lista';
  await page.locator('#messageInput').fill(draft);
  await page.locator('.nav-link[data-page=chat] svg').click();
  assert.equal(await page.locator('.dm-pane').isVisible(), true, 'Tapping the active Messages icon returns to the list');
  assert.equal(await page.locator('.conversation-pane').isVisible(), false);
  assert.equal(await page.evaluate(() => document.activeElement.id), '', 'The hidden composer loses focus');
  await page.evaluate(() => QP.emit('chat_msg', {peerId:'00000000-0000-0000-0000-000000000001',msgId:'while-list',sender:'Ana',message:'Nuevo mensaje',time:'12:01',isMe:false}));
  assert.equal(await page.locator('.dm-pane').isVisible(), true, 'Incoming messages do not reopen the conversation');
  await page.locator('.nav-link[data-page=chat]').click();
  assert.equal(await page.locator('.dm-pane').isVisible(), true, 'Repeated taps keep the list open');
  await conversation.click();
  assert.equal(await page.locator('#messageInput').inputValue(), draft, 'The shortcut preserves the per-contact draft');
  await assertChatFits('mobile return to conversation');
  await page.screenshot({path:'.artifacts/chat-layout-mobile.png'});
  await page.setViewportSize({width:1440,height:900});
  await page.locator('.nav-link[data-page=chat]').click();
  assert.equal(await page.locator('#messageInput').inputValue(), draft, 'Desktop navigation preserves the conversation draft');
  assert.equal(await page.locator('#chatContainer').evaluate(node => node.classList.contains('in-conversation')), true, 'Desktop Messages does not close the conversation');
  await page.screenshot({path:'.artifacts/chat-layout-desktop.png'});
  assert.deepEqual(errors, []);
  console.log('PASS workspace widths and chat: desktop/mobile/landscape, long history, multiline composer, scroll boundaries and navigation');
} finally { await closeBrowserFixture(browser, server); }
