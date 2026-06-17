// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// JSON-RPC service for language-specific scaffolding, detection, and runtime configuration.
/// </summary>
internal sealed class LanguageService
{
    private const string ScaffoldAppHostMethodName = "scaffoldAppHost";
    private const string DetectAppHostTypeMethodName = "detectAppHostType";
    private const string GetRuntimeSpecMethodName = "getRuntimeSpec";

    private readonly JsonRpcAuthenticationState _authenticationState;
    private readonly LanguageSupportResolver _resolver;
    private readonly ILogger<LanguageService> _logger;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry;

    public LanguageService(
        JsonRpcAuthenticationState authenticationState,
        LanguageSupportResolver resolver,
        ILogger<LanguageService> logger,
        RemoteHostProfilingTelemetry profilingTelemetry)
    {
        _authenticationState = authenticationState;
        _resolver = resolver;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
    }

    /// <summary>
    /// Scaffolds a new AppHost project for the specified language.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <param name="targetPath">The target directory path for the project.</param>
    /// <param name="projectName">Optional project name. If null, derived from directory name.</param>
    /// <returns>A dictionary of relative file paths to file contents.</returns>
    [JsonRpcMethod(ScaffoldAppHostMethodName)]
    public Dictionary<string, string> ScaffoldAppHost(string language, string targetPath, string? projectName = null)
    {
        using var rpcActivity = _profilingTelemetry.StartJsonRpcServerCall(ScaffoldAppHostMethodName);
        using var activity = _profilingTelemetry.StartLanguageScaffold(language);
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug(">> scaffoldAppHost({Language}, {TargetPath}, {ProjectName})", language, targetPath, projectName);
            var sw = Stopwatch.StartNew();
            var languageSupport = _resolver.GetLanguageSupport(language);
            if (languageSupport == null)
            {
                throw new ArgumentException(BuildNoLanguageSupportMessage(language));
            }

            var request = new ScaffoldRequest
            {
                TargetPath = targetPath,
                ProjectName = projectName
            };

            var files = languageSupport.Scaffold(request);
            activity.SetFileCount(files.Count);

            _logger.LogDebug("<< scaffoldAppHost({Language}) completed in {ElapsedMs}ms, generated {FileCount} files", language, sw.ElapsedMilliseconds, files.Count);
            return files;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "<< scaffoldAppHost({Language}) failed", language);
            throw;
        }
    }

    /// <summary>
    /// Detects the language of an AppHost in the specified directory.
    /// </summary>
    /// <param name="directoryPath">The directory to check.</param>
    /// <returns>Detection result with language and file information.</returns>
    [JsonRpcMethod(DetectAppHostTypeMethodName)]
    public DetectionResult DetectAppHostType(string directoryPath)
    {
        using var rpcActivity = _profilingTelemetry.StartJsonRpcServerCall(DetectAppHostTypeMethodName);
        using var activity = _profilingTelemetry.StartLanguageDetect();
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug(">> detectAppHostType({DirectoryPath})", directoryPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var languageSupport in _resolver.GetAllLanguages())
            {
                var result = languageSupport.Detect(directoryPath);
                if (result.IsValid)
                {
                    activity.SetLanguage(result.Language);
                    activity.SetDetectionMatched(true);
                    _logger.LogDebug("<< detectAppHostType({DirectoryPath}) found {Language} in {ElapsedMs}ms", directoryPath, result.Language, sw.ElapsedMilliseconds);
                    return result;
                }
            }

            _logger.LogDebug("<< detectAppHostType({DirectoryPath}) not found in {ElapsedMs}ms", directoryPath, sw.ElapsedMilliseconds);
            activity.SetDetectionMatched(false);
            return DetectionResult.NotFound;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "<< detectAppHostType({DirectoryPath}) failed", directoryPath);
            throw;
        }
    }

    /// <summary>
    /// Gets the runtime execution specification for the specified language.
    /// </summary>
    /// <param name="language">The target language (e.g., "TypeScript", "Python").</param>
    /// <returns>The runtime spec containing commands for execution.</returns>
    [JsonRpcMethod(GetRuntimeSpecMethodName)]
    public RuntimeSpec GetRuntimeSpec(string language)
    {
        using var rpcActivity = _profilingTelemetry.StartJsonRpcServerCall(GetRuntimeSpecMethodName);
        using var activity = _profilingTelemetry.StartLanguageGetRuntimeSpec(language);
        try
        {
            _authenticationState.ThrowIfNotAuthenticated();
            _logger.LogDebug(">> getRuntimeSpec({Language})", language);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var languageSupport = _resolver.GetLanguageSupport(language);
            if (languageSupport == null)
            {
                throw new ArgumentException(BuildNoLanguageSupportMessage(language));
            }

            var spec = languageSupport.GetRuntimeSpec();

            _logger.LogDebug("<< getRuntimeSpec({Language}) completed in {ElapsedMs}ms", language, sw.ElapsedMilliseconds);
            return spec;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            _logger.LogError(ex, "<< getRuntimeSpec({Language}) failed", language);
            throw;
        }
    }

    private string BuildNoLanguageSupportMessage(string language)
    {
        var available = _resolver.GetAllLanguages()
            .Select(l => l.Language)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (available.Length == 0)
        {
            // No language support discovered at all is almost always a binary-mismatch /
            // type-load failure (see LanguageSupportResolver warnings). Point the user at
            // the apphost server log so they can see the underlying LoaderExceptions.
            return $"No language support found for: {language}. " +
                   "No language support implementations were discovered in any loaded assembly. " +
                   "This usually indicates a binary mismatch between the bundled apphost server and the integration assemblies on disk; " +
                   "check the apphost server log for 'LoaderExceptions' Warnings.";
        }
        return $"No language support found for: {language}. Available languages: {string.Join(", ", available)}.";
    }
}
