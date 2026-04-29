// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.DurableTask.AzureManaged;

[assembly: ConfigurationSchema(
    "Aspire:Microsoft:DurableTask:AzureManaged",
    typeof(DurableTaskSchedulerSettings))]

[assembly: LoggingCategories(
    "Microsoft.DurableTask",
    "Microsoft.DurableTask.Client",
    "Microsoft.DurableTask.Worker")]
