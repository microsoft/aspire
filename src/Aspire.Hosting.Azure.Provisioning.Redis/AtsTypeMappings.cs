// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning;

[assembly: GenerateAspireProvisioningProxy(
    typeof(Azure.Provisioning.Redis.RedisResource),
    IncludeContainingAssemblyTypes = true,
    // The SDK's open-ended BinaryData dictionary cannot be projected to typed ATS values.
    ExcludedMemberNames = new[] { "AdditionalProperties" })]
