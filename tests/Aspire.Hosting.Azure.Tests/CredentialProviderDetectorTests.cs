// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class CredentialProviderDetectorTests
{
    [Fact]
    public async Task DetectAvailableProvidersAsync_ReturnsAtLeastInteractiveBrowser()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var detector = new CredentialProviderDetector(logger);

        // Act - use CancellationToken with timeout to avoid hanging
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var providers = await detector.DetectAvailableProvidersAsync(tenantId: null, cts.Token);

        // Assert
        Assert.NotNull(providers);
        Assert.NotEmpty(providers);
        Assert.Contains("InteractiveBrowser", providers);
    }

    [Fact]
    public async Task DetectAvailableProvidersAsync_DoesNotThrow()
    {
        // Arrange
        var logger = NullLogger.Instance;
        var detector = new CredentialProviderDetector(logger);

        // Act & Assert - should not throw even if no credentials are configured
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var exception = await Record.ExceptionAsync(async () =>
        {
            var providers = await detector.DetectAvailableProvidersAsync(tenantId: null, cts.Token);
        });

        Assert.Null(exception);
    }
}
