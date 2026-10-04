// C reference oracle for the S4c topology / normals / triangulation module of the
// ufbx -> C# port. `#include "ufbx.c"` to reach everything, exactly like the other
// oracles (see tools/s3_oracle.c / tools/s4a_oracle.c).
//
// Scope (P0):
//   * ufbx_compute_topology (ufbx.c:33176 -> ufbxi_compute_topology 28715)
//   * ufbx_topo_next/prev_vertex_edge (33179/33182 -> 32492/32502)
//   * ufbx_generate_normal_mapping (32588 -> 32542, is_edge_smooth 28794)
//   * ufbx_compute_normals (32622 -> 32593, weighted face normal 32509)
//   * ufbx_triangulate_face (33173 -> 32400, triangulate_ngon 28497 + kd tree)
//   * ufbx_get_weighted_face_normal (33185 -> 32509)
//
// The C# port cannot load FBX files yet (the toplevel reader seam is unported), so the
// oracle dumps both the *mesh definition* (positions/indices/faces/edges/smoothing as
// bit-exact records) and the S4c outputs for the same mesh. The S4cCheck harness
// reconstructs each mesh from the definition records, runs the ported functions and
// compares the output records line by line.
//
// Build (zig, matching the other oracles):
//   zig cc -O2 -DNDEBUG -fno-strict-aliasing -std=c11 -mcpu=x86_64 -ffp-contract=off \
//       -I C:/Workspace/_s4c_ufbx tools/s4c_oracle.c -o tools/s4c_oracle.exe
//
// NOTE: the oracle compiles against `C:/Workspace/_s4c_ufbx/ufbx.c` -- a verbatim copy
// of the frozen v0.23.1 source with ONE upstream bug fixed (marked `S4C-FIX` there):
// `ufbxi_subdivide_layer()` takes a stale `inputs` local pointer after the grow_array in
// the "Looped: Add the face from the other side" branch (ufbx.c:29311 in the frozen
// source) -- `ufbxi_grow_array` reallocs and may move the block; the sibling "+2" grow
// below refreshes `inputs = sc->inputs`, this site does not. The stale write/read hits
// freed heap memory and corrupts the arena nondeterministically (crashes with exit 139/127
// depending on layout). The refreshed pointer does not change the computed values (realloc
// preserves contents), it only removes the undefined behavior -- so the port's semantics
// are unchanged. See the S4c report.
//
// Run (from the repo root):
//   tools/s4c_oracle.exe \
//     C:/Workspace/_analyze_ufbx/data/maya_cube_7500_binary.fbx \
//     ... (see tools/_s4c_corpus_list.txt) > tools/s4c_oracle.txt
//
// Output grammar (space separated; <h> = 16 lowercase hex digits of the IEEE-754 bits of
// a double; index lists are decimal; UFBX_NO_INDEX prints as `-`):
//   SKIP <path-hex> <err-hex>                     scene failed to load
//   SC <path-hex> <num_meshes>                    scene loaded
//   M <ix> <num_faces> <num_indices> <num_vertices> <num_edges> <has_es> <has_fs> <vn_exists>
//   VP <n> <x:h> <y:h> <z:h> ...                  vertex_position.values
//   VPI <n> <i0> ...                              vertex_position.indices
//   F <face_ix> <index_begin> <num_indices>       per face
//   VTX <n> <v0> ...                              mesh->vertex_indices
//   VFI <n> <v0> ...                              mesh->vertex_first_index
//   ED <a> <b>                                    per edge
//   ES <0|1>                                      per edge, edge_smoothing (if exists)
//   FS <0|1>                                      per face, face_smoothing (if exists)
//   VNV <n> <x:h> <y:h> <z:h> ...                 vertex_normal.values (if exists)
//   VNI <n> <i0> ...                              vertex_normal.indices (if exists)
//   TOP <i> <index> <next> <prev> <twin> <face> <edge> <flags>   per index
//   MAP <s> <count> <i0> ...                      generate_normal_mapping, s = assume_smooth
//   NRM <kind> <count> <x:h> <y:h> <z:h> ...      compute_normals, kind = map0|map1|vn (-1 = skipped)
//   TRI <face_ix> <num_tris> <i0> ...             triangulate_face per face
//   WFN <face_ix> <x:h> <y:h> <z:h>               get_weighted_face_normal per face
//
// P1 extension (same grammar conventions):
//   NB <id> <order> <topology> <valid> <tmin:h> <tmax:h> <nknots> <k:h...> <nspans> <s:h...>
//   NC <id> <basis_id> <ncp> <x:h> <y:h> <z:h> <w:h> ...        nurbs curve definition
//   NS <id> <basis_u_id> <basis_v_id> <ncp_u> <ncp_v> <flip> <has_mat> <ncp> <x:h> <y:h> <z:h> <w:h> ...
//   EVB <basis_id> <u:h> <base|-> <w:h x order> <d:h x order>   ufbx_evaluate_nurbs_basis
//   EVC <curve_id> <u:h> <valid> <p:h x3> <d:h x3>              ufbx_evaluate_nurbs_curve
//   EVS <surf_id> <u:h> <v:h> <valid> <p:h x3> <du:h x3> <dv:h x3>  ufbx_evaluate_nurbs_surface
//   TSC <curve_id> <span_sub> <ok>                              ufbx_tessellate_nurbs_curve
//   TSCV <nvertices> <x:h> <y:h> <z:h> ...                      tessellated curve vertices
//   TSCI <nindices> <i0> ...                                    tessellated curve point_indices
//   TSS <surf_id> <sub_u> <sub_v> <skip_parts> <ok>             ufbx_tessellate_nurbs_surface
//   TSSVP <n> <x:h> <y:h> <z:h> ...                             tessellated mesh vertex_position.values
//   TSSVPI <n> <i0> ...                                         tessellated mesh vertex_position.indices
//   TSSF <face_ix> <index_begin> <num_indices>                  tessellated mesh faces
//   TSSUV <n> <u:h> <v:h> ...                                   tessellated mesh uvs via indices (consumer view)
//   TSSVN <n> <x:h> <y:h> <z:h> ...                             tessellated mesh vertex_normal.values
//   TSSPT <part> <num_faces> <num_triangles> <empty> <point> <line> <fi...>  material part
//   SUB <mesh_ix> <level> <interp> <boundary> <uv_boundary>     ufbx_subdivide_mesh
//   SUBFAIL <desc>                                              subdivision failed
//   SVP/SVPI/SF/SVTX/SVFI/SED/SES/SEV/SEC/SFS/SFM/SGR/SHL/SVNV/SVNI   subdivided mesh definition
//   STOP <i> <index> <next> <prev> <twin> <face> <edge> <flags>       subdivided mesh topology
//   SMP <part> <num_faces> <num_triangles> <empty> <point> <line> <fi...>  subdivided material part
//   SMU <n> <u0> ...                                            material_part_usage_order
//   SEND                                                        subdivided dump end
//   GINC <id> <num_streams> <num_indices> <vs0> ...             generate_indices config
//   GIND <id> <stream> <count> <b02> ...                        generate_indices input bytes
//   GINI <id> <count> <i0> ...                                  generate_indices result indices
//   GINO <id> <stream> <count> <b02> ...                        generate_indices deduped bytes

