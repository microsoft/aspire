// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using StreamJsonRpc;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class CodeGenerationDiagnosticBuilderTests
{
    [Fact]
    public void TryCreateRpcException_NonReflectionFailure_ReturnsNull()
    {
        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(
            new InvalidOperationException("plain failure"),
            assemblyLoader: null);

        Assert.Null(result);
    }

    [Fact]
    public void TryCreateRpcException_TypeLoadException_ReturnsLocalRpcExceptionWithDiagnostic()
    {
        var typeLoad = new TypeLoadException("type not found")
        {
            // TypeName/Message can be empty when the JIT throws — exercise the empty path here
            // separately; for this test we want to confirm the wrapping path itself works.
        };

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(typeLoad, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        Assert.Equal(CodeGenerationErrorCodes.IncompatibleAspireSdk, localRpc.ErrorCode);
        var diagnostic = Assert.IsType<CodeGenerationDiagnostic>(localRpc.ErrorData);
        Assert.Equal(typeof(TypeLoadException).FullName, diagnostic.OriginalExceptionType);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.RemediationHint));
        Assert.False(string.IsNullOrWhiteSpace(localRpc.Message));
        // The default message must NOT leak the .NET-specific type name.
        Assert.DoesNotContain("TypeLoadException", localRpc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateRpcException_TypeLoadExceptionWithEmptyMessage_ReturnsStructuredDiagnostic()
    {
        // Repro of issue #16709: JIT-thrown TypeLoadException with no Message — we must still
        // produce a non-empty, actionable Message on the wire.
        var typeLoad = new TypeLoadException();

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(typeLoad, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        Assert.False(string.IsNullOrWhiteSpace(localRpc.Message));
        var diagnostic = Assert.IsType<CodeGenerationDiagnostic>(localRpc.ErrorData);
        Assert.Equal(typeof(TypeLoadException).FullName, diagnostic.OriginalExceptionType);
    }

    [Fact]
    public void TryCreateRpcException_MissingMethodException_PopulatesMemberName()
    {
        var missing = new MissingMethodException("System.Void Aspire.Hosting.Foo.Bar()");

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(missing, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        var diagnostic = Assert.IsType<CodeGenerationDiagnostic>(localRpc.ErrorData);
        Assert.Equal(typeof(MissingMethodException).FullName, diagnostic.OriginalExceptionType);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.MemberName));
    }

    [Fact]
    public void TryCreateRpcException_WrappedTypeLoadException_FindsInnerCause()
    {
        var inner = new TypeLoadException("nested");
        var outer = new InvalidOperationException("wrapper", inner);

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(outer, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        var diagnostic = Assert.IsType<CodeGenerationDiagnostic>(localRpc.ErrorData);
        Assert.Equal(typeof(TypeLoadException).FullName, diagnostic.OriginalExceptionType);
    }

    [Fact]
    public void TryCreateRpcException_ReflectionTypeLoadException_FindsLoaderException()
    {
        var loader = new TypeLoadException("missing type");
        var rtle = new ReflectionTypeLoadException([null], [loader]);

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(rtle, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        var diagnostic = Assert.IsType<CodeGenerationDiagnostic>(localRpc.ErrorData);
        Assert.Equal(typeof(TypeLoadException).FullName, diagnostic.OriginalExceptionType);
    }

    [Fact]
    public void TryCreateRpcException_FileNotFoundLoaderException_CapturesMissingAssemblyName()
    {
        // Mirrors the real failure: a code-generation assembly references an Aspire.TypeSystem
        // version that is absent on disk, so the CLR raises a FileNotFoundException inside the
        // ReflectionTypeLoadException's LoaderExceptions.
        var loader = new FileNotFoundException(
            "Could not load file or assembly 'Aspire.TypeSystem, Version=42.42.42.42'. The system cannot find the file specified.",
            "Aspire.TypeSystem, Version=42.42.42.42, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");
        var rtle = new ReflectionTypeLoadException([null], [loader]);

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(rtle, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        Assert.Equal(CodeGenerationErrorCodes.IncompatibleAspireSdk, localRpc.ErrorCode);
        var diagnostic = Assert.IsType<CodeGenerationDiagnostic>(localRpc.ErrorData);
        Assert.Equal(typeof(FileNotFoundException).FullName, diagnostic.OriginalExceptionType);
        Assert.Contains("Aspire.TypeSystem", diagnostic.TypeName);
    }

    [Fact]
    public void TryCreateRpcException_ArgumentExceptionWrappingReflectionLoad_FindsInnerCause()
    {
        // Repro of the TypeScript "no code generator found" path: GenerateCode throws an
        // ArgumentException whose inner exception is the swallowed ReflectionTypeLoadException.
        var loader = new FileNotFoundException("missing", "Aspire.TypeSystem, Version=42.42.42.42");
        var rtle = new ReflectionTypeLoadException([null], [loader]);
        var argument = new ArgumentException("No code generator found for language: TypeScript.", rtle);

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(argument, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        Assert.Equal(CodeGenerationErrorCodes.IncompatibleAspireSdk, localRpc.ErrorCode);
        // The cryptic ArgumentException is replaced with the actionable incompatible-SDK guidance.
        Assert.Equal(
            "Aspire SDK code generation failed because the installed Aspire CLI appears to be incompatible with the configured SDK version. Run 'aspire update' to align the CLI and SDK and try again.",
            localRpc.Message);
    }

    [Fact]
    public void TryCreateRpcException_StandaloneFileNotFound_PlainFilePath_IsNotClassified()
    {
        // A code generator (or ATS context build) that fails to open a genuine data file raises a
        // FileNotFoundException with a path-like FileName. This must NOT be reported as an
        // incompatible SDK, otherwise the user gets a misleading "run aspire update" hint for an
        // unrelated missing-file error.
        var ioFailure = new FileNotFoundException(
            "Could not find file '/tmp/codegen/template.json'.",
            "/tmp/codegen/template.json");

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(ioFailure, assemblyLoader: null);

        Assert.Null(result);
    }

    [Fact]
    public void TryCreateRpcException_StandaloneFileNotFound_AssemblyDisplayName_IsClassified()
    {
        // A direct assembly-bind failure (for example a JIT-time bind during generation) surfaces a
        // FileNotFoundException whose FileName is a full assembly display name. That IS an
        // incompatible-SDK signal and should be classified even though it is not wrapped in a
        // ReflectionTypeLoadException.
        var bindFailure = new FileNotFoundException(
            "Could not load file or assembly 'Aspire.TypeSystem, Version=42.42.42.42'. The system cannot find the file specified.",
            "Aspire.TypeSystem, Version=42.42.42.42, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");

        var result = CodeGenerationDiagnosticBuilder.TryCreateRpcException(bindFailure, assemblyLoader: null);

        var localRpc = Assert.IsType<LocalRpcException>(result);
        Assert.Equal(CodeGenerationErrorCodes.IncompatibleAspireSdk, localRpc.ErrorCode);
    }

    [Fact]
    public void BuildDiagnostic_CapturesRuntimeAspireHostingVersion()
    {
        // BuildDiagnostic looks for the loaded Aspire.Hosting assembly via AppDomain. Calling
        // any Aspire.Hosting type forces its assembly to be loaded so the search succeeds.
        _ = typeof(global::Aspire.Hosting.DistributedApplication);

        var diagnostic = CodeGenerationDiagnosticBuilder.BuildDiagnostic(
            new TypeLoadException(),
            assemblyLoader: null);

        Assert.False(string.IsNullOrWhiteSpace(diagnostic.RuntimeAspireHostingVersion));
    }

    [Fact]
    public void BuildDiagnostic_RuntimeAspireHostingVersion_DoesNotFallBackToRemoteHostAssembly()
    {
        _ = typeof(global::Aspire.Hosting.DistributedApplication);

        var diagnostic = CodeGenerationDiagnosticBuilder.BuildDiagnostic(
            new TypeLoadException(),
            assemblyLoader: null);

        var aspireHosting = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => string.Equals(a.GetName().Name, "Aspire.Hosting", StringComparison.OrdinalIgnoreCase));
        var aspireHostingVersion = aspireHosting
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? aspireHosting.GetName().Version?.ToString();

        // Guards #16709 finding #3: prior code fell back to typeof(AssemblyLoader).Assembly which is
        // Aspire.Hosting.RemoteHost - a sibling, not the runtime that backed the failing codegen.
        Assert.Equal(aspireHostingVersion, diagnostic.RuntimeAspireHostingVersion);
    }
}
