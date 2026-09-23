// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Analyzers.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Aspire.Hosting.Analyzers;

public partial class AppHostAnalyzer
{
    private const string ConnectionStringExpressionRemediation =
        "use 'GetConnectionStringExpression()' to prefer the projection, or pass 'preferOwner: true' when owner precedence is intentional";

    private const string GetConnectionStringAsyncRemediation =
        "call it on the result of 'GetEffectiveCapability<IResourceWithConnectionString>()', or use 'GetValueProvider<IResourceWithConnectionString>()' when selection must remain lazy";

    /// <summary>
    /// Collects connection-string accesses for block-level <c>ASPIRE013</c> control-flow analysis.
    /// </summary>
    private static void CollectConnectionStringAccess(
        OperationAnalysisContext context,
        WellKnownTypes wellKnownTypes,
        ConcurrentQueue<ConnectionStringAccess> accesses)
    {
        if (!TryGetConnectionStringSymbols(wellKnownTypes, out var symbols) ||
            !TryGetConnectionStringAccess(context.Operation, symbols, out var instance, out var location, out var memberName, out var remediation))
        {
            return;
        }

        instance = UnwrapConversions(instance);
        if (IsDirectlyResolvedValue(instance, symbols))
        {
            return;
        }

        var operationBlock = GetOperationBlock(context.Operation);
        if (instance is ILocalReferenceOperation localReference)
        {
            accesses.Enqueue(new ConnectionStringAccess(
                operationBlock,
                localReference.Local,
                localReference.Syntax.Span,
                location,
                memberName,
                remediation));
        }
        else
        {
            accesses.Enqueue(new ConnectionStringAccess(
                operationBlock,
                null,
                default,
                location,
                memberName,
                remediation));
        }
    }

    /// <summary>
    /// Reports collected <c>ASPIRE013</c> diagnostics after validating local aliases against the block's control flow.
    /// </summary>
    private static void DetectDirectConnectionStringAccesses(
        OperationBlockAnalysisContext context,
        WellKnownTypes wellKnownTypes,
        ConcurrentQueue<ConnectionStringAccess> accessQueue)
    {
        if (accessQueue.IsEmpty || !TryGetConnectionStringSymbols(wellKnownTypes, out var symbols))
        {
            return;
        }

        var accesses = accessQueue.ToArray();
        foreach (var access in accesses.Where(static access => access.Local is null))
        {
            ReportConnectionStringDiagnostic(context, access);
        }

        foreach (var group in accesses
            .Where(static access => access.Local is not null)
            .GroupBy(static access => access.OperationBlock))
        {
            var rootControlFlowGraph = context.GetControlFlowGraph(group.Key);
            // Roslyn attaches local and anonymous functions to the containing operation block, but their bodies
            // have separate CFGs. Analyze the innermost graph so writes in the containing method do not become
            // textually ordered with operations that execute later inside a callback.
            var controlFlowGraphs = EnumerateControlFlowGraphs(rootControlFlowGraph, context.CancellationToken).ToArray();

            foreach (var controlFlowGraphGroup in group.GroupBy(
                access => FindInnermostControlFlowGraph(controlFlowGraphs, access.Location.SourceSpan)))
            {
                var flowAnalyzer = new ResolvedLocalFlowAnalyzer(symbols, controlFlowGraphGroup);
                var safeAccesses = flowAnalyzer.FindSafeAccesses(controlFlowGraphGroup.Key);

                foreach (var access in controlFlowGraphGroup)
                {
                    if (!safeAccesses.Contains(access))
                    {
                        ReportConnectionStringDiagnostic(context, access);
                    }
                }
            }
        }
    }

    private static IEnumerable<ControlFlowGraph> EnumerateControlFlowGraphs(
        ControlFlowGraph controlFlowGraph,
        CancellationToken cancellationToken)
    {
        yield return controlFlowGraph;

        foreach (var localFunction in controlFlowGraph.LocalFunctions)
        {
            var localFunctionControlFlowGraph =
                controlFlowGraph.GetLocalFunctionControlFlowGraph(localFunction, cancellationToken);

            foreach (var nestedControlFlowGraph in EnumerateControlFlowGraphs(
                localFunctionControlFlowGraph,
                cancellationToken))
            {
                yield return nestedControlFlowGraph;
            }
        }

        foreach (var anonymousFunction in GetAnonymousFunctions(controlFlowGraph))
        {
            var anonymousFunctionControlFlowGraph =
                controlFlowGraph.GetAnonymousFunctionControlFlowGraph(anonymousFunction, cancellationToken);

            foreach (var nestedControlFlowGraph in EnumerateControlFlowGraphs(
                anonymousFunctionControlFlowGraph,
                cancellationToken))
            {
                yield return nestedControlFlowGraph;
            }
        }
    }

