// Error propagation for the ported internals.
//
// PORTING_NOTES.md #3: C's `ufbxi_check(cond)` / `ufbxi_fail(desc)` return-value
// plumbing becomes an exception, and the public API layer catches it at the top level
// to fill `UfbxError` and return null, matching C semantics.
//
// The description strings reproduce the C condition/description text (ufbx.c:3415
// `ufbxi_fail_imp_err`), so error reports stay comparable with the C build.
//
// ERROR 口径 (2026-10-03, load-spine session): a failure is either a *message* failure
// (`ufbxi_check_msg` / `ufbxi_fail_msg`, macro `ufbxi_error_msg(cond,msg)` = `"$" msg "\0"
// cond`, ufbx.c:3413/6655-6657) which writes `error.description`, or a *plain* failure
// (`ufbxi_check` / `ufbxi_fail`, which go through `ufbxi_fail_no_msg` ->
// `ufbxi_fail_imp_err(err, NULL, NULL, 0)`, ufbx.c:3415-3448/6644-6653) which writes
// nothing. Plain failures therefore surface as the default string of
// `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)` (ufbx.c:25623) with type
// UFBX_ERROR_UNKNOWN. The distinction is carried by `UfbxParseError.HasDescription` and
// enforced at the single convergence point, the top-level catch of `UfbxApi` (src/Ufbx.NET/Api).
// New code must use `UfbxiFail.CheckMsg/FailMsg` for msg sites and
// `UfbxiFail.CheckNoDesc/FailNoDesc` for plain sites; existing sites keep the previous
// always-description behaviour until their module is audited.

using System;
using System.Diagnostics.CodeAnalysis;

namespace Ufbx.NET
{
    // C: the `return 0` path of ufbxi_fail_imp()/ufbxi_fail_imp_err() (ufbx.c:3415-3450, 6638-6657).
    internal sealed class UfbxParseError : Exception
    {
        // C: ufbx_error.type — most internal failures leave UFBX_ERROR_UNKNOWN and the
        // type is refined on the way out by ufbxi_fix_error_type().
        public UfbxErrorType ErrorType;

        // C: whether this failure carries an `error.description` at all.
        //
        // With UFBXI_FEATURE_ERROR_STACK == 0 (ufbx.c:170-172, the configuration the golden
        // binaries are built with) there are two distinct kinds of failure:
        //  - `ufbxi_check(cond)` / `ufbxi_fail(desc)` go through `ufbxi_fail_no_msg`, i.e.
        //    `ufbxi_fail_imp_err(err, NULL, NULL, 0)` (ufbx.c:3415-3448, 6644-6653). The
        //    condition string is *discarded*, so `error.description` stays NULL and
        //    `ufbxi_fix_error_type(&uc->error, "Failed to load", p_error)` (ufbx.c:25623)
        //    reports the default string with type UFBX_ERROR_UNKNOWN.
        //  - `ufbxi_check_msg(cond, msg)` / `ufbxi_fail_msg(desc, msg)` pass
        //    `ufbxi_error_msg(cond, msg)` = `"$" msg "\0" cond` (ufbx.c:3413, 6655-6657) and
        //    `ufbxi_fail_imp_err()` writes `description = msg` (only if still unset).
        // `true` reproduces the msg form, `false` the plain form. See UfbxiFail below.
        //
        // NOTE: existing call sites were written before this distinction existed and all
        // default to `true`; they are audited module by module. The public API layer
        // (`UfbxApi`) is the single place that honours the flag.
        public bool HasDescription = true;

        public UfbxParseError(string description)
            : base(description)
        {
            ErrorType = UfbxErrorType.Unknown;
        }

        public UfbxParseError(string description, UfbxErrorType type)
            : base(description)
        {
            ErrorType = type;
        }

        // The explicit two-form constructor: `hasDescription == false` is C's plain
        // `ufbxi_check`/`ufbxi_fail`, where `message` is only the stringified condition.
        internal UfbxParseError(string message, bool hasDescription)
            : base(message)
        {
            ErrorType = UfbxErrorType.Unknown;
            HasDescription = hasDescription;
        }
    }

    internal static class UfbxiFail
    {
        // C: #define ufbxi_check(cond) — throws instead of `return 0`.
        public static void Check(bool condition, string description)
        {
            if (!condition) throw new UfbxParseError(description);
        }

        // C: #define ufbxi_check_msg(cond, msg) with an explicit error type.
        public static void Check(bool condition, string description, UfbxErrorType type)
        {
            if (!condition) throw new UfbxParseError(description, type);
        }

        // C: #define ufbxi_fail(desc).
        [DoesNotReturn]
        public static void Fail(string description)
        {
            throw new UfbxParseError(description);
        }

        [DoesNotReturn]
        public static void Fail(string description, UfbxErrorType type)
        {
            throw new UfbxParseError(description, type);
        }

        // ------------------------------------------------------------------
        // The two C failure forms, spelled out (see UfbxParseError.HasDescription).
        // ------------------------------------------------------------------

        // C: #define ufbxi_check_msg(cond, msg) (ufbx.c:6655): the failure carries `msg` as
        // `error.description`; the stringified condition is dropped (no error stack).
        public static void CheckMsg(bool condition, string msg)
        {
            if (!condition) throw new UfbxParseError(msg, true);
        }

        // C: #define ufbxi_fail_msg(desc, msg) (ufbx.c:6657). `desc` is the condition text and
        // is not reported; `msg` becomes `error.description`.
        [DoesNotReturn]
        public static void FailMsg(string desc, string msg)
        {
            throw new UfbxParseError(msg, true);
        }

        // C: #define ufbxi_check(cond) (ufbx.c:6650) -> ufbxi_fail_no_msg() ->
        // ufbxi_fail_imp_err(err, NULL, NULL, 0): NO description is written, so
        // ufbxi_fix_error_type() reports the caller's default ("Failed to load").
        // `condText` is C's `#cond`, kept for diagnostics only.
        public static void CheckNoDesc(bool condition, string condText)
        {
            if (!condition) throw new UfbxParseError(condText, false);
        }

        // C: #define ufbxi_fail(desc) (ufbx.c:6652) -> ufbxi_fail_no_msg(), i.e. desc is the
        // stringified condition and is not reported as a description.
        [DoesNotReturn]
        public static void FailNoDesc(string condText)
        {
            throw new UfbxParseError(condText, false);
        }
    }
}
