/* Local Markdown rendering. Raw HTML and executable links are always treated as text. */
(() => {
  const escape = value => String(value).replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  function protectCode(text, hold) {
    // Visit adjacent delimiter runs once; unmatched backticks must never cause backtracking.
    const runs = /`+/g, output = [];
    let cursor = 0, opening;
    while ((opening = runs.exec(text))) {
      const contentStart = runs.lastIndex, closing = runs.exec(text);
      if (!closing) break;
      if (opening[0].length === closing[0].length && !text.slice(contentStart, closing.index).includes('\n')) {
        output.push(text.slice(cursor, opening.index), hold(`<code class="chat-inline-code">${escape(text.slice(contentStart, closing.index))}</code>`));
        cursor = runs.lastIndex;
      } else runs.lastIndex = closing.index;
    }
    output.push(text.slice(cursor));
    return output.join('');
  }
  function inline(value, depth = 0, mentions = []) {
    if (depth > 8) return escape(value);
    const tokens = [];
    const hold = html => `\u0000${tokens.push(html) - 1}\u0000`;
    let text = value.replace(/\u0000/g, '');
    text = text.replace(/\\([\\`*_{}\[\]()#+.!>~|])/g, (_, char) => hold(escape(char)));
    text = protectCode(text, hold);
    // Protect destinations and code before applying emphasis, including underscores in URLs.
    const link = (label, url) => {
      if (!/^(?:https?:\/\/|mailto:)[^\s<>\u0000-\u001f]+$/i.test(url)) return escape(`[${label}](${url})`);
      return `<a href="${escape(url)}" target="_blank" rel="noopener noreferrer" class="chat-link">${inline(label, depth + 1, mentions)}</a>`;
    };
    text = text.replace(/\[([^\[\]\n]+)\]\(([^\s()]+)\)/g, (_, label, url) => hold(link(label, url)));
    text = text.replace(/<?https?:\/\/[^\s<>\u0000]+>?/gi, url => {
      const address = url.replace(/^<|>$/g, '').replace(/[.,!?;:]+$/, '');
      const suffix = url.replace(/^</, '').replace(/>$/, '').slice(address.length);
      return hold(`<a href="${escape(address)}" target="_blank" rel="noopener noreferrer" class="chat-link">${escape(address)}</a>`) + suffix;
    });
    for (const mention of mentions.slice(0,2)) {
      if (typeof mention.name !== 'string' || !mention.name || mention.name.length>128) continue;
      const name=mention.name.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
      text=text.replace(new RegExp(`(^|[\\s(])@${name}(?=$|[\\s.,!?;:)])`,'giu'),(_,prefix)=>prefix+hold(`<span class="chat-mention">@${escape(mention.name)}</span>`));
    }
    text = escape(text);
    for (const [pattern, tag] of [
      [/\*\*\*([^\n]+?)\*\*\*/g, 'strong-em'],
      [/\*\*([^\n]+?)\*\*/g, 'strong'], [/__([^\n]+?)__/g, 'u'],
      [/~~([^\n]+?)~~/g, 'del'],
      [/(?<!\*)\*([^*\n]+)\*(?!\*)/g, 'em'], [/(?<![\w_])_([^_\n]+)_(?![\w_])/g, 'em']
    ]) text = text.replace(pattern, (_, content) => tag === 'strong-em' ? `<strong><em>${content}</em></strong>` : `<${tag}>${content}</${tag}>`);
    text = text.replace(/\|\|([^\n]+?)\|\|/g, '<button type="button" class="chat-spoiler" aria-expanded="false" aria-label="Mostrar contenido oculto">$1</button>');
    return text.replace(/\u0000(\d+)\u0000/g, (_, index) => tokens[Number(index)]).replace(/\n/g, '<br>');
  }
  function blocks(source, depth = 0, mentions = []) {
    if (depth > 6) return `<p>${inline(source,0,mentions)}</p>`;
    const lines = source.replace(/\r\n?/g, '\n').split('\n');
    const html = [];
    const special = line => /^\s*$|^ {0,3}(?:`{3,}|~{3,}|#{1,3}\s|>\s?|[-+*]\s|\d{1,6}\.\s)/.test(line);
    for (let i = 0; i < lines.length;) {
      const line = lines[i];
      if (!line.trim()) { i++; continue; }
      const fence = line.match(/^ {0,3}(`{3,}|~{3,})([\w+-]*)\s*$/);
      if (fence) {
        const code = []; i++;
        while (i < lines.length && !new RegExp(`^ {0,3}${fence[1][0]}{${fence[1].length},}\\s*$`).test(lines[i])) code.push(lines[i++]);
        if (i < lines.length) i++;
        html.push(`<pre class="chat-code-block"><code${fence[2] ? ` data-language="${escape(fence[2])}"` : ''}>${escape(code.join('\n'))}</code></pre>`); continue;
      }
      const heading = line.match(/^ {0,3}(#{1,3})\s+(.+)$/);
      if (heading) { html.push(`<h${heading[1].length}>${inline(heading[2],0,mentions)}</h${heading[1].length}>`); i++; continue; }
      if (/^ {0,3}>/.test(line)) {
        const quote = [];
        while (i < lines.length && /^ {0,3}>/.test(lines[i])) quote.push(lines[i++].replace(/^ {0,3}>\s?/, ''));
        html.push(`<blockquote>${blocks(quote.join('\n'), depth + 1, mentions)}</blockquote>`); continue;
      }
      const item = line.match(/^ {0,3}([-+*]|\d{1,6}\.)\s+(.+)$/);
      if (item) {
        const ordered = /\d/.test(item[1]), tag = ordered ? 'ol' : 'ul', items = [];
        while (i < lines.length) {
          const next = lines[i].match(/^ {0,3}([-+*]|\d{1,6}\.)\s+(.+)$/);
          if (!next || /\d/.test(next[1]) !== ordered) break;
          items.push(`<li>${inline(next[2],0,mentions)}</li>`); i++;
        }
        html.push(`<${tag}${ordered ? ` start="${parseInt(item[1], 10)}"` : ''}>${items.join('')}</${tag}>`); continue;
      }
      const paragraph = [line]; i++;
      while (i < lines.length && !special(lines[i])) paragraph.push(lines[i++]);
      html.push(`<p>${inline(paragraph.join('\n'),0,mentions)}</p>`);
    }
    return html.join('');
  }
  globalThis.QuicNetMarkdown = { render: (source, options={}) => blocks(String(source || '').replace(/\u0000/g, ''),0,Array.isArray(options.mentions)?options.mentions:[]) };
})();
