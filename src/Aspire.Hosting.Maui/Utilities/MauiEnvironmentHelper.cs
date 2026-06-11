// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Provides utilities for handling environment variables in MAUI projects.
/// </summary>
/// <remarks>
/// Some MAUI platforms (like Android and iOS) require environment variables to be passed via
/// an intermediate MSBuild targets file rather than directly through the process environment.
/// This class provides reusable infrastructure for generating these targets files.
/// </remarks>
internal static class MauiEnvironmentHelper
{
    /// <summary>
    /// Creates an MSBuild targets file for Android that sets environment variables.
    /// </summary>
    /// <param name="fileSystemService">The file system service for managing temp files.</param>
    /// <param name="resource">The resource to collect environment variables from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path to the generated targets file, or null if no environment variables are present.</returns>
    public static async Task<string?> CreateAndroidEnvironmentTargetsFileAsync(
        IFileSystemService fileSystemService,
        IResource resource,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var environmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var encodedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, logger, cancellationToken)
            .ConfigureAwait(false);

        // Normalize all the environment variables for the resource
        foreach (var envVar in executionConfiguration.EnvironmentVariables)
        {
            var normalizedKey = envVar.Key.ToUpperInvariant();
            var encodedValue = EncodeSemicolons(envVar.Value, out var wasEncoded);

            environmentVariables[normalizedKey] = encodedValue;

            if (wasEncoded)
            {
                encodedKeys.Add(normalizedKey);
            }
        }

        // If no environment variables, return null
        if (environmentVariables.Count == 0)
        {
            return null;
        }

        // Create a temporary targets file
        var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-maui-android-env").Path;

        // Prune old targets files
        PruneOldTargets(tempDirectory, logger);

        var sanitizedName = SanitizeFileName(resource.Name + "-android");
        var uniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var targetsFilePath = Path.Combine(tempDirectory, $"{sanitizedName}-{uniqueId}.targets");

        // Generate the targets file content
        var targetsContent = GenerateAndroidTargetsFileContent(environmentVariables);

        // Write the file
        await File.WriteAllTextAsync(targetsFilePath, targetsContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return targetsFilePath;
    }

