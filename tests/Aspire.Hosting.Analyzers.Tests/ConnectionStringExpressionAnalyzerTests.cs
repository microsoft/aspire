// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using static Microsoft.CodeAnalysis.Testing.DiagnosticResult;

namespace Aspire.Hosting.Analyzers.Tests;

public class ConnectionStringExpressionAnalyzerTests
{
    [Fact]
    public async Task DirectInterfaceAccessReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
                => resource.ConnectionStringExpression;
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(4, 17)
                    .WithMessage("Direct use of 'ConnectionStringExpression' may ignore a selected resource projection; use 'GetConnectionStringExpression()' to prefer the projection, or pass 'preferOwner: true' when owner precedence is intentional")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task DirectGetConnectionStringAsyncReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using System.Threading.Tasks;
            using Aspire.Hosting.ApplicationModel;

            static ValueTask<string?> GetConnectionString(IResourceWithConnectionString resource)
                => resource.GetConnectionStringAsync();
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(5, 17)
                    .WithMessage("Direct use of 'GetConnectionStringAsync' may ignore a selected resource projection; call it on the result of 'GetEffectiveCapability<IResourceWithConnectionString>()', or use 'GetValueProvider<IResourceWithConnectionString>()' when selection must remain lazy")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ConcreteImplementationAccessReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(CustomResource resource)
                => resource.ConnectionStringExpression;

            sealed class CustomResource(string name) : Resource(name), IResourceWithConnectionString
            {
                public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Empty;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(4, 17)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ConcreteGetConnectionStringAsyncReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using System.Threading;
            using System.Threading.Tasks;
            using Aspire.Hosting.ApplicationModel;

            static ValueTask<string?> GetConnectionString(CustomResource resource)
                => resource.GetConnectionStringAsync();

            sealed class CustomResource(string name) : Resource(name), IResourceWithConnectionString
            {
                public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Empty;

                public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
                    => ValueTask.FromResult<string?>(null);
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(6, 17)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task GenericCapabilityAccessReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression<T>(T resource)
                where T : IResourceWithConnectionString
                => resource.ConnectionStringExpression;
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(5, 17)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ProjectionAwareAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
                => resource
                    .GetEffectiveCapability<IResourceWithConnectionString>()!
                    .ConnectionStringExpression;

            static ReferenceExpression GetOwnerExpression(IResourceWithConnectionString resource)
                => resource
                    .GetEffectiveCapability<IResourceWithConnectionString>(preferOwner: true)!
                    .ConnectionStringExpression;

            static ReferenceExpression GetExpressionWithAccessor(IResourceWithConnectionString resource)
                => resource.GetConnectionStringExpression();

            static ReferenceExpression GetOwnerExpressionWithAccessor(IResourceWithConnectionString resource)
                => resource.GetConnectionStringExpression(preferOwner: true);
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ProjectionAwareLocalAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                return effective.ConnectionStringExpression;
            }

            static ReferenceExpression GetOwnerExpression(IResourceWithConnectionString resource)
            {
                var owner = resource.GetEffectiveCapability<IResourceWithConnectionString>(preferOwner: true)!;
                return owner.ConnectionStringExpression;
            }
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ProjectionAwareGetConnectionStringAsyncReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using System.Threading.Tasks;
            using Aspire.Hosting.ApplicationModel;

            static ValueTask<string?> GetConnectionString(IResourceWithConnectionString resource)
                => resource
                    .GetEffectiveCapability<IResourceWithConnectionString>()!
                    .GetConnectionStringAsync();

            static ValueTask<string?> GetConnectionStringFromLocal(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                return effective.GetConnectionStringAsync();
            }

            static ValueTask<string?> GetConnectionStringFromReference(ConnectionStringReference reference)
                => reference.Provider.GetConnectionStringAsync();
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ProjectionAwareLambdaLocalAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using System;
            using Aspire.Hosting.ApplicationModel;

            static Func<ReferenceExpression> GetExpression(IResourceWithConnectionString resource)
            {
                return () =>
                {
                    var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                    return effective.ConnectionStringExpression;
                };
            }
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ConnectionStringReferenceProviderAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(ConnectionStringReference reference)
                => reference.Provider.ConnectionStringExpression;

            static ReferenceExpression GetExpressionFromLocal(ConnectionStringReference reference)
            {
                var provider = reference.Provider;
                return provider.ConnectionStringExpression;
            }
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ReassignedProjectionAwareLocalReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                effective = resource;
                return effective.ConnectionStringExpression;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(7, 22)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ReassignedAfterProjectionAwareLocalAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                var expression = effective.ConnectionStringExpression;
                effective = resource;
                return expression;
            }
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ConditionallyReassignedProjectionAwareLocalReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource, bool replace)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                if (replace)
                {
                    effective = resource;
                }

                return effective.ConnectionStringExpression;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(11, 22)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task LoopBackEdgeReassignmentReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource, bool retry)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                ReferenceExpression expression;
                do
                {
                    expression = effective.ConnectionStringExpression;
                    effective = resource;
                }
                while (retry);

                return expression;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(9, 32)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task RefWriteOnlyInvalidatesSubsequentAccesses()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringAccessMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpressionBeforeWrite(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                var expression = effective.ConnectionStringExpression;
                Replace(ref effective, resource);
                return expression;
            }

            static ReferenceExpression GetExpressionAfterWrite(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                Replace(ref effective, resource);
                return effective.ConnectionStringExpression;
            }

            static void Replace(
                ref IResourceWithConnectionString effective,
                IResourceWithConnectionString replacement)
            {
                effective = replacement;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(15, 22)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExplicitOwnerAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResource resource)
                => ((IResourceWithConnectionString)resource.GetOwnerOrSelf())
                    .ConnectionStringExpression;
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnrelatedConcretePropertyReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(CustomResource resource)
                => resource.ConnectionStringExpression;

            sealed class CustomResource(string name) : Resource(name)
            {
                public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Empty;
            }
            """,
            []);

        await test.RunAsync();
    }
}
