window.ClimaCharts = (function () {
  function themeColors() {
    const css = getComputedStyle(document.documentElement);
    return {
      fg: css.getPropertyValue('--fg').trim() || '#e6edf3',
      muted: css.getPropertyValue('--muted').trim() || '#8b949e',
      line: css.getPropertyValue('--line').trim() || '#21262d',
    };
  }
  // Vertical crosshair at the hovered point, drawn 1pt thicker than the grid lines.
  const crosshairPlugin = {
    id: 'crosshair',
    afterDraw(chart) {
      const active = chart.getActiveElements();
      if (!active.length) return;
      const x = active[0].element.x;
      const { top, bottom } = chart.chartArea;
      const grid = (chart.options.scales.x.grid && chart.options.scales.x.grid.lineWidth) || 1;
      const ctx = chart.ctx;
      ctx.save();
      ctx.beginPath();
      ctx.moveTo(x, top); ctx.lineTo(x, bottom);
      ctx.lineWidth = grid + 1;                 // 1pt thicker than the gridlines
      ctx.strokeStyle = themeColors().fg;
      ctx.stroke();
      ctx.restore();
    }
  };
  function lineChart(canvasId, labels, datasets, opts) {
    opts = opts || {};
    const c = themeColors();
    const chart = new Chart(document.getElementById(canvasId), {
      type: 'line',
      data: { labels, datasets },
      options: {
        responsive: true, interaction: { mode: 'index', intersect: false },
        scales: { x: { ticks: { color: c.muted }, grid: { color: c.line } },
                  y: { ticks: { color: c.muted }, grid: { color: c.line } } },
        plugins: {
          legend: { labels: { color: c.fg, filter: (i, data) => !data.datasets[i.datasetIndex].isBand } }, // legend shows only the Ø lines
          tooltip: {
            filter: item => !(opts.hideBandInTooltip && item.dataset.isBand), // Live tooltip shows only the Ø lines, no Min/Max
            callbacks: { label: ctx => `${ctx.dataset.label}: ${window.deNum ? deNum(ctx.parsed.y) : ctx.parsed.y}` }
          }
        }
      },
      plugins: [crosshairPlugin]
    });
    enableBoxZoom(chart);
    return chart;
  }
  function ds(label, data, color) {
    return { label, data, borderColor: color, backgroundColor: color, tension: .25, pointRadius: 0, borderWidth: 2 };
  }
  // A shaded min..max band: invisible lower line + upper line filled down to it.
  // name + unit label the Min/Max series so the tooltip reads "Temperatur Min (°C): 18,7" — never blank.
  function band(lower, upper, fillColor, name, unit) {
    return [
      { label: `${name} Min (${unit})`, data: lower, borderColor: 'transparent', pointRadius: 0, fill: false, tension: .25, isBand: true },
      { label: `${name} Max (${unit})`, data: upper, borderColor: 'transparent', backgroundColor: fillColor, pointRadius: 0, fill: '-1', tension: .25, isBand: true },
    ];
  }
  // Drop any drag-zoom and return the chart to the full data range.
  function resetZoom(chart) {
    const x = chart.options.scales.x, y = chart.options.scales.y;
    delete x.min; delete x.max; delete y.min; delete y.max;
    chart.update('none');
  }
  // Drag a box across the chart to magnify that area; repeat to zoom further; the reset button restores the full view.
  function enableBoxZoom(chart) {
    const canvas = chart.canvas;
    if (canvas.__boxZoom) return;               // wire each canvas once; handlers fetch the live chart
    canvas.__boxZoom = true;
    let wrap = canvas.parentElement;
    if (!wrap.classList.contains('chart-wrap')) {
      wrap = document.createElement('div');
      wrap.className = 'chart-wrap';
      canvas.parentNode.insertBefore(wrap, canvas);
      wrap.appendChild(canvas);
    }
    const boxEl = document.createElement('div');
    boxEl.className = 'zoom-box';
    wrap.appendChild(boxEl);
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'zoom-reset';
    btn.textContent = '⤢ Standardansicht';
    btn.title = 'Zoom zurücksetzen — ganzen Zeitraum anzeigen';
    btn.addEventListener('click', () => { const ch = Chart.getChart(canvas); if (ch) resetZoom(ch); });
    wrap.appendChild(btn);
    let drag = null;
    const at = e => { const r = canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
    canvas.addEventListener('mousedown', e => {
      if (e.button !== 0) return;
      const p = at(e); drag = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
    });
    canvas.addEventListener('mousemove', e => {
      if (!drag) return;
      const p = at(e); drag.x1 = p.x; drag.y1 = p.y;
      boxEl.style.display = 'block';
      boxEl.style.left = Math.min(drag.x0, drag.x1) + 'px';
      boxEl.style.top = Math.min(drag.y0, drag.y1) + 'px';
      boxEl.style.width = Math.abs(drag.x1 - drag.x0) + 'px';
      boxEl.style.height = Math.abs(drag.y1 - drag.y0) + 'px';
    });
    window.addEventListener('mouseup', () => {
      if (!drag) return;
      const d = drag; drag = null;
      boxEl.style.display = 'none';
      if (Math.abs(d.x1 - d.x0) < 8 || Math.abs(d.y1 - d.y0) < 8) return;   // ignore clicks / tiny drags
      const ch = Chart.getChart(canvas);
      if (ch) applyBoxZoom(ch, d);
    });
  }
  function applyBoxZoom(chart, d) {
    const xs = chart.scales.x, ys = chart.scales.y;
    const pxL = Math.min(d.x0, d.x1), pxR = Math.max(d.x0, d.x1);
    const pxT = Math.min(d.y0, d.y1), pxB = Math.max(d.y0, d.y1);
    const last = chart.data.labels.length - 1;
    let i0 = Math.max(0, Math.min(Math.round(xs.getValueForPixel(pxL)), last));
    let i1 = Math.max(0, Math.min(Math.round(xs.getValueForPixel(pxR)), last));
    if (i1 - i0 < 1) return;                     // nothing finer left to show
    const vT = ys.getValueForPixel(pxT), vB = ys.getValueForPixel(pxB);
    const x = chart.options.scales.x, y = chart.options.scales.y;
    x.min = i0; x.max = i1;
    y.min = Math.min(vT, vB); y.max = Math.max(vT, vB);
    chart.update('none');
  }
  return { lineChart, ds, band, resetZoom };
})();
