using System.Reflection;
using NetArchTest.Rules;
using Orchestrator.Application.Steps;
using Shouldly;

namespace Orchestrator.Architecture.Tests;

public class GatewayUsageTests
{
    [Fact]
    public void StepHandlers_DoNotDependOnAdapterAssembliesDirectly()
    {
        var adapters = new[]
        {
            "Orchestrator.Adapters.Git", "Orchestrator.Adapters.AzureDevOps",
            "Orchestrator.Adapters.Agent", "Orchestrator.Adapters.Slack",
            "Orchestrator.Adapters.Obsidian", "Orchestrator.Adapters.P1",
            "Orchestrator.Adapters.Teams",
        };

        var result = Types.InAssembly(Assembly.Load("Orchestrator.Application"))
            .That().ImplementInterface(typeof(IStepHandler))
            .ShouldNot().HaveDependencyOnAny(adapters)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue("step handlers must go through ports and the tool gateway");
    }
}
