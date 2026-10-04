// Coordinate-axis / unit conversion + skinning evaluation entry point, ported from ufbx v0.23.1
// ufbx.c:
//
//   ufbxi_pow10_targets / ufbxi_round_if_near            (ufbx.c:23883-23904)
//   ufbxi_axis_matrix (STUB, S3c)                        (ufbx.c:23659-23677)
//   ufbxi_transform_to_axes                              (ufbx.c:24954-24989)
//   ufbxi_scale_units                                    (ufbx.c:24991-25018)
//   ufbxi_find_cubic_bezier_t                            (ufbx.c:25022-25061)
//   ufbxi_evaluate_skinning                              (ufbx.c:25063-25177)
//   ufbxi_fixup_opts_string                              (ufbx.c:25179-25193)
//   ufbxi_resolve_warning_elements                       (ufbx.c:25195-25210)
//   ufbx_catch_get_skin_vertex_matrix / _skin_vertex_matrix (ufbx.c:31936-32026, ufbx.h:5600-5603)
//   ufbx_get_blend_shape_offset_index/vertex_offset      (ufbx.c:32028-32048)
//   ufbx_get_blend_vertex_offset                         (ufbx.c:32050-32068)
//   ufbx_add_blend_shape_vertex_offsets                  (ufbx.c:32070-32089)
//   ufbx_add_blend_vertex_offsets                        (ufbx.c:32091-32103)
//   ufbxi_mul_quat / add_weighted_vec3/quat/mat          (ufbx.c:22665-22696)
//   ufbxi_mirror_matrix* / is_transform_identity         (ufbx.c:23490-23524, 11600)
//
// MAPPING 口径:
//  - All matrix/vector math goes through UfbxMath / UfbxMatrix / UfbxVec3 (no System.Math), and
//    `ufbx_real` is `double` in the golden configuration.
//  - `ufbi_transform_to_axes`/`ufbxi_scale_units` mutate `uc->scene` in place and return void /
//    `int`; the port keeps the `bool` return of the C `ufbxi_check` sites.
//  - `ufbxi_evaluate_skinning` allocates result arrays through C's result/tmp arena; the port
//    materializes the same arrays (PORTING_NOTES #4). The S4c-owned normal helpers
//    (`ufbx_compute_topology` 33176, `ufbx_generate_normal_mapping` 32588,
//    `ufbx_compute_normals` 32622) are NOT ported here: they are stubbed with
//    `UfbxiReaderNotPortedException("ufbxi_evaluate_skinning: s4c-...")` at their exact call
//    sites, and the wiring points are listed in the S4a report.
//   - C's `ufbxi_push(buf, T, n)` + `ufbxi_check_err(error, ptr)` sequence becomes a plain array
//    allocation; the null-array check is dropped (managed allocation cannot fail under the
//    reference build's assumptions, PORTING_NOTES #4).
using System;
using System.Collections.Generic;

namespace Ufbx.NET
{
    internal static class UfbxiSceneOpts
    {
        // C: ufbxi_pow10_targets[] (ufbx.c:23883-23890).
        static readonly double[] Pow10Targets = new double[] {
            0.0,
            1e-8, 1e-7, 1e-6, 1e-5,
            1e-4, 1e-3, 1e-2, 1e-1,
            1e+0, 1e+1, 1e+2, 1e+3,
            1e+4, 1e+5, 1e+6, 1e+7,
            1e+8, 1e+9,
        };

        // C: ufbxi_round_if_near (ufbx.c:23892-23904).
        internal static double RoundIfNear(double[] targets, int numTargets, double value)
        {
            for (int i = 0; i < numTargets; i++) {
                double target = targets[i];
                double error = target * 9.5367431640625e-7;
                if (error < 0.0) error = -error;
                if (error < 7.52316384526264005e-37) error = 7.52316384526264005e-37;
                if (value >= target - error && value <= target + error) {
                    return target;
                }
            }
            return value;
        }

        // C: ufbxi_mul_quat (ufbx.c:22665-22673). Equivalent to UfbxQuat.Mul (ufbx_quat_mul).
        static UfbxQuat MulQuat(UfbxQuat a, UfbxQuat b) => UfbxQuat.Mul(a, b);

