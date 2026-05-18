// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius;

/// <summary>
/// Represents a Radius resource type reference comprising a fully-qualified
/// type string (for example <c>MyOrg.Custom/myRedis</c>) and an API version.
/// </summary>
/// <remarks>
/// Encodes the "version requires a type" invariant in the type system so that
/// callers cannot specify an API version without also specifying a type.
/// </remarks>
/// <param name="Type">
/// The fully-qualified Radius resource type, in the form
/// <c>&lt;Namespace&gt;/&lt;TypeName&gt;</c> (for example <c>Radius.Data/redisCaches</c>).
/// </param>
/// <param name="ApiVersion">
/// The API version to use for this resource type. When <see langword="null"/>, the
/// integration's default Radius API version is used.
/// </param>
public sealed record RadiusResourceTypeReference(string Type, string? ApiVersion = null);
