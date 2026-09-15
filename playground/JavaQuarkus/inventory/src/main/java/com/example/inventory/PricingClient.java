package com.example.inventory;

import jakarta.ws.rs.GET;
import jakarta.ws.rs.Path;
import jakarta.ws.rs.PathParam;
import jakarta.ws.rs.Produces;
import jakarta.ws.rs.core.MediaType;

import org.eclipse.microprofile.rest.client.inject.RegisterRestClient;

// The MicroProfile REST client is used rather than a plain HTTP client because quarkus-opentelemetry
// instruments it, so the call to pricing becomes a child span of the incoming inventory request instead
// of an untraced hole in the middle of the trace.
@RegisterRestClient(configKey = "pricing-api")
@Path("/pricing")
public interface PricingClient {

    @GET
    @Path("/{sku}")
    @Produces(MediaType.APPLICATION_JSON)
    Price price(@PathParam("sku") String sku);
}
