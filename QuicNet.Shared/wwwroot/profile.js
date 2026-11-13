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
      input.disabled = !loaded;
      input.readOnly = busy;
      $('profileSave').disabled = busy || !loaded;
      $('profileReset').disabled = busy || !loaded;
      $('profileRetry')?.toggleAttribute('disabled', busy);
      form.setAttribute('aria-busy', String(busy));
      $('profileSave').textContent = busy && loaded ? 'Guardando…' : 'Guardar nombre';
    }
    function render(profile) {
      input.value = profile.customName;
      input.placeholder = profile.deviceName;
      $('profileDeviceName').textContent = profile.deviceName;
    }
    async function load() {
      if (busy || loaded) return;
      busy = true;
      controls();
      message.textContent = 'Consultando perfil…';
      try {
        render(await QP.api('/api/profile'));
        loaded = true;
        message.textContent = '';
        $('profileRetry')?.toggleAttribute('hidden', true);
      } catch (error) { message.textContent = `No se pudo consultar tu perfil: ${error.message}`; $('profileRetry')?.toggleAttribute('hidden', false); }
      finally { busy = false; controls(); }
    }
    async function save(displayName) {
      if (busy || !loaded) return;
      busy = true; controls(); message.textContent = 'Guardando…';
      try {
        const profile = await QP.api('/api/profile', { method: 'POST', body: JSON.stringify({ displayName }) });
        render(profile);
        message.textContent = `Nombre guardado: ${profile.displayName}`;
        // The profile is already persisted. A separate status refresh must not
        // turn that successful save into an apparent failure or lock the form.
        QP.getStatus(true).catch(error => QP.reportError(error, 'Actualizar el nombre en las demás vistas', 'warn'));
      } catch (error) { message.textContent = error.message; }
      finally { busy = false; controls(); }
    }
    form.addEventListener('submit', event => { event.preventDefault(); save(input.value); });
    $('profileReset').addEventListener('click', () => save(''));
    $('profileRetry')?.addEventListener('click', load);
    QP.on('settings_section_changed', ({ section }) => { if (section === 'profile') load(); });
    QP.on('view_changed', () => { if (QP.currentPage === 'settings') load(); });
    load();
  });
})();
