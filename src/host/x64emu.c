/* x64emu: see x64emu.h. One instruction per step(): prefixes, REX, ModRM/SIB, then the
   opcode. Flags are computed eagerly (CF ZF SF OF PF; AF is not kept). */
#include "x64emu.h"
/* Speed: step() and its decode/flag helpers are forced inline into x64_run's loop, so each
   instruction doesn't pay a call that saves and restores ten registers (a heavy battle effect
   such as Meteor runs ~1.5 M instructions a frame). */
#include <math.h>
#include <stdio.h>
#include <string.h>

static inline uint64_t rd(uint64_t a, int sz) {
    switch (sz) {
    case 1: return *(uint8_t *)a;
    case 2: { uint16_t v; memcpy(&v, (void *)a, 2); return v; }
    case 4: { uint32_t v; memcpy(&v, (void *)a, 4); return v; }
    default: { uint64_t v; memcpy(&v, (void *)a, 8); return v; }
    }
}
static inline void wr(uint64_t a, int sz, uint64_t v) {
    switch (sz) {
    case 1: *(uint8_t *)a = (uint8_t)v; break;
    case 2: { uint16_t t = (uint16_t)v; memcpy((void *)a, &t, 2); break; }
    case 4: { uint32_t t = (uint32_t)v; memcpy((void *)a, &t, 4); break; }
    default: memcpy((void *)a, &v, 8); break;
    }
}
static inline uint64_t mask(int sz) { return sz == 8 ? ~0ULL : (1ULL << (sz * 8)) - 1; }
static inline uint64_t sbit(int sz) { return 1ULL << (sz * 8 - 1); }
static inline int64_t sext(uint64_t v, int sz) {
    switch (sz) { case 1: return (int8_t)v; case 2: return (int16_t)v; case 4: return (int32_t)v; default: return (int64_t)v; }
}

/* ---- registers by size (8-bit: ah..bh without REX) ---- */
static inline uint64_t getr(X64Cpu *c, int r, int sz, int rex) {
    if (sz == 1 && !rex && r >= 4 && r < 8) return (c->r[r - 4] >> 8) & 0xff;
    return c->r[r] & mask(sz);
}
static inline void setr(X64Cpu *c, int r, int sz, int rex, uint64_t v) {
    switch (sz) {
    case 1:
        if (!rex && r >= 4 && r < 8) c->r[r - 4] = (c->r[r - 4] & ~0xff00ULL) | ((v & 0xff) << 8);
        else c->r[r] = (c->r[r] & ~0xffULL) | (v & 0xff);
        break;
    case 2: c->r[r] = (c->r[r] & ~0xffffULL) | (v & 0xffff); break;
    case 4: c->r[r] = (uint32_t)v; break;
    default: c->r[r] = v; break;
    }
}

/* ---- flags ---- */
static inline void szp(X64Cpu *c, uint64_t r, int sz) {
    r &= mask(sz);
    c->zf = r == 0;
    c->sf = (r & sbit(sz)) != 0;
    c->pf = !__builtin_parity((unsigned)(r & 0xff));
}
static inline __attribute__((always_inline)) uint64_t add_f(X64Cpu *c, uint64_t a, uint64_t b, int cin, int sz) {
    uint64_t m = mask(sz);
    a &= m; b &= m;
    uint64_t r = (a + b + cin) & m;
    if (sz == 8) c->cf = r < a || (cin && r == a);
    else c->cf = ((a + b + cin) >> (sz * 8)) & 1;
    c->of = ((a ^ r) & (b ^ r) & sbit(sz)) != 0;
    szp(c, r, sz);
    return r;
}
static inline __attribute__((always_inline)) uint64_t sub_f(X64Cpu *c, uint64_t a, uint64_t b, int cin, int sz) {
    uint64_t m = mask(sz);
    a &= m; b &= m;
    uint64_t r = (a - b - cin) & m;
    if (sz == 8) c->cf = a < b || (cin && a == b);
    else c->cf = a < b + (uint64_t)cin;
    c->of = ((a ^ b) & (a ^ r) & sbit(sz)) != 0;
    szp(c, r, sz);
    return r;
}
static inline __attribute__((always_inline)) uint64_t logic_f(X64Cpu *c, uint64_t r, int sz) { c->cf = c->of = 0; szp(c, r, sz); return r & mask(sz); }

static inline __attribute__((always_inline)) int cond(X64Cpu *c, int cc) {
    int r;
    switch (cc >> 1) {
    case 0: r = c->of; break;
    case 1: r = c->cf; break;
    case 2: r = c->zf; break;
    case 3: r = c->cf || c->zf; break;
    case 4: r = c->sf; break;
    case 5: r = c->pf; break;
    case 6: r = c->sf != c->of; break;
    default: r = c->zf || (c->sf != c->of); break;
    }
    return (cc & 1) ? !r : r;
}

static inline __attribute__((always_inline)) uint64_t alu(X64Cpu *c, int op, uint64_t a, uint64_t b, int sz) {
    switch (op) {
    case 0: return add_f(c, a, b, 0, sz);
    case 1: return logic_f(c, a | b, sz);
    case 2: return add_f(c, a, b, c->cf, sz);
    case 3: return sub_f(c, a, b, c->cf, sz);
    case 4: return logic_f(c, a & b, sz);
    case 5: return sub_f(c, a, b, 0, sz);
    case 6: return logic_f(c, a ^ b, sz);
    default: sub_f(c, a, b, 0, sz); return a;    /* cmp */
    }
}

static uint64_t shift(X64Cpu *c, int op, uint64_t v, unsigned n, int sz) {
    int bits = sz * 8;
    n &= sz == 8 ? 63 : 31;
    if (!n) return v & mask(sz);
    uint64_t m = mask(sz), r;
    v &= m;
    switch (op) {
    case 0: /* rol */
        n %= bits; r = n ? ((v << n) | (v >> (bits - n))) & m : v;
        c->cf = r & 1; c->of = ((r >> (bits - 1)) & 1) ^ c->cf; return r;
    case 1: /* ror */
        n %= bits; r = n ? ((v >> n) | (v << (bits - n))) & m : v;
        c->cf = (r >> (bits - 1)) & 1; c->of = c->cf ^ ((r >> (bits - 2)) & 1); return r;
    case 2: { /* rcl */
        r = v;
        for (unsigned i = 0; i < n; i++) { int top = (r >> (bits - 1)) & 1; r = ((r << 1) | c->cf) & m; c->cf = top; }
        c->of = ((r >> (bits - 1)) & 1) ^ c->cf; return r;
    }
    case 3: { /* rcr */
        r = v;
        for (unsigned i = 0; i < n; i++) { int low = r & 1; r = (r >> 1) | ((uint64_t)c->cf << (bits - 1)); c->cf = low; }
        c->of = ((r >> (bits - 1)) ^ (r >> (bits - 2))) & 1; return r;
    }
    case 4: case 6: /* shl */
        c->cf = n <= (unsigned)bits ? (v >> (bits - n)) & 1 : 0;
        r = n >= 64 ? 0 : (v << n) & m;
        c->of = ((r >> (bits - 1)) & 1) ^ c->cf; szp(c, r, sz); return r;
    case 5: /* shr */
        c->cf = (v >> (n - 1)) & 1; r = v >> n;
        c->of = (v >> (bits - 1)) & 1; szp(c, r, sz); return r;
    default: { /* sar */
        int64_t s = sext(v, sz);
        c->cf = (s >> (n - 1)) & 1; r = (uint64_t)(s >> n) & m;
        c->of = 0; szp(c, r, sz); return r;
    }
    }
}

