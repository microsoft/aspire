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
