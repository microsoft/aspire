// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis.Testing;
using Xunit;
using static Microsoft.CodeAnalysis.Testing.DiagnosticResult;

namespace Aspire.Hosting.Analyzers.Tests;

public class AspireExportAnalyzerTests
{
    [Fact]
    public async Task ValidExport_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport(Description = "Test method")]
                public static string TestMethod() => "test";
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidExportWithCamelCase_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport(Description = "Add Redis")]
                public static string AddRedis() => "test";
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidExportWithDottedOperation_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("Dictionary.set", Description = "Dictionary set")]
                public static void DictionarySet() { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task InstanceMethod_ReportsASPIREEXPORT001()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportMethodMustBeStatic;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public class TestExports
            {
                [AspireExport]
                public string InstanceMethod() => "test";
            }
            """,
            [CompilerError(diagnostic.Id).WithLocation(7, 6).WithMessage("Method 'InstanceMethod' marked with [AspireExport] must be static")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task InstanceMethodOnExportedType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport]
            public class TestExports
            {
                [AspireExport]
                public string InstanceMethod() => "test";
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidIdFormat_WithSlash_ReportsASPIREEXPORT002()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_invalidExportIdFormat;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("aspire/methodName")]
                public static string MethodWithSlash() => "test";
            }
            """,
            [CompilerError(diagnostic.Id).WithLocation(7, 6).WithMessage("Export ID 'aspire/methodName' is not a valid method name. Use a valid identifier (e.g., 'addRedis', 'withEnvironment').")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidIdFormat_WithAtSymbol_ReportsASPIREEXPORT002()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_invalidExportIdFormat;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("method@1")]
                public static string MethodWithAt() => "test";
            }
            """,
            [CompilerError(diagnostic.Id).WithLocation(7, 6).WithMessage("Export ID 'method@1' is not a valid method name. Use a valid identifier (e.g., 'addRedis', 'withEnvironment').")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidReturnType_ReportsASPIREEXPORT003()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_returnTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static Stream InvalidReturn() => Stream.Null;
            }
            """,
            [CompilerError(diagnostic.Id).WithLocation(8, 6).WithMessage("Method 'InvalidReturn' has return type 'System.IO.Stream' which is not ATS-compatible. Use void, Task, Task<T>, or a supported Aspire type.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidParameterType_ReportsASPIREEXPORT004()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_parameterTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void InvalidParam(Stream stream) { }
            }
            """,
            [CompilerError(diagnostic.Id).WithLocation(8, 6).WithMessage("Parameter 'stream' of type 'System.IO.Stream' in method 'InvalidParam' is not ATS-compatible. Use primitive types, enums, or supported Aspire types.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidPrimitiveTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static int Primitives(string s, int i, bool b, double d, long l) => i;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidNullableTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static int? Nullables(int? i, bool? b) => i;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidTaskReturn_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.Threading.Tasks;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static Task AsyncMethod() => Task.CompletedTask;

                [AspireExport]
                public static Task<string> AsyncMethodWithResult() => Task.FromResult("test");
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidValueTaskReturn_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.Threading.Tasks;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static ValueTask AsyncMethod() => ValueTask.CompletedTask;

                [AspireExport]
                public static ValueTask<string> AsyncMethodWithResult() => ValueTask.FromResult("test");
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidEnumTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public enum MyEnum { A, B, C }

            public static class TestExports
            {
                [AspireExport]
                public static MyEnum EnumMethod(MyEnum value) => value;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidDelegateParameter_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void WithCallback(Func<string, int> callback) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_InvokesActionInline_ReportsASPIREEXPORT010()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportedSyncDelegateInvokedInline;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithInlineCallback(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    callback(new TestContext());
                    return builder;
                }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 9).WithArguments("WithInlineCallback", "callback")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_InvokesNullableActionViaInvoke_ReportsASPIREEXPORT010()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportedSyncDelegateInvokedInline;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithInlineOptionalCallback(this IResourceBuilder<TestResource> builder, Action<TestContext>? callback)
                {
                    callback?.Invoke(new TestContext());
                    return builder;
                }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 18).WithArguments("WithInlineOptionalCallback", "callback")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_InvokesSyncFuncInline_ReportsASPIREEXPORT010()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportedSyncDelegateInvokedInline;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithInlineFunc(this IResourceBuilder<TestResource> builder, Func<TestContext, string> callback)
                {
                    _ = callback(new TestContext());
                    return builder;
                }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 13).WithArguments("WithInlineFunc", "callback")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_PassesActionIntoDeferredLambda_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public sealed class TestAnnotation : IResourceAnnotation
            {
                public TestAnnotation(Action<TestContext> callback) { }
            }

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithDeferredCallback(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    return builder.WithAnnotation(new TestAnnotation(ctx => callback(ctx)));
                }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_InvokesSyncCallbackInsideImmediatelyInvokedLambda_ReportsASPIREEXPORT010()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportedSyncDelegateInvokedInline;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithInlineLambdaCallback(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    ((Action)(() => callback(new TestContext())))();
                    return builder;
                }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 25).WithArguments("WithInlineLambdaCallback", "callback")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_PassesActionIntoDeferredLocalFunction_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public sealed class TestAnnotation : IResourceAnnotation
            {
                public TestAnnotation(Action<TestContext> callback) { }
            }

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithDeferredLocalFunction(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    void InvokeCallback(TestContext context) => callback(context);
                    return builder.WithAnnotation(new TestAnnotation(InvokeCallback));
                }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_InvokesSyncCallbackInsideInlineLocalFunction_ReportsASPIREEXPORT010()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportedSyncDelegateInvokedInline;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithInlineLocalFunction(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    void InvokeCallback()
                    {
                        callback(new TestContext());
                    }

                    InvokeCallback();
                    return builder;
                }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(17, 13).WithArguments("WithInlineLocalFunction", "callback")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_WithBackgroundThreadOptIn_NoASPIREEXPORT010()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport(RunSyncOnBackgroundThread = true)]
                public static IResourceBuilder<TestResource> WithInlineCallback(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    callback(new TestContext());
                    return builder;
                }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_WithContainingTypeBackgroundThreadOptIn_NoASPIREEXPORT010()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            [AspireExport(RunSyncOnBackgroundThread = true)]
            public static class TestExports
            {
                [AspireExport]
                public static IResourceBuilder<TestResource> WithInlineCallback(this IResourceBuilder<TestResource> builder, Action<TestContext> callback)
                {
                    callback(new TestContext());
                    return builder;
                }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportedBuilderMethod_InvokesAsyncCallbackInline_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using System.Threading.Tasks;
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public sealed class TestResource(string name) : Resource(name);
            public sealed class TestContext;

            public static class TestExports
            {
                [AspireExport]
                public static async Task<IResourceBuilder<TestResource>> WithAsyncCallback(this IResourceBuilder<TestResource> builder, Func<TestContext, Task> callback)
                {
                    await callback(new TestContext());
                    return builder;
                }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidBuilderParameter_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void BuilderMethod(IDistributedApplicationBuilder builder) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidParamsArray_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void ParamsMethod(params string[] args) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidCollectionTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static List<string> ListMethod(Dictionary<string, int> dict) => new();

                [AspireExport("readonlyCollections")]
                public static IReadOnlyList<int> ReadonlyMethod(IReadOnlyDictionary<string, bool> dict) => [];

                [AspireExport("enumerableCollections")]
                public static IEnumerable<int> EnumerableMethod(IEnumerable<int> values) => [];
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidDateOnlyTimeOnly_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("dateTimeMethod")]
                public static DateOnly DateMethod(TimeOnly time, DateTimeOffset dto, TimeSpan ts) => DateOnly.MinValue;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task TypeWithAspireExportAttribute_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport]
            public class MyCustomType
            {
                public string Name { get; set; } = "";
            }

            public static class TestExports
            {
                [AspireExport("customTypeMethod")]
                public static void Method(MyCustomType custom) { }

                [AspireExport("returnsCustomType")]
                public static MyCustomType ReturnsCustom() => new();
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task AssemblyExportedExternalType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;

            [assembly: AspireExport(typeof(IServiceProvider))]

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static IServiceProvider GetProvider(this IServiceProvider provider) => provider;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ArrayOfAssemblyExportedExternalType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;

            [assembly: AspireExport(typeof(IServiceProvider))]

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static IServiceProvider[] GetChildren(IServiceProvider[] providers) => providers;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidObjectType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static object ObjectMethod(object value) => value;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidArrayTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static string[] ArrayMethod(int[] numbers) => [];
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task NonGenericCollectionTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System.Collections;
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static object? DictMethod(this IDictionary dict, string key) => dict[key];

                [AspireExport]
                public static object? ListMethod(this IList list, int index) => list[index];
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task InvalidCollectionElementType_ReportsASPIREEXPORT004()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_parameterTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.Collections.Generic;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void InvalidList(List<Stream> streams) { }
            }
            """,
            [CompilerError(diagnostic.Id).WithLocation(9, 6).WithMessage("Parameter 'streams' of type 'System.Collections.Generic.List<System.IO.Stream>' in method 'InvalidList' is not ATS-compatible. Use primitive types, enums, or supported Aspire types.")]);

        await test.RunAsync();
    }

    // ASPIREEXPORT005 Tests - Union requires at least 2 types

    [Fact]
    public async Task UnionWithSingleType_ReportsASPIREEXPORT005()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_unionRequiresAtLeastTwoTypes;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void SingleUnion([AspireUnion(typeof(string))] object value) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(8, 37).WithArguments("1")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithEmptyTypes_ReportsASPIREEXPORT005()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_unionRequiresAtLeastTwoTypes;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void EmptyUnion([AspireUnion()] object value) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(8, 36).WithArguments("0")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidUnionWithTwoTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void ValidUnion([AspireUnion(typeof(string), typeof(int))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ValidUnionWithMultipleTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void MultiUnion([AspireUnion(typeof(string), typeof(int), typeof(bool))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    // ASPIREEXPORT006 Tests - Union types must be ATS-compatible

    [Fact]
    public async Task UnionWithIncompatibleType_ReportsASPIREEXPORT006()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_unionTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void InvalidUnion([AspireUnion(typeof(string), typeof(Stream))] object value) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(9, 38).WithArguments("System.IO.Stream")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithPlainClass_ReportsASPIREEXPORT006()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_unionTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyPlainClass { }

            public static class TestExports
            {
                [AspireExport]
                public static void PlainClassUnion([AspireUnion(typeof(string), typeof(MyPlainClass))] object value) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(10, 41).WithArguments("MyPlainClass")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithDtoType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireDto]
            public class MyDtoType { public string Name { get; set; } = ""; }

            public static class TestExports
            {
                [AspireExport]
                public static void DtoUnion([AspireUnion(typeof(string), typeof(MyDtoType))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithPrimitives_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void PrimitiveUnion([AspireUnion(typeof(string), typeof(int), typeof(bool))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    // ASPIREEXPORT007 Tests - No duplicate export IDs for same target type

    [Fact]
    public async Task DuplicateExportIdSameTargetType_ReportsASPIREEXPORT007()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_duplicateExportId;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }

                [AspireExport("addThing")]
                public static void AddThingWithPort(this IDistributedApplicationBuilder builder, string name, int port) { }
            }
            """,
            [
                new DiagnosticResult(diagnostic).WithLocation(7, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder"),
                new DiagnosticResult(diagnostic).WithLocation(10, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task DifferentExportIdsSameTargetType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }

                [AspireExport]
                public static void AddThingWithPort(this IDistributedApplicationBuilder builder, string name, int port) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task SameExportIdDifferentTargetTypes_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class TestExports
            {
                [AspireExport("configure")]
                public static void ConfigureBuilder(this IDistributedApplicationBuilder builder, string name) { }

                [AspireExport("configure")]
                public static IResourceBuilder<MyResource> ConfigureResource(this IResourceBuilder<MyResource> builder, string value)
                    => builder;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task NonExtensionMethod_NoDuplicateCheck()
    {
        // Non-extension methods with same export ID should not trigger ASPIREEXPORT007
        // since they don't have a target type in the same way
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static string GetValue() => "test";

                [AspireExport("getValue")]
                public static string GetValueWithDefault(string defaultValue) => defaultValue;
            }
            """, []);

        await test.RunAsync();
    }

    // Additional ASPIREEXPORT006 tests - valid ATS types in unions

    [Fact]
    public async Task UnionWithIResource_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void ResourceUnion([AspireUnion(typeof(string), typeof(IResource))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithEnum_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public enum MyEnum { A, B, C }

            public static class TestExports
            {
                [AspireExport]
                public static void EnumUnion([AspireUnion(typeof(string), typeof(MyEnum))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithAspireExportType_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport]
            public class MyExportType { public string Name { get; set; } = ""; }

            public static class TestExports
            {
                [AspireExport]
                public static void ExportTypeUnion([AspireUnion(typeof(string), typeof(MyExportType))] object value) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnionWithMultipleInvalidTypes_ReportsMultipleASPIREEXPORT006()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_unionTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyPlainClass { }

            public static class TestExports
            {
                [AspireExport]
                public static void MultiInvalid([AspireUnion(typeof(Stream), typeof(MyPlainClass))] object value) { }
            }
            """,
            [
                new DiagnosticResult(diagnostic).WithLocation(11, 38).WithArguments("System.IO.Stream"),
                new DiagnosticResult(diagnostic).WithLocation(11, 38).WithArguments("MyPlainClass")
            ]);

        await test.RunAsync();
    }

    // Additional ASPIREEXPORT007 tests - cross-class and multiple duplicates

    [Fact]
    public async Task DuplicateExportIdAcrossClasses_ReportsASPIREEXPORT007()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_duplicateExportId;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class ClassA
            {
                [AspireExport]
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }
            }

            public static class ClassB
            {
                [AspireExport("addThing")]
                public static void AddThingAlso(this IDistributedApplicationBuilder builder, string name) { }
            }
            """,
            [
                new DiagnosticResult(diagnostic).WithLocation(7, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder"),
                new DiagnosticResult(diagnostic).WithLocation(13, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ThreeOrMoreDuplicates_ReportsAllASPIREEXPORT007()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_duplicateExportId;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("addThing")]
                public static void AddThing1(this IDistributedApplicationBuilder builder) { }

                [AspireExport("addThing")]
                public static void AddThing2(this IDistributedApplicationBuilder builder) { }

                [AspireExport("addThing")]
                public static void AddThing3(this IDistributedApplicationBuilder builder) { }
            }
            """,
            [
                new DiagnosticResult(diagnostic).WithLocation(7, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder"),
                new DiagnosticResult(diagnostic).WithLocation(10, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder"),
                new DiagnosticResult(diagnostic).WithLocation(13, 6).WithArguments("addThing", "Aspire.Hosting.IDistributedApplicationBuilder")
            ]);

        await test.RunAsync();
    }

    // Combined ASPIREEXPORT005 + ASPIREEXPORT006 test

    [Fact]
    public async Task SingleInvalidType_ReportsBothASPIREEXPORT005AndASPIREEXPORT006()
    {
        var asp011 = AspireExportAnalyzer.Diagnostics.s_unionRequiresAtLeastTwoTypes;
        var asp012 = AspireExportAnalyzer.Diagnostics.s_unionTypeMustBeAtsCompatible;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void SingleInvalid([AspireUnion(typeof(Stream))] object value) { }
            }
            """,
            [
                new DiagnosticResult(asp011).WithLocation(9, 39).WithArguments("1"),
                new DiagnosticResult(asp012).WithLocation(9, 39).WithArguments("System.IO.Stream")
            ]);

        await test.RunAsync();
    }

    // ASPIREEXPORT008 Tests - Missing export attribute on builder extension methods

    [Fact]
    public async Task MissingExportAttribute_OnBuilderExtensionMethod_ReportsASPIREEXPORT008()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(7, 24).WithArguments("AddThing", "Add [AspireExport] if ATS-compatible, or [AspireExportIgnore] with a reason.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_WithIncompatibleParam_ReportsASPIREEXPORT008WithReason()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System.IO;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                public static void AddThing(this IDistributedApplicationBuilder builder, Stream data) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(8, 24).WithArguments("AddThing", "parameter 'data' of type 'System.IO.Stream' is not ATS-compatible.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_WithOutParam_ReportsASPIREEXPORT008WithReason()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                public static void TryAddThing(this IDistributedApplicationBuilder builder, out string result) { result = ""; }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(7, 24).WithArguments("TryAddThing", "'out' parameter 'result' is not ATS-compatible.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_WithAspireExport_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_WithAspireExportIgnore_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExportIgnore(Reason = "Not needed for polyglot hosts.")]
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_WithContainingTypeAspireExportIgnore_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExportIgnore(Reason = "Not part of the ATS surface.")]
            public static class TestExtensions
            {
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_ObsoleteMethod_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using System;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [Obsolete("Use something else.")]
                public static void AddThing(this IDistributedApplicationBuilder builder, string name) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_NonExtensionMethod_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                public static void AddThing(string name) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_NonBuilderExtension_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                public static void AddThing(this string name) { }
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_OnAspireExportedTypeExtension_ReportsASPIREEXPORT008()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport]
            public interface IExportedHandle
            {
            }

            public static class TestExports
            {
                public static void Configure(this IExportedHandle handle) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(12, 24).WithArguments("Configure", "Add [AspireExport] if ATS-compatible, or [AspireExportIgnore] with a reason.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_OnAssemblyExportedTypeExtension_ReportsASPIREEXPORT008()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            [assembly: AspireExport(typeof(AssemblyExportedHandle))]

            var builder = DistributedApplication.CreateBuilder(args);

            public class AssemblyExportedHandle
            {
            }

            public static class TestExports
            {
                public static void Configure(this AssemblyExportedHandle handle) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(13, 24).WithArguments("Configure", "Add [AspireExport] if ATS-compatible, or [AspireExportIgnore] with a reason.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_OnAssemblyExportedExternalTypeExtension_ReportsASPIREEXPORT008()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using System;
            using Aspire.Hosting;

            [assembly: AspireExport(typeof(IServiceProvider))]

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                public static void Configure(this IServiceProvider serviceProvider) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(10, 24).WithArguments("Configure", "Add [AspireExport] if ATS-compatible, or [AspireExportIgnore] with a reason.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_OnImplicitlyExportedResourceTypeExtension_ReportsASPIREEXPORT008()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_missingExportAttribute;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class ExportedMethods
            {
                [AspireExport]
                public static IResourceBuilder<MyResource> AddThing(this IDistributedApplicationBuilder builder, string name) => throw null!;
            }

            public static class ResourceExtensions
            {
                public static void Configure(this MyResource resource) { }
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(20, 24).WithArguments("Configure", "Add [AspireExport] if ATS-compatible, or [AspireExportIgnore] with a reason.")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task MissingExportAttribute_OnExportedTypeInternalExtensionMethod_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport]
            public interface IExportedHandle
            {
            }

            public static class TestExports
            {
                internal static void Configure(this IExportedHandle handle) { }
            }
            """, []);

        await test.RunAsync();
    }

    // ASPIREEXPORT009 Tests - Export name should be unique for target-specific methods

    [Fact]
    public async Task ExportNameMatchesMethodName_WithConcreteTarget_ReportsASPIREEXPORT009()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportNameShouldBeUnique;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class TestExports
            {
                [AspireExport("withRoleAssignments")]
                internal static IResourceBuilder<T> WithRoleAssignments<T>(
                    this IResourceBuilder<T> builder,
                    IResourceBuilder<MyResource> target,
                    params string[] roles)
                    where T : IResource
                    => builder;
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 6).WithArguments("withRoleAssignments", "WithRoleAssignments", "MyResource", "withMyRoleAssignments")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportNameMatchesMethodName_WithConcreteTarget_ParameterlessAspireExport_ReportsASPIREEXPORT009()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportNameShouldBeUnique;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class TestExports
            {
                [AspireExport]
                internal static IResourceBuilder<T> WithRoleAssignments<T>(
                    this IResourceBuilder<T> builder,
                    IResourceBuilder<MyResource> target,
                    params string[] roles)
                    where T : IResource
                    => builder;
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 6).WithArguments("withRoleAssignments", "WithRoleAssignments", "MyResource", "withMyRoleAssignments")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportNameIsUnique_WithConcreteTarget_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class TestExports
            {
                [AspireExport("withMyRoleAssignments")]
                internal static IResourceBuilder<T> WithRoleAssignments<T>(
                    this IResourceBuilder<T> builder,
                    IResourceBuilder<MyResource> target,
                    params string[] roles)
                    where T : IResource
                    => builder;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportNameMatchesMethodName_NoConcreteTarget_NoDiagnostics()
    {
        // No concrete IResourceBuilder<T> parameter, so no risk of collision
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                internal static IResourceBuilder<T> WithEnvironment<T>(
                    this IResourceBuilder<T> builder,
                    string name,
                    string value)
                    where T : IResource
                    => builder;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportNameMatchesMethodName_WithInterfaceTarget_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport]
                internal static IResourceBuilder<T> WithReference<T>(
                    this IResourceBuilder<T> builder,
                    IResourceBuilder<IResource> target)
                    where T : IResourceWithEnvironment
                    => builder;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportNameMatchesMethodName_ConcreteFirstParam_NoDiagnostics()
    {
        // First param is IResourceBuilder<MyResource> (not open generic), so it's already scoped
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public class MyResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class TestExports
            {
                [AspireExport]
                internal static IResourceBuilder<MyResource> AddDatabase(
                    this IResourceBuilder<MyResource> builder,
                    string name)
                    => builder;
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExportNameMatchesMethodName_AzureResourceSuffix_SuggestsCleanName()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_exportNameShouldBeUnique;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using System.Collections.Generic;

            var builder = DistributedApplication.CreateBuilder(args);

            public class AzureSearchResource : IResource
            {
                public string Name => "test";
                public ResourceAnnotationCollection Annotations { get; } = new();
            }

            public static class TestExports
            {
                [AspireExport("withRoleAssignments")]
                internal static IResourceBuilder<T> WithRoleAssignments<T>(
                    this IResourceBuilder<T> builder,
                    IResourceBuilder<AzureSearchResource> target,
                    params string[] roles)
                    where T : IResource
                    => builder;
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(15, 6).WithArguments("withRoleAssignments", "WithRoleAssignments", "AzureSearchResource", "withSearchRoleAssignments")]);

        await test.RunAsync();
    }

    // ASPIREEXPORT011 Tests - Redundant explicit export ID that matches convention

    [Fact]
    public async Task RedundantExportId_MatchesConvention_ReportsASPIREEXPORT011()
    {
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_redundantExportId;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("addRedis")]
                public static string AddRedis() => "test";
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(7, 6).WithArguments("addRedis", "AddRedis")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task NonRedundantExportId_DoesNotMatchConvention_NoDiagnostics()
    {
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            public static class TestExports
            {
                [AspireExport("addRedisWithPort")]
                public static string AddRedis(int port) => "test";
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task RedundantExportId_InstanceMethod_CamelCaseOnlyNotMatchingConvention_NoDiagnostics()
    {
        // For instance methods in a type with ExposeMethods=true, the convention is
        // "TypeName.camelCaseMethod" (e.g. "MyClass.getValue"), not just "getValue".
        // So [AspireExport("getValue")] on GetValue does NOT trigger ASPIREEXPORT011.
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport(ExposeMethods = true)]
            public class MyClass
            {
                [AspireExport("getValue")]
                public string GetValue() => "test";
            }
            """, []);

        await test.RunAsync();
    }

    [Fact]
    public async Task RedundantExportId_InstanceMethod_TypePrefixMatchesConvention_ReportsASPIREEXPORT011()
    {
        // [AspireExport("MyClass.getValue")] on GetValue matches the convention-derived id
        // "MyClass.getValue", so ASPIREEXPORT011 fires.
        var diagnostic = AspireExportAnalyzer.Diagnostics.s_redundantExportId;

        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport(ExposeMethods = true)]
            public class MyClass
            {
                [AspireExport("MyClass.getValue")]
                public string GetValue() => "test";
            }
            """,
            [new DiagnosticResult(diagnostic).WithLocation(8, 6).WithArguments("MyClass.getValue", "GetValue")]);

        await test.RunAsync();
    }

    [Fact]
    public async Task RedundantExportId_InstanceMethod_NoExplicitId_NoDiagnostics()
    {
        // Parameterless [AspireExport] on an instance method derives the id automatically.
        // No ASPIREEXPORT011 is expected.
        var test = AnalyzerTest.Create<AspireExportAnalyzer>("""
            using Aspire.Hosting;

            var builder = DistributedApplication.CreateBuilder(args);

            [AspireExport(ExposeMethods = true)]
            public class MyClass
            {
                [AspireExport]
                public string GetValue() => "test";
            }
            """, []);

        await test.RunAsync();
    }
}
