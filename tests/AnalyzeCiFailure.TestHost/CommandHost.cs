// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.TestServices;
using Aspire.TestTools;

namespace AnalyzeCiFailure.TestHost;

/// <summary>Exercises the real CLI with HTTP fixtures instead of network access.</summary>
public static class CommandHost
{
    public static async Task<int> Main(string[] args)
    {
        using var http = new HttpClient(new GitHubFixtureHandler(Environment.CurrentDirectory));
        return await Program.RunAsync(
            args,
            () => new GitHubApiClient(http, _ => Task.FromResult("fixture-token"), new Uri("https://api.github.com/")));
    }
}
