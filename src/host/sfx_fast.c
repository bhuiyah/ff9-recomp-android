/* Native versions of the effect plugin's two hottest loops. On a frame with a battle effect
   playing, more than half of the plugin's x86-64 instructions (~2.3 M) are spent in:
     A (RVA 0x2da0) ClearOTagR's loop (with the region search at 0x2e07): each of 4095 ordering-table entries gets the 24-bit
       "address" of the entry before it, found by searching the plugin's memory-region table;
     B (RVA 0x1850) the loop that follows a chain of those 24-bit addresses back to pointers.
   Each loop is done here in C, instruction for instruction: the registers, flags and memory
   it leaves are the ones the interpreter would leave, and it returns where the guest code
   continues. Rare paths (an address in no region) go back to the interpreter at the loop.

   A loop's first instruction is replaced in the loaded image by D6 <n>, which x64emu runs as
   hook n. That only happens when the loop's code bytes hash to the known DLL's; any other
   plugin build runs fully interpreted. FF9_TRACE_X64_CHECK=1 runs every call both ways (the
   loop natively, then the original bytes interpreted for the same number of instructions
   from the same state) and reports any difference in registers, flags or memory. */
#include "x64emu.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void sfx_fast_install(X64Cpu *c, uint8_t *image, size_t image_size);

static uint8_t *img;

static inline uint32_t ld32(uint64_t a) { uint32_t v; memcpy(&v, (void *)a, 4); return v; }
static inline uint64_t ld64(uint64_t a) { uint64_t v; memcpy(&v, (void *)a, 8); return v; }
static inline void st32(uint64_t a, uint32_t v) { memcpy((void *)a, &v, 4); }
static inline void st64(uint64_t a, uint64_t v) { memcpy((void *)a, &v, 8); }
#define G(rva) ((uint64_t)img + (rva))

/* flags as x64emu leaves them: test/and/or (CF = OF = 0) and cmp with 0 */
static inline void flags_logic(X64Cpu *c, uint64_t r, int sz) {
    uint64_t m = sz == 8 ? ~0ULL : (1ULL << (sz * 8)) - 1;
    r &= m;
    c->cf = c->of = 0;
    c->zf = r == 0;
    c->sf = (r >> (sz * 8 - 1)) & 1;
    c->pf = !__builtin_parity((unsigned)(r & 0xff));
}

/* ---- A: ClearOTagR loop, 0x2da0..0x2dde with the region search at 0x2e07..0x2e97 ---- */
#define A_HEAD 0x2da0
#define A_DONE 0x2de0
static uint64_t hook_a(X64Cpu *c) {
    uint64_t *r = c->r;
    uint64_t rax = r[RAX], rcx = r[RCX], rdx = r[RDX], rbx = r[RBX], rbp = r[RBP], rsi = r[RSI], rdi = r[RDI];
    uint64_t r8 = r[R8], r9 = r[R9], r10 = r[R10], r11 = r[R11];
    const uint64_t r12 = r[R12], r14 = r[R14];
    uint64_t r15 = r[R15], n = 0;
    const uint64_t table = G(0x576a20);
    for (;;) {
        /* one iteration in locals; dropped if it takes a path not done here */
        uint64_t xrax = (uint64_t)(int64_t)(int32_t)r15;
        uint64_t x8 = r14 + xrax * 4;
        uint64_t x11 = ld32(G(0x5789d0));
        if (!x8 || (int32_t)x11 <= 0) break;
        uint64_t xbx = 0xffffffffULL, xsi = ~0ULL, xdx = 0, x10 = 0, xdi = 0x300000, xcx = rcx, x9 = r9;
        uint64_t m = 4 + 8 + 2;
        xrax = table;
        for (;;) {
            m += 2;
            if (*(uint8_t *)(xrax - 0x10)) {
                x9 = ld64(xrax);
                m += 3;
                if (x8 >= x9) {
                    m += 2;
                    if (x8 < ld64(xrax + 8)) {
                        xcx = x8 - x9;
                        m += 4;
                        if (xdi > xcx) { xdi = xcx; xbx = (uint32_t)xdx; xsi = x10; m += 3; }
                    }
                }
            }
            xdx = (uint32_t)(xdx + 1); x10++; xrax += 0x20;
            m += 5;
            if (!((int32_t)xdx < (int32_t)x11)) break;
        }
        if ((int32_t)xbx < 0) break;
        m += 2 + 2;
        if ((uint32_t)xbx == 0x80) {
            x8 = (uint32_t)((uint32_t)x8 - ld32(G(0x5789e0))) | 0x80000000u;
            m += 3;
        } else {
            xsi <<= 5;
            xbx = (uint32_t)(xbx << 24);
            x8 = (uint32_t)((uint32_t)x8 - ld32(xsi + r12 + 8));
            xbx |= 0xc00000;
            x8 = (uint32_t)(x8 | xbx);
            m += 6;
        }
        /* 0x2dc9: commit */
        x8 &= 0xffffff;
        rbp--;
        r15 = (uint32_t)(r15 - 1);
        st32(r14 + rbp * 4 + 4, (uint32_t)x8);
        m += 6;
        rax = xrax; rcx = xcx; rdx = xdx; rbx = xbx; rsi = xsi; rdi = xdi; r8 = x8; r9 = x9; r10 = x10; r11 = x11;
        n += m;
        if ((int64_t)rbp <= 0) {
            flags_logic(c, rbp, 8);
            c->rip = G(A_DONE);
            goto out;
        }
    }
    /* back to the interpreter at the loop head: do its first instruction (movslq, no flags) */
    rax = (uint64_t)(int64_t)(int32_t)r15;
    n++;
    c->rip = G(A_HEAD + 3);
out:
    r[RAX] = rax; r[RCX] = rcx; r[RDX] = rdx; r[RBX] = rbx; r[RBP] = rbp; r[RSI] = rsi; r[RDI] = rdi;
    r[R8] = r8; r[R9] = r9; r[R10] = r10; r[R11] = r11; r[R15] = r15;
    return n;
}