        // C: ufbxi_add_weighted_vec3 (ufbx.c:22675-22680).
        static void AddWeightedVec3(ref UfbxVec3 r, UfbxVec3 b, double w)
        {
            r.X += b.X * w;
            r.Y += b.Y * w;
            r.Z += b.Z * w;
        }

        // C: ufbxi_add_weighted_quat (ufbx.c:22682-22688).
        static void AddWeightedQuat(ref UfbxQuat r, UfbxQuat b, double w)
        {
            r.X += b.X * w;
            r.Y += b.Y * w;
            r.Z += b.Z * w;
            r.W += b.W * w;
        }

        // C: ufbxi_add_weighted_mat (ufbx.c:22690-22696).
        static void AddWeightedMat(ref UfbxMatrix r, UfbxMatrix b, double w)
        {
            UfbxVec3 c0 = r.GetCol(0); AddWeightedVec3(ref c0, b.GetCol(0), w); SetCol(ref r, 0, c0);
            UfbxVec3 c1 = r.GetCol(1); AddWeightedVec3(ref c1, b.GetCol(1), w); SetCol(ref r, 1, c1);
            UfbxVec3 c2 = r.GetCol(2); AddWeightedVec3(ref c2, b.GetCol(2), w); SetCol(ref r, 2, c2);
            UfbxVec3 c3 = r.GetCol(3); AddWeightedVec3(ref c3, b.GetCol(3), w); SetCol(ref r, 3, c3);
        }

        // The port's matrices store scalars; write a modified column back (C writes `cols[i].v[j]`).
        static void SetCol(ref UfbxMatrix m, int col, UfbxVec3 v)
        {
            switch (col) {
                case 0: m.M00 = v.X; m.M10 = v.Y; m.M20 = v.Z; break;
                case 1: m.M01 = v.X; m.M11 = v.Y; m.M21 = v.Z; break;
                case 2: m.M02 = v.X; m.M12 = v.Y; m.M22 = v.Z; break;
                default: m.M03 = v.X; m.M13 = v.Y; m.M23 = v.Z; break;
            }
        }

        // C: ufbxi_mirror_matrix_dst (ufbx.c:23496-23503).
        static void MirrorMatrixDst(ref UfbxMatrix m, UfbxMirrorAxis axis)
        {
            if (axis == 0) return;
            int ax = (int)axis - 1;
            MirrorElement(ref m, 0, ax); MirrorElement(ref m, 1, ax);
            MirrorElement(ref m, 2, ax); MirrorElement(ref m, 3, ax);
        }

        // C: ufbxi_mirror_matrix_src (ufbx.c:23505-23512).
        static void MirrorMatrixSrc(ref UfbxMatrix m, UfbxMirrorAxis axis)
        {
            if (axis == 0) return;
            int ax = (int)axis - 1;
            // C: m->cols[ax].x/y/z = -... (the whole column is the row selected by `ax` here,
            // because the port's scalar storage names mXY as column X row Y).
            switch (ax) {
                case 0: m.M00 = -m.M00; m.M01 = -m.M01; m.M02 = -m.M02; break;
                case 1: m.M10 = -m.M10; m.M11 = -m.M11; m.M12 = -m.M12; break;
                default: m.M20 = -m.M20; m.M21 = -m.M21; m.M22 = -m.M22; break;
            }
        }

        // C: `m->cols[col].v[ax] = -m->cols[col].v[ax]` — negate one scalar of one column.
        static void MirrorElement(ref UfbxMatrix m, int col, int ax)
        {
            // cols[col].v[ax] == element (col, ax)
            switch (col * 3 + ax) {
                case 0: m.M00 = -m.M00; break;
                case 1: m.M01 = -m.M01; break;
                case 2: m.M02 = -m.M02; break;
                case 3: m.M10 = -m.M10; break;
                case 4: m.M11 = -m.M11; break;
                case 5: m.M12 = -m.M12; break;
                case 6: m.M20 = -m.M20; break;
                case 7: m.M21 = -m.M21; break;
                case 8: m.M22 = -m.M22; break;
                case 9: m.M03 = -m.M03; break;
                case 10: m.M13 = -m.M13; break;
                default: m.M23 = -m.M23; break;
            }
        }

