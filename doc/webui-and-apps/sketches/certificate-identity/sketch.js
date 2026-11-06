/* Only controls the sketch. No application state, network or cryptography. */
(() => {
  const root = document.getElementById('identity-library');
  const samples = [...root.querySelectorAll('[data-person]')];
  const initialDark = matchMedia('(prefers-color-scheme: dark)').matches;
  let selected = 'seda';
  let view = 'library';
  let theme = initialDark ? 'dark' : 'light';
  const appearance = {tint: 6, texture: 20};

  function save() {
    if (window.openai?.setWidgetState) {
      window.openai.setWidgetState({modelContent: {selectedFamily: selected, view, theme}, privateContent: null}).catch(() => {});
    }
  }
  function render() {
    const sample = samples.find(item => item.dataset.person === selected) || samples[0];
    selected = sample.dataset.person;
    samples.forEach(item => item.setAttribute('aria-pressed', String(item === sample)));
    for (const slot of root.querySelectorAll('[data-selected-slot]')) {
      const capsule = sample.querySelector('.ci').cloneNode(true);
      capsule.dataset.size = slot.dataset.selectedSlot;
      slot.replaceChildren(capsule);
    }
    root.querySelector('[data-family-label]').textContent = sample.querySelector('.sk-caption').textContent.split(' · ')[0];
    root.querySelectorAll('.ci').forEach(capsule => {
      capsule.style.setProperty('--ci-tint', appearance.tint + '%');
      capsule.style.setProperty('--ci-pattern-alpha', String(appearance.texture / 100));
    });
    root.dataset.theme = theme;
    const themeButton = root.querySelector('[data-theme-toggle]');
    themeButton.textContent = theme === 'dark' ? 'Tema claro' : 'Tema oscuro';
    themeButton.setAttribute('aria-label', theme === 'dark' ? 'Cambiar a tema claro' : 'Cambiar a tema oscuro');
    root.querySelectorAll('[data-view]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.view === view)));
    root.querySelector('#ci-library-panel').hidden = view !== 'library';
    root.querySelector('#ci-context-panel').hidden = view !== 'context';
  }
  function restore(state) {
    const values = state?.modelContent;
    if (samples.some(item => item.dataset.person === values?.selectedFamily)) selected = values.selectedFamily;
    if (['library', 'context'].includes(values?.view)) view = values.view;
    if (['light', 'dark'].includes(values?.theme)) theme = values.theme;
    render();
  }
  samples.forEach(sample => sample.addEventListener('click', () => {selected = sample.dataset.person; render(); save();}));
  root.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {view = button.dataset.view; render(); save();}));
  root.querySelector('[data-theme-toggle]').addEventListener('click', () => {theme = theme === 'dark' ? 'light' : 'dark'; render(); save();});
  root.querySelectorAll('[data-call-action]').forEach(button => button.addEventListener('click', () => {
    const result = root.querySelector('.sk-demo-result');
    result.textContent = button.dataset.callAction + ' · simulación del sketch';
    result.hidden = false;
  }));
  restore(window.openai?.widgetState);
  window.addEventListener('openai:set_globals', event => {
    if (event.detail?.globals?.widgetState) restore(event.detail.globals.widgetState);
  });
  if (globalThis.Tweak) {
    const tweak = new Tweak({container: root, onChange: render});
    tweak.addSlider(appearance, 'tint', {label: 'Intensidad del relleno', min: 2, max: 14, unit: '%'});
    tweak.addSlider(appearance, 'texture', {label: 'Presencia de la textura', min: 8, max: 35, unit: '%'});
  }
})();
