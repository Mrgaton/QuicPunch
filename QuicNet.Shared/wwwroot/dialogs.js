(() => {
  let nextId = 0;

  QP.confirmAction = ({ title, message, cancelLabel = 'Cancelar', confirmLabel, danger = false }) =>
    new Promise(resolve => {
      const opener = document.activeElement;
      const id = 'confirmAction' + ++nextId;
      const dialog = QP.el('dialog', 'confirmation-dialog');
      const heading = QP.el('h2', '', title);
      const description = QP.el('p', '', message);
      heading.id = id + 'Title';
      description.id = id + 'Description';
      dialog.setAttribute('aria-labelledby', heading.id);
      dialog.setAttribute('aria-describedby', description.id);

      const actions = QP.el('div', 'dialog-actions');
      const cancel = QP.button(cancelLabel, '', () => dialog.close());
      cancel.autofocus = true;
      actions.append(cancel, QP.button(confirmLabel, danger ? 'danger' : 'primary', () => dialog.close('confirm')));
      dialog.append(heading, description, actions);
      dialog.addEventListener('close', () => {
        dialog.remove();
        if (opener?.isConnected) opener.focus({ preventScroll: true });
        resolve(dialog.returnValue === 'confirm');
      }, { once: true });
      document.body.append(dialog);
      dialog.showModal();
    });
})();
