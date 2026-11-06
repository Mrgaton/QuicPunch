/* Persist the public display name; private Tor identities remain separate. */
(() => {
  document.addEventListener('DOMContentLoaded', () => {
    const $ = id => document.getElementById(id);
    const form = $('profileForm');
    if (!form) return;
    const input = $('profileDisplayName');
    const message = $('profileSaveStatus');
    let busy = false;
    let loaded = false;
    function controls() {
      input.disabled = busy || !loaded;
      $('profileSave').disabled = busy || !loaded;
      $('profileReset').disabled = busy || !loaded;
    }
    function render(profile) {
      input.value = profile.customName;
      input.placeholder = profile.deviceName;
      $('profileDeviceName').textContent = profile.deviceName;
    }
    async function load() {
      if (busy || loaded) return;
      busy = true;
      try {
        render(await QP.api('/api/profile'));
        loaded = true;
        message.textContent = '';
      } catch (error) { message.textContent = `No se pudo consultar tu perfil: ${error.message}`; }
      finally { busy = false; controls(); }
    }
    async function save(displayName) {
      if (busy || !loaded) return;
      busy = true; controls(); message.textContent = 'Guardando…';
      try {
        const profile = await QP.api('/api/profile', { method: 'POST', body: JSON.stringify({ displayName }) });
        render(profile);
        message.textContent = `Nombre guardado: ${profile.displayName}`;
        await QP.getStatus(true);
      } catch (error) { message.textContent = error.message; }
      finally { busy = false; controls(); }
    }
    form.addEventListener('submit', event => { event.preventDefault(); save(input.value); });
    $('profileReset').addEventListener('click', () => save(''));
    QP.on('view_changed', () => { if (QP.currentPage === 'settings') load(); });
    load();
  });
})();
