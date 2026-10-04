// The single seam through which the verification CLI obtains a loaded scene.
//
// Implemented (2026-10-04, S3b/S3c wave): mirrors `load_scene()` from test/hash_scene.c:97-146
// exactly, so the hashes can match the goldens:
//   ufbx_load_opts opts = {0};
//   opts.load_external_files = true;
//   opts.ignore_missing_external_files = true;
//   opts.evaluate_caches = true;
//   opts.evaluate_skinning = true;
//   opts.target_axes = ufbx_axes_right_handed_y_up;
//   opts.target_unit_meters = 1.0f;
// then, for frame > 0 (hash_scene.c:129-143):
//   ufbx_evaluate_opts: evaluate_caches/skinning/load_external_files = true;
//   time = scene.anim.time_begin + frame / scene.settings.frames_per_second;
//   scene = ufbx_evaluate_scene(scene, NULL, time, NULL, &error);  // free original
// The evaluate options are the NULL pointer, NOT the `eval_opts` filled in at hash_scene.c:130-133
// and never passed (the call at 136 takes NULL) -- so no skinning, no caches, no evaluate flags.
// Frame <= 0 means "no evaluation, use the loaded base scene".

using System;
using Ufbx;

namespace UfbxTests
{
    public static class SceneProvider
    {
        // Returns the scene to hash, or throws. `frame <= 0` -> no evaluation.
        public static UfbxScene Load(string path, int frame)
        {
            UfbxLoadOpts opts = new UfbxLoadOpts();
            opts.LoadExternalFiles = true;
            opts.IgnoreMissingExternalFiles = true;
            opts.EvaluateCaches = true;
            opts.EvaluateSkinning = true;
            opts.TargetAxes = UfbxCoordinateAxes.RightHandedYUp;
            opts.TargetUnitMeters = 1.0;

            UfbxError error = new UfbxError();
            UfbxScene scene = UfbxApi.LoadFile(path, opts, error);
            if (scene == null) {
                // hash_scene.c: fprintf(stderr, "Failed to load scene: %s\n", ...); exit(2).
                throw new InvalidOperationException(
                    "Failed to load scene: " + (error.Description ?? error.Type.ToString()));
            }

            if (frame > 0) {
                // C: hash_scene.c:135-142. `ufbx_free_scene(scene)` before `scene = state` is a
                // lifetime detail only (the evaluated scene retains the source in C); the port's
                // FreeScene() is the documented no-op.
                double time = scene.Anim.TimeBegin + frame / scene.Settings.FramesPerSecond;
                UfbxError evalError = new UfbxError();
                UfbxScene state = UfbxApi.EvaluateScene(scene, null, time, null, evalError);
                if (state == null) {
                    throw new InvalidOperationException(
                        "Failed to evaluate scene: " + (evalError.Description ?? evalError.Type.ToString()));
                }
                UfbxApi.FreeScene(scene);
                return state;
            }

            return scene;
        }
    }
}
