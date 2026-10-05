package com.example.orders;

import java.util.List;
import java.util.Map;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.client.RestTemplate;

@RestController
public class OrdersController {

	private final RestTemplate restTemplate;
	private final String catalogUrl;

	public OrdersController(
		RestTemplate restTemplate,
		// WithReference(catalog) projects the resolved endpoint as the environment variable
		// services__catalog__http__0. Spring's SystemEnvironmentPropertySource checks the literal name
		// before it tries any relaxed-binding transformation, so the Aspire name resolves as-is and
		// stays bindable from any other Spring configuration source. The default keeps the service
		// runnable outside the AppHost.
		@Value("${services__catalog__http__0:http://localhost:8080}") String catalogUrl) {
		this.restTemplate = restTemplate;
		this.catalogUrl = catalogUrl;
	}

	@GetMapping("/")
	public String index() {
		return "Orders service (Gradle + bootRun), catalog at " + catalogUrl;
	}

	@GetMapping("/orders")
	public Map<String, Object> orders() {
		// Calling across resources is what produces a distributed trace spanning both services, which is
		// the whole point of running the OpenTelemetry agent on each of them.
		//
		// RestTemplate can only be handed a raw Class token, so there is no way to ask it for a
		// List<Map<String, Object>> directly; the suppression covers the unchecked conversion that
		// assigning its raw List result to a parameterized type performs.
		@SuppressWarnings("unchecked")
		List<Map<String, Object>> products =
			restTemplate.getForObject(catalogUrl + "/products", List.class);

		return Map.of(
			"orderId", "ORD-1001",
			"catalog", products == null ? List.of() : products);
	}
}
