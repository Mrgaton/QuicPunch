import {launchBrowser, startPreviewServer, closeBrowserFixture} from '../Fixtures/BrowserFixture.mjs';
import {fileURLToPath} from 'node:url';
import {readFileSync, mkdirSync} from 'node:fs';
import assert from 'node:assert/strict';
const server = startPreviewServer([]);
let browser;
try{
 const url = await server.ready;
 browser = await launchBrowser();
 const source=readFileSync(new URL('../../QuicNet.Shared/wwwroot/design.css',import.meta.url),'utf8');
 assert.ok(!source.includes('[data-theme')&&!source.includes('prefers-color-scheme'),'Palette is still conditional');
 // A bright OS preference and a saved preference from an older installation
 // must have no effect, including before JavaScript runs.
 const noScript=await browser.newContext({javaScriptEnabled:false,colorScheme:'light'});
 for(const route of ['', 'settings.html','call.html','watch/','updates-help.html']){
  const page=await noScript.newPage();await page.goto(url+route);
  assert.equal(await page.locator('html').evaluate(n=>getComputedStyle(n).colorScheme),'dark',route+' depends on JavaScript to become dark');
  assert.equal(await page.locator('body').evaluate(n=>getComputedStyle(n).backgroundColor),'rgb(21, 28, 33)',route+' starts with the wrong background');
  await page.close();
 }
 await noScript.close();
 const context=await browser.newContext({colorScheme:'light',viewport:{width:1280,height:900}});
 await context.addInitScript(()=>localStorage.setItem('quicnet-theme','light'));
 const page=await context.newPage(),errors=[],requests=[];
 page.on('pageerror',e=>errors.push(e.message));page.on('request',r=>requests.push(r.url()));
 await page.goto(url+'settings.html');await page.locator('#settings-profile').waitFor();
 assert.equal(await page.locator('#settings-tab-appearance,#settings-appearance,#themeSelect').count(),0);
 assert.equal(await page.locator('#settingsCategory option[value=appearance]').count(),0);
 for(const width of [1280,390,320]){
  await page.setViewportSize({width,height:900});
  for(const view of ['dashboard','chat','voice','vpn','speedtest','settings','watch']){
   await page.evaluate(view=>QP.navigate(view),view);
   assert.equal(await page.locator('html').evaluate(n=>getComputedStyle(n).colorScheme),'dark',view);
   assert.equal(await page.locator('body').evaluate(n=>getComputedStyle(n).backgroundColor),'rgb(21, 28, 33)',view);
  }
 }
 const frame=page.frameLocator('#watchFrame');await frame.locator('body').waitFor();
 assert.equal(await frame.locator('html').evaluate(n=>getComputedStyle(n).colorScheme),'dark');
 await page.emulateMedia({colorScheme:'dark'});await page.emulateMedia({colorScheme:'light'});
 assert.equal(await page.locator('html').evaluate(n=>getComputedStyle(n).colorScheme),'dark','OS changes switched the palette');
 await page.evaluate(()=>QP.navigate('settings','/settings.html?section=appearance'));
 await page.locator('#settings-profile').waitFor();
 await page.reload();await page.locator('#settings-profile').waitFor();
 assert.equal(await page.locator('html').evaluate(n=>getComputedStyle(n).colorScheme),'dark');
 assert.ok(!requests.some(r=>r.includes('/api/ui/preferences')),'Removed preferences API is still being requested');
 mkdirSync(fileURLToPath(new URL('../../.artifacts/dark-interface/',import.meta.url)),{recursive:true});
 for(const width of [1280,390]){await page.setViewportSize({width,height:900});await page.screenshot({path:fileURLToPath(new URL('../../.artifacts/dark-interface/settings-'+width+'.png',import.meta.url)),fullPage:true});}
 assert.deepEqual(errors,[]);await context.close();
 console.log('PASS fixed dark interface: first render without JS, legacy preferences ignored, OS changes ignored, all views and Watch, removed controls/API calls, desktop and mobile');
}finally{await closeBrowserFixture(browser, server);}
