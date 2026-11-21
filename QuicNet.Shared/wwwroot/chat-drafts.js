function createChatDrafts({api, notify, onLoaded, newRevision}) {
  const conversationDrafts = new Map();

  function draftFor(peerId) {
    if (!conversationDrafts.has(peerId)) conversationDrafts.set(peerId, {
      text: '', revision: null, edit: 0, savedEdit: 0, loaded: false, loading: null, saving: null, timer: null
    });
    return conversationDrafts.get(peerId);
  }

  function draftUrl(peerId) { return '/api/chat/draft?peerId=' + encodeURIComponent(peerId); }

  function load(peerId) {
    const draft = draftFor(peerId);
    if (draft.loaded) return Promise.resolve();
    if (draft.loading) return draft.loading;
    draft.loading = api(draftUrl(peerId)).then(result => {
      if (typeof result.text !== 'string') throw new Error('No se pudo leer el borrador.');
      draft.loaded = true;
      draft.revision = result.revision ?? null;
      if (draft.edit !== 0) return;
      draft.text = result.text;
      onLoaded(peerId, draft.text);
    }).finally(() => { draft.loading = null; });
    return draft.loading;
  }

  function edit(peerId, text, immediately = false) {
    if (!peerId) return;
    const draft = draftFor(peerId);
    if (draft.text !== text) {
      draft.text = text;
      draft.edit++;
    }
    clearTimeout(draft.timer);
    if (immediately) void save(peerId);
    else draft.timer = setTimeout(() => save(peerId), 250);
  }

  async function save(peerId, leaving = false) {
    if (!peerId || !conversationDrafts.has(peerId)) return;
    const draft = draftFor(peerId);
    clearTimeout(draft.timer);
    if (draft.saving || draft.savedEdit === draft.edit) return;
    draft.saving = (async () => {
      await load(peerId);
      const edit = draft.edit;
      const nextRevision = newRevision();
      const body = JSON.stringify({ text: draft.text, revision: draft.revision, nextRevision });
      const response = await api(draftUrl(peerId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body,
        keepalive: leaving && new TextEncoder().encode(body).length < 60000
      });
      if (response.revision !== nextRevision) {
        draft.revision = response.revision ?? null;
        draft.savedEdit = draft.edit;
        notify('El borrador cambió en otra ventana. Conservamos tu texto aquí. Edita el texto para volver a guardarlo.', 'error');
        return;
      }
      draft.revision = nextRevision;
      draft.savedEdit = edit;
    })();
    try {
      await draft.saving;
    } catch {
      notify('No se pudo guardar el borrador. El texto sigue disponible en esta ventana.', 'error');
      return;
    } finally {
      draft.saving = null;
    }
    if (draft.savedEdit !== draft.edit) void save(peerId, leaving);
  }
  return {
    load, edit, save,
    has: peerId => conversationDrafts.has(peerId),
    text: peerId => conversationDrafts.get(peerId)?.text ?? '',
    editVersion: peerId => conversationDrafts.get(peerId)?.edit,
    saveAllOnPageHide() {
      for (const peerId of conversationDrafts.keys()) void save(peerId, true);
    }
  };
}