        // C: ufbxi_mirror_matrix (ufbx.c:23514-23519).
        internal static void MirrorMatrix(ref UfbxMatrix m, UfbxMirrorAxis axis)
        {
            if (axis == 0) return;
            MirrorMatrixSrc(ref m, axis);
            MirrorMatrixDst(ref m, axis);
        }

        // C: ufbxi_axis_matrix (ufbx.c:23659-23677). Owned by S3c: implemented in
        // Parse/SceneUpdate.cs; this is the call-site forwarding shim.
        internal static bool AxisMatrix(ref UfbxMatrix mat, UfbxCoordinateAxes src, UfbxCoordinateAxes dst)
        {
            return UfbxiSceneUpdate.AxisMatrix(ref mat, src, dst);
        }

        // C: ufbxi_transform_to_axes (ufbx.c:24954-24989).
        internal static void TransformToAxes(UfbxiContext uc, UfbxCoordinateAxes dstAxes)
        {
            if (!UfbxCoordinateAxes.IsValid(uc.Scene.Settings.Axes)) return;
            UfbxMatrix axisMatrix = uc.AxisMatrix;
            if (!AxisMatrix(ref axisMatrix, uc.Scene.Settings.Axes, dstAxes)) return;
            uc.AxisMatrix = axisMatrix;

            if (UfbxMatrix.Determinant(uc.AxisMatrix) < 0.0) {
                if (uc.Opts.HandednessConversionAxis != UfbxMirrorAxis.None) {
                    UfbxMirrorAxis mirrorAxis = uc.Opts.HandednessConversionAxis;
                    uc.MirrorAxis = mirrorAxis;
                    uc.Scene.Metadata.MirrorAxis = uc.MirrorAxis;

                    MirrorMatrix(ref uc.AxisMatrix, uc.MirrorAxis);
                    // C: ufbxi_dev_assert(ufbx_matrix_determinant(&uc->axis_matrix) >= 0.0f);

                    foreach (UfbxNode node in uc.Scene.Nodes) {
                        if (!node.IsRoot) {
                            node.AdjustMirrorAxis = mirrorAxis;
                        }
                    }
                }
            }

            if (uc.Opts.SpaceConversion == UfbxSpaceConversion.TransformRoot) {
                UfbxMatrix axisMat = uc.AxisMatrix;
                if (!UfbxiProperties.IsTransformIdentity(uc.Scene.RootNode.LocalTransform)) {
                    UfbxMatrix rootMat = UfbxMatrix.FromTransform(uc.Scene.RootNode.LocalTransform);
                    axisMat = UfbxMatrix.Mul(rootMat, axisMat);
                }

                MirrorMatrix(ref axisMat, uc.MirrorAxis);

                uc.Scene.RootNode.LocalTransform = UfbxMatrix.ToTransform(axisMat);
                uc.Scene.RootNode.NodeToParent = axisMat;
            }
        }

        // C: ufbxi_scale_units (ufbx.c:24991-25018).
        internal static bool ScaleUnits(UfbxiContext uc, double targetMeters)
        {
            if (uc.Scene.Settings.UnitMeters <= 0.0) return true;
            targetMeters = RoundIfNear(Pow10Targets, Pow10Targets.Length, targetMeters);

            double ratio = uc.Scene.Settings.UnitMeters / targetMeters;
            ratio = RoundIfNear(Pow10Targets, Pow10Targets.Length, ratio);
            if (ratio == 1.0) return true;

            uc.UnitScale = ratio;

            if (uc.Opts.SpaceConversion == UfbxSpaceConversion.TransformRoot) {
                UfbxNode root = uc.Scene.RootNode;
                UfbxTransform lt = root.LocalTransform;
                lt.Scale.X *= ratio;
                lt.Scale.Y *= ratio;
                lt.Scale.Z *= ratio;
                root.LocalTransform = lt;

                UfbxMatrix m = root.NodeToParent;
                m.M00 *= ratio; m.M01 *= ratio; m.M02 *= ratio;
                m.M10 *= ratio; m.M11 *= ratio; m.M12 *= ratio;
                m.M20 *= ratio; m.M21 *= ratio; m.M22 *= ratio;
                root.NodeToParent = m;
            }

            return true;
        }

