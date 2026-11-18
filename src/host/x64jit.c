/* x64jit: x86-64 guest code translated to ARM64, a basic block at a time, the first time the
   block runs. Guest state stays in X64Cpu (registers are loaded and stored around each
   instruction); within a block the five flags live in host registers w20-w24 and are only
   computed where a later instruction (or the block's end) can read them.

   Only the common integer instructions are translated (the ones x64emu runs most: moves, ALU,
   compares and branches, shifts, imul, push/pop/call/ret). A block ends before anything else
   (SSE, string ops, prefixes, indirect calls and jumps, hooks, a call out of the image) and the
   interpreter runs that instruction; an instruction that can't start a block is always
   interpreted. Each translated instruction does what step() does: same results, same flags
   (AF isn't kept by either), same partial-register rules.

   FF9_TRACE_X64_JITCHECK=1: every block run is replayed by the interpreter from the same state
   (the block's memory writes undone first) and registers, flags, rip and the written memory
   compared; on a difference the interpreter's result is kept and the block is reported. */
#include "x64emu.h"
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>

/* ---------------- decoding (the instructions translated) ---------------- */
enum { K_SLOW, K_ALU_MR, K_ALU_RM, K_ALU_AI, K_ALU_MI, K_TEST_MR, K_TEST_MI, K_TEST_AI,
       K_MOV_RM, K_STORE, K_MOVI_RM, K_MOVI_R, K_MOVX, K_LEA, K_PUSH, K_POP, K_RET, K_JCC, K_JMP,
       K_CALL, K_INCDEC, K_SHIFT, K_SHIFT_CL, K_CMOV, K_SETCC, K_IMUL2, K_IMUL3, K_NOP, K_CBW, K_CWD };
#define NOREG 0xff
typedef struct {
    uint64_t pc, next;
    int64_t disp, imm;            /* RIP-relative operands: disp is the absolute address; branches: imm is the target */
    uint8_t kind, sz, osz, sub, reg, base, index, scale, isreg, rex, ext;   /* ext: K_MOVX source size, |8 signed */
} Dec;

static int64_t imm_rd(uint8_t **pp, int sz) {
    uint8_t *p = *pp; int64_t v;
    switch (sz) {
    case 1: v = (int8_t)p[0]; break;
    case 2: { int16_t t; memcpy(&t, p, 2); v = t; break; }
    default: { int32_t t; memcpy(&t, p, 4); v = t; sz = 4; break; }
    }
    *pp = p + sz;
    return v;
}

/* ModRM/SIB/displacement as x64emu's modrm() reads them; `imm` = immediate bytes after it */
static uint8_t *dmodrm(Dec *d, uint8_t *p, int imm) {
    uint8_t m = *p++;
    int mod = m >> 6, rm = m & 7;
    d->reg = ((m >> 3) & 7) | ((d->rex & 4) << 1);
    d->base = d->index = NOREG;
    if (mod == 3) { d->isreg = 1; d->base = rm | ((d->rex & 1) << 3); return p; }
    if (rm == 4) {
        uint8_t sib = *p++;
        int idx = ((sib >> 3) & 7) | ((d->rex & 2) << 2), base = (sib & 7) | ((d->rex & 1) << 3);
        if (idx != 4) { d->index = idx; d->scale = sib >> 6; }
        if ((base & 7) == 5 && mod == 0) { int32_t v; memcpy(&v, p, 4); p += 4; d->disp = v; }
        else d->base = base;
    } else if (rm == 5 && mod == 0) {
        int32_t v; memcpy(&v, p, 4); p += 4;
        d->disp = (int64_t)((uint64_t)(p + imm) + (int64_t)v);
        return p;
    } else d->base = rm | ((d->rex & 1) << 3);
    if (mod == 1) d->disp += (int8_t)*p++;
    else if (mod == 2) { int32_t v; memcpy(&v, p, 4); p += 4; d->disp += v; }
    return p;
}

