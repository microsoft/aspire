// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal static class TypeScriptApiCompatRunner
{
    public static int Run(CommandLineOptions options)
    {
        try
        {
            var excludedPackages = ExcludedPackageLoader.Load(options.ExcludedPackagesFile);
            var baseline = AtsSurfaceSet.Load(options.BaselinePath);
            var current = AtsSurfaceSet.Load(options.CurrentPath);
            var diagnostics = AtsCompatibilityComparer.Compare(baseline, current, excludedPackages);
            var suppressionLoadResult = ApiCompatSuppressionLoader.Load(options.SuppressionsRoot);
            var baselineSuppressionLoadResult = options.BaselineSuppressionsRoot is null
                ? null
                : ApiCompatSuppressionLoader.Load(options.BaselineSuppressionsRoot);
            var result = ApiCompatSuppressor.ApplySuppressions(diagnostics, suppressionLoadResult, baselineSuppressionLoadResult, excludedPackages);
            var report = ApiCompatReport.Create(result);

            if (!string.IsNullOrWhiteSpace(options.ReportPath))
            {
                var reportDirectory = Path.GetDirectoryName(options.ReportPath);
                if (!string.IsNullOrEmpty(reportDirectory))
                {
                    Directory.CreateDirectory(reportDirectory);
                }

                File.WriteAllText(options.ReportPath, report);
            }

            if (options.GitHubAnnotations && result.HasFailures)
            {
                GitHubAnnotationWriter.WriteErrors(result);
            }

            Console.WriteLine(report);

            return result.HasFailures ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
