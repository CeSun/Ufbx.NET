using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ufbx
{
    // Ported from ufbx.c v0.23.1 "String pool" (ufbx.c:4903-5270) and the string helpers
    // used with it, plus ufbxi_load_strings (ufbx.c:11407-11424).
    //
    // C interns every string that comes out of a file so that (a) equal strings compare in
    // O(1) by pointer and (b) memory is shared. The port keeps that behaviour by handing out
    // the single canonical `string` instance per content, so `ReferenceEquals(a, b)` is
    // C's `a.data == b.data` and `string` ordinal comparison is C's `memcmp`/`ufbxi_str_cmp`.
    //
    // A `ufbx_string` is a (raw-byte `string`) here: one char per byte, so `Length` is C's
    // byte `length` (see UfbxiRawStr in Utf8.cs). Where C slices a string by moving `data`
    // and shrinking `length` (ufbx.c:4990-5019) the port uses `Substring`, which produces a
    // fresh instance; C's interior pointer is not one of the canonical ones either, so both
    // sides only compare such slices by content.
    //
    // Error plumbing note: with UFBXI_FEATURE_ERROR_STACK == 0 (ufbx.c:3537-3554) the
    // `ufbxi_check_err`/`ufbxi_check_return_err` macros pass a NULL condition string, so C
    // sets no description at all. The port throws with the stringified C condition for
    // diagnostics; it is not observable in scene hashes. Only the `*_err_msg` macros carry a
    // real description ("Invalid UTF-8", ufbx.c:5106) and those do affect the output.

    // C: ufbxi_sanitized_string (ufbx.c:4934-4940) is defined in Parse/UfbxiNode.cs, which the
    // DOM value types already depend on; UfbxiStringPool.PushSanitizedString fills it in.

    // C: a `ufbx_string` used as a ufbxi_get_concat_key()/ufbxi_concat_str_cmp() part, where
    // `length == SIZE_MAX` means "measure with strlen" (ufbx.c:4958, 4972). The port spells
    // that sentinel -1.
    internal struct UfbxiConcatPart
    {
        public string Data;  // C: data
        public int Length;   // C: length, -1 == SIZE_MAX

        public UfbxiConcatPart(string data)
        {
            Data = data;
            Length = data == null ? 0 : data.Length;
        }

        // C: explicit length, `length == -1` selects the strlen path.
        public UfbxiConcatPart(string data, int length)
        {
            Data = data;
            Length = length;
        }

        // C: `size_t length = part->length != SIZE_MAX ? part->length : strlen(part->data);`
        public int ResolvedLength
        {
            get
            {
                if (Length != -1) return Length;
                if (Data == null) return 0;
                int nul = Data.IndexOf('\0');
                return nul < 0 ? Data.Length : nul;
            }
        }
    }

    // C: the `ufbxi_str_*` / prefix-suffix helpers (ufbx.c:4926-5028).
    internal static class UfbxiStr
    {
        // C: ufbxi_str_equal (ufbx.c:4926-4929): a.length == b.length && !memcmp(...).
        internal static bool Equal(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.Length == b.Length && string.CompareOrdinal(a, 0, b, 0, a.Length) == 0;
        }

        // C: ufbxi_str_less (ufbx.c:4931-4936) — memcmp over the common length first, then
        // the shorter string sorts first.
        internal static bool Less(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            int cmp = string.CompareOrdinal(a, 0, b, 0, len);
            if (cmp != 0) return cmp < 0;
            return a.Length < b.Length;
        }

        // C: ufbxi_str_cmp (ufbx.c:4938-4945).
        internal static int Cmp(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            int cmp = string.CompareOrdinal(a, 0, b, 0, len);
            if (cmp != 0) return cmp < 0 ? -1 : 1;
            if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
            return 0;
        }

        // C: ufbxi_str_c (ufbx.c:4947-4951): { str, strlen(str) } — the port trims at the
        // terminator so Length is C's length.
        internal static string StrC(string str)
        {
            if (str == null) return string.Empty;
            int nul = str.IndexOf('\0');
            return nul < 0 ? str : str.Substring(0, nul);
        }

        // C: ufbxi_str_c over raw bytes.
        internal static string StrC(byte[] data, int offset)
        {
            int end = offset;
            while (end < data.Length && data[end] != 0) end++;
            return UfbxiRawStr.FromBytes(data, offset, end - offset);
        }

        // C: ufbxi_get_concat_key (ufbx.c:4953-4966). `shift` is `uint32_t`, so the byte
        // order is big-endian and the key is complete (and returned early) after 4 bytes.
        internal static uint GetConcatKey(UfbxiConcatPart[] parts, int numParts)
        {
            uint key = 0, shift = 32;
            for (int p = 0; p < numParts; p++) {
                string data = parts[p].Data;
                int length = parts[p].ResolvedLength;
                for (int i = 0; i < length; i++) {
                    shift -= 8;
                    key |= (uint)(byte)data[i] << (int)shift;
                    if (shift == 0) return key;
                }
            }
            return key;
        }

        // C: ufbxi_concat_str_cmp (ufbx.c:4968-4982): compare the concatenation of `parts`
        // against `reference`, with a part that runs past the end of `reference` losing (-1)
        // and leftover reference bytes winning (+1).
        internal static int ConcatStrCmp(string reference, UfbxiConcatPart[] parts, int numParts)
        {
            int ptr = 0;
            int end = reference.Length;  // C: end = ptr + ref->length
            for (int p = 0; p < numParts; p++) {
                string data = parts[p].Data;
                int length = parts[p].ResolvedLength;
                int toCmp = Math.Min(end - ptr, length);
                int cmp = toCmp > 0 ? string.CompareOrdinal(reference, ptr, data, 0, toCmp) : 0;
                if (cmp != 0) return cmp < 0 ? -1 : 1;
                if (toCmp != length) return -1;
                ptr += length;
            }
            return ptr == end ? 0 : 1;
        }

        // C: ufbxi_starts_with (ufbx.c:4984-4987).
        internal static bool StartsWith(string str, string prefix)
        {
            return str.Length >= prefix.Length && string.CompareOrdinal(str, 0, prefix, 0, prefix.Length) == 0;
        }

        // C: ufbxi_ends_with (ufbx.c:4989-4992).
        internal static bool EndsWith(string str, string suffix)
        {
            return str.Length >= suffix.Length
                && string.CompareOrdinal(str, str.Length - suffix.Length, suffix, 0, suffix.Length) == 0;
        }

        // C: ufbxi_remove_prefix_len (ufbx.c:4994-5002) — C moves `data` and shrinks
        // `length`; the port slices.
        internal static bool RemovePrefixLen(ref string str, string prefix, int prefixLen)
        {
            if (str.Length >= prefixLen && string.CompareOrdinal(str, 0, prefix, 0, prefixLen) == 0) {
                str = str.Substring(prefixLen);
                return true;
            }
            return false;
        }

        // C: ufbxi_remove_suffix_len (ufbx.c:5004-5011) — C only shrinks `length`.
        internal static bool RemoveSuffixLen(ref string str, string suffix, int suffixLen)
        {
            if (str.Length >= suffixLen
                && string.CompareOrdinal(str, str.Length - suffixLen, suffix, 0, suffixLen) == 0) {
                str = str.Substring(0, str.Length - suffixLen);
                return true;
            }
            return false;
        }

        // C: ufbxi_remove_prefix_str (ufbx.c:5013-5016).
        internal static bool RemovePrefixStr(ref string str, string prefix)
        {
            return RemovePrefixLen(ref str, prefix, prefix.Length);
        }

        // C: ufbxi_remove_suffix_c (ufbx.c:5018-5021) — the suffix length comes from strlen.
        internal static bool RemoveSuffixC(ref string str, string suffix)
        {
            return RemoveSuffixLen(ref str, suffix, StrC(suffix).Length);
        }

        // C: ufbxi_safe_string (ufbx.c:5029-5033): an empty string becomes ufbxi_empty_char.
        internal static string SafeString(string data)
        {
            return data != null && data.Length > 0 ? data : String.Empty;
        }
    }

    // Mapping of C pointer values to stable integers.
    //
    // C uses `const char*` / `uintptr_t` values as hash-map keys (ufbxi_map_cmp_const_char_ptr,
    // ufbxi_map_cmp_uintptr, ufbxi_hash_ptr) and compares interned strings by address. The port
    // assigns each canonical string a monotonically increasing id, which stands in for the
    // address:
    //   0                        C's NULL
    //   1                        C: ufbxi_empty_char (ufbx.c:3370), earlier in the translation
    //                            unit than the string constants, so it gets the first id
    //   2 .. 1+len(All)          the `ufbxi_*` constants in their declaration order
    //                            (ufbx.c:5280-5582, the order of UfbxiStrings.All) -- these are
    //                            .rodata addresses
    //   1+len(All) ..            pooled strings, in the order the string pool interns them
    //                            (C: the address order inside `pool->buf`, which is push order;
    //                            a `copy: false` entry keeps the source pointer and already has
    //                            an id here)
    // Anything else -- e.g. ufbxi_str_c() over a literal, or a slice -- is a pointer C itself
    // does not order predictably; those get a fresh id at first use.
    internal static class UfbxiPtrIdTable
    {
        // C: NULL
        public const ulong Null = 0;

        // C: ids that belong to the image (the `ufbxi_empty_char` byte and the string
        // constants), not to arena addresses. `ufbxi_load_strings()` is what makes these
        // appear in a pool; their order *against* arena pointers is unspecified in C
        // (see PORTING_NOTES.md "C 指针序 ≡ 分配序"), so oracles rank arena pointers only
        // and treat everything in this range as one "static" group.
        public static readonly ulong StaticIdCount = 1ul + (ulong)UfbxiStrings.All.Length;

        // C addresses are per-allocation, not per-content: interning the same bytes in two
        // pools gives two distinct addresses, so a `ufbxi_push()` copy must get a fresh id.
        // Keyed by reference to keep that; content would collapse cross-pool duplicates.
        static readonly Dictionary<string, ulong> _ids = new Dictionary<string, ulong>(ReferenceKeyComparer.Instance);
        static ulong _next = 1;

        static UfbxiPtrIdTable()
        {
            // C: ufbxi_empty_char
            Assign(string.Empty);
            // C: the ufbxi_* string constants, in declaration order
            for (int i = 0; i < UfbxiStrings.All.Length; i++) {
                Assign(UfbxiStrings.All[i]);
            }
        }

        // Called when a string becomes a canonical (address-carrying) instance.
        internal static ulong Assign(string data)
        {
            if (data == null) return Null;
            ulong id;
            if (_ids.TryGetValue(data, out id)) return id;
            id = _next++;
            _ids.Add(data, id);
            return id;
        }

        // C: (uintptr_t)ptr
        internal static ulong IdOf(string data)
        {
            if (data == null) return Null;
            return Assign(data);
        }

        // C: pointer inside the translation unit's static data rather than the arena.
        internal static bool IsStatic(string data)
        {
            return IdOf(data) <= StaticIdCount;
        }

        // C: ufbxi_hash_ptr(ptr) / ufbxi_hash_uptr((uintptr_t)ptr)
        internal static uint HashOf(string data)
        {
            return UfbxiHash.HashPtr(IdOf(data));
        }

        // Reference identity + reference hash code, i.e. C's pointer semantics.
        sealed class ReferenceKeyComparer : IEqualityComparer<string>
        {
            public static readonly ReferenceKeyComparer Instance = new ReferenceKeyComparer();

            public bool Equals(string a, string b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(string s)
            {
                return RuntimeHelpers.GetHashCode(s);
            }
        }
    }

    // C: ufbxi_string_pool (ufbx.c:4909-4918).
    internal sealed class UfbxiStringPool
    {
        // C: sizeof(ufbx_string) on x64 -- { const char *data; size_t length; }. Only feeds
        // C's byte-size arithmetic in ufbxi_map_grow_size_imp (ufbx.c:4526-4527), which the
        // managed arrays replace; kept so the map reads like the original.
        public const int UfbxStringSize = 16;

        // C: ufbx_error *error
        public UfbxError Error;

        // C: ufbxi_buf buf -- the string data arena (`ufbxi_push(&pool->buf, char, n)`,
        // ufbx.c:5057). One entry per interned copy, in push order.
        public UfbxiPushBuf Buf;

        // C: ufbxi_map map, items `ufbx_string`, cmp ufbxi_map_cmp_string (ufbx.c:25550).
        public UfbxiMap<string, string> Map;

        // C: size_t initial_size -- 1024 for a scene load (ufbx.c:25553), 64 for the cache
        // (ufbx.c:24749). It is the `min_size` of every ufbxi_map_grow(), so it decides the
        // table geometry.
        public uint InitialSize;

        // C: char *temp_str / size_t temp_cap -- the scratch buffer for sanitized strings.
        public byte[] TempStr;
        public int TempCap;

        // C: ufbx_unicode_error_handling error_handling
        public UfbxUnicodeErrorHandling ErrorHandling;

        // C: ufbxi_warnings *warnings
        public UfbxiWarnings Warnings;

        internal UfbxiStringPool(UfbxError error, UfbxiWarnings warnings, UfbxiPushBuf buf, uint initialSize, UfbxUnicodeErrorHandling errorHandling)
        {
            Error = error;
            Warnings = warnings;
            Buf = buf;
            InitialSize = initialSize;
            ErrorHandling = errorHandling;
            // C: ufbxi_map_init(&pool->map, ator, &ufbxi_map_cmp_string, NULL) (ufbx.c:25550)
            Map = new UfbxiMap<string, string>(UfbxiMapCmps.String, UfbxStringSize, error);
        }

        // C: ufbxi_string_pool_temp_free (ufbx.c:5035-5040).
        internal void TempFree()
        {
            TempStr = null;
            TempCap = 0;
            Map.Free();
        }

        // C: ufbxi_grow_array / ufbxi_grow_array_size (ufbx.c:3771-3791) for `char`:
        // new_cap = max(old_cap * 2, n), contents preserved.
        bool GrowTemp(int n)
        {
            if (n <= TempCap) return true;
            int newCap = Math.Max(unchecked(TempCap * 2), n);
            byte[] dst = new byte[newCap];
            if (TempStr != null) {
                Array.Copy(TempStr, 0, dst, 0, TempCap < TempStr.Length ? TempCap : TempStr.Length);
            }
            TempStr = dst;
            TempCap = newCap;
            return true;
        }

        // C: ufbxi_push_string_entry (ufbx.c:5041-5069) over a byte source -- the sanitize
        // scratch buffer (ufbx.c:5175): C's `const char **p_data` points into `pool->temp_str`,
        // so the content comes from the buffer and the interned result is a string.
        internal bool PushStringEntryBytes(ref string pData, byte[] src, int length, uint hash, bool copy)
        {
            pData = UfbxiRawStr.FromBytes(src, 0, length);
            return PushStringEntry(ref pData, 0, length, hash, copy);
        }

        // C: ufbxi_push_string_entry (ufbx.c:5041-5069). The pool map is keyed by content and
        // holds one canonical instance per content, which is what makes C's pointer equality
        // reproducible; interning a string that is already in the pool returns the existing
        // instance and therefore does not allocate or push an id.
        // `pData` is both the source bytes (`*p_data`, offset by `offset`) and the out
        // parameter, exactly like C's `const char **p_data`.
        internal bool PushStringEntry(ref string pData, int offset, int length, uint hash, bool copy)
        {
            if (length == 0) {
                // C: *p_data = ufbxi_empty_char
                pData = string.Empty;
                return true;
            }

            // C: ufbx_string ref = { *p_data, length };
            string src = pData;
            string refStr = offset == 0 && length == src.Length ? src : src.Substring(offset, length);

            // C: ufbxi_check_err(pool->error, ufbxi_map_grow(&pool->map, ufbx_string, pool->initial_size))
            UfbxiFail.CheckNoDesc(Map.Grow(InitialSize), "ufbxi_map_grow(&pool->map, ufbx_string, pool->initial_size)");

            // C: ufbxi_map_find(&pool->map, ufbx_string, hash, &ref)
            int found = Map.Find(hash, refStr);
            if (found < 0) {
                int index = Map.Insert(hash, refStr);
                UfbxiFail.CheckNoDesc(index >= 0, "ufbxi_map_insert(&pool->map, ufbx_string, hash, &ref)");

                string data;
                if (copy) {
                    // C: char *dst = ufbxi_push(&pool->buf, char, length + 1);
                    //      memcpy(dst, ref.data, length); dst[length] = '\0'; entry->data = dst;
                    data = Buf.PushCopy(refStr, 0, length);
                } else {
                    // C: entry->data = ref.data -- keep the source pointer canonical. This is
                    // how the ufbxi_* constants get their addresses into the pool
                    // (ufbxi_load_strings, ufbx.c:11413-11422).
                    data = refStr;
                }

                // C: entry->length = length; entry->data = data;
                Map.Items[index] = data;
                UfbxiPtrIdTable.Assign(data);
                pData = data;
            } else {
                pData = Map.Items[found];
            }

            return true;
        }

        // C: ufbxi_add_replacement_char (ufbx.c:5070-5100) -- the pool-scoped wrapper that
        // uses pool->error_handling and writes into the scratch buffer.
        internal int AddReplacementChar(byte[] dst, int offset, byte c)
        {
            return UfbxiUtf8.AddReplacementChar(ErrorHandling, dst, offset, c);
        }

        // C: ufbxi_push_sanitized_string_entry (ufbx.c:5102-5180). Writes the sanitized bytes
        // into `temp_str`, interns them (copy) and returns them.
        internal bool PushSanitizedStringEntry(out string pStr, out int pLength, string str, int offset, int length, int validLength)
        {
            // C: ufbx_assert(valid_length < length) -- ufbx_assert is compiled out in the
            // golden build (ufbx.c:1030-1038), so it is not reproduced.

            // C: ufbxi_check_err_msg(pool->error, pool->error_handling !=
            //      UFBX_UNICODE_ERROR_HANDLING_ABORT_LOADING, "Invalid UTF-8")
            UfbxiFail.CheckMsg(ErrorHandling != UfbxUnicodeErrorHandling.AbortLoading, "Invalid UTF-8");

            // C: ufbxi_check_err(pool->error, ufbxi_warnf_imp(pool->warnings,
            //      UFBX_WARNING_BAD_UNICODE, ~0u, "Bad UTF-8 string"))
            UfbxiFail.CheckNoDesc(UfbxiWarnings.Warnf(Warnings, UfbxWarningType.BadUnicode, UfbxiWarnings.NoElementId, "Bad UTF-8 string"), "ufbxi_warnf_imp(...)");

            int index = validLength;
            int dstLen = index;

            // C: ufbxi_check_err(pool->error, length <= SIZE_MAX - 64)
            UfbxiFail.CheckNoDesc(length <= int.MaxValue - 64, "length <= SIZE_MAX - 64");
            // C: ufbxi_check_err(pool->error, ufbxi_grow_array(ator, &pool->temp_str, &pool->temp_cap, length + 64))
            UfbxiFail.CheckNoDesc(GrowTemp(unchecked(length + 64)), "ufbxi_grow_array(..., &pool->temp_str, &pool->temp_cap, length + 64)");

            // C: memcpy(pool->temp_str, str, index) -- the valid prefix is copied verbatim
            for (int i = 0; i < index; i++) TempStr[i] = (byte)str[offset + i];

            byte[] dst = TempStr;
            while (index < length) {
                byte c = (byte)str[offset + index];
                int left = length - index;

                // C: "Not optimal but not the worst thing ever" (ufbx.c:5123)
                if (TempCap - dstLen < 16) {
                    UfbxiFail.CheckNoDesc(GrowTemp(dstLen + 16), "ufbxi_grow_array(..., dst_len + 16)");
                    dst = TempStr;
                }

                // The same accept tests as ufbxi_utf8_valid_length (ufbx.c:3450-3490), but
                // this loop also emits the accepted bytes (ufbx.c:5129-5168).
                if ((c & 0x80) == 0) {
                    if (c != 0) {
                        dst[dstLen] = c;
                        dstLen += 1;
                        index += 1;
                        continue;
                    }
                } else if ((c & 0xe0) == 0xc0 && left >= 2) {
                    byte t0 = (byte)str[offset + index + 1];
                    uint code = unchecked((uint)c << 8 | (uint)t0 << 0);
                    if ((code & 0xc0) == 0x80 && code >= 0xc280) {
                        dst[dstLen + 0] = c;
                        dst[dstLen + 1] = t0;
                        dstLen += 2;
                        index += 2;
                        continue;
                    }
                } else if ((c & 0xf0) == 0xe0 && left >= 3) {
                    byte t0 = (byte)str[offset + index + 1], t1 = (byte)str[offset + index + 2];
                    uint code = unchecked((uint)c << 16 | (uint)t0 << 8 | (uint)t1);
                    if ((code & 0xc0c0) == 0x8080 && code >= 0xe0a080 && (code < 0xeda080 || code >= 0xee8080)) {
                        dst[dstLen + 0] = c;
                        dst[dstLen + 1] = t0;
                        dst[dstLen + 2] = t1;
                        dstLen += 3;
                        index += 3;
                        continue;
                    }
                } else if ((c & 0xf8) == 0xf0 && left >= 4) {
                    byte t0 = (byte)str[offset + index + 1], t1 = (byte)str[offset + index + 2], t2 = (byte)str[offset + index + 3];
                    uint code = unchecked((uint)c << 24 | (uint)t0 << 16 | (uint)t1 << 8 | (uint)t2);
                    if ((code & 0xc0c0c0) == 0x808080 && code >= 0xf0908080u && code <= 0xf48fbfbfu) {
                        dst[dstLen + 0] = c;
                        dst[dstLen + 1] = t0;
                        dst[dstLen + 2] = t1;
                        dst[dstLen + 3] = t2;
                        dstLen += 4;
                        index += 4;
                        continue;
                    }
                }

                // C: dst_len += ufbxi_add_replacement_char(pool, dst + dst_len, (char)c); index++;
                dstLen += AddReplacementChar(dst, dstLen, c);
                index++;
            }

            // C: sanitized->data = pool->temp_str; sanitized->length = dst_len;
            //      hash = ufbxi_hash_string(sanitized->data, sanitized->length);
            //      ufbxi_push_string_entry(pool, &sanitized->data, sanitized->length, hash, true)
            string interned = string.Empty;
            uint sanitizeHash = UfbxiHash.HashString(dst, 0, dstLen);
            UfbxiFail.CheckNoDesc(PushStringEntryBytes(ref interned, dst, dstLen, sanitizeHash, true), "ufbxi_push_string_entry(...)");

            pStr = interned;
            pLength = interned.Length;
            return true;
        }

        // C: ufbxi_push_sanitized_string (ufbx.c:5182-5214). `hash` is C's caller-supplied
        // hash of the raw bytes; `nonAscii` comes from ufbxi_hash_string_check_ascii().
        internal bool PushSanitizedString(ref UfbxiSanitizedString sanitized, string str, int offset, int length, uint hash, bool nonAscii, bool raw)
        {
            // C: ufbxi_check_err(pool->error, length <= UINT32_MAX) -- C's `length` is a 64-bit
            // size_t; the port's byte counts are `int`, so this is the range check it stands
            // for (`sanitized->raw_length = (uint32_t)length`).
            UfbxiFail.CheckNoDesc(length >= 0, "length <= UINT32_MAX");

            // C: sanitized->raw_data = str; sanitized->raw_length = (uint32_t)length;
            //      push_string_entry(pool, &raw_data, raw_length, hash, true)
            string rawData = offset == 0 && length == str.Length ? str : str.Substring(offset, length);
            UfbxiFail.CheckNoDesc(PushStringEntry(ref rawData, 0, length, hash, true), "ufbxi_push_string_entry(pool, &sanitized->raw_data, ...)");
            sanitized.RawData = rawData;
            sanitized.RawLength = unchecked((int)length);

            if (raw) {
                sanitized.Utf8Data = null;
                sanitized.Utf8Length = 0;
            } else if (!nonAscii) {
                // C: the sanitized string aliases the raw one -- the same canonical instance.
                sanitized.Utf8Data = sanitized.RawData;
                sanitized.Utf8Length = sanitized.RawLength;
            } else {
                int validLength = UfbxiUtf8.ValidLengthStr(str, offset, length);
                if (validLength != length) {
                    string utf8;
                    int utf8Length;
                    UfbxiFail.CheckNoDesc(PushSanitizedStringEntry(out utf8, out utf8Length, str, offset, length, validLength), "ufbxi_push_sanitized_string_entry(...)");
                    // C: ufbxi_check_err(pool->error, utf8.length <= UINT32_MAX) -- see the
                    // note above about the port's `int` byte counts.
                    UfbxiFail.CheckNoDesc(utf8Length >= 0, "utf8.length <= UINT32_MAX");
                    sanitized.Utf8Data = utf8;
                    sanitized.Utf8Length = utf8Length;
                } else {
                    sanitized.Utf8Data = sanitized.RawData;
                    sanitized.Utf8Length = sanitized.RawLength;
                }
            }

            return true;
        }

        // C: ufbxi_push_string_imp (ufbx.c:5216-5240). Returns the interned string; C returns
        // NULL only on allocation failure. `pOutLength` receives the (possibly shortened)
        // length -- C leaves it alone unless it sanitizes, and every caller passes either a
        // pointer to the same length or NULL together with `raw: true`.
        internal string PushStringImp(string str, int offset, int length, out int pOutLength, bool copy, bool raw)
        {
            pOutLength = length;
            if (length == 0) {
                // C: return ufbxi_empty_char
                return string.Empty;
            }

            uint hash;
            if (raw) {
                hash = UfbxiHash.HashString(str, offset, length);
            } else {
                bool nonAscii;
                hash = UfbxiHash.HashStringCheckAscii(str, offset, length, out nonAscii);
                if (nonAscii) {
                    int validLength = UfbxiUtf8.ValidLengthStr(str, offset, length);
                    if (validLength < length) {
                        string sanitized;
                        int sanitizedLength;
                        UfbxiFail.CheckNoDesc(PushSanitizedStringEntry(out sanitized, out sanitizedLength, str, offset, length, validLength), "ufbxi_push_sanitized_string_entry(...)");
                        pOutLength = sanitizedLength;
                        return sanitized;
                    }
                }
            }

            string data = offset == 0 && length == str.Length ? str : str.Substring(offset, length);
            UfbxiFail.CheckNoDesc(PushStringEntry(ref data, 0, length, hash, copy), "ufbxi_push_string_entry(pool, &str, length, hash, copy)");
            return data;
        }

        // C: ufbxi_push_string (ufbx.c:5241-5245) -- copy: true, with the out length.
        internal string PushString(string str, int offset, int length, out int pOutLength, bool raw)
        {
            return PushStringImp(str, offset, length, out pOutLength, true, raw);
        }

        // C: ufbxi_push_string(pool, str, length, NULL, raw) (ufbx.c:5241-5245) for the many
        // call sites that discard the length.
        internal string PushString(string str, int offset, int length, bool raw)
        {
            int ignored;
            return PushStringImp(str, offset, length, out ignored, true, raw);
        }

        // C: ufbxi_push_string_place (ufbx.c:5246-5255) -- interns in place.
        internal bool PushStringPlace(ref string pStr, ref int pLength, bool raw)
        {
            string str = pStr;
            int length = pLength;
            // C: ufbxi_check_err(pool->error, str || length == 0)
            UfbxiFail.CheckNoDesc(str != null || length == 0, "str || length == 0");
            int outLength;
            str = PushString(str, 0, length, out outLength, raw);
            // C: ufbxi_check_err(pool->error, str)
            UfbxiFail.CheckNoDesc(str != null, "ufbxi_push_string(pool, str, length, p_length, raw)");
            pStr = str;
            pLength = outLength;
            return true;
        }

        // C: ufbxi_push_string_place_str (ufbx.c:5257-5261). The port's ufbx_string carries
        // its own length, so `pLength` is re-derived from the string.
        internal bool PushStringPlaceStr(ref string pStr, bool raw)
        {
            UfbxiFail.CheckNoDesc(pStr != null, "p_str");
            int length = pStr.Length;
            return PushStringPlace(ref pStr, ref length, raw);
        }

        // C: ufbxi_push_string_place_blob (ufbx.c:5263-5270). Blobs are `byte[]` in this port,
        // so the interned string is only used to (re-)derive the byte count; an unchanged
        // length keeps the original array (C keeps the same bytes, just pooled).
        internal bool PushStringPlaceBlob(ref byte[] pData, ref int pSize, bool raw)
        {
            if (pSize == 0) {
                pData = null;
                return true;
            }
            string str = UfbxiRawStr.FromBytes(pData, 0, pSize);
            int outLength;
            string interned = PushString(str, 0, pSize, out outLength, raw);
            // C: ufbxi_check_err(pool->error, p_blob->data)
            UfbxiFail.CheckNoDesc(interned != null, "p_blob->data");
            if (outLength != pSize) {
                pData = UfbxiRawStr.ToBytes(interned, 0, outLength);
                pSize = outLength;
            }
            return true;
        }

        // C: ufbxi_load_strings (ufbx.c:11406-11424): "Push all the global 'ufbxi_*' strings
        // into the pool without copying them. This allows us to compare name pointers to the
        // global values." This is what makes `ReferenceEquals(name, UfbxiStrings.X)` the
        // counterpart of C's `name == ufbxi_X`.
        internal static bool LoadStrings(UfbxiStringPool pool)
        {
            for (int i = 0; i < UfbxiStrings.All.Length; i++) {
                string s = UfbxiStrings.All[i];
                uint hash = UfbxiHash.HashString(s, 0, s.Length);
                int ignored;
                string interned = pool.PushStringImp(s, 0, s.Length, out ignored, false, true);
                UfbxiFail.CheckNoDesc(interned != null, "ufbxi_push_string_imp(&uc->string_pool, str->data, str->length, NULL, false, true)");
            }
            return true;
        }
    }
}