/* ---- B: the 24-bit address chain walk, 0x1850..0x18df, leaving at 0x18e5 or 0x18fc ---- */
#define B_HEAD 0x1850
static uint64_t hook_b(X64Cpu *c) {
    uint64_t *r = c->r;
    uint64_t rax = r[RAX], rcx = r[RCX], rdx = r[RDX], r8 = r[R8], r9 = r[R9];
    const uint64_t rbx = r[RBX], rbp = r[RBP], rsi = r[RSI], r10 = r[R10], r11 = r[R11];
    const uint64_t g_cur = G(0x171db8), g_left = G(0x6972c);
    uint64_t n = 0;
    for (;;) {
        rdx = ld32(rax);
        r9 = rax;
        rdx &= 0xffffff;
        n += 5;
        if (rdx == 0xffffff) { rax = 0; n++; goto link; }
        rdx = (uint32_t)(rdx | r10);
        n += 2;
        if (!rdx) { rax = 0; n++; goto link; }
        rcx = rdx; rax = rdx;
        rcx >>= 24;
        n += 5;
        if (rcx == 0x80) {
            rax &= 0xfffffff;
            n += 3;
            if (rax >= 0x200000) goto low;
            rdx = (uint32_t)(rdx - (uint32_t)rbx);
            rax = rdx + r11;
            n += 4;
            goto link;
        }
        rax &= 0xc00000;
        n += 3;
        if (rax != 0xc00000) goto low;
        n += 2;                                    /* test ecx / js: ecx < 256 */
        rax = rdx;
        rcx <<= 5;
        rax &= 0x3fffff;
        rax += ld64(rcx + rbp);
        n += 5;
        goto link;
    low:
        rdx = (uint32_t)(rdx + 0xe0800000u);
        n += 3;
        if ((uint32_t)rdx > 0x3ff) { rax = 0; n++; goto link; }
        rax = rdx + rsi;
        n += 3;
    link: {
        uint8_t last = *(uint8_t *)(r9 + 3);
        st64(g_cur, rax);
        n += 3;
        if (last) {                                /* cmpb $0, 3(%r9) */
            flags_logic(c, last, 1);
            c->rip = G(0x18fc);
            break;
        }
        r8 = (uint32_t)(r8 - 1);
        st32(g_left, (uint32_t)r8);
        n += 4;
        if (!rax) { flags_logic(c, 0, 8); c->rip = G(0x18e5); break; }
    }
    }
    r[RAX] = rax; r[RCX] = rcx; r[RDX] = rdx; r[R8] = r8; r[R9] = r9;
    return n;
}

/* ---- install and check ---- */
static const struct { uint32_t lo, hi; uint64_t hash; X64Hook fn; } loops[] = {
    {A_HEAD, 0x2e9c, 0xf81064eedc847a0bULL, hook_a},
    {B_HEAD, 0x1908, 0xae3455cef8311d34ULL, hook_b},
};
#define NLOOPS (int)(sizeof loops / sizeof loops[0])
static uint8_t orig[NLOOPS][2];
static X64Hook native[NLOOPS];
static uint64_t checks, diffs, runs[NLOOPS];

