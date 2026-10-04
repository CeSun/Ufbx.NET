// Property system and file header readers, ported from ufbx v0.23.1 ufbx.c:
//   ufbxi_get_prop_type                      (11466-11474)
//   ufbxi_find_prop_with_key / find_prop     (11476-11514)
//   ufbxi_find_real / vec3 / int / enum      (11516-11560)
//   is_*_zero / quat_identity / transform    (11562-11603)
//   ufbxi_get_name_key / _c / name_key_less  (11605-11639)
//   ufbxi_node_prop_names[]                  (11641-11714)
//   ufbxi_init_node_prop_names               (11716-11730)
//   ufbxi_is_node_property_name              (11732-11740)
//   ufbxi_read_embedded_blob                 (11760-11792)
//   ufbxi_read_property                      (11794-11865)
//   ufbxi_prop_less / sort / deduplicate     (11867-11897)
//   ufbxi_read_properties                    (11899-11928)
//   ufbxi_read_thumbnail / scene_info        (11930-11975)
//   ufbxi_read_header_extension              (11977-12029)
//   ufbxi_match_version_string / exporter    (12031-12124)
//   ufbxi_read_document / definitions        (12126-12189)
//   ufbxi_find_template                      (12191-12214)
//   ufbx_find_prop_len and the find_* family (30643-30698)
//
// NOTE: `ufbxi_load_maps()` (11742-11756) is already implemented by the load spine as
// `UfbxiLoad.LoadMaps()` (Parse/Load.cs), so it is deliberately not duplicated here.
using System;

namespace Ufbx.NET
{
    internal static class UfbxiProperties
    {
        // ==================================================================
        // ufbxi_node_prop_names[] (ufbx.c:11641-11714). Raw string literals, interned with
        // `copy: false` by `InitNodePropNames` so their addresses become the canonical pool
        // entries (exactly like C).
        // ==================================================================
        static readonly string[] NodePropNames = new string[] {
            "AxisLen",
            "DefaultAttributeIndex",
            "Freeze",
            "GeometricRotation",
            "GeometricScaling",
            "GeometricTranslation",
            "InheritType",
            "LODBox",
            "Lcl Rotation",
            "Lcl Scaling",
            "Lcl Translation",
            "LookAtProperty",
            "MaxDampRangeX",
            "MaxDampRangeY",
            "MaxDampRangeZ",
            "MaxDampStrengthX",
            "MaxDampStrengthY",
            "MaxDampStrengthZ",
            "MinDampRangeX",
            "MinDampRangeY",
            "MinDampRangeZ",
            "MinDampStrengthX",
            "MinDampStrengthY",
            "MinDampStrengthZ",
            "NegativePercentShapeSupport",
            "PostRotation",
            "PreRotation",
            "PreferedAngleX",
            "PreferedAngleY",
            "PreferedAngleZ",
            "QuaternionInterpolate",
            "RotationActive",
            "RotationMax",
            "RotationMaxX",
            "RotationMaxY",
            "RotationMaxZ",
            "RotationMin",
            "RotationMinX",
            "RotationMinY",
            "RotationMinZ",
            "RotationOffset",
            "RotationOrder",
            "RotationPivot",
            "RotationSpaceForLimitOnly",
            "RotationStiffnessX",
            "RotationStiffnessY",
            "RotationStiffnessZ",
            "ScalingActive",
            "ScalingMax",
            "ScalingMaxX",
            "ScalingMaxY",
            "ScalingMaxZ",
            "ScalingMin",
            "ScalingMinX",
            "ScalingMinY",
            "ScalingMinZ",
            "ScalingOffset",
            "ScalingPivot",
            "Show",
            "TranslationActive",
            "TranslationMax",
            "TranslationMaxX",
            "TranslationMaxY",
            "TranslationMaxZ",
            "TranslationMin",
            "TranslationMinX",
            "TranslationMinY",
            "TranslationMinZ",
            "UpVectorProperty",
            "Visibility Inheritance",
            "Visibility",
            "notes",
        };

        // ==================================================================
        // Small value/name helpers
        // ==================================================================

        // C: ufbxi_get_name_key (ufbx.c:11605-11618).
        internal static uint GetNameKey(string name, int len)
        {
            uint key = 0;
            if (len >= 4) {
                key = (uint)(byte)name[0] << 24 | (uint)(byte)name[1] << 16
                    | (uint)(byte)name[2] << 8 | (uint)(byte)name[3];
            } else {
                for (int i = 0; i < 4; i++) {
                    key <<= 8;
                    if (i < len) key |= (byte)name[i];
                }
            }
            return key;
        }

