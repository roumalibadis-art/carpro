# CLAUDE.md

Guidance for Claude Code in this repository. (`carpro` is independent from the `school` repository.)

## What this is
**Prospecta** — multi-user platform to census businesses (Algeria: wilaya › daïra › commune › quartier), prospect them (campaigns, outings, visits, follow-ups, expenses), evaluate performance (indicators) and produce reports (PDF/Excel). ASP.NET Core 8, EF Core + MySQL (SQLite for dev/tests), Identity (cookie for Razor UI, JWT for `/api`), Razor Pages. Phases 1–5 are implemented; see `docs/roadmap.md` for status and per-phase reports.

## Hard rules
* **No paid or keyed external services.** Google Places/Maps API, Meta, etc. are *not integrated* on purpose (shown as such in *Sources de données*). Free keyless sources only (OpenStreetMap Overpass, polite public-page reading, local parsing of pasted map links); everything else goes through the Excel/CSV template (`TemplateService`). Never scrape Google, bypass CAPTCHA/login/robots.txt, or add a key to the code.
* **Authorization is server-side**: permission policies + `BusinessService.Scoped()` for data scope. Out-of-scope ids behave like missing ones (404). Never rely on the UI.
* **Never invent data**: absent = "non indiqué/Non renseigné"; indicators with a missing basis are "Non calculable", not 0.
* Confirmed (human-verified) values are never overwritten silently (`FieldUpdater`). Duplicates are flagged, never auto-merged.
* Saved reports are frozen snapshots; do not recompute them.
* No secrets in the repo. Demo data only with `Seed:DemoData=true` and marked as demo.
* Schema changes only through EF migrations (`dotnet ef migrations add <Name> -p src/Prospecta.Infrastructure -s src/Prospecta.Web`). Test on real MySQL: SQLite hides length/APPLY/decimal issues.

## Layout
`src/Prospecta.Domain` (entities, pure rules) ← `Application` (use-case services, ports, permissions; references EF Core by design) ← `Infrastructure` (EF/MySQL, Identity, seeding, PDF renderer, HTTP clients) ← `Web` (API controllers `/api/v1`, Razor Pages, security middleware). Tests: `tests/Prospecta.UnitTests`, `tests/Prospecta.IntegrationTests`; E2E + accessibility: `e2e/`.

## Commands
```bash
dotnet tool restore && dotnet build Prospecta.sln          # warnings are errors
dotnet run --project src/Prospecta.Web                      # http://localhost:5190, SQLite + demo accounts (see README)
dotnet test Prospecta.sln                                   # unit + integration on SQLite
PROSPECTA_TEST_MYSQL='Server=localhost;User=u;Password=p;' dotnet test tests/Prospecta.IntegrationTests   # same suite on real MySQL
cd e2e && npm install && BASE_URL=http://localhost:5190 node run.mjs   # Chromium E2E + axe-core (needs the app running with demo data)
scripts/backup.sh | restore.sh | verify-backup.sh           # see docs/operations.md
```
Tests run sequentially (shared process-wide env for the DB selection). No test calls the real internet: Overpass and page fetching are faked.

## Conventions
Namespaces file-scoped; French user-facing text through pages (menu/common labels via `Strings.T`); errors use `{success,message,errors}`; every state-changing use case records an audit entry; large lists are paged with a capped page size.