        // C: ufbxi_find_cubic_bezier_t (ufbx.c:25022-25061). Newton-Raphson (three unrolled
        // iterations then up to four more pairs) with the C expressions transcribed verbatim;
        // `ufbx_fabs` is UfbxMath.Abs.
        internal static double FindCubicBezierT(double p1, double p2, double x0)
        {
            double p1_3 = p1 * 3.0, p2_3 = p2 * 3.0;
            double a = p1_3 - p2_3 + 1.0;
            double b = p2_3 - p1_3 - p1_3;
            double c = p1_3;

            double a_3 = 3.0 * a, b_2 = 2.0 * b;
            double t = x0;
            double x1, t2, t3;

            // Manually unroll three iterations of Newton-Raphson, this is enough
            // for most tangents
            t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
            t -= x1 / (a_3 * t2 + b_2 * t + c);

            t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
            t -= x1 / (a_3 * t2 + b_2 * t + c);

            t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
            t -= x1 / (a_3 * t2 + b_2 * t + c);

            // 4 ULP from 1.0
            const double eps = 8.881784197001252e-16;
            if (UfbxMath.Abs(x1) <= eps) return t;

            // Perform more iterations until we reach desired accuracy
            for (int i = 0; i < 4; i++) {
                t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
                t -= x1 / (a_3 * t2 + b_2 * t + c);

                t2 = t * t; t3 = t2 * t; x1 = a * t3 + b * t2 + c * t - x0;
                t -= x1 / (a_3 * t2 + b_2 * t + c);

                if (UfbxMath.Abs(x1) <= eps) return t;
            }

            return t;
        }

