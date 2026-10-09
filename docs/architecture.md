# Architecture

## Constat de départ
Le dépôt `carpro` était vide ; `school` (USTHB Study) est un produit distinct et reste intact. Conventions reprises de `school` : .NET 8, Directory.Build/Packages
centralisés, couches Domain ← Application ← Infrastructure ← Web, Identity + JWT, Serilog, Swagger, tests xUnit + FluentAssertions.

## Couches
| Projet | Rôle |
|---|---|
| `Prospecta.Domain` | Entités, énumérations, règles pures (normalisation de noms, téléphones algériens). Aucune dépendance. |
| `Prospecta.Application` | Cas d'usage (BusinessService, DuplicateService, ImportService, ExportService, ReferenceService, UserAdminService, Dashboard, Audit), permissions, ports. |
| `Prospecta.Infrastructure` | EF Core (`AppDbContext`, migrations MySQL), Identity, seeding, store de permissions. |
| `Prospecta.Web` | Hôte unique : contrôleurs API `/api/v1` (JWT) + Razor Pages (cookie). Aucune logique métier. |

Écart assumé : `Application` référence EF Core (`IAppDbContext`) plutôt que d'ajouter une couche de dépôts, pour ne pas sur-complexifier. Deux schémas d'authentification
(JWT pour `/api`, cookie pour l'UI) sélectionnés par chemin ; les Razor Pages appellent les mêmes services que l'API.

## Modèle de données (Phase 1)
`AspNetUsers/Roles/RoleClaims` (permissions = claims de rôle) · `GeographicAreas` (Wilaya→Daïra→Commune→Quartier) · `BusinessCategories` (2 niveaux) ·
`StatusValues` (3 dimensions : Census, Processing, Outcome) · `Businesses` (soft delete, colonnes dérivées de recherche) · `BusinessSources` · `FieldProvenances`
(origine par champ : Confirmed/External/Manual/Estimated) · `BusinessHistory` · `BusinessAssignments` · `DuplicateCandidates` (paire unique) · `ImportBatches/ImportRows` ·
`SavedFilters` · `AuditLogs`. Phase 2+ : Campaigns, CampaignAssignments, FieldVisits, Activities, FollowUps, Expenses, Reports (+Parameters/Snapshots), DataCollectionJobs(+Results).

## Règles clés
* Portée des données : `BusinessService.Scoped()` est l'unique porte de lecture (ViewAll = organisation ; sinon seulement les fiches affectées). Hors portée = 404.
* Donnée confirmée jamais écrasée silencieusement (`FieldUpdater`) ; une source externe qui ne dit rien n'efface rien.
* Doublons : score « noisy-OR » de signaux (id fournisseur, téléphone, site, nom, adresse, proximité), seuils configurables, **jamais de fusion automatique**.
* Opérations critiques (création, fusion, affectation, import) en transaction ; actions importantes journalisées dans `AuditLogs`.