        // C: ufbxi_get_name_key_c (ufbx.c:11620-11627). The four bytes of a NUL-terminated
        // C string; a short string pads with the following bytes, which the constant tables
        // never exercise (all names are >= 4 bytes).
        internal static uint GetNameKeyC(string name)
        {
            if (name.Length == 0 || name[0] == '\0') return 0;
            if (name.Length < 2 || name[1] == '\0') return (uint)(byte)name[0] << 24;
            if (name.Length < 3 || name[2] == '\0') return (uint)(byte)name[0] << 24 | (uint)(byte)name[1] << 16;
            return (uint)(byte)name[0] << 24 | (uint)(byte)name[1] << 16
                | (uint)(byte)name[2] << 8 | (uint)(byte)name[3];
        }

        // C: strcmp() over the port's one-byte-per-char strings.
        internal static int Strcmp(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++) {
                char ca = a[i], cb = b[i];
                if (ca == '\0') return cb == '\0' ? 0 : -1;
                if (cb == '\0') return 1;
                if (ca != cb) return ca < cb ? -1 : 1;
            }
            if (a.Length == b.Length) return 0;
            return a.Length < b.Length ? -1 : 1;
        }

        // C: ufbxi_is_vec3_zero (ufbx.c:11570-11573). Bitwise `&`: all three are evaluated.
        internal static bool IsVec3Zero(UfbxVec3 v)
        {
            return (v.X == 0.0) & (v.Y == 0.0) & (v.Z == 0.0);
        }

        // C: ufbxi_is_vec4_zero (ufbx.c:11575-11578) -- NOTE: only x/y/z, `w` is ignored.
        internal static bool IsVec4Zero(UfbxVec4 v)
        {
            return (v.X == 0.0) & (v.Y == 0.0) & (v.Z == 0.0);
        }

        // C: ufbxi_is_vec3_one (ufbx.c:11580-11583).
        internal static bool IsVec3One(UfbxVec3 v)
        {
            return (v.X == 1.0) & (v.Y == 1.0) & (v.Z == 1.0);
        }

        // C: ufbxi_is_quat_identity (ufbx.c:11585-11588).
        internal static bool IsQuatIdentity(UfbxQuat v)
        {
            return (v.X == 0.0) & (v.Y == 0.0) & (v.Z == 0.0) & (v.W == 1.0);
        }

        // C: ufbxi_is_vec3_equal (ufbx.c:11590-11593).
        internal static bool IsVec3Equal(UfbxVec3 a, UfbxVec3 b)
        {
            return (a.X == b.X) & (a.Y == b.Y) & (a.Z == b.Z);
        }

        // C: ufbxi_is_quat_equal (ufbx.c:11595-11598).
        internal static bool IsQuatEqual(UfbxQuat a, UfbxQuat b)
        {
            return (a.X == b.X) & (a.Y == b.Y) & (a.Z == b.Z) & (a.W == b.W);
        }

        // C: ufbxi_is_transform_identity (ufbx.c:11600-11603).
        internal static bool IsTransformIdentity(UfbxTransform t)
        {
            return IsVec3Zero(t.Translation) & IsQuatIdentity(t.Rotation) & IsVec3One(t.Scale);
        }

        // C: ufbxi_name_key_less (ufbx.c:11629-11639).
        internal static bool NameKeyLess(UfbxProp prop, string data, int nameLen, uint key)
        {
            if (prop.InternalKey < key) return true;
            if (prop.InternalKey > key) return false;

            int propLen = prop.Name.Length;
            int len = Math.Min(propLen, nameLen);
            int cmp = string.CompareOrdinal(prop.Name, 0, data, 0, len);
            if (cmp != 0) return cmp < 0;
            return propLen < nameLen;
        }

        // The map key C computes from the literal in `ufbxi_find_prop(props, name)`:
        // ((uint8_t)name[0]<<24) | (name[1]<<16) | (name[2]<<8) | name[3]. All call sites use
        // constants of length >= 4, where `GetNameKey` agrees.
        static uint FindPropKey(string name) => GetNameKey(name, name.Length);

        // ==================================================================
        // Property lookup
        // ==================================================================

        // C: ufbxi_find_prop_with_key (ufbx.c:11476-11505). Returns the item index inside the
        // owning `ufbx_props` and that owner through `out owner`; -1 == C's NULL.
        // `name` is one of the interned `ufbxi_*` constants, so C's `p->name.data == name`
        // pointer test is content equality over the canonical pool strings.
        internal static int FindPropIndex(UfbxProps props, string name, uint key, bool searchDefaults, out UfbxProps owner)
        {
            while (props != null) {
                UfbxProp[] propData = props.Props;
                if (propData != null) {
                    int begin = 0;
                    int end = propData.Length;
                    while (end - begin >= 16) {
                        int mid = (begin + end) >> 1;
                        UfbxProp p = propData[mid];
                        if (p.InternalKey < key) {
                            begin = mid + 1;
                        } else {
                            end = mid;
                        }
                    }

                    end = propData.Length;
                    for (; begin < end; begin++) {
                        UfbxProp p = propData[begin];
                        if (p.InternalKey > key) break;
                        if (p.Name == name && ((uint)p.Flags & (uint)UfbxPropFlags.NoValue) == 0) {
                            owner = props;
                            return begin;
                        }
                    }
                }

                if (!searchDefaults) break;
                props = props.Defaults;
            }

            owner = null;
            return -1;
        }

