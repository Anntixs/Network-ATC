# The EuroScope plugin bridge of Network-ATC. Plugins are 32-bit MSVC DLLs, so this builds for Win32 only:
#   cmake -S native/EsBridge -B build/esbridge -A Win32 && cmake --build build/esbridge --config Release
cmake_minimum_required(VERSION 3.20)
project(NetworkAtcEsBridge CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
  message(FATAL_ERROR "EuroScope plugins are 32-bit: configure with -A Win32")
endif()

# /EHa: structured exceptions (a crash inside a plugin) are caught as C++ exceptions.
add_compile_options(/W4 /EHa /utf-8 /permissive-)

# The DLL the plugins link to. Its file name must be exactly EuroScopePlugInDll.dll.
add_library(EuroScopePlugInDll SHARED src/Engine.cpp src/ApiData.cpp src/ApiPlugIn.cpp)
target_compile_definitions(EuroScopePlugInDll PRIVATE NATC_ES_BRIDGE WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
target_include_directories(EuroScopePlugInDll PUBLIC include)
target_link_libraries(EuroScopePlugInDll PRIVATE user32 gdi32)

add_executable(NetworkAtc.EsHost WIN32 host/main.cpp)
target_link_libraries(NetworkAtc.EsHost PRIVATE EuroScopePlugInDll)

# A plugin for the tests, built against our header like a real plugin against EuroScope's.
add_library(TestPlugin SHARED testplugin/TestPlugin.cpp)
target_compile_definitions(TestPlugin PRIVATE _CRT_SECURE_NO_WARNINGS)
target_link_libraries(TestPlugin PRIVATE EuroScopePlugInDll gdi32 user32)