#include "ufbx.c"

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static void dbits(double v) {
	uint64_t u;
	memcpy(&u, &v, 8);
	printf("%016llx", (unsigned long long)u);
}

static void phex(const char *data, size_t len) {
	if (len == 0) { putchar('-'); return; }
	for (size_t i = 0; i < len; i++) printf("%02x", (unsigned char)data[i]);
}

static void dump_mesh(size_t mesh_ix, ufbx_mesh *mesh)
{
	size_t num_indices = mesh->num_indices;
	bool has_es = mesh->edge_smoothing.data != NULL;   // C: ufbxi_is_edge_smooth checks .data (ufbx.c:28798)
	bool has_fs = mesh->face_smoothing.data != NULL;
	bool has_vn = mesh->vertex_normal.exists;

	printf("M %zu %zu %zu %zu %zu %d %d %d\n", mesh_ix, mesh->num_faces, num_indices,
		mesh->num_vertices, mesh->num_edges, has_es ? 1 : 0, has_fs ? 1 : 0, has_vn ? 1 : 0);

	printf("VP %zu", mesh->vertex_position.values.count);
	for (size_t i = 0; i < mesh->vertex_position.values.count; i++) {
		ufbx_vec3 v = mesh->vertex_position.values.data[i];
		putchar(' '); dbits(v.x); putchar(' '); dbits(v.y); putchar(' '); dbits(v.z);
	}
	printf("\n");

	printf("VPI %zu", mesh->vertex_position.indices.count);
	for (size_t i = 0; i < mesh->vertex_position.indices.count; i++) {
		printf(" %u", mesh->vertex_position.indices.data[i]);
	}
	printf("\n");

	for (size_t i = 0; i < mesh->num_faces; i++) {
		ufbx_face f = mesh->faces.data[i];
		printf("F %zu %u %u\n", i, f.index_begin, f.num_indices);
	}

	printf("VTX %zu", mesh->vertex_indices.count);
	for (size_t i = 0; i < mesh->vertex_indices.count; i++) {
		printf(" %u", mesh->vertex_indices.data[i]);
	}
	printf("\n");

	printf("VFI %zu", mesh->vertex_first_index.count);
	for (size_t i = 0; i < mesh->vertex_first_index.count; i++) {
		printf(" %u", mesh->vertex_first_index.data[i]);
	}
	printf("\n");

	for (size_t i = 0; i < mesh->num_edges; i++) {
		ufbx_edge e = mesh->edges.data[i];
		printf("ED %u %u\n", e.a, e.b);
	}
	if (has_es) {
		for (size_t i = 0; i < mesh->num_edges; i++) {
			printf("ES %d\n", mesh->edge_smoothing.data[i] ? 1 : 0);
		}
	}
	if (has_fs) {
		for (size_t i = 0; i < mesh->num_faces; i++) {
			printf("FS %d\n", mesh->face_smoothing.data[i] ? 1 : 0);
		}
	}

	if (has_vn) {
		printf("VNV %zu", mesh->vertex_normal.values.count);
		for (size_t i = 0; i < mesh->vertex_normal.values.count; i++) {
			ufbx_vec3 v = mesh->vertex_normal.values.data[i];
			putchar(' '); dbits(v.x); putchar(' '); dbits(v.y); putchar(' '); dbits(v.z);
		}
		printf("\n");
		printf("VNI %zu", mesh->vertex_normal.indices.count);
		for (size_t i = 0; i < mesh->vertex_normal.indices.count; i++) {
			printf(" %u", mesh->vertex_normal.indices.data[i]);
		}
		printf("\n");
	}

	// --- compute_topology (ufbx.c:33176 -> 28715)
	ufbx_topo_edge *topo = malloc(sizeof(ufbx_topo_edge) * (num_indices ? num_indices : 1));
	ufbx_compute_topology(mesh, topo, num_indices);
	for (size_t i = 0; i < num_indices; i++) {
		ufbx_topo_edge t = topo[i];
		printf("TOP %zu %u %u %u %u %u %u %u\n", i, t.index, t.next, t.prev, t.twin,
			t.face, t.edge, (unsigned)t.flags);
	}

	// --- generate_normal_mapping (ufbx.c:32588 -> 32542), both assume_smooth values.
	// NOTE: one index array per smoothing flag, so each compute_normals below replays the
	// mapping it is named after.
	uint32_t *nrm_ix[2];
	size_t cnt[2];
	for (int s = 0; s < 2; s++) {
		nrm_ix[s] = malloc(sizeof(uint32_t) * (num_indices ? num_indices : 1));
		cnt[s] = ufbx_generate_normal_mapping(mesh, topo, num_indices, nrm_ix[s], num_indices, s != 0);
		printf("MAP %d %zu", s, cnt[s]);
		for (size_t i = 0; i < num_indices; i++) {
			if (nrm_ix[s][i] == UFBX_NO_INDEX) printf(" -");
			else printf(" %u", nrm_ix[s][i]);
		}
		printf("\n");
	}

	// --- compute_normals (ufbx.c:32622 -> 32593) against the mapping indices
	for (int s = 0; s < 2; s++) {
		ufbx_vec3 *normals = malloc(sizeof(ufbx_vec3) * (cnt[s] ? cnt[s] : 1));
		ufbx_compute_normals(mesh, &mesh->vertex_position, nrm_ix[s], num_indices, normals, cnt[s]);
		printf("NRM map%d %zu", s, cnt[s]);
		for (size_t i = 0; i < cnt[s]; i++) {
			putchar(' '); dbits(normals[i].x); putchar(' '); dbits(normals[i].y); putchar(' '); dbits(normals[i].z);
		}
		printf("\n");
		free(normals);
	}

	// and against the loaded normals, when they can index the whole mesh
	if (has_vn && mesh->vertex_normal.indices.count >= num_indices) {
		size_t nn = mesh->vertex_normal.values.count;
		ufbx_vec3 *normals = malloc(sizeof(ufbx_vec3) * (nn ? nn : 1));
		ufbx_compute_normals(mesh, &mesh->vertex_position, mesh->vertex_normal.indices.data, num_indices, normals, nn);
		printf("NRM vn %zu", nn);
		for (size_t i = 0; i < nn; i++) {
			putchar(' '); dbits(normals[i].x); putchar(' '); dbits(normals[i].y); putchar(' '); dbits(normals[i].z);
		}
		printf("\n");
		free(normals);
	} else {
		printf("NRM vn -1\n");
	}

	// --- triangulate_face (ufbx.c:33173 -> 32400) per face
	for (size_t fi = 0; fi < mesh->num_faces; fi++) {
		ufbx_face face = mesh->faces.data[fi];
		if (face.num_indices < 3) {
			printf("TRI %zu 0\n", fi);
			continue;
		}
		size_t required = ((size_t)face.num_indices - 2) * 3;
		uint32_t *ti = malloc(sizeof(uint32_t) * required);
		uint32_t num_tris = ufbx_triangulate_face(ti, required, mesh, face);
		printf("TRI %zu %u", fi, num_tris);
		for (size_t k = 0; k < (size_t)num_tris * 3; k++) printf(" %u", ti[k]);
		printf("\n");
		free(ti);
	}

	// --- get_weighted_face_normal (ufbx.c:33185 -> 32509) per face
	for (size_t fi = 0; fi < mesh->num_faces; fi++) {
		ufbx_face face = mesh->faces.data[fi];
		ufbx_vec3 wn = ufbx_get_weighted_face_normal(&mesh->vertex_position, face);
		printf("WFN %zu ", fi); dbits(wn.x); putchar(' '); dbits(wn.y); putchar(' '); dbits(wn.z); putchar('\n');
	}

	free(nrm_ix[0]);
	free(nrm_ix[1]);
	free(topo);
}

