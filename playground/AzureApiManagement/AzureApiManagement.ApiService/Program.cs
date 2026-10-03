// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/openapi/v1.json", () => Results.Text(
    """
    {
      "openapi": "3.0.1",
      "info": {
        "title": "Catalog API",
        "version": "v1"
      },
      "paths": {
        "/products": {
          "get": {
            "operationId": "getProducts",
            "responses": {
              "200": {
                "description": "The catalog products."
              }
            }
          }
        },
        "/products/{id}": {
          "get": {
            "operationId": "getProduct",
            "parameters": [
              {
                "name": "id",
                "in": "path",
                "required": true,
                "schema": {
                  "type": "integer",
                  "format": "int32"
                }
              }
            ],
            "responses": {
              "200": {
                "description": "The requested product."
              },
              "404": {
                "description": "The product was not found."
              }
            }
          }
        }
      }
    }
    """,
    "application/json"));
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
