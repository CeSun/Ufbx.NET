// Material / texture / shader element types ported from ufbx v0.23.1 (ufbx.h).
// Element header fields are carried by the base UfbxElement.

namespace Ufbx
{
	// C: typedef struct ufbx_material_map (ufbx.h:2320)
	// Material property, either a constant value or a mapped texture.
	// The C `union { real; vec2; vec3; vec4; }` is stored as a single UfbxVec4 so the
	// different views alias the same 4 doubles exactly as the C union does.
	public sealed class UfbxMaterialMap
	{
		// Shared union storage.
		public UfbxVec4 ValueVec4;

		public double ValueReal
		{
			get => ValueVec4.X;
			set => ValueVec4.X = value;
		}

		public UfbxVec2 ValueVec2
		{
			get => new UfbxVec2(ValueVec4.X, ValueVec4.Y);
			set { ValueVec4.X = value.X; ValueVec4.Y = value.Y; }
		}

		public UfbxVec3 ValueVec3
		{
			get => new UfbxVec3(ValueVec4.X, ValueVec4.Y, ValueVec4.Z);
			set { ValueVec4.X = value.X; ValueVec4.Y = value.Y; ValueVec4.Z = value.Z; }
		}

		public long ValueInt;

		// Texture if connected, otherwise null.
		public UfbxTexture Texture;

		// True if the file has specified any of the values above.
		public bool HasValue;

		// Controls whether shading should use Texture.
		public bool TextureEnabled;

		// Set to true if this feature should be disabled (specific to shader type).
		public bool FeatureDisabled;

		// Number of components in the value from 1 to 4 if defined, 0 if not.
		public byte ValueComponents;

		// C: `ufbx_material_map` is embedded by value in `ufbx_material.fbx`/`pbr`, so copying an
		// element copies each map (ufbx.c:26157-26159) before ufbxi_translate_maps() rewrites
		// `texture` in the copy (ufbx.c:26097-26102).
		internal UfbxMaterialMap Clone() => (UfbxMaterialMap)MemberwiseClone();
	}

	// C: typedef struct ufbx_material_feature_info (ufbx.h:2332)
	public sealed class UfbxMaterialFeatureInfo
	{
		// Whether the material model uses this feature or not.
		public bool Enabled;

		// Explicitly enabled/disabled by the material.
		public bool IsExplicit;

		// C: embedded by value in `ufbx_material.features`, see UfbxMaterialMap.Clone.
		internal UfbxMaterialFeatureInfo Clone() => (UfbxMaterialFeatureInfo)MemberwiseClone();
	}

	// C: typedef struct ufbx_material_texture (ufbx.h:2342)
	// Texture attached to an FBX property.
	public sealed class UfbxMaterialTexture
	{
		// Name of the property in UfbxMaterial.Props.
		public string MaterialProp;

		// Shader-specific property mapping name.
		public string ShaderProp;

		// Texture attached to the property.
		public UfbxTexture Texture;

		// C: `textures[i] = material->textures.data[i]` (ufbx.c:26259).
		internal UfbxMaterialTexture Clone() => (UfbxMaterialTexture)MemberwiseClone();
	}

	// C: typedef struct ufbx_material_fbx_maps (ufbx.h:2513)
	// Union of `maps[]` and named fields; named fields alias the array in C.
	public sealed class UfbxMaterialFbxMaps
	{
		public readonly UfbxMaterialMap[] Maps;

		public UfbxMaterialFbxMaps()
		{
			Maps = new UfbxMaterialMap[UfbxEnumCounts.UfbxMaterialFbxMap];
			for (int i = 0; i < Maps.Length; i++) Maps[i] = new UfbxMaterialMap();
		}

