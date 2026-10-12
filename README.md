# VulkanStory

A Vulkan renderer, SDL3 window and input layer, and modern upscaling / frame
generation for **Vintage Story 1.22.7**, delivered as a mod for the official,
unmodified game.

> **Current release: [v0.1.3](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.3)** for Windows x64. Known defects and
> remaining work are tracked in the [roadmap](docs/ROADMAP.md).

## Features

- **Native Vulkan 1.3 renderer** replacing the game's OpenGL path: frame graph,
  bindless textures, asynchronous pipeline compilation with a persistent pipeline
  cache, and a native shader corpus compiled offline to SPIR-V.
- **Temporal rendering**: native TAA with per-object, sky and liquid motion vectors,
  plus GTAO ambient occlusion alongside the vanilla SSAO path.
- **Upscaling**: NVIDIA DLSS, AMD FSR 3.1 and FSR 4, and Intel XeSS, selected in the
  game's own Options menu, with automatic fallback when a provider is unavailable.
- **Frame generation**: DLSS-G (via NVIDIA Streamline), FSR 3 frame generation and
  XeSS-FG, with latency control (Reflex / PC Latency, Anti-Lag, XeLL).
- **SDL3 window and input**: high-DPI aware windowing, raw mouse, touch, and full
  gamepad support with remapping, a radial menu, on-screen glyphs and analog movement
  (with an optional server-side input companion).
- **Performance**: optional smoothness patches (culling, chunk streaming, entities,
  singleplayer worldgen, inventory, lighting, physics, chisel LOD and world-map caching),
  plus terrain/entity occlusion, indexed visibility traversal and stock cloud updates.
  Optional algorithms are switchable on the Performance options page.
- **Performance inspection**: in-game captures with offline flame graphs, allocation
  samples, frame/system timings, CPU/memory metrics, monitor contention and Speedscope export.
- **Initial OpenXR stereo rendering** with tracked headset views and a desktop mirror,
  enabled from the Device options page.
- **Extension points** for other mods to add renderer passes. A separate
  compatibility module for translating supported OpenGL usage by other mods is planned.

## Requirements

- Vintage Story **1.22.7** (official installation). Other versions are refused at startup.
- **Windows 10/11 x64** with a Vulkan 1.3 capable GPU and driver.
  Linux is not part of the current release.
- Some vendor features need matching hardware: DLSS and DLSS-G need an NVIDIA RTX GPU,
  and XeSS-FG needs a supported DX12 GPU and driver (including non-Intel GPUs with
  Shader Model 6.4). The FSR and XeSS upscalers run across vendors; unsupported
  upscalers fall back automatically.

## Installation (players)

The small **Vulkan Story Installer** mod can download and install the runtime for
you. Put its ZIP in `Mods`, enter a world, choose **Install**, then close and
restart the game when it finishes. The installer is for fresh installations and
does not overwrite existing files. See the [player guide](docs/player-guide.md)
for the full procedure and troubleshooting.

VulkanStory starts **before** the game creates its window, so it is installed beside
the game executable, not only into `Mods`.

1. Close the game.
2. Download **VulkanStory-win-x64.zip** from the [v0.1.3 release](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.3).
   Extract it into the folder that contains `Vintagestory.exe`, keeping
   the folder layout. This adds `hostfxr.dll`, `VulkanStory/`, `Mods/vulkanstory` and
   `Mods/vulkanstoryinput`. The game's own files are not modified.
3. Start the game with your normal shortcut.

Updating, disabling, removal and coexistence with other DLL proxies are described in
the [client README](packaging/README-client.txt) (Windows) and the
[Linux client README](packaging/README-linux-client.txt). In short:

- **Update**: use `VulkanStory/tools/deploy-runtime.ps1`. It verifies ownership and
  backs up everything it replaces.
- **Disable**: turn the mod off in the mod manager, or set `Enabled=0` under
  `[Bootstrap]` in `VulkanStory/loader.ini` to skip early startup entirely.
- **Remove**: use `VulkanStory/tools/remove-runtime.ps1`. Saves and settings stay.

In game, renderer options live in the regular **Options** menu. Chat commands:
`.vulkanstory settings`, `.vulkanstory status`, `.vulkanstory controller`.

To inspect performance, run `.vulkanstory profile start 30` in a loaded world.
Use `.vulkanstory profile stop` to finish early and `.vulkanstory profile status`
to find the result. Open `report.html` under `%LOCALAPPDATA%\VulkanStory\Performance`.
Captures last 1–300 seconds. Allocation samples are estimates; native/wait samples
are not CPU time, and GPU execution time is not measured.

When reporting a problem, include `%LOCALAPPDATA%\VulkanStory\Logs\bootstrap-*.jsonl`
and the game's `Logs/client-main.log`.

## Building from source

Prerequisites:

- .NET SDK 10.0.100 or newer (see `global.json`)
- An official Vintage Story 1.22.7 installation. Copy
  `Directory.Build.local.props.example` to `Directory.Build.local.props` and point
  `VintageStoryPath` at it. Game assemblies are only compiled against, never shipped.
