# API (`/api/v1`, JWT bearer) — Swagger : `/swagger` en développement
Connexion : `POST /auth/login {email,password}` → `{token,expiresAt}` ; `GET /auth/me`. Toutes les routes exigent un jeton, sauf `/auth/login` et `/health`. Erreurs : `{success:false,message,errors[]}` (400 validation, 401, 403 droit manquant, 404 hors périmètre/inexistant, 409 conflit, 429 limitation de débit, 502 source externe indisponible).
| Domaine | Routes principales (droit requis) |
|---|---|
| Référentiels | `GET/POST/PUT geo`, `categories`, `statuses` (écriture : `Reference.Manage`) |
| Entreprises | `GET/POST/PUT/DELETE businesses`, `{id}/verify`, `{id}/status`, `{id}/history`, `assign`, `bulk-status`, `export?format=csv|xlsx` |
| Doublons / imports | `duplicates` (+`{id}/merge|dismiss`, `rescan`), `imports` (upload → `{id}/mapping` → `{id}/commit`), `templates/businesses`, `templates/geography` |
| Prospection | `campaigns` (+targets), `outings` (+expenses), `visits` (`plan`, `log`, `{id}/complete|cancel|postpone`), `followups`, `notifications` |
| Évaluation | `indicators?from&to&campaignId&userId&communeId&categoryId`, `reports` (`preview`, enregistrement, `{id}/export?format=pdf|xlsx`, `duplicate`, `share`, `validate`) |
| Collecte gratuite | `collection/connectors`, `collection/search`, `collection/jobs…`, `collection/inspect-url`, `businesses/{id}/refresh-check|refresh-apply`, `map/points` |
| Administration | `users`, `roles`, `audit`, `saved-filters`, `dashboard`, `data/deleted`, `data/businesses/{id}` (purge) |
Les pages Razor (cookie) appellent les mêmes services : mêmes règles, mêmes droits.
