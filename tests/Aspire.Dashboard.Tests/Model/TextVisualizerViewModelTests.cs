// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class TextVisualizerViewModelTests
{
    [Fact]
    public void Create_PlainText_NotFormatted()
    {
        // Arrange & Act
        var vm = new TextVisualizerViewModel("Just some text.", indentText: true);

        // Assert
        Assert.Equal("Just some text.", vm.Text);
        Assert.Equal("Just some text.", vm.FormattedText);
    }

    [Fact]
    public void Create_Xml_Formatted()
    {
        // Arrange & Act
        var vm = new TextVisualizerViewModel(" <xml><text>Just some text</text></xml>", indentText: true);

        // Assert
        Assert.Equal(" <xml><text>Just some text</text></xml>", vm.Text);
        Assert.Equal(
            """
            <xml>
              <text>Just some text</text>
            </xml>
            """, vm.FormattedText);
    }

    [Fact]
    public void Create_XmlWithDeclaration_Formatted()
    {
        // Arrange & Act
        var vm = new TextVisualizerViewModel(" <?xml version=\"1.0\" encoding=\"utf-16\"?><xml><text>Just some text</text></xml>", indentText: true);

        // Assert
        Assert.Equal(" <?xml version=\"1.0\" encoding=\"utf-16\"?><xml><text>Just some text</text></xml>", vm.Text);
        Assert.Equal(
            """
            <?xml version="1.0" encoding="utf-16"?>
            <xml>
              <text>Just some text</text>
            </xml>
            """, vm.FormattedText);
    }

    [Fact]
    public void Create_Json_Formatted()
    {
        // Arrange & Act
        var vm = new TextVisualizerViewModel(" [true]", indentText: true);

        // Assert
        Assert.Equal(" [true]", vm.Text);
        Assert.Equal(
            """
            [
              true
            ]
            """, vm.FormattedText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_KnownJsonFormat_RespectsIndentText(bool indentText)
    {
        var vm = new TextVisualizerViewModel("""{"key":"value"}""", indentText, knownFormat: DashboardUIHelpers.JsonFormat);

        Assert.Equal("""{"key":"value"}""", vm.Text);
        Assert.Equal(DashboardUIHelpers.JsonFormat, vm.FormatKind);
        Assert.Equal(
            indentText
                ? """
                  {
                    "key": "value"
                  }
                  """
                : """{"key":"value"}""",
            vm.FormattedText);
    }

    [Fact]
    public void Create_KnownJsonFormat_PreservesLosslessNumberTextWhenFormatting()
    {
        var vm = new TextVisualizerViewModel("""{"large":9223372036854775807,"precise":0.1234567890123456789012345,"trailing":1.2300}""", indentText: true, knownFormat: DashboardUIHelpers.JsonFormat);

        Assert.Equal(
            """
            {
              "large": 9223372036854775807,
              "precise": 0.1234567890123456789012345,
              "trailing": 1.2300
            }
            """,
            vm.FormattedText);
    }

    [Fact]
    public void Create_KnownJsonFormat_PreservesRawNumberTextWhenNumberCannotBeRepresentedLosslessly()
    {
        var json = """{"exponent":1.23456789012345678901234567890123456789e-100,"huge":1e1000,"largeDouble":1e100}""";

        var vm = new TextVisualizerViewModel(json, indentText: true, knownFormat: DashboardUIHelpers.JsonFormat);

        Assert.Equal(DashboardUIHelpers.JsonFormat, vm.FormatKind);
        Assert.Equal(
            """
            {
              "exponent": 1.23456789012345678901234567890123456789e-100,
              "huge": 1e1000,
              "largeDouble": 1e100
            }
            """,
            vm.FormattedText);
    }

    [Fact]
    public void Create_KnownJsonFormat_FormatsNumberTextAfterComments()
    {
        var vm = new TextVisualizerViewModel("""[/* number */0.1234567890123456789012345,9223372036854775807,1.2300]""", indentText: true, knownFormat: DashboardUIHelpers.JsonFormat);

        Assert.Equal(
            """
            [
              /* number */
              0.1234567890123456789012345,
              9223372036854775807,
              1.2300
            ]
            """,
            vm.FormattedText);
    }
}