        // C: ufbxi_evaluate_skinning (ufbx.c:25063-25177).
        internal static void EvaluateSkinning(UfbxScene scene, UfbxError error, double time, bool loadCaches, UfbxGeometryCacheDataOpts cacheOpts)
        {
            int maxSkinnedIndices = 0;

            foreach (UfbxMesh mesh in scene.Meshes) {
                if (mesh.BlendDeformers.Length == 0 && mesh.SkinDeformers.Length == 0 &&
                    (mesh.CacheDeformers.Length == 0 || !loadCaches)) continue;
                maxSkinnedIndices = Math.Max(maxSkinnedIndices, mesh.NumIndices);
            }

            UfbxTopoEdge[] topo = new UfbxTopoEdge[maxSkinnedIndices];

            foreach (UfbxMesh mesh in scene.Meshes) {
                if (mesh.BlendDeformers.Length == 0 && mesh.SkinDeformers.Length == 0 &&
                    (mesh.CacheDeformers.Length == 0 || !loadCaches)) continue;
                if (mesh.NumVertices == 0) continue;

                int numVertices = mesh.NumVertices;
                // C: result_pos = ufbxi_push(buf_result, ufbx_vec3, num_vertices + 1); result_pos++.
                UfbxVec3[] resultPos = new UfbxVec3[numVertices + 1];

                resultPos[0] = default; // C: ufbx_zero_vec3
                int posBase = 1;        // `result_pos++`

                bool cachedPosition = false, cachedNormals = false;
                if (loadCaches && mesh.CacheDeformers.Length > 0) {
                    foreach (UfbxCacheDeformer cache in mesh.CacheDeformers) {
                        UfbxCacheChannel channel = cache.ExternalChannel;
                        if (channel == null) continue;

                        if ((channel.Interpretation == UfbxCacheInterpretation.VertexPosition || channel.Interpretation == UfbxCacheInterpretation.Points) && !cachedPosition) {
                            int numRead = UfbxiGeometryCacheSample.SampleGeometryCacheVec3(channel, time, resultPos, posBase, numVertices, cacheOpts);
                            if (numRead == numVertices) {
                                mesh.SkinnedIsLocal = true;
                                cachedPosition = true;
                            }
                        } else if (channel.Interpretation == UfbxCacheInterpretation.VertexNormal && !cachedNormals) {
                            // TODO: Is this right at all?
                            int numNormals = mesh.SkinnedNormal.Values != null ? mesh.SkinnedNormal.Values.Length : 0;
                            UfbxVec3[] normalData = new UfbxVec3[numNormals + 1];
                            normalData[0] = default;
                            int normalBase = 1;

                            int numRead = UfbxiGeometryCacheSample.SampleGeometryCacheVec3(channel, time, normalData, normalBase, numNormals, cacheOpts);
                            if (numRead == numNormals) {
                                cachedNormals = true;
                                UfbxVertexVec3 skinnedNormal = mesh.SkinnedNormal;
                                skinnedNormal.Values = normalData;
                                // C: `mesh->skinned_normal.values.data = normal_data` where
                                // `normal_data` was advanced past the sentinel.
                                skinnedNormal.Values = Slice(normalData, normalBase, numNormals);
                                mesh.SkinnedNormal = skinnedNormal;
                            }
                        }
                    }
                }

                UfbxVec3[] resultSlice;
                if (!cachedPosition) {
                    Array.Copy(mesh.Vertices, 0, resultPos, posBase, numVertices);

                    foreach (UfbxBlendDeformer blend in mesh.BlendDeformers) {
                        AddBlendVertexOffsets(blend, resultPos, posBase, numVertices, 1.0);
                    }

                    // TODO: What should we do about multiple skins??
                    if (mesh.SkinDeformers.Length > 0) {
                        UfbxMatrix? fallback = null;
                        if (mesh.Instances != null && mesh.Instances.Length > 0) fallback = mesh.Instances[0].GeometryToWorld;
                        UfbxSkinDeformer skin = mesh.SkinDeformers[0];
                        for (int i = 0; i < numVertices; i++) {
                            UfbxMatrix mat = GetSkinVertexMatrix(skin, i, fallback);
                            resultPos[posBase + i] = UfbxMatrix.TransformPosition(mat, resultPos[posBase + i]);
                        }

                        mesh.SkinnedIsLocal = false;
                    }
                }

                resultSlice = Slice(resultPos, posBase, numVertices);
                {
                    UfbxVertexVec3 skinnedPosition = mesh.SkinnedPosition;
                    skinnedPosition.Values = resultSlice;
                    mesh.SkinnedPosition = skinnedPosition;
                }

                if (!cachedNormals) {
                    int numIndices = mesh.NumIndices;
                    uint[] normalIndices = new uint[numIndices];

                    // S4c-owned helpers (ufbx.c:33176 / 32588 / 32622), wired per the C tail
                    // of `ufbxi_evaluate_skinning` (ufbx.c:25141-25168).
                    UfbxTopology.ComputeTopology(mesh, topo, numIndices);
                    int numNormals = UfbxTopology.GenerateNormalMapping(mesh, topo, numIndices, normalIndices, numIndices, false);

                    if (numNormals == mesh.NumVertices) {
                        UfbxVertexVec3 sn = mesh.SkinnedNormal;
                        sn.UniquePerVertex = true;
                        mesh.SkinnedNormal = sn;
                    }

                    // C: normal_data[0] = ufbx_zero_vec3; normal_data++ -- compute_normals then
                    // writes through the ADVANCED pointer, i.e. slots 0..num_normals-1 of the
                    // base-0 view below (the sentinel slot is never written or read).
                    UfbxVec3[] normalBase = new UfbxVec3[numNormals];

                    UfbxTopology.ComputeNormals(mesh, mesh.SkinnedPosition, normalIndices, numIndices, normalBase, numNormals);

                    mesh.GeneratedNormals = true;
                    UfbxVertexVec3 skinnedNormal = mesh.SkinnedNormal;
                    skinnedNormal.Exists = true;
                    skinnedNormal.Values = normalBase;
                    skinnedNormal.Indices = normalIndices;
                    skinnedNormal.ValueReals = 3;
                    mesh.SkinnedNormal = skinnedNormal;
                }
            }
        }

