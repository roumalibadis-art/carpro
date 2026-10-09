# End-to-end and accessibility checks

Drives the **real** application in Chromium (Playwright) and scans pages with axe-core (WCAG 2 A/AA, serious/critical).

```bash
dotnet run --project ../src/Prospecta.Web        # in another terminal: dev mode creates the demo accounts and demo data
npm install
BASE_URL=http://localhost:5190 CHROMIUM_PATH=/path/to/chromium node run.mjs
```
Covers: sign-in, hierarchical geography filters, business creation, duplicate flag, Excel/CSV template import, campaign → visit → follow-up, indicators, report → PDF/Excel, map (no key/CDN), honest connector states, mobile layout, accessibility of 18 pages.
The harness uses `bypassCSP` only to inject test code; the application's strict CSP is asserted by the integration tests. Data created carries a per-run tag, so the suite can be re-run on the same database.
