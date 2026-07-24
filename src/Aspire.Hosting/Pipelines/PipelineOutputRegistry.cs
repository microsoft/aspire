// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREPIPELINES001

using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Pipelines;

internal sealed class PipelineOutputRegistry
{
    internal const string PrimaryPublisherName = "aspire";
    internal const string PrimaryOutputName = "primary";
    internal const string StagingPathConfigurationKey = "Pipeline:Verification:StagingPath";
    internal const string TargetOutputPathConfigurationKey = "Pipeline:Verification:TargetOutputPath";
    internal const int MaximumStagedOutputNameByteCount = 200;
    private const int MaximumStagedOutputExtensionByteCount = 32;

    private readonly object _lock = new();
    private readonly IConfiguration _configuration;
    private readonly string? _stagingPath;
    private readonly Dictionary<OutputKey, ResolvedPipelineOutput> _outputs = [];
    private readonly ResolvedPipelineOutput _primaryOutput;
    private readonly TaskCompletionSource _preparationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _executionAuthorized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<PipelineStep> _selectedSteps = [];
    private Exception? _preparationException;
    private PipelineOutputExecutionState _executionState = PipelineOutputExecutionState.Preparing;
    private bool _prepared;

    public PipelineOutputRegistry(
        IConfiguration configuration,
        IPipelineOutputService outputService,
        IOptions<PipelineOptions> pipelineOptions)
    {
        _configuration = configuration;

        var configuredAppHostDirectory = configuration["AppHost:Directory"];
        if (string.IsNullOrWhiteSpace(configuredAppHostDirectory))
        {
            throw new InvalidOperationException("The AppHost directory is not available.");
        }

        AppHostDirectory = Path.GetFullPath(configuredAppHostDirectory);

        var configuredStagingPath = configuration[StagingPathConfigurationKey];
        var configuredPrimaryTargetPath = configuration[TargetOutputPathConfigurationKey];
        var hasStagingPath = !string.IsNullOrWhiteSpace(configuredStagingPath);
        var hasPrimaryTargetPath = !string.IsNullOrWhiteSpace(configuredPrimaryTargetPath);
        if (hasStagingPath != hasPrimaryTargetPath)
        {
            throw new InvalidOperationException(
                $"Configuration values '{StagingPathConfigurationKey}' and '{TargetOutputPathConfigurationKey}' must be specified together.");
        }

        _stagingPath = NormalizeOptionalPath(configuredStagingPath, AppHostDirectory);

        var primaryOutputPath = Path.GetFullPath(outputService.GetOutputDirectory());
        var primaryTargetPath = NormalizeOptionalPath(configuredPrimaryTargetPath, AppHostDirectory)
            ?? primaryOutputPath;
        // Direct manifest publishing historically accepts a .json file path. Every other
        // publisher treats the primary output as a directory, regardless of its suffix.
        var primaryKind =
            string.Equals(pipelineOptions.Value.Step, WellKnownPipelineSteps.PublishManifest, StringComparison.Ordinal) &&
            Path.GetExtension(primaryTargetPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? PipelineOutputKind.File
                : PipelineOutputKind.Directory;
        ValidateTargetKind(PrimaryPublisherName, PrimaryOutputName, primaryTargetPath, primaryKind);

        _primaryOutput = new ResolvedPipelineOutput(
            PrimaryPublisherName,
            PrimaryOutputName,
            primaryKind,
            primaryOutputPath,
            primaryTargetPath,
            isPrimary: true);
    }

    public string AppHostDirectory { get; }

    public bool RequiresExecutionAuthorization => _stagingPath is not null;

    public void Prepare(IEnumerable<PipelineStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        lock (_lock)
        {
            if (_preparationCompleted.Task.IsCompleted)
            {
                throw new InvalidOperationException("The pipeline output plan has already been prepared.");
            }

            try
            {
                var selectedSteps = steps.ToArray();
                foreach (var step in selectedSteps.OrderBy(step => step.Name, StringComparer.Ordinal))
                {
                    foreach (var definition in step.Outputs.OrderBy(output => output.Name, StringComparer.Ordinal))
                    {
                        Register(step.Name, definition);
                    }
                }

                ValidateExclusiveOwnership();
                _selectedSteps = selectedSteps;
                _prepared = true;
                _executionState = PipelineOutputExecutionState.Prepared;
            }
            catch (Exception ex)
            {
                _preparationException = ex;
                _executionState = PipelineOutputExecutionState.Failed;
                throw;
            }
            finally
            {
                _preparationCompleted.TrySetResult();
            }
        }
    }

    public async Task WaitForPreparationAsync(CancellationToken cancellationToken)
    {
        await _preparationCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        Exception? preparationException;
        lock (_lock)
        {
            preparationException = _preparationException;
        }

        if (preparationException is not null)
        {
            ExceptionDispatchInfo.Capture(preparationException).Throw();
        }
    }

    public async Task WaitForExecutionAuthorizationAsync(CancellationToken cancellationToken)
    {
        await WaitForPreparationAsync(cancellationToken).ConfigureAwait(false);

        if (RequiresExecutionAuthorization)
        {
            await _executionAuthorized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void AuthorizeExecution()
    {
        lock (_lock)
        {
            EnsurePrepared();

            if (!RequiresExecutionAuthorization)
            {
                throw new InvalidOperationException("Pipeline execution authorization is only available for relocated output plans.");
            }

            var unsupportedSteps = _selectedSteps
                .Where(step => !SupportsOutputPathRelocationCore(step))
                .Select(step => step.Name)
                .ToArray();
            if (unsupportedSteps.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Pipeline execution cannot be authorized because these selected steps do not support output-path relocation: " +
                    $"{string.Join(", ", unsupportedSteps.Select(name => $"'{name}'"))}.");
            }

            _executionAuthorized.TrySetResult();
        }
    }

    public void MarkExecutionSucceeded()
    {
        lock (_lock)
        {
            EnsurePrepared();
            _executionState = PipelineOutputExecutionState.Succeeded;
        }
    }

    public void MarkExecutionFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_lock)
        {
            _executionState = PipelineOutputExecutionState.Failed;
            if (!_preparationCompleted.Task.IsCompleted)
            {
                _preparationException = exception;
                _preparationCompleted.TrySetResult();
            }
        }
    }

