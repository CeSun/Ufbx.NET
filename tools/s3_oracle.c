// C reference oracle for the S3a scene-build helpers of the ufbx -> C# port.
//
// Scope: the pure / hash-sensitive helpers of Parse/SceneBuild.cs that decide element order
// and range searches:
//   ufbxi_pivot_nonzero / ufbxi_pivot_div          (ufbx.c:18091-18106)
//   cmp_name_element_less(_ref) / cmp_prop_less_ref (ufbx.c:18555-18581)
//   ufbxi_cmp_node_less                            (ufbx.c:18591-18609)
//   cmp_tmp_material_texture_less                  (ufbx.c:18619-18624)
//   ufbxi_cmp_connection_less                      (ufbx.c:18637-18645)
//   ufbxi_prop_connection_less                     (ufbx.c:19263-19268)
//   ufbxi_cmp_anim_prop_less                       (ufbx.c:19293-19298)
//   ufbxi_material_texture_less                    (ufbx.c:19307-19312)
//   ufbxi_bone_pose_less                           (ufbx.c:19321-19326)
//   ufbxi_blend_keyframe_less                      (ufbx.c:19357-19362)
//   ufbxi_macro_stable_sort / ufbxi_stable_sort    (ufbx.c:1142-1186, 1233-1291)
//   ufbxi_macro_lower_bound_eq / upper_bound_eq    (ufbx.c:1188-1229), exercised through
//       ufbxi_find_dst_connections / _src_connections / ufbxi_find_prop_connection
//
// The context-mutating chain (ufbxi_pre_finalize_scene / _resolve_connections /
// _add_connections_to_elements / _linearize_nodes) needs a live `ufbxi_context`; it is verified
// by the line-by-line audit plus the GraphCheck/LoadCheck/DomCheck regression harnesses, not
// here.
//
// Build (zig, matching the other oracles; `-DNDEBUG` matches the reference build per
// PORTING_NOTES.md so the inlined helpers' `ufbx_assert`s are compiled out, and
// `-fno-strict-aliasing` is required because `ufbxi_cmp_connection_less` indexes
// `(&conn->src)[index]` / `(&conn->src_prop)[index]`):
//   zig cc -O2 -DNDEBUG -fno-strict-aliasing -std=c11 -mcpu=x86_64 -ffp-contract=off \
//       -I C:/Workspace/_analyze_ufbx tools/s3_oracle.c -o tools/s3_oracle.exe
// Run:
//   tools/s3_oracle.exe > tools/s3_oracle.txt
//
// Output grammar (space separated, one record per line; `<h>` = lowercase 16 hex digits of the
// IEEE-754 bits of a double; `<i>` = decimal int; string fields are indices into the fixed
// table below):
//   PD  <off:h> <scale:h> <result:h>                          ufbxi_pivot_div
//   PN  <x:h> <y:h> <z:h> <r>                                 ufbxi_pivot_nonzero
//   CC  <ix> <as> <ad> <asp> <adp> <bs> <bd> <bsp> <bdp> <r>  ufbxi_cmp_connection_less
//   CN  <an> <ak> <at> <bn> <bk> <bt> <r>                     ufbxi_cmp_name_element_less
//   CNR <an> <ak> <at> <qn> <qk> <qt> <r>                     ufbxi_cmp_name_element_less_ref
//   CPR <an> <ak> <qn> <qk> <r>                               ufbxi_cmp_prop_less_ref
//   CMT <am> <at> <ap> <bm> <bt> <bp> <r>                     ufbxi_cmp_tmp_material_texture_less
//   CNODE <ad> <apid> <ag> <asc> <ae> <bd> <bpid> <bg> <bsc> <be> <r>   ufbxi_cmp_node_less
//   CA  <ae> <ak> <ap> <be> <bk> <bp> <r>                     ufbxi_cmp_anim_prop_less
//   MT  <ap> <bp> <r>                                         ufbxi_material_texture_less
//   BP  <at> <bt> <r>                                         ufbxi_bone_pose_less
//   BK  <aw:h> <bw:h> <r>                                     ufbxi_blend_keyframe_less
//   PC  <adp> <asplen> <r>                                    ufbxi_prop_connection_less
//   FDS <n> <q> <begin> <count> <n x (dpi spi)>               ufbxi_find_dst_connections
//   FSS <n> <q> <begin> <count> <n x (spi dpi)>               ufbxi_find_src_connections
//   FPC <n> <q> <found> <n x (dpi spi)>                       ufbxi_find_prop_connection
//   SC  <ix> <n> <n x (a b c d)> <n x (a b c d)>              connection stable sort (in, out)
//   SAP <n> <n x (e k p)> <n x (e k p)>                       anim prop stable sort (in, out)
//   SBK <n> <n x w:h> <n x w:h>                               blend keyframe stable sort (in, out)

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#include "ufbx.c"

