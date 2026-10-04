// Geometry / value / runtime working types ported from ufbx v0.23.1.
// Covers: ufbx_curve_point, ufbx_surface_point, ufbx_topo_edge, ufbx_vertex_stream,
//         ufbx_inflate_input, ufbx_inflate_retain, ufbx_string_view.
//
// Pointer fields (`void*` / `const void*`) map to `byte[]` where they name a byte
// buffer, or reference types where they name a callback context (`object user`).
// `size_t` -> `int`, `uint64_t` -> `ulong`. `ufbx_topo_flags`/`UFBX_NO_INDEX` live
// in Enums.cs (`UfbxTopoFlags`, `UfbxConstants.NoIndex`).

namespace Ufbx.NET
{
    // C: ufbx_curve_point (ufbx.h:4035-4039)
    public struct UfbxCurvePoint
    {
        public bool Valid;          // C: valid
        public UfbxVec3 Position;   // C: position
        public UfbxVec3 Derivative; // C: derivative
    }

    // C: ufbx_surface_point (ufbx.h:4041-4046)
    public struct UfbxSurfacePoint
    {
        public bool Valid;              // C: valid
        public UfbxVec3 Position;       // C: position
        public UfbxVec3 DerivativeU;    // C: derivative_u
        public UfbxVec3 DerivativeV;    // C: derivative_v
    }

    // C: ufbx_topo_edge (ufbx.h:4056-4065)
    public struct UfbxTopoEdge
    {
        public uint Index;    // C: index — starting index of the edge, always defined
        public uint Next;     // C: next — ending index / next per-face edge, always defined
        public uint Prev;     // C: prev — previous per-face edge, always defined
        public uint Twin;     // C: twin — opposite side, UFBX_NO_INDEX if not found
        public uint Face;     // C: face — index into mesh.faces[], always defined
        public uint Edge;     // C: edge — index into mesh.edges[], UFBX_NO_INDEX if not found
        public UfbxTopoFlags Flags; // C: ufbx_topo_flags flags
    }

    // Vertex data array for `ufbx_generate_indices()`. (C: ufbx_vertex_stream, ufbx.h:4070-4074)
    public struct UfbxVertexStream
    {
        public byte[] Data;         // C: void *data — shape `char[vertex_count][vertex_size]`
        public int VertexCount;     // C: size_t vertex_count
        public int VertexSize;      // C: size_t vertex_size
    }

    // Source data/stream to decompress with `ufbx_inflate()`.
    // (C: struct ufbx_inflate_input, ufbx.h:4410-4442)
    public class UfbxInflateInput
    {
        public int TotalSize;           // C: size_t total_size (ufbx.h:4412)

        public byte[] Data;             // C: const void *data (ufbx.h:4415)
        public int DataSize;            // C: size_t data_size

        public byte[] Buffer;           // C: void *buffer (ufbx.h:4419)
        public int BufferSize;          // C: size_t buffer_size

        public UfbxReadFn ReadFn;       // C: ufbx_read_fn *read_fn (ufbx.h:4423)
        public object ReadUser;         // C: void *read_user

        public UfbxProgressCb ProgressCb = new UfbxProgressCb(); // C: ufbx_progress_cb progress_cb (ufbx.h:4427)
        public ulong ProgressIntervalHint; // C: uint64_t progress_interval_hint

        public ulong ProgressSizeBefore;  // C: uint64_t progress_size_before (ufbx.h:4431)
        public ulong ProgressSizeAfter;   // C: uint64_t progress_size_after

        public bool NoHeader;             // C: bool no_header (ufbx.h:4435)
        public bool NoChecksum;           // C: bool no_checksum

        public int InternalFastBits;      // C: size_t internal_fast_bits (ufbx.h:4441)
    }

    // Persistent data between `ufbx_inflate()` calls. (C: struct ufbx_inflate_retain, ufbx.h:4446-4449)
    // NOTE (C): you must set `initialized` to `false`; `data` may be uninitialized.
    public class UfbxInflateRetain
    {
        public bool Initialized;          // C: bool initialized
        public ulong[] Data = new ulong[1024]; // C: uint64_t data[1024]
    }

    // C++ convenience string view (C: ufbx_string_view, ufbx.h:5834-5841).
    // Only defined under `UFBX_CPP11`; ported as a lightweight (data,length) pair.
    public struct UfbxStringView
    {
        public string Data;   // C: const char *data
        public int Length;    // C: size_t length

        public UfbxStringView(string data, int length)
        {
            Data = data;
            Length = length;
        }
    }

    // NOTE: `template <typename T> struct ufbx_converter { }` (ufbx.h:185) is a
    // C++-only SFINAE implicit-conversion helper with no data members; it has no
    // representation in the C# port and is intentionally not ported.
}
