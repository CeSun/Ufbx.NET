// C: typedef struct { const char *name; ufbx_prop_type type; } ufbxi_prop_type_name
// (ufbx.c:11426-11429), the element type of the `ufbxi_prop_type_names[]` table that
// `ufbxi_load_maps()` interns into `uc->map_prop_type`.
//
// It lives in its own file because `Parse/UfbxiContext.cs` declares the map field
// (`UfbxiMap<UfbxiPropTypeName, ulong> PropTypeMap`) and the isolated verifiers compile a
// curated subset of `src/Ufbx.NET` (see tools/AsciiCheck/AsciiCheck.csproj): a type only the
// load driver used would leave every closure that includes the context uncompilable.
// `UfbxiLoad.LoadMaps()` (Parse/Load.cs) is its only producer.

namespace Ufbx.NET
{
    // `Name` is the pooled pointer, which is also the map key
    // (`ufbxi_map_cmp_const_char_ptr`), so the port keys it by the pointer id.
    internal struct UfbxiPropTypeName
    {
        public string Name;          // C: const char *name
        public UfbxPropType Type;    // C: ufbx_prop_type type

        public UfbxiPropTypeName(string name, UfbxPropType type)
        {
            Name = name;
            Type = type;
        }
    }
}
