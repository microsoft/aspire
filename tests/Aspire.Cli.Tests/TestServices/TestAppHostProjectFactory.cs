// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

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
        if (languageId.Equals("csharp", StringComparison.OrdinalIgnoreCase))
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
        public string LanguageId => "csharp";
        public string DisplayName => "C# (.NET)";
        public string? AppHostFileName => "AppHost.csproj";

        public bool IsUsingProjectReferences(FileInfo appHostFile)
        {
            return false;
        }

        public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken)
            => Task.FromResult(s_detectionPatterns);

        public bool CanHandle(FileInfo appHostFile)
        {
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
            => throw new NotImplementedException();

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

        public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
            => throw new NotImplementedException();

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
