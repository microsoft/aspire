cmake_minimum_required(VERSION 3.20)

project(cpp_api LANGUAGES CXX)

find_package(httplib CONFIG REQUIRED)

add_executable(cpp-api main.cpp)

target_compile_features(cpp-api PRIVATE cxx_std_20)
target_link_libraries(cpp-api PRIVATE httplib::httplib)

install(TARGETS cpp-api RUNTIME DESTINATION bin)
