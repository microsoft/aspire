# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import MongoExpressContainerResource, create_builder


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
    # NOTE: An annotated function rather than a lambda. `configure_container` returns nothing while
    # `with_host_port` returns the builder, so a lambda would take on that return type and not match.
    def configure_mongo_express(container: MongoExpressContainerResource) -> None:
        container.with_host_port(port=8082)

    builder.add_mongo_db("mongo-express").with_mongo_express(configure_container=configure_mongo_express)
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
    # Test 12: Test with_replica_set with with_key_file, with_tls_mode and with_tls_allow_invalid_certificates
    key_file_param = builder.add_parameter("rs-keyfile", secret=True, value="my-secret-key")
    builder.add_mongo_db("mongo-rs-member").with_replica_set("rs0").with_key_file(key_file_param, key_file_path="/etc/rs.key").with_tls_mode().with_tls_allow_invalid_certificates()
    # Test 13: Test add_mongo_db_replica_set with with_member
    # NOTE: The members are not given a key file of their own here. with_member gives them the replica set's shared one,
    # and a member carrying a different key file is rejected.
    mongo1 = builder.add_mongo_db("mongo-rs-1")
    mongo2 = builder.add_mongo_db("mongo-rs-2")
    replica_set = builder.add_mongo_db_replica_set("rs0").with_member(mongo1).with_member(mongo2)
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
