# AudioRelayHM - Opus decoder native module

cmake_minimum_required(VERSION 3.16)
project(AudioRelayOpus2)

add_library(opus_decoder SHARED opus_decoder.cpp)

target_link_libraries(opus_decoder PUBLIC
    libnative_media_codecbase.so
    libnative_media_core.so
    libnative_media_acodec.so
    ace_napi.z
)

target_compile_options(opus_decoder PRIVATE
    -Wno-unused-variable
)
