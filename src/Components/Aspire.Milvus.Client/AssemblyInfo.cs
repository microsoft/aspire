// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Milvus.Client;

[assembly: ConfigurationSchema("Aspire:Milvus:Client", typeof(MilvusClientSettings))]
[assembly: ConfigurationSchema("Aspire:Milvus:Client:*", typeof(MilvusClientSettings))]

[assembly: LoggingCategories("Milvus.Client")]
