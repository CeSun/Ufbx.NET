// Deformer and geometry-cache element types ported from ufbx v0.23.1 (ufbx.h).
// Element header fields are carried by the base UfbxElement.

namespace Ufbx
{
	// C: typedef struct ufbx_skin_vertex (ufbx.h:1967)
	// Skin weight information for a single mesh vertex.
	public struct UfbxSkinVertex
	{
		// Index to start from in the weights array; weights sorted by decreasing weight.
		// NOTE: Weights are not guaranteed to be normalized!
		public uint WeightBegin;
		public uint NumWeights;

		// Blend weight between Linear Blend Skinning (0.0) and Dual Quaternion (1.0).
		public double DqWeight;
	}

	// C: typedef struct ufbx_skin_weight (ufbx.h:1975)
	// Single per-vertex per-cluster weight.
	public struct UfbxSkinWeight
	{
		// Index into UfbxSkinDeformer.Clusters.
		public uint ClusterIndex;

		// Amount this bone influences the vertex.
		public double Weight;
	}

	// C: struct ufbx_skin_deformer (ufbx.h:1982)
	public class UfbxSkinDeformer : UfbxElement
	{
		public UfbxSkinningMethod SkinningMethod;

		// Clusters (bones) in the skin.
		public UfbxSkinCluster[] Clusters;

		// Per-vertex weight information.
		public UfbxSkinVertex[] Vertices;
		public UfbxSkinWeight[] Weights;

		// Largest amount of weights a single vertex can have.
		public int MaxWeightsPerVertex;

		// Blend weights between Linear Blend Skinning (0.0) and Dual Quaternion (1.0).
		// NOTE: May be out-of-bounds for a given mesh, Vertices is always safe.
		public int NumDqWeights;
		public uint[] DqVertices;
		public double[] DqWeights;
	}

	// C: struct ufbx_skin_cluster (ufbx.h:2011)
	// Cluster of vertices bound to a single bone.
	public class UfbxSkinCluster : UfbxElement
	{
		// The bone node the cluster is attached to.
		public UfbxNode BoneNode;

		// Binding matrix from local mesh vertices to the bone.
		public UfbxMatrix GeometryToBone;

		// Binding matrix from local mesh node to the bone.
		public UfbxMatrix MeshNodeToBone;

		// Rest/bind pose transform of the node; not generally needed for skinning.
		public UfbxMatrix BindToWorld;

		// Precomputed matrix/transform accounting for the current bone transform.
		public UfbxMatrix GeometryToWorld;
		public UfbxTransform GeometryToWorldTransform;

		// Raw weights indexed by each vertex of a mesh.
		// NOTE: May be out-of-bounds for a given mesh, skin deformer vertices is always safe.
		public int NumWeights;
		public uint[] Vertices;
		public double[] Weights;
	}

	// C: struct ufbx_blend_deformer (ufbx.h:2050)
	public class UfbxBlendDeformer : UfbxElement
	{
		// Independent morph targets of the deformer.
		public UfbxBlendChannel[] Channels;
	}

	// C: typedef struct ufbx_blend_keyframe (ufbx.h:2072)
	// Blend shape associated with a target weight in a series of morphs.
	public class UfbxBlendKeyframe
	{
		// The target blend shape offsets.
		public UfbxBlendShape Shape;

		// Weight value at which to apply the keyframe at full strength.
		public double TargetWeight;

		// The weight the shape should be currently applied with.
		public double EffectiveWeight;

		// C: `keys[i] = chan->keyframes.data[i]` (ufbx.c:26237); ufbxi_update_blend_channel()
		// rewrites `effective_weight` in place, so the copy is required (ufbx.c:23302-23345).
		internal UfbxBlendKeyframe Clone() => (UfbxBlendKeyframe)MemberwiseClone();
	}

	// C: struct ufbx_blend_channel (ufbx.h:2078)
	public class UfbxBlendChannel : UfbxElement
	{
		// Current weight of the channel.
		public double Weight;

		// Key morph targets to blend between depending on weight.
		public UfbxBlendKeyframe[] Keyframes;

		// Final blend shape ignoring any intermediate blend shapes.
		public UfbxBlendShape TargetShape;
	}

	// C: struct ufbx_blend_shape (ufbx.h:2098)
	// Blend shape target containing the actual vertex offsets.
	public class UfbxBlendShape : UfbxElement
	{
		// NOTE: OffsetVertices may be out-of-bounds for a given mesh!
		public int NumOffsets;
		public uint[] OffsetVertices;
		public UfbxVec3[] PositionOffsets;
		public UfbxVec3[] NormalOffsets;

		// Optional weights for the offsets (Blender-only, not standard FBX).
		public double[] OffsetWeights;
	}

	// C: typedef struct ufbx_cache_frame (ufbx.h:2198)
	public class UfbxCacheFrame
	{
		// Name of the channel this frame belongs to.
		public string Channel;

		// Time of this frame in seconds.
		public double Time;

		// Name of the file containing the data.
		public string Filename;

		// Format of the wrapper file.
		public UfbxCacheFileFormat FileFormat;

		// Axis to mirror the read data by.
		public UfbxMirrorAxis MirrorAxis;

		// Factor to scale the geometry by.
		public double ScaleFactor;

		public UfbxCacheDataFormat DataFormat;
		public UfbxCacheDataEncoding DataEncoding;
		public ulong DataOffset;
		public uint DataCount;
		public uint DataElementBytes;
		public ulong DataTotalBytes;
	}

	// C: typedef struct ufbx_cache_channel (ufbx.h:2224)
	public class UfbxCacheChannel
	{
		// Name of the geometry cache channel.
		public string Name;

		// What the data in this channel represents.
		public UfbxCacheInterpretation Interpretation;

		// Source name for Interpretation.
		public string InterpretationName;

		// Frames belonging to this channel, sorted by UfbxCacheFrame.Time.
		public UfbxCacheFrame[] Frames;

		public UfbxMirrorAxis MirrorAxis;
		public double ScaleFactor;
	}

	// C: typedef struct ufbx_geometry_cache (ufbx.h:2233)
	public class UfbxGeometryCache
	{
		public string RootFilename;
		public UfbxCacheChannel[] Channels;
		public UfbxCacheFrame[] Frames;
		public string[] ExtraInfo;
	}

	// C: struct ufbx_cache_deformer (ufbx.h:2235)
	public class UfbxCacheDeformer : UfbxElement
	{
		public string Channel;
		public UfbxCacheFile File;

		// Only valid if ufbx_load_opts.load_external_files is set!
		public UfbxGeometryCache ExternalCache;
		public UfbxCacheChannel ExternalChannel;
	}

	// C: struct ufbx_cache_file (ufbx.h:2251)
	public class UfbxCacheFile : UfbxElement
	{
		// Paths to the resource.
		public string Filename;
		public string AbsoluteFilename;
		public string RelativeFilename;

		// Non-UTF-8 encoded variants.
		public byte[] RawFilename;
		public byte[] RawAbsoluteFilename;
		public byte[] RawRelativeFilename;

		public UfbxCacheFileFormat Format;

		// Only valid if ufbx_load_opts.load_external_files is set!
		public UfbxGeometryCache ExternalCache;
	}
}