// =====================================================================
// P1: NURBS / tessellation / subdivision / index generation
// =====================================================================

static void print_string_hex(const char *s) {
	phex(s, s ? strlen(s) : 0);
}

// C: NB record for one basis; returns the assigned id.
static size_t dump_basis(const ufbx_nurbs_basis *b)
{
	static size_t next_id = 0;
	size_t id = next_id++;

	printf("NB %zu %u %d %d ", id, (unsigned)b->order, (int)b->topology, b->valid ? 1 : 0);
	dbits(b->t_min); putchar(' '); dbits(b->t_max);
	printf(" %zu", b->knot_vector.count);
	for (size_t i = 0; i < b->knot_vector.count; i++) { putchar(' '); dbits(b->knot_vector.data[i]); }
	printf(" %zu", b->spans.count);
	for (size_t i = 0; i < b->spans.count; i++) { putchar(' '); dbits(b->spans.data[i]); }
	printf("\n");
	return id;
}

static void dump_curve_points(const ufbx_vec4 *cps, size_t n)
{
	printf(" %zu", n);
	for (size_t i = 0; i < n; i++) {
		putchar(' '); dbits(cps[i].x); putchar(' '); dbits(cps[i].y);
		putchar(' '); dbits(cps[i].z); putchar(' '); dbits(cps[i].w);
	}
}

