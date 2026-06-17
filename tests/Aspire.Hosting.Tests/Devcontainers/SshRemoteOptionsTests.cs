// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Devcontainers;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests.Devcontainers;

[Trait("Partition", "4")]
public class SshRemoteOptionsTests
{
    [Fact]
    public void ConfigureSshRemoteOptions_SetsIsSshRemoteWhenBothEnvironmentVariablesPresent()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "VSCODE_IPC_HOOK_CLI", "/some/path/to/hook" },
                { "SSH_CONNECTION", "192.168.1.1 12345 192.168.1.2 22" }
            })
            .Build();

        var configureOptions = new ConfigureSshRemoteOptions(configuration);
        var options = new SshRemoteOptions();

        // Act
        configureOptions.Configure(options);

        // Assert
        Assert.True(options.IsSshRemote);
    }

    [Fact]
    public void ConfigureSshRemoteOptions_DoesNotSetIsSshRemoteWhenVscodeIpcHookCliMissing()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "SSH_CONNECTION", "192.168.1.1 12345 192.168.1.2 22" }
            })
            .Build();

        var configureOptions = new ConfigureSshRemoteOptions(configuration);
        var options = new SshRemoteOptions();

        // Act
        configureOptions.Configure(options);

        // Assert
        Assert.False(options.IsSshRemote);
    }

    [Fact]
    public void ConfigureSshRemoteOptions_DoesNotSetIsSshRemoteWhenSshConnectionMissing()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "VSCODE_IPC_HOOK_CLI", "/some/path/to/hook" }
            })
            .Build();

        var configureOptions = new ConfigureSshRemoteOptions(configuration);
        var options = new SshRemoteOptions();

        // Act
        configureOptions.Configure(options);

        // Assert
        Assert.False(options.IsSshRemote);
    }

    [Fact]
    public void ConfigureSshRemoteOptions_DoesNotSetIsSshRemoteWhenBothEnvironmentVariablesEmpty()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "VSCODE_IPC_HOOK_CLI", "" },
                { "SSH_CONNECTION", "" }
            })
            .Build();

        var configureOptions = new ConfigureSshRemoteOptions(configuration);
        var options = new SshRemoteOptions();

        // Act
        configureOptions.Configure(options);

        // Assert
        Assert.False(options.IsSshRemote);
    }

    [Fact]
    public void ConfigureSshRemoteOptions_DoesNotSetIsSshRemoteWhenNoEnvironmentVariables()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var configureOptions = new ConfigureSshRemoteOptions(configuration);
        var options = new SshRemoteOptions();

        // Act
        configureOptions.Configure(options);

        // Assert
        Assert.False(options.IsSshRemote);
    }
}