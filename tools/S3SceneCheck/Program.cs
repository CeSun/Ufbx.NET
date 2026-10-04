// S3SceneCheck: isolated verification for the S3a scene-build helpers (Parse/SceneBuild.cs).
//
// What it proves
// --------------
// The S3a module is not yet wired into `UfbxiToplevel.SceneBuild()` (that seam is owned by the
// orchestrator, see COORDINATION.md), so GraphCheck/LoadCheck/DomCheck only show it does not
// regress the load path. This harness drives the ported helpers directly over an input/answer
// corpus produced by `tools/s3_oracle.c` (which `#include`s the frozen ufbx.c and calls the
// original `ufbxi_*` functions). Every record is an independent differential test of:
//
//   * ufbxi_pivot_div / ufbxi_pivot_nonzero                  (bit-exact doubles)
//   * cmp_connection_less / cmp_name_element_less(_ref) / cmp_prop_less_ref
//   * ufbxi_cmp_node_less / cmp_tmp_material_texture_less / cmp_anim_prop_less
//   * material_texture_less / bone_pose_less / blend_keyframe_less / prop_connection_less
//   * the lower/upper bound search macros behind find_dst_connections / find_src_connections
//     / find_prop_connection
//   * the stable sorts the S3a module runs (connection / anim-prop / blend-keyframe)
//
// The context-mutating chain (PreFinalizeScene / ResolveConnections / AddConnectionsToElements
// / LinearizeNodes) needs a live `ufbxi_context`; it is covered by the line-by-line audit and
// the three regression harnesses, not here.
//
// Mutation rig: break any covered helper in SceneBuild.cs (e.g. flip the `index ^ 1` tie-break
// in CmpConnectionLess, or the pivot epsilon), rebuild, and this program must FAIL; restore and
// it must PASS. See the S3a report.
//
// Usage: dotnet run --project tools/S3SceneCheck -c Release -- [tools/s3_oracle.txt]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx;

namespace S3SceneCheck
{
    static class Program
    {
        // Must match tools/s3_oracle.c `g_str[]`.
        static readonly string[] Str = { "", "a", "b", "ab", "X", "d|X", "Lcl Scaling", "Texture alpha" };

        static int fails;
        static int passes;
        static readonly List<string> failLines = new List<string>();

        static int Main(string[] args)
        {
            string oraclePath = args.Length > 0 ? args[0] : "tools/s3_oracle.txt";
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
                bool ok;
                try {
                    switch (rec) {
                    case "PD": ok = ChkHex(t[3], UfbxiSceneBuild.PivotDiv(D(t[1]), D(t[2]))); break;
                    case "PN": ok = ChkInt(t[4], UfbxiSceneBuild.PivotNonzero(new UfbxVec3(D(t[1]), D(t[2]), D(t[3]))) ? 1 : 0); break;
                    case "CC": ok = ChkInt(t[10], UfbxiSceneBuild.CmpConnectionLess(Conn(t[2], t[3], t[4], t[5]), Conn(t[6], t[7], t[8], t[9]), int.Parse(t[1])) ? 1 : 0); break;
                    case "CN": ok = ChkInt(t[7], UfbxiSceneBuild.CmpNameElementLess(NE(t[1], t[2], t[3]), NE(t[4], t[5], t[6])) ? 1 : 0); break;
                    case "CNR": ok = ChkInt(t[7], UfbxiSceneBuild.CmpNameElementLessRef(NE(t[1], t[2], t[3]), Str[int.Parse(t[4])], (UfbxElementType)int.Parse(t[6]), uint.Parse(t[5])) ? 1 : 0); break;
                    case "CPR": ok = ChkInt(t[5], UfbxiSceneBuild.CmpPropLessRef(Prop(t[1], t[2]), Str[int.Parse(t[3])], uint.Parse(t[4])) ? 1 : 0); break;
                    case "CMT": ok = ChkInt(t[7], UfbxiSceneBuild.CmpTmpMaterialTextureLess(TMT(t[1], t[2], t[3]), TMT(t[4], t[5], t[6])) ? 1 : 0); break;
                    case "CNODE": ok = ChkInt(t[11], UfbxiSceneBuild.CmpNodeLess(Node(t[1], t[2], t[3], t[4], t[5]), Node(t[6], t[7], t[8], t[9], t[10])) ? 1 : 0); break;
                    case "CA": ok = ChkInt(t[7], UfbxiSceneBuild.CmpAnimPropLess(AP(t[1], t[2], t[3]), AP(t[4], t[5], t[6])) ? 1 : 0); break;
                    case "MT": ok = ChkInt(t[3], UfbxiSceneBuild.MaterialTextureLess(null, MT(t[1]), MT(t[2])) ? 1 : 0); break;
                    case "BP": ok = ChkInt(t[3], UfbxiSceneBuild.BonePoseLess(null, BPose(t[1]), BPose(t[2])) ? 1 : 0); break;
                    case "BK": ok = ChkInt(t[3], UfbxiSceneBuild.BlendKeyframeLess(null, BKf(D(t[1])), BKf(D(t[2]))) ? 1 : 0); break;
                    case "PC": ok = ChkInt(t[3], UfbxiSceneBuild.PropConnectionLess(new UfbxConnection { DstProp = Str[int.Parse(t[1])], SrcProp = new string('x', int.Parse(t[2])) }, Str[int.Parse(t[1])]) ? 1 : 0); break;
                    case "FDS": ok = DoFds(t); break;
                    case "FSS": ok = DoFss(t); break;
                    case "FPC": ok = DoFpc(t); break;
                    case "SC": ok = DoSc(t); break;
                    case "SAP": ok = DoSap(t); break;
                    case "SBK": ok = DoSbk(t); break;
                    default: Console.Error.WriteLine("unknown record " + rec + " at line " + lineNo); return 2;
                    }
                } catch (Exception e) {
                    Console.Error.WriteLine("EXCEPTION on line " + lineNo + " (" + rec + "): " + e.Message);
                    return 2;
                }

                if (ok) {
                    passes++;
                } else {
                    fails++;
                    if (failLines.Count < 30) failLines.Add(lineNo + ": " + line);
                }
            }

