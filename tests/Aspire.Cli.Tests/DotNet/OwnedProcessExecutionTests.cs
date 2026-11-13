// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Aspire.Shared.Tests;
using Aspire.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

[RequiresTools(["dotnet"])]
[Collection(ProcessTestCollection.Name)]
public class OwnedProcessExecutionTests(ProcessTestFixture fixture)
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 23)]
    [InlineData(true, 0)]
    [InlineData(true, 23)]
    public async Task OwnedHelper_PreservesStatusAndReapsWorkers(bool parentExitOptIn, int exitCode)
    {
        using var readiness = new ProcessTestReadiness();
        var factory = new ProcessExecutionFactory(new TestEnvironment(), NullLogger<ProcessExecutionFactory>.Instance);
        await using var execution = factory.CreateExecution(
            fixture.CreateStartInfo("tree-exit", readiness.Name, exitCode.ToString(CultureInfo.InvariantCulture)),
            new ProcessInvocationOptions
            {
                KillOnParentExit = parentExitOptIn,
                Lifetime = parentExitOptIn ? ChildProcessLifetime.CallerManaged : ChildProcessLifetime.OwnedTree,
                CreateSupervisorStartInfo = fixture.CreateSupervisorStartInfo
            });
        ProcessTestIdentity[] identities = [];
        try
        {
            await execution.StartAsync(TestContext.Current.CancellationToken);
            identities = await readiness.ReadTreeAsync();

            Assert.Equal(exitCode, await execution.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30)));
            await Task.WhenAll(identities.Select(ProcessTestFixture.AssertExitedAsync));
        }
        finally
        {
            foreach (var identity in identities)
            {
                ProcessTestFixture.KillIfRunning(identity);
            }
        }
    }
}
