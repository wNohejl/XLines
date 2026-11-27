using Microsoft.Extensions.DependencyInjection;

namespace XLines.Web.Tests;

/// <summary>
/// The Desk's bench (<see cref="XLines.Desk.Testing.DeskTestContext"/>) with this application's
/// answers to what the desk asks its host: its name, its clock and what it can open. The
/// application's own catalogue is the honest answer for the last, because these tests read its keys
/// by name. Registered after the base, so these are the answers the container gives.
/// </summary>
public abstract class DeskTestContext : XLines.Desk.Testing.DeskTestContext
{
    protected DeskTestContext()
    {
        Services.AddSingleton(new XLines.Desk.DeskBrand("X", "LINES", "test desk"));
        Services.AddSingleton(new XLines.Desk.DeskClock(
            XLines.Core.Analytics.LeagueClock.Zone, XLines.Web.Components.DisplayTime.Label));
        Services.AddSingleton<XLines.Desk.IWindowCatalog, XLines.Web.Windowing.AppWindowCatalog>();
        // The tier a panel reads to (ADR 0024): everything, as AddXLinesData answers for a host
        // that says nothing about who is reading.
        Services.AddSingleton<XLines.Core.Contracts.IDataTier>(XLines.Core.Contracts.FullDataTier.Instance);
        // The windows' data services, over whichever IDbContextFactory a test registers.
        XLines.Web.Services.PanelDataRegistration.AddXLinesPanelData(Services);
        // Line movement's, as Program.cs registers it; a test that hands the window its own replaces it.
        Services.AddScoped<XLines.Web.Services.ILineMovementData, XLines.Web.Services.LineMovementData>();
    }
}
