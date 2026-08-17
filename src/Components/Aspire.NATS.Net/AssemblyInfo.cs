// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.NATS.Net;

[assembly: ConfigurationSchema("Aspire:NATS:Net", typeof(NatsClientSettings))]
[assembly: ConfigurationSchema("Aspire:NATS:Net:*", typeof(NatsClientSettings))]

[assembly: LoggingCategories("NATS")]
