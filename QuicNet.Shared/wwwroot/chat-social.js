/* Small conversation actions shared by the desktop and Android web UI. */
(() => {
  const emoji = ['👍', '❤️', '😂', '😮', '😢', '🎉'];
  const snapshots = new Map(), replies = new Map(), revisions = new Map(), loads = new Map(), dirty = new Set(), syncTimers = new Map();
  let bridge, mention, mentionDismissed = false, activePeer;
  const $ = id => document.getElementById(id);
  const current = () => bridge?.peer();
  function decode(raw) {
    if (typeof raw !== 'string' || !raw.startsWith('{')) return {text:String(raw || ''),mentions:[]};
    try {
      const value = JSON.parse(raw);
      if (value.type === 'text' && typeof value.text === 'string') return {
        text:value.text,
        replyTo:value.replyTo && typeof value.replyTo.msgId === 'string' ? {msgId:value.replyTo.msgId.slice(0,256),excerpt:String(value.replyTo.excerpt || '').slice(0,180)} : null,
        mentions:Array.isArray(value.mentions) ? value.mentions.slice(0,2).filter(m=>m&&typeof m.id==='string'&&typeof m.name==='string'&&m.name.length<=128) : []
      };
      const labels = {image:'Imagen',video:'Vídeo',audio:'Nota de voz',file:'Archivo',file_offer:'Archivo'};
      return {text:labels[value.type] ? `${labels[value.type]}${value.name ? ': '+String(value.name).slice(0,100) : ''}` : raw,mentions:[]};
    } catch { return {text:raw,mentions:[]}; }
  }
  function states(peerId, messageId) { return (snapshots.get(peerId)?.states || []).filter(state=>state.msgId===messageId); }
  function pinned(peerId, messageId) {
    return states(peerId,messageId).filter(state=>state.kind==='pin').sort((a,b)=>b.revision-a.revision||Number(b.active)-Number(a.active))[0]?.active === true;
  }
  async function refresh(peerId = current()) {
    if (!peerId || peerId==='global') return;
    if (loads.has(peerId)) {dirty.add(peerId);return loads.get(peerId);}
    const version=revisions.get(peerId)||0;
    const task = (async () => {
      try {
        const snapshot = await QP.api('/api/chat/interactions?peerId='+encodeURIComponent(peerId));
        if ((revisions.get(peerId)||0)!==version) {dirty.add(peerId);return;}
        const previous = snapshots.get(peerId);
        snapshots.set(peerId,snapshot);
        if (JSON.stringify(previous)!==JSON.stringify(snapshot)) { revisions.set(peerId,(revisions.get(peerId)||0)+1); if (peerId===current()) bridge.render(); }
        if (peerId===current()) renderBars();
      } catch (error) { console.warn('Could not load message actions:',error); }
    })();
    loads.set(peerId,task); try { await task; } finally { loads.delete(peerId);if(dirty.delete(peerId))void refresh(peerId); }
  }
  async function change(item, kind, key, active, button) {
    if (button) button.disabled=true;
    try {
      const snapshot = await QP.api('/api/chat/interactions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({peerId:item.peerId,msgId:item.msgId,kind,key,active})});
      snapshots.set(item.peerId,snapshot); revisions.set(item.peerId,(revisions.get(item.peerId)||0)+1);
      if (item.peerId===current()) { bridge.render(); renderBars(); }
    } catch (error) { QP.toast(error.message,'error'); }
    finally { if (button?.isConnected) button.disabled=false; }
  }
  function reply(item) {
    replies.set(item.peerId,{msgId:item.msgId,excerpt:decode(item.message).text.slice(0,180),author:item.isMe?'Tú':item.sender});
    renderBars(); $('messageInput').focus();
  }
  function encode(text, peerId, replyTo) {
    const contact = (bridge?.status()?.peers || []).find(p=>p.id===peerId) || (bridge?.status()?.savedPeers || []).find(p=>p.id===peerId);
    const candidate=contact?.name ? {id:peerId,name:contact.name} : null;
    const mentions = candidate&&QuicNetMarkdown.render(text,{mentions:[candidate]}).includes('class="chat-mention"') ? [candidate] : [];
    return replyTo || mentions.length ? JSON.stringify({type:'text',text,replyTo:replyTo ? {msgId:replyTo.msgId,excerpt:replyTo.excerpt} : null,mentions}) : text;
  }
  function renderBars() {
    if (!bridge) return;
    const peerId=current(), replyTo=replies.get(peerId), pins=snapshots.get(peerId)?.pins || [];
    $('chatReplyBar').classList.toggle('hidden',!replyTo);
    $('chatReplyAuthor').textContent=replyTo ? `Respondiendo a ${replyTo.author || 'un mensaje'}` : '';
    $('chatReplyExcerpt').textContent=replyTo?.excerpt || '';
    $('chatPinnedBar').classList.toggle('hidden',!pins.length);
    $('chatPinnedCount').textContent=`${pins.length} ${pins.length===1?'mensaje fijado':'mensajes fijados'}`;
  }
  async function jump(peerId, messageId) {
    if (peerId!==current()) return;
    let target=[...$('messages').querySelectorAll('[data-message-id]')].find(row=>row.dataset.messageId===messageId);
    if (!target) {
      try {
        const response=await QP.api(`/api/chat/message?peerId=${encodeURIComponent(peerId)}&msgId=${encodeURIComponent(messageId)}`);
        if (current()!==peerId) return;
        bridge.merge([response.message]); bridge.render();
        target=[...$('messages').querySelectorAll('[data-message-id]')].find(row=>row.dataset.messageId===messageId);
      } catch { QP.toast('Este mensaje ya no está disponible.'); return; }
    }
    if (!target) return;
    const box=$('messages'), rect=target.getBoundingClientRect(), viewport=box.getBoundingClientRect();
    box.scrollTo({top:box.scrollTop+rect.top-viewport.top-Math.max(0,(viewport.height-rect.height)/2),behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'instant':'smooth'});
    target.classList.add('message-highlight'); setTimeout(()=>target.classList.remove('message-highlight'),1500);
  }
  function decorate(item, body) {
    const data=decode(item.message), peerId=item.peerId;
    if (data.replyTo) {
      const target=(bridge?.status()?.chatMessages || []).find(m=>m.peerId===peerId&&m.msgId===data.replyTo.msgId);
      const quote=QP.button('', 'message-quote',()=>jump(peerId,data.replyTo.msgId));
      quote.append(QP.el('strong','',target ? target.isMe?'Tú':target.sender : 'Mensaje citado'),QP.el('span','',data.replyTo.excerpt));
      quote.setAttribute('aria-label','Ir al mensaje citado'); body.querySelector('.message').prepend(quote);
    }
    if (pinned(peerId,item.msgId)) body.querySelector('.message-meta').prepend(QP.el('span','message-pin-label','📌 Fijado'));
    const actions=states(peerId,item.msgId).filter(state=>state.kind==='reaction'&&state.active);
    if (actions.length) {
      const bar=QP.el('div','message-reactions'); bar.setAttribute('aria-label','Reacciones al mensaje');
      for (const symbol of emoji) {
        const users=actions.filter(state=>state.key===symbol); if (!users.length) continue;
        const mine=users.some(state=>state.mine);
        const button=QP.button(`${symbol} ${users.length}`,`reaction-chip${mine?' mine':''}`,()=>change(item,'reaction',symbol,!mine,button));
        button.setAttribute('aria-pressed',String(mine)); button.setAttribute('aria-label',`${mine?'Quitar':'Añadir'} reacción ${symbol}, ${users.length}`);bar.append(button);
      }
      body.append(bar);
    }
    if (!item.isMe&&data.mentions.length) body.querySelector('.message').classList.add('mentioned');
  }
  function menu(item, parent) {
    const fragment=document.createDocumentFragment(), enabled=item.peerId!=='global'&&(!item.deliveryState||item.deliveryState==='sent');
    const answer=QP.button('Responder','',()=>{parent.open=false;reply(item);}); answer.disabled=!enabled;
    const pin=QP.button(pinned(item.peerId,item.msgId)?'Desfijar mensaje':'Fijar mensaje','',()=>{parent.open=false;change(item,'pin','',!pinned(item.peerId,item.msgId));});pin.disabled=!enabled;
    const choices=QP.el('div','message-reaction-picker hidden');choices.setAttribute('aria-label','Elegir reacción');
    const react=QP.button('Añadir reacción','',()=>{choices.classList.toggle('hidden');parent.dispatchEvent(new Event('toggle'));});react.disabled=!enabled;
    for (const symbol of emoji) {
      const button=QP.button(symbol,'',()=>{parent.open=false;change(item,'reaction',symbol,!states(item.peerId,item.msgId).some(state=>state.kind==='reaction'&&state.key===symbol&&state.mine&&state.active));});
      button.setAttribute('aria-label','Reaccionar con '+symbol);choices.append(button);
    }
    fragment.append(answer,pin,react,choices);return fragment;
  }
  function showPins() {
    const peerId=current(), dialog=QP.el('dialog','chat-pins-dialog');dialog.setAttribute('aria-label','Mensajes fijados');
    dialog.append(QP.el('h2','','Mensajes fijados'));
    const pins=snapshots.get(peerId)?.pins || [];
    for (const item of pins) {
      const row=QP.el('div','chat-pin-row');
      const open=QP.button('', '',()=>{dialog.close();jump(peerId,item.msgId);});open.append(QP.el('strong','',item.isMe?'Tú':item.sender),QP.el('span','',decode(item.message).text.slice(0,180)));open.title='Ir al mensaje';
      const remove=QP.button('×','',()=>{dialog.close();change(item,'pin','',false);});remove.setAttribute('aria-label','Desfijar mensaje');row.append(open,remove);dialog.append(row);
    }
    if (!pins.length) dialog.append(QP.el('p','','No hay mensajes fijados.'));
    dialog.append(QP.button('Cerrar','',()=>dialog.close()));document.body.append(dialog);
    dialog.addEventListener('close',()=>{dialog.remove();$('chatPinnedButton').focus();},{once:true});dialog.showModal();
  }
  function suggestMention() {
    if (!bridge) return;
    const input=$('messageInput'), cursor=input.selectionStart ?? input.value.length;
    const match=input.value.slice(0,cursor).match(/(?:^|\s)@([^@\n]{0,128})$/);
    const contact=[...(bridge.status()?.peers || []),...(bridge.status()?.savedPeers || [])].find(p=>p.id===current());
    const box=$('chatMentionSuggestions');box.replaceChildren();mention=null;
    if (!mentionDismissed&&match&&contact?.name&&contact.name.toLocaleLowerCase('es').startsWith(match[1].toLocaleLowerCase('es'))) {
      mention={start:cursor-match[1].length-1,end:cursor,name:contact.name};
      const button=QP.button('@'+contact.name,'',chooseMention);button.id='chatMentionOption';button.setAttribute('role','option');button.setAttribute('aria-selected','true');
      button.addEventListener('pointerdown',event=>event.preventDefault());box.append(button);
    }
    box.classList.toggle('hidden',!mention);
    if (mention) input.setAttribute('aria-activedescendant','chatMentionOption');else input.removeAttribute('aria-activedescendant');
  }
  function chooseMention() {
    if (!mention) return;
    const input=$('messageInput'), selected=mention;
    input.setRangeText('@'+selected.name+' ',selected.start,selected.end,'end');mentionDismissed=true;input.dispatchEvent(new Event('input'));input.focus();
  }
  function configure(value) {
    bridge=value;
    $('chatReplyCancel').addEventListener('click',()=>{replies.delete(current());renderBars();$('messageInput').focus();});
    $('chatPinnedButton').addEventListener('click',showPins);
    const input=$('messageInput'); input.addEventListener('input',()=>{mentionDismissed=false;suggestMention();});
    input.addEventListener('click',()=>{mentionDismissed=false;suggestMention();});
    input.addEventListener('keydown',event=>{
      if (event.isComposing) return;
      if (!mention) return;
      if (event.key==='Enter'||event.key==='Tab'&&!event.shiftKey) {event.preventDefault();event.stopImmediatePropagation();chooseMention();}
      else if (event.key==='Escape') {mentionDismissed=true;mention=null;$('chatMentionSuggestions').classList.add('hidden');input.removeAttribute('aria-activedescendant');}
      else if (event.key==='ArrowDown'||event.key==='ArrowUp') event.preventDefault();
    },true);
    input.addEventListener('blur',()=>{mention=null;$('chatMentionSuggestions').classList.add('hidden');input.removeAttribute('aria-activedescendant');});
    for (const event of ['chat_social_changed','chat_sync','chat_message_deleted','chat_cleared','chat_expired']) QP.on(event,data=>{
      if (event==='chat_message_deleted'&&replies.get(data.peerId)?.msgId===data.msgId) {replies.delete(data.peerId);renderBars();}
      const peerId=data?.peerId || current();
      if(event==='chat_sync'){clearTimeout(syncTimers.get(peerId));syncTimers.set(peerId,setTimeout(()=>{syncTimers.delete(peerId);void refresh(peerId);},150));}
      else void refresh(peerId);
    });
    select();
  }
  function select() { if (bridge) {
    if(activePeer!==current()){activePeer=current();mention=null;$('chatMentionSuggestions').classList.add('hidden');$('messageInput').removeAttribute('aria-activedescendant');}
    renderBars();void refresh();
  } }
  globalThis.ChatSocial={configure,select,decode,encode,menu,decorate,reply:peerId=>replies.get(peerId),signature:peerId=>revisions.get(peerId)||0,rowSignature:(peerId,messageId)=>JSON.stringify(states(peerId,messageId)),
    restore(item){const data=decode(item.message);if(data.replyTo)replies.set(item.peerId,{...data.replyTo,author:'un mensaje'});if(item.peerId===current())renderBars();return data.text;},
    sent(peerId,original){if(replies.get(peerId)===original){replies.delete(peerId);if(peerId===current())renderBars();}}};
})();
