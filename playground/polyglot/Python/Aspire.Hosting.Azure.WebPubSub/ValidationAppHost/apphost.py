# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # addAzureWebPubSub — factory method
    webpubsub = builder.add_azure_web_pub_sub("resource")
    # addHub — adds a hub to the Web PubSub resource (with optional hubName)
    hub = webpubsub.add_hub("resource")
    hub_with_name = webpubsub.add_hub("resource")
    # addEventHandler — adds an event handler to a hub
    hub.add_event_handler("resource")
    hub.add_event_handler("resource")
    # withRoleAssignments — assigns roles on a container resource
    container = builder.add_container("resource", "image")
    container.with_web_pub_sub_role_assignments()
    # withRoleAssignments — also available directly on AzureWebPubSubResource builder
    webpubsub.with_web_pub_sub_role_assignments()
    # withReference — generic, works via IResourceWithConnectionString
    container.with_reference()
    container.with_reference()
    builder.run()
