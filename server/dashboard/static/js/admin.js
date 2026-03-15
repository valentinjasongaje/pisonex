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

// ── DELETE helper ─────────────────────────────────────────────────────────────
async function apiDelete(url) {
  const res = await fetch(url, { method: 'DELETE' });
  if (res.status === 401) { window.location.href = '/dashboard/login'; return null; }
  return res;
}

// ── Coin slot state ───────────────────────────────────────────────────────────
let _coinGlobal = true;
let _coinPc = {};

async function initCoinSlotStates() {
  try {
    const res = await fetch('/dashboard/api/hardware/coin-slot');
    if (!res.ok) return;
    const data = await res.json();
    _coinGlobal = data.global_enabled;
    _coinPc = data.per_pc || {};
    _applyGlobalCoinBtn(_coinGlobal);
    Object.entries(_coinPc).forEach(([pc, on]) => _applyPcCoinBtn(parseInt(pc), on));
    updateAnnouncementBanner(data.announcement);
  } catch(e) {}
}

function _applyGlobalCoinBtn(on) {
  const btn = document.getElementById('global-coin-btn');
  if (!btn) return;
  btn.textContent = `🪙 Coins: ${on ? 'ON' : 'OFF'}`;
  btn.className = `btn btn-sm coin-toggle-btn ${on ? 'coin-enabled' : 'coin-disabled'}`;
}

function _applyPcCoinBtn(pcNumber, on) {
  const btn = document.getElementById(`pc-coin-btn-${pcNumber}`);
  if (!btn) return;
  btn.textContent = on ? 'ON' : 'OFF';
  btn.className = `btn btn-sm coin-toggle-btn ${on ? 'coin-enabled' : 'coin-disabled'}`;
}

async function toggleGlobalCoinSlot() {
  const newVal = !_coinGlobal;
  if (!confirm(`${newVal ? 'Enable' : 'Disable'} coin slot for all PCs?`)) return;
  const res = await apiPost('/dashboard/api/hardware/coin-slot', { enabled: newVal });
  if (!res) return;
  if (res.ok) {
    _coinGlobal = newVal;
    _applyGlobalCoinBtn(_coinGlobal);
    showToast(`Coin slot ${newVal ? 'enabled' : 'disabled'} globally`, 'success');
  } else {
    showToast('Failed to update coin slot', 'error');
  }
}

async function togglePcCoinSlot(pcNumber) {
  const current = _coinPc[pcNumber] !== false;
  const newVal = !current;
  const res = await apiPost(`/dashboard/api/pc/${pcNumber}/coin-slot`, { enabled: newVal });
  if (!res) return;
  if (res.ok) {
    _coinPc[pcNumber] = newVal;
    _applyPcCoinBtn(pcNumber, newVal);
    showToast(`PC ${String(pcNumber).padStart(2,'0')} coins ${newVal ? 'enabled' : 'disabled'}`, 'success');
  } else {
    showToast('Failed to update coin slot', 'error');
  }
}

// ── Announcement ──────────────────────────────────────────────────────────────
function openAnnouncementModal() {
  const inp = document.getElementById('announcement-text');
  if (inp) inp.value = '';
  const modal = document.getElementById('announcement-modal');
  if (modal) { modal.classList.remove('hidden'); if (inp) setTimeout(() => inp.focus(), 50); }
}
function closeAnnouncementModal() {
  const m = document.getElementById('announcement-modal');
  if (m) m.classList.add('hidden');
}
async function clearAnnouncement() {
  const res = await apiDelete('/dashboard/api/announcement');
  if (res && res.ok) {
    showToast('Announcement cleared', 'success');
    updateAnnouncementBanner(null);
  }
}
function updateAnnouncementBanner(text) {
  const banner = document.getElementById('announcement-banner');
  if (!banner) return;
  if (text) {
    banner.querySelector('span').textContent = `📢 ${text}`;
    banner.style.display = 'flex';
  } else {
    banner.style.display = 'none';
  }
}

// ── Message modal ─────────────────────────────────────────────────────────────
let _msgPc = null;
function openMessageModal(pcNumber) {
  _msgPc = pcNumber;
  const lbl = document.getElementById('message-pc-label');
  const txt = document.getElementById('message-text');
  if (lbl) lbl.textContent = `PC ${String(pcNumber).padStart(2,'0')}`;
  if (txt) { txt.value = ''; setTimeout(() => txt.focus(), 50); }
  const m = document.getElementById('message-modal');
  if (m) m.classList.remove('hidden');
}
function closeMessageModal() {
  const m = document.getElementById('message-modal');
  if (m) m.classList.add('hidden');
  _msgPc = null;
}

