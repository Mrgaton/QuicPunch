/* Categories keep every control mounted, so changing sections never loses edits. */
(() => {
  document.addEventListener('DOMContentLoaded', () => {
    const root = document.getElementById('view-settings');
    const tabs = [...root.querySelectorAll('[data-settings-section]')];
    const picker = document.getElementById('settingsCategory');
    const panels = [...root.querySelectorAll('[data-settings-panel]')];
    function select(section, updateUrl = false) {
      if (!tabs.some(tab => tab.dataset.settingsSection === section)) section = 'profile';
      for (const tab of tabs) {
        const active = tab.dataset.settingsSection === section;
        tab.setAttribute('aria-selected', String(active));
        tab.tabIndex = active ? 0 : -1;
      }
      for (const panel of panels) panel.hidden = panel.dataset.settingsPanel !== section;
      picker.value = section;
      document.getElementById('appBehaviorForm').hidden = !['startup', 'notifications'].includes(section);
      document.getElementById('settingsBehaviorTitle').textContent = section === 'startup' ? 'Inicio y segundo plano' : 'Notificaciones';
      if (section === 'contacts' || section === 'diagnostics') {
        root.querySelector(`[data-settings-panel="${section}"] details`).open = true;
      }
      if (updateUrl) {
        const url = new URL(location.href);
        if (section === 'profile') url.searchParams.delete('section');
        else url.searchParams.set('section', section);
        if (url.href !== location.href) history.pushState({ page: 'settings' }, '', url);
        window.scrollTo({ top: 0, behavior: 'instant' });
      }
      QP.emit('settings_section_changed', { section });
    }
    for (const tab of tabs) {
      tab.addEventListener('click', () => select(tab.dataset.settingsSection, true));
      tab.addEventListener('keydown', event => {
        const index = tabs.indexOf(tab);
        const next = event.key === 'ArrowDown' ? (index + 1) % tabs.length :
          event.key === 'ArrowUp' ? (index + tabs.length - 1) % tabs.length :
          event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : -1;
        if (next < 0) return;
        event.preventDefault();
        tabs[next].focus();
        select(tabs[next].dataset.settingsSection, true);
      });
    }
    picker.addEventListener('change', () => select(picker.value, true));
    const fromUrl = () => {
      const query=new URLSearchParams(location.search);select(query.get('section'));
      if(query.get('section')==='diagnostics'&&query.get('tab')==='tab-activity-logs'){
        root.querySelector('[data-subtab="tab-activity-logs"]')?.click();
        const logs=document.getElementById('logs');logs.tabIndex=0;logs.focus();logs.scrollIntoView({block:'nearest'});
      }
    };
    QP.on('view_changed', ({ page }) => { if (page === 'settings') fromUrl(); });
    fromUrl();
  });
})();
