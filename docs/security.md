# Sécurité (Phase 1)
* Mots de passe Identity (10 car., complexité), verrouillage 5 échecs/15 min, limitation de débit sur les connexions, messages d'échec génériques.
* Autorisation **serveur** : politiques par permission + contrôle de portée par fiche ; l'UI ne fait que masquer. Compte désactivé / mot de passe réinitialisé = sessions révoquées immédiatement (security stamp vérifié à chaque requête).
* Permissions de rôle modifiables par l'admin, effet immédiat ; le rôle Admin ne peut pas perdre la gestion des utilisateurs/rôles ; le dernier admin actif est protégé.
* Imports : extensions .csv/.xlsx, 5 Mo, 5 000 lignes, 60 colonnes, garde anti « zip bomb », erreurs d'analyse = 400 propre. Exports : neutralisation des formules (= + - @), plafond 20 000 lignes, droits requis.
* En-têtes : CSP stricte (pas de script inline), X-Frame-Options DENY, nosniff ; cookies HttpOnly/SameSite=Lax/Secure ; antiforgery sur les formulaires.
* Erreurs : corps standard `{success,message,errors}`, aucune trace exposée. Secrets hors du code (variables d'environnement). JWT : secret obligatoire hors développement.
* À prévoir (phase 5) : sauvegarde/restauration documentées (mysqldump + fichiers), procédure de suppression/correction de données personnelles, audit de dépendances.