/* ---- decoding ---- */
typedef struct {
    int rex, w, opsz, rep, repne, seg;      /* seg: 0, 'f', 'g' */
    int mod, reg, rm, isreg;
    uint64_t addr;
} Ins;

/* ModRM at p; `imm` = bytes of immediate that follow (for RIP-relative). Returns the byte after the ModRM/SIB/disp. */
static inline __attribute__((always_inline)) uint8_t *modrm(X64Cpu *c, uint8_t *p, Ins *in, int imm) {
    uint8_t m = *p++;
    in->mod = m >> 6;
    in->reg = ((m >> 3) & 7) | ((in->rex & 4) << 1);
    int rm = m & 7;
    if (in->mod == 3) { in->isreg = 1; in->rm = rm | ((in->rex & 1) << 3); return p; }
    in->isreg = 0;
    uint64_t a = 0;
    if (rm == 4) {
        uint8_t sib = *p++;
        int scale = sib >> 6, idx = ((sib >> 3) & 7) | ((in->rex & 2) << 2), base = (sib & 7) | ((in->rex & 1) << 3);
        if (idx != 4) a += c->r[idx] << scale;
        if ((base & 7) == 5 && in->mod == 0) { int32_t d; memcpy(&d, p, 4); p += 4; a += (int64_t)d; }
        else a += c->r[base];
    } else if (rm == 5 && in->mod == 0) {
        int32_t d; memcpy(&d, p, 4); p += 4;
        a = (uint64_t)(p + imm) + (int64_t)d;      /* RIP-relative: from the next instruction */
        in->addr = a;
        if (in->seg == 'g') in->addr += c->gsbase; else if (in->seg == 'f') in->addr += c->fsbase;
        return p;
    } else a += c->r[rm | ((in->rex & 1) << 3)];
    if (in->mod == 1) { a += (int64_t)(int8_t)*p++; }
    else if (in->mod == 2) { int32_t d; memcpy(&d, p, 4); p += 4; a += (int64_t)d; }
    if (in->seg == 'g') a += c->gsbase; else if (in->seg == 'f') a += c->fsbase;
    in->addr = a;
    return p;
}
static inline __attribute__((always_inline)) uint64_t getrm(X64Cpu *c, Ins *in, int sz) { return in->isreg ? getr(c, in->rm, sz, in->rex) : rd(in->addr, sz); }
static inline __attribute__((always_inline)) void setrm(X64Cpu *c, Ins *in, int sz, uint64_t v) { if (in->isreg) setr(c, in->rm, sz, in->rex, v); else wr(in->addr, sz, v); }
static inline uint64_t imm(uint8_t **pp, int sz) {
    uint8_t *p = *pp; uint64_t v;
    switch (sz) {
    case 1: v = (uint64_t)(int64_t)(int8_t)p[0]; break;
    case 2: { int16_t t; memcpy(&t, p, 2); v = (uint64_t)(int64_t)t; break; }
    default: { int32_t t; memcpy(&t, p, 4); v = (uint64_t)(int64_t)t; sz = 4; break; }
    }
    *pp = p + sz;
    return v;
}

static inline void push(X64Cpu *c, uint64_t v) { c->r[RSP] -= 8; wr(c->r[RSP], 8, v); }
static inline uint64_t pop(X64Cpu *c) { uint64_t v = rd(c->r[RSP], 8); c->r[RSP] += 8; return v; }

static void fail(X64Cpu *c, const char *what, uint8_t *at) {
    if (!c->error) fprintf(stderr, "x64emu: %s at %p: %02x %02x %02x %02x %02x %02x\n", what, (void *)at, at[0], at[1], at[2], at[3], at[4], at[5]);
    c->error = 1;
}

/* XMM operand (reg or memory) */
static inline XmmReg *xmmsrc(X64Cpu *c, Ins *in, XmmReg *tmp, int bytes) {
    if (in->isreg) return &c->x[in->rm];
    memset(tmp, 0, sizeof *tmp);
    memcpy(tmp, (void *)in->addr, bytes);
    return tmp;
}

static int64_t cvt_round(double v, X64Cpu *c, int trunc, int sz) {
    if (isnan(v) || v >= (sz == 8 ? 9.2233720368547758e18 : 2147483648.0) || v < (sz == 8 ? -9.2233720368547758e18 : -2147483648.0))
        return sz == 8 ? INT64_MIN : INT32_MIN;
    if (trunc) return (int64_t)v;
    (void)c;
    return (int64_t)nearbyint(v);
}

static double fop(int op, double a, double b) {
    switch (op) {
    case 0x58: return a + b;
    case 0x59: return a * b;
    case 0x5C: return a - b;
    case 0x5D: return a < b ? a : b;
    case 0x5E: return a / b;
    default: return a > b ? a : b;     /* 5F */
    }
}

