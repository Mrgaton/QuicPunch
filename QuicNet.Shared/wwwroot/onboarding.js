(() => {
  const byId = id => document.getElementById(id);
  const panel = document.querySelector('.onboarding');
  const form = document.querySelector('.onboarding-form');
  const stages = [...form.querySelectorAll('.onboarding-stage')];
  const steps = [...document.querySelectorAll('.onboarding-steps li')];
  const selection = form.querySelector('.onboarding-modes');
  const errorBox = form.querySelector('.onboarding-error');
  const next = byId('onboarding-next');
  const back = byId('onboarding-back');
  const reload = byId('onboarding-reload');
  const displayName = byId('onboarding-display-name');
  const startup = byId('onboarding-startup');
  const background = byId('onboarding-background');
  const modeNames = { wan: 'WAN', tor: 'Solo Tor' };
  const chosenMode = () => form.querySelector('input[name="mode"]:checked')?.value;
  if (window.__qp_is_android) {
    byId('onboarding-startup-help').textContent = 'Activa QuicNet después de reiniciar y desbloquear el dispositivo.';
  }
  let step = 0;
  let settings;
  let saving = false;

  function showError(message = '') {
    errorBox.textContent = message;
    errorBox.hidden = !message;
  }

  async function request(options) {
    const response = await fetch('/api/onboarding', options);
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || 'QuicNet no está disponible. Inténtalo de nuevo.');
    }
    return response.json();
  }

  function updatePreview() {
    const tor = chosenMode() === 'tor';
    const name = tor ? settings.torName : displayName.value.trim() || settings.deviceName;
    byId('onboarding-display-preview').textContent = name;
    byId('onboarding-identity-label').textContent = tor ? 'Tu seudónimo en Tor' : 'Tu nombre en WAN';
    byId('onboarding-initials').textContent = tor ? 'T' : Array.from(name).slice(0, 2).join('').toUpperCase();
  }

  function showStep(index, focus = true) {
    step = index;
    stages.forEach((stage, i) => {
      stage.hidden = i !== step;
      stage.classList.toggle('is-entering', i === step && focus);
    });
    steps.forEach((item, i) => {
      if (i === step || (step === 3 && i === 2)) item.setAttribute('aria-current', 'step');
      else item.removeAttribute('aria-current');
    });
    panel.dataset.onboardingPalette = step === 0 ? 'wan' : (settings.saved ? settings.mode : chosenMode());
    back.hidden = step === 0 || step === 3;
    next.textContent = step === 3 ? 'Reintentar arranque' : step === 2 ? 'Terminar y conectar' : 'Continuar';
    next.disabled = !settings || (step === 0 && !chosenMode());
    if (step === 1) updatePreview();
    if (step === 2) byId('onboarding-summary-mode').textContent = modeNames[chosenMode()];
    if (focus) stages[step].querySelector('h1').focus();
  }

  async function load() {
    reload.hidden = true;
    showError();
    try {
      settings = await request();
      if (!settings.required) { location.replace('/'); return; }
      displayName.value = settings.displayName || '';
      displayName.placeholder = settings.deviceName;
      startup.checked = settings.startWithSystem;
      background.checked = settings.runInBackground;
      startup.disabled = !settings.startupSupported;
      byId('onboarding-no-startup').hidden = settings.startupSupported;
      selection.disabled = false;
      showStep(settings.saved ? 3 : 0, !!settings.saved);
    } catch (error) {
      showError(error.message);
      reload.hidden = false;
      reload.focus();
    }
  }

  selection.addEventListener('change', () => { next.disabled = !chosenMode(); });
  displayName.addEventListener('input', updatePreview);
  back.addEventListener('click', () => { showError(); showStep(step - 1); });
  reload.addEventListener('click', load);
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (!settings || saving || (step === 0 && !chosenMode())) return;
    showError();
    if (step < 2) {
      showStep(step + 1);
      return;
    }

    saving = next.disabled = back.disabled = true;
    startup.disabled = background.disabled = true;
    next.textContent = 'Iniciando QuicNet…';
    try {
      await request({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          mode: chosenMode(), displayName: displayName.value,
          startWithSystem: startup.checked, runInBackground: background.checked
        })
      });
      location.replace('/');
    } catch (error) {
      try {
        const latest = await request();
        if (!latest.required) { location.replace('/'); return; }
        if (latest.saved) { settings = latest; showStep(3); }
      } catch { /* Keep the form available if the local server cannot be reached. */ }
      saving = back.disabled = next.disabled = background.disabled = false;
      startup.disabled = !settings.startupSupported;
      next.textContent = step === 3 ? 'Reintentar arranque' : 'Terminar y conectar';
      showError(error.message);
    }
  });

  load();
})();
