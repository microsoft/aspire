// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Aspire.Hosting.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public partial class AppHostAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Diagnostics.SupportedDiagnostics;

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(AnalyzeCompilationStart);
    }

    private void AnalyzeCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var wellKnownTypes = WellKnownTypes.GetOrCreate(compilation);

        // We want ConcurrentHashSet here in case RegisterOperationAction runs in parallel.
        // Since ConcurrentHashSet doesn't exist, use ConcurrentDictionary and ignore the value.
        var concurrentQueue = new ConcurrentQueue<ConcurrentDictionary<ModelNameOperation, byte>>();
        context.RegisterOperationBlockStartAction(context =>
        {
            // Pool and reuse lists for each block.
            if (!concurrentQueue.TryDequeue(out var modelNameOperations))
            {
                modelNameOperations = new ConcurrentDictionary<ModelNameOperation, byte>();
            }

            context.RegisterOperationAction(c => DoOperationAnalysis(c, modelNameOperations), OperationKind.Invocation);

            context.RegisterOperationBlockEndAction(c =>
            {
                DetectInvalidModelNames(c, modelNameOperations);

                // Return to the pool.
                modelNameOperations.Clear();
                concurrentQueue.Enqueue(modelNameOperations);
            });
        });

        void DoOperationAnalysis(OperationAnalysisContext context, ConcurrentDictionary<ModelNameOperation, byte> modelNameOperations)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var targetMethod = invocation.TargetMethod;

            if (!IsModelNameInvocation(wellKnownTypes, targetMethod, out var parameterData))
            {
                return;
            }

            foreach (var (modelNameParameter, modelTypes) in parameterData)
            {
                if (TryGetStringToken(invocation, modelNameParameter, out var token))
                {
                    modelNameOperations.TryAdd(ModelNameOperation.Create(invocation, modelTypes, token), value: default);
                }
            }
        }
    }

    private static bool IsModelNameInvocation(
        WellKnownTypes wellKnownTypes,
        IMethodSymbol targetMethod,
        [NotNullWhen(true)] out (IParameterSymbol ModelNameParameter, ModelType[] ModelTypes)[]? parameterData)
    {
        if (!wellKnownTypes.TryGet(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IModelNameParameter, out var modelNameParameter) ||
            !wellKnownTypes.TryGet(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ResourceNameAttribute, out var resourceNameAttribute) ||
            !wellKnownTypes.TryGet(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_EndpointNameAttribute, out var endpointNameAttribute))
        {
            parameterData = null;
            return false;
        }

        // Look for string parameters annotated with attribute that implements IModelNameParameter
        var candidateParameters = targetMethod.Parameters
            .Select(ps => (Symbol: ps, ModelTypes: GetModelNameAttributes(ps)))
            .Where(parameterData =>
                SymbolEqualityComparer.Default.Equals(parameterData.Symbol.Type, wellKnownTypes.Get(SpecialType.System_String))
                && parameterData.ModelTypes.Length > 0)
            .ToArray();

        if (candidateParameters.Length > 0)
        {
            parameterData = candidateParameters.Select(p => (p.Symbol, p.ModelTypes)).ToArray();
            return true;
        }

        parameterData = null;
        return false;

        ModelType[] GetModelNameAttributes(IParameterSymbol parameter)
        {
            var attrData = parameter.GetAttributes()
                .Where(a => WellKnownTypes.Implements(a.AttributeClass, modelNameParameter));

            // Model type (e.g. Resource, Endpoint) is based on the concrete attribute type

            var modelTypes = attrData.Select(a =>
                SymbolEqualityComparer.Default.Equals(a?.AttributeClass, resourceNameAttribute)
                    ? ModelType.Resource
                    : SymbolEqualityComparer.Default.Equals(a?.AttributeClass, endpointNameAttribute)
                        ? ModelType.Endpoint
                        : (ModelType?)null);

            return modelTypes.Where(m => m is not null).Select(m => m.Value).ToArray();
        }
    }

    private static bool TryGetStringToken(IInvocationOperation invocation, IParameterSymbol modelNameParameter, out SyntaxToken token)
    {
        IArgumentOperation? argumentOperation = null;
        foreach (var argument in invocation.Arguments)
        {
            if (SymbolEqualityComparer.Default.Equals(modelNameParameter, argument.Parameter))
            {
                argumentOperation = argument;
                break;
            }
        }

        if (argumentOperation?.Syntax is not ArgumentSyntax routePatternArgumentSyntax ||
            routePatternArgumentSyntax.Expression is not LiteralExpressionSyntax routePatternArgumentLiteralSyntax)
        {
            token = default;
            return false;
        }

        token = routePatternArgumentLiteralSyntax.Token;
        return true;
    }

    private record struct ModelNameOperation(IInvocationOperation Operation, ModelType[] ModelTypes, SyntaxToken ModelNameToken)
    {
        public static ModelNameOperation Create(IInvocationOperation operation, ModelType[] modelTypes, SyntaxToken modelNameToken)
        {
            return new ModelNameOperation(operation, modelTypes, modelNameToken);
        }
    }

    private enum ModelType
    {
        Unknown,
        Resource,
        Endpoint
    }
}