        static UfbxVec3[] Slice(UfbxVec3[] src, int offset, int count)
        {
            UfbxVec3[] dst = new UfbxVec3[count];
            Array.Copy(src, offset, dst, 0, count);
            return dst;
        }

        // C: ufbxi_panicf(panic, cond, ...) (ufbx.c:3409-3410) -- `(cond) ? false :
        // (ufbxi_panicf_imp((panic), __VA_ARGS__), true)`. Same helper as Parse/Topology.cs:127
        // (its own copy stays private there); the panic-less public forms pass a scratch panic.
        static bool Panicf(ref UfbxPanic panic, bool condition, string fmt, UfbxiVaList args)
        {
            if (condition) return false;
            UfbxiPrint.Panicf(ref panic, fmt, args);
            return true;
        }

        // C: ufbx_catch_get_skin_vertex_matrix (ufbx.c:31936-32026). `vertex` is C's `size_t` and
        // **both** bounds tests are unsigned there (31939 and 31941), so the port compares as `ulong`
        // too: an oversized or negative index takes the identity branch instead of reaching the array.
        internal static UfbxMatrix CatchGetSkinVertexMatrix(ref UfbxPanic panic, UfbxSkinDeformer skin, long vertex, UfbxMatrix? fallback)
        {
            // C: `ufbx_assert(skin);` -- and the panic site right after it dereferences
            // `skin->vertices.count`, so a NULL skin is the assert site rather than a reachable input.
            if (skin == null) return UfbxMatrix.Identity;
            if (Panicf(ref panic, unchecked((ulong)vertex) < unchecked((ulong)skin.Vertices.Length),
                "vertex (%zu) out of bounds (%zu)",
                new UfbxiVaList().AddSizeT(unchecked((ulong)vertex)).AddSizeT(unchecked((ulong)skin.Vertices.Length)))) return UfbxMatrix.Identity;
            if (unchecked((ulong)vertex) >= unchecked((ulong)skin.Vertices.Length)) return UfbxMatrix.Identity;
            UfbxSkinVertex skinVertex = skin.Vertices[unchecked((int)vertex)];

            UfbxMatrix mat = default; // C: { 0.0f }
            UfbxQuat q0 = default, qe = default;
            UfbxQuat firstQ0 = default;
            UfbxVec3 qs = default;
            double totalWeight = 0.0;

            for (uint i = 0; i < skinVertex.NumWeights; i++) {
                UfbxSkinWeight weight = skin.Weights[skinVertex.WeightBegin + i];
                UfbxSkinCluster cluster = skin.Clusters[weight.ClusterIndex];
                UfbxNode node = cluster.BoneNode;
                if (node == null) continue;

                totalWeight += weight.Weight;
                if (skinVertex.DqWeight > 0.0) {
                    UfbxTransform t = cluster.GeometryToWorldTransform;
                    UfbxQuat vq0 = t.Rotation;
                    if (i == 0) firstQ0 = vq0;

                    if (UfbxQuat.Dot(firstQ0, vq0) < 0.0) {
                        vq0.X = -vq0.X;
                        vq0.Y = -vq0.Y;
                        vq0.Z = -vq0.Z;
                        vq0.W = -vq0.W;
                    }

                    UfbxQuat vqt = new UfbxQuat(0.5 * t.Translation.X, 0.5 * t.Translation.Y, 0.5 * t.Translation.Z, 0);
                    UfbxQuat vqe = MulQuat(vqt, vq0);
                    AddWeightedQuat(ref q0, vq0, weight.Weight);
                    AddWeightedQuat(ref qe, vqe, weight.Weight);
                    AddWeightedVec3(ref qs, t.Scale, weight.Weight);
                }

                if (skinVertex.DqWeight < 1.0) {
                    AddWeightedMat(ref mat, cluster.GeometryToWorld, (1.0 - skinVertex.DqWeight) * weight.Weight);
                }
            }

            if (totalWeight <= 0.0) {
                if (fallback.HasValue) {
                    return fallback.Value;
                } else {
                    return UfbxMatrix.Identity;
                }
            }

            if (UfbxMath.Abs(totalWeight - 1.0) > UfbxMathConsts.Epsilon) {
                double rcpWeight = UfbxMath.Abs(totalWeight) > UfbxMathConsts.Epsilon ? 1.0 / totalWeight : 0.0;
                if (skinVertex.DqWeight > 0.0) {
                    q0.X *= rcpWeight; q0.Y *= rcpWeight; q0.Z *= rcpWeight; q0.W *= rcpWeight;
                    qe.X *= rcpWeight; qe.Y *= rcpWeight; qe.Z *= rcpWeight; qe.W *= rcpWeight;
                    qs.X *= rcpWeight; qs.Y *= rcpWeight; qs.Z *= rcpWeight;
                }
                if (skinVertex.DqWeight < 1.0) {
                    mat.M00 *= rcpWeight; mat.M01 *= rcpWeight; mat.M02 *= rcpWeight; mat.M03 *= rcpWeight;
                    mat.M10 *= rcpWeight; mat.M11 *= rcpWeight; mat.M12 *= rcpWeight; mat.M13 *= rcpWeight;
                    mat.M20 *= rcpWeight; mat.M21 *= rcpWeight; mat.M22 *= rcpWeight; mat.M23 *= rcpWeight;
                }
            }

            if (skinVertex.DqWeight > 0.0) {
                UfbxTransform dqt = default;
                double rcpLen = 1.0 / UfbxMath.Sqrt(q0.X * q0.X + q0.Y * q0.Y + q0.Z * q0.Z + q0.W * q0.W);
                double rcpLen2x2 = 2.0 * rcpLen * rcpLen;
                dqt.Rotation.X = q0.X * rcpLen;
                dqt.Rotation.Y = q0.Y * rcpLen;
                dqt.Rotation.Z = q0.Z * rcpLen;
                dqt.Rotation.W = q0.W * rcpLen;
                dqt.Scale.X = qs.X;
                dqt.Scale.Y = qs.Y;
                dqt.Scale.Z = qs.Z;
                dqt.Translation.X = rcpLen2x2 * (-qe.W * q0.X + qe.X * q0.W - qe.Y * q0.Z + qe.Z * q0.Y);
                dqt.Translation.Y = rcpLen2x2 * (-qe.W * q0.Y + qe.X * q0.Z + qe.Y * q0.W - qe.Z * q0.X);
                dqt.Translation.Z = rcpLen2x2 * (-qe.W * q0.Z - qe.X * q0.Y + qe.Y * q0.X + qe.Z * q0.W);
                UfbxMatrix dqm = UfbxMatrix.FromTransform(dqt);
                if (skinVertex.DqWeight < 1.0) {
                    AddWeightedMat(ref mat, dqm, skinVertex.DqWeight);
                } else {
                    mat = dqm;
                }
            }

            return mat;
        }

