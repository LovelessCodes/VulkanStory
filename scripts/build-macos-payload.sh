#!/usr/bin/env bash
# Builds the macOS arm64 runtime payload from this checkout into a fresh directory.
# Usage: bash build-macos-payload.sh <output-directory> [Debug|Release]
# Requires the .NET SDK, a developer-local Directory.Build.local.props pointing at an
# official client (compile-time references only), python3, and a Vulkan header root
# (VULKAN_SDK, or the Homebrew prefix) when the VMA bridge has not been built yet.
# Stages payload, proofs and layout; it never installs and never launches the game.
set -euo pipefail
out="${1:?usage: build-macos-payload.sh <output-directory> [Debug|Release]}"
configuration="${2:-Release}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
rid=osx-arm64
profile=vs-1.22.7-osx-arm64

if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    echo 'The macOS payload builds on Darwin arm64 only.' >&2
    exit 2
fi
if [ ! -f "$repo/Directory.Build.local.props" ]; then
    echo 'Copy Directory.Build.local.props.example to Directory.Build.local.props and point VintageStoryPath at an official client.' >&2
    exit 2
fi
if [ -e "$out" ] && [ -n "$(ls -A "$out" 2>/dev/null || true)" ]; then
    echo "Output directory is not fresh: $out" >&2
    exit 2
fi

bridge="$repo/artifacts/native-vma/$configuration/$rid/libVulkanStoryVma.dylib"
if [ ! -f "$bridge" ]; then
    sdk_root="${VULKAN_SDK:-$(brew --prefix 2>/dev/null || true)}"
    if [ -z "$sdk_root" ] || [ ! -f "$sdk_root/include/vulkan/vulkan.h" ]; then
        echo 'Set VULKAN_SDK (or install Homebrew vulkan-headers) so the VMA bridge can be built.' >&2
        exit 2
    fi
    bash "$repo/native/vma/build.sh" "$repo/artifacts/native-vma/$configuration/$rid" "$sdk_root" "$configuration"
fi

managed="$out/VulkanStory/managed"
native="$out/VulkanStory/native/$rid"
publish() {
    dotnet publish "$1" -c "$configuration" -r "$rid" --self-contained false -o "$2" \
        -p:DebugType=none -p:GenerateDocumentationFile=false >/dev/null
}

mkdir -p "$managed" "$native" "$out/Mods/vulkanstory" "$out/Mods/vulkanstoryinput" "$out/VulkanStory/tools/performance"
publish "$repo/src/VulkanStory.Bootstrap/VulkanStory.Bootstrap.csproj" "$managed"
publish "$repo/src/VulkanStory.Game/VulkanStory.Game.csproj" "$managed"
publish "$repo/src/VulkanStory.Mod/VulkanStory.Mod.csproj" "$out/Mods/vulkanstory"
publish "$repo/src/VulkanStory.Input.Companion/VulkanStory.Input.Companion.csproj" "$out/Mods/vulkanstoryinput"
# The external capture harness the in-game `.vulkanstory profile` command launches.
dotnet publish "$repo/tools/VulkanStory.PerformanceHarness/VulkanStory.PerformanceHarness.csproj" -c "$configuration" \
    --self-contained false -o "$out/VulkanStory/tools/performance" -p:DebugType=none >/dev/null
# Native shader corpus for the reduced capacity tier this platform selects: the
# runtime refuses a corpus whose recorded tier does not match the device's.
pack_root="$repo/artifacts/macos-shaders/$configuration"
dotnet run --project "$repo/tools/VulkanStory.Shaders.Compiler/VulkanStory.Shaders.Compiler.csproj" \
    -c "$configuration" -- --build "$repo/shaders/native" "$pack_root" --capacity-tier portability >/dev/null
cp -f "$pack_root/shaders-vk.pak" "$out/VulkanStory/shaders-vk.pak"
cp -f "$repo/src/VulkanStory.Mod/modinfo.json" "$out/Mods/vulkanstory/modinfo.json"
cp -f "$repo/src/VulkanStory.Input.Companion/modinfo.json" "$out/Mods/vulkanstoryinput/modinfo.json"
cp -f "$repo/packaging/loader.ini" "$out/VulkanStory/loader.ini"
cp -f "$repo/scripts/toggle-macos-runtime.sh" "$out/VulkanStory/tools/toggle-macos-runtime.sh"
chmod +x "$out/VulkanStory/tools/toggle-macos-runtime.sh"

# The deploy layout keeps native runtimes out of managed/, in the RID directory.
for library in libSDL3.dylib libshaderc_shared.dylib; do
    if [ -f "$managed/$library" ]; then mv -f "$managed/$library" "$native/$library"; fi
done
cp -f "$bridge" "$native/libVulkanStoryVma.dylib"

# Self-contained Vulkan runtime: the Khronos loader and MoltenVK with their
# notices. The ICD manifest keeps a manifest-relative library_path, which is how
# the loader resolves the implementation. A release build should take both from
# pinned sources instead of the local Homebrew installation.
loader_prefix="$(brew --prefix vulkan-loader 2>/dev/null || true)"
moltenvk_prefix="$(brew --prefix molten-vk 2>/dev/null || true)"
if [ -z "$loader_prefix" ] || [ -z "$moltenvk_prefix" ]; then
    echo 'The payload needs the Homebrew vulkan-loader and molten-vk formulae (or pinned equivalents).' >&2
    exit 2
fi
cp -f "$loader_prefix/lib/libvulkan.1.dylib" "$native/libvulkan.1.dylib"
cp -f "$moltenvk_prefix/lib/libMoltenVK.dylib" "$native/libMoltenVK.dylib"
cat > "$native/MoltenVK_icd.json" <<'JSON'
{
    "file_format_version" : "1.0.0",
    "ICD": {
        "library_path": "libMoltenVK.dylib",
        "api_version" : "1.4.0",
        "is_portability_driver" : true
    }
}
JSON
mkdir -p "$out/VulkanStory/licenses/native"
cp -f "$loader_prefix/LICENSE.txt" "$out/VulkanStory/licenses/native/vulkan-loader-LICENSE.txt"
cp -f "$moltenvk_prefix/LICENSE" "$out/VulkanStory/licenses/native/moltenvk-LICENSE.txt"
find "$managed" "$out/Mods" -name '*.pdb' -delete

python3 - "$out" "$profile" "$rid" <<'PY'
import hashlib, json, os, sys
out, profile, rid = sys.argv[1], sys.argv[2], sys.argv[3]
files = {}
for root, _, names in os.walk(out):
    for name in names:
        path = os.path.join(root, name)
        relative = os.path.relpath(path, out)
        if relative == 'VulkanStory/package.json':
            continue
        digest = hashlib.sha256()
        with open(path, 'rb') as stream:
            for chunk in iter(lambda: stream.read(1 << 20), b''):
                digest.update(chunk)
        files[relative] = digest.hexdigest().upper()
record = {'schema': 1, 'product': 'VulkanStory', 'profile': profile, 'rid': rid,
          'acceptance': 'unverified', 'files': dict(sorted(files.items()))}
with open(os.path.join(out, 'VulkanStory/package.json'), 'w') as stream:
    json.dump(record, stream, indent=2)
    stream.write('\n')
print(f'package.json: {len(files)} files, profile {profile}')
PY

echo "vulkanstory-payload: staged $out"
