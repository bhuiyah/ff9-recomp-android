/* FF9SpecialEffectPlugin on Android: the player's own Windows DLL (x64/FF9_Data/Plugins, from
   their install) is loaded into memory at run time and executed by the x64 interpreter
   (x64emu.c). Nothing from the DLL is stored or shipped: it is read from the game folder.

   The DLL is plain MSVC C++ that imports only the C runtime and a few kernel32 calls; those
   are provided here. The game's callback (SFX_InitSystem) is a native function pointer and is
   called directly. If the DLL can't be loaded the entry points do nothing (fixed camera). */
#include "x64emu.h"
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>

#define EXPORT __attribute__((visibility("default")))

static X64Cpu cpu;
static uint8_t *image;
static size_t image_size;
static uint8_t *stubs;            /* one 16-byte slot per import: the address the IAT points to */
static int loaded = -1;           /* -1 not tried, 0 failed, 1 ok */
static int trace;

enum {
    I_UNKNOWN, I_printf, I_lock, I_unlock, I_calloc_crt, I_dllonexit, I_C_specific_handler, I_onexit,
    I_terminate, I_crtUnhandledException, I_crtTerminateProcess, I_crtCapturePreviousContext,
    I_CppXcptFilter, I_amsg_exit, I_malloc_crt, I_initterm, I_initterm_e, I_type_info_dtor,
    I_clean_type_info_names, I_sprintf, I_delete_arr, I_new_arr, I_srand, I_time64, I_malloc, I_free,
    I_new, I_delete, I_purecall, I_memmove, I_crt_debugger_hook, I_wassert, I_CxxThrowException,
    I_CxxFrameHandler3, I_atan2, I_cos, I_memcpy, I_memset, I_sin, I_sqrt,
    I_Xlength_error, I_Xout_of_range, I_Xbad_alloc, I_Syserror_map, I_Winerror_map,
    I_GetSystemTimeAsFileTime, I_GetCurrentThreadId, I_GetCurrentProcessId, I_QueryPerformanceCounter,
    I_IsProcessorFeaturePresent, I_IsDebuggerPresent, I_DecodePointer, I_EncodePointer,
    I_DisableThreadLibraryCalls, I_COUNT
};
static const struct { const char *name; int id; } known[] = {
    {"printf", I_printf}, {"_lock", I_lock}, {"_unlock", I_unlock}, {"_calloc_crt", I_calloc_crt},
    {"__dllonexit", I_dllonexit}, {"__C_specific_handler", I_C_specific_handler}, {"_onexit", I_onexit},
    {"?terminate@@YAXXZ", I_terminate}, {"__crtUnhandledException", I_crtUnhandledException},
    {"__crtTerminateProcess", I_crtTerminateProcess}, {"__crtCapturePreviousContext", I_crtCapturePreviousContext},
    {"__CppXcptFilter", I_CppXcptFilter}, {"_amsg_exit", I_amsg_exit}, {"_malloc_crt", I_malloc_crt},
    {"_initterm", I_initterm}, {"_initterm_e", I_initterm_e},
    {"?_type_info_dtor_internal_method@type_info@@QEAAXXZ", I_type_info_dtor},
    {"__clean_type_info_names_internal", I_clean_type_info_names}, {"sprintf", I_sprintf},
    {"??_V@YAXPEAX@Z", I_delete_arr}, {"??_U@YAPEAX_K@Z", I_new_arr}, {"srand", I_srand}, {"_time64", I_time64},
    {"malloc", I_malloc}, {"free", I_free}, {"??2@YAPEAX_K@Z", I_new}, {"??3@YAXPEAX@Z", I_delete},
    {"_purecall", I_purecall}, {"memmove", I_memmove}, {"__crt_debugger_hook", I_crt_debugger_hook},
    {"_wassert", I_wassert}, {"_CxxThrowException", I_CxxThrowException}, {"__CxxFrameHandler3", I_CxxFrameHandler3},
    {"atan2", I_atan2}, {"cos", I_cos}, {"memcpy", I_memcpy}, {"memset", I_memset}, {"sin", I_sin}, {"sqrt", I_sqrt},
    {"?_Xlength_error@std@@YAXPEBD@Z", I_Xlength_error}, {"?_Xout_of_range@std@@YAXPEBD@Z", I_Xout_of_range},
    {"?_Xbad_alloc@std@@YAXXZ", I_Xbad_alloc}, {"?_Syserror_map@std@@YAPEBDH@Z", I_Syserror_map},
    {"?_Winerror_map@std@@YAPEBDH@Z", I_Winerror_map},
    {"GetSystemTimeAsFileTime", I_GetSystemTimeAsFileTime}, {"GetCurrentThreadId", I_GetCurrentThreadId},
    {"GetCurrentProcessId", I_GetCurrentProcessId}, {"QueryPerformanceCounter", I_QueryPerformanceCounter},
    {"IsProcessorFeaturePresent", I_IsProcessorFeaturePresent}, {"IsDebuggerPresent", I_IsDebuggerPresent},
    {"DecodePointer", I_DecodePointer}, {"EncodePointer", I_EncodePointer},
    {"DisableThreadLibraryCalls", I_DisableThreadLibraryCalls},
};
#define MAX_IMPORTS 128
static int import_id[MAX_IMPORTS];
static char import_name[MAX_IMPORTS][64];
static int nimports;

