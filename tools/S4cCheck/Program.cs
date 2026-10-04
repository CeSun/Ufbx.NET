// S4cCheck: isolated verification for the S4c topology / normals / triangulation
// module (Parse/Topology.cs) and the P1 geometry API (Parse/Subdivide.cs).
//
// What it proves
// --------------
// The C# port cannot load FBX files yet (the toplevel reader seam is unported), so the
// oracle `tools/s4c_oracle.c` (which `#include`s the frozen ufbx.c) loads the corpus with
// the real C loader and, for every mesh of every scene, dumps
//
//   * the mesh definition bit-exactly (VP/VPI/F/VTX/VFI/ED/ES/FS/VNV/VNI records), and
//   * the S4c outputs computed by the original ufbx code:
//       TOP <i> <index> <next> <prev> <twin> <face> <edge> <flags>   ufbx_compute_topology
//       MAP <s> <count> <i0> ...                                     ufbx_generate_normal_mapping
//       NRM <kind> <count> <x:h> <y:h> <z:h> ...                     ufbx_compute_normals
//       TRI <face_ix> <num_tris> <i0> ...                            ufbx_triangulate_face
//       WFN <face_ix> <x:h> <y:h> <z:h>                              ufbx_get_weighted_face_normal
//
// plus the P1 records:
//   NB/NC/NS                     nurbs bases, curves, surfaces (definition)
//   EVB/EVC/EVS                  ufbx_evaluate_nurbs_basis/curve/surface
//   TSC/TSCV/TSCI                ufbx_tessellate_nurbs_curve
//   TSS/TSSVP/TSSVPI/TSSF/TSSUV/TSSVN/TSSMP/TSSPT
//                                ufbx_tessellate_nurbs_surface
//   SUB/SUBFAIL/S*/SEND          ufbx_subdivide_mesh over a synthetic mesh built from
//                                exactly the definition records (both sides see the
//                                same data)
//   GINC/GIND/GINI/GINO          ufbx_generate_indices over synthetic vertex streams
//
// This harness reconstructs each mesh/nurbs object from the definition records, runs the
// ported functions once per object (caching the results) and compares every output record
// byte-for-byte. All doubles travel as 16-hex-digit IEEE-754 bit patterns, so the
// comparison is bit-exact.
//
// Usage: dotnet run --project tools/S4cCheck -c Release -- [tools/s4c_oracle.txt]
//
// Mutation rig: break anything the port transcribes verbatim (e.g. flip the orient2d
// epsilon in `NgonTriWeight`, change the weight-accumulation order in
// `CatchGetWeightedFaceNormalCore`, or the subdivision boundary mask), rebuild, and this
// program must FAIL; restore and it must PASS. See the S4c report.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ufbx.NET;

namespace S4cCheck
{
    static class Program
    {
        const uint NoIndex = UfbxConstants.NoIndex;

        static int fails;
        static int passes;
        static readonly List<string> failLines = new List<string>();
        static readonly Dictionary<string, int> failTypes = new Dictionary<string, int>();

        // ------------------------------------------------------------ mesh defs

        // Everything the definition dump preserves for one mesh, plus lazily built
        // UfbxMesh and cached S4c results.
        class MeshDef
        {
            public int NumFaces, NumIndices, NumVertices, NumEdges;
            public bool HasEs, HasFs, HasVn;
            public UfbxVec3[] VpValues; public uint[] VpIndices;
            public List<UfbxFace> Faces;
            public uint[] Vtx, Vfi;
            public List<UfbxEdge> Edges;
            public List<bool> EsBits, FsBits;
            public UfbxVec3[] VnValues; public uint[] VnIndices;

            public UfbxMesh Mesh;           // built once

            // P0 caches (topology / mapping / normals / triangulation).
            public UfbxTopoEdge[] Topo;
            public uint[][] MapIx = new uint[2][];
            public int[] MapCount = new int[2];
            public UfbxVec3[][] Nrm = new UfbxVec3[3][]; // map0, map1, vn (vn null when skipped)
            public uint[] TriCount;
            public uint[][] TriIdx;
            public UfbxVec3[] Wfn;
        }

        class SceneState
        {
            public List<MeshDef> Meshes = new List<MeshDef>();
            public List<UfbxNurbsCurve> Curves = new List<UfbxNurbsCurve>();
            public List<UfbxNurbsSurface> Surfaces = new List<UfbxNurbsSurface>();
        }

        static SceneState scene;
        static MeshDef cur;             // mesh currently collecting definition records

        // NB ids are globally unique across the whole oracle run (C: static next_id).
        static readonly Dictionary<long, UfbxNurbsBasis> bases = new Dictionary<long, UfbxNurbsBasis>();

        // Pending tessellation / subdivision results.
        static UfbxLineCurve pendLine;
        static UfbxMesh pendTess;
        static UfbxMesh pendSub;
        static UfbxTopoEdge[] pendSubTopo;   // STOP rows: topology computed once per SUB block
        static bool pendSubFail;
        static string pendSubFailDesc;
        static readonly HashSet<string> subSeen = new HashSet<string>();

        // Pending generate-indices case.
        static byte[][] ginStreams;
        static int ginNumIndices;
        static string subContext = "";
        static int sceneNo = -1;

        static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--dump") {
                DumpMesh(args[1], int.Parse(args[2], CultureInfo.InvariantCulture));
                return 0;
            }

            // Debug probe: --probe <oracle> <scene-line-no-of-SUB> — recompute the SUB
            // block whose SUB header starts at the given 1-based line and print
            // per-value diffs for its SVP/SVNV records.
            if (args.Length >= 3 && args[0] == "--probe") {
                ProbeSub(args[1], int.Parse(args[2], CultureInfo.InvariantCulture));
                return 0;
            }

