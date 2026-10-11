// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing.Tests;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

/// <summary>
/// This fixture ensures the TestProgram application is started before a test is executed.
/// </summary>
public abstract class TestProgramFixture : IAsyncLifetime
{
    private DistributedApplication? _app;
    private TestProgram? _testProgram;

    public TestProgram TestProgram => _testProgram ?? throw new InvalidOperationException("TestProgram is not initialized.");

    public DistributedApplication App => _app ?? throw new InvalidOperationException("DistributedApplication is not initialized.");

    public abstract TestProgram CreateTestProgram();

    public abstract Task WaitReadyStateAsync(CancellationToken cancellationToken = default);

    public async ValueTask InitializeAsync()
    {
        _testProgram = CreateTestProgram();

        _app = _testProgram.Build();

        // Use separate timeout budgets for starting the app and waiting for it to become ready.
        // Sharing a single budget across both phases means a slow start can starve the readiness
        // wait of most of its allotted time, causing intermittent TaskCanceledExceptions.
        using (var startCts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration))
        {
            await _app.StartAsync(startCts.Token);
        }

        using var readyCts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        await WaitReadyStateAsync(readyCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _testProgram?.Dispose();
    }
}

/// <summary>
/// TestProgram with no dashboard, node app or integration services.
/// </summary>
/// <remarks>
/// Use <c>[Collection("SlimTestProgram")]</c> to inject this fixture in test constructors.
/// </remarks>
public class SlimTestProgramFixture : TestProgramFixture
{
    public override TestProgram CreateTestProgram()
    {
        return TestProgram.Create<DistributedApplicationTests>(randomizePorts: false);
    }

    public override async Task WaitReadyStateAsync(CancellationToken cancellationToken = default)
    {
        // Make sure services A, B and C are running. Wait for them in parallel rather than
        // sequentially so the total wait time isn't the sum of each service's individual wait,
        // which otherwise risks starving later services of most of the timeout budget under CI load.
        var waitForA = WaitForServiceReadyAsync(TestProgram.ServiceABuilder.Resource.Name, "servicea", cancellationToken);
        var waitForB = WaitForServiceReadyAsync(TestProgram.ServiceBBuilder.Resource.Name, "serviceb", cancellationToken);
        var waitForC = WaitForServiceReadyAsync(TestProgram.ServiceCBuilder.Resource.Name, "servicec", cancellationToken);

        await Task.WhenAll(waitForA, waitForB, waitForC);
    }

    private async Task WaitForServiceReadyAsync(string resourceName, string logResourceName, CancellationToken cancellationToken)
    {
        await App.WaitForTextAsync("Application started.", logResourceName, cancellationToken);
        using var client = App.CreateHttpClientWithResilience(resourceName, "http");
        await client.GetStringAsync("/", cancellationToken);
    }
}

[CollectionDefinition("SlimTestProgram")]
public class SlimTestProgramCollection : ICollectionFixture<SlimTestProgramFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
