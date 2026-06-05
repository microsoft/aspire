// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript.Internal;

/// <summary>
/// Sentinel annotation written by every <c>PublishAs*</c> method, regardless of execution mode.
/// Used solely to detect "the user called more than one publish method on the same resource" —
/// a contradiction in their AppHost code, not a configuration error. The publish mode behavior
/// itself is still driven by <see cref="JavaScriptPublishModeAnnotation"/>, which is added only
/// in publish mode.
/// </summary>
internal sealed class JavaScriptPublishModeChoiceAnnotation(string methodName) : IResourceAnnotation
{
    /// <summary>
    /// Name of the publish method the user called (e.g. <c>"PublishAsStaticWebsite"</c>).
    /// Surfaced verbatim in the duplicate-call exception message.
    /// </summary>
    public string MethodName { get; } = methodName;
}
