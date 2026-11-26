// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class E2ETelemetryTests
{
    [Fact]
    public Task ChildProcessesInheritOptOutAndCanOverrideIt() =>
        E2ETelemetryEnvironmentTests.ChildProcessesInheritOptOutAndCanOverrideIt();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShellEnvironmentSetupOptsOutOfProductTelemetry(bool includeBundlePath) =>
        E2ETelemetryEnvironmentTests.ShellEnvironmentSetupOptsOutOfProductTelemetry(includeBundlePath);
}
