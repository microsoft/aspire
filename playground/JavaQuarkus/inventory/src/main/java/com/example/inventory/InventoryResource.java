package com.example.inventory;

import java.util.List;
import java.util.Map;

import jakarta.ws.rs.GET;
import jakarta.ws.rs.Path;
import jakarta.ws.rs.Produces;
import jakarta.ws.rs.core.MediaType;

import org.eclipse.microprofile.rest.client.inject.RestClient;

@Path("/")
public class InventoryResource {

    private static final Map<String, String> CATALOG = Map.of(
        "ASP-1000", "Aspire mug",
        "ASP-1001", "Aspire hoodie",
        "ASP-1002", "Aspire stickers");

    private final PricingClient pricing;

    public InventoryResource(@RestClient PricingClient pricing) {
        this.pricing = pricing;
    }

    @GET
    @Produces(MediaType.TEXT_PLAIN)
    public String index() {
        return "Inventory service (Maven + quarkus:dev). Try /inventory.";
    }

    @GET
    @Path("/inventory")
    @Produces(MediaType.APPLICATION_JSON)
    public List<InventoryItem> inventory() {
        // Calling the other service is what produces a trace spanning both of them, which is the point of
        // running two resources rather than one.
        return CATALOG.entrySet().stream()
            .sorted(Map.Entry.comparingByKey())
            .map(entry -> new InventoryItem(
                entry.getKey(),
                entry.getValue(),
                10 + Math.abs(entry.getKey().hashCode() % 40),
                pricing.price(entry.getKey())))
            .toList();
    }
}
