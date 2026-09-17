using Orchestrator.Application.Agents;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Ports;
using Shouldly;

namespace Orchestrator.Application.Tests;

public sealed class FailureClassifierTests
{
    [Fact]
    public void Classify_PlainHttpRequestException_IsTransient() =>
        FailureClassifier.Classify(new HttpRequestException("connection reset"), null)
            .ShouldBe(FailureClass.Transient);

    [Theory]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.BadGateway)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    public void Classify_RateLimitOrServerError_IsTransient(System.Net.HttpStatusCode status) =>
        FailureClassifier.Classify(new HttpRequestException(null, null, status), null)
            .ShouldBe(FailureClass.Transient);

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public void Classify_AuthenticationStatus_IsAuthentication(System.Net.HttpStatusCode status) =>
        FailureClassifier.Classify(new HttpRequestException(null, null, status), null)
            .ShouldBe(FailureClass.Authentication);

    [Fact]
    public void Classify_TaskCanceledException_IsTransient() =>
        FailureClassifier.Classify(new TaskCanceledException("timeout"), null)
            .ShouldBe(FailureClass.Transient);

    [Fact]
    public void Classify_PolicyDeniedException_IsPolicyDenial() =>
        FailureClassifier.Classify(new PolicyDeniedException("stage not authorized"), null)
            .ShouldBe(FailureClass.PolicyDenial);

    [Fact]
    public void Classify_NotSupportedException_IsUnsupportedCapability() =>
        FailureClassifier.Classify(new NotSupportedException("unknown flag"), null)
            .ShouldBe(FailureClass.UnsupportedCapability);

    [Fact]
    public void Classify_FailedResult_IsImplementationFailure() =>
        FailureClassifier.Classify(null, FailedResult())
            .ShouldBe(FailureClass.ImplementationFailure);

    [Fact]
    public void Classify_SucceededResult_IsNone() =>
        FailureClassifier.Classify(null, SucceededResult())
            .ShouldBe(FailureClass.None);

    private static AgentResult FailedResult() => new(
        AgentStatus.Failed, "claude", "test-model", "abc", null, null,
        [], [], [], new AgentUsage(1m, TimeSpan.FromSeconds(1)), "tests still red");

    private static AgentResult SucceededResult() => new(
        AgentStatus.Succeeded, "claude", "test-model", "abc", "def", null,
        [], [], [], new AgentUsage(1m, TimeSpan.FromSeconds(1)), null);
}
