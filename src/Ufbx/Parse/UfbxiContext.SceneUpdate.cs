// Scene-update state appended to `ufbxi_context` by the S3c module
// (Parse/SceneUpdate.cs), owned by the S3b/S3c porting agent (2026-10-03 wave).
//
// NOTE(2026-10-03): this module needs no extra context fields. Everything
// `ufbxi_finalize_scene()` (ufbx.c:21644) and the `ufbxi_update_*` chain
// (ufbx.c:22631-23935) read from `uc` is already owned by other partials:
//   - `MirrorAxis` / `AxisMatrix`        -> UfbxiContext.Cache.cs (S4a)
//   - `UnitScale`                        -> UfbxiContext.cs (load spine)
//   - `ZeroIndices` / `ConsecutiveIndices` / `TmpElementFlag` -> UfbxiContext.SceneBuild.cs
//   - `MaxZeroIndices` / `MaxConsecutiveIndices` / `TmpFullWeights` -> UfbxiContext.Geometry.cs
//   - `TextureFileMap` / `FileContent`   -> UfbxiContext.SceneFinalize.cs
namespace Ufbx
{
    internal sealed partial class UfbxiContext
    {
    }
}
