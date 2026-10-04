// FBX object ID bookkeeping, ported from ufbx v0.23.1 ufbx.c:
//   UFBXI_MAXIMUM_FAST_POINTER_ID / UFBXI_POINTER_ID_START (12216-12223)
//   ufbxi_push_synthetic_id          (12225-12228)
//   ufbxi_synthetic_id_from_ptr_id   (12230-12244)
//   ufbxi_synthetic_id_from_string   (12246-12254)
//   ufbxi_validate_fbx_id            (12256-12265)
//   ufbxi_split_type_and_name        (12267-12301)
//   ufbxi_insert_fbx_id              (12303-12319)
//   ufbxi_find_fbx_id                (12321-12325)
//   ufbxi_fbx_id_exists              (12327-12330)
//   ufbxi_insert_fbx_attr            (12332-12346)
//
// The `fbx_id_map` / `ptr_fbx_id_map` / `fbx_attr_map` maps are created by
// `UfbxiContext.S1InitMaps()` (the port's stand-in for the `ufbxi_map_init` block of
// `ufbxi_load()`, ufbx.c:25557-25561); `synthetic_id_counter` is `SyntheticIdCounter`.
using System;

namespace Ufbx
{
    internal static class UfbxiFbxId
    {
        // C: UFBXI_MAXIMUM_FAST_POINTER_ID -- non-regression value (ufbx.c:12217-12221).
        internal const ulong MaximumFastPointerId = 0x4000000000000000ul;

        // C: UFBXI_POINTER_ID_START (ufbx.c:12222).
        internal const ulong PointerIdStart = 0x8000000000000000ul;

        // C: ufbxi_push_synthetic_id (ufbx.c:12225-12228).
        internal static ulong PushSyntheticId(UfbxiContext uc)
        {
            return ++uc.SyntheticIdCounter;
        }

        // C: ufbxi_synthetic_id_from_ptr_id (ufbx.c:12230-12244).
        internal static ulong SyntheticIdFromPtrId(UfbxiContext uc, ulong ptr, ulong id)
        {
            UfbxiPtrId ptrId = new UfbxiPtrId { Ptr = ptr, Id = id };
            uint hash = UfbxiHash.HashPtrId(ptrId);
            int index = uc.PtrFbxIdMap.Find(hash, ptrId);

            if (index < 0) {
                index = uc.PtrFbxIdMap.Insert(hash, ptrId);
                // C: ufbxi_check_return(entry, 0)
                UfbxiFail.CheckNoDesc(index >= 0,
                    "ufbxi_map_insert(&uc->ptr_fbx_id_map, ufbxi_ptr_fbx_id_entry, hash, &ptr_id)");
                UfbxiPtrFbxIdEntry entry = uc.PtrFbxIdMap.Items[index];
                entry.PtrId = ptrId;
                entry.FbxId = PushSyntheticId(uc);
                uc.PtrFbxIdMap.Items[index] = entry;
            }

            return uc.PtrFbxIdMap.Items[index].FbxId;
        }

        // C: ufbxi_synthetic_id_from_string (ufbx.c:12246-12254). C compares the raw pointer
        // against `UINTPTR_MAX < MAXIMUM_FAST_POINTER_ID ? UINTPTR_MAX : MAXIMUM_FAST_POINTER_ID`;
        // on the 64-bit golden build that is just `MAXIMUM_FAST_POINTER_ID`.
        internal static ulong SyntheticIdFromString(UfbxiContext uc, string str)
        {
            ulong uptr = UfbxiPtrIdTable.IdOf(str);
            ulong limit = ulong.MaxValue < MaximumFastPointerId ? ulong.MaxValue : MaximumFastPointerId;
            if (uptr < limit) {
                return uptr;
            } else {
                return SyntheticIdFromPtrId(uc, uptr, 0);
            }
        }

        // C: ufbxi_validate_fbx_id (ufbx.c:12256-12265).
        internal static ulong ValidateFbxId(UfbxiContext uc, ulong fbxId)
        {
            if (fbxId >= PointerIdStart) {
                fbxId = SyntheticIdFromPtrId(uc, 0, fbxId);
                UfbxiFail.CheckNoDesc(fbxId != 0, "fbx_id");
            }
            return fbxId;
        }

