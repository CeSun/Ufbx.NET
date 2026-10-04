// HashCheck: self-verification harness for UfbxHashScene (the C# port of
// test/hash_scene.h). Run with `dotnet run --project tools/HashCheck -c Release`.
//
// Three layers of verification, none of which needs the (not yet ported) loader:
//  1. Primitive byte-chain checks against an independent, hand-written FNV-1a
//     reference (written fresh from the C source of ufbxt_hash_data /
//     ufbxt_hash_string_imp / ufbxt_hash_pod_imp) over exact byte sequences.
//  2. A synthetic scene exercising every element type and the special value
//     paths (NaN canonicalization, -0.0, empty strings, zero-length blobs,
//     fixed-size arrays, prop references, DOM array branches).
//  3. Mutation checks: every single-field change on a hashed path must change
//     the scene hash; every change to a field the C reference SKIPS must NOT
//     change the scene hash (negative controls).
//
// NOTE: bit-exactness against the C `test/hash_scene.exe` for real files is
// only verifiable once SceneProvider.Load (the loader seam) is implemented;
// the synthetic-scene hash here is a self-consistency oracle, not a golden.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Ufbx;

namespace HashCheck
{
    static class Program
    {
        static int s_checks, s_passed, s_failed;
        static readonly List<string> s_failures = new List<string>();

        static void Check(string name, bool ok)
        {
            s_checks++;
            if (ok) s_passed++;
            else { s_failed++; s_failures.Add(name); }
        }

        // -- Independent reference FNV-1a-64 (written from the C source of
        // ufbxt_hash_data: state = (state ^ byte) * prime, initial constant)

        static ulong RefFnv(byte[] data)
        {
            ulong state = 0xcbf29ce484222325UL;
            for (int i = 0; i < data.Length; i++)
            {
                state = (state ^ (ulong)data[i]) * 0x00000100000001B3UL;
            }
            return state;
        }

        // Big-endian (MSB-first) bytes of a value: the order ufbxt_hash_pod_imp
        // produces on little-endian machines (reverse memory order).
        static byte[] Be(ulong value, int size)
        {
            byte[] b = new byte[size];
            for (int i = 0; i < size; i++)
            {
                b[size - 1 - i] = (byte)(value >> (8 * i));
            }
            return b;
        }

        static byte[] Concat(params byte[][] parts)
        {
            int len = 0;
            foreach (byte[] p in parts) len += p.Length;
            byte[] r = new byte[len];
            int off = 0;
            foreach (byte[] p in parts) { Array.Copy(p, 0, r, off, p.Length); off += p.Length; }
            return r;
        }

        static byte[] RealBytes(double d)
        {
            if (d != d) return Be(ulong.MaxValue, 8); // NaN canonicalization
            return Be(unchecked((ulong)BitConverter.DoubleToInt64Bits(d)), 8);
        }

        // ufbxt_hash_string_imp: size_t length + length + 1 bytes incl. NUL.
        // `text` here is the *display* form; C stores its UTF-8 bytes in `ufbx_string`.
        static ulong RefString(string text)
        {
            byte[] data = text == null ? new byte[0] : Encoding.UTF8.GetBytes(text);
            return RefFnv(Concat(Be((ulong)data.Length, 8), data, new byte[] { 0 }));
        }

        // The port's `ufbx_string` form of the same bytes: one char per byte
        // (UfbxiRawStr in Util/Utf8.cs, inlined because HashCheck excludes Util/).
        static string PortStr(string text)
        {
            byte[] data = text == null ? new byte[0] : Encoding.UTF8.GetBytes(text);
            char[] chars = new char[data.Length];
            for (int i = 0; i < data.Length; i++) chars[i] = (char)data[i];
            return new string(chars);
        }

        static ulong RefVec3(double x, double y, double z)
        {
            return RefFnv(Concat(RealBytes(x), RealBytes(y), RealBytes(z)));
        }

        // Apply an action to a fresh hasher and compare against `expected`.
        static void CheckState(string name, ulong expected, Action<UfbxHashScene> apply)
        {
            var h = new UfbxHashScene();
            apply(h);
            Check(name, h.State == expected);
        }

        // -- Scene mutation checks

        static void CheckChanges(string name, UfbxScene scene, Action apply, Action restore)
        {
            ulong before = UfbxHashScene.HashScene(scene);
            apply();
            ulong after = UfbxHashScene.HashScene(scene);
            restore();
            Check(name + " (changes hash)", before != after);
            Check(name + " (restore deterministic)", UfbxHashScene.HashScene(scene) == before);
        }

        static void CheckNoChange(string name, UfbxScene scene, Action apply, Action restore)
        {
            ulong before = UfbxHashScene.HashScene(scene);
            apply();
            ulong after = UfbxHashScene.HashScene(scene);
            restore();
            Check(name + " (skipped field leaves hash)", before == after);
        }

