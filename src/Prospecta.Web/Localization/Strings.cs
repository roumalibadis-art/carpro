using System.Globalization;

namespace Prospecta.Web.Localization;

/// <summary>
/// Minimal translation seam: layout and common labels go through <see cref="T"/>. French is the default and the only
/// complete catalogue; adding Arabic means adding an "ar" dictionary here (and RTL styles) — pages not yet migrated keep French text.
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalogues = new()
    {
        ["fr"] = new()
        {
            ["app.name"] = "Prospecta", ["nav.dashboard"] = "Tableau de bord", ["nav.map"] = "Carte des entreprises", ["nav.businesses"] = "Entreprises",
            ["nav.collect"] = "Recherche et collecte", ["nav.import"] = "Imports", ["nav.duplicates"] = "Doublons", ["nav.campaigns"] = "Campagnes",
            ["nav.outings"] = "Sorties commerciales", ["nav.visits"] = "Visites et activités", ["nav.followups"] = "Relances", ["nav.studies"] = "Rapports d'étude",
            ["nav.myreports"] = "Mes rapports", ["nav.evaluation"] = "Évaluation", ["nav.users"] = "Utilisateurs", ["nav.roles"] = "Permissions",
            ["nav.sources"] = "Sources de données", ["nav.settings"] = "Paramètres", ["nav.audit"] = "Journal d'activité", ["nav.soon"] = "bientôt",
            ["action.save"] = "Enregistrer", ["action.cancel"] = "Annuler", ["action.search"] = "Rechercher", ["action.reset"] = "Réinitialiser",
            ["action.logout"] = "Déconnexion", ["action.new"] = "Nouvelle entreprise", ["common.none"] = "non indiqué", ["common.empty"] = "Aucun résultat.",
        },
    };

    public static string T(string key)
    {
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (Catalogues.TryGetValue(lang, out var c) && c.TryGetValue(key, out var v)) return v;
        return Catalogues["fr"].GetValueOrDefault(key, key);
    }

    public static bool IsRtl => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
}
