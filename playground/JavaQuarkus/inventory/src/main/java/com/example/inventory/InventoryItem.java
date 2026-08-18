package com.example.inventory;

public record InventoryItem(String sku, String name, int quantity, Price price) {
}
