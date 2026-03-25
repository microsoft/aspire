# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # Test 1: Basic dev tunnel resource creation (addDevTunnel)
    tunnel = builder.add_dev_tunnel("resource")
    # Test 2: addDevTunnel with tunnelId option
    tunnel2 = builder.add_dev_tunnel("resource")
    # Test 3: withAnonymousAccess
    builder.add_dev_tunnel("resource")
    # Test 4: Add a container to reference its endpoints
    web = builder.add_container("resource", "image")
    # Test 5: withTunnelReference with EndpointReference (expose a specific endpoint)
    web_endpoint = web.get_endpoint("default")
    tunnel.with_tunnel_reference()
    # Test 6: withTunnelReferenceAnonymous with EndpointReference + allowAnonymous
    web2 = builder.add_container("resource", "image")
    web2_endpoint = web2.get_endpoint("default")
    tunnel2.with_tunnel_reference_anonymous()
    # Test 7: withTunnelReferenceAll - expose all endpoints on a resource
    tunnel3 = builder.add_dev_tunnel("resource")
    web3 = builder.add_container("resource", "image")
    tunnel3.with_tunnel_reference_all()
    # Test 8: getTunnelEndpoint - get the public tunnel endpoint for a specific endpoint
    web4 = builder.add_container("resource", "image")
    web4_endpoint = web4.get_endpoint("default")
    tunnel4 = builder.add_dev_tunnel("resource")
    tunnel4.with_tunnel_reference()
    _tunnel_endpoint = tunnel4.get_tunnel_endpoint()
    # Test 9: addDevTunnel with the dedicated polyglot parameters
    tunnel5 = builder.add_dev_tunnel("resource")
    web5 = builder.add_container("resource", "image")
    web5_endpoint = web5.get_endpoint("default")
    tunnel5.with_tunnel_reference_anonymous()
    # Test 10: Chained configuration
    builder.add_dev_tunnel("resource")
    builder.run()
