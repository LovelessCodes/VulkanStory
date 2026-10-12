#!/usr/bin/env bash
# build.sh <output directory> [Vulkan SDK root] [Debug|Release]
# Mandatory Linux x64 or macOS arm64/x86_64 runtime. VMA headers come from sdk/vma,
# Vulkan headers from VULKAN_SDK/include. CXX selects the compiler. The C++ runtime
# is statically linked on Linux; no Vulkan loader is linked.
# Redistribution notice: sdk/vma/LICENSE.txt (MIT).
set -euo pipefail
out="${1:?usage: build.sh <output directory> [Vulkan SDK root] [Debug|Release]}"
vulkan_sdk="${2:-${VULKAN_SDK:-}}"
configuration="${3:-Release}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
if [ "$(uname -s)" = Darwin ]; then
    case "$(uname -m)" in
        arm64|x86_64) ;;
        *) echo 'The VMA macOS build requires arm64 or x86_64.' >&2; exit 2 ;;
    esac
elif [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
    echo 'The VMA build requires Linux x64 or macOS arm64/x86_64.' >&2
    exit 2
fi
if [ -z "$vulkan_sdk" ] || [ ! -f "$vulkan_sdk/include/vulkan/vulkan.h" ]; then
    echo 'Set VULKAN_SDK or pass a Vulkan SDK root containing include/vulkan/vulkan.h.' >&2
    exit 2
fi
if [ ! -f "$repo/sdk/vma/include/vk_mem_alloc.h" ]; then
    echo 'Missing sdk/vma submodule. Run git submodule update --init sdk/vma.' >&2
    exit 2
fi
case "$configuration" in
    Debug) optimization=(-O0 -g) ;;
    Release) optimization=(-O2 -DNDEBUG) ;;
    *) echo 'Configuration must be Debug or Release.' >&2; exit 2 ;;
esac
compiler="${CXX:-g++}"
if [ "$(uname -s)" = Darwin ]; then
    compiler="${CXX:-clang++}"
    target="$out/libVulkanStoryVma.dylib"
    link_flags=(-pthread)
else
    target="$out/libVulkanStoryVma.so"
    link_flags=(-static-libgcc -static-libstdc++ -pthread)
fi
command -v "$compiler" >/dev/null
mkdir -p "$out"
trap 'rm -f "$target.tmp"' EXIT
"$compiler" -std=c++17 "${optimization[@]}" -fPIC -shared -fvisibility=hidden \
    "${link_flags[@]}" -Wall -Wextra -Wno-unused-parameter \
    -I "$repo/sdk/vma/include" -I "$vulkan_sdk/include" "$here/bridge.cpp" -o "$target.tmp"
mv -f "$target.tmp" "$target"
mkdir -p "$out/licenses/vma"
cp "$repo/sdk/vma/LICENSE.txt" "$out/licenses/vma/LICENSE.txt"
echo "vulkanstory-vma: built $target"