enum { E_InitSystem, E_StartPlungeCamera, E_SkipCameraAnimation, E_InitBattle, E_Update, E_LateUpdate,
       E_UpdateCamera, E_MoveFreeCamera, E_SendFloatData, E_SendIntData, E_Play, E_BeginRender, E_GetPrim, E_COUNT };
static const char *export_names[E_COUNT] = {
    "SFX_InitSystem", "SFX_StartPlungeCamera", "SFX_SkipCameraAnimation", "SFX_InitBattle", "SFX_Update",
    "SFX_LateUpdate", "SFX_UpdateCamera", "SFX_MoveFreeCamera", "SFX_SendFloatData", "SFX_SendIntData",
    "SFX_Play", "SFX_BeginRender", "SFX_GetPrim"};
static uint64_t exports[E_COUNT];

/* profiling (read by the engine's FF9_TRACE_PROFILE report): ns in each export (the game's own
   callbacks included), ns and count of those callbacks, x86-64 instructions run */
static uint64_t prof_ns[E_COUNT], prof_cb_ns, prof_cb_n, prof_steps;
static inline uint64_t now_ns(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec; }
EXPORT int ff9_sfx_prof(uint64_t *out, int n) {
    int k = 0;
    for (int e = 0; e < E_COUNT && k < n; e++) { out[k++] = prof_ns[e]; prof_ns[e] = 0; }
    if (k < n) out[k++] = prof_cb_ns;
    if (k < n) out[k++] = prof_cb_n;
    if (k < n) out[k++] = prof_steps;
    prof_cb_ns = prof_cb_n = prof_steps = 0;
    return k;
}

static inline uint32_t u32(const uint8_t *p) { uint32_t v; memcpy(&v, p, 4); return v; }
static inline uint16_t u16(const uint8_t *p) { uint16_t v; memcpy(&v, p, 2); return v; }
static inline uint64_t u64(const uint8_t *p) { uint64_t v; memcpy(&v, p, 8); return v; }

/* guest printf-style formatting, arguments from guest argument slots starting at `first` */
static int guest_format(char *out, size_t cap, const char *fmt, int first) {
    size_t n = 0;
    int ai = first;
    for (const char *f = fmt; *f && n + 1 < cap; f++) {
        if (*f != '%') { out[n++] = *f; continue; }
        if (f[1] == '%') { out[n++] = '%'; f++; continue; }
        char spec[32]; int k = 0;
        spec[k++] = '%';
        f++;
        while (*f && strchr("-+ #0123456789.", *f) && k < 20) spec[k++] = *f++;
        int is64 = 0;
        while (*f == 'l' || *f == 'h' || *f == 'I' || *f == '6' || *f == '4' || *f == 'z') { if (*f == 'l' || *f == '6' || *f == 'z') is64 = 1; f++; }
        char conv = *f;
        if (!conv) break;
        uint64_t a = x64_arg(&cpu, ai++);
        int w;
        if (conv == 's') { spec[k++] = 's'; spec[k] = 0; w = snprintf(out + n, cap - n, spec, a ? (const char *)a : "(null)"); }
        else if (strchr("fFeEgGaA", conv)) { double d; memcpy(&d, &a, 8); spec[k++] = conv; spec[k] = 0; w = snprintf(out + n, cap - n, spec, d); }
        else if (conv == 'p') { spec[k++] = 'p'; spec[k] = 0; w = snprintf(out + n, cap - n, spec, (void *)a); }
        else if (conv == 'c') { spec[k++] = 'c'; spec[k] = 0; w = snprintf(out + n, cap - n, spec, (int)a); }
        else {
            spec[k++] = 'l'; spec[k++] = 'l'; spec[k++] = conv; spec[k] = 0;
            int64_t v = is64 ? (int64_t)a : (conv == 'd' || conv == 'i') ? (int64_t)(int32_t)a : (int64_t)(uint32_t)a;
            w = snprintf(out + n, cap - n, spec, v);
        }
        if (w > 0) n += (size_t)w < cap - n ? (size_t)w : cap - n - 1;
    }
    out[n] = 0;
    return (int)n;
}

