// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelCliVersionValidationTests
{
    [Theory]
    [InlineData("1.0.1435", true)]
    [InlineData("1.0.1436", true)]
    [InlineData("1.1.1234", true)]
    [InlineData("2.0.0", true)]
    [InlineData("10.0.0", true)]
    [InlineData("1.10.0", true)]
    [InlineData("1.0.1434", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.0.1", false)]
    [InlineData("1.1.999", true)]
    public async Task ValidateDevTunnelCliVersionAsync_ReturnsInvalidForUnsupportedVersion(string testVersion, bool expectedIsValid)
    {
        var testCliVersion = Version.Parse(testVersion);
        var devTunnelClient = new TestDevTunnelClient(testCliVersion);

        var services = new ServiceCollection()
            .AddSingleton<IDevTunnelClient>(devTunnelClient)
            .BuildServiceProvider();

        var context = new RequiredCommandValidationContext("thepath", services, CancellationToken.None);
        var result = await DevTunnelsResourceBuilderExtensions.ValidateDevTunnelCliVersionAsync(context);

        Assert.Equal(expectedIsValid, result.IsValid);
        if (expectedIsValid)
        {
            Assert.Null(result.ValidationMessage);
        }
        else
        {
            Assert.Contains(DevTunnelCli.MinimumSupportedVersion.ToString(), result.ValidationMessage);
            Assert.Contains(testVersion, result.ValidationMessage);
        }
    }

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
