# TestShop

## Generate dashboard activity

The `activity-generator` resource starts stopped. Use **Start** on the resource in the
dashboard to continuously generate read-only traffic, and **Stop** to cancel it.

Four workers browse the frontend, fetch catalog images, and query the catalog through
the API gateway and directly. Each worker pauses for 500 ms between requests, producing
up to approximately eight requests per second. Requests produce distributed traces
and correlated logs without changing baskets, placing orders, or resetting data.

The generator uses Aspire service discovery, waits for its dependencies, and is
excluded from the deployment manifest.
