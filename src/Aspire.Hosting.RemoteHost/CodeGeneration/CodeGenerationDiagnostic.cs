// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.CodeGeneration;

/// <summary>
/// JSON-RPC error codes used by the AppHost server for code-generation failures.
/// </summary>
/// <remarks>
/// Values are within the JSON-RPC reserved server-error range (<c>-32000</c> to <c>-32099</c>).
/// </remarks>
internal static class CodeGenerationErrorCodes
{
    /// <summary>
    /// The AppHost server failed to load or JIT-compile reflection-based code generation
    /// metadata. Typically caused by an assembly-version mismatch between the bundled
    /// <c>Aspire.Hosting</c> runtime and the user-restored integration assemblies.
    /// </summary>
    public const int IncompatibleAspireSdk = -32050;
}

/// <summary>
/// Structured payload describing a reflection-load failure encountered while servicing a
/// code-generation RPC method.
/// </summary>
/// <remarks>
/// Carried as <see cref="LocalRpcException.ErrorData"/> from the server to the CLI so that the
/// CLI can render an actionable diagnostic. The shape is intentionally flat and JSON-serializable
/// so it survives the StreamJsonRpc <c>SystemTextJsonFormatter</c> round-trip without requiring
/// shared types between the server and the CLI.
/// </remarks>
internal sealed class CodeGenerationDiagnostic
{
    /// <summary>
    /// Gets the CLR type name of the original exception (e.g. <c>System.TypeLoadException</c>).
    /// </summary>
    public string OriginalExceptionType { get; init; } = "";

    /// <summary>
    /// Gets the name of the type that failed to load, if known. Populated from
    /// <see cref="TypeLoadException.TypeName"/> when available.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Gets the name of the missing member, if the failure was a
    /// <see cref="MissingMethodException"/> or <see cref="MissingFieldException"/>.
    /// </summary>
    public string? MemberName { get; init; }

    /// <summary>
    /// Gets the <see cref="AssemblyInformationalVersionAttribute"/> value of the bundled
    /// <c>Aspire.Hosting</c> assembly on the server side, if it could be discovered.
    /// </summary>
    public string? RuntimeAspireHostingVersion { get; init; }

    /// <summary>
    /// Gets the on-disk location of the bundled <c>Aspire.Hosting</c> assembly, if it could be
    /// discovered.
    /// </summary>
    public string? RuntimeAspireHostingPath { get; init; }

    /// <summary>
    /// Gets the loaded <c>Aspire.Hosting*</c> integration assemblies that were probed by the
    /// AppHost server at the time of the failure.
    /// </summary>
    public List<CodeGenerationLoadedAssemblyInfo> LoadedAssemblies { get; init; } = [];

    /// <summary>
    /// Gets a short, language-agnostic remediation hint suitable for surfacing to AppHost
    /// authors (e.g. instructing them to run <c>aspire update</c>).
    /// </summary>
    public string? RemediationHint { get; init; }
}

/// <summary>
/// Identity information for a single loaded assembly captured at the time of a code-generation
/// failure.
/// </summary>
internal sealed class CodeGenerationLoadedAssemblyInfo
{
    /// <summary>
    /// Gets the simple name of the assembly (e.g. <c>Aspire.Hosting.JavaScript</c>).
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the value of the assembly's <see cref="AssemblyInformationalVersionAttribute"/>
    /// when present, otherwise the assembly version.
    /// </summary>
    public string? InformationalVersion { get; init; }

    /// <summary>
    /// Gets the on-disk location of the assembly when available.
    /// </summary>
    public string? Location { get; init; }
}

/// <summary>
/// Builds <see cref="CodeGenerationDiagnostic"/> payloads from caught reflection-load
/// exceptions and converts them into <see cref="LocalRpcException"/> instances that StreamJsonRpc
/// will propagate to the CLI with structured error data.
/// </summary>
internal static class CodeGenerationDiagnosticBuilder
{
    private const string SafeMessage =
        "Aspire SDK code generation failed because the installed Aspire CLI appears to be incompatible with the configured SDK version. Run 'aspire update' to align the CLI and SDK and try again.";

    private const string RemediationHint =
        "Run 'aspire update' to align the installed Aspire CLI with the configured SDK version, then retry.";

    /// <summary>
    /// Inspects the supplied exception (and any inner exceptions) and, if it looks like a
    /// reflection/load failure, returns a <see cref="LocalRpcException"/> carrying a
    /// <see cref="CodeGenerationDiagnostic"/> in its <see cref="LocalRpcException.ErrorData"/>.
    /// Returns <see langword="null"/> for exceptions that are not reflection-load failures.
    /// </summary>
    public static LocalRpcException? TryCreateRpcException(Exception exception, AssemblyLoader? assemblyLoader, ILogger? logger = null)
    {
        var loadException = FindReflectionLoadException(exception);
        if (loadException is null)
        {
            return null;
        }

        var diagnostic = BuildDiagnostic(loadException, assemblyLoader, logger);

        return new LocalRpcException(SafeMessage)
        {
            ErrorCode = CodeGenerationErrorCodes.IncompatibleAspireSdk,
            ErrorData = diagnostic
        };
    }

