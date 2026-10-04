using System;
using System.Collections.Generic;

namespace Ufbx
{
    // Ported from ufbx.c v0.23.1 "Hash map" (ufbx.c:4363-4707).
    //
    // This is ufbx's own open-addressed Robin Hood table, NOT a dictionary: the probe
    // sequence, the grow/rehash order and the AA-tree overflow buckets decide which order
    // items are handed back to the scene builder, so the structure is reproduced literally
    // (PORTING_NOTES.md #4: the arena C uses for items/entries/AA nodes becomes arrays).
    //
    // Items live in `Items` at their insertion index (C: `(char*)map->items + index * item_size`);
    // `Entries[i]` is C's packed word `(uint64_t)index << 32 | (hash & ~mask) | scan`.

    // C: ufbxi_aa_node (ufbx.c:4375-4379). `left`/`right` pointers become indices into
    // UfbxiMap.AaNodes with -1 == NULL; nodes are appended in the same order C pushes them
    // into `map->aa_buf`, so the tree shape matches.
    internal sealed class UfbxiAaNode
    {
        public const int Null = -1;

        public int Left = Null;   // C: left
        public int Right = Null;  // C: right
        public uint Level = 1;    // C: level
        public uint Index;        // C: index — _item_ index, not node index
    }

    // C: ufbxi_ptr_id (ufbx.c:4695-4698) — `uintptr_t ptr; uint64_t id;`.
    // MAPPING OF C POINTERS: values C uses as `const char*`/`void*` keys get a stable
    // integer id (UfbxiStringPool.PtrIdOf for pooled strings: monotonically increasing from
    // 1 in interning order, which is C's arena push order, with the `ufbxi_*` static
    // constants interned first in their ufbx.c:5280-5582 declaration order). `Id` is C's
    // unrelated `uint64_t id` (synthetic object ids, ufbx.c:12222-12240).
    // NOTE: C compares raw addresses, so the relative order of a .rodata constant and a
    // heap string is unspecified in C itself; the port fixes it as "constants first".
    internal struct UfbxiPtrId
    {
        public ulong Ptr;   // C: ptr
        public ulong Id;    // C: id
    }

    // C: typedef int ufbxi_cmp_fn(void *user, const void *a, const void *b) (ufbx.c:4373).
    // `a` is the key being looked up, `b` is `&items[index]`. C reuses one cmp_fn for both
    // by having the key alias the first field of the item; KeyFromItem makes that explicit.
    internal interface IUfbxiMapCmp<TItem, TKey>
    {
        int Compare(TKey a, TItem b);
        TKey KeyFromItem(TItem item);
    }

    // Generic stand-in for a C comparator function pointer: item→key projection plus the
    // comparison of two keys.
    internal sealed class UfbxiMapCmp<TItem, TKey> : IUfbxiMapCmp<TItem, TKey>
    {
        readonly Func<TItem, TKey> _keyOfItem;
        readonly Func<TKey, TKey, int> _compare;

        public UfbxiMapCmp(Func<TItem, TKey> keyOfItem, Func<TKey, TKey, int> compare)
        {
            _keyOfItem = keyOfItem;
            _compare = compare;
        }

        public int Compare(TKey a, TItem b)
        {
            return _compare(a, _keyOfItem(b));
        }

        public TKey KeyFromItem(TItem item)
        {
            return _keyOfItem(item);
        }
    }

    internal sealed class UfbxiMap<TItem, TKey>
    {
        // C: UFBXI_MAP_MAX_SCAN (ufbx.c:55). UFBX_REGRESSION lowers it to 2 (ufbx.c:1004-1005),
        // which is not the golden build.
        public const int MapMaxScan = 32;

        // C: map->ator->error — the sink of the `ufbxi_check_return_err` in grow (ufbx.c:4522).
        public UfbxError Error;

        public TItem[] Items;              // C: void *items
        public ulong[] Entries;            // C: uint64_t *entries
        public uint Mask;                  // C: uint32_t mask
        public uint Size;                  // C: uint32_t size
        public uint Capacity;              // C: uint32_t capacity
        public long DataSize;              // C: size_t data_size (allocation bookkeeping only)

        // C: sizeof(type) passed to the ufbxi_map_* macros. It only feeds C's byte-size
        // arithmetic (ufbx.c:4526-4527), which the managed arrays replace; kept so the
        // overflow checks read like the original.
        public int ItemSize;

        public readonly IUfbxiMapCmp<TItem, TKey> Cmp;  // C: cmp_fn / cmp_user

