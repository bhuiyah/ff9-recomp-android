// framegen.cpp - libff9fg.so: frame generation. In-between frames shown between the ones the game
// draws, made by Lossless Scaling's shaders from the player's own Lossless.dll (FF8Recomp #8). The
// game itself runs as ever at its own rate: nothing is sped up, only more is shown.
//
// The engine is lsfg-vk's (dl/lsfg-vk-android, MIT). It runs on a Vulkan device of its own; images
// go between it and an OpenGL ES context as AHardwareBuffers (EGLImages here). Making frames takes
// longer than a 30 fps field can spare, so a thread of its own does it and owns the window: the game
// draws to a pbuffer, each frame is copied to a texture (shared context) and the game goes on. The
// thread copies it to one of two input buffers; the engine makes up to three frames
// between it and the one before (quarters of the way); those that fit 60 a second are presented
// before the frame itself, each with the time it is due (EGL_ANDROID_presentation_time).
//
// From FF7Recomp's src/ffnx_port/framegen.cpp (a Vulkan renderer there). Loaded by the engine
// (Gfx/FrameGen.cs) only when the launcher turned it on. Lossless.dll is never part of FF9Recomp:
// the launcher copies the player's to <files>/framegen, and its shaders (SPIR-V, RCDATA 304-351
// half and 353-400 full precision) are read from there.
#define EGL_EGLEXT_PROTOTYPES
#define GL_GLEXT_PROTOTYPES
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES2/gl2ext.h>
#include <android/hardware_buffer.h>
#include <android/log.h>
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <dlfcn.h>
#include <map>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>
#define VK_USE_PLATFORM_ANDROID_KHR
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "lsfg_3_1p.hpp"

