using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using XLines.Data;

namespace XLines.Tests.Support;

/// <summary>
/// The test's Postgres is the test's own, wherever Docker publishes it.
///
/// <para>
/// <see cref="DatabaseInitializer"/> migrates, seeds and cleans only a database on this machine
/// (ADR 0023); anywhere else it asks the database for its history first and leaves it as it is.
/// On a runner Testcontainers publishes on localhost and that rule never comes up. In the SDK
/// container CLAUDE.md gives for a machine with Smart App Control on, it publishes on
/// <c>host.docker.internal</c>, which the rule rightly reads as another machine: a fresh database
/// then failed with "does not exist" before EF could create it, and the cleanup tests passed over
/// the very steps they test. Initialising through here says, as an operator would with
/// <see cref="Placement.MigrationPlacement.AllowRemoteMigrationKey"/>, that this database is ours.
/// </para>
/// </summary>
public static class TestDatabases
{
    /// <summary>The placement every test database is initialised with.</summary>
    public static readonly MigrationPlacementOptions Owned = new(AllowRemoteMigration: true);

    /// <summary>The initializer as a host would build it, for a database the test owns.</summary>
    public static DatabaseInitializer Initializer(XLinesDbContext db)
        => new(db, NullLogger<DatabaseInitializer>.Instance, Owned);

    /// <summary>
    /// A database of the test's own beside the fixture's, created, migrated and seeded, and its
    /// connection string. Created here rather than by EF, which only creates a database it
    /// already counts as this machine's.
    /// </summary>
    public static async Task<string> FreshAsync(string fixtureConnection, string prefix)
    {
        var name = prefix + "_" + Guid.NewGuid().ToString("N")[..12];

        await using (var admin = new NpgsqlConnection(fixtureConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(fixtureConnection) { Database = name }.ConnectionString;

        await using var db = new XLinesDbContext(new DbContextOptionsBuilder<XLinesDbContext>().UseNpgsql(connection).Options);
        await Initializer(db).InitialiseAsync();

        return connection;
    }
}
