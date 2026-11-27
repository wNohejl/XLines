using System.Reflection;

namespace XLines.Tests;

/// <summary>
/// The unit run (<c>--filter "Category!=Integration"</c>) must pass with Docker stopped, so every
/// class that reaches Postgres through Testcontainers carries the Integration trait. xunit v2 does
/// not carry a trait from a collection definition onto its members, so it is stated per class, and
/// this test refuses a new Postgres class that forgets it.
/// </summary>
public class IntegrationTraitTests
{
    [Fact]
    public void Every_class_that_needs_postgres_is_marked_integration()
    {
        var unmarked = typeof(IntegrationTraitTests).Assembly.GetTypes()
            .Where(IsTestClass)
            .Where(NeedsPostgres)
            .Where(t => !IsIntegration(t))
            .Select(t => t.FullName)
            .Order()
            .ToList();

        Assert.True(unmarked.Count == 0,
            "These classes use Postgres but lack [Trait(\"Category\", \"Integration\")]: "
            + string.Join(", ", unmarked));
    }

    [Fact]
    public void The_rule_sees_the_postgres_classes()
    {
        // Guards the reflection above: if it stopped matching anything, the first test would pass
        // vacuously.
        var postgres = typeof(IntegrationTraitTests).Assembly.GetTypes()
            .Where(IsTestClass)
            .Count(NeedsPostgres);

        Assert.True(postgres >= 40, $"Only {postgres} Postgres test classes found.");
    }

    private static bool IsTestClass(Type type)
        => type is { IsClass: true, IsAbstract: false }
           && type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
               .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());

    private static bool NeedsPostgres(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.GetCustomAttributesData().Any(a =>
                    a.AttributeType == typeof(CollectionAttribute)
                    && a.ConstructorArguments.Any(arg => arg.Value as string == PostgresCollection.Name)))
                return true;

            if (t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(PostgresFixture))))
                return true;

            if (t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(f => f.FieldType == typeof(PostgresFixture)))
                return true;

            if (t.GetInterfaces().Any(i => i.IsGenericType
                    && (i.GetGenericTypeDefinition() == typeof(IClassFixture<>)
                        || i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>))
                    && i.GetGenericArguments()[0] == typeof(PostgresFixture)))
                return true;
        }

        return false;
    }

    private static bool IsIntegration(Type type)
        => type.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute)
            && a.ConstructorArguments.Count == 2
            && a.ConstructorArguments[0].Value as string == "Category"
            && a.ConstructorArguments[1].Value as string == "Integration");
}
