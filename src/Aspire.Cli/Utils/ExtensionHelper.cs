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

    // AppHost build ownership. The handshake is deliberately asymmetric: advertising this
    // capability means something different depending on which side of the backchannel does it.
    //
    //   - The CLI advertising it is a promise -- "I pre-build the AppHost before every launch,
    //     debug and no-debug alike". The extension reads that in InteractionService and passes
    //     forceBuild: false, skipping its own build.
    //   - An extension advertising it is a request -- "you own the pre-build, I will not do it".
    //     BuildAppHostIfNeededAsync reads that and builds. Without it the CLI stays out of the way,
    //     because an older extension host still builds for itself and a second build would
    //     duplicate work and race the extension's diagnostics/launch pipeline.
    //
    // The guarantee is narrower than "exactly one build": it is that neither side ever skips the
    // build believing the other side did it, so no launch runs stale output. A redundant build is
    // still possible and is deliberately tolerated -- a project-based AppHost with an `Executable`
    // launch profile always builds in the extension's debugger/languages/dotnet.ts, even when
    // forceBuild is false, because that build compiles the profile's dependencies rather than the
    // AppHost output. A wasted incremental build there is acceptable; a skipped one is not.
    //
    // The unversioned predecessor ("build-dotnet-using-cli", CLI 13.2.0-13.2.4) could not carry the
    // CLI-side promise: those versions advertised the token unconditionally but derived watch mode
    // from `isExtensionHost && !StartDebugSession`, so a no-debug launch skipped the CLI pre-build
    // entirely. An extension that believed the token skipped its build too, nobody built, and the
    // user silently launched stale output (https://github.com/microsoft/aspire/issues/15850). The
    // version suffix is what makes the promise verifiable, so matching stays exact: never accept
    // the unversioned token, and force a future revision to opt in deliberately.
    public const string BuildDotnetUsingCliV2 = "build-dotnet-using-cli.v2";
    public const string Baseline = "baseline.v1";
    public const string SecretPrompts = "secret-prompts.v1";
    public const string FilePickers = "file-pickers.v1";
    public const string Pipelines = "pipelines";

    // Advertised so tooling (e.g. the VS Code extension) can detect that `aspire describe`
    // understands the hidden `--include-disabled-commands` flag without having to optimistically
    // pass it and parse (localized) error output when an older CLI rejects it.
    public const string DescribeIncludeDisabledCommands = "describe-include-disabled-commands.v1";

    // Advertised so tooling can detect that `aspire ls --format json --stream` is supported
    // before opting into newline-delimited JSON candidate discovery.
    public const string LsJsonStream = "ls-json-stream.v1";

    /// <summary>
    /// Gets the set of capabilities this CLI advertises to extensions.
    /// </summary>
    public static string[] GetAdvertisedCapabilities() => [DevKit, Project, BuildDotnetUsingCliV2, Baseline, SecretPrompts, FilePickers, Pipelines, DescribeIncludeDisabledCommands, LsJsonStream];
}
