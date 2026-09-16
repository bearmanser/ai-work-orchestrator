using System.Reflection;
using NetArchTest.Rules;
using Shouldly;

namespace Orchestrator.Architecture.Tests;

public class SecretPersistenceTests
{
    [Fact]
    public void NoEntityProperty_IsNamedLikeASecretValue()
    {
        var forbidden = new[] { "Password", "Token", "ApiKey", "Secret", "ConnectionString" };
        var allProperties = Types.InAssembly(Assembly.Load("Orchestrator.Domain"))
            .GetTypes()
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.PropertyType == typeof(string))
            .ToArray();

        allProperties.Length.ShouldBeGreaterThan(0,
            "the scan must actually examine string properties on Orchestrator.Domain types, " +
            "otherwise this test would pass vacuously");

        var offenders = allProperties
            .Where(x => forbidden.Any(f => x.Property.Name.Contains(f, StringComparison.Ordinal)))
            .Where(x => !x.Property.Name.EndsWith("Ref", StringComparison.Ordinal)
                     && !x.Property.Name.EndsWith("Hash", StringComparison.Ordinal))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToArray();

        offenders.ShouldBeEmpty("store a SecretRef or a hash, never a secret value");
    }
}
