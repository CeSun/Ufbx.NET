// S3bcCheck: isolated differential verification for the S3b (scene finalize mid-half) + S3c
// (ufbxi_finalize_scene / ufbxi_update_* chain) modules.
//
// What it proves
// --------------
// tools/s3bc_oracle.c (#includes the frozen ufbx.c) loads real FBX files with the golden opts
// of test/hash_scene.c load_scene() -- load_external_files, evaluate_caches, evaluate_skinning,
// target_axes=right_handed_y_up, target_unit_meters=1 -- which drives the FULL C pipeline:
// ufbxi_finalize_scene (fetch_maps / finalize_shader_texture / deduplicate_textures /
// fetch_file_textures / texture_file_map / modify_geometry / ...) and ufbxi_update_scene
// (update_node / update_camera / update_initial_clusters / update_adjust_transforms / ...).
// The C# side replays the same files through `UfbxApi.LoadFile` (whose load spine now runs the
// ported Parse/SceneFinalize.cs + Parse/SceneUpdate.cs) and compares every record:
//
//   S/M/P/E  scene counts, material->textures order, all fbx+pbr material maps and features
//            (ufbxi_fetch_maps + the whole shader-mapping table + ufbxi_update_factor)
//   T        scene.texture_files (ufbxi_insert_texture_file/ufbxi_pop_texture_files order)
//   N        scene.nodes order / parentage / depth (ufbxi_linearize_nodes + update_scene)
//   X/W/LT   node_to_parent / node_to_world / local_transform bit patterns (ufbxi_update_node,
//            ufbxi_get_transform, ufbxi_update_adjust_transforms, axis/unit conversion)
//   V        mesh vertex positions after ufbxi_modify_geometry (mirror / scale / geometry
//            transforms / flip winding), full-sequence FNV + first vertices as bits
//
// Usage: cd C:/Workspace/_analyze_ufbx && dotnet run --project C:/Workspace/ufbx-cs/tools/S3bcCheck
//        -c Release -- C:/Workspace/ufbx-cs/tools/s3bc_oracle.txt
// The file list below must match the oracle invocation (or pass it as extra args).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx;

namespace S3bcCheck
{
    static class Program
    {
        // The corpus of tools/s3bc_oracle.txt, in oracle order (fi = index).
        static readonly string[] DefaultFiles = new string[] {
            "data/maya_slime_7500_binary.fbx",
            "data/blender_279_default_7400_binary.fbx",
            "data/blender_279_default_6100_ascii.fbx",
            "data/blender_279_bone_radius_7400_binary.fbx",
            "data/blender440_shape_weight_anim_7400_binary.fbx",
            "data/max_texture_mapping_7700_ascii.fbx",
            "data/max_openpbr_material_7700_ascii.fbx",
            "data/maya_constraint_zoo_7500_ascii.fbx",
            "data/blender_279_color_sets_6100_ascii.fbx",
            "data/blender_279_edge_circle_6100_ascii.fbx",
            "data/max_physical_material_inverted_7500_ascii.fbx",
            "data/max7_skin_5000_binary.fbx",
            "data/blender_293_half_skinned_7400_binary.fbx",
            "data/maya_advanced_skinned_pivot_7700_ascii.fbx",
            "data/maya_absolute_texture_7700_ascii.fbx",
        };

        static int fails;
        static int passes;
        static readonly List<string> failLines = new List<string>();

        static UfbxScene scene;
        static int sceneFi = -1;

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : "tools/s3bc_oracle.txt";
            List<string> files = new List<string>(DefaultFiles);
            if (args.Length > 1) {
                files.Clear();
                for (int i = 1; i < args.Length; i++) files.Add(args[i]);
            }
            if (!File.Exists(oraclePath)) {
                Console.Error.WriteLine("oracle not found: " + oraclePath);
                return 2;
            }

            int lineNo = 0;
            foreach (string raw in File.ReadLines(oraclePath)) {
                lineNo++;
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;
                string[] t = line.Split(' ');
                string rec = t[0];
                int fi = int.Parse(t[1]);

                bool ok;
                try {
                    if (fi != sceneFi) {
                        LoadScene(files, fi);
                    }
                    switch (rec) {
                        case "S": ok = DoS(t); break;
                        case "M": ok = DoM(t); break;
                        case "P": ok = DoP(t); break;
                        case "E": ok = DoE(t); break;
                        case "T": ok = DoT(t); break;
                        case "N": ok = DoN(t); break;
                        case "X": ok = DoMatrix(t, scene.Nodes[int.Parse(t[2])].NodeToParent); break;
                        case "W": ok = DoMatrix(t, scene.Nodes[int.Parse(t[2])].NodeToWorld); break;
                        case "LT": ok = DoLt(t); break;
                        case "V": ok = DoV(t); break;
                        default: Console.Error.WriteLine("unknown record " + rec + " at line " + lineNo); return 2;
                    }
                } catch (Exception e) {
                    Console.Error.WriteLine("EXCEPTION on line " + lineNo + " (" + rec + "): " + e.Message);
                    return 2;
                }

                if (ok) passes++;
                else {
                    fails++;
                    if (failLines.Count < 30) failLines.Add(lineNo + ": " + line);
                }
            }

