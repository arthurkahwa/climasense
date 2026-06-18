#!/usr/bin/env python3
"""Generate docs/api.html — the browser-viewable API reference — from openapi.yaml.

`openapi.yaml` is the single source of truth. This embeds it verbatim into a
self-contained Redoc page that renders both ways:

  * double-click docs/api.html (file://) — the spec is parsed in-browser with
    js-yaml, so no local fetch (and therefore no CORS error) is needed;
  * served over HTTP (GitHub Pages, `dotnet run` static files, `python -m
    http.server`) — works identically.

The only network dependency is the Redoc + js-yaml bundles from a CDN; an
offline fallback links to the raw spec and the Swagger Editor.

Re-run after editing the spec:

    python3 scripts/build-api-html.py
"""
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SPEC = ROOT / "openapi.yaml"
# The same generated page is written to two places, both fed from openapi.yaml:
#   * docs/api.html               — portfolio / GitHub Pages copy
#   * wwwroot/api.html            — served by the running app, embedded in the API tab
OUTPUTS = [
    ROOT / "docs" / "api.html",
    ROOT / "src" / "ClimaSense.Monitor" / "wwwroot" / "api.html",
]

# NOTE: keep "__OPENAPI_SPEC__" on its own line, unindented, so the embedded
# YAML keeps its original column-0 structure.
TEMPLATE = """<!doctype html>
<html lang="de">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>ClimaSense Monitor API &mdash; Referenz</title>
  <meta name="description" content="Interaktive Referenz für die API des ClimaSense USV-Raum-Umgebungsmonitors (OpenAPI 3.0)." />
  <!-- inline favicon to avoid a 404 when opened from file:// -->
  <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ctext y='14' font-size='14'%3E%F0%9F%8C%A1%EF%B8%8F%3C/text%3E%3C/svg%3E" />
  <style>
    html, body { background: #ffffff; }
    body { margin: 0; padding: 0; color: #1f2328; font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
    #fallback {
      display: none; max-width: 40rem; margin: 4rem auto; padding: 0 1.5rem;
      color: #24292f; line-height: 1.6;
    }
    #fallback h1 { font-size: 1.4rem; }
    #fallback code { background: #f0f0f4; padding: 0.1em 0.35em; border-radius: 4px; }
    #fallback a { color: #512BD4; }
    #loading {
      display: flex; align-items: center; justify-content: center;
      height: 100vh; color: #6e7781; font-size: 1.05rem;
    }
  </style>
</head>
<body>
  <div id="loading">ClimaSense API-Referenz wird geladen&hellip;</div>
  <div id="redoc"></div>

  <div id="fallback">
    <h1>&#127777;&#65039; ClimaSense Monitor API</h1>
    <p>
      Die interaktive Darstellung konnte nicht geladen werden (sie benötigt die
      Redoc- und js-yaml-Bundles von einem CDN, daher ist beim ersten Mal eine
      Internetverbindung erforderlich).
    </p>
    <p>Sie können die Spezifikation dennoch direkt einsehen:</p>
    <ul>
      <li>Die Rohspezifikation: <a href="../openapi.yaml">openapi.yaml</a></li>
      <li>
        Fügen Sie diese Datei für eine interaktive Ansicht in den
        <a href="https://editor.swagger.io/" target="_blank" rel="noopener">Swagger Editor</a>
        ein.
      </li>
    </ul>
  </div>

  <!-- The spec is embedded verbatim; do not edit here. Regenerate with scripts/build-api-html.py -->
  <script id="openapi-spec" type="application/yaml">
__OPENAPI_SPEC__
  </script>

  <!-- Pinned versions + Subresource Integrity: the browser refuses to run a bundle
       whose hash does not match, so a compromised CDN cannot inject code here. -->
  <script
    src="https://cdn.jsdelivr.net/npm/js-yaml@4.1.0/dist/js-yaml.min.js"
    integrity="sha384-+pxiN6T7yvpryuJmE1gM9PX7yQit15auDb+ZwwvJOd/4be2Cie5/IuVXgQb/S9du"
    crossorigin="anonymous"></script>
  <script
    src="https://cdn.jsdelivr.net/npm/redoc@2.5.3/bundles/redoc.standalone.js"
    integrity="sha384-xiEssMQFSpSfLbzRZCGfxxIM5QDb2DTrU6vyoZdp2sV1L6pmOMy6MpTtUoLbpC96"
    crossorigin="anonymous"></script>
  <script>
    (function () {
      var loading = document.getElementById('loading');
      function showFallback() {
        if (loading) loading.style.display = 'none';
        document.getElementById('redoc').style.display = 'none';
        document.getElementById('fallback').style.display = 'block';
      }
      try {
        if (!window.jsyaml || !window.Redoc) { showFallback(); return; }
        var text = document.getElementById('openapi-spec').textContent;
        var spec = window.jsyaml.load(text);
        window.Redoc.init(
          spec,
          {
            theme: {
              colors: {
                primary: { main: '#5b3fd6' },
                text: { primary: '#1f2328', secondary: '#57606a' },
                http: { get: '#1a7f37', post: '#0550ae', delete: '#cf222e', put: '#9a6700' }
              },
              typography: {
                fontSize: '15px',
                lineHeight: '1.6',
                fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
                headings: { fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif', fontWeight: '600' },
                links: { color: '#0550ae', visited: '#0550ae', hover: '#033d8a' },
                code: { fontSize: '13px', color: '#0550ae', backgroundColor: '#eff1f3' }
              },
              sidebar: { backgroundColor: '#f6f8fa', textColor: '#1f2328', activeTextColor: '#5b3fd6' },
              rightPanel: { backgroundColor: '#0d1117', textColor: '#e6edf3' }
            },
            hideDownloadButton: false,
            downloadFileName: 'climasense-openapi.yaml',
            expandResponses: '200',
            jsonSampleExpandLevel: 4,
            requiredPropsFirst: true,
            pathInMiddlePanel: true,
            hideHostname: false
          },
          document.getElementById('redoc'),
          function (err) {
            if (err) { console.error(err); showFallback(); }
            else if (loading) { loading.style.display = 'none'; }
          }
        );
      } catch (e) {
        console.error(e);
        showFallback();
      }
    })();
  </script>
</body>
</html>
"""


def main() -> None:
    spec = SPEC.read_text(encoding="utf-8")
    if "</script>" in spec.lower():
        raise SystemExit("Refusing to embed: spec contains '</script>', which would break the HTML.")
    html = TEMPLATE.replace("__OPENAPI_SPEC__", spec)
    for out in OUTPUTS:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(html, encoding="utf-8")
        print(f"Wrote {out.relative_to(ROOT)} ({len(html):,} bytes) from {SPEC.name}")


if __name__ == "__main__":
    main()