        // C: ufbxi_split_type_and_name (ufbx.c:12267-12301).
        internal static void SplitTypeAndName(UfbxiContext uc, string typeAndName, out string type, out string name)
        {
            // Name and type are packed in a single property as Type::Name (in ASCII)
            // or Name\x00\x01Type (in binary)
            string sep = uc.FromAscii ? "::" : "\x00\x01";
            int typeEnd = 2;
            for (; typeEnd <= typeAndName.Length; typeEnd++) {
                char ch0 = typeAndName[typeEnd - 2];
                char ch1 = typeAndName[typeEnd - 1];
                if (ch0 == sep[0] && ch1 == sep[1]) break;
            }

            // ???: ASCII and binary store type and name in different order
            if (typeEnd <= typeAndName.Length) {
                if (uc.FromAscii) {
                    name = typeAndName.Substring(typeEnd);
                    type = typeAndName.Substring(0, typeEnd - 2);
                } else {
                    name = typeAndName.Substring(0, typeEnd - 2);
                    type = typeAndName.Substring(typeEnd);
                }
            } else {
                name = typeAndName;
                type = string.Empty;
            }

            type = PushStringPlaceStr(uc, type);
            name = PushStringPlaceStr(uc, name);
        }

        // C: ufbxi_push_string_place_str(&uc->string_pool, &str, false) (ufbx.c:5257-5261).
        // The port's `ufbx_string` carries its own length, so the pooled substring replaces it.
        static string PushStringPlaceStr(UfbxiContext uc, string str)
        {
            UfbxiFail.CheckNoDesc(str != null, "p_str");
            int outLength;
            string interned = uc.StringPool.PushString(str, 0, str.Length, out outLength, false);
            UfbxiFail.CheckNoDesc(interned != null, "ufbxi_push_string_place_str(&uc->string_pool, &str, false)");
            return interned;
        }

        // C: ufbxi_insert_fbx_id (ufbx.c:12303-12319).
        internal static void InsertFbxId(UfbxiContext uc, ulong fbxId, uint elementId)
        {
            uint hash = UfbxiHash.Hash64(fbxId);
            int index = uc.FbxIdMap.Find(hash, fbxId);

            if (index < 0) {
                index = uc.FbxIdMap.Insert(hash, fbxId);
                UfbxiFail.CheckNoDesc(index >= 0,
                    "ufbxi_map_insert(&uc->fbx_id_map, ufbxi_fbx_id_entry, hash, &fbx_id)");
                UfbxiFbxIdEntry entry = uc.FbxIdMap.Items[index];
                entry.FbxId = fbxId;
                entry.ElementId = elementId;
                entry.UserId = 0;
                uc.FbxIdMap.Items[index] = entry;
            } else {
                UfbxiFail.CheckNoDesc(
                    UfbxiWarnings.Warnf(uc.Warnings, UfbxWarningType.DuplicateObjectId,
                        UfbxiWarnings.NoElementId, "Duplicate object ID"),
                    "ufbxi_warnf(UFBX_WARNING_DUPLICATE_OBJECT_ID, \"Duplicate object ID\")");
            }
        }

        // C: ufbxi_find_fbx_id (ufbx.c:12321-12325). Returns the map item index, -1 == NULL.
        internal static int FindFbxId(UfbxiContext uc, ulong fbxId)
        {
            uint hash = UfbxiHash.Hash64(fbxId);
            return uc.FbxIdMap.Find(hash, fbxId);
        }

        // C: ufbxi_fbx_id_exists (ufbx.c:12327-12330).
        internal static bool FbxIdExists(UfbxiContext uc, ulong fbxId)
        {
            return FindFbxId(uc, fbxId) >= 0;
        }

        // C: ufbxi_insert_fbx_attr (ufbx.c:12332-12346).
        internal static void InsertFbxAttr(UfbxiContext uc, ulong fbxId, ulong attribFbxId)
        {
            uint hash = UfbxiHash.Hash64(fbxId);
            int index = uc.FbxAttrMap.Find(hash, fbxId);
            // TODO: Strict / warn about duplicate objects

            if (index < 0) {
                index = uc.FbxAttrMap.Insert(hash, fbxId);
                UfbxiFail.CheckNoDesc(index >= 0,
                    "ufbxi_map_insert(&uc->fbx_attr_map, ufbxi_fbx_attr_entry, hash, &fbx_id)");
                UfbxiFbxAttrEntry entry = uc.FbxAttrMap.Items[index];
                entry.NodeFbxId = fbxId;
                entry.AttrFbxId = attribFbxId;
                uc.FbxAttrMap.Items[index] = entry;
            }
        }
    }
}
