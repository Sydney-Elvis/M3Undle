using M3Undle.Web.Application;
using MudBlazor;

namespace M3Undle.Web.Components.Layout;

internal static class SystemEventBadgePresentation
{
    public static Color ColorFor(SystemEventSummary summary) => summary.HighestSeverity switch
    {
        SystemEventSeverity.Error => Color.Error,
        SystemEventSeverity.Warning => Color.Warning,
        SystemEventSeverity.Info => Color.Info,
        _ => Color.Default,
    };
}
