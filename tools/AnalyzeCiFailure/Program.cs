// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Invocation;
using Aspire.TestTools;

namespace AnalyzeCiFailure;

/// <summary>Parses command-line input and dispatches analyzer commands.</summary>
public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args, GitHubApiClient.CreateDefault);

    internal static async Task<int> RunAsync(string[] args, Func<GitHubApiClient> createGitHubClient)
    {
        // Preserve literal option values and reject @file arguments without reading response files.
        var result = AnalysisCommands.CreateRootCommand(createGitHubClient).Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        var exitCode = await result.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });

        return result.Action is ParseErrorAction ? 2 : exitCode;
    }
}