            string oraclePath = args.Length > 0 ? args[0] : "tools/s4c_oracle.txt";
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
                bool ok = true;
                try {
                    switch (rec) {
                    case "SKIP":
                    case "SC":
                        scene = new SceneState();
                        cur = null;
                        sceneNo++;
                        break;
                    case "M":
                        cur = new MeshDef();
                        cur.NumFaces = int.Parse(t[2]);
                        cur.NumIndices = int.Parse(t[3]);
                        cur.NumVertices = int.Parse(t[4]);
                        cur.NumEdges = int.Parse(t[5]);
                        cur.HasEs = t[6] == "1";
                        cur.HasFs = t[7] == "1";
                        cur.HasVn = t[8] == "1";
                        cur.Faces = new List<UfbxFace>();
                        cur.Edges = new List<UfbxEdge>();
                        cur.EsBits = new List<bool>();
                        cur.FsBits = new List<bool>();
                        scene.Meshes.Add(cur);
                        break;
                    case "VP":
                        cur.VpValues = new UfbxVec3[int.Parse(t[1])];
                        for (int i = 0; i < cur.VpValues.Length; i++) {
                            cur.VpValues[i] = new UfbxVec3(D(t[2 + i * 3]), D(t[3 + i * 3]), D(t[4 + i * 3]));
                        }
                        break;
                    case "VPI":
                        cur.VpIndices = new uint[int.Parse(t[1])];
                        for (int i = 0; i < cur.VpIndices.Length; i++) cur.VpIndices[i] = uint.Parse(t[2 + i]);
                        break;
                    case "F":
                        cur.Faces.Add(new UfbxFace { IndexBegin = uint.Parse(t[2]), NumIndices = uint.Parse(t[3]) });
                        break;
                    case "VTX":
                        cur.Vtx = new uint[int.Parse(t[1])];
                        for (int i = 0; i < cur.Vtx.Length; i++) cur.Vtx[i] = uint.Parse(t[2 + i]);
                        break;
                    case "VFI":
                        cur.Vfi = new uint[int.Parse(t[1])];
                        for (int i = 0; i < cur.Vfi.Length; i++) cur.Vfi[i] = uint.Parse(t[2 + i]);
                        break;
                    case "ED":
                        cur.Edges.Add(new UfbxEdge { A = uint.Parse(t[1]), B = uint.Parse(t[2]) });
                        break;
                    case "ES":
                        cur.EsBits.Add(t[1] == "1");
                        break;
                    case "FS":
                        cur.FsBits.Add(t[1] == "1");
                        break;
                    case "VNV":
                        cur.VnValues = new UfbxVec3[int.Parse(t[1])];
                        for (int i = 0; i < cur.VnValues.Length; i++) {
                            cur.VnValues[i] = new UfbxVec3(D(t[2 + i * 3]), D(t[3 + i * 3]), D(t[4 + i * 3]));
                        }
                        break;
                    case "VNI":
                        cur.VnIndices = new uint[int.Parse(t[1])];
                        for (int i = 0; i < cur.VnIndices.Length; i++) cur.VnIndices[i] = uint.Parse(t[2 + i]);
                        break;

                    case "TOP": ok = ChkTop(cur, t); break;
                    case "MAP": ok = ChkMap(cur, t); break;
                    case "NRM": ok = ChkNrm(cur, t); break;
                    case "TRI": ok = ChkTri(cur, t); break;
                    case "WFN": ok = ChkWfn(cur, t); break;

                    // ------------------------------------------------------------ P1
                    case "NB": ok = DefBasis(t); break;
                    case "NC": ok = DefCurve(t); break;
                    case "NS": ok = DefSurface(t); break;
                    case "EVB": ok = ChkEvalBasis(t); break;
                    case "EVC": ok = ChkEvalCurve(t); break;
                    case "EVS": ok = ChkEvalSurface(t); break;
                    case "TSC": ok = RunTessCurve(t); break;
                    case "TSCV": ok = ChkTessCurvePts(t); break;
                    case "TSCI": ok = ChkTessCurveIdx(t); break;
                    case "TSS": ok = RunTessSurface(t); break;
                    case "TSSVP": ok = ChkTessVp(t); break;
                    case "TSSVPI": ok = ChkTessVpi(t); break;
                    case "TSSF": ok = ChkTessFace(t); break;
                    case "TSSUV": ok = ChkTessUv(t); break;
                    case "TSSVN": ok = ChkTessVn(t); break;
                    case "TSSMP": ok = ChkTessMp(t); break;
                    case "TSSPT": ok = ChkTessPt(t); break;
                    case "SUB": ok = RunSubdivide(t); break;
                    case "STOP": ok = ChkSubTop(t); break;
                    case "SUBFAIL": ok = ChkSubFail(t); break;
                    case "SVP": ok = ChkSubVp(t); break;
                    case "SVPI": ok = ChkSubVpi(t); break;
                    case "SF": ok = ChkSubFace(t); break;
                    case "SVTX": ok = ChkSubIndices(t, "SVTX"); break;
                    case "SVFI": ok = ChkSubIndices(t, "SVFI"); break;
                    case "SED": ok = ChkSubEdge(t); break;
                    case "SES": ok = ChkSubBools(t, "SES"); break;
                    case "SEV": ok = ChkSubBools(t, "SEV"); break;
                    case "SEC": ok = ChkSubReals(t, "SEC"); break;
                    case "SFS": ok = ChkSubBools(t, "SFS"); break;
                    case "SFM": ok = ChkSubUints(t, "SFM"); break;
                    case "SGR": ok = ChkSubUints(t, "SGR"); break;
                    case "SHL": ok = ChkSubBools(t, "SHL"); break;
                    case "SVNV": ok = ChkSubVnv(t); break;
                    case "SVNI": ok = ChkSubVni(t); break;
                    case "SMP": ok = ChkSubMp(t); break;
                    case "SMPt": ok = ChkSubMpPart(t); break;
                    case "SMU": ok = ChkSubUints(t, "SMU"); break;
                    case "SEND": ok = EndSubdivide(); break;
                    case "GINC": ok = DefGinCase(t); break;
                    case "GIND": ok = DefGinStream(t); break;
                    case "GINI": ok = RunGin(t); break;
                    case "GINO": ok = ChkGinOut(t); break;

                    default: Console.Error.WriteLine("unknown record " + rec + " at line " + lineNo); return 2;
                    }
                } catch (Exception e) {
                    Console.Error.WriteLine("EXCEPTION on line " + lineNo + " (" + rec + "): " + e);
                    return 2;
                }

