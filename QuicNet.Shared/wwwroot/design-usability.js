document.addEventListener('DOMContentLoaded', () => {
  const dialog = document.getElementById('mobileToolsDialog');
  if (!dialog) return;
  const links = [...dialog.querySelectorAll('a[data-page]')];
  function updateCurrentPage() {
    for (const link of links) {
      if (link.dataset.page === document.body.dataset.page) link.setAttribute('aria-current', 'page');
      else link.removeAttribute('aria-current');
    }
  }
  function openTools() {
    updateCurrentPage();
    if (!dialog.open) dialog.showModal();
  }
  document.getElementById('openMobileTools')?.addEventListener('click', openTools);
  document.querySelectorAll('[data-open-tools]').forEach(button => button.addEventListener('click', openTools));
  document.getElementById('closeMobileTools')?.addEventListener('click', () => dialog.close());
  for (const link of links) {
    link.addEventListener('click', event => {
      // Keep the current dialog open when opening a tool in a separate tab.
      if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      dialog.close();
    });
  }
  window.QP?.on('view_changed', updateCurrentPage);
  updateCurrentPage();
});