// ── Open URL modal ────────────────────────────────────────────────────────────
let _urlPc = null;
function openUrlModal(pcNumber) {
  _urlPc = pcNumber;
  closeAllCmdMenus();
  const lbl = document.getElementById('url-pc-label');
  const inp = document.getElementById('url-input');
  if (lbl) lbl.textContent = `PC ${String(pcNumber).padStart(2,'0')}`;
  if (inp) { inp.value = ''; setTimeout(() => inp.focus(), 50); }
  const m = document.getElementById('url-modal');
  if (m) m.classList.remove('hidden');
}
function closeUrlModal() {
  const m = document.getElementById('url-modal');
  if (m) m.classList.add('hidden');
  _urlPc = null;
}

// ── Command dropdown ──────────────────────────────────────────────────────────
function closeAllCmdMenus() {
  document.querySelectorAll('.cmd-menu').forEach(m => m.classList.add('hidden'));
}
function toggleCmdDropdown(pcNumber) {
  const menu = document.getElementById(`cmd-menu-${pcNumber}`);
  if (!menu) return;
  const isOpen = !menu.classList.contains('hidden');
  closeAllCmdMenus();
  if (!isOpen) menu.classList.remove('hidden');
}
document.addEventListener('click', e => {
  if (!e.target.closest('.cmd-dropdown')) closeAllCmdMenus();
});

async function sendCommand(pcNumber, type, payload = '') {
  closeAllCmdMenus();
  const labels = { shutdown: 'Shutdown', restart: 'Restart', lock: 'Lock' };
  const label = labels[type] || type;
  if (!confirm(`Send "${label}" to PC ${String(pcNumber).padStart(2,'0')}?`)) return;
  const res = await apiPost(`/dashboard/api/pc/${pcNumber}/command`, { type, payload });
  if (!res) return;
  if (res.ok) {
    showToast(`${label} sent to PC ${String(pcNumber).padStart(2,'0')}`, 'success');
  } else {
    const err = await res.json();
    showToast(err.detail || 'Failed to send command', 'error');
  }
}

// ── Wire forms (no-ops when elements are absent on the current page) ──────────
document.addEventListener('DOMContentLoaded', () => {
  initCoinSlotStates();

  // Announcement form
  const annForm = document.getElementById('announcement-form');
  if (annForm) {
    annForm.addEventListener('submit', async e => {
      e.preventDefault();
      const text = document.getElementById('announcement-text').value.trim();
      if (!text) return;
      const res = await apiPost('/dashboard/api/announcement', { text });
      if (!res) return;
      if (res.ok) {
        closeAnnouncementModal();
        showToast('Announcement sent to all PCs', 'success');
        updateAnnouncementBanner(text);
      } else {
        const err = await res.json();
        showToast(err.detail || 'Failed to send announcement', 'error');
      }
    });
    document.getElementById('announcement-modal').addEventListener('click', e => {
      if (e.target.id === 'announcement-modal') closeAnnouncementModal();
    });
  }

  // Message form
  const msgForm = document.getElementById('message-form');
  if (msgForm) {
    msgForm.addEventListener('submit', async e => {
      e.preventDefault();
      const text = document.getElementById('message-text').value.trim();
      if (!text || _msgPc === null) return;
      const res = await apiPost(`/dashboard/api/pc/${_msgPc}/message`, { text });
      if (!res) return;
      if (res.ok) {
        closeMessageModal();
        showToast(`Message sent to PC ${String(_msgPc).padStart(2,'0')}`, 'success');
      } else {
        const err = await res.json();
        showToast(err.detail || 'Failed to send message', 'error');
      }
    });
    document.getElementById('message-modal').addEventListener('click', e => {
      if (e.target.id === 'message-modal') closeMessageModal();
    });
  }

  // Open URL form
  const urlForm = document.getElementById('url-form');
  if (urlForm) {
    urlForm.addEventListener('submit', async e => {
      e.preventDefault();
      const url = document.getElementById('url-input').value.trim();
      if (!url || _urlPc === null) return;
      const res = await apiPost(`/dashboard/api/pc/${_urlPc}/command`, { type: 'open_url', payload: url });
      if (!res) return;
      if (res.ok) {
        closeUrlModal();
        showToast(`Open URL sent to PC ${String(_urlPc).padStart(2,'0')}`, 'success');
      } else {
        const err = await res.json();
        showToast(err.detail || 'Failed to send URL command', 'error');
      }
    });
    document.getElementById('url-modal').addEventListener('click', e => {
      if (e.target.id === 'url-modal') closeUrlModal();
    });
  }
});
