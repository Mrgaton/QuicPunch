(() => {
  document.addEventListener('DOMContentLoaded', () => {
    const output = document.getElementById('geolocationStatus');
    if (!output) return;
    let current;
    function render() {
      output.textContent = current?.error || (current?.ready || current?.embeddedReady
        ? `IPTrust integrado listo${current.databaseDate ? ` · edición ${current.databaseDate}` : ''} · funciona sin descargas.`
        : 'Preparando catálogo IPTrust.');
      output.dataset.state = current?.error ? 'error' : '';
    }
    QP.on('status_updated', status => { current = status.geolocation; render(); });
    QP.on('ws_status', render);
    current = QP.state.status?.geolocation; render();
  });
})();