static void decode(Dec *d, uint64_t pc) {
    memset(d, 0, sizeof *d);
    d->pc = pc;
    d->kind = K_SLOW;
    uint8_t *p = (uint8_t *)pc;
    int opsz = 0;
    for (;; p++) {
        uint8_t b = *p;
        if (b == 0x66) opsz = 1;
        else if (b == 0xF2 || b == 0xF3 || b == 0x64 || b == 0x65) return;
        else if (!(b == 0xF0 || b == 0x2E || b == 0x3E || b == 0x26 || b == 0x36)) break;
    }
    if ((*p & 0xF0) == 0x40) { d->rex = 0x40 | (*p & 0xF); p++; }
    int osz = (d->rex & 8) ? 8 : opsz ? 2 : 4;
    d->osz = (uint8_t)osz;
    uint8_t op = *p++;
    int kind = K_SLOW;
    if (op < 0x40 && (op & 7) < 6) {
        int form = op & 7;
        d->sub = op >> 3;
        if (form < 4) { d->sz = (form & 1) ? osz : 1; p = dmodrm(d, p, 0); kind = form < 2 ? K_ALU_MR : K_ALU_RM; }
        else { d->sz = form == 4 ? 1 : osz; d->imm = imm_rd(&p, d->sz == 8 ? 4 : d->sz); kind = K_ALU_AI; }
    } else switch (op) {
    case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
        d->reg = (op & 7) | ((d->rex & 1) << 3); kind = K_PUSH; break;
    case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
        d->reg = (op & 7) | ((d->rex & 1) << 3); kind = K_POP; break;
    case 0x63: p = dmodrm(d, p, 0); d->ext = 4 | 8; kind = K_MOVX; break;
    case 0x69: case 0x6B: {
        int isz = op == 0x69 ? (osz == 2 ? 2 : 4) : 1;
        p = dmodrm(d, p, isz); d->imm = imm_rd(&p, isz); kind = K_IMUL3; break;
    }
    case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77:
    case 0x78: case 0x79: case 0x7A: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F: {
        int64_t v = (int8_t)*p++;
        d->sub = op & 15; d->imm = (int64_t)((uint64_t)p + v); kind = K_JCC; break;
    }
    case 0x80: case 0x81: case 0x83: {
        d->sz = op == 0x80 ? 1 : osz;
        int isz = op == 0x81 ? (d->sz == 2 ? 2 : 4) : 1;
        p = dmodrm(d, p, isz); d->imm = imm_rd(&p, isz); d->sub = d->reg & 7; kind = K_ALU_MI; break;
    }
    case 0x84: case 0x85: d->sz = op == 0x84 ? 1 : osz; p = dmodrm(d, p, 0); kind = K_TEST_MR; break;
    case 0x88: case 0x89: d->sz = op == 0x88 ? 1 : osz; p = dmodrm(d, p, 0); kind = K_STORE; break;
    case 0x8A: case 0x8B: d->sz = op == 0x8A ? 1 : osz; p = dmodrm(d, p, 0); kind = K_MOV_RM; break;
    case 0x8D: p = dmodrm(d, p, 0); kind = d->isreg ? K_SLOW : K_LEA; break;
    case 0x90: kind = (d->rex & 1) ? K_SLOW : K_NOP; break;
    case 0x98: kind = K_CBW; break;
    case 0x99: kind = K_CWD; break;
    case 0xA8: d->sz = 1; d->imm = imm_rd(&p, 1); kind = K_TEST_AI; break;
    case 0xA9: d->sz = osz; d->imm = imm_rd(&p, osz == 2 ? 2 : 4); kind = K_TEST_AI; break;
    case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
        d->sz = 1; d->reg = (op & 7) | ((d->rex & 1) << 3); d->imm = *p++; kind = K_MOVI_R; break;
    case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF:
        d->sz = osz; d->reg = (op & 7) | ((d->rex & 1) << 3);
        if (osz == 8) { memcpy(&d->imm, p, 8); p += 8; }
        else if (osz == 4) { uint32_t t; memcpy(&t, p, 4); p += 4; d->imm = t; }
        else { uint16_t t; memcpy(&t, p, 2); p += 2; d->imm = t; }
        kind = K_MOVI_R; break;
    case 0xC0: case 0xC1: case 0xD0: case 0xD1: case 0xD2: case 0xD3:
        d->sz = (op & 1) ? osz : 1;
        p = dmodrm(d, p, op <= 0xC1 ? 1 : 0);
        d->sub = d->reg & 7;
        if (op <= 0xC1) { d->imm = *p++; kind = K_SHIFT; }
        else if (op <= 0xD1) { d->imm = 1; kind = K_SHIFT; }
        else kind = K_SHIFT_CL;
        break;
    case 0xC3: kind = K_RET; break;
    case 0xC6: case 0xC7: {
        d->sz = op == 0xC6 ? 1 : osz;
        int isz = d->sz == 1 ? 1 : d->sz == 2 ? 2 : 4;
        p = dmodrm(d, p, isz); d->imm = imm_rd(&p, isz); kind = K_MOVI_RM; break;
    }
    case 0xE8: { int32_t v; memcpy(&v, p, 4); p += 4; d->imm = (int64_t)((uint64_t)p + v); kind = K_CALL; break; }
    case 0xE9: { int32_t v; memcpy(&v, p, 4); p += 4; d->imm = (int64_t)((uint64_t)p + v); d->sub = 1; kind = K_JMP; break; }
    case 0xEB: { int64_t v = (int8_t)*p++; d->imm = (int64_t)((uint64_t)p + v); kind = K_JMP; break; }
    case 0xF6: case 0xF7: {
        d->sz = op == 0xF6 ? 1 : osz;
        int sub = (*p >> 3) & 7, isz = d->sz == 1 ? 1 : d->sz == 2 ? 2 : 4;
        if (sub >= 2) return;
        p = dmodrm(d, p, isz); d->imm = imm_rd(&p, isz); kind = K_TEST_MI; break;
    }
    case 0xFE: case 0xFF:
        d->sz = op == 0xFE ? 1 : osz;
        p = dmodrm(d, p, 0);
        d->sub = d->reg & 7;
        if (d->sub < 2) kind = K_INCDEC;
        break;
    case 0x0F: {
        uint8_t o2 = *p++;
        if (o2 >= 0x80 && o2 <= 0x8F) { int32_t v; memcpy(&v, p, 4); p += 4; d->sub = o2 & 15; d->imm = (int64_t)((uint64_t)p + v); kind = K_JCC; }
        else if (o2 >= 0x40 && o2 <= 0x4F) { p = dmodrm(d, p, 0); d->sub = o2 & 15; kind = K_CMOV; }
        else if (o2 >= 0x90 && o2 <= 0x9F) { p = dmodrm(d, p, 0); d->sub = o2 & 15; kind = K_SETCC; }
        else if (o2 == 0xAF) { p = dmodrm(d, p, 0); kind = K_IMUL2; }
        else if (o2 == 0xB6 || o2 == 0xB7 || o2 == 0xBE || o2 == 0xBF) { p = dmodrm(d, p, 0); d->ext = (o2 & 1 ? 2 : 1) | (o2 >= 0xBE ? 8 : 0); kind = K_MOVX; }
        else if (o2 == 0x1F) { p = dmodrm(d, p, 0); kind = K_NOP; }
        break;
    }
    }
    if (kind == K_SLOW) return;
    d->next = (uint64_t)p;
    d->kind = (uint8_t)kind;
}

