import {readVoiceSource} from '../Fixtures/VoiceSource.mjs';
import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
// Exercise the real picker against fake source/preview endpoints; no screen is shared.

import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try {
  const url = await server.ready;
  browser = await launchBrowser();
  const page=await browser.newPage({viewport:{width:1440,height:900}}), errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  const names=['Escritorio completo','Pantalla 1 · principal','Pantalla 2','Navegador','Terminal 1','Terminal 2','Editor de texto','Archivos','Música','Correo','Agenda','Calculadora'];
  const sources=names.map((Name,i)=>({Id:i===0?'desktop':`source:${i}`,Name,Kind:i<3?'display':'window',Width:1920,Height:1080}));
  let sourceList=sources, slowResolve;
  await page.route('**/voice.js',route=>route.fulfill({contentType:'text/javascript',body:readVoiceSource().replace(/\}\)\(\);\s*$/,'window.testCapturePicker = chooseNativeSource;})();')}));
  await page.route('**/api/voice/screen-share/sources',async route=>{
    const result=sourceList;
    if(slowResolve) {const wait=slowResolve;slowResolve=null;await wait;}
    await route.fulfill({json:{sources:result}});
  });
  await page.route('**/api/voice/screen-share/preview?**',route=>route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#182d38"/><rect x="40" y="35" width="560" height="290" rx="12" fill="#314b5b"/><rect x="60" y="65" width="520" height="35" fill="#8ad7bc"/><text x="65" y="155" fill="white" font-size="28">Vista previa de prueba</text></svg>'}));
  await page.goto(url+'voice.html');
  await page.waitForFunction(()=>typeof window.testCapturePicker==='function');
  const modal=page.locator('#screenNativeCaptureModal'), search=page.locator('#screenCaptureSearch');
  const options=page.locator('#screenCaptureGallery [role=option]');
  const selected=()=>page.locator('#screenCaptureGallery [aria-selected=true]').getAttribute('data-source-id');
  const settle=()=>page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  async function open(){
    await page.locator('.nav-link[data-page=voice]').focus();
    await page.evaluate(()=>{window.pickerChoice=null;window.testCapturePicker(true).then(choice=>window.pickerChoice=choice);});
    await modal.waitFor();await page.waitForFunction(()=>document.querySelectorAll('#screenCaptureGallery [role=option]').length===12);
    await page.waitForFunction(()=>document.activeElement===document.getElementById('screenCaptureSearch'));
  }
  await open();assert.equal(await selected(),'desktop');
  const columns=await options.evaluateAll(buttons=>{const y=buttons[0].getBoundingClientRect().top;return buttons.filter(button=>Math.abs(button.getBoundingClientRect().top-y)<2).length;});
  assert.ok(columns>1,'Desktop gallery must have multiple columns');
  await page.keyboard.press('ArrowRight');assert.equal(await selected(),'source:1');
  await page.keyboard.press('ArrowDown');assert.equal(await selected(),sources[1+columns].Id);
  await page.keyboard.press('ArrowUp');assert.equal(await selected(),'source:1');
  await page.keyboard.press('ArrowLeft');assert.equal(await selected(),'desktop');
  await page.keyboard.press('ArrowUp');await page.keyboard.press('ArrowLeft');assert.equal(await selected(),'desktop','Navigation escaped the grid boundary');
  await page.keyboard.type('Terminal');
  await page.waitForFunction(()=>document.querySelectorAll('#screenCaptureGallery [role=option]').length===2);
  assert.equal(await search.inputValue(),'Terminal');assert.equal(await options.count(),2);assert.equal(await selected(),'source:4');
  await page.keyboard.press('Enter');await modal.waitFor({state:'hidden'});assert.equal(await page.evaluate(()=>window.pickerChoice),'source:4');
  await open();await page.locator('#screenCaptureWindows').click();
  await page.keyboard.type('Navegador');await page.waitForFunction(()=>document.querySelectorAll('#screenCaptureGallery [role=option]').length===1);assert.equal(await options.count(),1);assert.equal(await selected(),'source:3');
  await page.locator('#screenCaptureDisplays').click();assert.equal(await options.count(),0);
  assert.equal(await page.locator('#screenNativeCaptureStart').isDisabled(),true);
  await search.focus();await page.keyboard.press('Enter');assert.equal(await modal.isVisible(),true,'Enter selected a hidden source with no matches');
  await search.fill('');await page.locator('#screenCaptureAll').click();
  await page.keyboard.press('Escape');await modal.waitFor({state:'hidden'});
  assert.equal(await page.evaluate(()=>window.pickerChoice),'cancel');
  await page.waitForFunction(()=>document.activeElement===document.querySelector('.nav-link[data-page=voice]'));
  // Reopening must not leave keyboard listeners or stale source selection behind.
  await open();await page.keyboard.press('Tab');
  assert.ok(await page.evaluate(()=>document.getElementById('screenNativeCaptureModal').contains(document.activeElement)),'Tab escaped the dialog');
  await page.keyboard.press('Control+a'); // A shortcut on a button must not become search text.
  assert.equal(await search.inputValue(),'');
  await page.evaluate(()=>getSelection().removeAllRanges());
  for(const theme of ['dark']) {
    await page.evaluate(theme=>document.documentElement.dataset.theme=theme,theme);
    await page.waitForTimeout(200); // Measure the final theme after button color transitions.
    for(const [width,height] of [[1440,900],[390,844],[320,568],[844,390]]) {
      await page.setViewportSize({width,height});await settle();
      const contrast=await page.locator('.capture-source-card, .capture-picker .btn').evaluateAll(buttons=>{
        const luminance=color=>{const rgb=color.match(/[\d.]+/g).slice(0,3).map(Number).map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4;});return rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722;};
        return buttons.map(button=>{let background=button;while(getComputedStyle(background).backgroundColor==='rgba(0, 0, 0, 0)')background=background.parentElement;const bg=luminance(getComputedStyle(background).backgroundColor),fg=luminance(getComputedStyle(button.querySelector('strong')||button).color);return {text:button.textContent,ratio:(Math.max(bg,fg)+.05)/(Math.min(bg,fg)+.05),background:getComputedStyle(background).backgroundColor};});
      });
      assert.ok(contrast.every(item=>item.ratio>=4.5),`Unreadable controls in ${theme} at ${width}px: ${JSON.stringify(contrast.filter(item=>item.ratio<4.5))}`);
      assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Picker overflows horizontally');
      await options.last().click();await page.keyboard.press('ArrowUp');
      const focused=await page.evaluate(()=>{const r=document.activeElement.getBoundingClientRect(),body=document.querySelector('.capture-picker>.modal-body').getBoundingClientRect();return r.bottom>body.top&&r.top<body.bottom&&scrollY===0;});
      assert.ok(focused,'Keyboard-selected source was not scrolled into view');
      await options.first().click();
      await page.locator('.capture-picker>.modal-body').evaluate(body=>body.scrollTop=0);
      await page.locator('#screenCaptureGallery').evaluate(grid=>grid.scrollTop=0);
      await page.screenshot({path:`.artifacts/screen-picker-${theme}-${width}.png`});
    }
  }
  await page.keyboard.press('Escape');await open();
  let release;slowResolve=new Promise(resolve=>release=resolve);
  await page.locator('#screenCaptureRefresh').click();await page.keyboard.press('Escape');
  release();await settle();assert.equal(await modal.isVisible(),false,'A late refresh reopened the picker');
  assert.deepEqual(errors,[]);
  console.log('PASS screen picker: dark contrast, autofocus, type-to-search, spatial arrows, Enter, no-result safety, Escape/focus return, reopen, mobile and late refresh');
} finally {await closeBrowserFixture(browser, server);}