/* SSE (0F xx with prefixes none/66/F3/F2). Returns 0 if unknown. */
static int sse(X64Cpu *c, uint8_t op, Ins *in, uint8_t **pp, uint8_t *start) {
    uint8_t *p = *pp;
    XmmReg tmp, *s;
    int pre = in->repne ? 2 : in->rep ? 3 : in->opsz ? 1 : 0;   /* 0 none, 1 66, 2 F2, 3 F3 */
    switch (op) {
    case 0x10: case 0x11: {   /* movups/movupd/movss/movsd */
        p = modrm(c, p, in, 0);
        int n = pre == 3 ? 4 : pre == 2 ? 8 : 16;
        if (op == 0x10) {
            if (in->isreg) { if (n == 16) c->x[in->reg] = c->x[in->rm]; else memcpy(&c->x[in->reg], &c->x[in->rm], n); }
            else { if (n < 16) memset(&c->x[in->reg], 0, 16); memcpy(&c->x[in->reg], (void *)in->addr, n); }
        } else {
            if (in->isreg) { if (n == 16) c->x[in->rm] = c->x[in->reg]; else memcpy(&c->x[in->rm], &c->x[in->reg], n); }
            else memcpy((void *)in->addr, &c->x[in->reg], n);
        }
        break;
    }
    case 0x12: case 0x13: case 0x16: case 0x17: {   /* movlps/movlpd/movhps/movhpd (memory forms), movhlps/movlhps */
        p = modrm(c, p, in, 0);
        int hi = op >= 0x16;
        if (op == 0x12 || op == 0x16) {
            if (in->isreg) { if (op == 0x12) c->x[in->reg].q[0] = c->x[in->rm].q[1]; else c->x[in->reg].q[1] = c->x[in->rm].q[0]; }
            else memcpy(&c->x[in->reg].q[hi], (void *)in->addr, 8);
        } else memcpy((void *)in->addr, &c->x[in->reg].q[hi], 8);
        break;
    }
    case 0x14: case 0x15: {   /* unpcklps/pd, unpckhps/pd */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, 16);
        XmmReg d = c->x[in->reg], r;
        if (pre == 1) { r.q[0] = op == 0x14 ? d.q[0] : d.q[1]; r.q[1] = op == 0x14 ? s->q[0] : s->q[1]; }
        else if (op == 0x14) { r.d[0] = d.d[0]; r.d[1] = s->d[0]; r.d[2] = d.d[1]; r.d[3] = s->d[1]; }
        else { r.d[0] = d.d[2]; r.d[1] = s->d[2]; r.d[2] = d.d[3]; r.d[3] = s->d[3]; }
        c->x[in->reg] = r;
        break;
    }
    case 0x28: case 0x29:     /* movaps/movapd */
        p = modrm(c, p, in, 0);
        if (op == 0x28) { if (in->isreg) c->x[in->reg] = c->x[in->rm]; else memcpy(&c->x[in->reg], (void *)in->addr, 16); }
        else { if (in->isreg) c->x[in->rm] = c->x[in->reg]; else memcpy((void *)in->addr, &c->x[in->reg], 16); }
        break;
    case 0x2A: {              /* cvtsi2ss/sd */
        p = modrm(c, p, in, 0);
        int64_t v = in->w ? (int64_t)getrm(c, in, 8) : (int32_t)getrm(c, in, 4);
        if (pre == 3) c->x[in->reg].f[0] = (float)v; else if (pre == 2) c->x[in->reg].g[0] = (double)v; else return 0;
        break;
    }
    case 0x2C: case 0x2D: {   /* cvtt/cvt ss/sd 2si */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, pre == 2 ? 8 : 4);
        double v = pre == 2 ? s->g[0] : pre == 3 ? s->f[0] : NAN;
        if (pre < 2) return 0;
        int sz = in->w ? 8 : 4;
        setr(c, in->reg, sz, in->rex, (uint64_t)cvt_round(v, c, op == 0x2C, sz));
        break;
    }
    case 0x2E: case 0x2F: {   /* ucomis/comis */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, pre == 1 ? 8 : 4);
        double a = pre == 1 ? c->x[in->reg].g[0] : c->x[in->reg].f[0], b = pre == 1 ? s->g[0] : s->f[0];
        c->of = c->sf = 0;
        if (isnan(a) || isnan(b)) c->zf = c->pf = c->cf = 1;
        else { c->pf = 0; c->zf = a == b; c->cf = a < b; }
        break;
    }
    case 0x51: {              /* sqrt */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, 16);
        XmmReg *d = &c->x[in->reg];
        if (pre == 3) d->f[0] = sqrtf(s->f[0]);
        else if (pre == 2) d->g[0] = sqrt(s->g[0]);
        else if (pre == 1) { d->g[0] = sqrt(s->g[0]); d->g[1] = sqrt(s->g[1]); }
        else for (int i = 0; i < 4; i++) d->f[i] = sqrtf(s->f[i]);
        break;
    }
    case 0x54: case 0x55: case 0x56: case 0x57: {   /* and/andn/or/xor ps/pd */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, 16);
        XmmReg *d = &c->x[in->reg];
        for (int i = 0; i < 2; i++) {
            uint64_t a = d->q[i], b = s->q[i];
            d->q[i] = op == 0x54 ? (a & b) : op == 0x55 ? (~a & b) : op == 0x56 ? (a | b) : (a ^ b);
        }
        break;
    }
    case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F: {
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, pre == 3 ? 4 : pre == 2 ? 8 : 16);
        XmmReg *d = &c->x[in->reg];
        if (pre == 3) d->f[0] = (float)fop(op, d->f[0], s->f[0]);
        else if (pre == 2) d->g[0] = fop(op, d->g[0], s->g[0]);
        else if (pre == 1) { d->g[0] = fop(op, d->g[0], s->g[0]); d->g[1] = fop(op, d->g[1], s->g[1]); }
        else for (int i = 0; i < 4; i++) d->f[i] = (float)fop(op, d->f[i], s->f[i]);
        break;
    }
    case 0x5A: {              /* cvtps2pd / cvtpd2ps / cvtss2sd / cvtsd2ss */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, pre == 0 || pre == 2 ? 8 : pre == 3 ? 4 : 16);
        XmmReg *d = &c->x[in->reg], r = *d;
        if (pre == 0) { r.g[0] = s->f[0]; r.g[1] = s->f[1]; }
        else if (pre == 1) { r.f[0] = (float)s->g[0]; r.f[1] = (float)s->g[1]; r.q[1] = 0; }
        else if (pre == 3) r.g[0] = s->f[0];
        else r.f[0] = (float)s->g[0];
        *d = r;
        break;
    }
    case 0x5B: {              /* cvtdq2ps / cvtps2dq (66) / cvttps2dq (F3) */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, 16);
        XmmReg r;
        for (int i = 0; i < 4; i++) {
            if (pre == 0) r.f[i] = (float)s->i[i];
            else r.i[i] = (int32_t)cvt_round(s->f[i], c, pre == 3, 4);
        }
        c->x[in->reg] = r;
        break;
    }
    case 0xE6: {              /* cvtdq2pd (F3) / cvtpd2dq (F2) / cvttpd2dq (66) */
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, pre == 3 ? 8 : 16);
        XmmReg r;
        memset(&r, 0, sizeof r);
        if (pre == 3) { r.g[0] = s->i[0]; r.g[1] = s->i[1]; }
        else if (pre) { r.i[0] = (int32_t)cvt_round(s->g[0], c, pre == 1, 4); r.i[1] = (int32_t)cvt_round(s->g[1], c, pre == 1, 4); }
        else return 0;
        c->x[in->reg] = r;
        break;
    }
    case 0x6E:                /* movd/movq xmm, r/m */
        p = modrm(c, p, in, 0);
        if (pre != 1) return 0;
        memset(&c->x[in->reg], 0, 16);
        c->x[in->reg].q[0] = getrm(c, in, in->w ? 8 : 4);
        break;
    case 0x7E:
        p = modrm(c, p, in, 0);
        if (pre == 1) setrm(c, in, in->w ? 8 : 4, in->w ? c->x[in->reg].q[0] : c->x[in->reg].d[0]);
        else if (pre == 3) { s = xmmsrc(c, in, &tmp, 8); uint64_t v = s->q[0]; memset(&c->x[in->reg], 0, 16); c->x[in->reg].q[0] = v; }
        else return 0;
        break;
    case 0x6F: case 0x7F:     /* movdqa/movdqu */
        p = modrm(c, p, in, 0);
        if (pre != 1 && pre != 3) return 0;
        if (op == 0x6F) { if (in->isreg) c->x[in->reg] = c->x[in->rm]; else memcpy(&c->x[in->reg], (void *)in->addr, 16); }
        else { if (in->isreg) c->x[in->rm] = c->x[in->reg]; else memcpy((void *)in->addr, &c->x[in->reg], 16); }
        break;
    case 0xD6:                /* movq xmm/m64, xmm */
        p = modrm(c, p, in, 0);
        if (in->isreg) { c->x[in->rm].q[0] = c->x[in->reg].q[0]; c->x[in->rm].q[1] = 0; }
        else memcpy((void *)in->addr, &c->x[in->reg].q[0], 8);
        break;
    case 0x70: {              /* pshufd (66) */
        p = modrm(c, p, in, 1); s = xmmsrc(c, in, &tmp, 16);
        uint8_t o = *p++;
        XmmReg r;
        if (pre != 1) return 0;
        for (int i = 0; i < 4; i++) r.d[i] = s->d[(o >> (2 * i)) & 3];
        c->x[in->reg] = r;
        break;
    }
    case 0x73: {              /* psrlq/psrldq/psllq/pslldq imm (66) */
        p = modrm(c, p, in, 1);
        uint8_t n = *p++;
        XmmReg *d = &c->x[in->rm];
        int sub = in->reg & 7;
        if (sub == 3 || sub == 7) {
            uint8_t b[16]; memcpy(b, d, 16);
            XmmReg r; memset(&r, 0, sizeof r);
            if (n < 16) { if (sub == 3) memcpy(r.b, b + n, 16 - n); else memcpy(r.b + n, b, 16 - n); }
            *d = r;
        } else if (sub == 2) { for (int i = 0; i < 2; i++) d->q[i] = n > 63 ? 0 : d->q[i] >> n; }
        else if (sub == 6) { for (int i = 0; i < 2; i++) d->q[i] = n > 63 ? 0 : d->q[i] << n; }
        else return 0;
        break;
    }
    case 0xC6: {              /* shufps / shufpd */
        p = modrm(c, p, in, 1); s = xmmsrc(c, in, &tmp, 16);
        uint8_t o = *p++;
        XmmReg d = c->x[in->reg], r;
        if (pre == 1) { r.q[0] = d.q[o & 1]; r.q[1] = s->q[(o >> 1) & 1]; }
        else { r.d[0] = d.d[o & 3]; r.d[1] = d.d[(o >> 2) & 3]; r.d[2] = s->d[(o >> 4) & 3]; r.d[3] = s->d[(o >> 6) & 3]; }
        c->x[in->reg] = r;
        break;
    }
    case 0x62: case 0x6C: case 0xD4: case 0xDB: case 0xEB: case 0xEF: case 0xFA: case 0xFE: case 0x66: case 0x76: case 0xFB: case 0x6A: case 0x6D: {
        p = modrm(c, p, in, 0); s = xmmsrc(c, in, &tmp, 16);
        if (pre != 1) return 0;
        XmmReg d = c->x[in->reg], r;
        switch (op) {
        case 0x62: r.d[0] = d.d[0]; r.d[1] = s->d[0]; r.d[2] = d.d[1]; r.d[3] = s->d[1]; break;
        case 0x6A: r.d[0] = d.d[2]; r.d[1] = s->d[2]; r.d[2] = d.d[3]; r.d[3] = s->d[3]; break;
        case 0x6C: r.q[0] = d.q[0]; r.q[1] = s->q[0]; break;
        case 0x6D: r.q[0] = d.q[1]; r.q[1] = s->q[1]; break;
        case 0xD4: r.q[0] = d.q[0] + s->q[0]; r.q[1] = d.q[1] + s->q[1]; break;
        case 0xFB: r.q[0] = d.q[0] - s->q[0]; r.q[1] = d.q[1] - s->q[1]; break;
        case 0xDB: r.q[0] = d.q[0] & s->q[0]; r.q[1] = d.q[1] & s->q[1]; break;
        case 0xEB: r.q[0] = d.q[0] | s->q[0]; r.q[1] = d.q[1] | s->q[1]; break;
        case 0xEF: r.q[0] = d.q[0] ^ s->q[0]; r.q[1] = d.q[1] ^ s->q[1]; break;
        case 0xFA: for (int i = 0; i < 4; i++) r.d[i] = d.d[i] - s->d[i]; break;
        case 0xFE: for (int i = 0; i < 4; i++) r.d[i] = d.d[i] + s->d[i]; break;
        case 0x66: for (int i = 0; i < 4; i++) r.d[i] = d.i[i] > s->i[i] ? ~0u : 0; break;
        default: for (int i = 0; i < 4; i++) r.d[i] = d.d[i] == s->d[i] ? ~0u : 0; break;   /* 76 */
        }
        c->x[in->reg] = r;
        break;
    }
    default:
        (void)start;
        return 0;
    }
    *pp = p;
    return 1;
}