// Fixed interned string table (all lengths = strlen). The comparators use strcmp /
// ufbxi_str_less on `.data`; the binary-search equality uses `.data` pointer identity, so both
// sides must share these exact strings.
static const char *g_str[] = {
	"", "a", "b", "ab", "X", "d|X", "Lcl Scaling", "Texture alpha",
};
#define NSTR ((int)(sizeof(g_str) / sizeof(g_str[0])))

static ufbx_string S(int i) { ufbx_string s; s.data = g_str[i]; s.length = strlen(g_str[i]); return s; }
// Element pool: ids 0..3 are backed by a contiguous static array so that pointer order equals
// id order (the invariant the port relies on), and equal ids share one pointer (C comparators
// compare element pointers). Larger ids get a fresh allocation.
static ufbx_element g_pool[4];
static ufbx_element *E(uint32_t id)
{
	if (id < 4) { g_pool[id].element_id = id; return &g_pool[id]; }
	ufbx_element *e = (ufbx_element*)calloc(1, sizeof(ufbx_element));
	e->element_id = id;
	return e;
}
static uint64_t dbits(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }

static void pd(double off, double scale)
{
	double r = ufbxi_pivot_div(off, scale);
	printf("PD %016llx %016llx %016llx\n",
		(unsigned long long)dbits(off), (unsigned long long)dbits(scale), (unsigned long long)dbits(r));
}

static void pn(double x, double y, double z)
{
	ufbx_vec3 v; v.x = x; v.y = y; v.z = z;
	printf("PN %016llx %016llx %016llx %d\n",
		(unsigned long long)dbits(x), (unsigned long long)dbits(y), (unsigned long long)dbits(z),
		ufbxi_pivot_nonzero(v) ? 1 : 0);
}

// ---------------------------------------------------------------- comparators

typedef struct { int src, dst, sp, dp; } ccase;
static ccase g_cc[] = {
	{0,0,0,0},{0,1,0,0},{1,0,0,0},{1,1,0,0},
	{0,0,1,0},{0,0,0,1},{0,0,1,2},{0,0,3,2},
	{2,1,1,1},{1,2,2,2},{0,2,3,3},{2,0,2,3},
	{1,1,7,6},{0,0,4,5},{2,2,6,7},{1,0,0,3},
};
#define NCC ((int)(sizeof(g_cc) / sizeof(g_cc[0])))

static void cc_test(void)
{
	for (int ix = 0; ix <= 1; ix++) {
		for (int i = 0; i < NCC; i++) {
			for (int j = 0; j < NCC; j++) {
				ufbx_connection a, b;
				memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
				a.src = E(g_cc[i].src); a.dst = E(g_cc[i].dst);
				a.src_prop = S(g_cc[i].sp); a.dst_prop = S(g_cc[i].dp);
				b.src = E(g_cc[j].src); b.dst = E(g_cc[j].dst);
				b.src_prop = S(g_cc[j].sp); b.dst_prop = S(g_cc[j].dp);
				int r = ufbxi_cmp_connection_less(&a, &b, (size_t)ix) ? 1 : 0;
				printf("CC %d %d %d %d %d %d %d %d %d %d\n",
					ix, g_cc[i].src, g_cc[i].dst, g_cc[i].sp, g_cc[i].dp,
					g_cc[j].src, g_cc[j].dst, g_cc[j].sp, g_cc[j].dp, r);
			}
		}
	}
}

typedef struct { int name, key, type; } ncase;
static ncase g_nc[] = {
	{0,0,0},{0,1,0},{1,0,0},{1,1,0},{2,1,0},{3,1,0},{1,1,1},{1,1,2},
};
#define NNC ((int)(sizeof(g_nc) / sizeof(g_nc[0])))

