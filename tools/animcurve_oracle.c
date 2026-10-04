// animcurve_oracle.c -- bit-exact reference vectors for the S2 animation tangent solvers.
//
// Includes ufbx.c directly (single translation unit) and calls the static
// ufbxi_solve_auto_tangent{,_left,_right} / ufbxi_solve_tcb internals, so the expected
// values come from the real C implementation, not a re-implementation.
//
// Output: tools/animcurve_oracle.txt, one record per line. All floats/doubles are printed
// as IEEE-754 bit patterns (hex) so the C# side can compare bitwise without any decimal
// round-trip:
//   A <flags:8> <prev_time:16> <time:16> <next_time:16> <prev_value:16> <value:16>
//     <next_value:16> <weight_left:8> <weight_right:8> <auto_bias:8> <result:8>
//   L <flags:8> <prev_time:16> <time:16> <prev_value:16> <value:16>
//     <weight_left:8> <auto_bias:8> <result:8>
//   R <flags:8> <time:16> <next_time:16> <value:16> <next_value:16>
//     <weight_right:8> <auto_bias:8> <result:8>
//   T <tension:16> <continuity:16> <bias:16> <slope_left:16> <slope_right:16>
//     <edge:1> <out_left:8> <out_right:8>
//
// Build (must match the frozen no-contraction reference semantics, PORTING_NOTES.md):
//   zig cc -O2 -std=c11 -mcpu=x86_64 -ffp-contract=off -I C:/Workspace/_analyze_ufbx \
//       tools/animcurve_oracle.c -o tools/animcurve_oracle.exe
//   C:/Workspace/ufbx-cs/tools/animcurve_oracle.exe > C:/Workspace/ufbx-cs/tools/animcurve_oracle.txt
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include "ufbx.c"

static uint64_t rng_state = 0x9e3779b97f4a7c15ull;
static uint64_t rng_next(void) {
    // xorshift64*
    rng_state ^= rng_state >> 12;
    rng_state ^= rng_state << 25;
    rng_state ^= rng_state >> 27;
    return rng_state * 0x2545f4914f6cdd1dull;
}
static uint32_t rng_below(uint32_t n) { return (uint32_t)(rng_next() % n); }
static double rng_double01(void) {
    // Uniform in [0, 1), 52 random mantissa bits
    return (double)(rng_next() >> 11) * (1.0 / 9007199254740992.0);
}

static void pd(double v) { uint64_t u; memcpy(&u, &v, 8); printf("%016llx", (unsigned long long)u); }
static void pf(float v) { uint32_t u; memcpy(&u, &v, 4); printf("%08x", (unsigned)u); }

// The solver only inspects these three bits; sample them densely, keep other (ignored)
// bits as noise so the port's masking is exercised too.
static const uint32_t flag_combos[] = {
    0x0,
    UFBXI_KEY_CLAMP,
    UFBXI_KEY_TIME_INDEPENDENT,
    UFBXI_KEY_CLAMP_PROGRESSIVE,
    UFBXI_KEY_CLAMP | UFBXI_KEY_TIME_INDEPENDENT,
    UFBXI_KEY_CLAMP | UFBXI_KEY_CLAMP_PROGRESSIVE,
    UFBXI_KEY_TIME_INDEPENDENT | UFBXI_KEY_CLAMP_PROGRESSIVE,
    UFBXI_KEY_CLAMP | UFBXI_KEY_TIME_INDEPENDENT | UFBXI_KEY_CLAMP_PROGRESSIVE,
};
#define NUM_FLAG_COMBOS (sizeof(flag_combos) / sizeof(flag_combos[0]))

