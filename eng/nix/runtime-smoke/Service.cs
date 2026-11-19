// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/", () => "aspire-nix-runtime-ok");
app.Run();