// Sample parameter values over a basis: endpoints, span starts, span midpoints.
// Returns the number of samples written to `us` (<= max).
static size_t sample_params(const ufbx_nurbs_basis *b, ufbx_real *us, size_t max)
{
	size_t nu = 0;
	if (nu < max) us[nu++] = b->t_min;
	if (nu < max) us[nu++] = b->t_max;
	for (size_t si = 0; si + 1 < b->spans.count && nu + 2 <= max; si++) {
		us[nu++] = b->spans.data[si];
		us[nu++] = (b->spans.data[si] + b->spans.data[si + 1]) * 0.5;
	}
	if (nu < max) us[nu++] = (b->t_min + b->t_max) * 0.5;
	return nu;
}

static void dump_nurbs(ufbx_scene *scene)
{
	ufbx_error error = { 0 };

	// --- curves: NB/NC records, EVB/EVC evaluations, TSC tessellations
	for (size_t ci = 0; ci < scene->nurbs_curves.count; ci++) {
		ufbx_nurbs_curve *c = scene->nurbs_curves.data[ci];
		size_t bid = dump_basis(&c->basis);

		printf("NC %zu %zu", ci, bid);
		dump_curve_points(c->control_points.data, c->control_points.count);
		printf("\n");

		ufbx_real us[16];
		size_t nu = sample_params(&c->basis, us, 16);

		for (size_t ui = 0; ui < nu; ui++) {
			ufbx_real u = us[ui];

			ufbx_real w[UFBXI_MAX_NURBS_ORDER];
			ufbx_real d[UFBXI_MAX_NURBS_ORDER];
			memset(w, 0xcd, sizeof(w));
			memset(d, 0xcd, sizeof(d));
			size_t base = ufbx_evaluate_nurbs_basis(&c->basis, u, w, c->basis.order, d, c->basis.order);
			printf("EVB %zu ", bid); dbits(u); putchar(' ');
			if (base == SIZE_MAX) printf("-1"); else printf("%zu", base);
			for (size_t i = 0; i < c->basis.order; i++) { putchar(' '); dbits(w[i]); }
			for (size_t i = 0; i < c->basis.order; i++) { putchar(' '); dbits(d[i]); }
			printf("\n");

			ufbx_curve_point p = ufbx_evaluate_nurbs_curve(c, u);
			printf("EVC %zu ", ci); dbits(u); printf(" %d ", p.valid ? 1 : 0);
			dbits(p.position.x); putchar(' '); dbits(p.position.y); putchar(' '); dbits(p.position.z);
			putchar(' '); dbits(p.derivative.x); putchar(' '); dbits(p.derivative.y); putchar(' '); dbits(p.derivative.z);
			printf("\n");
		}

		// tessellate with a couple of subdivision settings
		size_t subs[2] = { 4, 2 };
		for (int si = 0; si < 2; si++) {
			ufbx_tessellate_curve_opts topts = { 0 };
			topts.span_subdivision = subs[si];
			ufbx_line_curve *line = ufbx_tessellate_nurbs_curve(c, &topts, &error);
			if (!line) {
				printf("TSC %zu %zu 0 ", ci, subs[si]);
				print_string_hex(error.description.data);
				printf("\n");
				continue;
			}
			printf("TSC %zu %zu 1\n", ci, subs[si]);
			printf("TSCV %zu", line->control_points.count);
			for (size_t i = 0; i < line->control_points.count; i++) {
				putchar(' '); dbits(line->control_points.data[i].x);
				putchar(' '); dbits(line->control_points.data[i].y);
				putchar(' '); dbits(line->control_points.data[i].z);
			}
			printf("\n");
			printf("TSCI %zu", line->point_indices.count);
			for (size_t i = 0; i < line->point_indices.count; i++) {
				printf(" %u", line->point_indices.data[i]);
			}
			printf("\n");
			ufbx_free_line_curve(line);
		}
	}

	// --- surfaces: NS records, EVS evaluations, TSS tessellations
	for (size_t si = 0; si < scene->nurbs_surfaces.count; si++) {
		ufbx_nurbs_surface *s = scene->nurbs_surfaces.data[si];
		size_t bid_u = dump_basis(&s->basis_u);
		size_t bid_v = dump_basis(&s->basis_v);

		printf("NS %zu %zu %zu %zu %zu %d %d", si, bid_u, bid_v,
			(size_t)s->num_control_points_u, (size_t)s->num_control_points_v,
			s->flip_normals ? 1 : 0, s->material ? 1 : 0);
		dump_curve_points(s->control_points.data,
			(size_t)s->num_control_points_u * (size_t)s->num_control_points_v);
		printf("\n");

		ufbx_real us[16], vs[16];
		size_t nu = sample_params(&s->basis_u, us, 16);
		size_t nv = sample_params(&s->basis_v, vs, 16);

		// Pair up the (limited) samples: u from `us`, v sweeping vs.
		for (size_t ui = 0; ui < nu; ui++) {
			for (size_t vi = 0; vi < nv && ui * nv + vi < 16; vi++) {
				ufbx_real u = us[ui], v = vs[vi];

				ufbx_surface_point p = ufbx_evaluate_nurbs_surface(s, u, v);
				printf("EVS %zu ", si); dbits(u); putchar(' '); dbits(v);
				printf(" %d ", p.valid ? 1 : 0);
				dbits(p.position.x); putchar(' '); dbits(p.position.y); putchar(' '); dbits(p.position.z);
				putchar(' '); dbits(p.derivative_u.x); putchar(' '); dbits(p.derivative_u.y); putchar(' '); dbits(p.derivative_u.z);
				putchar(' '); dbits(p.derivative_v.x); putchar(' '); dbits(p.derivative_v.y); putchar(' '); dbits(p.derivative_v.z);
				printf("\n");
			}
		}

		// tessellate with a couple of settings
		struct { size_t su, sv; bool skip; } tss[] = {
			{ 4, 4, false }, { 1, 4, false }, { 4, 1, false }, { 2, 3, true },
		};
		for (size_t ti = 0; ti < sizeof(tss) / sizeof(tss[0]); ti++) {
			ufbx_tessellate_surface_opts topts = { 0 };
			topts.span_subdivision_u = tss[ti].su;
			topts.span_subdivision_v = tss[ti].sv;
			topts.skip_mesh_parts = tss[ti].skip;
			ufbx_mesh *m = ufbx_tessellate_nurbs_surface(s, &topts, &error);
			if (!m) {
				printf("TSS %zu %zu %zu %d 0 ", si, tss[ti].su, tss[ti].sv, tss[ti].skip ? 1 : 0);
				print_string_hex(error.description.data);
				printf("\n");
				continue;
			}
			printf("TSS %zu %zu %zu %d 1\n", si, tss[ti].su, tss[ti].sv, tss[ti].skip ? 1 : 0);
			printf("TSSVP %zu", m->vertex_position.values.count);
			for (size_t i = 0; i < m->vertex_position.values.count; i++) {
				ufbx_vec3 p = m->vertex_position.values.data[i];
				putchar(' '); dbits(p.x); putchar(' '); dbits(p.y); putchar(' '); dbits(p.z);
			}
			printf("\n");
			printf("TSSVPI %zu", m->vertex_position.indices.count);
			for (size_t i = 0; i < m->vertex_position.indices.count; i++) {
				printf(" %u", m->vertex_position.indices.data[i]);
			}
			printf("\n");
			for (size_t fi = 0; fi < m->num_faces; fi++) {
				ufbx_face f = m->faces.data[fi];
				printf("TSSF %zu %u %u\n", fi, f.index_begin, f.num_indices);
			}
			// ufbx v0.23.1 quirk: vertex_uv.values.count == dst_index overstates the
			// grid-sized values buffer (indices_u*indices_v entries). Every consumer
			// (and the C# port) goes through `.indices`, so dump the uv values in
			// consumer order: values[indices[i]] for i < indices.count. The grid
			// indices cover every face corner exactly, which pins the shared values.
			printf("TSSUV %zu", m->vertex_uv.indices.count);
			for (size_t i = 0; i < m->vertex_uv.indices.count; i++) {
				ufbx_vec2 uv = m->vertex_uv.values.data[m->vertex_uv.indices.data[i]];
				putchar(' '); dbits(uv.x); putchar(' '); dbits(uv.y);
			}
			printf("\n");
			if (m->vertex_normal.exists) {
				printf("TSSVN %zu", m->vertex_normal.values.count);
				for (size_t i = 0; i < m->vertex_normal.values.count; i++) {
					ufbx_vec3 p = m->vertex_normal.values.data[i];
					putchar(' '); dbits(p.x); putchar(' '); dbits(p.y); putchar(' '); dbits(p.z);
				}
				printf("\n");
			}
			printf("TSSMP %zu\n", m->material_parts.count);
			for (size_t pi = 0; pi < m->material_parts.count; pi++) {
				ufbx_mesh_part *part = &m->material_parts.data[pi];
				printf("TSSPT %zu %zu %zu %zu %zu %zu", pi, part->num_faces, part->num_triangles,
					part->num_empty_faces, part->num_point_faces, part->num_line_faces);
				for (size_t i = 0; i < part->face_indices.count; i++) {
					printf(" %u", part->face_indices.data[i]);
				}
				printf("\n");
			}
			ufbx_free_mesh(m);
		}
	}
}

