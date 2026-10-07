// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

internal static class GitTestHelper
{
    public static Task EnsureGitAvailableAsync(ITestOutputHelper outputHelper) =>
        RunGitAsync(Directory.GetCurrentDirectory(), outputHelper, "--version");

    public static async Task ConfigureGitIdentityAsync(string workingDirectory, ITestOutputHelper outputHelper)
    {
        // Fresh temporary repos have no inherited identity, but `git commit` requires
        // user.name and user.email. Set them locally to keep tests self-contained.
        await RunGitAsync(workingDirectory, outputHelper, "config", "user.email", "test@example.com");
        await RunGitAsync(workingDirectory, outputHelper, "config", "user.name", "Test User");
        await RunGitAsync(workingDirectory, outputHelper, "config", "commit.gpgsign", "false");
    }

    public static Task RunGitAsync(string workingDirectory, ITestOutputHelper outputHelper, params string[] arguments) =>
        TemporaryWorkspace.RunGitAsync(workingDirectory, outputHelper, arguments, TestContext.Current.CancellationToken);
}