        public List<UfbxiAaNode> AaNodes;         // C: ufbxi_buf aa_buf
        public int AaRoot = UfbxiAaNode.Null;     // C: ufbxi_aa_node *aa_root

        // C: ufbxi_map_init (ufbx.c:4400-4426). The UFBX_REGRESSION private allocator
        // (ufbx.c:4403-4420) is not part of the golden build.
        public UfbxiMap(IUfbxiMapCmp<TItem, TKey> cmp, int itemSize, UfbxError error)
        {
            Cmp = cmp;
            ItemSize = itemSize;
            Error = error;
            AaNodes = new List<UfbxiAaNode>();
        }

        // C: ufbxi_map_free (ufbx.c:4428-4447).
        public void Free()
        {
            AaNodes = null;
            Entries = null;
            Items = null;
            AaRoot = UfbxiAaNode.Null;
            Mask = Capacity = Size = 0;
            DataSize = 0;
        }

        // C: ufbxi_map_grow_size_imp (ufbx.c:4507-4581).
        bool GrowSizeImp(uint minSize)
        {
            // C: ufbx_assert(min_size > 0)
            const double LoadFactor = 0.7;

            // Find the lowest power of two size that fits `min_size` within `load_factor`
            ulong numEntries = (ulong)Mask + 1ul;
            ulong newSize = unchecked((ulong)((double)numEntries * LoadFactor));
            if (minSize < Capacity + 1) minSize = Capacity + 1;
            while (newSize < minSize) {
                numEntries *= 2;
                newSize = unchecked((ulong)((double)numEntries * LoadFactor));
            }

            // Check for overflow
            // C: ufbxi_check_return_err(err, SIZE_MAX / num_entries > sizeof(uint64_t), false)
            if (ulong.MaxValue / numEntries <= 8ul) return false;
            ulong allocSize = numEntries * 8ul;

            // C: ufbxi_check_return_err(err, (SIZE_MAX - alloc_size) / new_size > item_size, false)
            if (newSize == 0) return false;
            if ((ulong.MaxValue - allocSize) / newSize <= (ulong)ItemSize) return false;
            ulong dataSize = allocSize + newSize * (ulong)ItemSize;

            // C allocates one combined `data_size` byte block and splits it into entries and
            // items; the port uses two arrays, so only the element counts need range checks.
            if (numEntries > (ulong)int.MaxValue || newSize > (ulong)int.MaxValue) return false;

            ulong[] oldEntries = Entries;
            ulong[] newEntries = new ulong[(int)numEntries];
            TItem[] newItems = new TItem[(int)newSize];

            // Copy the previous user items over
            // C: if (map->size > 0) memcpy(new_items, map->items, item_size * map->size)
            if (Size > 0) {
                Array.Copy(Items, 0, newItems, 0, (int)Size);
            }

            // Re-hash the entries
            uint oldMask = Mask;
            uint newMask = unchecked((uint)numEntries) - 1;
            if (oldMask != 0) {
                for (uint i = 0; i <= oldMask; i++) {
                    ulong entry, newEntry = oldEntries[i];
                    if (newEntry == 0) continue;

                    // Reconstruct the hash of the old entry at `i`
                    uint oldScan = unchecked((uint)(newEntry & (ulong)oldMask)) - 1;
                    uint hash = unchecked((uint)newEntry & ~oldMask) | ((i - oldScan) & oldMask);
                    uint slot = hash & newMask;
                    newEntry &= ~(ulong)newMask;

                    // Scan forward until we find an empty slot, potentially swapping
                    // `new_element` if it has a shorter scan distance (Robin Hood).
                    uint scan = 1;
                    while ((entry = newEntries[slot]) != 0) {
                        uint entryScan = unchecked((uint)(entry & (ulong)newMask));
                        if (entryScan < scan) {
                            newEntries[slot] = newEntry + scan;
                            newEntry = entry & ~(ulong)newMask;
                            scan = entryScan;
                        }
                        scan += 1;
                        slot = (slot + 1) & newMask;
                    }
                    newEntries[slot] = newEntry + scan;
                }
            }

            // And finally free the previous allocation (left to the GC)
            Items = newItems;
            DataSize = unchecked((long)dataSize);
            Entries = newEntries;
            Mask = newMask;
            Capacity = unchecked((uint)newSize);

            return true;
        }

        // C: ufbxi_map_grow_size (ufbx.c:4583-4595); the regression-mode allocation-limit
        // check (ufbx.c:4585-4591) is not in the golden build.
        bool GrowSize(uint minSize)
        {
            if (Size < Capacity && Capacity >= minSize) return true;
            return GrowSizeImp(minSize);
        }