    private static IEnumerable<IFlowAnonymousFunctionOperation> GetAnonymousFunctions(
        ControlFlowGraph controlFlowGraph)
    {
        foreach (var block in controlFlowGraph.Blocks)
        {
            foreach (var operation in block.Operations)
            {
                foreach (var anonymousFunction in GetAnonymousFunctions(operation))
                {
                    yield return anonymousFunction;
                }
            }

            if (block.BranchValue is { } branchValue)
            {
                foreach (var anonymousFunction in GetAnonymousFunctions(branchValue))
                {
                    yield return anonymousFunction;
                }
            }
        }
    }

    private static IEnumerable<IFlowAnonymousFunctionOperation> GetAnonymousFunctions(IOperation operation)
    {
        if (operation is IFlowAnonymousFunctionOperation anonymousFunction)
        {
            yield return anonymousFunction;
        }

        foreach (var child in operation.ChildOperations)
        {
            foreach (var nestedAnonymousFunction in GetAnonymousFunctions(child))
            {
                yield return nestedAnonymousFunction;
            }
        }
    }

    private static ControlFlowGraph FindInnermostControlFlowGraph(
        IReadOnlyList<ControlFlowGraph> controlFlowGraphs,
        TextSpan accessSpan)
    {
        ControlFlowGraph? result = null;

        foreach (var controlFlowGraph in controlFlowGraphs)
        {
            var controlFlowGraphSpan = controlFlowGraph.OriginalOperation.Syntax.FullSpan;
            if (controlFlowGraphSpan.Contains(accessSpan) &&
                (result is null ||
                 controlFlowGraphSpan.Length < result.OriginalOperation.Syntax.FullSpan.Length))
            {
                result = controlFlowGraph;
            }
        }

        return result ?? controlFlowGraphs[0];
    }

