// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.CommandLine;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

//
// QuarantineTools – high-level overview
// -------------------------------------
// This small command-line tool helps developers quarantine or unquarantine failing/flaky xUnit tests
// across the repository's tests folder by adding or removing the [QuarantinedTest] attribute on
// test methods. It edits source files directly using Roslyn (Microsoft.CodeAnalysis) to ensure safe and
// structured modifications.
//
// Primary flow (Program.Main):
// 1. Parse command-line arguments: the mode is either quarantine (-q) or unquarantine (-u).
//    - quarantine: provide one or more fully-qualified test names immediately after -q, and pass the
//      issue URL using -i/--url. Example: `quarantine -q N.C.M -i https://...`
//    - unquarantine: provide one or more fully-qualified test names after -u. Example: `quarantine -u N.C.M`
// 2. Locate the repo root (asks `git rev-parse --show-toplevel`) and then the tests directory under it.
// 3. Enumerate all .cs files under tests, ignoring bin/ and obj/.
// 4. For each file, parse a syntax tree and find method declarations. For each method, compute its
//    containing namespace and type chain and compare against requested targets (namespace + nested type
//    chain + method name) to determine matches.
// 5. Depending on the action:
//      - quarantine: add [QuarantinedTest("<issue-url>")] to matched methods (if not present) and ensure
//        a using Aspire.TestUtilities; exists at the file level.
//      - unquarantine: remove the [QuarantinedTest] attribute from matched methods and remove the using
//        Aspire.TestUtilities; if no method in the file uses it anymore.
// 6. If any file contents change, write them back to disk and print a summary of updated files.
//
// The tool is conservative: if a requested test is already in the desired state, it makes no change.
// It also avoids touching files that don't contain the specified methods.

public class Program
{
    private const string DefaultQuarantinedTestAttributeFullName = "Aspire.TestUtilities.QuarantinedTest";
    private const string DefaultActiveIssueAttributeFullName = "Xunit.ActiveIssueAttribute";

    /// <summary>
    /// Upper bound for the `git rev-parse` probe.
    /// </summary>
    private static readonly TimeSpan s_gitProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Exit code for refusing to edit a tree the caller is not standing in. Distinct from the other
    /// failure codes (2 = tests folder not found, 3 = no matching test) so a caller can tell them apart.
    /// </summary>
    internal const int ExitCodeWrongTree = 4;

    /// <summary>
    /// Matches the conventional MAXSYMLINKS limit; bounds link resolution so a symlink cycle terminates.
    /// </summary>
    private const int MaxLinkDepth = 40;

    public static Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Quarantine or unquarantine xUnit tests by adding/removing a QuarantinedTest or ActiveIssue attribute.");

        var optQuarantine = new Option<bool>("--quarantine", "-q") { Description = "Quarantine the specified test(s)." };
        var optUnquarantine = new Option<bool>("--unquarantine", "-u") { Description = "Unquarantine the specified test(s)." };
        var optUrl = new Option<string?>("--url", "-i") { Description = "Issue URL required for quarantining (http/https)." };
        var optRoot = new Option<string?>("--root", "-r") { Description = "Tests root to scan (defaults to '<repo>/tests')." };
        var optAttribute = new Option<string?>("--attribute", "-a") { Description = "Fully-qualified attribute type to add/remove. If not specified, defaults based on --mode." };
        var optMode = new Option<string>("--mode", "-m") { Description = "Mode: 'quarantine' for QuarantinedTest or 'activeissue' for ActiveIssue (default: quarantine)." };
        optMode.DefaultValueFactory = _ => "quarantine";

        var argTests = new Argument<string[]>("tests") { Arity = ArgumentArity.ZeroOrMore, Description = "Fully-qualified test method name(s) like Namespace.Type.Method" };

        rootCommand.Options.Add(optQuarantine);
        rootCommand.Options.Add(optUnquarantine);
        rootCommand.Options.Add(optUrl);
        rootCommand.Options.Add(optRoot);
        rootCommand.Options.Add(optAttribute);
        rootCommand.Options.Add(optMode);
        rootCommand.Arguments.Add(argTests);

