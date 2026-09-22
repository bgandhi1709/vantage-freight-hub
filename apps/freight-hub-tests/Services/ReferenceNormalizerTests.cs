using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Tests.Services;

/// <summary>
/// These tests are the specification for the service's identity model. Each rule is narrow, and
/// the cases that must be left alone matter as much as the ones that are rewritten.
/// </summary>
public sealed class ReferenceNormalizerTests
{
    [Theory]
    [InlineData("VW-1042-7-r2", "VW-1042-7")]
    [InlineData("VW-1042-7-r13", "VW-1042-7")]
    [InlineData("vw-88-R3", "vw-88")]
    public void NormalizeBookingReference_StripsTheAttemptMarker(string input, string expected) =>
        Assert.Equal(expected, ReferenceNormalizer.NormalizeBookingReference(input));

    [Theory]
    [InlineData("VW-1042-7")]
    [InlineData("VW-1042")]
    [InlineData("VW-7")]
    public void NormalizeBookingReference_LeavesAReferenceEndingInANumberAlone(string input) =>
        // The bug this pins: a trailing number is an ordinary part of a reference. Treating it as
        // an attempt counter merged two unrelated consignments onto one storage key.
        Assert.Equal(input, ReferenceNormalizer.NormalizeBookingReference(input));

    [Theory]
    [InlineData("VW-1042-r104")]
    [InlineData("VW-1042-rx")]
    [InlineData("VW-1042-region")]
    [InlineData("VW1042r2")]
    public void NormalizeBookingReference_LeavesAnythingThatIsNotTheMarkerAlone(string input) =>
        Assert.Equal(input, ReferenceNormalizer.NormalizeBookingReference(input));

    [Fact]
    public void NormalizeBookingReference_TrimsSurroundingWhitespace() =>
        Assert.Equal("VW-1042-7", ReferenceNormalizer.NormalizeBookingReference("  VW-1042-7-r2  "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeBookingReference_WithNothingToNormalize_ReturnsEmpty(string input) =>
        Assert.Equal(string.Empty, ReferenceNormalizer.NormalizeBookingReference(input));

    [Theory]
    [InlineData("0012-0004-0001", "12-4-1")]
    [InlineData("100-020-003", "100-20-3")]
    public void NormalizeOrderNumber_StripsPaddingFromAnAllNumericThreePartNumber(
        string input,
        string expected
    ) => Assert.Equal(expected, ReferenceNormalizer.NormalizeOrderNumber(input));

    [Theory]
    [InlineData("ACME-0012-0004")]
    [InlineData("0012-0004")]
    [InlineData("0012-0004-0001-0002")]
    public void NormalizeOrderNumber_LeavesAnythingElseUntouched(string input) =>
        // A partner's padding is meaningful to the partner. Rewriting it produces a number their
        // system will not recognise.
        Assert.Equal(input, ReferenceNormalizer.NormalizeOrderNumber(input));

    [Fact]
    public void NormalizeOrderNumber_KeepsASegmentThatIsAllZeroes() =>
        Assert.Equal("12-0-1", ReferenceNormalizer.NormalizeOrderNumber("0012-0000-0001"));

    [Fact]
    public void ToStorageKey_NormalizesAndLowercases() =>
        // Blob and table storage both compare keys case-sensitively, so one retry written in a
        // different case would otherwise become a second consignment.
        Assert.Equal("vw-1042-7", ReferenceNormalizer.ToStorageKey("VW-1042-7-r2"));
}
