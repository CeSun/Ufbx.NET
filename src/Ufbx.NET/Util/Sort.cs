using System;

namespace Ufbx.NET
{
    // Ported from ufbx.c v0.23.1 sorting helpers (ufbx.c:1231-1347).
    //
    // The exact comparison/swap sequence decides the order of equal elements in the scene
    // output, so these reproduce C's algorithms rather than substituting Array.Sort:
    //   * `ufbxi_stable_sort` — blocked insertion sort + bottom-up merge sort ping-ponging
    //     between `data` and `tmp` (ufbx.c:1233-1291).
    //   * `ufbxi_unstable_sort` — in-place heapsort (build + extract-max) (ufbx.c:1318-1347).

    // C: typedef bool ufbxi_less_fn(void *user, const void *a, const void *b) (ufbx.c:1231).
    // `user` is C's `less_user` callback context, kept as an explicit parameter.
    internal delegate bool UfbxiLessFn<T>(object user, T a, T b);

    internal static class UfbxiSort
    {
        // C: ufbxi_stable_sort (ufbx.c:1233-1291).
        // `stride` is carried by the element type `T`; `data` and `tmp` must both hold at
        // least `size` items (C: "m_tmp must be a memory buffer with at least the same size
        // and alignment as m_data", ufbx.c:1141).
        internal static void StableSort<T>(int linearSize, T[] data, T[] tmp, int size, UfbxiLessFn<T> lessFn, object lessUser)
        {
            T[] src = tmp;
            T[] dst = data;
            int blockSize = linearSize;

            // Insertion sort in `linear_size` blocks
            for (int basis = 0; basis < size; basis += blockSize) {
                int iEnd = basis + blockSize;
                if (iEnd > size) iEnd = size;
                for (int i = basis + 1; i < iEnd; i++) {
                    // C: a = dst + i*stride, b = dst + (i-1)*stride
                    if (!lessFn(lessUser, dst[i], dst[i - 1])) continue;

                    int j = i - 1;
                    // C: memcpy(src, dst + i*stride, stride); the tmp buffer doubles as the
                    // single-element scratch slot (ufbx.c:1252).
                    Array.Copy(dst, i, src, 0, 1);
                    Array.Copy(dst, j, dst, i, 1);
                    for (; j != basis; --j) {
                        // C: a = src, b = dst + (j-1)*stride
                        if (!lessFn(lessUser, src[0], dst[j - 1])) break;
                        Array.Copy(dst, j - 1, dst, j, 1);
                    }
                    Array.Copy(src, 0, dst, j, 1);
                }
            }

            // Merge sort ping-ponging between `data` and `tmp`
            for (; blockSize < size; blockSize *= 2) {
                T[] swap = dst; dst = src; src = swap;
                for (int basis = 0; basis < size; basis += blockSize * 2) {
                    int i = basis, iEnd = basis + blockSize;
                    int j = iEnd, jEnd = j + blockSize;
                    int k = basis;
                    if (iEnd > size) iEnd = size;
                    if (jEnd > size) jEnd = size;
                    // C uses `&` here (ufbx.c:1271); same result for bools.
                    while (i < iEnd && j < jEnd) {
                        // C: a = src + j*stride, b = src + i*stride — note the inverted order:
                        // `less(j, i)` takes from `j` when the right run's head is smaller.
                        if (lessFn(lessUser, src[j], src[i])) {
                            Array.Copy(src, j, dst, k, 1);
                            j++;
                        } else {
                            Array.Copy(src, i, dst, k, 1);
                            i++;
                        }
                        k++;
                    }

                    Array.Copy(src, i, dst, k, iEnd - i);
                    if (j < jEnd) {
                        Array.Copy(src, j, dst, k + (iEnd - i), jEnd - j);
                    }
                }
            }

            // Copy the result to `data` if we ended up in `tmp`
            if (!ReferenceEquals(dst, data)) {
                Array.Copy(dst, 0, data, 0, size);
            }
        }

        // C: ufbxi_swap (ufbx.c:1293-1316). The UFBXI_HAS_ALIASING path exchanges 32-bit
        // words (ufbx.c:1295-1303), which is byte-identical to swapping the whole element.
        internal static void Swap<T>(T[] data, int indexA, int indexB)
        {
            T tmp = data[indexA];
            data[indexA] = data[indexB];
            data[indexB] = tmp;
        }

        // C: ufbxi_unstable_sort (ufbx.c:1318-1347): `size` elements, heapsort with the
        // sift-down inline in the outer loop (start/end walk).
        internal static void UnstableSort<T>(T[] data, int size, UfbxiLessFn<T> lessFn, object lessUser)
        {
            if (size <= 1) return;

            int start = (size - 1) >> 1;
            int end = size - 1;
            for (;;) {
                int root = start;
                int child;
                while ((child = root * 2 + 1) <= end) {
                    int next = lessFn(lessUser, data[child], data[root]) ? root : child;
                    if (child + 1 <= end && lessFn(lessUser, data[next], data[child + 1])) {
                        next = child + 1;
                    }
                    if (next == root) break;
                    Swap(data, root, next);
                    root = next;
                }

                if (start > 0) {
                    start--;
                } else if (end > 0) {
                    Swap(data, end, 0);
                    end--;
                } else {
                    break;
                }
            }
        }
    }
}