            Console.WriteLine("S3 SCENE CHECK " + (fails == 0 ? "PASS" : "FAIL"));
            Console.WriteLine("records checked: " + (passes + fails));
            Console.WriteLine("pass:            " + passes);
            Console.WriteLine("fail:            " + fails);
            foreach (string f in failLines) Console.WriteLine("  DIVERGE " + f);
            return fails == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- builders

        // Element pool: one object per element id, so `ReferenceEquals` matches C's pointer
        // identity (the port compares element pointers / ids, never two objects with one id).
        static readonly Dictionary<uint, UfbxUnknown> pool = new Dictionary<uint, UfbxUnknown>();

        static UfbxUnknown El(uint id)
        {
            if (!pool.TryGetValue(id, out UfbxUnknown e)) {
                e = new UfbxUnknown { ElementId = id };
                pool[id] = e;
            }
            return e;
        }

        static UfbxConnection Conn(string src, string dst, string sp, string dp)
        {
            return new UfbxConnection { Src = El(uint.Parse(src)), Dst = El(uint.Parse(dst)), SrcProp = Str[int.Parse(sp)], DstProp = Str[int.Parse(dp)] };
        }

        static UfbxNameElement NE(string name, string key, string type)
        {
            return new UfbxNameElement { Name = Str[int.Parse(name)], InternalKey = uint.Parse(key), Type = (UfbxElementType)int.Parse(type) };
        }

        static UfbxProp Prop(string name, string key)
        {
            return new UfbxProp { Name = Str[int.Parse(name)], InternalKey = uint.Parse(key) };
        }

        static UfbxiTmpMaterialTexture TMT(string mat, string tex, string prop)
        {
            return new UfbxiTmpMaterialTexture { MaterialId = int.Parse(mat), TextureId = int.Parse(tex), PropName = Str[int.Parse(prop)] };
        }

        static UfbxNode Node(string depth, string pid, string geom, string scale, string eid)
        {
            UfbxNode n = new UfbxNode {
                NodeDepth = uint.Parse(depth),
                IsGeometryTransformHelper = int.Parse(geom) != 0,
                IsScaleHelper = int.Parse(scale) != 0,
                ElementId = uint.Parse(eid),
            };
            int p = int.Parse(pid);
            if (p != 0) n.Parent = new UfbxNode { ElementId = (uint)p };
            return n;
        }

        static UfbxAnimProp AP(string elem, string key, string prop)
        {
            return new UfbxAnimProp { Element = El(uint.Parse(elem)), InternalKey = uint.Parse(key), PropName = Str[int.Parse(prop)] };
        }

        static UfbxMaterialTexture MT(string prop) { return new UfbxMaterialTexture { MaterialProp = Str[int.Parse(prop)] }; }
        static UfbxBonePose BPose(string tid) { return new UfbxBonePose { BoneNode = new UfbxNode { TypedId = uint.Parse(tid) } }; }
        static UfbxBlendKeyframe BKf(double w) { return new UfbxBlendKeyframe { TargetWeight = w }; }

        // ---------------------------------------------------------------- records

        static bool DoFds(string[] t)
        {
            int n = int.Parse(t[1]);
            int q = int.Parse(t[2]);
            int begin = int.Parse(t[3]);
            int count = int.Parse(t[4]);
            UfbxConnection[] arr = new UfbxConnection[n];
            for (int i = 0; i < n; i++) {
                arr[i] = new UfbxConnection { Src = El(0), Dst = El(0), DstProp = Str[int.Parse(t[5 + i * 2])], SrcProp = Str[int.Parse(t[6 + i * 2])] };
            }
            UfbxUnknown elem = new UfbxUnknown { ConnectionsDst = arr };
            UfbxConnectionList res = UfbxiSceneBuild.FindDstConnections(elem, Str[q]);
            return res.Offset == begin && res.Count == count;
        }

