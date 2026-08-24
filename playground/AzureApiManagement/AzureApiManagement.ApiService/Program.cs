// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new
{
    service = "catalog",
    endpoints = new[] { "/products", "/products/{id}" },
}));
app.MapGet("/products", () => Products.All);
app.MapGet("/products/{id:int}", (int id) =>
    Products.All.FirstOrDefault(product => product.Id == id) is { } product
        ? Results.Ok(product)
        : Results.NotFound());

app.Run();

internal static class Products
{
    public static readonly Product[] All =
    [
        new(1, "Mechanical keyboard", 129.00m),
        new(2, "Vertical mouse", 79.00m),
        new(3, "USB-C dock", 199.00m),
    ];
}

internal sealed record Product(int Id, string Name, decimal Price);
