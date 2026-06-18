"""Extract selected plots and tables from the executed notebook.

Reads the executed .ipynb, finds cells whose markdown title or code text
matches a curated set of patterns, and writes:
  - assets/fig_<name>.png    one image per selected plot cell
  - assets/results.json      structured tables for README embedding

This script is idempotent: it overwrites previous outputs.
"""
from __future__ import annotations
import base64
import json
import re
from pathlib import Path

ROOT = Path("/Users/arthur/Developer/github/arthurkahwa/climasense")
NB_PATH = ROOT / "Climate_Time_Series_Analysis.ipynb"
ASSETS = ROOT / "assets"
ASSETS.mkdir(exist_ok=True)

# (slug, regex over the *previous* markdown cell, search-in-source regex for the code cell)
# Order matters; first match wins. The *previous* markdown is checked first;
# if no match there, we fall back to the source.
SELECTORS: list[tuple[str, str | None, str | None]] = [
    ("01_overview_full_coverage",
     None,
     r"Hourly mean temperature — full coverage|sanity-check plot — does the hourly mean look sensible"),
    ("02_distributions",
     r"Univariate distributions|distribution",
     None),
    ("03_joint_T_RH",
     r"Joint distribution",
     None),
    ("04_hour_dow_heatmap",
     r"Hour-of-day x day-of-week|hour.*weekday",
     None),
    ("05_monthly_boxplot",
     r"Monthly seasonality",
     None),
    ("06_yearly_trend",
     r"Yearly trend",
     None),
    ("07_rolling_stats",
     r"Rolling statistics|rolling 24h",
     None),
    ("08_acf_pacf",
     r"ACF / PACF|ACF and PACF|Auto.*correlation|partial",
     None),
    ("09_seasonal_decompose_24",
     r"Seasonal decomposition|Daily seasonal decomposition",
     None),
    ("10_stl",
     r"STL decomposition",
     None),
    ("11_periodogram",
     r"Spectral analysis|periodogram|Welch",
     None),
    ("12_naive_forecast",
     r"7\.3 Baseline 1|Naive .*last value",
     None),
    ("13_seasonal_naive_forecast",
     r"7\.4 Baseline 2|Seasonal naive",
     None),
    ("14_holt_winters_forecast",
     r"7\.6 Holt-Winters|Triple Exponential",
     None),
    ("15_arima_forecast",
     r"7\.7 ARIMA",
     None),
    ("16_sarima_forecast",
     r"7\.8 SARIMA",
     None),
    ("17_model_comparison",
     r"7\.9 Model comparison",
     None),
    ("18_multi_horizon",
     r"7\.10 Multi-horizon|24 hours vs 72 hours",
     None),
    ("19_residual_diagnostics",
     r"7\.11 Residual diagnostics",
     None),
    ("20_lag_linear_forecast",
     r"Linear regression on lag features",
     None),
    ("21_gbt_forecast",
     r"Gradient.*boost(ed|ing)|HistGradientBoosting",
     None),
    ("22_lstm_training_loss",
     None,
     r"Training loop\. We use Adam"),
    ("23_lstm_forecast",
     None,
     r"# Evaluate the LSTM on the held-out test window"),
    ("24_recursive_multistep",
     None,
     r"Recursive multi-step forecast with the gradient booster"),
    ("25_sequence_comparison",
     None,
     r"# Build a comparison frame across the sequence models"),
    ("26_hidden_pca",
     None,
     r"Extract hidden states for a stratified sample of days"),
]


def get_text(cell: dict) -> str:
    src = cell.get("source", "")
    return src if isinstance(src, str) else "".join(src)


def first_png(outputs: list[dict]) -> bytes | None:
    """Return the bytes of the first PNG found in a code cell's outputs."""
    for out in outputs:
        data = out.get("data") or {}
        png = data.get("image/png")
        if png:
            return base64.b64decode(png)
    return None


def all_html_tables(outputs: list[dict]) -> list[str]:
    out: list[str] = []
    for o in outputs:
        data = o.get("data") or {}
        html = data.get("text/html")
        if html:
            html_str = html if isinstance(html, str) else "".join(html)
            if "<table" in html_str:
                out.append(html_str)
    return out


def extract() -> dict:
    nb = json.loads(NB_PATH.read_text())
    cells = nb["cells"]

    # Map slug → cell_index by scanning
    chosen: dict[str, int] = {}
    used: set[int] = set()

    def find_for(slug: str, prev_pat: str | None, src_pat: str | None) -> int | None:
        for i, c in enumerate(cells):
            if c["cell_type"] != "code" or i in used:
                continue
            prev_md = ""
            if i > 0 and cells[i - 1]["cell_type"] == "markdown":
                prev_md = get_text(cells[i - 1])
            src = get_text(c)
            ok = False
            if prev_pat and re.search(prev_pat, prev_md, re.I):
                ok = True
            if not ok and src_pat and re.search(src_pat, src, re.I):
                ok = True
            if ok and first_png(c.get("outputs", [])):
                return i
        return None

    for slug, prev_pat, src_pat in SELECTORS:
        idx = find_for(slug, prev_pat, src_pat)
        if idx is not None:
            chosen[slug] = idx
            used.add(idx)

    # Save PNGs
    saved: dict[str, str] = {}
    for slug, idx in chosen.items():
        png = first_png(cells[idx].get("outputs", []))
        if png is None:
            continue
        out_path = ASSETS / f"fig_{slug}.png"
        out_path.write_bytes(png)
        saved[slug] = out_path.name

    # Pull a few tables of interest by scanning for HTML tables in cells whose
    # markdown title or code references them.
    tables: dict[str, str] = {}
    table_anchors = {
        "describe": r"Summary statistics|describe\(\)",
        "model_comparison": r"7\.9 Model comparison|results.*sort_values\('RMSE'\)",
        "sequence_comparison": r"Sequence model comparison",
    }
    for key, pat in table_anchors.items():
        for i, c in enumerate(cells):
            if c["cell_type"] != "code":
                continue
            prev_md = get_text(cells[i - 1]) if i and cells[i - 1]["cell_type"] == "markdown" else ""
            src = get_text(c)
            if re.search(pat, prev_md + "\n" + src, re.I):
                tbls = all_html_tables(c.get("outputs", []))
                if tbls:
                    tables[key] = tbls[-1]
                    break

    # Pull a few key textual stdout outputs (e.g. stationarity report)
    stdout_anchors = {
        "stationarity": r"stationarity_report|adfuller|kpss",
        "data_summary":  r"After dedup|Hourly grid",
        "forecast_results":  r"results_df = pd\.DataFrame\(results\)\.set_index",
        "sequence_results":  r"comparison frame across the sequence models",
    }
    stdouts: dict[str, str] = {}
    for key, pat in stdout_anchors.items():
        for c in cells:
            if c["cell_type"] != "code":
                continue
            src = get_text(c)
            if re.search(pat, src, re.I):
                texts = []
                for o in c.get("outputs", []):
                    if o.get("output_type") == "stream":
                        t = o.get("text", "")
                        texts.append(t if isinstance(t, str) else "".join(t))
                if texts:
                    stdouts[key] = "".join(texts)
                    break

    summary = {
        "figures": saved,
        "tables_html": list(tables.keys()),
        "stdout_keys": list(stdouts.keys()),
    }
    (ASSETS / "results.json").write_text(json.dumps({
        "tables": tables,
        "stdouts": stdouts,
    }, indent=2))
    return summary


if __name__ == "__main__":
    s = extract()
    print(json.dumps(s, indent=2))