        static bool DoFss(string[] t)
        {
            int n = int.Parse(t[1]);
            int q = int.Parse(t[2]);
            int begin = int.Parse(t[3]);
            int count = int.Parse(t[4]);
            UfbxConnection[] arr = new UfbxConnection[n];
            for (int i = 0; i < n; i++) {
                arr[i] = new UfbxConnection { Src = El(0), Dst = El(0), SrcProp = Str[int.Parse(t[5 + i * 2])], DstProp = Str[int.Parse(t[6 + i * 2])] };
            }
            UfbxUnknown elem = new UfbxUnknown { ConnectionsSrc = arr };
            UfbxConnectionList res = UfbxiSceneBuild.FindSrcConnections(elem, Str[q]);
            return res.Offset == begin && res.Count == count;
        }

        static bool DoFpc(string[] t)
        {
            int n = int.Parse(t[1]);
            int q = int.Parse(t[2]);
            int found = int.Parse(t[3]);
            UfbxConnection[] arr = new UfbxConnection[n];
            for (int i = 0; i < n; i++) {
                arr[i] = new UfbxConnection { Src = El(0), Dst = El(0), DstProp = Str[int.Parse(t[4 + i * 2])], SrcProp = Str[int.Parse(t[5 + i * 2])] };
            }
            UfbxUnknown elem = new UfbxUnknown { ConnectionsDst = arr };
            UfbxConnection res = UfbxiSceneBuild.FindPropConnection(elem, Str[q]);
            int got = -1;
            if (res != null) {
                for (int i = 0; i < n; i++) if (ReferenceEquals(arr[i], res)) { got = i; break; }
            }
            return got == found;
        }

        static bool DoSc(string[] t)
        {
            int ix = int.Parse(t[1]);
            int n = int.Parse(t[2]);
            UfbxConnection[] arr = new UfbxConnection[n];
            for (int i = 0; i < n; i++) {
                arr[i] = new UfbxConnection {
                    Src = El(uint.Parse(t[3 + i * 4])), Dst = El(uint.Parse(t[4 + i * 4])),
                    SrcProp = Str[int.Parse(t[5 + i * 4])], DstProp = Str[int.Parse(t[6 + i * 4])],
                };
            }
            UfbxiSceneBuild.SortConnections(arr, n, ix);
            int b = 3 + n * 4;
            for (int i = 0; i < n; i++) {
                int o = b + i * 4;
                if (arr[i].Src.ElementId != uint.Parse(t[o]) || arr[i].Dst.ElementId != uint.Parse(t[o + 1])) return false;
                if (Idx(arr[i].SrcProp) != int.Parse(t[o + 2]) || Idx(arr[i].DstProp) != int.Parse(t[o + 3])) return false;
            }
            return true;
        }

        static bool DoSap(string[] t)
        {
            int n = int.Parse(t[1]);
            UfbxAnimProp[] arr = new UfbxAnimProp[n];
            for (int i = 0; i < n; i++) {
                arr[i] = new UfbxAnimProp {
                    Element = El(uint.Parse(t[2 + i * 3])), InternalKey = uint.Parse(t[3 + i * 3]), PropName = Str[int.Parse(t[4 + i * 3])],
                };
            }
            UfbxiSceneBuild.SortAnimProps(arr, n);
            int b = 2 + n * 3;
            for (int i = 0; i < n; i++) {
                int o = b + i * 3;
                if (arr[i].Element.ElementId != uint.Parse(t[o]) || arr[i].InternalKey != uint.Parse(t[o + 1]) || Idx(arr[i].PropName) != int.Parse(t[o + 2])) return false;
            }
            return true;
        }

        static bool DoSbk(string[] t)
        {
            int n = int.Parse(t[1]);
            UfbxBlendKeyframe[] arr = new UfbxBlendKeyframe[n];
            for (int i = 0; i < n; i++) arr[i] = new UfbxBlendKeyframe { TargetWeight = D(t[2 + i]) };
            UfbxiSceneBuild.SortBlendKeyframes(arr, n);
            int b = 2 + n;
            for (int i = 0; i < n; i++) {
                if (Bits(arr[i].TargetWeight) != t[b + i]) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- helpers

        static int Idx(string s) { for (int i = 0; i < Str.Length; i++) if (Str[i] == s) return i; return -1; }

        static double D(string hex) { return BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture))); }
        static string Bits(double d) { return BitConverter.DoubleToInt64Bits(d).ToString("x16"); }

        static bool ChkHex(string expect, double got) { return expect == Bits(got); }
        static bool ChkInt(string expect, int got) { return expect == got.ToString(CultureInfo.InvariantCulture); }
    }
}
