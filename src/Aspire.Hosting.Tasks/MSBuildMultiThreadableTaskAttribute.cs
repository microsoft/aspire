// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Build.Framework;

// Polyfill for MSBuild 18+ multi-threaded task opt-in while compiling against Microsoft.Build.Framework 17.9.5.
// MSBuild's TaskRouter recognizes this attribute by full type name.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class MSBuildMultiThreadableTaskAttribute : Attribute
{
}
