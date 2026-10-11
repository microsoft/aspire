// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Common;

namespace AnalyzeCiFailure.Validation;

/// <summary>
/// Runs the shell helpers that the publish steps also use. The redaction rules and the PR comment
/// renderer stay in one place so validation checks exactly what is later published.
/// </summary>
internal sealed class PublicationScripts(ValidationPaths paths, TextWriter log)
{
    private string PersistenceScript => Path.Combine(paths.ScriptsDirectory, "analyze-ci-failure-persistence.sh");
    private string CommentScript => Path.Combine(paths.ScriptsDirectory, "analyze-ci-failure-comment.sh");

    /// <summary>Runs <c>analyze-ci-failure-persistence.sh &lt;subcommand&gt; &lt;input&gt; &lt;output&gt;</c>.</summary>
    public async Task<bool> TrySanitizeAsync(string subcommand, string inputFile, string outputFile, CancellationToken cancellationToken)
    {
        var result = await RunBashAsync([PersistenceScript, subcommand, inputFile, outputFile], cancellationToken);
        await log.WriteAsync(System.Text.Encoding.UTF8.GetString(result.StandardOutput).AsMemory(), cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>Sanitizes an agent-written file and replaces it, so later publish steps read the sanitized copy.</summary>
    public async Task SanitizeInPlaceAsync(string subcommand, string file, CancellationToken cancellationToken)
    {
        var sanitizedFile = file + ".tmp";
        if (!await TrySanitizeAsync(subcommand, file, sanitizedFile, cancellationToken))
        {
            File.Delete(sanitizedFile);
            throw new ValidationException($"Unable to sanitize {WorkflowCommands.Display(Path.GetFileName(file))}");
        }

        File.Move(sanitizedFile, file, overwrite: true);
    }

    /// <summary>
    /// Renders the PR comment and returns its size in bytes, or null when rendering fails. The size
    /// is measured in bytes because GitHub's comment limit is.
    /// </summary>
    public async Task<int?> TryMeasureCommentAsync(string analysisFile, string trustedFailedJobsFile, string runUrl, CancellationToken cancellationToken)
    {
        var result = await RunBashAsync([CommentScript, analysisFile, trustedFailedJobsFile, runUrl], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Length : null;
    }

    private async Task<ProcessResult> RunBashAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("bash", arguments, paths.WorkingDirectory, cancellationToken);
        await log.WriteAsync(result.StandardError.AsMemory(), cancellationToken);
        return result;
    }
}
