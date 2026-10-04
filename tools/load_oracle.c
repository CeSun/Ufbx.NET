// C reference oracle for the load spine: `ufbx_load_file()` (ufbx.c:30520-30523) ->
// `ufbx_load_file_len()` (30525-30534) -> `ufbxi_load()` (25480-25632) ->
// `ufbxi_load_imp()` (25226-25418) down to the scene-content seam
// (`ufbxi_begin_parse`/`ufbxi_read_root`/`ufbxi_obj_load`/`ufbxi_mtl_load`).
//
// Unlike the DOM oracle it calls the *internal* `ufbxi_load()` with its own
// `ufbxi_context`, exactly the way `ufbx_load_file_len()` does (ufbx.c:30528-30533), because the
// state the port has to match at the seam lives in the context: `uc->version`, `uc->from_ascii`,
// `uc->file_big_endian` and `uc->scene.metadata.file_format`. Those stay readable after a failed
// load (`ufbx_scene metadata` is embedded in `uc->scene`, and `ufbxi_free_result()` only releases
// the arenas), which is what makes the record usable for C failures too -- the public API gives no
// way to see them. C copies `metadata.version/ascii/big_endian` only at ufbx.c:25380-25382, past
// the seam, so the port must be compared against the context fields, not the returned metadata.
//
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx tools/load_oracle.c -o tools/load_oracle.exe
//   cd C:/Workspace/_analyze_ufbx && C:/Workspace/ufbx-cs/tools/load_oracle.exe > C:/Workspace/ufbx-cs/tools/load_oracle.txt
//
// Run from the ufbx checkout root so the `data/...` paths resolve. `--list <file>` names a
// different newline-separated path list; any other argv is taken as an explicit file list.
//
// Output grammar (one record per file, space-separated; `<hex>` is lowercase nibbles):
//   L <file> <ok> <format> <ascii> <big_endian> <sure_fbx> <version> <err_type> <desc_len>
//     <desc_in_info> <desc_digest> <desc-hex> <info_len> <info_digest> <info-hex>
//     `format`/`ascii`/`big_endian`/`sure_fbx`/`version` are the context's own fields, so they
//     carry the format-detection result even when the load failed; `format` is
//     UFBX_FILE_FORMAT_UNKNOWN (0) when `ufbxi_determine_format()` failed. `sure_fbx` is set
//     only by `ufbxi_begin_parse()` (ufbx.c:11211 binary, 11228 ASCII-with-version), so a
//     record with `format == FBX && sure_fbx` proves C got past the pre-seam parsing and its
//     failure is inside a toplevel reader; see the classification in tools/LoadCheck.
//     `err_*` describes the public `ufbx_error` contract; on success every error field is fixed
//     zeros, so a stale `ufbx_error` cannot leak into the comparison. `desc_in_info` is 1 when
//     `description.data` aliases `info`, which the port models with the same two-field shape.
//
// Payloads longer than 64 bytes print the `-` placeholder (an embedded path in `info` can be
// long); the length and FNV-1a-64 digest stay exact.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ufbx.c"

static uint64_t fnv64(const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	uint64_t h = 0xcbf29ce484222325ull;
	for (size_t i = 0; i < n; i++) h = (h ^ b[i]) * 0x00000100000001b3ull;
	return h;
}

static char hex_buf[256];

static char *emit_hex_into(char *out, const void *p, size_t n)
{
	const uint8_t *b = (const uint8_t*)p;
	for (size_t i = 0; i < n; i++) sprintf(out + i * 2, "%02x", b[i]);
	out[n * 2] = 0;
	return out;
}

// Emit `len digest hex-or-placeholder`: bytes inline up to 64, digest-only beyond. Every field is
// a token, so a 0-byte description still cannot shift the columns after it.
static void emit_payload(const char *data, size_t len)
{
	printf(" %zu %016llx", len, (unsigned long long)fnv64(data, len));
	if (data && len > 0 && len <= 64) printf(" %s", emit_hex_into(hex_buf, data, len));
	else printf(" -");
}

// The same two trailing tokens without the length, for a field whose length is printed as its own
// column (`description.length` and the `description.data == info` alias flag).
static void emit_digest_and_hex(const char *data, size_t len)
{
	printf(" %016llx", (unsigned long long)fnv64(data, len));
	if (data && len > 0 && len <= 64) printf(" %s", emit_hex_into(hex_buf, data, len));
	else printf(" -");
}

static void run_file(const char *path, int fi)
{
	ufbxi_context uc; // ufbxi_uninit
	memset(&uc, 0, sizeof(ufbxi_context));
	uc.deferred_load = true;
	uc.load_filename = path;
	uc.load_filename_len = SIZE_MAX;

	ufbx_load_opts opts;
	memset(&opts, 0, sizeof(opts));

	ufbx_error error;
	memset(&error, 0, sizeof(error));

	ufbx_scene *scene = ufbxi_load(&uc, &opts, &error);

	bool ok = scene != NULL;
	printf("L %d %d %d %d %d %d %u", fi, ok ? 1 : 0,
		(int)uc.scene.metadata.file_format, uc.from_ascii ? 1 : 0,
		uc.file_big_endian ? 1 : 0, uc.sure_fbx ? 1 : 0, uc.version);

	if (ok) {
		printf(" 0 0 0 0000000000000000 - 0 0000000000000000 -\n");
	} else {
		printf(" %d", (int)error.type);
		printf(" %zu %d", error.description.length, error.description.data == error.info ? 1 : 0);
		emit_digest_and_hex(error.description.data, error.description.length);
		emit_payload(error.info, error.info_length);
		printf("\n");
	}

	if (scene) ufbx_free_scene(scene);
}

int main(int argc, char **argv)
{
	const char *list_path = "tools/load_corpus.txt";
	if (argc > 2 && !strcmp(argv[1], "--list")) {
		list_path = argv[2];
		argc = 1;
	}

	if (argc > 1) {
		for (int i = 1; i < argc; i++) run_file(argv[i], i - 1);
		return 0;
	}

	FILE *f = fopen(list_path, "rb");
	if (!f) {
		fprintf(stderr, "cannot open %s\n", list_path);
		return 2;
	}

	char line[4096];
	int fi = 0;
	while (fgets(line, sizeof(line), f)) {
		size_t len = strlen(line);
		while (len > 0 && (line[len - 1] == '\n' || line[len - 1] == '\r')) line[--len] = 0;
		if (len == 0) continue;
		run_file(line, fi++);
	}
	fclose(f);
	fprintf(stderr, "load_oracle: %d files\n", fi);
	return 0;
}
