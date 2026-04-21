// DigimonBinImporterWindow.cs
// Reads directly from the PS1 SLUS_010.10.BIN file.
// Produces: Texture2D, Mesh(es), Material, SkinnedMeshRenderer Prefab, AnimationClips.
//
// Reference: github.com/marceloadsj/digimon_world_1_character_viewer

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace DigimonTools
{
    // =========================================================================
    // Small data containers
    // =========================================================================

    struct NodeEntry
    {
        public sbyte objectIndex; // which TMD object this bone drives
        public sbyte nodeIndex;   // parent bone index (-1 = root)
    }

    class TimData
    {
        public int        bitsPerPixel;
        public Color32[]  pixels;      // decoded with CLUT row 0 (fallback)
        public int        pixelWidth;  // true pixel width after decoding
        public int        pixelHeight;
        public int        vramX;       // TIM position in VRAM (16-bit units)
        public int        vramY;
        // Multi-CLUT support: raw palette indices + all palette rows
        public byte[]     rawIndices;   // one byte per pixel (4bpp/8bpp) or null for 16bpp
        public Color32[][] cluts;       // all CLUT rows; null for 16bpp
        public int        numClutRows;  // cluts.Length or 1
        public int        clutBaseVramY; // VRAM Y where CLUT section starts (for row index calc)
        public int        clutBaseVramX; // VRAM X where CLUT section starts (for column offset calc)
    }

    struct TmdObjectData
    {
        public Vector3[]       vertices;
        public Vector3[]       normals;
        public List<int>       triIndices;
        public List<Vector2>   triUVs;
        public List<Color>     triColors;
        public List<int>       triClutRows;  // per-vertex absolute CLUT VRAM Y (from cba)
        public List<int>       triClutXVrams; // per-vertex absolute CLUT VRAM X (from cba)
        public int             texturePage; // -1 if untextured
        public int             cba;         // CLUT info (last seen)
        public int             tsb;         // texture page info
    }

    struct PostureData
    {
        public short rotX, rotY, rotZ;
        public short posX, posY, posZ;
        public short scaleX, scaleY, scaleZ;
        public bool  hasScale;
    }

    struct AnimKeyframe
    {
        public int  nodeIndex;
        public bool hasScaleX, hasScaleY, hasScaleZ;
        public bool hasRotX,   hasRotY,   hasRotZ;
        public bool hasPosX,   hasPosY,   hasPosZ;
        public int  duration;            // frames at 20 fps (game tick rate)
        public short scaleX, scaleY, scaleZ;
        public short rotX,   rotY,   rotZ;
        public short posX,   posY,   posZ;
    }

    enum SeqOpcode { Axis = 0, LoopStart = 1, LoopEnd = 2, Texture = 3, Sound = 4 }

    class AxisSeq
    {
        public int            sequenceIndex;
        public List<AnimKeyframe> keyframes = new List<AnimKeyframe>();
    }
    class LoopStartSeq { public int repetitions; }
    class LoopEndSeq   { public int sequenceIndex; public int startSequenceIndex; }
    class TextureSeq   { public int sequenceIndex; public int srcX, srcY, destX, destY, w, h; }
    // PS1 opcode 0x4000: play a sound from a VAB bank at a specific timecode.
    // soundId  = index within the VAB bank
    // vabId    = which VAB (voice allocation bank) — identifies the sound bank
    class SoundSeq     { public int sequenceIndex; public byte soundId; public byte vabId; }

    class AnimData
    {
        public int              numberOfSequences;
        public bool             hasScale;
        public PostureData[]    pose;           // rest pose for nodes 1..N-1
        public List<object>     sequences = new List<object>();
    }

    // =========================================================================
    // Main Window
    // =========================================================================

    public class DigimonBinImporterWindow : EditorWindow
    {
        // ── PSX sector constants ─────────────────────────────────────────────
        const int  PSX_SECTOR_SIZE   = 2352;
        const int  PSX_HEADER        = 24;
        const int  PSX_FOOTER        = 280;
        const int  PSX_CLEAN         = 2048;   // PSX_SECTOR_SIZE - PSX_HEADER - PSX_FOOTER

        // ── Offsets inside the cleaned buffer (USA SLUS_010.10) ──────────────
        const long ALLTIM_PTR        = 0x001db000L;
        const int  TIM_BYTE_LEN      = 0x4800;
        const long NODES_PTR         = 0x12245170L;
        const int  NODES_BYTE_LEN    = 0x0cef;
        const long BABY_NODES_PTR    = 0x1225cb2cL;
        const long SKEL_PTR_TBL      = 0x12245e60L;
        const long DIGIMONS_PTR      = 0x12255eb4L;
        const int  DIGI_STRUCT_SIZE  = 52;  // 20 name + 4 nNodes + 2 radius + 2 height + 1 type + 1 level + 22 skip

        // ── Animation math ───────────────────────────────────────────────────
        const float BAM_TO_DEG       = 180f / 2048f;
        const float PS1_POS_SCALE    = 1f / 256f;    // tweak if characters look too big/small
        const float PS1_SCALE_FACTOR = 1f / 4096f;
        const float FRAME_DT         = 1f / 20f;  // DW1 runs at 20fps (libetc_vsync(3), CURRENT_FRAME%1200==1 in-game hour)

        // ── Animation names (from reference: marceloadsj/digimon_world_1_character_viewer) ──
        static readonly string[] ANIM_NAMES =
        {
            "idle", "tired", "walking", "stumbling", "running",
            "happy", "throw", "finicky", "eating", "sleeping",
            "pooping", "joyful", "angry", "no", "tired",
            "hungry", "toilet", "dizzy", "tired", "contemplating",
            "pointing", "standing up", "yes", "staggering",
            "looking up", "looking down", "falling", "pushing",
            "coming down", "checking", "relaxing", "leaving", "preparing",
            "idle", "tired", "walking", "running",
            "defending", "petting", "waving", "falling back", "falling forward",
            "joyful", "fainting", "standing up",
            "attack 0", "attack 1", "attack 2", "attack 3", "attack 4",
            "attack 5", "attack 6", "attack 7", "attack 8", "attack 9",
            "attack 10", "attack 11", "attack 12", "attack 13", "attack 14",
        };

        // Maps ANIM_NAMES labels (from reference) → AnimatorController state names.
        // Attack states use generic names Attack0..Attack14 so any Digimon's
        // actual attack count is handled dynamically in BuildAnimatorController.
        static readonly Dictionary<string, string> s_AnimNameToState =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "idle",         "Idle" },
            { "walking",      "Walk" },
            { "running",      "Run" },
            { "stumbling",    "Stumbling" },
            { "dizzy",        "Dizzy" },
            { "hungry",       "Hungry" },
            { "toilet",       "Toilet" },
            { "eating",       "Eat" },
            { "sleeping",     "Sleep" },
            { "tired",        "Tired" },
            { "fainting",     "Faint" },
            { "pooping",      "Poop" },
            { "joyful",       "Joyful" },
            { "happy",        "Joyful" },
            { "finicky",      "Finicky" },
            { "no",           "Finicky" },
            { "angry",        "Angry" },
            { "waving",       "Win" },
            { "throw",        "Attack0" },
            { "defending",    "Hurt" },
            { "falling",      "Faint" },
            { "leaving",      "Evolve" },
            { "staggering",   "NeedBandageIdle" },
            { "attack 0",     "Attack0" },
            { "attack 1",     "Attack1" },
            { "attack 2",     "Attack2" },
            { "attack 3",     "Attack3" },
            { "attack 4",     "Attack4" },
            { "attack 5",     "Attack5" },
            { "attack 6",     "Attack6" },
            { "attack 7",     "Attack7" },
            { "attack 8",     "Attack8" },
            { "attack 9",     "Attack9" },
            { "attack 10",    "Attack10" },
            { "attack 11",    "Attack11" },
            { "attack 12",    "Attack12" },
            { "attack 13",    "Attack13" },
            { "attack 14",    "Attack14" },
        };

        // ── VRAM ─────────────────────────────────────────────────────────────
        const int  VRAM_W            = 1024;
        const int  VRAM_H            = 512;

        // ── MMD Pointer Table (USA SLUS_010.10) ──────────────────────────────
        static readonly (string name, long pointer, int byteLen)[] MMD_POINTERS =
        {
            // MMD0
            ("Main Character",  0x0056a800L, 0x0000decb),
            ("Botamon",         0x00565000L, 0x0000564b),
            ("Koromon",         0x00605000L, 0x00005977),
            ("Agumon",          0x00505800L, 0x0001194b),
            ("Betamon",         0x0053f800L, 0x0001019b),
            ("Greymon",         0x005cc000L, 0x00016cdf),
            ("Devimon",         0x00578800L, 0x0001597f),
            ("Airdramon",       0x00517800L, 0x000116ef),
            ("Tyrannomon",      0x006b6800L, 0x00015d53),
            ("Meramon",         0x0061f800L, 0x0001570f),
            ("Seadramon",       0x0068e000L, 0x0000f44f),
            ("Numemon",         0x0066e800L, 0x00014bbf),
            ("MetalGreymon",    0x00642000L, 0x00015edf),
            ("Mamemon",         0x0060b000L, 0x00014267),
            ("Monzaemon",       0x00635000L, 0x0000cbf7),
            ("Punimon",         0x00688800L, 0x000055ab),
            ("Tsunomon",        0x006b2800L, 0x00003a87),
            ("Gabumon",         0x005a2800L, 0x00011b07),
            ("Elecmon",         0x0058e800L, 0x00013be3),
            ("Kabuterimon",     0x005ed000L, 0x0001781b),
            ("Angemon",         0x00529000L, 0x000164bf),
            ("Birdramon",       0x00550000L, 0x00014aa3),
            ("Garurumon",       0x005b4800L, 0x00017643),
            ("Frigimon",        0x006e9800L, 0x0000f41f),
            ("Whamon",          0x005e3000L, 0x00009b23),
            ("Vegiemon",        0x006da000L, 0x0000f25b),
            ("SkullGreymon",    0x0069d800L, 0x00014d87),
            ("MetalMamemon",    0x00658000L, 0x0001661b),
            ("Vademon",         0x006cc800L, 0x0000d41f),
            ("Poyomon",         0x00683800L, 0x00004f07),
            // MMD1
            ("Tokomon",         0x00917800L, 0x0000858f),
            ("Patamon",         0x00885800L, 0x00014947),
            ("Kunemon",         0x007cf000L, 0x000130ef),
            ("Unimon",          0x00920000L, 0x0001626b),
            ("Ogremon",         0x00861800L, 0x0001276b),
            ("Shellmon",        0x008e8000L, 0x0000fdc7),
            ("Centarumon",      0x00724800L, 0x00017797),
            ("Bakemon",         0x0070f800L, 0x00014a83),
            ("Drimogemon",      0x00768800L, 0x000159bb),
            ("Sukamon",         0x008d6800L, 0x000111bf),
            ("Andromon",        0x006f9800L, 0x00015f5f),
            ("Giromon",         0x00796800L, 0x0000c42b),
            ("Etemon",          0x0077e800L, 0x00017e13),
            ("Yuramon",         0x00936800L, 0x00005e27),
            ("Tanemon",         0x0090d000L, 0x0000a59b),
            ("Biyomon",         0x008c3000L, 0x000137df),
            ("Palmon",          0x00874000L, 0x000112d3),
            ("Monochromon",     0x00837800L, 0x000177db),
            ("Leomon",          0x007fa800L, 0x00014f93),
            ("Coelamon",        0x008f8000L, 0x00014c8f),
            ("Kokatorimon",     0x0073c000L, 0x000174ab),
            ("Kuwagamon",       0x007e2800L, 0x00017857),
            ("Mojyamon",        0x00822000L, 0x000156a7),
            ("Nanimon",         0x0084f000L, 0x0001263f),
            ("Megadramon",      0x0080f800L, 0x000126ab),
            ("Piximon",         0x008b0000L, 0x00012c8f),
            ("Digitamamon",     0x00753800L, 0x00014e93),
            ("Penguinmon",      0x0089a800L, 0x000150e7),
            ("Ninjamon",        0x007bb000L, 0x00013da3),
            ("Phoenixmon",      0x007a3000L, 0x00017cd7),
            // MMD2
            ("H-Kabuterimon",   0x009a2800L, 0x000176d7),
            ("MegaSeadramon",   0x009d1000L, 0x0001008b),
            (null,              0L, 0),
            ("Panjyamon",       0x00a10000L, 0x00013eb3),
            ("Gigadramon",      0x00977800L, 0x0001242b),
            ("MetalEtemon",     0x009eb800L, 0x0001795b),
            ("Myotismon",       0x00a7c800L, 0x000100eb),
            ("Yanmamon",        0x00aa4800L, 0x0001030f),
            ("Gotsumon",        0x00998000L, 0x0000a00f),
            ("Flarerizamon",    0x00953000L, 0x0000bb0f),
            ("WaruMonzaemon",   0x00a9a800L, 0x00009fbf),
            ("SnowAgumon",      0x00ab5000L, 0x0000a8a7),
            ("Hyogamon",        0x009ba000L, 0x0000b8f7),
            ("PlatinumSukamon", 0x00a24000L, 0x0000a54f),
            ("Dokunemon",       0x00948000L, 0x0000ad6f),
            ("ShimaUnimon",     0x00a51000L, 0x0000cebb),
            ("Tankmon",         0x00a5e000L, 0x00009c93),
            ("RedVegiemon",     0x00a47000L, 0x00009b47),
            ("J-Mojyamon",      0x009c6000L, 0x0000ad1b),
            ("NiseDrimogemon",  0x00a03800L, 0x0000c307),
            ("Goburimon",       0x0098a000L, 0x0000d943),
            ("MudFrigimon",     0x00a73000L, 0x000092a7),
            ("Psychemon",       0x00a3c800L, 0x0000a4f3),
            ("ModokiBetamon",   0x009e1800L, 0x00009f17),
            ("ToyAgumon",       0x00a68000L, 0x0000af8f),
            ("Piddomon",        0x00a2e800L, 0x0000dc6b),
            ("Aruraumon",       0x0093d000L, 0x0000aea7),
            ("Geremon",         0x0096a800L, 0x0000cac7),
            ("Vermilimon",      0x00a8d000L, 0x0000d57f),
            ("Fugamon",         0x0095f000L, 0x0000b137),
            // MMD3
            ("Tekkamon",        0x00bda800L, 0x0000557f),
            ("MoriShellmon",    0x00b60000L, 0x00009c8b),
            ("Guardromon",      0x00af5800L, 0x0000d9f7),
            ("Muchomon",        0x00b55000L, 0x0000aecf),
            ("Icemon",          0x00b36000L, 0x0000983b),
            ("Akatorimon",      0x00ac0800L, 0x0000d41f),
            ("Tsukaimon",       0x00bed000L, 0x0000c2a7),
            ("Sharmamon",       0x00b92000L, 0x0000b203),
            ("ClearAgumon",     0x00adf000L, 0x0000af8f),
            ("Weedmon",         0x00c03000L, 0x00009b47),
            ("IceDevimon",      0x00b2a000L, 0x0000bedf),
            ("Darkrizamon",     0x00aea000L, 0x0000b373),
            ("SandYanmamon",    0x00b9d800L, 0x0000f00f),
            ("SnowGoburimon",   0x00bad000L, 0x0000bccb),
            ("BlueMeramon",     0x00ad3800L, 0x0000b15b),
            ("Gururumon",       0x00b1c000L, 0x0000dd8f),
            ("Saberdramon",     0x00b87000L, 0x0000ae7f),
            ("Soulmon",         0x00bb9000L, 0x0000c0bb),
            ("Rockmon",         0x00b11000L, 0x0000adb7),
            ("Otamamon",        0x00b7d800L, 0x000093df),
            ("Gekomon",         0x00b03800L, 0x0000d09f),
            ("Tentomon",        0x00bcc800L, 0x0000dd93),
            ("WaruSeadramon",   0x00bf9800L, 0x00009753),
            ("Meteormon",       0x00b40000L, 0x0000c1a3),
            (null,              0L, 0),
            ("Machinedramon",   0x00b6a000L, 0x000132fb),
            ("Analogman",       0x00ace000L, 0x0000564f),
            ("Jijimon",         0x00b4c800L, 0x00008197),
            ("MarketManager",   0x00bc5800L, 0x00006c73),
            ("ShogunGekomon",   0x00be0000L, 0x0000cf83),
            // MMD4
            ("KingOfSukamon",   0x00d1e000L, 0x0000747f),
            ("Cherrymon",       0x00d14800L, 0x0000755b),
            ("Hagurumon",       0x00d10800L, 0x00003fa3),
            ("Tinmon",          0x00c14800L, 0x00004507),
            ("MasterTyrannomon",0x00d25800L, 0x000063c7),
            ("Goburimon_2",     0x00c73800L, 0x00007c53),
            ("Brachiomon",      0x00c0d800L, 0x00006d87),
            ("DemiMeramon",     0x00d1c000L, 0x00001fd3),
            ("Betamon_2",       0x00c2d800L, 0x0000b6cf),
            ("Greymon_2",       0x00c7b800L, 0x0000caeb),
            ("Devimon_2",       0x00c45800L, 0x0000d647),
            ("Airdramon_2",     0x00c19000L, 0x0000a25b),
            ("Tyrannomon_2",    0x00cf6800L, 0x0000d747),
            ("Meramon_2",       0x00ca9800L, 0x0000c54b),
            ("Seadramon_2",     0x00ce4800L, 0x000055c3),
            ("Numemon_2",       0x00cd6800L, 0x0000dc37),
            ("MetalGreymon_2",  0x00cba800L, 0x0000e69f),
            ("Mamemon_2",       0x00c9d800L, 0x0000bb37),
            ("Monzaemon_2",     0x00cb6000L, 0x0000440f),
            ("Gabumon_2",       0x00c5b800L, 0x0000a517),
            ("Elecmon_2",       0x00c53000L, 0x00008327),
            ("Kabuterimon_2",   0x00c8d000L, 0x000100df),
            ("Angemon_2",       0x00c23800L, 0x00009a33),
            ("Birdramon_2",     0x00c39000L, 0x0000c50b),
            ("Garurumon_2",     0x00c66000L, 0x0000d7df),
            ("Frigimon_2",      0x00d0a000L, 0x000061cf),
            ("Whamon_2",        0x00c88800L, 0x000041e3),
            ("Vegiemon_2",      0x00d04000L, 0x00005e97),
            ("SkullGreymon_2",  0x00cea000L, 0x0000c63f),
            ("MetalMamemon_2",  0x00cc9000L, 0x0000d3cb),
            // MMD5
            ("Vademon_2",       0x00e47000L, 0x0000552b),
            ("Patamon_2",       0x00dfa000L, 0x0000d313),
            ("Kunemon_2",       0x00d9c000L, 0x0000d9bb),
            ("Unimon_2",        0x00e37000L, 0x00006a8b),
            ("Ogremon_2",       0x00ddf800L, 0x0000e753),
            ("Shellmon_2",      0x00e27000L, 0x00007567),
            ("Centarumon_2",    0x00d49000L, 0x0000658b),
            ("Bakemon_2",       0x00d42800L, 0x0000664b),
            ("Drimogemon_2",    0x00d6a000L, 0x0000f793),
            ("Sukamon_2",       0x00e20000L, 0x00006d6b),
            ("Andromon_2",      0x00d3a800L, 0x00007c13),
            ("Giromon_2",       0x00d88800L, 0x00007723),
            ("Etemon_2",        0x00d79800L, 0x0000ecef),
            ("Biyomon_2",       0x00e19800L, 0x000060d3),
            ("Palmon_2",        0x00dee000L, 0x0000bc2f),
            ("Monochromon_2",   0x00dc9800L, 0x00007f53),
            ("Leomon_2",        0x00db3800L, 0x0000a163),
            ("Coelamon_2",      0x00e2e800L, 0x000080e3),
            ("Kokatorimon_2",   0x00d4f800L, 0x0000e927),
            ("Kuwagamon_2",     0x00daa000L, 0x000090ef),
            ("Mojyamon_2",      0x00dd1800L, 0x00007f3b),
            ("Nanimon_2",       0x00dd9800L, 0x00005f57),
            ("Megadramon_2",    0x00dbe000L, 0x0000b17f),
            ("Piximon_2",       0x00e0f000L, 0x0000a747),
            ("Digitamamon_2",   0x00d5e800L, 0x0000b0cb),
            ("Ninjamon_2",      0x00d90000L, 0x0000bf73),
            ("Penguinmon_2",    0x00e07800L, 0x000075d3),
            ("Myotismon_2",     0x00e3e000L, 0x0000894b),
            ("Greymon_3",       0x00d2c800L, 0x000060a3),
            ("MetalGreymon_3",  0x00d33000L, 0x00007097),
        };

        // ── GUI state ─────────────────────────────────────────────────────────
        string  _binPath        = "";
        int     _selectedIdx    = 3;   // default: Agumon
        string  _outputDir      = "Assets/DigimonBin";
        string  _soundsDir      = "Assets/DigimonBin/Sounds"; // where extracted WAVs live
        int     _vballBankIndex = -1;  // VBALL bank that belongs to this Digimon (-1 = unknown)
        Vector2 _scroll;
        string  _status         = "Select SLUS_010.10.BIN and click Import.";

        // Cached stripped buffer (avoids re-stripping on every import)
        static string _cachedPath;
        static byte[] _clean;

        // ── Menu item ─────────────────────────────────────────────────────────
        [MenuItem("Tools/Digimon BIN Importer")]
        static void Open() => GetWindow<DigimonBinImporterWindow>("Digimon BIN Importer");

        // =========================================================================
        // GUI
        // =========================================================================
        void OnGUI()
        {
            GUILayout.Label("Digimon World 1 — PS1 BIN Importer", EditorStyles.boldLabel);
            GUILayout.Label("USA (SLUS_010.10)", EditorStyles.miniLabel);
            EditorGUILayout.Space(6);

            // BIN path
            EditorGUILayout.BeginHorizontal();
            _binPath = EditorGUILayout.TextField("BIN File", _binPath);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string p = EditorUtility.OpenFilePanel("Select SLUS_010.10.BIN", "", "BIN,bin");
                if (!string.IsNullOrEmpty(p)) _binPath = p;
            }
            EditorGUILayout.EndHorizontal();

            _outputDir = EditorGUILayout.TextField("Output Dir", _outputDir);
            EditorGUILayout.Space(4);

            // ── Sound wiring ──────────────────────────────────────────────────
            GUILayout.Label("Sound Wiring", EditorStyles.boldLabel);
            _soundsDir      = EditorGUILayout.TextField("Sounds Dir", _soundsDir);
            _vballBankIndex = EditorGUILayout.IntField(
                new GUIContent("VBALL Bank Index",
                    "Bank index in the extracted VBALL sounds (e.g. 3 for Agumon).\n" +
                    "Files must be named VBALL_bank{N:D2}_snd{soundId:D3}.wav.\n" +
                    "Set to -1 to skip automatic sound assignment."),
                _vballBankIndex);
            if (_vballBankIndex >= 0)
            {
                string samplePath = $"{_soundsDir.TrimEnd('/')}/VBALL_bank{_vballBankIndex:D2}_snd002.wav";
                bool   found      = AssetDatabase.LoadAssetAtPath<AudioClip>(samplePath) != null;
                EditorGUILayout.HelpBox(
                    found ? $"✓  Found snd002 in bank {_vballBankIndex}."
                          : $"✗  snd002 not found: {samplePath}",
                    found ? MessageType.Info : MessageType.Warning);
            }
            EditorGUILayout.Space(4);

            // Digimon picker (skip null entries)
            GUILayout.Label("Digimon", EditorStyles.boldLabel);
            var displayNames = MMD_POINTERS
                .Select((e, i) => e.name != null ? $"[{i:D3}] {e.name}" : null)
                .Where(s => s != null)
                .ToArray();
            // map display names back to real indices
            int[] realIndices = MMD_POINTERS
                .Select((e, i) => (e, i))
                .Where(t => t.e.name != null)
                .Select(t => t.i)
                .ToArray();

            int displayIdx = Array.IndexOf(realIndices, _selectedIdx);
            if (displayIdx < 0) displayIdx = 0;
            int newDisplayIdx = EditorGUILayout.Popup("Select Digimon", displayIdx, displayNames);
            if (newDisplayIdx != displayIdx) _selectedIdx = realIndices[newDisplayIdx];

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_binPath)))
            {
                if (GUILayout.Button("Import Selected Digimon", GUILayout.Height(32)))
                    DoImport(_selectedIdx);
            }

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(200));
            EditorGUILayout.HelpBox(_status, MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        // =========================================================================
        // Top-level import
        // =========================================================================
        void DoImport(int digimonIndex)
        {
            try
            {
                var entry = MMD_POINTERS[digimonIndex];
                if (entry.name == null) { _status = "Selected entry is null."; return; }
                _status = $"Importing {entry.name} (index {digimonIndex})…\n";

                // 1. Get cleaned buffer
                byte[] buf = GetCleanBuffer(_binPath);
                _status += $"Stripped binary: {buf.Length / (1024 * 1024)} MB\n";

                // 2. Read DigimonStruct (numberOfNodes, radius)
                int numberOfNodes = ReadNumberOfNodes(buf, digimonIndex);
                ushort digiRadius = ReadDigimonRadius(buf, digimonIndex);
                _status += $"numberOfNodes: {numberOfNodes}  radius: {digiRadius} PS1u" +
                           $" (walkDist≈{digiRadius * 2.5f / 256f:F2}m  runDist≈{digiRadius * 3.5f / 256f:F2}m)\n";

                // 3. Read node entries (objectIndex + nodeIndex = parent)
                NodeEntry[] nodes = ReadNodeEntries(buf, digimonIndex, numberOfNodes);

                // 4. Read TIM texture
                TimData tim = ParseTIM(buf, ALLTIM_PTR + (long)digimonIndex * TIM_BYTE_LEN);
                _status += $"TIM: {tim.pixelWidth}x{tim.pixelHeight} ({tim.bitsPerPixel}bpp) vramXY=({tim.vramX},{tim.vramY})\n";

                // 5. Read MMD
                byte[] mmd = ReadSlice(buf, entry.pointer, entry.byteLen + 1);

                long mmdOff = 0;
                uint tmdPointer = RL32(mmd, mmdOff);             mmdOff += 4;
                uint animOffsetsPtr = RL32(mmd, mmdOff);         mmdOff = 0;

                // Dump first 16 bytes as hex for diagnosis
                string hexDump = string.Join(" ", mmd.Take(16).Select(b => b.ToString("X2")));
                _status += $"MMD: size={mmd.Length} tmdPtr=0x{tmdPointer:X8} animPtr=0x{animOffsetsPtr:X8}\nMMD[0..15]: {hexDump}\n";

                // Validate tmdPointer is within the MMD slice
                if (tmdPointer >= mmd.Length)
                {
                    _status += $"NOTE: tmdPointer out of bounds. Trying offset 8 (right after header).\n";
                    tmdPointer = 8;
                }

                // 6. Parse TMD — pass TIM so UV page offsets can be computed correctly
                var tmdObjects = ParseTMD(mmd, (long)tmdPointer, tim);
                _status += $"TMD: {tmdObjects.Count} objects\n";

                // 7. Parse animations
                var anims = ParseAnimations(mmd, (long)animOffsetsPtr, numberOfNodes);
                _status += $"Animations: {anims.Count}\n";

                // 8. Build Unity assets
                string safeName = string.Concat(entry.name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                string dir = _outputDir.TrimEnd('/') + "/" + safeName;
                // Build the absolute filesystem path the same way CreateAssets does for file writes
                string absDirPath = dir.Replace("Assets/", Application.dataPath + "/");
                Directory.CreateDirectory(absDirPath);
                AssetDatabase.Refresh();

                CreateAssets(buf, mmd, digimonIndex, safeName, dir, tim, tmdObjects, nodes, numberOfNodes, anims);
                _status += "Done! Check the " + dir + " folder.\n";
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                _status += "\nERROR: " + ex.Message + "\n" + ex.StackTrace;
                Debug.LogException(ex);
            }
        }

        // =========================================================================
        // Sector stripping
        // =========================================================================
        byte[] GetCleanBuffer(string path)
        {
            if (_cachedPath == path && _clean != null) return _clean;
            _status += "Stripping sectors (one-time)…\n";
            _cachedPath = path;
            _clean = StripSectors(path);
            return _clean;
        }

        static byte[] StripSectors(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            long sectorCount = raw.Length / PSX_SECTOR_SIZE;
            byte[] clean = new byte[sectorCount * PSX_CLEAN];
            for (long s = 0; s < sectorCount; s++)
            {
                long src = s * PSX_SECTOR_SIZE + PSX_HEADER;
                long dst = s * PSX_CLEAN;
                Array.Copy(raw, src, clean, dst, PSX_CLEAN);
            }
            return clean;
        }

        static byte[] ReadSlice(byte[] buf, long offset, int length)
        {
            length = (int)Math.Min(length, buf.Length - offset);
            byte[] slice = new byte[length];
            Array.Copy(buf, offset, slice, 0, length);
            return slice;
        }

        // =========================================================================
        // DigimonStruct — get numberOfNodes
        // =========================================================================
        static int ReadNumberOfNodes(byte[] buf, int index)
        {
            long o = DIGIMONS_PTR + (long)index * DIGI_STRUCT_SIZE;
            if (o + 24 >= buf.Length) return 8; // fallback
            o += 20; // skip 20 name bytes
            return (int)RL32(buf, o);
        }

        // Returns the raw uint16 radius from the DigimonData struct at DIGI_STRUCT+24.
        // In the original game: walkThreshold = (radius/2)*5, sprintThreshold = (radius/2)*7 (PS1 units).
        // To convert to Unity world units: threshold_unity = threshold_ps1 / 256.
        static ushort ReadDigimonRadius(byte[] buf, int index)
        {
            long o = DIGIMONS_PTR + (long)index * DIGI_STRUCT_SIZE;
            if (o + 26 >= buf.Length) return 0; // fallback
            o += 24; // skip 20 name + 4 nNodes
            return RL16(buf, o);
        }

        // =========================================================================
        // Node entries  (objectIndex + nodeIndex pairs)
        // =========================================================================
        static NodeEntry[] ReadNodeEntries(byte[] buf, int digimonIndex, int numberOfNodes)
        {
            // Get skeleton pointer for this digimon
            long skelPtrOff = SKEL_PTR_TBL + (long)digimonIndex * 4;
            if (skelPtrOff + 4 >= buf.Length) return new NodeEntry[numberOfNodes];

            // The value stored is a PS1 memory address. Convert to a byte offset within
            // the nodes table by subtracting the PS1 base + first-skeleton offset.
            // Reference: startOffset = skeletonPointer - PSX_MEMORY_SPACE_POINTER - FIRST_SKELETON_POINTER
            //   PSX_MEMORY_SPACE_POINTER = 0x80090000, FIRST_SKELETON_POINTER = 0x8c170
            uint rawPtr      = RL32(buf, skelPtrOff);
            uint startOffset = rawPtr - 0x80090000u - 0x0008c170u;

            byte[] nodesSrc;
            long   nodeOff;

            if (startOffset > NODES_BYTE_LEN)
            {
                // baby digimon — separate node table
                nodesSrc = ReadSlice(buf, BABY_NODES_PTR, 8);
                nodeOff  = 0;
            }
            else
            {
                nodesSrc = ReadSlice(buf, NODES_PTR, NODES_BYTE_LEN + 1);
                nodeOff  = startOffset;
            }

            var result = new NodeEntry[numberOfNodes];
            for (int i = 0; i < numberOfNodes && nodeOff + 2 <= nodesSrc.Length; i++, nodeOff += 2)
                result[i] = new NodeEntry { objectIndex = (sbyte)nodesSrc[nodeOff], nodeIndex = (sbyte)nodesSrc[nodeOff + 1] };
            return result;
        }

        // =========================================================================
        // TIM parser
        // =========================================================================
        static TimData ParseTIM(byte[] buf, long offset)
        {
            if (offset + 8 >= buf.Length)
                return new TimData { pixels = new Color32[1], pixelWidth = 1, pixelHeight = 1, bitsPerPixel = 16, numClutRows = 1 };

            long o = offset;
            /* uint id = */ RL32(buf, o); o += 4;
            uint flag = RL32(buf, o);     o += 4;

            int  pixelMode = (int)(flag & 7);
            bool hasCLUT   = (flag & 8) != 0;
            int  bpp       = pixelMode == 0 ? 4 : pixelMode == 1 ? 8 : pixelMode == 2 ? 16 : 24;

            // CLUT
            Color32[][] cluts = null;
            int         clutW = 0;
            long        pixelSectionStart = o;

            int clutBaseVramY = 0;
            int clutBaseVramX = 0;
            if (hasCLUT && o + 12 <= buf.Length)
            {
                uint clutSecLen = RL32(buf, o); o += 4;
                clutBaseVramX = RL16(buf, o); o += 2;   // VRAM X of CLUT section (for column offset calc)
                clutBaseVramY = RL16S(buf, o); o += 2;
                ushort cw = RL16(buf, o);   o += 2;
                ushort ch = RL16(buf, o);   o += 2;

                clutW = cw;
                // Sanity cap: real digimon TIMs have at most a few hundred CLUT rows
                int safeRows = Mathf.Min((int)ch, 512);
                cluts = new Color32[safeRows][];
                for (int row = 0; row < safeRows; row++)
                {
                    cluts[row] = new Color32[cw];
                    for (int c = 0; c < cw && o + 2 <= buf.Length; c++, o += 2)
                        cluts[row][c] = Bgr555ToColor(RL16(buf, o));
                    // If we ran out of data before filling the CLUT, stop early
                    if (o + 2 > buf.Length) { Array.Resize(ref cluts, row + 1); break; }
                }
                pixelSectionStart = offset + 8 + clutSecLen;
            }

            // Pixel section
            o = pixelSectionStart;
            if (o + 12 > buf.Length)
                return new TimData { pixels = new Color32[1], pixelWidth = 1, pixelHeight = 1, bitsPerPixel = bpp, numClutRows = 1 };

            /* secLen */ RL32(buf, o); o += 4;
            ushort vramX = RL16(buf, o); o += 2;
            ushort vramY = RL16(buf, o); o += 2;
            ushort dataW = RL16(buf, o); o += 2;  // in 16-bit VRAM units
            ushort dataH = RL16(buf, o); o += 2;

            int pixW = bpp == 4  ? dataW * 4
                     : bpp == 8  ? dataW * 2
                     : bpp == 16 ? dataW
                                 : dataW; // 24bpp: rare
            int pixH = dataH;

            // Store raw palette indices (for multi-CLUT atlas) and decode row 0 as fallback
            byte[]    rawIdx   = (bpp == 4 || bpp == 8) ? new byte[pixW * pixH] : null;
            Color32[] decoded  = new Color32[pixW * pixH];
            Color32[] clutRow0 = (cluts != null && cluts.Length > 0) ? cluts[0] : null;

            for (int py = 0; py < dataH; py++)
            {
                for (int px = 0; px < dataW && o + 2 <= buf.Length; px++, o += 2)
                {
                    ushort val = RL16(buf, o);
                    if (bpp == 4)
                    {
                        int x0 = px * 4;
                        int[] nibbles = { (val >> 0) & 0xF, (val >> 4) & 0xF, (val >> 8) & 0xF, (val >> 12) & 0xF };
                        for (int n = 0; n < 4; n++)
                        {
                            decoded[py * pixW + x0 + n] = LookupClut(clutRow0, nibbles[n]);
                            rawIdx [py * pixW + x0 + n] = (byte)nibbles[n];
                        }
                    }
                    else if (bpp == 8)
                    {
                        int x0 = px * 2;
                        int[] bytes2 = { val & 0xFF, val >> 8 };
                        for (int n = 0; n < 2; n++)
                        {
                            decoded[py * pixW + x0 + n] = LookupClut(clutRow0, bytes2[n]);
                            rawIdx [py * pixW + x0 + n] = (byte)bytes2[n];
                        }
                    }
                    else
                    {
                        decoded[py * pixW + px] = Bgr555ToColor(val);
                    }
                }
            }

            return new TimData
            {
                bitsPerPixel = bpp,
                pixels       = decoded,
                pixelWidth   = pixW,
                pixelHeight  = pixH,
                vramX        = vramX,
                vramY        = vramY,
                rawIndices    = rawIdx,
                cluts         = cluts,
                numClutRows   = (cluts != null && cluts.Length > 0) ? cluts.Length : 1,
                clutBaseVramY = clutBaseVramY,
                clutBaseVramX = clutBaseVramX,
            };
        }

        static Color32 Bgr555ToColor(ushort v)
        {
            int r = (v & 0x1F) * 255 / 31;
            int g = ((v >>  5) & 0x1F) * 255 / 31;
            int b = ((v >> 10) & 0x1F) * 255 / 31;
            int a_flag = v >> 15;
            byte a = (a_flag == 0 && r == 0 && g == 0 && b == 0) ? (byte)0 : (byte)255;
            return new Color32((byte)r, (byte)g, (byte)b, a);
        }

        // Apply PS1-appropriate texture import settings: Point filter, no mipmaps, Clamp wrap.
        // Must use TextureImporter (not Texture2D.filterMode) so settings survive re-imports.
        static void ApplyPS1TextureSettings(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            bool changed = false;
            if (importer.filterMode    != FilterMode.Point)         { importer.filterMode    = FilterMode.Point;         changed = true; }
            if (importer.mipmapEnabled)                              { importer.mipmapEnabled = false;                    changed = true; }
            if (importer.wrapMode      != TextureWrapMode.Clamp)    { importer.wrapMode      = TextureWrapMode.Clamp;    changed = true; }
            if (changed) importer.SaveAndReimport();
        }

        static Color32 LookupClut(Color32[] clut, int index)
        {
            if (clut == null || index >= clut.Length) return Color.black;
            return clut[index];
        }

        // Paint the interior of a triangle (in UV pixel space) with the given CLUT row index.
        // Also marks each painted texel in the 'assigned' mask for FillClutMapBlocks.
        static void RasterizeTriangleClut(byte[] map, bool[] assigned, int w, int h, Vector2[] uv, byte cr)
        {
            float x0 = uv[0].x, y0 = uv[0].y;
            float x1 = uv[1].x, y1 = uv[1].y;
            float x2 = uv[2].x, y2 = uv[2].y;
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(y0, y1, y2)),     0, h - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt (Mathf.Max(y0, y1, y2)),     0, h - 1);
            for (int y = minY; y <= maxY; y++)
            {
                float fy = y + 0.5f;
                float lx = float.MaxValue, rx = float.MinValue;
                ScanEdgeClut(x0, y0, x1, y1, fy, ref lx, ref rx);
                ScanEdgeClut(x1, y1, x2, y2, fy, ref lx, ref rx);
                ScanEdgeClut(x2, y2, x0, y0, fy, ref lx, ref rx);
                if (lx > rx) continue;
                int sx = Mathf.Clamp(Mathf.FloorToInt(lx), 0, w - 1);
                int ex = Mathf.Clamp(Mathf.CeilToInt (rx), 0, w - 1);
                for (int x = sx; x <= ex; x++)
                {
                    int idx = y * w + x;
                    map[idx]      = cr;
                    assigned[idx] = true;
                }
            }
            // Vertex-point fallback: ensures degenerate / sub-pixel triangles still paint
            // at least their 3 vertex positions (matches DW1ModelConverter robustness).
            int[] vx = { Mathf.RoundToInt(x0), Mathf.RoundToInt(x1), Mathf.RoundToInt(x2) };
            int[] vy = { Mathf.RoundToInt(y0), Mathf.RoundToInt(y1), Mathf.RoundToInt(y2) };
            for (int k = 0; k < 3; k++)
            {
                int px = vx[k], py = vy[k];
                if (px >= 0 && px < w && py >= 0 && py < h)
                {
                    int idx = py * w + px;
                    map[idx]      = cr;
                    assigned[idx] = true;
                }
            }
        }

        // Fill unassigned CLUTMap pixels using 8×8 block voting.
        // Matches DW1ModelConverter CLUTMap::updateBlocks() — if a block has any explicitly
        // assigned pixels, all unassigned pixels in that block inherit the most-common value.
        static void FillClutMapBlocks(byte[] map, bool[] assigned, int w, int h)
        {
            for (int by = 0; by < (h + 7) / 8; by++)
            for (int bx = 0; bx < (w + 7) / 8; bx++)
            {
                var counts = new int[256];
                for (int dy = 0; dy < 8; dy++)
                for (int dx = 0; dx < 8; dx++)
                {
                    int px = bx * 8 + dx, py = by * 8 + dy;
                    if (px >= w || py >= h) continue;
                    int idx = py * w + px;
                    if (assigned[idx]) counts[map[idx]]++;
                }
                byte best = 0; int bestCount = 0;
                for (int i = 0; i < 256; i++)
                    if (counts[i] > bestCount) { best = (byte)i; bestCount = counts[i]; }
                if (bestCount == 0) continue;
                for (int dy = 0; dy < 8; dy++)
                for (int dx = 0; dx < 8; dx++)
                {
                    int px = bx * 8 + dx, py = by * 8 + dy;
                    if (px >= w || py >= h) continue;
                    int idx = py * w + px;
                    if (!assigned[idx]) map[idx] = best;
                }
            }
        }

        static void ScanEdgeClut(float x0, float y0, float x1, float y1, float y, ref float lx, ref float rx)
        {
            if (!((y0 <= y && y < y1) || (y1 <= y && y < y0))) return;
            float t = (y - y0) / (y1 - y0);
            float x = x0 + t * (x1 - x0);
            if (x < lx) lx = x;
            if (x > rx) rx = x;
        }

        // =========================================================================
        // TMD parser
        // =========================================================================
        static List<TmdObjectData> ParseTMD(byte[] buf, long offset, TimData tim = null)
        {
            long o = offset;
            if (o + 12 > buf.Length) return new List<TmdObjectData>();

            uint magic    = RL32(buf, o);
            if (magic != 0x41)
                UnityEngine.Debug.LogWarning($"ParseTMD: unexpected magic 0x{magic:X8} at offset 0x{offset:X} (expected 0x41)");
            o += 4;
            uint flags    = RL32(buf, o);     o += 4;
            uint numObj   = RL32(buf, o);     o += 4;

            // Sanity-check numObj to avoid iterating over corrupt data
            if (numObj == 0 || numObj > 512)
            {
                UnityEngine.Debug.LogWarning($"ParseTMD: numObj={numObj} looks corrupt at offset 0x{offset:X}");
                return new List<TmdObjectData>();
            }

            // nextOffset: if flags bit0=0 offsets are relative to end of header (byte 12)
            long nextOff = (flags & 1) == 0 ? offset + 12 : 0;

            var objects = new List<TmdObjectData>();

            for (uint i = 0; i < numObj && o + 28 <= buf.Length; i++)
            {
                uint vertStart  = RL32(buf, o); o += 4;
                uint vertCount  = RL32(buf, o); o += 4;
                uint normStart  = RL32(buf, o); o += 4;
                uint normCount  = RL32(buf, o); o += 4;
                uint primStart  = RL32(buf, o); o += 4;
                uint primCount  = RL32(buf, o); o += 4;
                /* int scale */    RL32(buf, o); o += 4;

                // Guard against corrupt/garbage header values
                if (vertCount > 65536 || normCount > 65536 || primCount > 65536)
                {
                    UnityEngine.Debug.LogWarning($"ParseTMD obj{i}: corrupt counts vert={vertCount} norm={normCount} prim={primCount}; skipping");
                    break;
                }

                long vBase = nextOff + vertStart;
                long nBase = nextOff + normStart;
                long pBase = nextOff + primStart;

                // Vertices
                var verts = new Vector3[vertCount];
                for (int v = 0; v < (int)vertCount && vBase + 8 <= buf.Length; v++, vBase += 8)
                {
                    verts[v] = new Vector3(
                        RL16S(buf, vBase + 0) * PS1_POS_SCALE,
                       -RL16S(buf, vBase + 2) * PS1_POS_SCALE,  // Y-flip: PS1 Y-down → Unity Y-up
                       -RL16S(buf, vBase + 4) * PS1_POS_SCALE); // Z-flip: matches reference scale.z=-1
                }

                // Normals
                var norms = new Vector3[normCount];
                for (int n = 0; n < (int)normCount && nBase + 8 <= buf.Length; n++, nBase += 8)
                {
                    norms[n] = new Vector3(
                         RL16S(buf, nBase + 0) / 4096f,
                        -RL16S(buf, nBase + 2) / 4096f,  // Y-flip
                        -RL16S(buf, nBase + 4) / 4096f).normalized;  // Z-flip
                }

                var obj = new TmdObjectData
                {
                    vertices     = verts,
                    normals      = norms,
                    triIndices   = new List<int>(),
                    triUVs       = new List<Vector2>(),
                    triColors    = new List<Color>(),
                    triClutRows  = new List<int>(),
                    triClutXVrams= new List<int>(),
                    texturePage  = -1,
                };

                // Primitives
                for (uint p = 0; p < primCount && pBase + 4 <= buf.Length; p++)
                {
                    byte olen = buf[pBase + 0];
                    byte ilen = buf[pBase + 1];
                    byte pflag= buf[pBase + 2];
                    byte mode = buf[pBase + 3];
                    pBase += 4;

                    long packetStart = pBase;
                    int  packetBytes = ilen * 4;

                    ParsePrimitive(buf, packetStart, packetBytes, mode, pflag, verts, norms, obj, tim);

                    pBase += packetBytes;
                }

                objects.Add(obj);
            }

            return objects;
        }

        static void ParsePrimitive(byte[] buf, long p, int packetBytes, byte mode, byte flag,
                                   Vector3[] verts, Vector3[] norms, TmdObjectData obj,
                                   TimData tim = null)
        {
            if (p + packetBytes > buf.Length) return;

            // Decode mode byte
            int codeRaw  = (mode & 0b11100000) >> 5;
            // code: 0 or 1 = polygon, 2 = line, 3 = sprite
            if (codeRaw == 2 || codeRaw == 3) return; // skip lines and sprites

            bool isTextured  = (mode & 0b00000100) != 0;
            bool isQuad      = (mode & 0b00001000) != 0;
            bool isGourad    = (mode & 0b00010000) != 0;
            bool noLight     = (flag & 0b00000001) != 0;   // flag bit0=1 → no light calculation
            bool isGradient  = (flag & 0b00000100) != 0;   // flag bit2=1 → gradient (multi-color)

            // We'll flatten quads into two triangles
            if (isTextured)
                ParseTexturedPrim(buf, p, isQuad, isGourad, noLight, verts, obj, tim);
            else
                ParseUntexturedPrim(buf, p, isQuad, isGourad, noLight, isGradient, verts, obj);
        }

        static void ParseTexturedPrim(byte[] buf, long p, bool isQuad, bool isGourad, bool noLight,
                                      Vector3[] verts, TmdObjectData obj,
                                      TimData tim = null)
        {
            // UV header (all textured types):
            //   Tri:  u0,v0,cba(4), u1,v1,tsb(4), u2,v2,unused(4)                   = 12 bytes
            //   Quad: + u3,v3,unused1(4) at p+12 before vertex data                 = 16 bytes
            // Vertex data after UV header:
            //   noLight:   r,g,b,unused(4), v0(2),v1(2),v2(2)[,v3(2)]
            //   flat lit:  n0(2), v0(2),v1(2),v2(2)[,v3(2)]
            //   gourad lit: n0(2),v0(2), n1(2),v1(2), n2(2),v2(2)[, n3(2),v3(2)]
            if (p + 12 > buf.Length) return;

            byte u0 = buf[p + 0], v0 = buf[p + 1];
            ushort cba = RL16(buf, p + 2);
            byte u1 = buf[p + 4], v1 = buf[p + 5];
            ushort tsb = RL16(buf, p + 6);
            byte u2 = buf[p + 8], v2 = buf[p + 9];

            int texPageX = tsb & 0x0F;          // bits [3:0] = X page (0-15)
            int texPageY = (tsb >> 4) & 1;       // bit  [4]   = Y page (0 or 1)
            obj.texturePage = texPageX;
            obj.tsb = tsb;
            obj.cba = cba;

            // Extract CLUT row and column from CBA:
            // bits [14:6]  = CLUT Y position in VRAM  (which palette row)
            // bits [5:0]×16 = CLUT X position in VRAM (which column offset within that row)
            // Reference: textureCLUTYPosition = (cba >> 6) & 0x1FF
            //            textureCLUTXPosition = (cba & 0x3F) << 4
            int clutVramY = (cba & 0b0111111111000000) >> 6;
            int clutVramX = (cba & 0b0000000000111111) << 4; // absolute VRAM X of palette start

            // Compute pixel-space UV offset from texture page position + TIM VRAM origin.
            // PS1 U/V (0-255) are texel offsets WITHIN the current texture page.
            // Each page X is 64 VRAM half-words wide regardless of bit depth:
            //   4bpp: 64 HW * 4 texels/HW = 256 texels per page
            //   8bpp: 64 HW * 2 texels/HW = 128 texels per page
            //   16bpp: 64 HW * 1 texel/HW = 64 texels per page
            // TIM vramX is in half-word units; multiply by bpp-factor to get texel X.
            float pixW = 256f, pixH = 256f;
            float pageOffsetX = 0f, pageOffsetY = 0f;
            if (tim != null)
            {
                pixW = tim.pixelWidth;
                pixH = tim.pixelHeight;
                int texelsPerPage = tim.bitsPerPixel == 4 ? 256
                                  : tim.bitsPerPixel == 8 ? 128
                                  : 64; // 16bpp
                int vramXScale    = tim.bitsPerPixel == 4 ? 4
                                  : tim.bitsPerPixel == 8 ? 2
                                  : 1;  // 16bpp
                pageOffsetX = texPageX * texelsPerPage - tim.vramX * vramXScale;
                pageOffsetY = texPageY * 256          - tim.vramY;
            }

            // For quads u3/v3 are in the UV header BEFORE vertex data
            long idxOff = p + 12;
            byte u3 = 0, v3 = 0;
            if (isQuad)
            {
                u3 = buf[idxOff + 0]; v3 = buf[idxOff + 1];
                idxOff += 4; // skip u3,v3,unused1 → now at vertex data
            }

            int i0, i1, i2, i3 = 0;
            if (noLight)
            {
                // No-light textured: 4-byte color block (r,g,b,unused), then vertices (no normals)
                idxOff += 4;
                i0 = RL16S_idx(buf, idxOff); idxOff += 2;
                i1 = RL16S_idx(buf, idxOff); idxOff += 2;
                i2 = RL16S_idx(buf, idxOff); idxOff += 2;
                if (isQuad) { i3 = RL16S_idx(buf, idxOff); }
            }
            else if (isGourad)
            {
                // Gourad lit: n0,v0, n1,v1, n2,v2 [, n3,v3]
                idxOff += 2; i0 = RL16S_idx(buf, idxOff); idxOff += 2;
                idxOff += 2; i1 = RL16S_idx(buf, idxOff); idxOff += 2;
                idxOff += 2; i2 = RL16S_idx(buf, idxOff); idxOff += 2;
                if (isQuad) { idxOff += 2; i3 = RL16S_idx(buf, idxOff); }
            }
            else
            {
                // Flat lit: n0, v0, v1, v2 [, v3]
                idxOff += 2; // skip n0
                i0 = RL16S_idx(buf, idxOff); idxOff += 2;
                i1 = RL16S_idx(buf, idxOff); idxOff += 2;
                i2 = RL16S_idx(buf, idxOff); idxOff += 2;
                if (isQuad) { i3 = RL16S_idx(buf, idxOff); }
            }

            if (!IndicesValid(verts, i0, i1, i2)) return;

            // Normalize UV: page-relative texel → 0..1 within the TIM image.
            // pageOffsetX/Y account for the texture page position relative to the TIM VRAM origin.
            // +0.0001f bias matches the reference DW1ModelConverter TexCoord constructor.
            // Without it, all integer PS1 UV values land exactly on texel boundaries;
            // after the V-flip, floor() samples 1 row above the correct texel → noise.
            Vector2 NormUV(byte u, byte v) =>
                new Vector2((pageOffsetX + u + 0.0001f) / pixW, (pageOffsetY + v + 0.0001f) / pixH);

            AddTexturedTri(obj, verts, i0, i1, i2,
                NormUV(u0, v0), NormUV(u1, v1), NormUV(u2, v2), clutVramY, clutVramX);

            if (isQuad && IndicesValid(verts, i2, i1, i3))
                AddTexturedTri(obj, verts, i2, i1, i3,
                    NormUV(u2, v2), NormUV(u1, v1), NormUV(u3, v3), clutVramY, clutVramX);
        }

        static void ParseUntexturedPrim(byte[] buf, long p, bool isQuad, bool isGourad, bool noLight, bool isGradient,
                                        Vector3[] verts, TmdObjectData obj)
        {
            // Layout after 4-byte color header (r,g,b,mode):
            //   noLight:       v0(2),v1(2),v2(2)[,v3(2)]  — no normals
            //   flat lit:      n0(2),v0(2),v1(2),v2(2)[,v3(2)]
            //   gourad lit:    n0(2),v0(2),n1(2),v1(2),n2(2),v2(2)[,n3(2),v3(2)]
            //   flat gradient: r1,g1,b1,pad(4)+r2,g2,b2,pad(4) BEFORE the n0+vertices
            if (p + 4 > buf.Length) return;

            byte r = buf[p + 0], g = buf[p + 1], b = buf[p + 2];
            var col = new Color(r / 255f, g / 255f, b / 255f, 1f);

            long idxOff = p + 4;
            // Flat gradient: 2 extra color blocks (8 bytes) before normal+vertices
            if (isGradient && !noLight && !isGourad) idxOff += 8;

            int i0, i1, i2, i3 = 0;
            if (noLight)
            {
                // No-light: vertices directly, no normals
                i0 = RL16S_idx(buf, idxOff); idxOff += 2;
                i1 = RL16S_idx(buf, idxOff); idxOff += 2;
                i2 = RL16S_idx(buf, idxOff); idxOff += 2;
                if (isQuad) { i3 = RL16S_idx(buf, idxOff); }
            }
            else if (isGourad)
            {
                // Gourad lit: n0,v0, n1,v1, n2,v2 [, n3,v3]
                idxOff += 2; i0 = RL16S_idx(buf, idxOff); idxOff += 2;
                idxOff += 2; i1 = RL16S_idx(buf, idxOff); idxOff += 2;
                idxOff += 2; i2 = RL16S_idx(buf, idxOff); idxOff += 2;
                if (isQuad) { idxOff += 2; i3 = RL16S_idx(buf, idxOff); }
            }
            else
            {
                // Flat lit: n0, v0, v1, v2 [, v3]
                idxOff += 2; // skip n0
                i0 = RL16S_idx(buf, idxOff); idxOff += 2;
                i1 = RL16S_idx(buf, idxOff); idxOff += 2;
                i2 = RL16S_idx(buf, idxOff); idxOff += 2;
                if (isQuad) { i3 = RL16S_idx(buf, idxOff); }
            }

            if (!IndicesValid(verts, i0, i1, i2)) return;

            AddUntexturedTri(obj, verts, i0, i1, i2, col);
            if (isQuad && IndicesValid(verts, i2, i1, i3))
                AddUntexturedTri(obj, verts, i2, i1, i3, col);
        }

        static bool IndicesValid(Vector3[] verts, params int[] indices)
        {
            foreach (int idx in indices)
                if (idx < 0 || idx >= verts.Length) return false;
            return true;
        }

        static void AddTexturedTri(TmdObjectData obj, Vector3[] verts, int i0, int i1, int i2,
                                   Vector2 uv0, Vector2 uv1, Vector2 uv2, int clutVramY = 0, int clutVramX = 0)
        {
            // Unity winding: flip i1/i2 to convert from PS1 CW → Unity CCW
            obj.triIndices.Add(i0); obj.triIndices.Add(i2); obj.triIndices.Add(i1);
            obj.triUVs.Add(uv0);   obj.triUVs.Add(uv2);   obj.triUVs.Add(uv1);
            obj.triColors.Add(Color.white); obj.triColors.Add(Color.white); obj.triColors.Add(Color.white);
            obj.triClutRows.Add(clutVramY);   obj.triClutRows.Add(clutVramY);   obj.triClutRows.Add(clutVramY);
            obj.triClutXVrams.Add(clutVramX); obj.triClutXVrams.Add(clutVramX); obj.triClutXVrams.Add(clutVramX);
        }

        static void AddUntexturedTri(TmdObjectData obj, Vector3[] verts, int i0, int i1, int i2, Color col)
        {
            obj.triIndices.Add(i0); obj.triIndices.Add(i2); obj.triIndices.Add(i1);
            obj.triUVs.Add(Vector2.zero); obj.triUVs.Add(Vector2.zero); obj.triUVs.Add(Vector2.zero);
            obj.triColors.Add(col); obj.triColors.Add(col); obj.triColors.Add(col);
            obj.triClutRows.Add(0); obj.triClutRows.Add(0); obj.triClutRows.Add(0);
        }

        // =========================================================================
        // MMD Animation parser
        // =========================================================================
        static List<AnimData> ParseAnimations(byte[] mmd, long animOffsetPtr, int numberOfNodes)
        {
            var result = new List<AnimData>();
            if (animOffsetPtr + 4 > mmd.Length) return result;

            // First uint32 = total byte length of offsets array
            uint offsetsBytes = RL32(mmd, animOffsetPtr);
            int  numAnims     = (int)(offsetsBytes / 4);

            var offsets = new uint[numAnims];
            for (int i = 0; i < numAnims && animOffsetPtr + i * 4 + 4 <= mmd.Length; i++)
                offsets[i] = RL32(mmd, animOffsetPtr + i * 4);

            int numPostureNodes = numberOfNodes - 1;

            foreach (uint animOffset in offsets)
            {
                if (animOffset == 0) { result.Add(null); continue; }
                long animStart = animOffsetPtr + animOffset;
                if (animStart >= mmd.Length) { result.Add(null); continue; }

                try
                {
                    var anim = ParseOneAnimation(mmd, animStart, numPostureNodes);
                    result.Add(anim);
                }
                catch { result.Add(null); }
            }
            return result;
        }

        static AnimData ParseOneAnimation(byte[] mmd, long start, int numPostureNodes)
        {
            long o = start;
            if (o + 2 > mmd.Length) return null;

            byte numSeq  = mmd[o++];
            byte hasScB  = mmd[o++];
            bool hasScale = hasScB == 128;

            int postureStride = hasScale ? 18 : 12; // 9×Int16 or 6×Int16

            var pose = new PostureData[numPostureNodes];
            for (int i = 0; i < numPostureNodes && o + postureStride <= mmd.Length; i++, o += postureStride)
            {
                long p = o;
                if (hasScale)
                {
                    pose[i] = new PostureData
                    {
                        hasScale = true,
                        scaleX = RL16S(mmd, p + 0),
                        scaleY = RL16S(mmd, p + 2),
                        scaleZ = RL16S(mmd, p + 4),
                        rotX   = RL16S(mmd, p + 6),
                        rotY   = RL16S(mmd, p + 8),
                        rotZ   = RL16S(mmd, p + 10),
                        posX   = RL16S(mmd, p + 12),
                        posY   = RL16S(mmd, p + 14),
                        posZ   = RL16S(mmd, p + 16),
                    };
                }
                else
                {
                    pose[i] = new PostureData
                    {
                        rotX = RL16S(mmd, p + 0),
                        rotY = RL16S(mmd, p + 2),
                        rotZ = RL16S(mmd, p + 4),
                        posX = RL16S(mmd, p + 6),
                        posY = RL16S(mmd, p + 8),
                        posZ = RL16S(mmd, p + 10),
                        scaleX = 4096, scaleY = 4096, scaleZ = 4096,
                    };
                }
            }

            // Sequences
            var sequences = new List<object>();
            ParseSequences(mmd, o, sequences);

            return new AnimData
            {
                numberOfSequences = numSeq,
                hasScale          = hasScale,
                pose              = pose,
                sequences         = sequences,
            };
        }

        static void ParseSequences(byte[] mmd, long o, List<object> sequences)
        {
            while (o + 2 <= mmd.Length)
            {
                ushort header = RL16(mmd, o); o += 2;
                if (header == 0) break;

                int opcode       = (header & 0xF000) >> 12;
                int seqIndex     = (header & 0x0FFF);

                if (opcode == 0) // AxisSeq
                {
                    var axis = new AxisSeq { sequenceIndex = seqIndex };
                    while (o + 2 <= mmd.Length)
                    {
                        ushort kbits = RL16(mmd, o);
                        if ((kbits & 0x8000) == 0) break;
                        o += 2;
                        var kf = ReadKeyframe(mmd, ref o, kbits);
                        axis.keyframes.Add(kf);
                    }
                    sequences.Add(axis);
                }
                else if (opcode == 1) // LoopStart
                {
                    sequences.Add(new LoopStartSeq { repetitions = seqIndex });
                }
                else if (opcode == 2) // LoopEnd
                {
                    if (o + 2 > mmd.Length) break;
                    ushort startIdx = RL16(mmd, o); o += 2;
                    sequences.Add(new LoopEndSeq { sequenceIndex = seqIndex, startSequenceIndex = startIdx });
                }
                else if (opcode == 3) // Texture blit
                {
                    if (o + 6 > mmd.Length) break;
                    ushort srcBits  = RL16(mmd, o); o += 2;
                    ushort sizeBits = RL16(mmd, o); o += 2;
                    ushort dstBits  = RL16(mmd, o); o += 2;

                    int srcY  = srcBits & 0xFF;
                    int srcX  = (srcBits >> 6) & ~1;
                    int height= sizeBits & 0x3F;
                    int width = (sizeBits >> 6);
                    int destY = dstBits & 0xFF;
                    int destX = (dstBits >> 6) & ~1;

                    sequences.Add(new TextureSeq
                    {
                        sequenceIndex = seqIndex,
                        srcX = srcX, srcY = srcY,
                        destX = destX, destY = destY,
                        w = width, h = height,
                    });
                }
                else if (opcode == 4) // Sound: soundId (byte) + vabId (byte)
                {
                    if (o + 2 > mmd.Length) break;
                    byte soundId = mmd[o];     o++;
                    byte vabId   = mmd[o];     o++;
                    sequences.Add(new SoundSeq { sequenceIndex = seqIndex, soundId = soundId, vabId = vabId });
                }
            }
        }

        static AnimKeyframe ReadKeyframe(byte[] mmd, ref long o, ushort bits)
        {
            var kf = new AnimKeyframe();
            kf.nodeIndex  = bits & 0x3F;
            kf.hasScaleX  = (bits & 0x4000) != 0;
            kf.hasScaleY  = (bits & 0x2000) != 0;
            kf.hasScaleZ  = (bits & 0x1000) != 0;
            kf.hasRotX    = (bits & 0x0800) != 0;
            kf.hasRotY    = (bits & 0x0400) != 0;
            kf.hasRotZ    = (bits & 0x0200) != 0;
            kf.hasPosX    = (bits & 0x0100) != 0;
            kf.hasPosY    = (bits & 0x0080) != 0;
            kf.hasPosZ    = (bits & 0x0040) != 0;

            if (o + 2 > mmd.Length) return kf;
            kf.duration = RL16(mmd, o); o += 2;

            if (kf.hasScaleX) { kf.scaleX = RL16S(mmd, o); o += 2; }
            if (kf.hasScaleY) { kf.scaleY = RL16S(mmd, o); o += 2; }
            if (kf.hasScaleZ) { kf.scaleZ = RL16S(mmd, o); o += 2; }
            if (kf.hasRotX)   { kf.rotX   = RL16S(mmd, o); o += 2; }
            if (kf.hasRotY)   { kf.rotY   = RL16S(mmd, o); o += 2; }
            if (kf.hasRotZ)   { kf.rotZ   = RL16S(mmd, o); o += 2; }
            if (kf.hasPosX)   { kf.posX   = RL16S(mmd, o); o += 2; }
            if (kf.hasPosY)   { kf.posY   = RL16S(mmd, o); o += 2; }
            if (kf.hasPosZ)   { kf.posZ   = RL16S(mmd, o); o += 2; }

            return kf;
        }

        // =========================================================================
        // Build Unity assets
        // =========================================================================
        void CreateAssets(byte[] buf, byte[] mmd, int digimonIndex, string name, string dir,
                          TimData tim, List<TmdObjectData> tmdObjects,
                          NodeEntry[] nodes, int numberOfNodes, List<AnimData> anims)
        {
            // ── Rest pose (used only for bone local transforms) ───────────────────
            PostureData[] restPose = null;
            for (int ai = 0; ai < anims.Count; ai++)
                if (anims[ai]?.pose != null) { restPose = anims[ai].pose; break; }

            // ── objToBone: TMD object index → bone index ──────────────────────────
            var objToBone = new Dictionary<int, int>();
            for (int i = 0; i < numberOfNodes; i++)
                if (nodes[i].objectIndex >= 0 && !objToBone.ContainsKey(nodes[i].objectIndex))
                    objToBone[nodes[i].objectIndex] = i;

            _status += $"objToBone entries: {objToBone.Count} (nodes: {numberOfNodes}, tmdObjs: {tmdObjects.Count})\n";
            for (int i = 0; i < numberOfNodes; i++)
                _status += $"  node[{i}] objIdx={nodes[i].objectIndex} parentIdx={nodes[i].nodeIndex}\n";

            // ── Texture → PNG file (CLUTMap: single flat image, per-pixel CLUT assignment) ──
            // Rather than stacking all CLUT rows as a vertical atlas, we:
            //   1. Resolve each unique (clutVramY, clutVramX) pair into a pre-sliced Color32[] palette.
            //      This handles models where multiple polygons share the same CLUT Y row but use
            //      different column offsets (clutX) to select different sub-palettes within that row.
            //      Reference: material-tracker.ts getCLUTFromVRAMXY(vramX, vramY) → cluts[y].slice(x)
            //   2. Rasterize every triangle's UV footprint into a CLUTMap with its resolved palette index.
            //   3. Decode each pixel using the palette assigned to it by the CLUTMap.
            //   4. Export one flat PNG (same size as the TIM pixel data).
            int numClutRows = Mathf.Max(1, tim.numClutRows);
            int pixW2 = Mathf.Clamp(Mathf.Max(1, tim.pixelWidth),  1, 16384);
            int pixH2 = Mathf.Clamp(Mathf.Max(1, tim.pixelHeight), 1, 16384);

            // Build resolved palettes: one entry per unique (clutVramY, clutVramX) combination.
            // The entry is a correctly-offset Color32[] so LookupClut(palette, rawIndex) is direct.
            var resolvedPalettes = new List<Color32[]>();
            var resolvedPalIdx   = new Dictionary<long, byte>(); // key = (clutVramY << 16) | clutVramX

            byte GetResolvedIdx(int cvy, int cvx)
            {
                long key = ((long)cvy << 16) | (uint)cvx;
                if (resolvedPalIdx.TryGetValue(key, out byte ri)) return ri;
                int cr       = Mathf.Clamp(cvy - tim.clutBaseVramY, 0, numClutRows - 1);
                int adjX     = Mathf.Max(0, cvx - tim.clutBaseVramX); // column offset within row
                Color32[] row = (tim.cluts != null && cr < tim.cluts.Length) ? tim.cluts[cr] : null;
                Color32[] pal;
                if (row == null)
                    pal = new Color32[16]; // fallback transparent
                else if (adjX == 0 || adjX >= row.Length)
                    pal = row;
                else
                    pal = row.Skip(adjX).ToArray(); // slice off to the correct sub-palette
                ri = (byte)Mathf.Clamp(resolvedPalettes.Count, 0, 254);
                resolvedPalettes.Add(pal);
                resolvedPalIdx[key] = ri;
                return ri;
            }

            // Build CLUTMap: for each TIM pixel, which resolved palette index covers it?
            var clutMap      = new byte[pixW2 * pixH2]; // default palette 0
            var clutAssigned = new bool[pixW2 * pixH2]; // tracks explicitly painted texels
            foreach (var obj2 in tmdObjects)
            {
                int tc2 = obj2.triIndices.Count / 3;
                for (int t2 = 0; t2 < tc2; t2++)
                {
                    int fi0 = t2 * 3;
                    int cvy = (fi0 < obj2.triClutRows.Count)  ? obj2.triClutRows[fi0]  : tim.clutBaseVramY;
                    int cvx = (fi0 < obj2.triClutXVrams.Count)? obj2.triClutXVrams[fi0]: tim.clutBaseVramX;
                    byte ri = GetResolvedIdx(cvy, cvx);
                    var triUV = new Vector2[3];
                    for (int c2 = 0; c2 < 3; c2++)
                    {
                        int fi = t2 * 3 + c2;
                        Vector2 raw = fi < obj2.triUVs.Count ? obj2.triUVs[fi] : Vector2.zero;
                        triUV[c2] = new Vector2(raw.x * pixW2, raw.y * pixH2);
                    }
                    RasterizeTriangleClut(clutMap, clutAssigned, pixW2, pixH2, triUV, ri);
                }
            }
            // Ensure palette 0 exists (fallback for unassigned texels)
            if (resolvedPalettes.Count == 0) GetResolvedIdx(tim.clutBaseVramY, tim.clutBaseVramX);

            // ── Propagate CLUTMap to expression storage areas ─────────────────────
            // Mirrors DW1ModelConverter CLUTMap::applyModel → TextureInstruction::handleTexture:
            //   handleTexture reads FROM destX/destY  (face display, has CLUT from mesh triangles)
            //              and writes TO   srcX/srcY  (expression storage, no triangles → empty)
            // This ensures expression storage areas use the correct face palette when decoded,
            // so our pixel-blit expression textures show correct colors instead of body-palette yellow.
            {
                var allTexSeqs = new List<TextureSeq>();
                foreach (var a in anims)
                    if (a?.sequences != null)
                        CollectTextureSeqs(a.sequences, allTexSeqs);

                foreach (var ts in allTexSeqs)
                {
                    for (int dy = 0; dy < ts.h; dy++)
                    {
                        for (int dx = 0; dx < ts.w; dx++)
                        {
                            // SOURCE of copy = face display area (destX/destY in our TextureSeq naming)
                            int fromIdx = (ts.destY + dy) * pixW2 + (ts.destX + dx);
                            // DESTINATION of copy = expression storage area (srcX/srcY)
                            int toIdx   = (ts.srcY  + dy) * pixW2 + (ts.srcX  + dx);
                            if (fromIdx >= 0 && fromIdx < clutMap.Length &&
                                toIdx   >= 0 && toIdx   < clutMap.Length)
                            {
                                clutMap[toIdx]      = clutMap[fromIdx];
                                clutAssigned[toIdx] = clutAssigned[fromIdx];
                            }
                        }
                    }
                }
            }

            // Fill unassigned 8×8 blocks with their most-common assigned palette index.
            // Matches DW1ModelConverter CLUTMap::updateBlocks() for edge-region robustness.
            FillClutMapBlocks(clutMap, clutAssigned, pixW2, pixH2);

            // Decode: each pixel uses its assigned resolved palette; V-flip for Unity
            var atlasPixels = new Color32[pixW2 * pixH2];
            for (int py = 0; py < pixH2; py++)
            {
                int srcRow = pixH2 - 1 - py; // V-flip (PS1 row 0=top → Unity row 0=bottom)
                for (int px = 0; px < pixW2; px++)
                {
                    int idx = srcRow * pixW2 + px;
                    int ri  = idx < clutMap.Length ? clutMap[idx] : 0;
                    Color32 c;
                    if (tim.rawIndices != null && ri < resolvedPalettes.Count && idx < tim.rawIndices.Length)
                        c = LookupClut(resolvedPalettes[ri], tim.rawIndices[idx]);
                    else if (idx < tim.pixels.Length)
                        c = tim.pixels[idx];
                    else
                        c = new Color32(0, 0, 0, 0);
                    atlasPixels[py * pixW2 + px] = c;
                }
            }

            var tex = new Texture2D(pixW2, pixH2, TextureFormat.RGBA32, false);
            tex.SetPixels32(atlasPixels);
            tex.Apply();

            string texPath = dir + "/" + name.ToLowerInvariant() + "_tex.png";
            File.WriteAllBytes(texPath.Replace("Assets/", Application.dataPath + "/"), tex.EncodeToPNG());
            AssetDatabase.ImportAsset(texPath);
            ApplyPS1TextureSettings(texPath);   // Point filter, no mipmaps, Clamp — persists via TextureImporter
            var texAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            // ── Material — saved as standalone .mat file on disk ─────────────────────
            var mat = new Material(Shader.Find("Standard"));
            mat.name        = name.ToLowerInvariant() + "_mat";
            mat.mainTexture = texAsset;
            mat.SetFloat("_Mode", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Cutoff", 0.1f);
            mat.renderQueue = 2450;

            string matAssetPath = dir + "/" + name.ToLowerInvariant() + "_mat.mat";
            AssetDatabase.DeleteAsset(matAssetPath);
            AssetDatabase.CreateAsset(mat, matAssetPath);
            mat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);

            // ── Bone hierarchy ────────────────────────────────────────────────────
            var root = new GameObject(name);
            var animator = root.AddComponent<Animator>();
            // PS1→Unity coordinate flip (Y and Z negated) is baked into vertex/bone/anim data.
            // Do NOT use a negative-scale root — FBX format does not reliably support it.

            var boneTransforms = new Transform[numberOfNodes];
            for (int i = 0; i < numberOfNodes; i++)
                boneTransforms[i] = new GameObject("Bone_" + i).transform;

            // Parent: bone 0 → root; bone i → its nodeIndex parent (or root if invalid)
            boneTransforms[0].SetParent(root.transform);
            for (int i = 1; i < numberOfNodes; i++)
            {
                int parentIdx = nodes[i].nodeIndex;
                if (parentIdx < 0 || parentIdx >= numberOfNodes || parentIdx == i)
                    boneTransforms[i].SetParent(root.transform);
                else
                    boneTransforms[i].SetParent(boneTransforms[parentIdx]);
            }

            // Local transforms from rest pose
            for (int i = 1; i < numberOfNodes; i++)
            {
                PostureData pd = (restPose != null && i - 1 < restPose.Length) ? restPose[i - 1] : default;
                boneTransforms[i].localPosition = new Vector3(pd.posX, -pd.posY, -pd.posZ) * PS1_POS_SCALE;
                // Use explicit XYZ intrinsic order (matches Three.js default Euler 'XYZ').
                // Negate Y and Z to convert from PS1/Three.js right-handed to Unity left-handed coords.
                boneTransforms[i].localRotation =
                    Quaternion.AngleAxis( pd.rotX * BAM_TO_DEG, Vector3.right) *
                    Quaternion.AngleAxis(-pd.rotY * BAM_TO_DEG, Vector3.up) *
                    Quaternion.AngleAxis(-pd.rotZ * BAM_TO_DEG, Vector3.forward);
                float sx = pd.hasScale ? pd.scaleX * PS1_SCALE_FACTOR : 1f;
                float sy = pd.hasScale ? pd.scaleY * PS1_SCALE_FACTOR : 1f;
                float sz = pd.hasScale ? pd.scaleZ * PS1_SCALE_FACTOR : 1f;
                boneTransforms[i].localScale = new Vector3(sx, sy, sz);
            }

            // Bone paths for animation curves (relative to root)
            var bonePaths = new string[numberOfNodes];
            for (int i = 0; i < numberOfNodes; i++)
                bonePaths[i] = AnimationUtility.CalculateTransformPath(boneTransforms[i], root.transform);

            // Renderer paths are collected during the mesh loop below.
            var allRendererPaths = new List<string>();

            // ── Per-object meshes ─────────────────────────────────────────────────
            // Each TMD object becomes one MeshFilter child of its bone.
            // Vertices are in bone-local space — the hierarchy provides all transforms,
            // exactly as the PS1 engine does.
            //
            // A TMD object can have BOTH textured prims (UV from atlas) and untextured
            // prims (UV=0,0 flat color). If both go into one mesh with one atlas material,
            // the untextured tris sample the atlas at UV(0,1) — wrong pixels (noise/black).
            // Fix: split into two submeshes per object:
            //   submesh 0 → textured tris  → atlas material (mat)
            //   submesh 1 → untextured tris → Unlit/Color material with the polygon's color
            // Cache solid-color materials so identical colors share one .mat file.
            var solidMatCache = new Dictionary<string, Material>();

            for (int oi = 0; oi < tmdObjects.Count; oi++)
            {
                var obj     = tmdObjects[oi];
                int boneIdx = objToBone.ContainsKey(oi) ? objToBone[oi] : 0;
                boneIdx     = Mathf.Clamp(boneIdx, 0, numberOfNodes - 1);

                _status += $"  Obj_{oi} → Bone_{boneIdx}: {obj.triIndices.Count/3} tris, {obj.vertices.Length} verts\n";
                if (obj.triIndices.Count == 0) continue;

                // Split tris: textured (UV != 0) → atlas submesh; untextured (UV=0,0) → solid color submesh
                var texVerts  = new List<Vector3>(); var texUVs   = new List<Vector2>(); var texTris  = new List<int>();
                var colVerts  = new List<Vector3>(); var colColors = new List<Color32>(); var colTris  = new List<int>();

                int triCount = obj.triIndices.Count / 3;
                for (int t = 0; t < triCount; t++)
                {
                    int fi0 = t * 3;
                    Vector2 u0 = fi0     < obj.triUVs.Count ? obj.triUVs[fi0]     : Vector2.zero;
                    Vector2 u1 = fi0 + 1 < obj.triUVs.Count ? obj.triUVs[fi0 + 1] : Vector2.zero;
                    Vector2 u2 = fi0 + 2 < obj.triUVs.Count ? obj.triUVs[fi0 + 2] : Vector2.zero;
                    bool untex = (u0 == Vector2.zero && u1 == Vector2.zero && u2 == Vector2.zero);

                    if (untex)
                    {
                        // Untextured prim — use polygon color stored in triColors
                        int baseCorner = colVerts.Count;
                        bool skip = false;
                        Color32 col32 = (Color32)(fi0 < obj.triColors.Count ? obj.triColors[fi0] : Color.white);
                        for (int c = 0; c < 3; c++)
                        {
                            int fi = fi0 + c;
                            int vi = fi < obj.triIndices.Count ? obj.triIndices[fi] : 0;
                            if (vi >= obj.vertices.Length) { skip = true; break; }
                            colVerts.Add(obj.vertices[vi]);
                            colColors.Add(col32);
                        }
                        if (!skip)
                        {
                            colTris.Add(baseCorner); colTris.Add(baseCorner + 1); colTris.Add(baseCorner + 2);
                        }
                        else
                        {
                            int n = colVerts.Count - baseCorner;
                            colVerts.RemoveRange(baseCorner, n);
                            colColors.RemoveRange(baseCorner, n);
                        }
                    }
                    else
                    {
                        // Textured prim — use atlas UV
                        int baseCorner = texVerts.Count;
                        bool skip = false;
                        for (int c = 0; c < 3; c++)
                        {
                            int fi = fi0 + c;
                            int vi = fi < obj.triIndices.Count ? obj.triIndices[fi] : 0;
                            if (vi >= obj.vertices.Length) { skip = true; break; }
                            texVerts.Add(obj.vertices[vi]);
                            Vector2 rawUV = fi < obj.triUVs.Count ? obj.triUVs[fi] : Vector2.zero;
                            // rawUV already has the +0.0001f bias from NormUV.
                            // Unity UV origin is bottom-left → flip V.
                            // 1f - rawUV.y = (pixH - v - 0.0001f)/pixH → floor gives pixH-1-v = correct Unity row.
                            texUVs.Add(new Vector2(rawUV.x, 1f - rawUV.y));
                        }
                        if (!skip)
                        {
                            texTris.Add(baseCorner); texTris.Add(baseCorner + 1); texTris.Add(baseCorner + 2);
                        }
                        else
                        {
                            int n = texVerts.Count - baseCorner;
                            texVerts.RemoveRange(baseCorner, n);
                            texUVs.RemoveRange(baseCorner, n);
                        }
                    }
                }

                if (texVerts.Count == 0 && colVerts.Count == 0) continue;

                // Merge into a single mesh with up to 2 submeshes
                var allVerts    = new List<Vector3>();
                var allUVs      = new List<Vector2>();
                var allColors   = new List<Color32>();
                var submeshTris = new List<List<int>>();
                var mats        = new List<Material>();

                if (texVerts.Count > 0)
                {
                    int off = allVerts.Count;
                    allVerts.AddRange(texVerts);
                    allUVs.AddRange(texUVs);
                    allColors.AddRange(Enumerable.Repeat(new Color32(255, 255, 255, 255), texVerts.Count));
                    submeshTris.Add(texTris.Select(i => i + off).ToList());
                    mats.Add(mat);
                }
                if (colVerts.Count > 0)
                {
                    int off = allVerts.Count;
                    allVerts.AddRange(colVerts);
                    allUVs.AddRange(Enumerable.Repeat(Vector2.zero, colVerts.Count));
                    allColors.AddRange(colColors);
                    submeshTris.Add(colTris.Select(i => i + off).ToList());

                    // Get or create Unlit/Color material for this polygon color
                    Color32 solidColor = colColors[0];
                    string colorKey = $"{solidColor.r}_{solidColor.g}_{solidColor.b}";
                    if (!solidMatCache.TryGetValue(colorKey, out var solidMat))
                    {
                        solidMat      = new Material(Shader.Find("Unlit/Color"));
                        solidMat.name = name.ToLowerInvariant() + "_solid_" + colorKey;
                        solidMat.color = new Color(solidColor.r / 255f, solidColor.g / 255f, solidColor.b / 255f, 1f);
                        string solidMatPath = dir + "/" + solidMat.name + ".mat";
                        AssetDatabase.DeleteAsset(solidMatPath);
                        AssetDatabase.CreateAsset(solidMat, solidMatPath);
                        solidMat = AssetDatabase.LoadAssetAtPath<Material>(solidMatPath);
                        solidMatCache[colorKey] = solidMat;
                    }
                    mats.Add(solidMat);
                }

                var partMesh = new Mesh { name = name + "_Obj" + oi };
                partMesh.SetVertices(allVerts);
                partMesh.SetUVs(0, allUVs);
                partMesh.SetColors(allColors);
                partMesh.subMeshCount = submeshTris.Count;
                for (int si = 0; si < submeshTris.Count; si++)
                    partMesh.SetTriangles(submeshTris[si], si);
                partMesh.RecalculateBounds();
                partMesh.RecalculateNormals();
                // Mesh is kept in memory only — geometry will be embedded in the FBX

                var partGO = new GameObject("Obj_" + oi);
                partGO.transform.SetParent(boneTransforms[boneIdx]);
                partGO.transform.localPosition = Vector3.zero;
                partGO.transform.localRotation = Quaternion.identity;
                partGO.transform.localScale    = Vector3.one;

                partGO.AddComponent<MeshFilter>().sharedMesh       = partMesh;
                partGO.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();

                // Only record renderer path if this object has textured tris.
                // Expression material swaps target submesh 0 (atlas material).
                // Untextured-only objects use Unlit/Color and never need face swaps.
                if (texVerts.Count > 0)
                {
                    string rpath = (boneIdx < bonePaths.Length)
                        ? bonePaths[boneIdx] + "/Obj_" + oi
                        : "Obj_" + oi;
                    allRendererPaths.Add(rpath);
                }
            }

            _status += $"  Renderers collected: {allRendererPaths.Count}\n";

            // ── Bake expression materials (pixel blit → new texture per sequenceIndex) ──
            // Reference: Three.js viewer copies decoded RGBA pixels (srcX/srcY→destX/destY)
            // and swaps ALL mesh materials. We do the same: bake per-expression PNG+Material.
            const float SEQUENCE_DT = 60f / 1000f;
            // Store paths (strings) instead of Material objects — objects can become fake-null
            // after AssetDatabase.Refresh(); paths are always stable plain strings.
            var animExprKeyPaths = new List<(float time, string matPath)>[anims.Count];
            for (int ai = 0; ai < anims.Count; ai++)
            {
                animExprKeyPaths[ai] = new List<(float, string)>();
                if (anims[ai]?.sequences == null) continue;

                var texSeqs = new List<TextureSeq>();
                CollectTextureSeqs(anims[ai].sequences, texSeqs);
                if (texSeqs.Count == 0) continue;

                // Group blits by sequenceIndex (multiple blits at same seq idx = one expression state)
                var seqGroups = new SortedDictionary<int, List<TextureSeq>>();
                foreach (var ts in texSeqs)
                {
                    if (!seqGroups.ContainsKey(ts.sequenceIndex))
                        seqGroups[ts.sequenceIndex] = new List<TextureSeq>();
                    seqGroups[ts.sequenceIndex].Add(ts);
                }

                foreach (var kvp in seqGroups)
                {
                    int seqIdx = kvp.Key;
                    var blits  = kvp.Value;

                    // Stable file name: hash of the blit signature so shared expressions reuse files
                    string sig = string.Join("_", blits.Select(b => $"{b.srcX}_{b.srcY}_{b.destX}_{b.destY}_{b.w}_{b.h}"));
                    string exprPngPath = dir + "/" + name.ToLowerInvariant() + "_expr_" + sig + ".png";
                    string exprMatPath = dir + "/" + name.ToLowerInvariant() + "_expr_" + sig + ".mat";

                    // Always regenerate — never reuse stale cached files.
                    // (Main texture is also always overwritten; expression textures must match.)
                    Material exprMat;
                    {
                        // Clone atlasPixels (already V-flipped for Unity), apply blits in atlas space.
                        // TIM pixel (tx, ty) lives at atlasPixels[(pixH2-1-ty)*pixW2+tx].
                        var exprPixels = (Color32[])atlasPixels.Clone();
                        foreach (var b in blits)
                        {
                            for (int dy = 0; dy < b.h; dy++)
                            {
                                for (int dx = 0; dx < b.w; dx++)
                                {
                                    int si = (pixH2 - 1 - (b.srcY  + dy)) * pixW2 + (b.srcX  + dx);
                                    int di = (pixH2 - 1 - (b.destY + dy)) * pixW2 + (b.destX + dx);
                                    if (si >= 0 && si < exprPixels.Length && di >= 0 && di < exprPixels.Length)
                                        exprPixels[di] = exprPixels[si];
                                }
                            }
                        }

                        var exprTex = new Texture2D(pixW2, pixH2, TextureFormat.RGBA32, false);
                        exprTex.SetPixels32(exprPixels);
                        exprTex.Apply();
                        File.WriteAllBytes(exprPngPath.Replace("Assets/", Application.dataPath + "/"), exprTex.EncodeToPNG());
                        AssetDatabase.ImportAsset(exprPngPath);
                        ApplyPS1TextureSettings(exprPngPath);   // Point filter, no mipmaps, Clamp
                        var exprTexAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(exprPngPath);

                        exprMat = new Material(mat);
                        exprMat.mainTexture = exprTexAsset;
                        AssetDatabase.DeleteAsset(exprMatPath);
                        AssetDatabase.CreateAsset(exprMat, exprMatPath);
                        exprMat = AssetDatabase.LoadAssetAtPath<Material>(exprMatPath);
                    }

                    animExprKeyPaths[ai].Add((seqIdx * SEQUENCE_DT, exprMatPath));
                }
                _status += $"  Anim[{ai}]: {animExprKeyPaths[ai].Count} expression keyframes baked\n";
            }

            // ── Export FBX (geometry + bone hierarchy only) ──────────────────────
            // Clips are built AFTER FBX import (see below) so that material
            // ObjectReferenceKeyframe values are serialised with up-to-date GUIDs.
            // Building clips before FBX export can leave mat/exprMat pointers stale
            // (fake-null) after Unity's import pipeline runs, causing pink mesh on play.
            // Animation clips go to a separate .asset file — see Augmon.fbx.meta:
            // clipAnimations:[] means Augmon's clips come from animation tracks baked
            // INTO the FBX binary by Blender/Maya, not via AddObjectToAsset.
            // AddObjectToAsset on a freshly-exported binary FBX causes SaveAssets() to
            // overwrite the binary with Unity YAML, which corrupts it on next Refresh().
            string fbxAssetPath = dir + "/" + name + ".fbx";
            string fbxAbsPath   = fbxAssetPath.Replace("Assets/", Application.dataPath + "/");
            string metaAbsPath  = fbxAbsPath + ".meta";

            // Remove stale files (a previous run may have left a Unity-YAML asset at .fbx).
            AssetDatabase.DeleteAsset(fbxAssetPath);
            if (System.IO.File.Exists(fbxAbsPath))  System.IO.File.Delete(fbxAbsPath);
            if (System.IO.File.Exists(metaAbsPath)) System.IO.File.Delete(metaAbsPath);
            // Also remove leftover .asset from old code paths.
            AssetDatabase.DeleteAsset(dir + "/" + name + ".asset");

            bool fbxExported = false;
            var exporterType = System.Type.GetType(
                "UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor");
            if (exporterType != null)
            {
                var exportMethod = exporterType.GetMethod("ExportObject",
                    new[] { typeof(string), typeof(UnityEngine.Object) });
                if (exportMethod != null)
                {
                    exportMethod.Invoke(null, new object[] { fbxAbsPath, root });
                    long sz = System.IO.File.Exists(fbxAbsPath)
                        ? new System.IO.FileInfo(fbxAbsPath).Length : 0L;
                    fbxExported = sz > 1024;
                    _status += fbxExported
                        ? $"FBX exported OK ({sz} B): {fbxAssetPath}\n"
                        : $"WARNING: FBX file too small ({sz} B) — export may have failed.\n";
                }
                else _status += "WARNING: ExportObject method not found in ModelExporter.\n";
            }
            else _status += "WARNING: FBX Exporter package not found (com.unity.formats.fbx).\n";

            GameObject.DestroyImmediate(root);

            if (fbxExported)
                AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceSynchronousImport);

            // Refresh first — FBX import must fully complete before we read back any assets.
            AssetDatabase.Refresh();

            // ── Reload material references after FBX import + Refresh ─────────────
            // Paths are plain strings and survive Refresh unchanged; load fresh objects now.
            mat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath) ?? mat;
            var animExprKeys = new List<(float time, Material exprMat)>[animExprKeyPaths.Length];
            for (int ai = 0; ai < animExprKeyPaths.Length; ai++)
            {
                animExprKeys[ai] = new List<(float, Material)>();
                foreach (var (kt, mpath) in animExprKeyPaths[ai])
                {
                    var freshMat = AssetDatabase.LoadAssetAtPath<Material>(mpath);
                    if (freshMat != null)
                        animExprKeys[ai].Add((kt, freshMat));
                    else
                        _status += $"  WARNING: expr mat not found after Refresh: {mpath}\n";
                }
            }

            // ── Build animation clips in memory ──────────────────────────────────
            var allClips   = new List<AnimationClip>();
            int clipsAdded = 0;
            for (int ai = 0; ai < anims.Count; ai++)
            {
                if (anims[ai] == null) continue;

                var clip = BuildAnimClip(anims[ai], bonePaths, name, ai, numberOfNodes,
                                         allRendererPaths, mat, animExprKeys[ai]);
                if (clip == null) { _status += $"  Anim[{ai}]: null/empty (skipped)\n"; continue; }
                _status += $"  Anim[{ai}]: {clip.name} duration={clip.length:F2}s curves={AnimationUtility.GetCurveBindings(clip).Length}\n";
                allClips.Add(clip);
                clipsAdded++;
            }
            _status += $"Total clips built: {clipsAdded}\n";

            // ── Write animation clips to a companion .asset file ─────────────────
            // Binary FBX files cannot host AddObjectToAsset sub-assets reliably —
            // SaveAssets() overwrites the binary with Unity YAML on fresh exports.
            // A _clips.asset is the correct Unity pattern for procedural clips.
            string clipsAssetPath = dir + "/" + name + "_clips.asset";
            AssetDatabase.DeleteAsset(clipsAssetPath);
            string clipContainerPath = "(none — no clips built)";
            if (allClips.Count > 0)
            {
                var container = ScriptableObject.CreateInstance<AnimationClipContainer>();
                AssetDatabase.CreateAsset(container, clipsAssetPath);
                foreach (var c in allClips)
                    AssetDatabase.AddObjectToAsset(c, clipsAssetPath);
                AssetDatabase.SaveAssets();
                clipContainerPath = clipsAssetPath;
                _status += $"  Clips saved ({allClips.Count}): {clipsAssetPath}\n";
            }

            // ── AnimatorController + Prefab ────────────────────────────────────────
            var ctrl = BuildAnimatorController(name, dir, allClips);
            if (fbxExported && ctrl != null)
                BuildPrefab(name, dir, fbxAssetPath, ctrl);

            _status += $"Done.\n  FBX:   {(fbxExported ? fbxAssetPath : "(failed)")}\n  Clips: {clipContainerPath}\n  Mat:   {matAssetPath}\n  Renderers: {allRendererPaths.Count}\n";
        }

        // =========================================================================
        // Build AnimationClip from AnimData
        // Keyframe values are ADDITIVE DELTAS from the posture (rest) values.
        // DW1 runs at 20fps (vsync(3)). Each raw duration unit = 1 game tick = 1/20s.
        // Each bone has its own independent timeline from its own keyframes only.
        // =========================================================================
        AnimationClip BuildAnimClip(AnimData anim, string[] bonePaths, string digiName, int animIdx, int numNodes,
                                     List<string> allRendererPaths, Material baseMat,
                                     List<(float time, Material exprMat)> exprKeys)
        {
            if (anim == null || anim.sequences == null) return null;

            // Detect whether this animation contains an infinite loop (repetitions >= 8 → treat as
            // looping forever). Infinite-loop animations (Idle, Walk, Run, …) should be built as a
            // single pass of the loop body with WrapMode.Loop, not unrolled many times.
            bool isLooping = anim.sequences.OfType<LoopStartSeq>().Any(l => l.repetitions >= 8);
            int maxLoopReps = isLooping ? 1 : 4; // single pass for infinite; unroll up to 4× for finite

            var flatKFs = new List<AnimKeyframe>();
            FlattenKeyframes(anim.sequences, flatKFs, 0, maxLoopReps);
            if (flatKFs.Count == 0) return null;

            int n = numNodes;
            PostureData[] pose = anim.pose ?? new PostureData[0];

            // Per-bone: independent timeline + running accumulated transform values
            var boneTime = new float[n];
            var curPx = new float[n]; var curPy = new float[n]; var curPz = new float[n];
            var curRx = new float[n]; var curRy = new float[n]; var curRz = new float[n];
            var curSx = new float[n]; var curSy = new float[n]; var curSz = new float[n];
            var boneHasKeys = new bool[n];

            var pxC = new AnimationCurve[n]; var pyC = new AnimationCurve[n]; var pzC = new AnimationCurve[n];
            // Quaternion component curves — avoids Unity re-interpreting Euler angles in ZXY order.
            var qxC = new AnimationCurve[n]; var qyC = new AnimationCurve[n];
            var qzC = new AnimationCurve[n]; var qwC = new AnimationCurve[n];
            var sxC = new AnimationCurve[n]; var syC = new AnimationCurve[n]; var szC = new AnimationCurve[n];

            for (int i = 0; i < n; i++)
            {
                pxC[i] = new AnimationCurve(); pyC[i] = new AnimationCurve(); pzC[i] = new AnimationCurve();
                qxC[i] = new AnimationCurve(); qyC[i] = new AnimationCurve();
                qzC[i] = new AnimationCurve(); qwC[i] = new AnimationCurve();
                sxC[i] = new AnimationCurve(); syC[i] = new AnimationCurve(); szC[i] = new AnimationCurve();

                // Initialize running values from posture (node 0 = root, no posture entry)
                PostureData pd = (i > 0 && i - 1 < pose.Length) ? pose[i - 1] : default;
                curRx[i] = pd.rotX * BAM_TO_DEG;
                curRy[i] = -pd.rotY * BAM_TO_DEG;
                curRz[i] = -pd.rotZ * BAM_TO_DEG;
                curPx[i] = pd.posX * PS1_POS_SCALE;
                curPy[i] = -pd.posY * PS1_POS_SCALE;
                curPz[i] = -pd.posZ * PS1_POS_SCALE;
                curSx[i] = pd.hasScale ? pd.scaleX * PS1_SCALE_FACTOR : 1f;
                curSy[i] = pd.hasScale ? pd.scaleY * PS1_SCALE_FACTOR : 1f;
                curSz[i] = pd.hasScale ? pd.scaleZ * PS1_SCALE_FACTOR : 1f;
            }

            // Walk keyframes: each kf belongs to one bone (kf.nodeIndex).
            // Duration is in ms → seconds = duration / 1000f.
            // Values are deltas — accumulate onto running values.
            foreach (var kf in flatKFs)
            {
                int ni = Mathf.Clamp(kf.nodeIndex, 0, n - 1);
                if (ni == 0) continue; // root node is not animated

                // Add initial posture key on first encounter of this bone
                if (!boneHasKeys[ni])
                {
                    boneHasKeys[ni] = true;
                    pxC[ni].AddKey(0f, curPx[ni]); pyC[ni].AddKey(0f, curPy[ni]); pzC[ni].AddKey(0f, curPz[ni]);
                    var q0 = Quaternion.AngleAxis(curRx[ni], Vector3.right) *
                             Quaternion.AngleAxis(curRy[ni], Vector3.up) *
                             Quaternion.AngleAxis(curRz[ni], Vector3.forward);
                    qxC[ni].AddKey(0f, q0.x); qyC[ni].AddKey(0f, q0.y);
                    qzC[ni].AddKey(0f, q0.z); qwC[ni].AddKey(0f, q0.w);
                    sxC[ni].AddKey(0f, curSx[ni]); syC[ni].AddKey(0f, curSy[ni]); szC[ni].AddKey(0f, curSz[ni]);
                }

                // 1 tick = 1/20s (20fps confirmed from DW1 engine)
                boneTime[ni] += kf.duration / 20f;
                float t = boneTime[ni];

                // Accumulate deltas (only for present channels; absent = delta 0 = no change)
                if (kf.hasRotX)   curRx[ni] += kf.rotX  * BAM_TO_DEG;
                if (kf.hasRotY)   curRy[ni] -= kf.rotY  * BAM_TO_DEG;  // negated (Y-flip)
                if (kf.hasRotZ)   curRz[ni] -= kf.rotZ  * BAM_TO_DEG;  // negated (Z-flip)
                if (kf.hasPosX)   curPx[ni] += kf.posX  * PS1_POS_SCALE;
                if (kf.hasPosY)   curPy[ni] -= kf.posY  * PS1_POS_SCALE;  // negated (Y-flip)
                if (kf.hasPosZ)   curPz[ni] -= kf.posZ  * PS1_POS_SCALE;  // negated (Z-flip)
                if (kf.hasScaleX) curSx[ni] += kf.scaleX * PS1_SCALE_FACTOR;
                if (kf.hasScaleY) curSy[ni] += kf.scaleY * PS1_SCALE_FACTOR;
                if (kf.hasScaleZ) curSz[ni] += kf.scaleZ * PS1_SCALE_FACTOR;

                // Add key for all channels at this time (keeps interpolation aligned)
                pxC[ni].AddKey(t, curPx[ni]); pyC[ni].AddKey(t, curPy[ni]); pzC[ni].AddKey(t, curPz[ni]);
                var q = Quaternion.AngleAxis(curRx[ni], Vector3.right) *
                        Quaternion.AngleAxis(curRy[ni], Vector3.up) *
                        Quaternion.AngleAxis(curRz[ni], Vector3.forward);
                // Ensure shortest-path interpolation (avoid sign flip between keys)
                if (qwC[ni].length > 0)
                {
                    float prevW = qwC[ni][qwC[ni].length - 1].value;
                    if (prevW * q.w + qxC[ni][qxC[ni].length - 1].value * q.x +
                        qyC[ni][qyC[ni].length - 1].value * q.y +
                        qzC[ni][qzC[ni].length - 1].value * q.z < 0f)
                        q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                }
                qxC[ni].AddKey(t, q.x); qyC[ni].AddKey(t, q.y);
                qzC[ni].AddKey(t, q.z); qwC[ni].AddKey(t, q.w);
                sxC[ni].AddKey(t, curSx[ni]); syC[ni].AddKey(t, curSy[ni]); szC[ni].AddKey(t, curSz[ni]);
            }

            // Clip total duration = longest bone timeline
            float totalTime = 0f;
            for (int i = 1; i < n; i++)
                totalTime = Mathf.Max(totalTime, boneTime[i]);
            if (totalTime < 0.001f) return null;

            var clip = new AnimationClip();
            string animLabel = (animIdx < ANIM_NAMES.Length) ? ANIM_NAMES[animIdx] : ("anim" + animIdx);
            clip.name = digiName + "_" + animLabel;
            clip.frameRate = 60f;

            for (int i = 1; i < n; i++) // skip root (i=0)
            {
                if (!boneHasKeys[i]) continue;
                string bonePath = (bonePaths != null && i < bonePaths.Length) ? bonePaths[i] : "Bone_" + i;

                // Match Three.js NumberKeyframeTrack: linear interpolation between keyframes.
                MakeLinear(pxC[i]); MakeLinear(pyC[i]); MakeLinear(pzC[i]);
                MakeLinear(qxC[i]); MakeLinear(qyC[i]); MakeLinear(qzC[i]); MakeLinear(qwC[i]);
                MakeLinear(sxC[i]); MakeLinear(syC[i]); MakeLinear(szC[i]);

                clip.SetCurve(bonePath, typeof(Transform), "localPosition.x", pxC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localPosition.y", pyC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localPosition.z", pzC[i]);
                // Quaternion curves: Unity reads these directly, bypassing ZXY Euler reinterpretation.
                clip.SetCurve(bonePath, typeof(Transform), "localRotation.x", qxC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localRotation.y", qyC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localRotation.z", qzC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localRotation.w", qwC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localScale.x", sxC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localScale.y", syC[i]);
                clip.SetCurve(bonePath, typeof(Transform), "localScale.z", szC[i]);
            }

            AnimationUtility.SetAnimationClipSettings(clip, new AnimationClipSettings
            {
                loopTime = isLooping,  // only loop animations that have an infinite loop in the data
                stopTime = totalTime,
            });

            // ── Texture animation: material swap on ALL renderers (pixel blit approach) ──
            // Each exprKey holds a pre-baked expression material. We add ObjectReferenceKeyframe
            // curves on all renderer paths so the entire digimon texture updates at once,
            // matching the reference Three.js viewer which replaces mesh.material on every mesh.
            // Both MeshRenderer and SkinnedMeshRenderer bindings are added: Unity FBX Exporter
            // may produce either type for static-mesh children of bones depending on Unity version.
            if (baseMat != null && exprKeys != null && exprKeys.Count > 0
                && allRendererPaths != null && allRendererPaths.Count > 0)
            {
                var keyframes = new List<ObjectReferenceKeyframe>();
                // t=0 → base material (resets the material when the animation restarts)
                keyframes.Add(new ObjectReferenceKeyframe { time = 0f, value = baseMat });
                foreach (var (t, exprMat) in exprKeys)
                {
                    if (exprMat != null)
                        keyframes.Add(new ObjectReferenceKeyframe { time = t, value = exprMat });
                }
                // Only add curves when there is at least one expression mat beyond the reset key
                if (keyframes.Count > 1)
                {
                    var kfsArray = keyframes.ToArray();
                    foreach (string rpath in allRendererPaths)
                    {
                        foreach (var rendType in new System.Type[] { typeof(MeshRenderer), typeof(SkinnedMeshRenderer) })
                        {
                            var binding = new EditorCurveBinding
                            {
                                path         = rpath,
                                type         = rendType,
                                propertyName = "m_Materials.Array.data[0]"
                            };
                            AnimationUtility.SetObjectReferenceCurve(clip, binding, kfsArray);
                        }
                    }
                }
            }

            // ── Sound cues: AnimationEvent markers ────────────────────────────────
            // PS1 opcode 0x4000 encodes a sound + vabId at a specific sequence index.
            // seqIndex = animFrame (1-based game tick at 20 fps), so t = seqIndex / 20f.
            // VLALL bank (vabId=3) notes 49-52 map to PlayHappy/Angry/Hurt/Die.
            // SB bank (vabId=8) notes 60-75 map to PlayFootstep.
            // The eating animation (anim index 8) has no VLALL/SB sound opcodes.
            // A synthetic PlayEat event is injected below at frame 11 (food removal frame).
            if (anim.sequences != null)
            {
                var soundSeqs = new List<SoundSeq>();
                CollectSoundSeqs(anim.sequences, soundSeqs);
                if (soundSeqs.Count > 0)
                {
                    var events = new List<AnimationEvent>();
                    foreach (var ss in soundSeqs)
                    {
                        float t = ss.sequenceIndex / 20f;  // sequenceIndex = animFrame (1-based game tick), 20fps
                        if (t > totalTime + 0.001f) continue;
                        t = Mathf.Min(t, totalTime);

                        AudioClip soundClip = TryLoadSoundClip(ss);
                        Debug.Log($"[DigimonImport] {clip.name}  frame={ss.sequenceIndex}  t={t:F3}s" +
                                  $"  soundId={ss.soundId}  vabId={ss.vabId}" +
                                  $"  clip={(soundClip != null ? soundClip.name : "NOT FOUND")}");

                        string evtName = SoundIdToEventName(ss.soundId, ss.vabId);
                        if (evtName == null) continue; // unknown vabId/soundId — skip event

                        var evt = new AnimationEvent();
                        evt.time            = t;
                        evt.messageOptions  = SendMessageOptions.DontRequireReceiver;
                        // VRChat UnityEventFilter only allows "SendCustomEvent" as a custom
                        // AnimationEvent function name.  stringParameter = semantic method name
                        // on DigimonSoundPlayer (e.g. "PlayAngry", "PlayFootstep").
                        evt.functionName    = "SendCustomEvent";
                        evt.stringParameter = evtName;
                        events.Add(evt);
                    }
                    if (events.Count > 0)
                        AnimationUtility.SetAnimationEvents(clip, events.ToArray());
                }
            }

            // ── Eating animation: synthetic sound event ───────────────────────────
            // tickFeedItem() case 5 (Partner.cpp) removes the food item at animFrame==11,
            // marking the bite moment. No VLALL/SB opcode fires in the eating clip, so we
            // inject PlayEat manually. Guard against any future opcode data at that time.
            if (animLabel == "eating")
            {
                float eatTime = Mathf.Min(11f / 20f, totalTime);
                bool alreadyHasEat = false;
                foreach (var e in AnimationUtility.GetAnimationEvents(clip))
                    if (Mathf.Abs(e.time - eatTime) < 0.05f) { alreadyHasEat = true; break; }
                if (!alreadyHasEat)
                {
                    var eatEvt = new AnimationEvent
                    {
                        time             = eatTime,
                        messageOptions   = SendMessageOptions.DontRequireReceiver,
                        functionName     = "SendCustomEvent",
                        stringParameter  = "PlayEat",
                    };
                    var merged = new List<AnimationEvent>(AnimationUtility.GetAnimationEvents(clip)) { eatEvt };
                    AnimationUtility.SetAnimationEvents(clip, merged.ToArray());
                }
            }

            return clip;
        }

        // Maps a DW1 (soundId, vabId) pair to the SendCustomEvent name on DigimonSoundPlayer.
        // vabId selects the sound bank so the same MIDI note in different banks doesn't collide.
        // Returns null for unrecognised combinations — the importer will skip baking those events.
        //
        // NOTE: DW1 attack sounds (VBALL, vabId=4-7) were triggered by battle engine code
        // (BTL_REL.BIN), NOT by animation opcodes. Attack animation clips contain no opcode=4
        // sound events. The "PlayAttack" case here handles any future VBALL events in data.
        static string SoundIdToEventName(int id, int vabId)
        {
            switch (vabId)
            {
                case 3: // VLALL — per-digimon partner voice sounds
                    switch (id)
                    {
                        case 48: return "PlayIdle";
                        case 49: return "PlayHappy";
                        case 50: return "PlayAngry";
                        case 51: return "PlayHurt";
                        case 52: return "PlayDie";
                    }
                    return null;

                case 4: case 5: case 6: case 7: // VBALL — per-digimon battle SFX
                    // All VBALL notes map to PlayAttack; the first available clip plays.
                    return "PlayAttack";

                case 8: // SB / ESALL — footstep and environment SFX
                    // Footstep variants 60-75; terrain selection is runtime logic.
                    if (id >= 60 && id <= 75) return "PlayFootstep";
                    return null;

                default:
                    return null;
            }
        }

        // Resolve a SoundSeq to an AudioClip from the extracted sounds directory.
        // Uses vabId to pick the correct bank file prefix.
        //   vabId=3 (VLALL): VLALL_*_sndNNN.wav — scanned by wildcard (bank index not in importer)
        //   vabId=4-7 (VBALL): VBALL_bankNN_sndNNN.wav — requires _vballBankIndex
        //   vabId=8 (SB/ESALL): SB_bank00_sndNNN.wav
        // Used only for the debug log; clip loading at runtime is done by the Sound Assigner.
        AudioClip TryLoadSoundClip(SoundSeq ss)
        {
            if (string.IsNullOrEmpty(_soundsDir)) return null;
            string dir = _soundsDir.TrimEnd('/');

            if (ss.vabId >= 4 && ss.vabId <= 7)
            {
                if (_vballBankIndex < 0) return null;
                return AssetDatabase.LoadAssetAtPath<AudioClip>(
                    $"{dir}/VBALL_bank{_vballBankIndex:D2}_snd{ss.soundId:D3}.wav");
            }
            if (ss.vabId == 8)
            {
                return AssetDatabase.LoadAssetAtPath<AudioClip>(
                    $"{dir}/SB_bank00_snd{ss.soundId:D3}.wav");
            }
            if (ss.vabId == 3)
            {
                // VLALL bank index is not stored in the importer; scan for any matching file.
                var guids = AssetDatabase.FindAssets($"VLALL t:AudioClip", new[] { _soundsDir });
                string suffix = $"_snd{ss.soundId:D3}.wav";
                foreach (var g in guids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                        return AssetDatabase.LoadAssetAtPath<AudioClip>(p);
                }
                return null;
            }
            return null;
        }

        // =========================================================================
        // Collect TextureSeqs from an animation's sequences (no loop-unrolling).
        // =========================================================================
        static void CollectTextureSeqs(List<object> seqs, List<TextureSeq> result)
        {
            foreach (var s in seqs)
                if (s is TextureSeq ts) result.Add(ts);
        }

        static void CollectSoundSeqs(List<object> seqs, List<SoundSeq> result)
        {
            foreach (var s in seqs)
                if (s is SoundSeq ss) result.Add(ss);
        }

        // Flatten all keyframes from a sequence list (loop-unrolled), in play order.
        // maxReps: maximum number of times to play a loop body.
        // Pass 1 for infinite-loop animations (builds a single loopable pass).
        void FlattenKeyframes(List<object> seqs, List<AnimKeyframe> result, int depth, int maxReps = 1)
        {
            if (depth > 6) return;

            int loopStart = -1;
            int loopReps  = 0;

            for (int si = 0; si < seqs.Count; si++)
            {
                var s = seqs[si];

                if (s is AxisSeq axis)
                {
                    foreach (var kf in axis.keyframes)
                        result.Add(kf);
                }
                else if (s is LoopStartSeq ls)
                {
                    loopStart = si + 1;
                    // Clamp to maxReps: infinite loops (repetitions>=8) use maxReps=1 so only 1 pass is built.
                    loopReps  = Mathf.Clamp(ls.repetitions, 1, maxReps);
                }
                else if (s is LoopEndSeq && loopStart >= 0)
                {
                    var loopBlock = seqs.GetRange(loopStart, si - loopStart);
                    // The block already ran once during the main forward scan above;
                    // run (loopReps-1) additional times.
                    for (int r = 0; r < loopReps - 1; r++)
                        FlattenKeyframes(loopBlock, result, depth + 1, maxReps);
                    loopStart = -1;
                }
                // TextureSeq / SoundSeq: skip for transform animation
            }
        }



        // =========================================================================
        // Set all keyframe tangents to Linear — matches Three.js NumberKeyframeTrack default.
        // =========================================================================
        static void MakeLinear(AnimationCurve c)
        {
            for (int k = 0; k < c.length; k++)
            {
                AnimationUtility.SetKeyLeftTangentMode(c,  k, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(c, k, AnimationUtility.TangentMode.Linear);
            }
        }

        // =========================================================================
        // Little-endian read helpers
        // =========================================================================
        static uint RL32(byte[] b, long o)
        {
            if (o + 4 > b.Length) return 0;
            return (uint)(b[o] | (b[o+1] << 8) | (b[o+2] << 16) | (b[o+3] << 24));
        }

        static ushort RL16(byte[] b, long o)
        {
            if (o + 2 > b.Length) return 0;
            return (ushort)(b[o] | (b[o+1] << 8));
        }

        static short RL16S(byte[] b, long o)
        {
            if (o + 2 > b.Length) return 0;
            return (short)(b[o] | (b[o+1] << 8));
        }

        // Returns uint16 treated as array index (clamped positive)
        static int RL16S_idx(byte[] b, long o)
        {
            return RL16(b, o);  // indices in TMD are unsigned uint16
        }

        // ─────────────────────────────────────────────────────────────────────
        // BuildAnimatorController
        // Dynamically generates a controller for any Digimon:
        //   • Non-attack states are always created (clip assigned if present)
        //   • Attack states are created only for attack clips that actually exist
        //   • Locomotion uses IsWalking / IsRunning Bool params (matches original
        //     game's 3-zone STOP/WALK/SPRINT distance-bucket system)
        // ─────────────────────────────────────────────────────────────────────
        AnimatorController BuildAnimatorController(string name, string dir, List<AnimationClip> allClips)
        {
            string ctrlPath = dir + "/" + name + "_anim_controller.controller";
            AssetDatabase.DeleteAsset(ctrlPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var sm = ctrl.layers[0].stateMachine;

            // ── Clip lookup: label (everything after first '_') → clip ────────
            var clipByState = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in allClips)
            {
                if (clip == null) continue;
                int us = clip.name.IndexOf('_');
                string label = us >= 0 ? clip.name.Substring(us + 1) : clip.name;
                if (s_AnimNameToState.TryGetValue(label, out var sn) && !clipByState.ContainsKey(sn))
                    clipByState[sn] = clip;
            }

            // ── Detect which attack clips this Digimon actually has ───────────
            // Collect Attack0..Attack14 in order, keeping only those with clips.
            var attackStates = new List<string>();
            for (int ai = 0; ai <= 14; ai++)
            {
                string sn = "Attack" + ai;
                if (clipByState.ContainsKey(sn))
                    attackStates.Add(sn);
            }
            // Guarantee at least one attack state even if no clip exists
            if (attackStates.Count == 0) attackStates.Add("Attack0");

            // ── Parameters ────────────────────────────────────────────────────
            ctrl.AddParameter("IsWalking", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("IsRunning",  AnimatorControllerParameterType.Bool);
            // Attack triggers: one per detected attack state
            foreach (var aSn in attackStates)
                ctrl.AddParameter(aSn, AnimatorControllerParameterType.Trigger);
            // Other action triggers
            foreach (var t in new[] { "Hurt","Faint","Eat","Joyful","Finicky","Poop","Win","Evolve","Angry" })
                ctrl.AddParameter(t, AnimatorControllerParameterType.Trigger);
            // Bool parameters
            foreach (var b in new[] {
                "Sleep","Tired","HasNeedFood","HasNeedSleep","HasNeedEvolve",
                "HasNeedTired","HasNeedBandage","HasNeedMedicine","HasNeedPoop" })
                ctrl.AddParameter(b, AnimatorControllerParameterType.Bool);

            // ── Build states ──────────────────────────────────────────────────
            // Column 0: locomotion + needs (x=250)
            // Column 1: attacks (x=600)
            // Column 2: reactions / misc (x=950)
            var st = new Dictionary<string, AnimatorState>();
            AnimatorState MakeState(string sn, float x, float y)
            {
                var s = sm.AddState(sn, new Vector3(x, y, 0f));
                s.writeDefaultValues = true;
                if (clipByState.TryGetValue(sn, out var mc)) s.motion = mc;
                st[sn] = s;
                return s;
            }

            // Locomotion + needs column
            var locoNames = new[] {
                "Idle","Walk","Run","Stumbling","Dizzy","Hungry",
                "Toilet","Evolve","Tired","NeedBandageIdle","NeedMedicineIdle"
            };
            for (int i = 0; i < locoNames.Length; i++)
                MakeState(locoNames[i], 250f, i * 75f);

            // Attack column — only the states this Digimon has clips for
            for (int i = 0; i < attackStates.Count; i++)
                MakeState(attackStates[i], 600f, i * 75f);

            // Reactions column
            var reactionNames = new[] { "Hurt","Faint","Eat","Joyful","Finicky","Sleep","Poop","Win","Angry" };
            for (int i = 0; i < reactionNames.Length; i++)
                MakeState(reactionNames[i], 950f, i * 75f);

            sm.defaultState = st["Idle"];

            // ── Local helpers ─────────────────────────────────────────────────
            AnimatorStateTransition Tr(AnimatorState from, AnimatorState to,
                float dur = 0.15f, bool exitTime = false, float exitT = 1f)
            {
                var tr = from.AddTransition(to);
                tr.hasExitTime = exitTime; tr.exitTime = exitT;
                tr.duration = dur; tr.canTransitionToSelf = false;
                return tr;
            }
            AnimatorStateTransition AnyTr(AnimatorState to, float dur = 0f)
            {
                var tr = sm.AddAnyStateTransition(to);
                tr.hasExitTime = false; tr.duration = dur; tr.canTransitionToSelf = false;
                return tr;
            }

            // ── Locomotion transitions ────────────────────────────────────────
            // Matches the original game's 3-zone distance-bucket system:
            //   STOP  : IsWalking == false, IsRunning == false  →  Idle / TiredIdle
            //   WALK  : IsWalking == true,  IsRunning == false  →  Walk / Stumbling
            //   SPRINT: IsWalking == true,  IsRunning == true   →  Run

            { var t=Tr(st["Idle"],st["Stumbling"]); t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); t.AddCondition(AnimatorConditionMode.If,0f,"Tired"); }
            { var t=Tr(st["Idle"],st["Walk"]);      t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); }
            { var t=Tr(st["Idle"],st["Dizzy"]);     t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedSleep"); }
            { var t=Tr(st["Idle"],st["Hungry"]);    t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedFood"); }
            { var t=Tr(st["Idle"],st["Evolve"]);    t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedEvolve"); }
            { var t=Tr(st["Idle"],st["Tired"]);     t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedTired"); }
            { var t=Tr(st["Idle"],st["NeedBandageIdle"]); t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedBandage"); }
            { var t=Tr(st["Idle"],st["Toilet"]);    t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedPoop"); }
            { var t=Tr(st["Idle"],st["NeedMedicineIdle"]); t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedMedicine"); }

            { var t=Tr(st["Walk"],st["Run"]);       t.AddCondition(AnimatorConditionMode.If,0f,"IsRunning"); }
            { var t=Tr(st["Walk"],st["Stumbling"]); t.AddCondition(AnimatorConditionMode.If,0f,"Tired"); t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); }
            { var t=Tr(st["Walk"],st["Idle"]);      t.AddCondition(AnimatorConditionMode.IfNot,0f,"IsWalking"); }

            { var t=Tr(st["Run"],st["Walk"]); t.AddCondition(AnimatorConditionMode.IfNot,0f,"IsRunning"); t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); }
            { var t=Tr(st["Run"],st["Idle"]); t.AddCondition(AnimatorConditionMode.IfNot,0f,"IsWalking"); }

            // Stumbling = tired walk. Exits:
            //   → Run      : IsRunning (urgency sprint overrides tiredness)
            //   → Walk     : IfNot Tired  +  IsWalking  (recovered while walking)
            //   → Tired    : IfNot IsWalking  +  HasNeedTired  (stopped; return to tired-need idle)
            //   → Idle     : IfNot IsWalking  (stopped, no active need)
            { var t=Tr(st["Stumbling"],st["Run"]);   t.AddCondition(AnimatorConditionMode.If,0f,"IsRunning"); }
            { var t=Tr(st["Stumbling"],st["Walk"]);  t.AddCondition(AnimatorConditionMode.IfNot,0f,"Tired"); t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); }
            { var t=Tr(st["Stumbling"],st["Tired"]); t.AddCondition(AnimatorConditionMode.IfNot,0f,"IsWalking"); t.AddCondition(AnimatorConditionMode.If,0f,"HasNeedTired"); }
            { var t=Tr(st["Stumbling"],st["Idle"]);  t.AddCondition(AnimatorConditionMode.IfNot,0f,"IsWalking"); }

            // ── Need-state exits → Idle ───────────────────────────────────────
            { var t=Tr(st["Dizzy"],st["Idle"]);           t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedSleep"); }
            { var t=Tr(st["Hungry"],st["Idle"]);          t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedFood"); }
            { var t=Tr(st["Toilet"],st["Idle"]);          t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedPoop"); }
            { var t=Tr(st["Evolve"],st["Idle"]);          t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedEvolve"); }
            // Tired idle (need=5): walking exits must come before the Idle exit so they take priority.
            { var t=Tr(st["Tired"],st["Stumbling"]); t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); t.AddCondition(AnimatorConditionMode.If,0f,"Tired"); }
            { var t=Tr(st["Tired"],st["Walk"]);      t.AddCondition(AnimatorConditionMode.If,0f,"IsWalking"); }
            { var t=Tr(st["Tired"],st["Idle"]);      t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedTired"); }
            { var t=Tr(st["NeedBandageIdle"],st["Idle"]); t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedBandage"); }
            { var t=Tr(st["NeedMedicineIdle"],st["Idle"]); t.AddCondition(AnimatorConditionMode.IfNot,0f,"HasNeedMedicine"); }

            // ── Sleep (Bool AnyState) ─────────────────────────────────────────
            { var t=AnyTr(st["Sleep"],0.15f); t.AddCondition(AnimatorConditionMode.If,0f,"Sleep"); t.canTransitionToSelf=true; }
            { var t=Tr(st["Sleep"],st["Idle"]); t.AddCondition(AnimatorConditionMode.IfNot,0f,"Sleep"); }

            // ── AnyState → attack states (one trigger per attack) ─────────────
            foreach (var aSn in attackStates)
            {
                var t = AnyTr(st[aSn]);
                t.AddCondition(AnimatorConditionMode.If, 0f, aSn);
            }

            // ── AnyState → reaction states ────────────────────────────────────
            foreach (var kv in new (string param, string sn)[] {
                ("Hurt","Hurt"),("Faint","Faint"),("Eat","Eat"),("Joyful","Joyful"),
                ("Finicky","Finicky"),("Poop","Poop"),("Win","Win"),("Evolve","Evolve"),("Angry","Angry") })
            {
                var t = AnyTr(st[kv.sn]);
                t.AddCondition(AnimatorConditionMode.If, 0f, kv.param);
            }

            // ── Exit back to Idle after clip finishes ─────────────────────────
            // Faint is terminal; Evolve exits via condition above.
            foreach (var aSn in attackStates)
                Tr(st[aSn], st["Idle"], 0.15f, true, 1f);
            foreach (var sn in new[] { "Hurt","Eat","Joyful","Finicky","Poop","Win","Angry" })
                Tr(st[sn], st["Idle"], 0.15f, true, 1f);

            AssetDatabase.SaveAssets();
            _status += $"  Controller: {ctrlPath} ({attackStates.Count} attack state(s))\n";
            return ctrl;
        }

        // ─────────────────────────────────────────────────────────────────────
        // BuildPrefab
        // Instantiates the exported FBX, attaches the AnimatorController,
        // and saves the result as {name}.prefab.
        // ─────────────────────────────────────────────────────────────────────
        void BuildPrefab(string name, string dir, string fbxAssetPath, AnimatorController controller)
        {
            string prefabPath = dir + "/" + name + ".prefab";
            AssetDatabase.DeleteAsset(prefabPath);

            var fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (fbxPrefab == null)
            {
                _status += "  WARNING: FBX not found — prefab skipped.\n";
                return;
            }

            var instance = UnityEngine.Object.Instantiate(fbxPrefab);
            instance.name = name;
            // Use Unity's == null (not C# ??) to properly detect fake-null UnityEngine.Objects.
            Animator animator = instance.GetComponent<Animator>();
            if (animator == null) animator = instance.AddComponent<Animator>();
            if (animator == null)
            {
                _status += "  WARNING: Could not attach Animator — prefab saved without controller.\n";
            }
            else
            {
                animator.runtimeAnimatorController = controller;
            }

            // Add AudioSource required by DigimonSoundPlayer.
            if (instance.GetComponent<AudioSource>() == null)
            {
                var src = instance.AddComponent<AudioSource>();
                src.playOnAwake  = false;
                src.spatialBlend = 1f;
            }

           

            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            UnityEngine.Object.DestroyImmediate(instance);
            AssetDatabase.ImportAsset(prefabPath);
            _status += $"  Prefab:     {prefabPath}\n";
        }

    }
}
