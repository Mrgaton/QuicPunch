document.addEventListener('DOMContentLoaded', () => {
  const dialog = document.getElementById('mobileToolsDialog');
  if (!dialog) return;
  const links = [...dialog.querySelectorAll('a[data-page]')];
  const more = document.getElementById('navMore');
  let opener;
  let backdropPress = false;

  function focusVisible(...targets) {
    const target = targets.find(node => node?.isConnected && node.checkVisibility());
    target?.focus({ preventScroll: true });
  }

  function pageHeading() {
    const heading = document.querySelector('.spa-view:not(.hidden) h1');
    if (heading) heading.tabIndex = -1;
    return heading;
  }

  function restoreFocus(trigger) {
    focusVisible(trigger, trigger?.closest('details')?.querySelector('summary'), more,
      document.querySelector('.nav a[aria-current="page"]'), document.getElementById('openMobileTools'), pageHeading());
  }

  function updateCurrentPage() {
    const page = document.body.dataset.page;
    const current = links.find(link => link.dataset.page === page);
    for (const link of links) {
      if (link === current) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
    }
    more.classList.toggle('active', !!current);
    if (current) more.setAttribute('aria-description', `Pantalla actual: ${current.querySelector('strong').textContent}`);
    else more.removeAttribute('aria-description');
  }
  function openTools(event) {
    if (dialog.open) return;
    opener = event.currentTarget;
    const menu = opener.closest('details');
    if (menu) menu.open = false;
    dialog.showModal();
    dialog.querySelector('.mobile-tools-list').scrollTop = 0;
  }

  function closeTools(restore = true) {
    if (!dialog.open) return false;
    const trigger = opener;
    opener = null;
    dialog.close();
    if (restore) restoreFocus(trigger);
    return true;
  }

  document.querySelectorAll('[data-open-tools]').forEach(button => button.addEventListener('click', openTools));
  document.getElementById('closeMobileTools').addEventListener('click', () => closeTools());
  dialog.addEventListener('cancel', event => {
    event.preventDefault();
    closeTools();
  });
  dialog.addEventListener('pointerdown', event => { backdropPress = event.target === dialog; });
  dialog.addEventListener('click', event => {
    if (!backdropPress || event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (event.clientX < bounds.left || event.clientX >= bounds.right ||
        event.clientY < bounds.top || event.clientY >= bounds.bottom) closeTools();
  });
  QP.dismissMobileTools = () => closeTools();
  document.getElementById('mobilePresenceButton')?.addEventListener('click', () => {
    const trigger = opener;
    closeTools();
    QP.openUserPresence(() => restoreFocus(trigger));
  });
  for (const link of links) {
    link.addEventListener('click', event => {
      // Keep the current dialog open when opening a tool in a separate tab.
      if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      if (link.getAttribute('aria-current') === 'page') {
        event.preventDefault();
        event.stopPropagation();
        closeTools();
        return;
      }
      closeTools(false);
    });
  }
  QP.on('view_changed', () => {
    updateCurrentPage();
    if (closeTools(false)) focusVisible(pageHeading());
  });
  updateCurrentPage();
});