    public PipelineOutputExecutionState GetExecutionState()
    {
        lock (_lock)
        {
            return _executionState;
        }
    }

    public ResolvedPipelineOutput GetPrimaryOutput()
    {
        lock (_lock)
        {
            EnsurePrepared();
            return _primaryOutput;
        }
    }

    public ResolvedPipelineOutput Resolve(PipelineStep step, PipelineOutputDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(definition);

        if (!step.Outputs.Contains(definition))
        {
            throw new InvalidOperationException(
                $"Pipeline output '{definition.Name}' is not declared by step '{step.Name}'.");
        }

        lock (_lock)
        {
            EnsurePrepared();

            var key = new OutputKey(step.Name, definition.Name);
            if (!_outputs.TryGetValue(key, out var output))
            {
                throw new InvalidOperationException(
                    $"Pipeline output '{definition.Name}' for step '{step.Name}' was not prepared before execution.");
            }

            return output;
        }
    }

    public IReadOnlyList<ResolvedPipelineOutput> GetOutputs()
    {
        lock (_lock)
        {
            EnsurePrepared();

            return _outputs.Values
                .Append(_primaryOutput)
                .OrderBy(output => output.PublisherName, StringComparer.Ordinal)
                .ThenBy(output => output.Name, StringComparer.Ordinal)
                .ThenByDescending(output => output.IsPrimary)
                .ToArray();
        }
    }

    public IReadOnlyList<PipelineStep> GetSelectedSteps()
    {
        lock (_lock)
        {
            EnsurePrepared();
            return _selectedSteps;
        }
    }

    public bool SupportsOutputPathRelocation(PipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        lock (_lock)
        {
            EnsurePrepared();
            return SupportsOutputPathRelocationCore(step);
        }
    }

