using System.Text.Json;
using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Services;

/// <summary>
/// One deserialized message, held so it can be read twice.
/// </summary>
public sealed class CommandEnvelope
{
    public required string RawBody { get; init; }

    public required CommandBase Command { get; init; }

    private readonly JsonSerializerOptions options;

    internal CommandEnvelope(JsonSerializerOptions options) => this.options = options;

    /// <summary>The typed second pass, keyed on the discriminator the first pass read.</summary>
    public T? Deserialize<T>()
        where T : CommandBase => JsonSerializer.Deserialize<T>(this.RawBody, this.options);
}

public interface ICommandEnvelopeFactory
{
    CommandEnvelope Create(string body);
}

/// <summary>
/// Reads an inbound message twice: once as <see cref="CommandBase"/> to learn what it is, then as
/// the concrete type once something has decided what to do with it.
/// </summary>
/// <remarks>
/// The alternative — a polymorphic converter keyed on the discriminator — forces every command
/// type to be known to the deserializer, which turns an unrecognised operation type into a parse
/// failure at the very edge of the system. Reading the discriminator first keeps that decision in
/// the dispatcher, where an unknown type can be logged and completed instead of poisoning a queue.
/// The raw body is retained because the audit record stores exactly what arrived, not a re-render
/// of it.
/// </remarks>
internal sealed class CommandEnvelopeFactory(JsonSerializerOptions options)
    : ICommandEnvelopeFactory
{
    public CommandEnvelope Create(string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var command =
            JsonSerializer.Deserialize<CommandBase>(body, options)
            ?? throw new InvalidOperationException(
                "The message body did not deserialize to a command."
            );

        return new CommandEnvelope(options) { RawBody = body, Command = command };
    }
}
