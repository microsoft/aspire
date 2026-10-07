// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;

namespace Aspire.Cli.Tests.Utils;

internal static class GitTestHelper
{
    public static async Task EnsureGitAvailableAsync(ITestOutputHelper outputHelper)
    {
        try
        {
            await RunGitAsync(Directory.GetCurrentDirectory(), outputHelper, "--version");
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip($"git is required for this test but 'git --version' failed: {ex.Message}");
        }
    }

    public static async Task ConfigureGitIdentityAsync(string workingDirectory, ITestOutputHelper outputHelper)
    {
        // Fresh temporary repos have no inherited identity, but `git commit` requires
        // user.name and user.email. Set them locally to keep tests self-contained.
        await RunGitAsync(workingDirectory, outputHelper, "config", "user.email", "test@example.com");
        await RunGitAsync(workingDirectory, outputHelper, "config", "user.name", "Test User");
        await RunGitAsync(workingDirectory, outputHelper, "config", "commit.gpgsign", "false");
    }

    public static async Task RunGitAsync(string workingDirectory, ITestOutputHelper outputHelper, params string[] arguments)
    {
        try
        {
            await TemporaryWorkspace.RunGitAsync(workingDirectory, outputHelper, arguments, TestContext.Current.CancellationToken);
        }
        catch (Win32Exception ex)
        {
            outputHelper.WriteLine($"Failed to run git: {ex}");
            Assert.Skip("git is required for this test but was not found on PATH.");
        }
    }
}
