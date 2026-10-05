package com.example.pricing;

import jakarta.ws.rs.GET;
import jakarta.ws.rs.Path;
import jakarta.ws.rs.PathParam;
import jakarta.ws.rs.Produces;
import jakarta.ws.rs.core.MediaType;

@Path("/")
public class PricingResource {

    @GET
    @Produces(MediaType.TEXT_PLAIN)
    public String index() {
        return "Pricing service (Gradle + quarkusDev). Try /pricing/ASP-1000.";
    }

    @GET
    @Path("/pricing/{sku}")
    @Produces(MediaType.APPLICATION_JSON)
    public Price price(@PathParam("sku") String sku) {
        // Derived from the SKU so the sample returns the same answer on every run, which keeps the
        // trace and log output comparable between runs.
        var amount = 10 + Math.abs(sku.hashCode() % 9000) / 100.0;

        return new Price(sku, Math.round(amount * 100) / 100.0, "USD");
    }
}
