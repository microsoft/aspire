// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class ViteTestHelpers
{
    internal static string GetCreateCommand(string projectDirectory)
    {
        // Read devDependencies["create-vite"] from the embedded npm manifest so Dependabot
        // owns the pin instead of adopting newly published templates during a test run.
        using var stream = typeof(ViteTestHelpers).Assembly.GetManifestResourceStream("Aspire.Cli.EndToEnd.Tests.package.json")
            ?? throw new InvalidOperationException("The E2E npm dependency manifest is missing from the test assembly.");
        using var manifest = JsonDocument.Parse(stream);
        var version = manifest.RootElement.GetProperty("devDependencies").GetProperty("create-vite").GetString();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("The E2E npm dependency manifest must specify a create-vite version.");
        }

        return $"npm create -y {AspireCliShellCommandHelpers.QuoteBashArg($"vite@{version}")} {AspireCliShellCommandHelpers.QuoteBashArg(projectDirectory)} -- --template vanilla-ts --no-interactive";
    }
}
