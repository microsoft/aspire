// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.DotnetWatch;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.DotnetWatch;

public class DotnetWatchRunEnvironmentTests
{
    [Fact]
    public void RunEnvironmentEstablishesCliOwnershipWithoutLeakingAnOldHostGeneration()
    {
        var environment = new Dictionary<string, string>();

        DotnetWatchRunEnvironment.Apply(environment);

        Assert.Equal("true", Assert.Single(environment, e => e.Key == DotnetWatchRunEnvironment.WatchEnabledVariable).Value);
        Assert.Equal(DotnetWatchOwnership.CliOwner, environment[DotnetWatchOwnership.OwnerEnvironmentVariable]);
        Assert.Equal("", environment[DotnetWatchOwnership.AppHostControlEnvironmentVariable]);
        Assert.Equal("", environment[DotnetWatchOwnership.AppHostSessionEnvironmentVariable]);
    }

    [Fact]
    public void DisableOverridesInheritedWatchAndPrivateGenerationState()
    {
        var environment = DotnetWatchOwnership.PrivateEnvironmentVariables.ToDictionary(name => name, _ => "inherited");
        environment[DotnetWatchRunEnvironment.WatchEnabledVariable] = "true";
        environment["ApplicationVariable"] = "preserved";

        DotnetWatchRunEnvironment.Disable(environment);

        Assert.Equal("false", environment[DotnetWatchRunEnvironment.WatchEnabledVariable]);
        Assert.All(DotnetWatchOwnership.PrivateEnvironmentVariables, name => Assert.Equal("", environment[name]));
        Assert.Equal("preserved", environment["ApplicationVariable"]);
    }

    [Theory]
    [InlineData(LogLevel.Information, "🔥 Hot reload applied to api in 42 ms (Program.cs).", "🔥 Hot reload applied to api in 42 ms (Program.cs).")]
    [InlineData(LogLevel.Error, "❌ Build error in api: Program.cs(8,43): CS0117: [x]", "[red]❌ Build error in api: Program.cs(8,43): CS0117: [[x]][/]")]
    [InlineData(LogLevel.Warning, "📦 Api.csproj changed.", "[yellow]📦 Api.csproj changed.[/]")]
    public void WatchNoticesAreFormattedForTheTerminal(LogLevel level, string message, string expected)
    {
        var line = RunCommand.FormatWatchNotice(CreateEntry(RunCommand.WatchNoticeCategory, level, message));

        Assert.NotNull(line);
        Assert.StartsWith("[grey]", line);
        Assert.EndsWith($"[/] {expected}", line);
    }

    [Theory]
    [InlineData("Aspire.Hosting.Dotnet.Watch", LogLevel.Debug)]
    [InlineData("Aspire.Hosting.Dotnet.WatchServer", LogLevel.Information)]
    [InlineData("Aspire.Hosting.Dcp", LogLevel.Error)]
    public void OtherAppHostLogsAreNotShownAsWatchNotices(string category, LogLevel level)
    {
        Assert.Null(RunCommand.FormatWatchNotice(CreateEntry(category, level, "message")));
    }

    private static BackchannelLogEntry CreateEntry(string category, LogLevel level, string message) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        LogLevel = level,
        Message = message,
        EventId = new EventId(),
        CategoryName = category,
    };
}