namespace {
constexpr uint32_t k_between = 3;      // made per pair of frames at most: 15 to 60
constexpr double k_display = 60.0;
constexpr double k_lead = 0.015;

bool g_failed, g_engine, g_half = true, g_trace;
float g_flow = 0.6f;
std::vector<uint8_t> g_dll;
std::unordered_map<uint32_t, std::pair<uint32_t, uint32_t>> g_rcdata;   // id -> file offset, size

void say(const char *fmt, ...) {       // stderr is ff9.log
    va_list v;
    va_start(v, fmt);
    fputs("[framegen] ", stderr);
    vfprintf(stderr, fmt, v);
    fputc('\n', stderr);
    fflush(stderr);
    va_end(v);
}

// ---------------------------------------------------------------- Lossless.dll
uint32_t u32(size_t at) { uint32_t v = 0; if (at + 4 <= g_dll.size()) memcpy(&v, &g_dll[at], 4); return v; }
uint16_t u16(size_t at) { uint16_t v = 0; if (at + 2 <= g_dll.size()) memcpy(&v, &g_dll[at], 2); return v; }

// RVA -> file offset
size_t rva(uint32_t r) {
    const size_t pe = u32(0x3C), n = u16(pe + 6), sec = pe + 24 + u16(pe + 20);
    for (size_t i = 0; i < n; i++) {
        const size_t s = sec + 40 * i;
        const uint32_t va = u32(s + 12), raw = u32(s + 16);
        if (r >= va && r < va + raw) return u32(s + 20) + (r - va);
    }
    return 0;
}

bool read_dll(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    uint8_t buf[65536];
    for (size_t n; (n = fread(buf, 1, sizeof buf, f)) > 0 && g_dll.size() < (64u << 20);) g_dll.insert(g_dll.end(), buf, buf + n);
    fclose(f);
    const size_t pe = u32(0x3C);
    if (u16(0) != 0x5A4D || u32(pe) != 0x4550) return false;
    const size_t dir = pe + 24 + (u16(pe + 24) == 0x20B ? 112 : 96) + 8 * 2;   // resources
    const size_t root = rva(u32(dir));
    if (!root) return false;
    auto entries = [&](size_t at, auto fn) {
        const size_t n = std::min<size_t>(u16(at + 12) + u16(at + 14), 4096);
        for (size_t i = 0; i < n; i++) fn(u32(at + 16 + 8 * i), u32(at + 20 + 8 * i));
    };
    entries(root, [&](uint32_t type, uint32_t off) {
        if (type != 10 || !(off >> 31)) return;                                // RT_RCDATA
        entries(root + (off & 0x7FFFFFFF), [&](uint32_t id, uint32_t off2) {
            if (id >> 31 || !(off2 >> 31)) return;
            entries(root + (off2 & 0x7FFFFFFF), [&](uint32_t, uint32_t leaf) {
                if (leaf >> 31) return;
                const size_t at = rva(u32(root + leaf));
                const uint32_t size = u32(root + leaf + 4);
                if (at && at + size <= g_dll.size()) g_rcdata[id] = { (uint32_t)at, size };
            });
        });
    });
    return true;
}

// lsfg-vk's names for the performance variant's shaders -> the shader's number in the dll
uint32_t shader_id(const std::string &n) {
    static const std::map<std::string, uint32_t> t = {
        { "p_mipmaps", 255 }, { "p_alpha[0]", 290 }, { "p_alpha[1]", 291 }, { "p_alpha[2]", 292 }, { "p_alpha[3]", 293 },
        { "p_beta[0]", 298 }, { "p_beta[1]", 299 }, { "p_beta[2]", 300 }, { "p_beta[3]", 301 }, { "p_beta[4]", 302 },
        { "p_gamma[0]", 280 }, { "p_gamma[1]", 282 }, { "p_gamma[2]", 283 }, { "p_gamma[3]", 284 }, { "p_gamma[4]", 285 },
        { "p_delta[0]", 280 }, { "p_delta[1]", 286 }, { "p_delta[2]", 287 }, { "p_delta[3]", 288 }, { "p_delta[4]", 289 },
        { "p_delta[5]", 281 }, { "p_delta[6]", 294 }, { "p_delta[7]", 295 }, { "p_delta[8]", 296 }, { "p_delta[9]", 297 },
        { "p_generate", 256 } };
    const auto it = t.find(n);
    return it == t.end() ? 0 : it->second;
}

// The dll's SPIR-V numbers its bindings by HLSL register; the engine's layouts count uniform
// buffers, samplers, sampled images, then storage images (as DXVK's translation has them).
bool renumber(std::vector<uint8_t> &code) {
    const size_t n = code.size() / 4;
    std::vector<uint32_t> w(n);
    memcpy(w.data(), code.data(), n * 4);
    if (n < 5 || w[0] != 0x07230203) return false;
    std::unordered_map<uint32_t, int> kind;                     // type -> 0 buffer, 1 sampler, 2 sampled, 3 storage
    std::unordered_map<uint32_t, uint32_t> pointee, var;
    struct Site { size_t at; uint32_t id, binding; int kind; };
    std::vector<Site> sites;
    for (size_t i = 5; i < n;) {
        const uint32_t len = w[i] >> 16, op = w[i] & 0xFFFF;
        if (!len || i + len > n) return false;
        if (op == 54) break;                                    // OpFunction
        if (op == 26) kind[w[i + 1]] = 1;
        else if (op == 25 && len >= 8) kind[w[i + 1]] = w[i + 7] == 2 ? 3 : 2;
        else if (op == 27) kind[w[i + 1]] = 2;
        else if (op == 30) kind[w[i + 1]] = 0;
        else if (op == 32 && len == 4) pointee[w[i + 1]] = w[i + 3];
        else if (op == 59 && len >= 4) var[w[i + 2]] = w[i + 1];
        else if (op == 71 && len == 4 && w[i + 2] == 33) sites.push_back({ i + 3, w[i + 1], w[i + 3], -1 });
        i += len;
    }
    for (Site &s : sites) {
        const auto v = var.find(s.id);
        const auto p = v == var.end() ? pointee.end() : pointee.find(v->second);
        const auto k = p == pointee.end() ? kind.end() : kind.find(p->second);
        if (k == kind.end()) return false;
        s.kind = k->second;
    }
    std::stable_sort(sites.begin(), sites.end(), [](const Site &a, const Site &b) {
        return a.kind != b.kind ? a.kind < b.kind : a.binding < b.binding; });
    for (size_t k = 0; k < sites.size(); k++) w[sites[k].at] = (uint32_t)k;
    memcpy(code.data(), w.data(), n * 4);
    return true;
}

std::vector<uint8_t> shader(const std::string &name) {
    const uint32_t id = shader_id(name) + (g_half ? 49 : 98);
    const auto it = g_rcdata.find(id);
    if (it == g_rcdata.end()) return {};
    std::vector<uint8_t> code(g_dll.begin() + it->second.first, g_dll.begin() + it->second.first + it->second.second);
    if (!renumber(code)) return {};
    return code;
}

// ---------------------------------------------------------------- the game's side
struct Shared { AHardwareBuffer *ahb = nullptr; EGLImageKHR image = EGL_NO_IMAGE_KHR; GLuint tex = 0, fbo = 0; };
Shared g_in[2], g_out[k_between];

// the game's frames on their way to the thread
struct Frame { GLuint tex = 0, fbo = 0, read = 0; GLsync done = nullptr; int64_t at = 0; bool movie = false; int target = 0; };
constexpr uint32_t k_ring = 3;
Frame g_ring[k_ring];                    // fbo: the game's context; read: the thread's
std::mutex g_mutex;
std::condition_variable g_cv;
uint32_t g_given, g_taken;               // frames the game gave, and the thread has finished with
bool g_stop;
std::thread g_thread;
std::atomic<int> g_shown;
EGLContext g_thread_ctx = EGL_NO_CONTEXT;
EGLSurface g_pbuffer = EGL_NO_SURFACE;
EGLDisplay g_dpy = EGL_NO_DISPLAY;
EGLSurface g_surface = EGL_NO_SURFACE;
int g_w, g_h;
int32_t g_context = -1;
uint64_t g_frames;              // captured since the context was made
int64_t g_last_ns, g_due_ns;
int g_cap = k_between + 1, g_over;   // frames shown for one drawn, at most
int64_t g_cap_since;
int g_target = -1;
double g_delay = 1.0 / 30, g_work = 0.016, g_dts[5];

PFNEGLCREATEIMAGEKHRPROC p_create_image;
PFNEGLDESTROYIMAGEKHRPROC p_destroy_image;
PFNEGLGETNATIVECLIENTBUFFERANDROIDPROC p_client_buffer;
PFNGLEGLIMAGETARGETTEXTURE2DOESPROC p_image_texture;
PFNEGLPRESENTATIONTIMEANDROIDPROC p_time;
PFNEGLGETNEXTFRAMEIDANDROIDPROC p_frame_id;
PFNEGLGETFRAMETIMESTAMPSANDROIDPROC p_stamps;
PFNEGLGETCOMPOSITORTIMINGANDROIDPROC p_timing;

int64_t now_ns() { timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec * 1000000000ll + t.tv_nsec; }

int64_t g_refresh_ns, g_vsync_ns;   // how long a refresh lasts, and when one was
int64_t g_last_vsync;               // the refresh the last frame was for
EGLuint64KHR g_ids[8];              // the last frames presented (get_frame_timestamps)
uint32_t g_n_ids;

bool load_procs() {
#define P(v, n) v = (decltype(v))eglGetProcAddress(n)
    P(p_create_image, "eglCreateImageKHR"); P(p_destroy_image, "eglDestroyImageKHR");
    P(p_client_buffer, "eglGetNativeClientBufferANDROID"); P(p_image_texture, "glEGLImageTargetTexture2DOES");
    P(p_time, "eglPresentationTimeANDROID"); P(p_frame_id, "eglGetNextFrameIdANDROID");
    P(p_stamps, "eglGetFrameTimestampsANDROID"); P(p_timing, "eglGetCompositorTimingANDROID");
#undef P
    const char *e = eglQueryString(g_dpy, EGL_EXTENSIONS);
    if (!e || !strstr(e, "EGL_ANDROID_presentation_time")) p_time = nullptr;
    if (!e || !strstr(e, "EGL_ANDROID_get_frame_timestamps")) p_stamps = nullptr;
    return p_create_image && p_destroy_image && p_client_buffer && p_image_texture;
}

void make_shared(Shared &s, int w, int h) {
    AHardwareBuffer_Desc d = {};
    d.width = w; d.height = h; d.layers = 1;
    d.format = AHARDWAREBUFFER_FORMAT_R8G8B8A8_UNORM;
    d.usage = AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE | AHARDWAREBUFFER_USAGE_GPU_COLOR_OUTPUT;
    if (AHardwareBuffer_allocate(&d, &s.ahb)) throw std::runtime_error("no shared buffer");
    const EGLint at[] = { EGL_IMAGE_PRESERVED_KHR, EGL_TRUE, EGL_NONE };
    s.image = p_create_image(g_dpy, EGL_NO_CONTEXT, EGL_NATIVE_BUFFER_ANDROID, p_client_buffer(s.ahb), at);
    if (s.image == EGL_NO_IMAGE_KHR) throw std::runtime_error("no EGL image");
    glGenTextures(1, &s.tex);
    glBindTexture(GL_TEXTURE_2D, s.tex);
    p_image_texture(GL_TEXTURE_2D, s.image);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glGenFramebuffers(1, &s.fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, s.fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, s.tex, 0);
    if (glCheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE) throw std::runtime_error("the shared buffer can't be drawn to");
}

void drop_shared(Shared &s) {
    if (s.fbo) glDeleteFramebuffers(1, &s.fbo);
    if (s.tex) glDeleteTextures(1, &s.tex);
    if (s.image != EGL_NO_IMAGE_KHR) p_destroy_image(g_dpy, s.image);
    if (s.ahb) AHardwareBuffer_release(s.ahb);
    s = {};
}

void drop() {
    glFinish();
    if (g_context != -1) { try { LSFG_3_1P::waitIdle(); LSFG_3_1P::deleteContext(g_context); } catch (...) { } }
    g_context = -1;
    for (Shared &s : g_in) drop_shared(s);
    for (Shared &s : g_out) drop_shared(s);
    g_frames = 0;
    g_w = g_h = 0;
}

// The GPU, as the engine picks it (vendor and device id), and what it can do.
uint64_t gpu_id() {
    void *lib = dlopen("libvulkan.so", RTLD_NOW);
    if (!lib) throw std::runtime_error("no Vulkan");
    const auto get = (PFN_vkGetInstanceProcAddr)dlsym(lib, "vkGetInstanceProcAddr");
    const auto create = (PFN_vkCreateInstance)get(nullptr, "vkCreateInstance");
    VkApplicationInfo ai = { VK_STRUCTURE_TYPE_APPLICATION_INFO };
    ai.apiVersion = VK_API_VERSION_1_3;
    VkInstanceCreateInfo ci = { VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    ci.pApplicationInfo = &ai;
    VkInstance in;
    if (create(&ci, nullptr, &in) != VK_SUCCESS) throw std::runtime_error("no Vulkan instance");
    const auto list = (PFN_vkEnumeratePhysicalDevices)get(in, "vkEnumeratePhysicalDevices");
    const auto props = (PFN_vkGetPhysicalDeviceProperties)get(in, "vkGetPhysicalDeviceProperties");
    const auto feats = (PFN_vkGetPhysicalDeviceFeatures2)get(in, "vkGetPhysicalDeviceFeatures2");
    const auto bye = (PFN_vkDestroyInstance)get(in, "vkDestroyInstance");
    VkPhysicalDevice pd = VK_NULL_HANDLE;
    uint32_t n = 1;
    list(in, &n, &pd);
    VkPhysicalDeviceProperties p = {};
    VkPhysicalDeviceVulkan12Features f12 = { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES };
    VkPhysicalDeviceFeatures2 f2 = { VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, &f12 };
    if (pd) { props(pd, &p); if (p.apiVersion >= VK_API_VERSION_1_3) feats(pd, &f2); }
    bye(in, nullptr);
    if (!pd || p.apiVersion < VK_API_VERSION_1_3) throw std::runtime_error("needs a Vulkan 1.3 driver");
    g_half = f12.shaderFloat16 && f12.vulkanMemoryModel && g_rcdata.count(304);
    return (uint64_t)p.vendorID << 32 | p.deviceID;
}

void setup(int w, int h) {
    if (!load_procs()) throw std::runtime_error("this OpenGL ES can't share its images");
    g_w = w; g_h = h;
    for (Shared &s : g_in) make_shared(s, w, h);
    for (Shared &s : g_out) make_shared(s, w, h);
    if (!g_engine) {
        LSFG_3_1P::initialize(gpu_id(), false, g_flow, k_between, shader);
        g_engine = true;
    }
    std::vector<AHardwareBuffer *> out;
    for (Shared &s : g_out) out.push_back(s.ahb);
    g_context = LSFG_3_1P::createContextFromAHB(g_in[0].ahb, g_in[1].ahb, out, { (uint32_t)w, (uint32_t)h }, VK_FORMAT_R8G8B8A8_UNORM);
    say("on: %dx%d, %s precision, flow scale %.2f%s%s", w, h, g_half ? "half" : "full", g_flow,
        p_time ? "" : ", untimed", p_stamps ? "" : ", no display times");
}

void blit(GLuint from, GLuint to) {
    glBindFramebuffer(GL_READ_FRAMEBUFFER, from);
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, to);
    glBlitFramebuffer(0, 0, g_w, g_h, 0, 0, g_w, g_h, GL_COLOR_BUFFER_BIT, GL_NEAREST);
}

// Room for the frames waiting to be shown: with the usual three buffers the swap waits for one to be
// free, and the game falls behind. The buffer count has no NDK call; this is how the EGL drivers set
// it (system/window.h: NATIVE_WINDOW_SET_BUFFER_COUNT).
struct Window {
    int magic, version;
    void *reserved[4], *inc, *dec;
    uint32_t flags;
    int min_interval, max_interval;
    float xdpi, ydpi;
    intptr_t oem[4];
    void *set_interval, *old_dequeue, *old_lock, *old_queue;
    int (*query)(const Window *, int, int *);
    int (*perform)(Window *, int, ...);
};
void more_buffers(void *window) {
    Window *w = (Window *)window;
    if (!w || w->magic != (('_' << 24) | ('w' << 16) | ('n' << 8) | 'd') || !w->perform) { say("window buffers as usual"); return; }
    const int e = w->perform(w, 4, 8);
    say("window buffers: %s (%d)", e ? "as usual" : "8", e);
}

// a window surface is asked to keep its frames' times; a new one starts over
void new_surface(EGLSurface s, void *window) {
    g_surface = s;
    if (!getenv("FF9_TEST_FG_BUFFERS")) more_buffers(window);
    g_refresh_ns = g_vsync_ns = g_last_vsync = 0;
    g_n_ids = 0;
    g_frames = 0;
    if (p_stamps && !eglSurfaceAttrib(g_dpy, s, EGL_TIMESTAMPS_ANDROID, EGL_TRUE)) { p_stamps = nullptr; eglGetError(); }
}

void refresh() {
    if (!p_stamps) return;
    if (p_timing) {
        const EGLint name = EGL_COMPOSITE_INTERVAL_ANDROID;
        EGLnsecsANDROID v = 0;
        if (p_timing(g_dpy, g_surface, 1, &name, &v) && v > 0) g_refresh_ns = v; else eglGetError();
    }
    for (uint32_t k = 0; k < std::min(g_n_ids, 8u); k++) {
        const EGLint name = EGL_DISPLAY_PRESENT_TIME_ANDROID;
        EGLnsecsANDROID v = 0;
        if (!p_stamps(g_dpy, g_surface, g_ids[(g_n_ids - 1 - k) % 8], 1, &name, &v)) { eglGetError(); continue; }
        if (v > 0) { g_vsync_ns = std::max<int64_t>(g_vsync_ns, v); return; }
    }
}

// The middle of the refresh before the one at or after t, and never the same one twice.
int64_t aligned(int64_t t) {
    if (g_refresh_ns <= 0 || g_vsync_ns <= 0) return t;
    const int64_t r = g_refresh_ns;
    int64_t k = (t - g_vsync_ns + r - 1) / r;
    const int64_t last = g_last_vsync ? (g_last_vsync - g_vsync_ns + r / 2) / r : k - 1;
    if (k <= last && last - k < 4) k = last + 1;
    g_last_vsync = g_vsync_ns + k * r;
    return g_last_vsync - r / 2;
}

void show(int64_t due) {
    if (p_time && due) p_time(g_dpy, g_surface, due);
    if (p_stamps && p_frame_id) {
        EGLuint64KHR id = 0;
        if (p_frame_id(g_dpy, g_surface, &id)) g_ids[g_n_ids++ % 8] = id; else eglGetError();
    }
    eglSwapBuffers(g_dpy, g_surface);
}

// the GL state touched in the game's context, put back for the engine's renderer
struct Keep {
    GLint read, draw, tex;
    GLboolean scissor;
    Keep() {
        glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &read);
        glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &draw);
        glGetIntegerv(GL_TEXTURE_BINDING_2D, &tex);
        scissor = glIsEnabled(GL_SCISSOR_TEST);
        glDisable(GL_SCISSOR_TEST);
    }
    ~Keep() {
        glBindFramebuffer(GL_READ_FRAMEBUFFER, read);
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, draw);
        glBindTexture(GL_TEXTURE_2D, tex);
        if (scissor) glEnable(GL_SCISSOR_TEST);
    }
};

