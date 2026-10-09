# Base de données (EF Core, migrations MySQL)
Migrations : `InitialCreate` (socle), `Phase2Prospecting`, `Phase3Reporting`, `Phase4Collection`. Schéma uniquement par migrations ; au démarrage `Database__AutoMigrate=true` les applique.
* Identité : `AspNetUsers/Roles/UserRoles/RoleClaims` (les permissions sont des claims de rôle ; modifiables dans l'UI).
* Référentiels : `GeographicAreas` (hiérarchie 4 niveaux), `BusinessCategories` (+`OsmFilter`), `StatusValues` (3 dimensions : recensement/traitement/résultat).
* Entreprises : `Businesses` (suppression logique, colonnes dérivées de recherche), `BusinessSources`, `FieldProvenances` (origine par champ), `BusinessHistory`, `BusinessAssignments`, `DuplicateCandidates`.
* Imports/collecte : `ImportBatches/ImportRows`, `DataCollectionJobs/Results`, `ConnectorUsage` (quotas), `SavedFilters`.
* Prospection : `Campaigns`, `CampaignParticipants`, `CampaignTargets`, `Outings`, `OutingParticipants`, `Visits`, `FollowUps`, `Expenses`, `Notifications`.
* Reporting : `Reports` (paramètres, partage, validation), `ReportSnapshots` (chiffres figés).
* Transverse : `AuditLogs`, `SystemFlags`.
Index : composites sur les filtres réels (zone, activité, statuts, dates), uniques sur les paires de doublons, cibles de campagne, notifications (dédoublonnage des rappels). Montants : `decimal(14,2)`.