		public UfbxMaterialMap DiffuseFactor { get => Maps[0]; set => Maps[0] = value; }
		public UfbxMaterialMap DiffuseColor { get => Maps[1]; set => Maps[1] = value; }
		public UfbxMaterialMap SpecularFactor { get => Maps[2]; set => Maps[2] = value; }
		public UfbxMaterialMap SpecularColor { get => Maps[3]; set => Maps[3] = value; }
		public UfbxMaterialMap SpecularExponent { get => Maps[4]; set => Maps[4] = value; }
		public UfbxMaterialMap ReflectionFactor { get => Maps[5]; set => Maps[5] = value; }
		public UfbxMaterialMap ReflectionColor { get => Maps[6]; set => Maps[6] = value; }
		public UfbxMaterialMap TransparencyFactor { get => Maps[7]; set => Maps[7] = value; }
		public UfbxMaterialMap TransparencyColor { get => Maps[8]; set => Maps[8] = value; }
		public UfbxMaterialMap EmissionFactor { get => Maps[9]; set => Maps[9] = value; }
		public UfbxMaterialMap EmissionColor { get => Maps[10]; set => Maps[10] = value; }
		public UfbxMaterialMap AmbientFactor { get => Maps[11]; set => Maps[11] = value; }
		public UfbxMaterialMap AmbientColor { get => Maps[12]; set => Maps[12] = value; }
		public UfbxMaterialMap NormalMap { get => Maps[13]; set => Maps[13] = value; }
		public UfbxMaterialMap Bump { get => Maps[14]; set => Maps[14] = value; }
		public UfbxMaterialMap BumpFactor { get => Maps[15]; set => Maps[15] = value; }
		public UfbxMaterialMap DisplacementFactor { get => Maps[16]; set => Maps[16] = value; }
		public UfbxMaterialMap Displacement { get => Maps[17]; set => Maps[17] = value; }
		public UfbxMaterialMap VectorDisplacementFactor { get => Maps[18]; set => Maps[18] = value; }
		public UfbxMaterialMap VectorDisplacement { get => Maps[19]; set => Maps[19] = value; }

		// C: the `maps[]` union member is inline storage inside `ufbx_material`, so the element
		// copy gives the evaluated scene its own set (ufbx.c:26157-26159). Needed for real:
		// ufbxi_translate_maps() writes `texture` here (ufbx.c:26097-26102) and
		// ufbxi_fetch_maps() memsets and refills the whole group (ufbx.c:20130-20132).
		internal UfbxMaterialFbxMaps CloneMaps()
		{
			UfbxMaterialFbxMaps dst = new UfbxMaterialFbxMaps();
			for (int i = 0; i < dst.Maps.Length; i++) dst.Maps[i] = Maps[i].Clone();
			return dst;
		}
	}

	// C: typedef struct ufbx_material_pbr_maps (ufbx.h:2541)
	public sealed class UfbxMaterialPbrMaps
	{
		public readonly UfbxMaterialMap[] Maps;

		public UfbxMaterialPbrMaps()
		{
			Maps = new UfbxMaterialMap[UfbxEnumCounts.UfbxMaterialPbrMap];
			for (int i = 0; i < Maps.Length; i++) Maps[i] = new UfbxMaterialMap();
		}

