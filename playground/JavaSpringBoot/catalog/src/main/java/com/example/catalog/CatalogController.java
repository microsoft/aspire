package com.example.catalog;

import java.util.List;
import java.util.Map;

import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class CatalogController {

	private static final List<Map<String, Object>> Products = List.of(
		Map.of("id", 1, "name", "Aspire Mug", "price", 12.50),
		Map.of("id", 2, "name", "Aspire Hoodie", "price", 48.00),
		Map.of("id", 3, "name", "Aspire Stickers", "price", 4.25));

	@GetMapping("/")
	public String index() {
		return "Catalog service (Maven + spring-boot:run)";
	}

	@GetMapping("/products")
	public List<Map<String, Object>> products() {
		return Products;
	}
}
