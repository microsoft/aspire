// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Azure.Provisioning.AppService;

[assembly: AspireExport(typeof(WebSite), ExposeProperties = true)]
[assembly: AspireExport(typeof(WebSiteSlot), ExposeProperties = true)]