        rootCommand.SetAction(static (parseResult, token) =>
        {
            var quarantine = parseResult.GetValue<bool>("--quarantine");
            var unquarantine = parseResult.GetValue<bool>("--unquarantine");

            if (quarantine == unquarantine)
            {
                Console.Error.WriteLine("Specify exactly one of -q/--quarantine or -u/--unquarantine.");
                return Task.FromResult(1);
            }

            var tests = parseResult.GetValue<string[]?>("tests") ?? [];

            if (tests.Length == 0)
            {
                Console.Error.WriteLine("Specify at least one fully-qualified test method name.");
                return Task.FromResult(1);
            }

            var issueUrl = parseResult.GetValue<string?>("--url");
            var scanRoot = parseResult.GetValue<string?>("--root");
            var mode = parseResult.GetValue<string>("--mode") ?? "quarantine";
            var attributeFullName = parseResult.GetValue<string?>("--attribute");

            // Validate mode
            if (mode != "quarantine" && mode != "activeissue")
            {
                Console.Error.WriteLine("Mode must be 'quarantine' or 'activeissue'.");
                return Task.FromResult(1);
            }

            // If attribute not explicitly provided, use default based on mode
            if (string.IsNullOrWhiteSpace(attributeFullName))
            {
                attributeFullName = mode == "activeissue"
                    ? DefaultActiveIssueAttributeFullName
                    : DefaultQuarantinedTestAttributeFullName;
            }

            if (quarantine)
            {
                if (string.IsNullOrWhiteSpace(issueUrl))
                {
                    Console.Error.WriteLine("Quarantining requires an issue URL (--url or -i).");
                    return Task.FromResult(1);
                }
                if (!IsHttpUrl(issueUrl!))
                {
                    Console.Error.WriteLine("Quarantining requires a valid http(s) URL, e.g. https://github.com/org/repo/issues/1234.");
                    return Task.FromResult(1);
                }
            }

            return ExecuteAsync(
                    quarantine,
                    unquarantine,
                    tests.ToList(),
                    string.IsNullOrWhiteSpace(issueUrl) ? null : issueUrl,
                    scanRoot,
                    attributeFullName,
                    token);
        });

        return rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> ExecuteAsync(bool quarantine, bool unquarantine, List<string> fullMethodNames, string? issueUrl, string? scanRootOverride, string attributeFullName, CancellationToken cancellationToken)
    {
        // Resolve repository root and tests folder
        var currentDirectory = Directory.GetCurrentDirectory();
        var repoRoot = await FindRepoRootAsync(currentDirectory, cancellationToken).ConfigureAwait(false) ?? currentDirectory;

        // This tool rewrites source files in bulk, so a wrong root is destructive rather than merely
        // wrong. Refuse instead of editing a tree the caller is not standing in.
        if (TryGetWrongTreeError(repoRoot, currentDirectory) is { } wrongTreeError)
        {
            Console.Error.WriteLine(wrongTreeError);
            return ExitCodeWrongTree;
        }

        var testsRoot = string.IsNullOrWhiteSpace(scanRootOverride)
                            ? Path.Combine(repoRoot, "tests")
                            : (Path.IsPathRooted(scanRootOverride!)
                                ? scanRootOverride!
                                : Path.GetFullPath(Path.Combine(repoRoot, scanRootOverride!)));

        if (!Directory.Exists(testsRoot))
        {
            Console.Error.WriteLine($"Tests folder not found at: {testsRoot}");
            return 2;
        }

        // Pre-parse targets for efficiency and group by method name for fast filtering
        var targets = fullMethodNames.Select(ParseFullMethodName).ToList();
        var targetsByMethod = targets
            .GroupBy(t => t.Method, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.PathPartsBeforeMethod).ToList(), StringComparer.Ordinal);

        // Build a single regex to prefilter files by method name (avoids N x Contains)
        var methodNamePrefilterRegex = BuildAnyMethodNameRegex(targetsByMethod.Keys);

        // Gather candidate source files under tests, ignoring build outputs and common heavy folders
        var csFiles = EnumerateCsFiles(testsRoot).ToList();

        var foundAnyCount = 0; // incremented if any target method found
        var modifiedFiles = new ConcurrentBag<string>();

        // Prep attribute handling based on configuration
        PrepareAttributeHandling(attributeFullName, out var attributeNameToInsert, out var attributeNamespaceToEnsure, out var isTargetAttribute);
        // Build a regex to quickly detect the attribute textually in files
        var attributePrefilterRegex = BuildAttributeRegex(attributeNamespaceToEnsure, attributeNameToInsert);