int main(void) {
    // The solvers read only uc->opts.key_clamp_threshold. ufbxi_load() never assigns it,
    // so the loader default is the zero-initialized 0.0 -- mirror that here.
    ufbxi_context uc;
    memset(&uc, 0, sizeof(uc));

    // "Interesting" value/time deltas: 0 (degenerate/equal keys), tiny, and large.
    static const double deltas[] = { 0.0, 1e-9, 0.25, 1.0, 7.5, 1000.0 };
    const uint32_t num_deltas = (uint32_t)(sizeof(deltas) / sizeof(deltas[0]));

    enum { N_A = 3000, N_L = 1500, N_R = 1500, N_T = 2000 };

    for (int i = 0; i < N_A; i++) {
        uint32_t flags = flag_combos[rng_below(NUM_FLAG_COMBOS)];
        if (rng_below(2)) flags |= (rng_next() & 0x9004f130u); // noise bits (incl. weighted/auto/tcb)
        flags &= ~0x2u; // keep INTERPOLATION_CONSTANT off: it never reaches the solver

        double time = (double)rng_below(200) + rng_double01();
        double prev_time = time - deltas[rng_below(num_deltas)];
        double next_time = time + deltas[rng_below(num_deltas)];
        if (rng_below(16) == 0) { double t = prev_time; prev_time = next_time; next_time = t; }

        double value = (double)(int32_t)rng_next() / 512.0;
        double prev_value = value + ((double)rng_below(8) - 4.0) * deltas[rng_below(num_deltas)];
        double next_value = value + ((double)rng_below(8) - 4.0) * deltas[rng_below(num_deltas)];
        if (rng_below(32) == 0) prev_value = value;   // exact equality -> CLAMP threshold path
        if (rng_below(32) == 1) next_value = value;

        float weight_left = (float)rng_double01();
        float weight_right = (float)rng_double01();
        if (rng_below(8) == 0) weight_left = 0.333333f;
        if (rng_below(8) == 0) weight_right = 0.333333f;
        if (rng_below(16) == 0) weight_left = 0.0f;
        if (rng_below(16) == 0) weight_right = 0.0f;

        // auto_bias across the ±500 kink and zero
        float auto_bias;
        switch (rng_below(6)) {
        case 0: auto_bias = 0.0f; break;
        case 1: auto_bias = 500.0f; break;
        case 2: auto_bias = -500.0f; break;
        case 3: auto_bias = 1000.0f; break;
        case 4: auto_bias = -1000.0f; break;
        default: auto_bias = (float)((double)rng_below(4001) - 2000.0); break;
        }

        float result = ufbxi_solve_auto_tangent(&uc, prev_time, time, next_time,
            prev_value, value, next_value, weight_left, weight_right, auto_bias, flags);

        printf("A %08x ", flags);
        pd(prev_time); printf(" "); pd(time); printf(" "); pd(next_time); printf(" ");
        pd(prev_value); printf(" "); pd(value); printf(" "); pd(next_value); printf(" ");
        pf(weight_left); printf(" "); pf(weight_right); printf(" "); pf(auto_bias); printf(" ");
        pf(result); printf("\n");
    }

    for (int i = 0; i < N_L; i++) {
        uint32_t flags = flag_combos[rng_below(NUM_FLAG_COMBOS)];
        if (rng_below(2)) flags |= (rng_next() & 0x9004f130u);

        double time = (double)rng_below(200) + rng_double01();
        double prev_time = time - deltas[rng_below(num_deltas)];

        double value = (double)(int32_t)rng_next() / 512.0;
        double prev_value = value + ((double)rng_below(8) - 4.0) * deltas[rng_below(num_deltas)];
        if (rng_below(32) == 0) prev_value = value;

        float weight_left = (float)rng_double01();
        float auto_bias;
        switch (rng_below(6)) {
        case 0: auto_bias = 0.0f; break;
        case 1: auto_bias = 500.0f; break;
        case 2: auto_bias = -500.0f; break;
        case 3: auto_bias = 1000.0f; break;
        case 4: auto_bias = -1000.0f; break;
        default: auto_bias = (float)((double)rng_below(4001) - 2000.0); break;
        }

        float result = ufbxi_solve_auto_tangent_left(&uc, prev_time, time,
            prev_value, value, weight_left, auto_bias, flags);

        printf("L %08x ", flags);
        pd(prev_time); printf(" "); pd(time); printf(" ");
        pd(prev_value); printf(" "); pd(value); printf(" ");
        pf(weight_left); printf(" "); pf(auto_bias); printf(" ");
        pf(result); printf("\n");
    }

    for (int i = 0; i < N_R; i++) {
        uint32_t flags = flag_combos[rng_below(NUM_FLAG_COMBOS)];
        if (rng_below(2)) flags |= (rng_next() & 0x9004f130u);

        double time = (double)rng_below(200) + rng_double01();
        double next_time = time + deltas[rng_below(num_deltas)];

        double value = (double)(int32_t)rng_next() / 512.0;
        double next_value = value + ((double)rng_below(8) - 4.0) * deltas[rng_below(num_deltas)];
        if (rng_below(32) == 0) next_value = value;

        float weight_right = (float)rng_double01();
        float auto_bias;
        switch (rng_below(6)) {
        case 0: auto_bias = 0.0f; break;
        case 1: auto_bias = 500.0f; break;
        case 2: auto_bias = -500.0f; break;
        case 3: auto_bias = 1000.0f; break;
        case 4: auto_bias = -1000.0f; break;
        default: auto_bias = (float)((double)rng_below(4001) - 2000.0); break;
        }

        float result = ufbxi_solve_auto_tangent_right(&uc, time, next_time,
            value, next_value, weight_right, auto_bias, flags);

        printf("R %08x ", flags);
        pd(time); printf(" "); pd(next_time); printf(" ");
        pd(value); printf(" "); pd(next_value); printf(" ");
        pf(weight_right); printf(" "); pf(auto_bias); printf(" ");
        pf(result); printf("\n");
    }

    for (int i = 0; i < N_T; i++) {
        double tension = (double)rng_below(41) / 10.0 - 2.0;      // [-2, 2] in 0.1 steps
        double continuity = (double)rng_below(41) / 10.0 - 2.0;
        double bias = (double)rng_below(41) / 10.0 - 2.0;
        if (rng_below(8) == 0) tension = (double)rng_next() / (double)rng_next(); // stress
        if (rng_below(8) == 0) continuity = (double)rng_next() / (double)rng_next();
        if (rng_below(8) == 0) bias = (double)rng_next() / (double)rng_next();

        double slope_left = (double)(int32_t)rng_next() / 1024.0;
        double slope_right = (double)(int32_t)rng_next() / 1024.0;
        if (rng_below(16) == 0) slope_left = 0.0;
        if (rng_below(16) == 0) slope_right = 0.0;
        bool edge = (rng_below(2) & 1) != 0;

        float out_left, out_right;
        ufbxi_solve_tcb(&out_left, &out_right, tension, continuity, bias, slope_left, slope_right, edge);

        printf("T ");
        pd(tension); printf(" "); pd(continuity); printf(" "); pd(bias); printf(" ");
        pd(slope_left); printf(" "); pd(slope_right); printf(" ");
        printf("%u ", edge ? 1u : 0u);
        pf(out_left); printf(" "); pf(out_right); printf("\n");
    }

    return 0;
}
