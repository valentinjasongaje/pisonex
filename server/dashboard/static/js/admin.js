/* PisoNet Admin Dashboard — client-side logic */

// ── Mobile sidebar toggle ─────────────────────────────────────────────────────
(function initSidebar() {
  const btn     = document.getElementById('hamburger-btn');
  const overlay = document.getElementById('sidebar-overlay');
  if (!btn) return;
  function closeSidebar() { document.body.classList.remove('sidebar-open'); }
  btn.addEventListener('click', () => document.body.classList.toggle('sidebar-open'));
  overlay.addEventListener('click', closeSidebar);
})();

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

// ── Shared auth-aware fetch ───────────────────────────────────────────────────
async function apiPost(url, body) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (res.status === 401) {
    window.location.href = '/dashboard/login';
    return null;
  }
  return res;
}

// ── Lock PC ───────────────────────────────────────────────────────────────────
async function lockPc(pcNumber) {
  if (!confirm(`Lock PC ${String(pcNumber).padStart(2, '0')}? This will end the active session.`)) return;
  const res = await apiPost(`/dashboard/api/pc/${pcNumber}/lock`, {});
  if (!res) return;
  if (res.ok) {
    showToast(`PC ${String(pcNumber).padStart(2, '0')} locked`, 'success');
    // Refresh the grid if on overview, or the whole page if on pcs
    if (document.getElementById('pc-grid')) {
      htmx.trigger('#pc-grid', 'refresh');
    } else {
      location.reload();
    }
  } else {
    const err = await res.json();
    showToast(err.detail || 'Failed to lock PC', 'error');
  }
}

// ── Rename Modal ──────────────────────────────────────────────────────────────
function openRename(pcNumber, currentName) {
  document.getElementById('rename-pc-label').textContent = `PC ${String(pcNumber).padStart(2, '0')}`;
  document.getElementById('rename-pc-number').value = pcNumber;
  document.getElementById('rename-input').value = currentName;
  document.getElementById('rename-modal').classList.remove('hidden');
  setTimeout(() => document.getElementById('rename-input').select(), 50);
}

function closeRenameModal() {
  document.getElementById('rename-modal').classList.add('hidden');
}

document.addEventListener('DOMContentLoaded', () => {
  // ── Add Time form ──────────────────────────────────────────────────────────
  const addTimeForm = document.getElementById('add-time-form');
  if (addTimeForm) {
    addTimeForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const pcNumber = addTimeForm.querySelector('[name="pc_number"]').value;
      const minutes  = addTimeForm.querySelector('[name="minutes"]').value;

      const res = await apiPost('/dashboard/api/pc/add-time', {
        pc_number: parseInt(pcNumber),
        minutes: parseInt(minutes),
      });
      if (!res) return;

      if (res.ok) {
        closeModal();
        showToast(`Added ${minutes} min to PC ${String(pcNumber).padStart(2, '0')}`, 'success');
        if (document.getElementById('pc-grid')) {
          htmx.trigger('#pc-grid', 'refresh');
        } else {
          location.reload();
        }
      } else {
        const err = await res.json();
        showToast(err.detail || 'Failed to add time', 'error');
      }
    });

    document.getElementById('add-time-modal').addEventListener('click', (e) => {
      if (e.target.id === 'add-time-modal') closeModal();
    });
  }

  // ── Rename form ────────────────────────────────────────────────────────────
  const renameForm = document.getElementById('rename-form');
  if (renameForm) {
    renameForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const pcNumber = renameForm.querySelector('[name="pc_number"]').value;
      const name     = renameForm.querySelector('[name="name"]').value.trim();

      const res = await apiPost(`/dashboard/api/pc/${pcNumber}/rename`, { name });
      if (!res) return;

      if (res.ok) {
        const data = await res.json();
        closeRenameModal();
        showToast(`PC ${String(pcNumber).padStart(2, '0')} renamed to "${data.name}"`, 'success');
        // Update the name in the table without a full reload
        const nameEl = document.getElementById(`pc-name-${pcNumber}`);
        if (nameEl) nameEl.textContent = data.name;
      } else {
        const err = await res.json();
        showToast(err.detail || 'Failed to rename PC', 'error');
      }
    });

    document.getElementById('rename-modal').addEventListener('click', (e) => {
      if (e.target.id === 'rename-modal') closeRenameModal();
    });
  }
});

// ── Real-time countdown ───────────────────────────────────────────────────────
function formatTimeHMS(sec) {
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  return `${h}h ${m}m ${String(s).padStart(2, '0')}s`;
}

(function startTimerCountdown() {
  setInterval(() => {
    if (document.hidden) return;
    document.querySelectorAll('[data-remaining-sec]').forEach(el => {
      let sec = parseInt(el.dataset.remainingSec, 10);
      if (isNaN(sec) || sec <= 0) return;
      sec -= 1;
      el.dataset.remainingSec = sec;
      el.textContent = formatTimeHMS(sec);
    });
  }, 1000);
})();

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