        // C: ufbx_get_skin_vertex_matrix (ufbx.h:5601-5603) -- the inline wrapper that calls the
        // catch body with `panic == NULL`. The port models that as a scratch panic whose message
        // nobody reads (same convention as the plain forms in Parse/Topology.cs).
        internal static UfbxMatrix GetSkinVertexMatrix(UfbxSkinDeformer skin, long vertex, UfbxMatrix? fallback)
        {
            UfbxPanic panic = default;
            return CatchGetSkinVertexMatrix(ref panic, skin, vertex, fallback);
        }

        // C: ufbx_get_blend_shape_offset_index (ufbx.c:32028-32041).
        internal static uint GetBlendShapeOffsetIndex(UfbxBlendShape shape, int vertex)
        {
            if (shape == null) return UfbxConstants.NoIndex;

            // C: ufbxi_macro_lower_bound_eq(uint32_t, 16, ...) over offset_vertices.
            uint vertexIx = (uint)vertex;
            uint[] data = shape.OffsetVertices;
            int numOffsets = shape.NumOffsets;
            int begin = 0, end = numOffsets;
            while (end - begin >= 16) {
                int mid = (begin + end) >> 1;
                if (data[mid] < vertexIx) begin = mid + 1; else end = mid;
            }
            end = numOffsets;
            int index = -1;
            for (int i = begin; i < end; i++) {
                if (data[i] == vertexIx) { index = i; break; }
                if (data[i] > vertexIx) break;
            }
            if (index < 0) return UfbxConstants.NoIndex;

            return (uint)index;
        }

