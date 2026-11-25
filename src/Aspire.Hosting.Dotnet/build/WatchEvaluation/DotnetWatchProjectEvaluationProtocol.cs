// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>One physical project and its SDK-evaluated build context.</summary>
internal sealed record DotnetWatchProjectNode(
    string Path,
    IReadOnlyDictionary<string, string> Properties,
    string[] References,
    string[] Imports,
    string[] Outputs);

/// <summary>A graph evaluated using one selected SDK and its dotnet host.</summary>
internal sealed record DotnetWatchProjectGraphResult(
    string SdkDirectory,
    string Muxer,
    string SdkVersion,
    IReadOnlyDictionary<string, DotnetWatchProjectNode> Projects);

/// <summary>Evaluation input shared by the hosting library and its isolated SDK adapter.</summary>
internal sealed record DotnetWatchProjectEvaluationRequest(
    int Version,
    string ProjectPath,
    string? Configuration,
    IReadOnlyDictionary<string, string> Environment,
    bool GlobalProperties,
    string SdkDirectory,
    string Muxer,
    string SdkVersion);

/// <summary>Evaluation result or a diagnosed unsupported context, never a partial graph.</summary>
internal sealed record DotnetWatchProjectEvaluationResponse(
    int Version,
    DotnetWatchProjectGraphResult? Graph,
    string? Error);

/// <summary>An evaluated compile item, including the SDK's physical path.</summary>
internal sealed record DotnetWatchCompileItem(string Identity, string FullPath);

/// <summary>An evaluated package reference and its explicit or central version inputs, not restored assets.</summary>
internal sealed record DotnetWatchPackageReference(
    string Identity, string Version, string VersionOverride, string[] CentralVersions);

/// <summary>Limits and SDK properties used by both sides of the evaluation transport.</summary>
internal static class DotnetWatchProjectEvaluationProtocol
{
    internal const int Version = 1;
    internal const string AssemblyFileName = "Aspire.DotnetWatch.Evaluation.dll";
    internal const string SdkQueryFileName = "Aspire.DotnetWatch.Evaluation.proj";
    internal const int MaxRequestLength = 4 * 1024 * 1024;
    internal const int MaxResponseLength = 16 * 1024 * 1024;
    internal const int MaxProjects = 4096;

    internal static IReadOnlyList<string> PropertyNames { get; } = Array.AsReadOnly(
    [
        "Configuration", "Platform", "TargetFramework", "TargetFrameworks",
        "TargetFrameworkIdentifier", "TargetFrameworkVersion", "TargetPlatformIdentifier", "TargetPlatformVersion",
        "OutputType", "TargetPath", "UseAppHost", "OutputPath", "OutDir", "BaseOutputPath",
        "BaseIntermediateOutputPath", "IntermediateOutputPath", "MSBuildProjectExtensionsPath",
        "RunCommand", "RunArguments", "RunWorkingDirectory", "Optimize", "DebugSymbols",
        "DebugType", "DefineConstants", "LangVersion", "Nullable", "AllowUnsafeBlocks", "CheckForOverflowUnderflow",
        "AssemblyName", "TargetName", "Deterministic", "EnablePreviewFeatures",
        "StartupHookSupport", "MetadataUpdaterSupport", "PublishAot", "PublishTrimmed", "WebPublishProfileFile",
        "RuntimeIdentifier", "RuntimeIdentifiers", "SelfContained", "ArtifactsPath", "UseArtifactsOutput",
        "MSBuildProjectFullPath", "MSBuildProjectDirectory", "MSBuildStartupDirectory", "MSBuildToolsPath", "DOTNET_HOST_PATH",
        "FileBasedProgram", "EntryPointFilePath"
    ]);

    internal static IReadOnlyList<string> OutputPropertyNames { get; } = Array.AsReadOnly(
    [
        "OutputPath", "OutDir", "BaseIntermediateOutputPath", "IntermediateOutputPath",
        "MSBuildProjectExtensionsPath", "TargetPath"
    ]);
}

[JsonSerializable(typeof(DotnetWatchProjectEvaluationRequest))]
[JsonSerializable(typeof(DotnetWatchProjectEvaluationResponse))]
[JsonSerializable(typeof(DotnetWatchCompileItem[]))]
[JsonSerializable(typeof(DotnetWatchPackageReference[]))]
internal sealed partial class DotnetWatchProjectEvaluationJsonContext : JsonSerializerContext;