static void do_call(X64Cpu *c, uint64_t target, uint64_t ret) {
    if (c->host && c->host(c, target)) { c->rip = ret; return; }
    push(c, ret);
    c->rip = target;
}
static void do_jmp(X64Cpu *c, uint64_t target) {
    /* a tail jump to host code: call it, then return for the guest */
    if (c->host && c->host(c, target)) { c->rip = pop(c); return; }
    c->rip = target;
}

static void cpuid(X64Cpu *c) {
    uint32_t leaf = (uint32_t)c->r[RAX];
    uint32_t a = 0, b = 0, cc = 0, d = 0;
    if (leaf == 0) { a = 1; b = 0x756e6547; d = 0x49656e69; cc = 0x6c65746e; }   /* GenuineIntel */
    else if (leaf == 1) { a = 0x000306a9; d = (1u << 0) | (1u << 15) | (1u << 23) | (1u << 25) | (1u << 26); cc = 1u << 0; }
    c->r[RAX] = a; c->r[RBX] = b; c->r[RCX] = cc; c->r[RDX] = d;
}

static inline __attribute__((always_inline)) void step(X64Cpu *c) {
    uint8_t *start = (uint8_t *)c->rip, *p = start;
    Ins in;
    memset(&in, 0, sizeof in);
    for (;;) {
        uint8_t b = *p;
        if (b == 0x66) in.opsz = 1;
        else if (b == 0xF2) in.repne = 1;
        else if (b == 0xF3) in.rep = 1;
        else if (b == 0xF0 || b == 0x2E || b == 0x3E || b == 0x26 || b == 0x36) {}
        else if (b == 0x64) in.seg = 'f';
        else if (b == 0x65) in.seg = 'g';
        else break;
        p++;
    }
    if ((*p & 0xF0) == 0x40) { in.rex = 0x40 | (*p & 0xF); in.w = (*p >> 3) & 1; p++; }
    int osz = in.w ? 8 : in.opsz ? 2 : 4;
    uint8_t op = *p++;
    uint64_t v, a;

    if (op < 0x40 && (op & 7) < 6) {             /* ALU: add or adc sbb and sub xor cmp */
        int k = op >> 3, form = op & 7;
        if (form < 4) {
            int sz = (form & 1) ? osz : 1;
            p = modrm(c, p, &in, 0);
            if (form < 2) { v = alu(c, k, getrm(c, &in, sz), getr(c, in.reg, sz, in.rex), sz); if (k != 7) setrm(c, &in, sz, v); }
            else { v = alu(c, k, getr(c, in.reg, sz, in.rex), getrm(c, &in, sz), sz); if (k != 7) setr(c, in.reg, sz, in.rex, v); }
        } else {
            int sz = form == 4 ? 1 : osz;
            uint64_t im = imm(&p, sz == 8 ? 4 : sz);
            v = alu(c, k, getr(c, RAX, sz, 1), im, sz);
            if (k != 7) setr(c, RAX, sz, 1, v);
        }
        c->rip = (uint64_t)p;
        return;
    }
    switch (op) {
    case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
        push(c, c->r[(op & 7) | ((in.rex & 1) << 3)]); break;
    case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
        c->r[(op & 7) | ((in.rex & 1) << 3)] = pop(c); break;
    case 0x63:                                     /* movsxd */
        p = modrm(c, p, &in, 0);
        setr(c, in.reg, osz, in.rex, (uint64_t)(int64_t)(int32_t)getrm(c, &in, 4));
        break;
    case 0x68: push(c, imm(&p, 4)); break;
    case 0x6A: push(c, imm(&p, 1)); break;
    case 0x69: case 0x6B: {                        /* imul r, rm, imm */
        p = modrm(c, p, &in, op == 0x69 ? (osz == 2 ? 2 : 4) : 1);
        int64_t x = sext(getrm(c, &in, osz), osz), y = (int64_t)imm(&p, op == 0x69 ? (osz == 2 ? 2 : 4) : 1);
        __int128 r = (__int128)x * y;
        uint64_t t = (uint64_t)r & mask(osz);
        c->cf = c->of = (__int128)sext(t, osz) != r;
        setr(c, in.reg, osz, in.rex, t);
        break;
    }
    case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77:
    case 0x78: case 0x79: case 0x7A: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F: {
        int64_t d = (int8_t)*p++;
        c->rip = (uint64_t)p;
        if (cond(c, op & 15)) c->rip += d;
        return;
    }
    case 0x80: case 0x81: case 0x83: {
        int sz = op == 0x80 ? 1 : osz;
        int isz = op == 0x81 ? (sz == 2 ? 2 : 4) : 1;
        p = modrm(c, p, &in, isz);
        uint64_t im = imm(&p, isz);
        v = alu(c, in.reg & 7, getrm(c, &in, sz), im, sz);
        if ((in.reg & 7) != 7) setrm(c, &in, sz, v);
        break;
    }
    case 0x84: case 0x85: {
        int sz = op == 0x84 ? 1 : osz;
        p = modrm(c, p, &in, 0);
        logic_f(c, getrm(c, &in, sz) & getr(c, in.reg, sz, in.rex), sz);
        break;
    }
    case 0x86: case 0x87: {
        int sz = op == 0x86 ? 1 : osz;
        p = modrm(c, p, &in, 0);
        uint64_t x = getrm(c, &in, sz), y = getr(c, in.reg, sz, in.rex);
        setrm(c, &in, sz, y); setr(c, in.reg, sz, in.rex, x);
        break;
    }
    case 0x88: p = modrm(c, p, &in, 0); setrm(c, &in, 1, getr(c, in.reg, 1, in.rex)); break;
    case 0x89: p = modrm(c, p, &in, 0); setrm(c, &in, osz, getr(c, in.reg, osz, in.rex)); break;
    case 0x8A: p = modrm(c, p, &in, 0); setr(c, in.reg, 1, in.rex, getrm(c, &in, 1)); break;
    case 0x8B: p = modrm(c, p, &in, 0); setr(c, in.reg, osz, in.rex, getrm(c, &in, osz)); break;
    case 0x8D: {
        int seg = in.seg; in.seg = 0;
        p = modrm(c, p, &in, 0); (void)seg;
        setr(c, in.reg, osz, in.rex, in.addr);
        break;
    }
    case 0x8F: p = modrm(c, p, &in, 0); v = pop(c); setrm(c, &in, 8, v); break;
    case 0x90:
        if (in.rex & 1) { uint64_t t = c->r[R8]; c->r[R8] = c->r[RAX]; c->r[RAX] = t; }
        break;
    case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97: {
        int r = (op & 7) | ((in.rex & 1) << 3);
        uint64_t x = getr(c, r, osz, 1), y = getr(c, RAX, osz, 1);
        setr(c, r, osz, 1, y); setr(c, RAX, osz, 1, x);
        break;
    }
    case 0x98:
        if (osz == 8) c->r[RAX] = (uint64_t)(int64_t)(int32_t)c->r[RAX];
        else if (osz == 4) c->r[RAX] = (uint32_t)(int32_t)(int16_t)c->r[RAX];
        else setr(c, RAX, 2, 1, (uint64_t)(int64_t)(int8_t)c->r[RAX]);
        break;
    case 0x99:
        if (osz == 8) c->r[RDX] = (int64_t)c->r[RAX] < 0 ? ~0ULL : 0;
        else if (osz == 4) c->r[RDX] = (int32_t)c->r[RAX] < 0 ? 0xffffffffULL : 0;
        else setr(c, RDX, 2, 1, (int16_t)c->r[RAX] < 0 ? 0xffff : 0);
        break;
    case 0x9C: push(c, (uint64_t)c->cf | 2 | ((uint64_t)c->pf << 2) | ((uint64_t)c->zf << 6) | ((uint64_t)c->sf << 7) | ((uint64_t)c->df << 10) | ((uint64_t)c->of << 11)); break;
    case 0x9D: v = pop(c); c->cf = v & 1; c->pf = (v >> 2) & 1; c->zf = (v >> 6) & 1; c->sf = (v >> 7) & 1; c->df = (v >> 10) & 1; c->of = (v >> 11) & 1; break;
    case 0x9E: v = c->r[RAX] >> 8; c->cf = v & 1; c->pf = (v >> 2) & 1; c->zf = (v >> 6) & 1; c->sf = (v >> 7) & 1; break;
    case 0x9F: setr(c, 4, 1, 0, (uint64_t)c->cf | 2 | ((uint64_t)c->pf << 2) | ((uint64_t)c->zf << 6) | ((uint64_t)c->sf << 7)); break;
    case 0xA0: case 0xA1: case 0xA2: case 0xA3: {   /* mov moffs */
        uint64_t ad; memcpy(&ad, p, 8); p += 8;
        if (in.seg == 'g') ad += c->gsbase;
        int sz = (op & 1) ? osz : 1;
        if (op < 0xA2) setr(c, RAX, sz, 1, rd(ad, sz)); else wr(ad, sz, getr(c, RAX, sz, 1));
        break;
    }
    case 0xA4: case 0xA5: case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xA6: case 0xA7: case 0xAE: case 0xAF: {
        int sz = (op & 1) ? osz : 1;
        int64_t step_ = c->df ? -sz : sz;
        int repeat = in.rep || in.repne;
        for (;;) {
            if (repeat && c->r[RCX] == 0) break;
            if (op == 0xA4 || op == 0xA5) { wr(c->r[RDI], sz, rd(c->r[RSI], sz)); c->r[RSI] += step_; c->r[RDI] += step_; }
            else if (op == 0xAA || op == 0xAB) { wr(c->r[RDI], sz, getr(c, RAX, sz, 1)); c->r[RDI] += step_; }
            else if (op == 0xAC || op == 0xAD) { setr(c, RAX, sz, 1, rd(c->r[RSI], sz)); c->r[RSI] += step_; }
            else if (op == 0xA6 || op == 0xA7) { sub_f(c, rd(c->r[RSI], sz), rd(c->r[RDI], sz), 0, sz); c->r[RSI] += step_; c->r[RDI] += step_; }
            else { sub_f(c, getr(c, RAX, sz, 1), rd(c->r[RDI], sz), 0, sz); c->r[RDI] += step_; }
            if (!repeat) break;
            c->r[RCX]--;
            if (op == 0xA6 || op == 0xA7 || op == 0xAE || op == 0xAF) { if (in.rep && !c->zf) break; if (in.repne && c->zf) break; }
        }
        break;
    }
    case 0xA8: logic_f(c, getr(c, RAX, 1, 1) & imm(&p, 1), 1); break;
    case 0xA9: logic_f(c, getr(c, RAX, osz, 1) & imm(&p, osz == 2 ? 2 : 4), osz); break;
    case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
        setr(c, (op & 7) | ((in.rex & 1) << 3), 1, in.rex, *p++); break;
    case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF: {
        int r = (op & 7) | ((in.rex & 1) << 3);
        if (osz == 8) { memcpy(&v, p, 8); p += 8; c->r[r] = v; }
        else if (osz == 4) { uint32_t t; memcpy(&t, p, 4); p += 4; c->r[r] = t; }
        else { uint16_t t; memcpy(&t, p, 2); p += 2; setr(c, r, 2, 1, t); }
        break;
    }
    case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3: {
        int sz = (op & 1) ? osz : 1;
        p = modrm(c, p, &in, op <= 0xC1 ? 1 : 0);
        unsigned n = op <= 0xC1 ? *p++ : op <= 0xD1 ? 1 : (unsigned)(c->r[RCX] & 0xff);
        setrm(c, &in, sz, shift(c, in.reg & 7, getrm(c, &in, sz), n, sz));
        break;
    }
    case 0xC2: { uint16_t n; memcpy(&n, p, 2); c->rip = pop(c); c->r[RSP] += n; return; }
    case 0xC3: c->rip = pop(c); return;
    case 0xC6: case 0xC7: {
        int sz = op == 0xC6 ? 1 : osz;
        int isz = sz == 1 ? 1 : sz == 2 ? 2 : 4;
        p = modrm(c, p, &in, isz);
        setrm(c, &in, sz, imm(&p, isz));
        break;
    }
    case 0xC9: c->r[RSP] = c->r[RBP]; c->r[RBP] = pop(c); break;
    case 0xCC: fail(c, "int3", start); return;
    case 0xD6:                                     /* hook (see x64emu.h) */
        if (*p < X64_HOOKS && c->hooks[*p]) { c->steps += c->hooks[*p](c) - 1; return; }
        fail(c, "unknown opcode", start);
        return;
    case 0xE2: { int64_t d = (int8_t)*p++; c->rip = (uint64_t)p; if (--c->r[RCX]) c->rip += d; return; }
    case 0xE3: { int64_t d = (int8_t)*p++; c->rip = (uint64_t)p; if (!c->r[RCX]) c->rip += d; return; }
    case 0xE8: { int32_t d; memcpy(&d, p, 4); p += 4; do_call(c, (uint64_t)p + (int64_t)d, (uint64_t)p); return; }
    case 0xE9: { int32_t d; memcpy(&d, p, 4); p += 4; do_jmp(c, (uint64_t)p + (int64_t)d); return; }
    case 0xEB: { int64_t d = (int8_t)*p++; c->rip = (uint64_t)p + d; return; }
    case 0xF5: c->cf = !c->cf; break;
    case 0xF8: c->cf = 0; break;
    case 0xF9: c->cf = 1; break;
    case 0xFC: c->df = 0; break;
    case 0xFD: c->df = 1; break;
    case 0xF6: case 0xF7: {
        int sz = op == 0xF6 ? 1 : osz;
        int sub = 0;
        { uint8_t m = *p; sub = (m >> 3) & 7; }
        p = modrm(c, p, &in, sub < 2 ? (sz == 1 ? 1 : sz == 2 ? 2 : 4) : 0);
        uint64_t x = getrm(c, &in, sz);
        switch (sub) {
        case 0: case 1: logic_f(c, x & imm(&p, sz == 1 ? 1 : sz == 2 ? 2 : 4), sz); break;
        case 2: setrm(c, &in, sz, ~x); break;
        case 3: { uint64_t r = sub_f(c, 0, x, 0, sz); c->cf = (x & mask(sz)) != 0; setrm(c, &in, sz, r); break; }
        case 4: case 5: {                          /* mul / imul */
            if (sz == 1) {
                uint64_t al = c->r[RAX] & 0xff;
                uint64_t r = sub == 4 ? al * x : (uint64_t)((int64_t)(int8_t)al * (int8_t)x);
                setr(c, RAX, 2, 1, r);
                c->cf = c->of = sub == 4 ? (r >> 8) != 0 : (int16_t)r != (int8_t)r;
            } else {
                unsigned __int128 r;
                if (sub == 4) r = (unsigned __int128)(c->r[RAX] & mask(sz)) * x;
                else r = (unsigned __int128)((__int128)sext(c->r[RAX], sz) * sext(x, sz));
                uint64_t lo = (uint64_t)r & mask(sz), hi = (uint64_t)(r >> (sz * 8)) & mask(sz);
                if (sz == 8) hi = (uint64_t)(r >> 64);
                setr(c, RAX, sz, 1, lo); setr(c, RDX, sz, 1, hi);
                if (sub == 4) c->cf = c->of = hi != 0;
                else c->cf = c->of = (__int128)sext(lo, sz) != (__int128)r;
            }
            break;
        }
        default: {                                 /* div / idiv */
            if ((x & mask(sz)) == 0) { fail(c, "divide by zero", start); return; }
            if (sz == 1) {
                uint64_t ax = c->r[RAX] & 0xffff;
                if (sub == 6) { setr(c, 4, 1, 0, ax % x); setr(c, RAX, 1, 1, ax / x); }
                else { int16_t n = (int16_t)ax; int8_t d = (int8_t)x; setr(c, 4, 1, 0, (uint8_t)(n % d)); setr(c, RAX, 1, 1, (uint8_t)(n / d)); }
            } else if (sz == 8) {
                if (sub == 6) {
                    unsigned __int128 n = ((unsigned __int128)c->r[RDX] << 64) | c->r[RAX];
                    c->r[RAX] = (uint64_t)(n / x); c->r[RDX] = (uint64_t)(n % x);
                } else {
                    __int128 n = (__int128)(((unsigned __int128)c->r[RDX] << 64) | c->r[RAX]);
                    c->r[RAX] = (uint64_t)(n / (int64_t)x); c->r[RDX] = (uint64_t)(n % (int64_t)x);
                }
            } else {
                uint64_t m = mask(sz);
                if (sub == 6) {
                    uint64_t n = ((c->r[RDX] & m) << (sz * 8)) | (c->r[RAX] & m);
                    setr(c, RAX, sz, 1, n / x); setr(c, RDX, sz, 1, n % x);
                } else {
                    int64_t n = (int64_t)(((c->r[RDX] & m) << (sz * 8)) | (c->r[RAX] & m));
                    if (sz == 2) n = (int32_t)n;
                    int64_t d = sext(x, sz);
                    setr(c, RAX, sz, 1, (uint64_t)(n / d)); setr(c, RDX, sz, 1, (uint64_t)(n % d));
                }
            }
            break;
        }
        }
        break;
    }
    case 0xFE: case 0xFF: {
        int sz = op == 0xFE ? 1 : osz;
        p = modrm(c, p, &in, 0);
        int sub = in.reg & 7;
        if (sub == 0 || sub == 1) {
            int cf = c->cf;
            v = sub == 0 ? add_f(c, getrm(c, &in, sz), 1, 0, sz) : sub_f(c, getrm(c, &in, sz), 1, 0, sz);
            c->cf = cf;
            setrm(c, &in, sz, v);
        } else if (op == 0xFF && sub == 2) { a = getrm(c, &in, 8); do_call(c, a, (uint64_t)p); return; }
        else if (op == 0xFF && sub == 4) { a = getrm(c, &in, 8); do_jmp(c, a); return; }
        else if (op == 0xFF && sub == 6) push(c, getrm(c, &in, 8));
        else { fail(c, "unknown FF", start); return; }
        break;
    }
    case 0x0F: {
        uint8_t o2 = *p++;
        if (o2 >= 0x80 && o2 <= 0x8F) {
            int32_t d; memcpy(&d, p, 4); p += 4;
            c->rip = (uint64_t)p;
            if (cond(c, o2 & 15)) c->rip += (int64_t)d;
            return;
        }
        if (o2 >= 0x40 && o2 <= 0x4F) {           /* cmovcc */
            p = modrm(c, p, &in, 0);
            uint64_t src = getrm(c, &in, osz);
            if (cond(c, o2 & 15)) setr(c, in.reg, osz, in.rex, src);
            else if (osz == 4) c->r[in.reg] &= 0xffffffffULL;
            break;
        }
        if (o2 >= 0x90 && o2 <= 0x9F) { p = modrm(c, p, &in, 0); setrm(c, &in, 1, cond(c, o2 & 15)); break; }
        switch (o2) {
        case 0x0B: fail(c, "ud2", start); return;
        case 0x18: case 0x1F: p = modrm(c, p, &in, 0); break;     /* prefetch / nop rm */
        case 0x31: c->r[RAX] = 0; c->r[RDX] = 0; break;           /* rdtsc */
        case 0xA2: cpuid(c); break;
        case 0xAF: {
            p = modrm(c, p, &in, 0);
            __int128 r = (__int128)sext(getr(c, in.reg, osz, in.rex), osz) * sext(getrm(c, &in, osz), osz);
            uint64_t t = (uint64_t)r & mask(osz);
            c->cf = c->of = (__int128)sext(t, osz) != r;
            setr(c, in.reg, osz, in.rex, t);
            break;
        }
        case 0xB6: p = modrm(c, p, &in, 0); setr(c, in.reg, osz, in.rex, getrm(c, &in, 1)); break;
        case 0xB7: p = modrm(c, p, &in, 0); setr(c, in.reg, osz, in.rex, getrm(c, &in, 2)); break;
        case 0xBE: p = modrm(c, p, &in, 0); setr(c, in.reg, osz, in.rex, (uint64_t)(int64_t)(int8_t)getrm(c, &in, 1)); break;
        case 0xBF: p = modrm(c, p, &in, 0); setr(c, in.reg, osz, in.rex, (uint64_t)(int64_t)(int16_t)getrm(c, &in, 2)); break;
        case 0xA3: case 0xAB: case 0xB3: case 0xBB: case 0xBA: {
            int isimm = o2 == 0xBA;
            p = modrm(c, p, &in, isimm);
            int kind = isimm ? (in.reg & 7) - 4 : (o2 == 0xA3 ? 0 : o2 == 0xAB ? 1 : o2 == 0xB3 ? 2 : 3);
            int64_t bit = isimm ? *p++ : (int64_t)sext(getr(c, in.reg, osz, in.rex), osz);
            int bits = osz * 8;
            if (in.isreg || isimm) bit &= bits - 1;
            else { in.addr += (bit >> 3) & ~(int64_t)(osz - 1); bit &= bits - 1; }
            uint64_t x = getrm(c, &in, osz);
            c->cf = (x >> bit) & 1;
            if (kind == 1) setrm(c, &in, osz, x | (1ULL << bit));
            else if (kind == 2) setrm(c, &in, osz, x & ~(1ULL << bit));
            else if (kind == 3) setrm(c, &in, osz, x ^ (1ULL << bit));
            break;
        }
        case 0xA4: case 0xA5: case 0xAC: case 0xAD: {   /* shld/shrd */
            p = modrm(c, p, &in, (o2 & 1) ? 0 : 1);
            unsigned n = (o2 & 1) ? (unsigned)(c->r[RCX] & 0xff) : *p++;
            n &= osz == 8 ? 63 : 31;
            if (!n) break;
            int bits = osz * 8;
            uint64_t d = getrm(c, &in, osz), s = getr(c, in.reg, osz, in.rex), r;
            if (o2 <= 0xA5) { r = ((d << n) | (s >> (bits - n))) & mask(osz); c->cf = (d >> (bits - n)) & 1; }
            else { r = ((d >> n) | (s << (bits - n))) & mask(osz); c->cf = (d >> (n - 1)) & 1; }
            szp(c, r, osz);
            setrm(c, &in, osz, r);
            break;
        }
        case 0xB0: case 0xB1: {
            int sz = o2 == 0xB0 ? 1 : osz;
            p = modrm(c, p, &in, 0);
            uint64_t dst = getrm(c, &in, sz);
            sub_f(c, getr(c, RAX, sz, 1), dst, 0, sz);
            if (c->zf) setrm(c, &in, sz, getr(c, in.reg, sz, in.rex)); else setr(c, RAX, sz, 1, dst);
            break;
        }
        case 0xC0: case 0xC1: {                   /* xadd */
            int sz = o2 == 0xC0 ? 1 : osz;
            p = modrm(c, p, &in, 0);
            uint64_t dst = getrm(c, &in, sz), src = getr(c, in.reg, sz, in.rex);
            setr(c, in.reg, sz, in.rex, dst);
            setrm(c, &in, sz, add_f(c, dst, src, 0, sz));
            break;
        }
        case 0xC8: case 0xC9: case 0xCA: case 0xCB: case 0xCC: case 0xCD: case 0xCE: case 0xCF: {
            int r = (o2 & 7) | ((in.rex & 1) << 3);
            if (osz == 8) c->r[r] = __builtin_bswap64(c->r[r]); else c->r[r] = __builtin_bswap32((uint32_t)c->r[r]);
            break;
        }
        case 0xAE: {                              /* ldmxcsr/stmxcsr/fences */
            p = modrm(c, p, &in, 0);
            int sub = in.reg & 7;
            if (in.isreg) break;                  /* lfence/mfence/sfence */
            if (sub == 2) c->mxcsr = (uint32_t)rd(in.addr, 4);
            else if (sub == 3) wr(in.addr, 4, c->mxcsr);
            break;
        }
        default:
            if (!sse(c, o2, &in, &p, start)) { fail(c, "unknown 0F opcode", start); return; }
            break;
        }
        break;
    }
    default:
        fail(c, "unknown opcode", start);
        return;
    }
    c->rip = (uint64_t)p;
}