		public UfbxMaterialMap BaseFactor { get => Maps[0]; set => Maps[0] = value; }
		public UfbxMaterialMap BaseColor { get => Maps[1]; set => Maps[1] = value; }
		public UfbxMaterialMap Roughness { get => Maps[2]; set => Maps[2] = value; }
		public UfbxMaterialMap Metalness { get => Maps[3]; set => Maps[3] = value; }
		public UfbxMaterialMap DiffuseRoughness { get => Maps[4]; set => Maps[4] = value; }
		public UfbxMaterialMap SpecularFactor { get => Maps[5]; set => Maps[5] = value; }
		public UfbxMaterialMap SpecularColor { get => Maps[6]; set => Maps[6] = value; }
		public UfbxMaterialMap SpecularIor { get => Maps[7]; set => Maps[7] = value; }
		public UfbxMaterialMap SpecularAnisotropy { get => Maps[8]; set => Maps[8] = value; }
		public UfbxMaterialMap SpecularRotation { get => Maps[9]; set => Maps[9] = value; }
		public UfbxMaterialMap TransmissionFactor { get => Maps[10]; set => Maps[10] = value; }
		public UfbxMaterialMap TransmissionColor { get => Maps[11]; set => Maps[11] = value; }
		public UfbxMaterialMap TransmissionDepth { get => Maps[12]; set => Maps[12] = value; }
		public UfbxMaterialMap TransmissionScatter { get => Maps[13]; set => Maps[13] = value; }
		public UfbxMaterialMap TransmissionScatterAnisotropy { get => Maps[14]; set => Maps[14] = value; }
		public UfbxMaterialMap TransmissionDispersion { get => Maps[15]; set => Maps[15] = value; }
		public UfbxMaterialMap TransmissionRoughness { get => Maps[16]; set => Maps[16] = value; }
		public UfbxMaterialMap TransmissionExtraRoughness { get => Maps[17]; set => Maps[17] = value; }
		public UfbxMaterialMap TransmissionPriority { get => Maps[18]; set => Maps[18] = value; }
		public UfbxMaterialMap TransmissionEnableInAov { get => Maps[19]; set => Maps[19] = value; }
		public UfbxMaterialMap SubsurfaceFactor { get => Maps[20]; set => Maps[20] = value; }
		public UfbxMaterialMap SubsurfaceColor { get => Maps[21]; set => Maps[21] = value; }
		public UfbxMaterialMap SubsurfaceRadius { get => Maps[22]; set => Maps[22] = value; }
		public UfbxMaterialMap SubsurfaceScale { get => Maps[23]; set => Maps[23] = value; }
		public UfbxMaterialMap SubsurfaceAnisotropy { get => Maps[24]; set => Maps[24] = value; }
		public UfbxMaterialMap SubsurfaceTintColor { get => Maps[25]; set => Maps[25] = value; }
		public UfbxMaterialMap SubsurfaceType { get => Maps[26]; set => Maps[26] = value; }
		public UfbxMaterialMap SheenFactor { get => Maps[27]; set => Maps[27] = value; }
		public UfbxMaterialMap SheenColor { get => Maps[28]; set => Maps[28] = value; }
		public UfbxMaterialMap SheenRoughness { get => Maps[29]; set => Maps[29] = value; }
		public UfbxMaterialMap CoatFactor { get => Maps[30]; set => Maps[30] = value; }
		public UfbxMaterialMap CoatColor { get => Maps[31]; set => Maps[31] = value; }
		public UfbxMaterialMap CoatRoughness { get => Maps[32]; set => Maps[32] = value; }
		public UfbxMaterialMap CoatIor { get => Maps[33]; set => Maps[33] = value; }
		public UfbxMaterialMap CoatAnisotropy { get => Maps[34]; set => Maps[34] = value; }
		public UfbxMaterialMap CoatRotation { get => Maps[35]; set => Maps[35] = value; }
		public UfbxMaterialMap CoatNormal { get => Maps[36]; set => Maps[36] = value; }
		public UfbxMaterialMap CoatAffectBaseColor { get => Maps[37]; set => Maps[37] = value; }
		public UfbxMaterialMap CoatAffectBaseRoughness { get => Maps[38]; set => Maps[38] = value; }
		public UfbxMaterialMap ThinFilmFactor { get => Maps[39]; set => Maps[39] = value; }
		public UfbxMaterialMap ThinFilmThickness { get => Maps[40]; set => Maps[40] = value; }
		public UfbxMaterialMap ThinFilmIor { get => Maps[41]; set => Maps[41] = value; }
		public UfbxMaterialMap EmissionFactor { get => Maps[42]; set => Maps[42] = value; }
		public UfbxMaterialMap EmissionColor { get => Maps[43]; set => Maps[43] = value; }
		public UfbxMaterialMap Opacity { get => Maps[44]; set => Maps[44] = value; }
		public UfbxMaterialMap IndirectDiffuse { get => Maps[45]; set => Maps[45] = value; }
		public UfbxMaterialMap IndirectSpecular { get => Maps[46]; set => Maps[46] = value; }
		public UfbxMaterialMap NormalMap { get => Maps[47]; set => Maps[47] = value; }
		public UfbxMaterialMap TangentMap { get => Maps[48]; set => Maps[48] = value; }
		public UfbxMaterialMap DisplacementMap { get => Maps[49]; set => Maps[49] = value; }
		public UfbxMaterialMap MatteFactor { get => Maps[50]; set => Maps[50] = value; }
		public UfbxMaterialMap MatteColor { get => Maps[51]; set => Maps[51] = value; }
		public UfbxMaterialMap AmbientOcclusion { get => Maps[52]; set => Maps[52] = value; }
		public UfbxMaterialMap Glossiness { get => Maps[53]; set => Maps[53] = value; }
		public UfbxMaterialMap CoatGlossiness { get => Maps[54]; set => Maps[54] = value; }
		public UfbxMaterialMap TransmissionGlossiness { get => Maps[55]; set => Maps[55] = value; }

