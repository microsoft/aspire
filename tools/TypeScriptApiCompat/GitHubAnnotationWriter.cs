// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal static class GitHubAnnotationWriter
{
    public static void WriteErrors(ApiCompatResult result)
    {
        foreach (var diagnostic in result.UnsuppressedDiagnostics)
        {
            WriteError($"[{diagnostic.PackageName}] {diagnostic.Kind}: {diagnostic.Symbol}", diagnostic.Message);
        }

        foreach (var error in result.SuppressionErrors)
        {
            WriteError("TypeScript API compatibility suppression error", error);
        }

        foreach (var suppression in result.UnusedSuppressions)
        {
            WriteError(
                "Unused TypeScript API compatibility suppression",
                $"{suppression.FilePath}:{suppression.LineNumber}: {suppression.Kind} {suppression.PackageName} {suppression.Symbol}");
        }
    }

    private static void WriteError(string title, string message)
    {
        Console.WriteLine($"::error title={Escape(title)}::{Escape(message)}");
    }

    private static string Escape(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("\r", "%0D", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal);
}