/* ---------------- flags: which an instruction writes and reads ---------------- */
enum { F_CF = 1, F_ZF = 2, F_SF = 4, F_OF = 8, F_PF = 16, F_ALL = 31 };
static int cc_use(int cc) {
    static const uint8_t u[8] = {F_OF, F_CF, F_ZF, F_CF | F_ZF, F_SF, F_PF, F_SF | F_OF, F_ZF | F_SF | F_OF};
    return u[cc >> 1];
}
static unsigned shift_count(const Dec *d) { return (unsigned)d->imm & (d->sz == 8 ? 63 : 31); }
/* flags written (always), flags read */
static int f_def(const Dec *d) {
    switch (d->kind) {
    case K_ALU_MR: case K_ALU_RM: case K_ALU_AI: case K_ALU_MI: case K_TEST_MR: case K_TEST_MI: case K_TEST_AI: return F_ALL;
    case K_INCDEC: return F_ZF | F_SF | F_OF | F_PF;
    case K_IMUL2: case K_IMUL3: return F_CF | F_OF;
    case K_SHIFT: return shift_count(d) ? (d->sub < 2 ? F_CF | F_OF : F_ALL) : 0;
    default: return 0;
    }
}
/* flags a shift may write (not computed here: it's only translated when none of them is read) */
static int f_shift_may(const Dec *d) {
    if (d->kind == K_SHIFT && !shift_count(d)) return 0;
    return d->sub < 2 ? F_CF | F_OF : F_ALL;
}
static int f_use(const Dec *d) {
    switch (d->kind) {
    case K_JCC: case K_CMOV: case K_SETCC: return cc_use(d->sub);
    default: return 0;
    }
}
static int terminator(const Dec *d) { return d->kind == K_JCC || d->kind == K_JMP || d->kind == K_CALL || d->kind == K_RET; }

/* can this instruction be translated, given the flags read after it? */
static int translatable(const Dec *d, int live_after) {
    switch (d->kind) {
    case K_ALU_MR: case K_ALU_RM: case K_ALU_AI: case K_ALU_MI: return d->sub != 2 && d->sub != 3;   /* not adc/sbb */
    case K_SHIFT:
        if (d->sub == 2 || d->sub == 3) return 0;                       /* rcl/rcr */
        return (f_shift_may(d) & live_after) == 0;
    case K_SHIFT_CL:
        if (d->sub == 2 || d->sub == 3) return 0;
        if (d->sub < 2 && d->sz < 4) return 0;                          /* narrow rol/ror by cl */
        return (f_shift_may(d) & live_after) == 0;
    case K_SLOW: return 0;
    default: return 1;
    }
}

/* ---------------- ARM64 emission ---------------- */
static uint32_t *cp, *cbase, *cend;
static inline void E(uint32_t w) { if (cp < cend) *cp++ = w; else cp = cend + 1; }
enum { XCPU = 19, WCF = 20, WZF = 21, WSF = 22, WOF = 23, WPF = 24, XEA = 25, XVAL = 26, XPAR = 27, ZR = 31 };
enum { EQ = 0, NE = 1, CS = 2, CC = 3, MI = 4, PL = 5, VS = 6, VC = 7 };

static size_t o_cf, o_zf, o_sf, o_of, o_pf, o_rip, o_steps;

