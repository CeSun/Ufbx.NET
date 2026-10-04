// Shared file/path infrastructure, ported from ufbx v0.23.1 ufbx.c:
//   ufbxi_strblob union + ufbxi_strblob_set/data/length (16530-16555)
//   ufbxi_is_absolute_path                 (16556-16564)
//   ufbxi_resolve_relative_filename        (16566-16649)
//   ufbxi_open_file                        (16653-16668)
//
// MAPPING NOTES (PORTING_NOTES.md "raw-byte 字符串"):
//  - `ufbx_string` -> `string` with one char per byte, `ufbx_blob` -> `byte[]`.
//  - The C `ufbxi_strblob` union (ufbx.c:16530-16533) aliases one `const char*` as both an
//    `ufbx_string` and an `ufbx_blob`; the port keeps both arms and `raw` selects the live
//    one, exactly like C's flag.
//  - `uc->tmp_stack` is only scratch for the resolved path (ufbx.c:16622/16644); the port
//    allocates a local `byte[]`, which is unobservable (the arena address/order through
//    `tmp_stack` is not part of any output). The one observable is the string-pool
//    interning order at ufbx.c:16643, reproduced verbatim.
using System;

namespace Ufbx
{
    internal static class UfbxiSceneFiles
    {
        // C: typedef union { ufbx_string str; ufbx_blob blob; } ufbxi_strblob (ufbx.c:16530-16533).
        internal struct UfbxiStrblob
        {
            public string Str;    // C: str  (live when `raw == false`)
            public byte[] Blob;   // C: blob (live when `raw == true`)
        }

        // C: ufbxi_strblob_set (ufbx.c:16535-16544).
        // raw=true  -> blob.data = data, blob.size = length (a 0-length blob stays NULL).
        // raw=false -> str.data = length==0 ? ufbxi_empty_char : data, str.length = length.
        internal static void StrblobSet(ref UfbxiStrblob dst, byte[] data, int offset, int length, bool raw)
        {
            if (raw) {
                if (data == null || length == 0) {
                    dst.Blob = null;
                } else if (offset == 0 && length == data.Length) {
                    dst.Blob = data;
                } else {
                    byte[] copy = new byte[length];
                    Array.Copy(data, offset, copy, 0, length);
                    dst.Blob = copy;
                }
            } else {
                dst.Str = length == 0 ? string.Empty : UfbxiRawStr.FromBytes(data, offset, length);
            }
        }

        // C: ufbxi_strblob_data (ufbx.c:16546-16549) — the byte view of whichever arm is live.
        static byte[] StrblobData(UfbxiStrblob src, bool raw)
        {
            if (raw) return src.Blob != null ? src.Blob : Array.Empty<byte>();
            return UfbxiRawStr.ToBytes(src.Str != null ? src.Str : string.Empty);
        }

        // C: ufbxi_strblob_length (ufbx.c:16551-16554).
        static int StrblobLength(UfbxiStrblob src, bool raw)
        {
            if (raw) return src.Blob != null ? src.Blob.Length : 0;
            return src.Str != null ? src.Str.Length : 0;
        }

        // C: ufbxi_is_absolute_path (ufbx.c:16556-16564).
        // `path[0] == '/' || path[0] == '\\'`, or a drive letter `x:\` / `x:/`.
        internal static bool IsAbsolutePath(byte[] path, int offset, int length)
        {
            if (length > 0 && (path[offset] == (byte)'/' || path[offset] == (byte)'\\')) {
                return true;
            } else if (length > 2 && path[offset + 1] == (byte)':' &&
                       (path[offset + 2] == (byte)'\\' || path[offset + 2] == (byte)'/')) {
                return true;
            }
            return false;
        }

