using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Imports;
using Prospecta.Domain.Common;

namespace Prospecta.Application.Collection;

/// <summary>The Excel/CSV templates that replace any paid or keyed source: headers match what the import wizard recognises automatically.</summary>
public sealed class TemplateService(IAppDbContext db)
{
    public static readonly string[] Headers =
    [
        "Nom commercial", "Nom légal", "Activité", "Sous-activité", "Wilaya", "Daïra", "Commune", "Quartier", "Adresse", "Latitude", "Longitude", "Plus Code",
        "Téléphone", "Site web", "URL Google Maps", "URL source", "Description", "Identifiant fournisseur",
    ];

    private static readonly string[] Required = ["Nom commercial"];

    private static readonly string[][] Examples =
    [
        ["EXEMPLE — Agence Soleil Location", "", "Location de véhicules", "Location de voitures", "Alger", "", "Rouïba", "", "12 rue de l'Indépendance", "36.7391", "3.2812", "", "0555 12 34 56", "https://exemple-agence.dz", "https://www.google.com/maps/place/Exemple/@36.7391,3.2812,17z", "", "Agence fictive : supprimez cette ligne", "ex-001"],
        ["EXEMPLE — Auto Express", "", "Location de véhicules", "", "Alger", "", "Réghaïa", "", "Zone industrielle", "", "", "", "023 85 12 34", "", "", "https://www.facebook.com/exemple", "Les champs inconnus restent vides", ""],
    ];

    private static readonly (string Header, string Rule)[] Rules =
    [
        ("Nom commercial", "OBLIGATOIRE. 200 caractères max."), ("Nom légal", "Facultatif, seulement s'il est public."),
        ("Activité", "Doit exister dans la liste « Activités » (menu déroulant). Sinon la ligne est signalée, rien n'est créé."), ("Sous-activité", "Doit appartenir à l'activité choisie."),
        ("Wilaya", "Nom ou code à 2 chiffres (ex. Alger ou 16). Menu déroulant."), ("Daïra", "Doit appartenir à la wilaya (voir feuille « Communes »)."), ("Commune", "Doit exister dans le référentiel (feuille « Communes »). Si elle manque, l'administrateur l'ajoute ou l'importe."),
        ("Quartier", "Facultatif ; doit exister pour cette commune."), ("Adresse", "Texte libre, 300 caractères max."), ("Latitude", "Décimal avec point ou virgule (ex. 36.7391). Doit être en Algérie."), ("Longitude", "Décimal (ex. 3.2812). Latitude et longitude vont ensemble."),
        ("Plus Code", "Facultatif."), ("Téléphone", "Numéro algérien : 0555 12 34 56, +213 555 12 34 56, 023 85 12 34. Un numéro invalide fait rejeter la ligne."), ("Site web", "URL http(s) publique."),
        ("URL Google Maps", "Lien copié depuis Google Maps (relevé manuellement). Aucune extraction automatique de Google n'est faite."), ("URL source", "Page publique d'où provient l'information."),
        ("Description", "2000 caractères max."), ("Identifiant fournisseur", "Identifiant propre à votre source (sert à reconnaître les doublons lors de réimports)."),
    ];

    public async Task<(byte[] Content, string ContentType, string FileName)> BusinessTemplateAsync(string format, CancellationToken ct = default)
    {
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder("﻿" + string.Join(";", Headers) + "\r\n");
            return (Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "modele-import-entreprises.csv");
        }

