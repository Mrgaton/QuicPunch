(() => {
  let pending = null;
  QP.confirmTorDiscoveryRisk = () => {
    if (pending) return pending;
    pending = new Promise(resolve => {
      const opener = document.activeElement;
      let accepted = false;
      const dialog = QP.el('dialog', 'tor-discovery-warning');
      dialog.setAttribute('aria-labelledby', 'torDiscoveryRiskTitle');
      dialog.setAttribute('aria-describedby', 'torDiscoveryRiskText');
      const title = QP.el('h2', '', 'El descubrimiento Tor puede revelar tu identidad');
      title.id = 'torDiscoveryRiskTitle';
      const text = QP.el('p', '', 'Publicarás tu dirección .onion y el nombre del dispositivo en Nostr. Al compararlos con los nombres y horarios de tus anuncios WAN, un observador puede vincular tu identidad Tor con tu IP pública. Usar claves distintas no impide esa correlación. Los relays pueden conservar los anuncios incluso después de desactivarlo.');
      text.id = 'torDiscoveryRiskText';
      const advice = QP.el('p', 'tor-discovery-advice', 'Si necesitas anonimato, mantenlo desactivado y comparte tu invitación Tor por un canal de confianza.');
      const label = QP.el('label', 'tor-discovery-acknowledgement');
      const checkbox = QP.el('input'); checkbox.type = 'checkbox';
      label.append(checkbox, QP.el('span', '', 'He leído el aviso y acepto el riesgo de perder el anonimato.'));
      const actions = QP.el('div', 'tor-discovery-warning-actions');
      const cancel = QP.button('Mantener desactivado', '', () => dialog.close());
      const enable = QP.button('Activar descubrimiento Tor', 'danger', () => {
        if (!checkbox.checked) return;
        accepted = true; dialog.close();
      });
      enable.disabled = true;
      checkbox.addEventListener('change', () => { enable.disabled = !checkbox.checked; });
      actions.append(cancel, enable); dialog.append(title, text, advice, label, actions);
      dialog.addEventListener('close', () => {
        dialog.remove(); pending = null;
        if (opener?.isConnected) opener.focus({preventScroll:true});
        resolve(accepted);
      }, {once:true});
      document.body.append(dialog); dialog.showModal(); cancel.focus();
    });
    return pending;
  };
})();