// On the thread: shows one of the game's frames (target: the game's frame rate, 0 unknown) and the
// ones made before it. Returns how many.
int frame(const Frame &f, int w, int h) {
    const int64_t now = f.at;
    const int target = f.target;
    const GLenum drawn_by = glClientWaitSync(f.done, 0, 1000000000ull);
    if (g_failed || f.movie || drawn_by == GL_TIMEOUT_EXPIRED) {
        g_frames = 0; g_last_ns = 0; g_due_ns = 0;
        g_w = w; g_h = h;
        g_last_vsync = 0;
        blit(f.read, 0);
        show(0);
        return 1;
    }
    int shown = 0;
    try {
        if (g_context == -1) setup(w, h);
        const int64_t start = now_ns();
        const double dt = g_last_ns ? (now - g_last_ns) * 1e-9 : g_delay;
        g_last_ns = now;
        // When this frame is shown. What is made lies between the last frame and this one, so this
        // one has to wait while that is made and shown: that wait is felt as the controls' delay,
        // and is kept as short as it can be. (1 made: one refresh and the work; 60 a second
        // already: none.)
        g_dts[g_frames % 5] = dt;
        double s[5];
        std::copy(g_dts, g_dts + 5, s);
        std::sort(s, s + 5);
        const double usual = std::clamp(s[2], 1 / k_display, (k_between + 1) / k_display);
        // No more than fit the game's own rate: a slow moment (a load) would have three made where one
        // belongs, and making them would keep the game slow (a field at 15 instead of 30).
        if (target != g_target) { g_target = target; g_cap = k_between + 1; g_over = 0; }
        const int fit = target > 0 ? std::clamp((int)(k_display / target + 0.5), 1, g_cap) : g_cap;
        const int n_usual = std::min((int)(usual * k_display + 0.5), fit);
        const double wait = n_usual > 1 ? g_work + (n_usual - 1) / k_display : 0;
        if (dt > 0.004 && dt < 0.25) {
            const double longest = std::min(dt, (k_between + 1) / k_display);
            g_delay = std::max(longest, g_delay - (g_delay - longest) * 0.02);
        }
        const int64_t drawn_due = now + (int64_t)(wait * 1e9);
        // Frames still waiting to be shown beyond that: the display is behind. None are added until
        // it has caught up. A frame that took much longer than the last ones (a load, a menu that
        // waits) has nothing between it and the one before.
        const bool behind = g_refresh_ns > 0 && g_last_vsync - now > (int64_t)(wait * 1e9) + g_refresh_ns * 5 / 2;
        const bool still = g_frames == 0 || dt > 0.25 || behind || g_due_ns == 0;
        const double gap = still ? 0 : (drawn_due - g_due_ns) * 1e-9;
        int steps = std::clamp((int)(gap * k_display + 0.5), 1, fit);
        // One refresh each: after a hiccup the frames would stay that much later for good (felt as the
        // controls' delay). One is left out until they are back where they should be.
        if (steps > 1 && g_refresh_ns > 0 && g_last_vsync + steps * g_refresh_ns > drawn_due + g_refresh_ns * 3 / 2) steps--;

        // 1. the frame, to the input buffer the engine reads next; the engine is another device and
        // can only be told through us
        Shared &in = g_in[g_frames % 2];
        blit(f.read, in.fbo);
        GLsync done = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        const GLenum waited = glClientWaitSync(done, GL_SYNC_FLUSH_COMMANDS_BIT, 1000000000ull);
        glDeleteSync(done);
        if (waited == GL_TIMEOUT_EXPIRED || waited == GL_WAIT_FAILED) throw std::runtime_error("the GPU did not finish the frame");

        // 2. what lies between (the engine keeps its own history, so it runs for every frame)
        uint32_t mask = 0;
        for (int k = 1; !still && k < steps; k++) {
            const int q = (k * (int)(k_between + 1) + steps / 2) / steps;   // in quarters
            mask |= 1u << (q - 1);
        }
        static double s_render, s_gen; static int s_n;
        const int64_t t_r = now_ns();   // (start: the thread took the frame)
        LSFG_3_1P::setPasses(mask);
        LSFG_3_1P::presentContext(g_context, -1, {});
        if (mask) LSFG_3_1P::waitIdle();
        g_frames++;
        if (mask) { s_render += (t_r - start) * 1e-6; s_gen += (now_ns() - t_r) * 1e-6; s_n++; }
        // and the time the compositor wants a frame before the refresh it is for: with 4 ms the first
        // made frame came too late for stretches, and was shown over by the next (45 a second seen)
        const double work = (now_ns() - now) * 1e-9 + k_lead;
        // Too slow for the time there is (a weak GPU): fewer are made, down to none; after a while
        // one more is tried.
        if (dt > 0.004 && dt < 0.25) {
            const double part = (now_ns() - start) * 1e-9 / dt;
            // or the game has fallen behind its rate while they are made
            g_over = part > 0.85 || (mask && target > 0 && s[2] > 1.2 / target) ? g_over + 1 : 0;
            if (g_over >= 20 && fit > 1) {
                g_cap = fit - 1; g_over = 0; g_cap_since = now;
                say("too slow here: %d made between at most", g_cap - 1);
            } else if (g_cap < (int)k_between + 1 && now - g_cap_since > 30000000000ll && part < 0.5) {
                g_cap++; g_cap_since = now;
            }
        }
        if (mask) g_work += (std::min(work, 0.056) - g_work) * 0.1;

        // 3. shown in order, each when it is due. A due time is half a refresh before the refresh it
        // is for: the compositor shows the newest frame that is due and drops the others, so two
        // must never fall on one refresh.
        const int count = __builtin_popcount(mask) + 1;
        const int64_t each = (drawn_due - g_due_ns) / count, first = g_due_ns;
        refresh();
        auto when = [&](int k) { return aligned(k == count - 1 ? drawn_due : first + each * (k + 1)); };
        for (uint32_t q = 0; q < k_between; q++) {
            if (!(mask >> q & 1)) continue;
            blit(g_out[q].fbo, 0);
            show(when(shown));
            shown++;
        }
        blit(in.fbo, 0);
        // where nothing is made (menus at 60) a hiccup's frames would wait for good too: one is dropped
        if (shown || g_refresh_ns <= 0 || g_last_vsync - drawn_due < g_refresh_ns * 5 / 2) {
            show(when(shown));
            shown++;
        }
        g_due_ns = drawn_due;
        static int traced;
        if (g_trace && traced++ % 600 < 60)
            say("%.1f steps %d shown %d behind %d work %.1f took %.1f wait %.1f ahead %.1f gl %x", dt * 1e3, steps, shown, behind, work * 1e3,
                (now_ns() - now) * 1e-6, wait * 1e3, (g_last_vsync - now) * 1e-6, glGetError());

        static int64_t t0; static uint32_t drawn, made;
        drawn++; made += std::max(shown - 1, 0);
        if (!t0) t0 = now;
        if (now - t0 > 10000000000ll) {
            const double sec = (now - t0) * 1e-9;
            say("%.1f drawn + %.1f made a second, shown %.0f ms after, refresh %.2f ms; waiting for the drawing %.1f ms, making %.1f ms",
                drawn / sec, made / sec, wait * 1e3, g_refresh_ns * 1e-6, s_n ? s_render / s_n : 0, s_n ? s_gen / s_n : 0);
            s_render = s_gen = 0; s_n = 0;
            t0 = now; drawn = made = 0;
        }
        return shown;
    } catch (const std::exception &e) {
        say("off: %s", e.what());
        g_failed = true;
        g_w = w; g_h = h;
        g_last_vsync = 0;
        blit(f.read, 0);
        show(0);
        return shown + 1;
    }
}