static void guest_fail(const char *what) {
    fprintf(stderr, "sfx plugin: %s\n", what);
    cpu.error = 1;
}

static int host_call(X64Cpu *c, uint64_t target) {
    if (stubs && target >= (uint64_t)stubs && target < (uint64_t)stubs + MAX_IMPORTS * 16) {
        int idx = (int)((target - (uint64_t)stubs) / 16);
        int id = import_id[idx];
        uint64_t a0 = x64_arg(c, 0), a1 = x64_arg(c, 1), a2 = x64_arg(c, 2);
        uint64_t r = 0;
        switch (id) {
        case I_printf: { char buf[1024]; guest_format(buf, sizeof buf, (const char *)a0, 1); if (trace) fputs(buf, stdout); r = strlen(buf); break; }
        case I_sprintf: { char buf[4096]; int n = guest_format(buf, sizeof buf, (const char *)a1, 2); memcpy((void *)a0, buf, (size_t)n + 1); r = (uint64_t)n; break; }
        case I_lock: case I_unlock: case I_srand: case I_type_info_dtor: case I_clean_type_info_names:
        case I_crtCapturePreviousContext: case I_crt_debugger_hook: break;
        case I_calloc_crt: r = (uint64_t)calloc(a0, a1); break;
        case I_malloc_crt: case I_malloc: case I_new: case I_new_arr: r = (uint64_t)malloc(a0 ? a0 : 1); break;
        case I_free: case I_delete: case I_delete_arr: free((void *)a0); break;
        case I_dllonexit: case I_onexit: r = a0; break;
        case I_initterm: case I_initterm_e:
            for (uint64_t p = a0; p < a1 && !c->error; p += 8) {
                uint64_t fn = u64((const uint8_t *)p);
                if (!fn) continue;
                uint64_t v = x64_call(c, fn, 0, NULL, NULL);
                if (id == I_initterm_e && (uint32_t)v) { r = (uint32_t)v; break; }
            }
            break;
        case I_time64: { time_t t = time(NULL); if (a0) memcpy((void *)a0, &(int64_t){(int64_t)t}, 8); r = (uint64_t)t; break; }
        case I_memmove: r = (uint64_t)memmove((void *)a0, (const void *)a1, a2); break;
        case I_memcpy: r = (uint64_t)memcpy((void *)a0, (const void *)a1, a2); break;
        case I_memset: r = (uint64_t)memset((void *)a0, (int)a1, a2); break;
        case I_sin: c->x[0].g[0] = sin(c->x[0].g[0]); return 1;
        case I_cos: c->x[0].g[0] = cos(c->x[0].g[0]); return 1;
        case I_sqrt: c->x[0].g[0] = sqrt(c->x[0].g[0]); return 1;
        case I_atan2: c->x[0].g[0] = atan2(c->x[0].g[0], c->x[1].g[0]); return 1;
        case I_wassert: fprintf(stderr, "sfx plugin: assertion failed\n"); break;
        case I_crtUnhandledException: case I_CppXcptFilter: case I_IsDebuggerPresent: r = 0; break;
        case I_GetSystemTimeAsFileTime: { uint64_t ft = (uint64_t)time(NULL) * 10000000ULL + 116444736000000000ULL; memcpy((void *)a0, &ft, 8); break; }
        case I_GetCurrentThreadId: r = (uint64_t)gettid(); break;
        case I_GetCurrentProcessId: r = (uint64_t)getpid(); break;
        case I_QueryPerformanceCounter: { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); uint64_t v = (uint64_t)ts.tv_sec * 1000000000ULL + (uint64_t)ts.tv_nsec; memcpy((void *)a0, &v, 8); r = 1; break; }
        case I_IsProcessorFeaturePresent: r = (a0 == 6 || a0 == 7 || a0 == 10) ? 1 : 0; break;   /* SSE, SSE2 */
        case I_DecodePointer: case I_EncodePointer: r = a0; break;
        case I_DisableThreadLibraryCalls: r = 1; break;
        default: {
            char msg[128];
            snprintf(msg, sizeof msg, "unsupported import %s", import_name[idx]);
            guest_fail(msg);
            break;
        }
        }
        c->r[RAX] = r;
        return 1;
    }
    if (target >= (uint64_t)image && target < (uint64_t)image + image_size) return 0;   /* guest code */
    /* a native function pointer (the game's SFX callback): int f(int, int, int, int, int, void *) */
    typedef int (*Cb)(int, int, int, int, int, void *);
    uint64_t t0 = now_ns();
    int v = ((Cb)target)((int)x64_arg(c, 0), (int)x64_arg(c, 1), (int)x64_arg(c, 2), (int)x64_arg(c, 3), (int)x64_arg(c, 4), (void *)x64_arg(c, 5));
    prof_cb_ns += now_ns() - t0; prof_cb_n++;
    c->r[RAX] = (uint32_t)v;
    return 1;
}

