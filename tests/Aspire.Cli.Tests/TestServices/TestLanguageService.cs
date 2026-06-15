// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestLanguageService : ILanguageService
{
    public Func<CancellationToken, Task<IAppHostProject?>>? GetConfiguredProjectAsyncCallback { get; set; }
    public Func<IAppHostProject, bool, CancellationToken, Task>? SetLanguageAsyncCallback { get; set; }
    public Func<CancellationToken, Task<IAppHostProject>>? PromptForProjectAsyncCallback { get; set; }
    public Func<string?, bool, CancellationToken, Task<IAppHostProject>>? GetOrPromptForProjectAsyncCallback { get; set; }
    public Func<string?, bool, CancellationToken, Task<AppHostProjectSelection>>? GetOrPromptForProjectSelectionAsyncCallback { get; set; }

    /// <summary>
    /// The default project to return when no callback is set.
    /// </summary>
    public IAppHostProject? DefaultProject { get; set; }

    public Task<IAppHostProject?> GetConfiguredProjectAsync(CancellationToken cancellationToken = default)
    {
        return GetConfiguredProjectAsyncCallback is not null
            ? GetConfiguredProjectAsyncCallback(cancellationToken)
            : Task.FromResult(DefaultProject);
    }

    public Task SetLanguageAsync(IAppHostProject project, bool isGlobal = false, CancellationToken cancellationToken = default)
    {
        return SetLanguageAsyncCallback is not null
            ? SetLanguageAsyncCallback(project, isGlobal, cancellationToken)
            : Task.CompletedTask;
    }

    public Task<IAppHostProject> PromptForProjectAsync(CancellationToken cancellationToken = default)
    {
        if (PromptForProjectAsyncCallback is not null)
        {
            return PromptForProjectAsyncCallback(cancellationToken);
        }

        if (DefaultProject is null)
        {
            throw new InvalidOperationException("No default project set and no callback provided");
        }

        return Task.FromResult(DefaultProject);
    }

    public Task<IAppHostProject> GetOrPromptForProjectAsync(string? explicitLanguageId = null, bool saveLanguageSelection = true, CancellationToken cancellationToken = default)
    {
        if (GetOrPromptForProjectAsyncCallback is not null)
        {
            return GetOrPromptForProjectAsyncCallback(explicitLanguageId, saveLanguageSelection, cancellationToken);
        }

        if (DefaultProject is null)
        {
            throw new InvalidOperationException("No default project set and no callback provided");
        }

        return Task.FromResult(DefaultProject);
    }

    public async Task<AppHostProjectSelection> GetOrPromptForProjectSelectionAsync(string? explicitLanguageId = null, bool saveLanguageSelection = true, CancellationToken cancellationToken = default)
    {
        if (GetOrPromptForProjectSelectionAsyncCallback is not null)
        {
            return await GetOrPromptForProjectSelectionAsyncCallback(explicitLanguageId, saveLanguageSelection, cancellationToken);
        }

        var project = await GetOrPromptForProjectAsync(explicitLanguageId, saveLanguageSelection, cancellationToken);
        return new AppHostProjectSelection(project, ShouldPersistSelection: false);
    }
}