static void e_movz(int rd, unsigned imm, int hw) { E(0xD2800000u | (unsigned)hw << 21 | (imm & 0xffff) << 5 | rd); }
static void e_movk(int rd, unsigned imm, int hw) { E(0xF2800000u | (unsigned)hw << 21 | (imm & 0xffff) << 5 | rd); }
static void e_movn(int rd, unsigned imm, int hw) { E(0x92800000u | (unsigned)hw << 21 | (imm & 0xffff) << 5 | rd); }
static void e_imm(int rd, uint64_t v) {
    int ones = 0, zeros = 0;
    for (int i = 0; i < 4; i++) { unsigned c = (unsigned)(v >> (16 * i)) & 0xffff; ones += c == 0xffff; zeros += c == 0; }
    if (ones > zeros) {
        int first = 1;
        for (int i = 0; i < 4; i++) {
            unsigned c = (unsigned)(v >> (16 * i)) & 0xffff;
            if (first && (c != 0xffff || i == 3)) { e_movn(rd, ~c, i); first = 0; }
            else if (!first && c != 0xffff) e_movk(rd, c, i);
        }
    } else {
        int first = 1;
        for (int i = 0; i < 4; i++) {
            unsigned c = (unsigned)(v >> (16 * i)) & 0xffff;
            if (first && (c != 0 || i == 3)) { e_movz(rd, c, i); first = 0; }
            else if (!first && c != 0) e_movk(rd, c, i);
        }
    }
}
/* data processing, shifted register: base opcode for 64-bit (sf set); w = 32-bit */
#define OP_ADD 0x8B000000u
#define OP_ADDS 0xAB000000u
#define OP_SUB 0xCB000000u
#define OP_SUBS 0xEB000000u
#define OP_AND 0x8A000000u
#define OP_ORR 0xAA000000u
#define OP_EOR 0xCA000000u
static void e_rrr(uint32_t op, int w, int rd, int rn, int rm, int lsl) {
    E((w ? op & ~0x80000000u : op) | (unsigned)rm << 16 | (unsigned)lsl << 10 | (unsigned)rn << 5 | rd);
}
static void e_mov(int rd, int rm) { if (rd != rm) e_rrr(OP_ORR, 0, rd, ZR, rm, 0); }
static void e_movw(int rd, int rm) { e_rrr(OP_ORR, 1, rd, ZR, rm, 0); }                 /* zero-extends */
static void e_addi(int rd, int rn, unsigned imm) { E(0x91000000u | (imm & 0xfff) << 10 | (unsigned)rn << 5 | rd); }
static void e_subi(int rd, int rn, unsigned imm) { E(0xD1000000u | (imm & 0xfff) << 10 | (unsigned)rn << 5 | rd); }
static void e_cmp0x(int rn) { E(0xF100001Fu | (unsigned)rn << 5); }
static void e_cmp0w(int rn) { E(0x7100001Fu | (unsigned)rn << 5); }
static void e_ubfm(int w, int rd, int rn, int immr, int imms) { E((w ? 0x53000000u : 0xD3400000u) | (unsigned)immr << 16 | (unsigned)imms << 10 | (unsigned)rn << 5 | rd); }
static void e_sbfm(int w, int rd, int rn, int immr, int imms) { E((w ? 0x13000000u : 0x93400000u) | (unsigned)immr << 16 | (unsigned)imms << 10 | (unsigned)rn << 5 | rd); }
static void e_bfm(int rd, int rn, int immr, int imms) { E(0xB3400000u | (unsigned)immr << 16 | (unsigned)imms << 10 | (unsigned)rn << 5 | rd); }
static void e_bfi(int rd, int rn, int lsb, int width) { e_bfm(rd, rn, (64 - lsb) & 63, width - 1); }
static void e_ubfx(int rd, int rn, int lsb, int width) { e_ubfm(0, rd, rn, lsb, lsb + width - 1); }
static void e_lsli(int rd, int rn, int s) { e_ubfm(0, rd, rn, (64 - s) & 63, 63 - s); }
static void e_lsliw(int rd, int rn, int s) { e_ubfm(1, rd, rn, (32 - s) & 31, 31 - s); }
static void e_lsriw(int rd, int rn, int s) { e_ubfm(1, rd, rn, s, 31); }
static void e_sext(int rd, int rn, int sz) { if (sz < 8) e_sbfm(0, rd, rn, 0, sz * 8 - 1); else e_mov(rd, rn); }
static void e_zext(int rd, int rn, int sz) {
    if (sz == 8) e_mov(rd, rn);
    else if (sz == 4) e_movw(rd, rn);
    else e_ubfm(1, rd, rn, 0, sz * 8 - 1);
}
static void e_shv(int w, int kind, int rd, int rn, int rm) {   /* kind 0 lsl 1 lsr 2 asr 3 ror */
    E((w ? 0x1AC02000u : 0x9AC02000u) | (unsigned)kind << 10 | (unsigned)rm << 16 | (unsigned)rn << 5 | rd);
}
static void e_mul(int rd, int rn, int rm) { E(0x9B007C00u | (unsigned)rm << 16 | (unsigned)rn << 5 | rd); }
static void e_smulh(int rd, int rn, int rm) { E(0x9B407C00u | (unsigned)rm << 16 | (unsigned)rn << 5 | rd); }
static void e_csel(int w, int rd, int rn, int rm, int cond) { E((w ? 0x1A800000u : 0x9A800000u) | (unsigned)rm << 16 | (unsigned)cond << 12 | (unsigned)rn << 5 | rd); }
static void e_cset(int rd, int cond) { E(0x1A9F07E0u | (unsigned)(cond ^ 1) << 12 | rd); }
static void e_eor1(int rd, int rn) { E(0x52000000u | (unsigned)rn << 5 | rd); }   /* eor wd, wn, #1 */
static void e_ldst(uint32_t op, int sz, int rt, int rn, size_t off) {
    int sh = sz == 8 ? 3 : sz == 4 ? 2 : sz == 2 ? 1 : 0;
    E(op | (uint32_t)sh << 30 | (uint32_t)((off >> sh) & 0xfff) << 10 | (unsigned)rn << 5 | rt);
}
static void e_ldr(int sz, int rt, int rn, size_t off) { e_ldst(0x39400000u, sz, rt, rn, off); }   /* zero-extending */
static void e_str(int sz, int rt, int rn, size_t off) { e_ldst(0x39000000u, sz, rt, rn, off); }
static void e_ldrb_reg(int rt, int rn, int rm) { E(0x38606800u | (unsigned)rm << 16 | (unsigned)rn << 5 | rt); }
static void e_blr(int rn) { E(0xD63F0000u | (unsigned)rn << 5); }

/* guest registers and memory */
static void g_ld(int rd, int r) { e_ldr(8, rd, XCPU, 8 * (size_t)r); }
static void g_st(int rs, int r) { e_str(8, rs, XCPU, 8 * (size_t)r); }
static void get_reg(int rd, int r, int sz, int rex) {
    if (sz == 1 && !rex && r >= 4 && r < 8) { g_ld(rd, r - 4); e_ubfx(rd, rd, 8, 8); return; }
    g_ld(rd, r);
    if (sz < 8) e_zext(rd, rd, sz);
}
/* value in rs (its low sz bytes); x9 is used */
static void set_reg(int rs, int r, int sz, int rex) {
    switch (sz) {
    case 8: g_st(rs, r); break;
    case 4: e_movw(9, rs); g_st(9, r); break;
    case 2: g_ld(9, r); e_bfi(9, rs, 0, 16); g_st(9, r); break;
    default:
        if (!rex && r >= 4 && r < 8) { g_ld(9, r - 4); e_bfi(9, rs, 8, 8); g_st(9, r - 4); }
        else { g_ld(9, r); e_bfi(9, rs, 0, 8); g_st(9, r); }
        break;
    }
}
/* effective address of the memory operand into XEA (x9 used) */
static void ea(const Dec *d) {
    int started = 0;
    if (d->base != NOREG) { g_ld(XEA, d->base); started = 1; }
    if (d->index != NOREG) {
        g_ld(9, d->index);
        if (started) e_rrr(OP_ADD, 0, XEA, XEA, 9, d->scale);
        else { if (d->scale) e_lsli(XEA, 9, d->scale); else e_mov(XEA, 9); started = 1; }
    }
    int64_t disp = d->disp;
    if (!started) { e_imm(XEA, (uint64_t)disp); return; }
    if (!disp) return;
    if (disp > 0 && disp < 4096) e_addi(XEA, XEA, (unsigned)disp);
    else if (disp < 0 && disp > -4096) e_subi(XEA, XEA, (unsigned)-disp);
    else { e_imm(9, (uint64_t)disp); e_rrr(OP_ADD, 0, XEA, XEA, 9, 0); }
}

