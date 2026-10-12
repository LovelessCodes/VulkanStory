# Example shader pack

`example.tint` is the reference pack for contract 1
([docs/shader-packs.md](../../docs/shader-packs.md)). It replaces the shipped
`chunkopaque` program with a copy of
[shaders/native/chunkopaque.glsl](../../shaders/native/chunkopaque.glsl) that
multiplies lit terrain by a faint warm tint behind the `PACK_TINT` option.

## Try it

1. Copy `example.tint` into `<game>/VulkanStory/shaderpacks/` (the folder that
   contains `shaders-vk.pak`).
2. Start the game with `VULKANSTORY_VK_SHADER_PACK=example.tint` set in the
   environment. An absolute pack directory also works. This is the development
   selection until the Options menu carries the pack list.
3. The log (`Logs/client-main.log`, or the console) reports the pack and its
   program count:

   ```
   [VulkanStory] shaders: pack 'example.tint' built from <path>
   [VulkanStory] shaders: native 50 programs from ...; pack 'example.tint' replaces 1 of 1 declared program(s)
   ```

4. Lit terrain is slightly warmer. `PACK_TINT` sits at its manifest default
   until per-pack options are selectable in game; edit `default` in `pack.json`
   to compare, and the pack rebuilds on the next launch.

## Deliberately break it

Set the option to `"default": "not a bool"` to see the refusal path, or delete a
`}` in the source to see per-slot fallback: the pack stays loaded, the log names
the failing program, and the shipped program draws the world.

## What a pack may do in contract 1

- Replace shipped programs by slot name, one GLSL 450 file per program carrying
  both stages (`OPTIMUM_VERTEX` / `OPTIMUM_FRAGMENT`) as the native corpus does.
- Declare `bool` and `choice` options that become `#define`s in its sources.
- Use everything the native shader contract offers: `shaders/native/include`
  (embedded into the build tree), the shared descriptor sets, reserved varyings
  and the motion output interface.

Not yet: pack-provided targets and declared passes, pack textures, per-pack
option UI, hot reload, and the OptiFine/Iris format. A pack must not ship its
own `include/` directory.
