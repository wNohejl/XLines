using System.Reflection;
using Bunit;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Desk;
using XLines.Desk.Primitives;
using XLines.Reliability;
using XLines.Web.Components.Panels;
using XLines.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Web.Tests;

/// <summary>
/// The Ops and Incidents windows' writes are the operator's (ADR 0018). A visitor's window draws
/// none of their keys, and each handler refuses a visitor itself, as the desk's jobs and the Slip
/// do: the markup is one way in, not the only one. Each handler is invoked here directly, as a
/// visitor, and must reach for nothing that writes — no database, no incident service, no toast
/// to say it tried.
/// </summary>
public class OperatorWriteGuardTests : DeskTestContext
{
    private sealed class Visitor : IDeskAccess
    {
        public bool CanOperate => false;
    }

    private int _databases;
    private int _incidentServices;

    public OperatorWriteGuardTests()
    {
        Services.AddSingleton<IDeskAccess>(new Visitor());
        Services.AddScoped<DeskToasts>();

        // Every write asks its scope for one of these first; both refuse, and both are counted.
        Services.AddScoped<XLinesDbContext>(_ =>
        {
            Interlocked.Increment(ref _databases);
            throw new InvalidOperationException("No database in a guard test.");
        });
        Services.AddScoped<IncidentService>(_ =>
        {
            Interlocked.Increment(ref _incidentServices);
            throw new InvalidOperationException("No incident service in a guard test.");
        });
    }

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Set(object panel, string field, object? value)
        => panel.GetType().GetField(field, Hidden)!.SetValue(panel, value);

    private static Task Invoke<TPanel>(IRenderedComponent<TPanel> panel, string handler, params object?[] args)
        where TPanel : Microsoft.AspNetCore.Components.IComponent
        => panel.InvokeAsync(() => (Task)typeof(TPanel).GetMethod(handler, Hidden)!.Invoke(panel.Instance, args)!);

    private void NothingWasReachedFor(string handler, int databases, int incidents)
    {
        Assert.True(databases == Volatile.Read(ref _databases), $"{handler} asked for the database as a visitor");
        Assert.True(incidents == Volatile.Read(ref _incidentServices), $"{handler} asked for the incident service as a visitor");
        Assert.Null(Services.GetRequiredService<DeskToasts>().Last);
    }

    private IRenderedComponent<OpsPanel> RenderOps()
    {
        var panel = RenderComponent<OpsPanel>();
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll(".empty--error")));

        Set(panel.Instance, "_alerts", new List<Alert>
        {
            new() { Id = 7, RuleKey = "stale", Severity = AlertSeverity.Critical, Message = "Stale", TriggeredAt = DateTimeOffset.UnixEpoch }
        });
        Set(panel.Instance, "_injectable", new List<Source> { new() { Id = 1, Key = "theoddsapi", Name = "The Odds API" } });
        PanelProbe.MarkLoaded(panel.Instance);
        panel.Render();
        return panel;
    }

    [Fact]
    public void A_visitors_ops_window_draws_none_of_the_operators_keys()
    {
        var panel = RenderOps();

        Assert.DoesNotContain(panel.FindAll("button"), b => b.TextContent.Trim() is "Evaluate now" or "Open incident");
        Assert.DoesNotContain("On-call drill", panel.Markup);
        Assert.Empty(panel.FindAll("input[type=password]"));
    }

    [Theory]
    [InlineData("SetPollingAsync", new object?[] { true })]
    [InlineData("SaveKeyAsync", new object?[0])]
    [InlineData("RemoveKeyAsync", new object?[0])]
    [InlineData("EvaluateAsync", new object?[0])]
    [InlineData("SetModeAsync", new object?[] { 1, "empty" })]
    [InlineData("DrillAsync", new object?[0])]
    [InlineData("PromoteAsync", new object?[] { 7L })]
    public async Task An_ops_write_invoked_as_a_visitor_does_nothing(string handler, object?[] args)
    {
        var panel = RenderOps();

        // A key on the desk and one typed, so Save and Remove have something to act on.
        Set(panel.Instance, "_oddsKey", OddsKey());
        Set(panel.Instance, "_keyDraft", "a-key");

        var databases = Volatile.Read(ref _databases);
        var incidents = Volatile.Read(ref _incidentServices);

        await Invoke(panel, handler, args);

        NothingWasReachedFor(handler, databases, incidents);
        Assert.Empty(Services.GetRequiredService<XLines.Desk.Windowing.WindowManager>().Windows);
    }

    private static XLines.Web.Services.DeskOddsKey OddsKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xlines-guardkey-" + Guid.NewGuid().ToString("N"));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [XLines.Web.Services.DeskOddsKey.StorePathKey] = directory })
            .Build();

        return new XLines.Web.Services.DeskOddsKey(
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            new XLines.Ingestion.Services.RuntimeOddsKey(),
            _ => Task.CompletedTask, new NoClients(), configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<XLines.Web.Services.DeskOddsKey>.Instance);
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException("No call is made in this test.");
    }

    private IRenderedComponent<IncidentsPanel> RenderIncident()
    {
        var panel = RenderComponent<IncidentsPanel>();
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll(".empty--error")));

        Set(panel.Instance, "_selected", new Incident
        {
            Id = 4,
            Title = "Odds feed stalled",
            Severity = AlertSeverity.Critical,
            Status = IncidentStatus.Open,
            OpenedAt = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero)
        });
        Set(panel.Instance, "_newNote", "Seen at the gate");
        Set(panel.Instance, "_rootCause", "A cause");
        Set(panel.Instance, "_corrective", "A fix");
        PanelProbe.MarkLoaded(panel.Instance);
        panel.Render();
        return panel;
    }

    [Fact]
    public void A_visitors_incident_draws_no_note_field_and_no_resolve_form()
    {
        var panel = RenderIncident();

        Assert.Empty(panel.FindAll("[aria-label='New timeline note']"));
        Assert.Empty(panel.FindAll("textarea"));
        Assert.DoesNotContain(panel.FindAll("button"), b => b.TextContent.Trim() is "Add" or "Resolve" or "Mark mitigated");
    }

    [Theory]
    [InlineData("AddNoteAsync")]
    [InlineData("ResolveAsync")]
    [InlineData("MitigateAsync")]
    public async Task An_incident_write_invoked_as_a_visitor_does_nothing(string handler)
    {
        var panel = RenderIncident();

        var databases = Volatile.Read(ref _databases);
        var incidents = Volatile.Read(ref _incidentServices);

        await Invoke(panel, handler);

        NothingWasReachedFor(handler, databases, incidents);
        Assert.Null(panel.Instance.GetType().GetField("_error", Hidden)!.GetValue(panel.Instance));
        Assert.Null(panel.Instance.GetType().GetField("_noteError", Hidden)!.GetValue(panel.Instance));
        Assert.Equal("Seen at the gate", panel.Instance.GetType().GetField("_newNote", Hidden)!.GetValue(panel.Instance));
    }
}
