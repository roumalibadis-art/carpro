# Exploitation : installation, sauvegarde, restauration, conservation des données

## Installation (Linux, sans Docker)
1. MySQL 8 (ou MariaDB 10.6+) : `CREATE DATABASE prospecta CHARACTER SET utf8mb4; CREATE USER 'prospecta'@'localhost' IDENTIFIED BY '…'; GRANT ALL ON prospecta.* TO 'prospecta'@'localhost';`
2. Runtime : `apt install aspnetcore-runtime-8.0`.
3. Publication : `dotnet publish src/Prospecta.Web -c Release -o /opt/prospecta` ; créer l'utilisateur système `prospecta` ; `mkdir /opt/prospecta/logs && chown prospecta /opt/prospecta/logs`.
4. Configuration : copier `deploy/prospecta.env.example` vers `/etc/prospecta/prospecta.env` (droits 600) et renseigner **chaque** valeur (aucun secret dans le dépôt). `Jwt__Secret` : `openssl rand -base64 48`. Le premier administrateur est créé depuis `Bootstrap__*` uniquement si aucun utilisateur n'existe ; changez ce mot de passe à la première connexion, puis retirez ces deux variables.
5. Service : `deploy/prospecta.service` → `systemctl enable --now prospecta`. Reverse proxy TLS : `deploy/nginx.conf` (certificat gratuit Let's Encrypt).
6. Vérification : `curl https://prospecta.example.dz/health` → `Healthy`. Les migrations sont appliquées au démarrage (`Database__AutoMigrate=true`) ; mettez `false` si vous les appliquez à part (`dotnet ef database update`).
Le mode Production refuse de démarrer sans `Jwt__Secret` ; Swagger est désactivé (`Swagger__Enabled=true` pour l'ouvrir) ; aucune donnée de démonstration n'est créée.

## Sauvegarde
```bash
DB_NAME=prospecta DB_USER=prospecta MYSQL_PWD='…' BACKUP_DIR=/var/backups/prospecta RETENTION_DAYS=30 /opt/prospecta/scripts/backup.sh
```
`mysqldump --single-transaction` (sans verrou), compression, contrôle d'intégrité (`gzip -t`), somme SHA-256, rotation, chiffrement GPG optionnel (`GPG_RECIPIENT`). Planification : `0 2 * * * … backup.sh` (cron) ; copiez le dossier hors du serveur. L'application ne stocke aucun fichier métier sur disque (imports et rapports sont en base) : la base est la seule donnée à sauvegarder, avec `/etc/prospecta/prospecta.env`.

## Vérification périodique (à faire chaque semaine)
`DB_USER=… MYSQL_PWD=… scripts/verify-backup.sh /var/backups/prospecta/<fichier>.sql.gz prospecta` restaure la sauvegarde dans une base jetable et compare les volumes. Une sauvegarde jamais restaurée n'est pas une sauvegarde.

## Restauration
1. `systemctl stop prospecta`
2. `DB_USER=root MYSQL_PWD=… scripts/restore.sh /var/backups/prospecta/prospecta-AAAAMMJJTHHMMSSZ.sql.gz` (vérifie la somme SHA-256 avant d'écrire ; refuse un fichier altéré).
3. `systemctl start prospecta` puis contrôle `/health` et connexion. Les migrations manquantes éventuelles sont appliquées au démarrage.
Objectifs conseillés : RPO ≤ 24 h (sauvegarde nocturne), RTO ≤ 1 h. Testé : sauvegarde → restauration dans une base jetable → volumes identiques ; fichier altéré → refus (exit 2).

## Conservation, correction et suppression des données
* **Correction** : modifier la fiche ; l'ancienne valeur, l'auteur et la date restent dans l'historique. Une valeur « confirmée » ne se change qu'avec le droit de vérification.
* **Suppression** : l'entreprise est d'abord *supprimée* (retirée des listes, récupérable, historique conservé). Une demande d'effacement se traite ensuite par la **purge** (*Admin › Données*, droit `Data.Purge`) : fiche, sources, provenance, visites (noms des responsables rencontrés), relances, historique et liens de campagne sont effacés définitivement, avec une trace dans le journal (sans les données effacées). Purge automatique des suppressions de plus de N jours (N ≥ 30) possible.
* **Comptes** : désactiver (accès coupé immédiatement, historique d'audit conservé). Le journal d'activité contient l'identité des auteurs d'actions : durée de conservation à fixer par votre politique interne (suppression des lignes anciennes en base).
* **Provenance et licences** : chaque fiche garde sa source ; les données OpenStreetMap restent soumises à l'ODbL (attribution « © contributeurs OpenStreetMap »).
* **Collecte limitée au nécessaire** : seules des informations professionnelles publiques sont collectées ; aucun accès à des groupes privés ni à des données personnelles ; aucune clé ni service payant.

## Surveillance
`/health` (base de données) ; journaux : `logs/prospecta-AAAAMMJJ.log` (30 jours) ; échecs de connexion, verrouillages, exports, purges et collectes sont dans *Admin › Journal d'activité*.
