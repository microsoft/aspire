#include <cstdlib>
#include <iostream>

#include <httplib.h>

int main()
{
    const char* portValue = std::getenv("PORT");
    int port = portValue != nullptr ? std::atoi(portValue) : 8080;

    httplib::Server server;

    server.Get("/", [](const httplib::Request&, httplib::Response& response)
    {
        response.set_content("Hello from C++ on Aspire!\n", "text/plain");
    });

    server.Get("/health", [](const httplib::Request&, httplib::Response& response)
    {
        response.set_content("Healthy\n", "text/plain");
    });

    std::cout << "C++ API listening on port " << port << std::endl;
    server.listen("0.0.0.0", port);
}

