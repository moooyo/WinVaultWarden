using System.Net.WebSockets;
using Api;
using Xunit;

namespace Api.Tests;

public sealed class NotificationsConnectionProtocolTests
{
    [Fact]
    public void Handshake_fragments_can_fill_the_limit_exactly()
    {
        using var response = new MemoryStream();
        NotificationsConnection.AppendHandshakeFragment(
            response,
            WebSocketMessageType.Text,
            new byte[NotificationsConnection.MaxHandshakeResponseSize - 1]);
        NotificationsConnection.AppendHandshakeFragment(
            response,
            WebSocketMessageType.Text,
            new byte[1]);

        Assert.Equal(NotificationsConnection.MaxHandshakeResponseSize, response.Length);
    }

    [Fact]
    public void Handshake_fragment_exceeding_the_cumulative_limit_is_rejected_before_write()
    {
        using var response = new MemoryStream();
        NotificationsConnection.AppendHandshakeFragment(
            response,
            WebSocketMessageType.Text,
            new byte[NotificationsConnection.MaxHandshakeResponseSize - 1]);

        Assert.Throws<InvalidDataException>(() =>
            NotificationsConnection.AppendHandshakeFragment(
                response,
                WebSocketMessageType.Text,
                new byte[2]));
        Assert.Equal(NotificationsConnection.MaxHandshakeResponseSize - 1, response.Length);
    }

    [Fact]
    public void Binary_handshake_fragment_is_rejected_before_write()
    {
        using var response = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            NotificationsConnection.AppendHandshakeFragment(
                response,
                WebSocketMessageType.Binary,
                new byte[] { 0x7b, 0x7d, 0x1e }));
        Assert.Equal(0, response.Length);
    }

    [Fact]
    public void Later_handshake_fragment_with_a_different_type_is_rejected()
    {
        using var response = new MemoryStream();
        NotificationsConnection.AppendHandshakeFragment(
            response,
            WebSocketMessageType.Text,
            new byte[] { 0x7b });

        Assert.Throws<InvalidDataException>(() =>
            NotificationsConnection.AppendHandshakeFragment(
                response,
                WebSocketMessageType.Binary,
                new byte[] { 0x7d, 0x1e }));
        Assert.Equal(1, response.Length);
    }

    [Fact]
    public void Handshake_acknowledgement_must_match_the_success_record_exactly()
    {
        NotificationsConnection.ValidateHandshakeAcknowledgement(
            new byte[] { 0x7b, 0x7d, 0x1e });

        Assert.Throws<InvalidDataException>(() =>
            NotificationsConnection.ValidateHandshakeAcknowledgement(
                new byte[] { 0x7b, 0x7d, 0x1e, 0x00 }));
        Assert.Throws<InvalidDataException>(() =>
            NotificationsConnection.ValidateHandshakeAcknowledgement(
                new byte[] { 0x7b, 0x7d }));
    }
}