    private void Register(string publisherName, PipelineOutputDefinition definition)
    {
        ValidateIdentifier(publisherName, nameof(publisherName));
        ArgumentNullException.ThrowIfNull(definition);
        ValidateIdentifier(definition.Name, nameof(definition.Name));

        if (string.IsNullOrWhiteSpace(definition.DefaultPath))
        {
            throw new ArgumentException("The pipeline output default path cannot be empty.", nameof(definition));
        }

        var key = new OutputKey(publisherName, definition.Name);
        var configuredPath = _configuration[$"Pipeline:Outputs:{publisherName}:{definition.Name}:Path"];
        var targetPath = ResolveTargetPath(configuredPath ?? definition.DefaultPath);
        ValidateTargetKind(publisherName, definition.Name, targetPath, definition.Kind);
        var outputPath = _stagingPath is null
            ? targetPath
            : GetStagedOutputPath(publisherName, definition, targetPath);
        var resolved = new ResolvedPipelineOutput(
            publisherName,
            definition.Name,
            definition.Kind,
            outputPath,
            targetPath,
            isPrimary: false);

        if (_outputs.TryGetValue(key, out var existing))
        {
            if (existing.Kind == resolved.Kind &&
                PathEquals(existing.OutputPath, resolved.OutputPath) &&
                PathEquals(existing.LogicalTargetPath, resolved.LogicalTargetPath))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Pipeline output '{publisherName}/{definition.Name}' was declared more than once with conflicting metadata.");
        }

        _outputs.Add(key, resolved);
    }

    private string ResolveTargetPath(string path)
    {
        return Path.GetFullPath(path, AppHostDirectory);
    }

    private string GetStagedOutputPath(
        string publisherName,
        PipelineOutputDefinition definition,
        string targetPath)
    {
        var identity = $"{publisherName}\0{definition.Name}\0{targetPath}";
        var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var extension = definition.Kind == PipelineOutputKind.File
            ? TruncateToUtf8ByteCount(
                SanitizePathSegment(Path.GetExtension(targetPath)),
                MaximumStagedOutputExtensionByteCount)
            : string.Empty;
        var fixedByteCount = hash.Length + Encoding.UTF8.GetByteCount(extension) + 2;
        var readableByteCount = MaximumStagedOutputNameByteCount - fixedByteCount;
        var publisherByteCount = readableByteCount / 2;
        var outputByteCount = readableByteCount - publisherByteCount;
        var publisherSegment = TruncateToUtf8ByteCount(SanitizePathSegment(publisherName), publisherByteCount);
        var outputSegment = TruncateToUtf8ByteCount(SanitizePathSegment(definition.Name), outputByteCount);
        var leafName = $"{publisherSegment}-{outputSegment}-{hash}{extension}";

        return Path.Combine(_stagingPath!, "outputs", leafName);
    }

    private bool SupportsOutputPathRelocationCore(PipelineStep step)
    {
        return step.OutputPathRelocationSupportEvaluator?.Invoke(_primaryOutput)
            ?? step.SupportsOutputPathRelocation;
    }

    private void ValidateExclusiveOwnership()
    {
        var outputs = _outputs.Values.Append(_primaryOutput).ToArray();
        for (var i = 0; i < outputs.Length; i++)
        {
            for (var j = i + 1; j < outputs.Length; j++)
            {
                var first = outputs[i];
                var second = outputs[j];

                if (_stagingPath is null && TryGetPrimaryAndNamedOutput(first, second, out var primary, out var named) &&
                    primary.Kind == PipelineOutputKind.Directory &&
                    !PathEquals(primary.LogicalTargetPath, named.LogicalTargetPath) &&
                    IsNestedPath(named.LogicalTargetPath, primary.LogicalTargetPath))
                {
                    continue;
                }

                if (PathsOverlap(first.LogicalTargetPath, second.LogicalTargetPath))
                {
                    throw new InvalidOperationException(
                        $"Pipeline outputs '{first.PublisherName}/{first.Name}' and " +
                        $"'{second.PublisherName}/{second.Name}' have overlapping target paths.");
                }

                if (_stagingPath is not null && PathsOverlap(first.OutputPath, second.OutputPath))
                {
                    throw new InvalidOperationException(
                        $"Pipeline outputs '{first.PublisherName}/{first.Name}' and " +
                        $"'{second.PublisherName}/{second.Name}' have overlapping writable paths.");
                }
            }
        }
    }

