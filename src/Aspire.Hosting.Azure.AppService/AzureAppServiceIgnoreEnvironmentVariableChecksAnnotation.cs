// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an annotation that disables validation for environment variable names that Azure App Service normalizes.
/// </summary>
internal sealed class AzureAppServiceIgnoreEnvironmentVariableChecksAnnotation : IResourceAnnotation;