static int load(void) {
    if (loaded >= 0) return loaded;
    loaded = 0;
    trace = getenv("FF9_SFX_TRACE") != NULL;
    if (getenv("FF9_SFX_STUB")) return 0;
    const char *path = getenv("FF9_SFX_PLUGIN");
    if (!path) path = "x64/FF9_Data/Plugins/FF9SpecialEffectPlugin.dll";
    FILE *f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "sfx plugin: %s not found; battle camera and effects stay fixed\n", path); return 0; }
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *file = malloc((size_t)len);
    if (!file || fread(file, 1, (size_t)len, f) != (size_t)len) { fclose(f); free(file); return 0; }
    fclose(f);

    uint32_t pe = u32(file + 0x3c);
    if (u32(file + pe) != 0x4550 || u16(file + pe + 4) != 0x8664) { fprintf(stderr, "sfx plugin: not an x64 PE\n"); free(file); return 0; }
    uint16_t nsec = u16(file + pe + 6), optsz = u16(file + pe + 20);
    const uint8_t *opt = file + pe + 24;
    uint64_t imagebase = u64(opt + 24);
    image_size = u32(opt + 56);
    uint32_t hdrsize = u32(opt + 60), entry = u32(opt + 16);
    const uint8_t *dirs = opt + 112;
    image = mmap(NULL, image_size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (image == MAP_FAILED) { image = NULL; free(file); return 0; }
    memcpy(image, file, hdrsize);
    const uint8_t *sec = opt + optsz;
    for (int i = 0; i < nsec; i++, sec += 40) {
        uint32_t vsize = u32(sec + 8), va = u32(sec + 12), rawsize = u32(sec + 16), raw = u32(sec + 20);
        uint32_t n = rawsize < vsize || vsize == 0 ? rawsize : vsize;
        if (raw && n) memcpy(image + va, file + raw, n);
    }
    dirs = image + (dirs - file);          /* the headers were copied: read the directories there */
    free(file);

    if (trace) fprintf(stderr, "sfx: mapped %zu bytes at %p (base %llx)\n", image_size, (void *)image, (unsigned long long)imagebase);
    /* relocations */
    int64_t delta = (int64_t)((uint64_t)image - imagebase);
    uint32_t rel = u32(dirs + 5 * 8), relsize = u32(dirs + 5 * 8 + 4);
    for (uint32_t o = 0; o < relsize;) {
        uint32_t page = u32(image + rel + o), bsize = u32(image + rel + o + 4);
        if (!bsize) break;
        for (uint32_t k = 8; k < bsize; k += 2) {
            uint16_t e = u16(image + rel + o + k);
            uint8_t *at = image + page + (e & 0xfff);
            if ((e >> 12) == 10) { uint64_t v = u64(at) + (uint64_t)delta; memcpy(at, &v, 8); }
            else if ((e >> 12) == 3) { uint32_t v = u32(at) + (uint32_t)delta; memcpy(at, &v, 4); }
        }
        o += bsize;
    }

    if (trace) fprintf(stderr, "sfx: relocated\n");
    /* imports: each IAT slot points at its own stub address */
    stubs = mmap(NULL, MAX_IMPORTS * 16, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    uint32_t imp = u32(dirs + 1 * 8);
    for (const uint8_t *d = image + imp; u32(d + 12); d += 20) {
        uint32_t oft = u32(d), ft = u32(d + 16);
        for (uint32_t t = 0;; t += 8) {
            uint64_t e = u64(image + (oft ? oft : ft) + t);
            if (!e) break;
            if (nimports >= MAX_IMPORTS) { fprintf(stderr, "sfx plugin: too many imports\n"); return 0; }
            int idx = nimports++;
            const char *name = (e >> 63) ? "(ordinal)" : (const char *)(image + (uint32_t)e + 2);
            snprintf(import_name[idx], sizeof import_name[idx], "%s", name);
            import_id[idx] = I_UNKNOWN;
            for (size_t k = 0; k < sizeof known / sizeof known[0]; k++)
                if (!strcmp(known[k].name, name)) { import_id[idx] = known[k].id; break; }
            uint64_t slot = (uint64_t)stubs + (uint64_t)idx * 16;
            memcpy(image + ft + t, &slot, 8);
        }
    }

    if (trace) fprintf(stderr, "sfx: %d imports\n", nimports);
    /* exports */
    uint32_t exp = u32(dirs);
    uint32_t nnames = u32(image + exp + 24), funcs = u32(image + exp + 28), names = u32(image + exp + 32), ords = u32(image + exp + 36);
    for (uint32_t i = 0; i < nnames; i++) {
        const char *n = (const char *)(image + u32(image + names + 4 * i));
        uint16_t ord = u16(image + ords + 2 * i);
        for (int k = 0; k < E_COUNT; k++)
            if (!strcmp(n, export_names[k])) exports[k] = (uint64_t)image + u32(image + funcs + 4 * ord);
    }
    for (int k = 0; k < E_COUNT; k++)
        if (!exports[k]) { fprintf(stderr, "sfx plugin: no export %s\n", export_names[k]); return 0; }

    /* stack and a minimal TEB (gs:[0x08] stack base, gs:[0x10] stack limit, gs:[0x30] self) */
    size_t stacksz = 4 << 20;
    uint8_t *stack = mmap(NULL, stacksz, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    uint8_t *teb = calloc(1, 0x2000);
    uint64_t top = (uint64_t)stack + stacksz - 0x100, low = (uint64_t)stack;
    memcpy(teb + 0x08, &top, 8);
    memcpy(teb + 0x10, &low, 8);
    uint64_t self = (uint64_t)teb;
    memcpy(teb + 0x30, &self, 8);
    memset(&cpu, 0, sizeof cpu);
    cpu.r[RSP] = top;
    cpu.gsbase = (uint64_t)teb;
    cpu.mxcsr = 0x1f80;
    cpu.host = host_call;

    if (trace) fprintf(stderr, "sfx: exports ok, running DllMain\n");
    /* DllMain(hinst, DLL_PROCESS_ATTACH, NULL): the C runtime and static constructors */
    uint64_t args[3] = {(uint64_t)image, 1, 0};
    uint64_t ok = x64_call(&cpu, (uint64_t)image + entry, 3, args, NULL);
    if (cpu.error || !(uint32_t)ok) { fprintf(stderr, "sfx plugin: DllMain failed\n"); return 0; }
    fprintf(stdout, "sfx plugin: loaded (%zu KB image, %d imports, %llu steps to start)\n", image_size >> 10, nimports, (unsigned long long)cpu.steps);
    loaded = 1;
    return 1;
}

static uint64_t run(int e, int nargs, const uint64_t *args, const XmmReg *fargs) {
    if (!load()) return 0;
    cpu.error = 0;
    uint64_t t0 = now_ns(), s0 = cpu.steps;
    uint64_t r = x64_call(&cpu, exports[e], nargs, args, fargs);
    prof_ns[e] += now_ns() - t0; prof_steps += cpu.steps - s0;
    if (cpu.error) {
        fprintf(stderr, "sfx plugin: %s stopped early\n", export_names[e]);
        cpu.error = 0;
    }
    return r;
}

static float fallback_camera[13];

EXPORT void SFX_InitSystem(void *cb) { uint64_t a[1] = {(uint64_t)cb}; run(E_InitSystem, 1, a, NULL); }
EXPORT void SFX_StartPlungeCamera(void *btlseq, int len, int camOffset, int projOffset) {
    uint64_t a[4] = {(uint64_t)btlseq, (uint64_t)(int64_t)len, (uint64_t)(int64_t)camOffset, (uint64_t)(int64_t)projOffset};
    run(E_StartPlungeCamera, 4, a, NULL);
}
EXPORT void SFX_SkipCameraAnimation(int skip) { uint64_t a[1] = {(uint64_t)(int64_t)skip}; run(E_SkipCameraAnimation, 1, a, NULL); }
EXPORT void SFX_InitBattle(void *param) { uint64_t a[1] = {(uint64_t)param}; run(E_InitBattle, 1, a, NULL); }
EXPORT int SFX_Update(int *frameIndex) { uint64_t a[1] = {(uint64_t)frameIndex}; return (int)(run(E_Update, 1, a, NULL) & 0xff); }
EXPORT void SFX_LateUpdate(void) { run(E_LateUpdate, 0, NULL, NULL); }
EXPORT void *SFX_UpdateCamera(int isDebug) {
    uint64_t a[1] = {(uint64_t)(int64_t)isDebug};
    if (!load()) {
        memset(fallback_camera, 0, sizeof fallback_camera);
        fallback_camera[0] = 4096; fallback_camera[4] = 3500; fallback_camera[5] = -2100; fallback_camera[7] = 2100; fallback_camera[8] = 3500;
        fallback_camera[10] = 1000; fallback_camera[11] = 8000; fallback_camera[12] = 350;
        return fallback_camera;
    }
    return (void *)run(E_UpdateCamera, 1, a, NULL);
}
EXPORT void SFX_MoveFreeCamera(int type, int x, int y) {
    uint64_t a[3] = {(uint64_t)(int64_t)type, (uint64_t)(int64_t)x, (uint64_t)(int64_t)y};
    run(E_MoveFreeCamera, 3, a, NULL);
}
EXPORT int SFX_SendFloatData(int type, int btl_id, float a0, float a1, float a2) {
    XmmReg f[4];
    memset(f, 0, sizeof f);
    f[2].f[0] = a0; f[3].f[0] = a1;
    uint64_t f2 = 0, f3 = 0, f4 = 0;
    memcpy(&f2, &a0, 4); memcpy(&f3, &a1, 4); memcpy(&f4, &a2, 4);
    uint64_t a[5] = {(uint64_t)(int64_t)type, (uint64_t)(int64_t)btl_id, f2, f3, f4};
    return (int)run(E_SendFloatData, 5, a, f);
}
EXPORT int SFX_SendIntData(int type, int a0, int a1, int a2) {
    uint64_t a[4] = {(uint64_t)(int64_t)type, (uint64_t)(int64_t)a0, (uint64_t)(int64_t)a1, (uint64_t)(int64_t)a2};
    return (int)run(E_SendIntData, 4, a, NULL);
}
EXPORT void SFX_Play(int effnum, void *bin, int size, void *req) {
    uint64_t a[4] = {(uint64_t)(int64_t)effnum, (uint64_t)bin, (uint64_t)(int64_t)size, (uint64_t)req};
    run(E_Play, 4, a, NULL);
}
EXPORT int SFX_BeginRender(void) { return (int)(run(E_BeginRender, 0, NULL, NULL) & 0xff); }
EXPORT void *SFX_GetPrim(int *otz) { uint64_t a[1] = {(uint64_t)otz}; return (void *)run(E_GetPrim, 1, a, NULL); }