static uint64_t fnv(const uint8_t *p, size_t n) {
    uint64_t h = 0xcbf29ce484222325ULL;
    for (size_t i = 0; i < n; i++) h = (h ^ p[i]) * 0x100000001b3ULL;
    return h;
}

/* the memory a loop writes, from the state at its head */
static int writes(int k, const X64Cpu *c, uint64_t *at, size_t *len) {
    if (k == 0) { at[0] = c->r[R14] + 4; len[0] = (size_t)c->r[RBP] * 4; return (int64_t)c->r[RBP] > 0 && c->r[RBP] < (1 << 20) ? 1 : 0; }
    at[0] = G(0x171db8); len[0] = 8;
    at[1] = G(0x6972c); len[1] = 4;
    return 2;
}

static uint64_t check(X64Cpu *c, int k) {
    uint64_t at[2]; size_t len[2];
    int nw = writes(k, c, at, len);
    uint8_t *before[2], *after[2];
    for (int i = 0; i < nw; i++) { before[i] = malloc(len[i]); after[i] = malloc(len[i]); memcpy(before[i], (void *)at[i], len[i]); }
    X64Cpu start = *c;
    uint64_t n = native[k](c);
    X64Cpu fast = *c;
    for (int i = 0; i < nw; i++) { memcpy(after[i], (void *)at[i], len[i]); memcpy((void *)at[i], before[i], len[i]); }
    *c = start;
    uint8_t *head = img + loops[k].lo;
    memcpy(head, orig[k], 2);
    for (uint64_t i = 0; i < n && !c->error; i++) x64_step(c);
    head[0] = 0xD6; head[1] = (uint8_t)k;
    c->steps -= n;                                 /* x64emu counts what the hook returns */
    int bad = c->rip != fast.rip || memcmp(c->r, fast.r, sizeof c->r) || c->cf != fast.cf || c->zf != fast.zf ||
              c->sf != fast.sf || c->of != fast.of || c->pf != fast.pf;
    for (int i = 0; i < nw; i++) bad |= memcmp(after[i], (void *)at[i], len[i]) != 0;
    checks++; runs[k]++;
    if (bad && diffs++ < 20) {
        fprintf(stderr, "x64 check: loop %c differs after %llu instructions (rip %llx vs %llx)\n", 'A' + k,
                (unsigned long long)n, (unsigned long long)(fast.rip - (uint64_t)img), (unsigned long long)(c->rip - (uint64_t)img));
        for (int i = 0; i < 16; i++)
            if (c->r[i] != fast.r[i]) fprintf(stderr, "  r%d %llx vs %llx\n", i, (unsigned long long)fast.r[i], (unsigned long long)c->r[i]);
        fprintf(stderr, "  flags c%d z%d s%d o%d p%d vs c%d z%d s%d o%d p%d\n", fast.cf, fast.zf, fast.sf, fast.of, fast.pf, c->cf, c->zf, c->sf, c->of, c->pf);
    }
    if (checks % 2000 == 0) printf("x64 check: %llu loop runs compared (A %llu, B %llu), %llu differ\n", (unsigned long long)checks,
                                    (unsigned long long)runs[0], (unsigned long long)runs[1], (unsigned long long)diffs);
    for (int i = 0; i < nw; i++) { free(before[i]); free(after[i]); }
    return n;
}
static uint64_t check_a(X64Cpu *c) { return check(c, 0); }
static uint64_t check_b(X64Cpu *c) { return check(c, 1); }

void sfx_fast_install(X64Cpu *c, uint8_t *image, size_t image_size) {
    img = image;
    if (getenv("FF9_TRACE_X64_SLOW")) return;
    int on = getenv("FF9_TRACE_X64_CHECK") != NULL, n = 0;
    for (int k = 0; k < NLOOPS; k++) {
        if (loops[k].hi > image_size || fnv(image + loops[k].lo, loops[k].hi - loops[k].lo) != loops[k].hash) continue;
        memcpy(orig[k], image + loops[k].lo, 2);
        native[k] = loops[k].fn;
        c->hooks[k] = on ? (k == 0 ? check_a : check_b) : loops[k].fn;
        image[loops[k].lo] = 0xD6;
        image[loops[k].lo + 1] = (uint8_t)k;
        n++;
    }
    printf("sfx plugin: %d of %d hot loops run natively%s\n", n, NLOOPS, on && n ? " (checked against the interpreter)" : "");
}