    private static bool TryGetPrimaryAndNamedOutput(
        ResolvedPipelineOutput first,
        ResolvedPipelineOutput second,
        out ResolvedPipelineOutput primary,
        out ResolvedPipelineOutput named)
    {
        if (first.IsPrimary)
        {
            primary = first;
            named = second;
            return true;
        }

        if (second.IsPrimary)
        {
            primary = second;
            named = first;
            return true;
        }

        primary = first;
        named = second;
        return false;
    }

    private static bool PathsOverlap(string first, string second)
    {
        return PathEquals(first, second) ||
            IsNestedPath(first, second) ||
            IsNestedPath(second, first);
    }

    private static bool IsNestedPath(string path, string possibleParent)
    {
        var parentWithSeparator = Path.EndsInDirectorySeparator(possibleParent)
            ? possibleParent
            : possibleParent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSeparator, PathComparison);
    }

    private static bool PathEquals(string first, string second)
    {
        return string.Equals(first, second, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string? NormalizeOptionalPath(string? path, string basePath)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, basePath);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(':'))
        {
            throw new ArgumentException(
                "Pipeline output identifiers cannot be empty or contain ':'.",
                parameterName);
        }
    }

    private static void ValidateTargetKind(
        string publisherName,
        string outputName,
        string targetPath,
        PipelineOutputKind kind)
    {
        if (kind == PipelineOutputKind.File && Directory.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"Pipeline output '{publisherName}/{outputName}' declares a file, but its target path is an existing directory.");
        }

        if (kind == PipelineOutputKind.Directory && File.Exists(targetPath))
        {
            throw new InvalidOperationException(
                $"Pipeline output '{publisherName}/{outputName}' declares a directory, but its target path is an existing file.");
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = value
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray();
        return new string(characters);
    }

    private static string TruncateToUtf8ByteCount(string value, int maximumByteCount)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumByteCount)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maximumByteCount)
            {
                break;
            }

            builder.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private void EnsurePrepared()
    {
        if (_preparationException is not null)
        {
            ExceptionDispatchInfo.Capture(_preparationException).Throw();
        }

        if (!_prepared)
        {
            throw new InvalidOperationException("The pipeline output plan has not been prepared.");
        }
    }

    private readonly record struct OutputKey(string PublisherName, string Name);
}

internal sealed class PipelineStepOutputResolver : IPipelineOutputResolver
{
    private readonly Lazy<PipelineOutputRegistry> _registry;
    private readonly PipelineStep _step;

    public PipelineStepOutputResolver(PipelineOutputRegistry registry, PipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(step);

        _registry = new(() => registry);
        _step = step;
    }

    public PipelineStepOutputResolver(IServiceProvider services, PipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(step);

        _registry = new(services.GetRequiredService<PipelineOutputRegistry>);
        _step = step;
    }

    public string AppHostDirectory => _registry.Value.AppHostDirectory;

    public ResolvedPipelineOutput PrimaryOutput => _registry.Value.GetPrimaryOutput();

    public ResolvedPipelineOutput Resolve(PipelineOutputDefinition definition)
    {
        return _registry.Value.Resolve(_step, definition);
    }
}

internal sealed class UnavailablePipelineOutputResolver : IPipelineOutputResolver
{
    private const string ErrorMessage = "Pipeline outputs are only available while a pipeline step is executing.";

    public static UnavailablePipelineOutputResolver Instance { get; } = new();

    private UnavailablePipelineOutputResolver()
    {
    }

    public string AppHostDirectory => throw new InvalidOperationException(ErrorMessage);

    public ResolvedPipelineOutput PrimaryOutput => throw new InvalidOperationException(ErrorMessage);

    public ResolvedPipelineOutput Resolve(PipelineOutputDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        throw new InvalidOperationException(ErrorMessage);
    }
}

internal enum PipelineOutputExecutionState
{
    Preparing,
    Prepared,
    Succeeded,
    Failed
}
