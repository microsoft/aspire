// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Shared.Mcp;

namespace Aspire.Cli.Tests.Commands;

public class McpIconHelperTests
{
    [Fact]
    public void GetAspireIcons_LoadsAllIconsFromCli()
    {
        // Arrange
        var assembly = typeof(McpStartCommand).Assembly;
        var resourceNamespace = "Aspire.Cli.Mcp.Resources";

        // Act
        var icons = McpIconHelper.GetAspireIcons(assembly, resourceNamespace);

        // Assert
        Assert.NotNull(icons);
        Assert.Equal(5, icons.Count);

        // Verify each icon has correct properties
        Assert.All(icons, icon =>
        {
            Assert.NotNull(icon);
            Assert.Equal("image/png", icon.MimeType);
            Assert.NotNull(icon.Source);
            Assert.StartsWith("data:image/png;base64,", icon.Source);
            
            // Verify it's actually base64 encoded data
            var base64Part = icon.Source.Substring("data:image/png;base64,".Length);
            Assert.True(base64Part.Length > 0, "Icon should have base64 data");
            
            // Verify base64 is valid by attempting to decode
            var bytes = Convert.FromBase64String(base64Part);
            Assert.True(bytes.Length > 0, "Icon data should not be empty");
            
            // Verify PNG header (first 4 bytes should be PNG signature: 89 50 4E 47)
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal(0x50, bytes[1]);
            Assert.Equal(0x4E, bytes[2]);
            Assert.Equal(0x47, bytes[3]);

            Assert.NotNull(icon.Sizes);
            Assert.Single(icon.Sizes);
        });

        // Verify all expected sizes are present
        var sizes = icons.SelectMany(i => i.Sizes ?? []).ToHashSet();
        Assert.Contains("16", sizes);
        Assert.Contains("32", sizes);
        Assert.Contains("48", sizes);
        Assert.Contains("64", sizes);
        Assert.Contains("256", sizes);
    }

    [Fact]
    public void GetAspireIcons_MissingResource_ThrowsInvalidOperationException()
    {
        // Arrange
        var assembly = typeof(McpStartCommand).Assembly;
        var invalidResourceNamespace = "Aspire.Cli.Invalid.Namespace";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            McpIconHelper.GetAspireIcons(assembly, invalidResourceNamespace));

        Assert.Contains("Could not find embedded resource", exception.Message);
        Assert.Contains(invalidResourceNamespace, exception.Message);
    }
}
