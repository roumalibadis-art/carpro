# Feuille de route

| Phase | État |
|---|---|
| 1 — Socle fonctionnel | **Terminée** (voir rapport) |
| 2 — Prospection (campagnes, affectations, sorties, visites, activités, relances, dépenses) | **Terminée** (voir rapport) |
| 3 — Reporting & évaluation (rapports A–D, indicateurs, PDF/Excel, snapshots) | **Terminée** |
| 4 — Collecte externe **gratuite et sans clé** (OpenStreetMap, URL publiques, modèle Excel) | **Terminée** (voir rapport) |
| 5 — Finalisation (carte, mobile, tests E2E, sauvegarde/déploiement) | À faire |

## Rapport de la phase 1
**Terminé** : voir README « État ». **Partiel** : colonnes configurables (préférence navigateur), vues partagées (équipe) sans notion d'équipe hiérarchique (le champ `ManagerId` existe, utilisé en phase 2/3), i18n (couche de traduction en place, catalogue français uniquement ; pages non encore migrées en dur), pas de concurrence optimiste sur les fiches (historique conservé, dernier enregistrement gagne).
**Restant** : filtres campagne / relance en retard (phase 2), carte interactive (phase 5), PDF.
**Tests exécutés** (vraiment) : 43 unitaires + 25 intégration sur SQLite ; les 25 intégration également sur MySQL 8.0 réel (migration `InitialCreate` appliquée) — tous verts.
Vérification manuelle : application lancée, connexion, cascade wilaya→daïra→commune par le vrai JS (Chromium), rendu desktop/mobile.
**Dépendances externes** : aucune pour la phase 1. Connecteurs Google/Meta : non implémentés, aucun n'est présenté comme intégré.
**Écarts** : schéma de test SQLite via `EnsureCreated` (les migrations ciblent MySQL) ; démo SQLite en développement.

