// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
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
}