        // Parallel parse each file asynchronously, identify target methods, then add/remove attributes
        await Parallel.ForEachAsync(
            csFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount - 1, 1), CancellationToken = cancellationToken },
            async (file, ct) =>
            {
                try
                {
                    // Cheap textual prefilter: if unquarantining, require attribute hint; otherwise require any target method name present
                    // This avoids Roslyn parsing for most files.
                    string text;
                    Encoding encoding;
                    try
                    {
                        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
                        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                        encoding = reader.CurrentEncoding;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[warn] Failed to read file: {file}. {ex.Message}");
                        return; // skip unreadable files
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (unquarantine)
                    {
                        // If attribute isn't present textually (regex), skip.
                        if (!attributePrefilterRegex.IsMatch(text))
                        {
                            return;
                        }
                    }
                    else if (quarantine) // quarantine
                    {
                        // If none of the target method names appear (regex), skip.
                        if (!methodNamePrefilterRegex.IsMatch(text))
                        {
                            return;
                        }
                    }

                    var newline = DetectNewLine(text);
                    var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct);
                    var root = tree.GetCompilationUnitRoot(ct);

                    var methodNodes = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

                    var updates = new List<(MethodDeclarationSyntax original, MethodDeclarationSyntax updated)>();

                    foreach (var method in methodNodes)
                    {
                        // Fast filter by method name first
                        var name = method.Identifier.ValueText;
                        if (!targetsByMethod.TryGetValue(name, out var candidatePaths))
                        {
                            continue;
                        }

                        // Compute the enclosing namespace and nested type chain for the method
                        var (ns, typeChain) = GetEnclosingNames(method);
                        var actualParts = new List<string>(typeChain.Count + (string.IsNullOrEmpty(ns) ? 0 : ns.Count(c => c == '.') + 1));
                        if (!string.IsNullOrEmpty(ns))
                        {
                            actualParts.AddRange(ns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        }
                        actualParts.AddRange(typeChain);

                        // Match any of the requested targets by enclosing paths
                        var matchesAny = candidatePaths.Any(cp => SequenceEquals(actualParts, cp));
                        if (!matchesAny)
                        {
                            continue;
                        }

                        Interlocked.Increment(ref foundAnyCount);

                        if (unquarantine)
                        {
                            var updated = RemoveTargetAttribute(method, isTargetAttribute, out var removed);
                            if (removed)
                            {
                                updates.Add((method, updated));
                            }
                        }
                        else if (quarantine) // quarantine
                        {
                            var updated = AddTargetAttribute(method, attributeNameToInsert, issueUrl ?? string.Empty, newline);
                            if (!ReferenceEquals(updated, method))
                            {
                                updates.Add((method, updated));
                            }
                        }
                    }

                    if (updates.Count == 0)
                    {
                        return;
                    }

                    // Replace nodes using a dictionary to avoid O(n^2) lookups
                    var map = updates.ToDictionary(t => t.original, t => t.updated);
                    var newRoot = root.ReplaceNodes(map.Keys, (orig, _) => map[orig]);

                    // Manage using directives for configured attribute namespace when using short name
                    if (quarantine)
                    {
                        if (!string.IsNullOrEmpty(attributeNamespaceToEnsure) && !attributeNameToInsert.Contains('.'))
                        {
                            newRoot = EnsureUsingDirective(newRoot, attributeNamespaceToEnsure!, newline);
                        }
                    }
                    else if (unquarantine)
                    {
                        if (!string.IsNullOrEmpty(attributeNamespaceToEnsure))
                        {
                            var anyLeft = newRoot.DescendantNodes().OfType<AttributeSyntax>().Any(isTargetAttribute);
                            if (!anyLeft)
                            {
                                newRoot = RemoveUsingDirective(newRoot, attributeNamespaceToEnsure!);
                            }
                        }
                    }

                    var newText = newRoot.ToFullString();
                    if (newText != text)
                    {
                        try
                        {
                            using var outStream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
                            using var writer = new StreamWriter(outStream, encoding);
                            await writer.WriteAsync(newText.AsMemory(), ct).ConfigureAwait(false);
                            await writer.FlushAsync(ct).ConfigureAwait(false);
                            modifiedFiles.Add(file);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[warn] Failed to write file: {file}. {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[warn] Error processing {file}: {ex.Message}");
                }
            }).ConfigureAwait(false);

        if (foundAnyCount == 0)
        {
            Console.Error.WriteLine($"No method found matching any of: {string.Join(", ", fullMethodNames)}");
            return 3;
        }

        if (modifiedFiles.IsEmpty)
        {
            Console.WriteLine(quarantine
                ? "The test is already quarantined or no change was necessary."
                : "The test was already unquarantined or no change was necessary.");
            return 0;
        }

        var modified = modifiedFiles.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Console.WriteLine($"Updated {modified.Count} file(s):");
        foreach (var f in modified)
        {
            Console.WriteLine($" - {Path.GetRelativePath(repoRoot, f)}");
        }

        return 0;
    }

    /// <summary>
    /// Enumerate .cs files under root while proactively skipping common heavy/irrelevant directories.
    /// Avoids the overhead of scanning into folders like bin, obj, .git, artifacts, node_modules, etc.
    /// </summary>
    private static IEnumerable<string> EnumerateCsFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        // Directories to skip by exact segment match
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".github", ".vscode", ".vs", "artifacts", "packages", "node_modules", "out", "dist", ".idea"
        };

        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sd in subdirs)
            {
                var name = Path.GetFileName(sd);
                if (skip.Contains(name))
                {
                    continue;
                }
                dirs.Push(sd);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }
        }
    }

    /// <summary>
    /// Builds a compiled regex that matches any appearance of the configured attribute name in text, including:
    /// - short name (e.g., QuarantinedTest)
    /// - with Attribute suffix (e.g., QuarantinedTestAttribute)
    /// - fully-qualified with namespace if provided (e.g., Aspire.TestUtilities.QuarantinedTest[Attribute])
    /// Word boundaries are enforced to avoid partial matches.
    /// </summary>
    private static Regex BuildAttributeRegex(string? attributeNamespace, string attributeShortName)
    {
        var variants = new List<string>
        {
            Regex.Escape(attributeShortName),
            Regex.Escape(attributeShortName + "Attribute")
        };

        if (!string.IsNullOrEmpty(attributeNamespace))
        {
            variants.Add(Regex.Escape(attributeNamespace + "." + attributeShortName));
            variants.Add(Regex.Escape(attributeNamespace + "." + attributeShortName + "Attribute"));
        }

        // Use alternation with word boundaries
        var pattern = $"\\b(?:{string.Join("|", variants.Distinct(StringComparer.Ordinal))})\\b";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    }

    /// <summary>
    /// Builds a compiled regex that matches any of the provided method names as whole words.
    /// Intended for fast prefiltering of files before Roslyn parsing.
    /// </summary>
    private static Regex BuildAnyMethodNameRegex(IEnumerable<string> methodNames)
    {
        var alts = methodNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(Regex.Escape)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var pattern = alts.Length == 0 ? "(?!)" : $"\\b(?:{string.Join("|", alts)})\\b";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    }

    /// <summary>
    /// Detects and returns the newline convention used by the file content: CRLF ("\r\n") if present,
    /// otherwise LF ("\n"). This ensures we preserve existing line endings when editing files.
    /// </summary>
    private static string DetectNewLine(string text)
    {
        // Respect existing file line endings. If any CRLF is present, use CRLF; otherwise use LF.
        // This avoids introducing Windows newlines on Unix files.
        if (text.Contains("\r\n"))
        {
            return "\r\n";
        }
        // Default to LF if no newlines or only LF are present
        return "\n";
    }

    /// <summary>
    /// Minimal validation that a string is an absolute http or https URL.
    /// </summary>
    private static bool IsHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// Resolves the root of the git working tree that contains <paramref name="startDir"/>.
    /// Returns null if no repository root can be determined.
    /// </summary>
    internal static async Task<string?> FindRepoRootAsync(string startDir, CancellationToken cancellationToken)
    {
        // Ask git first. Git owns the definition of "working tree root" and gets linked worktrees,
        // submodules, and symlinked paths right, none of which a directory probe can do reliably.
        if (await TryGetGitTopLevelAsync(startDir, cancellationToken).ConfigureAwait(false) is { } topLevel)
        {
            return topLevel;
        }

        // Fallback for when git is unavailable or `startDir` is not inside a repository.
        return FindRepoRootByMarker(startDir);
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a <c>.git</c> marker. Used when git cannot
    /// answer. Returns null if no marker is found.
    /// </summary>
    internal static string? FindRepoRootByMarker(string startDir)
    {
        // IMPORTANT: `.git` must be matched as a file *or* a directory. In a linked worktree `.git` is a
        // regular file holding a `gitdir: <path>` pointer, so a directory-only probe walks straight past
        // the worktree root. When a worktree is nested inside another checkout of the same repository,
        // that walk terminates on the outer checkout's real `.git` directory and this tool then rewrites
        // source files in the wrong tree - silently, because the edit itself succeeds.
        // See https://git-scm.com/docs/gitrepository-layout#Documentation/gitrepository-layout.txt-gitfile
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Runs <c>git rev-parse --show-toplevel</c> in <paramref name="startDir"/>. Returns null when git is
    /// not on PATH, the directory is not inside a working tree, or the command otherwise fails.
    /// </summary>
    private static async Task<string?> TryGetGitTopLevelAsync(string startDir, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = startDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("--show-toplevel");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Resolving the repo root must never be the reason the tool appears to hang, so bound the
            // probe and fall back to the directory walk if git does not answer.
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(s_gitProbeTimeout);

            // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardErrorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);

                // The linked source cancels for two unrelated reasons, and only one of them is a probe
                // failure. If the caller cancelled (Ctrl+C), swallowing it here would send
                // FindRepoRootAsync into the fallback walk and let ExecuteAsync enumerate the whole
                // tests tree before cancellation is next observed. Re-throw that; fall back only for
                // the internal timeout.
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            // git prints a single absolute path using forward slashes on every platform, including
            // Windows (for example `C:/src/aspire`). GetFullPath normalizes the separators.
            var trimmed = standardOutputTask.Result.Trim();
            return trimmed.Length == 0 ? null : Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // git missing from PATH or not executable - fall back to the directory walk.
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process already exited or cannot be killed; nothing useful to do either way.
        }
    }

    /// <summary>
    /// Returns the error text to print when <paramref name="repoRoot"/> is not a tree the caller is
    /// standing in, or null when the root is safe to use.
    /// </summary>
    internal static string? TryGetWrongTreeError(string repoRoot, string currentDirectory)
    {
        if (IsSameOrAncestorDirectory(repoRoot, currentDirectory))
        {
            return null;
        }

        // Only now, on the failure path, pay for symlink resolution. The two paths can legitimately be
        // spelled differently - a Windows junction or `subst` drive lets the caller's directory read as
        // `D:\src\aspire` while git reports `C:/src/aspire` - and refusing a valid run would be its own
        // bug. Confining canonicalization to this path means a gap in it can only rescue a run that was
        // already being refused, never block one that was about to succeed.
        if (IsSameOrAncestorDirectory(Canonicalize(repoRoot), Canonicalize(currentDirectory)))
        {
            return null;
        }

        // Name both paths: the whole point of this guard is that a wrong-tree run is otherwise
        // indistinguishable from a correct one, so the message has to say which tree was rejected.
        return $"""
            Refusing to run: the resolved repository root is not the working directory or one of its ancestors.
              Resolved repository root: {repoRoot}
              Current directory:        {currentDirectory}
            """;
    }

    /// <summary>
    /// Resolves every symlinked component of <paramref name="path"/>. Returns the input unchanged if it
    /// cannot be resolved; this is a best-effort comparison aid, never a correctness requirement.
    /// </summary>
    private static string Canonicalize(string path)
    {
        try
        {
            return CanonicalizeCore(Path.GetFullPath(path), MaxLinkDepth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    private static string CanonicalizeCore(string path, int remainingDepth)
    {
        if (remainingDepth <= 0)
        {
            // A cycle, or a chain long enough to be indistinguishable from one.
            return path;
        }

        var info = new DirectoryInfo(path);

        // A link's stored target is whatever string was written at creation time, so substituting it can
        // reintroduce an unresolved spelling (and may itself be relative to the link's own directory).
        // Re-canonicalize the substituted path rather than trusting it, one hop at a time.
        if (ResolveLink(info) is { } target)
        {
            var linkDirectory = info.Parent?.FullName ?? path;
            return CanonicalizeCore(Path.GetFullPath(target, linkDirectory), remainingDepth - 1);
        }

        return info.Parent is { } parent
            ? Path.Combine(CanonicalizeCore(parent.FullName, remainingDepth - 1), info.Name)
            : Path.TrimEndingDirectorySeparator(info.FullName);
    }

    /// <summary>
    /// Returns the link target of <paramref name="info"/>, or null when it is not a link.
    /// </summary>
    private static string? ResolveLink(DirectoryInfo info)
    {
        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
        }
        catch (IOException)
        {
            // On Windows the entry can be a kind this overload rejects; treat it as "not a link" and let
            // the caller keep the path as spelled.
            return null;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="candidateAncestor"/> is <paramref name="directory"/> itself or
    /// one of its ancestors.
    /// </summary>
    internal static bool IsSameOrAncestorDirectory(string candidateAncestor, string directory)
        => IsSameOrAncestorDirectory(candidateAncestor, directory, IsCaseSensitiveDirectory);

    /// <summary>
    /// Returns true when <paramref name="candidateAncestor"/> is <paramref name="directory"/> itself or
    /// one of its ancestors, comparing names with the supplied casing rule.
    /// </summary>
    /// <param name="candidateAncestor">The directory being tested as an ancestor.</param>
    /// <param name="directory">The directory whose ancestry is walked.</param>
    /// <param name="caseSensitive">Whether directory names on the volume holding the paths distinguish case.</param>
    internal static bool IsSameOrAncestorDirectory(string candidateAncestor, string directory, bool caseSensitive)
        => IsSameOrAncestorDirectory(candidateAncestor, directory, _ => caseSensitive);

    /// <summary>
    /// Returns true when <paramref name="candidateAncestor"/> is <paramref name="directory"/> itself or
    /// one of its ancestors, comparing each segment with the casing rules of that segment's parent.
    /// </summary>
    internal static bool IsSameOrAncestorDirectory(string candidateAncestor, string directory, Func<string, bool> isCaseSensitiveDirectory)
    {
        var ancestor = TrimTrailingSeparator(Path.GetFullPath(candidateAncestor));
        var current = TrimTrailingSeparator(Path.GetFullPath(directory));
        var ancestorRoot = Path.GetPathRoot(ancestor) ?? string.Empty;
        var currentRoot = Path.GetPathRoot(current) ?? string.Empty;

        if (!string.Equals(ancestorRoot, currentRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }

        var ancestorSegments = GetPathSegments(ancestor, ancestorRoot);
        var currentSegments = GetPathSegments(current, currentRoot);
        if (ancestorSegments.Length > currentSegments.Length)
        {
            return false;
        }

        var parent = currentRoot;
        for (var i = 0; i < ancestorSegments.Length; i++)
        {
            // Comparing case-insensitively under a case-sensitive parent would let two genuinely
            // different trees whose paths differ only by case satisfy this guard, which is the one
            // outcome it exists to prevent. Comparing case-sensitively everywhere is not the answer
            // either: git canonicalizes --show-toplevel to the on-disk casing, and while getcwd(3)
            // does the same on Unix, the Windows current directory keeps whatever casing the process
            // was given. Follow each parent instead of applying the repository root's final-segment
            // probe to the whole absolute path.
            var comparison = isCaseSensitiveDirectory(parent) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!string.Equals(ancestorSegments[i], currentSegments[i], comparison))
            {
                return false;
            }

            parent = Path.Combine(parent, currentSegments[i]);
        }

        return true;
    }

    private static string[] GetPathSegments(string fullPath, string root)
    {
        var remainder = fullPath[root.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Probes whether directory names alongside <paramref name="directory"/> distinguish case, by
    /// asking whether it is reachable through a case-flipped spelling of its own final segment.
    /// </summary>
    /// <remarks>
    /// The operating system is not a reliable proxy for the volume: macOS APFS can be formatted
    /// case-sensitive, Windows exposes a per-directory case-sensitivity flag that WSL sets, and Linux
    /// can mount case-insensitive volumes. The probe is read-only - it only tests for existence.
    /// </remarks>
    internal static bool IsCaseSensitiveDirectory(string directory)
    {
        var full = TrimTrailingSeparator(Path.GetFullPath(directory));
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        var flipped = FlipCase(name);

        // A volume root has no segment to flip, and a segment with no cased letters cannot answer the
        // question. Neither is a reason to refuse, so fall back to the platform's usual default.
        if (parent is null || name.Length == 0 || string.Equals(flipped, name, StringComparison.Ordinal))
        {
            return !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        }

        // A case-sensitive volume that happens to hold a real sibling differing only by case is read as
        // case-insensitive here. That is acceptable: it only restores the behavior this guard had
        // before the probe existed, and such a pair is not something a checkout layout produces.
        return !Directory.Exists(Path.Combine(parent, flipped));
    }

    private static string FlipCase(string value)
    {
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                destination[i] = char.IsUpper(source[i]) ? char.ToLowerInvariant(source[i]) : char.ToUpperInvariant(source[i]);
            }
        });
    }

    private static string TrimTrailingSeparator(string path)
    {
        // A root such as "/" or "C:\" must keep its separator, so never trim the path down to empty.
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }

    /// <summary>
    /// Parses a fully-qualified method name like "A.B.Type+Nested.Method" into its enclosing path
    /// parts (namespace and nested type names) and the method name.
    /// </summary>
    private static (List<string> PathPartsBeforeMethod, string Method) ParseFullMethodName(string input)
    {
        var parts = input.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"Invalid method name '{input}'. Expected 'Namespace.Type.Method'.");
        }

        var method = parts[^1];
        var beforeMethod = parts.Take(parts.Length - 1)
            .SelectMany(p => p.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        return (beforeMethod, method);
    }

    /// <summary>
    /// From a syntax node inside a method, determines the file-scoped or block-scoped namespace name
    /// and the chain of enclosing types (including nested classes/structs/records/interfaces).
    /// </summary>
    private static (string Namespace, List<string> TypeChain) GetEnclosingNames(SyntaxNode node)
    {
        var typeNames = new List<string>();
        var ns = string.Empty;

        for (var current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case ClassDeclarationSyntax cd:
                    typeNames.Insert(0, cd.Identifier.ValueText);
                    break;
                case StructDeclarationSyntax sd:
                    typeNames.Insert(0, sd.Identifier.ValueText);
                    break;
                case RecordDeclarationSyntax rd:
                    typeNames.Insert(0, rd.Identifier.ValueText);
                    break;
                case InterfaceDeclarationSyntax id:
                    typeNames.Insert(0, id.Identifier.ValueText);
                    break;
                case NamespaceDeclarationSyntax nd:
                    ns = nd.Name.ToString();
                    current = null; // break out
                    break;
                case FileScopedNamespaceDeclarationSyntax fsn:
                    ns = fsn.Name.ToString();
                    current = null; // break out
                    break;
            }
            if (current == null)
            {
                break;
            }
        }

        return (ns, typeNames);
    }

    /// <summary>
    /// Ordinal equality comparison for two string lists.
    /// </summary>
    private static bool SequenceEquals(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    // Creates a predicate that matches attributes against the configured attribute full name.
    private static void PrepareAttributeHandling(string attributeFullNameInput, out string attributeNameToInsert, out string? namespaceToEnsure, out Func<AttributeSyntax, bool> matcher)
    {
        // Normalize input: allow with or without namespace and with or without "Attribute" suffix
        attributeFullNameInput = attributeFullNameInput.Trim();
        var ns = string.Empty;
        var typeName = attributeFullNameInput;
        var lastDot = attributeFullNameInput.LastIndexOf('.');
        if (lastDot >= 0)
        {
            ns = attributeFullNameInput.Substring(0, lastDot);
            typeName = attributeFullNameInput.Substring(lastDot + 1);
        }

        // Determine preferred short name (without Attribute suffix if present)
        var shortName = typeName;
        var shortNameNoSuffix = shortName.EndsWith("Attribute", StringComparison.Ordinal) ? shortName.Substring(0, shortName.Length - "Attribute".Length) : shortName;

        // We'll insert short name (without namespace), relying on a using directive if ns provided
        attributeNameToInsert = shortNameNoSuffix;
        namespaceToEnsure = string.IsNullOrEmpty(ns) ? null : ns;

        // Build a matcher that accepts qualified and unqualified, with or without Attribute suffix, matching configured ns/type
        matcher = attr =>
        {
            var full = attr.Name.ToString(); // may be qualified or not
                                             // Extract right-most identifier for suffix/no-suffix comparison
            var rightMost = attr.Name switch
            {
                IdentifierNameSyntax ins => ins.Identifier.ValueText,
                QualifiedNameSyntax qns => (qns.Right as IdentifierNameSyntax)?.Identifier.ValueText ?? qns.Right.ToString(),
                AliasQualifiedNameSyntax aqn => (aqn.Name as IdentifierNameSyntax)?.Identifier.ValueText ?? aqn.Name.ToString(),
                _ => full.Split('.').Last(),
            };
            var rightMatches = string.Equals(rightMost, shortNameNoSuffix, StringComparison.Ordinal)
                || string.Equals(rightMost, shortNameNoSuffix + "Attribute", StringComparison.Ordinal);

            if (!rightMatches)
            {
                return false;
            }

            if (string.IsNullOrEmpty(ns))
            {
                // No namespace constraint supplied; accept any ns as long as right-most matches
                return true;
            }

            // If a namespace is supplied, ensure qualification (if any) ends with the same right-most
            // and the left part (if present) matches provided namespace exactly
            if (attr.Name is QualifiedNameSyntax qn)
            {
                var leftNs = qn.Left.ToString();
                return string.Equals(leftNs, ns, StringComparison.Ordinal);
            }
            // Unqualified attribute in code but ns required; allow match because we will also add the using for that ns
            return true;
        };
    }

    /// <summary>
    /// Removes the [QuarantinedTest] attribute from a method, if present. Returns the (potentially)
    /// modified method and flags via <paramref name="removed"/> whether a change occurred.
    /// </summary>
    private static MethodDeclarationSyntax RemoveTargetAttribute(MethodDeclarationSyntax method, Func<AttributeSyntax, bool> isTargetAttribute, out bool removed)
    {
        removed = false;
        if (method.AttributeLists.Count == 0)
        {
            return method;
        }

        var newLists = new List<AttributeListSyntax>();
        foreach (var list in method.AttributeLists)
        {
            var remaining = list.Attributes.Where(a => !isTargetAttribute(a)).ToList();
            if (remaining.Count == list.Attributes.Count)
            {
                newLists.Add(list);
                continue;
            }

            removed = true;
            if (remaining.Count > 0)
            {
                var newList = list.WithAttributes(SyntaxFactory.SeparatedList(remaining));
                newLists.Add(newList);
            }
        }

        return removed ? method.WithAttributeLists(SyntaxFactory.List(newLists)) : method;
    }

    /// <summary>
    /// Adds the configured attribute (optionally with an issue URL) to a method if one does
    /// not already exist. Preserves indentation and ensures a clean newline layout.
    /// </summary>
    private static MethodDeclarationSyntax AddTargetAttribute(MethodDeclarationSyntax method, string attributeNameToInsert, string issueUrl, string newline)
    {
        foreach (var list in method.AttributeLists)
        {
            // If any attribute with the same right-most identifier (with/without suffix) exists, skip adding
            if (list.Attributes.Any(a =>
            {
                var id = a.Name switch
                {
                    IdentifierNameSyntax ins => ins.Identifier.ValueText,
                    QualifiedNameSyntax qns => (qns.Right as IdentifierNameSyntax)?.Identifier.ValueText ?? qns.Right.ToString(),
                    AliasQualifiedNameSyntax aqn => (aqn.Name as IdentifierNameSyntax)?.Identifier.ValueText ?? aqn.Name.ToString(),
                    _ => a.Name.ToString().Split('.').Last()
                };
                return string.Equals(id, attributeNameToInsert, StringComparison.Ordinal)
                    || string.Equals(id, attributeNameToInsert + "Attribute", StringComparison.Ordinal);
            }))
            {
                return method;
            }
        }

        // Use provided attribute name as-is (can be short or qualified)
        var attrName = SyntaxFactory.ParseName(attributeNameToInsert);
        var attrArgs = string.IsNullOrWhiteSpace(issueUrl)
            ? null
            : SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(issueUrl)))));

        var attr = SyntaxFactory.Attribute(attrName, attrArgs);
        var newList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attr));

        if (method.AttributeLists.Count > 0)
        {
            // Append after existing attributes.
            // Ensure there is exactly one newline between the previous attribute and the new one.
            // If the previous attribute does not end with a newline (e.g., attributes were on the same line
            // as the method signature), add a leading newline to the new attribute so it starts on the next line.
            var last = method.AttributeLists[method.AttributeLists.Count - 1];
            var indentation = SyntaxFactory.TriviaList(last.GetLeadingTrivia().Where(t => !t.IsKind(SyntaxKind.EndOfLineTrivia)));
            var lastEndsWithNewline = last.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            var leading = lastEndsWithNewline
                ? indentation
                : indentation.Add(SyntaxFactory.EndOfLine(newline));
            newList = newList
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline));
        }
        else
        {
            var leading = method.GetLeadingTrivia();
            newList = newList.WithLeadingTrivia(leading)
                             .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline));
        }

        var newLists = method.AttributeLists.Add(newList);
        var updated = method.WithAttributeLists(newLists);
        return updated;
    }

    // Removed legacy Options/ParseArgs in favor of System.CommandLine
    /// <summary>
    /// Ensures a <c>using &lt;namespaceName&gt;;</c> directive is present at the compilation unit level.
    /// Respects existing file trivia and newline style.
    /// </summary>
    private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax root, string namespaceName, string newline)
    {
        // If a matching using already exists, do nothing
        if (root.Usings.Any(u => u.Name != null && u.Name.ToString() == namespaceName))
        {
            return root;
        }
        // Create a using directive with a trailing newline, but avoid inserting an extra
        // leading blank line when appending to an existing list of usings.
        var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName))
            .WithUsingKeyword(
                SyntaxFactory.Token(SyntaxKind.UsingKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space))
            .WithSemicolonToken(
                SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                    .WithTrailingTrivia(SyntaxFactory.EndOfLine(newline)));

        // Only add a leading newline if there are no existing usings and the file
        // already has content that would otherwise run into the using. In typical
        // cases (either first using at top of file, or appending after other usings),
        // no leading trivia is needed.
        if (root.Usings.Count == 0)
        {
            // If the file has any leading trivia (e.g., license header) that ends
            // without a newline, ensure there's a newline before the first using.
            var leadingTrivia = root.GetLeadingTrivia();
            var endsWithNewline = leadingTrivia.Count > 0 &&
                leadingTrivia.Last().IsKind(SyntaxKind.EndOfLineTrivia);

            if (!endsWithNewline)
            {
                usingDirective = usingDirective.WithLeadingTrivia(SyntaxFactory.EndOfLine(newline));
            }
        }

        return root.WithUsings(root.Usings.Add(usingDirective));
    }

    /// <summary>
    /// Removes all occurrences of <c>using &lt;namespaceName&gt;;</c> from the file, if present. This is
    /// called after unquarantining when no QuarantinedTest attributes remain in the file.
    /// </summary>
    private static CompilationUnitSyntax RemoveUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        // Remove matching using directives wherever they appear in the tree
        var nodesToRemove = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.Name != null && u.Name.ToString() == namespaceName)
            .ToList();
        CompilationUnitSyntax updated;
        if (nodesToRemove.Count > 0)
        {
            updated = (CompilationUnitSyntax)root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
        }
        else
        {
            updated = root;
        }

        // Also ensure the compilation unit usings are filtered (in case any remain)
        if (updated.Usings.Count > 0)
        {
            var filtered = updated.Usings.Where(u => u.Name == null || u.Name.ToString() != namespaceName).ToList();
            updated = updated.WithUsings(SyntaxFactory.List(filtered));
        }

        // Fallback: if textual occurrence remains, strip it textually and reparse
        var text = updated.ToFullString();
        if (text.Contains($"using {namespaceName};"))
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                $@"^\s*using\s+{System.Text.RegularExpressions.Regex.Escape(namespaceName)}\s*;\s*\r?\n",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline);
            updated = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        }

        return updated;
    }
}