        // NOTE: .NET's `double.NaN` constant is already the NEGATIVE quiet NaN
        // (bits 0xfff8000000000000), so a differing payload needs the positive one.
        static double PosNaN() { return BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8000000000000UL)); }
        static float NegNaNF() { return BitConverter.Int32BitsToSingle(unchecked((int)0xFFC00001)); }

        // =====================================================================

        static void CheckPrimitives()
        {
            Check("initial state == C 0xcbf29ce484222325",
                new UfbxHashScene().State == UfbxHashScene.InitialState && UfbxHashScene.InitialState == 0xcbf29ce484222325UL);

            CheckState("zero-length data is a no-op", new UfbxHashScene().State,
                h => h.HashData(new byte[] { 1, 2, 3 }, 0, 0));

            CheckState("hash_string(\"\")", RefString(""), h => h.HashString(""));
            CheckState("hash_string(null) == hash_string(\"\")", RefString(""), h => h.HashString(null));
            CheckState("hash_string(\"a\")", RefString("a"), h => h.HashString("a"));
            CheckState("hash_string(\"ab\")", RefString("ab"), h => h.HashString("ab"));
            CheckState("hash_string(\"abc\")", RefString("abc"), h => h.HashString("abc"));
            // Non-ASCII: C hashes the UTF-8 bytes; the port holds them one char per byte,
            // so the hasher must feed those bytes verbatim (no re-encoding).
            CheckState("hash_string utf8 2-byte", RefString("ä"), h => h.HashString(PortStr("ä")));
            CheckState("hash_string high byte 0xff", RefFnv(Concat(Be(1, 8), new byte[] { 0xFF, 0 })),
                h => h.HashString("\u00ff"));

            CheckState("hash_pod uint32 0x11223344 -> 11 22 33 44",
                RefFnv(new byte[] { 0x11, 0x22, 0x33, 0x44 }), h => h.HashU32(0x11223344u));
            CheckState("hash_pod int32 -1 -> FF*4",
                RefFnv(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }), h => h.HashI32(-1));
            CheckState("hash_pod size_t 1 -> 8 bytes",
                RefFnv(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }), h => h.HashSizeT(1));
            CheckState("hash_pod uint64", RefFnv(Be(0x0102030405060708UL, 8)),
                h => h.HashU64(0x0102030405060708UL));
            CheckState("hash_bool true -> 01", RefFnv(new byte[] { 1 }), h => h.HashBool(true));
            CheckState("hash_bool false -> 00", RefFnv(new byte[] { 0 }), h => h.HashBool(false));

            CheckState("hash_float NaN -> FF FF FF FF", RefFnv(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }),
                h => h.HashFloat(float.NaN));
            float snanF = BitConverter.Int32BitsToSingle(unchecked((int)0x7F800001));
            CheckState("hash_float sNaN payload also -> FF FF FF FF", RefFnv(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }),
                h => h.HashFloat(snanF));
            CheckState("hash_float negative NaN", RefFnv(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }),
                h => h.HashFloat(NegNaNF()));
            CheckState("hash_float -0.0 -> 80 00 00 00", RefFnv(new byte[] { 0x80, 0, 0, 0 }),
                h => h.HashFloat(-0.0f));
            CheckState("hash_float +0.0 -> 00 00 00 00", RefFnv(new byte[] { 0, 0, 0, 0 }),
                h => h.HashFloat(0.0f));

            CheckState("hash_real NaN -> FF*8", RefFnv(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }),
                h => h.HashReal(double.NaN));
            CheckState("hash_real NaN payload-invariant", RefFnv(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }),
                h => h.HashReal(PosNaN()));
            CheckState("hash_real -0.0 -> 80 00*7", RefFnv(new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }),
                h => h.HashReal(-0.0));
            CheckState("hash_real +Inf -> 7F F0*6", RefFnv(new byte[] { 0x7F, 0xF0, 0, 0, 0, 0, 0, 0 }),
                h => h.HashReal(double.PositiveInfinity));
            {
                // -0.0 and NaN must hash differently (no accidental collapse)
                var a = new UfbxHashScene(); a.HashReal(-0.0);
                var b = new UfbxHashScene(); b.HashReal(double.NaN);
                Check("real -0.0 != real NaN state", a.State != b.State);
                // the FF*8 pod path (uint64 raw) and double-NaN canon agree on bytes
                var c = new UfbxHashScene(); c.HashU64(ulong.MaxValue);
                Check("real NaN canon == raw u64 FF*8", b.State == c.State);
            }

            CheckState("hash_blob empty == size_t 0 only", RefFnv(Be(0, 8)), h => h.HashBlob(new byte[0]));
            CheckState("hash_blob null == size_t 0 only", RefFnv(Be(0, 8)), h => h.HashBlob(null));
            CheckState("hash_blob {1,2,3}", RefFnv(Concat(Be(3, 8), new byte[] { 1, 2, 3 })),
                h => h.HashBlob(new byte[] { 1, 2, 3 }));

            CheckState("hash_vec3 (1,-0.0,NaN)", RefVec3(1.0, -0.0, double.NaN),
                h => h.HashVec3(new UfbxVec3(1.0, -0.0, double.NaN)));
        }

        static void CheckNoFieldData()
        {
            // "field-less data hash": hashing nothing at all leaves the C initial
            // constant (there is no field-less ufbx_scene traversal in C either --
            // even an empty scene hashes its embedded metadata/settings).
            ulong s = new UfbxHashScene().State;
            Check("no data hashed -> initial constant", s == 0xcbf29ce484222325UL);

            // An "empty" scene still traverses embedded structs; verify it runs
            // and is deterministic, without NaN-bearing inputs.
            var e = new UfbxScene();
            e.Anim = new UfbxAnim();
            ulong h1 = UfbxHashScene.HashScene(e);
            ulong h2 = UfbxHashScene.HashScene(e);
            Check("empty scene hash deterministic", h1 == h2);
            Check("empty scene hash != initial constant", h1 != 0xcbf29ce484222325UL);
        }

        // -- Synthetic scene

        static UfbxScene BuildSyntheticScene(
            out UfbxNode root, out UfbxNode child, out UfbxMesh mesh, out UfbxMaterial material,
            out UfbxTexture texture, out UfbxAnimCurve curve, out UfbxDomNode domRoot,
            out UfbxConnection connMeshNode, out UfbxLight light, out UfbxCamera camera,
            out UfbxVideo video, out UfbxBlendShape blendShape, out UfbxPose pose,
            out UfbxAnim anim, out UfbxAnimLayer layer, out UfbxAnimValue animValue)
        {
            var scene = new UfbxScene();

            // metadata
            scene.Metadata.Ascii = false;
            scene.Metadata.Version = 7400;
            scene.Metadata.Creator = "HashCheck Unit";
            scene.Metadata.ExporterVersion = 1200;
            scene.Metadata.SceneProps.Props = new[]
            {
                new UfbxProp
                {
                    Name = "Version", InternalKey = 1, Type = UfbxPropType.Integer,
                    ValueInt = 536879151, ValueVec4 = new UfbxVec4(536879151, 0, 0, 0),
                },
                new UfbxProp
                {
                    Name = "NaNProp", InternalKey = 2, Type = UfbxPropType.Number,
                    ValueVec4 = new UfbxVec4(double.NaN, -0.0, 3.5, 0.0), ValueStr = "",
                },
                new UfbxProp
                {
                    Name = "StrProp", InternalKey = 3, Type = UfbxPropType.String,
                    ValueStr = "abc", ValueBlob = new byte[] { 0xDE, 0xAD },
                },
            };
            scene.Metadata.OriginalApplication = new UfbxApplication { Vendor = "Kaydara", Name = "FBX SDK", Version = "2018.1" };
            scene.Metadata.LatestApplication = new UfbxApplication { Vendor = "Blender", Name = "Blender", Version = "2.92.0" };

            // settings
            scene.Settings.Axes = UfbxCoordinateAxes.RightHandedYUp;
            scene.Settings.UnitMeters = 1.0;
            scene.Settings.FramesPerSecond = 30.0;
            scene.Settings.AmbientColor = new UfbxVec3(0.1, -0.0, double.NaN);
            scene.Settings.TimeMode = UfbxTimeMode.Fps30;
            scene.Settings.OriginalAxisUp = UfbxCoordinateAxis.PositiveY;
            scene.Settings.OriginalUnitMeters = 0.01;
            scene.Settings.Props.Props = new[]
            {
                new UfbxProp { Name = "UpAxis", InternalKey = 4, Type = UfbxPropType.Integer, ValueInt = 2 },
            };

            // element ids
            uint id = 0;
            Func<uint> next = () => ++id;

            root = new UfbxNode { IsRoot = true, Visible = true, NodeDepth = 0 };
            root.Name = "RootNode"; root.ElementId = next(); root.TypedId = 0; root.Type = UfbxElementType.Node;

            child = new UfbxNode { Visible = true, NodeDepth = 1, AdjustPreScale = -0.0, AdjustPostScale = 1.0 };
            child.Name = "Cube"; child.ElementId = next(); child.TypedId = 1; child.Type = UfbxElementType.Node;
            child.Parent = root;
            child.LocalTransform = new UfbxTransform(new UfbxVec3(1, 2, 3), UfbxQuat.Identity, new UfbxVec3(1, 1, -0.0));
            child.NodeToParent = UfbxMatrix.Identity;
            child.NodeToWorld = UfbxMatrix.Identity;
            child.AdjustPostRotation = new UfbxQuat(0, 0, 0, 1);
            child.AdjustMirrorAxis = UfbxMirrorAxis.None;
            root.Children = new[] { child };

            mesh = new UfbxMesh
            {
                NumVertices = 3, NumIndices = 3, NumFaces = 1, NumTriangles = 1, NumEdges = 3,
                MaxFaceTriangles = 1,
                Faces = new[] { new UfbxFace { IndexBegin = 0, NumIndices = 3 } },
                FaceSmoothing = new[] { true },
                FaceMaterial = new uint[] { 0 },
                FaceGroup = new uint[] { 2 },
                FaceHole = new[] { false },
                Edges = new[]
                {
                    new UfbxEdge { A = 0, B = 1 }, new UfbxEdge { A = 1, B = 2 }, new UfbxEdge { A = 2, B = 0 },
                },
                EdgeSmoothing = new[] { true, true, false },
                EdgeCrease = new[] { double.NaN, -0.0, 1.5 },
                EdgeVisibility = new[] { true, true, true },
                VertexIndices = new uint[] { 0, 1, 2 },
                Vertices = new[]
                {
                    new UfbxVec3(0, 0, 0), new UfbxVec3(1, -0.0, double.NaN), new UfbxVec3(0, 1, 0),
                },
                VertexFirstIndex = new uint[] { 0, 1, 2 },
                VertexPosition = new UfbxVertexVec3
                {
                    Exists = true,
                    Values = new[] { new UfbxVec3(0, 0, 0), new UfbxVec3(1, -0.0, 0), new UfbxVec3(0, 1, 0) },
                    Indices = new uint[] { 0, 1, 2 },
                    ValueReals = 1,
                    UniquePerVertex = true,
                    ValuesW = new[] { double.NaN, 0.0 },
                },
                VertexNormal = new UfbxVertexVec3 { Exists = false },
                VertexCrease = new UfbxVertexReal
                {
                    Exists = true, Values = new[] { -0.0, 0.25, double.NaN },
                    Indices = new uint[] { 0, 1, 2 }, ValueReals = 1, UniquePerVertex = false,
                },
                UvSets = new[]
                {
                    new UfbxUvSet
                    {
                        Name = "map1", Index = 0,
                        VertexUv = new UfbxVertexVec2
                        {
                            Exists = true,
                            Values = new[] { new UfbxVec2(0, 0), new UfbxVec2(1, double.NaN), new UfbxVec2(-0.0, 1) },
                            Indices = new uint[] { 0, 1, 2 }, ValueReals = 1, UniquePerVertex = true,
                        },
                    },
                },
                ColorSets = new[]
                {
                    new UfbxColorSet
                    {
                        Name = "colorSet1", Index = 0,
                        VertexColor = new UfbxVertexVec4
                        {
                            Exists = true,
                            Values = new[] { new UfbxVec4(1, 0, 0, double.NaN) },
                            Indices = new uint[] { 0, 0, 0 }, ValueReals = 1, UniquePerVertex = false,
                        },
                    },
                },
                FaceGroups = new[] { new UfbxFaceGroup { Id = 2, Name = "grp" } },
                MaterialParts = new[]
                {
                    new UfbxMeshPart { Index = 0, NumFaces = 1, NumTriangles = 1, FaceIndices = new uint[] { 0 } },
                },
                FaceGroupParts = new[]
                {
                    new UfbxMeshPart { Index = 0, NumFaces = 1, NumTriangles = 1, FaceIndices = new uint[] { 0 } },
                },
                SubdivisionPreviewLevels = 1,
                SubdivisionRenderLevels = 2,
            };
            mesh.Name = "CubeMesh"; mesh.ElementId = next(); mesh.TypedId = 0; mesh.Type = UfbxElementType.Mesh;
            mesh.Instances = new[] { child };

            // material
            UfbxMaterial mat = new UfbxMaterial();
            material = mat;
            mat.Name = "MAT"; mat.ElementId = next(); mat.TypedId = 0; mat.Type = UfbxElementType.Material;
            mesh.Materials = new[] { mat };
            child.Mesh = mesh;
            mat.Fbx.DiffuseColor.ValueVec4 = new UfbxVec4(0.8, double.NaN, -0.0, 0.0);
            mat.Fbx.DiffuseColor.HasValue = true;
            mat.Fbx.DiffuseColor.ValueComponents = 3;
            mat.Pbr.BaseColor.ValueVec4 = new UfbxVec4(0.5, 0.5, 0.5, 1.0);
            mat.Pbr.BaseColor.HasValue = true;
            mat.Pbr.BaseColor.ValueComponents = 4;
            mat.Features.Pbr.Enabled = true;
            mat.Features.Pbr.IsExplicit = true;
            mat.ShaderType = UfbxShaderType.FbxLambert;
            mat.ShadingModelName = "Lambert";
            child.Materials = new[] { mat };

            // texture (+ shader texture path, empty strings, zero-length blobs)
            texture = new UfbxTexture
            {
                Type = UfbxTextureType.File,
                AbsoluteFilename = "C:/tex/a.png",
                RelativeFilename = "a.png",
                RawAbsoluteFilename = new byte[] { 0x43, 0x3A },
                RawRelativeFilename = new byte[0],           // zero-length blob
                Content = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                FileIndex = 0,
                HasFile = true,
                UvSet = "",                                  // empty string
                WrapU = UfbxWrapMode.Repeat,
                WrapV = UfbxWrapMode.Clamp,
                HasUvTransform = true,
                UvTransform = new UfbxTransform(UfbxVec3.Zero, UfbxQuat.Identity, new UfbxVec3(1, 1, double.NaN)),
            };
            texture.Name = "";                               // empty element name
            texture.ElementId = next(); texture.TypedId = 0; ((UfbxElement)texture).Type = UfbxElementType.Texture;
            texture.Instances = new[] { child };
            texture.FileTextures = new[] { texture };
            texture.Shader = new UfbxShaderTexture
            {
                Type = UfbxShaderTextureType.SelectOutput,
                ShaderName = "LayerTexture",
                ShaderTypeId = 0x6C736174UL,
                Inputs = new[]
                {
                    // input with NULL props (default struct => Name == null)
                    new UfbxShaderTextureInput { Name = "Top", ValueVec4 = new UfbxVec4(1, double.NaN, 0, 0), Texture = texture, TextureEnabled = true },
                    // input with all three prop references present
                    new UfbxShaderTextureInput
                    {
                        Name = "Bottom", ValueStr = "abc", ValueBlob = new byte[0], ValueInt = -1,
                        Prop = new UfbxProp { Name = "P1", InternalKey = 7, Type = UfbxPropType.Number },
                        TextureProp = new UfbxProp { Name = "P1", InternalKey = 7 },
                        TextureEnabledProp = new UfbxProp { Name = "P2", InternalKey = 8 },
                    },
                },
                ShaderSource = "shader src",
                RawShaderSource = new byte[0],
                MainTexture = texture,
                MainTextureOutputIndex = 0,
                PropPrefix = "",
            };
            mat.Textures = new[]
            {
                new UfbxMaterialTexture { MaterialProp = "DiffuseColor", ShaderProp = "", Texture = texture },
            };

            mat.Fbx.DiffuseColor.Texture = texture;
            mat.Fbx.DiffuseColor.TextureEnabled = true;

            // unknown
            var unknown = new UfbxUnknown { FbxType = "AnimationStack", SuperType = "", SubType = "AnimStack" };
            unknown.Name = "Take 001"; unknown.ElementId = next(); unknown.TypedId = 0; unknown.Type = UfbxElementType.Unknown;

            // light / camera / bone / empty
            light = new UfbxLight
            {
                Color = new UfbxVec3(1, -0.0, double.NaN), Intensity = 1.0,
                LocalDirection = new UfbxVec3(0, -1, 0), Type = UfbxLightType.Point,
                CastLight = true,
            };
            light.Name = "Light"; light.ElementId = next(); light.TypedId = 0; ((UfbxElement)light).Type = UfbxElementType.Light;

            camera = new UfbxCamera
            {
                ResolutionIsPixels = true, Resolution = new UfbxVec2(1920, 1080),
                FieldOfViewDeg = new UfbxVec2(39.3, double.NaN), FieldOfViewTan = new UfbxVec2(-0.0, 0.5),
                FocalLengthMm = 50.0, FilmSizeInch = new UfbxVec2(1.0, 0.75),
                ApertureSizeInch = new UfbxVec2(1.0, 0.75), SqueezeRatio = 1.0,
                // unhashed (negative control) fields:
                NearPlane = 0.1, FarPlane = 1000.0, ProjectionMode = UfbxProjectionMode.Perspective,
            };
            camera.Name = "Camera"; camera.ElementId = next(); camera.TypedId = 0; camera.Type = UfbxElementType.Camera;

            var bone = new UfbxBone { Radius = 1.5, RelativeLength = -0.0, IsRoot = true };
            bone.Name = "Bone"; bone.ElementId = next(); bone.TypedId = 0; bone.Type = UfbxElementType.Bone;
            var boneNode = new UfbxNode { Bone = bone, Visible = true, Parent = root, NodeDepth = 1 };
            boneNode.Name = "BoneNode"; boneNode.ElementId = next(); boneNode.TypedId = 2; boneNode.Type = UfbxElementType.Node;
            boneNode.NodeToParent = UfbxMatrix.Identity;
            root.Children = new[] { child, boneNode };

            var empty = new UfbxEmpty();
            empty.Name = "Locator"; empty.ElementId = next(); empty.TypedId = 0; empty.Type = UfbxElementType.Empty;

            var lineCurve = new UfbxLineCurve
            {
                Color = new UfbxVec3(0, -0.0, 1),
                ControlPoints = new[] { new UfbxVec3(0, 0, 0), new UfbxVec3(1, double.NaN, 0) },
                PointIndices = new uint[] { 0, 1 },
                Segments = new[] { new UfbxLineSegment { IndexBegin = 0, NumIndices = 2 } },
            };
            lineCurve.Name = "Line"; lineCurve.ElementId = next(); lineCurve.TypedId = 0; lineCurve.Type = UfbxElementType.LineCurve;

            var nurbsBasis = new UfbxNurbsBasis
            {
                Order = 3, Topology = UfbxNurbsTopology.Periodic,
                KnotVector = new[] { 0.0, -0.0, double.NaN, 1.0 }, TMin = 0.0, TMax = 1.0,
                Spans = new[] { 0.5 }, Is2D = false, NumWrapControlPoints = 2, Valid = true,
            };
            var nurbsCurve = new UfbxNurbsCurve { Basis = nurbsBasis, ControlPoints = new[] { new UfbxVec4(0, 1, double.NaN, -0.0) } };
            nurbsCurve.Name = "NCurve"; nurbsCurve.ElementId = next(); nurbsCurve.TypedId = 0; nurbsCurve.Type = UfbxElementType.NurbsCurve;

            var nurbsSurface = new UfbxNurbsSurface
            {
                BasisU = nurbsBasis, BasisV = new UfbxNurbsBasis { Order = 2, KnotVector = new double[0] },
                NumControlPointsU = 1, NumControlPointsV = 1,
                ControlPoints = new[] { new UfbxVec4(1, 2, 3, 4) },
                SpanSubdivisionU = 4, SpanSubdivisionV = 8,
                Material = mat,
            };
            nurbsSurface.Name = "NSurf"; nurbsSurface.ElementId = next(); nurbsSurface.TypedId = 0; nurbsSurface.Type = UfbxElementType.NurbsSurface;

            var marker = new UfbxMarker { Type = UfbxMarkerType.IkEffector };
            marker.Name = "Marker"; marker.ElementId = next(); marker.TypedId = 0; ((UfbxElement)marker).Type = UfbxElementType.Marker;

            var lodGroup = new UfbxLodGroup
            {
                RelativeDistances = true,
                LodLevels = new[]
                {
                    new UfbxLodLevel { Distance = 1.0, Display = UfbxLodDisplay.UseLod },
                    new UfbxLodLevel { Distance = double.NaN, Display = UfbxLodDisplay.Hide },
                },
                UseDistanceLimit = true, DistanceLimitMin = -0.0, DistanceLimitMax = 100.0,
            };
            lodGroup.Name = "LOD"; lodGroup.ElementId = next(); lodGroup.TypedId = 0; lodGroup.Type = UfbxElementType.LodGroup;

            // deformers
            var skin = new UfbxSkinDeformer
            {
                Clusters = new UfbxSkinCluster[0],
                Vertices = new[] { new UfbxSkinVertex { WeightBegin = 0, NumWeights = 1, DqWeight = -0.0 } },
                Weights = new[] { new UfbxSkinWeight { ClusterIndex = 0, Weight = double.NaN } },
                MaxWeightsPerVertex = 1,
            };
            skin.Name = "Skin"; skin.ElementId = next(); skin.TypedId = 0; skin.Type = UfbxElementType.SkinDeformer;
            var cluster = new UfbxSkinCluster
            {
                BoneNode = boneNode, GeometryToBone = UfbxMatrix.Identity,
                NumWeights = 2, Vertices = new uint[] { 0, 1 }, Weights = new[] { 1.0, double.NaN },
            };
            cluster.Name = "Cluster"; cluster.ElementId = next(); cluster.TypedId = 0; cluster.Type = UfbxElementType.SkinCluster;
            skin.Clusters = new[] { cluster };
            mesh.SkinDeformers = new[] { skin };
            mesh.AllDeformers = new UfbxElement[] { skin };
            boneNode.AllAttribs = new UfbxElement[] { skin };
            boneNode.AttribType = UfbxElementType.SkinDeformer;

            blendShape = new UfbxBlendShape
            {
                NumOffsets = 1, OffsetVertices = new uint[] { 1 },
                PositionOffsets = new[] { new UfbxVec3(double.NaN, -0.0, 0.5) },
                NormalOffsets = new UfbxVec3[0],
                OffsetWeights = new[] { 1.0 }, // NOT hashed by the C reference
            };
            blendShape.Name = "Shape"; blendShape.ElementId = next(); blendShape.TypedId = 0; blendShape.Type = UfbxElementType.BlendShape;
            var blendChannel = new UfbxBlendChannel
            {
                Weight = -0.0,
                Keyframes = new[] { new UfbxBlendKeyframe { Shape = blendShape, TargetWeight = 1.0, EffectiveWeight = double.NaN } },
                TargetShape = blendShape,
            };
            blendChannel.Name = "Channel"; blendChannel.ElementId = next(); blendChannel.TypedId = 0; blendChannel.Type = UfbxElementType.BlendChannel;
            var blendDeformer = new UfbxBlendDeformer { Channels = new[] { blendChannel } };
            blendDeformer.Name = "Blend"; blendDeformer.ElementId = next(); blendDeformer.TypedId = 0; blendDeformer.Type = UfbxElementType.BlendDeformer;
            mesh.BlendDeformers = new[] { blendDeformer };

            // geometry cache
            var cacheFrame = new UfbxCacheFrame
            {
                Channel = "geom", Time = 1.5, FileFormat = UfbxCacheFileFormat.Pc2,
                DataFormat = UfbxCacheDataFormat.RealFloat, DataEncoding = UfbxCacheDataEncoding.LittleEndian,
                DataOffset = 44, DataCount = 3, DataElementBytes = 12, DataTotalBytes = 36,
                Filename = "unhashed.pc2", MirrorAxis = UfbxMirrorAxis.X, ScaleFactor = 2.0,
            };
            var cacheChannel = new UfbxCacheChannel
            {
                Name = "geom", Interpretation = UfbxCacheInterpretation.Points,
                Frames = new[] { cacheFrame },
            };
            var cacheFile = new UfbxCacheFile
            {
                AbsoluteFilename = "C:/cache/geom.pc2", RelativeFilename = "geom.pc2",
                RawAbsoluteFilename = new byte[0], RawRelativeFilename = new byte[0],
                Filename = "unhashed", // NOT hashed
                ExternalCache = new UfbxGeometryCache
                {
                    Channels = new[] { cacheChannel }, Frames = new[] { cacheFrame },
                    ExtraInfo = new[] { "", "info" },
                },
            };
            cacheFile.Name = "CacheFile"; cacheFile.ElementId = next(); cacheFile.TypedId = 0; cacheFile.Type = UfbxElementType.CacheFile;
            var cacheDeformer = new UfbxCacheDeformer { Channel = "geom", File = cacheFile };
            cacheDeformer.Name = "CacheDeformer"; cacheDeformer.ElementId = next(); cacheDeformer.TypedId = 0; cacheDeformer.Type = UfbxElementType.CacheDeformer;
            mesh.CacheDeformers = new[] { cacheDeformer };

            // video / shader / shader binding
            video = new UfbxVideo
            {
                AbsoluteFilename = "C:/v.mkv", RelativeFilename = "v.mkv",
                RawAbsoluteFilename = new byte[0], RawRelativeFilename = new byte[0],
                Content = new byte[0],
                Filename = "unhashed.mkv", // NOT hashed
            };
            video.Name = "Video"; video.ElementId = next(); video.TypedId = 0; video.Type = UfbxElementType.Video;

            var shaderBinding = new UfbxShaderBinding
            {
                PropBindings = new[] { new UfbxShaderPropBinding { ShaderProp = "diffuse_color", MaterialProp = "DiffuseColor" } },
            };
            shaderBinding.Name = "SB_Lambert"; shaderBinding.ElementId = next(); shaderBinding.TypedId = 0; shaderBinding.Type = UfbxElementType.ShaderBinding;
            var shader = new UfbxShader { Type = UfbxShaderType.FbxLambert, Bindings = new[] { shaderBinding } };
            shader.Name = "SH_Lambert"; shader.ElementId = next(); shader.TypedId = 0; ((UfbxElement)shader).Type = UfbxElementType.Shader;

            // animation
            anim = new UfbxAnim
            {
                TimeBegin = 0.0, TimeEnd = 2.0,
                Layers = new UfbxAnimLayer[0],
                OverrideLayerWeights = new double[0], // raw-bit hashed (no NaN canon)
            };

            curve = new UfbxAnimCurve
            {
                Keyframes = new[]
                {
                    new UfbxKeyframe
                    {
                        Time = 0.0, Value = 1.0, Interpolation = UfbxInterpolation.Linear,
                        Left = new UfbxTangent { Dx = 1.0f, Dy = -0.0f },
                        Right = new UfbxTangent { Dx = float.NaN, Dy = 2.5f },
                    },
                    new UfbxKeyframe
                    {
                        Time = double.NaN, Value = -0.0, Interpolation = UfbxInterpolation.Cubic,
                        Left = new UfbxTangent { Dx = -0.0f, Dy = float.NaN },
                        Right = new UfbxTangent(),
                    },
                },
                MinValue = double.NaN, MaxValue = -0.0,
                MinTime = 0.0, MaxTime = 1.0, // NOT hashed
                PreExtrapolation = new UfbxExtrapolation { Mode = UfbxExtrapolationMode.Constant },
            };
            curve.Name = "CurveX"; curve.ElementId = next(); curve.TypedId = 0; curve.Type = UfbxElementType.AnimCurve;

            animValue = new UfbxAnimValue
            {
                DefaultValue = new UfbxVec3(double.NaN, -0.0, 0.5),
                Curves = new[] { curve, null, null }, // fixed curves[3]
            };
            animValue.Name = "Lcl Translation"; animValue.ElementId = next(); animValue.TypedId = 0; animValue.Type = UfbxElementType.AnimValue;

            layer = new UfbxAnimLayer
            {
                Weight = 1.0, Blended = true,
                AnimValues = new[] { animValue },
                AnimProps = new[]
                {
                    new UfbxAnimProp { Element = child, InternalKey = 9, PropName = "Lcl Translation", AnimValue = animValue },
                },
                Anim = new UfbxAnim { TimeBegin = -0.0, TimeEnd = double.NaN },
                MinElementId = 2, MaxElementId = 2,
                ElementIdBitmask = new uint[] { 0, 2, 0, 0 }, // fixed [4]
            };
            layer.Name = "Layer"; layer.ElementId = next(); layer.TypedId = 0; layer.Type = UfbxElementType.AnimLayer;

            var stack = new UfbxAnimStack
            {
                TimeBegin = 0.0, TimeEnd = 2.0, Layers = new[] { layer },
                Anim = new UfbxAnim
                {
                    TimeBegin = 0.0, TimeEnd = 2.0,
                    Layers = new[] { layer },
                    OverrideLayerWeights = new[] { double.NaN }, // raw bits
                    PropOverrides = new[]
                    {
                        new UfbxPropOverride
                        {
                            ElementId = child.ElementId, InternalKey = 9, PropName = "Lcl Translation",
                            Value = new UfbxVec4(1, double.NaN, -0.0, 0), ValueStr = "", ValueInt = -1,
                        },
                    },
                    TransformOverrides = new[]
                    {
                        new UfbxTransformOverride { NodeId = child.ElementId, Transform = child.LocalTransform },
                    },
                },
            };
            stack.Name = "Take 001"; stack.ElementId = next(); stack.TypedId = 0; stack.Type = UfbxElementType.AnimStack;
            anim.Layers = new[] { layer };
            anim.OverrideLayerWeights = new[] { double.NaN, -0.0 };

            // misc collections
            var displayLayer = new UfbxDisplayLayer
            {
                Nodes = new[] { child }, Visible = true, Frozen = false,
                UiColor = new UfbxVec3(-0.0, 0.5, double.NaN),
            };
            displayLayer.Name = "Layer 1"; displayLayer.ElementId = next(); displayLayer.TypedId = 0; displayLayer.Type = UfbxElementType.DisplayLayer;

            var selectionNode = new UfbxSelectionNode
            {
                TargetNode = child, TargetMesh = mesh, IncludeNode = true,
                Vertices = new uint[] { 0 }, Edges = new uint[0], Faces = new uint[] { 0 },
            };
            selectionNode.Name = "SelectionNode"; selectionNode.ElementId = next(); selectionNode.TypedId = 0; selectionNode.Type = UfbxElementType.SelectionNode;
            var selectionSet = new UfbxSelectionSet { Nodes = new[] { selectionNode } };
            selectionSet.Name = "Select"; selectionSet.ElementId = next(); selectionSet.TypedId = 0; selectionSet.Type = UfbxElementType.SelectionSet;

            var character = new UfbxCharacter();
            character.Name = "Character"; character.ElementId = next(); character.TypedId = 0; character.Type = UfbxElementType.Character;

            var constraint = new UfbxConstraint
            {
                Type = UfbxConstraintType.Aim, TypeName = "AimConstraint",
                Node = child,
                Targets = new[] { new UfbxConstraintTarget { Node = boneNode, Weight = 1.0, Transform = child.LocalTransform } },
                Weight = double.NaN, Active = true,
                ConstrainTranslation = new[] { true, false, true }, // fixed bool[3]
                ConstrainRotation = new[] { false, false, false },
                ConstrainScale = new[] { true, true, true },
                AimVector = new UfbxVec3(0, -0.0, 1), AimUpType = UfbxConstraintAimUpType.Vector,
                AimUpVector = new UfbxVec3(0, 1, double.NaN),
            };
            constraint.Name = "Aim"; constraint.ElementId = next(); constraint.TypedId = 0; ((UfbxElement)constraint).Type = UfbxElementType.Constraint;

            var audioClip = new UfbxAudioClip
            {
                AbsoluteFilename = "C:/a.wav", RelativeFilename = "a.wav",
                RawAbsoluteFilename = new byte[0], RawRelativeFilename = new byte[0],
                Content = new byte[] { 0x52, 0x49 },
                Filename = "unhashed.wav", // NOT hashed
            };
            audioClip.Name = "Audio"; audioClip.ElementId = next(); audioClip.TypedId = 0; audioClip.Type = UfbxElementType.AudioClip;
            var audioLayer = new UfbxAudioLayer { Clips = new[] { audioClip } };
            audioLayer.Name = "AudioLayer"; audioLayer.ElementId = next(); audioLayer.TypedId = 0; audioLayer.Type = UfbxElementType.AudioLayer;

            pose = new UfbxPose
            {
                IsBindPose = true,
                BonePoses = new[]
                {
                    new UfbxBonePose
                    {
                        BoneNode = boneNode, BoneToWorld = UfbxMatrix.Identity,
                        BoneToParent = UfbxMatrix.Identity, // NOT hashed
                    },
                },
            };
            pose.Name = "BindPose"; pose.ElementId = next(); pose.TypedId = 0; pose.Type = UfbxElementType.Pose;
            boneNode.BindPose = pose; // NOT hashed either

            var metadataObject = new UfbxMetadataObject();
            metadataObject.Name = "Metadata"; metadataObject.ElementId = next(); metadataObject.TypedId = 0; metadataObject.Type = UfbxElementType.MetadataObject;

            var stereoCamera = new UfbxStereoCamera { Left = camera, Right = camera };
            stereoCamera.Name = "Stereo"; stereoCamera.ElementId = next(); stereoCamera.TypedId = 0; stereoCamera.Type = UfbxElementType.StereoCamera;
            var cameraSwitcher = new UfbxCameraSwitcher();
            cameraSwitcher.Name = "Switcher"; cameraSwitcher.ElementId = next(); cameraSwitcher.TypedId = 0; cameraSwitcher.Type = UfbxElementType.CameraSwitcher;
            var procGeom = new UfbxProceduralGeometry();
            procGeom.Name = "Proc"; procGeom.ElementId = next(); procGeom.TypedId = 0; procGeom.Type = UfbxElementType.ProceduralGeometry;
            var trimSurf = new UfbxNurbsTrimSurface();
            trimSurf.Name = "Trim"; trimSurf.ElementId = next(); trimSurf.TypedId = 0; trimSurf.Type = UfbxElementType.NurbsTrimSurface;
            var trimBound = new UfbxNurbsTrimBoundary();
            trimBound.Name = "TrimB"; trimBound.ElementId = next(); trimBound.TypedId = 0; trimBound.Type = UfbxElementType.NurbsTrimBoundary;

            // connections (shared identity objects; hashed in element headers AND scene lists)
            connMeshNode = new UfbxConnection { Src = mesh, Dst = child, SrcProp = "", DstProp = null };
            child.ConnectionsSrc = new[] { connMeshNode };
            mesh.ConnectionsDst = new[] { connMeshNode };
            var connTexMat = new UfbxConnection { Src = texture, Dst = mat, SrcProp = "FileName", DstProp = "DiffuseColor" };
            texture.ConnectionsDst = new[] { connTexMat };
            mat.ConnectionsSrc = new[] { connTexMat };
            scene.ConnectionsSrc = new[] { connMeshNode, connTexMat };
            scene.ConnectionsDst = new[] { connMeshNode, connTexMat };

            // texture_files
            scene.TextureFiles = new[]
            {
                new UfbxTextureFile
                {
                    Index = 0, AbsoluteFilename = "C:/tex/a.png", RelativeFilename = "a.png",
                    RawAbsoluteFilename = new byte[0], RawRelativeFilename = new byte[0],
                    Content = new byte[] { 0x89, 0x50 },
                },
            };

            // DOM tree: Number / String / ArrayF64 (NaN) / ArrayBlob records / Blob
            byte[] f64 = new byte[16];
            BinaryPrimitives.WriteDoubleLittleEndian(new Span<byte>(f64, 0, 8), 1.0);
            BinaryPrimitives.WriteDoubleLittleEndian(new Span<byte>(f64, 8, 8), double.NaN);
            byte[] blobRec = new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0xAA, 0xBB, 0xCC }; // [8-byte LE size][payload]
            domRoot = new UfbxDomNode
            {
                Name = "FbxFile",
                Children = new[]
                {
                    new UfbxDomNode
                    {
                        Name = "FileContent",
                        Values = new[]
                        {
                            new UfbxDomValue { Type = UfbxDomValueType.Number, ValueStr = "123", ValueInt = 123, ValueFloat = double.NaN },
                            new UfbxDomValue { Type = UfbxDomValueType.String, ValueStr = "abc", ValueFloat = -0.0 },
                        },
                    },
                },
                Values = new[]
                {
                    new UfbxDomValue { Type = UfbxDomValueType.Number, ValueStr = "", ValueInt = 7400, ValueFloat = 7400.0 },
                    new UfbxDomValue { Type = UfbxDomValueType.ArrayF64, ValueStr = "", ValueBlob = f64, ValueInt = 2 },
                    new UfbxDomValue { Type = UfbxDomValueType.ArrayBlob, ValueStr = "", ValueBlob = blobRec, ValueInt = 1 },
                    new UfbxDomValue { Type = UfbxDomValueType.Blob, ValueStr = "", ValueBlob = new byte[0], ValueInt = 0 },
                },
            };
            mesh.DomNode = domRoot.Children[0]; // not hashed; loader-style wiring only

            // scene wiring
            scene.RootNode = root;
            scene.Anim = anim;

            scene.Elements = new UfbxElement[]
            {
                root, child, boneNode, mesh, light, camera, bone, empty, lineCurve, nurbsCurve,
                nurbsSurface, trimSurf, trimBound, procGeom, stereoCamera, cameraSwitcher, marker,
                lodGroup, skin, cluster, blendDeformer, blendChannel, blendShape, cacheDeformer,
                cacheFile, mat, texture, video, shader, shaderBinding, stack, layer, animValue,
                curve, displayLayer, selectionSet, selectionNode, character, constraint,
                audioLayer, audioClip, pose, metadataObject, unknown,
            };

            scene.Unknowns = new[] { unknown };
            scene.Nodes = new[] { root, child, boneNode };
            scene.Meshes = new[] { mesh };
            scene.Lights = new[] { light };
            scene.Cameras = new[] { camera };
            scene.Bones = new[] { bone };
            scene.Empties = new[] { empty };
            scene.LineCurves = new[] { lineCurve };
            scene.NurbsCurves = new[] { nurbsCurve };
            scene.NurbsSurfaces = new[] { nurbsSurface };
            scene.NurbsTrimSurfaces = new[] { trimSurf };
            scene.NurbsTrimBoundaries = new[] { trimBound };
            scene.ProceduralGeometries = new[] { procGeom };
            scene.StereoCameras = new[] { stereoCamera };
            scene.CameraSwitchers = new[] { cameraSwitcher };
            scene.Markers = new[] { marker };
            scene.LodGroups = new[] { lodGroup };
            scene.SkinDeformers = new[] { skin };
            scene.SkinClusters = new[] { cluster };
            scene.BlendDeformers = new[] { blendDeformer };
            scene.BlendChannels = new[] { blendChannel };
            scene.BlendShapes = new[] { blendShape };
            scene.CacheDeformers = new[] { cacheDeformer };
            scene.CacheFiles = new[] { cacheFile };
            scene.Materials = new[] { mat };
            scene.Textures = new[] { texture };
            scene.Videos = new[] { video };
            scene.Shaders = new[] { shader };
            scene.ShaderBindings = new[] { shaderBinding };
            scene.AnimStacks = new[] { stack };
            scene.AnimLayers = new[] { layer };
            scene.AnimValues = new[] { animValue };
            scene.AnimCurves = new[] { curve };
            scene.DisplayLayers = new[] { displayLayer };
            scene.SelectionSets = new[] { selectionSet };
            scene.SelectionNodes = new[] { selectionNode };
            scene.Characters = new[] { character };
            scene.Constraints = new[] { constraint };
            scene.AudioLayers = new[] { audioLayer };
            scene.AudioClips = new[] { audioClip };
            scene.Poses = new[] { pose };
            scene.MetadataObjects = new[] { metadataObject };

            scene.ElementsByName = new[]
            {
                new UfbxNameElement { Name = "RootNode", Type = UfbxElementType.Node, InternalKey = 101, Element = root },
                new UfbxNameElement { Name = "Cube", Type = UfbxElementType.Node, InternalKey = 102, Element = child },
                new UfbxNameElement { Name = "MAT", Type = UfbxElementType.Material, InternalKey = 104, Element = mat },
                new UfbxNameElement { Name = "", Type = UfbxElementType.Texture, InternalKey = 105, Element = texture },
            };

            scene.DomRoot = domRoot;
            return scene;
        }

        static void CheckScene(UfbxScene scene)
        {
            ulong h1 = UfbxHashScene.HashScene(scene);
            ulong h2 = UfbxHashScene.HashScene(scene);
            Check("synthetic scene hash deterministic", h1 == h2);

            // --- must change: each touches exactly one hashed model field ---

            CheckChanges("element name", scene,
                () => { ((UfbxNode)scene.Elements[1]).Name = "CubeX"; },
                () => { ((UfbxNode)scene.Elements[1]).Name = "Cube"; });

            CheckChanges("mesh vertex -0.0 -> +0.0", scene,
                () => { var m = scene.Meshes[0]; var v = m.Vertices[1]; v.Y = 0.0; m.Vertices[1] = v; },
                () => { var m = scene.Meshes[0]; var v = m.Vertices[1]; v.Y = -0.0; m.Vertices[1] = v; });

            CheckChanges("edge_crease -0.0 -> +0.0", scene,
                () => { scene.Meshes[0].EdgeCrease[1] = 0.0; },
                () => { scene.Meshes[0].EdgeCrease[1] = -0.0; });

            CheckChanges("anim override_layer_weights NaN payload", scene,
                () => { scene.Anim.OverrideLayerWeights[0] = PosNaN(); },
                () => { scene.Anim.OverrideLayerWeights[0] = double.NaN; });

            CheckChanges("material map ValueComponents", scene,
                () => { scene.Materials[0].Fbx.DiffuseColor.ValueComponents = 4; },
                () => { scene.Materials[0].Fbx.DiffuseColor.ValueComponents = 3; });

            CheckChanges("scene.Elements order swap", scene,
                () => { var e = scene.Elements; var t = e[0]; e[0] = e[1]; e[1] = t; },
                () => { var e = scene.Elements; var t = e[0]; e[0] = e[1]; e[1] = t; });

            CheckChanges("connection src_prop \"\" -> \"X\"", scene,
                () => { scene.ConnectionsSrc[0].SrcProp = "X"; },
                () => { scene.ConnectionsSrc[0].SrcProp = ""; });

            CheckChanges("dom string value", scene,
                () => { var v = scene.DomRoot.Children[0].Values[1]; v.ValueStr = "abd"; scene.DomRoot.Children[0].Values[1] = v; },
                () => { var v = scene.DomRoot.Children[0].Values[1]; v.ValueStr = "abc"; scene.DomRoot.Children[0].Values[1] = v; });

            CheckChanges("settings frames_per_second", scene,
                () => { scene.Settings.FramesPerSecond = 24.0; },
                () => { scene.Settings.FramesPerSecond = 30.0; });

            CheckChanges("metadata has_warning[0]", scene,
                () => { scene.Metadata.HasWarning[0] = true; },
                () => { scene.Metadata.HasWarning[0] = false; });

            CheckChanges("node adjust_mirror_axis (enum hashed as real)", scene,
                () => { scene.Nodes[1].AdjustMirrorAxis = UfbxMirrorAxis.X; },
                () => { scene.Nodes[1].AdjustMirrorAxis = UfbxMirrorAxis.None; });

            CheckChanges("keyframe tangent float", scene,
                () => { var c = scene.AnimCurves[0]; var k = c.Keyframes[0]; var t = k.Left; t.Dx = 2.0f; k.Left = t; c.Keyframes[0] = k; },
                () => { var c = scene.AnimCurves[0]; var k = c.Keyframes[0]; var t = k.Left; t.Dx = 1.0f; k.Left = t; c.Keyframes[0] = k; });

            CheckChanges("texture content blob", scene,
                () => { scene.Textures[0].Content = new byte[0]; },
                () => { scene.Textures[0].Content = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; });

            CheckChanges("anim value curves[1] ref", scene,
                () => { scene.AnimValues[0].Curves[1] = scene.AnimCurves[0]; },
                () => { scene.AnimValues[0].Curves[1] = null; });

            CheckChanges("constraint bool array element", scene,
                () => { scene.Constraints[0].ConstrainRotation[0] = true; },
                () => { scene.Constraints[0].ConstrainRotation[0] = false; });

            CheckChanges("typed list gains a node", scene,
                () => { scene.Nodes = new[] { scene.Nodes[0], scene.Nodes[1], scene.Nodes[2], scene.Nodes[1] }; },
                () => { scene.Nodes = new[] { scene.Nodes[0], scene.Nodes[1], scene.Nodes[2] }; });

            CheckChanges("elements_by_name internal key", scene,
                () => { var ne = scene.ElementsByName[0]; ne.InternalKey = 999; scene.ElementsByName[0] = ne; },
                () => { var ne = scene.ElementsByName[0]; ne.InternalKey = 101; scene.ElementsByName[0] = ne; });

            // --- must NOT change: fields the C reference skips ---

            CheckNoChange("mesh reversed_winding (skipped)", scene,
                () => { scene.Meshes[0].ReversedWinding = true; },
                () => { scene.Meshes[0].ReversedWinding = false; });

            CheckNoChange("material map feature_disabled (skipped)", scene,
                () => { scene.Materials[0].Fbx.DiffuseColor.FeatureDisabled = true; },
                () => { scene.Materials[0].Fbx.DiffuseColor.FeatureDisabled = false; });

            CheckNoChange("node use_rotation_space / bind_pose (skipped)", scene,
                () => { scene.Nodes[1].UseRotationSpace = true; },
                () => { scene.Nodes[1].UseRotationSpace = false; });

            CheckNoChange("camera near_plane (skipped)", scene,
                () => { scene.Cameras[0].NearPlane = 42.0; },
                () => { scene.Cameras[0].NearPlane = 0.1; });

            CheckNoChange("anim curve min/max time (skipped)", scene,
                () => { scene.AnimCurves[0].MinTime = 7.0; },
                () => { scene.AnimCurves[0].MinTime = 0.0; });

            CheckNoChange("blend shape offset_weights (skipped)", scene,
                () => { scene.BlendShapes[0].OffsetWeights = new[] { 2.0, 3.0 }; },
                () => { scene.BlendShapes[0].OffsetWeights = new[] { 1.0 }; });

            CheckNoChange("video filename (only abs/rel hashed)", scene,
                () => { scene.Videos[0].Filename = "other.mkv"; },
                () => { scene.Videos[0].Filename = "unhashed.mkv"; });

            CheckNoChange("bone_pose bone_to_parent (skipped)", scene,
                () => { scene.Poses[0].BonePoses[0].BoneToParent = new UfbxMatrix { M00 = 2.0 }; },
                () => { scene.Poses[0].BonePoses[0].BoneToParent = UfbxMatrix.Identity; });

            CheckNoChange("cache frame filename/scale (skipped)", scene,
                () =>
                {
                    var cf = scene.CacheFiles[0].ExternalCache.Frames[0];
                    cf.Filename = "z.pc2"; cf.ScaleFactor = 9.0; cf.MirrorAxis = UfbxMirrorAxis.Z;
                },
                () =>
                {
                    var cf = scene.CacheFiles[0].ExternalCache.Frames[0];
                    cf.Filename = "unhashed.pc2"; cf.ScaleFactor = 2.0; cf.MirrorAxis = UfbxMirrorAxis.X;
                });

            // --- NaN canonicalization invariants inside the scene ---

            {
                ulong before = UfbxHashScene.HashScene(scene);
                scene.Meshes[0].EdgeCrease[0] = PosNaN(); // different payload, same canonicalization
                ulong after = UfbxHashScene.HashScene(scene);
                scene.Meshes[0].EdgeCrease[0] = double.NaN;
                Check("edge_crease NaN payload canonicalized (no change)", before == after);
                Check("edge_crease restore deterministic", UfbxHashScene.HashScene(scene) == before);
            }
        }

        static int Main()
        {
            CheckPrimitives();
            CheckNoFieldData();

            UfbxScene scene = BuildSyntheticScene(
                out _, out _, out _, out _, out _, out _, out _, out _, out _,
                out _, out _, out _, out _, out _, out _, out _);
            ulong hash = UfbxHashScene.HashScene(scene);
            Console.WriteLine("synthetic: " + hash.ToString("x16"));
            CheckScene(scene);

            Console.WriteLine("hashcheck: " + s_checks + " checks, " + s_passed + " passed, " + s_failed + " failed");
            foreach (string f in s_failures) Console.WriteLine("  FAIL: " + f);
            return s_failed == 0 ? 0 : 1;
        }
    }
}
