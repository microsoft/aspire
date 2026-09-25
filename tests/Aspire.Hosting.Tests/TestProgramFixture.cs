// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Testing.Tests;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
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
        _testProgram.AppBuilder.WithTestHostShutdownTimeout();

        _app = _testProgram.Build();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        await _app.StartAsync(cts.Token);

        await WaitReadyStateAsync(cts.Token);
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
        // Make sure services A, B and C are running
        await App.WaitForTextAsync("Application started.", "servicea", cancellationToken);
        using var clientA = App.CreateHttpClientWithResilience(TestProgram.ServiceABuilder.Resource.Name, "http");
        await clientA.GetStringAsync("/", cancellationToken);

        await App.WaitForTextAsync("Application started.", "serviceb", cancellationToken);
        using var clientB = App.CreateHttpClientWithResilience(TestProgram.ServiceBBuilder.Resource.Name, "http");
        await clientB.GetStringAsync("/", cancellationToken);

        await App.WaitForTextAsync("Application started.", "servicec", cancellationToken);
        using var clientC = App.CreateHttpClientWithResilience(TestProgram.ServiceCBuilder.Resource.Name, "http");
        await clientC.GetStringAsync("/", cancellationToken);
    }
}

[CollectionDefinition("SlimTestProgram")]
public class SlimTestProgramCollection : ICollectionFixture<SlimTestProgramFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