static int checking;
static void jit_log(uint64_t addr);
/* store rs (low sz bytes) at [XEA]; in check mode the old bytes are logged first */
static void st_mem(int rs, int sz) {
    if (checking) {
        e_mov(XVAL, rs);
        e_mov(0, XEA);
        e_imm(16, (uint64_t)(uintptr_t)jit_log);
        e_blr(16);
        rs = XVAL;
    }
    e_str(sz, rs, XEA, 0);
}
static void get_rm(int rd, const Dec *d, int sz) {     /* memory: XEA must hold the address */
    if (d->isreg) get_reg(rd, d->base, sz, d->rex);
    else e_ldr(sz, rd, XEA, 0);
}
static void set_rm(int rs, const Dec *d, int sz) {
    if (d->isreg) set_reg(rs, d->base, sz, d->rex);
    else st_mem(rs, sz);
}

/* zf/sf/pf of the masked result in xr, as needed */
static void flags_szp(int xr, int sz, int need) {
    if (need & F_SF) e_ubfx(WSF, xr, sz * 8 - 1, 1);
    if (need & F_PF) { e_ubfx(9, xr, 0, 8); e_ldrb_reg(WPF, XPAR, 9); }
    if (need & F_ZF) { e_cmp0x(xr); e_cset(WZF, EQ); }
}
/* condition cc (0..15, as x86) into w10 from the flag registers */
static void cond(int cc) {
    switch (cc >> 1) {
    case 0: e_mov(10, WOF); break;
    case 1: e_mov(10, WCF); break;
    case 2: e_mov(10, WZF); break;
    case 3: e_rrr(OP_ORR, 1, 10, WCF, WZF, 0); break;
    case 4: e_mov(10, WSF); break;
    case 5: e_mov(10, WPF); break;
    case 6: e_rrr(OP_EOR, 1, 10, WSF, WOF, 0); break;
    default: e_rrr(OP_EOR, 1, 10, WSF, WOF, 0); e_rrr(OP_ORR, 1, 10, 10, WZF, 0); break;
    }
    if (cc & 1) e_eor1(10, 10);
}

/* add (sub=0) or subtract (sub=1) x0 and x1 (masked to sz) into x2, with the flags needed */
static void addsub(int sub, int sz, int need) {
    int w = sz < 8;
    if (need & (F_CF | F_OF)) {
        if (sz >= 4) e_rrr(sub ? OP_SUBS : OP_ADDS, w, 2, 0, 1, 0);
        else {
            e_lsliw(3, 0, 32 - sz * 8); e_lsliw(4, 1, 32 - sz * 8);
            e_rrr(sub ? OP_SUBS : OP_ADDS, 1, 5, 3, 4, 0);
        }
        if (need & F_CF) e_cset(WCF, sub ? CC : CS);
        if (need & F_OF) e_cset(WOF, VS);
        if (sz < 4) { e_rrr(sub ? OP_SUB : OP_ADD, 1, 2, 0, 1, 0); e_zext(2, 2, sz); }
    } else {
        e_rrr(sub ? OP_SUB : OP_ADD, w, 2, 0, 1, 0);
        if (sz < 4) e_zext(2, 2, sz);
    }
    flags_szp(2, sz, need);
}

/* ---------------- blocks ---------------- */
#define MAXB 48
typedef void (*BlockFn)(X64Cpu *c);
typedef struct { uint64_t pc; BlockFn fn; } Blk;
#define NBLK (1 << 14)
static Blk *blk;
static int nblk;
static uint64_t code_lo, code_hi;
static uint8_t partab[256];
static uint64_t n_blocks, n_interp_steps;

static void exit_to(uint64_t rip, int rip_reg, int n, int defined) {
    if (defined & F_CF) e_str(1, WCF, XCPU, o_cf);
    if (defined & F_ZF) e_str(1, WZF, XCPU, o_zf);
    if (defined & F_SF) e_str(1, WSF, XCPU, o_sf);
    if (defined & F_OF) e_str(1, WOF, XCPU, o_of);
    if (defined & F_PF) e_str(1, WPF, XCPU, o_pf);
    if (rip_reg < 0) { e_imm(0, rip); rip_reg = 0; }
    e_str(8, rip_reg, XCPU, o_rip);
    e_ldr(8, 1, XCPU, o_steps); e_addi(1, 1, (unsigned)n); e_str(8, 1, XCPU, o_steps);
    /* epilogue */
    E(0xA94153F3u);   /* ldp x19, x20, [sp, #16] */
    E(0xA9425BF5u);   /* ldp x21, x22, [sp, #32] */
    E(0xA94363F7u);   /* ldp x23, x24, [sp, #48] */
    E(0xA9446BF9u);   /* ldp x25, x26, [sp, #64] */
    E(0xA94573FBu);   /* ldp x27, x28, [sp, #80] */
    E(0xA8C67BFDu);   /* ldp x29, x30, [sp], #96 */
    E(0xD65F03C0u);   /* ret */
}

