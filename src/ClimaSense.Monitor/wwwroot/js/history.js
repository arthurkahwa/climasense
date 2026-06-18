(function () {
  let chart = null, lastQuery = 'range=7d', lastLabel = '7d', mode = 'avg';   // 'avg' = Mittelwerte (default) | 'raw' = Messwerte
  const B = window.appBase || '';   // app base path: '' at root, '/ups3' under a sub-application
  const METRIC_DE = { Temperature: 'Temperatur', Humidity: 'Feuchte' };
  const BAND_DE = { Recommended: 'Normal', Allowable: 'Zulässig', OutOfRange: 'Kritisch' };
  async function loadQuery(query, activeLabel) {
    lastQuery = query; lastLabel = activeLabel;
    document.querySelectorAll('.ranges button[data-r]').forEach(b => b.classList.toggle('active', b.dataset.r === activeLabel));
    const [, daily, excursions] = await Promise.all([
      renderTrend(query),
      fetch(B + '/api/readings/daily?' + query).then(r => r.ok ? r.json() : []),
      fetch(B + '/api/readings/excursions?' + query).then(r => r.ok ? r.json() : []),
    ]);
    renderHeatmap(daily); renderExcursions(excursions);
  }
  async function renderTrend(query) {
    // Messwerte works for every range now: the server returns individual readings up to
    // ~210 days and a min/max envelope of recorded extremes beyond that.
    clearChartNote();
    if (mode === 'raw') renderRawChart(await fetch(B + '/api/readings/raw?' + query).then(r => r.ok ? r.json() : []));
    else                renderChart(await fetch(B + '/api/readings/series?' + query).then(r => r.ok ? r.json() : []));
  }
  function clearChartNote() { const n = document.getElementById('chartNote'); n.textContent = ''; n.style.display = 'none'; }
  function load(range) { loadQuery('range=' + range, range); }
  function drawChart(labels, datasets) {
    if (chart) {
      const x = chart.options.scales.x, y = chart.options.scales.y;
      delete x.min; delete x.max; delete y.min; delete y.max;   // new data -> full view, drop any drag-zoom
      chart.data.labels = labels; chart.data.datasets = datasets; chart.update();
    } else chart = ClimaCharts.lineChart('histChart', labels, datasets);
  }
  function renderChart(pts) {        // Mittelwerte: averaged lines + Min–Max bands
    const labels = pts.map(p => new Date(p.bucketStartCet).toLocaleString('de-DE'));
    const tAvg = pts.map(p => p.avgTemp), tMin = pts.map(p => p.minTemp), tMax = pts.map(p => p.maxTemp);
    const hAvg = pts.map(p => p.avgHumidity), hMin = pts.map(p => p.minHumidity), hMax = pts.map(p => p.maxHumidity);
    drawChart(labels, [
      ...ClimaCharts.band(tMin, tMax, 'rgba(88,166,255,0.15)', 'Temperatur', '°C'), ClimaCharts.ds('Temperatur Ø (°C)', tAvg, '#58a6ff'),
      ...ClimaCharts.band(hMin, hMax, 'rgba(63,185,80,0.15)', 'Feuchte', '%'), ClimaCharts.ds('Feuchte Ø (%)', hAvg, '#3fb950'),
    ]);
  }
  function renderRawChart(pts) {     // Messwerte: actual readings, no bands
    const labels = pts.map(p => new Date(p.timestampCet).toLocaleString('de-DE'));
    drawChart(labels, [
      ClimaCharts.ds('Temperatur (°C)', pts.map(p => p.temperatureC), '#58a6ff'),
      ClimaCharts.ds('Feuchte (%)', pts.map(p => p.humidityPct), '#3fb950'),
    ]);
  }
  function renderHeatmap(daily) {
    const grid = document.getElementById('heatmap'); grid.replaceChildren();
    if (!daily.length) return;
    const temps = daily.map(d => d.avgTemp), min = Math.min(...temps), max = Math.max(...temps);
    daily.forEach(d => {
      const cell = document.createElement('div'); cell.className = 'cell';
      const f = (d.avgTemp - min) / (max - min || 1);
      cell.style.background = `rgb(${Math.round(40 + f * 200)},80,${Math.round(120 - f * 60)})`;
      cell.title = d.dateCet + ': Ø ' + deNum(d.avgTemp) + ' °C';
      grid.appendChild(cell);
    });
  }
  function renderExcursions(ex) {
    const tb = document.querySelector('#excursions tbody'); tb.replaceChildren();
    if (!ex.length) {
      const tr = document.createElement('tr'), td = document.createElement('td');
      td.colSpan = 5; td.className = 'muted'; td.textContent = 'Keine Überschreitungen im Zeitraum.';
      tr.appendChild(td); tb.appendChild(tr); return;
    }
    ex.forEach(e => {
      const tr = document.createElement('tr');
      const cells = [
        { text: METRIC_DE[e.metric] || e.metric },
        { text: new Date(e.startCet).toLocaleString('de-DE') },
        { text: new Date(e.endCet).toLocaleString('de-DE') },
        { text: e.durationMinutes + ' Min' },
        { text: deNum(e.peak), cls: 'band-' + e.band, title: BAND_DE[e.band] || e.band },
      ];
      for (const c of cells) {
        const td = document.createElement('td');
        td.textContent = c.text;
        if (c.cls) td.className = c.cls;
        if (c.title) td.title = c.title;
        tr.appendChild(td);
      }
      tb.appendChild(tr);
    });
  }
  document.querySelectorAll('.ranges button[data-r]').forEach(b => b.addEventListener('click', () => load(b.dataset.r)));
  document.querySelectorAll('.modes button[data-mode]').forEach(b => b.addEventListener('click', () => {
    mode = b.dataset.mode;
    document.querySelectorAll('.modes button[data-mode]').forEach(x => x.classList.toggle('active', x === b));
    loadQuery(lastQuery, lastLabel);
  }));
  document.getElementById('applyCustom').addEventListener('click', () => {
    const from = document.getElementById('from').value, to = document.getElementById('to').value;
    if (!from || !to) return;
    loadQuery(`from=${from}T00:00:00&to=${to}T23:59:59`, null);
  });
  window.addEventListener('themechange', () => { if (chart) { chart.destroy(); chart = null; } loadQuery(lastQuery, lastLabel); });
  load('7d');
})();
