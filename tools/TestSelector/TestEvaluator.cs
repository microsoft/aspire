// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using TestSelector.Models;

namespace TestSelector;

internal static class TestEvaluator
{
    // Evaluates changed files and determines which tests to run.
    //
    // Evaluation Flow:
    // ================
    //
    //                    ┌─────────────────┐
    //                    │  Changed Files  │
    //                    └────────┬────────┘
    //                             │
    //                    ┌────────▼────────┐
    //                    │ Filter Ignored  │
    //                    │     Files       │
    //                    └────────┬────────┘
    //                             │
    //              ┌──────────────┴──────────────┐
    //              │ all ignored                 │
    //              ▼                             │
    //      ┌────────────────┐                    │
    //      │  Skip Tests    │                    │
    //      └────────────────┘                    │
    //                                            │
    //               ┌────────────────────────────┘
    //               │
    //               ▼
    //   ┌─────────────────────────────┐
    //   │  Check runEverything Paths  │
    //   └─────────────┬───────────────┘
    //                 │
    //       ┌─────────┴─────────┐
    //       │ matched           │
    //       ▼                   │
    //  ┌────────────────┐       │
    //  │  Run ALL Tests │       │
    //  └────────────────┘       │
    //                           │
    //              ┌────────────┘
    //              │
    //              ▼
    //   ┌──────────────────────────────┐
    //   │  With Config? (jobCategories,│
    //   │  mappings, edges, ...)       │
    //   └───────────────┬──────────────┘
    //                   │
    //          ┌────────┴─────────┐
    //          │ no config        │ has config
    //          ▼                  ▼
    //   ┌──────────────┐  ┌──────────────────┐
    //   │ dotnet-      │  │ Match jobCats    │
    //   │ affected     │  │ + Mappings       │
    //   │ only         │  │ + Edges          │
    //   └──────┬───────┘  └────────┬─────────┘
    //          │                   │
    //          │          ┌────────▼─────────┐
    //          │          │ Check Unmatched  │
    //          │          │ Files            │
    //          │          └────────┬─────────┘
    //          │                   │
    //          │          ┌────────┴────────┐
    //          │          │ unmatched       │
    //          │          ▼                 │
    //          │  ┌──────────────┐          │
    //          │  │ Run ALL      │          │
    //          │  │ Tests        │          │
    //          │  └──────────────┘          │
    //          │                            │
    //          │          ┌─────────────────┘
    //          │          │
    //          │          ▼
    //          │  ┌────────────────────┐
    //          │  │  dotnet-affected   │
    //          │  └────────┬───────────┘
    //          │           │
    //          │      ┌────┴────────┐
    //          │      │ failed      │
    //          │      ▼             │
    //          │  ┌──────────────┐  │
    //          │  │ Run ALL      │  │
    //          │  │ Tests        │  │
    //          │  └──────────────┘  │
    //          │                    │
    //          │           ┌────────┘
    //          │           │
    //          │           ▼
    //          │  ┌──────────────────┐
    //          │  │ Filter + Combine │
    //          │  │ + Union Edges    │
    //          └──►──────┬───────────┘
    //                    │
    //           ┌────────▼─────────┐
    //           │ Build Result     │
    //           │ (selective run)  │
    //           └──────────────────┘
    public static async Task<TestSelectionResult> EvaluateAsync(
        TestSelectorConfig? config,
        List<string> changedFiles,
        string solution,
        string? fromRef,
        string? toRef,
        string workingDir,
        string ciEnvironment,
        bool verbose,
        IReadOnlyCollection<string>? nonApplyingPaths = null)
    {
        var logger = new DiagnosticLogger(verbose);

        // Step 1: Handle no changes
        if (changedFiles.Count == 0)
        {
            return HandleNoChanges(logger, config);
        }

        logger.LogInfo($"Processing {changedFiles.Count} changed files");

        // Step 2: Filter ignored files
        var (activeFiles, ignoredFiles) = FilterIgnoredFiles(logger, config, changedFiles);

        // Step 2b: Rescue ignored files that match a jobCategory.when or an edge.from.
        // This prevents triggers (e.g. polyglot, whose when-paths are under .github/workflows/**)
        // that overlap with the blanket ignore globs from being silently dropped.
        if (config is not null && ignoredFiles.Count > 0)
        {
            var rescued = RescueTriggerFiles(logger, config, ignoredFiles);
            if (rescued.Count > 0)
            {
                activeFiles.AddRange(rescued);
                ignoredFiles = ignoredFiles.Except(rescued, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        if (activeFiles.Count == 0)
        {
            return HandleAllFilesIgnored(logger, config, ignoredFiles);
        }

        var nonApplyingResult = CheckNonApplyingPaths(logger, config, activeFiles, ignoredFiles, nonApplyingPaths);
        if (nonApplyingResult is not null)
        {
            return nonApplyingResult;
        }

        // Step 3: Check for runEverything paths
        var runEverythingResult = CheckRunEverythingPaths(logger, config, activeFiles, ignoredFiles);
        if (runEverythingResult is not null)
        {
            return runEverythingResult;
        }

        // Step 4: If no config, use dotnet-affected only (skip relationship-graph logic)
        if (config is null)
        {
            return await EvaluateWithDotnetAffectedOnlyAsync(
                logger, activeFiles, ignoredFiles, solution, fromRef, toRef, workingDir, ciEnvironment, verbose).ConfigureAwait(false);
        }

        // Build the static projection of which test projects bear which edge category label.
        // run_<category> is later derived purely by intersecting this with the selected set, so
        // the boolean and the affected_test_projects matrix can never disagree (the D7 contract).
        var categoryToProjects = BuildCategoryToProjects(config);

        // Step 5: Match files to job categories via their when-globs
        var (jobCategoryMatched, jobCategoryMatchedFiles) = MatchFilesToJobCategories(logger, config, activeFiles);

        // Step 6: Resolve mappings (conventional source→test couplings)
        var (mappedProjects, mappingMatchedFiles) = ApplyMappings(logger, config, activeFiles);

        // Step 7: Resolve explicit edges (non-conventional couplings, e.g. runtime edges)
        var (edgeProjects, edgeMatchedFiles) = ResolveEdges(logger, config, activeFiles);

        // Step 8: Check for unmatched files (conservative fallback). A file is "explained" if any
        // jobCategory, mapping, or edge claimed it; anything left over forces a full run.
        var unmatchedResult = CheckUnmatchedFiles(
            logger, config, activeFiles, ignoredFiles, jobCategoryMatchedFiles, mappingMatchedFiles, edgeMatchedFiles);
        if (unmatchedResult is not null)
        {
            return unmatchedResult;
        }

        if (CanUseMappingsOnly(logger, activeFiles, mappingMatchedFiles, mappedProjects))
        {
            logger.LogStep("Skip dotnet-affected");
            logger.LogDecision("Use mappings only", "All active files were resolved by mappings");

            // Edges are still unioned in: a file can match both a mapping and an edge, and the edge
            // opts its test project in regardless of inferDeps (an edge is an explicit declaration).
            var mappedOnlyTestProjects = FilterAndCombineTestProjects(logger, config, [], mappedProjects);
            mappedOnlyTestProjects = UnionEdgeProjects(logger, mappedOnlyTestProjects, edgeProjects);
            var mappedOnlyNugetInfo = DetectNuGetDependentTests(logger, config, [], activeFiles, workingDir);

            var mappedOnlyCategories = DeriveCategories(jobCategoryMatched, categoryToProjects, mappedOnlyTestProjects);

            var mappedOnlyResult = BuildSelectiveResult(
                logger,
                activeFiles,
                ignoredFiles,
                [],
                mappedOnlyTestProjects,
                mappedOnlyCategories,
                reason: "selective_mappings_only");

            mappedOnlyResult.NuGetDependentTests = mappedOnlyNugetInfo;
            return mappedOnlyResult;
        }

        // Step 9: Run dotnet-affected
        var affectedResult = await RunDotnetAffectedAsync(
            logger, config, activeFiles, ignoredFiles, solution, fromRef, toRef, workingDir, ciEnvironment, verbose).ConfigureAwait(false);
        if (affectedResult.FallbackResult is not null)
        {
            return affectedResult.FallbackResult;
        }

        // Step 10: Filter and combine test projects (dotnet-affected ∪ mappings, then inferDeps).
        var allTestProjects = FilterAndCombineTestProjects(
            logger, config, affectedResult.AffectedProjects, mappedProjects);

        // Step 10b: Conservative guard for the "matched-but-zero" gap (see CheckMatchedButZeroProjects).
        // Runs before edges are unioned so the guard observes a genuinely empty integrations
        // resolution rather than being masked by an unrelated edge (e.g. cli_e2e) firing.
        var matchedButZeroResult = CheckMatchedButZeroProjects(
            logger, config, jobCategoryMatched, affectedResult.AffectedProjects, allTestProjects, activeFiles, ignoredFiles);
        if (matchedButZeroResult is not null)
        {
            return matchedButZeroResult;
        }

        // Step 10c: Union explicit edges. These express couplings dotnet-affected cannot follow —
        // most importantly runtime edges where there is no ProjectReference (e.g. CLI end-to-end
        // tests consume a built CLI archive). Added after the matched-but-zero guard and after
        // inferDeps filtering because an edge is an explicit opt-in.
        allTestProjects = UnionEdgeProjects(logger, allTestProjects, edgeProjects);

        // Step 11: Detect NuGet-dependent tests
        var nugetInfo = DetectNuGetDependentTests(
            logger, config, affectedResult.AffectedProjects, activeFiles, workingDir);

        // Step 12: Derive category booleans (edge labels by projection; job categories by trigger).
        var categories = DeriveCategories(jobCategoryMatched, categoryToProjects, allTestProjects);

        // Step 13: Build final result
        var result = BuildSelectiveResult(
            logger, activeFiles, ignoredFiles, affectedResult.AffectedProjects, allTestProjects, categories);
        result.NuGetDependentTests = nugetInfo;
        return result;
    }

    /// <summary>
    /// Static projection of which test projects bear which edge <see cref="SelectionEdge.Category"/>
    /// label. The keys are the distinct edge categories (e.g. <c>cli_e2e</c>); each value is the set
    /// of <see cref="SelectionEdge.To"/> paths labeled with that category. Category edges use literal
    /// <c>to</c> paths (no <c>{name}</c>), so this is the exact set of test projects the label points at.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildCategoryToProjects(TestSelectorConfig config)
    {
        return config.Edges
            .Where(e => !string.IsNullOrEmpty(e.Category))
            .GroupBy(e => e.Category!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.To.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Checks ignored files against each jobCategory's full when/exclude rules and each edge's
    /// from/exclude rules, and rescues any that <em>would actually fire</em>, so triggers like
    /// polyglot (whose when-paths are under .github/workflows/**) are not silently dropped by the
    /// blanket ignore filter.
    /// </summary>
    /// <remarks>
    /// Honors per-jobCategory <see cref="JobCategory.Exclude"/> and per-edge
    /// <see cref="SelectionEdge.Exclude"/> by routing through the same matchers used for active
    /// files. Without this, an ignored file that textually matches some trigger glob but is
    /// excluded from it would still be rescued and then fall through to fallback/RunAll — which is
    /// strictly worse than leaving it ignored.
    /// </remarks>
    private static List<string> RescueTriggerFiles(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> ignoredFiles)
    {
        var rescued = new List<string>();

        if (config.JobCategories.Count == 0 && config.Edges.Count == 0)
        {
            return rescued;
        }

        var jobCategoryMatcher = new CategoryMapper(config.JobCategories);
        var edgeResolver = new ProjectMappingResolver(EdgesAsMappings(config));

        foreach (var file in ignoredFiles)
        {
            var jobCategoryResult = jobCategoryMatcher.GetCategoriesWithDetails([file]);
            var matchesJobCategory = jobCategoryResult.CategoryStatus.Values.Any(triggered => triggered);

            if (matchesJobCategory || edgeResolver.Matches(file))
            {
                rescued.Add(file);
            }
        }

        if (rescued.Count > 0)
        {
            logger.LogInfo($"Rescued {rescued.Count} ignored file(s) that match a jobCategory.when or edge.from:");
            foreach (var file in rescued)
            {
                logger.LogInfo($"    • {file}");
            }
        }

        return rescued;
    }

    private static TestSelectionResult HandleNoChanges(DiagnosticLogger logger, TestSelectorConfig? config)
    {
        logger.LogInfo("No changed files detected");
        var result = TestSelectionResult.NoChanges();
        InitializeCategories(result, config);
        logger.LogSummary(false, "no_changes", 0, []);
        return result;
    }

    private static (List<string> ActiveFiles, List<string> IgnoredFiles) FilterIgnoredFiles(
        DiagnosticLogger logger,
        TestSelectorConfig? config,
        List<string> changedFiles)
    {
        logger.LogStep("Filter Ignored Files");

        var ignorePatterns = config?.Ignore ?? [];
        var ignoreFilter = new IgnorePathFilter(ignorePatterns);
        var ignoreResult = ignoreFilter.SplitFilesWithDetails(changedFiles);

        logger.LogInfo($"Ignore patterns configured: {ignoreFilter.Patterns.Count}");
        if (ignoreResult.IgnoredFiles.Count > 0)
        {
            logger.LogSubSection("Ignored files");
            foreach (var ignored in ignoreResult.IgnoredFiles)
            {
                logger.LogMatch(ignored.FilePath, ignored.MatchedPattern);
            }
        }
        logger.LogInfo($"Result: {ignoreResult.IgnoredFiles.Count} ignored, {ignoreResult.ActiveFiles.Count} active");

        var ignoredFiles = ignoreResult.IgnoredFiles.Select(f => f.FilePath).ToList();
        return (ignoreResult.ActiveFiles, ignoredFiles);
    }

    private static TestSelectionResult HandleAllFilesIgnored(
        DiagnosticLogger logger,
        TestSelectorConfig? config,
        List<string> ignoredFiles)
    {
        logger.LogDecision("Skip all tests", "All changed files are ignored");
        var result = TestSelectionResult.AllIgnored(ignoredFiles);
        InitializeCategories(result, config);
        logger.LogSummary(false, "all_ignored", 0, []);
        return result;
    }

    private static TestSelectionResult? CheckNonApplyingPaths(
        DiagnosticLogger logger,
        TestSelectorConfig? config,
        List<string> activeFiles,
        List<string> ignoredFiles,
        IReadOnlyCollection<string>? nonApplyingPaths)
    {
        if (nonApplyingPaths is null || nonApplyingPaths.Count == 0)
        {
            return null;
        }

        var normalizedNonApplyingPaths = new HashSet<string>(
            nonApplyingPaths.Select(path => path.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        if (activeFiles.All(file => normalizedNonApplyingPaths.Contains(file.Replace('\\', '/'))))
        {
            logger.LogDecision("Record audit-only change", "All active files are marked as non-applying");

            var result = TestSelectionResult.NonApplying("audit_config_only");
            result.ChangedFiles = activeFiles;
            result.IgnoredFiles = ignoredFiles;
            InitializeCategories(result, config);
            logger.LogSummary(false, "audit_config_only", 0, []);
            return result;
        }

        return null;
    }

    private static TestSelectionResult? CheckRunEverythingPaths(
        DiagnosticLogger logger,
        TestSelectorConfig? config,
        List<string> activeFiles,
        List<string> ignoredFiles)
    {
        logger.LogStep("Check runEverything Paths");

        var runEverythingPatterns = config?.RunEverything ?? [];
        var runEverythingDetector = new CriticalFileDetector(runEverythingPatterns);
        logger.LogInfo($"runEverything patterns: {runEverythingDetector.TriggerPatterns.Count}");

        var runEverythingFileInfo = runEverythingDetector.FindFirstCriticalFileWithDetails(activeFiles);

        if (runEverythingFileInfo is not null)
        {
            logger.LogWarning("runEverything file detected!");
            logger.LogMatch(runEverythingFileInfo.FilePath, runEverythingFileInfo.MatchedPattern);
            logger.LogDecision("Run ALL tests", "File matched runEverything");

            var result = TestSelectionResult.CriticalPath(runEverythingFileInfo.FilePath, runEverythingFileInfo.MatchedPattern);
            result.ChangedFiles = activeFiles;
            result.IgnoredFiles = ignoredFiles;
            InitializeCategories(result, config, allEnabled: true);
            logger.LogSummary(true, "run_everything_path", 0, []);
            return result;
        }

        logger.LogSuccess("No runEverything files detected");
        return null;
    }

    private static async Task<TestSelectionResult> EvaluateWithDotnetAffectedOnlyAsync(
        DiagnosticLogger logger,
        List<string> activeFiles,
        List<string> ignoredFiles,
        string solution,
        string? fromRef,
        string? toRef,
        string workingDir,
        string ciEnvironment,
        bool verbose)
    {
        logger.LogStep("Run dotnet-affected (no config - relationship-graph logic skipped)");

        // If no fromRef, we can't run dotnet-affected (it needs git refs)
        // In this case, run all tests as a conservative fallback
        if (string.IsNullOrEmpty(fromRef))
        {
            logger.LogWarning("No --from ref provided - cannot run dotnet-affected without git refs");
            logger.LogDecision("Run ALL tests", "Conservative fallback - no git ref to compare against");
            CIHelper.WriteWarning(ciEnvironment, "Selective test scope could not be evaluated because no --from git ref was provided. IGNORING selective scope and running all tests.");

            var fallbackResult = TestSelectionResult.RunAll("No git ref provided (--from) - cannot determine affected projects");
            fallbackResult.ChangedFiles = activeFiles;
            fallbackResult.IgnoredFiles = ignoredFiles;
            logger.LogSummary(true, "no_git_ref", 0, []);
            return fallbackResult;
        }

        var solutionPath = Path.IsPathRooted(solution) ? solution : Path.Combine(workingDir, solution);
        logger.LogInfo($"Solution: {solutionPath}");
        logger.LogInfo($"Comparing: {fromRef} → {toRef ?? "HEAD"}");

        var affectedRunner = new DotNetAffectedRunner(solutionPath, workingDir, verbose);
        var affectedResult = await affectedRunner.RunAsync(fromRef, toRef).ConfigureAwait(false);

        if (!affectedResult.Success)
        {
            CIHelper.WriteError(ciEnvironment, $"dotnet-affected failed (exit code {affectedResult.ExitCode}): {affectedResult.Error}");
            CIHelper.WriteWarning(ciEnvironment, "dotnet-affected failed during test selection. IGNORING selective scope and running all tests as a conservative fallback.");
            logger.LogWarning($"dotnet-affected failed (exit code {affectedResult.ExitCode})");
            logger.LogDecision("Run ALL tests", "Conservative fallback due to dotnet-affected failure");

            var errorResult = TestSelectionResult.RunAll($"dotnet-affected failed: {affectedResult.Error}");
            errorResult.ChangedFiles = activeFiles;
            errorResult.IgnoredFiles = ignoredFiles;
            logger.LogSummary(true, "dotnet_affected_failed", 0, []);
            return errorResult;
        }

        logger.LogSuccess($"dotnet-affected succeeded: {affectedResult.AffectedProjects.Count} affected projects");
        logger.LogList("Affected projects", affectedResult.AffectedProjects);

        // Filter to test projects using default patterns
        var testProjectFilter = new TestProjectFilter(new IncludeExcludePatterns());
        var filterResult = testProjectFilter.FilterWithDetails(affectedResult.AffectedProjects);
        var testProjects = filterResult.TestProjects.Select(p => p.Path).ToList();

        logger.LogInfo($"Test projects: {testProjects.Count}");

        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective_dotnet_affected_only",
            ChangedFiles = activeFiles,
            IgnoredFiles = ignoredFiles,
            DotnetAffectedProjects = affectedResult.AffectedProjects,
            AffectedTestProjects = testProjects,
            Categories = [],
            IntegrationsProjects = testProjects
        };

        logger.LogSummary(false, "selective_dotnet_affected_only", testProjects.Count, testProjects);
        return result;
    }

    private static (Dictionary<string, bool> CategoryStatus, List<string> MatchedFiles) MatchFilesToJobCategories(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles)
    {
        logger.LogStep("Match Files to Job Categories");
        var jobCategoryMapper = new CategoryMapper(config.JobCategories);
        var jobCategoryResult = jobCategoryMapper.GetCategoriesWithDetails(activeFiles);

        foreach (var (categoryName, matches) in jobCategoryResult.CategoryMatches)
        {
            if (matches.Count > 0)
            {
                logger.LogSubSection($"Job category '{categoryName}' triggered by:");
                foreach (var match in matches)
                {
                    logger.LogMatch(match.FilePath, match.MatchedPattern);
                }
            }
        }

        logger.LogCategories("Job category status", jobCategoryResult.CategoryStatus);
        logger.LogInfo($"Files matched by job categories: {jobCategoryResult.MatchedFiles.Count}");

        return (jobCategoryResult.CategoryStatus, jobCategoryResult.MatchedFiles.ToList());
    }

    private static (List<string> MappedProjects, List<string> MatchedFiles) ApplyMappings(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles)
    {
        logger.LogStep("Apply Mappings");
        var mappingResolver = new ProjectMappingResolver(config.Mappings);
        logger.LogInfo($"Mappings configured: {mappingResolver.MappingCount}");

        var mappingResult = mappingResolver.ResolveAllWithDetails(activeFiles);

        if (mappingResult.Mappings.Count > 0)
        {
            logger.LogSubSection("Files matched by mappings");
            foreach (var mapping in mappingResult.Mappings)
            {
                var detail = mapping.CapturedName is not null
                    ? $"captured {{name}}={mapping.CapturedName}"
                    : null;
                logger.LogMatch(mapping.SourceFile, mapping.SourcePattern, detail);
                logger.LogInfo($"      → test project: {mapping.TestProject}");
            }
        }

        logger.LogInfo($"Resolved test projects from mappings: {mappingResult.TestProjects.Count}");
        logger.LogInfo($"Files matched: {mappingResult.MatchedFiles.Count}");

        return (mappingResult.TestProjects.ToList(), mappingResult.MatchedFiles.ToList());
    }

    private static (List<string> EdgeProjects, List<string> MatchedFiles) ResolveEdges(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles)
    {
        logger.LogStep("Resolve Edges");
        var edgeResolver = new ProjectMappingResolver(EdgesAsMappings(config));
        logger.LogInfo($"Edges configured: {edgeResolver.MappingCount}");

        var edgeResult = edgeResolver.ResolveAllWithDetails(activeFiles);

        if (edgeResult.Mappings.Count > 0)
        {
            logger.LogSubSection("Files matched by edges");
            foreach (var edge in edgeResult.Mappings)
            {
                logger.LogMatch(edge.SourceFile, edge.SourcePattern);
                logger.LogInfo($"      → test project: {edge.TestProject}");
            }
        }

        logger.LogInfo($"Resolved test projects from edges: {edgeResult.TestProjects.Count}");
        logger.LogInfo($"Files matched: {edgeResult.MatchedFiles.Count}");

        return (edgeResult.TestProjects.ToList(), edgeResult.MatchedFiles.ToList());
    }

    /// <summary>
    /// Projects <see cref="SelectionEdge"/> entries onto the <see cref="SelectionMapping"/> shape so
    /// the same glob/{name}/exclude resolver handles both. The category/type tags are not needed for
    /// resolution (they are policy, applied elsewhere), so they are dropped here.
    /// </summary>
    private static IEnumerable<SelectionMapping> EdgesAsMappings(TestSelectorConfig config)
    {
        return config.Edges.Select(e => new SelectionMapping
        {
            From = e.From,
            To = e.To,
            Exclude = e.Exclude
        });
    }

    private static TestSelectionResult? CheckUnmatchedFiles(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles,
        List<string> ignoredFiles,
        List<string> jobCategoryMatchedFiles,
        List<string> mappingMatchedFiles,
        List<string> edgeMatchedFiles)
    {
        logger.LogStep("Check for Unmatched Files");
        var allMatchedFiles = jobCategoryMatchedFiles
            .Union(mappingMatchedFiles)
            .Union(edgeMatchedFiles)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmatchedFiles = activeFiles.Where(f => !allMatchedFiles.Contains(f.Replace('\\', '/'))).ToList();

        logger.LogInfo($"Total files: {activeFiles.Count}");
        logger.LogInfo($"Matched by job categories: {jobCategoryMatchedFiles.Count}");
        logger.LogInfo($"Matched by mappings: {mappingMatchedFiles.Count}");
        logger.LogInfo($"Matched by edges: {edgeMatchedFiles.Count}");
        logger.LogInfo($"Unmatched files: {unmatchedFiles.Count}");

        if (unmatchedFiles.Count > 0)
        {
            logger.LogWarning("Unmatched files found - triggering conservative fallback");
            logger.LogList("Unmatched files", unmatchedFiles);
            logger.LogDecision("Run ALL tests", "Conservative fallback due to unmatched files");

            var reason = unmatchedFiles.Count <= 5
                ? $"Unmatched files: {string.Join(", ", unmatchedFiles)}"
                : $"Unmatched files ({unmatchedFiles.Count}): {string.Join(", ", unmatchedFiles.Take(5))}...";

            var unmatchedResult = TestSelectionResult.RunAll(reason);
            unmatchedResult.ChangedFiles = activeFiles;
            unmatchedResult.IgnoredFiles = ignoredFiles;
            InitializeCategories(unmatchedResult, config, allEnabled: true);
            logger.LogSummary(true, reason, 0, []);
            return unmatchedResult;
        }

        logger.LogSuccess("All files are accounted for");
        return null;
    }

    /// <summary>
    /// Conservative guard for the "matched-but-zero" gap: integration-relevant files were
    /// matched (the <c>integrations</c> jobCategory fired) but the build graph attributed the
    /// change to no project at all, so zero integration test projects were selected.
    /// Returns a run-all result in that case, otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The usual cause is a changed file that is not an MSBuild input of any project (for
    /// example a file under a project that sets <c>EnableDefaultItems=false</c>, or one that
    /// is otherwise outside the default item globs), so dotnet-affected's reverse-dependency
    /// walk returns nothing. Selecting no tests there would silently skip coverage for a
    /// source area we know changed, so we fall back to running everything.
    /// <para>
    /// Scoped to the <c>integrations</c> jobCategory on purpose. The <c>extension</c> and
    /// <c>polyglot</c> categories run as dedicated jobs gated by their <c>run_&lt;category&gt;</c>
    /// flag, independent of the affected-projects matrix, so an empty matrix is expected and
    /// correct for them. The <c>cli_e2e</c> edges <em>do</em> flow through the
    /// affected_test_projects matrix (the CLI end-to-end job filters on it), but an edge unions its
    /// test project into the matrix whenever it fires, so it can never resolve to zero. Requiring
    /// <paramref name="affectedProjects"/> to be empty keeps this from firing when dotnet-affected
    /// <em>did</em> see the change but it resolved only to an <c>inferDeps:false</c> opt-out project
    /// (see <see cref="InferDepsFilter"/>) or a source project with no dependent test — those are
    /// deliberate outcomes, not blind spots.
    /// </para>
    /// </remarks>
    internal static TestSelectionResult? CheckMatchedButZeroProjects(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        Dictionary<string, bool> jobCategoryMatched,
        List<string> affectedProjects,
        List<string> allTestProjects,
        List<string> activeFiles,
        List<string> ignoredFiles)
    {
        logger.LogStep("Check Matched-But-Zero Projects");

        // "integrations" is the reserved jobCategory whose coverage is driven by the
        // affected_test_projects matrix (see TestSelectionResult.WriteGitHubOutput).
        if (!jobCategoryMatched.GetValueOrDefault(TestSelectorConfig.IntegrationsCategory)
            || allTestProjects.Count > 0
            || affectedProjects.Count > 0)
        {
            logger.LogSuccess("No matched-but-zero gap detected");
            return null;
        }

        logger.LogWarning("Integrations category matched but the change resolved to zero projects");
        logger.LogDecision("Run ALL tests", "Integration-relevant change not attributable to any project (likely a non-MSBuild-input file)");

        var result = TestSelectionResult.RunAll(
            "Integration-relevant files matched but resolved to zero test projects (change not seen by dotnet-affected; likely a non-MSBuild-input file)");
        result.ChangedFiles = activeFiles;
        result.IgnoredFiles = ignoredFiles;
        InitializeCategories(result, config, allEnabled: true);
        logger.LogSummary(true, "integrations_matched_zero_projects", 0, []);
        return result;
    }

    private static async Task<(List<string> AffectedProjects, TestSelectionResult? FallbackResult)> RunDotnetAffectedAsync(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles,
        List<string> ignoredFiles,
        string solution,
        string? fromRef,
        string? toRef,
        string workingDir,
        string ciEnvironment,
        bool verbose)
    {
        logger.LogStep("Run dotnet-affected");

        // If no fromRef, we can't run dotnet-affected (it needs git refs)
        if (string.IsNullOrEmpty(fromRef))
        {
            logger.LogWarning("No --from ref provided - cannot run dotnet-affected without git refs");
            logger.LogDecision("Run ALL tests", "Conservative fallback - no git ref to compare against");
            CIHelper.WriteWarning(ciEnvironment, "Selective test scope could not be evaluated because no --from git ref was provided. IGNORING selective scope and running all tests.");

            var fallbackResult = TestSelectionResult.RunAll("No git ref provided (--from) - cannot determine affected projects");
            fallbackResult.ChangedFiles = activeFiles;
            fallbackResult.IgnoredFiles = ignoredFiles;
            InitializeCategories(fallbackResult, config, allEnabled: true);
            logger.LogSummary(true, "no_git_ref", 0, []);
            return ([], fallbackResult);
        }

        var solutionPath = Path.IsPathRooted(solution) ? solution : Path.Combine(workingDir, solution);
        logger.LogInfo($"Solution: {solutionPath}");
        logger.LogInfo($"Comparing: {fromRef} → {toRef ?? "HEAD"}");

        var affectedRunner = new DotNetAffectedRunner(solutionPath, workingDir, verbose);
        var affectedResult = await affectedRunner.RunAsync(fromRef, toRef).ConfigureAwait(false);

        if (affectedResult.Success)
        {
            logger.LogSuccess($"dotnet-affected succeeded: {affectedResult.AffectedProjects.Count} affected projects");
            logger.LogList("Affected projects", affectedResult.AffectedProjects);
            return (affectedResult.AffectedProjects, null);
        }

        // dotnet-affected failed - conservative fallback
        CIHelper.WriteError(ciEnvironment, $"dotnet-affected failed (exit code {affectedResult.ExitCode}): {affectedResult.Error}");
        CIHelper.WriteWarning(ciEnvironment, "dotnet-affected failed during test selection. IGNORING selective scope and running all tests as a conservative fallback.");
        logger.LogWarning($"dotnet-affected failed (exit code {affectedResult.ExitCode})");
        if (!string.IsNullOrWhiteSpace(affectedResult.Error))
        {
            logger.LogInfo($"Error: {affectedResult.Error}");
        }
        if (!string.IsNullOrWhiteSpace(affectedResult.StdOut))
        {
            logger.LogInfo($"stdout: {affectedResult.StdOut.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(affectedResult.StdErr))
        {
            logger.LogInfo($"stderr: {affectedResult.StdErr.Trim()}");
        }

        logger.LogDecision("Run ALL tests", "Conservative fallback due to dotnet-affected failure");

        var errorResult = TestSelectionResult.RunAll($"dotnet-affected failed: {affectedResult.Error}");
        errorResult.ChangedFiles = activeFiles;
        errorResult.IgnoredFiles = ignoredFiles;
        InitializeCategories(errorResult, config, allEnabled: true);
        logger.LogSummary(true, "dotnet_affected_failed", 0, []);
        return ([], errorResult);
    }

    private static List<string> FilterAndCombineTestProjects(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> affectedProjects,
        List<string> mappedProjects)
    {
        logger.LogStep("Filter Test Projects");
        var testProjectFilter = new TestProjectFilter(config.TestProjectPatterns);
        logger.LogInfo($"Include patterns: {string.Join(", ", testProjectFilter.IncludePatterns)}");
        logger.LogInfo($"Exclude patterns: {string.Join(", ", testProjectFilter.ExcludePatterns)}");

        var filterResult = testProjectFilter.FilterWithDetails(affectedProjects);

        if (filterResult.TestProjects.Count > 0)
        {
            logger.LogSubSection("Test projects (from dotnet-affected)");
            foreach (var proj in filterResult.TestProjects)
            {
                logger.LogInfo($"    • {proj.Name ?? proj.Path}");
            }
        }

        // Keep .csproj paths from dotnet-affected for direct matching with
        // matrix entry testProjectPath values (e.g. "tests/Foo.Tests/Foo.Tests.csproj")
        var testProjects = filterResult.TestProjects.Select(p =>
            p.Path.Replace('\\', '/')
        ).ToList();
        logger.LogInfo($"Test projects from dotnet-affected: {testProjects.Count}");

        logger.LogStep("Combine Test Projects");

        // dotnet-affected results are additive: they supplement mapping resolution by catching
        // test projects the mappings missed (e.g. transitive dependencies, projects with no
        // mapping). The union of both gives the most complete picture.
        var allTestProjects = mappedProjects
            .Concat(testProjects)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Apply inferDeps: a project with inferDeps:false runs ONLY when a declared mapping resolved
        // to it (it's in mappedProjects). dotnet-affected pulling it in via an inferred build-graph
        // reference is not enough — the opt-in is the explicit edge. Edges are unioned in by the
        // caller AFTER this filter, so an explicit edge also keeps an inferDeps:false project.
        // See InferDepsFilter for the contract and unit tests.
        var beforeCount = allTestProjects.Count;
        allTestProjects = InferDepsFilter.Apply(allTestProjects, config.InferDeps, mappedProjects);
        var dropped = beforeCount - allTestProjects.Count;
        if (dropped > 0)
        {
            logger.LogInfo($"Dropped {dropped} inferDeps:false test project(s) not resolved by a declared edge");
        }

        logger.LogInfo($"From mappings: {mappedProjects.Count}");
        logger.LogInfo($"From dotnet-affected: {testProjects.Count}");
        logger.LogInfo($"Total unique test projects: {allTestProjects.Count}");

        return allTestProjects;
    }

    /// <summary>
    /// Returns <paramref name="testProjects"/> unioned with <paramref name="edgeProjects"/>,
    /// de-duplicated case-insensitively. Edges are explicit declarations, so they bypass
    /// <see cref="InferDepsFilter"/> — an edge resolving to an <c>inferDeps:false</c> project still
    /// selects it.
    /// </summary>
    private static List<string> UnionEdgeProjects(
        DiagnosticLogger logger,
        List<string> testProjects,
        List<string> edgeProjects)
    {
        if (edgeProjects.Count == 0)
        {
            return testProjects;
        }

        var combined = testProjects
            .Concat(edgeProjects.Select(p => p.Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var added = combined.Count - testProjects.Count;
        if (added > 0)
        {
            logger.LogInfo($"Added {added} test project(s) from explicit edges");
        }

        return combined;
    }

    /// <summary>
    /// Derives the <c>run_&lt;category&gt;</c> booleans. Edge-label categories (e.g. <c>cli_e2e</c>)
    /// are a pure projection over the final selected set, so the boolean can never disagree with the
    /// affected_test_projects matrix. Standalone job categories (e.g. <c>extension</c>,
    /// <c>polyglot</c>) reflect whether their when-globs matched a changed file. The reserved
    /// <c>integrations</c> jobCategory is intentionally excluded: its boolean is derived from the
    /// selected test count in <see cref="TestSelectionResult.WriteGitHubOutput"/>.
    /// </summary>
    private static Dictionary<string, bool> DeriveCategories(
        Dictionary<string, bool> jobCategoryMatched,
        Dictionary<string, HashSet<string>> categoryToProjects,
        List<string> allTestProjects)
    {
        var categories = new Dictionary<string, bool>();
        var selected = allTestProjects
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (category, labeledProjects) in categoryToProjects)
        {
            categories[category] = labeledProjects.Overlaps(selected);
        }

        foreach (var (name, matched) in jobCategoryMatched)
        {
            if (string.Equals(name, TestSelectorConfig.IntegrationsCategory, StringComparison.Ordinal))
            {
                continue;
            }

            categories[name] = matched;
        }

        return categories;
    }

    private static TestSelectionResult BuildSelectiveResult(
        DiagnosticLogger logger,
        List<string> activeFiles,
        List<string> ignoredFiles,
        List<string> affectedProjects,
        List<string> allTestProjects,
        Dictionary<string, bool> categories,
        string reason = "selective")
    {
        logger.LogStep("Build Final Result");
        var finalResult = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = reason,
            ChangedFiles = activeFiles,
            IgnoredFiles = ignoredFiles,
            DotnetAffectedProjects = affectedProjects,
            AffectedTestProjects = allTestProjects,
            Categories = categories,
            IntegrationsProjects = allTestProjects
        };

        logger.LogSummary(false, reason, allTestProjects.Count, allTestProjects);

        return finalResult;
    }

    private static bool CanUseMappingsOnly(
        DiagnosticLogger logger,
        List<string> activeFiles,
        List<string> mappingMatchedFiles,
        List<string> mappedProjects)
    {
        if (mappedProjects.Count == 0)
        {
            return false;
        }

        var mappingMatchedFileSet = mappingMatchedFiles
            .Select(f => f.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmappedActiveFiles = activeFiles
            .Where(f => !mappingMatchedFileSet.Contains(f.Replace('\\', '/')))
            .ToList();

        if (unmappedActiveFiles.Count > 0)
        {
            logger.LogInfo($"Mappings-only mode unavailable: {unmappedActiveFiles.Count} active file(s) are not covered by mappings");
            return false;
        }

        return true;
    }

    private static NuGetDependentTestsInfo? DetectNuGetDependentTests(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> affectedProjects,
        List<string> activeFiles,
        string workingDir)
    {
        logger.LogStep("Detect NuGet-Dependent Tests");

        var detector = new NuGetDependentTestDetector(config.PackageOrArchiveProducingProjects);
        var nugetInfo = detector.Detect(affectedProjects, activeFiles, workingDir);

        if (nugetInfo is not null)
        {
            logger.LogWarning("NuGet-dependent tests triggered!");
            logger.LogList("Affected packable projects", nugetInfo.AffectedPackableProjects);
            logger.LogList("NuGet-dependent test projects", nugetInfo.Projects);
        }
        else
        {
            logger.LogSuccess("No NuGet-dependent tests triggered");
        }

        return nugetInfo;
    }

    public static NuGetDependentTestsInfo? PopulateAllNuGetDependentTests(string workingDir)
    {
        var nugetTestProjects = NuGetDependentTestDetector.FindNuGetDependentTestProjects(workingDir);
        if (nugetTestProjects.Count == 0)
        {
            return null;
        }

        return new NuGetDependentTestsInfo
        {
            Triggered = true,
            Reason = "All tests triggered (runAll=true)",
            AffectedPackableProjects = [],
            Projects = nugetTestProjects
        };
    }

    /// <summary>
    /// Initializes the <c>run_&lt;category&gt;</c> output keys for the non-selective result paths
    /// (no-changes, all-ignored, runEverything, fallbacks). The keys are the distinct edge
    /// categories plus the standalone job categories (everything except the reserved
    /// <c>integrations</c>, whose boolean is count-derived in
    /// <see cref="TestSelectionResult.WriteGitHubOutput"/>).
    /// </summary>
    private static void InitializeCategories(TestSelectionResult result, TestSelectorConfig? config, bool allEnabled = false)
    {
        result.Categories = [];
        if (config is null)
        {
            return;
        }

        foreach (var category in config.Edges
            .Where(e => !string.IsNullOrEmpty(e.Category))
            .Select(e => e.Category!)
            .Distinct(StringComparer.Ordinal))
        {
            result.Categories[category] = allEnabled;
        }

        foreach (var name in config.JobCategories.Keys)
        {
            if (string.Equals(name, TestSelectorConfig.IntegrationsCategory, StringComparison.Ordinal))
            {
                continue;
            }

            result.Categories[name] = allEnabled;
        }
    }
}
