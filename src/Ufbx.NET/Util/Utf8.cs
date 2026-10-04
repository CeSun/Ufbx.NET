using System;

namespace Ufbx.NET
{
    // Ported from ufbx.c v0.23.1 "Printf"/"String pool" UTF-8 helpers
    // (ufbx.c:3450-3500, 5071-5100).

    internal static class UfbxiUtf8
    {
        // C: ufbxi_utf8_valid_length (ufbx.c:3450-3490) — the length of the longest valid
        // UTF-8 prefix. Stops at an embedded NUL and at the first malformed sequence
        // (overlong, surrogate, >U+10FFFF, truncated tail, 5/6-byte leads).
        internal static int ValidLength(byte[] str, int offset, int length)
        {
            int index = 0;
            while (index < length) {
                byte c = str[offset + index];
                int left = length - index;

                if ((c & 0x80) == 0) {
                    if (c != 0) {
                        index += 1;
                        continue;
                    }
                } else if ((c & 0xe0) == 0xc0 && left >= 2) {
                    byte t0 = str[offset + index + 1];
                    uint code = unchecked((uint)c << 8 | (uint)t0 << 0);
                    if ((code & 0xc0) == 0x80 && code >= 0xc280) {
                        index += 2;
                        continue;
                    }
                } else if ((c & 0xf0) == 0xe0 && left >= 3) {
                    byte t0 = str[offset + index + 1], t1 = str[offset + index + 2];
                    uint code = unchecked((uint)c << 16 | (uint)t0 << 8 | (uint)t1);
                    if ((code & 0xc0c0) == 0x8080 && code >= 0xe0a080 && (code < 0xeda080 || code >= 0xee8080)) {
                        index += 3;
                        continue;
                    }
                } else if ((c & 0xf8) == 0xf0 && left >= 4) {
                    byte t0 = str[offset + index + 1], t1 = str[offset + index + 2], t2 = str[offset + index + 3];
                    uint code = unchecked((uint)c << 24 | (uint)t0 << 16 | (uint)t1 << 8 | (uint)t2);
                    if ((code & 0xc0c0c0) == 0x808080 && code >= 0xf0908080u && code <= 0xf48fbfbfu) {
                        index += 4;
                        continue;
                    }
                }

                break;
            }

            // C: ufbx_assert(index <= length)
            return index;
        }

        // C: ufbxi_utf8_valid_length (ufbx.c:3450-3490), raw-byte `string` input.
        internal static int ValidLengthStr(string str, int offset, int length)
        {
            int index = 0;
            while (index < length) {
                byte c = (byte)str[offset + index];
                int left = length - index;

                if ((c & 0x80) == 0) {
                    if (c != 0) {
                        index += 1;
                        continue;
                    }
                } else if ((c & 0xe0) == 0xc0 && left >= 2) {
                    byte t0 = (byte)str[offset + index + 1];
                    uint code = unchecked((uint)c << 8 | (uint)t0 << 0);
                    if ((code & 0xc0) == 0x80 && code >= 0xc280) {
                        index += 2;
                        continue;
                    }
                } else if ((c & 0xf0) == 0xe0 && left >= 3) {
                    byte t0 = (byte)str[offset + index + 1], t1 = (byte)str[offset + index + 2];
                    uint code = unchecked((uint)c << 16 | (uint)t0 << 8 | (uint)t1);
                    if ((code & 0xc0c0) == 0x8080 && code >= 0xe0a080 && (code < 0xeda080 || code >= 0xee8080)) {
                        index += 3;
                        continue;
                    }
                } else if ((c & 0xf8) == 0xf0 && left >= 4) {
                    byte t0 = (byte)str[offset + index + 1], t1 = (byte)str[offset + index + 2], t2 = (byte)str[offset + index + 3];
                    uint code = unchecked((uint)c << 24 | (uint)t0 << 16 | (uint)t1 << 8 | (uint)t2);
                    if ((code & 0xc0c0c0) == 0x808080 && code >= 0xf0908080u && code <= 0xf48fbfbfu) {
                        index += 4;
                        continue;
                    }
                }

                break;
            }

            return index;
        }

        // C: ufbxi_clean_string_utf8 (ufbx.c:3492-3500) — replace every byte that starts a
        // malformed sequence with '?' and skip exactly that one byte.
        internal static void CleanStringUtf8(byte[] str, int offset, int length)
        {
            int pos = 0;
            for (;;) {
                pos += ValidLength(str, offset + pos, length - pos);
                if (pos == length) break;
                str[offset + pos++] = (byte)'?';
            }
        }

        // C: ufbxi_clean_string_utf8 (ufbx.c:3492-3500), NUL-terminated `string` form used
        // for the `ufbx_error` info buffer when it is not held in a byte array.
        internal static string CleanStringUtf8Str(string str, int length)
        {
            byte[] bytes = UfbxiRawStr.ToBytes(str, 0, length);
            CleanStringUtf8(bytes, 0, length);
            return UfbxiRawStr.FromBytes(bytes, 0, length);
        }

        // C: ufbxi_add_replacement_char (ufbx.c:5071-5100).
        internal static int AddReplacementChar(UfbxUnicodeErrorHandling errorHandling, byte[] dst, int offset, byte c)
        {
            switch (errorHandling) {

            case UfbxUnicodeErrorHandling.ReplacementCharacter:
                dst[offset + 0] = (byte)0xef;
                dst[offset + 1] = (byte)0xbf;
                dst[offset + 2] = (byte)0xbd;
                return 3;

            case UfbxUnicodeErrorHandling.Underscore:
                dst[offset + 0] = (byte)'_';
                return 1;

            case UfbxUnicodeErrorHandling.QuestionMark:
                dst[offset + 0] = (byte)'?';
                return 1;

            case UfbxUnicodeErrorHandling.Remove:
                return 0;

            case UfbxUnicodeErrorHandling.UnsafeIgnore:
                dst[offset + 0] = c;
                return 1;

            default:
                return 0;

            }
        }
    }

    // `ufbx_string { const char *data; size_t length; }` maps to a C# `string` (see
    // PORTING_NOTES.md) that holds the bytes *verbatim*: one char per byte, code points
    // 0x00..0xFF. That keeps the two properties the port depends on:
    //   * `str.Length` == C's `length` (a byte count), and
    //   * ordinal char comparison == C's `memcmp`.
    // Non-ASCII UTF-8 text therefore looks "mojibake" when shown as a .NET string; decode
    // with `UfbxiRawStr.ToBytes` + UTF-8 at the display boundary.
    internal static class UfbxiRawStr
    {
        internal static string FromBytes(byte[] data, int offset, int length)
        {
            if (length == 0) return string.Empty;
            char[] chars = new char[length];
            for (int i = 0; i < length; i++) {
                chars[i] = (char)data[offset + i];
            }
            return new string(chars);
        }

        internal static byte[] ToBytes(string str)
        {
            return ToBytes(str, 0, str == null ? 0 : str.Length);
        }

        internal static byte[] ToBytes(string str, int offset, int length)
        {
            if (str == null) return null;
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++) {
                bytes[i] = (byte)str[offset + i];
            }
            return bytes;
        }
    }
}