        // C: ufbxi_resolve_relative_filename (ufbx.c:16566-16649). Resolves `p_src` against
        // `uc->scene.metadata.relative_root` (raw arm when `raw`), cancelling `../` segments and
        // rewriting every separator to `uc->opts.path_separator`, then interns the result.
        internal static bool ResolveRelativeFilename(UfbxiContext uc, ref UfbxiStrblob pDst, UfbxiStrblob pSrc, bool raw)
        {
            byte[] src = StrblobData(pSrc, raw);
            int srcLength = StrblobLength(pSrc, raw);
            int srcOffset = 0;

            // Skip leading directory separators and early return if the relative path is empty
            while (srcLength > 0 && (src[srcOffset] == (byte)'/' || src[srcOffset] == (byte)'\\')) {
                srcOffset++;
                srcLength--;
            }
            if (srcLength == 0) {
                StrblobSet(ref pDst, null, 0, 0, raw);
                return true;
            }

            byte[] prefixData;
            int prefixLength;
            if (raw) {
                prefixData = uc.Scene.Metadata.RawRelativeRoot != null ? uc.Scene.Metadata.RawRelativeRoot : Array.Empty<byte>();
                prefixLength = uc.Scene.Metadata.RawRelativeRoot != null ? uc.Scene.Metadata.RawRelativeRoot.Length : 0;
            } else {
                string relativeRoot = uc.Scene.Metadata.RelativeRoot != null ? uc.Scene.Metadata.RelativeRoot : string.Empty;
                prefixData = UfbxiRawStr.ToBytes(relativeRoot);
                prefixLength = relativeRoot.Length;
            }

            // Retain absolute paths
            if (IsAbsolutePath(src, srcOffset, srcLength)) {
                prefixLength = 0;
            }

            // Undo directories from `prefix` for every `..`
            while (prefixLength > 0 && srcLength >= 3 && src[srcOffset] == (byte)'.' &&
                   src[srcOffset + 1] == (byte)'.' &&
                   (src[srcOffset + 2] == (byte)'/' || src[srcOffset + 2] == (byte)'\\')) {
                int partStart = prefixLength;
                while (partStart > 0 && !(prefixData[partStart - 1] == (byte)'/' || prefixData[partStart - 1] == (byte)'\\')) {
                    partStart--;
                }
                int partLen = prefixLength - partStart;

                if (partLen == 2 && prefixData[partStart] == (byte)'.' && prefixData[partStart + 1] == (byte)'.') {
                    // Prefix itself ends in `..`, cannot cancel out a leading `../`
                    break;
                }

                // Eat the leading '/' before the part segment
                prefixLength = partStart > 0 ? partStart - 1 : 0;

                if (partLen == 1 && prefixData[partStart] == (byte)'.') {
                    // Single '.' -> remove and continue without cancelling out a leading `../`
                    continue;
                }

                srcOffset += 3;
                srcLength -= 3;
            }

            int resultCap = prefixLength + srcLength + 1;
            byte[] result = new byte[resultCap];
            int ptr = 0;

            // Copy prefix and suffix converting separators in the process
            if (prefixLength > 0) {
                Array.Copy(prefixData, 0, result, 0, prefixLength);
                result[prefixLength] = (byte)uc.Opts.PathSeparator;
                ptr = prefixLength + 1;
            }
            for (int i = 0; i < srcLength; i++) {
                byte c = src[srcOffset + i];
                if (c == (byte)'/' || c == (byte)'\\') {
                    c = (byte)uc.Opts.PathSeparator;
                }
                result[ptr++] = c;
            }

            // Intern the string (C: ufbxi_push_string_place_str(&uc->string_pool, &dst, raw)).
            int dstLength = ptr;
            string dstStr = UfbxiRawStr.FromBytes(result, 0, dstLength);
            uc.StringPool.PushStringPlaceStr(ref dstStr, raw);

            byte[] dstBytes = UfbxiRawStr.ToBytes(dstStr);
            StrblobSet(ref pDst, dstBytes, 0, dstBytes.Length, raw);

            return true;
        }

        // C: ufbxi_open_file (ufbx.c:16653-16668). Fills a `ufbx_open_file_info` and forwards
        // to the user callback. `ator` is C's `ufbxi_allocator *` context (the port's allocator
        // has no observable role, so the raw `uintptr_t` is passed through unchanged).
        internal static bool OpenFile(UfbxOpenFileCb cb, UfbxiLoad.UfbxiOpenFileStream stream, string path, int pathLen,
            byte[] originalFilename, nint ator, UfbxOpenFileType type)
        {
            if (cb == null || cb.Fn == null) return false;

            UfbxOpenFileInfo info = new UfbxOpenFileInfo();
            info.Context = ator;
            if (originalFilename != null) {
                info.OriginalFilename = originalFilename;
            } else {
                info.OriginalFilename = UfbxiRawStr.ToBytes(path, 0, pathLen);
            }
            info.Type = type;

            return cb.Fn(cb.User, stream, path, pathLen, info);
        }
    }
}
