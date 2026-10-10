// Copy buttons: <button data-copy="input-id">
document.querySelectorAll('[data-copy]').forEach(btn => {
  btn.addEventListener('click', async () => {
    const input = document.getElementById(btn.dataset.copy);
    if (!input) return;
    try {
      await navigator.clipboard.writeText(input.value);
      const old = btn.textContent;
      btn.textContent = 'Kopirano';
      setTimeout(() => (btn.textContent = old), 1500);
    } catch {
      input.select();
    }
  });
});

// Review screen: Ctrl+Enter (Cmd+Enter on Mac) confirms the invoice.
document.addEventListener('keydown', e => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
    const confirm = document.querySelector('#review-form button.primary');
    if (confirm) {
      e.preventDefault();
      confirm.click();
    }
  }
});
