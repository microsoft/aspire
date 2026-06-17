# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # Test 1: Basic MongoDB resource creation (addMongoDB)
    mongo = builder.add_mongo_db("mongo")
    # Test 2: Add database to MongoDB (addDatabase)
    mongo.add_database("mydb")
    # Test 3: Add database with custom database name
    mongo.add_database("db2", database_name="customdb2")
    # Test 4: Test with_data_volume
    builder.add_mongo_db("mongo-volume").with_data_volume()
    # Test 5: Test with_data_volume with custom name
    builder.add_mongo_db("mongo-volume-named").with_data_volume(name="mongo-data")
    # Test 6: Test with_host_port on MongoExpress
    builder.add_mongo_db("mongo-express").with_mongo_express(
        configure_container=lambda container: container.with_host_port(8082)
    )
    # Test 7: Test with_mongo_express with container name
    builder.add_mongo_db("mongo-express-named").with_mongo_express(container_name="my-mongo-express")
    # Test 8: Custom password parameter with add_parameter
    custom_password = builder.add_parameter("mongo-password", secret=True)
    builder.add_mongo_db("mongo-custom-pass", password=custom_password)
    # Test 9: Chained configuration - multiple with_* methods
    mongo_chained = builder.add_mongo_db("mongo-chained").with_persistent_lifetime().with_data_volume(name="mongo-chained-data")
    # Test 10: Add multiple databases to same server
    mongo_chained.add_database("app-db")
    mongo_chained.add_database("analytics-db", database_name="analytics")
    # Test 11: Test with_bind_ip_all
    builder.add_mongo_db("mongo-bind-all").with_bind_ip_all()
    # Test 12: Test with_replica_set
    mongo_rs = builder.add_mongo_db("mongo-rs").with_replica_set("rs0")
    # Test 13: Test with_tls with default mode
    builder.add_mongo_db("mongo-tls").with_tls()
    # Test 14: Test with_tls with specific mode
    builder.add_mongo_db("mongo-tls-allow").with_tls(mode="allow_tls")
    # Test 15: Test with_key_file for replica set member
    key_file_param = builder.add_parameter("rs-keyfile", secret=True, value="my-secret-key")
    builder.add_mongo_db("mongo-rs-secured").with_replica_set("rs-secure").with_key_file(key_file_param, "/etc/rs.key")
    # Test 16: Complete replica set with security - TLS + KeyFile + ReplicaSet
    tls_key_file_param = builder.add_parameter("rs-tls-key", secret=True, value="tls-secret")
    builder.add_mongo_db("mongo-rs-full").with_replica_set("rs-full").with_key_file(tls_key_file_param, "/etc/rs.key").with_tls(mode="require_tls")
    # ---- Property access on MongoDBServerResource ----
    _endpoint = mongo.primary_endpoint
    _host = mongo.host
    _port = mongo.port
    _uri = mongo.uri_expression
    _user_name = mongo.user_name_reference
    # Build and run the app
    _cstr = mongo.connection_string_expression
    _databases = mongo.databases
    builder.run()
