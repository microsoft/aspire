// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Projects;

internal interface ISolutionLocator
{
    Task<FileInfo?> FindSolutionFileAsync(DirectoryInfo startDirectory, CancellationToken cancellationToken = default);
}

internal sealed class SolutionLocator(ILogger<SolutionLocator> logger, IInteractionService interactionService) : ISolutionLocator
{
    public async Task<FileInfo?> FindSolutionFileAsync(DirectoryInfo startDirectory, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Searching for solution files starting from {StartDirectory}", startDirectory.FullName);

        var solutionFiles = await GetSolutionFilesInDirectoryAndSubfoldersAsync(startDirectory, cancellationToken);
        
        if (!solutionFiles.Any())
        {
            logger.LogDebug("No solution files found in {Directory} or subdirectories", startDirectory.FullName);
            return null;
        }

        logger.LogDebug("Found {Count} solution file(s) in {Directory}", solutionFiles.Count, startDirectory.FullName);
        
        if (solutionFiles.Count == 1)
        {
            // Single solution found, use it
            return solutionFiles[0];
        }
        else
        {
            // Multiple solutions found, prompt user to choose
            var selectedSolution = await interactionService.PromptForSelectionAsync(
                InitCommandStrings.MultipleSolutionsFound,
                solutionFiles,
                solutionFile => $"{solutionFile.Name.EscapeMarkup()} ({Path.GetRelativePath(startDirectory.FullName, solutionFile.FullName).EscapeMarkup()})",
                cancellationToken);
            
            return selectedSolution;
        }
    }

    private static async Task<List<FileInfo>> GetSolutionFilesInDirectoryAndSubfoldersAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        // Search for .sln and .slnx files in parallel
        var slnTask = Task.Run(() => 
        {
            try
            {
                return directory.GetFiles("*.sln", SearchOption.AllDirectories).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we don't have access to
                return new List<FileInfo>();
            }
        }, cancellationToken);

        var slnxTask = Task.Run(() => 
        {
            try
            {
                return directory.GetFiles("*.slnx", SearchOption.AllDirectories).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we don't have access to
                return new List<FileInfo>();
            }
        }, cancellationToken);

        await Task.WhenAll(slnTask, slnxTask);

        var solutionFiles = new List<FileInfo>();
        solutionFiles.AddRange(slnTask.Result);
        solutionFiles.AddRange(slnxTask.Result);

        // Sort by directory depth (closest first) then by name
        return solutionFiles
            .OrderBy(f => f.Directory?.FullName.Count(c => c == Path.DirectorySeparatorChar) ?? 0)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}