# Sécurité (Phase 1)
* Mots de passe Identity (10 car., complexité), verrouillage 5 échecs/15 min, limitation de débit sur les connexions, messages d'échec génériques.
* Autorisation **serveur** : politiques par permission + contrôle de portée par fiche ; l'UI ne fait que masquer. Compte désactivé / mot de passe réinitialisé = sessions révoquées immédiatement (security stamp vérifié à chaque requête).
* Permissions de rôle modifiables par l'admin, effet immédiat ; le rôle Admin ne peut pas perdre la gestion des utilisateurs/rôles ; le dernier admin actif est protégé.
* Imports : extensions .csv/.xlsx, 5 Mo, 5 000 lignes, 60 colonnes, garde anti « zip bomb », erreurs d'analyse = 400 propre. Exports : neutralisation des formules (= + - @), plafond 20 000 lignes, droits requis.
* En-têtes : CSP stricte (pas de script inline), X-Frame-Options DENY, nosniff ; cookies HttpOnly/SameSite=Lax/Secure ; antiforgery sur les formulaires.
* Erreurs : corps standard `{success,message,errors}`, aucune trace exposée. Secrets hors du code (variables d'environnement). JWT : secret obligatoire hors développement.
* À prévoir (phase 5) : sauvegarde/restauration documentées (mysqldump + fichiers), procédure de suppression/correction de données personnelles, audit de dépendances.

## Audit de la phase 5 (vérifié par tests automatiques)
* **Balayage anonyme** : chaque route `/api` (plus de 90, détectées automatiquement) renvoie 401 sans jeton (hors connexion). **Balayage commercial** : toutes les routes de gestion répondent 403. Les politiques par permission sont posées sur les contrôleurs **en plus** des contrôles dans les services (défense en profondeur).
* Jetons : signature altérée, `alg=none`, clé étrangère, texte quelconque → 401. Compte désactivé ou mot de passe réinitialisé → sessions révoquées immédiatement.
* En-têtes : CSP stricte (aucun script inline ni `unsafe-eval`, aucune ressource externe : Leaflet et polices du PDF sont embarqués), `X-Frame-Options: DENY`, `nosniff`, pas d'en-tête `Server`, cookie `HttpOnly; SameSite=Lax` (+`Secure` en production), antiforgery sur tous les formulaires.
* Injection : tentatives SQL/LIKE (`'`, `%`, `_`, `\`) inertes (requêtes paramétrées) ; balisage stocké (`<script>`, `<img onerror>`) encodé dans les pages et rapports ; formules `= + - @` neutralisées dans les exports CSV/Excel ; sur-envoi de champs serveur ignoré.
* Collecte : garde SSRF (adresses publiques seulement, contrôlée à la connexion TCP — un DNS piégé ou une redirection vers `169.254.169.254` est refusé), robots.txt respecté, CAPTCHA/403/429 jamais contournés, quotas et délais, requêtes Overpass construites à partir de filtres validés (pas d'injection de requête).
* Limitation de débit : 10 soumissions de connexion/minute/IP (l'affichage de la page n'est pas limité) ; verrouillage après 5 échecs.
* Défauts trouvés et corrigés pendant l'audit : recherche faite uniquement de symboles qui renvoyait tout ; limiteur de connexion qui bloquait aussi l'affichage de la page ; redémarrage plantant (seeding non idempotent) ; contrôleurs de gestion répondant 400 avant 403 ; clés de quota trop longues pour MySQL ; libellés non associés aux champs et contrastes insuffisants (accessibilité).
* À votre charge en production : TLS (nginx + Let's Encrypt), secrets (`/etc/prospecta/prospecta.env` en 600), sauvegardes hors site (`docs/operations.md`), mises à jour régulières du runtime .NET et des paquets (`dotnet list package --vulnerable`).