// Build a synthetic mesh containing exactly the fields the definition dump
// (VP/VPI/F/VTX/VFI/ED/ES/FS/VNV/VNI) preserves, so the C# checker can
// reconstruct a byte-identical mesh from the records. Everything else is
// zeroed: uv/color sets, creases, visibility, materials, face groups,
// subdivision properties. The skinned position/normal alias the vertex ones
// exactly like the loader does for meshes without skin deformers
// (ufbx.c:16724-16727; ufbx.c:29694/29708 compare `.values.data` identity).
// `element` (in particular `.scene`, needed by the result refcount parent at
// ufbx.c:30025-30029) is kept from the real mesh; the imp header linkage is
// intentionally broken by the copy -- it is never used because the synthetic
// mesh is neither freed nor retained.
static ufbx_mesh *synth_mesh(const ufbx_mesh *mesh)
{
	ufbx_mesh *m = calloc(1, sizeof(ufbx_mesh));
	*m = *mesh;

	memset(&m->vertex_uv, 0, sizeof(m->vertex_uv));
	memset(&m->vertex_tangent, 0, sizeof(m->vertex_tangent));
	memset(&m->vertex_bitangent, 0, sizeof(m->vertex_bitangent));
	memset(&m->vertex_color, 0, sizeof(m->vertex_color));
	m->uv_sets.data = NULL; m->uv_sets.count = 0;
	m->color_sets.data = NULL; m->color_sets.count = 0;
	memset(&m->edge_crease, 0, sizeof(m->edge_crease));
	memset(&m->edge_visibility, 0, sizeof(m->edge_visibility));
	memset(&m->vertex_crease, 0, sizeof(m->vertex_crease));
	memset(&m->face_material, 0, sizeof(m->face_material));
	memset(&m->face_group, 0, sizeof(m->face_group));
	memset(&m->face_hole, 0, sizeof(m->face_hole));
	m->materials.data = NULL; m->materials.count = 0;
	m->material_parts.data = NULL; m->material_parts.count = 0;
	m->face_group_parts.data = NULL; m->face_group_parts.count = 0;
	m->material_part_usage_order.data = NULL; m->material_part_usage_order.count = 0;
	m->skin_deformers.data = NULL; m->skin_deformers.count = 0;
	m->subdivision_boundary = UFBX_SUBDIVISION_BOUNDARY_DEFAULT;
	m->subdivision_uv_boundary = UFBX_SUBDIVISION_BOUNDARY_DEFAULT;
	m->subdivision_preview_levels = 0;
	m->subdivision_render_levels = 0;
	// NOTE: `max_face_triangles` is kept from the real mesh -- ufbxi_subdivide_layer
	// sizes its per-face input array with `max(32, max_face_triangles + 2)`
	// (ufbx.c:29084). The checker recomputes it from the face records.
	m->num_empty_faces = 0;
	m->num_point_faces = 0;
	m->num_line_faces = 0;
	m->generated_normals = false;
	m->subdivision_evaluated = false;
	m->from_tessellated_nurbs = false;
	m->subdivision_result = NULL;
	m->vertices.data = NULL; m->vertices.count = 0;

	m->skinned_position = m->vertex_position;
	m->skinned_normal = m->vertex_normal;
	return m;
}

