// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestTypeScriptStarterProjectFactory(Func<DirectoryInfo, CancellationToken, string?, Task<bool>> buildAndGenerateSdkAsync) : IAppHostProjectFactory
{
    private readonly TestTypeScriptStarterProject _project = new(buildAndGenerateSdkAsync);

    public TestTypeScriptStarterProject Project => _project;

    public IAppHostProject GetProject(LanguageInfo language)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (!string.Equals(language.LanguageId, KnownLanguageId.TypeScript, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"No handler available for language '{language.LanguageId}'.");
        }

        return _project;
    }

    public IAppHostProject? TryGetProject(FileInfo appHostFile)
    {
        return IsTypeScriptAppHost(appHostFile) ? _project : null;
    }

    public IAppHostProject GetProject(FileInfo appHostFile)
    {
        return TryGetProject(appHostFile) ?? throw new NotSupportedException($"No handler available for AppHost file '{appHostFile.Name}'.");
    }

    internal static bool IsTypeScriptAppHost(FileInfo appHostFile)
    {
        return appHostFile.Name.Equals("apphost.mts", StringComparison.OrdinalIgnoreCase) ||
            appHostFile.Name.Equals("apphost.ts", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TestTypeScriptStarterProject(Func<DirectoryInfo, CancellationToken, string?, Task<bool>> buildAndGenerateSdkAsync) : IAppHostProject, IGuestAppHostSdkGenerator
{
    public bool IsUnsupported { get; set; }

    public string? LastPackageSourceOverride { get; private set; }

    public string LanguageId => KnownLanguageId.TypeScript;

    public string DisplayName => "TypeScript (Node.js)";

    public string? AppHostFileName => "apphost.mts";

    public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string[]>(["apphost.mts", "apphost.ts"]);
    }

    public bool CanHandle(FileInfo appHostFile)
    {
        return TestTypeScriptStarterProjectFactory.IsTypeScriptAppHost(appHostFile);
    }

    public bool IsUsingProjectReferences(FileInfo appHostFile)
    {
        return false;
    }

    public Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        return Task.FromResult(new AppHostValidationResult(IsValid: CanHandle(appHostFile)));
    }

    public Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(VersionHelper.GetDefaultTemplateVersion());
    }

    public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
    {
        return Task.FromResult(RunningInstanceResult.NoRunningInstance);
    }

    public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<(string PackageId, string Version)>> GetPackageReferencesAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<bool> BuildAndGenerateSdkAsync(DirectoryInfo directory, string? packageSourceOverride = null, CancellationToken cancellationToken = default)
    {
        LastPackageSourceOverride = packageSourceOverride;
        return buildAndGenerateSdkAsync(directory, cancellationToken, packageSourceOverride);
    }

}