        // C: ufbxi_find_prop(props, name) (ufbx.c:11512-11514).
        internal static bool TryFindProp(UfbxProps props, string name, out UfbxProp prop)
        {
            int index = FindPropIndex(props, name, FindPropKey(name), true, out UfbxProps owner);
            if (index >= 0) {
                prop = owner.Props[index];
                return true;
            }
            prop = default;
            return false;
        }

        // C: ufbxi_find_real (ufbx.c:11516-11524).
        internal static double FindReal(UfbxProps props, string name, double def)
        {
            UfbxProp prop;
            if (TryFindProp(props, name, out prop)) return prop.ValueReal;
            return def;
        }

        // C: ufbxi_find_vec3 (ufbx.c:11526-11535).
        internal static UfbxVec3 FindVec3(UfbxProps props, string name, double defX, double defY, double defZ)
        {
            UfbxProp prop;
            if (TryFindProp(props, name, out prop)) return prop.ValueVec3;
            return new UfbxVec3(defX, defY, defZ);
        }

        // C: ufbxi_find_int (ufbx.c:11537-11545).
        internal static long FindInt(UfbxProps props, string name, long def)
        {
            UfbxProp prop;
            if (TryFindProp(props, name, out prop)) return prop.ValueInt;
            return def;
        }

        // C: ufbxi_find_enum (ufbx.c:11547-11560).
        internal static long FindEnum(UfbxProps props, string name, long def, long maxValue)
        {
            UfbxProp prop;
            if (TryFindProp(props, name, out prop)) {
                long value = prop.ValueInt;
                if (value >= 0 && value <= maxValue) return value;
                return def;
            }
            return def;
        }

        // C: ufbxi_get_prop_type (ufbx.c:11466-11474).
        internal static UfbxPropType GetPropType(UfbxiContext uc, string name)
        {
            ulong id = UfbxiPtrIdTable.IdOf(name);
            uint hash = UfbxiHash.HashPtr(id);
            int index = uc.PropTypeMap.Find(hash, id);
            if (index >= 0) {
                return uc.PropTypeMap.Items[index].Type;
            }
            return UfbxPropType.Unknown;
        }

        // ==================================================================
        // Node property names (pre-7000)
        // ==================================================================

        // C: ufbxi_init_node_prop_names (ufbx.c:11716-11730).
        internal static void InitNodePropNames(UfbxiContext uc)
        {
            UfbxiFail.CheckNoDesc(uc.NodePropSet.Grow((uint)NodePropNames.Length),
                "ufbxi_map_grow(&uc->node_prop_set, const char*, ufbxi_arraycount(ufbxi_node_prop_names))");
            for (int i = 0; i < NodePropNames.Length; i++) {
                string name = NodePropNames[i];
                int ignored;
                string pooled = uc.StringPool.PushStringImp(name, 0, name.Length, out ignored, false, true);
                UfbxiFail.CheckNoDesc(pooled != null,
                    "ufbxi_push_string_imp(&uc->string_pool, name, strlen(name), NULL, false, true)");

                ulong key = UfbxiPtrIdTable.IdOf(pooled);
                uint hash = UfbxiHash.HashPtr(key);
                int index = uc.NodePropSet.Insert(hash, key);
                UfbxiFail.CheckNoDesc(index >= 0, "ufbxi_map_insert(&uc->node_prop_set, const char*, hash, &pooled)");
                uc.NodePropSet.Items[index] = pooled;
            }
        }

        // C: ufbxi_is_node_property_name (ufbx.c:11732-11740).
        internal static bool IsNodePropertyName(UfbxiContext uc, string name)
        {
            // C: ufbx_assert(uc->node_prop_set.size > 0)
            ulong key = UfbxiPtrIdTable.IdOf(name);
            uint hash = UfbxiHash.HashPtr(key);
            return uc.NodePropSet.Find(hash, key) >= 0;
        }

        // ==================================================================
        // Reading properties
        // ==================================================================

