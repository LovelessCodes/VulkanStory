# Example shader pack

`example.tint` is the reference pack for contract 1
([docs/shader-packs.md](../../docs/shader-packs.md)). It is deliberately obvious:
lit terrain gets a heavy warm duotone, so the pack is plainly visible in any
world view.

- `chunkopaque` is a copy of [shaders/native/chunkopaque.glsl](../../shaders/native/chunkopaque.glsl)
  with the duotone behind the `PACK_TINT` option. Terrain always draws, so this
  is the part that is always visible.
- `colorgrade` is a copy of [shaders/native/colorgrade.glsl](../../shaders/native/colorgrade.glsl)
  with a warm grade behind the `PACK_GRADE` option. The game only draws this
  program when it grades the image itself (sleep, damage), so expect it to be
  invisible during normal play; it shows that a pack can replace post programs
  too.

## Try it

1. Copy `example.tint` into `<game>/VulkanStory/shaderpacks/` (the folder that
   contains `shaders-vk.pak`).
2. Start the game. With exactly one pack installed it activates automatically;
   with several, set `VULKANSTORY_VK_SHADER_PACK=example.tint` to pick one. The
   Options menu carries the pack list once it exists.
3. The log (`Logs/client-main.log`, or the console) reports the pack:

   ```
   [VulkanStory] shaders: pack 'example.tint' built from <path>
   [VulkanStory] shaders: native 52 programs from ...; pack 'example.tint' replaces 2 of 2 declared program(s)
   ```

4. The world is visibly warmer and more saturated, with soft vignetting. Turn an
   option off in `pack.json` (`"default": false`) and relaunch to compare: the
   pack rebuilds at startup. `PACK_GRADE` off leaves only the terrain tint.

## Deliberately break it

Set an option to a bad value (`"default": "not a bool"`) to see the refusal path,
or delete a `}` in one source to see per-slot fallback: the pack stays loaded,
the log names the failing program, and the shipped program draws that slot.

## What a pack may do in contract 1

- Replace shipped programs by slot name, one GLSL 450 file per program carrying
  both stages (`OPTIMUM_VERTEX` / `OPTIMUM_FRAGMENT`) as the native corpus does.
- Declare `bool` and `choice` options that become `#define`s in its sources.
- Use everything the native shader contract offers: `shaders/native/include`
  (embedded into the build tree), the shared descriptor sets, reserved varyings
  and the motion output interface.

Not yet: pack-provided targets and declared passes, pack textures, per-pack
option UI, hot reload, and the OptiFine/Iris format. A pack must not ship its own
`include/` directory.
