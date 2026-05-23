// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Commands;

internal abstract class IntegrationDiscoveryCommand : BaseCommand
{
    private readonly IntegrationPackageSearchService _integrationPackageSearchService;
    private readonly OptionWithLegacy<FileInfo?> _appHostOption = new("--apphost", "--project", AddCommandStrings.IntegrationSearchAppHostOptionDescription);
    private readonly Option<OutputFormat> _formatOption = new("--format")
    {
        Description = AddCommandStrings.FormatOptionDescription
    };

    protected IntegrationDiscoveryCommand(
        string name,
        string description,
        IntegrationPackageSearchService integrationPackageSearchService,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base(name, description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _integrationPackageSearchService = integrationPackageSearchService;

        Options.Add(_appHostOption);
        Options.Add(_formatOption);
    }

    protected abstract string? GetSearchTerm(ParseResult parseResult);

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        try
        {
            var searchTerm = GetSearchTerm(parseResult);
            var passedAppHostProjectFile = parseResult.GetValue(_appHostOption);
            var format = parseResult.GetValue(_formatOption);

            var (workingDirectory, configuredChannel, contextExitCode) = await _integrationPackageSearchService.GetPackageSearchContextAsync(passedAppHostProjectFile, cancellationToken);
            if (contextExitCode is { } exitCode)
            {
                return CommandResult.FromExitCode(exitCode);
            }

            var packagesWithChannels = (await InteractionService.ShowStatusAsync(
                AddCommandStrings.SearchingForAspirePackages,
                async () => await _integrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync(workingDirectory, configuredChannel, cancellationToken)))
                .ToArray();

            var packagesWithShortName = packagesWithChannels
                .Select(IntegrationPackageSearchService.GenerateFriendlyName)
                .OrderBy(p => p.FriendlyName, new CommunityToolkitFirstComparer())
                .ToArray();

            return CommandResult.FromExitCode(DisplayIntegrationResults(packagesWithShortName, searchTerm, format));
        }
        catch (ProjectLocatorException ex)
        {
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Cancelled();
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ErrorOccurredWhileSearchingIntegrations, ex.Message);
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToSearchIntegrations, errorMessage);
        }
    }

    private int DisplayIntegrationResults(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string? searchTerm, OutputFormat format)
    {
        var matches = (searchTerm is null
            ? packages.Select(p => (p.FriendlyName, p.Package, p.Channel, SearchScore: 0.0))
            : IntegrationPackageSearchService.GetIntegrationSearchMatches(packages, searchTerm))
            .GroupBy(p => p.Package.Id)
            .Select(IntegrationPackageSearchService.SelectPreferredIntegrationPackage);

        var orderedMatches = searchTerm is null
            ? matches.OrderBy(p => p.FriendlyName, new CommunityToolkitFirstComparer()).ThenBy(p => p.Package.Id, StringComparer.OrdinalIgnoreCase)
            : matches;

        var results = orderedMatches
            .Select(p => new IntegrationSearchResult
            {
                Name = p.FriendlyName,
                Package = p.Package.Id,
                Version = p.Package.Version
            })
            .ToArray();

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(results, JsonSourceGenerationContext.RelaxedEscaping.IntegrationSearchResultArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
            return CliExitCodes.Success;
        }

        if (results.Length == 0)
        {
            if (searchTerm is not null)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NoIntegrationPackagesMatchedSearchTerm, searchTerm));
            }
            else
            {
                InteractionService.DisplayError(AddCommandStrings.NoPackagesFound);
            }

            return CliExitCodes.Success;
        }

        if (searchTerm is not null)
        {
            InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.FoundIntegrationPackagesMatchingSearchTerm, results.Length, searchTerm));
        }
        else
        {
            InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.FoundIntegrationPackages, results.Length));
        }

        var table = new Table();
        table.AddBoldColumn(AddCommandStrings.HeaderName);
        table.AddBoldColumn(AddCommandStrings.HeaderPackage);
        table.AddBoldColumn(AddCommandStrings.HeaderVersion);

        foreach (var result in results)
        {
            table.AddRow(
                Markup.Escape(result.Name),
                Markup.Escape(result.Package),
                Markup.Escape(result.Version));
        }

        InteractionService.DisplayRenderable(table);
        return CliExitCodes.Success;
    }
}

internal sealed class IntegrationListCommand : IntegrationDiscoveryCommand
{
    public IntegrationListCommand(
        IntegrationPackageSearchService integrationPackageSearchService,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("list", AddCommandStrings.IntegrationListDescription, integrationPackageSearchService, interactionService, features, updateNotifier, executionContext, telemetry)
    {
    }

    protected override string? GetSearchTerm(ParseResult parseResult) => null;
}

internal sealed class IntegrationSearchCommand : IntegrationDiscoveryCommand
{
    private readonly Argument<string> _queryArgument = new("query")
    {
        Description = AddCommandStrings.IntegrationSearchQueryArgumentDescription,
        Arity = ArgumentArity.ExactlyOne
    };

    public IntegrationSearchCommand(
        IntegrationPackageSearchService integrationPackageSearchService,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("search", AddCommandStrings.IntegrationSearchDescription, integrationPackageSearchService, interactionService, features, updateNotifier, executionContext, telemetry)
    {
        Arguments.Add(_queryArgument);
    }

    protected override string? GetSearchTerm(ParseResult parseResult) => parseResult.GetValue(_queryArgument);
}

// `aspire integration list --format json` and `aspire integration search --format json`
// use this shape; keep docs/specs/cli-output-formats.md in sync when changing it.
internal sealed class IntegrationSearchResult
{
    public required string Name { get; init; }

    public required string Package { get; init; }

    public required string Version { get; init; }
}