        // C: #define ufbxi_map_grow(map, type, min_size) (ufbx.c:4664).
        public bool Grow(uint minSize)
        {
            return GrowSize(minSize);
        }

        // C: ufbxi_aa_tree_insert (ufbx.c:4450-4488).
        // Plain recursion: the ufbxi_recursive_function depth check (ufbx.c:1052-1080)
        // compiles away outside regression/analysis builds. C returns NULL if the AA node
        // push fails; managed allocation has no such failure here.
        int AaTreeInsert(int node, TKey value, uint index)
        {
            if (node == UfbxiAaNode.Null) {
                UfbxiAaNode newNode = new UfbxiAaNode();
                newNode.Left = UfbxiAaNode.Null;
                newNode.Right = UfbxiAaNode.Null;
                newNode.Level = 1;
                newNode.Index = index;
                AaNodes.Add(newNode);
                return AaNodes.Count - 1;
            }

            UfbxiAaNode n = AaNodes[node];
            TItem entry = Items[n.Index];
            int cmp = Cmp.Compare(value, entry);
            if (cmp < 0) {
                n.Left = AaTreeInsert(n.Left, value, index);
            } else if (cmp >= 0) {
                n.Right = AaTreeInsert(n.Right, value, index);
            }

            if (n.Left != UfbxiAaNode.Null && AaNodes[n.Left].Level == n.Level) {
                int left = n.Left;
                n.Left = AaNodes[left].Right;
                AaNodes[left].Right = node;
                node = left;
            }

            UfbxiAaNode cur = AaNodes[node];
            if (cur.Right != UfbxiAaNode.Null && AaNodes[cur.Right].Right != UfbxiAaNode.Null
                && AaNodes[AaNodes[cur.Right].Right].Level == cur.Level) {
                int right = cur.Right;
                cur.Right = AaNodes[right].Left;
                AaNodes[right].Left = node;
                AaNodes[right].Level += 1;
                node = right;
            }

            return node;
        }

        // C: ufbxi_aa_tree_find (ufbx.c:4490-4505). Returns the item index, -1 == NULL.
        int AaTreeFind(TKey value)
        {
            int node = AaRoot;
            while (node != UfbxiAaNode.Null) {
                UfbxiAaNode n = AaNodes[node];
                TItem entry = Items[n.Index];
                int cmp = Cmp.Compare(value, entry);
                if (cmp < 0) {
                    node = n.Left;
                } else if (cmp > 0) {
                    node = n.Right;
                } else {
                    return (int)n.Index;
                }
            }
            return -1;
        }

        // C: ufbxi_map_find_size (ufbx.c:4597-4624), see also #define ufbxi_map_find
        // (ufbx.c:4665). Returns the item index, -1 == the C NULL pointer.
        public int Find(uint hash, TKey value)
        {
            ulong[] entries = Entries;
            uint mask = Mask;
            uint scan = 0;

            uint refBits = hash & ~mask;
            if (mask == 0 || scan == uint.MaxValue) return -1;

            // Scan entries until we find an exact match of the hash or until we hit
            // an element that has lower scan distance than our search (Robin Hood).
            // The encoding guarantees that zero slots also terminate with the same test.
            for (;;) {
                ulong entry = entries[(hash + scan) & mask];
                scan += 1;
                if (unchecked((uint)entry) == refBits + scan) {
                    uint index = unchecked((uint)(entry >> 32));
                    TItem data = Items[index];
                    int cmp = Cmp.Compare(value, data);
                    if (cmp == 0) return (int)index;
                } else if ((entry & (ulong)mask) < scan) {
                    if (AaRoot != UfbxiAaNode.Null) {
                        return AaTreeFind(value);
                    } else {
                        return -1;
                    }
                }
            }
        }

