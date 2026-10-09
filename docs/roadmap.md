# Feuille de route

| Phase | État |
|---|---|
| 1 — Socle fonctionnel | **Terminée** (voir rapport) |
| 2 — Prospection (campagnes, affectations, sorties, visites, activités, relances, dépenses) | À faire |
| 3 — Reporting & évaluation (rapports A–D, indicateurs, PDF/Excel, snapshots) | À faire |
| 4 — Collecte externe (Google Places, sources publiques, quotas, jobs, actualisation) | À faire — nécessite une clé API Google Places (Places API, facturation activée) ; Meta/Facebook : permissions d'app et revue Meta |
| 5 — Finalisation (carte, mobile, tests E2E, sauvegarde/déploiement) | À faire |

## Rapport de la phase 1
**Terminé** : voir README « État ». **Partiel** : colonnes configurables (préférence navigateur), vues partagées (équipe) sans notion d'équipe hiérarchique (le champ `ManagerId` existe, utilisé en phase 2/3), i18n (couche de traduction en place, catalogue français uniquement ; pages non encore migrées en dur), pas de concurrence optimiste sur les fiches (historique conservé, dernier enregistrement gagne).
**Restant** : filtres campagne / relance en retard (phase 2), carte interactive (phase 5), PDF.
**Tests exécutés** (vraiment) : 43 unitaires + 25 intégration sur SQLite ; les 25 intégration également sur MySQL 8.0 réel (migration `InitialCreate` appliquée) — tous verts.
Vérification manuelle : application lancée, connexion, cascade wilaya→daïra→commune par le vrai JS (Chromium), rendu desktop/mobile.
**Dépendances externes** : aucune pour la phase 1. Connecteurs Google/Meta : non implémentés, aucun n'est présenté comme intégré.
**Écarts** : schéma de test SQLite via `EnsureCreated` (les migrations ciblent MySQL) ; démo SQLite en développement.
