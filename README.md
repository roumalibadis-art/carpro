# Prospecta — recensement, prospection et rapports d'étude

Plateforme multi-utilisateur pour recenser des entreprises (par wilaya / daïra / commune / quartier / activité), les enrichir et dédupliquer,
puis (phases suivantes) organiser les sorties commerciales et produire les rapports. ASP.NET Core 8 (API + Razor Pages), EF Core, MySQL, Identity + JWT.

> Dépôt indépendant de `school` (USTHB Study). Voir `docs/architecture.md`, `docs/roadmap.md`, `docs/security.md`.

## État (Phases 1 et 2)
Terminé : authentification (cookie + JWT, verrouillage, limitation de débit), utilisateurs/rôles/permissions serveur, référentiels (géographie hiérarchique,
activités, statuts configurables), fiches entreprises (CRUD, provenance par champ, historique, vérification, suppression logique), 3 états indépendants
(recensement / traitement / résultat), filtres combinables + vues enregistrées + tri + pagination + colonnes configurables, affectation et actions en masse,
import CSV/Excel (aperçu, mapping, validation, doublons, compte rendu), export CSV/Excel filtré selon les droits, détection de doublons + comparaison + fusion,
journal d'audit, tableau de bord de recensement, import CSV de la géographie officielle.
Phase 2 : campagnes, cibles/affectations, sorties, visites et appels (historique complet), relances, dépenses, notifications internes.
**Non fait (annoncé « bientôt » dans l'UI)** : rapports PDF/Excel et indicateurs (phase 3) ;
connecteurs Google Places & sources publiques (phase 4) ; carte, tests E2E complets (phase 5). Aucun connecteur externe n'existe encore : rien n'est simulé.

## Démarrage rapide (développement, SQLite, données de démo)
```bash
dotnet tool restore
dotnet run --project src/Prospecta.Web          # http://localhost:5190  (Swagger : /swagger)
```
Comptes **de démonstration (dev uniquement)** : `admin@example.local / Admin#2026!x`, `manager@example.local / Manager#2026!x`, `sales@example.local / Sales#2026!xx`.
Les entreprises « [DÉMO] » et la zone « Zone démo Est » sont factices et marquées `IsDemo`.

## MySQL (production)
```bash
export ConnectionStrings__Default='Server=...;Database=prospecta;User=...;Password=...;'
export Database__Provider=MySql Jwt__Secret='<32+ caractères aléatoires>' Bootstrap__AdminEmail=... Bootstrap__AdminPassword=...
dotnet run --project src/Prospecta.Web -c Release   # applique les migrations puis démarre (Database__AutoMigrate=true)
```
Nouvelle migration : `dotnet ef migrations add <Nom> -p src/Prospecta.Infrastructure -s src/Prospecta.Web`. Voir `.env.example`.
Premier chargement géographique : se connecter en admin → *Géographie* → importer un CSV `wilaya;daira;commune[;quartier]` issu d'une source officielle
(les 58 wilayas sont pré-chargées ; les daïras/communes ne sont **pas** inventées).

## Tests
```bash
dotnet test Prospecta.sln                                                    # unitaires + intégration (SQLite en mémoire)
PROSPECTA_TEST_MYSQL='Server=localhost;User=u;Password=p;' dotnet test Prospecta.sln   # la même suite sur MySQL réel (bases jetables)
```
Résultats de la dernière exécution : voir `docs/roadmap.md`.

## Configuration
`appsettings.json` (valeurs par défaut sans secret) ; secrets via variables d'environnement / user-secrets. Clés : `Database:*`, `ConnectionStrings:Default`,
`Jwt:Secret` (obligatoire hors développement), `Bootstrap:*`, `Duplicates:*` (seuils), `Import:*` (limites), `RateLimit:LoginPerMinute`, `ForwardedHeaders:Enabled`, `Notifications:Enabled` (rappels horaires), `Swagger:Enabled`, `Seed:DemoData`.