- CMake and Ninja (native bootstrap), the Vulkan SDK, and MinGW-w64 `g++`/`gcc`
  (provider bridges)
- Visual Studio 2022 or 2026 with x64 C++ build tools (the corrected FidelityFX
  Vulkan runtime and OpenXR loader)
- Vendor SDKs are pinned git submodules under `sdk/` (DLSS, FidelityFX for FSR 3
  and FSR 4, XeSS, Streamline 2.14.1, VMA, OpenXR). Clone with `--recurse-submodules`, or run
  `git submodule update --init`.

```powershell
# Vendor SDKs (submodules) and the Streamline 2.14.1 release runtimes (hash-pinned download)
git submodule update --init
pwsh scripts/fetch-streamline-release.ps1

# Managed projects, native bootstrap and the full shader corpus
pwsh scripts/build-runtime.ps1 -Configuration Release

# The five native provider bridges (NGX, FSR3, FSR4, XeSS-FG, Streamline); SDK roots default to sdk/
pwsh scripts/build-provider-bridges.ps1 -OutputDirectory <fresh-dir>

# Collect redistributables, stage a package and build the ZIPs
pwsh scripts/prepare-native-bundle.ps1 -BridgesDirectory <bridges> -CoreNativeDirectory ... -OutputDirectory <fresh-dir>
pwsh scripts/stage-runtime.ps1 -NativeDirectory ... -ShaderPack artifacts/runtime-shaders/Release/shaders-vk.pak -OutputDirectory <fresh-dir> ...
pwsh scripts/package-runtime.ps1 -StagingDirectory <stage> -OutputDirectory <fresh-dir>
```

Every script documents its parameters (`Get-Help scripts/<name>.ps1 -Full`). The
build and package scripts above never launch the game. Renderer checks normally run in the isolated
[headless harness](docs/headless-harness.md) against a world snapshot rather than in
your own installation.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/VulkanStory.Bootstrap` | Early managed startup, activated by the native `hostfxr.dll` proxy |
| `src/VulkanStory.Render.Vulkan` | Vulkan device, frame graph, pipelines, upscalers, frame generation, presentation, latency |
| `src/VulkanStory.Platform.Sdl` | SDL3 window, events and coordinates (game-independent) |
| `src/VulkanStory.Game` | Game integration: Harmony patches, graphics/platform adapters, temporal state, controller input |
| `src/VulkanStory.Mod` | Mod entry point, settings UI and chat commands |
| `src/VulkanStory.Contracts`, `src/Shared` | Shared contracts and settings choices |
| `src/VulkanStory.Input`, `src/VulkanStory.Input.Companion` | Analog movement protocol and the optional server companion |
| `native/` | Native bootstrap and provider bridges (NGX, FSR3, FSR4, XeSS-FG, Streamline) |
| `shaders/native` | Native GLSL shader corpus |
| `samples/` | Example shader pack; see [samples/shaderpacks](samples/shaderpacks/README.md) |
| `tools/` | Shader compiler, game-profile/preflight tools and the performance collector |
| `scripts/` | Build, stage, package, deploy and removal scripts |
| `sdk/` | Vendor SDKs as pinned submodules; `streamline-release-2.14.1/` is fetched, not tracked |
| `packaging/`, `profiles/` | Package inventories, notices and supported game profiles |
| `tests/` | Unit and integration tests |
| `docs/` | Roadmap, architecture, installation and harness documents |

## Documentation

- [Documentation index](docs/README.md): player and developer guides
- [Player guide](docs/player-guide.md): installer mod, manual installation, settings, commands and troubleshooting
- [Settings reference](docs/settings-reference.md): all renderer properties, defaults, ranges and live/restart behavior
- [Rendering](docs/rendering.md): frame flow, shaders, GPU lifetime, TAA, GTAO, reconstruction and frame generation
- [Window and controllers](docs/window-and-input.md): SDL input, profiles, gestures, GUI navigation and analog protocol
- [Performance](docs/performance.md): optimization behavior and interpreting captures
- [Mod integration](docs/mod-integration.md): render-pass API, attachments, motion writers and compatibility
- [Development](docs/development.md): builds, packages, source map and diagnostics
- [Roadmap](docs/ROADMAP.md): current status, known defects and remaining work
- [Architecture](docs/architecture.md): components, ownership, rendering contracts, mod compatibility
- [Bootstrap and installation](docs/bootstrap-and-installation.md): native activation, install/update/removal, Linux
- [Headless harness](docs/headless-harness.md): isolated captures, scheduled commands, scenarios and profiling limits

## Background

VulkanStory compiles against the official game assemblies, keeps its own state
outside game objects, and integrates through Harmony instead of a modified client.

## AI-assisted development

AI tools are used in the development of VulkanStory, including writing code,
debugging and documentation.

## License and attribution

Third-party notices for SDL, Shaderc, Silk.NET, PromptFont and the vendor SDK runtimes
are under [`packaging/notices`](packaging/notices) and ship with every package under
`VulkanStory/licenses`. Vendor SDKs are subject to their own license terms.

Vintage Story is © Anego Studios. This project is not affiliated with or endorsed by
Anego Studios, NVIDIA, AMD or Intel.