/* one instruction; returns 0 if it couldn't be emitted */
static int emit(const Dec *d, int need) {
    int sz = d->sz, w = sz < 8;
    switch (d->kind) {
    case K_ALU_MR: case K_ALU_RM: case K_ALU_AI: case K_ALU_MI:
    case K_TEST_MR: case K_TEST_MI: case K_TEST_AI: {
        int k = d->kind;
        int mem = (k == K_ALU_MR || k == K_ALU_RM || k == K_ALU_MI || k == K_TEST_MR || k == K_TEST_MI) && !d->isreg;
        if (mem) ea(d);
        /* a: destination operand, b: source */
        if (k == K_ALU_MR || k == K_ALU_MI || k == K_TEST_MR || k == K_TEST_MI) get_rm(0, d, sz);
        else if (k == K_ALU_RM) get_reg(0, d->reg, sz, d->rex);
        else get_reg(0, 0, sz, 1);
        if (k == K_ALU_MR || k == K_TEST_MR) get_reg(1, d->reg, sz, d->rex);
        else if (k == K_ALU_RM) get_rm(1, d, sz);
        else e_imm(1, (uint64_t)d->imm & (sz == 8 ? ~0ULL : (1ULL << (sz * 8)) - 1));
        int op = (k == K_TEST_MR || k == K_TEST_MI || k == K_TEST_AI) ? 4 : d->sub;
        if (op == 0 || op == 5 || op == 7) addsub(op != 0, sz, need);
        else {
            e_rrr(op == 1 ? OP_ORR : op == 4 ? OP_AND : OP_EOR, 0, 2, 0, 1, 0);
            if (need & F_CF) e_movw(WCF, ZR);
            if (need & F_OF) e_movw(WOF, ZR);
            flags_szp(2, sz, need);
        }
        if (op == 7 || k == K_TEST_MR || k == K_TEST_MI || k == K_TEST_AI) return 1;
        if (k == K_ALU_MR || k == K_ALU_MI) set_rm(2, d, sz);
        else if (k == K_ALU_RM) set_reg(2, d->reg, sz, d->rex);
        else set_reg(2, 0, sz, 1);
        return 1;
    }
    case K_INCDEC:
        if (!d->isreg) ea(d);
        get_rm(0, d, sz);
        e_movz(1, 1, 0);
        addsub(d->sub, sz, need & ~F_CF);
        set_rm(2, d, sz);
        return 1;
    case K_MOV_RM:
        if (!d->isreg) ea(d);
        get_rm(0, d, sz);
        set_reg(0, d->reg, sz, d->rex);
        return 1;
    case K_STORE:
        get_reg(0, d->reg, sz, d->rex);
        if (!d->isreg) ea(d);
        set_rm(0, d, sz);
        return 1;
    case K_MOVI_RM:
        if (!d->isreg) ea(d);
        e_imm(0, (uint64_t)d->imm);
        set_rm(0, d, sz);
        return 1;
    case K_MOVI_R:
        e_imm(0, (uint64_t)d->imm);
        if (sz == 8 || sz == 4) g_st(0, d->reg);              /* imm32 was stored zero-extended */
        else set_reg(0, d->reg, sz, sz == 1 ? d->rex : 1);
        return 1;
    case K_MOVX: {
        int s = d->ext & 7;
        if (!d->isreg) ea(d);
        get_rm(0, d, s);
        if (d->ext & 8) e_sext(0, 0, s);
        set_reg(0, d->reg, d->osz, d->rex);
        return 1;
    }
    case K_LEA:
        ea(d);
        set_reg(XEA, d->reg, d->osz, d->rex);
        return 1;
    case K_CBW:
        g_ld(0, 0);
        if (d->osz == 8) { e_sext(0, 0, 4); g_st(0, 0); }
        else if (d->osz == 4) { e_sext(0, 0, 2); e_movw(0, 0); g_st(0, 0); }
        else { e_sext(0, 0, 1); set_reg(0, 0, 2, 1); }
        return 1;
    case K_CWD:
        g_ld(0, 0);
        if (d->osz == 8) { e_sbfm(0, 0, 0, 63, 63); g_st(0, 2); }
        else if (d->osz == 4) { e_sbfm(1, 0, 0, 31, 31); e_movw(0, 0); g_st(0, 2); }
        else { e_sbfm(1, 0, 0, 15, 15); set_reg(0, 2, 2, 1); }
        return 1;
    case K_PUSH:
        g_ld(1, d->reg);
        g_ld(0, 4);
        e_subi(XEA, 0, 8);
        st_mem(1, 8);
        g_st(XEA, 4);
        return 1;
    case K_POP:
        g_ld(0, 4);
        e_ldr(8, 1, 0, 0);
        e_addi(0, 0, 8);
        g_st(0, 4);
        g_st(1, d->reg);
        return 1;
    case K_NOP:
        return 1;
    case K_SHIFT: case K_SHIFT_CL: {
        if (!d->isreg) ea(d);
        get_rm(0, d, sz);
        int bits = sz * 8, op = d->sub;
        if (d->kind == K_SHIFT) {
            unsigned n = shift_count(d);
            if (!n) { set_rm(0, d, sz); return 1; }
            if (op == 0 || op == 1) {
                n %= (unsigned)bits;
                if (!n) { set_rm(0, d, sz); return 1; }
                unsigned left = op == 0 ? n : (unsigned)bits - n;     /* rotate left by `left` */
                e_lsli(1, 0, (int)left);
                e_ubfm(0, 2, 0, (int)(bits - left), bits - 1);         /* v >> (bits-left) */
                e_rrr(OP_ORR, 0, 2, 2, 1, 0);
                e_zext(2, 2, sz);
            } else if (op == 4 || op == 6) {
                if (n >= 64) e_movz(2, 0, 0); else { e_lsli(2, 0, (int)n); e_zext(2, 2, sz); }
            } else if (op == 5) {
                e_ubfm(0, 2, 0, (int)n, 63);
            } else {
                e_sext(2, 0, sz);
                e_sbfm(0, 2, 2, (int)n, 63);
                e_zext(2, 2, sz);
            }
            set_rm(2, d, sz);
            return 1;
        }
        /* by cl: count = cl & (63 or 31); a count of 0 leaves the value */
        g_ld(1, 1);
        E(((sz == 8) ? 0x92401421u : 0x92401021u));                  /* and x1, x1, #63 / #31 */
        if (op == 0 || op == 1) {                                     /* 32/64-bit only */
            if (op == 0) e_rrr(OP_SUB, w, 1, ZR, 1, 0);               /* rol n = ror -n */
            e_shv(w, 3, 2, 0, 1);
        } else if (op == 4 || op == 6) { e_shv(0, 0, 2, 0, 1); e_zext(2, 2, sz); }
        else if (op == 5) e_shv(0, 1, 2, 0, 1);
        else { e_sext(2, 0, sz); e_shv(0, 2, 2, 2, 1); e_zext(2, 2, sz); }
        set_rm(2, d, sz);
        return 1;
    }
    case K_CMOV: {
        int o = d->osz;
        if (!d->isreg) ea(d);
        get_rm(1, d, o);
        g_ld(2, d->reg);
        cond(d->sub);
        e_cmp0w(10);
        if (o == 8) e_csel(0, 3, 1, 2, NE);
        else if (o == 4) e_csel(1, 3, 1, 2, NE);
        else { e_mov(4, 2); e_bfi(4, 1, 0, 16); e_csel(0, 3, 4, 2, NE); }
        g_st(3, d->reg);
        return 1;
    }
    case K_SETCC:
        if (!d->isreg) ea(d);
        cond(d->sub);
        set_rm(10, d, 1);
        return 1;
    case K_IMUL2: case K_IMUL3: {
        int o = d->osz;
        if (!d->isreg) ea(d);
        if (d->kind == K_IMUL2) { get_reg(0, d->reg, o, d->rex); e_sext(0, 0, o); get_rm(1, d, o); e_sext(1, 1, o); }
        else { get_rm(0, d, o); e_sext(0, 0, o); e_imm(1, (uint64_t)d->imm); }
        e_mul(2, 0, 1);
        if (need & (F_CF | F_OF)) {
            if (o == 8) { e_smulh(3, 0, 1); e_sbfm(0, 4, 2, 63, 63); e_rrr(OP_SUBS, 0, ZR, 3, 4, 0); }
            else { e_sext(3, 2, o); e_rrr(OP_SUBS, 0, ZR, 3, 2, 0); }
            e_cset(WCF, NE);
            e_mov(WOF, WCF);
        }
        set_reg(2, d->reg, o, d->rex);
        return 1;
    }
    }
    return 0;
}

