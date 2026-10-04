// Collection / constraint / misc element types ported from ufbx v0.23.1 (ufbx.h).
// Element header fields are carried by the base UfbxElement.

namespace Ufbx.NET
{
	// C: struct ufbx_display_layer (ufbx.h:3241)
	// Collection of nodes to hide/freeze.
	public class UfbxDisplayLayer : UfbxElement
	{
		// Nodes included in the layer (exclusively at most one layer per node).
		public UfbxNode[] Nodes;

		// Layer state.
		public bool Visible; // < Contained nodes are visible
		public bool Frozen;  // < Contained nodes cannot be edited

		public UfbxVec3 UiColor; // < Visual color for UI
	}

	// C: struct ufbx_selection_set (ufbx.h:3260)
	// Named set of nodes/geometry features to select.
	public class UfbxSelectionSet : UfbxElement
	{
		// Included nodes and geometry features.
		public UfbxSelectionNode[] Nodes;
	}

	// C: struct ufbx_selection_node (ufbx.h:3273)
	// Selection state of a node, may contain vertex/edge/face selection.
	public class UfbxSelectionNode : UfbxElement
	{
		// Selection targets, possibly null.
		public UfbxNode TargetNode;
		public UfbxMesh TargetMesh;
		public bool IncludeNode; // < Is TargetNode included in the selection

		// Indices to selected components.
		public uint[] Vertices;
		public uint[] Edges;
		public uint[] Faces;
	}

	// C: struct ufbx_character (ufbx.h:3296)
	public class UfbxCharacter : UfbxElement
	{
	}

	// C: typedef struct ufbx_constraint_target (ufbx.h:3327)
	// Target to follow with a constraint.
	public sealed class UfbxConstraintTarget
	{
		// Target node reference.
		public UfbxNode Node;

		// Relative weight to other targets (does not always sum to 1).
		public double Weight;

		// Offset from the actual target.
		public UfbxTransform Transform;

		// C: `targets[i] = constraint->targets.data[i]` (ufbx.c:26326); the copy is then written
		// through in ufbxi_evaluate_imp, so sharing it would translate the source scene too.
		internal UfbxConstraintTarget Clone() => (UfbxConstraintTarget)MemberwiseClone();
	}

	// C: struct ufbx_constraint (ufbx.h:3354)
	public class UfbxConstraint : UfbxElement
	{
		// Type of constraint to use. C: ufbx_constraint_type type shadows ufbx_element.type.
		public new UfbxConstraintType Type;
		public string TypeName;

		// Node to be constrained.
		public UfbxNode Node;

		// List of weighted targets (pole vectors for IK).
		public UfbxConstraintTarget[] Targets;

		// State of the constraint.
		public double Weight;
		public bool Active;

		// Translation/rotation/scale axes the constraint is applied to (fixed size 3).
		public bool[] ConstrainTranslation;
		public bool[] ConstrainRotation;
		public bool[] ConstrainScale;

		// Offset from the constrained position.
		public UfbxTransform TransformOffset;

		// AIM: Target and up vectors.
		public UfbxVec3 AimVector;
		public UfbxConstraintAimUpType AimUpType;
		public UfbxNode AimUpNode;
		public UfbxVec3 AimUpVector;

		// SINGLE_CHAIN_IK: Target for the IK; Targets contains pole vectors.
		public UfbxNode IkEffector;
		public UfbxNode IkEndNode;
		public UfbxVec3 IkPoleVector;
	}

	// C: struct ufbx_audio_layer (ufbx.h:3398)
	public class UfbxAudioLayer : UfbxElement
	{
		// Clips contained in this layer.
		public UfbxAudioClip[] Clips;
	}

	// C: struct ufbx_audio_clip (ufbx.h:3410)
	public class UfbxAudioClip : UfbxElement
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

	// C: typedef struct ufbx_bone_pose (ufbx.h:3457)
	public sealed class UfbxBonePose
	{
		// Node to apply the pose to.
		public UfbxNode BoneNode;

		// Matrix from node local space to world space.
		public UfbxMatrix BoneToWorld;

		// Matrix from node local space to parent space.
		// NOTE: FBX only stores world transforms so this is approximated.
		public UfbxMatrix BoneToParent;

		// C: `bones[i] = pose->bone_poses.data[i]` (ufbx.c:26367).
		internal UfbxBonePose Clone() => (UfbxBonePose)MemberwiseClone();
	}

	// C: struct ufbx_pose (ufbx.h:3461)
	public class UfbxPose : UfbxElement
	{
		// Set if this pose is marked as a bind pose.
		public bool IsBindPose;

		// List of bone poses, sorted by node typed_id.
		public UfbxBonePose[] BonePoses;
	}

	// C: struct ufbx_metadata_object (ufbx.h:3477)
	// Header-only element in this ufbx version.
	public class UfbxMetadataObject : UfbxElement
	{
	}
}
