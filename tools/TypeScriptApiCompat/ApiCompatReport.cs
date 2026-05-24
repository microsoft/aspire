// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Globalization;

namespace TypeScriptApiCompat;

internal static class ApiCompatReport
{
    public static string Create(ApiCompatResult result)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# TypeScript API compatibility report");
        builder.AppendLine();

        AppendExcludedPackages(builder, result);

        if (!result.HasFailures)
        {
            builder.AppendLine("No undeclared TypeScript API compatibility breaks were found.");
            if (result.SuppressedDiagnostics.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "Suppressed diagnostics: {0}", result.SuppressedDiagnostics.Count));
            }

            return builder.ToString();
        }

        if (result.UnsuppressedDiagnostics.Count > 0)
        {
            builder.AppendLine("## Unsuppressed breaking changes");
            builder.AppendLine();
            foreach (var diagnostic in result.UnsuppressedDiagnostics.OrderBy(static d => d.PackageName, StringComparer.Ordinal).ThenBy(static d => d.Kind, StringComparer.Ordinal).ThenBy(static d => d.Symbol, StringComparer.Ordinal))
            {
                builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "- `{0}` `{1}` `{2}` - {3}", diagnostic.Kind, diagnostic.PackageName, diagnostic.Symbol, diagnostic.Message));
            }

            builder.AppendLine();
        }

        if (result.SuppressionErrors.Count > 0)
        {
            builder.AppendLine("## Suppression file errors");
            builder.AppendLine();
            foreach (var error in result.SuppressionErrors)
            {
                builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "- {0}", error));
            }

            builder.AppendLine();
        }

        if (result.UnusedSuppressions.Count > 0)
        {
            builder.AppendLine("## Unused suppressions");
            builder.AppendLine();
            foreach (var suppression in result.UnusedSuppressions)
            {
                builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "- `{0}` `{1}` `{2}` at `{3}:{4}`", suppression.Kind, suppression.PackageName, suppression.Symbol, suppression.FilePath, suppression.LineNumber));
            }

            builder.AppendLine();
        }

        if (result.SuppressedDiagnostics.Count > 0)
        {
            builder.AppendLine("## Suppressed breaking changes");
            builder.AppendLine();
            foreach (var diagnostic in result.SuppressedDiagnostics.OrderBy(static d => d.PackageName, StringComparer.Ordinal).ThenBy(static d => d.Kind, StringComparer.Ordinal).ThenBy(static d => d.Symbol, StringComparer.Ordinal))
            {
                builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "- `{0}` `{1}` `{2}` - {3}", diagnostic.Kind, diagnostic.PackageName, diagnostic.Symbol, diagnostic.Message));
            }
        }

        return builder.ToString();
    }

    private static void AppendExcludedPackages(StringBuilder builder, ApiCompatResult result)
    {
        if (result.ExcludedPackages.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Excluded packages");
        builder.AppendLine();
        builder.AppendLine("These packages set `DisablePackageBaselineValidation=true`, so TypeScript API compatibility is not enforced for them.");
        builder.AppendLine();

        foreach (var packageName in result.ExcludedPackages.Order(StringComparer.Ordinal))
        {
            builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "- `{0}`", packageName));
        }

        builder.AppendLine();
    }
}
