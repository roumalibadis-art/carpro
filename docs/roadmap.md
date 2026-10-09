# Feuille de route

| Phase | État |
|---|---|
| 1 — Socle fonctionnel | **Terminée** (voir rapport) |
| 2 — Prospection (campagnes, affectations, sorties, visites, activités, relances, dépenses) | **Terminée** (voir rapport) |
| 3 — Reporting & évaluation (rapports A–D, indicateurs, PDF/Excel, snapshots) | **Terminée** |
| 4 — Collecte externe **gratuite et sans clé** (OpenStreetMap, URL publiques, modèle Excel) | **Terminée** (voir rapport) |
| 5 — Finalisation (carte, mobile, sécurité, E2E, sauvegarde/déploiement) | **Terminée** |

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

## Rapport de la phase 5 — finalisation
**Terminé** :
* **Carte interactive** (phase 4) et **optimisation mobile** : menu burger, tableaux en cartes, aucun débordement horizontal (vérifié sur 5 pages en 390 px).
* **Audit de sécurité automatisé** (`SecurityAuditTests`) : balayage anonyme de toutes les routes API, balayage « commercial » des routes de gestion, jetons falsifiés, en-têtes et absence de fuite, injections inertes, XSS stocké encodé, sur-envoi ignoré, limitation de débit, purge des données ; politiques par permission ajoutées sur les contrôleurs (défense en profondeur) ; paquet SQLite natif vulnérable remplacé par une version corrigée ; `npm audit` : 0 vulnérabilité.
* **Tests de bout en bout** (`e2e/`, Chromium réel) : 12 parcours fonctionnels (connexion, filtres géographiques, création, doublon, import du modèle, campagne → visite → relance, indicateurs, rapport → PDF/Excel, carte, états honnêtes des connecteurs, mobile) + **accessibilité axe-core WCAG 2 A/AA sur 18 pages : 0 violation sérieuse/critique** — 30/30, rejouable sur une base déjà utilisée.
* **Sauvegarde / restauration** (`scripts/`) : sauvegarde cohérente, contrôle d'intégrité, SHA-256, rotation, GPG optionnel ; restauration refusant un fichier altéré ; vérification par restauration dans une base jetable — testés sur MySQL réel. **Mise à niveau** d'une base existante de la phase 1 à la phase 4 testée (données et droits conservés).
* **Déploiement** : `dotnet publish` Release démarré en mode Production (refus de démarrer sans `Jwt__Secret`, Swagger fermé, CSP, HTTPS via proxy), unité systemd durcie, nginx + Let's Encrypt, fichier d'environnement modèle (`deploy/`, `docs/operations.md`).
* **Conservation / correction / suppression** : purge définitive tracée (`Data.Purge`, *Admin › Données*), documentée.
* Documentation : `README`, `CLAUDE.md`, `docs/{architecture,api,database,security,operations,roadmap}.md`.
**Défauts trouvés et corrigés grâce à cette phase** : recherche « symboles » qui renvoyait tout ; limiteur qui bloquait l'affichage de la page de connexion ; redémarrage plantant (seeding non idempotent) ; 400 avant 403 sur les routes de gestion ; clé de quota trop longue pour MySQL ; libellés de formulaires non associés, contrastes et page d'erreur sans `lang`/`title`.
**Résultats finaux** : 101 tests unitaires + 54 d'intégration (SQLite), les 54 aussi sur MySQL 8 réel, E2E 30/30 : tous verts.
**Reste à votre charge / limites connues** : première exécution de la collecte OpenStreetMap contre l'instance publique (non joignable depuis l'environnement de développement) ; hébergement TLS, secrets et sauvegardes hors site ; rapports non planifiables automatiquement ; traduction arabe (couche prête, catalogue français seul) ; pas de concurrence optimiste sur les fiches ; pas de stockage de pièces justificatives (référence texte).
