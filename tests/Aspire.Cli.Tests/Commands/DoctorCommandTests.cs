// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DoctorCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DoctorCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Help should return success
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }
}