static BlockFn translate(uint64_t pc) {
    Dec ins[MAXB];
    int n = 0;
    uint64_t at = pc;
    while (n < MAXB) {
        decode(&ins[n], at);
        Dec *d = &ins[n];
        if (d->kind == K_SLOW) break;
        if ((d->kind == K_CALL || (d->kind == K_JMP && d->sub)) && ((uint64_t)d->imm < code_lo || (uint64_t)d->imm >= code_hi)) break;
        n++;
        at = d->next;
        if (terminator(d)) break;
    }
    int live[MAXB + 1];
    for (;;) {
        live[n] = F_ALL;
        for (int i = n - 1; i >= 0; i--) live[i] = (live[i + 1] & ~f_def(&ins[i])) | f_use(&ins[i]);
        int cut = -1;
        for (int i = 0; i < n; i++) if (!translatable(&ins[i], live[i + 1])) { cut = i; break; }
        if (cut < 0) break;
        n = cut;
    }
    if (!n) return NULL;
    if (cend - cp < 4096) return NULL;
    int defined = 0;
    for (int i = 0; i < n; i++) defined |= f_def(&ins[i]);

    uint32_t *start = cp;
    /* prologue: stp x29, x30, [sp, #-96]!; stp x19..x28; cpu, parity table, flags */
    E(0xA9BA7BFDu);
    E(0xA90153F3u); E(0xA9025BF5u); E(0xA90363F7u); E(0xA9046BF9u); E(0xA90573FBu);
    e_mov(XCPU, 0);
    e_imm(XPAR, (uint64_t)(uintptr_t)partab);
    e_ldr(1, WCF, XCPU, o_cf); e_ldr(1, WZF, XCPU, o_zf); e_ldr(1, WSF, XCPU, o_sf);
    e_ldr(1, WOF, XCPU, o_of); e_ldr(1, WPF, XCPU, o_pf);

    for (int i = 0; i < n; i++) {
        Dec *d = &ins[i];
        int need = f_def(d) & live[i + 1];
        switch (d->kind) {
        case K_JCC: {
            cond(d->sub);
            uint32_t *br = cp;
            E(0);                                              /* cbz w10, not taken */
            exit_to((uint64_t)d->imm, -1, n, defined);
            if (br < cend) *br = 0x34000000u | (uint32_t)(((cp - br) & 0x7ffff) << 5) | 10;
            exit_to(d->next, -1, n, defined);
            break;
        }
        case K_JMP: exit_to((uint64_t)d->imm, -1, n, defined); break;
        case K_CALL:
            g_ld(0, 4);
            e_subi(XEA, 0, 8);
            e_imm(1, d->next);
            st_mem(1, 8);
            g_st(XEA, 4);
            exit_to((uint64_t)d->imm, -1, n, defined);
            break;
        case K_RET:
            g_ld(0, 4);
            e_ldr(8, 1, 0, 0);
            e_addi(0, 0, 8);
            g_st(0, 4);
            exit_to(0, 1, n, defined);
            break;
        default:
            if (!emit(d, need)) return NULL;
            if (i == n - 1) exit_to(d->next, -1, n, defined);
            break;
        }
    }
    if (cp > cend) return NULL;
    __builtin___clear_cache((char *)start, (char *)cp);
    n_blocks++;
    return (BlockFn)(void *)start;
}

