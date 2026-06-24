// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;

namespace Aspire.Cli.Utils;

internal class ExtensionHelper
{
    public static bool IsExtensionHost(
        IInteractionService interactionService,
        [NotNullWhen(true)] out IExtensionInteractionService? extensionInteractionService,
        [NotNullWhen(true)] out IExtensionBackchannel? extensionBackchannel)
    {
        if (interactionService is IExtensionInteractionService eis)
        {
            extensionInteractionService = eis;
            extensionBackchannel = eis.Backchannel;
            return true;
        }

        extensionInteractionService = null;
        extensionBackchannel = null;
        return false;
    }
}

internal static class KnownCapabilities
{
    public const string DevKit = "devkit";
    public const string Project = "project";
    public const string Node = "node";
    public const string BuildDotnetUsingCli = "build-dotnet-using-cli";
    public const string Baseline = "baseline.v1";
    public const string SecretPrompts = "secret-prompts.v1";
    public const string FilePickers = "file-pickers.v1";
    public const string Pipelines = "pipelines";

    // Advertised so tooling (e.g. the VS Code extension) can detect that `aspire describe`
    // understands the hidden `--include-disabled-commands` flag without having to optimistically
    // pass it and parse (localized) error output when an older CLI rejects it.
    public const string DescribeIncludeDisabledCommands = "describe-include-disabled-commands.v1";

    /// <summary>
    /// Gets the set of capabilities this CLI advertises to extensions.
    /// </summary>
    public static string[] GetAdvertisedCapabilities() => [DevKit, Project, BuildDotnetUsingCli, Baseline, SecretPrompts, FilePickers, Pipelines, DescribeIncludeDisabledCommands];
}
