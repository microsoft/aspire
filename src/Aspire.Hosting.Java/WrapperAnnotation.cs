// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Stores the wrapper path for Maven or Gradle build tools.
/// </summary>
internal sealed class WrapperAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the path to the wrapper executable (mvnw, mvnw.cmd, gradlew, gradlew.bat).
    /// </summary>
    public required string WrapperPath { get; init; }
}
