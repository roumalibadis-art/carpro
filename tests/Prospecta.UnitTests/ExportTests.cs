using FluentAssertions;
using Prospecta.Application.Businesses;

namespace Prospecta.UnitTests;

public class ExportSafetyTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+33", "'+33")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Normal", "Normal")]
    [InlineData(null, "")]
    public void Formula_prefixes_are_neutralised(string? input, string expected) => ExportService.Safe(input).Should().Be(expected);
}
