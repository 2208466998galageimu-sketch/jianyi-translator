cmake_minimum_required(VERSION 3.20)
project(Thanslator.LocalMtHost LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The native build uses the locally built CPU-only CTranslate2 + SentencePiece.
# Set CT2_INSTALL_ROOT / SP_INSTALL_ROOT when configuring if they live elsewhere.
set(CT2_INSTALL_ROOT "" CACHE PATH "CTranslate2 install prefix")
set(SP_INSTALL_ROOT "" CACHE PATH "SentencePiece install prefix")

if (NOT CT2_INSTALL_ROOT OR NOT SP_INSTALL_ROOT)
    message(FATAL_ERROR "Specify CT2_INSTALL_ROOT and SP_INSTALL_ROOT; see tools/LocalMtHost/README.md")
endif()

list(APPEND CMAKE_PREFIX_PATH "${CT2_INSTALL_ROOT}")
find_package(ctranslate2 CONFIG REQUIRED)

add_executable(Thanslator.LocalMtHost
    main.cpp
)

target_include_directories(Thanslator.LocalMtHost PRIVATE
    "${SP_INSTALL_ROOT}/include"
)

# When CMake is launched from a Developer Command Prompt, make the Windows SDK
# include paths explicit as well; some Build Tools installations do not expose
# them reliably through the NMake environment.
if (DEFINED ENV{WindowsSdkDir} AND DEFINED ENV{WindowsSDKVersion})
    string(REGEX REPLACE "\\\\$" "" SDK_VERSION "$ENV{WindowsSDKVersion}")
    file(TO_CMAKE_PATH "$ENV{WindowsSdkDir}" SDK_ROOT)
    target_include_directories(Thanslator.LocalMtHost PRIVATE
        "${SDK_ROOT}/Include/${SDK_VERSION}/ucrt"
        "${SDK_ROOT}/Include/${SDK_VERSION}/um"
        "${SDK_ROOT}/Include/${SDK_VERSION}/shared"
    )
endif()

target_link_libraries(Thanslator.LocalMtHost PRIVATE
    CTranslate2::ctranslate2
    "${SP_INSTALL_ROOT}/lib/sentencepiece.lib"
)

if (MSVC)
    target_compile_options(Thanslator.LocalMtHost PRIVATE /W4 /utf-8)
endif()

# Copy the runtime DLL next to the host in the build tree.
add_custom_command(TARGET Thanslator.LocalMtHost POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CT2_INSTALL_ROOT}/bin/ctranslate2.dll"
        "$<TARGET_FILE_DIR:Thanslator.LocalMtHost>/ctranslate2.dll"
)