static void dump_subdivide_mesh(size_t mesh_ix, ufbx_mesh *mesh, size_t level, bool interp,
	ufbx_subdivision_boundary boundary, ufbx_subdivision_boundary uv_boundary)
{
	printf("SUB %zu %zu %d %d %d\n", mesh_ix, level, interp ? 1 : 0, (int)boundary, (int)uv_boundary);

	ufbx_subdivide_opts opts = { 0 };
	opts.boundary = boundary;
	opts.uv_boundary = uv_boundary;
	opts.interpolate_normals = interp;

	ufbx_error error = { 0 };
	ufbx_mesh *syn = synth_mesh(mesh);
	ufbx_mesh *sub = ufbx_subdivide_mesh(syn, level, &opts, &error);
	free(syn);
	if (!sub) {
		printf("SUBFAIL ");
		print_string_hex(error.description.data);
		printf("\n");
		return;
	}

	printf("SVP %zu", sub->vertex_position.values.count);
	for (size_t i = 0; i < sub->vertex_position.values.count; i++) {
		ufbx_vec3 v = sub->vertex_position.values.data[i];
		putchar(' '); dbits(v.x); putchar(' '); dbits(v.y); putchar(' '); dbits(v.z);
	}
	printf("\n");

	printf("SVPI %zu", sub->vertex_position.indices.count);
	for (size_t i = 0; i < sub->vertex_position.indices.count; i++) {
		printf(" %u", sub->vertex_position.indices.data[i]);
	}
	printf("\n");

	for (size_t i = 0; i < sub->num_faces; i++) {
		ufbx_face f = sub->faces.data[i];
		printf("SF %zu %u %u\n", i, f.index_begin, f.num_indices);
	}

	printf("SVTX %zu", sub->vertex_indices.count);
	for (size_t i = 0; i < sub->vertex_indices.count; i++) {
		printf(" %u", sub->vertex_indices.data[i]);
	}
	printf("\n");

	printf("SVFI %zu", sub->vertex_first_index.count);
	for (size_t i = 0; i < sub->vertex_first_index.count; i++) {
		printf(" %u", sub->vertex_first_index.data[i]);
	}
	printf("\n");

	// --- topology of the subdivided mesh (same records as the P0 `TOP` dump) --
	// The subdivision levels recompute the topology internally on the *level
	// output* mesh, a state the plain records never cover, so dump it here too.
	{
		ufbx_topo_edge *stopo = malloc(sizeof(ufbx_topo_edge) * (sub->num_indices ? sub->num_indices : 1));
		ufbx_compute_topology(sub, stopo, sub->num_indices);
		for (size_t i = 0; i < sub->num_indices; i++) {
			ufbx_topo_edge t = stopo[i];
			printf("STOP %zu %u %u %u %u %u %u %u\n", i, t.index, t.next, t.prev, t.twin,
				t.face, t.edge, (unsigned)t.flags);
		}
		free(stopo);
	}

	for (size_t i = 0; i < sub->num_edges; i++) {
		printf("SED %u %u\n", sub->edges.data[i].a, sub->edges.data[i].b);
	}

	if (sub->edge_smoothing.data) {
		printf("SES %zu", sub->edge_smoothing.count);
		for (size_t i = 0; i < sub->edge_smoothing.count; i++) printf(" %d", sub->edge_smoothing.data[i] ? 1 : 0);
		printf("\n");
	}
	if (sub->edge_visibility.data) {
		printf("SEV %zu", sub->edge_visibility.count);
		for (size_t i = 0; i < sub->edge_visibility.count; i++) printf(" %d", sub->edge_visibility.data[i] ? 1 : 0);
		printf("\n");
	}
	if (sub->edge_crease.data) {
		printf("SEC %zu", sub->edge_crease.count);
		for (size_t i = 0; i < sub->edge_crease.count; i++) { putchar(' '); dbits(sub->edge_crease.data[i]); }
		printf("\n");
	}
	if (sub->face_smoothing.data) {
		printf("SFS %zu", sub->face_smoothing.count);
		for (size_t i = 0; i < sub->face_smoothing.count; i++) printf(" %d", sub->face_smoothing.data[i] ? 1 : 0);
		printf("\n");
	}
	if (sub->face_material.data) {
		printf("SFM %zu", sub->face_material.count);
		for (size_t i = 0; i < sub->face_material.count; i++) printf(" %u", sub->face_material.data[i]);
		printf("\n");
	}
	if (sub->face_group.data) {
		printf("SGR %zu", sub->face_group.count);
		for (size_t i = 0; i < sub->face_group.count; i++) printf(" %u", sub->face_group.data[i]);
		printf("\n");
	}
	if (sub->face_hole.data) {
		printf("SHL %zu", sub->face_hole.count);
		for (size_t i = 0; i < sub->face_hole.count; i++) printf(" %d", sub->face_hole.data[i] ? 1 : 0);
		printf("\n");
	}

	if (sub->vertex_normal.exists) {
		printf("SVNV %zu", sub->vertex_normal.values.count);
		for (size_t i = 0; i < sub->vertex_normal.values.count; i++) {
			ufbx_vec3 v = sub->vertex_normal.values.data[i];
			putchar(' '); dbits(v.x); putchar(' '); dbits(v.y); putchar(' '); dbits(v.z);
		}
		printf("\n");
		printf("SVNI %zu", sub->vertex_normal.indices.count);
		for (size_t i = 0; i < sub->vertex_normal.indices.count; i++) {
			printf(" %u", sub->vertex_normal.indices.data[i]);
		}
		printf("\n");
	}

	printf("SMP %zu\n", sub->material_parts.count);
	for (size_t pi = 0; pi < sub->material_parts.count; pi++) {
		ufbx_mesh_part *part = &sub->material_parts.data[pi];
		printf("SMPt %zu %zu %zu %zu %zu %zu", pi, part->num_faces, part->num_triangles,
			part->num_empty_faces, part->num_point_faces, part->num_line_faces);
		for (size_t i = 0; i < part->face_indices.count; i++) {
			printf(" %u", part->face_indices.data[i]);
		}
		printf("\n");
	}

	printf("SMU %zu", sub->material_part_usage_order.count);
	for (size_t i = 0; i < sub->material_part_usage_order.count; i++) {
		printf(" %u", sub->material_part_usage_order.data[i]);
	}
	printf("\n");

	printf("SEND\n");
	ufbx_free_mesh(sub);
}

