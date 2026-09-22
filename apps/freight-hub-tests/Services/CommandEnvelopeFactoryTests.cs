using System.Text.Json;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Tests.Services;

public sealed class CommandEnvelopeFactoryTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly CommandEnvelopeFactory factory = new(Options);

    [Fact]
    public void Create_ReadsTheDiscriminatorWithoutTypingThePayload()
    {
        var body = """
            {"operationType":"SubmitShipment","bookingReference":"VW-1042-7","pieces":4}
            """;

        var envelope = this.factory.Create(body);

        Assert.Equal(OperationType.SubmitShipment, envelope.Command.OperationType);
    }

    [Fact]
    public void Deserialize_ProducesTheTypedCommandOnTheSecondPass()
    {
        var body = """
            {"operationType":"SubmitShipment","bookingReference":"VW-1042-7","consigneeId":"c-1",
             "venue":"Hall 3","pieces":4,"weightKg":118.5,"sourceSystem":"bookings"}
            """;

        var command = this.factory.Create(body).Deserialize<SubmitShipmentCommand>();

        Assert.NotNull(command);
        Assert.Equal("VW-1042-7", command.BookingReference);
        Assert.Equal(4, command.Pieces);
        Assert.Equal(118.5m, command.WeightKg);
    }

    [Fact]
    public void Create_KeepsTheRawBodyExactlyAsItArrived()
    {
        // The audit record stores what the sender sent, not a re-serialization of it.
        var body = """
            {"operationType":"UpdateShipmentStatus","bookingReference":"VW-1042-7","extraFieldWeDoNotModel":true}
            """;

        var envelope = this.factory.Create(body);

        Assert.Equal(body, envelope.RawBody);
    }

    [Fact]
    public void Create_WithAnUnrecognisedOperationType_ParsesAsUnknownRatherThanThrowing()
    {
        // The edge of the system must not decide that an unknown event is fatal; the dispatcher
        // logs it and completes the message so one unrelated event type cannot poison a queue.
        var body = """{"operationType":"SomethingNobodyModelledYet"}""";

        var envelope = this.factory.Create(body);

        Assert.Equal(OperationType.Unknown, envelope.Command.OperationType);
    }

    [Fact]
    public void Create_WithMalformedJson_Throws() =>
        Assert.Throws<JsonException>(() => this.factory.Create("{not json"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithAnEmptyBody_Throws(string body) =>
        Assert.Throws<ArgumentException>(() => this.factory.Create(body));

    [Fact]
    public void Create_WithALiteralNullBody_ThrowsAnExplainedError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => this.factory.Create("null"));

        Assert.Contains("did not deserialize", error.Message, StringComparison.Ordinal);
    }
}
