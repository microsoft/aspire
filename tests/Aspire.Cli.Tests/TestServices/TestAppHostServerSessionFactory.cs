// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test implementation of <see cref="IAppHostServerSessionFactory"/> that returns a failure result
/// from <see cref="CreateAsync"/> and a <see cref="FakeAppHostServerSession"/> from <see cref="Start"/>.
/// </summary>
internal sealed class TestAppHostServerSessionFactory : IAppHostServerSessionFactory
{
    public Func<IAppHostServerProject, Dictionary<string, string>?, bool, IAppHostServerSession>? StartCallback { get; set; }

    public Task<AppHostServerSessionResult> CreateAsync(
        string appHostPath,
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        Dictionary<string, string>? launchSettingsEnvVars,
        bool debug,
        CancellationToken cancellationToken)
    {
        // Return a failure result for tests - most tests don't actually need to run an AppHost server
        var outputCollector = new OutputCollector();
        return Task.FromResult(new AppHostServerSessionResult(
            Success: false,
            Session: null,
            BuildOutput: outputCollector,
            ChannelName: null));
    }

    public IAppHostServerSession Start(
        IAppHostServerProject appHostServerProject,
        Dictionary<string, string>? environmentVariables,
        bool debug)
    {
        if (StartCallback is { } callback)
        {
            return callback(appHostServerProject, environmentVariables, debug);
        }

        return new FakeAppHostServerSession();
    }
}