// Deterministic LCG for the generate-index fixtures.
static uint32_t lcg_state;
static uint32_t lcg(void) { lcg_state = lcg_state * 1664525u + 1013904223u; return lcg_state >> 8; }

static void dump_generate_indices(void)
{
	struct { size_t num_streams; size_t sizes[4]; size_t num_indices; uint32_t seed; } cases[] = {
		{ 1, { 12, 0, 0, 0 }, 64, 1 },
		{ 2, { 12, 8, 0, 0 }, 96, 2 },
		{ 3, { 5, 4, 7, 0 }, 80, 3 },
		{ 1, { 24, 0, 0, 0 }, 1000, 4 },
		{ 2, { 3, 16, 0, 0 }, 40, 5 },
	};

	for (size_t ci = 0; ci < sizeof(cases) / sizeof(cases[0]); ci++) {
		size_t num_streams = cases[ci].num_streams;
		size_t num_indices = cases[ci].num_indices;
		lcg_state = cases[ci].seed;

		printf("GINC %zu %zu %zu", ci, num_streams, num_indices);
		for (size_t s = 0; s < num_streams; s++) printf(" %zu", cases[ci].sizes[s]);
		printf("\n");

		ufbx_vertex_stream streams[4];
		uint8_t *orig[4];
		for (size_t s = 0; s < num_streams; s++) {
			size_t vs = cases[ci].sizes[s];
			orig[s] = malloc(vs * num_indices);
			uint8_t *p = orig[s];
			for (size_t r = 0; r < num_indices; r++) {
				if (r > 0 && (lcg() % 4u) == 0) {
					memcpy(p, p - vs, vs);         // duplicate the previous row
				} else {
					for (size_t k = 0; k < vs; k++) p[k] = (uint8_t)lcg();
				}
				p += vs;
			}
			streams[s].data = orig[s];
			streams[s].vertex_count = num_indices;
			streams[s].vertex_size = vs;

			printf("GIND %zu %zu %zu", ci, s, vs * num_indices);
			for (size_t i = 0; i < vs * num_indices; i++) printf(" %02x", orig[s][i]);
			printf("\n");
		}

		uint32_t *indices = malloc(sizeof(uint32_t) * num_indices);
		ufbx_error error = { 0 };
		size_t num_vertices = ufbx_generate_indices(streams, num_streams, indices, num_indices, NULL, &error);
		if (num_vertices == 0 && error.type != UFBX_ERROR_NONE) {
			printf("GINI %zu FAIL ", ci);
			print_string_hex(error.description.data);
			printf("\n");
		} else {
			printf("GINI %zu %zu", ci, num_vertices);
			for (size_t i = 0; i < num_indices; i++) printf(" %u", indices[i]);
			printf("\n");
			for (size_t s = 0; s < num_streams; s++) {
				size_t vs = cases[ci].sizes[s];
				uint8_t *p = orig[s];   // rewritten in place with the deduped vertices
				printf("GINO %zu %zu %zu", ci, s, vs * num_vertices);
				for (size_t i = 0; i < vs * num_vertices; i++) printf(" %02x", p[i]);
				printf("\n");
			}
		}

		for (size_t s = 0; s < num_streams; s++) free(orig[s]);
		free(indices);
	}
}

