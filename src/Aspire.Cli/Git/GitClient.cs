// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Git;

internal sealed class GitClient(IGitCommandRunner commands) : IGitClient
{
    internal static bool IsCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    public async Task<string> ResolveReferenceAsync(string repository, string? reference, DirectoryInfo workingDirectory, GitCommandMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is not null && IsCommitSha(reference))
        {
            return reference.ToLowerInvariant();
        }

        var requestedRef = reference ?? "HEAD";
        string[] candidates = requestedRef == "HEAD" || requestedRef.StartsWith("refs/", StringComparison.Ordinal)
            ? [$"{requestedRef}^{{}}", requestedRef]
            : [$"refs/heads/{requestedRef}", $"refs/tags/{requestedRef}^{{}}", $"refs/tags/{requestedRef}"];
        var output = await commands.RunAsync(workingDirectory, ["ls-remote", "--", repository, .. candidates],
            mode, cancellationToken);

        // git ls-remote emits "<object SHA>\t<ref>", including peeled annotated tags:
        //   abc...123\trefs/tags/v1
        //   def...456\trefs/tags/v1^{}
        // Match exact names, not Git's wildcard patterns, preferring the peeled commit.
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('\t');
            if (separator > 0 && IsCommitSha(line[..separator]))
            {
                refs[line[(separator + 1)..]] = line[..separator].ToLowerInvariant();
            }
        }
        foreach (var candidate in candidates)
        {
            if (refs.TryGetValue(candidate, out var sha))
            {
                return sha;
            }
        }

        throw new GitReferenceException(requestedRef);
    }

    public Task InitializeAsync(DirectoryInfo workingDirectory, GitRepositoryLocation repository, string? objectFormat, GitCommandMode mode, CancellationToken cancellationToken)
    {
        List<string> arguments = ["init"];
        if (repository.IsBare)
        {
            arguments.Add("--bare");
        }
        else
        {
            arguments.Add("--quiet");
        }
        if (objectFormat is not null)
        {
            arguments.Add($"--object-format={objectFormat}");
        }
        arguments.AddRange(["--", repository.Directory.FullName]);

        return commands.RunAsync(workingDirectory, [.. arguments], mode, cancellationToken);
    }

    public Task FetchAsync(GitRepositoryLocation destination, string repository, string reference, GitCommandMode mode, CancellationToken cancellationToken) =>
        RunAsync(destination, ["fetch", "--depth", "1", "--no-tags", "--", repository, reference], mode, cancellationToken);

    public Task CheckoutAsync(GitRepositoryLocation repository, GitCommandMode mode, CancellationToken cancellationToken) =>
        RunAsync(repository, ["checkout", "--detach", "--force", "FETCH_HEAD"], mode, cancellationToken);

    public async Task<string> GetCommitAsync(GitRepositoryLocation repository, string reference, GitCommandMode mode, CancellationToken cancellationToken) =>
        (await RunAsync(repository, repository.IsBare ? ["rev-parse", reference] : ["rev-parse", "--verify", reference], mode, cancellationToken)).Trim();

    public async Task<GitTreeEntry?> GetFileAsync(GitRepositoryLocation repository, string commit, string path, GitCommandMode mode, CancellationToken cancellationToken)
    {
        var output = await RunAsync(repository, ["ls-tree", "-z", commit, "--", path], mode, cancellationToken);
        // git ls-tree -z emits "100644 blob <object-id>\tpath\0". Require a single
        // entry; the caller decides which modes and object types its content supports.
        var tab = output.IndexOf('\t');
        var header = tab < 0 ? [] : output[..tab].Split(' ');
        return header.Length == 3 && output.IndexOf('\0') == output.Length - 1
            ? new GitTreeEntry(header[0], header[1], header[2])
            : null;
    }

    public async Task<long?> GetObjectSizeAsync(GitRepositoryLocation repository, string objectId, GitCommandMode mode, CancellationToken cancellationToken)
    {
        var output = await RunAsync(repository, ["cat-file", "-s", objectId], mode, cancellationToken);
        return long.TryParse(output.Trim(), CultureInfo.InvariantCulture, out var size) ? size : null;
    }

    public Task<string> ReadBlobAsync(GitRepositoryLocation repository, string objectId, GitCommandMode mode, CancellationToken cancellationToken) =>
        RunAsync(repository, ["cat-file", "blob", objectId], mode, cancellationToken);

    private Task<string> RunAsync(GitRepositoryLocation repository, string[] arguments, GitCommandMode mode, CancellationToken cancellationToken)
    {
        // Bare repositories must be named explicitly when safe.bareRepository=explicit.
        // https://git-scm.com/docs/git-config#Documentation/git-config.txt-safebareRepository
        string[] gitArguments = repository.IsBare ? [$"--git-dir={repository.Directory.FullName}", .. arguments] : arguments;
        return commands.RunAsync(repository.Directory, gitArguments, mode, cancellationToken);
    }
}