		// C: inline `maps[]` union storage, see UfbxMaterialFbxMaps.CloneMaps.
		internal UfbxMaterialPbrMaps CloneMaps()
		{
			UfbxMaterialPbrMaps dst = new UfbxMaterialPbrMaps();
			for (int i = 0; i < dst.Maps.Length; i++) dst.Maps[i] = Maps[i].Clone();
			return dst;
		}
	}

	// C: typedef struct ufbx_material_features (ufbx.h:2605)
	public sealed class UfbxMaterialFeatures
	{
		public readonly UfbxMaterialFeatureInfo[] Features;

		public UfbxMaterialFeatures()
		{
			Features = new UfbxMaterialFeatureInfo[UfbxEnumCounts.UfbxMaterialFeature];
			for (int i = 0; i < Features.Length; i++) Features[i] = new UfbxMaterialFeatureInfo();
		}

		public UfbxMaterialFeatureInfo Pbr { get => Features[0]; set => Features[0] = value; }
		public UfbxMaterialFeatureInfo Metalness { get => Features[1]; set => Features[1] = value; }
		public UfbxMaterialFeatureInfo Diffuse { get => Features[2]; set => Features[2] = value; }
		public UfbxMaterialFeatureInfo Specular { get => Features[3]; set => Features[3] = value; }
		public UfbxMaterialFeatureInfo Emission { get => Features[4]; set => Features[4] = value; }
		public UfbxMaterialFeatureInfo Transmission { get => Features[5]; set => Features[5] = value; }
		public UfbxMaterialFeatureInfo Coat { get => Features[6]; set => Features[6] = value; }
		public UfbxMaterialFeatureInfo Sheen { get => Features[7]; set => Features[7] = value; }
		public UfbxMaterialFeatureInfo Opacity { get => Features[8]; set => Features[8] = value; }
		public UfbxMaterialFeatureInfo AmbientOcclusion { get => Features[9]; set => Features[9] = value; }
		public UfbxMaterialFeatureInfo Matte { get => Features[10]; set => Features[10] = value; }
		public UfbxMaterialFeatureInfo Unlit { get => Features[11]; set => Features[11] = value; }
		public UfbxMaterialFeatureInfo Ior { get => Features[12]; set => Features[12] = value; }
		public UfbxMaterialFeatureInfo DiffuseRoughness { get => Features[13]; set => Features[13] = value; }
		public UfbxMaterialFeatureInfo TransmissionRoughness { get => Features[14]; set => Features[14] = value; }
		public UfbxMaterialFeatureInfo ThinWalled { get => Features[15]; set => Features[15] = value; }
		public UfbxMaterialFeatureInfo Caustics { get => Features[16]; set => Features[16] = value; }
		public UfbxMaterialFeatureInfo ExitToBackground { get => Features[17]; set => Features[17] = value; }
		public UfbxMaterialFeatureInfo InternalReflections { get => Features[18]; set => Features[18] = value; }
		public UfbxMaterialFeatureInfo DoubleSided { get => Features[19]; set => Features[19] = value; }
		public UfbxMaterialFeatureInfo RoughnessAsGlossiness { get => Features[20]; set => Features[20] = value; }
		public UfbxMaterialFeatureInfo CoatRoughnessAsGlossiness { get => Features[21]; set => Features[21] = value; }
		public UfbxMaterialFeatureInfo TransmissionRoughnessAsGlossiness { get => Features[22]; set => Features[22] = value; }

		// C: inline `features[]` union storage, see UfbxMaterialFbxMaps.CloneMaps.
		internal UfbxMaterialFeatures CloneFeatures()
		{
			UfbxMaterialFeatures dst = new UfbxMaterialFeatures();
			for (int i = 0; i < dst.Features.Length; i++) dst.Features[i] = Features[i].Clone();
			return dst;
		}
	}