            Console.WriteLine("S3BC CHECK " + (fails == 0 ? "PASS" : "FAIL"));
            Console.WriteLine("records checked: " + (passes + fails));
            Console.WriteLine("pass:            " + passes);
            Console.WriteLine("fail:            " + fails);
            foreach (string f in failLines) Console.WriteLine("  DIVERGE " + f);
            return fails == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------
        // Scene loading (golden opts of test/hash_scene.c load_scene())
        // ------------------------------------------------------------------

        static void LoadScene(List<string> files, int fi)
        {
            sceneFi = fi;
            scene = null;
            if (fi >= files.Count) return;

            UfbxLoadOpts opts = new UfbxLoadOpts();
            opts.LoadExternalFiles = true;
            opts.IgnoreMissingExternalFiles = true;
            opts.EvaluateCaches = true;
            opts.EvaluateSkinning = true;
            opts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
            opts.TargetUnitMeters = 1.0;

            scene = UfbxApi.LoadFile(files[fi], opts, new UfbxError());
        }

        // The oracle never emits records for a failed file beyond the `S <fi> 0 <type>`
        // record; guard the field accessors.

        // ------------------------------------------------------------------
        // Records
        // ------------------------------------------------------------------

        static bool DoS(string[] t)
        {
            // S <fi> <ok> <elements> <nodes> <meshes> <materials> <textures>
            //   <texture_files> <num_shader_textures>
            if (t[2] == "0") {
                // C failed to load: the port must fail too (error type compared loosely here;
                // the byte-exact error contracts are LoadCheck's domain).
                return scene == null;
            }
            return scene != null
                && Eq(t[3], scene.Elements.Length)
                && Eq(t[4], scene.Nodes.Length)
                && Eq(t[5], scene.Meshes.Length)
                && Eq(t[6], scene.Materials.Length)
                && Eq(t[7], scene.Textures.Length)
                && Eq(t[8], scene.TextureFiles.Length)
                && Eq(t[9], scene.Metadata.NumShaderTextures);
        }

        static bool DoM(string[] t)
        {
            int mi = int.Parse(t[2]);
            int n = int.Parse(t[3]);
            UfbxMaterial mat = scene.Materials[mi];
            UfbxMaterialTexture[] textures = mat.Textures ?? Array.Empty<UfbxMaterialTexture>();
            if (n != textures.Length) return false;
            for (int i = 0; i < n; i++) {
                string matProp = t[4 + i * 3];
                string texId = t[5 + i * 3];
                string fileIndex = t[6 + i * 3];
                if (!EqHex64(matProp, FnvStr(textures[i].MaterialProp))) return false;
                if (!EqId(texId, textures[i].Texture != null ? textures[i].Texture.ElementId : uint.MaxValue)) return false;
                if (!EqId(fileIndex, textures[i].Texture != null ? textures[i].Texture.FileIndex : uint.MaxValue)) return false;
            }
            return true;
        }

        static bool DoP(string[] t)
        {
            int mi = int.Parse(t[2]);
            int mapIx = int.Parse(t[4]);
            UfbxMaterialMap map = t[3] == "m"
                ? scene.Materials[mi].Fbx.Maps[mapIx]
                : scene.Materials[mi].Pbr.Maps[mapIx];

            return Eq(t[5], map.HasValue ? 1 : 0)
                && EqHex64(t[6], Bit(map.ValueVec4.X))
                && EqHex64(t[7], Bit(map.ValueVec4.Y))
                && EqHex64(t[8], Bit(map.ValueVec4.Z))
                && EqHex64(t[9], Bit(map.ValueVec4.W))
                && Eq(t[10], map.ValueInt)
                && EqId(t[11], map.Texture != null ? map.Texture.ElementId : uint.MaxValue)
                && Eq(t[12], map.TextureEnabled ? 1 : 0)
                && Eq(t[13], map.ValueComponents);
        }

        static bool DoE(string[] t)
        {
            int mi = int.Parse(t[2]);
            int featureIx = int.Parse(t[3]);
            UfbxMaterialFeatureInfo feature = scene.Materials[mi].Features.Features[featureIx];
            return Eq(t[4], feature.Enabled ? 1 : 0)
                && Eq(t[5], feature.IsExplicit ? 1 : 0);
        }

