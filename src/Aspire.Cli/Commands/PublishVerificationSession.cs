// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Coordinates staging, authorization, reconciliation, and cleanup for <c>publish --verify</c>.
/// </summary>
internal sealed class PublishVerificationSession : IPipelineExecutionSession
{
    private const string PipelineOutputsCapability = "pipeline-outputs.v1";
    private const string PrimaryPublisherName = "aspire";
    private const string PrimaryOutputName = "primary";
    private const string PreparedState = "Prepared";
    private const string SucceededState = "Succeeded";

    private readonly DirectoryInfo _appHostDirectory;
    private readonly DirectoryInfo _repositoryRoot;
    private readonly DirectoryInfo _stagingDirectory;
    private readonly string _logicalPrimaryTargetPath;
    private readonly IGitRepository _gitRepository;
    private readonly IInteractionService _interactionService;
    private readonly ILogger _logger;
    private readonly string[] _regenerateArguments;
    private PublishVerificationOutput[]? _outputs;
    private PipelineOutputStepInfo[]? _steps;
    private PublishVerificationInventory[]? _inventory;
    private bool _finalStateCaptured;

    private PublishVerificationSession(
        DirectoryInfo appHostDirectory,
        DirectoryInfo repositoryRoot,
        DirectoryInfo stagingDirectory,
        string logicalPrimaryTargetPath,
        IGitRepository gitRepository,
        IInteractionService interactionService,
        ILogger logger,
        string[] regenerateArguments)
    {
        _appHostDirectory = appHostDirectory;
        _repositoryRoot = repositoryRoot;
        _stagingDirectory = stagingDirectory;
        _logicalPrimaryTargetPath = logicalPrimaryTargetPath;
        _gitRepository = gitRepository;
        _interactionService = interactionService;
        _logger = logger;
        _regenerateArguments = regenerateArguments;

        OutputPath = Path.Combine(stagingDirectory.FullName, "primary");
        AdditionalAppHostArguments =
        [
            $"Pipeline:Verification:StagingPath={stagingDirectory.FullName}",
            $"Pipeline:Verification:TargetOutputPath={logicalPrimaryTargetPath}"
        ];
    }

    /// <inheritdoc />
    public string OutputPath { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> AdditionalAppHostArguments { get; }

    /// <summary>
    /// Resolves verification roots and creates the secure staging directory.
    /// </summary>
    public static async Task<PublishVerificationSession> CreateAsync(
        FileInfo appHostFile,
        string? fullyQualifiedOutputPath,
        IGitRepository gitRepository,
        IInteractionService interactionService,
        ILogger logger,
        string[] regenerateArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appHostFile);
        ArgumentNullException.ThrowIfNull(gitRepository);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(regenerateArguments);

        var appHostDirectory = appHostFile.Directory
            ?? throw new PublishVerificationException(PublishCommandStrings.VerifyAppHostDirectoryUnavailable);
        var repositoryRoot = await gitRepository.GetRootAsync(appHostDirectory, cancellationToken).ConfigureAwait(false)
            ?? throw new PublishVerificationException(PublishCommandStrings.VerifyGitUnavailable);
        var logicalPrimaryTargetPath = Path.GetFullPath(
            fullyQualifiedOutputPath ?? Path.Combine(appHostDirectory.FullName, "aspire-output"));

        await PublishVerificationPathSafety.ValidateDestinationAsync(
            logicalPrimaryTargetPath,
            repositoryRoot,
            gitRepository,
            cancellationToken).ConfigureAwait(false);

        var stagingDirectory = Directory.CreateTempSubdirectory("aspire-publish-verify-");
        try
        {
            return new PublishVerificationSession(
                appHostDirectory,
                repositoryRoot,
                stagingDirectory,
                logicalPrimaryTargetPath,
                gitRepository,
                interactionService,
                logger,
                regenerateArguments);
        }
        catch
        {
            stagingDirectory.Delete(recursive: true);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task PreflightAndAuthorizeAsync(
        IAppHostCliBackchannel backchannel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backchannel);

        var capabilities = await backchannel.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.Contains(PipelineOutputsCapability, StringComparer.Ordinal))
        {
            throw new AppHostIncompatibleException(
                PublishCommandStrings.VerifyHostingIncompatible,
                PipelineOutputsCapability);
        }

        var plan = await backchannel.GetPipelineOutputsAsync(cancellationToken).ConfigureAwait(false);
        var (steps, outputs) = await ValidatePlanAsync(plan, PreparedState, cancellationToken).ConfigureAwait(false);
        _steps = steps;
        _outputs = outputs;

        var includedFiles = await _gitRepository.GetIncludedFilesAsync(
            _repositoryRoot,
            outputs.Select(output => output.LogicalTargetPath).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (includedFiles is null)
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyGitQueryFailed);
        }

        await backchannel.AuthorizePipelineExecutionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CaptureFinalStateAsync(
        IAppHostCliBackchannel backchannel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backchannel);

        var finalPlan = await backchannel.GetPipelineOutputsAsync(cancellationToken).ConfigureAwait(false);
        var (steps, outputs) = await ValidatePlanAsync(finalPlan, SucceededState, cancellationToken).ConfigureAwait(false);

        if (!PlanMatchesFrozenState(steps, outputs))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyOutputPlanChanged);
        }

        foreach (var output in outputs)
        {
            ValidateOutputKind(output, validateStagedPath: true);
            PublishVerificationPathSafety.ValidateGeneratedTree(output.OutputPath);
        }

        _inventory = PublishVerificationReconciler.CreateGeneratedInventory(outputs);
        _finalStateCaptured = true;
    }

    /// <inheritdoc />
    public async Task<CommandResult> CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_finalStateCaptured || _outputs is null || _inventory is null)
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyFinalStateUnavailable);
        }

        foreach (var output in _outputs)
        {
            await PublishVerificationPathSafety.ValidateDestinationAsync(
                output.LogicalTargetPath,
                _repositoryRoot,
                _gitRepository,
                cancellationToken).ConfigureAwait(false);
        }

        var currentInventory = PublishVerificationReconciler.CreateGeneratedInventory(_outputs);
        if (!InventoryMatches(_inventory, currentInventory))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyOutputChangedDuringShutdown);
        }

        var includedFiles = await _gitRepository.GetIncludedFilesAsync(
            _repositoryRoot,
            _outputs.Select(output => output.LogicalTargetPath).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (includedFiles is null)
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyGitQueryFailed);
        }

        var absentCandidates = currentInventory
            .SelectMany(output => output.Files.Values)
            .Select(file => file.TargetPath)
            .Where(path => !File.Exists(path) && !includedFiles.Contains(path))
            .Distinct(PublishVerificationPathSafety.PathComparer)
            .ToArray();
        var ignoredFiles = await _gitRepository.GetIgnoredFilesAsync(
            _repositoryRoot,
            absentCandidates,
            cancellationToken).ConfigureAwait(false);
        if (ignoredFiles is null)
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyGitQueryFailed);
        }

        var result = await PublishVerificationReconciler.ReconcileAsync(
            _repositoryRoot.FullName,
            _outputs,
            currentInventory,
            includedFiles,
            ignoredFiles,
            cancellationToken).ConfigureAwait(false);

        if (!result.HasDrift)
        {
            _interactionService.DisplaySuccess(PublishCommandStrings.VerificationSucceeded);
            return CommandResult.Success();
        }

        PrintDrift(result);
        return CommandResult.Failure(CliExitCodes.PublishVerificationFailed);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _stagingDirectory.Refresh();
        if (!_stagingDirectory.Exists && _stagingDirectory.LinkTarget is null)
        {
            return;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                DeleteTreeWithoutFollowingLinks(_stagingDirectory);
                return;
            }
            catch (IOException ex) when (attempt < 3)
            {
                _logger.LogDebug(ex, "Publish verification staging cleanup attempt {Attempt} failed.", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 3)
            {
                _logger.LogDebug(ex, "Publish verification staging cleanup attempt {Attempt} failed.", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt)).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Unable to delete publish verification staging directory {StagingDirectory}.", _stagingDirectory.FullName);
                _interactionService.DisplayError(PublishCommandStrings.VerifyStagingCleanupFailed);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Unable to delete publish verification staging directory {StagingDirectory}.", _stagingDirectory.FullName);
                _interactionService.DisplayError(PublishCommandStrings.VerifyStagingCleanupFailed);
            }
        }
    }

    private async Task<(PipelineOutputStepInfo[] Steps, PublishVerificationOutput[] Outputs)> ValidatePlanAsync(
        GetPipelineOutputsResponse plan,
        string expectedState,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(plan.State, expectedState, StringComparison.Ordinal))
        {
            throw new PublishVerificationException(string.Format(
                CultureInfo.CurrentCulture,
                PublishCommandStrings.VerifyUnexpectedOutputState,
                plan.State));
        }

        var planAppHostDirectory = Path.GetFullPath(plan.AppHostDirectory);
        if (!PublishVerificationPathSafety.PathEquals(planAppHostDirectory, _appHostDirectory.FullName))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyAppHostDirectoryChanged);
        }

        var unsupportedSteps = plan.Steps
            .Where(step => !step.SupportsOutputPathRelocation)
            .Select(step => step.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unsupportedSteps.Length > 0)
        {
            throw new PublishVerificationException(string.Format(
                CultureInfo.CurrentCulture,
                PublishCommandStrings.VerifyUnsupportedSteps,
                string.Join(", ", unsupportedSteps)));
        }

        var outputs = plan.Outputs
            .Select(ParseOutput)
            .OrderBy(output => output.PublisherName, StringComparer.Ordinal)
            .ThenBy(output => output.Name, StringComparer.Ordinal)
            .ToArray();
        var primaryOutputs = outputs
            .Where(output => output.PublisherName == PrimaryPublisherName && output.Name == PrimaryOutputName)
            .ToArray();
        if (primaryOutputs.Length != 1 ||
            !PublishVerificationPathSafety.PathEquals(primaryOutputs[0].OutputPath, OutputPath) ||
            !PublishVerificationPathSafety.PathEquals(primaryOutputs[0].LogicalTargetPath, _logicalPrimaryTargetPath))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyPrimaryOutputMismatch);
        }

        if (outputs.Select(output => (output.PublisherName, output.Name)).Distinct().Count() != outputs.Length)
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyDuplicateOutputs);
        }

        for (var index = 0; index < outputs.Length; index++)
        {
            var output = outputs[index];
            if (!PublishVerificationPathSafety.IsWithinRoot(_stagingDirectory.FullName, output.OutputPath))
            {
                throw new PublishVerificationException(PublishCommandStrings.VerifyWritablePathOutsideStaging);
            }

            await PublishVerificationPathSafety.ValidateDestinationAsync(
                output.LogicalTargetPath,
                _repositoryRoot,
                _gitRepository,
                cancellationToken).ConfigureAwait(false);
            ValidateOutputKind(output, validateStagedPath: false);

            for (var otherIndex = index + 1; otherIndex < outputs.Length; otherIndex++)
            {
                var other = outputs[otherIndex];
                if (PublishVerificationPathSafety.PathsOverlap(output.OutputPath, other.OutputPath) ||
                    PublishVerificationPathSafety.PathsOverlap(output.LogicalTargetPath, other.LogicalTargetPath))
                {
                    throw new PublishVerificationException(PublishCommandStrings.VerifyOverlappingOutputs);
                }
            }
        }

        return (plan.Steps, outputs);
    }

    private static PublishVerificationOutput ParseOutput(PipelineOutputInfo output)
    {
        var kind = output.Kind switch
        {
            "File" => PublishVerificationOutputKind.File,
            "Directory" => PublishVerificationOutputKind.Directory,
            _ => throw new PublishVerificationException(string.Format(
                CultureInfo.CurrentCulture,
                PublishCommandStrings.VerifyUnknownOutputKind,
                output.Kind))
        };

        return new PublishVerificationOutput(
            output.PublisherName,
            output.Name,
            kind,
            Path.GetFullPath(output.OutputPath),
            Path.GetFullPath(output.LogicalTargetPath));
    }

    private bool PlanMatchesFrozenState(
        PipelineOutputStepInfo[] finalSteps,
        PublishVerificationOutput[] finalOutputs)
    {
        if (_steps is null || _outputs is null || _steps.Length != finalSteps.Length || _outputs.Length != finalOutputs.Length)
        {
            return false;
        }

        for (var index = 0; index < _steps.Length; index++)
        {
            if (_steps[index].Name != finalSteps[index].Name ||
                _steps[index].SupportsOutputPathRelocation != finalSteps[index].SupportsOutputPathRelocation)
            {
                return false;
            }
        }

        for (var index = 0; index < _outputs.Length; index++)
        {
            var expected = _outputs[index];
            var actual = finalOutputs[index];
            if (expected.PublisherName != actual.PublisherName ||
                expected.Name != actual.Name ||
                expected.Kind != actual.Kind ||
                !PublishVerificationPathSafety.PathEquals(expected.OutputPath, actual.OutputPath) ||
                !PublishVerificationPathSafety.PathEquals(expected.LogicalTargetPath, actual.LogicalTargetPath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InventoryMatches(
        IReadOnlyList<PublishVerificationInventory> expected,
        IReadOnlyList<PublishVerificationInventory> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (var outputIndex = 0; outputIndex < expected.Count; outputIndex++)
        {
            var expectedOutput = expected[outputIndex];
            var actualOutput = actual[outputIndex];
            if (expectedOutput.Output.PublisherName != actualOutput.Output.PublisherName ||
                expectedOutput.Output.Name != actualOutput.Output.Name ||
                expectedOutput.Files.Count != actualOutput.Files.Count)
            {
                return false;
            }

            foreach (var (relativePath, expectedFile) in expectedOutput.Files)
            {
                if (!actualOutput.Files.TryGetValue(relativePath, out var actualFile) ||
                    expectedFile.Length != actualFile.Length ||
                    expectedFile.LastWriteTimeUtc != actualFile.LastWriteTimeUtc)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void PrintDrift(PublishVerificationResult result)
    {
        _interactionService.DisplayError(PublishCommandStrings.VerificationFailed);

        PrintCategory(PublishCommandStrings.VerifyStaleFilesHeader, result.Groups, group => group.StaleFiles);
        PrintCategory(PublishCommandStrings.VerifyMissingFilesHeader, result.Groups, group => group.MissingFiles);
        PrintCategory(PublishCommandStrings.VerifyOrphanedFilesHeader, result.Groups, group => group.OrphanedFiles);

        var command = PublishVerificationCommandFormatter.Format(_regenerateArguments);
        _interactionService.DisplayPlainText(string.Format(
            CultureInfo.CurrentCulture,
            PublishCommandStrings.VerifyRegenerateCommand,
            command));
    }

    private void PrintCategory(
        string headerFormat,
        IReadOnlyList<PublishVerificationGroup> groups,
        Func<PublishVerificationGroup, IReadOnlyList<string>> selector)
    {
        foreach (var group in groups)
        {
            var files = selector(group);
            if (files.Count == 0)
            {
                continue;
            }

            _interactionService.DisplayPlainText(string.Format(
                CultureInfo.CurrentCulture,
                headerFormat,
                PublishVerificationDisplayFormatter.EscapePath(group.Destination)));
            foreach (var file in files)
            {
                _interactionService.DisplayPlainText($"  {PublishVerificationDisplayFormatter.EscapePath(file)}");
            }
        }
    }

    private static void ValidateOutputKind(
        PublishVerificationOutput output,
        bool validateStagedPath)
    {
        if (output.Kind == PublishVerificationOutputKind.File)
        {
            if (Directory.Exists(output.LogicalTargetPath) ||
                validateStagedPath && Directory.Exists(output.OutputPath))
            {
                throw new PublishVerificationException(PublishCommandStrings.VerifyOutputKindMismatch);
            }

            return;
        }

        if (File.Exists(output.LogicalTargetPath) ||
            validateStagedPath && File.Exists(output.OutputPath))
        {
            throw new PublishVerificationException(PublishCommandStrings.VerifyOutputKindMismatch);
        }
    }

    private static void DeleteTreeWithoutFollowingLinks(DirectoryInfo directory)
    {
        directory.Refresh();
        if (directory.LinkTarget is not null)
        {
            directory.Delete();
            return;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            entry.Refresh();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                entry.Delete();
            }
            else if (entry is DirectoryInfo childDirectory)
            {
                DeleteTreeWithoutFollowingLinks(childDirectory);
            }
            else
            {
                var file = (FileInfo)entry;
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }

                file.Delete();
            }
        }

        if ((directory.Attributes & FileAttributes.ReadOnly) != 0)
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }

        directory.Delete();
    }
}

internal enum PublishVerificationOutputKind
{
    File,
    Directory
}

internal sealed record PublishVerificationOutput(
    string PublisherName,
    string Name,
    PublishVerificationOutputKind Kind,
    string OutputPath,
    string LogicalTargetPath);
