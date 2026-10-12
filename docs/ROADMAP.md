# Roadmap

Current release: [v0.1.3](https://github.com/KillerPixelCrew/VulkanStory/releases/tag/v0.1.3) (Windows x64, Vintage Story 1.22.7).

## Done

- Vulkan renderer, SDL3 window and input
- TAA, render scale, SSAO / GTAO
- Upscaling: DLSS, FSR 3.1, FSR 4 (INT8 path on NVIDIA and Intel), XeSS
- Stable geometry boundaries against sky and clouds with TAA and upscaling
- Frame generation: DLSS-G (up to 6×), FSR 3 FG, XeSS-FG
- Low latency: Reflex / PC Latency, Anti-Lag, XeLL
- Options menu integration, with clipped scrolling that moves labels, controls and their backgrounds together, and bounds checks for saved Graphics dropdown selections
- Controllers (remapping, radial menu, glyphs, analog movement + server companion), touch
- Screenshot, timelapse and AVI capture
- Render-pass extension points for other mods
- Windows install, update and removal
- Loading screen stays up until the first world frame is complete
- Shaders ship as one container, `shaders-vk.pak`
- **PERF-01: Smoothness ports** (ideas from Optimum, rebuilt as optional Harmony patches; each has a switch on the Performance settings page, all on by default)
  - Culling: skip foliage in the far shadow cascade, entity shadow distance cull, the occlusion culler's rays precomputed into one shared ray tree, particle and dropped-item distance gates
  - Chunk streaming: skip unchanged upload-queue sorting, tessellator mesh-part pool, parallel raw chunk reads in singleplayer
  - Entities: allocation-free animation checks, falling-block mesh fix, villager/trader outfit shape cache with outfit textures pre-inserted gradually after world load
  - Worldgen (singleplayer): the game's own per-pass worldgen workers, made safe with column footprint leases, a shared Vegetation/sunlight lock, per-generator locks and atomic entity ids, with a fallback to the chunk thread alone when other mods touch worldgen or a worker fails
- **PERF-03: GPU occlusion culling** (two switches on the Performance settings page, on by default)
  - Terrain chunk parts and entities hidden behind opaque terrain are left out of the main camera's draws, tested against a max-depth pyramid of the opaque terrain from a recent frame
  - Test boxes grow by the camera's movement since that depth was captured; boxes outside the captured view or crossing its near plane are always drawn; players are never culled
  - Shadow passes keep their own culling
- **PERF-04: Additional Optimum ports** (runtime controls on the Performance settings page)
  - Inventory render-info reuse, wind sample caching without slowing smoothing, dynamic-light scans and nearest selection, animated-block LOD without freezing distant machines, entity retessellation budgeting, BFS visibility, pathfinding node pooling with independent returned paths, entity light batching and SIMD frustum tests with full coefficient refresh and original precision.
  - Outfit animator caching with separate mutable poses per entity, parallel Terrain columns with independent workspaces and a reusable worldgen worker pool, dedicated chisel LOD pools with live distance control, bounded Vulkan world-map pages with a compressed disk cache, and physics collection reuse with reader leases and activation capacity batching without delaying simulation.
- **PERF-05: Performance inspection harness**: bounded EventPipe captures from `.vulkanstory profile start [seconds]`, stop/status; per-thread call stacks, sampled allocations, frame/system timings, searchable offline flame graphs and Speedscope export. GPU execution time is not measured.
- **PERF-06: Profile-driven optimizations**
  - Recurring costs: reusable occlusion hierarchy storage, descriptor comparisons without handle boxing, short chunk-map membership snapshots with visibility work outside the shared lock, indexed flood-fill traversal, and private coordinate reuse in the stock FluffyClouds updater. Optional algorithms have live controls.
  - Loading and streaming: background outfit metadata preparation, translation resolution caching with language/reload invalidation, bounded mesh-part capacity classes, independent static-shape templates for fruiting bushes, and pooled PCM accumulation with the original decoder and runtime unmute behavior.

- **PERF-07: Drop and particle budgets** (ideas from RAM-Garbage-Collector, rebuilt as optional patches)
  - Earlier merging of settled matching drops in singleplayer, with bounded work, player ownership checks and the game's automatic stack/collectible rules.
  - Live particle density, per-spawn and per-pool limits for cubes, quads and mod providers. Defaults: 50% density, 256 per spawn and 8,192 per pool; effects marked to ignore user configuration are preserved. Both features have live switches on the Performance page. No forced GC.

## Known defects

- **REN-08:** TAA / temporal reconstruction sometimes drops completely during play.
- **REN-09:** Allocation crashes and severe stalls during framebuffer rebuilds, with device-local use above its reported budget and nearly 20 GB process RAM reported. The reserved workspace covers full-resolution scene/OIT, TAA and GTAO targets before geometry growth; terrain pools share immutable quad indices, and abandoned game meshes retire through the GPU timeline. The reported lag and RAM growth remain open.
- **REN-10:** Additional Vulkan correctness defects in supported attachment, shader translation, mod API and provider transition paths.
- **REN-11:** AMD startup crashes after the main menu appears, reported on Radeon RX 6600.

## Planned

- **MOD-01: General mod compatibility**
  1. Preserve selected shader stages/includes and certify compiled motion outputs independently of asset origin; provide complete camera/object motion inputs to reconstruction and frame-generation providers.
  2. Extend ordinary RenderAPI motion histories to custom moving geometry and retain resource/state contracts across mod rendering and world transitions.
  3. Keep repeated input registration and optional entity discovery resilient, and exercise the complete supplied mod set without patches keyed to mod IDs.

- **PACK-01: Shader packs** ([contract design](shader-packs.md))
  1. Pack contract: replaceable programs, provided uniforms/samplers, readable and writable attachments, pass order, options. Decide which internal layouts become public API.
  2. Loader: `shaderpacks/` folder, pack selection and per-pack options in the Options menu, hot reload; compiled at runtime with the shipped shaderc.
  3. Per-program fallback to the built-in shaders on compile/link errors, with in-game error reporting.
  4. Pipeline cache keyed by the active pack.
  5. Pack-declared composite/deferred passes in the frame graph.

- **VR-01: OpenXR VR**
  - First stereo step: OpenXR session and eye swapchains on the renderer's Vulkan device; two tracked eye views, runtime FOVs, headset presentation and a right-eye desktop mirror. Enable OpenXR on the Device settings page and restart with the headset connected. Raster rendering only; desktop temporal-provider settings are retained.
  1. Per-eye temporal state: TAA history, motion vectors and jitter; per-eye or disabled upscaler and frame-generation contexts where the SDKs don't support VR.
  2. Motion-controller input through OpenXR actions: movement, look, interaction, hotbar, inventory, with bindings for common controllers.
  3. GUI on world-space panels, plus comfort options (snap/smooth turn, vignette, seated/standing).
  4. Full-body pose from head and hand tracking via inverse kinematics on the player model.
  5. Server companion mod that streams head/hand poses so other players see VR players' tracked movement, with fallback to normal animation for players without it.

## Deferred

- **GL-02 / GL-03:** OpenGL compatibility adapters and profiles for other mods, once a target mod is chosen.
- **TEST-01:** Tests.
- **LINUX-01:** Linux release.