	// C: struct ufbx_material (ufbx.h:2638)
	public class UfbxMaterial : UfbxElement
	{
		public UfbxMaterial()
		{
			// C ufbxi_new memsets these embedded-by-value union groups; mirror that here
			// so the always-present map arrays/instances are non-null.
			Fbx = new UfbxMaterialFbxMaps();
			Pbr = new UfbxMaterialPbrMaps();
			Features = new UfbxMaterialFeatures();
		}

		// FBX builtin properties. NOTE: may be empty if using a custom shader.
		public UfbxMaterialFbxMaps Fbx;

		// PBR material properties, defined for all shading models.
		public UfbxMaterialPbrMaps Pbr;

		// Material features, primarily applies to Pbr.
		public UfbxMaterialFeatures Features;

		// Shading information.
		public UfbxShaderType ShaderType;
		public UfbxShader Shader;
		public string ShadingModelName;

		// Prefix before shader property names with trailing '|'.
		public string ShaderPropPrefix;

		// All textures attached to the material, sorted by MaterialProp.
		public UfbxMaterialTexture[] Textures;
	}

	// C: typedef struct ufbx_texture_layer (ufbx.h:2753)
	public sealed class UfbxTextureLayer
	{
		// The inner texture to evaluate, never null.
		public UfbxTexture Texture;

		// Equation to combine the layer to the background.
		public UfbxBlendMode BlendMode;

		// Blend weight of this layer.
		public double Alpha;

		// C: `layers[i] = texture->layers.data[i]` (ufbx.c:26272).
		internal UfbxTextureLayer Clone() => (UfbxTextureLayer)MemberwiseClone();
	}

	// C: typedef struct ufbx_shader_texture_input (ufbx.h:2810)
	// Input to a shader texture. The value union aliases the same 4 doubles as UfbxVec4.
	public sealed class UfbxShaderTextureInput
	{
		// Name of the input.
		public string Name;

		// Constant value union storage.
		public UfbxVec4 ValueVec4;

		public double ValueReal
		{
			get => ValueVec4.X;
			set => ValueVec4.X = value;
		}

		public UfbxVec2 ValueVec2
		{
			get => new UfbxVec2(ValueVec4.X, ValueVec4.Y);
			set { ValueVec4.X = value.X; ValueVec4.Y = value.Y; }
		}

		public UfbxVec3 ValueVec3
		{
			get => new UfbxVec3(ValueVec4.X, ValueVec4.Y, ValueVec4.Z);
			set { ValueVec4.X = value.X; ValueVec4.Y = value.Y; ValueVec4.Z = value.Z; }
		}

		public long ValueInt;
		public string ValueStr;
		public byte[] ValueBlob;

		// Texture connected to this input.
		public UfbxTexture Texture;

		// Index of the output to use if Texture is a multi-output shader node.
		public long TextureOutputIndex;

		// Controls whether shading should use Texture.
		public bool TextureEnabled;

		// Property representing this input.
		public UfbxProp Prop;

		// Property representing Texture.
		public UfbxProp TextureProp;

		// Property representing TextureEnabled.
		public UfbxProp TextureEnabledProp;

		// C: `ufbxi_push_copy(&ec->result, ufbx_shader_texture_input, count, data)`
		// (ufbx.c:26285) -- and required, not just faithful: ufbxi_update_shader_texture()
		// rewrites `prop`/`texture`/`texture_enabled` in place (ufbx.c:20498-20530).
		internal UfbxShaderTextureInput Clone() => (UfbxShaderTextureInput)MemberwiseClone();
	}

	// C: typedef struct ufbx_shader_texture (ufbx.h:2852)
	// Texture that emulates a shader graph node.
	public sealed class UfbxShaderTexture
	{
		public UfbxShaderTextureType Type;

		// Name of the shader to use.
		public string ShaderName;

		// 64-bit opaque identifier for the shader type.
		public ulong ShaderTypeId;

		// Input values/textures to the shader, sorted by UfbxShaderTextureInput.Name.
		public UfbxShaderTextureInput[] Inputs;

		// Shader source code if found.
		public string ShaderSource;
		public byte[] RawShaderSource;

		// Representative texture for this shader.
		public UfbxTexture MainTexture;

		// Output index of MainTexture if it is a multi-output shader.
		public long MainTextureOutputIndex;

		// Prefix for properties related to this shader. Contains trailing '|' if not empty.
		public string PropPrefix;

