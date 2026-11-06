import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Isolated fixture: touch targets, narrow forms, rotation and bounded video rooms.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page = await browser.newPage({viewport:{width:390,height:844},hasTouch:true,isMobile:true});
  const errors=[]; page.on('pageerror',error => errors.push(error.message));
  const settings={startWithSystem:false,runInBackground:false,notifications:{enabled:true,desktop:true,inApp:true,sound:true,preview:true,whileFocused:true,doNotDisturb:false,quietHours:false,quietStart:1320,quietEnd:480,volume:60,tone:'soft',features:{},contacts:{}}};
  await page.route('**/api/app/preferences',route => route.fulfill({json:{success:true,settings,startupSupported:true,platformHelp:'Host de prueba'}}));
  const peerId='00000000-0000-0000-0000-000000000001';
  const messages=Array.from({length:12},(_,i)=>({peerId,msgId:`responsive-${i}`,sender:i%2?'Tú':'Ana',message:'Un mensaje largo que se debe leer cómodamente en el móvil, con suficiente espacio para todo el texto. '+ 'abcdefgh'.repeat(15),isMe:!!(i%2),isConfirmed:true,time:'12:00'}));
  await page.route('**/api/chat/messages?**',route => route.fulfill({json:{messages,nextBefore:null}}));
  await page.goto(url+'settings.html');
  await page.waitForFunction(()=>!document.getElementById('appBehaviorFields').disabled);
  const settle=()=>page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  for(const viewport of [{width:320,height:568},{width:390,height:844},{width:700,height:844},{width:844,height:390}]) {
    await page.setViewportSize(viewport);
    for(const view of ['settings','dashboard','voice','vpn','speedtest']) {
      await page.evaluate(view=>QP.navigate(view),view);await settle();
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`${view} overflows at ${viewport.width}`);
    }
    await page.evaluate(()=>QP.navigate('settings','/settings.html?section=notifications'));await settle();
    if (!await page.locator('#noticeDnd').isChecked()) await page.locator('#noticeDnd').check();
    const geometry=await page.evaluate(()=>{
      const date=document.getElementById('noticeDndUntil'),box=date.getBoundingClientRect(),parent=date.closest('.field').getBoundingClientRect();
      const toggle=document.getElementById('noticeSound').closest('label').getBoundingClientRect();
      return {dateWidth:box.width,formWidth:parent.width,toggleHeight:toggle.height,font:parseFloat(getComputedStyle(date).fontSize)};
    });
    assert.ok(geometry.toggleHeight>=44,'Checkbox labels need a usable touch area');
    if(viewport.width<=680) {assert.ok(geometry.dateWidth>=geometry.formWidth-1,'Mobile date picker is squeezed into half a row');assert.ok(geometry.font>=16);}
    await page.goto(url+'chat.html?peer='+peerId);
    await page.waitForFunction(()=>document.querySelectorAll('#messages .message-row').length===12&&!document.getElementById('messageInput').disabled);
    await page.locator('#messages .message-row').last().locator('summary').tap();
    await page.getByRole('button',{name:'Añadir reacción',exact:true}).tap();await settle();
    const menu=await page.locator('.message-menu[open]').evaluate(menu=>{
      const bounds=menu.querySelector('.message-menu-actions').getBoundingClientRect(),hit=menu.querySelector('summary').getBoundingClientRect();
      return {left:bounds.left,right:bounds.right,top:bounds.top,bottom:bounds.bottom,width:innerWidth,height:innerHeight,hitWidth:hit.width,hitHeight:hit.height,emoji:[...menu.querySelectorAll('.message-reaction-picker .btn')].map(btn=>({width:btn.getBoundingClientRect().width,height:btn.getBoundingClientRect().height}))};
    });
    assert.ok(menu.left>=0&&menu.top>=0&&menu.right<=menu.width&&menu.bottom<=menu.height,'Message menu escapes the viewport');
    assert.ok(menu.hitWidth>=44&&menu.hitHeight>=44&&menu.emoji.every(b=>b.width>=44&&b.height>=44),'Message actions are too small for touch');
    await page.keyboard.press('Escape');
    const input=await page.locator('#messageInput').boundingBox();
    assert.ok(input.y>=0&&input.y+input.height<=viewport.height,'Composer disappeared');
    await page.evaluate(()=>QP.navigate('watch'));await settle();
    assert.ok(await page.locator('#watchFrame').evaluate(frame=>{const r=frame.getBoundingClientRect(),nav=document.querySelector('.nav-wrap').getBoundingClientRect();return r.height>0&&r.bottom<=(innerWidth<=680?nav.top:innerHeight)+1&&document.scrollingElement.scrollHeight<=innerHeight+1;}),'Video room extends underneath navigation or scrolls the outer page');
    if(viewport.width===844) {
      const nav=page.locator('.nav-link[data-page=settings]');await nav.tap();
      assert.equal(await page.locator('body').getAttribute('data-page'),'settings','Settings unreachable in landscape');
    }
  }
  await page.setViewportSize({width:390,height:844});await page.evaluate(()=>QP.navigate('settings'));await settle();
  await page.screenshot({path:'.artifacts/responsive-settings-mobile.png'});
  await page.goto(url+'chat.html?peer='+peerId);await page.locator('#messages .message-row').last().waitFor();
  await page.screenshot({path:'.artifacts/responsive-chat-mobile.png'});
  await page.locator('#messages .message-row').last().locator('summary').tap();
  await page.setViewportSize({width:844,height:390});await settle();
  assert.equal(await page.locator('.message-menu[open]').count(),0,'Rotation leaves a stale floating menu');
  await page.setViewportSize({width:1440,height:900});await page.evaluate(()=>QP.navigate('settings','/settings.html?section=notifications'));await settle();
  assert.ok(await page.locator('.behavior-layout').evaluate(grid=>getComputedStyle(grid).gridTemplateColumns.split(' ').length>=2),'Desktop cards lost their columns');
  assert.deepEqual(errors,[]);
  console.log('PASS responsive UI: phone/tablet/landscape, touch menus, forms, rotation, video room and desktop columns');
} finally { await closeBrowserFixture(browser, server); }