static void cn_test(void)
{
	for (int i = 0; i < NNC; i++) {
		for (int j = 0; j < NNC; j++) {
			ufbx_name_element a, b;
			memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
			a.name = S(g_nc[i].name); a._internal_key = (uint32_t)g_nc[i].key; a.type = (ufbx_element_type)g_nc[i].type;
			b.name = S(g_nc[j].name); b._internal_key = (uint32_t)g_nc[j].key; b.type = (ufbx_element_type)g_nc[j].type;
			printf("CN %d %d %d %d %d %d %d\n",
				g_nc[i].name, g_nc[i].key, g_nc[i].type, g_nc[j].name, g_nc[j].key, g_nc[j].type,
				ufbxi_cmp_name_element_less(&a, &b) ? 1 : 0);
			printf("CNR %d %d %d %d %d %d %d\n",
				g_nc[i].name, g_nc[i].key, g_nc[i].type, g_nc[j].name, g_nc[j].key, g_nc[j].type,
				ufbxi_cmp_name_element_less_ref(&a, S(g_nc[j].name), (ufbx_element_type)g_nc[j].type, (uint32_t)g_nc[j].key) ? 1 : 0);
			ufbx_prop p;
			memset(&p, 0, sizeof(p));
			p.name = S(g_nc[i].name);
			p._internal_key = (uint32_t)g_nc[i].key;
			printf("CPR %d %d %d %d %d\n", g_nc[i].name, g_nc[i].key, g_nc[j].name, g_nc[j].key,
				ufbxi_cmp_prop_less_ref(&p, S(g_nc[j].name), (uint32_t)g_nc[j].key) ? 1 : 0);
		}
	}
}

static void cnode_test(void)
{
	int depths[] = { 0, 1, 2 };
	int pids[] = { 0, 1, 2 };      // 0 = no parent
	int geoms[] = { 0, 1 };
	int scales[] = { 0, 1 };
	int eids[] = { 0, 1, 2 };
	for (int d1 = 0; d1 < 3; d1++) for (int p1 = 0; p1 < 3; p1++)
	for (int g1 = 0; g1 < 2; g1++) for (int s1 = 0; s1 < 2; s1++) for (int e1 = 0; e1 < 3; e1++)
	for (int d2 = 0; d2 < 3; d2++) for (int p2 = 0; p2 < 3; p2++)
	for (int g2 = 0; g2 < 2; g2++) for (int s2 = 0; s2 < 2; s2++) for (int e2 = 0; e2 < 3; e2++) {
		ufbx_node na, nb, pa, pb;
		memset(&na, 0, sizeof(na)); memset(&nb, 0, sizeof(nb));
		memset(&pa, 0, sizeof(pa)); memset(&pb, 0, sizeof(pb));
		na.node_depth = (uint32_t)depths[d1];
		na.is_geometry_transform_helper = geoms[g1] ? true : false;
		na.is_scale_helper = scales[s1] ? true : false;
		na.element.element_id = (uint32_t)eids[e1];
		nb.node_depth = (uint32_t)depths[d2];
		nb.is_geometry_transform_helper = geoms[g2] ? true : false;
		nb.is_scale_helper = scales[s2] ? true : false;
		nb.element.element_id = (uint32_t)eids[e2];
		if (pids[p1]) { pa.element.element_id = (uint32_t)pids[p1]; na.parent = &pa; }
		if (pids[p2]) { pb.element.element_id = (uint32_t)pids[p2]; nb.parent = &pb; }
		printf("CNODE %d %d %d %d %d %d %d %d %d %d %d\n",
			depths[d1], pids[p1], geoms[g1], scales[s1], eids[e1],
			depths[d2], pids[p2], geoms[g2], scales[s2], eids[e2],
			ufbxi_cmp_node_less(&na, &nb) ? 1 : 0);
	}
}

typedef struct { int mat, tex, prop; } mcase;
static mcase g_mc[] = {
	{0,0,0},{1,0,0},{0,1,0},{0,0,1},{1,1,1},{2,0,3},{0,2,2},{1,2,3},
};
#define NMC ((int)(sizeof(g_mc) / sizeof(g_mc[0])))

static void cmt_test(void)
{
	for (int i = 0; i < NMC; i++) for (int j = 0; j < NMC; j++) {
		ufbxi_tmp_material_texture a, b;
		memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
		a.material_id = g_mc[i].mat; a.texture_id = g_mc[i].tex; a.prop_name = S(g_mc[i].prop);
		b.material_id = g_mc[j].mat; b.texture_id = g_mc[j].tex; b.prop_name = S(g_mc[j].prop);
		printf("CMT %d %d %d %d %d %d %d\n",
			g_mc[i].mat, g_mc[i].tex, g_mc[i].prop, g_mc[j].mat, g_mc[j].tex, g_mc[j].prop,
			ufbxi_cmp_tmp_material_texture_less(&a, &b) ? 1 : 0);
	}
}

