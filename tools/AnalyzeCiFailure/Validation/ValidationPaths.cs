// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AnalyzeCiFailure.Validation;

/// <summary>
/// Input locations for validation. Agent-written files live under <see cref="AnalysisDirectory"/>;
/// files the workflow collected itself (trusted) live under <c>ci-failure-data</c> in
/// <see cref="WorkingDirectory"/>.
/// </summary>
internal sealed record ValidationPaths(string AnalysisDirectory, string WorkingDirectory, string ScriptsDirectory)
{
    public string AnalysisFile => Path.Combine(AnalysisDirectory, "analysis-result.json");
    public string CausesDirectory => Path.Combine(AnalysisDirectory, "causes");

    public string RunContextFile => TrustedData("run-context.json");
    public string RunFile => TrustedData("run.json");
    public string FailedJobsFile => TrustedData("failed-jobs.json");
    public string TestEvidenceFile => TrustedData("test-evidence.json");
    public string TestFailuresFile => TrustedData("test-failures.json");

    /// <summary>The cause file with the same name stored by a previous analysis, if any.</summary>
    public string PriorCauseFile(string causeFileName) => TrustedData(Path.Combine("prior-causes", causeFileName));

    private string TrustedData(string relativePath) => Path.Combine(WorkingDirectory, "ci-failure-data", relativePath);
}
