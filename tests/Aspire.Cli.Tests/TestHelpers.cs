// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests;

internal static class TestHelpers
{
    public static ICliHostEnvironment CreateInteractiveHostEnvironment()
    {
        return new TestCliHostEnvironment(supportsInteractiveInput: true, supportsInteractiveOutput: true, supportsAnsi: true);
    }

    public static ICliHostEnvironment CreateNonInteractiveHostEnvironment()
    {
        return new TestCliHostEnvironment(supportsInteractiveInput: false, supportsInteractiveOutput: false, supportsAnsi: false);
    }

    public static void WriteEmptyIntegrationClosureFiles(FileInfo appHostFile)
    {
        var workingDir = CliManagedAppHostIntegrationClosureRestorer.GetOrCreateWorkingDirectory(appHostFile);
        var restoreDir = Path.Combine(workingDir.FullName, IntegrationClosureBuilder.IntegrationRestoreFolderName);
        Directory.CreateDirectory(restoreDir);
        File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureBuilder.ClosureSourcesFileName), string.Empty);
        File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureBuilder.ClosureMetadataFileName), string.Empty);
        File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureBuilder.ClosureTargetsFileName), string.Empty);
        File.WriteAllText(Path.Combine(restoreDir, IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName), string.Empty);
    }
}

internal sealed class TestCliHostEnvironment(bool supportsInteractiveInput = false, bool supportsInteractiveOutput = false, bool supportsAnsi = true) : ICliHostEnvironment
{
    public bool SupportsInteractiveInput => supportsInteractiveInput;

    public bool SupportsInteractiveOutput => supportsInteractiveOutput;

    public bool SupportsAnsi => supportsAnsi;
}
