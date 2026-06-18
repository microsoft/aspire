// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// A resolved identity value tagged with the layer that produced it. The
/// tag is the only signal an operator has for "is my override actually
/// taking effect?", so every resolved field carries one.
/// </summary>
internal readonly record struct IdentityValue<T>(T Value, IdentitySource Source);
