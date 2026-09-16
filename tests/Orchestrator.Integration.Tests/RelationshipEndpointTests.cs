using System.Net;
using Shouldly;

namespace Orchestrator.Integration.Tests;

[Collection(nameof(PostgresCollection))]
[Trait("Category", "Integration")]
public sealed class RelationshipEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListRelationships_WithoutAuthentication_Returns401()
    {
        OrchestratorApiFactory.ConnectionString = fixture.ConnectionString;
        using var client = new OrchestratorApiFactory().CreateClient();

        var response = await client.GetAsync($"/api/admin/repositories/{Guid.CreateVersion7()}/relationships");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
