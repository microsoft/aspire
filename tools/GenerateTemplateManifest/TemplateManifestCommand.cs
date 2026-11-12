// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace GenerateTemplateManifest;

internal sealed class TemplateManifestCommand : RootCommand
{
    internal Argument<DirectoryInfo> SourceDirectory { get; } = new("source-templates-directory")
    {
        Description = "Directory containing the template sources.",
        Arity = ArgumentArity.ExactlyOne
    };

    internal Argument<DirectoryInfo> ProcessedDirectory { get; } = new("processed-templates-directory")
    {
        Description = "Directory containing templates with package versions substituted.",
        Arity = ArgumentArity.ExactlyOne
    };

    internal Argument<FileInfo> ManifestPath { get; } = new("cgmanifest.json")
    {
        Description = "Generated manifest output path.",
        Arity = ArgumentArity.ExactlyOne
    };

    internal Argument<FileInfo> NuGetConfigPath { get; } = new("nuget-config")
    {
        Description = "Repository NuGet configuration.",
        Arity = ArgumentArity.ExactlyOne
    };

    internal Argument<DirectoryInfo> LocalPackageFeed { get; } = new("local-package-feed")
    {
        Description = "Shipping package directory from the current build.",
        Arity = ArgumentArity.ExactlyOne
    };

    internal Argument<DirectoryInfo> RestoreDirectory { get; } = new("restore-directory")
    {
        Description = "Working directory for generated restore graphs and their package cache.",
        Arity = ArgumentArity.ExactlyOne
    };

    internal Option<bool> PlanOnly { get; } = new("--plan-only")
    {
        Description = "Generate the deduplicated restore plan without restoring packages."
    };

    internal TemplateManifestCommand() : base("Generate the component manifest for template dependencies.")
    {
        Arguments.Add(SourceDirectory);
        Arguments.Add(ProcessedDirectory);
        Arguments.Add(ManifestPath);
        Arguments.Add(NuGetConfigPath);
        Arguments.Add(LocalPackageFeed);
        Arguments.Add(RestoreDirectory);
        Options.Add(PlanOnly);

        SourceDirectory.AcceptExistingOnly();
        ProcessedDirectory.AcceptExistingOnly();
        NuGetConfigPath.AcceptExistingOnly();

    }
}