## Rapport de la phase 2
**Terminé** : campagnes (cycle Brouillon→Active→Terminée/Annulée, participants, zone, objectif de visites, budget), cibles et affectations par campagne (ajout depuis la liste des entreprises ou par filtre, accès automatique du commercial aux fiches), sorties commerciales (participants, clôture, observations),
visites/appels/rendez-vous/démonstrations (planifiée, réalisée, annulée, reportée = états distincts ; plusieurs par entreprise ; résultat immuable une fois enregistré ; report = nouvelle action liée), effets explicites sur le traitement commercial (jamais en arrière) et le résultat, jamais sur le recensement,
relances (échéance, priorité, effectuée/reportée/annulée, chaînage de la prochaine échéance, retards), dépenses prévisionnelles/réelles par catégorie (transport, supports marketing, autres) avec référence de justificatif, notifications internes de relances (idempotentes par jour, exécution horaire + journal d'audit), historique des interactions sur la fiche entreprise, filtres « relance en retard » côté relances, nouvelles permissions (`Campaign.Manage`, `Outing.Manage`, `Activity.Record`, `Activity.ViewAll`).
**Partiel** : pièces justificatives = référence texte (pas de stockage de fichiers) ; filtre « campagne » dans la liste des entreprises et « relance en retard » dans ses filtres : non ajoutés (accessibles via Campagnes / Relances) ; pas d'envoi e-mail/SMS (notifications internes uniquement).
**Rappel de décision** : la migration `Phase2Prospecting` s'ajoute à `InitialCreate` ; les rôles existants reçoivent les nouveaux droits par défaut une seule fois (drapeau `seed:permissions:phase2`), les ajustements ultérieurs de l'admin ne sont jamais écrasés.
**Tests exécutés** : 43 unitaires + 30 intégration (SQLite) ; les 30 intégration aussi sur MySQL 8.0 réel ; mise à niveau vérifiée d'une base phase 1 contenant des données (12 entreprises conservées, migration appliquée, droits attribués). Tous verts.

## Rapport de la phase 3
**Terminé** : six indicateurs avec numérateur/dénominateur explicites, « Non calculable » (jamais 0 %) quand les données manquent, comparaison à la période comparable, filtres affichés (`/Evaluation`, `/api/v1/indicators`) ;
rapports A (étude de marché), B (bilan de sortie), C (individuel), D (responsable) avec faits et analyses générées séparés, champs absents = « Non renseigné », aperçu, enregistrement avec **instantané figé**, duplication (nouvelle analyse), partage (privé par défaut), validation par la hiérarchie, export **PDF** (PDFsharp/MigraDoc, MIT, police Liberation OFL embarquée) et **Excel** (une feuille par tableau, cellules en texte).
**Définitions** (documentées dans l'UI) : réalisation = visites planifiées réalisées ÷ planifiées (visite/rendez-vous/démo ; un report est remplacé par son successeur, donc compté une fois) ; couverture = entreprises distinctes traitées ÷ affectées ; intérêt/conversion = ÷ entreprises distinctes contactées (dernier résultat de la période) ; relances = effectuées ÷ échues ; coût par visite = dépenses réelles des sorties ÷ visites réalisées.
**Restant / limites** : rapports non planifiables automatiquement ; le rapport A plafonne le tableau à 500 lignes et les fiches détaillées à 100 (mentionné dans le rapport).
**Tests** : 46 unitaires, 35 intégration (SQLite) et 35 sur MySQL 8 réel : verts. Un PDF réel a été généré et inspecté visuellement.

## Rapport de la phase 4 — collecte gratuite, sans clé, sans service payant
**Décision** : aucune API payante ni à clé (Google Places, Meta…) n'est intégrée, et aucune extraction automatisée de Google Maps n'est faite. Ces sources sont affichées « Non intégré » dans *Sources de données*, avec l'alternative : le **modèle Excel/CSV**.
**Terminé** :
* **OpenStreetMap (Overpass)** — vraie recherche par zone (wilaya/daïra via ses communes/commune) ou boîte géographique + activité (correspondance activité → étiquettes OSM, administrable) + mots-clés ; gratuit, sans clé ; ODbL/attribution conservée ; limites de débit et quota quotidien (`ConnectorQuota`), miroirs de secours, délais d'attente, journal des collectes (`DataCollectionJobs/Results`), revue avant import (nouvelles / doublons probables / déjà importées / écartées), reprise après erreur, jamais de doublon sur ré-exécution.
* **URL publique** — site web (JSON-LD schema.org, meta, tel:) lu poliment : robots.txt respecté, garde SSRF (adresses publiques uniquement, contrôlée à la connexion), redirections re-validées, limites de taille/temps, refus explicite des CAPTCHA/403/429 (jamais contournés) ; lien Google Maps/OpenStreetMap collé : coordonnées extraites **localement**, sans requête vers Google.
* **Modèle Excel/CSV** — `modele-import-entreprises.xlsx` (feuilles Entreprises, Listes, Exemple, Communes, Instructions ; listes déroulantes ; téléphones en texte), `.csv` et modèle de géographie ; les en-têtes sont reconnus automatiquement par l'assistant d'import (testé de bout en bout).
* **Actualisation** — sélection par ancienneté de vérification / téléphone manquant / erreur de contact / changement signalé, comparaison au site de l'entreprise, propositions appliquées en origine « externe » ; une valeur confirmée n'est jamais écrasée.
* **Carte interactive** — Leaflet + MarkerCluster **embarqués** (BSD-2/MIT, aucun CDN) sur fonds OpenStreetMap (gratuits, sans clé, attribution) ; filtres activité/commune/statut/campagne/commercial, popup → fiche, périmètre de droits respecté.
* Filtres « campagne » et « relance en retard » ajoutés à la liste des entreprises (restes de la phase 2).
**Limites** : OpenStreetMap couvre inégalement l'Algérie (résultats toujours « à vérifier ») ; les daïras ne sont pas des limites OSM (recherche via leurs communes) ; **le serveur de collecte n'a pas pu être joint depuis l'environnement de développement** (accès réseau sortant restreint) : la logique est testée avec un faux serveur Overpass et un serveur HTTP local, pas contre l'instance publique réelle. À essayer une première fois depuis votre serveur.
**Tests** : 101 unitaires ; 45 intégration sur SQLite et sur MySQL 8 réel (un vrai défaut de longueur de colonne n'a été vu que sur MySQL, corrigé).
