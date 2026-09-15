using System.Reflection;
using NetArchTest.Rules;
using Shouldly;

namespace Orchestrator.Architecture.Tests;

public class AdapterIsolationTests
{
    private static readonly string[] AdapterAssemblies =
    [
        "Orchestrator.Adapters.Git",
        "Orchestrator.Adapters.AzureDevOps",
        "Orchestrator.Adapters.Agent",
        "Orchestrator.Adapters.Slack",
        "Orchestrator.Adapters.Obsidian",
        "Orchestrator.Adapters.P1",
        "Orchestrator.Adapters.Teams",
    ];

    [Fact]
    public void Adapter_DoesNotDependOnAnotherAdapter()
    {
        foreach (var name in AdapterAssemblies)
        {
            var others = AdapterAssemblies.Where(a => a != name).ToArray();
            var result = Types.InAssembly(Assembly.Load(name))
                .ShouldNot().HaveDependencyOnAny(others)
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(
                $"{name} depends on another adapter via: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }

    [Fact]
    public void Domain_DoesNotDependOnInfrastructureConcerns()
    {
        var result = Types.InAssembly(Assembly.Load("Orchestrator.Domain"))
            .ShouldNot().HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore", "Npgsql", "Hangfire",
                "System.Net.Http", "Microsoft.Extensions.DependencyInjection")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain leaked infrastructure into: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    [Fact]
    public void Application_DoesNotDependOnAnyAdapterOrInfrastructure()
    {
        var forbidden = AdapterAssemblies.Append("Orchestrator.Infrastructure").ToArray();
        var result = Types.InAssembly(Assembly.Load("Orchestrator.Application"))
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application depends on an implementation: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
