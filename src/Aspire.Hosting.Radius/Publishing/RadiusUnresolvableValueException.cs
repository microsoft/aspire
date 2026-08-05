// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Thrown when one specific fragment of a container environment value cannot be produced at publish
/// time, so the publisher can skip that single variable without also swallowing unrelated failures.
/// </summary>
/// <remarks>
/// <para>
/// The env-var resolution loop in <c>RadiusInfrastructureBuilder</c> used to wrap every value in
/// <c>catch (InvalidOperationException)</c>. That is far broader than intended: it also swallowed
/// genuine publish errors, so whether a bug surfaced depended on the exception's <em>type</em>
/// rather than on the publisher having decided the value was legitimately unavailable.
/// </para>
/// <para>
/// The two cases the publisher does intend to skip both raise <see cref="InvalidOperationException"/>
/// from framework code it does not own:
/// </para>
/// <list type="number">
/// <item>A reference to an endpoint that is not defined on the target resource
/// (<c>EndpointReference.EndpointAnnotation</c>).</item>
/// <item>A reference to an output of a resource whose values are only known after its own
/// deployment — for example an Azure Bicep output, which throws
/// <c>"...has no value..."</c> until the deployment that produces it has run. Radius cannot
/// deploy those resources, so the value is genuinely unavailable at publish time. The skip is
/// gated on the value positively declaring deployment-substituted semantics by implementing
/// <see cref="IManifestExpressionProvider"/>; a plain <see cref="IValueProvider"/> that raises
/// <see cref="InvalidOperationException"/> for a genuine invalid state fails the publish.</item>
/// </list>
/// <para>
/// Both are detected at their exact call site and re-thrown as this type, so the loop's catch names
/// the condition rather than a type that any other failure could also share.
/// </para>
/// </remarks>
internal sealed class RadiusUnresolvableValueException : Exception
{
    public RadiusUnresolvableValueException(IResource owner, string reason, Exception? innerException = null)
        : base(reason, innerException)
    {
        Owner = owner;
    }

    /// <summary>Gets the resource whose environment value could not be resolved.</summary>
    public IResource Owner { get; }
}