                if (ok) {
                    passes++;
                } else {
                    fails++;
                    if (!failTypes.TryGetValue(rec, out int c)) c = 0;
                    failTypes[rec] = c + 1;
                    if (failLines.Count < 2000) failLines.Add(lineNo + ": " + Trunc(line));
                }
            }

            Console.WriteLine("S4C CHECK " + (fails == 0 ? "PASS" : "FAIL"));
            Console.WriteLine("records checked: " + (passes + fails));
            Console.WriteLine("pass:            " + passes);
            Console.WriteLine("fail:            " + fails);
            foreach (KeyValuePair<string, int> kv in failTypes) {
                Console.WriteLine("  fail " + kv.Key + ": " + kv.Value);
            }
            foreach (string f in failLines) Console.WriteLine("  DIVERGE " + f);
            return fails == 0 ? 0 : 1;
        }

        static string Trunc(string s) { return s.Length > 200 ? s.Substring(0, 200) + "..." : s; }

        // Debug probe: recompute one SUB block and diff SVP/SVNV per value.
        static void ProbeSub(string oraclePath, int subLine)
        {
            string[] lines = File.ReadAllLines(oraclePath);
            // Rebuild state up to the SUB line.
            scene = new SceneState();
            cur = null;
            sceneNo = -1;
            for (int i = 0; i < subLine - 1; i++) {
                string[] t = lines[i].Split(' ');
                switch (t[0]) {
                case "SKIP": case "SC":
                    scene = new SceneState(); cur = null; sceneNo++;
                    break;
                case "M":
                    cur = new MeshDef {
                        NumFaces = int.Parse(t[2]), NumIndices = int.Parse(t[3]),
                        NumVertices = int.Parse(t[4]), NumEdges = int.Parse(t[5]),
                        HasEs = t[6] == "1", HasFs = t[7] == "1", HasVn = t[8] == "1",
                        Faces = new List<UfbxFace>(), Edges = new List<UfbxEdge>(),
                        EsBits = new List<bool>(), FsBits = new List<bool>(),
                    };
                    scene.Meshes.Add(cur);
                    break;
                case "VP": case "VPI": case "F": case "VTX": case "VFI":
                case "ED": case "ES": case "FS": case "VNV": case "VNI":
                    DefMeshRecord(t[0], t);
                    break;
                }
            }

            string[] sub = lines[subLine - 1].Split(' ');
            int meshIx = int.Parse(sub[1]);
            int level = int.Parse(sub[2]);
            var opts = new UfbxSubdivideOpts {
                InterpolateNormals = sub[3] == "1",
                Boundary = (UfbxSubdivisionBoundary)int.Parse(sub[4]),
                UvBoundary = (UfbxSubdivisionBoundary)int.Parse(sub[5]),
            };
            UfbxMesh src = BuildMesh(scene.Meshes[meshIx]);
            var error = new UfbxError();
            UfbxMesh port = UfbxGeometryApi.SubdivideMesh(src, level, opts, error);

            // Walk forward collecting the oracle's SVP/SVNV for this block.
            UfbxVec3[] ovp = null, ovn = null;
            for (int i = subLine; i < lines.Length; i++) {
                string[] t = lines[i].Split(' ');
                if (t[0] == "SUB" || t[0] == "SC" || t[0] == "SKIP") break;
                if (t[0] == "SEND") break;
                if (t[0] == "SVP") {
                    int n = int.Parse(t[1]);
                    ovp = new UfbxVec3[n];
                    for (int k = 0; k < n; k++) {
                        ovp[k] = new UfbxVec3(D(t[2 + k * 3]), D(t[3 + k * 3]), D(t[4 + k * 3]));
                    }
                } else if (t[0] == "SVNV") {
                    int n = int.Parse(t[1]);
                    ovn = new UfbxVec3[n];
                    for (int k = 0; k < n; k++) {
                        ovn[k] = new UfbxVec3(D(t[2 + k * 3]), D(t[3 + k * 3]), D(t[4 + k * 3]));
                    }
                }
            }

            UfbxVec3[] pvp = port.VertexPosition.Values;
            Console.WriteLine("SVP: oracle " + (ovp?.Length ?? -1) + " port " + (pvp?.Length ?? -1));
            if (ovp != null && pvp != null) {
                int bad = 0;
                for (int k = 0; k < Math.Min(ovp.Length, pvp.Length); k++) {
                    if (Bits(ovp[k].X) != Bits(pvp[k].X) || Bits(ovp[k].Y) != Bits(pvp[k].Y) || Bits(ovp[k].Z) != Bits(pvp[k].Z)) {
                        if (bad < 12) Console.WriteLine("  SVP[" + k + "] DIFF oracle " + Bits(ovp[k].X) + " " + Bits(ovp[k].Y) + " " + Bits(ovp[k].Z)
                            + "  port " + Bits(pvp[k].X) + " " + Bits(pvp[k].Y) + " " + Bits(pvp[k].Z));
                        bad++;
                    }
                }
                Console.WriteLine("  SVP total diff " + bad + " / " + Math.Min(ovp.Length, pvp.Length));
            }
            UfbxVec3[] pvn = port.VertexNormal.Values;
            if (ovn != null && pvn != null) {
                int bad = 0;
                for (int k = 0; k < Math.Min(ovn.Length, pvn.Length); k++) {
                    if (Bits(ovn[k].X) != Bits(pvn[k].X) || Bits(ovn[k].Y) != Bits(pvn[k].Y) || Bits(ovn[k].Z) != Bits(pvn[k].Z)) {
                        if (bad < 8) Console.WriteLine("  SVNV[" + k + "] DIFF oracle " + Bits(ovn[k].X) + " " + Bits(ovn[k].Y) + " " + Bits(ovn[k].Z)
                            + "  port " + Bits(pvn[k].X) + " " + Bits(pvn[k].Y) + " " + Bits(pvn[k].Z));
                        bad++;
                    }
                }
                Console.WriteLine("  SVNV total diff " + bad + " / " + Math.Min(ovn.Length, pvn.Length));
            }
        }

        // Debug aid: --dump <oracle> <mesh-ordinal> reconstructs the mesh and prints the
        // port's computed records for manual comparison against the oracle lines.
        static void DumpMesh(string oraclePath, int ordinal)
        {
            int seen = -1;
            foreach (string raw in File.ReadLines(oraclePath)) {
                string line = raw.TrimEnd('\r', '\n');
                if (line.Length == 0) continue;
                string[] t = line.Split(' ');
                if (t[0] == "SC" || t[0] == "SKIP") { scene = new SceneState(); cur = null; continue; }
                if (t[0] == "M") {
                    seen++;
                    cur = new MeshDef {
                        NumFaces = int.Parse(t[2]), NumIndices = int.Parse(t[3]),
                        NumVertices = int.Parse(t[4]), NumEdges = int.Parse(t[5]),
                        HasEs = t[6] == "1", HasFs = t[7] == "1", HasVn = t[8] == "1",
                        Faces = new List<UfbxFace>(), Edges = new List<UfbxEdge>(),
                        EsBits = new List<bool>(), FsBits = new List<bool>(),
                    };
                    scene.Meshes.Add(cur);
                    continue;
                }
                if (seen != ordinal) continue;
                switch (t[0]) {
                case "VP": case "VPI": case "F": case "VTX": case "VFI":
                case "ED": case "ES": case "FS": case "VNV": case "VNI":
                    DefMeshRecord(t[0], t);
                    break;
                case "TOP":
                    ComputeP0(cur);
                    for (int i = 0; i < cur.NumIndices; i++) {
                        UfbxTopoEdge e = cur.Topo[i];
                        Console.WriteLine("TOP " + i + " " + e.Index + " " + e.Next + " " + e.Prev + " " + e.Twin + " " + e.Face + " " + e.Edge + " " + (uint)e.Flags);
                    }
                    for (int s = 0; s < 2; s++) {
                        Console.Write("MAP " + s + " " + cur.MapCount[s]);
                        for (int i = 0; i < cur.NumIndices; i++) {
                            Console.Write(" " + (cur.MapIx[s][i] == NoIndex ? "-" : cur.MapIx[s][i].ToString(CultureInfo.InvariantCulture)));
                        }
                        Console.WriteLine();
                    }
                    for (int s = 0; s < 2; s++) {
                        Console.Write("NRM map" + s + " " + cur.MapCount[s]);
                        for (int i = 0; i < cur.MapCount[s]; i++) {
                            Console.Write(" " + Bits(cur.Nrm[s][i].X) + " " + Bits(cur.Nrm[s][i].Y) + " " + Bits(cur.Nrm[s][i].Z));
                        }
                        Console.WriteLine();
                    }
                    if (cur.Nrm[2] != null) {
                        Console.Write("NRM vn " + cur.Nrm[2].Length);
                        for (int i = 0; i < cur.Nrm[2].Length; i++) {
                            Console.Write(" " + Bits(cur.Nrm[2][i].X) + " " + Bits(cur.Nrm[2][i].Y) + " " + Bits(cur.Nrm[2][i].Z));
                        }
                        Console.WriteLine();
                    } else {
                        Console.WriteLine("NRM vn -1");
                    }
                    for (int fi = 0; fi < cur.NumFaces; fi++) {
                        UfbxFace face = cur.Mesh.Faces[fi];
                        Console.WriteLine("WFN " + fi + " " + Bits(cur.Wfn[fi].X) + " " + Bits(cur.Wfn[fi].Y) + " " + Bits(cur.Wfn[fi].Z));
                    }
                    for (int fi = 0; fi < cur.NumFaces; fi++) {
                        Console.Write("TRI " + fi + " " + cur.TriCount[fi]);
                        uint[] ti = cur.TriIdx[fi];
                        if (ti != null) {
                            for (int k = 0; k < (int)cur.TriCount[fi] * 3; k++) Console.Write(" " + ti[k]);
                        }
                        Console.WriteLine();
                    }
                    return;
                }
            }
            Console.Error.WriteLine("mesh ordinal not found: " + ordinal);
        }

        static void DefMeshRecord(string rec, string[] t)
        {
            switch (rec) {
            case "VP":
                cur.VpValues = new UfbxVec3[int.Parse(t[1])];
                for (int i = 0; i < cur.VpValues.Length; i++) {
                    cur.VpValues[i] = new UfbxVec3(D(t[2 + i * 3]), D(t[3 + i * 3]), D(t[4 + i * 3]));
                }
                break;
            case "VPI":
                cur.VpIndices = new uint[int.Parse(t[1])];
                for (int i = 0; i < cur.VpIndices.Length; i++) cur.VpIndices[i] = uint.Parse(t[2 + i]);
                break;
            case "F":
                cur.Faces.Add(new UfbxFace { IndexBegin = uint.Parse(t[2]), NumIndices = uint.Parse(t[3]) });
                break;
            case "VTX":
                cur.Vtx = new uint[int.Parse(t[1])];
                for (int i = 0; i < cur.Vtx.Length; i++) cur.Vtx[i] = uint.Parse(t[2 + i]);
                break;
            case "VFI":
                cur.Vfi = new uint[int.Parse(t[1])];
                for (int i = 0; i < cur.Vfi.Length; i++) cur.Vfi[i] = uint.Parse(t[2 + i]);
                break;
            case "ED":
                cur.Edges.Add(new UfbxEdge { A = uint.Parse(t[1]), B = uint.Parse(t[2]) });
                break;
            case "ES":
                cur.EsBits.Add(t[1] == "1");
                break;
            case "FS":
                cur.FsBits.Add(t[1] == "1");
                break;
            case "VNV":
                cur.VnValues = new UfbxVec3[int.Parse(t[1])];
                for (int i = 0; i < cur.VnValues.Length; i++) {
                    cur.VnValues[i] = new UfbxVec3(D(t[2 + i * 3]), D(t[3 + i * 3]), D(t[4 + i * 3]));
                }
                break;
            case "VNI":
                cur.VnIndices = new uint[int.Parse(t[1])];
                for (int i = 0; i < cur.VnIndices.Length; i++) cur.VnIndices[i] = uint.Parse(t[2 + i]);
                break;
            }
        }

        // Build the UfbxMesh for a definition (C oracle: dump_mesh's records describe
        // exactly these fields).
        static UfbxMesh BuildMesh(MeshDef md)
        {
            if (md.Mesh != null) return md.Mesh;

            UfbxVertexVec3 vpos = new UfbxVertexVec3 {
                Exists = true, Values = md.VpValues, Indices = md.VpIndices,
                ValueReals = 3,      // C loader: vertex_position.value_reals = 3 (ufbx.c:13220-13224)
            };
            UfbxVertexVec3 vn = new UfbxVertexVec3 {
                Exists = md.HasVn, Values = md.VnValues, Indices = md.VnIndices,
                ValueReals = 3,
            };

            UfbxMesh mesh = new UfbxMesh {
                NumFaces = md.NumFaces,
                NumIndices = md.NumIndices,
                NumVertices = md.NumVertices,
                NumEdges = md.NumEdges,
                Faces = md.Faces.ToArray(),
                // C: `if (mesh->edges.data)` gates the whole edge subdivision -- a mesh
                // with no edge records has data == NULL (the oracle dumps one ED per
                // edge, so no records => NULL).
                Edges = md.Edges.Count > 0 ? md.Edges.ToArray() : null,
                EdgeSmoothing = md.HasEs ? md.EsBits.ToArray() : null,
                FaceSmoothing = md.HasFs ? md.FsBits.ToArray() : null,
                VertexIndices = md.Vtx,
                VertexFirstIndex = md.Vfi,
                VertexPosition = vpos,
                VertexNormal = vn,
            };

            // C loader: skinned position/normal alias the vertex ones when the mesh has
            // no skin deformers; ufbx.c:29694/29708 compare `.values.data` identity.
            mesh.SkinnedPosition = vpos;
            mesh.SkinnedNormal = vn;

            // C: ufbxi_finalize_mesh recomputes `max_face_triangles` as
            // `max(face.num_indices - 2)` (ufbx.c:16705-16718); ufbxi_subdivide_layer
            // sizes its per-face input array with `max(32, max_face_triangles + 2)`
            // (ufbx.c:29084).
            int maxFaceTriangles = 0;
            foreach (UfbxFace f in md.Faces) {
                if ((int)f.NumIndices - 2 > maxFaceTriangles) maxFaceTriangles = (int)f.NumIndices - 2;
            }
            mesh.MaxFaceTriangles = maxFaceTriangles;

            md.Mesh = mesh;
            return mesh;
        }

        // Run every P0 S4c function once (C: dump_mesh in s4c_oracle.c).
        static void ComputeP0(MeshDef md)
        {
            if (md.Topo != null) return;
            UfbxMesh mesh = BuildMesh(md);

            // C: ufbx_compute_topology(mesh, topo, mesh->num_indices) (ufbx.c:33176).
            md.Topo = new UfbxTopoEdge[mesh.NumIndices];
            UfbxTopology.ComputeTopology(mesh, md.Topo, mesh.NumIndices);

            // C: ufbx_generate_normal_mapping(...) for assume_smooth = false/true (32588).
            for (int s = 0; s < 2; s++) {
                md.MapIx[s] = new uint[mesh.NumIndices];
                md.MapCount[s] = UfbxTopology.GenerateNormalMapping(mesh, md.Topo, mesh.NumIndices, md.MapIx[s], mesh.NumIndices, s == 1);
            }

            // C: ufbx_compute_normals(...) against the mapping (32622) and the loaded
            // normals when they can index the whole mesh.
            md.Nrm[2] = null;
            for (int s = 0; s < 2; s++) {
                md.Nrm[s] = new UfbxVec3[md.MapCount[s]];
                UfbxTopology.ComputeNormals(mesh, mesh.VertexPosition, md.MapIx[s], mesh.NumIndices, md.Nrm[s], md.MapCount[s]);
            }
            if (md.HasVn && md.VnIndices != null && md.VnIndices.Length >= mesh.NumIndices) {
                md.Nrm[2] = new UfbxVec3[md.VnValues != null ? md.VnValues.Length : 0];
                UfbxTopology.ComputeNormals(mesh, mesh.VertexPosition, md.VnIndices, mesh.NumIndices, md.Nrm[2], md.Nrm[2].Length);
            }

            md.TriCount = new uint[mesh.NumFaces];
            md.TriIdx = new uint[mesh.NumFaces][];
            md.Wfn = new UfbxVec3[mesh.NumFaces];
            for (int fi = 0; fi < mesh.NumFaces; fi++) {
                UfbxFace face = mesh.Faces[fi];
                if (face.NumIndices >= 3) {
                    int required = ((int)face.NumIndices - 2) * 3;
                    uint[] ti = new uint[required];
                    md.TriCount[fi] = UfbxTopology.TriangulateFace(ti, required, mesh, face);
                    md.TriIdx[fi] = ti;
                }
                md.Wfn[fi] = UfbxTopology.GetWeightedFaceNormal(mesh.VertexPosition, face);
            }
        }

        // ---------------------------------------------------------------- P0 comparisons

        static bool ChkTop(MeshDef md, string[] t)
        {
            ComputeP0(md);
            int i = int.Parse(t[1]);
            if (i < 0 || i >= md.NumIndices || t.Length != 9) return false;
            UfbxTopoEdge e = md.Topo[i];
            return t[2] == e.Index.ToString(CultureInfo.InvariantCulture)
                && t[3] == e.Next.ToString(CultureInfo.InvariantCulture)
                && t[4] == e.Prev.ToString(CultureInfo.InvariantCulture)
                && t[5] == e.Twin.ToString(CultureInfo.InvariantCulture)
                && t[6] == e.Face.ToString(CultureInfo.InvariantCulture)
                && t[7] == e.Edge.ToString(CultureInfo.InvariantCulture)
                && t[8] == ((uint)e.Flags).ToString(CultureInfo.InvariantCulture);
        }

        // STOP: topology of the subdivided mesh, recomputed the same way the
        // oracle does (ufbx_compute_topology on the result mesh). The array is
        // computed once per SUB block and cached (one row per index would be O(n^2)).
        static bool ChkSubTop(string[] t)
        {
            if (pendSubFail) return false;
            if (pendSubTopo == null || pendSubTopo.Length != pendSub.NumIndices) {
                pendSubTopo = new UfbxTopoEdge[pendSub.NumIndices];
                UfbxTopology.ComputeTopology(pendSub, pendSubTopo, pendSub.NumIndices);
            }
            int i = int.Parse(t[1]);
            if (i < 0 || i >= pendSub.NumIndices || t.Length != 9) return false;
            UfbxTopoEdge e = pendSubTopo[i];
            return t[2] == e.Index.ToString(CultureInfo.InvariantCulture)
                && t[3] == e.Next.ToString(CultureInfo.InvariantCulture)
                && t[4] == e.Prev.ToString(CultureInfo.InvariantCulture)
                && t[5] == e.Twin.ToString(CultureInfo.InvariantCulture)
                && t[6] == e.Face.ToString(CultureInfo.InvariantCulture)
                && t[7] == e.Edge.ToString(CultureInfo.InvariantCulture)
                && t[8] == ((uint)e.Flags).ToString(CultureInfo.InvariantCulture);
        }

        static bool ChkMap(MeshDef md, string[] t)
        {
            ComputeP0(md);
            int s = int.Parse(t[1]);
            int count = int.Parse(t[2]);
            if (s != 0 && s != 1) return false;
            if (count != md.MapCount[s]) return false;
            if (t.Length != 3 + md.NumIndices) return false;
            for (int i = 0; i < md.NumIndices; i++) {
                string want = md.MapIx[s][i] == NoIndex ? "-" : md.MapIx[s][i].ToString(CultureInfo.InvariantCulture);
                if (t[3 + i] != want) return false;
            }
            return true;
        }

        static bool ChkNrm(MeshDef md, string[] t)
        {
            ComputeP0(md);
            UfbxVec3[] want;
            if (t[1] == "map0") want = md.Nrm[0];
            else if (t[1] == "map1") want = md.Nrm[1];
            else want = md.Nrm[2]; // "vn"
            int count = int.Parse(t[2]);
            if (t[2] == "-1") return want == null; // skipped on both sides
            if (want == null || count != want.Length) return false;
            if (t.Length != 3 + count * 3) return false;
            for (int i = 0; i < count; i++) {
                if (t[3 + i * 3] != Bits(want[i].X) || t[4 + i * 3] != Bits(want[i].Y) || t[5 + i * 3] != Bits(want[i].Z)) {
                    if (failLines.Count < 30) {
                        failLines.Add(string.Format(CultureInfo.InvariantCulture,
                            "    NRM[{0}] oracle ({1},{2},{3}) port ({4},{5},{6})",
                            i, t[3 + i * 3], t[4 + i * 3], t[5 + i * 3], Bits(want[i].X), Bits(want[i].Y), Bits(want[i].Z)));
                    }
                    return false;
                }
            }
            return true;
        }

        static bool ChkTri(MeshDef md, string[] t)
        {
            ComputeP0(md);
            int fi = int.Parse(t[1]);
            if (fi < 0 || fi >= md.NumFaces) return false;
            int nt = int.Parse(t[2]);
            if (nt != (int)md.TriCount[fi]) return false;
            if (t.Length != 3 + nt * 3) return false;
            for (int k = 0; k < nt * 3; k++) {
                if (uint.Parse(t[3 + k]) != md.TriIdx[fi][k]) return false;
            }
            return true;
        }

        static bool ChkWfn(MeshDef md, string[] t)
        {
            ComputeP0(md);
            int fi = int.Parse(t[1]);
            if (fi < 0 || fi >= md.NumFaces) return false;
            UfbxVec3 w = md.Wfn[fi];
            return t[2] == Bits(w.X) && t[3] == Bits(w.Y) && t[4] == Bits(w.Z);
        }

        // ---------------------------------------------------------------- nurbs defs

        static bool DefBasis(string[] t)
        {
            long id = long.Parse(t[1]);
            var b = new UfbxNurbsBasis {
                Order = uint.Parse(t[2]),
                Topology = (UfbxNurbsTopology)int.Parse(t[3]),
                Valid = t[4] == "1",
                TMin = D(t[5]),
                TMax = D(t[6]),
            };
            int nk = int.Parse(t[7]);
            b.KnotVector = new double[nk];
            for (int i = 0; i < nk; i++) b.KnotVector[i] = D(t[8 + i]);
            int ns = int.Parse(t[8 + nk]);
            b.Spans = new double[ns];
            for (int i = 0; i < ns; i++) b.Spans[i] = D(t[9 + nk + i]);
            bases[id] = b;
            return true;
        }

        static bool DefCurve(string[] t)
        {
            long ci = long.Parse(t[1]);
            long bid = long.Parse(t[2]);
            int ncp = int.Parse(t[3]);
            var c = new UfbxNurbsCurve {
                Basis = bases[bid],
                ControlPoints = new UfbxVec4[ncp],
            };
            for (int i = 0; i < ncp; i++) {
                c.ControlPoints[i] = new UfbxVec4(D(t[4 + i * 4]), D(t[5 + i * 4]), D(t[6 + i * 4]), D(t[7 + i * 4]));
            }
            while (scene.Curves.Count <= (int)ci) scene.Curves.Add(null);
            scene.Curves[(int)ci] = c;
            return true;
        }

        static bool DefSurface(string[] t)
        {
            long si = long.Parse(t[1]);
            long bidu = long.Parse(t[2]);
            long bidv = long.Parse(t[3]);
            int ncpu = int.Parse(t[4]);
            int ncpv = int.Parse(t[5]);
            var s = new UfbxNurbsSurface {
                BasisU = bases[bidu],
                BasisV = bases[bidv],
                NumControlPointsU = ncpu,
                NumControlPointsV = ncpv,
                FlipNormals = t[6] == "1",
                ControlPoints = new UfbxVec4[ncpu * ncpv],
            };
            // NS header: <si> <bidu> <bidv> <nu> <nv> <flip> <mat> then
            // dump_curve_points prints the point count first, points start at t[9].
            for (int i = 0; i < ncpu * ncpv; i++) {
                s.ControlPoints[i] = new UfbxVec4(D(t[9 + i * 4]), D(t[10 + i * 4]), D(t[11 + i * 4]), D(t[12 + i * 4]));
            }
            while (scene.Surfaces.Count <= (int)si) scene.Surfaces.Add(null);
            scene.Surfaces[(int)si] = s;
            return true;
        }

        // ---------------------------------------------------------------- nurbs eval

        // C fills the caller's weight/derivative buffers up to `order`; when it returns
        // early the buffers keep their prior contents (the oracle memsets 0xcd). The
        // port mirrors that by leaving the arrays untouched, so pre-fill with the same
        // bit pattern before calling.
        static readonly double CdFill = BitConverter.Int64BitsToDouble(unchecked((long)0xcdcdcdcdcdcdcdcdUL));

        static bool ChkEvalBasis(string[] t)
        {
            long bid = long.Parse(t[1]);
            double u = D(t[2]);
            UfbxNurbsBasis b = bases[bid];
            int order = (int)b.Order;
            double[] w = new double[order];
            double[] d = new double[order];
            for (int i = 0; i < order; i++) { w[i] = CdFill; d[i] = CdFill; }

            long res = UfbxGeometryApi.EvaluateNurbsBasis(b, u, w, order, d, order);

            // C: base is size_t; compare bit-exactly (the wrapped no-span path yields
            // huge size_t values the port keeps bit-identical via long; SIZE_MAX
            // prints as "-1").
            ulong wantBase = t[3] == "-1" ? ulong.MaxValue
                : ulong.Parse(t[3], CultureInfo.InvariantCulture);
            if (wantBase != unchecked((ulong)res)) return false;
            if (t.Length != 4 + order * 2) return false;
            for (int i = 0; i < order; i++) {
                if (t[4 + i] != Bits(w[i])) return false;
                if (t[4 + order + i] != Bits(d[i])) return false;
            }
            return true;
        }

        static bool ChkEvalCurve(string[] t)
        {
            long ci = long.Parse(t[1]);
            double u = D(t[2]);
            var p = UfbxGeometryApi.EvaluateNurbsCurve(scene.Curves[(int)ci], u);
            bool valid = t[3] == "1";
            if (p.Valid != valid) return false;
            return t[4] == Bits(p.Position.X) && t[5] == Bits(p.Position.Y) && t[6] == Bits(p.Position.Z)
                && t[7] == Bits(p.Derivative.X) && t[8] == Bits(p.Derivative.Y) && t[9] == Bits(p.Derivative.Z);
        }

        static bool ChkEvalSurface(string[] t)
        {
            long si = long.Parse(t[1]);
            double u = D(t[2]);
            double v = D(t[3]);
            var p = UfbxGeometryApi.EvaluateNurbsSurface(scene.Surfaces[(int)si], u, v);
            bool valid = t[4] == "1";
            if (p.Valid != valid) {
                if (failLines.Count < 2000) failLines.Add("    EVS si " + si + " valid oracle " + t[4] + " port " + p.Valid);
                return false;
            }
            if (t[5] != Bits(p.Position.X) || t[6] != Bits(p.Position.Y) || t[7] != Bits(p.Position.Z)
                || t[8] != Bits(p.DerivativeU.X) || t[9] != Bits(p.DerivativeU.Y) || t[10] != Bits(p.DerivativeU.Z)
                || t[11] != Bits(p.DerivativeV.X) || t[12] != Bits(p.DerivativeV.Y) || t[13] != Bits(p.DerivativeV.Z)) {
                if (failLines.Count < 2000) failLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "    EVS si {0} u {1} v {2} oracle pos ({3},{4},{5}) port ({6},{7},{8})",
                    si, t[2], t[3], t[5], t[6], t[7], Bits(p.Position.X), Bits(p.Position.Y), Bits(p.Position.Z)));
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- tessellation

        static bool RunTessCurve(string[] t)
        {
            long ci = long.Parse(t[1]);
            var opts = new UfbxTessellateCurveOpts { SpanSubdivision = int.Parse(t[2]) };
            var error = new UfbxError();
            bool wantOk = t[3] == "1";
            var line = UfbxGeometryApi.TessellateNurbsCurve(scene.Curves[(int)ci], opts, error);
            if (wantOk != (line != null)) return false;
            if (!wantOk) {
                return DescEq(t[4], error.Description);
            }
            pendLine = line;
            return true;
        }

        static bool ChkTessCurvePts(string[] t)
        {
            int n = int.Parse(t[1]);
            if (pendLine == null || n != pendLine.ControlPoints.Length) return false;
            if (t.Length != 2 + n * 3) return false;
            for (int i = 0; i < n; i++) {
                UfbxVec3 p = pendLine.ControlPoints[i];
                if (t[2 + i * 3] != Bits(p.X) || t[3 + i * 3] != Bits(p.Y) || t[4 + i * 3] != Bits(p.Z)) return false;
            }
            return true;
        }

        static bool ChkTessCurveIdx(string[] t)
        {
            int n = int.Parse(t[1]);
            if (pendLine == null || n != pendLine.PointIndices.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (uint.Parse(t[2 + i]) != pendLine.PointIndices[i]) return false;
            }
            return true;
        }

        static bool RunTessSurface(string[] t)
        {
            long si = long.Parse(t[1]);
            var opts = new UfbxTessellateSurfaceOpts {
                SpanSubdivisionU = int.Parse(t[2]),
                SpanSubdivisionV = int.Parse(t[3]),
                SkipMeshParts = t[4] == "1",
            };
            var error = new UfbxError();
            bool wantOk = t[5] == "1";
            var mesh = UfbxGeometryApi.TessellateNurbsSurface(scene.Surfaces[(int)si], opts, error);
            tessFaceIx = 0;
            tessPartIx = 0;
            if (wantOk != (mesh != null)) return false;
            if (!wantOk) {
                return DescEq(t[6], error.Description);
            }
            pendTess = mesh;
            return true;
        }

        static bool ChkTessVp(string[] t)
        {
            int n = int.Parse(t[1]);
            if (pendTess == null || n != pendTess.VertexPosition.Values.Length) return false;
            if (t.Length != 2 + n * 3) return false;
            UfbxVec3[] v = pendTess.VertexPosition.Values;
            for (int i = 0; i < n; i++) {
                if (t[2 + i * 3] != Bits(v[i].X) || t[3 + i * 3] != Bits(v[i].Y) || t[4 + i * 3] != Bits(v[i].Z)) return false;
            }
            return true;
        }

        static bool ChkTessVpi(string[] t)
        {
            int n = int.Parse(t[1]);
            if (pendTess == null || n != pendTess.VertexPosition.Indices.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (uint.Parse(t[2 + i]) != pendTess.VertexPosition.Indices[i]) return false;
            }
            return true;
        }

        static int tessFaceIx;
        static bool ChkTessFace(string[] t)
        {
            int fi = int.Parse(t[1]);
            if (pendTess == null || fi != tessFaceIx) return false;
            tessFaceIx++;
            if (fi >= pendTess.Faces.Length) return false;
            UfbxFace f = pendTess.Faces[fi];
            return uint.Parse(t[2]) == f.IndexBegin && uint.Parse(t[3]) == f.NumIndices;
        }

        static bool ChkTessUv(string[] t)
        {
            int n = int.Parse(t[1]);
            if (pendTess == null) return false;
            uint[] ix = pendTess.VertexUv.Indices;
            UfbxVec2[] uv = pendTess.VertexUv.Values;
            if (ix == null || uv == null || n != ix.Length) return false;
            if (t.Length != 2 + n * 2) return false;
            for (int i = 0; i < n; i++) {
                // C oracle quirk note: the C values.count overstates the buffer; both
                // sides consume through `.indices`.
                UfbxVec2 v = uv[ix[i]];
                if (t[2 + i * 2] != Bits(v.X) || t[3 + i * 2] != Bits(v.Y)) return false;
            }
            return true;
        }

        static bool ChkTessVn(string[] t)
        {
            int n = int.Parse(t[1]);
            if (pendTess == null) return false;
            UfbxVec3[] v = pendTess.VertexNormal.Values;
            if (v == null || n != v.Length) return false;
            if (t.Length != 2 + n * 3) return false;
            for (int i = 0; i < n; i++) {
                if (t[2 + i * 3] != Bits(v[i].X) || t[3 + i * 3] != Bits(v[i].Y) || t[4 + i * 3] != Bits(v[i].Z)) return false;
            }
            return true;
        }

        static bool ChkTessMp(string[] t)
        {
            if (pendTess == null) return false;
            int n = int.Parse(t[1]);
            int have = pendTess.MaterialParts != null ? pendTess.MaterialParts.Length : 0;
            return n == have;
        }

        static int tessPartIx;
        static bool ChkTessPt(string[] t)
        {
            if (pendTess == null) return false;
            int pi = int.Parse(t[1]);
            if (pi != tessPartIx) return false;
            tessPartIx++;
            if (pi >= pendTess.MaterialParts.Length) return false;
            UfbxMeshPart p = pendTess.MaterialParts[pi];
            if (int.Parse(t[2]) != p.NumFaces || int.Parse(t[3]) != p.NumTriangles
                || int.Parse(t[4]) != p.NumEmptyFaces || int.Parse(t[5]) != p.NumPointFaces
                || int.Parse(t[6]) != p.NumLineFaces) return false;
            if (t.Length != 7 + p.FaceIndices.Length) return false;
            for (int i = 0; i < p.FaceIndices.Length; i++) {
                if (uint.Parse(t[7 + i]) != p.FaceIndices[i]) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- subdivision

        static bool RunSubdivide(string[] t)
        {
            int meshIx = int.Parse(t[1]);
            int level = int.Parse(t[2]);
            var opts = new UfbxSubdivideOpts {
                InterpolateNormals = t[3] == "1",
                Boundary = (UfbxSubdivisionBoundary)int.Parse(t[4]),
                UvBoundary = (UfbxSubdivisionBoundary)int.Parse(t[5]),
            };
            UfbxMesh src = BuildMesh(scene.Meshes[meshIx]);
            subContext = "scene #" + sceneNo + " SUB mesh " + meshIx + " level " + level + " interp " + opts.InterpolateNormals
                + " boundary " + (int)opts.Boundary + "/" + (int)opts.UvBoundary;
            var error = new UfbxError();
            var sub = UfbxGeometryApi.SubdivideMesh(src, level, opts, error);
            subFaceIx = 0;
            subEdgeIx = 0;
            subPartIx = 0;
            subSeen.Clear();
            pendSubTopo = null;
            if (sub == null) {
                pendSubFail = true;
                pendSubFailDesc = error.Description;
                return true;    // SUBFAIL record does the comparing
            }
            pendSubFail = false;
            pendSub = sub;
            return true;
        }

        static bool ChkSubFail(string[] t)
        {
            if (!pendSubFail) return false;
            return DescEq(t[1], pendSubFailDesc);
        }

        static bool ChkSubVp(string[] t)
        {
            if (pendSubFail) return false;
            int n = int.Parse(t[1]);
            UfbxVec3[] v = pendSub.VertexPosition.Values;
            if (v == null || n != v.Length) {
                if (failLines.Count < 2000) failLines.Add("    SVP count oracle " + n + " port " + (v?.Length ?? -1));
                return false;
            }
            if (t.Length != 2 + n * 3) return false;
            for (int i = 0; i < n; i++) {
                if (t[2 + i * 3] != Bits(v[i].X) || t[3 + i * 3] != Bits(v[i].Y) || t[4 + i * 3] != Bits(v[i].Z)) {
                    if (failLines.Count < 2000) failLines.Add(string.Format(CultureInfo.InvariantCulture,
                        "    [" + subContext + "] SVP[{0} oracle ({1},{2},{3}) port ({4},{5},{6})",
                        i, t[2 + i * 3], t[3 + i * 3], t[4 + i * 3], Bits(v[i].X), Bits(v[i].Y), Bits(v[i].Z)));
                    return false;
                }
            }
            return true;
        }

        static bool ChkSubVpi(string[] t)
        {
            if (pendSubFail) return false;
            int n = int.Parse(t[1]);
            uint[] ix = pendSub.VertexPosition.Indices;
            if (ix == null || n != ix.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (uint.Parse(t[2 + i]) != ix[i]) return false;
            }
            return true;
        }

        static int subFaceIx, subEdgeIx, subPartIx;
        static bool ChkSubFace(string[] t)
        {
            if (pendSubFail) return false;
            int fi = int.Parse(t[1]);
            if (fi != subFaceIx) return false;
            subFaceIx++;
            if (fi >= pendSub.Faces.Length) return false;
            UfbxFace f = pendSub.Faces[fi];
            return uint.Parse(t[2]) == f.IndexBegin && uint.Parse(t[3]) == f.NumIndices;
        }

        static bool ChkSubIndices(string[] t, string kind)
        {
            if (pendSubFail) return false;
            uint[] want = kind == "SVTX" ? pendSub.VertexIndices : pendSub.VertexFirstIndex;
            int n = int.Parse(t[1]);
            // C: vertex_first_index.count = 0 (ufbx.c:29926); the port uses null.
            if (want == null) return n == 0;
            if (n != want.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (uint.Parse(t[2 + i]) != want[i]) return false;
            }
            return true;
        }

        // C: `printf("SED %u %u\n", ...)` -- no index field, implicit order.
        static bool ChkSubEdge(string[] t)
        {
            if (pendSubFail) return false;
            int i = subEdgeIx;
            subEdgeIx++;
            if (i >= pendSub.Edges.Length) return false;
            return uint.Parse(t[1]) == pendSub.Edges[i].A && uint.Parse(t[2]) == pendSub.Edges[i].B;
        }

        static bool ChkSubBools(string[] t, string kind)
        {
            if (pendSubFail) return false;
            subSeen.Add(kind);
            bool[] want =
                kind == "SES" ? pendSub.EdgeSmoothing :
                kind == "SEV" ? pendSub.EdgeVisibility :
                kind == "SFS" ? pendSub.FaceSmoothing :
                pendSub.FaceHole;
            int n = int.Parse(t[1]);
            if (want == null) return false;
            if (n != want.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if ((t[2 + i] == "1") != want[i]) return false;
            }
            return true;
        }

        static bool ChkSubReals(string[] t, string kind)
        {
            if (pendSubFail) return false;
            subSeen.Add(kind);
            double[] want = pendSub.EdgeCrease;
            int n = int.Parse(t[1]);
            if (want == null) return false;
            if (n != want.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (t[2 + i] != Bits(want[i])) return false;
            }
            return true;
        }

        static bool ChkSubUints(string[] t, string kind)
        {
            if (pendSubFail) return false;
            subSeen.Add(kind);
            uint[] want =
                kind == "SFM" ? pendSub.FaceMaterial :
                kind == "SGR" ? pendSub.FaceGroup :
                pendSub.MaterialPartUsageOrder;
            int n = int.Parse(t[1]);
            if (want == null) return n == 0;
            if (n != want.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (uint.Parse(t[2 + i]) != want[i]) return false;
            }
            return true;
        }

        static bool ChkSubVnv(string[] t)
        {
            if (pendSubFail) return false;
            subSeen.Add("SVNV");
            int n = int.Parse(t[1]);
            UfbxVec3[] v = pendSub.VertexNormal.Values;
            if (v == null || n != v.Length) {
                if (failLines.Count < 2000) failLines.Add("    SVNV count oracle " + n + " port " + (v?.Length ?? -1));
                return false;
            }
            if (t.Length != 2 + n * 3) return false;
            for (int i = 0; i < n; i++) {
                if (t[2 + i * 3] != Bits(v[i].X) || t[3 + i * 3] != Bits(v[i].Y) || t[4 + i * 3] != Bits(v[i].Z)) {
                    if (failLines.Count < 2000) failLines.Add(string.Format(CultureInfo.InvariantCulture,
                        "    [" + subContext + "] SVNV[{0} oracle ({1},{2},{3}) port ({4},{5},{6})",
                        i, t[2 + i * 3], t[3 + i * 3], t[4 + i * 3], Bits(v[i].X), Bits(v[i].Y), Bits(v[i].Z)));
                    return false;
                }
            }
            return true;
        }

        static bool ChkSubVni(string[] t)
        {
            if (pendSubFail) return false;
            subSeen.Add("SVNI");
            int n = int.Parse(t[1]);
            uint[] ix = pendSub.VertexNormal.Indices;
            if (ix == null || n != ix.Length) return false;
            if (t.Length != 2 + n) return false;
            for (int i = 0; i < n; i++) {
                if (uint.Parse(t[2 + i]) != ix[i]) return false;
            }
            return true;
        }

        static bool ChkSubMp(string[] t)
        {
            if (pendSubFail) return false;
            int n = int.Parse(t[1]);
            int have = pendSub.MaterialParts != null ? pendSub.MaterialParts.Length : 0;
            return n == have;
        }

        static bool ChkSubMpPart(string[] t)
        {
            if (pendSubFail) return false;
            int pi = int.Parse(t[1]);
            if (pi != subPartIx) return false;
            subPartIx++;
            if (pi >= pendSub.MaterialParts.Length) return false;
            UfbxMeshPart p = pendSub.MaterialParts[pi];
            if (int.Parse(t[2]) != p.NumFaces || int.Parse(t[3]) != p.NumTriangles
                || int.Parse(t[4]) != p.NumEmptyFaces || int.Parse(t[5]) != p.NumPointFaces
                || int.Parse(t[6]) != p.NumLineFaces) return false;
            if (t.Length != 7 + p.FaceIndices.Length) return false;
            for (int i = 0; i < p.FaceIndices.Length; i++) {
                if (uint.Parse(t[7 + i]) != p.FaceIndices[i]) return false;
            }
            return true;
        }

        static bool EndSubdivide()
        {
            if (pendSubFail) return false;
            // Presence symmetry: C dumps each optional list iff `.data != NULL`.
            if (pendSub.EdgeSmoothing != null != subSeen.Contains("SES")) return false;
            if (pendSub.EdgeVisibility != null != subSeen.Contains("SEV")) return false;
            if (pendSub.EdgeCrease != null != subSeen.Contains("SEC")) return false;
            if (pendSub.FaceSmoothing != null != subSeen.Contains("SFS")) return false;
            if (pendSub.FaceMaterial != null != subSeen.Contains("SFM")) return false;
            if (pendSub.FaceGroup != null != subSeen.Contains("SGR")) return false;
            if (pendSub.FaceHole != null != subSeen.Contains("SHL")) return false;
            if (pendSub.VertexNormal.Exists != subSeen.Contains("SVNV")) return false;
            // Face/edge/part counters must have been exhausted exactly.
            if (subFaceIx != pendSub.NumFaces) return false;
            if (pendSub.Edges != null && subEdgeIx != pendSub.Edges.Length) return false;
            if (pendSub.MaterialParts != null && subPartIx != pendSub.MaterialParts.Length) return false;
            pendSub = null;
            return true;
        }

        // ---------------------------------------------------------------- generate indices

        static bool DefGinCase(string[] t)
        {
            int ci = int.Parse(t[1]);
            int nstreams = int.Parse(t[2]);
            ginNumIndices = int.Parse(t[3]);
            ginStreams = new byte[nstreams][];
            for (int s = 0; s < nstreams; s++) {
                int vs = int.Parse(t[4 + s]);
                ginStreams[s] = new byte[vs * ginNumIndices];
            }
            return true;
        }

        static bool DefGinStream(string[] t)
        {
            int ci = int.Parse(t[1]);
            int s = int.Parse(t[2]);
            int nbytes = int.Parse(t[3]);
            if (ginStreams == null || s >= ginStreams.Length || nbytes != ginStreams[s].Length) return false;
            if (t.Length != 4 + nbytes) return false;
            byte[] d = ginStreams[s];
            for (int i = 0; i < nbytes; i++) d[i] = byte.Parse(t[4 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        static byte[][] ginOrig;
        static bool RunGin(string[] t)
        {
            int ci = int.Parse(t[1]);
            var streams = new UfbxVertexStream[ginStreams.Length];
            ginOrig = new byte[ginStreams.Length][];
            for (int s = 0; s < ginStreams.Length; s++) {
                ginOrig[s] = (byte[])ginStreams[s].Clone();
                streams[s].Data = ginStreams[s];
                streams[s].VertexCount = ginNumIndices;
                streams[s].VertexSize = ginStreams[s].Length / ginNumIndices;
            }
            uint[] indices = new uint[ginNumIndices];
            var error = new UfbxError();
            int numVertices = UfbxGeometryApi.GenerateIndices(streams, streams.Length, indices, ginNumIndices, error);

            bool fail = t[2] == "FAIL";
            if (fail) {
                if (numVertices != 0) return false;
                return DescEq(t[3], error.Description);
            }
            if (numVertices == 0) return false;
            int wantV = int.Parse(t[2]);
            if (wantV != numVertices) return false;
            if (t.Length != 3 + ginNumIndices) return false;
            for (int i = 0; i < ginNumIndices; i++) {
                if (uint.Parse(t[3 + i]) != indices[i]) return false;
            }
            return true;
        }

        static bool ChkGinOut(string[] t)
        {
            int ci = int.Parse(t[1]);
            int s = int.Parse(t[2]);
            int nbytes = int.Parse(t[3]);
            byte[] d = ginStreams[s];
            if (nbytes > d.Length) return false;
            if (t.Length != 4 + nbytes) return false;
            for (int i = 0; i < nbytes; i++) {
                if (byte.Parse(t[4 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture) != d[i]) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- helpers

        // The oracle hex-encodes descriptions; '-' means empty (C: length 0).
        static bool DescEq(string hex, string desc)
        {
            if (hex == "-") return string.IsNullOrEmpty(desc);
            if (hex.Length % 2 != 0) return false;
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) {
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            string want = System.Text.Encoding.UTF8.GetString(bytes);
            return want == desc;
        }

        static double D(string hex) {
            return BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }
        static string Bits(double d) {
            return BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
