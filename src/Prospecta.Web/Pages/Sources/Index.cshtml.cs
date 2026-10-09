using Prospecta.Application.Collection;

namespace Prospecta.Web.Pages.Sources;

public class IndexModel(CollectionService service) : AppPage
{
    public IReadOnlyList<ConnectorInfo> Connectors { get; private set; } = [];

    public async Task OnGetAsync() => Connectors = await service.ConnectorsAsync();

    public static (string Label, string Css) Badge(ConnectorState s) => s switch
    {
        ConnectorState.Available => ("Disponible — gratuit, sans clé", "ok"),
        ConnectorState.FileOnly => ("Par fichier Excel/CSV", "ok"),
        ConnectorState.Disabled => ("Désactivé par l'administrateur", "warn"),
        _ => ("Non intégré (voir alternative)", "bad"),
    };
}
