// Node-attribute element types (light/camera/bone/empty/curves/nurbs/cameras/markers/LOD)
// ported from ufbx v0.23.1 (ufbx.h). Element header fields (name/props/element_id/typed_id/
// instances) are carried by the base UfbxElement, so they are not repeated here.

namespace Ufbx
{
	// C: struct ufbx_light (ufbx.h:1424)
	// Light source attached to a node.
	public class UfbxLight : UfbxElement
	{
		// Color and intensity, usually use `color * intensity`.
		// NOTE: Intensity is 0.01x of the "Intensity" property.
		public UfbxVec3 Color;
		public double Intensity;

		// Direction aimed in node's local space, usually -Y.
		public UfbxVec3 LocalDirection;

		// C: ufbx_light_type type (ufbx.h:1443) — shadows ufbx_element.type by name.
		public new UfbxLightType Type;
		public UfbxLightDecay Decay;
		public UfbxLightAreaShape AreaShape;
		public double InnerAngle;
		public double OuterAngle;

		public bool CastLight;
		public bool CastShadows;
	}

	// C: struct ufbx_camera (ufbx.h:1567)
	public class UfbxCamera : UfbxElement
	{
		public UfbxProjectionMode ProjectionMode;

		// If true, Resolution is in pixels; otherwise only the aspect ratio matters.
		public bool ResolutionIsPixels;
		public UfbxVec2 Resolution;

		public UfbxVec2 FieldOfViewDeg;
		public UfbxVec2 FieldOfViewTan;

		public double OrthographicExtent;
		public UfbxVec2 OrthographicSize;

		// Size of the projection plane at distance 1.
		public UfbxVec2 ProjectionPlane;

		public double AspectRatio;
		public double NearPlane;
		public double FarPlane;

		// Coordinate system the projection uses (see ufbx_load_opts.target_camera_axes).
		public UfbxCoordinateAxes ProjectionAxes;

		// Advanced properties used to compute the above.
		public UfbxAspectMode AspectMode;
		public UfbxApertureMode ApertureMode;
		public UfbxGateFit GateFit;
		public UfbxApertureFormat ApertureFormat;
		public double FocalLengthMm;
		public UfbxVec2 FilmSizeInch;
		public UfbxVec2 ApertureSizeInch;
		public double SqueezeRatio;
	}

	// C: struct ufbx_bone (ufbx.h:1635)
	public class UfbxBone : UfbxElement
	{
		public double Radius;
		public double RelativeLength;
		public bool IsRoot;
	}

	// C: struct ufbx_empty (ufbx.h:1656)
	// Empty/NULL/locator; actual details are in the node.
	public class UfbxEmpty : UfbxElement
	{
	}

	// C: typedef struct ufbx_line_segment (ufbx.h:1672)
	// Segment of a line curve, indices refer to UfbxLineCurve.PointIndices.
	public struct UfbxLineSegment
	{
		public uint IndexBegin;
		public uint NumIndices;
	}

	// C: struct ufbx_line_curve (ufbx.h:1676)
	public class UfbxLineCurve : UfbxElement
	{
		public UfbxVec3 Color;

		// List of possible values the line passes through.
		public UfbxVec3[] ControlPoints;

		// Indices into ControlPoints that the line goes through.
		public uint[] PointIndices;

		public UfbxLineSegment[] Segments;

		// Tessellation (result).
		public bool FromTessellatedNurbs;
	}

	// C: typedef struct ufbx_nurbs_basis (ufbx.h:1743)
	// NURBS basis functions for one axis. Embedded by value, so a struct (C memset-zeroed).
	public struct UfbxNurbsBasis
	{
		// Number of control points influencing a point; degree + 1.
		public uint Order;

		public UfbxNurbsTopology Topology;

		// Subdivision of the parameter range to control points.
		public double[] KnotVector;

		public double TMin;
		public double TMax;

		// Parameter values of control points.
		public double[] Spans;

		public bool Is2D;

		// Number of control points that need to be copied to the end.
		public int NumWrapControlPoints;

		// True if the parametrization is well defined.
		public bool Valid;
	}

	// C: struct ufbx_nurbs_curve (ufbx.h:1745)
	public class UfbxNurbsCurve : UfbxElement
	{
		// Basis in the U axis.
		public UfbxNurbsBasis Basis;

		// Control points are NOT homogeneous: multiply by W before evaluating.
		public UfbxVec4[] ControlPoints;
	}

	// C: struct ufbx_nurbs_surface (ufbx.h:1763)
	public class UfbxNurbsSurface : UfbxElement
	{
		public UfbxNurbsBasis BasisU;
		public UfbxNurbsBasis BasisV;

		public int NumControlPointsU;
		public int NumControlPointsV;

		// 2D array, layout: `V * num_control_points_u + U`. NOT homogeneous.
		public UfbxVec4[] ControlPoints;

		// Segments tessellating each span in UfbxNurbsBasis.Spans.
		public uint SpanSubdivisionU;
		public uint SpanSubdivisionV;

		// If true, resulting normals should be flipped when evaluated.
		public bool FlipNormals;

		// May be null.
		public UfbxMaterial Material;
	}

	// C: struct ufbx_nurbs_trim_surface (ufbx.h:1798)
	public class UfbxNurbsTrimSurface : UfbxElement
	{
	}

	// C: struct ufbx_nurbs_trim_boundary (ufbx.h:1808)
	public class UfbxNurbsTrimBoundary : UfbxElement
	{
	}

	// C: struct ufbx_procedural_geometry (ufbx.h:1820)
	public class UfbxProceduralGeometry : UfbxElement
	{
	}

	// C: struct ufbx_stereo_camera (ufbx.h:1830)
	public class UfbxStereoCamera : UfbxElement
	{
		public UfbxCamera Left;
		public UfbxCamera Right;
	}

	// C: struct ufbx_camera_switcher (ufbx.h:1843)
	public class UfbxCameraSwitcher : UfbxElement
	{
	}

	// C: struct ufbx_marker (ufbx.h:1864)
	// Tracking marker for effectors.
	public class UfbxMarker : UfbxElement
	{
		public new UfbxMarkerType Type;
	}

	// C: typedef struct ufbx_lod_level (ufbx.h:1902)
	// Single LOD level; specifies properties of the Nth child of the containing node.
	public struct UfbxLodLevel
	{
		// Minimum distance to show this level. World units, or screen percentage
		// when UfbxLodGroup.RelativeDistances is set.
		public double Distance;

		public UfbxLodDisplay Display;
	}

	// C: struct ufbx_lod_group (ufbx.h:1908)
	public class UfbxLodGroup : UfbxElement
	{
		// If true, UfbxLodLevel.Distance is a screen size percentage.
		public bool RelativeDistances;

		// LOD levels matching in order to node children.
		public UfbxLodLevel[] LodLevels;

		// If true, don't account for parent transform when computing distance.
		public bool IgnoreParentTransform;

		// If UseDistanceLimit, hide the group if distance is outside [min, max].
		public bool UseDistanceLimit;
		public double DistanceLimitMin;
		public double DistanceLimitMax;
	}
}
