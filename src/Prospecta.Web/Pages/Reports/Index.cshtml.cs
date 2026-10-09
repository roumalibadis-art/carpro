using Microsoft.AspNetCore.Mvc;
using Prospecta.Application.Common;
using Prospecta.Application.Reporting;
using Prospecta.Domain.Common;

namespace Prospecta.Web.Pages.Reports;

public class IndexModel(ReportService service) : AppPage
{
    [BindProperty(SupportsGet = true)] public bool Team { get; set; }
    [BindProperty(SupportsGet = true)] public ReportType? Type { get; set; }
    [BindProperty(Name = "Page", SupportsGet = true)] public int PageNo { get; set; } = 1;
    public PagedResult<ReportSummary> Result { get; private set; } = new([], 0, 1, 25);

    public async Task OnGetAsync() => Result = await service.ListAsync(Team, Type, PageNo, 25);

    public static string TypeLabel(ReportType t) => t switch { ReportType.MarketStudy => "Étude de marché", ReportType.OutingBalance => "Bilan de sortie", ReportType.Individual => "Rapport individuel", _ => "Rapport du responsable" };
}