        static bool DoT(string[] t)
        {
            int ix = int.Parse(t[2]);
            UfbxTextureFile file = scene.TextureFiles[ix];
            ulong contentFnv = Fnv(file.Content ?? Array.Empty<byte>());
            return Eq(t[3], file.Index)
                && EqHex64(t[4], FnvStr(file.Filename))
                && EqHex64(t[5], FnvStr(file.AbsoluteFilename))
                && EqHex64(t[6], FnvStr(file.RelativeFilename))
                && Eq(t[7], file.Content != null ? file.Content.Length : 0)
                && EqHex64(t[8], contentFnv);
        }

        static bool DoN(string[] t)
        {
            // N <fi> <typed_id> <parent|-> <depth> <name_fnv>
            int typedId = int.Parse(t[2]);
            UfbxNode node = scene.Nodes[typedId];
            if (!Eq(t[2], node.TypedId)) return false;
            bool parentOk = t[3] == "-" ? node.Parent == null : Eq(t[3], node.Parent.TypedId);
            return parentOk
                && Eq(t[4], node.NodeDepth)
                && EqHex64(t[5], FnvStr(node.Name));
        }

        static bool DoMatrix(string[] t, UfbxMatrix m)
        {
            // X/W <fi> <typed_id> <m00 m10 m20 m01 m11 m21 m02 m12 m22 m03 m13 m23>
            double[] values = new double[] {
                m.M00, m.M10, m.M20, m.M01, m.M11, m.M21,
                m.M02, m.M12, m.M22, m.M03, m.M13, m.M23,
            };
            for (int i = 0; i < 12; i++) {
                if (!EqHex64(t[3 + i], Bit(values[i]))) return false;
            }
            return true;
        }

        static bool DoLt(string[] t)
        {
            // LT <fi> <typed_id> <tx ty tz qx qy qz qw sx sy sz>
            UfbxNode node = scene.Nodes[int.Parse(t[2])];
            UfbxTransform tr = node.LocalTransform;
            double[] values = new double[] {
                tr.Translation.X, tr.Translation.Y, tr.Translation.Z,
                tr.Rotation.X, tr.Rotation.Y, tr.Rotation.Z, tr.Rotation.W,
                tr.Scale.X, tr.Scale.Y, tr.Scale.Z,
            };
            for (int i = 0; i < 10; i++) {
                if (!EqHex64(t[3 + i], Bit(values[i]))) return false;
            }
            return true;
        }

        static bool DoV(string[] t)
        {
            int meshIx = int.Parse(t[2]);
            UfbxMesh mesh = scene.Meshes[meshIx];
            UfbxVec3[] values = (UfbxVec3[])mesh.VertexPosition.Values;
            int num = values != null ? values.Length : 0;
            if (!Eq(t[3], num)) return false;

            ulong h = 0xcbf29ce484222325;
            for (int i = 0; i < num; i++) {
                h = FnvStep(h, BitConverter.DoubleToInt64Bits(values[i].X));
                h = FnvStep(h, BitConverter.DoubleToInt64Bits(values[i].Y));
                h = FnvStep(h, BitConverter.DoubleToInt64Bits(values[i].Z));
            }
            if (!EqHex64(t[4], h)) return false;

            int first = Math.Min(num, 8);
            for (int i = 0; i < first; i++) {
                if (!EqHex64(t[5 + i * 3], Bit(values[i].X))) return false;
                if (!EqHex64(t[6 + i * 3], Bit(values[i].Y))) return false;
                if (!EqHex64(t[7 + i * 3], Bit(values[i].Z))) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        // FNV-1a-64 over the raw bytes of a string (C: ufbx_string {data, len} bytes).
        static ulong FnvStr(string s)
        {
            byte[] bytes = UfbxiRawStr.ToBytes(s ?? "");
            return Fnv(bytes);
        }

        static ulong Fnv(byte[] bytes)
        {
            ulong h = 0xcbf29ce484222325;
            for (int i = 0; i < bytes.Length; i++) {
                h = (h ^ bytes[i]) * 0x100000001b3;
            }
            return h;
        }

        static ulong FnvStep(ulong h, long bits)
        {
            ulong u = unchecked((ulong)bits);
            for (int k = 0; k < 8; k++) {
                h = (h ^ ((u >> (k * 8)) & 0xff)) * 0x100000001b3;
            }
            return h;
        }

        static ulong Bit(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d));

        static bool Eq(string a, long b) => long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v == b;

        static bool EqId(string a, uint b) => ulong.Parse(a, CultureInfo.InvariantCulture) == b;

        static bool EqHex64(string a, ulong b) => ulong.Parse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture) == b;
    }
}
