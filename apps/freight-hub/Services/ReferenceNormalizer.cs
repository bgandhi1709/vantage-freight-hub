namespace Vantage.Freight.Hub.Services;

/// <summary>
/// Turns the booking references three systems use into the one form this service stores under.
/// </summary>
/// <remarks>
/// Every rule here is narrow on purpose, and each one exists because a wider version broke
/// something. They are the closest thing this service has to a specification of its own identity
/// model, which is why each has its own tests.
/// </remarks>
internal static class ReferenceNormalizer
{
    /// <summary>
    /// Strips the attempt suffix a re-raised booking carries, so a retry lands on the same storage
    /// key as the original instead of creating a second consignment.
    /// </summary>
    /// <remarks>
    /// The suffix is an explicit marker — "-r2", "-r13" — and only that shape is stripped.
    ///
    /// An earlier version of this rule stripped any short trailing number from a reference with
    /// three or more parts. That is unsound: "VW-1042-7" is a perfectly ordinary reference whose
    /// last segment is a small number, and the rule silently rewrote it to "VW-1042", merging two
    /// unrelated consignments onto one storage key. A marker the booking system actually emits
    /// removes the guesswork; anything that is not a marker is left exactly as it arrived.
    /// </remarks>
    internal static string NormalizeBookingReference(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var trimmed = reference.Trim();
        var lastSeparator = trimmed.LastIndexOf('-');

        if (lastSeparator <= 0)
        {
            return trimmed;
        }

        var last = trimmed[(lastSeparator + 1)..];

        if (
            last.Length is >= 2 and <= 3
            && (last[0] == 'r' || last[0] == 'R')
            && last[1..].All(char.IsAsciiDigit)
        )
        {
            return trimmed[..lastSeparator];
        }

        return trimmed;
    }

    /// <summary>
    /// Normalizes an order number from the booking system, which pads its numeric segments.
    /// </summary>
    /// <remarks>
    /// Applied only to an exact all-numeric three-part shape. Partner-prefixed numbers
    /// ("ACME-0012-0004") pass through untouched: the padding is meaningful to whoever issued them,
    /// and rewriting it would produce a number their system does not recognise.
    /// </remarks>
    internal static string NormalizeOrderNumber(string orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            return string.Empty;
        }

        var trimmed = orderNumber.Trim();
        var parts = trimmed.Split('-');

        if (parts.Length != 3 || !parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit)))
        {
            return trimmed;
        }

        return string.Join('-', parts.Select(part => part.TrimStart('0') is { Length: > 0 } t ? t : "0"));
    }

    /// <summary>
    /// Storage keys are compared as case-sensitive strings by both blob and table storage, so every
    /// key passes through here and nothing else decides the casing.
    /// </summary>
    internal static string ToStorageKey(string reference) =>
        NormalizeBookingReference(reference).ToLowerInvariant();
}