    /// <summary>
    /// Generates the content of an MSBuild targets file for Android environment variables.
    /// </summary>
    private static string GenerateAndroidTargetsFileContent(Dictionary<string, string> environmentVariables)
    {
        var projectElement = new XElement("Project");

        // Import the standard Custom.After.Microsoft.Common.targets if it exists
        projectElement.Add(new XElement(
            "Import",
            new XAttribute("Project", "$(MSBuildExtensionsPath)/v$(MSBuildToolsVersion)/Custom.After.Microsoft.Common.targets"),
            new XAttribute("Condition", "Exists('$(MSBuildExtensionsPath)/v$(MSBuildToolsVersion)/Custom.After.Microsoft.Common.targets')")
        ));

        // Create an ItemGroup for AndroidEnvironment files to be generated
        var itemGroup = new XElement("ItemGroup");
        foreach (var (key, value) in environmentVariables.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            itemGroup.Add(new XElement("_GeneratedAndroidEnvironment", new XAttribute("Include", $"{key}={value}")));
        }
        projectElement.Add(itemGroup);

        // Add target to generate environment file(s)
        var targetElement = new XElement(
            "Target",
            new XAttribute("Name", "AspireGenerateAndroidEnvironmentFiles"),
            new XAttribute("BeforeTargets", "_GenerateEnvironmentFiles"),
            new XAttribute("Condition", "'@(_GeneratedAndroidEnvironment)' != ''")
        );

        // Write environment variables to a temporary file in IntermediateOutputPath
        targetElement.Add(new XElement(
            "WriteLinesToFile",
            new XAttribute("File", "$(IntermediateOutputPath)__aspire_environment__.txt"),
            new XAttribute("Lines", "@(_GeneratedAndroidEnvironment)"),
            new XAttribute("Overwrite", "True"),
            new XAttribute("WriteOnlyWhenDifferent", "True")
        ));

        // Add the file to AndroidEnvironment items
        targetElement.Add(new XElement(
            "ItemGroup",
            new XElement("AndroidEnvironment", new XAttribute("Include", "$(IntermediateOutputPath)__aspire_environment__.txt"))
        ));

        // Add the file to FileWrites for clean
        targetElement.Add(new XElement(
            "ItemGroup",
            new XElement("FileWrites", new XAttribute("Include", "$(IntermediateOutputPath)__aspire_environment__.txt"))
        ));

        // Force the GeneratePackageManagerJava target to re-run by deleting its stamp file
        targetElement.Add(new XElement(
            "Delete",
            new XAttribute("Files", "$(_AndroidStampDirectory)_GeneratePackageManagerJava.stamp")
        ));

        projectElement.Add(targetElement);

        var document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), projectElement);

        using var stringWriter = new StringWriter();
        document.Save(stringWriter);
        return stringWriter.ToString();
    }

    private static void PruneOldTargets(string directory, ILogger logger)
    {
        var expiration = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        var deletedFiles = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.targets", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.LastWriteTimeUtc < expiration)
                {
                    info.Delete();
                    deletedFiles.Add(info.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to prune stale Android environment targets file '{TargetsFile}'.", file);
            }
        }

        if (deletedFiles.Count > 0)
        {
            logger.LogDebug("Pruned {Count} stale Android environment targets file(s): {Files}", deletedFiles.Count, string.Join(", ", deletedFiles));
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalidCharacters) < 0)
        {
            return name;
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalidCharacters, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static string EncodeSemicolons(string value, out bool wasEncoded)
    {
        wasEncoded = value.Contains(';', StringComparison.Ordinal);
        if (!wasEncoded)
        {
            return value;
        }

        return value.Replace(";", "%3B", StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates an MSBuild targets file for iOS that sets environment variables.
    /// </summary>
    /// <param name="fileSystemService">The file system service for managing temp files.</param>
    /// <param name="resource">The resource to collect environment variables from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path to the generated targets file, or null if no environment variables are present.</returns>
    public static async Task<string?> CreateiOSEnvironmentTargetsFileAsync(
        IFileSystemService fileSystemService,
        IResource resource,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, logger, cancellationToken)
            .ConfigureAwait(false);

        // If no environment variables, return null
        if (!executionConfiguration.EnvironmentVariables.Any())
        {
            return null;
        }

        // Create a temporary targets file
        var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-maui-mlaunch-env").Path;

        // Prune old targets files
        PruneOldTargetsiOS(tempDirectory, logger);

        var sanitizedName = SanitizeFileName(resource.Name + "-ios");
        var uniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var targetsFilePath = Path.Combine(tempDirectory, $"{sanitizedName}-{uniqueId}.targets");

        // Generate the targets file content
        var targetsContent = GenerateiOSTargetsFileContent(executionConfiguration.EnvironmentVariables.ToDictionary());

        // Write the file
        await File.WriteAllTextAsync(targetsFilePath, targetsContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return targetsFilePath;
    }

    /// <summary>
    /// Generates the content of an MSBuild targets file for iOS environment variables.
    /// </summary>
    private static string GenerateiOSTargetsFileContent(Dictionary<string, string> environmentVariables)
    {
        var projectElement = new XElement("Project");

        // Import the standard Custom.After.Microsoft.Common.targets if it exists
        projectElement.Add(new XElement(
            "Import",
            new XAttribute("Project", "$(MSBuildExtensionsPath)/v$(MSBuildToolsVersion)/Custom.After.Microsoft.Common.targets"),
            new XAttribute("Condition", "Exists('$(MSBuildExtensionsPath)/v$(MSBuildToolsVersion)/Custom.After.Microsoft.Common.targets')")
        ));

        // Create an ItemGroup to add environment variables using MlaunchEnvironmentVariables
        // iOS apps need environment variables passed to mlaunch as KEY=VALUE pairs
        var itemGroup = new XElement("ItemGroup");

        foreach (var (key, value) in environmentVariables.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            // Encode semicolons as %3B to prevent MSBuild from treating them as item separators
            var encodedValue = value.Replace(";", "%3B", StringComparison.Ordinal);

            // Add as MlaunchEnvironmentVariables item with Include="KEY=VALUE"
            itemGroup.Add(new XElement("MlaunchEnvironmentVariables",
                new XAttribute("Include", $"{key}={encodedValue}")));
        }

        projectElement.Add(itemGroup);

        // Add a diagnostic message target to show what's being forwarded
        projectElement.Add(new XElement(
            "Target",
            new XAttribute("Name", "AspireLogMlaunchEnvironmentVariables"),
            new XAttribute("AfterTargets", "PrepareForBuild"),
            new XAttribute("Condition", "'@(MlaunchEnvironmentVariables)' != ''"),
            new XElement(
                "Message",
                new XAttribute("Importance", "High"),
                new XAttribute("Text", "Aspire forwarding mlaunch environment variables: @(MlaunchEnvironmentVariables, ', ')")
            )
        ));

        var document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), projectElement);

        using var stringWriter = new StringWriter();
        document.Save(stringWriter);
        return stringWriter.ToString();
    }

    private static void PruneOldTargetsiOS(string directory, ILogger logger)
    {
        var expiration = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);
        var deletedFiles = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.targets", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.LastWriteTimeUtc < expiration)
                {
                    info.Delete();
                    deletedFiles.Add(info.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to prune stale iOS environment targets file '{TargetsFile}'.", file);
            }
        }

        if (deletedFiles.Count > 0)
        {
            logger.LogDebug("Pruned {Count} stale iOS environment targets file(s): {Files}", deletedFiles.Count, string.Join(", ", deletedFiles));
        }
    }
}