        // C: ufbxi_read_embedded_blob (ufbx.c:11760-11792). `wrote` is false when C returns
        // without touching `dst_blob` (node == NULL, or no usable 'C' array).
        internal static bool ReadEmbeddedBlob(UfbxiContext uc, UfbxiNode node, out byte[] data, out int size)
        {
            data = null;
            size = 0;
            if (node == null) return false;

            UfbxiValueArray contentArr = node.GetArray('C');
            if (contentArr != null && contentArr.Size > 0) {
                int numParts = contentArr.Size;
                if (numParts == 1 && !uc.FromAscii) {
                    string part = contentArr.Strings[contentArr.Offset + 0];
                    data = UfbxiSanitizedString.ToBlob(part, part.Length);
                    size = part.Length;
                } else {
                    int totalSize = 0;
                    for (int i = 0; i < numParts; i++) {
                        totalSize += contentArr.Strings[contentArr.Offset + i].Length;
                    }
                    byte[] dst = new byte[totalSize];
                    int o = 0;
                    for (int i = 0; i < numParts; i++) {
                        string part = contentArr.Strings[contentArr.Offset + i];
                        for (int j = 0; j < part.Length; j++) dst[o++] = unchecked((byte)part[j]);
                    }
                    data = dst;
                    size = totalSize;
                }
                return true;
            }

            return false;
        }

        // C: ufbxi_read_property (ufbx.c:11794-11865).
        internal static void ReadProperty(UfbxiContext uc, UfbxiNode node, ref UfbxProp prop, int version)
        {
            string typeStr = null, subtypeStr = null;
            string propName;
            UfbxiFail.CheckNoDesc(node.GetValS(0, out propName) && node.GetValC(1, out typeStr),
                "ufbxi_get_val2(node, \"SC\", &prop->name, (char**)&type_str)");
            prop.Name = propName;

            int valIx = 2;
            if (version == 70) {
                string sub;
                bool ok = node.GetValC(valIx, out sub);
                valIx++;
                UfbxiFail.CheckNoDesc(ok, "ufbxi_get_val_at(node, val_ix++, 'C', (char**)&subtype_str)");
                subtypeStr = sub;
            }

            uint flags = 0;
            prop.InternalKey = GetNameKey(prop.Name, prop.Name.Length);

            string flagsStr;
            bool hasFlags = node.GetValS(valIx, out flagsStr);
            valIx++;
            if (hasFlags) {
                for (int i = 0; i < flagsStr.Length; i++) {
                    char next = i + 1 < flagsStr.Length ? flagsStr[i + 1] : '0';
                    switch (flagsStr[i]) {
                    case 'A': flags |= (uint)UfbxPropFlags.Animatable; break;
                    case 'U': flags |= (uint)UfbxPropFlags.UserDefined; break;
                    case 'H': flags |= (uint)UfbxPropFlags.Hidden; break;
                    case 'L': flags |= ((uint)(next - '0') & 0xf) << 4; break; // UFBX_PROP_FLAG_LOCK_*
                    case 'M': flags |= ((uint)(next - '0') & 0xf) << 8; break; // UFBX_PROP_FLAG_MUTE_*
                    default: break; // Ignore unknown flags
                    }
                }
            }

            prop.Type = GetPropType(uc, typeStr);
            if (prop.Type == UfbxPropType.Unknown && subtypeStr != null) {
                prop.Type = GetPropType(uc, subtypeStr);
            }

            long valueInt;
            if (node.GetValL(valIx, out valueInt)) {
                prop.ValueInt = valueInt;
                flags |= (uint)UfbxPropFlags.ValueInt;
            }

            int realIx;
            for (realIx = 0; realIx < 4; realIx++) {
                double rv;
                if (!node.GetValR(valIx + realIx, out rv)) break;
                prop.SetRealAt(realIx, rv);
            }
            if (realIx > 0) {
                flags |= (uint)UfbxPropFlags.ValueReal << (realIx - 1);
            }

            // Skip one value forward in case the current value is not a string, as some
            // properties contain mixed numbers and strings (ufbx.c:11838-11844).
            if (node.GetValType(valIx) != UfbxiValueType.String) {
                valIx++;
            }

            string valueStr;
            if (node.GetValS(valIx, out valueStr)) {
                // C: `vals[val_ix]` is known to be a string, fetch non-sanitized blob directly.
                prop.ValueStr = valueStr;
                UfbxiSanitizedString ss = node.Vals[valIx].S;
                prop.ValueBlob = UfbxiSanitizedString.ToBlob(ss.RawData, ss.RawLength);
                flags |= (uint)UfbxPropFlags.ValueStr;
            } else {
                prop.ValueStr = string.Empty;
            }

            // Very unlikely, seems to only exist in some "non standard" FBX files.
            if (node.NumChildren > 0) {
                UfbxiNode binary = node.FindChild(UfbxiStrings.BinaryData);
                byte[] blob;
                int blobSize;
                if (ReadEmbeddedBlob(uc, binary, out blob, out blobSize)) {
                    prop.ValueBlob = blob;
                }
                flags |= (uint)UfbxPropFlags.ValueBlob;
            }

            prop.Flags = (UfbxPropFlags)flags;
        }

