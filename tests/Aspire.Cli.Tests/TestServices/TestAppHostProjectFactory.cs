// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test implementation of IAppHostProjectFactory that simulates .NET project detection.
/// </summary>
internal sealed class TestAppHostProjectFactory : IAppHostProjectFactory
{
    private static readonly HashSet<string> s_projectExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".fsproj", ".vbproj" };
    private readonly TestAppHostProject _testProject;

    /// <summary>
    /// Optional callback to control validation behavior. If not set, all valid project files are considered valid AppHosts.
    /// </summary>
    public Func<FileInfo, AppHostValidationResult>? ValidateAppHostCallback { get; set; }

    /// <summary>
    /// Optional callback for tests that need this factory to handle non-.NET AppHost file names.
    /// </summary>
    public Func<FileInfo, bool>? CanHandleCallback { get; set; }

    /// <summary>
    /// Optional callback to control AppHost version resolution behavior.
    /// </summary>
    public Func<FileInfo, CancellationToken, Task<string?>>? GetAspireHostingVersionAsyncCallback { get; set; }

    /// <summary>
    /// Optional async callback to control validation behavior.
    /// </summary>
    public Func<FileInfo, CancellationToken, Task<AppHostValidationResult>>? ValidateAppHostAsyncCallback { get; set; }

    public Func<AppHostProjectContext, CancellationToken, Task<int>>? RunAsyncCallback { get; set; }

    public Func<UpdatePackagesContext, CancellationToken, Task<UpdatePackagesResult>>? UpdatePackagesAsyncCallback { get; set; }

    public string LanguageId { get; set; } = "csharp";

    public string DisplayName { get; set; } = "C# (.NET)";

    /// <summary>
    /// Optional detection patterns to advertise from the test project.
    /// </summary>
    public string[]? DetectionPatterns { get; set; }

    public TestAppHostProjectFactory()
    {
        _testProject = new TestAppHostProject(this);
    }

    public IAppHostProject GetProject(LanguageInfo language)
    {
        // For tests, always return the test project regardless of language
        return _testProject;
    }

    public IAppHostProject GetProject(FileInfo appHostFile)
    {
        return TryGetProject(appHostFile) ?? throw new NotSupportedException($"No handler available for AppHost file '{appHostFile.Name}'.");
    }

    public IAppHostProject? TryGetProject(FileInfo appHostFile)
    {
        if (CanHandleCallback?.Invoke(appHostFile) == true)
        {
            return _testProject;
        }

        // Handle .csproj, .fsproj, .vbproj files
        if (s_projectExtensions.Contains(appHostFile.Extension))
        {
            return _testProject;
        }

        // Handle apphost.cs single-file apphosts
        if (appHostFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase))
        {
            // Check for #:sdk Aspire.AppHost.Sdk directive
            if (IsValidSingleFileAppHost(appHostFile))
            {
                return _testProject;
            }
        }

        return null;
    }

    public IAppHostProject? GetProjectByLanguageId(string languageId)
    {
        if (languageId.Equals(LanguageId, StringComparison.OrdinalIgnoreCase))
        {
            return _testProject;
        }
        return null;
    }

    public IEnumerable<IAppHostProject> GetAllProjects()
    {
        return [_testProject];
    }

    private static bool IsValidSingleFileAppHost(FileInfo candidateFile)
    {
        // Check no sibling .csproj files exist
        var siblingCsprojFiles = candidateFile.Directory!.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly);
        if (siblingCsprojFiles.Any())
        {
            return false;
        }

        // Check for #:sdk Aspire.AppHost.Sdk directive
        try
        {
            using var reader = candidateFile.OpenText();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmedLine = line.TrimStart();
                if (trimmedLine.StartsWith("#:sdk Aspire.AppHost.Sdk", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Minimal test implementation of IAppHostProject for .NET projects.
    /// </summary>
    private sealed class TestAppHostProject : IAppHostProject
    {
        private static readonly string[] s_detectionPatterns = ["*.csproj", "*.fsproj", "*.vbproj", "apphost.cs"];
        private readonly TestAppHostProjectFactory _factory;

        public TestAppHostProject(TestAppHostProjectFactory factory)
        {
            _factory = factory;
        }

        public bool IsUnsupported { get; set; }
        public string LanguageId => _factory.LanguageId;
        public string DisplayName => _factory.DisplayName;
        public string? AppHostFileName => "AppHost.csproj";

        public bool IsUsingProjectReferences(FileInfo appHostFile)
        {
            return false;
        }

        public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_factory.DetectionPatterns ?? s_detectionPatterns);

        public bool CanHandle(FileInfo appHostFile)
        {
            if (_factory.CanHandleCallback?.Invoke(appHostFile) == true)
            {
                return true;
            }

            var extension = appHostFile.Extension.ToLowerInvariant();
            if (extension is ".csproj" or ".fsproj" or ".vbproj")
            {
                return true;
            }
            if (appHostFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase))
            {
                return IsValidSingleFileAppHost(appHostFile);
            }
            return false;
        }

        public Task ScaffoldAsync(DirectoryInfo directory, string? projectName, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
            => _factory.RunAsyncCallback is not null
                ? _factory.RunAsyncCallback(context, cancellationToken)
                : throw new NotImplementedException();

        public Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<(string PackageId, string Version)>> GetPackageReferencesAsync(FileInfo appHostFile, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
        {
            if (IsUnsupported)
            {
                return Task.FromResult(new AppHostValidationResult(IsValid: false, IsUnsupported: true));
            }

            if (_factory.ValidateAppHostAsyncCallback is not null)
            {
                return _factory.ValidateAppHostAsyncCallback(appHostFile, cancellationToken);
            }

            if (_factory.ValidateAppHostCallback is not null)
            {
                return Task.FromResult(_factory.ValidateAppHostCallback(appHostFile));
            }

            // Match production behavior: for .cs files, validate as single-file apphost
            if (appHostFile.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AppHostValidationResult(IsValid: IsValidSingleFileAppHost(appHostFile)));
            }

            return Task.FromResult(new AppHostValidationResult(IsValid: true));
        }

        public Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
        {
            return _factory.GetAspireHostingVersionAsyncCallback is not null
                ? _factory.GetAspireHostingVersionAsyncCallback(appHostFile, cancellationToken)
                : Task.FromResult<string?>(VersionHelper.GetDefaultTemplateVersion());
        }

        public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
            => _factory.UpdatePackagesAsyncCallback is not null
                ? _factory.UpdatePackagesAsyncCallback(context, cancellationToken)
                : throw new NotImplementedException();

        public Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
            => Task.FromResult(RunningInstanceResult.NoRunningInstance);

        public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        private static bool IsValidSingleFileAppHost(FileInfo candidateFile)
        {
            var siblingCsprojFiles = candidateFile.Directory!.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly);
            if (siblingCsprojFiles.Any())
            {
                return false;
            }

            try
            {
                using var reader = candidateFile.OpenText();
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var trimmedLine = line.TrimStart();
                    if (trimmedLine.StartsWith("#:sdk Aspire.AppHost.Sdk", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }
    }
}
