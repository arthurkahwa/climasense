(function () {
  let chart = null;
  const B = window.appBase || '';   // app base path: '' at root, '/ups3' under a sub-application
  const BAND_DE = { Recommended: 'Normal', Allowable: 'Zulässig', OutOfRange: 'Kritisch' };
  function setTile(id, value, band) {
    const el = document.getElementById(id);
    el.textContent = value;
    el.className = 'v band-' + band;
    el.title = 'Status: ' + (BAND_DE[band] || band); // annotate the number with its band
  }
  function renderAlerts(alerts) {
    const active = alerts.filter(a => a.endCet === null); // laufende Überschreitungen + veralteter Feed
    const panel = document.getElementById('alerts');
    panel.replaceChildren();
    if (!active.length) {
      const p = document.createElement('p'); p.className = 'muted'; p.textContent = 'Keine aktiven Warnungen.';
      panel.appendChild(p); return;
    }
    for (const a of active) {
      const div = document.createElement('div');
      div.className = 'alert band-' + a.severity;
      div.textContent = a.message;
      panel.appendChild(div);
    }
  }
  async function refresh() {
    try {
      const stale = document.getElementById('stale');
      const s = await fetch(B + '/api/readings/latest').then(r => r.ok ? r.json() : null);
      if (!s) { stale.textContent = 'Keine Daten verfügbar.'; stale.classList.add('show'); return; }
      setTile('temp', s.reading.temperatureC + ' °C', s.tempBand);
      setTile('hum', s.reading.humidityPct + ' %', s.humidityBand);
      document.getElementById('updated').textContent = 'aktualisiert vor ' + s.minutesOld + ' Min';
      stale.classList.toggle('show', s.isStale);
      stale.textContent = s.isStale ? ('Datenfeed veraltet — letzte Messung vor ' + s.minutesOld + ' Min') : '';
      const pts = await fetch(B + '/api/readings/raw?range=24h').then(r => r.ok ? r.json() : []);   // actual readings, not averages
      const labels = pts.map(p => new Date(p.timestampCet).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }));
      const datasets = [
        ClimaCharts.ds('Temperatur (°C)', pts.map(p => p.temperatureC), '#58a6ff'),
        ClimaCharts.ds('Feuchte (%)', pts.map(p => p.humidityPct), '#3fb950'),
      ];
      if (chart) { chart.data.labels = labels; chart.data.datasets = datasets; chart.update(); }
      else chart = ClimaCharts.lineChart('liveChart', labels, datasets);
      const alerts = await fetch(B + '/api/alerts?range=24h').then(r => r.ok ? r.json() : []);
      renderAlerts(alerts);
    } catch (e) { console.error(e); }
  }
  window.addEventListener('themechange', () => { if (chart) { chart.destroy(); chart = null; } refresh(); });
  refresh();
  setInterval(refresh, 60000);
})();