void thread_main(EGLConfig, EGLSurface surface, void *window, int w, int h) {
    if (!eglMakeCurrent(g_dpy, surface, surface, g_thread_ctx)) { say("the thread has no context: %x", eglGetError()); g_failed = true; }
    load_procs();
    new_surface(surface, window);
    eglSwapInterval(g_dpy, 1);
    for (Frame &f : g_ring) {
        glGenFramebuffers(1, &f.read);
        glBindFramebuffer(GL_READ_FRAMEBUFFER, f.read);
        glFramebufferTexture2D(GL_READ_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, f.tex, 0);
    }
    std::unique_lock<std::mutex> l(g_mutex);
    while (true) {
        g_cv.wait(l, [] { return g_stop || g_taken != g_given; });
        if (g_stop) break;
        Frame &f = g_ring[g_taken % k_ring];
        l.unlock();
        g_shown += frame(f, w, h);
        glDeleteSync(f.done);
        f.done = nullptr;
        l.lock();
        g_taken++;
        g_cv.notify_all();
    }
    l.unlock();
    drop();
    for (Frame &f : g_ring) { glDeleteFramebuffers(1, &f.read); f.read = 0; if (f.done) { glDeleteSync(f.done); f.done = nullptr; } }
    eglMakeCurrent(g_dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT);
    eglReleaseThread();
}

