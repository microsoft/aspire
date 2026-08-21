// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Semver;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Exports a package's canonical API reference for a target language.
/// </summary>
/// <remarks>
/// Standard output contains only the JSON document. Preparation diagnostics and errors are written
/// to standard error so the command can be redirected directly to a file.
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
        Description = "Package to export in PackageName@Version form. Defaults to Aspire.Hosting at this CLI's SDK version."
    };

    public SdkExportCommand(
        IAppHostServerProjectFactory appHostServerProjectFactory,
        IAppHostServerSessionFactory serverSessionFactory,
        ILanguageDiscovery languageDiscovery,
        ILogger<SdkExportCommand> logger,
        CommonCommandServices services)
        : base("export", "Export a canonical package API reference.", services)
    {
        _appHostServerProjectFactory = appHostServerProjectFactory;
        _serverSessionFactory = serverSessionFactory;
        _languageDiscovery = languageDiscovery;
        _logger = logger;

        Options.Add(s_languageOption);
        Options.Add(s_packageOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        InteractionService.Console = ConsoleOutput.Error;

        var language = parseResult.GetValue(s_languageOption)!;
        if (string.IsNullOrWhiteSpace(language))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, "The export language cannot be empty.");
        }

        var packageArgument = parseResult.GetValue(s_packageOption);
        var packageName = CorePackageName;
        var packageVersion = ExecutionContext.IdentitySdkVersion;
        var integrations = new List<IntegrationReference>();

        if (!string.IsNullOrWhiteSpace(packageArgument))
        {
            if (!TryParsePackage(packageArgument, out packageName, out packageVersion, out var errorMessage))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, errorMessage);
            }

            if (string.Equals(packageName, CorePackageName, StringComparison.OrdinalIgnoreCase))
            {
                packageName = CorePackageName;
                if (!string.Equals(packageVersion, ExecutionContext.IdentitySdkVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Failure(
                        CliExitCodes.InvalidCommand,
                        $"This CLI exports {CorePackageName} at {ExecutionContext.IdentitySdkVersion}; {packageVersion} was requested.");
                }
            }
            else
            {
                integrations.Add(CreateExactPackageReference(packageName, packageVersion));
            }
        }

        var physicalSdkVersion = VersionHelper.GetDefaultSdkVersion();
        if (string.Equals(packageName, CorePackageName, StringComparison.Ordinal) &&
            !string.Equals(ExecutionContext.IdentitySdkVersion, physicalSdkVersion, StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Failure(
                CliExitCodes.InvalidCommand,
                $"This CLI reports SDK version {ExecutionContext.IdentitySdkVersion}, but its embedded {CorePackageName} surface is from {physicalSdkVersion}.");
        }

        var languageInfo = await FindLanguageAsync(language, cancellationToken);
        if (languageInfo is not null)
        {
            var codeGenerationPackage = await _languageDiscovery.GetPackageForLanguageAsync(
                languageInfo.LanguageId,
                cancellationToken);

            if (codeGenerationPackage is not null &&
                !integrations.Any(integration =>
                    integration.Name.Equals(codeGenerationPackage, StringComparison.OrdinalIgnoreCase)))
            {
                // Match sdk generate: repository mode uses the generator from this checkout, while
                // installed CLIs restore the package that accompanies their build.
                integrations.Add(IntegrationReference.FromPackage(
                    codeGenerationPackage,
                    ExecutionContext.IdentityVersion));
            }
        }

        var exitCode = await ExportApiAsync(
            languageInfo?.CodeGenerator ?? language,
            packageName,
            packageVersion,
            integrations,
            cancellationToken);

        return CommandResult.FromExitCode(exitCode);
    }

    private async Task<LanguageInfo?> FindLanguageAsync(string language, CancellationToken cancellationToken)
    {
        try
        {
            var languages = await _languageDiscovery.GetAvailableLanguagesAsync(cancellationToken);
            return languages.FirstOrDefault(candidate =>
                candidate.LanguageId.Value.StartsWith(language, StringComparison.OrdinalIgnoreCase) ||
                candidate.CodeGenerator.Equals(language, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to resolve export language {Language}", language);
            return null;
        }
    }

    private async Task<int> ExportApiAsync(
        string language,
        string packageName,
        string packageVersion,
        IReadOnlyList<IntegrationReference> integrations,
        CancellationToken cancellationToken)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-sdk-export-");
        var tempDirectoryPath = tempDirectory.FullName;

        try
        {
            var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(
                tempDirectoryPath,
                cancellationToken);

            var prepareResult = await appHostServerProject.PrepareAsync(
                ExecutionContext.IdentityVersion,
                integrations,
                cancellationToken: cancellationToken);

            if (!prepareResult.Success)
            {
                InteractionService.DisplayError("Failed to build the API export scanner.");
                if (prepareResult.Output is not null)
                {
                    foreach (var (_, line) in prepareResult.Output.GetLines())
                    {
                        InteractionService.DisplayMessage(KnownEmojis.Wrench, line);
                    }
                }

                return CliExitCodes.FailedToBuildArtifacts;
            }

            await using var serverSession = _serverSessionFactory.Create(
                appHostServerProject,
                environmentVariables: null,
                debug: false,
                gracefulShutdownSignaler: null,
                shutdownService: null,
                isolateConsole: false,
                cancellationToken);

            await serverSession.StartAsync();
            var rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

            JsonElement export;
            try
            {
                export = await rpcClient.ExportApiAsync(
                    language,
                    packageName,
                    packageVersion,
                    cancellationToken);
            }
            catch (NotSupportedException ex)
            {
                InteractionService.DisplayError(ex.Message);
                return CliExitCodes.InvalidCommand;
            }
            catch (RemoteInvocationException ex) when (ex.ErrorCode == (int)JsonRpcErrorCode.InvalidParams)
            {
                InteractionService.DisplayError(ex.Message);
                return CliExitCodes.InvalidCommand;
            }
            catch (Exception ex) when (ex is RemoteInvocationException or AppHostCodeGenerationException)
            {
                InteractionService.DisplayError(ex.Message);
                return CliExitCodes.FailedToBuildArtifacts;
            }

            var json = export.GetRawText()
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            InteractionService.DisplayRawText(json, consoleOverride: ConsoleOutput.Standard);
            return CliExitCodes.Success;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectoryPath))
                {
                    Directory.Delete(tempDirectoryPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up API export directory {TempDirectory}", tempDirectoryPath);
            }
        }
    }

    private static IntegrationReference CreateExactPackageReference(string packageName, string packageVersion)
        => IntegrationReference.FromPackage(
            packageName,
            $"[{packageVersion}]",
            disableLocalProjectSubstitution: true);

    private static bool TryParsePackage(
        string argument,
        out string packageName,
        out string packageVersion,
        out string errorMessage)
    {
        packageName = string.Empty;
        packageVersion = string.Empty;
        errorMessage = string.Empty;

        // Parse the literal PackageName@Version shape. NuGet package IDs cannot contain '@', so an
        // additional separator is malformed rather than part of the package name.
        var separatorIndex = argument.LastIndexOf('@');
        if (separatorIndex <= 0 ||
            separatorIndex == argument.Length - 1 ||
            argument.AsSpan(0, separatorIndex).Contains('@'))
        {
            errorMessage = $"Invalid package '{argument}'. Expected PackageName@Version.";
            return false;
        }

        packageName = argument[..separatorIndex];
        var requestedVersion = argument[(separatorIndex + 1)..];
        if (packageName.Any(char.IsWhiteSpace))
        {
            errorMessage = $"Invalid package '{packageName}'. NuGet package IDs cannot contain whitespace.";
            return false;
        }

        if (requestedVersion.Any(char.IsWhiteSpace))
        {
            errorMessage = $"Invalid version '{requestedVersion}'. Expected an exact NuGet version.";
            return false;
        }

        if (SemVersion.TryParse(requestedVersion, SemVersionStyles.Any, out var parsedVersion))
        {
            packageVersion = parsedVersion.ToString();
        }
        else if (!TryNormalizeFourPartVersion(requestedVersion, out packageVersion))
        {
            errorMessage = $"Invalid version '{requestedVersion}'. Expected an exact NuGet version.";
            return false;
        }

        var buildMetadataIndex = packageVersion.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataIndex >= 0)
        {
            packageVersion = packageVersion[..buildMetadataIndex];
        }

        return true;
    }

    private static bool TryNormalizeFourPartVersion(string version, out string normalizedVersion)
    {
        normalizedVersion = string.Empty;

        // NuGet accepts a four-component numeric version that SemVer does not:
        //   1.2.3.4
        // Keep SemVersion as the primary parser so ordinary versions and prerelease labels retain
        // their existing normalization, then narrowly fall back to System.Version for this shape.
        var components = version.Split('.');
        if (components.Length != 4 ||
            components.Any(static component =>
                component.Length == 0 || component.Any(static character => !char.IsAsciiDigit(character))) ||
            !Version.TryParse(version, out var parsedVersion))
        {
            return false;
        }

        normalizedVersion = parsedVersion.Revision == 0
            ? parsedVersion.ToString(3)
            : parsedVersion.ToString(4);
        return true;
    }
}