typedef struct { int elem, key, prop; } acase;
static acase g_ac[] = {
	{0,0,0},{1,0,0},{0,1,0},{0,0,1},{1,1,1},{2,0,3},{0,2,2},{1,2,3},
};
#define NAC ((int)(sizeof(g_ac) / sizeof(g_ac[0])))

static void ca_test(void)
{
	for (int i = 0; i < NAC; i++) for (int j = 0; j < NAC; j++) {
		ufbx_anim_prop a, b;
		memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
		a.element = E(g_ac[i].elem); a._internal_key = (uint32_t)g_ac[i].key; a.prop_name = S(g_ac[i].prop);
		b.element = E(g_ac[j].elem); b._internal_key = (uint32_t)g_ac[j].key; b.prop_name = S(g_ac[j].prop);
		printf("CA %d %d %d %d %d %d %d\n",
			g_ac[i].elem, g_ac[i].key, g_ac[i].prop, g_ac[j].elem, g_ac[j].key, g_ac[j].prop,
			ufbxi_cmp_anim_prop_less(&a, &b) ? 1 : 0);
	}
}

static void simple_test(void)
{
	for (int i = 0; i < NSTR; i++) for (int j = 0; j < NSTR; j++) {
		ufbx_material_texture a, b;
		memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
		a.material_prop = S(i); b.material_prop = S(j);
		printf("MT %d %d %d\n", i, j, ufbxi_material_texture_less(NULL, &a, &b) ? 1 : 0);
	}
	int tids[] = { 0, 1, 2, 5 };
	for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) {
		ufbx_node na, nb; ufbx_bone_pose a, b;
		memset(&na, 0, sizeof(na)); memset(&nb, 0, sizeof(nb));
		memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
		na.element.typed_id = (uint32_t)tids[i];
		nb.element.typed_id = (uint32_t)tids[j];
		a.bone_node = &na; b.bone_node = &nb;
		printf("BP %d %d %d\n", tids[i], tids[j], ufbxi_bone_pose_less(NULL, &a, &b) ? 1 : 0);
	}
	double ws[] = { 0.0, 0.25, 0.5, 0.5, 1.0 };
	for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) {
		ufbx_blend_keyframe a, b;
		memset(&a, 0, sizeof(a)); memset(&b, 0, sizeof(b));
		a.target_weight = ws[i]; b.target_weight = ws[j];
		printf("BK %016llx %016llx %d\n", (unsigned long long)dbits(ws[i]), (unsigned long long)dbits(ws[j]),
			ufbxi_blend_keyframe_less(NULL, &a, &b) ? 1 : 0);
	}
	int splens[] = { 0, 1, 3 };
	for (int i = 0; i < NSTR; i++) for (int k = 0; k < 3; k++) {
		ufbx_connection a;
		memset(&a, 0, sizeof(a));
		a.dst_prop = S(i);
		a.src_prop = S(1);            // data value is irrelevant: the comparator reads length only
		a.src_prop.length = (size_t)splens[k];
		printf("PC %d %d %d\n", i, splens[k], ufbxi_prop_connection_less(&a, g_str[i]) ? 1 : 0);
	}
}

// ---------------------------------------------------------------- binary-search macros

static uint64_t g_rng;
static uint32_t rnd(uint32_t n) { g_rng = g_rng * 6364136223846793005ULL + 1442695040888963407ULL; return (uint32_t)((g_rng >> 33) % n); }

static int stridx(const char *p) { for (int s = 0; s < NSTR; s++) if (g_str[s] == p) return s; return -1; }

static void fds_test(void)
{
	for (int t = 0; t < 48; t++) {
		int n = (int)rnd(9);
		ufbx_connection conns[16];
		memset(conns, 0, sizeof(conns));
		ufbx_element *dst = E(0);
		for (int i = 0; i < n; i++) {
			conns[i].src = E((uint32_t)rnd(4));
			conns[i].dst = dst;
			conns[i].src_prop = S((int)rnd(NSTR));
			conns[i].dst_prop = S((int)rnd(4));
		}
		{ ufbx_connection tmp[16]; ufbxi_macro_stable_sort(ufbx_connection, 32, conns, tmp, (size_t)n, (ufbxi_cmp_connection_less(a, b, 1))); }
		int q = (int)rnd(4);
		ufbx_element elem; memset(&elem, 0, sizeof(elem));
		elem.connections_dst.data = conns; elem.connections_dst.count = (size_t)n;
		ufbx_connection_list res = ufbxi_find_dst_connections(&elem, g_str[q]);
		printf("FDS %d %d %d %d", n, q, (int)(res.data - conns), (int)res.count);
		for (int i = 0; i < n; i++) printf(" %d %d", stridx(conns[i].dst_prop.data), stridx(conns[i].src_prop.data));
		printf("\n");
	}
}

