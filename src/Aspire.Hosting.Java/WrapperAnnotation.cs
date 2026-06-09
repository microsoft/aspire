// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Stores the wrapper path for Maven or Gradle build tools.
/// </summary>
internal sealed record WrapperAnnotation(string WrapperPath) : IResourceAnnotation;
