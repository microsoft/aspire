# Aspire Python validation AppHost
# Mirrors the top-level TypeScript playground surface with Python-style members.

from aspire_app import create_builder


with create_builder() as builder:
    # Test 1: Basic MongoDB resource creation (addMongoDB)
    mongo = builder.add_mongo_db("resource")
    # Test 2: Add database to MongoDB (addDatabase)
    mongo.add_database("resource")
    # Test 3: Add database with custom database name
    mongo.add_database("resource")
    # Test 4: Test withDataVolume
    builder.add_mongo_db("resource")
    # Test 5: Test withDataVolume with custom name
    builder.add_mongo_db("resource")
    # Test 6: Test withHostPort on MongoExpress
    builder.add_mongo_db("resource")
    # Test 7: Test withMongoExpress with container name
    builder.add_mongo_db("resource")
    # Test 8: Custom password parameter with addParameter
    custom_password = builder.add_parameter("parameter")
    builder.add_mongo_db("resource")
    # Test 9: Chained configuration - multiple With* methods
    mongo_chained = builder.add_mongo_db("resource")
    # Test 10: Add multiple databases to same server
    mongo_chained.add_database("resource")
    mongo_chained.add_database("resource")
    # ---- Property access on MongoDBServerResource ----
    _endpoint = mongo.primary_endpoint
    _host = mongo.host
    _port = mongo.port
    _uri = mongo.uri_expression
    _user_name = mongo.user_name_reference
    # Build and run the app
    _cstr = mongo.connection_string_expression
    _databases = None
    builder.run()
