// Scene-finalize (mid-half) state appended to `ufbxi_context` by the S3b module
// (Parse/SceneFinalize.cs), owned by the S3b/S3c porting agent (2026-10-03 wave).
//
// Append-only partial: never edit Parse/UfbxiContext.cs from here (see its NOTE).
namespace Ufbx.NET
{
    internal sealed partial class UfbxiContext
    {
        // C: ufbxi_map texture_file_map (ufbx.c:6504, items `ufbxi_texture_file_entry`,
        // keyed by `const char*`). The port models it as an insertion-ordered entry list
        // because `ufbxi_pop_texture_files()` (ufbx.c:20804) reads the item array in
        // insertion order and `file->index = uc->texture_file_map.size - 1` (ufbx.c:20784)
        // numbers files by insertion. C's key is a raw `const char*` pointer into the
        // texture's `raw_*_filename` blob (offset +1 for relative names, ufbx.c:20766-20772):
        // identical strings share one pool pointer, so the port keys on (arm, content) --
        // content equality over the blob bytes reproduces the pool's pointer identity, and
        // the arm tag reproduces the `+1` trick that keeps an overlapping absolute key
        // distinct from a relative one.
        internal UfbxiTextureFileMap TextureFileMap;

        internal UfbxiTextureFileMap EnsureTextureFileMap()
        {
            if (TextureFileMap == null) TextureFileMap = new UfbxiTextureFileMap();
            return TextureFileMap;
        }

        // C: ufbxi_file_content *file_content / size_t num_file_content (ufbx.c:6533-6534
        // area). Filled by `ufbxi_resolve_file_content()` (ufbx.c:21493-21531) and looked up
        // by `ufbxi_fetch_file_content()` (ufbx.c:21480) with `absolute_filename.data`
        // POINTER equality, so entries keep the pooled string instance they were pushed with.
        internal readonly System.Collections.Generic.List<UfbxiFileContent> FileContent
            = new System.Collections.Generic.List<UfbxiFileContent>();

        // C: uint32_t *tmp_mesh_consecutive_indices (ufbx.c:6595 area) -- the per-mesh cache
        // `ufbxi_flip_winding()` (ufbx.c:21111) uses so the consecutive index buffer of a
        // mesh is duplicated at most once; reset to NULL at the start of every mesh.
        internal uint[] TmpMeshConsecutiveIndices;
    }

    // C: typedef struct { const char *key; ufbx_texture_file *file; } ufbxi_texture_file_entry
    // (ufbx.c:11501-11504). The port replaces C's pointer key with (array, offset) identity,
    // see `UfbxiContext.TextureFileMap` above.
    internal sealed class UfbxiTextureFileMap
    {
        internal struct Key
        {
            public byte[] Data;    // C: the pooled raw-string bytes (content equality)
            public bool Relative;  // C: the `+1` offset arm (relative key)

            public static bool Equals(Key a, Key b)
            {
                if (a.Relative != b.Relative || a.Data == null || b.Data == null) {
                    return a.Relative == b.Relative && a.Data == b.Data;
                }
                if (a.Data.Length != b.Data.Length) return false;
                for (int i = 0; i < a.Data.Length; i++) {
                    if (a.Data[i] != b.Data[i]) return false;
                }
                return true;
            }
        }

        // Insertion-ordered items (C: the `items` array of `ufbxi_map`).
        public readonly System.Collections.Generic.List<UfbxiTextureFileEntry> Items =
            new System.Collections.Generic.List<UfbxiTextureFileEntry>();

        private readonly System.Collections.Generic.List<Key> keys =
            new System.Collections.Generic.List<Key>();

        // C: uc->texture_file_map.size
        public int Size => Items.Count;

        // C: ufbxi_map_find(&uc->texture_file_map, ..., hash, &key) -- hash order is not
        // observable (lookup only), linear scan by identity is equivalent.
        public UfbxiTextureFileEntry Find(Key key)
        {
            for (int i = 0; i < keys.Count; i++) {
                if (Key.Equals(keys[i], key)) return Items[i];
            }
            return null;
        }

        // C: ufbxi_map_insert(...) -- appends the entry and returns its item index.
        public int Insert(Key key, UfbxiTextureFileEntry entry)
        {
            keys.Add(key);
            Items.Add(entry);
            return Items.Count - 1;
        }
    }

    // C: typedef struct { const char *key; ufbx_texture_file *file; } ufbxi_texture_file_entry.
    internal sealed class UfbxiTextureFileEntry
    {
        public UfbxiTextureFileMap.Key Key; // C: const char *key
        public UfbxTextureFile File;        // C: ufbx_texture_file *file
    }

    // C: typedef struct { ufbx_string absolute_filename; ufbx_blob content; } ufbxi_file_content
    // (ufbx.c:21440 area, consumed by ufbx_embed_texture_files). The `absolute_filename` keeps
    // the pooled string instance so `ufbxi_fetch_file_content()`'s pointer comparison can be
    // reproduced with reference equality.
    internal sealed class UfbxiFileContent
    {
        public string AbsoluteFilename; // C: ufbx_string absolute_filename
        public byte[] Content;          // C: ufbx_blob content
    }
}
