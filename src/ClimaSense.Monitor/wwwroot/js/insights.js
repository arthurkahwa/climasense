(function () {
  const DRIFT_DE = { Rising: 'Steigend', Falling: 'Fallend', Stable: 'Stabil' };
  const HEALTH_DE = { Healthy: 'OK', Stuck: 'Eingefroren', Spike: 'Ausreißer' };
  let fcChart = null, cur = '7d';
  const B = window.appBase || '';   // app base path: '' at root, '/ups3' under a sub-application
  async function load(range) {
    cur = range;
    document.querySelectorAll('.ranges button[data-r]').forEach(b => b.classList.toggle('active', b.dataset.r === range));
    const ins = await fetch(B + '/api/insights?range=' + range).then(r => r.ok ? r.json() : null);
    const psy = document.getElementById('psy');
    if (!ins) {
      psy.textContent = 'Keine Daten verfügbar.';
      document.getElementById('tempInsight').replaceChildren();
      document.getElementById('humInsight').replaceChildren();
      renderForecast(null);
      return;
    }
    psy.textContent = `Taupunkt ${deNum(ins.dewPointC)} °C · Abstand zur Kondensation ${deNum(ins.condensationMarginC)} °C`;
    psy.title = 'Bei geringem Abstand droht Kondensation an kalten Flächen';
    renderMetric('tempInsight', ins.temperature, '°C');
    renderMetric('humInsight', ins.humidity, '%');
    renderForecast(ins);
  }
  function renderMetric(id, m, unit) {
    const el = document.getElementById(id); el.replaceChildren();
    const warn = s => (s === 'Stable' || s === 'Healthy') ? '' : 'band-Allowable';
    const rows = [
      { k: 'Trend', v: DRIFT_DE[m.drift] || m.drift, cls: warn(m.drift), t: 'Richtung des Mittelwerts (jüngere vs. ältere Fensterhälfte)' },
      { k: 'Sensorzustand', v: HEALTH_DE[m.sensorHealth] || m.sensorHealth, cls: warn(m.sensorHealth), t: 'OK · Eingefroren (flach) · Ausreißer (Sprung > 10)' },
      { k: 'Anomalien im Fenster', v: String(m.anomalies.length), cls: m.anomalies.length ? 'band-Allowable' : '', t: 'Werte mehr als 2,5σ vom Mittelwert entfernt (statistisch auffällig)' },
      { k: `Prognose (nächste ${m.forecast.length})`, v: m.forecast.length ? m.forecast.map(v => deNum(v)).join(', ') + ' ' + unit : '—', cls: '', t: 'Lineare Trend-Fortschreibung je Intervall' },
      { k: 'Schritte bis Obergrenze', v: m.stepsToBreach == null ? '—' : `~${m.stepsToBreach} Intervalle`, cls: (m.stepsToBreach != null && m.stepsToBreach < 12) ? 'band-OutOfRange' : '', t: 'Intervalle, bis der Trend die empfohlene Obergrenze erreicht' },
    ];
    for (const row of rows) {
      const div = document.createElement('div'); div.title = row.t;
      const key = document.createElement('span'); key.className = 'muted'; key.textContent = row.k + ': ';
      const val = document.createElement('span'); val.textContent = row.v; if (row.cls) val.className = row.cls;
      div.appendChild(key); div.appendChild(val); el.appendChild(div);
    }
  }
  function renderForecast(ins) {
    const note = document.getElementById('forecastNote');
    const t = (ins && ins.temperature.forecast) || [];
    const h = (ins && ins.humidity.forecast) || [];
    const n = Math.max(t.length, h.length);
    if (!n) {                                   // 24h with no data, or empty window
      if (fcChart) { fcChart.destroy(); fcChart = null; }
      note.textContent = 'Keine Prognose verfügbar.'; note.style.display = 'block';
      return;
    }
    note.style.display = 'none';
    const labels = Array.from({ length: n }, (_, i) => '+' + (i + 1));   // intervals ahead
    const datasets = [
      { ...ClimaCharts.ds('Temperatur (°C)', t, '#58a6ff'), pointRadius: 3 },
      { ...ClimaCharts.ds('Feuchte (%)', h, '#3fb950'), pointRadius: 3 },
    ];
    if (fcChart) { fcChart.data.labels = labels; fcChart.data.datasets = datasets; fcChart.update(); }
    else fcChart = ClimaCharts.lineChart('forecastChart', labels, datasets);
  }
  document.querySelectorAll('.ranges button[data-r]').forEach(b => b.addEventListener('click', () => load(b.dataset.r)));
  window.addEventListener('themechange', () => { if (fcChart) { fcChart.destroy(); fcChart = null; } load(cur); });
  load('7d');
})();