static void fss_test(void)
{
	for (int t = 0; t < 48; t++) {
		int n = (int)rnd(9);
		ufbx_connection conns[16];
		memset(conns, 0, sizeof(conns));
		ufbx_element *src = E(0);
		for (int i = 0; i < n; i++) {
			conns[i].src = src;
			conns[i].dst = E((uint32_t)rnd(4));
			conns[i].src_prop = S((int)rnd(4));
			conns[i].dst_prop = S((int)rnd(NSTR));
		}
		{ ufbx_connection tmp[16]; ufbxi_macro_stable_sort(ufbx_connection, 32, conns, tmp, (size_t)n, (ufbxi_cmp_connection_less(a, b, 0))); }
		int q = (int)rnd(4);
		ufbx_element elem; memset(&elem, 0, sizeof(elem));
		elem.connections_src.data = conns; elem.connections_src.count = (size_t)n;
		ufbx_connection_list res = ufbxi_find_src_connections(&elem, g_str[q]);
		printf("FSS %d %d %d %d", n, q, (int)(res.data - conns), (int)res.count);
		for (int i = 0; i < n; i++) printf(" %d %d", stridx(conns[i].src_prop.data), stridx(conns[i].dst_prop.data));
		printf("\n");
	}
}

static void fpc_test(void)
{
	for (int t = 0; t < 48; t++) {
		int n = (int)rnd(9);
		ufbx_connection conns[16];
		memset(conns, 0, sizeof(conns));
		for (int i = 0; i < n; i++) {
			conns[i].src = E((uint32_t)rnd(4));
			conns[i].dst = E(0);
			conns[i].src_prop = S((int)rnd(NSTR));
			conns[i].dst_prop = S((int)rnd(4));
		}
		// Same order as the real `connections_dst`: cmp_connection_less(_, _, 1).
		{ ufbx_connection tmp[16]; ufbxi_macro_stable_sort(ufbx_connection, 32, conns, tmp, (size_t)n, (ufbxi_cmp_connection_less(a, b, 1))); }
		int q = (int)rnd(4);
		ufbx_element elem; memset(&elem, 0, sizeof(elem));
		elem.connections_dst.data = conns; elem.connections_dst.count = (size_t)n;
		ufbx_connection *res = ufbxi_find_prop_connection(&elem, g_str[q]);
		printf("FPC %d %d %d", n, q, res ? (int)(res - conns) : -1);
		for (int i = 0; i < n; i++) printf(" %d %d", stridx(conns[i].dst_prop.data), stridx(conns[i].src_prop.data));
		printf("\n");
	}
}

// ---------------------------------------------------------------- stable sorts

static void sc_test(void)
{
	for (int ix = 0; ix <= 1; ix++) {
		for (int t = 0; t < 24; t++) {
			int n = (int)rnd(12);
			ufbx_connection conns[16];
			memset(conns, 0, sizeof(conns));
			int as[16], ad[16], asp[16], adp[16];
			for (int i = 0; i < n; i++) {
				as[i] = (int)rnd(3); ad[i] = (int)rnd(3);
				asp[i] = (int)rnd(NSTR); adp[i] = (int)rnd(NSTR);
				conns[i].src = E((uint32_t)as[i]); conns[i].dst = E((uint32_t)ad[i]);
				conns[i].src_prop = S(asp[i]); conns[i].dst_prop = S(adp[i]);
			}
			{ ufbx_connection tmp[16]; ufbxi_macro_stable_sort(ufbx_connection, 32, conns, tmp, (size_t)n, (ufbxi_cmp_connection_less(a, b, ix))); }
			printf("SC %d %d", ix, n);
			for (int i = 0; i < n; i++) printf(" %d %d %d %d", as[i], ad[i], asp[i], adp[i]);
			for (int i = 0; i < n; i++) printf(" %d %d %d %d", (int)conns[i].src->element_id, (int)conns[i].dst->element_id, stridx(conns[i].src_prop.data), stridx(conns[i].dst_prop.data));
			printf("\n");
		}
	}
}

