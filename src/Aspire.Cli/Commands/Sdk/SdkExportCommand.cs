// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Microsoft.Extensions.Logging;
using Semver;
using StreamJsonRpc;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Command for exporting the canonical API reference of an Aspire package for a target language.
///
/// Usage:
///   aspire sdk export --language typescript                                     # Core Aspire.Hosting at this CLI's SDK version
///   aspire sdk export --language typescript --package Aspire.Hosting.Redis@13.5.0
/// </summary>
/// <remarks>
/// The output is consumed by documentation pipelines, so stdout carries exactly one JSON document
/// and nothing else. Every status message, warning, and error goes to stderr, which is what makes
/// <c>aspire sdk export ... &gt; api.json</c> produce a usable file.
/// </remarks>
internal sealed class SdkExportCommand : BaseCommand
{
    private const string CorePackageName = "Aspire.Hosting";

    private readonly IAppHostServerProjectFactory _appHostServerProjectFactory;
    private readonly IAppHostServerSessionFactory _serverSessionFactory;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly ILogger<SdkExportCommand> _logger;

    private static readonly Option<string> s_languageOption = new("--language", "-l")
    {
        Description = "Target language for the API export (e.g., typescript).",
        Required = true
    };
    private static readonly Option<string?> s_packageOption = new("--package", "-p")
    {
        Description = "Package to export in PackageName@Version format. Defaults to the core Aspire.Hosting package at this CLI's SDK version."
    };
    private static readonly Option<string?> s_sourceOption = new("--source", "-s")
    {
        Description = "NuGet package source to restore the package from."
    };
    private static readonly Option<FileInfo?> s_outputOption = new("--output", "-o")
    {
        Description = "Output file. If not specified, the document is written to stdout."
    };

