# ClimaSense — iOS Pendant

The iOS-family companion to the **ClimaSense Monitor** (the read-only .NET 10
ASP.NET Core dashboard at the repo root). A SwiftUI multiplatform app that mirrors
the Monitor **read-only** over its OpenAPI JSON API — across iPhone, a bespoke iPad
dashboard, a Mac menu-bar + window app, Apple Watch + complications, widgets, and a
Live Activity. German-primary (de-DE) + English.

- **Design spec:** [`specs/2026-06-16-climasense-ios-pendant-design.md`](./specs/2026-06-16-climasense-ios-pendant-design.md)
- **Status:** design approved; Xcode project not yet scaffolded.
- **API contract it consumes:** [`../openapi.yaml`](../openapi.yaml) — rendered reference: [`../docs/api.html`](../docs/api.html)

The Xcode project and Swift sources (incl. the shared `ClimaSenseKit` package) will
live under this `ios/` folder.
