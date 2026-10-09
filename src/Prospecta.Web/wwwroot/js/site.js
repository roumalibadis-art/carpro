// Progressive enhancement only: every page still works (server-side validation) without JavaScript.
(function () {
  'use strict';

  function fill(select, items, placeholder, selected) {
    select.innerHTML = '';
    var first = document.createElement('option');
    first.value = ''; first.textContent = placeholder;
    select.appendChild(first);
    items.forEach(function (i) {
      var o = document.createElement('option');
      o.value = i.id; o.textContent = i.name;
      if (selected && String(selected).toLowerCase() === String(i.id).toLowerCase()) o.selected = true;
      select.appendChild(o);
    });
  }

  function load(url, cb) {
    fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : []; }).then(cb).catch(function () { cb([]); });
  }

  // Dependent geography selects: data-geo-chain="wilaya,daira,commune,quartier" on a container.
  document.querySelectorAll('[data-geo-chain]').forEach(function (box) {
    var levels = ['Daira', 'Commune', 'Quartier'];
    var selects = box.querySelectorAll('select[data-geo]');
    selects.forEach(function (sel, idx) {
      sel.addEventListener('change', function () {
        for (var j = idx + 1; j < selects.length; j++) fill(selects[j], [], selects[j].dataset.placeholder || '—');
        if (!sel.value || idx + 1 >= selects.length) return;
        var next = selects[idx + 1];
        load('/ui/geo?level=' + levels[idx] + '&parentId=' + encodeURIComponent(sel.value), function (items) {
          fill(next, items, next.dataset.placeholder || '—');
        });
      });
    });
  });

  // Activity → sub-activity.
  document.querySelectorAll('select[data-category-root]').forEach(function (root) {
    var sub = document.getElementById(root.dataset.categoryRoot);
    if (!sub) return;
    root.addEventListener('change', function () {
      if (!root.value) { fill(sub, [], sub.dataset.placeholder || '—'); return; }
      load('/ui/categories?parentId=' + encodeURIComponent(root.value), function (items) { fill(sub, items, sub.dataset.placeholder || '—'); });
    });
  });

  // Confirmation for destructive / bulk actions.
  document.querySelectorAll('form[data-confirm],button[data-confirm]').forEach(function (el) {
    var evt = el.tagName === 'FORM' ? 'submit' : 'click';
    el.addEventListener(evt, function (e) { if (!window.confirm(el.dataset.confirm)) e.preventDefault(); });
  });

  // Select-all checkbox.
  document.querySelectorAll('input[data-select-all]').forEach(function (master) {
    master.addEventListener('change', function () {
      document.querySelectorAll(master.dataset.selectAll).forEach(function (c) { c.checked = master.checked; });
    });
  });

  // Auto-submit selects marked data-autosubmit (e.g. saved-view picker).
  document.querySelectorAll('select[data-autosubmit]').forEach(function (s) { s.addEventListener('change', function () { s.form.submit(); }); });
})();

// Configurable columns (remembered per browser; the data itself is never hidden server-side).
(function () {
  'use strict';
  var KEY = 'prospecta.cols';
  var hidden = [];
  try { hidden = JSON.parse(localStorage.getItem(KEY) || '[]'); } catch (e) { hidden = []; }
  function apply() {
    document.querySelectorAll('[data-col]').forEach(function (el) { el.style.display = hidden.indexOf(el.dataset.col) >= 0 ? 'none' : ''; });
    document.querySelectorAll('input[data-col-toggle]').forEach(function (c) { c.checked = hidden.indexOf(c.dataset.colToggle) < 0; });
  }
  document.querySelectorAll('input[data-col-toggle]').forEach(function (c) {
    c.addEventListener('change', function () {
      hidden = hidden.filter(function (h) { return h !== c.dataset.colToggle; });
      if (!c.checked) hidden.push(c.dataset.colToggle);
      try { localStorage.setItem(KEY, JSON.stringify(hidden)); } catch (e) { /* storage unavailable: still works for this page */ }
      apply();
    });
  });
  apply();
})();

// The bulk "status" picker carries the dimension of the chosen option.
(function () {
  var form = document.getElementById('bulkform');
  if (!form) return;
  var sel = form.querySelector('select[name=statusId]'), kind = form.querySelector('select[name=kind]');
  if (sel && kind) sel.addEventListener('change', function () {
    var o = sel.options[sel.selectedIndex]; if (o && o.dataset.kind) kind.value = o.dataset.kind;
  });
})();
