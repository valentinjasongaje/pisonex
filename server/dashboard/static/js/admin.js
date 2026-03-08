/* PisoNet Admin Dashboard — client-side logic */

// ── Active nav link ───────────────────────────────────────────────────────────
(function markActiveNav() {
  const path = window.location.pathname;
  document.querySelectorAll('.nav-link').forEach(link => {
    if (link.getAttribute('href') === path ||
        (path === '/dashboard' && link.getAttribute('href') === '/dashboard')) {
      link.classList.add('active');
    }
  });
})();

// ── Add Time Modal ────────────────────────────────────────────────────────────
function openAddTime(pcNumber) {
  document.getElementById('modal-pc-label').textContent = `PC ${String(pcNumber).padStart(2, '0')}`;
  document.getElementById('modal-pc-number').value = pcNumber;
  document.getElementById('add-time-modal').classList.remove('hidden');
}

function closeModal() {
  document.getElementById('add-time-modal').classList.add('hidden');
}

document.addEventListener('DOMContentLoaded', () => {
  const form = document.getElementById('add-time-form');
  if (!form) return;

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const pcNumber = form.querySelector('[name="pc_number"]').value;
    const minutes  = form.querySelector('[name="minutes"]').value;

    const res = await fetch('/api/admin/pc/add-time', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pc_number: parseInt(pcNumber), minutes: parseInt(minutes) }),
    });

    if (res.ok) {
      closeModal();
      showToast(`Added ${minutes} minutes to PC ${String(pcNumber).padStart(2, '0')}`, 'success');
      // Trigger HTMX grid refresh
      htmx.trigger('#pc-grid', 'refresh');
    } else {
      const err = await res.json();
      showToast(err.detail || 'Failed to add time', 'error');
    }
  });

  // Close modal on backdrop click
  document.getElementById('add-time-modal').addEventListener('click', (e) => {
    if (e.target.id === 'add-time-modal') closeModal();
  });
});

// ── Toast notifications ───────────────────────────────────────────────────────
function showToast(message, type = 'success') {
  const toast = document.createElement('div');
  toast.className = `toast toast-${type}`;
  toast.textContent = message;
  document.body.appendChild(toast);

  requestAnimationFrame(() => toast.classList.add('toast-visible'));
  setTimeout(() => {
    toast.classList.remove('toast-visible');
    setTimeout(() => toast.remove(), 300);
  }, 3000);
}

// Toast styles injected dynamically (keeps CSS file clean)
const toastStyle = document.createElement('style');
toastStyle.textContent = `
  .toast {
    position: fixed; bottom: 24px; right: 24px;
    padding: 12px 20px; border-radius: 8px; font-size: 14px; font-weight: 600;
    opacity: 0; transform: translateY(10px);
    transition: opacity 0.25s, transform 0.25s;
    z-index: 9999;
  }
  .toast-visible { opacity: 1; transform: translateY(0); }
  .toast-success { background: #22c55e; color: #000; }
  .toast-error   { background: #ef4444; color: #fff; }
`;
document.head.appendChild(toastStyle);
