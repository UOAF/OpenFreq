using OpenFreq.Common.Signaling;

namespace OpenFreq.Common.Tests;

public class SignalingMessageFactoryTests
{
    [Fact]
    public void CreateAuthenticate_SetsCorrectType()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", "Player1");
        Assert.Equal(SignalingMessageTypes.Authenticate, msg.Type);
    }

    [Fact]
    public void CreateAuthenticate_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("secret", "Viper");

        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal("secret", payload.Password);
        Assert.Equal("Viper", payload.DisplayName);
    }

    [Fact]
    public void CreateAuthenticate_NullDisplayName_Preserved()
    {
        var msg = SignalingMessageFactory.CreateAuthenticate("pass", null);
        var payload = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Null(payload.DisplayName);
    }

    [Fact]
    public void CreateJoin_SetsCorrectTypeAndFrequency()
    {
        var msg = SignalingMessageFactory.CreateJoin(251000);

        Assert.Equal(SignalingMessageTypes.Join, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<JoinChannelMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(251000, payload.FrequencyKhz);
    }

    [Fact]
    public void CreateLeave_SetsCorrectTypeAndFrequency()
    {
        var msg = SignalingMessageFactory.CreateLeave(135100);

        Assert.Equal(SignalingMessageTypes.Leave, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<LeaveChannelMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(135100, payload.FrequencyKhz);
    }

    [Fact]
    public void CreateTransmission_TransmittingTrue_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateTransmission(251000, transmitting: true, is3d: false);

        Assert.Equal(SignalingMessageTypes.Transmission, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(251000, payload.FrequencyKhz);
        Assert.True(payload.Transmitting);
        Assert.False(payload.Is3d);
    }

    [Fact]
    public void CreateTransmission_Is3dTrue_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateTransmission(251000, transmitting: false, is3d: true);
        var payload = SignalingMessageFactory.DeserializePayload<AudioTransmissionMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.True(payload.Is3d);
    }

    [Fact]
    public void CreateError_SetsCorrectTypeAndMessage()
    {
        var msg = SignalingMessageFactory.CreateError("Wrong password");

        Assert.Equal(SignalingMessageTypes.Error, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<ErrorMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("Wrong password", payload.Error);
    }

    [Fact]
    public void CreatePeerJoined_AllFieldsPreserved()
    {
        var msg = SignalingMessageFactory.CreatePeerJoined("peer-42", "Maverick", 251000);

        Assert.Equal(SignalingMessageTypes.PeerJoined, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<PeerJoinedMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("peer-42", payload.PeerId);
        Assert.Equal("Maverick", payload.PeerDisplayName);
        Assert.Equal(251000, payload.FrequencyKhz);
    }

    [Fact]
    public void CreatePeerJoined_NullDisplayName_FallsBackToUnnamed()
    {
        var msg = SignalingMessageFactory.CreatePeerJoined("peer-1", null, 251000);
        var payload = SignalingMessageFactory.DeserializePayload<PeerJoinedMessage>(msg.Payload);

        Assert.NotNull(payload);
        Assert.Equal("Unnamed", payload.PeerDisplayName);
    }

    [Fact]
    public void CreatePeerLeft_AllFieldsPreserved()
    {
        var msg = SignalingMessageFactory.CreatePeerLeft("peer-99", 135100);

        Assert.Equal(SignalingMessageTypes.PeerLeft, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<PeerLeftMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("peer-99", payload.PeerId);
        Assert.Equal(135100, payload.FrequencyKhz);
    }

    [Fact]
    public void CreateSetDisplayName_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateSetDisplayName("Goose");

        Assert.Equal(SignalingMessageTypes.SetDisplayName, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<DisplayNameMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("Goose", payload.DisplayName);
    }

    [Fact]
    public void CreateTransmissionEvent_AllFieldsPreserved()
    {
        var msg = SignalingMessageFactory.CreateTransmissionEvent("peer-7", 251000, transmitting: true, is3d: true);

        var payload = SignalingMessageFactory.DeserializePayload<TransmissionEventMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("peer-7", payload.PeerId);
        Assert.Equal(251000, payload.FrequencyKhz);
        Assert.True(payload.Transmitting);
        Assert.True(payload.Is3d);
    }

    [Fact]
    public void CreateModeUpdate_Is3dTrue_PayloadRoundTrips()
    {
        var msg = SignalingMessageFactory.CreateModeUpdate(is3d: true);

        Assert.Equal(SignalingMessageTypes.ModeUpdate, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<ModeUpdateMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.True(payload.Is3d);
    }

    [Fact]
    public void CreateSuccess_AllFieldsPreserved()
    {
        var peers = new SortedDictionary<int, List<PeerData>>();
        var msg = SignalingMessageFactory.CreateSuccess(
            "Authenticated", peers, peerId: "me-123", audioPort: 9988, opusEnabled: true);

        Assert.Equal(SignalingMessageTypes.Success, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<SuccessMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal("Authenticated", payload.Message);
        Assert.Equal("me-123", payload.PeerId);
        Assert.Equal(9988, payload.AudioPort);
        Assert.True(payload.OpusCompressionEnabled);
    }

    [Fact]
    public void CreateAllPeersStatus_EmptyDict_PayloadRoundTrips()
    {
        var allPeers = new SortedDictionary<int, List<PeerData>>();
        var msg = SignalingMessageFactory.CreateAllPeersStatusMessage(allPeers);

        Assert.Equal(SignalingMessageTypes.AllPeersStatus, msg.Type);
        var payload = SignalingMessageFactory.DeserializePayload<AllPeersStatusMessage>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Empty(payload.FrequenciesPeers);
    }

    [Fact]
    public void DeserializePayload_NullPayload_ReturnsNull()
    {
        var result = SignalingMessageFactory.DeserializePayload<AuthenticateMessage>(null);
        Assert.Null(result);
    }
}
