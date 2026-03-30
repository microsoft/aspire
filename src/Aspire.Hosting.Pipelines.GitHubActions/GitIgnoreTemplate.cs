// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Provides a sensible .gitignore template for Aspire projects.
/// </summary>
internal static class GitIgnoreTemplate
{
    public const string Content = """
        ## .NET
        bin/
        obj/
        artifacts/
        TestResults/
        *.user
        *.suo
        *.userosscache
        *.sln.docstates

        ## NuGet
        *.nupkg
        **/[Pp]ackages/*
        !**/[Pp]ackages/build/

        ## IDE
        .vs/
        .vscode/
        .idea/
        *.swp
        *~

        ## Aspire
        .aspire/state/
        .aspire/secrets/

        ## OS
        .DS_Store
        Thumbs.db
        """;
}
