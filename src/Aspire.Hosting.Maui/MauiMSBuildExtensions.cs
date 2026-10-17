// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for injecting MSBuild properties into the build and launch of MAUI
/// platform resources.
/// </summary>
/// <remarks>
/// The supplied properties are written to a generated <c>.props</c> file that is imported into the
/// build and launch (mirroring how environment variables are emitted into a generated <c>.targets</c>
/// file for Android/iOS). Emitting values into an imported file keeps them off the process command line
/// and lets XML handle escaping, so values containing special characters need no command-line encoding.
/// </remarks>
public static partial class MauiMSBuildExtensions
{
    /// <summary>
    /// Adds an MSBuild property that is available when the MAUI platform resource is built.
    /// </summary>
    /// <typeparam name="T">The MAUI platform resource type.</typeparam>
    /// <param name="builder">The MAUI platform resource builder.</param>
    /// <param name="name">The MSBuild property name.</param>
    /// <param name="value">The MSBuild property value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The property is written into a generated <c>.props</c> file that is imported into both the build
    /// and the launch of the app, so it can influence build-time evaluation (for example switching
    /// authentication endpoints or feature flags) while staying consistent between the two steps.
    /// </para>
    /// <para>
    /// Calling this method again with the same <paramref name="name"/> replaces the previous value.
    /// </para>
    /// </remarks>
    /// <example>
    /// Provide a build-time authentication client id:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// maui.AddAndroidDevice()
    ///     .WithBuildProperty("AuthClientId", "00000000-0000-0000-0000-000000000000");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithBuildProperty<T>(
        this IResourceBuilder<T> builder,
        string name,
        string value) where T : IMauiPlatformResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        ValidatePropertyName(name);

        GetOrAddAnnotation(builder).BuildProperties[name] = value;
        return builder;
    }

    /// <summary>
    /// Adds an MSBuild property that is applied only when the MAUI platform resource is launched.
    /// </summary>
    /// <typeparam name="T">The MAUI platform resource type.</typeparam>
    /// <param name="builder">The MAUI platform resource builder.</param>
    /// <param name="name">The MSBuild property name.</param>
    /// <param name="value">The MSBuild property value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The property is written into a generated <c>.props</c> file that is imported into the launch command
    /// only (the <c>Run</c> target). Use this for values that only matter while running the app and that
    /// should not participate in the build.
    /// </para>
    /// <para>
    /// Calling this method again with the same <paramref name="name"/> replaces the previous value.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithRunProperty<T>(
        this IResourceBuilder<T> builder,
        string name,
        string value) where T : IMauiPlatformResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);
        ValidatePropertyName(name);

        GetOrAddAnnotation(builder).RunProperties[name] = value;
        return builder;
    }

    private static MauiMSBuildPropertiesAnnotation GetOrAddAnnotation<T>(IResourceBuilder<T> builder)
        where T : IMauiPlatformResource
    {
        if (!builder.Resource.TryGetLastAnnotation<MauiMSBuildPropertiesAnnotation>(out var annotation))
        {
            annotation = new MauiMSBuildPropertiesAnnotation();
            builder.WithAnnotation(annotation);
        }

        return annotation;
    }

    // MSBuild reserves these property names to describe the project and the MSBuild binaries; setting
    // one would either be silently ignored or fail the build, so reject them at configuration time.
    // https://learn.microsoft.com/visualstudio/msbuild/msbuild-reserved-and-well-known-properties
    private static readonly HashSet<string> s_reservedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSBuildAssemblyVersion",
        "MSBuildBinPath",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildFileVersion",
        "MSBuildInteractive",
        "MSBuildLastTaskResult",
        "MSBuildNodeCount",
        "MSBuildOverrideTasksPath",
        "MSBuildProgramFiles32",
        "MSBuildProjectDefaultTargets",
        "MSBuildProjectDirectory",
        "MSBuildProjectDirectoryNoRoot",
        "MSBuildProjectExtension",
        "MSBuildProjectFile",
        "MSBuildProjectFullPath",
        "MSBuildProjectName",
        "MSBuildRuntimeType",
        "MSBuildSemanticVersion",
        "MSBuildStartupDirectory",
        "MSBuildThisFile",
        "MSBuildThisFileDirectory",
        "MSBuildThisFileDirectoryNoRoot",
        "MSBuildThisFileExtension",
        "MSBuildThisFileFullPath",
        "MSBuildThisFileName",
        "MSBuildToolsPath",
        "MSBuildToolsPath32",
        "MSBuildToolsPath64",
        "MSBuildToolsVersion",
        "MSBuildUserExtensionsPath",
        "MSBuildVersion",
    };

    private static void ValidatePropertyName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The name becomes an XML element in the generated .props file, so reject values that are not
        // valid MSBuild property names (which must start with a letter or underscore and otherwise
        // contain only letters, digits, or underscores) to fail fast at configuration time.
        // https://learn.microsoft.com/visualstudio/msbuild/msbuild-properties#create-and-use-a-property
        if (!MSBuildPropertyNameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid MSBuild property name. Property names must start with a letter or underscore and contain only letters, digits, or underscores.",
                nameof(name));
        }

        if (s_reservedPropertyNames.Contains(name))
        {
            throw new ArgumentException(
                $"'{name}' is a reserved MSBuild property name and cannot be set.",
                nameof(name));
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex MSBuildPropertyNameRegex();
}