    /// <summary>
    /// Builds a <see cref="CodeGenerationDiagnostic"/> from the supplied exception without
    /// wrapping it in a <see cref="LocalRpcException"/>. Exposed for testing.
    /// </summary>
    internal static CodeGenerationDiagnostic BuildDiagnostic(Exception exception, AssemblyLoader? assemblyLoader, ILogger? logger = null)
    {
        string? typeName = null;
        string? memberName = null;

        switch (exception)
        {
            case TypeLoadException tle:
                typeName = tle.TypeName;
                break;
            case MissingMethodException mme:
                memberName = SanitizeMemberMessage(mme.Message);
                break;
            case MissingFieldException mfe:
                memberName = SanitizeMemberMessage(mfe.Message);
                break;
            case FileLoadException fle:
                typeName = fle.FileName;
                break;
            case BadImageFormatException bife:
                typeName = bife.FileName;
                break;
        }

        var (runtimeVersion, runtimePath, loadedAssemblies) = CaptureLoadedAssemblies(assemblyLoader, logger);

        return new CodeGenerationDiagnostic
        {
            OriginalExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            TypeName = typeName,
            MemberName = memberName,
            RuntimeAspireHostingVersion = runtimeVersion,
            RuntimeAspireHostingPath = runtimePath,
            LoadedAssemblies = loadedAssemblies,
            RemediationHint = RemediationHint
        };
    }

    /// <summary>
    /// Walks the exception chain and returns the first inner exception that looks like a
    /// reflection-load failure, or <see langword="null"/> if none is found.
    /// </summary>
    internal static Exception? FindReflectionLoadException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ReflectionTypeLoadException rtle)
            {
                foreach (var loaderException in rtle.LoaderExceptions)
                {
                    if (loaderException is not null && IsReflectionLoadException(loaderException))
                    {
                        return loaderException;
                    }
                }

                // No specific loader exception matched, but the RTLE itself is a reflection-load
                // failure — fall through and return it from the IsReflectionLoadException check below.
            }

            if (IsReflectionLoadException(current))
            {
                return current;
            }
        }

        return null;
    }

    private static bool IsReflectionLoadException(Exception exception) => exception
        is TypeLoadException
        or MissingMethodException
        or MissingFieldException
        or FileLoadException
        or BadImageFormatException
        or ReflectionTypeLoadException;

    private static (string? Version, string? Path, List<CodeGenerationLoadedAssemblyInfo> Assemblies) CaptureLoadedAssemblies(
        AssemblyLoader? assemblyLoader,
        ILogger? logger)
    {
        string? runtimeVersion = null;
        string? runtimePath = null;
        var loaded = new List<CodeGenerationLoadedAssemblyInfo>();

        // Locate the actually-loaded Aspire.Hosting assembly (the runtime that backed the failing
        // codegen). We avoid `typeof(Aspire.Hosting.X).Assembly` because Aspire.Hosting.RemoteHost
        // does not reference Aspire.Hosting; if for any reason it isn't in AppDomain.Assemblies we
        // leave the version null rather than substituting a sibling like Aspire.Hosting.RemoteHost.
        var aspireHostingAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Aspire.Hosting", StringComparison.OrdinalIgnoreCase));

        if (aspireHostingAssembly is not null)
        {
            runtimeVersion = GetInformationalVersion(aspireHostingAssembly);
            runtimePath = TryGetLocation(aspireHostingAssembly);
        }

        if (assemblyLoader is null)
        {
            return (runtimeVersion, runtimePath, loaded);
        }

        try
        {
            foreach (var info in assemblyLoader.GetLoadedAssemblyDiagnostics())
            {
                loaded.Add(info);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to capture loaded assembly diagnostics while building code-generation diagnostic.");
        }

        return (runtimeVersion, runtimePath, loaded);
    }

    internal static string? GetInformationalVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString();
    }

    private static string? TryGetLocation(Assembly assembly)
    {
        try
        {
            return string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
        }
        catch
        {
            return null;
        }
    }

    private static string? SanitizeMemberMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        // The CLR's Missing*Exception messages are typically of the form
        //   "Method not found: 'System.Void Aspire.Hosting.X.Y.Z(Aspire.Hosting.Foo)'."
        // We keep them verbatim; the CLI controls whether they are surfaced to the user.
        return message;
    }
}
