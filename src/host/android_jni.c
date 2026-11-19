/* JNI entry points for the APK (com.ff9recomp.GameActivity), built only into libff9.so
 * (FF9_LIBRARY). The game runs on the thread that calls nativeRun. */
#ifdef FF9_LIBRARY
#include <android/log.h>
#include <android/native_window_jni.h>
#include <jni.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

int ff9_main(int argc, char **argv);
extern void *ff9_native_window;
void ff9_pad_set(uint16_t buttons, uint8_t lt, uint8_t rt, int16_t lx, int16_t ly, int16_t rx, int16_t ry);

/* stdout/stderr -> logcat (tag FF9) and a log file next to the game */
static int log_pipe[2];
static void *log_thread(void *arg) {
    char buf[1024];
    FILE *f = (FILE *)arg;
    ssize_t n;
    char line[2048];
    size_t len = 0;
    while ((n = read(log_pipe[0], buf, sizeof buf)) > 0) {
        for (ssize_t i = 0; i < n; i++) {
            if (buf[i] == '\n' || len == sizeof line - 1) {
                line[len] = 0;
                __android_log_write(ANDROID_LOG_INFO, "FF9", line);
                if (f) { fputs(line, f); fputc('\n', f); fflush(f); }
                len = 0;
            } else line[len++] = buf[i];
        }
    }
    return NULL;
}

/* The window can come and go (sleep, the app in the background). Each change bumps a generation;
 * the engine (Gfx/Device.cs, at each Present) drops its EGL surface, makes one on the new window
 * or waits for one, and acknowledges the generation. Losing the window waits for that (Android
 * wants the surface unused once surfaceDestroyed returns), at most a second. */
static pthread_mutex_t win_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t win_cond = PTHREAD_COND_INITIALIZER;
static int win_gen, win_ack;
static int game_running;

__attribute__((visibility("default"))) int ff9_window_gen(void) { return __atomic_load_n(&win_gen, __ATOMIC_ACQUIRE); }
__attribute__((visibility("default"))) void ff9_window_ack(int gen) {
    pthread_mutex_lock(&win_lock);
    win_ack = gen;
    pthread_cond_broadcast(&win_cond);
    pthread_mutex_unlock(&win_lock);
}

JNIEXPORT void JNICALL Java_com_ff9recomp_GameActivity_nativeSetSurface(JNIEnv *env, jclass cls, jobject surface) {
    pthread_mutex_lock(&win_lock);
    void *old = ff9_native_window;
    ff9_native_window = surface ? ANativeWindow_fromSurface(env, surface) : NULL;
    int gen = ++win_gen;
    if (game_running && !surface) {
        struct timespec ts;
        clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_sec += 1;
        while (win_ack < gen && pthread_cond_timedwait(&win_cond, &win_lock, &ts) == 0) { }
    }
    pthread_mutex_unlock(&win_lock);
    /* the EGL surface holds its own reference to the old window */
    if (old) ANativeWindow_release(old);
}

JNIEXPORT jint JNICALL Java_com_ff9recomp_GameActivity_nativeRun(JNIEnv *env, jclass cls, jstring jdir, jstring jlog) {
    const char *dir = (*env)->GetStringUTFChars(env, jdir, NULL);
    const char *logp = (*env)->GetStringUTFChars(env, jlog, NULL);
    FILE *lf = fopen(logp, "w");
    setvbuf(stdout, NULL, _IOLBF, 0);
    pipe(log_pipe);
    dup2(log_pipe[1], 1);
    dup2(log_pipe[1], 2);
    pthread_t t;
    pthread_create(&t, NULL, log_thread, lf);
    char *args[] = {"ff9", strdup(dir), "play", NULL};
    (*env)->ReleaseStringUTFChars(env, jdir, dir);
    (*env)->ReleaseStringUTFChars(env, jlog, logp);
    game_running = 1;
    int r = ff9_main(3, args);
    /* the game quit: end the game process (":game") without running static destructors
       while the runtime's and the audio threads are still alive */
    fflush(stdout); fflush(stderr);
    /* let the log thread forward the last lines: close every write end, wait for EOF */
    close(1); close(2); close(log_pipe[1]);
    pthread_join(t, NULL);
    if (lf) fclose(lf);
    _exit(r);
}

void ff9_rumble_now(float *left, float *right);
extern volatile uint32_t ff9_frames_shown;
/* the game's rumble as two 0..255 values (left << 8 | right) */
JNIEXPORT jint JNICALL Java_com_ff9recomp_GameActivity_nativeRumble(JNIEnv *env, jclass cls) {
    float fl, fr; ff9_rumble_now(&fl, &fr);
    int l = (int)(fl * 255 + 0.5f), r = (int)(fr * 255 + 0.5f);
    if (l < 0) l = 0; if (l > 255) l = 255; if (r < 0) r = 0; if (r > 255) r = 255;
    return l << 8 | r;
}
JNIEXPORT jint JNICALL Java_com_ff9recomp_GameActivity_nativeFrames(JNIEnv *env, jclass cls) { return (jint)ff9_frames_shown; }

void ff9_companion_command(int cmd);
JNIEXPORT void JNICALL Java_com_ff9recomp_GameActivity_nativeCommand(JNIEnv *env, jclass cls, jint cmd) { ff9_companion_command(cmd); }

char *ff9_companion_get(void);
JNIEXPORT jstring JNICALL Java_com_ff9recomp_GameActivity_nativeCompanion(JNIEnv *env, jclass cls) {
    char *c = ff9_companion_get();
    if (!c) return NULL;
    jstring r = (*env)->NewStringUTF(env, c);
    free(c);
    return r;
}

/* the game's text fields (ff9host.c ff9_text_*): a new request's text or null, whether a field is
   still selected, and the text typed in the APK's dialog */
char *ff9_text_poll(void);
int ff9_text_is_open(void);
void ff9_text_reply(const char *s);
JNIEXPORT jstring JNICALL Java_com_ff9recomp_GameActivity_nativeTextRequest(JNIEnv *env, jclass cls) {
    char *c = ff9_text_poll();
    if (!c) return NULL;
    jstring r = (*env)->NewStringUTF(env, c);
    free(c);
    return r;
}
JNIEXPORT jboolean JNICALL Java_com_ff9recomp_GameActivity_nativeTextOpen(JNIEnv *env, jclass cls) { return ff9_text_is_open() ? JNI_TRUE : JNI_FALSE; }
JNIEXPORT void JNICALL Java_com_ff9recomp_GameActivity_nativeTextReply(JNIEnv *env, jclass cls, jstring s) {
    const char *u = (*env)->GetStringUTFChars(env, s, NULL);
    ff9_text_reply(u);
    (*env)->ReleaseStringUTFChars(env, s, u);
}

JNIEXPORT void JNICALL Java_com_ff9recomp_GameActivity_nativePad(JNIEnv *env, jclass cls, jint buttons, jint lt, jint rt, jint lx, jint ly, jint rx, jint ry) {
    static int last = -1;
    if (buttons != last) { __android_log_print(ANDROID_LOG_INFO, "FF9", "pad buttons %04x", buttons); last = buttons; }
    ff9_pad_set((uint16_t)buttons, (uint8_t)lt, (uint8_t)rt, (int16_t)lx, (int16_t)ly, (int16_t)rx, (int16_t)ry);
}
#endif