    public SdkExportCommand(
        IAppHostServerProjectFactory appHostServerProjectFactory,
        IAppHostServerSessionFactory serverSessionFactory,
        ILanguageDiscovery languageDiscovery,
        ILogger<SdkExportCommand> logger,
        CommonCommandServices services)
        : base("export", "Export the canonical API reference for an Aspire package in a target language.", services)
    {
        _appHostServerProjectFactory = appHostServerProjectFactory;
        _serverSessionFactory = serverSessionFactory;
        _languageDiscovery = languageDiscovery;
        _logger = logger;

        // Not marked Hidden: the parent `sdk` command already hides the whole subtree, and setting
        // Hidden here additionally suppresses this command's own --help output.
        Options.Add(s_languageOption);
        Options.Add(s_packageOption);
        Options.Add(s_sourceOption);
        Options.Add(s_outputOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // This command always emits machine-readable JSON, so it has no --format option for
        // BaseCommand's json redirect to key off. Without this, preparation diagnostics and the
        // --output success message land on stdout and corrupt the document a caller is piping.
        // The JSON write overrides back to stdout explicitly, and an explicit override wins.
        InteractionService.Console = ConsoleOutput.Error;

        var language = parseResult.GetValue(s_languageOption)!;
        var package = parseResult.GetValue(s_packageOption);
        var packageSource = parseResult.GetValue(s_sourceOption);
        var outputFile = parseResult.GetValue(s_outputOption);

        string packageName;
        string packageVersion;
        var integrations = new List<IntegrationReference>();

        if (string.IsNullOrWhiteSpace(package))
        {
            // Documentation has to describe the SDK this CLI generates against, so the default is
            // the CLI's own identity version rather than whatever the feed currently calls latest.
            packageName = CorePackageName;
            packageVersion = ExecutionContext.IdentityVersion;
        }
        else
        {
            if (!SdkCommandPreparation.TryParseIntegrationArgument(
                    package,
                    requireExactVersion: true,
                    out var reference,
                    out var errorExitCode,
                    out var errorMessage))
            {
                return CommandResult.Failure(errorExitCode, errorMessage!);
            }

            if (reference!.Version is null)
            {
                return CommandResult.Failure(
                    CliExitCodes.InvalidCommand,
                    $"Invalid package '{package}'. Expected PackageName@Version (e.g. Aspire.Hosting.Redis@13.5.0); project references are not supported by sdk export.");
            }

            packageVersion = reference.Version;

            // NuGet package ids are case-insensitive, so `aspire.hosting` names the core package
            // exactly as `Aspire.Hosting` does:
            // https://learn.microsoft.com/nuget/consume-packages/finding-and-choosing-packages#package-identifiers.
            // Nothing downstream is. The substitution probe resolves the name through the
            // filesystem, the generated scanner project-references src/Aspire.Hosting under the
            // canonical spelling no matter what was asked for, and the exported document records
            // this string verbatim as the package identity documentation is keyed on. Settling on
            // the canonical spelling here — before the scanner project is created and validated —
            // is what keeps the guard, the scanner, and the label describing one package.
            var isCorePackage = string.Equals(reference.Name, CorePackageName, StringComparison.OrdinalIgnoreCase);
            packageName = isCorePackage ? CorePackageName : reference.Name;

            // The core package is always restored by the scanner AppHost, so adding it again would
            // produce a duplicate package reference.
            if (isCorePackage)
            {
                // The scanner loads the core assemblies this CLI was built against, so a different
                // requested version would be exported as this CLI's surface under someone else's
                // version number. That is the same stale-signature problem this command exists to
                // fix, so refuse instead of labelling the export with a version it does not describe.
                var requested = StripBuildMetadata(packageVersion);
                if (!string.Equals(requested, ExecutionContext.IdentitySdkVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Failure(
                        CliExitCodes.InvalidCommand,
                        $"This CLI can only export {CorePackageName}@{ExecutionContext.IdentitySdkVersion}, but {packageVersion} was requested. " +
                        $"The scanner loads the core assemblies this CLI ships with, so exporting a different version would describe the wrong API surface. " +
                        $"Run the export with the {requested} CLI instead.");
                }
            }
            else
            {
                // Pin the requested version: a bare NuGet version is a minimum, so an unavailable
                // version would restore as a later one and be published under the wrong number.
                // Use packageName rather than reference.Name so the restored reference and the
                // exported label can never name the package differently.
                integrations.Add(IntegrationReference.FromExactPackage(packageName, reference.Version));
            }
        }

        // The code generator lives in a separate package that the scanner AppHost does not reference
        // by default, so without this the server loads no generators and every export fails with
        // "No code generator found". `sdk generate` adds the same package for the same reason.
        var codeGenPackage = await GetCodeGenerationPackageAsync(language, cancellationToken);
        if (codeGenPackage is not null)
        {
            integrations.Add(IntegrationReference.FromPackage(codeGenPackage, ExecutionContext.IdentityVersion));
        }

        return CommandResult.FromExitCode(await ExportApiAsync(
            language,
            packageName,
            packageVersion,
            integrations,
            packageSource,
            outputFile,
            cancellationToken));
    }

    /// <summary>
    /// Resolves the code generation package that provides the requested language, matching the way
    /// <c>sdk generate</c> resolves it. Returns <see langword="null"/> when the language is unknown so
    /// that the server produces the authoritative unsupported-language error.
    /// </summary>
    private async Task<string?> GetCodeGenerationPackageAsync(string language, CancellationToken cancellationToken)
    {
        try
        {
            var languages = await _languageDiscovery.GetAvailableLanguagesAsync(cancellationToken);

            var languageInfo = languages.FirstOrDefault(l =>
                l.LanguageId.Value.StartsWith(language, StringComparison.OrdinalIgnoreCase) ||
                l.CodeGenerator.Equals(language, StringComparison.OrdinalIgnoreCase));

            if (languageInfo is null)
            {
                return null;
            }

            return await _languageDiscovery.GetPackageForLanguageAsync(languageInfo.LanguageId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve the code generation package for language {Language}", language);
            return null;
        }
    }

    /// <summary>
    /// Drops SemVer build metadata so <c>13.5.0+abc123</c> and <c>13.5.0</c> compare equal, matching
    /// how <see cref="CliExecutionContext.IdentitySdkVersion"/> normalizes this CLI's own version.
    /// </summary>
    private static string StripBuildMetadata(string version)
    {
        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        return plusIndex < 0 ? version : version[..plusIndex];
    }

    /// <summary>
    /// Refuses an export the scanner would satisfy from a local checkout instead of restoring the
    /// requested package version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In repository development mode the scanner AppHost replaces every first-party
    /// <c>Aspire.Hosting.*</c> package reference that exists under <c>src/</c> with that project and
    /// discards the requested version, so the checkout's API surface would be published under
    /// someone else's version number. That is the same stale-signature problem the core-package
    /// guard prevents, so this refuses for the same reason. Asking for the version this CLI was
    /// built from is still allowed: that is exactly what the checkout contains. Third-party packages
    /// are never substituted, so they fall straight through.
    /// </para>
    /// <para>
    /// The check cannot rest on <see cref="CliExecutionContext.IdentitySdkVersion"/> alone, because
    /// that value is overrideable by design (<c>ASPIRE_CLI_VERSION</c>, the install sidecar) and
    /// would let a caller name local source whatever they like. The checkout's own version line is
    /// the independent half; the identity is still compared so a checkout on the right line cannot
    /// publish a neighbouring build's number.
    /// </para>
    /// <para>
    /// The core package needs both halves as well. Neither <see cref="IAppHostServerProject"/>
    /// implementation honours the requested SDK version — the repository scanner builds
    /// <c>src/Aspire.Hosting</c> and the prebuilt scanner loads the assemblies bundled with the CLI
    /// — so a core export always describes this CLI, and the label has to be this CLI's real
    /// version. The comparison that enforces that runs before any project is created, but it
    /// compares the request against the identity while the default request <em>is</em> the identity,
    /// so on its own it only catches an explicitly wrong <c>--package</c>. Two cases get past it.
    /// An <c>ASPIRE_CLI_VERSION</c> override makes the identity itself caller-controlled, and the
    /// prebuilt scanner has no second signal to check it against, so that override is refused
    /// outright. It has to be that specific signal rather than the <c>IdentityOverridden</c>
    /// aggregate, which every installed CLI trips through its install sidecar. Repository mode
    /// is entered through <c>ASPIRE_REPO_ROOT</c>, which is not an identity field at all, so an
    /// installed CLI can be pointed at a checkout on a different version line with no override in
    /// effect; the core package therefore falls through to the same checkout comparison every other
    /// first-party package gets, which is available because <c>src/Aspire.Hosting</c> is always
    /// project-referenced by the generated scanner.
    /// </para>
    /// </remarks>
    /// <param name="serverProject">The scanner AppHost that will restore the export.</param>
    /// <param name="packageName">The package being exported.</param>
    /// <param name="packageVersion">The version the caller asked for.</param>
    /// <returns>The rejection reason, or <see langword="null"/> when the request is exportable.</returns>
    private string? ValidateRequestedPackageIsRestorable(IAppHostServerProject serverProject, string packageName, string packageVersion)
    {
        var isCorePackage = string.Equals(packageName, CorePackageName, StringComparison.OrdinalIgnoreCase);

        if (isCorePackage && ExecutionContext.IdentityVersionForged)
        {
            // The prebuilt scanner has no second signal: the core assemblies come from the bundle
            // this CLI shipped with, so a forged version leaves nothing to check the label against.
            // Repository mode does have one, and falls through to it below.
            //
            // This tests IdentityVersionForged rather than the IdentityOverridden aggregate on
            // purpose. Every install route writes a sidecar carrying channel and version, so the
            // aggregate is true for an ordinary installed CLI and gating on it rejected the
            // advertised default export on exactly the installs the error told callers to use.
            return $"The scanner loads the {CorePackageName} assemblies this CLI ships with, so an export of it describes this CLI. " +
                   $"This run claims a different version through ASPIRE_CLI_VERSION, so the export cannot be attributed to a real build of {packageVersion}. " +
                   $"Re-run without the override, or export {CorePackageName} from an installed CLI.";
        }

        // The name arrives canonical: the caller settles the core package on CorePackageName before
        // the scanner project is created, because this lookup resolves through the filesystem and
        // would miss `aspire.hosting` on a case-sensitive one while the scanner still built
        // src/Aspire.Hosting.
        if (serverProject.GetLocalProjectSubstitution(packageName) is not { } substitution)
        {
            return null;
        }

        var preamble = $"This CLI runs from an Aspire repository checkout, so {packageName} is built from {substitution.ProjectPath} " +
                       $"instead of being restored from a package feed.";

        if (ExecutionContext.IdentityVersionForged)
        {
            // A forged version makes this run an emulation of a build the checkout is not, which is
            // exactly the combination that cannot be checked: both the source and the label are
            // caller-controlled. Every other ASPIRE_CLI_* override stays available here, and a
            // sidecar version does not qualify because the installer wrote it.
            return $"{preamble} This run also claims a version through ASPIRE_CLI_VERSION, so nothing can confirm the " +
                   $"checkout really is {packageVersion}. Re-run without the override, or export {packageName} from an installed CLI.";
        }

        if (substitution.CheckoutVersionPrefix is not string checkoutPrefix)
        {
            return $"{preamble} This checkout does not say which version it builds (eng/Versions.props is missing or unreadable), " +
                   $"so an export labelled {packageVersion} cannot be verified. Export {packageName} from an installed CLI instead.";
        }

        if (!SemVersion.TryParse(packageVersion, SemVersionStyles.Any, out var requestedVersion)
            || $"{requestedVersion.Major}.{requestedVersion.Minor}.{requestedVersion.Patch}" != checkoutPrefix)
        {
            // A core export takes its version from this CLI's identity rather than from --package,
            // so telling the caller to re-run with the requested version's CLI would name the CLI
            // they are already running. There the checkout is the half that has to move.
            return isCorePackage
                ? $"{preamble} That checkout builds {checkoutPrefix}, but this CLI is {packageVersion}, and exporting it " +
                  $"would describe the checkout's API surface under this CLI's version. " +
                  $"Export {packageName} from a {checkoutPrefix} CLI, or point this one at a {StripBuildMetadata(packageVersion)} checkout."
                : $"{preamble} That checkout builds {checkoutPrefix}, but {packageVersion} was requested, and exporting it " +
                  $"would describe the checkout's API surface under the requested version. " +
                  $"Run the export with the {StripBuildMetadata(packageVersion)} CLI instead.";
        }

        var requested = StripBuildMetadata(packageVersion);
        if (!string.Equals(requested, ExecutionContext.IdentitySdkVersion, StringComparison.OrdinalIgnoreCase))
        {
            return $"{preamble} That checkout is {ExecutionContext.IdentitySdkVersion}, but {packageVersion} was requested, " +
                   $"and exporting it would describe the checkout's API surface under the requested version. " +
                   $"Run the export with the {requested} CLI, or request {packageName}@{ExecutionContext.IdentitySdkVersion}.";
        }

        return null;
    }

    private async Task<int> ExportApiAsync(
        string language,
        string packageName,
        string packageVersion,
        List<IntegrationReference> integrations,
        string? packageSource,
        FileInfo? outputFile,
        CancellationToken cancellationToken)
    {
        // The AppHost is restored at the version being documented so the export describes that exact
        // SDK, not the CLI's bundled one.
        var sdkVersion = string.Equals(packageName, CorePackageName, StringComparison.OrdinalIgnoreCase)
            ? packageVersion
            : ExecutionContext.IdentityVersion;

        string? rejection = null;

        await using var session = await SdkCommandPreparation.PrepareSessionAsync(
            _appHostServerProjectFactory,
            _serverSessionFactory,
            InteractionService,
            _logger,
            "aspire-sdk-export-",
            sdkVersion,
            integrations,
            packageSource,
            validateProject: serverProject => rejection = ValidateRequestedPackageIsRestorable(serverProject, packageName, packageVersion),
            cancellationToken);

        if (session is null)
        {
            // A rejection is a usage error the caller fixes by asking for a different version or
            // running a different CLI, so it must not look like the scanner failed to build.
            return rejection is not null
                ? CliExitCodes.InvalidCommand
                : CliExitCodes.FailedToBuildArtifacts;
        }

        JsonElement export;
        try
        {
            _logger.LogDebug("Exporting {Language} API reference for {PackageName}@{PackageVersion} via RPC", language, packageName, packageVersion);
            export = await session.RpcClient.ExportApiAsync(language, packageName, packageVersion, cancellationToken);
        }
        catch (RemoteInvocationException ex)
        {
            InteractionService.DisplayError(ex.Message);

            // An unsupported language is a usage error the caller can fix by choosing another
            // language, so it is worth distinguishing from the AppHost genuinely falling over.
            return IsUnsupportedLanguage(ex)
                ? CliExitCodes.InvalidCommand
                : CliExitCodes.FailedToBuildArtifacts;
        }
        catch (NotSupportedException ex)
        {
            InteractionService.DisplayError(ex.Message);
            return CliExitCodes.InvalidCommand;
        }

        // GetRawText is the document exactly as the language provider wrote it. Re-serializing would
        // reshape whitespace and property order for no benefit, and the whole contract here is that
        // the payload passes through untouched.
        var json = export.GetRawText();

        if (outputFile is not null)
        {
            var outputDir = outputFile.Directory;
            if (outputDir is not null && !outputDir.Exists)
            {
                outputDir.Create();
            }

            await File.WriteAllTextAsync(outputFile.FullName, json, cancellationToken);
            InteractionService.DisplaySuccess($"API reference written to {outputFile.FullName}");
            return CliExitCodes.Success;
        }

        InteractionService.DisplayRawText(json, consoleOverride: ConsoleOutput.Standard);
        return CliExitCodes.Success;
    }

    // RemoteHost raises NotSupportedException for a generator that cannot export; StreamJsonRpc
    // flattens that to a message string, so the type name is the only marker that survives the wire.
    private static bool IsUnsupportedLanguage(RemoteInvocationException ex)
        => ex.Message.Contains("IApiReferenceExporter", StringComparison.Ordinal)
           || ex.Message.Contains("No code generator found for language", StringComparison.Ordinal);
}