// On the game's thread, its context current: the window goes away.
void window_gone() {
    if (!g_thread.joinable()) return;
    { std::lock_guard<std::mutex> l(g_mutex); g_stop = true; }
    g_cv.notify_all();
    g_thread.join();
    for (Frame &f : g_ring) { glDeleteFramebuffers(1, &f.fbo); glDeleteTextures(1, &f.tex); f = {}; }
    g_given = g_taken = 0;
    g_stop = false;
}

// On the game's thread, its context current on the window's surface: moves it to a pbuffer and gives
// the window to the thread. False: nothing changed.
bool window(EGLDisplay dpy, EGLConfig cfg, EGLContext ctx, EGLSurface surface, void *native, int w, int h) {
    g_dpy = dpy;
    if (g_failed) return false;
    if (!load_procs()) { say("off: this OpenGL ES can't share its images"); return false; }
    if (g_thread_ctx == EGL_NO_CONTEXT) {
        const EGLint at[] = { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE };
        g_thread_ctx = eglCreateContext(dpy, cfg, ctx, at);
        if (g_thread_ctx == EGL_NO_CONTEXT) { say("off: no second context (%x)", eglGetError()); return false; }
    }
    const EGLint pb[] = { EGL_WIDTH, w, EGL_HEIGHT, h, EGL_NONE };
    EGLSurface p = eglCreatePbufferSurface(dpy, cfg, pb);
    if (p == EGL_NO_SURFACE) { say("off: no pbuffer (%x)", eglGetError()); return false; }
    if (!eglMakeCurrent(dpy, p, p, ctx)) {
        say("off: pbuffer not usable (%x)", eglGetError());
        eglDestroySurface(dpy, p);
        eglMakeCurrent(dpy, surface, surface, ctx);
        return false;
    }
    if (g_pbuffer != EGL_NO_SURFACE) eglDestroySurface(dpy, g_pbuffer);
    g_pbuffer = p;
    {
        Keep keep;
        for (Frame &f : g_ring) {
            glGenTextures(1, &f.tex);
            glBindTexture(GL_TEXTURE_2D, f.tex);
            glTexStorage2D(GL_TEXTURE_2D, 1, GL_RGBA8, w, h);
            glGenFramebuffers(1, &f.fbo);
            glBindFramebuffer(GL_DRAW_FRAMEBUFFER, f.fbo);
            glFramebufferTexture2D(GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, f.tex, 0);
        }
    }
    glFinish();
    g_thread = std::thread(thread_main, cfg, surface, native, w, h);
    return true;
}

