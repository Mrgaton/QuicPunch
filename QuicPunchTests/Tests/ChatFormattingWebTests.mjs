import {readFileSync} from 'node:fs';
import {Script, createContext} from 'node:vm';
import assert from 'node:assert/strict';
const context = createContext({});
new Script(readFileSync(new URL('../../QuicNet.Shared/wwwroot/chat-markdown.js',import.meta.url),'utf8')).runInContext(context);
const render = context.QuicNetMarkdown.render;
const html = render('# Título\n\n**fuerte** *énfasis* __subrayado__ ~~tachado~~ ***ambos***\n\n> Una cita\n\n- uno\n- dos\n\n3. tercero\n4. cuarto\n\n```js\n**literal**\n<a>\n```\n\n`*literal*` [sitio](https://example.org/a_b) ||secreto||');
for (const tag of ['h1','strong','em','u','del','blockquote','ul','ol','li','pre','code','a','button']) assert.ok(html.includes(`<${tag}`), `Missing Markdown ${tag}`);
assert.ok(html.includes('<ol start="3">'));
assert.ok(html.includes('**literal**\n&lt;a&gt;'), 'Code blocks must remain literal');
assert.ok(html.includes('>*literal*</code>'), 'Inline code must remain literal');
assert.ok(html.includes('href="https://example.org/a_b"'), 'Emphasis must never corrupt URLs');
assert.equal(render('\\*literal\\*'), '<p>*literal*</p>');
for (const source of ['<img src=x onerror=alert(1)>','[x](javascript:alert)','[x](data:text/html,evil)','https://example.org/"onclick="alert(1)','[x](https://example.org/"onmouseover="evil)','\u00000\u0000<script>alert(1)</script>']) {
  const result = render(source);
  assert.ok(!/<(?:script|img)\b/i.test(result), 'Raw HTML became executable');
  assert.ok(!/href="(?:javascript|data):/i.test(result), 'Unsafe link protocol');
  assert.ok(!/href="[^"]*"\s*on\w+=/i.test(result), 'Link attribute injection');
  assert.ok(!result.includes('\u0000'), 'Internal formatting token leaked');
}
assert.ok(!render('```\n[x](https://example.org) **plain**\n```').includes('<a '));
const mentions=[{id:'peer',name:'Ana · Portátil'}];
assert.ok(render('Hola @Ana · Portátil!',{mentions}).includes('class="chat-mention"'));
assert.ok(!render('`@Ana · Portátil`',{mentions}).includes('class="chat-mention"'));
assert.ok(!render('@Ana · PortátilX',{mentions}).includes('class="chat-mention"'));
assert.ok(!render('@<img src=x onerror=alert(1)>',{mentions:[{id:'peer',name:'<img src=x onerror=alert(1)>'}]}).includes('<img'));
console.log('PASS chat Markdown: formatting, lists, quotes, code isolation, escapes, safe links, spoilers and HTML injection protection');
for(const text of ['texto '+'`'.repeat(120000),'['.repeat(120000),('['+'a'.repeat(80)).repeat(1500), 'texto '+('`'.repeat(1000)+'x\n').repeat(100)]) {
  context.adversarialText=text;
  new Script('QuicNetMarkdown.render(adversarialText)').runInContext(context,{timeout:1500});
}
assert.ok(render('Antes ``literal`` después').includes('>literal</code>'));
assert.ok(render('`sin cerrar\ntexto').includes('`sin cerrar'));
console.log('PASS bounded Markdown rendering: long unmatched delimiters, brackets and mixed lines');