		// C: `ufbxi_push_copy(&ec->result, ufbx_shader_texture, 1, shader)` (ufbx.c:26281);
		// `Inputs` is replaced by the caller with a cloned array (ufbx.c:26285-26287).
		internal UfbxShaderTexture CloneShallow() => (UfbxShaderTexture)MemberwiseClone();
	}

	// C: typedef struct ufbx_texture_file (ufbx.h:2885)
	// Unique texture within the file.
	public sealed class UfbxTextureFile
	{
		// Index in ufbx_scene.texture_files[].
		public uint Index;

		// Paths to the resource.
		public string Filename;
		public string AbsoluteFilename;
		public string RelativeFilename;

		// Non-UTF-8 encoded variants.
		public byte[] RawFilename;
		public byte[] RawAbsoluteFilename;
		public byte[] RawRelativeFilename;

		// Optional embedded content blob, eg. raw .png format data.
		public byte[] Content;
	}

	// C: struct ufbx_texture (ufbx.h:2890)
	public class UfbxTexture : UfbxElement
	{
		// Texture type (file / layered / procedural / shader). C: ufbx_texture_type type shadows ufbx_element.type.
		public new UfbxTextureType Type;

		// FILE: Paths to the resource.
		public string Filename;
		public string AbsoluteFilename;
		public string RelativeFilename;

		public byte[] RawFilename;
		public byte[] RawAbsoluteFilename;
		public byte[] RawRelativeFilename;

		// FILE: Optional embedded content blob.
		public byte[] Content;

		// FILE: Optional video texture.
		public UfbxVideo Video;

		// FILE: Index into ufbx_scene.texture_files[] or UFBX_NO_INDEX.
		public uint FileIndex;

		// FILE: True if FileIndex has a valid value.
		public bool HasFile;

		// LAYERED: Inner texture layers, ordered from bottom to top.
		public UfbxTextureLayer[] Layers;

		// SHADER: Shader information.
		public UfbxShaderTexture Shader;

		// List of file textures representing this texture.
		public UfbxTexture[] FileTextures;

		// Name of the UV set to use.
		public string UvSet;

		public UfbxWrapMode WrapU;
		public UfbxWrapMode WrapV;

		// UV transform.
		public bool HasUvTransform;
		public UfbxTransform UvTransform;
		public UfbxMatrix TextureToUv;
		public UfbxMatrix UvToTexture;
	}

	// C: struct ufbx_video (ufbx.h:2962)
	public class UfbxVideo : UfbxElement
	{
		// Paths to the resource.
		public string Filename;
		public string AbsoluteFilename;
		public string RelativeFilename;

		public byte[] RawFilename;
		public byte[] RawAbsoluteFilename;
		public byte[] RawRelativeFilename;

		// Optional embedded content blob.
		public byte[] Content;
	}

	// C: struct ufbx_shader (ufbx.h:2998)
	public class UfbxShader : UfbxElement
	{
		// Known shading model. C: ufbx_shader_type type shadows ufbx_element.type.
		public new UfbxShaderType Type;

		// Bindings from FBX properties to the shader.
		public UfbxShaderBinding[] Bindings;
	}

	// C: typedef struct ufbx_shader_prop_binding (ufbx.h:3020)
	// Binding from a material property to shader implementation.
	public sealed class UfbxShaderPropBinding
	{
		// Property name used by the shader implementation.
		public string ShaderProp;

		// Property name inside UfbxMaterial.Props.
		public string MaterialProp;
	}

	// C: struct ufbx_shader_binding (ufbx.h:3025)
	// Shader binding table.
	public class UfbxShaderBinding : UfbxElement
	{
		// Sorted by ShaderProp.
		public UfbxShaderPropBinding[] PropBindings;
	}

	// C: typedef struct ufbx_prop_override (ufbx.h:3047)
	public sealed class UfbxPropOverride
	{
		public uint ElementId;

		// C: uint32_t _internal_key
		public uint InternalKey;

		public string PropName;
		public UfbxVec4 Value;
		public string ValueStr;
		public long ValueInt;
	}

	// C: typedef struct ufbx_transform_override (ufbx.h:3054)
	public sealed class UfbxTransformOverride
	{
		public uint NodeId;
		public UfbxTransform Transform;
	}
}
