static uint64_t bits_of(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static double d_from_bits(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }
static uint64_t rng_state = 0x9e3779b97f4a7c15ull;
static uint64_t rng_next(void) { rng_state ^= rng_state << 13; rng_state ^= rng_state >> 7; rng_state ^= rng_state << 17; return rng_state; }
static double rand_double(uint64_t mode) {
    uint64_t u = rng_next();
    if (mode == 0) { u &= 0x7ff0000000000000ull; u |= 0x0000000000000001ull & rng_next(); }
    else if (mode == 1) { u &= 0x4330000000000000ull; }
    else if (mode == 2) { u &= 0x4030000000000000ull; u |= (rng_next() & 0x000fffffffffffffull); }
    else if (mode == 3) { u &= 0x7ff0000000000000ull; u |= (rng_next() & 0x000fffffffffffffull); if ((u >> 52) == 0x7ff) u &= ~0x000fffffffffffffull; }
    else if (mode == 4) { u &= 0x3ff0000000000000ull; u |= (rng_next() & 0x000fffffffffffffull); }
    else { u &= 0x7fffffffffffffffull; }
    return d_from_bits(u);
}
static void pr(const char *fn, uint64_t arg_bits, uint64_t res_bits) { printf("%s %016llx %016llx\n", fn, (unsigned long long)arg_bits, (unsigned long long)res_bits); }
void run_vectors(void) {
    static const double specials[] = { 0.0, -0.0, 1.0, -1.0, 0.5, 2.0, 1e10, 1e-10, 1e300, 1e-300, 1.0/0.0, -1.0/0.0 };
    for (int i = 0; i < 4000; i++) {
        double x = rand_double(i % 5);
        double y = rand_double((i + 2) % 5);
        if (i < (int)(sizeof(specials)/sizeof(specials[0]))) x = specials[i];
        uint64_t xb = bits_of(x), yb = bits_of(y);
        pr("sqrt", xb, bits_of(ufbx_sqrt(x)));
        pr("abs", xb, bits_of(ufbx_fabs(x)));
        pr("rint", xb, bits_of(ufbx_rint(x)));
        pr("floor", xb, bits_of(ufbx_floor(x)));
        pr("ceil", xb, bits_of(ufbx_ceil(x)));
        pr("isnan", xb, (uint64_t)(uint32_t)ufbx_isnan(x));
        pr("nextafter", xb | yb, bits_of(ufbx_nextafter(x, y)));
        pr("fmin", xb | yb, bits_of(ufbx_fmin(x, y)));
        pr("fmax", xb | yb, bits_of(ufbx_fmax(x, y)));
        pr("copysign", xb | yb, bits_of(ufbx_copysign(x, y)));
        pr("sin", xb, bits_of(ufbx_sin(x)));
        pr("cos", xb, bits_of(ufbx_cos(x)));
        pr("tan", xb, bits_of(ufbx_tan(x)));
        pr("asin", xb, bits_of(ufbx_asin(x)));
        pr("acos", xb, bits_of(ufbx_acos(x)));
        pr("atan", xb, bits_of(ufbx_atan(x)));
        pr("atan2", xb | yb, bits_of(ufbx_atan2(x, y)));
        pr("pow", xb | yb, bits_of(ufbx_pow(x, y)));
    }
}
