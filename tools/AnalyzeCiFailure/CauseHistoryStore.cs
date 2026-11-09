// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace AnalyzeCiFailure;

/// <summary>Owns one cause-history checkout, its layout and ordered persistence receipts.</summary>
internal sealed class CauseHistoryStore(
    AnalyzerCommandExecutor command,
    string repository,
    string directory,
    AnalysisDiagnostics diagnostics)
{
    internal const string BranchName = "memory/ci-failure-analysis";

    private async Task<bool> OpenAsync(bool create)
    {
        var git = new GitRepository(command);
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            throw new ValidationException("History checkout destination already exists. Use a new directory to read current history.");
        }
        var remote = await git.GetOriginUrlAsync();
        var url = remote.ExitCode == 0 ? remote.Output.Trim() : $"https://github.com/{repository}.git";
        var reference = await git.FindRemoteBranchAsync(url, BranchName);
        // ls-remote exit 2 means a successful request with no matching reference.
        // Authentication, transport, and malformed responses must not erase history.
        if (!(reference.ExitCode is 0 or 2))
        {
            throw new ValidationException($"Unable to load CI failure history: {CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(reference.Error, limit: 4096))}");
        }
        if (reference.ExitCode == 0)
        {
            if (!reference.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => Regex.IsMatch(line, @"^[0-9a-fA-F]{40,64}\trefs/heads/" + Regex.Escape(BranchName) + @"\z")))
            {
                throw new ValidationException("Unable to load CI failure history: invalid memory branch reference");
            }
            await git.CloneBranchAsync(url, BranchName, directory);
        }
        else if (create)
        {
            await git.InitializeAsync(directory);
            await git.CreateOrphanBranchAsync(directory, BranchName);
            await git.AddOriginAsync(directory, url);
        }
        else
        {
            return false;
        }
        if (create)
        {
            await ConfigurePublicationAsync(git, directory);
        }

        return true;
    }

    private static async Task ConfigurePublicationAsync(GitRepository git, string directory)
    {
        await git.ConfigureCommitIdentityAsync(directory, "github-actions[bot]", "github-actions[bot]@users.noreply.github.com");
        await git.ConfigureGitHubCredentialsAsync(directory);
    }
    internal Task<bool> OpenForReadAsync() => OpenAsync(create: false);

    internal async Task OpenForPublicationAsync() => await OpenAsync(create: true);

    internal Task<string> GetHeadCommitAsync() => new GitRepository(command).GetHeadCommitAsync(directory);

    internal string CausePath(string fileName) => Path.Combine(directory, "causes", fileName);

    internal string RunPath(string runId) => Path.Combine(directory, "runs", runId + ".json");

    internal IEnumerable<string> EnumerateCauseFiles()
        => Directory.Exists(Path.Combine(directory, "causes"))
            ? Directory.EnumerateFiles(Path.Combine(directory, "causes"), "*.json") : [];

    internal void CreateRecordDirectories()
    {
        Directory.CreateDirectory(Path.Combine(directory, "runs"));
        Directory.CreateDirectory(Path.Combine(directory, "causes"));
    }


    internal async Task CommitAndPushAsync(string runId, IEnumerable<string> paths)
    {
        var files = paths.Select(p => Path.GetRelativePath(directory, p)).ToArray();
        if (files.Length == 0)
        {
            return;
        }
        var git = new GitRepository(command);
        await git.StageFilesAsync(directory, files);
        var changed = await git.GetStagedFileNamesAsync(directory);
        if (changed.Length == 0)
        {
            diagnostics.Receipt("history-push", "skipped", new { reason = "no-changes" });
            return;
        }
        await git.CommitAsync(directory, $"Update CI failure analysis for run {runId}");
        var commit = await GetHeadCommitAsync();
        try
        {
            await git.PushBranchAsync(directory, BranchName);
            diagnostics.Receipt("history-push", "confirmed", new { commit, branch = BranchName });
        }
        catch (Exception error)
        {
            diagnostics.Receipt("history-push", "unknown", new { commit, reason = error.Message });
            throw;
        }
    }

}
