(function () {
  'use strict';
  var el = document.getElementById('map');
  if (!el || typeof L === 'undefined') return;
  L.Icon.Default.imagePath = '/lib/leaflet/images/';
  var map = L.map('map').setView([28.0, 2.5], 5); // Algeria
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors' }).addTo(map);
  var cluster = L.markerClusterGroup(); map.addLayer(cluster);
  var form = document.getElementById('mapfilters'), info = document.getElementById('mapinfo');

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }

  function load() {
    var qs = new URLSearchParams();
    new FormData(form).forEach(function (v, k) { if (v) qs.append(k, v); });
    info.textContent = 'Chargement…';
    fetch('/ui/map-points?' + qs.toString(), { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (d) {
        cluster.clearLayers();
        var bounds = [];
        d.points.forEach(function (p) {
          var m = L.marker([p.lat, p.lon], { title: p.name });
          m.bindPopup('<strong><a href="/Businesses/Details/' + encodeURIComponent(p.id) + '">' + esc(p.name) + '</a></strong><br>' + esc(p.category || '') + (p.commune ? ' · ' + esc(p.commune) : '') + '<br><small>' + esc(p.census) + ' · ' + esc(p.processing) + '</small>');
          cluster.addLayer(m); bounds.push([p.lat, p.lon]);
        });
        if (bounds.length) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 15 });
        info.textContent = d.shown + ' point(s) affiché(s)' + (d.total > d.shown ? ' sur ' + d.total + ' (affinez les filtres)' : '') + (d.shown === 0 ? ' — aucune entreprise avec coordonnées pour ces filtres' : '');
      })
      .catch(function () { info.textContent = 'Impossible de charger les points.'; });
  }

  form.addEventListener('submit', function (e) { e.preventDefault(); load(); });
  load();
})();