        // C: ufbxi_prop_less (ufbx.c:11867-11872).
        static bool PropLess(object user, UfbxProp a, UfbxProp b)
        {
            if (a.InternalKey < b.InternalKey) return true;
            if (a.InternalKey > b.InternalKey) return false;
            return Strcmp(a.Name, b.Name) < 0;
        }

        // C: ufbxi_sort_properties (ufbx.c:11874-11879). C grows `uc->tmp_arr`; the port uses
        // a scratch array of the same size (PORTING_NOTES.md #4).
        internal static void SortProperties(UfbxProp[] props)
        {
            int count = props.Length;
            UfbxProp[] tmp = new UfbxProp[count];
            UfbxiSort.StableSort(32, props, tmp, count, PropLess, null);
        }

        // C: ufbxi_deduplicate_properties (ufbx.c:11881-11897). C keeps the allocation and
        // lowers `count`; the port's `ufbx_prop_list` is the array itself, so the array is
        // shrunk to the surviving count (the dropped tail is never observable).
        internal static void DeduplicateProperties(UfbxProps list)
        {
            UfbxProp[] ps = list.Props;
            int count = ps != null ? ps.Length : 0;
            if (count >= 2) {
                int dst = 0, src = 0, end = count;
                while (src < end) {
                    if (src + 1 < end && ps[src].Name == ps[src + 1].Name) {
                        src++;
                    } else if (dst != src) {
                        ps[dst++] = ps[src++];
                    } else {
                        dst++; src++;
                    }
                }
                if (dst != count) {
                    Array.Resize(ref list.Props, dst);
                }
            }
        }

        // C: ufbxi_read_properties (ufbx.c:11899-11928).
        internal static void ReadProperties(UfbxiContext uc, UfbxiNode parent, UfbxProps props)
        {
            props.Defaults = null;

            int version = 70;
            UfbxiNode node = parent.FindChild(UfbxiStrings.Properties70);
            if (node == null) {
                node = parent.FindChild(UfbxiStrings.Properties60);
                if (node == null) {
                    // No properties found, not an error
                    props.Props = null;
                    return;
                }
                version = 60;
            }

            props.Props = new UfbxProp[(int)node.NumChildren];
            for (int i = 0; i < node.NumChildren; i++) {
                ReadProperty(uc, node.Children[i], ref props.Props[i], version);
            }

            SortProperties(props.Props);
            DeduplicateProperties(props);
        }

        // ==================================================================
        // Public content-based property lookup (ufbx.c:30643-30698)
        // ==================================================================

        // C: ufbxi_cmp_prop_less_ref (ufbx.c:18571-18575).
        static bool CmpPropLessRef(UfbxProp a, string name, uint key)
        {
            if (a.InternalKey != key) return a.InternalKey < key;
            return UfbxiStr.Less(a.Name, name);
        }

        // C: ufbxi_macro_lower_bound_eq (ufbx.c:1188-1205) with m_type=ufbx_prop,
        // m_linear_size=4. Note the `hi = mid + 1` (not `mid`) of the original macro.
        static bool LowerBoundEqProps(UfbxProp[] data, int begin, int size, uint key, string name, out int result)
        {
            int lo = begin, hi = size;
            int linearSize = 4; // ufbxi_clamp_linear_threshold(4) == 4 in the golden build
            while (hi - lo > linearSize) {
                int mid = lo + (hi - lo) / 2;
                UfbxProp a = data[mid];
                if (CmpPropLessRef(a, name, key)) {
                    lo = mid + 1;
                } else {
                    hi = mid + 1;
                }
            }
            for (; lo < hi; lo++) {
                UfbxProp a = data[lo];
                if (a.InternalKey == key && UfbxiStr.Equal(a.Name, name)) {
                    result = lo;
                    return true;
                }
            }
            result = -1;
            return false;
        }

        // C: ufbx_find_prop_len (ufbx.c:30643-30658). Returns the owning props and the index,
        // or false. `nameLen` is C's explicit length; the port's `name` string already carries
        // exactly that many characters.
        internal static bool TryFindPropLen(UfbxProps props, string name, out UfbxProps owner, out int index)
        {
            uint key = GetNameKey(name, name.Length);
            while (props != null) {
                UfbxProp[] data = props.Props;
                if (data != null) {
                    int found;
                    if (LowerBoundEqProps(data, 0, data.Length, key, name, out found)) {
                        owner = props;
                        index = found;
                        return true;
                    }
                }
                props = props.Defaults;
            }
            owner = null;
            index = -1;
            return false;
        }

        // C: ufbx_find_int (ufbx.h) -> ufbx_find_int_len (ufbx.c:30680-30688).
        internal static long FindIntPublic(UfbxProps props, string name, long def)
        {
            UfbxProps owner;
            int index;
            if (TryFindPropLen(props, name, out owner, out index)) return owner.Props[index].ValueInt;
            return def;
        }

