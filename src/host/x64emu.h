/* x64emu: a small x86-64 interpreter for user-mode Windows code (MSVC output: integer ops,
   scalar/packed SSE, string ops). Guest addresses are host addresses: the guest image is
   loaded into host memory and pointers pass both ways unchanged. Calls into "host" code
   (the guest's imports, or any address the import hook claims) go through a callback. */
#ifndef FF9_X64EMU_H
#define FF9_X64EMU_H
#include <stdint.h>

typedef union {
    uint8_t b[16]; uint16_t w[8]; uint32_t d[4]; uint64_t q[2];
    int32_t i[4]; int64_t l[2]; float f[4]; double g[2];
} XmmReg;

typedef struct X64Cpu X64Cpu;

/* returns 1 if `target` is host code and was called (result left in rax/xmm0), 0 otherwise */
typedef int (*X64HostCall)(X64Cpu *c, uint64_t target);

/* Native code standing in for a stretch of guest code: the guest bytes there are D6 <n> (D6 is
   not an x86-64 instruction), which runs hooks[n]. The hook updates the state as the guest
   code would, sets rip to where the guest continues, and returns how many guest instructions
   it stood in for (counted in `steps`). */
typedef uint64_t (*X64Hook)(X64Cpu *c);
#define X64_HOOKS 4

struct X64Cpu {
    uint64_t r[16];           /* rax rcx rdx rbx rsp rbp rsi rdi r8..r15 */
    uint64_t rip;
    uint8_t cf, zf, sf, of, pf, df;
    XmmReg x[16];
    uint64_t gsbase, fsbase;
    uint32_t mxcsr;
    X64HostCall host;
    int error;                /* set when an instruction can't be run; the call returns early */
    uint64_t steps;
    X64Hook hooks[X64_HOOKS];
    int jit;                  /* run through x64jit.c (x64_jit_init) */
    uint32_t *hist; uint64_t hist_lo, hist_n;   /* FF9_TRACE_X64_HIST: instructions run at each address */
};

enum { RAX, RCX, RDX, RBX, RSP, RBP, RSI, RDI, R8, R9, R10, R11, R12, R13, R14, R15 };

/* Run from c->rip until a `ret` pops X64_SENTINEL (pushed by x64_call). */
#define X64_SENTINEL 0x00005E9715E9715EULL
void x64_run(X64Cpu *c);

/* x64jit.c: translate guest code in [lo, hi) to ARM64 as it runs (check: compare every block
   with the interpreter). Returns 0 if no executable memory; the interpreter is used then. */
int x64_jit_init(X64Cpu *c, uint64_t lo, uint64_t hi, int check);
void x64_jit_run(X64Cpu *c);
void x64_jit_stats(uint64_t *blocks, uint64_t *interpreted);

/* One instruction (tests: comparing a hook with the guest code it stands in for). */
void x64_step(X64Cpu *c);

/* Call guest code at `addr` with the Windows x64 convention: integer args in rcx/rdx/r8/r9,
   the rest on the stack; `fargs` (may be NULL) gives xmm0..3 for float/double positions.
   Saves and restores the caller-visible state, so host code may call it re-entrantly. */
uint64_t x64_call(X64Cpu *c, uint64_t addr, int nargs, const uint64_t *args, const XmmReg *fargs);

/* The i-th argument of a call being made by guest code to host code (rsp as at the call). */
uint64_t x64_arg(X64Cpu *c, int i);

#endif
