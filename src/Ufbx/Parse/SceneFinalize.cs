// Scene-finalize (mid-half) ported from ufbx v0.23.1 ufbx.c. Owned by the S3b porting agent
// (2026-10-03 wave). Scope (C line numbers):
//   ufbxi_mat_transform_* / shader mapping tables      (ufbx.c:19371-19966)
//   ufbxi_fetch_mapping_maps                           (ufbx.c:19968-20093)
//   ufbxi_update_factor                                (ufbx.c:20095-20106)
//   ufbxi_glossiness_remaps / ufbxi_fetch_maps         (ufbx.c:20108-20215)
//   ufbxi_constraint_props / ufbxi_add_constraint_prop (ufbx.c:20217-20265)
//   ufbxi_finalize_nurbs_basis                         (ufbx.c:20267-20314)
//   ufbxi_finalize_lod_group                           (ufbx.c:20316-20364)
//   ufbxi_generate_normals                             (ufbx.c:20366-20405)
//   ufbxi_push_prop_prefix                             (ufbx.c:20407-20429)
//   ufbxi_shader_texture_find_prefix                   (ufbx.c:20431-20480)
//   ufbxi_file_shaders / ufbxi_update_shader_texture   (ufbx.c:20482-20537)
//   ufbxi_finalize_shader_texture                      (ufbx.c:20539-20692)
//   ufbxi_propagate_main_textures                      (ufbx.c:20694-20754)
//   ufbxi_insert_texture_file / ufbxi_pop_texture_files(ufbx.c:20759-20819)
//   ufbxi_ordered_texture_* / ufbxi_deduplicate_textures(ufbx.c:20821-20869)
//   ufbxi_fetch_file_textures                          (ufbx.c:20878-21009)
//   ufbxi_get_geometry_transform_node                  (ufbx.c:21011-21018)
//   ufbxi_mirror/scale/transform/normalize_vec3_list   (ufbx.c:21020-21070)
//   ufbxi_flip_attrib_winding / ufbxi_flip_winding     (ufbx.c:21075-21165)
//   ufbxi_modify_geometry                              (ufbx.c:21167-21334)
//   ufbxi_postprocess_scene                            (ufbx.c:21336-21358)
//   ufbxi_next_path_segment / absolute_to_relative_path(ufbx.c:21360-21438)
//   ufbxi_resolve_filenames                            (ufbx.c:21440-21453)
//   ufbxi_file_content / sort/push/fetch/resolve       (ufbx.c:21455-21531)
//   ufbxi_validate_indices                             (ufbx.c:21533-21549)
//   ufbxi_material_part_usage_less                     (ufbx.c:21551; used by S4c)
// plus the `ufbx_find_*` public property finders (ufbx.c:30643-30765, 31413-31484) that
// `ufbxi_finalize_scene` and the update chain call. `ufbxi_finalize_mesh_material`
// (ufbx.c:21564-21624) is NOT ported here: it already lives in Parse/Subdivide.cs as
// `UfbxiSubdivide.FinalizeMeshMaterial` (S4c) and is called as-is.
//
// Conventions (PORTING_NOTES.md):
//  * C element pointer order == `element_id` order (comparators use ElementId).
//  * `ufbx_real` is `double`; no `System.Math`/`MathF`; no reordered float evaluation.
//  * Error sites: plain `ufbxi_check` -> `CheckNoDesc`, `ufbxi_check_msg` -> `CheckMsg`.
//  * C's pointer-keyed `texture_file_map` is modeled by `UfbxiTextureFileMap` (insertion
//    order observable through `file->index` and `ufbxi_pop_texture_files`).
//  * `ufbxi_push(buf, T, n)` becomes array allocation (PORTING_NOTES #4).
using System;
using System.Collections.Generic;

namespace Ufbx
{
    // C: ufbxi_shader_mapping (ufbx.c:19422-19428). `prop_len` is carried by the string.
    internal struct UfbxiShaderMapping
    {
        public int Index;      // C: uint8_t index (ufbx_material_(fbx|pbr)_map / feature)
        public uint Flags;     // C: uint8_t flags (ufbxi_shader_mapping_flag)
        public int Transform;  // C: uint8_t transform (ufbxi_mat_transform)
        public string Prop;    // C: const char *prop
    }

    // C: ufbxi_shader_mapping_list (ufbx.c:19430-19440).
    internal struct UfbxiShaderMappingList
    {
        public UfbxiShaderMapping[] Data;
        public UfbxiShaderMapping[] Features;
        public uint DefaultFeatures;
        public string TexturePrefix;
        public string TextureSuffix;
        public string TextureEnabledPrefix;
        public string TextureEnabledSuffix;
    }

    // C: ufbxi_ordered_texture (ufbx.c:20821-20824). Value type, sorted by stable sort.
    internal struct UfbxiOrderedTexture
    {
        public UfbxTexture Texture; // C: ufbx_texture *texture
        public int Order;           // C: size_t order
    }

    internal static class UfbxiSceneFinalize
    {
        // ==================================================================
        // Material transform fns + shader mapping tables (ufbx.c:19371-19966)
        // ==================================================================

        // C: ufbxi_mat_transform (ufbx.c:19380-19388).
        internal const int MatTransformIdentity = 0;
        internal const int MatTransformInvertXId = 1;
        internal const int MatTransformUnknownShininessId = 2;
        internal const int MatTransformBlenderOpacityId = 3;
        internal const int MatTransformBlenderShininessId = 4;

        // C: ufbxi_shader_mapping_flag (ufbx.c:19390-19397).
        internal const uint ShaderMappingDefaultW1 = 0x1;
        internal const uint ShaderMappingWidenToRgb = 0x2;
        internal const uint ShaderMappingMultiplyValue = 0x4;

        // C: ufbxi_shader_feature_flag (ufbx.c:19399-19410).
        internal const uint ShaderFeatureInverted = 0x1;
        internal const uint ShaderFeatureIfExists = 0x2;
        internal const uint ShaderFeatureIfTexture = 0x4;
        internal const uint ShaderFeatureIfAround1 = 0x8;
        internal const uint ShaderFeatureIfExistsOrTexture = ShaderFeatureIfExists | ShaderFeatureIfTexture;

        // C: ufbxi_mat_transform_invert_x (ufbx.c:19375).
        static void MatTransformInvertX(ref UfbxVec4 v) { v.X = 1.0 - v.X; }

        // C: ufbxi_mat_transform_unknown_shininess (ufbx.c:19376). The second `if` re-tests
        // the (possibly rewritten) value: NaN maps to 0.
        static void MatTransformUnknownShininess(ref UfbxVec4 v)
        {
            if (v.X >= 0.0) v.X = 1.0 - UfbxMath.Sqrt(v.X) * 0.1;
            if (!(v.X >= 0.0)) v.X = 0.0;
        }

        // C: ufbxi_mat_transform_blender_opacity (ufbx.c:19377).
        static void MatTransformBlenderOpacity(ref UfbxVec4 v) { v.X = 1.0 - v.X; }

        // C: ufbxi_mat_transform_blender_shininess (ufbx.c:19378).
        static void MatTransformBlenderShininess(ref UfbxVec4 v)
        {
            if (v.X >= 0.0) v.X = 1.0 - UfbxMath.Sqrt(v.X) * 0.1;
            if (!(v.X >= 0.0)) v.X = 0.0;
        }

        // C: ufbxi_mat_transform_fns[] (ufbx.c:19412-19418) dispatched by switch.
        static void ApplyMatTransform(int transform, ref UfbxVec4 v)
        {
            switch (transform) {
                case MatTransformIdentity: break;
                case MatTransformInvertXId: MatTransformInvertX(ref v); break;
                case MatTransformUnknownShininessId: MatTransformUnknownShininess(ref v); break;
                case MatTransformBlenderOpacityId: MatTransformBlenderOpacity(ref v); break;
                case MatTransformBlenderShininessId: MatTransformBlenderShininess(ref v); break;
            }
        }

        static UfbxiShaderMapping Map(int index, uint flags, int transform, string prop)
        {
            UfbxiShaderMapping m;
            m.Index = index;
            m.Flags = flags;
            m.Transform = transform;
            m.Prop = prop;
            return m;
        }