    private static void ReportConnectionStringDiagnostic(
        OperationBlockAnalysisContext context,
        ConnectionStringAccess access)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.s_connectionStringAccessMustBeResolved,
            access.Location,
            access.MemberName,
            access.Remediation));
    }

    private static bool TryGetConnectionStringAccess(
        IOperation operation,
        ConnectionStringSymbols symbols,
        out IOperation? instance,
        out Location location,
        out string memberName,
        out string remediation)
    {
        if (operation is IPropertyReferenceOperation propertyReference &&
            ImplementsInterfaceProperty(propertyReference.Property, symbols.ConnectionStringExpression))
        {
            instance = propertyReference.Instance;
            location = GetMemberLocation(propertyReference.Syntax);
            memberName = nameof(symbols.ConnectionStringExpression);
            remediation = ConnectionStringExpressionRemediation;
            return true;
        }

        if (operation is IInvocationOperation invocation &&
            ImplementsInterfaceMethod(invocation.TargetMethod, symbols.GetConnectionStringAsync))
        {
            instance = invocation.Instance;
            location = GetMemberLocation(invocation.Syntax);
            memberName = nameof(symbols.GetConnectionStringAsync);
            remediation = GetConnectionStringAsyncRemediation;
            return true;
        }

        instance = null;
        location = Location.None;
        memberName = string.Empty;
        remediation = string.Empty;
        return false;
    }

    private static Location GetMemberLocation(SyntaxNode syntax) =>
        syntax switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.GetLocation(),
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.GetLocation(),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } => memberAccess.Name.GetLocation(),
            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax memberBinding } => memberBinding.Name.GetLocation(),
            _ => syntax.GetLocation()
        };

    private static bool ImplementsInterfaceProperty(
        IPropertySymbol property,
        IPropertySymbol interfaceProperty)
    {
        if (SymbolEqualityComparer.Default.Equals(property.OriginalDefinition, interfaceProperty.OriginalDefinition) ||
            property.ExplicitInterfaceImplementations.Any(
                implementation => SymbolEqualityComparer.Default.Equals(
                    implementation.OriginalDefinition,
                    interfaceProperty.OriginalDefinition)))
        {
            return true;
        }

        var implementation = property.ContainingType.FindImplementationForInterfaceMember(interfaceProperty);
        return implementation is IPropertySymbol implementationProperty &&
            SymbolEqualityComparer.Default.Equals(
                implementationProperty.OriginalDefinition,
                property.OriginalDefinition);
    }

    private static bool ImplementsInterfaceMethod(
        IMethodSymbol method,
        IMethodSymbol interfaceMethod)
    {
        if (SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, interfaceMethod.OriginalDefinition) ||
            method.ExplicitInterfaceImplementations.Any(
                implementation => SymbolEqualityComparer.Default.Equals(
                    implementation.OriginalDefinition,
                    interfaceMethod.OriginalDefinition)))
        {
            return true;
        }

        var implementation = method.ContainingType.FindImplementationForInterfaceMember(interfaceMethod);
        return implementation is IMethodSymbol implementationMethod &&
            SymbolEqualityComparer.Default.Equals(
                implementationMethod.OriginalDefinition,
                method.OriginalDefinition);
    }

    private static bool TryGetConnectionStringSymbols(
        WellKnownTypes wellKnownTypes,
        out ConnectionStringSymbols symbols)
    {
        symbols = default;

        if (!wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_IResourceWithConnectionString,
                out var connectionStringResource) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ResourceExtensions,
                out var resourceExtensions) ||
            !wellKnownTypes.TryGet(
                WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ConnectionStringReference,
                out var connectionStringReference))
        {
            return false;
        }

        var connectionStringExpression = connectionStringResource
            .GetMembers("ConnectionStringExpression")
            .OfType<IPropertySymbol>()
            .SingleOrDefault();
        var getConnectionStringAsync = connectionStringResource
            .GetMembers("GetConnectionStringAsync")
            .OfType<IMethodSymbol>()
            .SingleOrDefault();

        if (connectionStringExpression is null || getConnectionStringAsync is null)
        {
            return false;
        }

        symbols = new(
            connectionStringExpression,
            getConnectionStringAsync,
            resourceExtensions,
            connectionStringReference);
        return true;
    }

    private static bool IsDirectlyResolvedValue(
        IOperation? value,
        ConnectionStringSymbols symbols)
    {
        value = UnwrapConversions(value);

        if (value is IInvocationOperation invocation &&
            IsResolverInvocation(invocation, symbols.ResourceExtensions))
        {
            return true;
        }

        if (value is IPropertyReferenceOperation propertyReference &&
            propertyReference.Property.Name == "Provider" &&
            SymbolEqualityComparer.Default.Equals(
                propertyReference.Property.ContainingType,
                symbols.ConnectionStringReference))
        {
            return true;
        }

        return value switch
        {
            IConditionalOperation conditional =>
                IsDirectlyResolvedValue(conditional.WhenTrue, symbols) &&
                IsDirectlyResolvedValue(conditional.WhenFalse, symbols),
            ICoalesceOperation coalesce =>
                IsDirectlyResolvedValue(coalesce.Value, symbols) &&
                IsDirectlyResolvedValue(coalesce.WhenNull, symbols),
            _ => false
        };
    }

    private static bool IsResolverInvocation(
        IInvocationOperation invocation,
        INamedTypeSymbol resourceExtensions)
    {
        var targetMethod = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (!SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, resourceExtensions))
        {
            return false;
        }

        return targetMethod.Name is "GetEffectiveCapability" or "GetOwnerOrSelf";
    }

    private static IOperation? UnwrapConversions(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static IOperation GetOperationBlock(IOperation operation)
    {
        while (operation.Parent is { } parent)
        {
            operation = parent;
        }

        return operation;
    }

    private readonly struct ConnectionStringSymbols(
        IPropertySymbol connectionStringExpression,
        IMethodSymbol getConnectionStringAsync,
        INamedTypeSymbol resourceExtensions,
        INamedTypeSymbol connectionStringReference)
    {
        internal IPropertySymbol ConnectionStringExpression { get; } = connectionStringExpression;

        internal IMethodSymbol GetConnectionStringAsync { get; } = getConnectionStringAsync;

        internal INamedTypeSymbol ResourceExtensions { get; } = resourceExtensions;

        internal INamedTypeSymbol ConnectionStringReference { get; } = connectionStringReference;
    }

    private sealed class ConnectionStringAccess(
        IOperation operationBlock,
        ILocalSymbol? local,
        TextSpan localReferenceSpan,
        Location location,
        string memberName,
        string remediation)
    {
        internal IOperation OperationBlock { get; } = operationBlock;

        internal ILocalSymbol? Local { get; } = local;

        internal TextSpan LocalReferenceSpan { get; } = localReferenceSpan;

        internal Location Location { get; } = location;

        internal string MemberName { get; } = memberName;

        internal string Remediation { get; } = remediation;
    }

    /// <summary>
    /// Computes whether tracked locals definitely contain a projection-aware value at each access.
    /// </summary>
    private sealed class ResolvedLocalFlowAnalyzer
    {
        private readonly ConnectionStringSymbols _symbols;
        private readonly ConnectionStringAccess[] _accesses;
        private readonly Dictionary<ILocalSymbol, int> _localIndexes;

        internal ResolvedLocalFlowAnalyzer(
            ConnectionStringSymbols symbols,
            IEnumerable<ConnectionStringAccess> accesses)
        {
            _symbols = symbols;
            _accesses = [.. accesses];
            _localIndexes = new(SymbolEqualityComparer.Default);

            foreach (var access in _accesses)
            {
                if (access.Local is { } local && !_localIndexes.ContainsKey(local))
                {
                    _localIndexes.Add(local, _localIndexes.Count);
                }
            }
        }

        internal HashSet<ConnectionStringAccess> FindSafeAccesses(ControlFlowGraph controlFlowGraph)
        {
            var inputs = new bool[controlFlowGraph.Blocks.Length][];
            var outputs = new bool[controlFlowGraph.Blocks.Length][];

            // This is a forward must analysis: a local is resolved at an access only when every path preserves
            // a resolver-derived value. Start non-entry blocks at the top value so loop back-edges converge
            // downward when any reachable path contains an unsafe write.
            for (var i = 0; i < controlFlowGraph.Blocks.Length; i++)
            {
                inputs[i] = CreateState(initialValue: true);
                outputs[i] = CreateState(initialValue: true);
            }

            bool changed;
            do
            {
                changed = false;

                foreach (var block in controlFlowGraph.Blocks)
                {
                    var input = GetInputState(block, outputs);
                    var output = (bool[])input.Clone();
                    ProcessBlock(block, output, safeAccesses: null);

                    if (!inputs[block.Ordinal].SequenceEqual(input))
                    {
                        inputs[block.Ordinal] = input;
                        changed = true;
                    }

                    if (!outputs[block.Ordinal].SequenceEqual(output))
                    {
                        outputs[block.Ordinal] = output;
                        changed = true;
                    }
                }
            }
            while (changed);

            var safeAccesses = new HashSet<ConnectionStringAccess>();
            foreach (var block in controlFlowGraph.Blocks)
            {
                var state = (bool[])inputs[block.Ordinal].Clone();
                ProcessBlock(block, state, safeAccesses);
            }

            return safeAccesses;
        }

        private bool[] GetInputState(BasicBlock block, bool[][] outputs)
        {
            if (block.Kind == BasicBlockKind.Entry || !block.IsReachable)
            {
                return CreateState(initialValue: false);
            }

            bool[]? input = null;
            foreach (var predecessor in block.Predecessors)
            {
                if (!predecessor.Source.IsReachable)
                {
                    continue;
                }

                if (input is null)
                {
                    input = (bool[])outputs[predecessor.Source.Ordinal].Clone();
                }
                else
                {
                    for (var i = 0; i < input.Length; i++)
                    {
                        input[i] &= outputs[predecessor.Source.Ordinal][i];
                    }
                }
            }

            return input ?? CreateState(initialValue: false);
        }

        private bool[] CreateState(bool initialValue)
        {
            var state = new bool[_localIndexes.Count];
            if (initialValue)
            {
                for (var i = 0; i < state.Length; i++)
                {
                    state[i] = true;
                }
            }

            return state;
        }

        private void ProcessBlock(
            BasicBlock block,
            bool[] state,
            HashSet<ConnectionStringAccess>? safeAccesses)
        {
            foreach (var operation in block.Operations)
            {
                ProcessOperation(operation, state, safeAccesses);
            }

            if (block.BranchValue is { } branchValue)
            {
                ProcessOperation(branchValue, state, safeAccesses);
            }
        }

        private void ProcessOperation(
            IOperation operation,
            bool[] state,
            HashSet<ConnectionStringAccess>? safeAccesses)
        {
            if (safeAccesses is not null &&
                operation is ILocalReferenceOperation localReference &&
                _localIndexes.TryGetValue(localReference.Local, out var localIndex))
            {
                foreach (var access in _accesses)
                {
                    if (SymbolEqualityComparer.Default.Equals(access.Local, localReference.Local) &&
                        access.LocalReferenceSpan == localReference.Syntax.Span &&
                        state[localIndex])
                    {
                        safeAccesses.Add(access);
                    }
                }
            }

            foreach (var child in operation.ChildOperations)
            {
                ProcessOperation(child, state, safeAccesses);
            }

            ApplyWrite(operation, state);
        }

        private void ApplyWrite(IOperation operation, bool[] state)
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator
                    when _localIndexes.TryGetValue(declarator.Symbol, out var declaratorIndex):
                    state[declaratorIndex] =
                        declarator.Initializer is { } initializer &&
                        IsResolvedValue(initializer.Value, state);
                    break;

                case ISimpleAssignmentOperation assignment
                    when TryGetDirectLocal(assignment.Target, out var assignedLocal) &&
                         _localIndexes.TryGetValue(assignedLocal, out var assignmentIndex):
                    state[assignmentIndex] =
                        IsResolvedValue(assignment.Value, state);
                    break;

                case ICompoundAssignmentOperation assignment:
                    InvalidateDirectLocal(assignment.Target, state);
                    break;

                case ICoalesceAssignmentOperation assignment:
                    InvalidateDirectLocal(assignment.Target, state);
                    break;

                case IDeconstructionAssignmentOperation assignment:
                    InvalidateTargetLocals(assignment.Target, state);
                    break;

                case IIncrementOrDecrementOperation increment:
                    InvalidateDirectLocal(increment.Target, state);
                    break;

                case IArgumentOperation argument
                    when argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out:
                    InvalidateDirectLocal(argument.Value, state);
                    break;
            }
        }

        private bool IsResolvedValue(IOperation? value, bool[] state)
        {
            value = UnwrapConversions(value);

            if (IsDirectlyResolvedValue(value, _symbols))
            {
                return true;
            }

            return value switch
            {
                ILocalReferenceOperation localReference
                    when _localIndexes.TryGetValue(localReference.Local, out var localIndex) =>
                    state[localIndex],
                IConditionalOperation conditional =>
                    IsResolvedValue(conditional.WhenTrue, state) &&
                    IsResolvedValue(conditional.WhenFalse, state),
                ICoalesceOperation coalesce =>
                    IsResolvedValue(coalesce.Value, state) &&
                    IsResolvedValue(coalesce.WhenNull, state),
                _ => false
            };
        }

        private void InvalidateDirectLocal(IOperation target, bool[] state)
        {
            if (TryGetDirectLocal(target, out var local) &&
                _localIndexes.TryGetValue(local, out var index))
            {
                state[index] = false;
            }
        }

        private void InvalidateTargetLocals(IOperation target, bool[] state)
        {
            if (target is ILocalReferenceOperation localReference &&
                _localIndexes.TryGetValue(localReference.Local, out var index))
            {
                state[index] = false;
                return;
            }

            foreach (var child in target.ChildOperations)
            {
                InvalidateTargetLocals(child, state);
            }
        }

        private static bool TryGetDirectLocal(IOperation operation, out ILocalSymbol local)
        {
            operation = UnwrapConversions(operation)!;
            if (operation is ILocalReferenceOperation localReference)
            {
                local = localReference.Local;
                return true;
            }

            local = null!;
            return false;
        }
    }
}
