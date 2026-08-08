// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Seq;

[assembly: ConfigurationSchema("Aspire:Seq", typeof(SeqSettings))]
[assembly: ConfigurationSchema("Aspire:Seq:*", typeof(SeqSettings))]

[assembly: LoggingCategories("Seq")]
