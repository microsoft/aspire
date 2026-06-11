// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.TestUtilities;
using Aspire.TestUtilities;
using DotNet.Testcontainers.Builders;
using Testcontainers.Oracle;
using Xunit;
using Xunit.Sdk;

namespace Aspire.Oracle.EntityFrameworkCore.Tests;

public sealed class OracleContainerFixture : IAsyncLifetime
{
    private readonly IMessageSink _diagnosticMessageSink;

    public OracleContainer? Container { get; private set; }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public OracleContainerFixture(IMessageSink diagnosticMessageSink)
    {
        _diagnosticMessageSink = diagnosticMessageSink;
    }

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            _diagnosticMessageSink.OnMessage(new DiagnosticMessage("Oracle container initialization starting..."));
            Container = new OracleBuilder()
                .WithPortBinding(1521, true)
                .WithHostname("localhost")
                .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/gvenzl/oracle-xe:21.3.0-slim-faststart")
                .WithWaitStrategy(Wait
                    .ForUnixContainer()
                    .UntilMessageIsLogged("Completed: ALTER DATABASE OPEN")
                ).Build();

            _diagnosticMessageSink.OnMessage(new DiagnosticMessage("Starting Oracle container..."));
            await Container.StartAsync();
            _diagnosticMessageSink.OnMessage(new DiagnosticMessage($"Oracle container started. Connection string: {GetConnectionString()}"));
        }
        else
        {
            _diagnosticMessageSink.OnMessage(new DiagnosticMessage("Docker is not supported on this platform. Oracle tests will be skipped."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
        {
            _diagnosticMessageSink.OnMessage(new DiagnosticMessage("Disposing Oracle container..."));
            await Container.DisposeAsync();
            _diagnosticMessageSink.OnMessage(new DiagnosticMessage("Oracle container disposed."));
        }
    }
}

[CollectionDefinition("Oracle Database collection")]
public class DatabaseCollection : ICollectionFixture<OracleContainerFixture>
{

}
