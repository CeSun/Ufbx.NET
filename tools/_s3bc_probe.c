#include "ufbx.c"
#include <stdio.h>
int main(int argc, char **argv) {
    ufbx_load_opts opts = { 0 };
    ufbx_error error;
    ufbx_scene *scene = ufbx_load_file(argv[1], &opts, &error);
    if (scene) {
        printf("OK nodes=%zu\n", scene->nodes.count);
        for (size_t i = 0; i < scene->nodes.count; i++) {
            ufbx_node *n = scene->nodes.data[i];
            printf("node %zu id=%u depth=%u parent=%s\n", i, n->typed_id, n->node_depth,
                n->parent ? (const char*)n->parent->name.data : "-");
        }
        ufbx_free_scene(scene);
    } else {
        printf("FAIL type=%d desc=%s\n", (int)error.type, error.description.data);
    }
    return 0;
}