        // ==================================================================
        // Thumbnail / SceneInfo / header extension
        // ==================================================================

        // C: ufbxi_read_thumbnail (ufbx.c:11930-11963).
        internal static void ReadThumbnail(UfbxiContext uc, UfbxiNode node, UfbxThumbnail thumbnail)
        {
            ReadProperties(uc, node, thumbnail.Props);

            long customWidth = FindIntPublic(thumbnail.Props, "CustomWidth", 0);
            long customHeight = FindIntPublic(thumbnail.Props, "CustomHeight", 0);

            UfbxiNode formatNode = node.FindChildStrCmp("Format");
            int format;
            if (formatNode != null && formatNode.GetValI(0, out format)) {
                if (format >= 0 && format + 1 < UfbxEnumCounts.UfbxThumbnailFormat) {
                    thumbnail.Format = (UfbxThumbnailFormat)(format + 1);
                }
            }

            UfbxiNode sizeNode = node.FindChild(UfbxiStrings.Size);
            int size;
            if (sizeNode != null && sizeNode.GetValI(0, out size)) {
                if (size > 0) {
                    thumbnail.Width = (uint)size;
                    thumbnail.Height = (uint)size;
                } else if (size < 0 && customWidth > 0 && customHeight > 0) {
                    thumbnail.Width = (uint)customWidth;
                    thumbnail.Height = (uint)customHeight;
                }
            }

            UfbxiValueArray dataArr = node.FindArray(UfbxiStrings.ImageData, 'c');
            if (dataArr != null) {
                // C: thumbnail->data = { data_arr->data, data_arr->size }.
                byte[] blob = new byte[dataArr.Size];
                if (blob.Length > 0) {
                    Array.Copy(dataArr.Data, dataArr.Offset, blob, 0, blob.Length);
                }
                thumbnail.Data = blob;
            }
        }

        // C: ufbxi_read_scene_info (ufbx.c:11965-11975).
        internal static void ReadSceneInfo(UfbxiContext uc, UfbxiNode node)
        {
            ReadProperties(uc, node, uc.Scene.Metadata.SceneProps);

            UfbxiNode thumbnail = node.FindChild(UfbxiStrings.Thumbnail);
            if (thumbnail != null) {
                ReadThumbnail(uc, thumbnail, uc.Scene.Metadata.Thumbnail);
            }
        }

        // C: ufbxi_read_header_extension (ufbx.c:11977-12029).
        internal static void ReadHeaderExtension(UfbxiContext uc)
        {
            bool hasTcDefinition = false;
            int tcDefinition = 0;
            int headerVersion = 0;

            for (;;) {
                UfbxiNode child = UfbxiRoot.ParseToplevelChild(uc);
                if (child == null) break;

                if (child.Name == UfbxiStrings.Creator) {
                    string creator;
                    if (child.GetValS(0, out creator)) uc.Scene.Metadata.Creator = creator;
                }

                if (uc.Version < 6000 && child.Name == UfbxiStrings.FBXVersion) {
                    int version;
                    if (child.GetValI(0, out version)) {
                        if (version > 0 && version < 6000 && (uint)version > uc.Version) {
                            uc.Version = (uint)version;
                        }
                    }
                }

                if (child.Name == UfbxiStrings.FBXHeaderVersion) {
                    int hv;
                    if (child.GetValI(0, out hv)) headerVersion = hv;
                }

                if (child.Name == UfbxiStrings.OtherFlags) {
                    UfbxiNode tcNode = child.FindChild(UfbxiStrings.TCDefinition);
                    int tc;
                    if (tcNode != null && tcNode.GetValI(0, out tc)) {
                        tcDefinition = tc;
                        hasTcDefinition = true;
                    }
                }

                if (child.Name == UfbxiStrings.SceneInfo) {
                    ReadSceneInfo(uc, child);
                }
            }

            // FBX 8000 changes the KTime units; the new units are opt-in via `TCDefinition`.
            bool useV7Ktime = uc.Version < 8000;
            if (headerVersion >= 1004 && hasTcDefinition) {
                useV7Ktime = tcDefinition == 127;
            }

            uc.KtimeSec = useV7Ktime ? 46186158000L : 141120000L;
            uc.KtimeSecDouble = (double)uc.KtimeSec;
        }

        // ==================================================================
        // Exporter detection
        // ==================================================================

