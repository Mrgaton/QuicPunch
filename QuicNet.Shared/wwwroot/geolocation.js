(() => {
  document.addEventListener('DOMContentLoaded', () => {
    const output = document.getElementById('geolocationStatus');
    const connectionOutput = document.getElementById('connectionGeolocationStatus');
    const connectionResults = document.getElementById('connectionGeolocationResults');
    const frame = document.getElementById('geolocationWorkbench');
    const loading = document.getElementById('geolocationPanelLoading');
    let section;
    function showWorkbench() {
      const visible = document.body.dataset.page === 'settings' && section === 'geolocation' && !document.hidden;
      if (visible) {
        frame.hidden = false;
        if (!frame.hasAttribute('src')) { loading.hidden = false; frame.src = frame.dataset.src; }
      } else {
        frame.hidden = true;
        frame.removeAttribute('src');
        loading.hidden = false;
      }
    }
    frame.addEventListener('load', () => { if (frame.hasAttribute('src')) loading.hidden = true; });
    QP.on('settings_section_changed', event => { section = event.section; showWorkbench(); });
    QP.on('view_changed', showWorkbench);
    document.addEventListener('visibilitychange', showWorkbench);
    window.addEventListener('message', event => {
      if (event.origin !== location.origin || event.source !== frame.contentWindow || event.data?.type !== 'geolocation-height') return;
      if (Number.isFinite(event.data.height)) frame.style.height = `${Math.max(460,Math.min(3000,event.data.height))}px`;
    });
    section = new URLSearchParams(location.search).get('section'); showWorkbench();
    let current;
    function render() {
      output.textContent = current?.error || (current?.ready || current?.embeddedReady
        ? `IPTrust integrado listo${current.databaseDate ? ` · edición ${current.databaseDate}` : ''} · funciona sin descargas.`
        : 'Preparando catálogo IPTrust.');
      output.dataset.state = current?.error ? 'error' : '';
      const analysis = current?.connection;
      if (connectionOutput) {
        const pending = analysis?.pending || 0;
        const resume = analysis?.nextAllowedUtc ? new Date(analysis.nextAllowedUtc).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}) : '';
        connectionOutput.textContent = analysis?.error || (analysis?.running ? 'Analizando esta conexión…' : pending ?
          `${pending} IP pendiente${pending === 1 ? '' : 's'} · próxima ejecución a partir de ${resume}.` :
          analysis?.locations?.length ? 'Análisis guardado. Se reutilizará para esta IP.' : 'Esperando una IP pública de la conexión directa.');
        connectionOutput.dataset.state = analysis?.error ? 'error' : '';
      }
      if (connectionResults) {
        const labels = {pending:'En espera',running:'Analizando',complete:'Completado',interrupted:'Interrumpido',failed:'No se pudo completar'};
        connectionResults.replaceChildren(...(analysis?.locations || []).map(item => {
          const row = document.createElement('li');
          const published = [item.publishedCity,item.countryCode].filter(Boolean).join(', ');
          const region = Number.isFinite(item.latitude) && Number.isFinite(item.longitude) && Number.isFinite(item.radiusKm) ?
            ` · centro de zona ${item.latitude.toFixed(2)}, ${item.longitude.toFixed(2)} · radio de cobertura ${Math.ceil(item.radiusKm)} km` : '';
          row.textContent = `${item.address}: ${labels[item.state] || 'Sin datos'}${published ? ` · catálogo: ${published}` : ''}${region}` +
            (item.localitySummary ? ` · ${item.localitySummary}` : '') +
            (Number.isFinite(item.modelSpreadKm) ? ` · sensibilidad entre modelos ${Math.ceil(item.modelSpreadKm)} km` : '') +
            (item.level === 'Conflicting' ? ' · evidencias incompatibles' : '') + (item.error ? ` · ${item.error}` : '');
          return row;
        }));
      }
    }
    QP.on('status_updated', status => { current = status.geolocation; render(); });
    QP.on('ws_status', render);
    current = QP.state.status?.geolocation; render();
  });
})();
