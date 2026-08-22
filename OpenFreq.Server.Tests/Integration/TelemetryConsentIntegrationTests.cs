namespace OpenFreq.Server.Tests.Integration;

public sealed class TelemetryConsentIntegrationTests
{
    [Fact]
    public async Task CapabilityAndServerCollectionFollowRuntimeConsent()
    {
        await using var server = await SignalingServerHarness.StartAsync(
            telemetryEndpoint: "https://telemetry.example/v1/batches",
            telemetrySigningKey: "integration-test-signing-key-at-least-32-bytes");
        await using var client = RtcClientHarness.Create(server, "VIPR11");
        await client.ConnectAsync();

        Assert.Null(client.Client.TelemetryCapability);
        Assert.False(server.Server.Clients[client.PeerId!].DiagnosticTelemetryConsent);

        await client.Client.SetDiagnosticTelemetryConsentAsync(true);
        await TestAsync.PollUntil(() => client.Client.TelemetryCapability != null);
        Assert.True(server.Server.Clients[client.PeerId!].DiagnosticTelemetryConsent);
        Assert.Equal("https://telemetry.example/v1/batches", client.Client.TelemetryCapability!.Endpoint);

        await client.Client.SetDiagnosticTelemetryConsentAsync(false);
        await TestAsync.PollUntil(() => !server.Server.Clients[client.PeerId!].DiagnosticTelemetryConsent);
        Assert.Null(client.Client.TelemetryCapability);
    }
}
