// SPDX-License-Identifier: MIT
//
// Shared Plotly.js layout + axis defaults for the dark GitHub-style
// dashboard. Imported by every page that renders charts (currently
// Explorer.cshtml; later slices add Comfort / Leaderboard pages).
//
// CDN vs vendored decision (slice 4):
//   * Plotly.js is loaded from cdn.plot.ly (see the <script> tag in
//     Explorer.cshtml). The library is ~3 MB minified; committing it
//     to the repo would bloat the worktree without portfolio benefit.
//     Reviewers run the demo with network connectivity available.
//   * If the demo is ever shipped to an air-gapped reviewer, vendor
//     the file into wwwroot/lib/plotly.min.js and swap the <script>
//     src. The rest of this file (the layout config) is unchanged.
//
// Why a function instead of an exported object:
//   Plotly mutates layouts; passing a fresh deep-copy per chart
//   prevents one chart's interaction state (zoom / range) leaking
//   into a sibling. The `darkLayout()` factory returns a fresh object
//   every call.

(function (root) {
  'use strict';

  // GitHub-style palette. Mirrors the dashboard's CSS (Pages/Index.cshtml,
  // Pages/Explorer.cshtml). Keep these in sync if the page theme changes.
  var THEME = Object.freeze({
    // Page chrome
    bg: '#0d1117',
    panel: '#161b22',
    border: '#30363d',
    // Foreground
    text: '#c9d1d9',
    accent: '#58a6ff',
    muted: '#8b949e',
    // Series — distinct hues per metric, kept colour-blind friendly.
    temperature: '#f7768e',     // warm coral
    temperatureBand: 'rgba(247, 118, 142, 0.18)',
    humidity: '#79c0ff',        // cool blue
    humidityBand: 'rgba(121, 192, 255, 0.16)',
    // Heatmap scale — sequential, dark-on-dark friendly.
    heatmapScale: [
      [0.0, '#161b22'],
      [0.2, '#0e4429'],
      [0.4, '#006d32'],
      [0.6, '#26a641'],
      [0.8, '#39d353'],
      [1.0, '#ffd5a8']
    ],
  });

  /**
   * Return a fresh Plotly layout object configured for the dark theme.
   * Override / merge per-chart fields on the returned value as needed.
   */
  function darkLayout(overrides) {
    var base = {
      paper_bgcolor: THEME.bg,
      plot_bgcolor: THEME.bg,
      font: {
        family: '-apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif',
        color: THEME.text,
        size: 13,
      },
      margin: { l: 56, r: 24, t: 36, b: 48 },
      hoverlabel: {
        bgcolor: THEME.panel,
        bordercolor: THEME.border,
        font: { color: THEME.text },
      },
      xaxis: defaultAxis(),
      yaxis: defaultAxis(),
      legend: {
        bgcolor: 'rgba(0,0,0,0)',
        font: { color: THEME.text },
        orientation: 'h',
        x: 0,
        y: 1.12,
      },
      showlegend: true,
    };
    return mergeDeep(base, overrides || {});
  }

  function defaultAxis() {
    return {
      color: THEME.text,
      gridcolor: THEME.border,
      linecolor: THEME.border,
      zerolinecolor: THEME.border,
      tickfont: { color: THEME.muted },
    };
  }

  /**
   * Standard Plotly config: dark mode-toolbar, disable the Plotly logo,
   * remove the lasso / select tools we don't use.
   */
  function darkConfig() {
    return {
      displayModeBar: true,
      displaylogo: false,
      modeBarButtonsToRemove: ['lasso2d', 'select2d', 'autoScale2d'],
      responsive: true,
    };
  }

  /**
   * Shallow-recursive merge (treats arrays as opaque values — does NOT
   * concat). Sufficient for layout overrides where users typically
   * replace whole sub-objects (e.g. `{ yaxis: { range: [a, b] } }`).
   */
  function mergeDeep(target, source) {
    if (!source || typeof source !== 'object') return target;
    Object.keys(source).forEach(function (key) {
      var sv = source[key];
      var tv = target[key];
      if (sv && typeof sv === 'object' && !Array.isArray(sv) &&
          tv && typeof tv === 'object' && !Array.isArray(tv)) {
        target[key] = mergeDeep(tv, sv);
      } else {
        target[key] = sv;
      }
    });
    return target;
  }

  root.ClimaSensePlotly = Object.freeze({
    THEME: THEME,
    darkLayout: darkLayout,
    darkConfig: darkConfig,
  });
})(window);
