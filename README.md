# Digimon BIN Importer

A Unity Editor tool that reads character data directly from the **Digimon World 1** PlayStation 1 disc image (`SLUS_010.10.BIN`, USA region) and produces fully usable Unity assets — textures, meshes, materials, skinned mesh prefabs, and animation clips — without any intermediate conversion step.

> Reference research: [marceloadsj/digimon_world_1_character_viewer](https://github.com/marceloadsj/digimon_world_1_character_viewer)

---

## What It Does

For every Digimon selected, the importer:

1. **Strips the raw PS1 disc sectors** — removes the 24-byte header and 280-byte footer from each 2352-byte sector, producing a clean 2048-byte-per-sector buffer that mirrors PS1 addressable memory.
2. **Decodes TIM textures** — reads palette-indexed (4 bpp / 8 bpp) and direct-colour (16 bpp) TIM images from VRAM layout, supporting multi-CLUT (multiple palette rows) per texture.
3. **Parses TMD model data** — reads vertices, normals, and face primitives (textured, flat-coloured, Gouraud-shaded) for every object in the model.
4. **Reads the skeleton** — reconstructs the bone hierarchy from the node table (`objectIndex` → parent `nodeIndex`), including the special baby/small Digimon node table.
5. **Decodes animation data (MMD format)** — parses every animation sequence, including:
   - Per-bone keyframes (rotation, translation, optional scale) stored as BAM angles and fixed-point positions.
   - Loop start/end markers with repetition counts.
   - Texture swap sequences (UV animation / skin changes).
   - Sound cue sequences (VAB bank + sound ID at a given timecode).
6. **Builds Unity assets** and writes them to the configured output directory:
   - `Texture2D` (PNG) with full CLUT support.
   - `Mesh` per TMD object, merged into a single skinned mesh.
   - `Material` using the decoded texture.
   - `SkinnedMeshRenderer` prefab with the bind-pose skeleton wired up.
   - `AnimationClip` for each named animation (idle, walk, run, attack 0–14, etc.).
   - `AnimatorController` with states mapped to the standard animation names.
7. **Wires sound clips** — if a VBALL bank index is provided, `AudioSource` components on the prefab are populated with the correct extracted WAV files (`VBALL_bank{N}_snd{id}.wav`).

---

## Requirements

| Requirement | Details |
|---|---|
| Unity version | Any version with `UnityEditor` (tested on Unity 2021+) |
| Disc image | `SLUS_010.10.BIN` — USA release of Digimon World 1 |
| Platform | Editor only (not included in builds) |
| Optional | Pre-extracted VBALL `.wav` files for sound wiring |

---

## Installation

1. Copy `DigimonBinImporterWindow.cs` into any **Editor** folder inside your Unity project (e.g. `Assets/Editor/DigimonTools/`).
2. Unity will compile it automatically.
3. Open the tool via the menu bar: **Tools → Digimon BIN Importer**.

---

## Usage

### Basic Import

1. Open **Tools → Digimon BIN Importer** from the Unity menu bar.
2. In the **BIN File** field, paste the full path to `SLUS_010.10.BIN`, or click **…** to browse.
3. Set **Output Dir** to where assets should be saved (default: `Assets/DigimonBin`).
4. Pick a Digimon from the **Select Digimon** dropdown (over 160 characters available).
5. Click **Import Selected Digimon**.

Generated assets appear under the output directory, organised by Digimon name.

### Sound Wiring (optional)

If you have already extracted the VBALL sound banks from the disc as WAV files:

1. Point **Sounds Dir** at the folder containing the WAV files.  
   Files must follow the naming convention:  
   `VBALL_bank{NN}_snd{SSS}.wav` (e.g. `VBALL_bank03_snd002.wav`).
2. Set **VBALL Bank Index** to the bank number that belongs to the selected Digimon (e.g. `3` for Agumon). A live validation message shows whether the expected sample was found.
3. Import as normal — the resulting prefab will have `AudioSource` components pre-assigned.

Set **VBALL Bank Index** to `-1` to skip sound wiring entirely.

---

## Animation Names

The importer maps the 55 raw animation slots from the PS1 data to human-readable names and Unity Animator states:

| Slot range | Examples |
|---|---|
| General behaviour | `idle`, `walking`, `running`, `sleeping`, `eating`, `pooping` |
| Emotional | `happy`, `joyful`, `angry`, `finicky`, `dizzy` |
| Battle | `defending`, `fainting`, `standing up`, `falling` |
| Attacks | `attack 0` … `attack 14` → Animator states `Attack0`…`Attack14` |
| Special | `leaving` (evolution), `staggering` (needs bandage) |

---

## Output Assets

After a successful import the following assets are created under `<Output Dir>/<DigimonName>/`:

```
<DigimonName>/
├── <DigimonName>_texture.png
├── <DigimonName>_mesh.asset
├── <DigimonName>_material.mat
├── <DigimonName>.prefab              ← SkinnedMeshRenderer + skeleton
├── <DigimonName>_animator.controller
└── Animations/
    ├── idle.anim
    ├── walk.anim
    ├── run.anim
    └── ...
```

---

## Technical Notes

- **Coordinate system** — PS1 fixed-point positions are scaled by `1/256`. Rotations use BAM units (`1 BAM = 180°/2048`). The animation tick rate is **20 fps** (matching the game's `libetc_vsync(3)` cadence).
- **CLUT handling** — textures with multiple palette rows are supported; the importer picks the appropriate CLUT row per face using the `cba` (CLUT Base Address) field from each TMD primitive.
- **Null entries** — a small number of MMD slots are empty (`null`); they are hidden from the dropdown automatically.
- **Caching** — the stripped binary buffer is cached in memory after the first load, so switching between Digimons does not re-read and re-strip the entire disc image.
- **Region** — only the **USA** disc (`SLUS_010.10`) is supported. All hard-coded offsets target that specific release.
