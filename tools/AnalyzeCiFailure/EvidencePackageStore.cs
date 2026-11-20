// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.



namespace AnalyzeCiFailure;

/// <summary>Reads validated packages and persists or invalidates generated evidence views.</summary>
internal static class EvidencePackageStore
{

    internal static ValidatedAnalysis ReadAndValidate(EvidencePaths paths, string analysisDirectory, string repository)
        => AnalysisValidator.Validate(
            TransientJson.ReadUtf8<EvidenceBundle>(File.ReadAllBytes(paths.Bundle)),
            TransientJson.ReadUtf8<AnalysisProposal>(File.ReadAllBytes(Path.Combine(analysisDirectory, "proposal.json"))),
            repository);

    internal static async Task WriteDiagnosisAsync(EvidencePaths paths, DiagnosisInput input, CancellationToken ct)
    {
        await File.WriteAllTextAsync(paths.Bundle, TransientJson.Write(input.Evidence), ct);
        await File.WriteAllTextAsync(paths.Summary, input.Summary, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PriorCauseIndex)!);
        CiFailureJson.WriteContract(paths.PriorCauseIndex, input.Catalogue);
        foreach (var (name, value) in input.Attachments)
        {
            var path = Path.Combine(paths.RootDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, value.Bytes.ToArray(), ct);
        }
        foreach (var (name, value) in input.JsonContext.Where(pair => EvidencePaths.IsDiagnosisContextJson(pair.Key)))
        {
            var path = Path.Combine(paths.RootDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, CiFailureJson.Serialize(value) + Environment.NewLine, ct);
        }
    }

    internal static Task WriteOutcomeAsync(EvidencePaths paths, AnalysisOutcome outcome)
    {
        return File.WriteAllTextAsync(paths.Outcome, TransientJson.Write(outcome));
    }

    internal static void InvalidateEvidenceViews(EvidencePaths paths, AnalysisProgress progress)
    {
        foreach (var path in new[] { paths.Bundle, paths.Summary, paths.PriorCauseIndex })
        {
            try
            {
                if (Directory.Exists(Path.GetDirectoryName(path)))
                {
                    File.Delete(path);
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // Cleanup cannot prevent best-effort outcome finalization or hide the original error.
                progress.RecordProblem(AnalysisProblemCategory.Fatal, "cleanup", cleanupError.Message);
            }
        }
    }
}
