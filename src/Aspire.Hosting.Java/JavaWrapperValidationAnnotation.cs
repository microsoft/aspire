// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Marks that a resource has already registered a deferred check that its build tool wrapper exists.
/// </summary>
/// <remarks>
/// The check cannot run while the AppHost is being authored, because <c>WithWrapperPath</c> may still
/// supply a wrapper that the default location lacks. This annotation keeps the subscription from being
/// registered more than once per tool when several builder methods ask for the same validation.
/// </remarks>
internal sealed record JavaWrapperValidationAnnotation(JavaBuildTool Tool) : IResourceAnnotation;