        // C: ufbxi_match_version_string (ufbx.c:12031-12078).
        static bool MatchVersionString(string fmt, string str, uint[] pVersion)
        {
            int numIx = 0;
            int pos = 0;
            for (int fi = 0; fi < fmt.Length; fi++) {
                char c = fmt[fi];
                if (c >= 'a' && c <= 'z') {
                    if (pos >= str.Length) return false;
                    char s = str[pos];
                    if (s != c && (int)s + (int)('a' - 'A') != (int)c) return false;
                    pos++;
                } else if (c == ' ') {
                    while (pos < str.Length) {
                        char s = str[pos];
                        if (s != ' ' && s != '\t') break;
                        pos++;
                    }
                } else if (c == '-') {
                    while (pos < str.Length) {
                        char s = str[pos];
                        if (s == '-') break;
                        pos++;
                    }
                    if (pos >= str.Length) return false;
                    pos++;
                } else if (c == '/' || c == '.' || c == '(' || c == ')' || c == '_') {
                    if (pos >= str.Length) return false;
                    if (str[pos] != c) return false;
                    pos++;
                } else if (c == '?') {
                    uint num = 0;
                    int len = 0;
                    while (pos < str.Length) {
                        char s = str[pos];
                        if (!(s >= '0' && s <= '9')) break;
                        num = num * 10 + (uint)(s - '0');
                        pos++;
                        len++;
                    }
                    if (len == 0) return false;
                    pVersion[numIx++] = num;
                } else {
                    // C: ufbxi_unreachable("Unhandled match character")
                    UfbxiFail.FailNoDesc("Unhandled match character");
                }
            }

            return true;
        }

        // C: ufbx_pack_version (ufbx.h:260).
        internal static uint PackVersion(uint major, uint minor, uint patch)
        {
            return major * 1000000u + minor * 1000u + patch;
        }

        // C: ufbxi_match_exporter (ufbx.c:12080-12124).
        internal static void MatchExporter(UfbxiContext uc)
        {
            string creator = uc.Scene.Metadata.Creator;
            uint[] version = new uint[3];
            if (MatchVersionString("blender-- ?.?.?", creator, version)) {
                uc.Exporter = UfbxExporter.BlenderBinary;
                uc.ExporterVersion = PackVersion(version[0], version[1], version[2]);
            } else if (MatchVersionString("blender- ?.?", creator, version)) {
                uc.Exporter = UfbxExporter.BlenderBinary;
                uc.ExporterVersion = PackVersion(version[0], version[1], 0);
            } else if (MatchVersionString("blender version ?.?", creator, version)) {
                uc.Exporter = UfbxExporter.BlenderAscii;
                uc.ExporterVersion = PackVersion(version[0], version[1], 0);
            } else if (MatchVersionString("fbx sdk/fbx plugins version ?.?", creator, version)) {
                uc.Exporter = UfbxExporter.FbxSdk;
                uc.ExporterVersion = PackVersion(version[0], version[1], 0);
            } else if (MatchVersionString("fbx sdk/fbx plugins build ?", creator, version)) {
                uc.Exporter = UfbxExporter.FbxSdk;
                uc.ExporterVersion = PackVersion(version[0] / 10000u, version[0] / 100u % 100u, version[0] % 100u);
            } else if (MatchVersionString("motionbuilder version ?.?", creator, version)) {
                uc.Exporter = UfbxExporter.MotionBuilder;
                uc.ExporterVersion = PackVersion(version[0], version[1], 0);
            } else if (MatchVersionString("motionbuilder/mocap/online version ?.?", creator, version)) {
                uc.Exporter = UfbxExporter.MotionBuilder;
                uc.ExporterVersion = PackVersion(version[0], version[1], 0);
            } else if (MatchVersionString("ufbx_write", creator, version)) {
                uc.Exporter = UfbxExporter.UfbxWrite;
                uc.ExporterVersion = PackVersion(0, 0, 1);
            }

            uc.Scene.Metadata.Exporter = uc.Exporter;
            uc.Scene.Metadata.ExporterVersion = uc.ExporterVersion;

            // Un-detect the exporter in `ufbxi_context` to disable special cases.
            if (uc.Opts.DisableQuirks) {
                uc.Exporter = UfbxExporter.Unknown;
                uc.ExporterVersion = 0;
            }

            if (uc.Exporter == UfbxExporter.BlenderBinary) {
                uc.BlenderFullWeights = true;
            }
        }

        // ==================================================================
        // Document / Definitions / Templates
        // ==================================================================

        // C: ufbxi_read_document (ufbx.c:12126-12145).
        internal static void ReadDocument(UfbxiContext uc)
        {
            bool foundRootId = false;

            for (;;) {
                UfbxiNode child = UfbxiRoot.ParseToplevelChild(uc);
                if (child == null) break;

                if (child.Name == UfbxiStrings.Document && !foundRootId) {
                    // Post-7000: Try to find the first document node and root ID.
                    UfbxiNode rootNode = child.FindChild(UfbxiStrings.RootNode);
                    long rootId;
                    if (rootNode != null && rootNode.GetValL(0, out rootId)) {
                        uc.RootId = unchecked((ulong)rootId);
                        foundRootId = true;
                    }
                }
            }
        }

