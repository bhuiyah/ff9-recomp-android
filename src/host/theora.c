/* theorawrapper.dll's API (FF9's MovieMaterial) on FFmpeg: Ogg Theora movies decoded on the game
 * thread and uploaded as three single-channel GL textures (Y, Cb, Cr) that the game wraps with
 * Texture2D.CreateExternalTexture and converts in its movie shader. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <GLES3/gl3.h>

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>

#define EXPORT __attribute__((visibility("default")))
typedef int BOOL;

typedef struct {
    AVFormatContext *fmt;
    AVCodecContext *dec;
    AVFrame *frame;
    AVPacket *pkt;
    int stream;
    double fps, duration, timeBase;
    double decodedTime, uploadedTime, targetTime;
    int finished, eof, haveFrame, uploadedFrame;
    int width, height;
    int ystride, yheight, uvstride, uvheight;
    GLuint tex[3];
    /* decode cost, written to the log when the movie closes (high-resolution movie mods) */
    int frames, slow;
    double decodeMs, worstMs;
    /* FF9_TRACE_MOVIE: draws that showed no new frame, and ones that skipped frames */
    int draws, repeats, skips;
} Movie;

static double now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1000.0 + t.tv_nsec / 1e6;
}

EXPORT Movie *CreateContext(void) {
    Movie *m = calloc(1, sizeof *m);
    m->stream = -1;
    return m;
}

EXPORT void CloseStream(Movie *m) {
    if (!m) return;
    if (m->frames > 0 && m->dec)
        fprintf(stdout, "movie %dx%d: %d frames, decode %.1f ms average, %.1f ms worst, %d over a frame's time\n",
                m->dec->width, m->dec->height, m->frames, m->decodeMs / m->frames, m->worstMs, m->slow);
    if (m->draws > 0 && getenv("FF9_TRACE_MOVIE"))
        fprintf(stdout, "movie draws %d: %d without a new frame, %d skipping frames\n", m->draws, m->repeats, m->skips);
    m->frames = m->slow = m->draws = m->repeats = m->skips = 0;
    m->decodeMs = m->worstMs = 0;
    if (m->dec) avcodec_free_context(&m->dec);
    if (m->fmt) avformat_close_input(&m->fmt);
    if (m->frame) av_frame_free(&m->frame);
    if (m->pkt) av_packet_free(&m->pkt);
    m->stream = -1;
    m->finished = m->eof = m->haveFrame = 0;
}

EXPORT void DestroyContext(Movie *m) {
    if (!m) return;
    CloseStream(m);
    if (m->tex[0]) glDeleteTextures(3, m->tex);
    free(m);
}

static int decode_next(Movie *m) {
    while (1) {
        int r = avcodec_receive_frame(m->dec, m->frame);
        if (r == 0) {
            int64_t pts = m->frame->best_effort_timestamp != AV_NOPTS_VALUE ? m->frame->best_effort_timestamp : m->frame->pts;
            m->decodedTime = pts == AV_NOPTS_VALUE ? m->decodedTime + 1.0 / m->fps : pts * m->timeBase;
            m->haveFrame = 1;
            m->uploadedFrame = 0;
            return 1;
        }
        if (r == AVERROR_EOF) { m->finished = 1; return 0; }
        if (m->eof) { avcodec_send_packet(m->dec, NULL); continue; }
        r = av_read_frame(m->fmt, m->pkt);
        if (r < 0) { m->eof = 1; avcodec_send_packet(m->dec, NULL); continue; }
        if (m->pkt->stream_index == m->stream) avcodec_send_packet(m->dec, m->pkt);
        av_packet_unref(m->pkt);
    }
}

