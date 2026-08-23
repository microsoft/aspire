# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import ReferenceExpression, create_builder


with create_builder() as builder:
    # addAzureWebPubSub — factory method
    webpubsub = builder.add_azure_web_pub_sub("resource")
    # addHub — adds a hub to the Web PubSub resource (with optional hubName)
    hub = webpubsub.add_hub("resource")
    hub_with_name = webpubsub.add_hub("resource")
    # addEventHandler — adds an event handler to a hub
    event_handler_url = ReferenceExpression.format_string("https://example.com/events")
    hub.add_event_handler(event_handler_url)
    hub.add_event_handler(
        event_handler_url,
        user_event_pattern="*",
        system_events=["connected", "disconnected"],
    )
    # with_web_pub_sub_role_assignments — assigns roles on a container resource
    container = builder.add_container("resource", "image")
    container.with_web_pub_sub_role_assignments(webpubsub, ["WebPubSubServiceReader"])
    # with_web_pub_sub_role_assignments — also available directly on AzureWebPubSubResource builder
    webpubsub.with_web_pub_sub_role_assignments(webpubsub, ["WebPubSubContributor"])
    # withReference — generic, works via IResourceWithConnectionString
    container.with_reference(webpubsub)
    container.with_reference(hub, connection_name="hub")
    builder.run()