        var wilayas = await db.GeographicAreas.AsNoTracking().Where(a => a.Level == GeoLevel.Wilaya && a.IsActive).OrderBy(a => a.Code).Select(a => a.Name).ToListAsync(ct);
        var cats = await db.BusinessCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new { c.Name, ParentName = c.Parent!.Name }).ToListAsync(ct);
        var communes = await db.GeographicAreas.AsNoTracking().Where(a => a.Level == GeoLevel.Commune && a.IsActive).OrderBy(a => a.Name).Take(5000)
            .Select(a => new { a.Name, Daira = a.Parent!.Name, Wilaya = a.Parent!.Parent!.Name }).ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Entreprises");
        for (var i = 0; i < Headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1); c.Value = Headers[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.FromHtml(Required.Contains(Headers[i]) ? "#FFE699" : "#DDEBF7");
            ws.Column(i + 1).Width = Math.Max(16, Headers[i].Length + 4);
        }

        ws.Column(1).Width = 34; ws.Column(9).Width = 36; ws.Column(17).Width = 40;
        ws.SheetView.FreezeRows(1);
        ws.Column(13).Style.NumberFormat.Format = "@"; // phone numbers stay text: the leading zero is kept
        ws.Range(2, 13, 5001, 13).Style.NumberFormat.Format = "@";

        var lists = wb.Worksheets.Add("Listes");
        lists.Cell(1, 1).Value = "Wilayas"; lists.Cell(1, 2).Value = "Activités"; lists.Cell(1, 3).Value = "Sous-activités";
        for (var i = 0; i < wilayas.Count; i++) lists.Cell(i + 2, 1).Value = wilayas[i];
        var roots = cats.Where(c => c.ParentName is null).Select(c => c.Name).ToList();
        for (var i = 0; i < roots.Count; i++) lists.Cell(i + 2, 2).Value = roots[i];
        var subs = cats.Where(c => c.ParentName is not null).Select(c => $"{c.Name}").Distinct().ToList();
        for (var i = 0; i < subs.Count; i++) lists.Cell(i + 2, 3).Value = subs[i];
        lists.Row(1).Style.Font.Bold = true; lists.Columns().AdjustToContents();
        if (wilayas.Count > 0) ws.Range(2, 5, 5001, 5).CreateDataValidation().List(lists.Range(2, 1, wilayas.Count + 1, 1), true);
        if (roots.Count > 0) ws.Range(2, 3, 5001, 3).CreateDataValidation().List(lists.Range(2, 2, roots.Count + 1, 2), true);
        if (subs.Count > 0) { var dv = ws.Range(2, 4, 5001, 4).CreateDataValidation(); dv.List(lists.Range(2, 3, subs.Count + 1, 3), true); dv.IgnoreBlanks = true; dv.ShowErrorMessage = false; }

        var ex = wb.Worksheets.Add("Exemple");
        ex.Cell(1, 1).Value = "Lignes d'EXEMPLE fictives : ne pas importer. Copiez la structure dans la feuille « Entreprises ».";
        ex.Cell(1, 1).Style.Font.Bold = true; ex.Cell(1, 1).Style.Font.FontColor = XLColor.Red;
        for (var i = 0; i < Headers.Length; i++) { ex.Cell(2, i + 1).Value = Headers[i]; ex.Cell(2, i + 1).Style.Font.Bold = true; }
        for (var r = 0; r < Examples.Length; r++) for (var c = 0; c < Headers.Length; c++) ex.Cell(r + 3, c + 1).SetValue(Examples[r][c]);
        ex.Columns().AdjustToContents(1, 5);

        var com = wb.Worksheets.Add("Communes");
        com.Cell(1, 1).Value = "Wilaya"; com.Cell(1, 2).Value = "Daïra"; com.Cell(1, 3).Value = "Commune"; com.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < communes.Count; i++) { com.Cell(i + 2, 1).Value = communes[i].Wilaya; com.Cell(i + 2, 2).Value = communes[i].Daira; com.Cell(i + 2, 3).Value = communes[i].Name; }
        if (communes.Count == 0) com.Cell(2, 1).Value = "Aucune commune n'est encore chargée : demandez à l'administrateur d'importer le référentiel (Admin › Géographie).";
        com.Columns().AdjustToContents(1, 200);

        var ins = wb.Worksheets.Add("Instructions");
        ins.Cell(1, 1).Value = "Modèle d'import d'entreprises — Prospecta"; ins.Cell(1, 1).Style.Font.Bold = true; ins.Cell(1, 1).Style.Font.FontSize = 14;
        var lines = new[]
        {
            "1. Remplissez la feuille « Entreprises » : une ligne par entreprise, à partir de la ligne 2. Ne renommez pas les en-têtes.",
            "2. Seul « Nom commercial » est obligatoire. Laissez vide ce que vous ignorez : une donnée absente reste « non indiquée », n'inventez rien.",
            "3. Utilisez les listes déroulantes (Wilaya, Activité). Les noms de communes doivent figurer dans la feuille « Communes ».",
            "4. Dans Prospecta : Imports › choisir le fichier › vérifier l'association des colonnes › lire l'aperçu (lignes valides, invalides, doublons) › confirmer.",
            "5. Les lignes en erreur sont signalées avec la cause ; elles ne sont pas importées. Un fichier de 5 000 lignes et 5 Mo maximum.",
            "6. Respectez les conditions d'utilisation de vos sources : ne copiez que des informations publiques et professionnelles.",
            "",
            "Colonne — règle",
        };
        for (var i = 0; i < lines.Length; i++) ins.Cell(i + 3, 1).Value = lines[i];
        var row = lines.Length + 3;
        foreach (var (h, rule) in Rules) { ins.Cell(row, 1).Value = h + (Required.Contains(h) ? " *" : ""); ins.Cell(row, 2).Value = rule; row++; }
        ins.Column(1).Width = 70; ins.Column(2).Width = 100;
        wb.Worksheet("Entreprises").SetTabColor(XLColor.Gold);
        // The data sheet must come first: the import reads the first worksheet.
        ws.Position = 1;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return (ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "modele-import-entreprises.xlsx");
    }

    public (byte[] Content, string ContentType, string FileName) GeographyTemplate()
    {
        var csv = "﻿wilaya;daira;commune;quartier\r\nAlger;Rouïba;Rouïba;\r\nAlger;Réghaïa;Réghaïa;Centre-ville\r\n";
        return (Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", "modele-import-geographie.csv");
    }

    /// <summary>Header names exactly as the import wizard would auto-map them (used by tests and the UI).</summary>
    public static bool AllHeadersAutoMap() => ImportService.SuggestMapping(Headers).Count == Headers.Length;
}
