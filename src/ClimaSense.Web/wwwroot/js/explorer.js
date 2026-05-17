// SPDX-License-Identifier: MIT
//
// Explorer page — range selector logic, bucket toggle, fetch + render.
// Loaded by Pages/Explorer.cshtml. Depends on Plotly.js (loaded from
// CDN in the page) and wwwroot/js/plotly-config.js (sibling file).
//
// Surface:
//   * Range buttons (1D / 1W / 1M / 3M / 1Y / ALL) +
//     two <input type="datetime-local"> pickers for custom ranges.
//   * Bucket toggle (Raw / Hourly / Daily / Weekly).
//   * Plotly time-series chart with min/max envelope band (aggregated only).
//   * GitHub-contribution-style heatmap below the time-series.
//
// Vanilla JS — no framework. The page is single-pass: load once, then
// every UI interaction triggers a fetch + re-render.

(function () {
  'use strict';

  var TS_CHART_ID = 'explorer-timeseries';
  var HEATMAP_CHART_ID = 'explorer-heatmap';

  // Map button data-range -> milliseconds. ALL is special-cased to use
  // the corpus's earliest known date.
  var RANGE_MS = {
    '1D': 24 * 3600 * 1000,
    '1W': 7 * 24 * 3600 * 1000,
    '1M': 30 * 24 * 3600 * 1000,
    '3M': 90 * 24 * 3600 * 1000,
    '1Y': 365 * 24 * 3600 * 1000,
  };

  // Earliest reading in the bundled CSV (2019-07-09). Used by the ALL
  // button to set the start; the real corpus boundary is verified
  // server-side via the cursor clip.
  var CORPUS_START = '2019-07-09T00:00:00Z';

  // State held in the URL hash so reloads preserve the selected range.
  // Form: #range=1W&bucket=hour&year=2024
  var state = {
    range: '1W',
    bucket: 'hour',
    customStart: null,    // ISO 8601 or null
    customEnd: null,
    year: defaultYear(),
  };

  function defaultYear() {
    return new Date().getUTCFullYear();
  }

  // ----------------------------------------------------------------
  // DOM wiring
  // ----------------------------------------------------------------
  document.addEventListener('DOMContentLoaded', function () {
    parseHashIntoState();
    renderHeaderState();

    // Range buttons.
    Array.prototype.forEach.call(
      document.querySelectorAll('[data-range]'),
      function (btn) {
        btn.addEventListener('click', function () {
          state.range = btn.getAttribute('data-range');
          state.customStart = null;
          state.customEnd = null;
          syncCustomInputsFromRange();
          renderHeaderState();
          loadRange();
        });
      });

    // Custom range picker.
    var customStart = document.getElementById('custom-start');
    var customEnd = document.getElementById('custom-end');
    var customApply = document.getElementById('custom-apply');
    if (customApply) {
      customApply.addEventListener('click', function () {
        if (!customStart.value || !customEnd.value) {
          flash('Pick both a start and an end.');
          return;
        }
        state.range = 'CUSTOM';
        state.customStart = new Date(customStart.value).toISOString();
        state.customEnd = new Date(customEnd.value).toISOString();
        renderHeaderState();
        loadRange();
      });
    }

    // Bucket toggle.
    Array.prototype.forEach.call(
      document.querySelectorAll('[data-bucket]'),
      function (btn) {
        btn.addEventListener('click', function () {
          state.bucket = btn.getAttribute('data-bucket');
          renderHeaderState();
          loadRange();
        });
      });

    // Heatmap year selector.
    var yearInput = document.getElementById('heatmap-year');
    if (yearInput) {
      yearInput.value = state.year;
      yearInput.addEventListener('change', function () {
        var y = parseInt(yearInput.value, 10);
        if (isNaN(y) || y < 1900 || y > 2100) {
          flash('Year must be in [1900, 2100].');
          return;
        }
        state.year = y;
        renderHeaderState();
        loadHeatmap();
      });
    }

    // Initial loads.
    syncCustomInputsFromRange();
    loadRange();
    loadHeatmap();
  });

  function parseHashIntoState() {
    var h = window.location.hash.replace(/^#/, '');
    if (!h) return;
    h.split('&').forEach(function (kv) {
      var pair = kv.split('=');
      if (pair.length !== 2) return;
      var k = decodeURIComponent(pair[0]);
      var v = decodeURIComponent(pair[1]);
      if (k === 'range') state.range = v;
      else if (k === 'bucket') state.bucket = v;
      else if (k === 'year') state.year = parseInt(v, 10) || state.year;
      else if (k === 'start') state.customStart = v;
      else if (k === 'end') state.customEnd = v;
    });
  }

  function writeStateToHash() {
    var parts = [
      'range=' + encodeURIComponent(state.range),
      'bucket=' + encodeURIComponent(state.bucket),
      'year=' + encodeURIComponent(state.year),
    ];
    if (state.range === 'CUSTOM' && state.customStart && state.customEnd) {
      parts.push('start=' + encodeURIComponent(state.customStart));
      parts.push('end=' + encodeURIComponent(state.customEnd));
    }
    history.replaceState(null, '', '#' + parts.join('&'));
  }

  function renderHeaderState() {
    writeStateToHash();
    // Mark the selected button.
    Array.prototype.forEach.call(
      document.querySelectorAll('[data-range]'),
      function (b) {
        if (b.getAttribute('data-range') === state.range) {
          b.classList.add('selected');
        } else {
          b.classList.remove('selected');
        }
      });
    Array.prototype.forEach.call(
      document.querySelectorAll('[data-bucket]'),
      function (b) {
        if (b.getAttribute('data-bucket') === state.bucket) {
          b.classList.add('selected');
        } else {
          b.classList.remove('selected');
        }
      });
    var status = document.getElementById('explorer-status');
    if (status) {
      var window_ = resolveRange();
      status.textContent =
        'range: ' + state.range +
        ' (' + window_.start + ' → ' + window_.end + ')' +
        ' · bucket: ' + state.bucket;
    }
  }

  function syncCustomInputsFromRange() {
    var startEl = document.getElementById('custom-start');
    var endEl = document.getElementById('custom-end');
    if (!startEl || !endEl) return;
    var w = resolveRange();
    // datetime-local needs local-timezone strings; trim the seconds for legibility.
    startEl.value = w.start.slice(0, 16);
    endEl.value = w.end.slice(0, 16);
  }

  // ----------------------------------------------------------------
  // Range resolution
  // ----------------------------------------------------------------
  function resolveRange() {
    var now = new Date();
    if (state.range === 'CUSTOM' && state.customStart && state.customEnd) {
      return { start: state.customStart, end: state.customEnd };
    }
    if (state.range === 'ALL') {
      return { start: CORPUS_START, end: now.toISOString() };
    }
    var ms = RANGE_MS[state.range] || RANGE_MS['1W'];
    var start = new Date(now.getTime() - ms);
    return { start: start.toISOString(), end: now.toISOString() };
  }

  // ----------------------------------------------------------------
  // Range fetch + render
  // ----------------------------------------------------------------
  function loadRange() {
    var w = resolveRange();
    var url =
      '/api/readings/range' +
      '?start=' + encodeURIComponent(w.start) +
      '&end=' + encodeURIComponent(w.end) +
      '&bucket=' + encodeURIComponent(state.bucket);

    fetch(url, { headers: { 'Accept': 'application/json' } })
      .then(function (resp) {
        if (!resp.ok) {
          return resp.json().then(function (body) {
            throw { status: resp.status, body: body };
          }, function () {
            throw { status: resp.status, body: { error: 'http_error', message: 'HTTP ' + resp.status } };
          });
        }
        return resp.json();
      })
      .then(function (body) {
        renderTimeSeries(body);
      })
      .catch(function (err) {
        renderRangeError(err);
      });
  }

  function renderTimeSeries(body) {
    var times = [];
    var tMean = [];
    var tMin = [];
    var tMax = [];
    var hMean = [];
    body.buckets.forEach(function (b) {
      times.push(b.bucketTime);
      tMean.push(b.temperatureMean);
      tMin.push(b.temperatureMin);
      tMax.push(b.temperatureMax);
      hMean.push(b.humidityMean);
    });

    var T = window.ClimaSensePlotly.THEME;
    var traces = [];

    // Min/max envelope for aggregated buckets (skipped for raw).
    if (body.bucket !== 'raw') {
      traces.push({
        x: times.concat(times.slice().reverse()),
        y: tMax.concat(tMin.slice().reverse()),
        fill: 'toself',
        fillcolor: T.temperatureBand,
        line: { color: 'rgba(0,0,0,0)' },
        hoverinfo: 'skip',
        showlegend: false,
        name: 'T min/max',
      });
    }

    traces.push({
      x: times,
      y: tMean,
      type: 'scatter',
      mode: body.bucket === 'raw' ? 'lines' : 'lines+markers',
      name: 'Temperature (°C)',
      line: { color: T.temperature, width: 2 },
      marker: { size: 4, color: T.temperature },
      yaxis: 'y',
      hovertemplate: '%{x|%Y-%m-%d %H:%M}<br>%{y:.2f} °C<extra></extra>',
    });

    traces.push({
      x: times,
      y: hMean,
      type: 'scatter',
      mode: body.bucket === 'raw' ? 'lines' : 'lines+markers',
      name: 'Humidity (% RH)',
      line: { color: T.humidity, width: 2, dash: 'dot' },
      marker: { size: 4, color: T.humidity },
      yaxis: 'y2',
      hovertemplate: '%{x|%Y-%m-%d %H:%M}<br>%{y:.1f} %<extra></extra>',
    });

    var layout = window.ClimaSensePlotly.darkLayout({
      title: {
        text: 'Sensor readings · ' + body.bucket,
        font: { color: T.text, size: 16 },
      },
      hovermode: 'x unified',
      crosshair: true,
      xaxis: { type: 'date', title: { text: '' } },
      yaxis: { title: { text: 'Temperature (°C)' }, side: 'left' },
      yaxis2: {
        title: { text: 'Humidity (% RH)' },
        overlaying: 'y',
        side: 'right',
        color: T.text,
        gridcolor: 'rgba(0,0,0,0)',
        tickfont: { color: T.muted },
      },
      margin: { l: 56, r: 56, t: 56, b: 48 },
    });

    Plotly.react(TS_CHART_ID, traces, layout, window.ClimaSensePlotly.darkConfig());
    showRangeMeta(body);
  }

  function showRangeMeta(body) {
    var meta = document.getElementById('explorer-meta');
    if (!meta) return;
    var totalSamples = 0;
    var populated = 0;
    body.buckets.forEach(function (b) {
      totalSamples += (b.sampleCount || 0);
      if (b.sampleCount > 0) populated += 1;
    });
    meta.textContent =
      body.buckets.length + ' buckets · ' +
      populated + ' populated · ' +
      totalSamples + ' raw rows';
  }

  function renderRangeError(err) {
    var msg = (err && err.body && err.body.message) || 'unknown error';
    flash('Range query failed: ' + msg);
    replaceChartWithError(TS_CHART_ID, '/api/readings/range failed: ' + msg);
  }

  // ----------------------------------------------------------------
  // Heatmap fetch + render
  // ----------------------------------------------------------------
  function loadHeatmap() {
    var url = '/api/readings/heatmap?year=' + encodeURIComponent(state.year);
    fetch(url, { headers: { 'Accept': 'application/json' } })
      .then(function (resp) {
        if (!resp.ok) {
          return resp.json().then(function (body) {
            throw { status: resp.status, body: body };
          }, function () {
            throw { status: resp.status, body: { error: 'http_error', message: 'HTTP ' + resp.status } };
          });
        }
        return resp.json();
      })
      .then(function (body) {
        renderHeatmap(body);
      })
      .catch(function (err) {
        renderHeatmapError(err);
      });
  }

  function renderHeatmap(body) {
    // GitHub-style: x = week-of-year (0..53), y = weekday (Mon..Sun).
    // We flip y so Monday is at the top of the visible grid.
    var weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
    var weeks = 53;
    var z = [];
    var hoverText = [];
    var dateText = [];
    for (var r = 0; r < 7; r++) {
      z.push(new Array(weeks).fill(null));
      hoverText.push(new Array(weeks).fill(''));
      dateText.push(new Array(weeks).fill(''));
    }

    var anchor = new Date(Date.UTC(body.year, 0, 1));
    var anchorDow = (anchor.getUTCDay() + 6) % 7; // Mon=0..Sun=6

    body.cells.forEach(function (cell, idx) {
      var dow = (new Date(cell.date + 'T00:00:00Z').getUTCDay() + 6) % 7;
      var col = Math.floor((idx + anchorDow) / 7);
      if (col >= weeks) col = weeks - 1;
      z[dow][col] = cell.temperatureMean;
      dateText[dow][col] = cell.date;
      hoverText[dow][col] = cell.sampleCount > 0
        ? cell.date + ' · ' + cell.temperatureMean.toFixed(2) + ' °C · ' +
            cell.sampleCount + ' samples'
        : cell.date + ' · no data';
    });

    var T = window.ClimaSensePlotly.THEME;
    var trace = {
      type: 'heatmap',
      x: monthLabels(body.year),
      y: weekdays,
      z: z,
      colorscale: T.heatmapScale,
      hoverongaps: false,
      showscale: true,
      colorbar: {
        title: { text: '°C', font: { color: T.text } },
        tickfont: { color: T.muted },
        bgcolor: 'rgba(0,0,0,0)',
        outlinecolor: T.border,
      },
      xgap: 2,
      ygap: 2,
      text: hoverText,
      hovertemplate: '%{text}<extra></extra>',
    };

    var layout = window.ClimaSensePlotly.darkLayout({
      title: {
        text: 'Daily mean temperature · ' + body.year,
        font: { color: T.text, size: 16 },
      },
      yaxis: {
        autorange: 'reversed',
        color: T.text,
        gridcolor: 'rgba(0,0,0,0)',
        tickfont: { color: T.muted },
      },
      xaxis: {
        color: T.text,
        gridcolor: 'rgba(0,0,0,0)',
        tickmode: 'auto',
        tickfont: { color: T.muted },
      },
      margin: { l: 56, r: 24, t: 56, b: 48 },
      showlegend: false,
    });

    Plotly.react(HEATMAP_CHART_ID, [trace], layout, window.ClimaSensePlotly.darkConfig());

    var heatmapMeta = document.getElementById('heatmap-meta');
    if (heatmapMeta) {
      var withData = body.cells.filter(function (c) { return c.sampleCount > 0; }).length;
      heatmapMeta.textContent =
        body.cells.length + ' days · ' + withData + ' with data';
    }
  }

  function monthLabels(year) {
    var labels = [];
    for (var w = 0; w < 53; w++) {
      var start = new Date(Date.UTC(year, 0, 1 + w * 7));
      if (start.getUTCDate() <= 7) {
        labels.push(start.toLocaleDateString(undefined, { month: 'short' }));
      } else {
        labels.push('');
      }
    }
    return labels;
  }

  function renderHeatmapError(err) {
    var msg = (err && err.body && err.body.message) || 'unknown error';
    flash('Heatmap query failed: ' + msg);
    replaceChartWithError(HEATMAP_CHART_ID, '/api/readings/heatmap failed: ' + msg);
  }

  // ----------------------------------------------------------------
  // Helpers
  // ----------------------------------------------------------------
  function replaceChartWithError(elementId, message) {
    var div = document.getElementById(elementId);
    if (!div) return;
    while (div.firstChild) {
      div.removeChild(div.firstChild);
    }
    var box = document.createElement('div');
    box.style.padding = '1rem';
    box.style.color = '#f85149';
    box.textContent = message;
    div.appendChild(box);
  }

  // Tiny toast utility — `flash` overwrites the previous message.
  function flash(text) {
    var el = document.getElementById('explorer-toast');
    if (!el) {
      console.log('[explorer]', text);
      return;
    }
    el.textContent = text;
    el.classList.add('visible');
    clearTimeout(flash._t);
    flash._t = setTimeout(function () {
      el.classList.remove('visible');
    }, 3500);
  }
})();