        // C: ufbxi_base_fbx_mapping (ufbx.c:19444-19473).
        static readonly UfbxiShaderMapping[] BaseFbxMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFbxMap.DiffuseColor, ShaderMappingDefaultW1, 0, "Diffuse"),
            Map((int)UfbxMaterialFbxMap.DiffuseColor, ShaderMappingDefaultW1, 0, "DiffuseColor"),
            Map((int)UfbxMaterialFbxMap.DiffuseFactor, 0, 0, "DiffuseFactor"),
            Map((int)UfbxMaterialFbxMap.SpecularColor, ShaderMappingDefaultW1, 0, "Specular"),
            Map((int)UfbxMaterialFbxMap.SpecularColor, ShaderMappingDefaultW1, 0, "SpecularColor"),
            Map((int)UfbxMaterialFbxMap.SpecularFactor, 0, 0, "SpecularFactor"),
            Map((int)UfbxMaterialFbxMap.SpecularExponent, 0, 0, "Shininess"),
            Map((int)UfbxMaterialFbxMap.SpecularExponent, 0, 0, "ShininessExponent"),
            Map((int)UfbxMaterialFbxMap.ReflectionColor, ShaderMappingDefaultW1, 0, "Reflection"),
            Map((int)UfbxMaterialFbxMap.ReflectionColor, ShaderMappingDefaultW1, 0, "ReflectionColor"),
            Map((int)UfbxMaterialFbxMap.ReflectionFactor, 0, 0, "ReflectionFactor"),
            Map((int)UfbxMaterialFbxMap.TransparencyColor, ShaderMappingDefaultW1, 0, "Transparent"),
            Map((int)UfbxMaterialFbxMap.TransparencyColor, ShaderMappingDefaultW1, 0, "TransparentColor"),
            Map((int)UfbxMaterialFbxMap.TransparencyFactor, 0, 0, "TransparentFactor"),
            Map((int)UfbxMaterialFbxMap.TransparencyFactor, 0, 0, "TransparencyFactor"),
            Map((int)UfbxMaterialFbxMap.EmissionColor, ShaderMappingDefaultW1, 0, "Emissive"),
            Map((int)UfbxMaterialFbxMap.EmissionColor, ShaderMappingDefaultW1, 0, "EmissiveColor"),
            Map((int)UfbxMaterialFbxMap.EmissionFactor, 0, 0, "EmissiveFactor"),
            Map((int)UfbxMaterialFbxMap.AmbientColor, ShaderMappingDefaultW1, 0, "Ambient"),
            Map((int)UfbxMaterialFbxMap.AmbientColor, ShaderMappingDefaultW1, 0, "AmbientColor"),
            Map((int)UfbxMaterialFbxMap.AmbientFactor, 0, 0, "AmbientFactor"),
            Map((int)UfbxMaterialFbxMap.NormalMap, 0, 0, "NormalMap"),
            Map((int)UfbxMaterialFbxMap.Bump, 0, 0, "Bump"),
            Map((int)UfbxMaterialFbxMap.BumpFactor, 0, 0, "BumpFactor"),
            Map((int)UfbxMaterialFbxMap.Displacement, 0, 0, "Displacement"),
            Map((int)UfbxMaterialFbxMap.DisplacementFactor, 0, 0, "DisplacementFactor"),
            Map((int)UfbxMaterialFbxMap.VectorDisplacement, 0, 0, "VectorDisplacement"),
            Map((int)UfbxMaterialFbxMap.VectorDisplacementFactor, 0, 0, "VectorDisplacementFactor"),
        };

        // C: ufbxi_obj_fbx_mapping (ufbx.c:19475-19486).
        static readonly UfbxiShaderMapping[] ObjFbxMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFbxMap.AmbientColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Ka"),
            Map((int)UfbxMaterialFbxMap.DiffuseColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Kd"),
            Map((int)UfbxMaterialFbxMap.SpecularColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Ks"),
            Map((int)UfbxMaterialFbxMap.EmissionColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Ke"),
            Map((int)UfbxMaterialFbxMap.SpecularExponent, 0, 0, "Ns"),
            Map((int)UfbxMaterialFbxMap.TransparencyFactor, 0, MatTransformInvertXId, "d"),
            Map((int)UfbxMaterialFbxMap.NormalMap, 0, 0, "norm"),
            Map((int)UfbxMaterialFbxMap.Displacement, 0, 0, "disp"),
            Map((int)UfbxMaterialFbxMap.Bump, 0, 0, "bump"),
            Map((int)UfbxMaterialFbxMap.Bump, 0, 0, "Bump"),
        };

        // C: ufbxi_fbx_lambert_shader_pbr_mapping (ufbx.c:19488-19500).
        static readonly UfbxiShaderMapping[] FbxLambertShaderPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "Diffuse"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "DiffuseColor"),
            Map((int)UfbxMaterialPbrMap.BaseFactor, 0, 0, "DiffuseFactor"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "Transparent"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "TransparentColor"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "TransparentFactor"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "TransparencyFactor"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "Emissive"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "EmissiveColor"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "EmissiveFactor"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "NormalMap"),
        };

        // C: ufbxi_fbx_phong_shader_pbr_mapping (ufbx.c:19502-19519).
        static readonly UfbxiShaderMapping[] FbxPhongShaderPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "Diffuse"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "DiffuseColor"),
            Map((int)UfbxMaterialPbrMap.BaseFactor, 0, 0, "DiffuseFactor"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "Specular"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "SpecularColor"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "SpecularFactor"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, MatTransformUnknownShininessId, "Shininess"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, MatTransformUnknownShininessId, "ShininessExponent"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "Transparent"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "TransparentColor"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "TransparentFactor"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "TransparencyFactor"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "Emissive"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "EmissiveColor"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "EmissiveFactor"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "NormalMap"),
        };

        // C: ufbxi_osl_standard_shader_pbr_mapping (ufbx.c:19521-19564).
        static readonly UfbxiShaderMapping[] OslStandardShaderPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseFactor, 0, 0, "base"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "base_color"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "specular_roughness"),
            Map((int)UfbxMaterialPbrMap.DiffuseRoughness, 0, 0, "diffuse_roughness"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "metalness"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "specular"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "specular_color"),
            Map((int)UfbxMaterialPbrMap.SpecularIor, 0, 0, "specular_IOR"),
            Map((int)UfbxMaterialPbrMap.SpecularAnisotropy, 0, 0, "specular_anisotropy"),
            Map((int)UfbxMaterialPbrMap.SpecularRotation, 0, 0, "specular_rotation"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "transmission"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "transmission_color"),
            Map((int)UfbxMaterialPbrMap.TransmissionDepth, 0, 0, "transmission_depth"),
            Map((int)UfbxMaterialPbrMap.TransmissionScatter, ShaderMappingWidenToRgb, 0, "transmission_scatter"),
            Map((int)UfbxMaterialPbrMap.TransmissionScatterAnisotropy, 0, 0, "transmission_scatter_anisotropy"),
            Map((int)UfbxMaterialPbrMap.TransmissionDispersion, 0, 0, "transmission_dispersion"),
            Map((int)UfbxMaterialPbrMap.TransmissionExtraRoughness, 0, 0, "transmission_extra_roughness"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceFactor, 0, 0, "subsurface"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceColor, ShaderMappingDefaultW1, 0, "subsurface_color"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceRadius, ShaderMappingWidenToRgb, 0, "subsurface_radius"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceScale, 0, 0, "subsurface_scale"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceAnisotropy, 0, 0, "subsurface_anisotropy"),
            Map((int)UfbxMaterialPbrMap.SheenFactor, 0, 0, "sheen"),
            Map((int)UfbxMaterialPbrMap.SheenColor, ShaderMappingDefaultW1, 0, "sheen_color"),
            Map((int)UfbxMaterialPbrMap.SheenRoughness, 0, 0, "sheen_roughness"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "coat"),
            Map((int)UfbxMaterialPbrMap.CoatColor, ShaderMappingDefaultW1, 0, "coat_color"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "coat_roughness"),
            Map((int)UfbxMaterialPbrMap.CoatIor, 0, 0, "coat_IOR"),
            Map((int)UfbxMaterialPbrMap.CoatAnisotropy, 0, 0, "coat_anisotropy"),
            Map((int)UfbxMaterialPbrMap.CoatRotation, 0, 0, "coat_rotation"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "coat_normal"),
            Map((int)UfbxMaterialPbrMap.CoatAffectBaseColor, ShaderMappingDefaultW1, 0, "coat_affect_color"),
            Map((int)UfbxMaterialPbrMap.CoatAffectBaseRoughness, 0, 0, "coat_affect_roughness"),
            Map((int)UfbxMaterialPbrMap.ThinFilmThickness, 0, 0, "thin_film_thickness"),
            Map((int)UfbxMaterialPbrMap.ThinFilmIor, 0, 0, "thin_film_IOR"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "emission"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emission_color"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "opacity"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "NormalMap"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "normalCamera"),
            Map((int)UfbxMaterialPbrMap.TangentMap, 0, 0, "tangent"),
        };

        // C: ufbxi_osl_standard_shader_features (ufbx.c:19566-19568).
        static readonly UfbxiShaderMapping[] OslStandardShaderFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.ThinWalled, 0, 0, "thin_walled"),
        };

        // C: ufbxi_arnold_shader_pbr_mapping (ufbx.c:19570-19618).
        static readonly UfbxiShaderMapping[] ArnoldShaderPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseFactor, 0, 0, "base"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "baseColor"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "specularRoughness"),
            Map((int)UfbxMaterialPbrMap.DiffuseRoughness, 0, 0, "diffuseRoughness"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "metalness"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "specular"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "specularColor"),
            Map((int)UfbxMaterialPbrMap.SpecularIor, 0, 0, "specularIOR"),
            Map((int)UfbxMaterialPbrMap.SpecularAnisotropy, 0, 0, "specularAnisotropy"),
            Map((int)UfbxMaterialPbrMap.SpecularRotation, 0, 0, "specularRotation"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "transmission"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "transmissionColor"),
            Map((int)UfbxMaterialPbrMap.TransmissionDepth, 0, 0, "transmissionDepth"),
            Map((int)UfbxMaterialPbrMap.TransmissionScatter, ShaderMappingWidenToRgb, 0, "transmissionScatter"),
            Map((int)UfbxMaterialPbrMap.TransmissionScatterAnisotropy, 0, 0, "transmissionScatterAnisotropy"),
            Map((int)UfbxMaterialPbrMap.TransmissionDispersion, 0, 0, "transmissionDispersion"),
            Map((int)UfbxMaterialPbrMap.TransmissionExtraRoughness, 0, 0, "transmissionExtraRoughness"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceFactor, 0, 0, "subsurface"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceColor, ShaderMappingDefaultW1, 0, "subsurfaceColor"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceRadius, ShaderMappingWidenToRgb, 0, "subsurfaceRadius"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceScale, 0, 0, "subsurfaceScale"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceAnisotropy, 0, 0, "subsurfaceAnisotropy"),
            Map((int)UfbxMaterialPbrMap.SheenFactor, 0, 0, "sheen"),
            Map((int)UfbxMaterialPbrMap.SheenColor, ShaderMappingDefaultW1, 0, "sheenColor"),
            Map((int)UfbxMaterialPbrMap.SheenRoughness, 0, 0, "sheenRoughness"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "coat"),
            Map((int)UfbxMaterialPbrMap.CoatColor, ShaderMappingDefaultW1, 0, "coatColor"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "coatRoughness"),
            Map((int)UfbxMaterialPbrMap.CoatIor, 0, 0, "coatIOR"),
            Map((int)UfbxMaterialPbrMap.CoatAnisotropy, 0, 0, "coatAnisotropy"),
            Map((int)UfbxMaterialPbrMap.CoatRotation, 0, 0, "coatRotation"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "coatNormal"),
            Map((int)UfbxMaterialPbrMap.ThinFilmThickness, 0, 0, "thinFilmThickness"),
            Map((int)UfbxMaterialPbrMap.ThinFilmIor, 0, 0, "thinFilmIOR"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "emission"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emissionColor"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "opacity"),
            Map((int)UfbxMaterialPbrMap.IndirectDiffuse, 0, 0, "indirectDiffuse"),
            Map((int)UfbxMaterialPbrMap.IndirectSpecular, 0, 0, "indirectSpecular"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "NormalMap"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "normalCamera"),
            Map((int)UfbxMaterialPbrMap.TangentMap, 0, 0, "tangent"),
            Map((int)UfbxMaterialPbrMap.MatteColor, ShaderMappingDefaultW1, 0, "aiMatteColor"),
            Map((int)UfbxMaterialPbrMap.MatteFactor, 0, 0, "aiMatteColorA"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceType, 0, 0, "subsurfaceType"),
            Map((int)UfbxMaterialPbrMap.TransmissionPriority, 0, 0, "dielectricPriority"),
            Map((int)UfbxMaterialPbrMap.TransmissionEnableInAov, 0, 0, "transmitAovs"),
        };

        // C: ufbxi_arnold_shader_features (ufbx.c:19620-19626).
        static readonly UfbxiShaderMapping[] ArnoldShaderFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.Matte, 0, 0, "aiEnableMatte"),
            Map((int)UfbxMaterialFeature.ThinWalled, 0, 0, "thinWalled"),
            Map((int)UfbxMaterialFeature.Caustics, 0, 0, "caustics"),
            Map((int)UfbxMaterialFeature.InternalReflections, 0, 0, "internalReflections"),
            Map((int)UfbxMaterialFeature.ExitToBackground, 0, 0, "exitToBackground"),
        };

        // C: ufbxi_3ds_max_physical_material_pbr_mapping (ufbx.c:19628-19669).
        static readonly UfbxiShaderMapping[] Max3dsPhysicalMaterialPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseFactor, 0, 0, "base_weight"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "base_color"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "roughness"),
            Map((int)UfbxMaterialPbrMap.DiffuseRoughness, 0, 0, "diff_rough"),
            Map((int)UfbxMaterialPbrMap.DiffuseRoughness, 0, 0, "diff_roughness"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "metalness"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "reflectivity"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "refl_color"),
            Map((int)UfbxMaterialPbrMap.SpecularAnisotropy, 0, 0, "anisotropy"),
            Map((int)UfbxMaterialPbrMap.SpecularRotation, 0, 0, "aniso_angle"),
            Map((int)UfbxMaterialPbrMap.SpecularRotation, 0, 0, "anisoangle"),
            Map((int)UfbxMaterialPbrMap.SpecularIor, 0, 0, "trans_ior"), // NOTE: Not a typo, IOR is same for transparency/specular
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "transparency"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "trans_color"),
            Map((int)UfbxMaterialPbrMap.TransmissionDepth, 0, 0, "trans_depth"),
            Map((int)UfbxMaterialPbrMap.TransmissionRoughness, 0, 0, "trans_rough"),
            Map((int)UfbxMaterialPbrMap.TransmissionRoughness, 0, 0, "trans_roughness"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceFactor, 0, 0, "scattering"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceTintColor, ShaderMappingDefaultW1, 0, "sss_color"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceColor, ShaderMappingDefaultW1, 0, "sss_scatter_color"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceRadius, ShaderMappingWidenToRgb, 0, "sss_depth"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceScale, 0, 0, "sss_scale"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "coat"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "coating"),
            Map((int)UfbxMaterialPbrMap.CoatColor, ShaderMappingDefaultW1, 0, "coat_color"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "coat_rough"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "coat_roughness"),
            Map((int)UfbxMaterialPbrMap.CoatIor, 0, 0, "coat_ior"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "coat_bump"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "clearcoat_bump_map_amt"),
            Map((int)UfbxMaterialPbrMap.CoatAffectBaseColor, ShaderMappingDefaultW1, 0, "coat_affect_color"),
            Map((int)UfbxMaterialPbrMap.CoatAffectBaseRoughness, 0, 0, "coat_affect_roughness"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "emission"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emit_color"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "cutout"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "bump"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "bump_map_amt"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement_map_amt"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceType, 0, 0, "subsurfaceType"),
        };

        // C: ufbxi_3ds_max_physical_material_features (ufbx.c:19671-19679).
        static readonly UfbxiShaderMapping[] Max3dsPhysicalMaterialFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.ThinWalled, 0, 0, "thin_walled"),
            Map((int)UfbxMaterialFeature.Specular, 0, 0, "material_mode"),
            Map((int)UfbxMaterialFeature.DiffuseRoughness, 0, 0, "material_mode"),
            Map((int)UfbxMaterialFeature.TransmissionRoughness, ShaderFeatureInverted, 0, "trans_roughness_lock"),
            Map((int)UfbxMaterialFeature.RoughnessAsGlossiness, 0, 0, "roughness_inv"),
            Map((int)UfbxMaterialFeature.TransmissionRoughnessAsGlossiness, 0, 0, "trans_roughness_inv"),
            Map((int)UfbxMaterialFeature.CoatRoughnessAsGlossiness, 0, 0, "coat_roughness_inv"),
        };

        // C: ufbxi_gltf_material_pbr_mapping (ufbx.c:19681-19701).
        static readonly UfbxiShaderMapping[] GltfMaterialPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "main|baseColor"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "main|roughness"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "main|metalness"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "main|normal"),
            Map((int)UfbxMaterialPbrMap.AmbientOcclusion, 0, 0, "main|ambientOcclusion"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "main|emission"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "main|emissionColor"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "main|Alpha"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "extension|clearcoat"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "extension|clearcoatRoughness"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "extension|clearcoatNormal"),
            Map((int)UfbxMaterialPbrMap.SheenColor, ShaderMappingDefaultW1, 0, "extension|sheenColor"),
            Map((int)UfbxMaterialPbrMap.SheenRoughness, 0, 0, "extension|sheenRoughness"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "extension|specular"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "extension|Specular"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "extension|specularcolor"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "extension|specularColor"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "extension|transmission"),
            Map((int)UfbxMaterialPbrMap.SpecularIor, 0, 0, "extension|indexOfRefraction"),
        };

        // C: ufbxi_openpbr_material_pbr_mapping (ufbx.c:19703-19747).
        static readonly UfbxiShaderMapping[] OpenPbrMaterialPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseFactor, 0, 0, "base_weight"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "base_color"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "specular_roughness"),
            Map((int)UfbxMaterialPbrMap.DiffuseRoughness, 0, 0, "base_diffuse_roughness"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "base_metalness"),
            Map((int)UfbxMaterialPbrMap.SpecularFactor, 0, 0, "specular_weight"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "specular_color"),
            Map((int)UfbxMaterialPbrMap.SpecularAnisotropy, 0, 0, "specular_roughness_anisotropy"),
            Map((int)UfbxMaterialPbrMap.SpecularIor, 0, 0, "specular_ior"),
            Map((int)UfbxMaterialPbrMap.TransmissionFactor, 0, 0, "transmission_weight"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1, 0, "transmission_color"),
            Map((int)UfbxMaterialPbrMap.TransmissionDepth, 0, 0, "transmission_depth"),
            Map((int)UfbxMaterialPbrMap.TransmissionScatter, ShaderMappingWidenToRgb, 0, "transmission_scatter"),
            Map((int)UfbxMaterialPbrMap.TransmissionScatterAnisotropy, 0, 0, "transmission_scatter_anisotropy"),
            Map((int)UfbxMaterialPbrMap.TransmissionDispersion, 0, 0, "transmission_dispersion_scale"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceFactor, 0, 0, "subsurface_weight"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceColor, ShaderMappingDefaultW1, 0, "subsurface_color"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceRadius, ShaderMappingWidenToRgb, 0, "subsurface_radius_scale"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceScale, 0, 0, "subsurface_radius"),
            Map((int)UfbxMaterialPbrMap.SubsurfaceAnisotropy, 0, 0, "subsurface_scatter_anisotropy"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "coat_weight"),
            Map((int)UfbxMaterialPbrMap.CoatColor, ShaderMappingDefaultW1, 0, "coat_color"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "coat_roughness"),
            Map((int)UfbxMaterialPbrMap.CoatAnisotropy, 0, 0, "coat_roughness_anisotropy"),
            Map((int)UfbxMaterialPbrMap.CoatIor, 0, 0, "coat_ior"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "coat_normal_map"),
            Map((int)UfbxMaterialPbrMap.SheenFactor, 0, 0, "fuzz_weight"),
            Map((int)UfbxMaterialPbrMap.SheenColor, ShaderMappingDefaultW1, 0, "fuzz_color"),
            Map((int)UfbxMaterialPbrMap.SheenRoughness, 0, 0, "fuzz_roughness"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "emission_weight"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, ShaderMappingMultiplyValue, 0, "emission_luminance"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emission_color"),
            Map((int)UfbxMaterialPbrMap.ThinFilmFactor, 0, 0, "thin_film_weight"),
            Map((int)UfbxMaterialPbrMap.ThinFilmThickness, 0, 0, "thin_film_thickness"),
            Map((int)UfbxMaterialPbrMap.ThinFilmIor, 0, 0, "thin_film_ior"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "bump"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "bump_map_amt"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement_map_amt"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "coat_bump"),
            Map((int)UfbxMaterialPbrMap.CoatNormal, 0, 0, "coat_bump_map_amt"),
            Map((int)UfbxMaterialPbrMap.TangentMap, 0, 0, "geometry_tangent_map"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "geometry_opacity"),
        };

        // C: ufbxi_openpbr_material_features (ufbx.c:19749-19751).
        static readonly UfbxiShaderMapping[] OpenPbrMaterialFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.ThinWalled, 0, 0, "geometry_thin_walled"),
        };

        // C: ufbxi_3ds_max_pbr_metal_rough_pbr_mapping (ufbx.c:19753-19765).
        static readonly UfbxiShaderMapping[] Max3dsPbrMetalRoughPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "base_color"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "baseColor"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "roughness"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "Roughness_Map"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "metalness"),
            Map((int)UfbxMaterialPbrMap.AmbientOcclusion, 0, 0, "ao"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "norm"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emit_color"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement_amt"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "opacity"),
        };

        // C: ufbxi_3ds_max_pbr_spec_gloss_pbr_mapping (ufbx.c:19767-19779).
        static readonly UfbxiShaderMapping[] Max3dsPbrSpecGlossPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "base_color"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "baseColor"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "Specular"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1, 0, "specular"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "glossiness"),
            Map((int)UfbxMaterialPbrMap.AmbientOcclusion, 0, 0, "ao"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "norm"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emit_color"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "displacement_amt"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "opacity"),
        };

        // C: ufbxi_3ds_max_pbr_features (ufbx.c:19781-19783).
        static readonly UfbxiShaderMapping[] Max3dsPbrFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.RoughnessAsGlossiness, ShaderFeatureIfAround1, 0, "useGlossiness"),
        };

        // C: ufbxi_gltf_material_features (ufbx.c:19785-19793).
        static readonly UfbxiShaderMapping[] GltfMaterialFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.DoubleSided, 0, 0, "main|DoubleSided"),
            Map((int)UfbxMaterialFeature.Sheen, 0, 0, "extension|enableSheen"),
            Map((int)UfbxMaterialFeature.Coat, 0, 0, "extension|enableClearCoat"),
            Map((int)UfbxMaterialFeature.Transmission, 0, 0, "extension|enableTransmission"),
            Map((int)UfbxMaterialFeature.Ior, 0, 0, "extension|enableIndexOfRefraction"),
            Map((int)UfbxMaterialFeature.Specular, 0, 0, "extension|enableSpecular"),
            Map((int)UfbxMaterialFeature.Unlit, 0, 0, "extension|unlit"),
        };

        // C: ufbxi_shaderfx_graph_pbr_mapping (ufbx.c:19797-19806).
        static readonly UfbxiShaderMapping[] ShaderFxGraphPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "color"),
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "base_color"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "roughness"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "metallic"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "normal"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "emissive_intensity"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "emissive"),
            Map((int)UfbxMaterialPbrMap.AmbientOcclusion, 0, 0, "ao"),
        };

        // C: ufbxi_blender_phong_shader_pbr_mapping (ufbx.c:19808-19817).
        static readonly UfbxiShaderMapping[] BlenderPhongShaderPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1, 0, "DiffuseColor"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, MatTransformBlenderOpacityId, "TransparencyFactor"),
            Map((int)UfbxMaterialPbrMap.EmissionFactor, 0, 0, "EmissiveFactor"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1, 0, "EmissiveColor"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, MatTransformBlenderShininessId, "Shininess"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, MatTransformBlenderShininessId, "ShininessExponent"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "ReflectionFactor"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "NormalMap"),
        };

        // C: ufbxi_obj_pbr_mapping (ufbx.c:19819-19838).
        static readonly UfbxiShaderMapping[] ObjPbrMapping = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialPbrMap.BaseColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Kd"),
            Map((int)UfbxMaterialPbrMap.SpecularColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Ks"),
            Map((int)UfbxMaterialPbrMap.EmissionColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Ke"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, MatTransformUnknownShininessId, "Ns"),
            Map((int)UfbxMaterialPbrMap.Roughness, 0, 0, "Pr"),
            Map((int)UfbxMaterialPbrMap.SpecularIor, 0, 0, "Ni"),
            Map((int)UfbxMaterialPbrMap.Metalness, 0, 0, "Pm"),
            Map((int)UfbxMaterialPbrMap.Opacity, ShaderMappingWidenToRgb, 0, "d"),
            Map((int)UfbxMaterialPbrMap.TransmissionColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Tf"),
            Map((int)UfbxMaterialPbrMap.DisplacementMap, 0, 0, "disp"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "bump"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "Bump"),
            Map((int)UfbxMaterialPbrMap.NormalMap, 0, 0, "norm"),
            Map((int)UfbxMaterialPbrMap.SheenColor, ShaderMappingDefaultW1|ShaderMappingWidenToRgb, 0, "Ps"),
            Map((int)UfbxMaterialPbrMap.CoatFactor, 0, 0, "Pc"),
            Map((int)UfbxMaterialPbrMap.CoatRoughness, 0, 0, "Pcr"),
            Map((int)UfbxMaterialPbrMap.SpecularAnisotropy, 0, 0, "aniso"),
            Map((int)UfbxMaterialPbrMap.SpecularRotation, 0, 0, "anisor"),
        };

        // C: ufbxi_obj_features (ufbx.c:19840-19850).
        static readonly UfbxiShaderMapping[] ObjFeatures = new UfbxiShaderMapping[] {
            Map((int)UfbxMaterialFeature.Pbr, ShaderFeatureIfExistsOrTexture, 0, "Pr"),
            Map((int)UfbxMaterialFeature.Pbr, ShaderFeatureIfExistsOrTexture, 0, "Pm"),
            Map((int)UfbxMaterialFeature.Sheen, ShaderFeatureIfExistsOrTexture, 0, "Ps"),
            Map((int)UfbxMaterialFeature.Coat, ShaderFeatureIfExistsOrTexture, 0, "Pc"),
            Map((int)UfbxMaterialFeature.Metalness, ShaderFeatureIfExistsOrTexture, 0, "Pm"),
            Map((int)UfbxMaterialFeature.Ior, ShaderFeatureIfExistsOrTexture, 0, "Ni"),
            Map((int)UfbxMaterialFeature.Opacity, ShaderFeatureIfExistsOrTexture, 0, "d"),
            Map((int)UfbxMaterialFeature.Transmission, ShaderFeatureIfExistsOrTexture, 0, "Tf"),
            Map((int)UfbxMaterialFeature.Emission, ShaderFeatureIfExistsOrTexture, 0, "Ke"),
        };

        // C: UFBXI_MAT_* feature-bit constants (ufbx.c:19852-19873), composed per list.
        const uint MatPbr = 1u << (int)UfbxMaterialFeature.Pbr;
        const uint MatMetalness = 1u << (int)UfbxMaterialFeature.Metalness;
        const uint MatDiffuse = 1u << (int)UfbxMaterialFeature.Diffuse;
        const uint MatSpecular = 1u << (int)UfbxMaterialFeature.Specular;
        const uint MatEmission = 1u << (int)UfbxMaterialFeature.Emission;
        const uint MatCoat = 1u << (int)UfbxMaterialFeature.Coat;
        const uint MatSheen = 1u << (int)UfbxMaterialFeature.Sheen;
        const uint MatTransmission = 1u << (int)UfbxMaterialFeature.Transmission;
        const uint MatOpacity = 1u << (int)UfbxMaterialFeature.Opacity;
        const uint MatAmbientOcclusion = 1u << (int)UfbxMaterialFeature.AmbientOcclusion;
        const uint MatIor = 1u << (int)UfbxMaterialFeature.Ior;
        const uint MatDiffuseRoughness = 1u << (int)UfbxMaterialFeature.DiffuseRoughness;

        // C: ufbxi_shader_pbr_mappings[] (ufbx.c:19875-19957), in UFBX_SHADER_* enum order.
        static readonly UfbxiShaderMappingList[] ShaderPbrMappings = new UfbxiShaderMappingList[] {
            // UFBX_SHADER_UNKNOWN
            new UfbxiShaderMappingList {
                Data = FbxPhongShaderPbrMapping, Features = null, DefaultFeatures = MatDiffuse | MatSpecular | MatEmission | MatTransmission,
            },
            // UFBX_SHADER_FBX_LAMBERT
            new UfbxiShaderMappingList {
                Data = FbxLambertShaderPbrMapping, Features = null, DefaultFeatures = MatDiffuse | MatEmission | MatTransmission,
            },
            // UFBX_SHADER_FBX_PHONG
            new UfbxiShaderMappingList {
                Data = FbxPhongShaderPbrMapping, Features = null, DefaultFeatures = MatDiffuse | MatSpecular | MatEmission | MatTransmission,
            },
            // UFBX_SHADER_OSL_STANDARD_SURFACE
            new UfbxiShaderMappingList {
                Data = OslStandardShaderPbrMapping, Features = OslStandardShaderFeatures,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatSpecular | MatCoat | MatSheen | MatTransmission | MatOpacity | MatIor | MatDiffuseRoughness,
            },
            // UFBX_SHADER_ARNOLD_STANDARD_SURFACE
            new UfbxiShaderMappingList {
                Data = ArnoldShaderPbrMapping, Features = ArnoldShaderFeatures,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatSpecular | MatCoat | MatSheen | MatTransmission | MatOpacity | MatIor | MatDiffuseRoughness,
            },
            // UFBX_SHADER_3DS_MAX_PHYSICAL_MATERIAL
            new UfbxiShaderMappingList {
                Data = Max3dsPhysicalMaterialPbrMapping, Features = Max3dsPhysicalMaterialFeatures,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatCoat | MatSheen | MatTransmission | MatOpacity | MatIor,
                TexturePrefix = "", TextureSuffix = "_map",
                TextureEnabledPrefix = "", TextureEnabledSuffix = "_map_on",
            },
            // UFBX_SHADER_3DS_MAX_PBR_METAL_ROUGH
            new UfbxiShaderMappingList {
                Data = Max3dsPbrMetalRoughPbrMapping, Features = Max3dsPbrFeatures,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatOpacity,
                TexturePrefix = "", TextureSuffix = "_map",
            },
            // UFBX_SHADER_3DS_MAX_PBR_SPEC_GLOSS
            new UfbxiShaderMappingList {
                Data = Max3dsPbrSpecGlossPbrMapping, Features = Max3dsPbrFeatures,
                DefaultFeatures = MatPbr | MatSpecular | MatDiffuse | MatOpacity,
                TexturePrefix = "", TextureSuffix = "_map",
            },
            // UFBX_SHADER_GLTF_MATERIAL
            new UfbxiShaderMappingList {
                Data = GltfMaterialPbrMapping, Features = GltfMaterialFeatures,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatEmission | MatOpacity | MatAmbientOcclusion,
                TexturePrefix = "", TextureSuffix = "Map",
            },
            // UFBX_SHADER_OPENPBR_MATERIAL
            new UfbxiShaderMappingList {
                Data = OpenPbrMaterialPbrMapping, Features = OpenPbrMaterialFeatures,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatSpecular | MatCoat | MatSheen | MatTransmission | MatOpacity | MatIor | MatDiffuseRoughness,
                TexturePrefix = "", TextureSuffix = "_map",
                TextureEnabledPrefix = "", TextureEnabledSuffix = "_map_on",
            },
            // UFBX_SHADER_SHADERFX_GRAPH
            new UfbxiShaderMappingList {
                Data = ShaderFxGraphPbrMapping, Features = null,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatEmission | MatAmbientOcclusion,
                TexturePrefix = "TEX_", TextureSuffix = "_map",
                TextureEnabledPrefix = "use_", TextureEnabledSuffix = "_map",
            },
            // UFBX_SHADER_BLENDER_PHONG
            new UfbxiShaderMappingList {
                Data = BlenderPhongShaderPbrMapping, Features = null,
                DefaultFeatures = MatPbr | MatMetalness | MatDiffuse | MatEmission,
            },
            // UFBX_SHADER_WAVEFRONT_MTL
            new UfbxiShaderMappingList {
                Data = ObjPbrMapping, Features = ObjFeatures,
                DefaultFeatures = MatDiffuse | MatSpecular,
            },
        };

        // C: UFBXI_MAPPING_* fetch flags (ufbx.c:19961-19966).
        internal const uint MappingFetchValue = 0x1;
        internal const uint MappingFetchTexture = 0x2;
        internal const uint MappingFetchTextureEnabled = 0x4;
        internal const uint MappingFetchFeature = 0x8;

        // ==================================================================
        // ufbxi_fetch_mapping_maps (ufbx.c:19968-20093)
        // ==================================================================

        // C: ufbxi_fetch_mapping_maps(). `maps` is `material->fbx.maps`/`material->pbr.maps`
        // (null when fetching features); `features` is `material->features.features`.
        static void FetchMappingMaps(UfbxMaterial material, UfbxMaterialMap[] maps, UfbxMaterialFeatureInfo[] features,
            UfbxShader shader, UfbxiShaderMapping[] mappings, string prefix, string prefix2, string suffix, uint flags)
        {
            if (mappings == null) return; // C: count == 0
            foreach (UfbxiShaderMapping mapping in mappings) {
                string propName = mapping.Prop;
                if (prefix.Length > 0 || prefix2.Length > 0 || suffix.Length > 0) {
                    // C: build into `combined_name[512]`, keep the uncombined name if it
                    // does not fit.
                    if (propName.Length + prefix.Length + prefix2.Length + suffix.Length <= 512) {
                        propName = prefix + prefix2 + propName + suffix;
                    }
                }

                UfbxShaderPropBinding[] bindings = FindShaderPropBindings(shader, propName);
                if (bindings == null || bindings.Length == 0) {
                    // C: identity_binding { material_prop = prop_name, shader_prop = empty }
                    bindings = new UfbxShaderPropBinding[] {
                        new UfbxShaderPropBinding { MaterialProp = propName, ShaderProp = string.Empty },
                    };
                }

                uint mappingFlags = mapping.Flags;
                foreach (UfbxShaderPropBinding binding in bindings) {
                    string name = binding.MaterialProp;

                    bool hasProp = UfbxiProperties.TryFindPropLen(material.Props, name, out UfbxProps owner, out int propIndex);
                    UfbxProp prop = hasProp ? owner.Props[propIndex] : default;
                    if (prop.Name == null) {
                        // Normalize "no prop" to Name == null (the port's NULL-prop marker).
                        prop = default;
                    }

                    if ((flags & MappingFetchFeature) != 0) {
                        UfbxMaterialFeatureInfo feature = features[mapping.Index];
                        if (hasProp && prop.Type != UfbxPropType.Reference) {
                            feature.Enabled = prop.ValueInt != 0;
                            feature.IsExplicit = true;
                            if ((mappingFlags & ShaderFeatureIfAround1) != 0) {
                                feature.Enabled = prop.ValueReal >= 0.5 && prop.ValueReal <= 1.5;
                            }
                            if ((mappingFlags & ShaderFeatureInverted) != 0) {
                                feature.Enabled = !feature.Enabled;
                            }
                            if ((mappingFlags & ShaderFeatureIfExists) != 0) {
                                feature.Enabled = true;
                            }
                        }
                        if ((mappingFlags & ShaderFeatureIfTexture) != 0) {
                            UfbxTexture texture = FindPropTexture(material, name);
                            if (texture != null) {
                                feature.Enabled = true;
                            }
                        }
                        continue;
                    }

                    UfbxMaterialMap map = maps[mapping.Index];

                    if ((flags & MappingFetchValue) != 0) {
                        if (hasProp && prop.Type != UfbxPropType.Reference) {
                            if ((mapping.Flags & ShaderMappingMultiplyValue) != 0) {
                                map.ValueVec4.X *= prop.ValueVec4.X;
                                map.ValueInt = UfbxiBinaryArray.F64ToInt64(map.ValueVec4.X);
                            } else {
                                map.ValueVec4 = prop.ValueVec4;
                                map.ValueInt = prop.ValueInt;
                            }
                            map.HasValue = true;
                            if (mapping.Transform != 0) {
                                ApplyMatTransform(mapping.Transform, ref map.ValueVec4);
                            }

                            uint propFlags = (uint)prop.Flags;
                            if ((mapping.Flags & ShaderMappingDefaultW1) != 0 && (propFlags & (uint)UfbxPropFlags.ValueVec4) == 0) {
                                map.ValueVec4.W = 1.0;
                            }
                            if ((mapping.Flags & ShaderMappingWidenToRgb) != 0 && (propFlags & (uint)UfbxPropFlags.ValueReal) != 0) {
                                map.ValueVec4.Y = map.ValueVec4.X;
                                map.ValueVec4.Z = map.ValueVec4.X;
                            }
                            if ((propFlags & (uint)UfbxPropFlags.ValueReal) != 0) {
                                map.ValueComponents = 1;
                            } else if ((propFlags & (uint)UfbxPropFlags.ValueVec2) != 0) {
                                map.ValueComponents = 2;
                            } else if ((propFlags & (uint)UfbxPropFlags.ValueVec3) != 0) {
                                map.ValueComponents = 3;
                            } else if ((propFlags & (uint)UfbxPropFlags.ValueVec4) != 0) {
                                map.ValueComponents = 4;
                            } else {
                                map.ValueComponents = 0;
                            }
                        }
                    }

                    if ((flags & MappingFetchTexture) != 0) {
                        UfbxTexture texture = FindPropTexture(material, name);
                        if (texture != null) {
                            map.Texture = texture;
                            map.TextureEnabled = true;
                        }
                    }

                    if ((flags & MappingFetchTextureEnabled) != 0) {
                        if (hasProp) {
                            map.TextureEnabled = prop.ValueInt != 0;
                        }
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_update_factor / ufbxi_fetch_maps (ufbx.c:20095-20215)
        // ==================================================================

        // C: ufbxi_update_factor (ufbx.c:20095-20106).
        static void UpdateFactor(UfbxMaterialMap factorMap, UfbxMaterialMap colorMap)
        {
            if (!factorMap.HasValue) {
                if (colorMap.HasValue && !UfbxiProperties.IsVec4Zero(colorMap.ValueVec4)) {
                    factorMap.ValueReal = 1.0;
                    factorMap.ValueInt = 1;
                } else {
                    factorMap.ValueReal = 0.0;
                    factorMap.ValueInt = 0;
                }
            }
        }

        // C: ufbxi_glossiness_remap / ufbxi_glossiness_remaps[] (ufbx.c:20108-20121).
        struct UfbxiGlossinessRemap
        {
            public UfbxMaterialFeature Feature;
            public UfbxMaterialPbrMap RoughnessMap;
            public UfbxMaterialPbrMap GlossinessMap;
        }

        static readonly UfbxiGlossinessRemap[] GlossinessRemaps = new UfbxiGlossinessRemap[] {
            new UfbxiGlossinessRemap { Feature = UfbxMaterialFeature.RoughnessAsGlossiness, RoughnessMap = UfbxMaterialPbrMap.Roughness, GlossinessMap = UfbxMaterialPbrMap.Glossiness },
            new UfbxiGlossinessRemap { Feature = UfbxMaterialFeature.CoatRoughnessAsGlossiness, RoughnessMap = UfbxMaterialPbrMap.CoatRoughness, GlossinessMap = UfbxMaterialPbrMap.CoatGlossiness },
            new UfbxiGlossinessRemap { Feature = UfbxMaterialFeature.TransmissionRoughnessAsGlossiness, RoughnessMap = UfbxMaterialPbrMap.TransmissionRoughness, GlossinessMap = UfbxMaterialPbrMap.TransmissionGlossiness },
        };

        // C: memset of one `ufbx_material_map` (fetch_maps resets fbx/pbr tables each call).
        static void ResetMap(UfbxMaterialMap map)
        {
            map.ValueVec4 = default;
            map.ValueInt = 0;
            map.Texture = null;
            map.HasValue = false;
            map.TextureEnabled = false;
            map.FeatureDisabled = false;
            map.ValueComponents = 0;
        }

        // C: ufbxi_fetch_maps (ufbx.c:20123-20215).
        internal static void FetchMaps(UfbxScene scene, UfbxMaterial material)
        {
            UfbxShader shader = material.Shader;

            foreach (UfbxMaterialMap map in material.Fbx.Maps) ResetMap(map);
            foreach (UfbxMaterialMap map in material.Pbr.Maps) ResetMap(map);
            foreach (UfbxMaterialFeatureInfo feature in material.Features.Features) {
                feature.Enabled = false;
                feature.IsExplicit = false;
            }

            UfbxiShaderMapping[] baseMapping = BaseFbxMapping;

            if (scene.Metadata.FileFormat == UfbxFileFormat.Obj || scene.Metadata.FileFormat == UfbxFileFormat.Mtl) {
                baseMapping = ObjFbxMapping;
            }

            FetchMappingMaps(material, material.Fbx.Maps, null, null,
                baseMapping, string.Empty, string.Empty, string.Empty,
                MappingFetchValue | MappingFetchTexture);

            UfbxiShaderMappingList list = ShaderPbrMappings[(int)material.ShaderType];
            // C: unset prefixes are `{ NULL, 0 }`; normalize to empty strings.
            if (list.TexturePrefix == null) list.TexturePrefix = string.Empty;
            if (list.TextureSuffix == null) list.TextureSuffix = string.Empty;
            if (list.TextureEnabledPrefix == null) list.TextureEnabledPrefix = string.Empty;
            if (list.TextureEnabledSuffix == null) list.TextureEnabledSuffix = string.Empty;

            uint numFeatures = (uint)UfbxEnumCounts.UfbxMaterialFeature;
            for (uint i = 0; i < numFeatures; i++) {
                if ((list.DefaultFeatures & (1u << (int)i)) != 0) {
                    material.Features.Features[i].Enabled = true;
                }
            }

            string prefix = string.Empty;
            if (shader == null) {
                prefix = material.ShaderPropPrefix != null ? material.ShaderPropPrefix : string.Empty;
            }

            if (list.TexturePrefix.Length > 0 || list.TextureSuffix.Length > 0) {
                FetchMappingMaps(material, material.Pbr.Maps, null, shader,
                    list.Data, prefix, list.TexturePrefix, list.TextureSuffix,
                    MappingFetchTexture);
            }

            FetchMappingMaps(material, material.Pbr.Maps, null, shader,
                list.Data, prefix, string.Empty, string.Empty,
                MappingFetchValue | MappingFetchTexture);

            if (list.TextureEnabledPrefix.Length > 0 || list.TextureEnabledSuffix.Length > 0) {
                FetchMappingMaps(material, material.Pbr.Maps, null, shader,
                    list.Data, prefix, list.TextureEnabledPrefix, list.TextureEnabledSuffix,
                    MappingFetchTextureEnabled);
            }

            FetchMappingMaps(material, null, material.Features.Features, shader,
                list.Features, prefix, string.Empty, string.Empty,
                MappingFetchFeature);

            UpdateFactor(material.Fbx.DiffuseFactor, material.Fbx.DiffuseColor);
            UpdateFactor(material.Fbx.SpecularFactor, material.Fbx.SpecularColor);
            UpdateFactor(material.Fbx.ReflectionFactor, material.Fbx.ReflectionColor);
            UpdateFactor(material.Fbx.TransparencyFactor, material.Fbx.TransparencyColor);
            UpdateFactor(material.Fbx.EmissionFactor, material.Fbx.EmissionColor);
            UpdateFactor(material.Fbx.AmbientFactor, material.Fbx.AmbientColor);

            UpdateFactor(material.Pbr.BaseFactor, material.Pbr.BaseColor);
            UpdateFactor(material.Pbr.SpecularFactor, material.Pbr.SpecularColor);
            UpdateFactor(material.Pbr.EmissionFactor, material.Pbr.EmissionColor);
            UpdateFactor(material.Pbr.SheenFactor, material.Pbr.SheenColor);
            UpdateFactor(material.Pbr.ThinFilmFactor, material.Pbr.ThinFilmThickness);
            UpdateFactor(material.Pbr.TransmissionFactor, material.Pbr.TransmissionColor);

            // Patch transmission roughness if only extra roughness is defined
            if (!material.Pbr.TransmissionRoughness.HasValue && material.Pbr.Roughness.HasValue && material.Pbr.TransmissionExtraRoughness.HasValue) {
                material.Pbr.TransmissionRoughness.ValueReal = material.Pbr.Roughness.ValueReal + material.Pbr.TransmissionExtraRoughness.ValueReal;
            }

            // Map roughness to glossiness and vice versa
            foreach (UfbxiGlossinessRemap remap in GlossinessRemaps) {
                UfbxMaterialMap roughness = material.Pbr.Maps[(int)remap.RoughnessMap];
                UfbxMaterialMap glossiness = material.Pbr.Maps[(int)remap.GlossinessMap];
                if (material.Features.Features[(int)remap.Feature].Enabled) {
                    // C: *glossiness = *roughness; memset(roughness, 0, ...)
                    UfbxMaterialMap src = roughness;
                    CopyMap(ref glossiness, ref src);
                    ResetMap(roughness);
                    if (glossiness.HasValue) {
                        roughness.ValueReal = 1.0 - glossiness.ValueReal;
                    }
                } else {
                    if (roughness.HasValue) {
                        glossiness.ValueReal = 1.0 - roughness.ValueReal;
                    }
                }
            }
        }

        // C: struct assignment `*glossiness = *roughness`.
        static void CopyMap(ref UfbxMaterialMap dst, ref UfbxMaterialMap src)
        {
            dst.ValueVec4 = src.ValueVec4;
            dst.ValueInt = src.ValueInt;
            dst.Texture = src.Texture;
            dst.HasValue = src.HasValue;
            dst.TextureEnabled = src.TextureEnabled;
            dst.FeatureDisabled = src.FeatureDisabled;
            dst.ValueComponents = src.ValueComponents;
        }

        // ==================================================================
        // Constraint props (ufbx.c:20217-20265)
        // ==================================================================

        // C: ufbxi_constraint_prop_type (ufbx.c:20217-20223).
        const int ConstraintPropNode = 0;
        const int ConstraintPropIkEffector = 1;
        const int ConstraintPropIkEndNode = 2;
        const int ConstraintPropAimUp = 3;
        const int ConstraintPropTarget = 4;

        // C: ufbxi_constraint_prop / ufbxi_constraint_props[] (ufbx.c:20225-20241).
        struct UfbxiConstraintProp
        {
            public int Type;
            public string Name;
        }

        static readonly UfbxiConstraintProp[] ConstraintProps = new UfbxiConstraintProp[] {
            new UfbxiConstraintProp { Type = ConstraintPropNode, Name = "Constrained Object" },
            new UfbxiConstraintProp { Type = ConstraintPropNode, Name = "Constrained object (Child)" },
            new UfbxiConstraintProp { Type = ConstraintPropNode, Name = "First Joint" },
            new UfbxiConstraintProp { Type = ConstraintPropTarget, Name = "Source" },
            new UfbxiConstraintProp { Type = ConstraintPropTarget, Name = "Source (Parent)" },
            new UfbxiConstraintProp { Type = ConstraintPropTarget, Name = "Aim At Object" },
            new UfbxiConstraintProp { Type = ConstraintPropTarget, Name = "Pole Vector Object" },
            new UfbxiConstraintProp { Type = ConstraintPropIkEffector, Name = "Effector" },
            new UfbxiConstraintProp { Type = ConstraintPropIkEndNode, Name = "End Joint" },
            new UfbxiConstraintProp { Type = ConstraintPropAimUp, Name = "World Up Object" },
        };

        // C: ufbxi_add_constraint_prop (ufbx.c:20243-20265). The pushed target is appended to
        // the caller's scratch list (C uses `uc->tmp_stack`).
        internal static void AddConstraintProp(List<UfbxConstraintTarget> targets, UfbxConstraint constraint, UfbxNode node, string prop)
        {
            foreach (UfbxiConstraintProp cprop in ConstraintProps) {
                if (cprop.Name != prop) continue;
                switch (cprop.Type) {
                case ConstraintPropNode: constraint.Node = node; break;
                case ConstraintPropIkEffector: constraint.IkEffector = node; break;
                case ConstraintPropIkEndNode: constraint.IkEndNode = node; break;
                case ConstraintPropAimUp: constraint.AimUpNode = node; break;
                case ConstraintPropTarget: {
                    UfbxConstraintTarget target = new UfbxConstraintTarget();
                    target.Node = node;
                    target.Weight = 1.0;
                    target.Transform = UfbxTransform.Identity;
                    targets.Add(target);
                } break;
                }
            }
        }

        // ==================================================================
        // ufbxi_finalize_nurbs_basis (ufbx.c:20267-20314)
        // ==================================================================

        internal static void FinalizeNurbsBasis(ref UfbxNurbsBasis basis)
        {
            // Check that the basis is reasonable, and so we don't overflow in later code.
            UfbxiFail.CheckNoDesc(basis.Order < uint.MaxValue / 4, "basis->order < UINT32_MAX / 4");

            if (basis.Topology == UfbxNurbsTopology.Closed) {
                basis.NumWrapControlPoints = 1;
            } else if (basis.Topology == UfbxNurbsTopology.Periodic) {
                basis.NumWrapControlPoints = (int)basis.Order - 1;
            } else {
                basis.NumWrapControlPoints = 0;
            }

            if (basis.Order > 1) {
                uint degree = basis.Order - 1;
                double[] knots = basis.KnotVector;
                int knotCount = knots != null ? knots.Length : 0;
                if (knotCount >= (int)(2 * degree + 1)) {
                    basis.TMin = knots[degree];
                    basis.TMax = knots[knotCount - (int)degree - 1];

                    int maxSpans = knotCount - (int)(2 * degree);
                    double[] spans = new double[maxSpans];

                    double prev = double.NegativeInfinity; // C: -UFBX_INFINITY
                    int numSpans = 0;
                    for (int i = 0; i < maxSpans; i++) {
                        double t = knots[degree + i];
                        if (t != prev) {
                            spans[numSpans++] = t;
                            prev = t;
                        }
                    }

                    if (numSpans != spans.Length) {
                        Array.Resize(ref spans, numSpans);
                    }
                    basis.Spans = spans;
                    basis.Valid = true;
                    for (int i = 1; i < knotCount; i++) {
                        if (knots[i - 1] > knots[i]) {
                            basis.Valid = false;
                            break;
                        }
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_finalize_lod_group (ufbx.c:20316-20364)
        // ==================================================================

        internal static void FinalizeLodGroup(UfbxLodGroup lod)
        {
            int numLevels = 0;
            if (lod.Instances != null) {
                for (int i = 0; i < lod.Instances.Length; i++) {
                    // C: `num_levels = ufbxi_max_sz(num_levels, lod->instances.data[0]->children.count)`
                    // -- indexes data[0] unconditionally inside the loop (kept verbatim).
                    int childCount = lod.Instances[0].Children != null ? lod.Instances[0].Children.Length : 0;
                    if (numLevels < childCount) numLevels = childCount;
                }
            }

            for (int i = 0; ; i++) {
                string propName = "Thresholds|Level" + i.ToString();
                bool found = UfbxiProperties.TryFindPropLen(lod.Props, propName, out UfbxProps owner, out int index);
                if (!found) break;
                if (numLevels < i + 1) numLevels = i + 1;
            }

            UfbxLodLevel[] levels = new UfbxLodLevel[numLevels];

            lod.RelativeDistances = FindBoolLen(lod.Props, "ThresholdsUsedAsPercentage", false);
            lod.IgnoreParentTransform = !FindBoolLen(lod.Props, "WorldSpace", true);

            lod.UseDistanceLimit = FindBoolLen(lod.Props, "MinMaxDistance", false);
            lod.DistanceLimitMin = FindRealLen(lod.Props, "MinDistance", -100.0);
            lod.DistanceLimitMax = FindRealLen(lod.Props, "MaxDistance", 100.0);

            lod.LodLevels = levels;

            for (int i = 0; i < numLevels; i++) {
                UfbxLodLevel level = levels[i];

                if (i > 0) {
                    string propName = "Thresholds|Level" + (i - 1).ToString();
                    level.Distance = FindRealLen(lod.Props, propName, 0.0);
                } else if (lod.RelativeDistances) {
                    level.Distance = 100.0;
                }

                {
                    string propName = "DisplayLevels|Level" + i.ToString();
                    long display = FindIntLen(lod.Props, propName, 0);
                    if (display >= 0 && display <= 2) {
                        level.Display = (UfbxLodDisplay)display;
                    }
                }

                levels[i] = level;
            }
        }

        // ==================================================================
        // ufbxi_generate_normals (ufbx.c:20366-20405)
        // ==================================================================

        internal static void GenerateNormals(UfbxiContext uc, UfbxMesh mesh)
        {
            int numIndices = mesh.NumIndices;

            mesh.GeneratedNormals = true;

            UfbxTopoEdge[] topo = new UfbxTopoEdge[numIndices];
            uint[] normalIndices = new uint[numIndices];

            UfbxTopology.ComputeTopology(mesh, topo, numIndices);
            int numNormals = UfbxTopology.GenerateNormalMapping(mesh, topo, numIndices, normalIndices, numIndices, false);

            if (numNormals == mesh.NumVertices) {
                UfbxVertexVec3 vn = mesh.VertexNormal;
                vn.UniquePerVertex = true;
                mesh.VertexNormal = vn;
            }

            // C: normal_data[0] = ufbx_zero_vec3; normal_data++ -- compute_normals then writes
            // through the ADVANCED pointer, i.e. slots 0..num_normals-1 of the base-0 view
            // below (the sentinel slot is never written or read).
            UfbxVec3[] normalBase = new UfbxVec3[numNormals];

            UfbxTopology.ComputeNormals(mesh, mesh.VertexPosition, normalIndices, numIndices, normalBase, numNormals);

            UfbxVertexVec3 vertexNormal = mesh.VertexNormal;
            vertexNormal.Exists = true;
            vertexNormal.Values = normalBase;
            vertexNormal.Indices = normalIndices;
            vertexNormal.ValueReals = 3;
            mesh.VertexNormal = vertexNormal;

            mesh.SkinnedNormal = mesh.VertexNormal;
        }

        static UfbxVec3[] Slice(UfbxVec3[] src, int offset, int count)
        {
            UfbxVec3[] dst = new UfbxVec3[count];
            Array.Copy(src, offset, dst, 0, count);
            return dst;
        }

        // ==================================================================
        // ufbxi_push_prop_prefix (ufbx.c:20407-20429)
        // ==================================================================

        internal static void PushPropPrefix(UfbxiContext uc, ref string dst, string prefix)
        {
            // C: append '|' to a temporary copy when the prefix does not already end with one.
            if (prefix.Length > 0 && prefix[prefix.Length - 1] != '|') {
                prefix = prefix + "|";
            }

            UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref prefix, false),
                "ufbxi_push_string_place_str(&uc->string_pool, &prefix, false)");
            dst = prefix;
        }

        // ==================================================================
        // ufbxi_shader_texture_find_prefix (ufbx.c:20431-20480)
        // ==================================================================

        internal static void ShaderTextureFindPrefix(UfbxiContext uc, UfbxTexture texture, UfbxShaderTexture shader)
        {
            string[] suffixes = new string[3];
            int numSuffixes = 0;

            suffixes[numSuffixes++] = " Parameters/Connections";
            if (shader.ShaderName.Length > 0) {
                suffixes[numSuffixes++] = shader.ShaderName;
            }
            suffixes[numSuffixes++] = "3dsMax|parameters";

            for (int si = 0; si < numSuffixes; si++) {
                string suffix = suffixes[si];

                UfbxProp[] props = texture.Props.Props;
                if (props != null) {
                    for (int i = 0; i < props.Length; i++) {
                        UfbxProp prop = props[i];
                        if (prop.Type != UfbxPropType.Compound) continue;
                        if (UfbxiStr.EndsWith(prop.Name, suffix)) {
                            string prefix = null;
                            PushPropPrefix(uc, ref prefix, prop.Name);
                            shader.PropPrefix = prefix;
                            return;
                        }
                    }
                }
            }

            // Pre-7000 files don't have explicit Compound properties, so let's look for
            // any property that has the suffix before the last `|` ...
            for (int si = 0; si < numSuffixes; si++) {
                string suffix = suffixes[si];

                UfbxProp[] props = texture.Props.Props;
                if (props != null) {
                    for (int i = 0; i < props.Length; i++) {
                        UfbxProp prop = props[i];
                        string name = prop.Name;
                        // C: cut `name` at the last '|', then strip it.
                        while (name.Length > 0) {
                            if (name[name.Length - 1] == '|') {
                                break;
                            }
                            name = name.Substring(0, name.Length - 1);
                        }
                        if (name.Length <= 1) continue;
                        name = name.Substring(0, name.Length - 1);

                        if (UfbxiStr.EndsWith(name, suffix)) {
                            string prefix = null;
                            PushPropPrefix(uc, ref prefix, name);
                            shader.PropPrefix = prefix;
                            return;
                        }
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_file_shaders / ufbxi_update_shader_texture (ufbx.c:20482-20537)
        // ==================================================================

        // C: ufbxi_file_shader / ufbxi_file_shaders[] (ufbx.c:20482-20496). Known shaders
        // that represent sampled images.
        struct UfbxiFileShader
        {
            public ulong ShaderId;
            public string ShaderName;
            public string InputName;
        }

        static readonly UfbxiFileShader[] FileShaders = new UfbxiFileShader[] {
            new UfbxiFileShader { ShaderId = 0x7e73161fad53b12a, ShaderName = "ai_image", InputName = "filename" },
            new UfbxiFileShader { ShaderId = 0, ShaderName = "OSLBitmap", InputName = UfbxiStrings.Filename },
            new UfbxiFileShader { ShaderId = 0, ShaderName = "OSLBitmap2", InputName = UfbxiStrings.Filename },
            new UfbxiFileShader { ShaderId = 0, ShaderName = "OSLBitmap3", InputName = UfbxiStrings.Filename },
            new UfbxiFileShader { ShaderId = 0, ShaderName = "UberBitmap", InputName = UfbxiStrings.Filename },
            new UfbxiFileShader { ShaderId = 0, ShaderName = "UberBitmap2", InputName = UfbxiStrings.Filename },
        };

        // C: ufbxi_update_shader_texture (ufbx.c:20498-20537).
        internal static void UpdateShaderTexture(UfbxTexture texture, UfbxShaderTexture shader)
        {
            foreach (UfbxShaderTextureInput input in shader.Inputs) {
                if (input.Prop.Name != null) {
                    // C: re-find the prop inside the texture's own props (may turn NULL).
                    bool hasProp = UfbxiProperties.TryFindPropLen(texture.Props, input.Prop.Name, out UfbxProps owner, out int index);
                    if (hasProp) {
                        input.Prop = owner.Props[index];
                        input.ValueVec4 = input.Prop.ValueVec4;
                        input.ValueInt = input.Prop.ValueInt;
                        input.ValueStr = input.Prop.ValueStr;
                        input.ValueBlob = input.Prop.ValueBlob;
                        input.Texture = (UfbxTexture)GetPropElement(texture, input.Prop, UfbxElementType.Texture);
                    } else {
                        input.Prop = default;
                    }
                }

                if (input.TextureProp.Name != null) {
                    bool hasProp = UfbxiProperties.TryFindPropLen(texture.Props, input.TextureProp.Name, out UfbxProps owner, out int index);
                    if (hasProp) {
                        input.TextureProp = owner.Props[index];
                        UfbxTexture tex = (UfbxTexture)GetPropElement(texture, input.TextureProp, UfbxElementType.Texture);
                        if (tex != null) input.Texture = tex;
                    } else {
                        input.TextureProp = default;
                    }
                }

                input.TextureEnabled = input.Texture != null;
                if (input.TextureEnabledProp.Name != null) {
                    bool hasProp = UfbxiProperties.TryFindPropLen(texture.Props, input.TextureEnabledProp.Name, out UfbxProps owner, out int index);
                    if (hasProp) {
                        input.TextureEnabledProp = owner.Props[index];
                        input.TextureEnabled = input.TextureEnabledProp.ValueInt != 0;
                    } else {
                        input.TextureEnabledProp = default;
                    }
                }
            }

            if (shader.Type == UfbxShaderTextureType.SelectOutput) {
                UfbxShaderTextureInput map = FindShaderTextureInput(shader, "sourceMap");
                UfbxShaderTextureInput index = FindShaderTextureInput(shader, "outputChannelIndex");
                if (index != null) {
                    shader.MainTextureOutputIndex = index.ValueInt;
                }
                if (map != null) {
                    shader.MainTexture = map.Texture;
                    map.TextureOutputIndex = shader.MainTextureOutputIndex;
                }
            }
        }

        // ==================================================================
        // ufbxi_finalize_shader_texture (ufbx.c:20539-20692)
        // ==================================================================

        internal static void FinalizeShaderTexture(UfbxiContext uc, UfbxTexture texture)
        {
            uint classidA = (uint)(ulong)UfbxiProperties.FindIntPublic(texture.Props, "3dsMax|ClassIDa", 0);
            uint classidB = (uint)(ulong)UfbxiProperties.FindIntPublic(texture.Props, "3dsMax|ClassIDb", 0);
            ulong classid = ((ulong)classidA << 32) | classidB;

            string maxTexture = FindStringLen(texture.Props, "3dsMax|MaxTexture", string.Empty);

            // Check first if the texture looks like it could be a shader.
            UfbxShaderTextureType type = (UfbxShaderTextureType)UfbxEnumCounts.UfbxShaderTextureType;

            if (maxTexture == "MULTIOUTPUT_TO_OSLMap" || classid == 0x896ef2fc44bd743f) {
                type = UfbxShaderTextureType.SelectOutput;
            } else if (maxTexture == "OSLMap" || classid == 0x7f9a7b9d6fcdf00d) {
                type = UfbxShaderTextureType.Osl;
            } else if (texture.Type == UfbxTextureType.File && texture.RelativeFilename.Length == 0 && texture.AbsoluteFilename.Length == 0 && texture.Video == null) {
                type = UfbxShaderTextureType.Unknown;
            }

            if ((int)type == UfbxEnumCounts.UfbxShaderTextureType) return;

            UfbxShaderTexture shader = new UfbxShaderTexture();

            // C: ufbxi_push_zero -- { NULL, 0 } strings; the port's empty-string marker.
            shader.Type = type;
            shader.ShaderName = string.Empty;
            shader.ShaderSource = string.Empty;
            shader.PropPrefix = string.Empty;

            // C: static name_props[]/source_props[] with a single entry each.
            {
                bool hasProp = UfbxiProperties.TryFindPropLen(texture.Props, "3dsMax|params|OSLShaderName", out UfbxProps owner, out int index);
                if (hasProp) {
                    shader.ShaderName = owner.Props[index].ValueStr;
                }
            }
            {
                bool hasProp = UfbxiProperties.TryFindPropLen(texture.Props, "3dsMax|params|OSLCode", out UfbxProps owner, out int index);
                if (hasProp) {
                    shader.ShaderSource = owner.Props[index].ValueStr;
                    shader.RawShaderSource = owner.Props[index].ValueBlob;
                }
            }

            if (shader.ShaderName == null) shader.ShaderName = string.Empty;
            if (shader.ShaderSource == null) shader.ShaderSource = string.Empty;

            ShaderTextureFindPrefix(uc, texture, shader);

            if (shader.ShaderName.Length == 0) {
                string name = shader.PropPrefix;
                if (UfbxiStr.RemoveSuffixC(ref name, " Parameters/Connections|")) {
                    int begin = name.Length;
                    while (begin > 0 && name[begin - 1] != '|') {
                        begin--;
                    }

                    shader.ShaderName = name.Substring(begin);
                    UfbxiFail.CheckNoDesc(uc.StringPool.PushStringPlaceStr(ref shader.ShaderName, false),
                        "ufbxi_push_string_place_str(&uc->string_pool, &shader->shader_name, false)");
                }
            }

            if (shader.ShaderName.Length == 0) {
                if (maxTexture.Length > 0) {
                    shader.ShaderName = maxTexture;
                }
            }

            if (classid != 0) {
                shader.ShaderTypeId = classid;
            }

            if (shader.PropPrefix == null || shader.PropPrefix.Length == 0) {
                // If we not find any shader properties so we might have guessed wrong.
                return;
            }

            // C: grows `shader->inputs` in-place so newly added inputs are visible to the
            // `_map`/`.connected` lookups while iterating; the port keeps a List.
            List<UfbxShaderTextureInput> inputs = new List<UfbxShaderTextureInput>();

            UfbxProp[] props = texture.Props.Props;
            if (props != null) {
                for (int i = 0; i < props.Length; i++) {
                    UfbxProp prop = props[i];

                    string name = prop.Name;
                    if (!UfbxiStr.RemovePrefixStr(ref name, shader.PropPrefix)) continue;

                    // Check if this property is a modifier to an existing input.
                    string baseName = name;
                    if (UfbxiStr.RemoveSuffixC(ref baseName, "_map") || UfbxiStr.RemoveSuffixC(ref baseName, ".shader")) {
                        UfbxShaderTextureInput baseInput = FindShaderTextureInput(inputs, baseName);
                        if (baseInput != null) {
                            baseInput.TextureProp = prop;
                            continue;
                        }
                    } else if (UfbxiStr.RemoveSuffixC(ref baseName, ".connected") || UfbxiStr.RemoveSuffixC(ref baseName, "Enabled")) {
                        UfbxShaderTextureInput baseInput = FindShaderTextureInput(inputs, baseName);
                        if (baseInput != null) {
                            baseInput.TextureEnabledProp = prop;
                            continue;
                        }
                    }

                    // Add a new property
                    UfbxShaderTextureInput input = new UfbxShaderTextureInput();
                    input.Name = name;
                    input.Prop = prop;
                    inputs.Add(input);
                }
            }

            shader.Inputs = inputs.ToArray();

            texture.Shader = shader;
            texture.Type = UfbxTextureType.Shader;
            uc.Scene.Metadata.NumShaderTextures++;

            if (!uc.Opts.DisableQuirks) {
                foreach (UfbxiFileShader fs in FileShaders) {
                    if ((fs.ShaderId != 0 && shader.ShaderTypeId == fs.ShaderId) || shader.ShaderName == fs.ShaderName) {
                        UfbxShaderTextureInput input = FindShaderTextureInput(shader, fs.InputName);
                        if (input != null) {
                            UfbxProp prop = input.Prop;
                            texture.AbsoluteFilename = prop.ValueStr;
                            texture.RawAbsoluteFilename = prop.ValueBlob;
                            texture.Type = UfbxTextureType.File;
                            break;
                        }
                    }
                }
            }

            UpdateShaderTexture(texture, shader);
        }

        // ==================================================================
        // ufbxi_propagate_main_textures (ufbx.c:20694-20754)
        // ==================================================================

        internal static void PropagateMainTextures(UfbxScene scene)
        {
            // We need to do at least 2^(N-1) passes for N shader textures
            int mask = scene.Metadata.NumShaderTextures;
            while (mask != 0) {
                mask >>= 1;

                foreach (UfbxTexture texture in scene.Textures) {
                    UfbxShaderTexture shader = texture.Shader;
                    if (shader == null) continue;

                    UfbxTexture mainTex = shader.MainTexture;
                    if (mainTex == null || shader.MainTextureOutputIndex != 0) continue;

                    UfbxShaderTexture mainShader = mainTex.Shader;
                    if (mainShader == null || mainShader.MainTexture == null) continue;

                    shader.MainTexture = mainShader.MainTexture;
                    shader.MainTextureOutputIndex = mainShader.MainTextureOutputIndex;
                }
            }

            // Remove cyclic main textures
            foreach (UfbxTexture texture in scene.Textures) {
                UfbxShaderTexture shader = texture.Shader;
                if (shader == null || shader.MainTexture == null || shader.MainTextureOutputIndex != 0) continue;
                UfbxTexture mainTex = shader.MainTexture;
                if (mainTex != null && mainTex.Shader != null && mainTex.Shader.MainTexture != null) {
                    // Should have been propagated to `texture`
                    shader.MainTexture = null;
                }
            }

            foreach (UfbxTexture texture in scene.Textures) {
                UfbxShaderTexture shader = texture.Shader;
                if (shader == null) continue;

                foreach (UfbxShaderTextureInput input in shader.Inputs) {
                    if (input.Texture == null || input.Texture.Shader == null) continue;
                    UfbxShaderTexture inputShader = input.Texture.Shader;
                    if (inputShader.MainTexture != null) {
                        input.Texture = inputShader.MainTexture;
                        input.TextureOutputIndex = inputShader.MainTextureOutputIndex;
                    }
                }
            }

            foreach (UfbxMaterial material in scene.Materials) {
                foreach (UfbxMaterialTexture tex in material.Textures) {
                    UfbxShaderTexture shader = tex.Texture.Shader;
                    if (shader != null && shader.MainTexture != null && shader.MainTextureOutputIndex == 0) {
                        tex.Texture = shader.MainTexture;
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_insert_texture_file / ufbxi_pop_texture_files (ufbx.c:20759-20819)
        // ==================================================================

        // C: ufbxi_patch_empty(m_dst, m_len, m_src) (ufbx.c:20756-20757).
        static void PatchEmptyStr(ref string dst, string src)
        {
            if (dst == null || dst.Length == 0) dst = src;
        }

        static void PatchEmptyBlob(ref byte[] dst, byte[] src)
        {
            if (dst == null || dst.Length == 0) dst = src;
        }

        internal static void InsertTextureFile(UfbxiContext uc, UfbxTexture texture)
        {
            texture.FileIndex = UfbxConstants.NoIndex;

            // HACK: Even the raw entries have a null terminator so we can offset the
            // pointer by one for relative filenames. This guarantees that an overlapping
            // absolute and relative filenames will get separate textures. (ufbx.c:20765-20772)
            UfbxiTextureFileMap.Key key = default;
            bool hasKey = false;
            if (texture.RawAbsoluteFilename != null && texture.RawAbsoluteFilename.Length > 0) {
                key.Data = texture.RawAbsoluteFilename;
                key.Relative = false;
                hasKey = true;
            } else if (texture.RawRelativeFilename != null && texture.RawRelativeFilename.Length > 0) {
                key.Data = texture.RawRelativeFilename;
                key.Relative = true;
                hasKey = true;
            }

            if (!hasKey) return;
            UfbxiTextureFileMap map = uc.EnsureTextureFileMap();
            UfbxiTextureFileEntry entry = map.Find(key);
            if (entry == null) {
                UfbxTextureFile file = new UfbxTextureFile();

                UfbxiTextureFileEntry newEntry = new UfbxiTextureFileEntry();
                newEntry.File = file;
                map.Insert(key, newEntry);

                file.Index = (uint)(map.Size - 1);

                newEntry.Key = key;
                entry = newEntry;
            }

            UfbxTextureFile f = entry.File;
            texture.FileIndex = f.Index;
            texture.HasFile = true;
            PatchEmptyStr(ref f.Filename, texture.Filename);
            PatchEmptyStr(ref f.RelativeFilename, texture.RelativeFilename);
            PatchEmptyStr(ref f.AbsoluteFilename, texture.AbsoluteFilename);
            PatchEmptyBlob(ref f.RawFilename, texture.RawFilename);
            PatchEmptyBlob(ref f.RawRelativeFilename, texture.RawRelativeFilename);
            PatchEmptyBlob(ref f.RawAbsoluteFilename, texture.RawAbsoluteFilename);
            PatchEmptyBlob(ref f.Content, texture.Content);
        }

        // C: ufbxi_pop_texture_files (ufbx.c:20804-20819).
        internal static void PopTextureFiles(UfbxiContext uc)
        {
            int numFiles = uc.TextureFileMap != null ? uc.TextureFileMap.Size : 0;
            UfbxTextureFile[] files = new UfbxTextureFile[numFiles];

            uc.Scene.TextureFiles = files;

            for (int i = 0; i < numFiles; i++) {
                files[i] = uc.TextureFileMap.Items[i].File;
            }
        }

        // ==================================================================
        // ufbxi_deduplicate_textures (ufbx.c:20821-20869)
        // ==================================================================

        // C: ufbxi_ordered_texture_less_texture (ufbx.c:20826-20831). Texture pointer order
        // == element_id order (PORTING_NOTES "C 指针序 ≡ 分配序").
        static bool OrderedTextureLessTexture(object user, UfbxiOrderedTexture a, UfbxiOrderedTexture b)
        {
            return a.Texture.ElementId < b.Texture.ElementId;
        }

        // C: ufbxi_ordered_texture_less_order (ufbx.c:20833-20838).
        static bool OrderedTextureLessOrder(object user, UfbxiOrderedTexture a, UfbxiOrderedTexture b)
        {
            return a.Order < b.Order;
        }

        // C: ufbxi_deduplicate_textures (ufbx.c:20840-20869). Sorts by texture pointer,
        // removes adjacent duplicates, then restores the original order by `order`.
        internal static UfbxiOrderedTexture[] DeduplicateTextures(UfbxiOrderedTexture[] textures)
        {
            int count = textures.Length;
            UfbxiOrderedTexture[] tmp = new UfbxiOrderedTexture[count];
            UfbxiSort.StableSort(16, textures, tmp, count, OrderedTextureLessTexture, null);

            // Remove adjacent duplicates
            int dstIx = 0;
            for (int srcIx = 0; srcIx < count; srcIx++) {
                if (srcIx > 0 && ReferenceEquals(textures[srcIx - 1].Texture, textures[srcIx].Texture)) {
                    continue;
                } else {
                    if (srcIx != dstIx) {
                        textures[dstIx] = textures[srcIx];
                    }
                    dstIx++;
                }
            }

            int newCount = dstIx;
            tmp = new UfbxiOrderedTexture[newCount];
            UfbxiSort.StableSort(16, textures, tmp, newCount, OrderedTextureLessOrder, null);

            UfbxiOrderedTexture[] result = new UfbxiOrderedTexture[newCount];
            Array.Copy(textures, result, newCount);
            return result;
        }

        // ==================================================================
        // ufbxi_fetch_file_textures (ufbx.c:20878-21009)
        // ==================================================================

        internal static void FetchFileTextures(UfbxiContext uc)
        {
            // Start by pushing all the textures into the stack
            List<UfbxTexture> textureStack = new List<UfbxTexture>(uc.Scene.Textures);

            // Compressed `ufbxi_file_texture_fetch_state` (ufbx.c:20871-20875)
            // C states: UFBXI_FILE_TEXTURE_FETCH_INITIAL / STARTED / FINISHED -- the INITIAL
            // value never needs testing (0 is the default), so only the two others are named.
            const byte FetchStarted = 1, FetchFinished = 2;
            byte[] states = new byte[uc.Scene.Textures.Length];

            int numStackTextures = textureStack.Count;
            while (numStackTextures-- > 0) {
                UfbxTexture texture = textureStack[textureStack.Count - 1];
                textureStack.RemoveAt(textureStack.Count - 1);

                byte state = states[texture.TypedId];
                if (state == FetchFinished) continue;
                UfbxShaderTexture shader = texture.Shader;

                if (state == FetchStarted) {
                    states[texture.TypedId] = FetchFinished;

                    // Now all non-cyclical dependents should be processed.
                    List<UfbxiOrderedTexture> deps = new List<UfbxiOrderedTexture>();
                    int numDeps = 0;

                    if (texture.Type == UfbxTextureType.File) {
                        UfbxiOrderedTexture dst;
                        dst.Texture = texture;
                        dst.Order = numDeps++;
                        deps.Add(dst);
                    }

                    foreach (UfbxTextureLayer layer in texture.Layers != null ? texture.Layers : Array.Empty<UfbxTextureLayer>()) {
                        UfbxTexture depTex = layer.Texture;
                        if (depTex.FileTextures != null && depTex.FileTextures.Length > 0) {
                            UfbxiOrderedTexture dst;
                            dst.Texture = depTex;
                            dst.Order = numDeps++;
                            deps.Add(dst);
                        }
                    }

                    if (shader != null) {
                        foreach (UfbxShaderTextureInput input in shader.Inputs) {
                            UfbxTexture depTex = input.Texture;
                            if (depTex != null && depTex.FileTextures != null && depTex.FileTextures.Length > 0) {
                                UfbxiOrderedTexture dst;
                                dst.Texture = depTex;
                                dst.Order = numDeps++;
                                deps.Add(dst);
                            }
                        }
                    }

                    // Deduplicate the direct dependencies first
                    UfbxiOrderedTexture[] depsArr = DeduplicateTextures(deps.ToArray());
                    numDeps = depsArr.Length;

                    if (numDeps == 1) {
                        // If we have only a single dependency (that is not the same one) we can just copy the pointer
                        texture.FileTextures = depsArr[0].Texture.FileTextures;
                    } else {
                        // Now collect all the file textures and deduplicate them
                        List<UfbxiOrderedTexture> files = new List<UfbxiOrderedTexture>();
                        int numFiles = 0;
                        foreach (UfbxiOrderedTexture dep in depsArr) {
                            foreach (UfbxTexture tex in dep.Texture.FileTextures) {
                                UfbxiOrderedTexture dst;
                                dst.Texture = tex;
                                dst.Order = numFiles++;
                                files.Add(dst);
                            }
                        }

                        // Deduplicate the file textures
                        UfbxiOrderedTexture[] filesArr = DeduplicateTextures(files.ToArray());
                        numFiles = filesArr.Length;

                        UfbxTexture[] fileTextures = new UfbxTexture[numFiles];
                        for (int i = 0; i < numFiles; i++) {
                            fileTextures[i] = filesArr[i].Texture;
                        }
                        texture.FileTextures = fileTextures;
                    }

                } else {
                    if (texture.Type == UfbxTextureType.File) {
                        // Simple case: Just point to self
                        texture.FileTextures = new UfbxTexture[] { texture };

                        // In simple cases we can quit here, for more complex file textures queue
                        // the texture in case there are other file textures as inputs.
                        if (texture.Shader == null) {
                            states[texture.TypedId] = FetchFinished;
                            continue;
                        }
                    }

                    // Complex: Process all dependencies first
                    states[texture.TypedId] = FetchStarted;

                    // Push self first so we can return after processing dependencies
                    textureStack.Add(texture);
                    numStackTextures++;

                    foreach (UfbxTextureLayer layer in texture.Layers != null ? texture.Layers : Array.Empty<UfbxTextureLayer>()) {
                        textureStack.Add(layer.Texture);
                        numStackTextures++;
                    }

                    if (shader != null) {
                        foreach (UfbxShaderTextureInput input in shader.Inputs) {
                            if (input.Texture != null) {
                                textureStack.Add(input.Texture);
                                numStackTextures++;
                            }
                        }
                    }
                }
            }
        }

        // ==================================================================
        // ufbxi_get_geometry_transform_node (ufbx.c:21011-21018)
        // ==================================================================

        internal static UfbxNode GetGeometryTransformNode(UfbxElement element)
        {
            if (element.Instances != null && element.Instances.Length == 1) {
                UfbxNode node = element.Instances[0];
                if (node.HasGeometryTransform) return node;
            }
            return null;
        }

        // ==================================================================
        // vec3 list transforms (ufbx.c:21020-21070)
        // ==================================================================

        // C: ufbxi_mirror_vec3_list (ufbx.c:21020-21033) with `stride == sizeof(ufbx_vec3)`.
        internal static void MirrorVec3List(UfbxVec3[] list, UfbxMirrorAxis axis)
        {
            if (axis == 0 || list == null || list.Length == 0) return;
            int ax = (int)axis - 1;
            for (int i = 0; i < list.Length; i++) {
                UfbxVec3 v = list[i];
                switch (ax) {
                case 0: v.X = -v.X; break;
                case 1: v.Y = -v.Y; break;
                default: v.Z = -v.Z; break;
                }
                list[i] = v;
            }
        }

        // C: ufbxi_mirror_vec3_list with `stride == sizeof(ufbx_vec4)` (NURBS control points);
        // the mirrored component is still `v[ax]` over the x/y/z prefix.
        internal static void MirrorVec4List(UfbxVec4[] list, UfbxMirrorAxis axis)
        {
            if (axis == 0 || list == null || list.Length == 0) return;
            int ax = (int)axis - 1;
            for (int i = 0; i < list.Length; i++) {
                UfbxVec4 v = list[i];
                switch (ax) {
                case 0: v.X = -v.X; break;
                case 1: v.Y = -v.Y; break;
                default: v.Z = -v.Z; break;
                }
                list[i] = v;
            }
        }

        // C: ufbxi_scale_vec3_list (ufbx.c:21035-21049).
        internal static void ScaleVec3List(UfbxVec3[] list, double scale)
        {
            if (list == null || list.Length == 0) return;
            for (int i = 0; i < list.Length; i++) {
                UfbxVec3 v = list[i];
                v.X *= scale;
                v.Y *= scale;
                v.Z *= scale;
                list[i] = v;
            }
        }

        // C: ufbxi_scale_vec3_list with `stride == sizeof(ufbx_vec4)`.
        internal static void ScaleVec4List(UfbxVec4[] list, double scale)
        {
            if (list == null || list.Length == 0) return;
            for (int i = 0; i < list.Length; i++) {
                UfbxVec4 v = list[i];
                v.X *= scale;
                v.Y *= scale;
                v.Z *= scale;
                list[i] = v;
            }
        }

        // C: ufbxi_transform_vec3_list (ufbx.c:21051-21063).
        internal static void TransformVec3List(UfbxVec3[] list, UfbxMatrix matrix)
        {
            if (list == null || list.Length == 0) return;
            for (int i = 0; i < list.Length; i++) {
                list[i] = UfbxMatrix.TransformPosition(matrix, list[i]);
            }
        }

        // C: ufbxi_transform_vec3_list over ufbx_vec4 control points: reads/writes x/y/z
        // through a ufbx_vec3* view, `w` is left untouched.
        internal static void TransformVec4List(UfbxVec4[] list, UfbxMatrix matrix)
        {
            if (list == null || list.Length == 0) return;
            for (int i = 0; i < list.Length; i++) {
                UfbxVec4 v = list[i];
                UfbxVec3 t = UfbxMatrix.TransformPosition(matrix, new UfbxVec3(v.X, v.Y, v.Z));
                v.X = t.X;
                v.Y = t.Y;
                v.Z = t.Z;
                list[i] = v;
            }
        }

        // C: ufbxi_normalize_vec3_list (ufbx.c:21065-21070).
        internal static void NormalizeVec3List(UfbxVec3[] list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Length; i++) {
                list[i] = NormalizeVec3(list[i]);
            }
        }

        // C: ufbxi_normalize3 (ufbx.c:5938-5945).
        internal static UfbxVec3 NormalizeVec3(UfbxVec3 a)
        {
            double len = UfbxMath.Sqrt(UfbxVec3.Dot3(a, a));
            if (len > UfbxMathConsts.Epsilon) {
                return UfbxVec3.Mul3(a, 1.0 / len);
            } else {
                return default;
            }
        }

        // ==================================================================
        // ufbxi_flip_attrib_winding / ufbxi_flip_winding (ufbx.c:21075-21165)
        // ==================================================================

        // C: `indices->count` for the shared sentinel buffers is the mesh's real count
        // (PORTING_NOTES "哨兵索引数组"): vertex-attribute index lists are always per-index.
        static int IndexListCount(UfbxiContext uc, uint[] data, UfbxMesh mesh)
        {
            if (ReferenceEquals(data, uc.ZeroIndices) || ReferenceEquals(data, uc.ConsecutiveIndices)) {
                return mesh.NumIndices;
            }
            return data != null ? data.Length : 0;
        }

        internal static void FlipAttribWinding(UfbxiContext uc, UfbxMesh mesh, ref uint[] indices, bool isPosition)
        {
            int count = IndexListCount(uc, indices, mesh);

            // All zero, no flipping needed
            if (ReferenceEquals(indices, uc.ZeroIndices) || count == 0) return;

            if (ReferenceEquals(indices, mesh.VertexPosition.Indices) && !isPosition) {
                // Sharing indices with vertex position, already flipped.
                return;
            } else if (ReferenceEquals(indices, uc.ConsecutiveIndices)) {
                // Need to duplicate consecutive indices, but we can cache the per mesh.
                if (uc.TmpMeshConsecutiveIndices != null) {
                    indices = uc.TmpMeshConsecutiveIndices;
                    return;
                }
                uint[] copy = new uint[count];
                Array.Copy(indices, copy, count);
                indices = copy;
                uc.TmpMeshConsecutiveIndices = copy;
            }

            uint[] data = indices;
            foreach (UfbxFace face in mesh.Faces) {
                if (face.NumIndices == 0) continue;
                int begin = (int)face.IndexBegin + 1;
                int end = (int)face.IndexBegin + (int)face.NumIndices - 1;
                while (begin < end) {
                    uint tmp = data[begin];
                    data[begin] = data[end];
                    data[end] = tmp;
                    begin++;
                    end--;
                }
            }
        }

        internal static void FlipWinding(UfbxiContext uc, UfbxMesh mesh)
        {
            uc.TmpMeshConsecutiveIndices = null;
            {
                ref uint[] p = ref mesh.VertexPosition.Indices;
                FlipAttribWinding(uc, mesh, ref p, true);
            }
            {
                ref uint[] p = ref mesh.VertexNormal.Indices;
                FlipAttribWinding(uc, mesh, ref p, false);
            }
            {
                ref uint[] p = ref mesh.VertexCrease.Indices;
                FlipAttribWinding(uc, mesh, ref p, false);
            }
            if (mesh.UvSets != null && mesh.UvSets.Length > 0) {
                for (int i = 0; i < mesh.UvSets.Length; i++) {
                    ref uint[] pUv = ref mesh.UvSets[i].VertexUv.Indices;
                    FlipAttribWinding(uc, mesh, ref pUv, false);
                    ref uint[] pTan = ref mesh.UvSets[i].VertexTangent.Indices;
                    FlipAttribWinding(uc, mesh, ref pTan, false);
                    ref uint[] pBit = ref mesh.UvSets[i].VertexBitangent.Indices;
                    FlipAttribWinding(uc, mesh, ref pBit, false);
                }
                mesh.VertexUv = mesh.UvSets[0].VertexUv;
                mesh.VertexBitangent = mesh.UvSets[0].VertexBitangent;
                mesh.VertexTangent = mesh.UvSets[0].VertexTangent;
            }
            if (mesh.ColorSets != null && mesh.ColorSets.Length > 0) {
                for (int i = 0; i < mesh.ColorSets.Length; i++) {
                    ref uint[] pColor = ref mesh.ColorSets[i].VertexColor.Indices;
                    FlipAttribWinding(uc, mesh, ref pColor, false);
                }
                mesh.VertexColor = mesh.ColorSets[0].VertexColor;
            }
            {
                ref uint[] p = ref mesh.SkinnedPosition.Indices;
                FlipAttribWinding(uc, mesh, ref p, false);
            }
            if (!ReferenceEquals(mesh.SkinnedNormal.Indices, mesh.VertexNormal.Indices)) {
                ref uint[] p = ref mesh.SkinnedNormal.Indices;
                FlipAttribWinding(uc, mesh, ref p, false);
            }

            UfbxiSceneBuild.UpdateVertexFirstIndex(mesh);

            // Mapping from old index values to flipped ones, reserve index -1
            // (aka `UFBX_NO_INDEX`) for itself.
            if (mesh.Edges != null && mesh.Edges.Length > 0) {
                uint[] indexMapping = new uint[mesh.NumIndices + 1];
                indexMapping[0] = UfbxConstants.NoIndex; // C: index_mapping[-1]
                foreach (UfbxFace face in mesh.Faces) {
                    if (face.NumIndices == 0) continue;
                    uint begin = face.IndexBegin;
                    uint count = face.NumIndices - 1;
                    indexMapping[begin + 1] = begin;
                    for (uint i = 0; i < count; i++) {
                        indexMapping[begin + 1 + 1 + i] = begin + count - i;
                    }
                }

                for (int i = 0; i < mesh.Edges.Length; i++) {
                    UfbxEdge edge = mesh.Edges[i];
                    // C: index_mapping[(int32_t)p_edge->a] -- NoIndex (0xffffffff) reads the
                    // reserved -1 slot, mapped here to index 0.
                    uint a = indexMapping[unchecked((int)edge.A) + 1];
                    uint b = indexMapping[unchecked((int)edge.B) + 1];
                    edge.A = b;
                    edge.B = a;
                }
            }
        }

        // ==================================================================
        // ufbxi_modify_geometry (ufbx.c:21167-21334)
        // ==================================================================

        internal static void ModifyGeometry(UfbxiContext uc)
        {
            bool doMirror = false;
            bool doWinding = uc.Opts.ReverseWinding;
            bool doScale = false;
            bool doGeometryTransforms = false;
            if (uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.ModifyGeometry
                || uc.Opts.GeometryTransformHandling == UfbxGeometryTransformHandling.ModifyGeometryNoFallback) {
                // Prefetch geometry transforms for processing, they will later be overwritten in `ufbxi_update_node()`.
                foreach (UfbxNode node in uc.Scene.Nodes) {
                    if (node.IsRoot) continue;

                    node.GeometryTransform = UfbxiSceneUpdate.GetGeometryTransform(node.Props, node);
                    if (!UfbxiProperties.IsTransformIdentity(node.GeometryTransform)) {
                        node.GeometryToNode = UfbxMatrix.FromTransform(node.GeometryTransform);
                        node.HasGeometryTransform = true;
                    } else {
                        node.GeometryToNode = UfbxMatrix.Identity;
                        node.HasGeometryTransform = false;
                    }
                }
                doGeometryTransforms = true;
            }
            if (uc.MirrorAxis != 0) {
                doMirror = true;
            }
            if (uc.Scene.Metadata.GeometryScale != 1.0) {
                doScale = true;
            }

            double geometryScale = uc.Scene.Metadata.GeometryScale;
            UfbxMirrorAxis mirrorAxis = uc.MirrorAxis;

            foreach (UfbxBlendShape shape in uc.Scene.BlendShapes) {
                if (doScale) {
                    ScaleVec3List(shape.PositionOffsets, geometryScale);
                }

                if (doMirror) {
                    MirrorVec3List(shape.PositionOffsets, mirrorAxis);
                    MirrorVec3List(shape.NormalOffsets, mirrorAxis);
                }
            }

            foreach (UfbxMesh mesh in uc.Scene.Meshes) {
                if (doScale) {
                    UfbxVertexVec3 vp = mesh.VertexPosition;
                    ScaleVec3List((UfbxVec3[])vp.Values, geometryScale);
                    mesh.VertexPosition = vp;
                }

                bool doFlipWinding = doWinding;
                if (doMirror) {
                    {
                        UfbxVertexVec3 vp = mesh.VertexPosition;
                        MirrorVec3List((UfbxVec3[])vp.Values, mirrorAxis);
                        mesh.VertexPosition = vp;
                    }
                    {
                        UfbxVertexVec3 vn = mesh.VertexNormal;
                        MirrorVec3List((UfbxVec3[])vn.Values, mirrorAxis);
                        mesh.VertexNormal = vn;
                    }
                    for (int si = 0; mesh.UvSets != null && si < mesh.UvSets.Length; si++) {
                        UfbxUvSet set = mesh.UvSets[si];
                        UfbxVertexVec3 tan = set.VertexTangent;
                        MirrorVec3List((UfbxVec3[])tan.Values, mirrorAxis);
                        set.VertexTangent = tan;
                        UfbxVertexVec3 bit = set.VertexBitangent;
                        MirrorVec3List((UfbxVec3[])bit.Values, mirrorAxis);
                        set.VertexBitangent = bit;
                        mesh.UvSets[si] = set;
                    }
                    if (!uc.Opts.HandednessConversionRetainWinding) {
                        doFlipWinding = !doFlipWinding;
                    }
                }

                // Flip face winding retaining the first vertex
                if (doFlipWinding) {
                    mesh.ReversedWinding = true;
                    FlipWinding(uc, mesh);
                }

                UfbxNode geoNode = GetGeometryTransformNode(mesh);
                if (doGeometryTransforms && geoNode != null) {
                    UfbxMatrix tangentMatrix = geoNode.GeometryToNode;
                    tangentMatrix.M03 = 0.0;
                    tangentMatrix.M13 = 0.0;
                    tangentMatrix.M23 = 0.0;
                    UfbxMatrix normalMatrix = UfbxMatrix.ForNormals(geoNode.GeometryToNode);

                    {
                        UfbxVertexVec3 vp = mesh.VertexPosition;
                        TransformVec3List((UfbxVec3[])vp.Values, geoNode.GeometryToNode);
                        mesh.VertexPosition = vp;
                    }
                    {
                        UfbxVertexVec3 vn = mesh.VertexNormal;
                        TransformVec3List((UfbxVec3[])vn.Values, normalMatrix);
                        mesh.VertexNormal = vn;
                    }
                    NormalizeVec3List((UfbxVec3[])mesh.VertexNormal.Values);

                    for (int si = 0; mesh.UvSets != null && si < mesh.UvSets.Length; si++) {
                        UfbxUvSet set = mesh.UvSets[si];
                        UfbxVertexVec3 tan = set.VertexTangent;
                        TransformVec3List((UfbxVec3[])tan.Values, tangentMatrix);
                        set.VertexTangent = tan;
                        UfbxVertexVec3 bit = set.VertexBitangent;
                        TransformVec3List((UfbxVec3[])bit.Values, tangentMatrix);
                        set.VertexBitangent = bit;
                        mesh.UvSets[si] = set;
                        NormalizeVec3List((UfbxVec3[])mesh.UvSets[si].VertexTangent.Values);
                        NormalizeVec3List((UfbxVec3[])mesh.UvSets[si].VertexBitangent.Values);
                    }
                }
            }

            foreach (UfbxLineCurve curve in uc.Scene.LineCurves) {
                if (doScale) {
                    ScaleVec3List(curve.ControlPoints, geometryScale);
                }

                if (doMirror) {
                    MirrorVec3List(curve.ControlPoints, mirrorAxis);
                }

                UfbxNode geoNode = GetGeometryTransformNode(curve);
                if (doGeometryTransforms && geoNode != null) {
                    TransformVec3List(curve.ControlPoints, geoNode.GeometryToNode);
                }
            }

            foreach (UfbxNurbsCurve curve in uc.Scene.NurbsCurves) {
                if (doScale) {
                    ScaleVec4List(curve.ControlPoints, geometryScale);
                }

                if (doMirror) {
                    MirrorVec4List(curve.ControlPoints, mirrorAxis);
                }

                UfbxNode geoNode = GetGeometryTransformNode(curve);
                if (doGeometryTransforms && geoNode != null) {
                    TransformVec4List(curve.ControlPoints, geoNode.GeometryToNode);
                }
            }

            foreach (UfbxNurbsSurface surface in uc.Scene.NurbsSurfaces) {
                if (doScale) {
                    ScaleVec4List(surface.ControlPoints, geometryScale);
                }

                if (doMirror) {
                    MirrorVec4List(surface.ControlPoints, mirrorAxis);
                }

                UfbxNode geoNode = GetGeometryTransformNode(surface);
                if (doGeometryTransforms && geoNode != null) {
                    TransformVec4List(surface.ControlPoints, geoNode.GeometryToNode);
                }
            }

            if (uc.Opts.GeometryTransformHandling != UfbxGeometryTransformHandling.Preserve) {
                // Reset all geometry transforms if we're not preserving them
                UfbxProps defaults = null;
                foreach (UfbxNode node in uc.Scene.Nodes) {
                    if (defaults == null) defaults = node.Props.Defaults;

                    if (node.HasGeometryTransform) {
                        UfbxiProperties.SetOwnPropVec3Uniform(node.Props, UfbxiStrings.GeometricTranslation, 0.0);
                        UfbxiProperties.SetOwnPropVec3Uniform(node.Props, UfbxiStrings.GeometricRotation, 0.0);
                        UfbxiProperties.SetOwnPropVec3Uniform(node.Props, UfbxiStrings.GeometricScaling, 1.0);
                    }
                }

                if (defaults != null) {
                    UfbxiProperties.SetOwnPropVec3Uniform(defaults, UfbxiStrings.GeometricTranslation, 0.0);
                    UfbxiProperties.SetOwnPropVec3Uniform(defaults, UfbxiStrings.GeometricRotation, 0.0);
                    UfbxiProperties.SetOwnPropVec3Uniform(defaults, UfbxiStrings.GeometricScaling, 1.0);
                }
            }
        }

        // ==================================================================
        // ufbxi_postprocess_scene (ufbx.c:21336-21358)
        // ==================================================================

        internal static void PostprocessScene(UfbxiContext uc)
        {
            if (uc.Opts.NormalizeNormals || uc.Opts.NormalizeTangents) {
                foreach (UfbxMesh mesh in uc.Scene.Meshes) {
                    if (uc.Opts.NormalizeNormals) {
                        NormalizeVec3List((UfbxVec3[])mesh.VertexNormal.Values);
                    }
                    // NOTE: reproduced verbatim from C, which normalizes the canonical
                    // tangent/bitangent inside the UV-set loop (not the per-set arrays).
                    if (uc.Opts.NormalizeTangents) {
                        foreach (UfbxUvSet set in mesh.UvSets) {
                            NormalizeVec3List((UfbxVec3[])mesh.VertexTangent.Values);
                            NormalizeVec3List((UfbxVec3[])mesh.VertexBitangent.Values);
                        }
                    }
                }
            }

            if (uc.Exporter == UfbxExporter.BlenderBinary) {
                uc.Scene.Metadata.OrthoSizeUnit = 1.0 / uc.Scene.Metadata.GeometryScale;
            } else {
                uc.Scene.Metadata.OrthoSizeUnit = 30.0;
            }
        }

        // ==================================================================
        // ufbxi_next_path_segment / ufbxi_absolute_to_relative_path
        // (ufbx.c:21360-21438)
        // ==================================================================

        // C: ufbxi_next_path_segment (ufbx.c:21360-21368).
        static int NextPathSegment(byte[] data, int begin, int length)
        {
            for (int i = begin; i < length; i++) {
                if (data[i] == (byte)'/' || data[i] == (byte)'\\') {
                    return i;
                }
            }
            return length;
        }

        // C: ufbxi_strblob_data / ufbxi_strblob_length (ufbx.c:16546-16554) — local copies
        // (the `UfbxiSceneFiles` ones are private and Parse/SceneFiles.cs is not this
        // module's file).
        internal static byte[] StrblobData(UfbxiSceneFiles.UfbxiStrblob src, bool raw)
        {
            if (raw) return src.Blob != null ? src.Blob : Array.Empty<byte>();
            return UfbxiRawStr.ToBytes(src.Str != null ? src.Str : string.Empty);
        }

        internal static int StrblobLength(UfbxiSceneFiles.UfbxiStrblob src, bool raw)
        {
            if (raw) return src.Blob != null ? src.Blob.Length : 0;
            return src.Str != null ? src.Str.Length : 0;
        }

        // C: ufbxi_absolute_to_relative_path (ufbx.c:21370-21438).
        internal static void AbsoluteToRelativePath(UfbxiContext uc, ref UfbxiSceneFiles.UfbxiStrblob pDst,
            UfbxiSceneFiles.UfbxiStrblob pRel, UfbxiSceneFiles.UfbxiStrblob pSrc, bool raw)
        {
            byte[] rel = StrblobData(pRel, raw);
            byte[] src = StrblobData(pSrc, raw);
            int relLength = StrblobLength(pRel, raw);
            int srcLength = StrblobLength(pSrc, raw);

            if (relLength == 0 || srcLength == 0) return;

            // Absolute paths must start with the same character (either drive or '/')
            if (rel[0] != src[0]) return;

            // Find the last directory of the path we want to be relative to
            while (relLength > 0 && (rel[relLength - 1] != (byte)'/' && rel[relLength - 1] != (byte)'\\')) {
                relLength--;
            }

            if (relLength == 0) return;
            byte separator = rel[relLength - 1];

            // C: ufbxi_check(rel_length <= (SIZE_MAX - src_length) / 3) -- unreachable overflow
            // with managed array lengths.
            int maxLength = relLength * 3 + srcLength;

            byte[] tmp = new byte[maxLength];
            int tmpLength = 0;

            int relBegin = 0;
            int srcBegin = 0;
            while (relBegin < relLength && srcBegin < srcLength) {
                int relEnd = NextPathSegment(rel, relBegin, relLength);
                int srcEnd = NextPathSegment(src, srcBegin, srcLength);
                if (relEnd != srcEnd || !SpanEqual(rel, relBegin, src, srcBegin, srcEnd - srcBegin)) break;

                relBegin = relEnd + 1;
                srcBegin = srcEnd + 1;
            }

            while (relBegin < relLength) {
                int relEnd = NextPathSegment(rel, relBegin, relLength);
                tmp[tmpLength++] = (byte)'.';
                tmp[tmpLength++] = (byte)'.';
                tmp[tmpLength++] = separator;
                relBegin = relEnd + 1;
            }

            while (srcBegin < srcLength) {
                int srcEnd = NextPathSegment(src, srcBegin, srcLength);
                int len = srcEnd - srcBegin;

                Array.Copy(src, srcBegin, tmp, tmpLength, len);
                tmpLength += len;

                if (srcEnd < srcLength) {
                    tmp[tmpLength++] = separator;
                }

                srcBegin = srcEnd + 1;
            }

            // C: ufbx_assert(tmp_length <= max_length) -- holds by construction.

            string pooled = UfbxiRawStr.FromBytes(tmp, 0, tmpLength);
            UfbxiFail.CheckNoDesc(uc.StringPool.PushStringImp(pooled, 0, tmpLength, out int _, false, true) != null,
                "ufbxi_push_string(&uc->string_pool, tmp, tmp_length, NULL, true)");

            UfbxiSceneFiles.StrblobSet(ref pDst, tmp, 0, tmpLength, raw);
        }

        // C: memcmp(rel + rel_begin, src + src_begin, n) == 0
        static bool SpanEqual(byte[] a, int aOff, byte[] b, int bOff, int count)
        {
            for (int i = 0; i < count; i++) {
                if (a[aOff + i] != b[bOff + i]) return false;
            }
            return true;
        }

        // C: ufbxi_resolve_filenames (ufbx.c:21440-21453).
        internal static void ResolveFilenames(UfbxiContext uc, ref UfbxiSceneFiles.UfbxiStrblob filename,
            ref UfbxiSceneFiles.UfbxiStrblob absoluteFilename, ref UfbxiSceneFiles.UfbxiStrblob relativeFilename, bool raw)
        {
            if (StrblobLength(relativeFilename, raw) == 0) {
                UfbxiSceneFiles.UfbxiStrblob originalFilePath = default;
                if (raw) {
                    originalFilePath.Blob = uc.Scene.Metadata.RawOriginalFilePath;
                } else {
                    originalFilePath.Str = uc.Scene.Metadata.OriginalFilePath;
                    originalFilePath.Blob = null;
                }

                AbsoluteToRelativePath(uc, ref relativeFilename, originalFilePath, absoluteFilename, raw);
            }

            UfbxiFail.CheckNoDesc(UfbxiSceneFiles.ResolveRelativeFilename(uc, ref filename, relativeFilename, raw),
                "ufbxi_resolve_relative_filename(uc, filename, relative_filename, raw)");
        }

        // ==================================================================
        // file contents (ufbx.c:21455-21531)
        // ==================================================================

        // C: ufbxi_file_content_less (ufbx.c:21455-21460).
        static bool FileContentLess(object user, UfbxiFileContent a, UfbxiFileContent b)
        {
            return UfbxiStr.Less(a.AbsoluteFilename, b.AbsoluteFilename);
        }

        // C: ufbxi_sort_file_contents (ufbx.c:21462-21467).
        static void SortFileContents(UfbxiFileContent[] content, int count)
        {
            UfbxiFileContent[] tmp = new UfbxiFileContent[count];
            UfbxiSort.StableSort(32, content, tmp, count, FileContentLess, null);
        }

        // C: ufbxi_push_file_content (ufbx.c:21469-21478).
        static void PushFileContent(UfbxiContext uc, string filename, byte[] data)
        {
            if (data == null || data.Length == 0 || filename == null || filename.Length == 0) return;
            UfbxiFileContent content = new UfbxiFileContent();
            content.AbsoluteFilename = filename;
            content.Content = data;
            uc.FileContent.Add(content);
        }

        // C: ufbxi_macro_lower_bound_eq over `uc->file_content` with data-pointer equality
        // (ufbx.c:21484-21491). The port's pooled strings are interned, so the C data-pointer
        // identity is reproduced with reference equality.
        static void LowerBoundEqFileContent(List<UfbxiFileContent> data, int size, string filename, ref int result)
        {
            int lo = 0, hi = size;
            while (hi - lo > 8) {
                int mid = lo + (hi - lo) / 2;
                if (UfbxiStr.Less(data[mid].AbsoluteFilename, filename)) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (ReferenceEquals(data[lo].AbsoluteFilename, filename)) { result = lo; break; }
            }
        }

        // C: ufbxi_fetch_file_content (ufbx.c:21480-21491).
        static void FetchFileContent(UfbxiContext uc, ref string pFilename, ref byte[] pData)
        {
            if (pData != null && pData.Length > 0) return;
            string filename = pFilename;
            int index = int.MaxValue; // C: SIZE_MAX
            LowerBoundEqFileContent(uc.FileContent, uc.FileContent.Count, filename, ref index);
            if (index != int.MaxValue) {
                pData = uc.FileContent[index].Content;
            }
        }

        // C: ufbxi_resolve_file_content (ufbx.c:21493-21531).
        internal static void ResolveFileContent(UfbxiContext uc)
        {
            foreach (UfbxVideo video in uc.Scene.Videos) {
                UfbxiSceneFiles.UfbxiStrblob filename = default, absolute = default, relative = default;
                filename.Str = video.Filename;
                absolute.Str = video.AbsoluteFilename;
                relative.Str = video.RelativeFilename;
                ResolveFilenames(uc, ref filename, ref absolute, ref relative, false);
                video.Filename = filename.Str;
                video.AbsoluteFilename = absolute.Str;
                video.RelativeFilename = relative.Str;

                UfbxiSceneFiles.UfbxiStrblob rawFilename = default, rawAbsolute = default, rawRelative = default;
                rawFilename.Blob = video.RawFilename;
                rawAbsolute.Blob = video.RawAbsoluteFilename;
                rawRelative.Blob = video.RawRelativeFilename;
                ResolveFilenames(uc, ref rawFilename, ref rawAbsolute, ref rawRelative, true);
                video.RawFilename = rawFilename.Blob;
                video.RawAbsoluteFilename = rawAbsolute.Blob;
                video.RawRelativeFilename = rawRelative.Blob;

                PushFileContent(uc, video.AbsoluteFilename, video.Content);
            }

            foreach (UfbxAudioClip clip in uc.Scene.AudioClips) {
                clip.AbsoluteFilename = FindStringLen(clip.Props, "Path", string.Empty);
                clip.RelativeFilename = FindStringLen(clip.Props, "RelPath", string.Empty);
                clip.RawAbsoluteFilename = FindBlobLen(clip.Props, "Path", null);
                clip.RawRelativeFilename = FindBlobLen(clip.Props, "RelPath", null);

                UfbxiSceneFiles.UfbxiStrblob filename = default, absolute = default, relative = default;
                filename.Str = clip.Filename;
                absolute.Str = clip.AbsoluteFilename;
                relative.Str = clip.RelativeFilename;
                ResolveFilenames(uc, ref filename, ref absolute, ref relative, false);
                clip.Filename = filename.Str;
                clip.AbsoluteFilename = absolute.Str;
                clip.RelativeFilename = relative.Str;

                UfbxiSceneFiles.UfbxiStrblob rawFilename = default, rawAbsolute = default, rawRelative = default;
                rawFilename.Blob = clip.RawFilename;
                rawAbsolute.Blob = clip.RawAbsoluteFilename;
                rawRelative.Blob = clip.RawRelativeFilename;
                ResolveFilenames(uc, ref rawFilename, ref rawAbsolute, ref rawRelative, true);
                clip.RawFilename = rawFilename.Blob;
                clip.RawAbsoluteFilename = rawAbsolute.Blob;
                clip.RawRelativeFilename = rawRelative.Blob;

                PushFileContent(uc, clip.AbsoluteFilename, clip.Content);
            }

            UfbxiFileContent[] contentArr = uc.FileContent.ToArray();
            SortFileContents(contentArr, contentArr.Length);

            foreach (UfbxVideo video in uc.Scene.Videos) {
                string fn = video.AbsoluteFilename;
                byte[] data = video.Content;
                FetchFileContent(uc, ref fn, ref data);
                video.Content = data;
            }

            foreach (UfbxAudioClip clip in uc.Scene.AudioClips) {
                string fn = clip.AbsoluteFilename;
                byte[] data = clip.Content;
                FetchFileContent(uc, ref fn, ref data);
                clip.Content = data;
            }
        }

        // ==================================================================
        // ufbxi_validate_indices (ufbx.c:21533-21549)
        // ==================================================================

        internal static void ValidateIndices(UfbxiContext uc, ref uint[] indices, int maxIndex)
        {
            int count = indices != null ? indices.Length : 0;

            if (maxIndex == 0 && uc.Opts.IndexErrorHandling == UfbxIndexErrorHandling.Clamp) {
                indices = null;
                return;
            }

            for (int i = 0; i < count; i++) {
                uint ix = indices[i];
                if (ix >= (uint)maxIndex) {
                    UfbxiReadElement.FixIndex(uc, indices, i, ix, (ulong)maxIndex);
                }
            }
        }

        // ==================================================================
        // Public `ufbx_find_*` helpers (ufbx.c:30643-30765, 31413-31484)
        // ==================================================================

        // C: ufbx_find_prop_len (ufbx.c:30643-30658) via UfbxiProperties.TryFindPropLen.
        internal static bool FindPropLen(UfbxProps props, string name, out UfbxProp prop)
        {
            if (UfbxiProperties.TryFindPropLen(props, name, out UfbxProps owner, out int index)) {
                prop = owner.Props[index];
                return true;
            }
            prop = default;
            return false;
        }

        // C: ufbx_find_real_len (ufbx.c:30660-30668).
        internal static double FindRealLen(UfbxProps props, string name, double def)
        {
            if (FindPropLen(props, name, out UfbxProp prop)) return prop.ValueReal;
            return def;
        }

        // C: ufbx_find_vec3_len (ufbx.c:30670-30678).
        internal static UfbxVec3 FindVec3Len(UfbxProps props, string name, UfbxVec3 def)
        {
            if (FindPropLen(props, name, out UfbxProp prop)) return prop.ValueVec3;
            return def;
        }

        // C: ufbx_find_int_len (ufbx.c:30680-30688).
        internal static long FindIntLen(UfbxProps props, string name, long def)
        {
            if (FindPropLen(props, name, out UfbxProp prop)) return prop.ValueInt;
            return def;
        }

        // C: ufbx_find_bool_len (ufbx.c:30690-30698).
        internal static bool FindBoolLen(UfbxProps props, string name, bool def)
        {
            if (FindPropLen(props, name, out UfbxProp prop)) return prop.ValueInt != 0;
            return def;
        }

        // C: ufbx_find_string_len (ufbx.c:30700-30708).
        internal static string FindStringLen(UfbxProps props, string name, string def)
        {
            if (FindPropLen(props, name, out UfbxProp prop)) return prop.ValueStr;
            return def;
        }

        // C: ufbx_find_blob_len (ufbx.c:30710-30718).
        internal static byte[] FindBlobLen(UfbxProps props, string name, byte[] def)
        {
            if (FindPropLen(props, name, out UfbxProp prop)) return prop.ValueBlob;
            return def;
        }

        // C: ufbx_find_prop_concat (ufbx.c:30720-30736).
        internal static UfbxProp FindPropConcat(UfbxProps props, UfbxiConcatPart[] parts, int numParts)
        {
            uint key = UfbxiStr.GetConcatKey(parts, numParts);

            while (props != null) {
                UfbxProp[] data = props.Props;
                if (data != null) {
                    int size = data.Length;
                    int lo = 0, hi = size;
                    while (hi - lo > 2) {
                        int mid = lo + (hi - lo) / 2;
                        if (UfbxiSceneBuild.CmpPropLessConcat(data[mid], parts, numParts, key)) {
                            lo = mid + 1;
                        } else {
                            hi = mid + 1;
                        }
                    }
                    for (; lo < hi; lo++) {
                        UfbxProp a = data[lo];
                        if (a.InternalKey == key && UfbxiStr.ConcatStrCmp(a.Name, parts, numParts) == 0) {
                            return a;
                        }
                    }
                }
                props = props.Defaults;
            }

            return default;
        }

        // C: ufbx_get_prop_element (ufbx.c:30751-30756).
        internal static UfbxElement GetPropElement(UfbxElement element, UfbxProp prop, UfbxElementType type)
        {
            if (element == null || prop.Name == null) return null;
            return UfbxiSceneBuild.FetchDstElement(element, false, prop.Name, type);
        }

        // C: ufbx_find_prop_texture_len (ufbx.c:31422-31431).
        internal static UfbxTexture FindPropTexture(UfbxMaterial material, string name)
        {
            if (material == null) return null;

            UfbxMaterialTexture[] textures = material.Textures;
            int size = textures != null ? textures.Length : 0;

            int index = int.MaxValue; // C: SIZE_MAX
            int lo = 0, hi = size;
            while (hi - lo > 4) {
                int mid = lo + (hi - lo) / 2;
                if (UfbxiStr.Less(textures[mid].MaterialProp, name)) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (UfbxiStr.Equal(textures[lo].MaterialProp, name)) { index = lo; break; }
            }

            return index != int.MaxValue ? textures[index].Texture : null;
        }

        // C: ufbx_find_shader_prop_bindings_len (ufbx.c:31442-31469). Returns the matched
        // binding slice (empty when no binding matches or `shader` is NULL).
        internal static UfbxShaderPropBinding[] FindShaderPropBindings(UfbxShader shader, string name)
        {
            if (shader == null) return null;

            if (shader.Bindings != null) {
                foreach (UfbxShaderBinding bind in shader.Bindings) {
                    UfbxShaderPropBinding[] data = bind.PropBindings;
                    int size = data != null ? data.Length : 0;

                    int begin = int.MaxValue; // C: SIZE_MAX
                    {
                        int lo = 0, hi = size;
                        while (hi - lo > 4) {
                            int mid = lo + (hi - lo) / 2;
                            if (UfbxiStr.Less(data[mid].ShaderProp, name)) {
                                lo = mid + 1;
                            } else {
                                hi = mid + 1;
                            }
                        }
                        for (; lo < hi; lo++) {
                            if (UfbxiStr.Equal(data[lo].ShaderProp, name)) { begin = lo; break; }
                        }
                    }

                    if (begin != int.MaxValue) {
                        int end = begin;
                        {
                            int lo = begin, hi = size;
                            for (int step = 1; step < 100 && hi - lo > step; step *= 2) {
                                if (!UfbxiStr.Equal(data[lo + step].ShaderProp, name)) { hi = lo + step; break; }
                                lo += step;
                            }
                            while (hi - lo > 4) {
                                int mid = lo + (hi - lo) / 2;
                                if (UfbxiStr.Equal(data[mid].ShaderProp, name)) {
                                    lo = mid + 1;
                                } else {
                                    hi = mid + 1;
                                }
                            }
                            for (; lo < hi; lo++) {
                                if (!UfbxiStr.Equal(data[lo].ShaderProp, name)) break;
                            }
                            end = lo;
                        }

                        UfbxShaderPropBinding[] result = new UfbxShaderPropBinding[end - begin];
                        Array.Copy(data, begin, result, 0, result.Length);
                        return result;
                    }
                }
            }

            return null;
        }

        // C: ufbx_find_shader_texture_input_len (ufbx.c:31471-31484) over a materialized
        // input list (used while the input list is still being built).
        internal static UfbxShaderTextureInput FindShaderTextureInput(List<UfbxShaderTextureInput> inputs, string name)
        {
            int size = inputs.Count;

            int index = int.MaxValue; // C: SIZE_MAX
            int lo = 0, hi = size;
            while (hi - lo > 4) {
                int mid = lo + (hi - lo) / 2;
                if (UfbxiStr.Less(inputs[mid].Name, name)) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (UfbxiStr.Equal(inputs[lo].Name, name)) { index = lo; break; }
            }

            return index != int.MaxValue ? inputs[index] : null;
        }

        // C: ufbx_find_shader_texture_input (ufbx.c:33168) over the final input array.
        internal static UfbxShaderTextureInput FindShaderTextureInput(UfbxShaderTexture shader, string name)
        {
            UfbxShaderTextureInput[] inputs = shader.Inputs;
            int size = inputs != null ? inputs.Length : 0;

            int index = int.MaxValue; // C: SIZE_MAX
            int lo = 0, hi = size;
            while (hi - lo > 4) {
                int mid = lo + (hi - lo) / 2;
                if (UfbxiStr.Less(inputs[mid].Name, name)) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (UfbxiStr.Equal(inputs[lo].Name, name)) { index = lo; break; }
            }

            return index != int.MaxValue ? inputs[index] : null;
        }

        // C: ufbx_get_bone_pose (ufbx.c:31413-31420).
        internal static UfbxBonePose GetBonePose(UfbxPose pose, UfbxNode node)
        {
            if (pose == null || node == null) return null;
            UfbxBonePose[] data = pose.BonePoses;
            int size = data != null ? data.Length : 0;

            int index = int.MaxValue; // C: SIZE_MAX
            int lo = 0, hi = size;
            while (hi - lo > 8) {
                int mid = lo + (hi - lo) / 2;
                if (data[mid].BoneNode.TypedId < node.TypedId) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                if (ReferenceEquals(data[lo].BoneNode, node)) { index = lo; break; }
            }

            return index != int.MaxValue ? data[index] : null;
        }
    }
}