// On the game's thread: its frame is drawn. Waits only when the thread is a whole frame behind.
int give(int w, int h, bool movie, int target) {
    if (!g_thread.joinable()) return -1;
    {
        std::unique_lock<std::mutex> l(g_mutex);
        g_cv.wait(l, [] { return g_given - g_taken < k_ring - 1; });
    }
    Frame &f = g_ring[g_given % k_ring];
    {
        Keep keep;
        glBindFramebuffer(GL_READ_FRAMEBUFFER, 0);
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, f.fbo);
        glBlitFramebuffer(0, 0, w, h, 0, 0, w, h, GL_COLOR_BUFFER_BIT, GL_NEAREST);
    }
    f.done = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
    glFlush();
    f.at = now_ns(); f.movie = movie; f.target = target;
    { std::lock_guard<std::mutex> l(g_mutex); g_given++; }
    g_cv.notify_all();
    return g_shown.exchange(0);
}
}  // namespace

extern "C" {
#define API __attribute__((visibility("default")))
// dir: <files>/framegen. 1 when frames can be made.
API int ff9_fg_init(const char *dir) {
    g_trace = getenv("FF9_TRACE_FRAMEGEN") != nullptr;
    if (const char *f = getenv("FF9_TEST_FG_FLOW")) g_flow = std::clamp((float)atof(f), 0.5f, 1.0f);
    const std::string path = std::string(dir) + "/Lossless.dll";
    if (!read_dll(path.c_str())) { say("off: no readable Lossless.dll"); return 0; }
    bool half = true, full = true;
    for (uint32_t id = 255; id <= 302; id++) { half &= g_rcdata.count(id + 49) != 0; full &= g_rcdata.count(id + 98) != 0; }
    say("Lossless.dll: %zu resources, half precision %s, full %s", g_rcdata.size(), half ? "yes" : "no", full ? "yes" : "no");
    if (!full) { say("off: this Lossless.dll has no Vulkan shaders (update Lossless Scaling in Steam and add it again)"); return 0; }
    return 1;
}
API int ff9_fg_window(void *dpy, void *cfg, void *ctx, void *surface, void *native, int w, int h) { return window(dpy, cfg, ctx, surface, native, w, h); }
API void ff9_fg_window_gone(void) { window_gone(); }
// How many frames were shown since the last call; -1: not on, show it as usual.
API int ff9_fg_present(int w, int h, int movie, int target) { return give(w, h, movie != 0, target); }
}
