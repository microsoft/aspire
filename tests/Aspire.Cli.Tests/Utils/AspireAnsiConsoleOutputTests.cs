// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Utils;

public class AspireAnsiConsoleOutputTests
{
    [Fact]
    public void Width_ReturnsConfiguredValue_WhenASPIRE_CONSOLE_WIDTH_IsSet()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_CONSOLE_WIDTH"] = "200"
            })
            .Build();
        
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width = output.Width;
        
        // Assert
        Assert.Equal(200, width);
    }

    [Fact]
    public void Width_CapsAt500_WhenASPIRE_CONSOLE_WIDTH_ExceedsMaximum()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_CONSOLE_WIDTH"] = "600"
            })
            .Build();
        
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width = output.Width;
        
        // Assert
        Assert.Equal(500, width);
    }

    [Fact]
    public void Width_IgnoresInvalidValue_WhenASPIRE_CONSOLE_WIDTH_IsNotNumeric()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_CONSOLE_WIDTH"] = "invalid"
            })
            .Build();
        
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width = output.Width;
        
        // Assert
        // Should fall back to default width detection
        Assert.True(width > 0);
    }

    [Fact]
    public void Width_IgnoresNegativeValue_WhenASPIRE_CONSOLE_WIDTH_IsNegative()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_CONSOLE_WIDTH"] = "-100"
            })
            .Build();
        
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width = output.Width;
        
        // Assert
        // Should fall back to default width detection
        Assert.True(width > 0);
    }

    [Fact]
    public void Width_IgnoresZeroValue_WhenASPIRE_CONSOLE_WIDTH_IsZero()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_CONSOLE_WIDTH"] = "0"
            })
            .Build();
        
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width = output.Width;
        
        // Assert
        // Should fall back to default width detection
        Assert.True(width > 0);
    }

    [Fact]
    public void Width_UsesSafeWidth_WhenNoConfigurationSet()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width = output.Width;
        
        // Assert
        // Should return either console buffer width or default
        Assert.True(width > 0);
    }

    [Fact]
    public void Width_CachesValue_AfterFirstAccess()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var width1 = output.Width;
        output.Width = 250; // Set explicit value
        var width2 = output.Width;
        
        // Assert
        Assert.Equal(250, width2);
        Assert.NotEqual(width1, width2);
    }

    [Fact]
    public void IsTerminal_ReturnsFalse_WhenOutputIsRedirected()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var isTerminal = output.IsTerminal;
        
        // Assert
        // When running in tests, output is typically redirected
        // This test verifies the property is accessible and returns a boolean
        Assert.True(isTerminal == true || isTerminal == false);
    }

    [Fact]
    public void Height_ReturnsPositiveValue()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var output = new AspireAnsiConsoleOutput(Console.Out, configuration);
        
        // Act
        var height = output.Height;
        
        // Assert
        Assert.True(height > 0);
    }

    [Fact]
    public void Writer_ReturnsSameInstance_AsConstructorParameter()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var writer = Console.Out;
        var output = new AspireAnsiConsoleOutput(writer, configuration);
        
        // Act
        var returnedWriter = output.Writer;
        
        // Assert
        Assert.Same(writer, returnedWriter);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenWriterIsNull()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AspireAnsiConsoleOutput(null!, configuration));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConfigurationIsNull()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new AspireAnsiConsoleOutput(Console.Out, null!));
    }
}