/* ---------------- check mode ---------------- */
#define MAXLOG 256
static struct { uint64_t addr; uint8_t old[8]; } logv[MAXLOG];
static int nlog;
static uint64_t checked, check_diffs;
static void jit_log(uint64_t addr) {
    if (nlog < MAXLOG) { logv[nlog].addr = addr; memcpy(logv[nlog].old, (void *)addr, 8); }
    nlog++;
}

static void check_block(X64Cpu *c, BlockFn fn) {
    X64Cpu start = *c;
    nlog = 0;
    fn(c);
    X64Cpu fast = *c;
    int nl = nlog < MAXLOG ? nlog : MAXLOG;
    uint8_t now[MAXLOG][8];
    for (int i = 0; i < nl; i++) memcpy(now[i], (void *)logv[i].addr, 8);
    for (int i = nl - 1; i >= 0; i--) memcpy((void *)logv[i].addr, logv[i].old, 8);
    *c = start;
    uint64_t n = fast.steps - start.steps;
    for (uint64_t i = 0; i < n && !c->error; i++) x64_step(c);
    int bad = nlog > MAXLOG || c->rip != fast.rip || memcmp(c->r, fast.r, sizeof c->r) || c->cf != fast.cf ||
              c->zf != fast.zf || c->sf != fast.sf || c->of != fast.of || c->pf != fast.pf;
    for (int i = 0; i < nl; i++) bad |= memcmp(now[i], (void *)logv[i].addr, 8) != 0;
    checked++;
    if (bad && check_diffs++ < 40) {
        uint8_t *p = (uint8_t *)start.rip;
        fprintf(stderr, "x64 jit check: block at +%llx differs after %llu instructions: %02x %02x %02x %02x %02x %02x %02x %02x\n",
                (unsigned long long)(start.rip - code_lo), (unsigned long long)n, p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7]);
        for (int i = 0; i < 16; i++)
            if (c->r[i] != fast.r[i]) fprintf(stderr, "  r%d jit %llx interp %llx\n", i, (unsigned long long)fast.r[i], (unsigned long long)c->r[i]);
        if (c->rip != fast.rip) fprintf(stderr, "  rip jit +%llx interp +%llx\n", (unsigned long long)(fast.rip - code_lo), (unsigned long long)(c->rip - code_lo));
        fprintf(stderr, "  flags jit c%d z%d s%d o%d p%d interp c%d z%d s%d o%d p%d\n", fast.cf, fast.zf, fast.sf, fast.of, fast.pf, c->cf, c->zf, c->sf, c->of, c->pf);
    }
    if (checked % 1000000 == 0)
        printf("x64 jit check: %llu blocks compared, %llu differ\n", (unsigned long long)checked, (unsigned long long)check_diffs);
}

/* ---------------- dispatcher ---------------- */
static void flush(void) {
    memset(blk, 0, NBLK * sizeof *blk);
    nblk = 0;
    cp = cbase;
}

static Blk *lookup(uint64_t pc) {
    uint64_t h = (pc * 0x9E3779B97F4A7C15ULL) >> (64 - 14);
    for (;;) {
        Blk *b = &blk[h];
        if (b->pc == pc) return b;
        if (!b->pc) return b;
        h = (h + 1) & (NBLK - 1);
    }
}

void x64_jit_run(X64Cpu *c) {
    while (!c->error && c->rip != X64_SENTINEL) {
        uint64_t pc = c->rip;
        Blk *b = lookup(pc);
        if (b->pc != pc) {
            if (nblk > NBLK * 3 / 4 || cend - cp < 8192) { flush(); b = lookup(pc); }
            b->pc = pc;
            b->fn = pc >= code_lo && pc < code_hi ? translate(pc) : NULL;
            nblk++;
        }
        if (!b->fn) { x64_step(c); n_interp_steps++; }
        else if (checking) check_block(c, b->fn);
        else b->fn(c);
    }
}

int x64_jit_init(X64Cpu *c, uint64_t lo, uint64_t hi, int check) {
    size_t size = 32 << 20;
    void *mem = mmap(NULL, size, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (mem == MAP_FAILED) return 0;
    blk = calloc(NBLK, sizeof *blk);
    if (!blk) { munmap(mem, size); return 0; }
    cbase = cp = mem;
    cend = cbase + size / 4;
    code_lo = lo; code_hi = hi;
    checking = check;
    for (int i = 0; i < 256; i++) partab[i] = !__builtin_parity((unsigned)i);
    o_cf = offsetof(X64Cpu, cf); o_zf = offsetof(X64Cpu, zf); o_sf = offsetof(X64Cpu, sf);
    o_of = offsetof(X64Cpu, of); o_pf = offsetof(X64Cpu, pf); o_rip = offsetof(X64Cpu, rip); o_steps = offsetof(X64Cpu, steps);
    c->jit = 1;
    return 1;
}

void x64_jit_stats(uint64_t *blocks, uint64_t *interp) { *blocks = n_blocks; *interp = n_interp_steps; n_interp_steps = 0; }