static void sap_test(void)
{
	for (int t = 0; t < 24; t++) {
		int n = (int)rnd(12);
		ufbx_anim_prop aps[16];
		memset(aps, 0, sizeof(aps));
		int ae[16], ak[16], ap[16];
		for (int i = 0; i < n; i++) {
			ae[i] = (int)rnd(3); ak[i] = (int)rnd(3); ap[i] = (int)rnd(NSTR);
			aps[i].element = E((uint32_t)ae[i]);
			aps[i]._internal_key = (uint32_t)ak[i];
			aps[i].prop_name = S(ap[i]);
		}
		{ ufbx_anim_prop tmp[16]; ufbxi_macro_stable_sort(ufbx_anim_prop, 32, aps, tmp, (size_t)n, (ufbxi_cmp_anim_prop_less(a, b))); }
		printf("SAP %d", n);
		for (int i = 0; i < n; i++) printf(" %d %d %d", ae[i], ak[i], ap[i]);
		for (int i = 0; i < n; i++) printf(" %d %d %d", (int)aps[i].element->element_id, (int)aps[i]._internal_key, stridx(aps[i].prop_name.data));
		printf("\n");
	}
}

static void sbk_test(void)
{
	for (int t = 0; t < 24; t++) {
		int n = (int)rnd(12);
		ufbx_blend_keyframe kfs[16];
		memset(kfs, 0, sizeof(kfs));
		double w[16];
		for (int i = 0; i < n; i++) { w[i] = (double)(int)rnd(4); kfs[i].target_weight = w[i]; }
		{ ufbx_blend_keyframe tmp[16]; ufbxi_stable_sort(sizeof(ufbx_blend_keyframe), 32, kfs, tmp, (size_t)n, &ufbxi_blend_keyframe_less, NULL); }
		printf("SBK %d", n);
		for (int i = 0; i < n; i++) printf(" %016llx", (unsigned long long)dbits(w[i]));
		for (int i = 0; i < n; i++) printf(" %016llx", (unsigned long long)dbits(kfs[i].target_weight));
		printf("\n");
	}
}

int main(void)
{
	g_rng = 0x9e3779b97f4a7c15ULL;
	setvbuf(stdout, NULL, _IONBF, 0);

	double vals[] = { -2.0, -1.0, -0.5, -0.0078125, -0.007, 0.0, 0.007, 0.0078125, 0.008, 0.5, 1.0, 2.0, 1e9 };
	for (int i = 0; i < 13; i++) for (int j = 0; j < 13; j++) pd(vals[i], vals[j]);
	fprintf(stderr, "END pd\n");

	double pvals[] = { -1.0, -0.0009765625, -0.0009765624, 0.0, 0.0009765624, 0.0009765625, 0.001, 1.0 };
	for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) for (int k = 0; k < 8; k++) pn(pvals[i], pvals[j], pvals[k]);
	fprintf(stderr, "END pn\n");

	fprintf(stderr, "BEGIN cc\n"); cc_test(); fprintf(stderr, "END cc\n");
	fprintf(stderr, "BEGIN cn\n"); cn_test(); fprintf(stderr, "END cn\n");
	fprintf(stderr, "BEGIN cnode\n"); cnode_test(); fprintf(stderr, "END cnode\n");
	fprintf(stderr, "BEGIN cmt\n"); cmt_test(); fprintf(stderr, "END cmt\n");
	fprintf(stderr, "BEGIN ca\n"); ca_test(); fprintf(stderr, "END ca\n");
	fprintf(stderr, "BEGIN simple\n"); simple_test(); fprintf(stderr, "END simple\n");
	fprintf(stderr, "BEGIN fds\n"); fds_test(); fprintf(stderr, "END fds\n");
	fprintf(stderr, "BEGIN fss\n"); fss_test(); fprintf(stderr, "END fss\n");
	fprintf(stderr, "BEGIN fpc\n"); fpc_test(); fprintf(stderr, "END fpc\n");
	fprintf(stderr, "BEGIN sc\n"); sc_test(); fprintf(stderr, "END sc\n");
	fprintf(stderr, "BEGIN sap\n"); sap_test(); fprintf(stderr, "END sap\n");
	fprintf(stderr, "BEGIN sbk\n"); sbk_test(); fprintf(stderr, "END sbk\n");

	return 0;
}