        // C: ufbxi_map_insert_size (ufbx.c:4626-4662), see also #define ufbxi_map_insert
        // (ufbx.c:4666). Returns the index of the slot the caller must fill in (C hands out a
        // pointer to the still-uninitialized item), -1 on grow failure.
        public int Insert(uint hash, TKey value)
        {
            // C: ufbxi_map_grow_size(map, size, 64) — min_size is hardcoded to 64 here.
            if (!GrowSize(64)) return -1;

            uint index = Size++;

            ulong[] entries = Entries;
            uint mask = Mask;

            // Scan forward until we find an empty slot, potentially swapping
            // `new_element` if it has a shorter scan distance (Robin Hood).
            uint slot = hash & mask;
            ulong entry;
            ulong newEntry = (ulong)index << 32 | (ulong)(hash & ~mask);
            uint scan = 1;
            while ((entry = entries[slot]) != 0) {
                uint entryScan = unchecked((uint)(entry & (ulong)mask));
                if (entryScan < scan) {
                    entries[slot] = newEntry + scan;
                    newEntry = entry & ~(ulong)mask;
                    scan = entryScan;
                }
                scan += 1;
                slot = (slot + 1) & mask;

                if (scan > MapMaxScan) {
                    uint newIndex = unchecked((uint)(newEntry >> 32));
                    // C: new_index == index ? value : (const void*)((char*)map->items + size * new_index)
                    TKey treeValue = newIndex == index ? value : Cmp.KeyFromItem(Items[newIndex]);
                    AaRoot = AaTreeInsert(AaRoot, treeValue, newIndex);
                    return (int)index;
                }
            }
            entries[slot] = newEntry + scan;

            return (int)index;
        }
    }

    // C: the `ufbxi_map_cmp_*` comparators (ufbx.c:4668-4707) and ufbxi_map_cmp_string
    // (ufbx.c:5023-5028) expressed over the ported key/item types.
    internal static class UfbxiMapCmps
    {
        // C: ufbxi_map_cmp_uint64 (ufbx.c:4668-4675).
        public static int CmpUInt64(ulong a, ulong b)
        {
            if (a < b) return -1;
            if (a > b) return +1;
            return 0;
        }

        // C: ufbxi_map_cmp_const_char_ptr (ufbx.c:4677-4684) — C orders the interned string
        // _addresses_; the port orders them by pool-assigned pointer id (see UfbxiPtrId).
        public static int CmpConstCharPtr(ulong a, ulong b)
        {
            if (a < b) return -1;
            if (a > b) return +1;
            return 0;
        }

        // C: ufbxi_map_cmp_uintptr (ufbx.c:4686-4693) — same integer-id mapping.
        public static int CmpUIntPtr(ulong a, ulong b)
        {
            if (a < b) return -1;
            if (a > b) return +1;
            return 0;
        }

        // C: ufbxi_map_cmp_ptr_id (ufbx.c:4700-4707).
        public static int CmpPtrId(UfbxiPtrId a, UfbxiPtrId b)
        {
            if (a.Id != b.Id) return a.Id < b.Id ? -1 : +1;
            if (a.Ptr != b.Ptr) return a.Ptr < b.Ptr ? -1 : +1;
            return 0;
        }

        // C: ufbxi_map_cmp_string (ufbx.c:5023-5028) via ufbxi_str_cmp.
        public static int CmpString(string a, string b)
        {
            return UfbxiStr.Cmp(a, b);
        }

        // C: ufbxi_map_cmp_uint64 over an item whose first field is the uint64_t key.
        public static UfbxiMapCmp<TItem, ulong> UInt64<TItem>(Func<TItem, ulong> keyOfItem)
        {
            return new UfbxiMapCmp<TItem, ulong>(keyOfItem, CmpUInt64);
        }

        // C: ufbxi_map_cmp_const_char_ptr over an item whose first field is the pointer key.
        public static UfbxiMapCmp<TItem, ulong> ConstCharPtr<TItem>(Func<TItem, ulong> keyOfItem)
        {
            return new UfbxiMapCmp<TItem, ulong>(keyOfItem, CmpConstCharPtr);
        }

        // C: ufbxi_map_cmp_uintptr over an item whose first field is the pointer key.
        public static UfbxiMapCmp<TItem, ulong> UIntPtr<TItem>(Func<TItem, ulong> keyOfItem)
        {
            return new UfbxiMapCmp<TItem, ulong>(keyOfItem, CmpUIntPtr);
        }

        // C: ufbxi_map_cmp_ptr_id over an item whose first field is `ufbxi_ptr_id`.
        public static UfbxiMapCmp<TItem, UfbxiPtrId> PtrId<TItem>(Func<TItem, UfbxiPtrId> keyOfItem)
        {
            return new UfbxiMapCmp<TItem, UfbxiPtrId>(keyOfItem, CmpPtrId);
        }

        // C: ufbxi_map_cmp_string with `ufbx_string` items and keys — in this port a
        // `ufbx_string` _is_ a (raw-byte) string, so item and key are the same type.
        public static readonly UfbxiMapCmp<string, string> String =
            new UfbxiMapCmp<string, string>(v => v, CmpString);
    }
}