        // C: ufbxi_read_definitions (ufbx.c:12147-12189).
        internal static void ReadDefinitions(UfbxiContext uc)
        {
            // C: `ufbxi_template *tmpl = ufbxi_push_zero(&uc->tmp_stack, ufbxi_template, 1)`
            // followed by `ufbxi_push_pop(&uc->result, &uc->tmp_stack, ..., num_templates)`.
            System.Collections.Generic.List<UfbxiTemplate> tmplStack =
                new System.Collections.Generic.List<UfbxiTemplate>();

            for (;;) {
                UfbxiNode obj = UfbxiRoot.ParseToplevelChild(uc);
                if (obj == null) break;

                if (obj.Name != UfbxiStrings.ObjectType) continue;

                UfbxiTemplate tmpl = new UfbxiTemplate();
                // C: `ufbxi_push_zero(&uc->tmp_stack, ufbxi_template, 1)` — `props` is an
                // embedded zeroed `ufbx_props`, never NULL.
                tmpl.Props = new UfbxProps();
                uc.NumTemplates++;
                tmplStack.Add(tmpl);

                string type;
                UfbxiFail.CheckNoDesc(obj.GetValC(0, out type), "ufbxi_get_val1(object, \"C\", (char**)&tmpl->type)");
                tmpl.Type = type;

                // Pre-7000 FBX versions don't have property templates.
                UfbxiNode props = obj.FindChild(UfbxiStrings.PropertyTemplate);
                if (props != null) {
                    string subType;
                    UfbxiFail.CheckNoDesc(props.GetValS(0, out subType), "ufbxi_get_val1(props, \"S\", &tmpl->sub_type)");

                    // Remove the "Fbx" prefix from sub-types, remember to re-intern!
                    if (subType.Length > 3 && string.CompareOrdinal(subType, 0, "Fbx", 0, 3) == 0) {
                        string sub = subType.Substring(3);

                        // HACK: LOD groups use LODGroup for Template, LodGroup for Object?
                        if (sub.Length == 8 && string.CompareOrdinal(sub, 0, "LODGroup", 0, 8) == 0) {
                            sub = "LodGroup";
                        }

                        int outLength;
                        string interned = uc.StringPool.PushString(sub, 0, sub.Length, out outLength, false);
                        UfbxiFail.CheckNoDesc(interned != null, "ufbxi_push_string_place_str(&uc->string_pool, &tmpl->sub_type, false)");
                        subType = interned;
                    }
                    tmpl.SubType = subType;

                    ReadProperties(uc, props, tmpl.Props);
                } else {
                    // C: tmpl->sub_type = ufbx_empty_string; props stay zeroed (no defaults).
                    tmpl.SubType = null;
                }
            }

            // C: uc->templates = ufbxi_push_pop(&uc->result, &uc->tmp_stack, ufbxi_template, ...)
            uc.Templates = tmplStack.ToArray();
        }

        // C: ufbxi_find_template (ufbx.c:12191-12214).
        internal static UfbxProps FindTemplate(UfbxiContext uc, string name, string subType)
        {
            UfbxiTemplate[] templates = uc.Templates;
            if (templates == null) return null;

            for (int i = 0; i < templates.Length; i++) {
                UfbxiTemplate tmpl = templates[i];
                if (tmpl.Type == name) {
                    // Check that sub_type matches unless the type is Material, Model, AnimationStack,
                    // AnimationLayer. Those match to all sub-types.
                    if (tmpl.Type != UfbxiStrings.Material && tmpl.Type != UfbxiStrings.Model
                        && tmpl.Type != UfbxiStrings.AnimationStack && tmpl.Type != UfbxiStrings.AnimationLayer) {
                        if (tmpl.SubType != subType) {
                            return null;
                        }
                    }

                    if (tmpl.Props.Props != null && tmpl.Props.Props.Length > 0) {
                        return tmpl.Props;
                    } else {
                        return null;
                    }
                }
            }
            return null;
        }

        // C: ufbxi_set_own_prop_vec3_uniform (ufbx.c:12489-12501). `props->defaults` is
        // temporarily cleared (the local struct shares the item array), so only the top level
        // is searched and the found prop is modified in place.
        internal static void SetOwnPropVec3Uniform(UfbxProps props, string name, double value)
        {
            int index = FindPropIndex(props, name, FindPropKey(name), false, out UfbxProps owner);
            if (index >= 0) {
                UfbxProp prop = owner.Props[index];
                prop.ValueVec4 = new UfbxVec4(value, value, value, 0.0);
                prop.ValueInt = (long)value;
                owner.Props[index] = prop;
            }
        }
    }
}
