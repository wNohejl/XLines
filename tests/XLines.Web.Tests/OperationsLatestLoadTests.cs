using Bunit;
using XLines.Data;
using XLines.Desk.Primitives;
using XLines.Reliability;
using XLines.Web.Components.Panels;
using XLines.Web.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace XLines.Web.Tests;

/// <summary>
/// The latest load wins in the operations windows too (<see cref="LatestLoad"/>). Ops re-runs its
/// read of ten queries on every runs and alerts notice, Runs and Incidents on theirs; each now
/// cancels the read of the load before it, says nothing for the one cancelled, and lets go of the
/// one left when the window closes. Every connection is held at a gate its own token releases,
/// which also shows the token reaches the query.
/// </summary>
public class OpsLoadTests : DeskTestContext
{
    private readonly ConnectionGate _gate = new();

    public OpsLoadTests()
    {
        Services.AddScoped<DeskToasts>();
        Services.AddScoped(_ => new XLinesDbContext(GatedDatabase.Options(_gate)));
    }

    [Fact]
    public async Task A_newer_load_cancels_the_one_in_flight_and_disposal_cancels_the_rest()
    {
        // The read's first query is the operator's polling setting; it waits at the gate.
        var panel = RenderComponent<OpsPanel>();
        await LatestLoad.NewerCancelsOlderAndDisposalCancelsTheRest(panel, _gate.Reads);
    }
}

public class RunsLoadTests : DeskTestContext
{
    private readonly ConnectionGate _gate = new();

    public RunsLoadTests()
    {
        Services.AddSingleton<IDbContextFactory<XLinesDbContext>>(new GatedDbFactory(GatedDatabase.Options(_gate)));
    }

    [Fact]
    public async Task A_newer_load_cancels_the_one_in_flight_and_disposal_cancels_the_rest()
    {
        var panel = RenderComponent<RunsPanel>();
        await LatestLoad.NewerCancelsOlderAndDisposalCancelsTheRest(panel, _gate.Reads);
    }
}

public class IncidentsLoadTests : DeskTestContext
{
    private readonly ConnectionGate _gate = new();

    public IncidentsLoadTests()
    {
        Services.AddScoped<DeskToasts>();
        Services.AddScoped(_ => new XLinesDbContext(GatedDatabase.Options(_gate)));
        Services.AddScoped(sp => new IncidentService(sp.GetRequiredService<XLinesDbContext>(), NullLogger<IncidentService>.Instance));
    }

    [Fact]
    public async Task A_newer_load_cancels_the_one_in_flight_and_disposal_cancels_the_rest()
    {
        var panel = RenderComponent<IncidentsPanel>();
        await LatestLoad.NewerCancelsOlderAndDisposalCancelsTheRest(panel, _gate.Reads);
    }
}
