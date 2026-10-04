using System;
using System.Collections.Generic;

namespace Ufbx
{
    // Ported from ufbx.c v0.23.1 "Warnings" (ufbx.c:4829-4901) plus the pieces of
    // ufbxi_buf those need.
    //
    // Warning deduplication is order-sensitive state: C keeps the last warning per
    // (type, has_element_id) pair and *increments `count`* on a repeat instead of emitting a
    // new entry, so the number and the `count` fields of the emitted warnings depend on this
    // bookkeeping exactly. `prev_warnings` is the flattened C array
    // `ufbx_warning *prev_warnings[UFBX_WARNING_TYPE_COUNT][2]`.

    // C: ufbxi_buf (ufbx.c:3941-3948) restricted to the append/move operations used by
    // warnings and the string pool. PORTING_NOTES.md #4: the chunked arena becomes a List,
    // and a pushed run of `char` becomes one managed string, which is what gives C's
    // "pointer into the buffer" its C# counterpart (a canonical instance).
    internal sealed class UfbxiPushBuf
    {
        // C: b->chunks / b->pushed_size / b->pos collapsed into the byte stream itself.
        public readonly List<byte> Bytes = new List<byte>();

        // C: b->num_items
        public int NumItems { get { return Bytes.Count; } }

        // C: ufbxi_push(b, char, n) + memcpy(dst, src, length) + dst[length] = '\0'
        // (ufbx.c:5057-5061) and ufbxi_push_copy(b, char, n, data) (ufbx.c:4867).
        // `n` is `length + 1`; the trailing NUL is part of the buffer, the returned string
        // covers the `length` data bytes (C: the caller keeps `length` separately).
        public string PushCopy(byte[] src, int offset, int length)
        {
            for (int i = 0; i < length; i++) Bytes.Add(src[offset + i]);
            Bytes.Add(0);
            return UfbxiRawStr.FromBytes(src, offset, length);
        }

        // C: ufbxi_push(b, char, n) + memcpy + '\0' over a raw-byte string source. The copy
        // always materializes a fresh instance: C's arena copy is a new address, distinct from
        // the source pointer, even when the bytes are the same.
        public string PushCopy(string src, int offset, int length)
        {
            char[] chars = new char[length];
            for (int i = 0; i < length; i++) {
                chars[i] = src[offset + i];
                Bytes.Add((byte)src[offset + i]);
            }
            Bytes.Add(0);
            return new string(chars);
        }
    }

    // C: ufbxi_warnings (ufbx.c:4830-4838).
    internal sealed class UfbxiWarnings
    {
        // C: UFBX_WARNING_TYPE_COUNT
        public const int WarningTypeCount = UfbxEnumCounts.UfbxWarningType;

        // C: char desc[256] in ufbxi_vwarnf_imp (ufbx.c:4856)
        public const int WarningDescLength = 256;

        // C: ufbx_error *error
        public UfbxError Error;

        // C: ufbxi_buf *result (ufbx.c:25597: `uc->warnings.result = &uc->result`)
        public UfbxiPushBuf Result;

        // C: ufbxi_buf tmp_stack — the pending `ufbx_warning` items. Reference-typed items,
        // so `prev_warnings` can point at an item the way C's pointers do (ufbx.c:4878).
        public readonly List<UfbxWarning> TmpStack = new List<UfbxWarning>();

        // C: uint32_t deferred_element_id_plus_one
        public uint DeferredElementIdPlusOne;

        // C: ufbx_warning *prev_warnings[UFBX_WARNING_TYPE_COUNT][2]
        // Flattened as [type*2 + has_element_id].
        public readonly UfbxWarning[] PrevWarnings = new UfbxWarning[WarningTypeCount * 2];

        // C: #define ufbxi_warnf_tag(type, element_id, ...) (ufbx.c:6660) — the element ID
        // "no warning is tagged to an element" is `(uint32_t)~0u`.
        public const uint NoElementId = unchecked((uint)-1);