        // C: ufbx_get_blend_shape_vertex_offset (ufbx.c:32043-32048).
        internal static UfbxVec3 GetBlendShapeVertexOffset(UfbxBlendShape shape, int vertex)
        {
            uint index = GetBlendShapeOffsetIndex(shape, vertex);
            if (index == UfbxConstants.NoIndex) return default;
            return shape.PositionOffsets[index];
        }

        // C: ufbx_get_blend_vertex_offset (ufbx.c:32050-32068).
        internal static UfbxVec3 GetBlendVertexOffset(UfbxBlendDeformer blend, int vertex)
        {
            if (blend == null) return default;

            UfbxVec3 offset = default;

            foreach (UfbxBlendChannel chan in blend.Channels) {
                foreach (UfbxBlendKeyframe key in chan.Keyframes) {
                    if (key.EffectiveWeight == 0.0) continue;

                    UfbxVec3 keyOffset = GetBlendShapeVertexOffset(key.Shape, vertex);
                    AddWeightedVec3(ref offset, keyOffset, key.EffectiveWeight);
                }
            }

            return offset;
        }

        // C: ufbx_add_blend_shape_vertex_offsets (ufbx.c:32070-32089). `vertices` is a slice
        // [offset, offset+numVertices); the C pointer arithmetic on `vertices[index]` becomes an
        // index into the caller's backing array.
        internal static void AddBlendShapeVertexOffsets(UfbxBlendShape shape, UfbxVec3[] vertices, int offset, int numVertices, double weight)
        {
            if (weight == 0.0) return;
            if (vertices == null) return;

            int numOffsets = shape.NumOffsets;
            uint[] vertexIndices = shape.OffsetVertices;
            UfbxVec3[] offsets = shape.PositionOffsets;
            double[] weights = shape.OffsetWeights;
            int weightCount = weights != null ? weights.Length : 0;
            for (int i = 0; i < numOffsets; i++) {
                uint index = vertexIndices[i];
                if (index < numVertices) {
                    double vertexWeight = weight;
                    if (i < weightCount) {
                        vertexWeight *= weights[i];
                    }
                    int slot = offset + (int)index;
                    AddWeightedVec3(ref vertices[slot], offsets[i], vertexWeight);
                }
            }
        }

        // C: ufbx_add_blend_vertex_offsets (ufbx.c:32091-32103).
        internal static void AddBlendVertexOffsets(UfbxBlendDeformer blend, UfbxVec3[] vertices, int offset, int numVertices, double weight)
        {
            if (blend == null) return;

            foreach (UfbxBlendChannel chan in blend.Channels) {
                foreach (UfbxBlendKeyframe key in chan.Keyframes) {
                    if (key.EffectiveWeight == 0.0) continue;
                    AddBlendShapeVertexOffsets(key.Shape, vertices, offset, numVertices, weight * key.EffectiveWeight);
                }
            }
        }

        // C: ufbxi_resolve_warning_elements (ufbx.c:25195-25210).
        internal static bool ResolveWarningElements(UfbxiContext uc)
        {
            int numElements = uc.TmpElementIds.Count;
            uint[] elementIds = new uint[numElements];
            for (int i = 0; i < numElements; i++) elementIds[i] = uc.TmpElementIds[i];
            uc.TmpElementIds.Clear();

            UfbxWarning[] warnings = uc.Scene.Metadata.Warnings;
            if (warnings != null) {
                for (int i = 0; i < warnings.Length; i++) {
                    UfbxWarning warning = warnings[i];
                    uint elementId = warning.ElementId;
                    // Decode `element_id`, see HACK(warning-element) in `ufbxi_vwarnf_imp()`.
                    if ((elementId & 0x80000000u) != 0 && elementId != ~0u) {
                        warning.ElementId = elementIds[elementId & ~0x80000000u];
                    }
                }
            }

            return true;
        }
    }
}