int main(int argc, char **argv)
{
	setvbuf(stdout, NULL, _IONBF, 0);
	for (int a = 1; a < argc; a++) {
		const char *path = argv[a];
		size_t plen = strlen(path);

		ufbx_load_opts opts = { 0 };
		ufbx_error error = { 0 };
		ufbx_scene *scene = ufbx_load_file(path, &opts, &error);
		if (!scene) {
			printf("SKIP ");
			phex(path, plen);
			putchar(' ');
			phex(error.description.data, error.description.length);
			printf("\n");
			continue;
		}

		printf("SC ");
		phex(path, plen);
		printf(" %zu\n", scene->meshes.count);

		for (size_t i = 0; i < scene->meshes.count; i++) {
			dump_mesh(i, scene->meshes.data[i]);
		}

		dump_nurbs(scene);

		// Subdivision over the corpus meshes (size-gated to keep the dump bounded):
		//   level 1 generated normals for every mesh with <= 20000 indices,
		//   level 2 generated normals for small meshes (<= 2000 indices),
		//   level 1 interpolated normals + boundary variants for tiny meshes (<= 512).
		for (size_t i = 0; i < scene->meshes.count; i++) {
			ufbx_mesh *m = scene->meshes.data[i];
			if (m->num_indices == 0) continue;
			if (m->num_indices <= 20000) {
				dump_subdivide_mesh(i, m, 1, false, UFBX_SUBDIVISION_BOUNDARY_DEFAULT, UFBX_SUBDIVISION_BOUNDARY_DEFAULT);
			}
			if (m->num_indices <= 2000) {
				dump_subdivide_mesh(i, m, 2, false, UFBX_SUBDIVISION_BOUNDARY_DEFAULT, UFBX_SUBDIVISION_BOUNDARY_DEFAULT);
			}
			if (m->num_indices <= 512) {
				dump_subdivide_mesh(i, m, 1, true, UFBX_SUBDIVISION_BOUNDARY_DEFAULT, UFBX_SUBDIVISION_BOUNDARY_DEFAULT);
				dump_subdivide_mesh(i, m, 1, true, UFBX_SUBDIVISION_BOUNDARY_SHARP_BOUNDARY, UFBX_SUBDIVISION_BOUNDARY_SHARP_BOUNDARY);
				dump_subdivide_mesh(i, m, 1, false, UFBX_SUBDIVISION_BOUNDARY_SHARP_INTERIOR, UFBX_SUBDIVISION_BOUNDARY_SHARP_INTERIOR);
				dump_subdivide_mesh(i, m, 1, false, UFBX_SUBDIVISION_BOUNDARY_LEGACY, UFBX_SUBDIVISION_BOUNDARY_LEGACY);
			}
		}

		ufbx_free_scene(scene);
	}

	dump_generate_indices();
	return 0;
}
