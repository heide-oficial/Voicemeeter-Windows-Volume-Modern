cmake_minimum_required(VERSION 3.24)

project(VMWVNativeHost LANGUAGES CXX RC)

add_executable(VMWV.NativeHost WIN32
    src/main.cpp
    src/audio_monitor.cpp
    src/settings.cpp
    src/voicemeeter_client.cpp
    VMWV.NativeHost.rc
    app.manifest
)

target_compile_features(VMWV.NativeHost PRIVATE cxx_std_20)
target_include_directories(VMWV.NativeHost PRIVATE src .)

target_compile_definitions(VMWV.NativeHost PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)

target_link_libraries(VMWV.NativeHost PRIVATE
    advapi32
    ole32
    shell32
    user32
    uuid
)

if(MSVC)
    set_property(TARGET VMWV.NativeHost PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

    target_compile_options(VMWV.NativeHost PRIVATE
        /W4
        /WX
        /permissive-
        /utf-8
        /GR-
        $<$<CONFIG:Release>:/O1>
        $<$<CONFIG:Release>:/GL>
        $<$<CONFIG:Release>:/Gy>
    )

    target_link_options(VMWV.NativeHost PRIVATE
        $<$<CONFIG:Release>:/LTCG>
        $<$<CONFIG:Release>:/OPT:REF>
        $<$<CONFIG:Release>:/OPT:ICF>
    )
endif()
