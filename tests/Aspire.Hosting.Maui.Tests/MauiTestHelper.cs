// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

/// <summary>
/// Shared helpers for creating temporary MAUI project files in tests.
/// </summary>
internal static class MauiTestHelper
{
    /// <summary>
    /// Creates a minimal project file with only the specified TFM.
    /// Each test targets a single platform, so multi-TFM is not needed here.
    /// </summary>
    public static string CreateProjectContent(string requiredTfm)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFrameworks>{{requiredTfm}}</TargetFrameworks>
                </PropertyGroup>
            </Project>
            """;
    }

    public static string CreateTempProjectFile(string content)
    {
        var tempFolder = Directory.CreateTempSubdirectory();
        var tempFile = Path.Combine(tempFolder.FullName, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, content);
        return tempFile;
    }

    public static void CleanupTempFile(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