void x64_run(X64Cpu *c) {
    if (c->hist) {
        while (!c->error && c->rip != X64_SENTINEL) { if (c->rip - c->hist_lo < c->hist_n) c->hist[c->rip - c->hist_lo]++; step(c); c->steps++; }
        return;
    }
    if (c->jit) { x64_jit_run(c); return; }
    while (!c->error && c->rip != X64_SENTINEL) {
        step(c);
        c->steps++;
    }
}

void x64_step(X64Cpu *c) { step(c); c->steps++; }

uint64_t x64_arg(X64Cpu *c, int i) {
    static const int regs[4] = {RCX, RDX, R8, R9};
    if (i < 4) return c->r[regs[i]];
    return rd(c->r[RSP] + 0x20 + 8 * (uint64_t)(i - 4), 8);
}

uint64_t x64_call(X64Cpu *c, uint64_t addr, int nargs, const uint64_t *args, const XmmReg *fargs) {
    static const int regs[4] = {RCX, RDX, R8, R9};
    uint64_t saved[16], rip = c->rip;
    memcpy(saved, c->r, sizeof saved);
    XmmReg savedx[16];
    memcpy(savedx, c->x, sizeof savedx);
    uint64_t sp = (c->r[RSP] - 0x40) & ~0xFULL;
    int stackargs = nargs > 4 ? nargs - 4 : 0;
    sp -= (uint64_t)(0x20 + 8 * stackargs);
    sp &= ~0xFULL;
    for (int i = 0; i < nargs; i++) {
        if (i < 4) c->r[regs[i]] = args[i];
        else wr(sp + 0x20 + 8 * (uint64_t)(i - 4), 8, args[i]);
        if (fargs && i < 4) c->x[i] = fargs[i];
    }
    c->r[RSP] = sp;
    push(c, X64_SENTINEL);
    c->rip = addr;
    x64_run(c);
    uint64_t result = c->r[RAX];
    XmmReg x0 = c->x[0];
    memcpy(c->r, saved, sizeof saved);
    memcpy(c->x, savedx, sizeof savedx);
    c->x[0] = x0;
    c->r[RAX] = result;
    c->rip = rip;
    return result;
}
