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
    //   │  Check TriggerAll Paths     │
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
    //   │  With Config? (categories,   │
    //   │  sourceToTestMappings, etc.) │
    //   └───────────────┬──────────────┘
    //                   │
    //          ┌────────┴─────────┐
    //          │ no config        │ has config
    //          ▼                  ▼
    //   ┌──────────────┐  ┌──────────────────┐
    //   │ dotnet-      │  │ Match Categories │
    //   │ affected     │  │ + SourceToTest   │
    //   │ only         │  │ Mappings         │
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
    //          │  │ Test Projects    │
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

        // Step 2b: Rescue ignored files that match category triggerPaths.
        // This prevents categories (e.g. polyglot) whose triggerPaths overlap with
        // ignorePaths (e.g. .github/workflows/**) from being silently dropped.
        if (config is not null && ignoredFiles.Count > 0)
        {
            var rescued = RescueCategoryTriggerFiles(logger, config, ignoredFiles);
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

        // Step 3: Check for triggerAll paths
        var triggerAllResult = CheckTriggerAllPaths(logger, config, activeFiles, ignoredFiles);
        if (triggerAllResult is not null)
        {
            return triggerAllResult;
        }

        // Step 4: If no config, use dotnet-affected only (skip category logic)
        if (config is null)
        {
            return await EvaluateWithDotnetAffectedOnlyAsync(
                logger, activeFiles, ignoredFiles, solution, fromRef, toRef, workingDir, ciEnvironment, verbose).ConfigureAwait(false);
        }

        // Step 5: Match files to categories via triggerPaths
        var (pathTriggeredCategories, pathMatchedFiles) = MatchFilesToCategories(logger, config, activeFiles);

        // Step 6: Apply sourceToTestMappings
        var (mappedProjects, mappingMatchedFiles) = ApplySourceToTestMappings(logger, config, activeFiles);

        // Step 7: Check for unmatched files (conservative fallback)
        var unmatchedResult = CheckUnmatchedFiles(
            logger, config, activeFiles, ignoredFiles, pathMatchedFiles, mappingMatchedFiles);
        if (unmatchedResult is not null)
        {
            return unmatchedResult;
        }

        if (CanUseSourceToTestMappingsOnly(logger, activeFiles, mappingMatchedFiles, mappedProjects))
        {
            logger.LogStep("Skip dotnet-affected");
            logger.LogDecision("Use source-to-test mappings only", "All active files were resolved by source-to-test mappings");

            var mappedOnlyTestProjects = FilterAndCombineTestProjects(logger, config, [], mappedProjects);
            var mappedOnlyNugetInfo = DetectNuGetDependentTests(logger, config, [], activeFiles, workingDir);

            UpdateCategoriesFromTestProjects(logger, config, pathTriggeredCategories, mappedOnlyTestProjects);

            var mappedOnlyResult = BuildSelectiveResult(
                logger,
                activeFiles,
                ignoredFiles,
                [],
                mappedOnlyTestProjects,
                pathTriggeredCategories,
                reason: "selective_mappings_only");

            mappedOnlyResult.NuGetDependentTests = mappedOnlyNugetInfo;
            return mappedOnlyResult;
        }

        // Step 8: Run dotnet-affected
        var affectedResult = await RunDotnetAffectedAsync(
            logger, config, activeFiles, ignoredFiles, solution, fromRef, toRef, workingDir, ciEnvironment, verbose).ConfigureAwait(false);
        if (affectedResult.FallbackResult is not null)
        {
            return affectedResult.FallbackResult;
        }

        // Step 9: Filter and combine test projects
        var allTestProjects = FilterAndCombineTestProjects(
            logger, config, affectedResult.AffectedProjects, mappedProjects);

        // Step 10: Detect NuGet-dependent tests
        var nugetInfo = DetectNuGetDependentTests(
            logger, config, affectedResult.AffectedProjects, activeFiles, workingDir);

        // Step 11: Match test projects against categories
        UpdateCategoriesFromTestProjects(logger, config, pathTriggeredCategories, allTestProjects);

        // Step 12: Build final result
        var result = BuildSelectiveResult(
            logger, activeFiles, ignoredFiles, affectedResult.AffectedProjects, allTestProjects, pathTriggeredCategories);
        result.NuGetDependentTests = nugetInfo;
        return result;
    }

    /// <summary>
    /// Checks ignored files against category triggerPaths and rescues any that match,
    /// so that categories like polyglot (whose triggerPaths are under .github/workflows/**)
    /// are not silently dropped by the blanket ignorePaths filter.
    /// </summary>
    private static List<string> RescueCategoryTriggerFiles(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> ignoredFiles)
    {
        var rescued = new List<string>();
        var allCategoryTriggerPatterns = config.Categories.Values
            .SelectMany(c => c.TriggerPaths)
            .Distinct()
            .ToList();

        if (allCategoryTriggerPatterns.Count == 0)
        {
            return rescued;
        }

        var matcher = new CategoryMapper(new Dictionary<string, CategoryConfig>
        {
            ["_rescue"] = new CategoryConfig { TriggerPaths = allCategoryTriggerPatterns }
        });

        foreach (var file in ignoredFiles)
        {
            var result = matcher.GetCategoriesWithDetails([file]);
            if (result.CategoryStatus.GetValueOrDefault("_rescue"))
            {
                rescued.Add(file);
            }
        }

        if (rescued.Count > 0)
        {
            logger.LogInfo($"Rescued {rescued.Count} ignored file(s) that match category triggerPaths:");
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

        var ignorePatterns = config?.IgnorePaths ?? [];
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

    private static TestSelectionResult? CheckTriggerAllPaths(
        DiagnosticLogger logger,
        TestSelectorConfig? config,
        List<string> activeFiles,
        List<string> ignoredFiles)
    {
        logger.LogStep("Check TriggerAll Paths");

        var triggerAllPatterns = config?.TriggerAllPaths ?? [];
        var triggerAllDetector = new CriticalFileDetector(triggerAllPatterns);
        logger.LogInfo($"TriggerAll patterns: {triggerAllDetector.TriggerPatterns.Count}");

        var triggerAllFileInfo = triggerAllDetector.FindFirstCriticalFileWithDetails(activeFiles);

        if (triggerAllFileInfo is not null)
        {
            logger.LogWarning("TriggerAll file detected!");
            logger.LogMatch(triggerAllFileInfo.FilePath, triggerAllFileInfo.MatchedPattern);
            logger.LogDecision("Run ALL tests", "File matched triggerAllPaths");

            var result = TestSelectionResult.CriticalPath(triggerAllFileInfo.FilePath, triggerAllFileInfo.MatchedPattern);
            result.ChangedFiles = activeFiles;
            result.IgnoredFiles = ignoredFiles;
            InitializeCategories(result, config, allEnabled: true);
            logger.LogSummary(true, "trigger_all_path", 0, []);
            return result;
        }

        logger.LogSuccess("No triggerAll files detected");
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
        logger.LogStep("Run dotnet-affected (no config - category logic skipped)");

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

    private static (Dictionary<string, bool> CategoryStatus, List<string> MatchedFiles) MatchFilesToCategories(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles)
    {
        logger.LogStep("Match Files to Categories");
        var categoryMapper = new CategoryMapper(config.Categories);
        var categoryResult = categoryMapper.GetCategoriesWithDetails(activeFiles);

        foreach (var (categoryName, matches) in categoryResult.CategoryMatches)
        {
            if (matches.Count > 0)
            {
                logger.LogSubSection($"Category '{categoryName}' triggered by:");
                foreach (var match in matches)
                {
                    logger.LogMatch(match.FilePath, match.MatchedPattern);
                }
            }
        }

        logger.LogCategories("Category status", categoryResult.CategoryStatus);
        logger.LogInfo($"Files matched by categories: {categoryResult.MatchedFiles.Count}");

        return (categoryResult.CategoryStatus, categoryResult.MatchedFiles.ToList());
    }

    private static (List<string> MappedProjects, List<string> MatchedFiles) ApplySourceToTestMappings(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles)
    {
        logger.LogStep("Apply Source-to-Test Mappings");
        var projectMappingResolver = new ProjectMappingResolver(config.SourceToTestMappings);
        logger.LogInfo($"Source-to-test mappings configured: {projectMappingResolver.MappingCount}");

        var mappingResult = projectMappingResolver.ResolveAllWithDetails(activeFiles);

        if (mappingResult.Mappings.Count > 0)
        {
            logger.LogSubSection("Files matched by source-to-test mappings");
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

    private static TestSelectionResult? CheckUnmatchedFiles(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        List<string> activeFiles,
        List<string> ignoredFiles,
        List<string> pathMatchedFiles,
        List<string> mappingMatchedFiles)
    {
        logger.LogStep("Check for Unmatched Files");
        var allMatchedFiles = pathMatchedFiles
            .Union(mappingMatchedFiles)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmatchedFiles = activeFiles.Where(f => !allMatchedFiles.Contains(f.Replace('\\', '/'))).ToList();

        logger.LogInfo($"Total files: {activeFiles.Count}");
        logger.LogInfo($"Matched by categories: {pathMatchedFiles.Count}");
        logger.LogInfo($"Matched by source-to-test mappings: {mappingMatchedFiles.Count}");
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

        // dotnet-affected results are additive: they supplement pattern-based mappings
        // by catching test projects the mappings missed (e.g. transitive dependencies,
        // projects with no sourceToTestMapping). The union of both gives the most
        // complete picture.
        var allTestProjects = mappedProjects
            .Concat(testProjects)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInfo($"From source-to-test mappings: {mappedProjects.Count}");
        logger.LogInfo($"From dotnet-affected: {testProjects.Count}");
        logger.LogInfo($"Total unique test projects: {allTestProjects.Count}");

        return allTestProjects;
    }

    private static void UpdateCategoriesFromTestProjects(
        DiagnosticLogger logger,
        TestSelectorConfig config,
        Dictionary<string, bool> pathTriggeredCategories,
        List<string> allTestProjects)
    {
        logger.LogStep("Match Test Projects to Categories");
        var categoryMapper = new CategoryMapper(config.Categories);
        var testProjectCategoryResult = categoryMapper.GetCategoriesWithDetails(allTestProjects);

        foreach (var (categoryName, enabled) in testProjectCategoryResult.CategoryStatus)
        {
            if (enabled && !pathTriggeredCategories.GetValueOrDefault(categoryName))
            {
                logger.LogInfo($"Category '{categoryName}' additionally triggered by test project paths");
                pathTriggeredCategories[categoryName] = true;
            }
        }
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

    private static bool CanUseSourceToTestMappingsOnly(
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
            logger.LogInfo($"Source-to-test mapping only mode unavailable: {unmappedActiveFiles.Count} active file(s) are not covered by source-to-test mappings");
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

    private static void InitializeCategories(TestSelectionResult result, TestSelectorConfig? config, bool allEnabled = false)
    {
        result.Categories = [];
        if (config is not null)
        {
            foreach (var categoryName in config.Categories.Keys)
            {
                result.Categories[categoryName] = allEnabled;
            }
        }
    }
}
