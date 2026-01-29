// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelCliInstallationManagerTests
{
    [Theory]
    [InlineData("1.0.1435", "1.0.1435", true)]
    [InlineData("1.0.1435", "1.0.1436", true)]
    [InlineData("1.0.1435", "1.1.1234", true)]
    [InlineData("1.0.1435", "2.0.0", true)]
    [InlineData("1.0.1435", "10.0.0", true)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("1.0.1435", "1.0.1434", false)]
    [InlineData("1.0.1435", "1.0.0", false)]
    [InlineData("1.0.1435", "0.0.1", false)]
    [InlineData("1.2.0", "1.1.999", false)]
    public async Task OnResolvedAsync_ReturnsInvalidForUnsupportedVersion(string minVersion, string testVersion, bool expectedIsValid)
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<DevTunnelCliInstallationManager>();
        var configuration = new ConfigurationBuilder().Build();
        var testCliVersion = Version.Parse(testVersion);
        var devTunnelClient = new TestDevTunnelClient(testCliVersion);

        var manager = new DevTunnelCliInstallationManager(devTunnelClient, configuration, new TestInteractionService(), logger, Version.Parse(minVersion));

        var (isValid, validationMessage) = await manager.OnResolvedAsync("thepath", CancellationToken.None);

        Assert.Equal(expectedIsValid, isValid);
        if (expectedIsValid)
        {
            Assert.Null(validationMessage);
        }
        else
        {
            Assert.Contains(minVersion, validationMessage);
            Assert.Contains(testVersion, validationMessage);
        }
    }

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    private sealed class TestInteractionService : IInteractionService
    {
        public bool IsAvailable => true;

        public Task<InteractionResult<bool>> PromptConfirmationAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<InteractionResult<bool>> PromptMessageBoxAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<InteractionResult<bool>> PromptNotificationAsync(string title, string message, NotificationInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    private sealed class TestDevTunnelClient(Version cliVersion) : IDevTunnelClient
    {
        public Task<Version> GetVersionAsync(ILogger? logger = null, CancellationToken cancellationToken = default) => Task.FromResult(cliVersion);

        public Task<DevTunnelPortStatus> CreatePortAsync(string tunnelId, int portNumber, DevTunnelPortOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<DevTunnelStatus> CreateTunnelAsync(string tunnelId, DevTunnelOptions options, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<DevTunnelAccessStatus> GetAccessAsync(string tunnelId, int? portNumber = null, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<DevTunnelStatus> GetTunnelAsync(string tunnelId, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<UserLoginStatus> GetUserLoginStatusAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<UserLoginStatus> UserLoginAsync(LoginProvider provider, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<DevTunnelPortList> GetPortListAsync(string tunnelId, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<DevTunnelPortDeleteResult> DeletePortAsync(string tunnelId, int portNumber, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