        // C: ufbxi_vwarnf_imp (ufbx.c:4840-4879). Returns C's `int` success flag.
        public bool Vwarnf(UfbxWarningType type, uint elementId, string fmt, UfbxiVaList args)
        {
            // C: `if (!ws) return 1;` — a NULL warnings context (parse without a warning
            // sink) swallows the warning. Callers pass `null` for that case; this method is
            // only reached with a live context, so the check lives in Warnf below.
            // C: "HACK(warning-element): Encode potential deferred element ID into
            // ufbx_warning.element_id ... Tag deferred indices with the high bit."
            if (elementId == NoElementId && DeferredElementIdPlusOne > 0) {
                elementId = unchecked((DeferredElementIdPlusOne - 1u) | 0x80000000u);
            }

            bool hasElementId = elementId != NoElementId;

            // C: `if (type >= UFBX_WARNING_TYPE_FIRST_DEDUPLICATED)` — from
            // UFBX_WARNING_INDEX_CLAMPED onwards, a repeat of the same (type, element) merges
            // into the previous entry by bumping `count`.
            if (type >= UfbxWarningType.FirstDeduplicated) {
                UfbxWarning prev = PrevWarnings[(int)type * 2 + (hasElementId ? 1 : 0)];
                if (prev != null && prev.ElementId == elementId) {
                    prev.Count++;
                    return true;
                }
            }

            // C: `char desc[256]; size_t desc_len = (size_t)ufbxi_vsnprintf(desc, sizeof(desc), fmt, args);`
            byte[] desc = new byte[WarningDescLength];
            int descLen = UfbxiPrint.Vsnprintf(desc, desc.Length, fmt, args);

            // C: ufbxi_clean_string_utf8(desc, desc_len) (ufbx.c:4862) — in-place, '?' per
            // malformed byte sequence; the buffer beyond `desc_len` is untouched.
            UfbxiUtf8.CleanStringUtf8(desc, 0, descLen);

            // C: `char *desc_copy = ufbxi_push_copy(ws->result, char, desc_len + 1, desc);`
            string description = Result.PushCopy(desc, 0, descLen);

            // C: `ufbx_warning *warning = ufbxi_push(&ws->tmp_stack, ufbx_warning, 1);`
            // All five fields are assigned below, so C's uninitialized memory is not
            // observable and a fresh object is equivalent.
            UfbxWarning warning = new UfbxWarning();
            TmpStack.Add(warning);

            warning.Type = type;
            warning.Description = description;
            warning.ElementId = elementId;
            warning.Count = 1;
            PrevWarnings[(int)type * 2 + (hasElementId ? 1 : 0)] = warning;

            return true;
        }

        // C: ufbxi_warnf_imp (ufbx.c:4881-4889) — the variadic wrapper. `ws` may be NULL
        // ("NOTE: `ws` may be `NULL` here, handled by `ufbxi_vwarnf()`"), which drops the
        // warning. `args` is the already-evaluated argument list.
        internal static bool Warnf(UfbxiWarnings ws, UfbxWarningType type, uint elementId, string fmt, UfbxiVaList args)
        {
            if (ws == null) return true;
            return ws.Vwarnf(type, elementId, fmt, args);
        }

        // C: ufbxi_warnf(type, ...) / ufbxi_warnf_tag(type, element_id, ...) (ufbx.c:6659-6660)
        // with a single description and no arguments.
        internal static bool Warnf(UfbxiWarnings ws, UfbxWarningType type, uint elementId, string fmt)
        {
            if (ws == null) return true;
            return ws.Vwarnf(type, elementId, fmt, null);
        }

        // C: ufbxi_pop_warnings (ufbx.c:4891-4901). `p_has_warning` is C's
        // `bool p_has_warning[UFBX_WARNING_TYPE_COUNT]`.
        // C hands the warning items over with `ufbxi_push_pop(result, &tmp_stack,
        // ufbx_warning, count)`: the items are appended to the result arena and `tmp_stack`
        // is truncated. The items are managed objects here, so the "move" transfers the
        // references; the arena offsets C assigns them are not observable in the output.
        internal static UfbxWarning[] PopWarnings(UfbxiWarnings ws, bool[] pHasWarning)
        {
            // C: warnings->count = ws->tmp_stack.num_items;
            int count = ws.TmpStack.Count;
            UfbxWarning[] warnings = ws.TmpStack.ToArray();
            ws.TmpStack.Clear();

            // C: `ufbxi_for_list(ufbx_warning, warning, *warnings) p_has_warning[warning->type] = true;`
            for (int i = 0; i < count; i++) {
                pHasWarning[(int)warnings[i].Type] = true;
            }

            return warnings;
        }
    }
}
