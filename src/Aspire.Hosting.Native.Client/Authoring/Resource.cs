// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Contains authoring data only; native runtime state is never stored on this object.</summary>
/// <param name="name">The logical native resource name.</param>
public abstract class Resource(string name)
{
    /// <summary>Gets the resource's declared name.</summary>
    public string Name { get; } = name;
}

/// <summary>Contains a generic container declaration, not a running container.</summary>
/// <param name="name">The logical resource name.</param>
/// <param name="image">The image selected by the integration author.</param>
public class ContainerResource(string name, string image) : Resource(name)
{
    /// <summary>Gets the declared image.</summary>
    public string Image { get; } = image;
}

/// <summary>Contains a host executable declaration.</summary>
/// <param name="name">The logical resource name.</param>
/// <param name="executablePath">The absolute executable path.</param>
/// <param name="workingDirectory">The absolute working directory.</param>
public sealed class ExecutableResource(string name, string executablePath, string workingDirectory) : Resource(name)
{
    /// <summary>Gets the executable path.</summary>
    public string ExecutablePath { get; } = executablePath;
    /// <summary>Gets the working directory.</summary>
    public string WorkingDirectory { get; } = workingDirectory;
}

/// <summary>Contains parameter authoring intent; generated credentials remain native-owned.</summary>
/// <param name="name">The native parameter name.</param>
/// <param name="secret">Whether payloads require native secret treatment.</param>
public sealed class ParameterResource(string name, bool secret) : Resource(name)
{
    /// <summary>Gets whether this is a secret parameter.</summary>
    public bool IsSecret { get; } = secret;
}

/// <summary>Exposes a symbolic connection reference, never resolved credentials.</summary>
public interface IResourceWithConnectionString
{
    /// <summary>Gets the connection expression materialized by the native server.</summary>
    ReferenceExpression ConnectionStringExpression { get; }
}

/// <summary>Provides typed fluent composition over a transport command plan.</summary>
public interface IResourceBuilder<out T> where T : Resource
{
    /// <summary>Gets the inert authored resource.</summary>
    T Resource { get; }
    /// <summary>Gets the shared authoring builder.</summary>
    DistributedApplicationBuilder ApplicationBuilder { get; }
}

/// <summary>Records symbolic syntax. Resolution and secret substitution occur only in the native server.</summary>
public sealed class ReferenceExpression
{
    internal Func<NativeBuildContext, Task<Client.DeclaredValue>> Materialize { get; }
    internal ReferenceExpression(Func<NativeBuildContext, Task<Client.DeclaredValue>> materialize) => Materialize = materialize;
    /// <summary>Creates literal syntax without resolving resource values.</summary>
    /// <param name="value">Literal authoring text, not a resolved reference.</param>
    /// <returns>Native-materialized literal syntax.</returns>
    public static ReferenceExpression Literal(string value) => new(context => context.Composition.CreateLiteralValueAsync(value, context.Token));
    /// <summary>Composes syntax without converting references or credentials into strings.</summary>
    /// <param name="parts">Symbolic components in order.</param>
    /// <returns>A native concatenation expression.</returns>
    public static ReferenceExpression Create(params ReferenceExpression[] parts)
    {
        if (parts.Length == 0)
        {
            throw new ArgumentException("A reference expression needs at least one part.", nameof(parts));
        }
        var syntax = parts.ToArray();

        return new(async context =>
        {
            var result = await context.Value(syntax[0]).ConfigureAwait(false);
            foreach (var part in syntax.Skip(1))
            {
                result = await result.ConcatValueAsync(await context.Value(part).ConfigureAwait(false), context.Token).ConfigureAwait(false);
            }

            return result;
        });
    }
    /// <summary>Records URI segment encoding performed natively at materialization.</summary>
    /// <returns>Encoded native expression syntax.</returns>
    public ReferenceExpression EncodeUri() => new(async context =>
        await (await context.Value(this).ConfigureAwait(false)).EncodeUriValueAsync(context.Token).ConfigureAwait(false));
    /// <summary>Converts literal authoring text to syntax.</summary>
    /// <param name="value">Literal authoring text.</param>
    /// <returns>A native literal expression.</returns>
    public static implicit operator ReferenceExpression(string value) => Literal(value);
    /// <summary>Never exposes value contents through diagnostics.</summary>
    /// <returns>A non-sensitive diagnostic classification.</returns>
    public override string ToString() => "[native reference expression]";
}

/// <summary>Provides late-bound named endpoint syntax rather than a local address.</summary>
public sealed class EndpointReference
{
    internal EndpointReference(Resource resource, string name)
    {
        Resource = resource;
        Name = name;
        Host = Property("host");
        Port = Property("port");
        HostAndPort = Property("hostAndPort");
        ReferenceExpression Property(string property) => new(context =>
            context.Composition.CreateEndpointValueAsync(context.Declaration(Resource), Name, property, context.Token));
    }
    /// <summary>Gets the inert resource owning the named endpoint.</summary>
    public Resource Resource { get; }
    /// <summary>Gets the endpoint's authored name.</summary>
    public string Name { get; }
    /// <summary>Gets symbolic host syntax.</summary>
    public ReferenceExpression Host { get; }
    /// <summary>Gets symbolic port syntax.</summary>
    public ReferenceExpression Port { get; }
    /// <summary>Gets symbolic host-and-port syntax.</summary>
    public ReferenceExpression HostAndPort { get; }
}
