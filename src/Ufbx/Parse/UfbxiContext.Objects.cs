// S2-objects additions to the parse context (append-only partial, see
// UfbxiContext.cs NOTE 2026-10-03). Owned by the S2-objects porting agent.
//
// NOTE(2026-10-03): this module needs no extra context fields. `MaxZeroIndices` /
// `MaxConsecutiveIndices` (C ufbx.c:6515-6516) — written by ufbxi_read_legacy_mesh
// (Parse/Legacy.cs:16241) as well as the geometry readers — are declared in
// Parse/UfbxiContext.Geometry.cs (S2-geometry agent); use those, do not redeclare.
namespace Ufbx
{
    internal sealed partial class UfbxiContext
    {
    }
}