EXPORT BOOL OpenStream(Movie *m, const char *path, int offset, int size, BOOL pot, BOOL scanDuration, int maxSkipFrames) {
    CloseStream(m);
    if (avformat_open_input(&m->fmt, path, NULL, NULL) < 0) { fprintf(stderr, "theora: cannot open %s\n", path); return 0; }
    avformat_find_stream_info(m->fmt, NULL);
    m->stream = av_find_best_stream(m->fmt, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    if (m->stream < 0) { CloseStream(m); return 0; }
    AVStream *st = m->fmt->streams[m->stream];
    const AVCodec *c = avcodec_find_decoder(st->codecpar->codec_id);
    if (!c) { fprintf(stderr, "theora: no decoder for %s\n", path); CloseStream(m); return 0; }
    m->dec = avcodec_alloc_context3(c);
    avcodec_parameters_to_context(m->dec, st->codecpar);
    /* the decoder on several cores: high-resolution movie mods (1920x1440, 2880x2160) are too
       much for one core while the game runs */
    m->dec->thread_count = 0;
    m->dec->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    if (avcodec_open2(m->dec, c, NULL) < 0) { CloseStream(m); return 0; }
    m->frame = av_frame_alloc();
    m->pkt = av_packet_alloc();
    m->timeBase = av_q2d(st->time_base);
    AVRational fr = st->avg_frame_rate.num ? st->avg_frame_rate : st->r_frame_rate;
    m->fps = fr.num ? av_q2d(fr) : 30.0;
    m->duration = m->fmt->duration > 0 ? m->fmt->duration / (double)AV_TIME_BASE : (st->duration > 0 ? st->duration * m->timeBase : 0);
    m->width = m->dec->width;
    if (getenv("FF9_TRACE_MOVIE")) fprintf(stdout, "movie %s: %dx%d %.2f fps %.1f s codec %s\n", path, m->dec->width, m->dec->height, m->fps, m->duration, c->name);
    m->height = m->dec->height;
    m->decodedTime = m->uploadedTime = -1;
    m->targetTime = 0;
    /* the first frame gives the plane strides */
    if (!decode_next(m)) return 0;
    m->ystride = m->frame->linesize[0];
    m->yheight = m->frame->height;
    m->uvstride = m->frame->linesize[1];
    m->uvheight = m->frame->format == AV_PIX_FMT_YUV420P ? (m->frame->height + 1) / 2 : m->frame->height;
    if (!m->tex[0]) glGenTextures(3, m->tex);
    for (int i = 0; i < 3; i++) {
        glBindTexture(GL_TEXTURE_2D, m->tex[i]);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        int w = i == 0 ? m->ystride : m->uvstride, h = i == 0 ? m->yheight : m->uvheight;
        glTexImage2D(GL_TEXTURE_2D, 0, GL_ALPHA, w, h, 0, GL_ALPHA, GL_UNSIGNED_BYTE, NULL);
    }
    return 1;
}

/* a movie's frame was drawn in the last half second (frame generation leaves movies alone) */
static double movie_at = -1e9;
EXPORT int ff9_movie_recent(void) { return now_ms() - movie_at < 500; }

static void upload(Movie *m) {
    if (!m->haveFrame || m->uploadedFrame) return;
    movie_at = now_ms();
    if (getenv("FF9_NO_MOVIE_UPLOAD")) { m->uploadedFrame = 1; m->uploadedTime = m->decodedTime; return; }
    glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    for (int i = 0; i < 3; i++) {
        glBindTexture(GL_TEXTURE_2D, m->tex[i]);
        int w = i == 0 ? m->ystride : m->uvstride, h = i == 0 ? m->yheight : m->uvheight;
        if (m->frame->linesize[i] != w) continue;
        glTexSubImage2D(GL_TEXTURE_2D, 0, 0, 0, w, h, GL_ALPHA, GL_UNSIGNED_BYTE, m->frame->data[i]);
    }
    m->uploadedFrame = 1;
    m->uploadedTime = m->decodedTime;
}

/* decode up to the target time (dropping late frames) and upload the newest: the frame nearest
   the target. The game draws movies at the movie's own rate (MBG.Play sets the frame rate to it),
   so targets fall about on frames' starts; the nearest frame, not the last one before the target,
   keeps a little timing noise from showing a frame twice and skipping the next. */
EXPORT void SetTargetDisplayDecodeTime(Movie *m, double t) {
    if (!m || m->stream < 0) return;
    /* FF9_TRACE_MOVIE=2: each draw's target time and the wall clock (the first 6000 draws) */
    static int traceDraws = -1;
    if (traceDraws < 0) { const char *e = getenv("FF9_TRACE_MOVIE"); traceDraws = e && e[0] == '2'; }
    if (traceDraws && m->draws < 6000) fprintf(stdout, "mdraw %.4f %.1f %.4f\n", t, now_ms(), m->decodedTime);
    m->targetTime = t;
    int n = 0;
    while (!m->finished && m->decodedTime + 0.5 / m->fps <= t) {
        double t0 = now_ms();
        if (!decode_next(m)) break;
        n++;
        double ms = now_ms() - t0;
        m->frames++;
        m->decodeMs += ms;
        if (ms > m->worstMs) m->worstMs = ms;
        if (ms > 1000.0 / m->fps) m->slow++;
    }
    if (!m->finished) { m->draws++; if (n == 0) m->repeats++; else if (n > 1) m->skips++; }
    upload(m);
}

EXPORT int GetPicWidth(Movie *m) { return m->width; }
EXPORT int GetPicHeight(Movie *m) { return m->height; }
EXPORT int GetPicX(Movie *m) { return 0; }
EXPORT int GetPicY(Movie *m) { return 0; }
EXPORT int GetYStride(Movie *m) { return m->ystride; }
EXPORT int GetYHeight(Movie *m) { return m->yheight; }
EXPORT int GetUVStride(Movie *m) { return m->uvstride; }
EXPORT int GetUVHeight(Movie *m) { return m->uvheight; }
EXPORT BOOL HasFinished(Movie *m) { return m->finished; }
EXPORT double GetDecodedFrameTime(Movie *m) { return m->decodedTime; }
EXPORT double GetUploadedFrameTime(Movie *m) { return m->uploadedTime; }
EXPORT double GetTargetDecodeFrameTime(Movie *m) { return m->targetTime; }
EXPORT double GetVideoFPS(Movie *m) { return m->fps; }
EXPORT float GetAspectRatio(Movie *m) { return m->height ? (float)m->width / m->height : 1.0f; }
EXPORT double GetDuration(Movie *m) { return m->duration; }
EXPORT void *GetNativeHandle(Movie *m, int plane) { return (void *)(uintptr_t)m->tex[plane]; }
EXPORT void *GetNativeTextureContext(Movie *m) { return m; }
EXPORT void SetPostProcessingLevel(Movie *m, int level) {}

EXPORT double Seek(Movie *m, double seconds, BOOL waitKeyFrame) {
    if (!m || m->stream < 0) return 0;
    AVStream *st = m->fmt->streams[m->stream];
    int64_t ts = (int64_t)(seconds / m->timeBase);
    av_seek_frame(m->fmt, m->stream, ts, AVSEEK_FLAG_BACKWARD);
    avcodec_flush_buffers(m->dec);
    m->eof = m->finished = 0;
    m->decodedTime = seconds - 1.0 / m->fps;
    decode_next(m);
    while (!m->finished && m->decodedTime + 1.0 / m->fps <= seconds) if (!decode_next(m)) break;
    upload(m);
    (void)st;
    return m->decodedTime;
}
