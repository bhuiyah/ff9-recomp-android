// un7z.c: unpack a .7z mod download for the launcher (ModDownloader.java, FF9Recomp; from FF7Recomp), with the LZMA SDK's 7z
// reader (7-Zip 24.09 C sources, public domain; tools/fetch_deps.sh).
//
// Why native: Java's 7z decoder (commons-compress + xz) holds the LZMA window in one Java array
// at the size the packer chose, 256 MB for 7-Zip's Ultra preset (ChaOS), which is the whole
// default Java heap. The app cannot ask for the large heap either: it moves ART's heap over the
// game's fixed guest arena (0x33000000, src/host/ff7_boot.c). Each solid block ("folder") is
// decoded into a file; a block holding a single file (an .iro, the usual mod) becomes that file
// by a rename.
//
// An LZMA or LZMA2 block (what 7-Zip writes for mods) is decoded as a stream through a buffer of
// its dictionary size and written out as it goes: memory stays at the dictionary (64-256 MB),
// whatever the size of the mod. Decoding a 2.4 GB block into one memory-mapped file (SYW Unified
// Fields Textures) put gigabytes of dirty pages in the launcher's process and got it killed.
// The block's files are written directly as they come out, each with its own CRC check. Streaming
// the block into one file and splitting that file through a mapping did the same for a block of
// many files (Moguri Mod: 3.9 GB, 3078 files; GitHub issue 58: stuck at 83%, then closed for low
// memory) and needed the block's size twice on the storage. Other methods (BCJ2, filters) still go through a memory-mapped file. ff9_un7z_cancel() stops a
// streaming decode.

#include "7z.h"
#include "7zAlloc.h"
#include "7zCrc.h"
#include "7zFile.h"
#include "Lzma2Dec.h"
#include "LzmaDec.h"

#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <unistd.h>

static const ISzAlloc g_alloc = { SzAlloc, SzFree };
static volatile int g_cancel;
static volatile UInt64 g_done, g_total;   /* unpacked bytes so far / in all (progress) */
void ff9_un7z_cancel(void) { g_cancel = 1; }
void ff9_un7z_progress(unsigned long long *done, unsigned long long *total) { *done = g_done; *total = g_total; }
#define SZ_CANCELLED 1000
static const ISzAlloc g_alloc_temp = { SzAllocTemp, SzFreeTemp };

static void fail(char *err, size_t n, const char *what, const char *detail) {
    snprintf(err, n, "%s%s%s", what, detail && *detail ? ": " : "", detail ? detail : "");
}

static const char *sres_name(SRes r) {
    switch (r) {
    case SZ_ERROR_DATA: return "damaged data";
    case SZ_ERROR_MEM: return "out of memory";
    case SZ_ERROR_CRC: return "checksum mismatch";
    case SZ_ERROR_UNSUPPORTED: return "unsupported compression method";
    case SZ_ERROR_INPUT_EOF: return "the archive is cut short";
    case SZ_ERROR_ARCHIVE: return "not a valid 7z archive";
    case SZ_ERROR_READ: return "read error";
    case SZ_ERROR_WRITE: return "cannot write (storage full?)";
    case SZ_CANCELLED: return "cancelled";
    default: return "error";
    }
}

/* name of file i as UTF-8, '\\' turned into '/'; NULL if it would leave the output folder */
static char *entry_path(const CSzArEx *db, UInt32 i) {
    size_t n = SzArEx_GetFileNameUtf16(db, i, NULL);
    UInt16 *w = (UInt16 *)malloc(n * sizeof(UInt16));
    char *s = (char *)malloc(n * 4 + 1);
    if (!w || !s) { free(w); free(s); return NULL; }
    SzArEx_GetFileNameUtf16(db, i, w);
    size_t o = 0;
    for (size_t k = 0; k < n && w[k]; k++) {
        UInt32 c = w[k];
        if (c >= 0xD800 && c < 0xDC00 && k + 1 < n && w[k + 1] >= 0xDC00 && w[k + 1] < 0xE000) {
            c = 0x10000 + ((c - 0xD800) << 10) + (w[k + 1] - 0xDC00);
            k++;
        }
        if (c == '\\') c = '/';
        if (c < 0x80) s[o++] = (char)c;
        else if (c < 0x800) { s[o++] = (char)(0xC0 | c >> 6); s[o++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { s[o++] = (char)(0xE0 | c >> 12); s[o++] = (char)(0x80 | ((c >> 6) & 0x3F)); s[o++] = (char)(0x80 | (c & 0x3F)); }
        else { s[o++] = (char)(0xF0 | c >> 18); s[o++] = (char)(0x80 | ((c >> 12) & 0x3F)); s[o++] = (char)(0x80 | ((c >> 6) & 0x3F)); s[o++] = (char)(0x80 | (c & 0x3F)); }
    }
    s[o] = 0;
    free(w);
    // no absolute paths, no ".." components
    if (s[0] == '/') { free(s); return NULL; }
    for (char *p = s; *p;) {
        char *e = strchr(p, '/');
        size_t len = e ? (size_t)(e - p) : strlen(p);
        if (len == 2 && p[0] == '.' && p[1] == '.') { free(s); return NULL; }
        if (!e) break;
        p = e + 1;
    }
    return s;
}

static int mkdirs(const char *path) {   /* path: a directory */
    char buf[4096];
    size_t n = strlen(path);
    if (n >= sizeof buf) return -1;
    memcpy(buf, path, n + 1);
    for (size_t i = 1; i <= n; i++) {
        if (buf[i] == '/' || buf[i] == 0) {
            char c = buf[i];
            buf[i] = 0;
            if (mkdir(buf, 0775) != 0 && errno != EEXIST) return -1;
            buf[i] = c;
        }
    }
    return 0;
}

static int mkparent(const char *file) {
    char buf[4096];
    size_t n = strlen(file);
    if (n >= sizeof buf) return -1;
    memcpy(buf, file, n + 1);
    char *slash = strrchr(buf, '/');
    if (!slash) return 0;
    *slash = 0;
    return mkdirs(buf);
}

static int write_all(int fd, const Byte *p, size_t n) {
    while (n) {
        ssize_t w = write(fd, p, n > (1u << 24) ? (1u << 24) : n);
        if (w <= 0) { if (w < 0 && errno == EINTR) continue; return -1; }
        p += w; n -= (size_t)w;
    }
    return 0;
}

/* Where a streamed block's bytes go: its files, one after the other. */
typedef struct {
    const CSzArEx *db;
    const char *outdir;
    UInt32 f, next, last;  /* folder; next file to look at; one past its last file */
    UInt32 cur;            /* the open file */
    int fd;                /* -1: none open */
    UInt64 left;           /* bytes still to come for the open file */
    UInt32 sum;
    UInt64 unsynced;
    const char *what;      /* set on a failure: what failed (with errno in err) */
    int err;
} Sink;

static SRes sink_close(Sink *k) {
    int fd = k->fd;
    k->fd = -1;
    if (close(fd) != 0) { k->what = "cannot write"; k->err = errno; return SZ_ERROR_WRITE; }
    if (SzBitWithVals_Check(&k->db->CRCs, k->cur) && CRC_GET_DIGEST(k->sum) != k->db->CRCs.Vals[k->cur]) return SZ_ERROR_CRC;
    return SZ_OK;
}

/* Opens the block's next file. */
static SRes sink_next(Sink *k) {
    while (k->next < k->last && (k->db->FileToFolder[k->next] != k->f || SzArEx_IsDir(k->db, k->next))) k->next++;
    if (k->next >= k->last) return SZ_ERROR_DATA;       /* more bytes than the files hold */
    k->cur = k->next++;
    k->left = SzArEx_GetFileSize(k->db, k->cur);
    k->sum = CRC_INIT_VAL;
    char *rel = entry_path(k->db, k->cur);
    if (!rel) { k->what = "bad path in archive"; return SZ_ERROR_ARCHIVE; }
    char path[4096];
    int n = snprintf(path, sizeof path, "%s/%s", k->outdir, rel);
    free(rel);
    if (n < 0 || n >= (int)sizeof path) { k->what = "bad path in archive"; return SZ_ERROR_ARCHIVE; }
    if (mkparent(path) != 0 || (k->fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0664)) < 0) {
        k->what = "cannot write"; k->err = errno;
        return SZ_ERROR_WRITE;
    }
    return SZ_OK;
}

static SRes sink_write(Sink *k, const Byte *p, size_t n) {
    while (n) {
        if (k->fd < 0) RINOK(sink_next(k))
        size_t m = k->left < n ? (size_t)k->left : n;
        if (write_all(k->fd, p, m) != 0) { k->what = "cannot write"; k->err = errno; return SZ_ERROR_WRITE; }
        k->sum = CrcUpdate(k->sum, p, m);
        p += m; n -= m;
        k->left -= m;
        k->unsynced += m;
        if (k->unsynced >= (256u << 20)) {   /* keep the page cache from piling up */
            fdatasync(k->fd);
            posix_fadvise(k->fd, 0, 0, POSIX_FADV_DONTNEED);
            k->unsynced = 0;
        }
        if (k->left == 0) RINOK(sink_close(k))
    }
    return SZ_OK;
}

/* Decode folder f as a stream into its files when it is one LZMA or LZMA2 coder.
 * SZ_ERROR_UNSUPPORTED: another kind of block (the caller decodes it in memory). */
static SRes stream_folder(const CSzArEx *db, UInt32 f, ILookInStreamPtr look, Sink *sink, UInt64 size) {
    const CSzAr *p = &db->db;
    CSzFolder folder;
    CSzData sd;
    const Byte *data = p->CodersData + p->FoCodersOffsets[f];
    sd.Data = data;
    sd.Size = p->FoCodersOffsets[(size_t)f + 1] - p->FoCodersOffsets[f];
    if (SzGetNextFolderItem(&folder, &sd) != SZ_OK || folder.NumCoders != 1 || folder.NumPackStreams != 1)
        return SZ_ERROR_UNSUPPORTED;
    const CSzCoderInfo *c = &folder.Coders[0];
    int lzma2 = c->MethodID == 0x21;
    if (!lzma2 && c->MethodID != 0x30101) return SZ_ERROR_UNSUPPORTED;
    const Byte *props = data + c->PropsOffset;
    UInt64 dict;
    if (lzma2) {
        if (c->PropsSize != 1 || props[0] > 40) return SZ_ERROR_UNSUPPORTED;
        dict = props[0] == 40 ? 0xFFFFFFFFu : ((UInt64)(2 | (props[0] & 1)) << (props[0] / 2 + 11));
    } else {
        if (c->PropsSize < 5) return SZ_ERROR_UNSUPPORTED;
        dict = props[1] | ((UInt32)props[2] << 8) | ((UInt32)props[3] << 16) | ((UInt32)props[4] << 24);
    }
    if (dict > size) dict = size;
    if (dict < (1u << 20)) dict = 1u << 20;
    size_t dicSize = (size_t)dict;
    UInt32 pi = p->FoStartPackStreamIndex[f];
    UInt64 inSize = p->PackPositions[pi + 1] - p->PackPositions[pi];
    RINOK(LookInStream_SeekTo(look, db->dataPos + p->PackPositions[pi]))

    CLzmaDec lz;
    CLzma2Dec lz2;
    CLzmaDec *dec;
    if (lzma2) {
        Lzma2Dec_CONSTRUCT(&lz2)
        RINOK(Lzma2Dec_AllocateProbs(&lz2, props[0], &g_alloc))
        dec = &lz2.decoder;
    } else {
        LzmaDec_CONSTRUCT(&lz)
        RINOK(LzmaDec_AllocateProbs(&lz, props, c->PropsSize, &g_alloc))
        dec = &lz;
    }
    Byte *dic = (Byte *)malloc(dicSize);
    if (!dic) {
        if (lzma2) Lzma2Dec_FreeProbs(&lz2, &g_alloc); else LzmaDec_FreeProbs(&lz, &g_alloc);
        return SZ_ERROR_MEM;
    }
    dec->dic = dic;
    dec->dicBufSize = dicSize;
    if (lzma2) Lzma2Dec_Init(&lz2); else LzmaDec_Init(&lz);

    SRes res = SZ_OK;
    UInt64 out = 0;
    UInt32 sum = CRC_INIT_VAL;
    for (;;) {
        if (g_cancel) { res = SZ_CANCELLED; break; }
        const void *inBuf = NULL;
        size_t lookahead = 1 << 18;
        if (lookahead > inSize) lookahead = (size_t)inSize;
        res = ILookInStream_Look(look, &inBuf, &lookahead);
        if (res != SZ_OK) break;
        size_t start = dec->dicPos;
        SizeT limit = dicSize;
        if (size - out < (UInt64)(dicSize - start)) limit = start + (SizeT)(size - out);
        SizeT inProcessed = (SizeT)lookahead;
        ELzmaStatus status;
        res = lzma2 ? Lzma2Dec_DecodeToDic(&lz2, limit, (const Byte *)inBuf, &inProcessed, LZMA_FINISH_ANY, &status)
                    : LzmaDec_DecodeToDic(&lz, limit, (const Byte *)inBuf, &inProcessed, LZMA_FINISH_ANY, &status);
        inSize -= inProcessed;
        if (res != SZ_OK) break;
        res = ILookInStream_Skip(look, inProcessed);
        if (res != SZ_OK) break;
        size_t made = dec->dicPos - start;
        if (made) {
            res = sink_write(sink, dic + start, made);
            if (res != SZ_OK) break;
            sum = CrcUpdate(sum, dic + start, made);
            out += made;
            g_done += made;
        }
        if (dec->dicPos == dicSize) dec->dicPos = 0;    /* the dictionary is a ring */
        if (out == size) break;
        if (inProcessed == 0 && made == 0) { res = SZ_ERROR_DATA; break; }
        if (status == LZMA_STATUS_FINISHED_WITH_MARK) { res = SZ_ERROR_DATA; break; }   /* ended early */
    }
    free(dic);
    if (lzma2) Lzma2Dec_FreeProbs(&lz2, &g_alloc); else LzmaDec_FreeProbs(&lz, &g_alloc);
    if (res == SZ_OK && SzBitWithVals_Check(&p->FolderCRCs, f) && CRC_GET_DIGEST(sum) != p->FolderCRCs.Vals[f]) res = SZ_ERROR_CRC;
    if (res == SZ_OK && sink->fd >= 0) res = SZ_ERROR_DATA;     /* the last file is cut short */
    return res;
}

/* Returns 0 and leaves err empty on success; otherwise a message in err. */
int ff9_un7z(const char *archive, const char *outdir, char *err, size_t errlen) {
    err[0] = 0;
    g_cancel = 0;
    static int crc_ready;
    if (!crc_ready) { CrcGenerateTable(); crc_ready = 1; }

    CFileInStream in;
    if (InFile_Open(&in.file, archive) != 0) { fail(err, errlen, "cannot open the download", strerror(errno)); return -1; }
    FileInStream_CreateVTable(&in);
    CLookToRead2 look;
    LookToRead2_CreateVTable(&look, False);
    look.bufSize = 1 << 18;
    look.buf = (Byte *)malloc(look.bufSize);
    look.realStream = &in.vt;
    LookToRead2_INIT(&look);

    CSzArEx db;
    SzArEx_Init(&db);
    int rc = -1;
    SRes r = look.buf ? SzArEx_Open(&db, &look.vt, &g_alloc, &g_alloc_temp) : SZ_ERROR_MEM;
    if (r != SZ_OK) { fail(err, errlen, "cannot read the 7z archive", sres_name(r)); goto done; }
    if (mkdirs(outdir) != 0) { fail(err, errlen, "cannot create the unpack folder", strerror(errno)); goto done; }
    {   /* everything unpacks before the archive is deleted: check the room first */
        UInt64 total = 0;
        for (UInt32 f = 0; f < db.db.NumFolders; f++) total += SzAr_GetFolderUnpackSize(&db.db, f);
        g_done = 0;
        g_total = total;
        struct statvfs st;
        if (statvfs(outdir, &st) == 0) {
            UInt64 freeb = (UInt64)st.f_bavail * st.f_frsize;
            if (freeb < total + (64u << 20)) {
                char d[160];
                snprintf(d, sizeof d, "this mod unpacks to %.1f GB, %.1f GB free", total / 1073741824.0, freeb / 1073741824.0);
                fail(err, errlen, "not enough free space", d);
                goto done;
            }
        }
    }

    // directories and empty files (no folder)
    for (UInt32 i = 0; i < db.NumFiles; i++) {
        if (db.FileToFolder[i] != (UInt32)-1) continue;
        char *rel = entry_path(&db, i);
        if (!rel) { fail(err, errlen, "bad path in archive", NULL); goto done; }
        char path[4096];
        snprintf(path, sizeof path, "%s/%s", outdir, rel);
        free(rel);
        if (SzArEx_IsDir(&db, i)) { if (mkdirs(path) != 0) { fail(err, errlen, "cannot create folder", strerror(errno)); goto done; } }
        else {
            if (mkparent(path) != 0) { fail(err, errlen, "cannot create folder", strerror(errno)); goto done; }
            int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0664);
            if (fd < 0) { fail(err, errlen, "cannot write", strerror(errno)); goto done; }
            close(fd);
        }
    }

    for (UInt32 f = 0; f < db.db.NumFolders; f++) {
        UInt64 size64 = SzAr_GetFolderUnpackSize(&db.db, f);
        if (size64 == 0) continue;
        if (size64 > (UInt64)(SIZE_MAX / 2)) { fail(err, errlen, "archive block too large", NULL); goto done; }
        size_t size = (size_t)size64;
        UInt32 first = db.FolderToFile[f], last = db.FolderToFile[f + 1];
        Sink sink = { .db = &db, .outdir = outdir, .f = f, .next = first, .last = last, .fd = -1 };
        r = stream_folder(&db, f, &look.vt, &sink, size64);
        if (sink.fd >= 0) close(sink.fd);
        if (r == SZ_OK) continue;
        if (r != SZ_ERROR_UNSUPPORTED) {
            if (sink.what) fail(err, errlen, sink.what, sink.err ? strerror(sink.err) : NULL);
            else fail(err, errlen, "cannot unpack", sres_name(r));
            goto done;
        }
        /* a block not streamed: decode it into a mapped file and split that */
        char tmp[4096];
        snprintf(tmp, sizeof tmp, "%s/.7zblock%u", outdir, (unsigned)f);
        int fd = open(tmp, O_RDWR | O_CREAT | O_TRUNC, 0664);
        if (fd < 0) { fail(err, errlen, "cannot write", strerror(errno)); goto done; }
        if (ftruncate(fd, (off_t)size) != 0) { fail(err, errlen, "not enough free space", strerror(errno)); close(fd); goto done; }
        Byte *buf = (Byte *)mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (buf == MAP_FAILED) { fail(err, errlen, "cannot map the unpack file", strerror(errno)); close(fd); unlink(tmp); goto done; }
        r = SzAr_DecodeFolder(&db.db, f, &look.vt, db.dataPos, buf, size, &g_alloc);
        if (r != SZ_OK) { fail(err, errlen, "cannot unpack", sres_name(r)); munmap(buf, size); close(fd); unlink(tmp); goto done; }
        g_done += size64;

        UInt64 start = db.UnpackPositions[first];
        UInt32 nfiles = 0, only = first;
        for (UInt32 i = first; i < last; i++) {
            if (SzArEx_IsDir(&db, i)) continue;
            UInt64 off = db.UnpackPositions[i] - start, len = SzArEx_GetFileSize(&db, i);
            if (off + len > size64) { fail(err, errlen, "cannot unpack", "damaged data"); munmap(buf, size); close(fd); unlink(tmp); goto done; }
            if (SzBitWithVals_Check(&db.CRCs, i) && CrcCalc(buf + off, (size_t)len) != db.CRCs.Vals[i]) {
                fail(err, errlen, "cannot unpack", "checksum mismatch"); munmap(buf, size); close(fd); unlink(tmp); goto done;
            }
            nfiles++;
            only = i;
        }
        int renamed = 0;
        if (nfiles == 1 && SzArEx_GetFileSize(&db, only) == size64) {
            // the block is exactly one file (the common .iro mod): it becomes that file
            munmap(buf, size);
            close(fd);
            char *rel = entry_path(&db, only);
            if (!rel) { fail(err, errlen, "bad path in archive", NULL); unlink(tmp); goto done; }
            char path[4096];
            snprintf(path, sizeof path, "%s/%s", outdir, rel);
            free(rel);
            if (mkparent(path) != 0 || rename(tmp, path) != 0) { fail(err, errlen, "cannot write", strerror(errno)); unlink(tmp); goto done; }
            renamed = 1;
        } else {
            for (UInt32 i = first; i < last; i++) {
                if (SzArEx_IsDir(&db, i)) continue;
                char *rel = entry_path(&db, i);
                if (!rel) { fail(err, errlen, "bad path in archive", NULL); munmap(buf, size); close(fd); unlink(tmp); goto done; }
                char path[4096];
                snprintf(path, sizeof path, "%s/%s", outdir, rel);
                free(rel);
                int ofd = mkparent(path) == 0 ? open(path, O_WRONLY | O_CREAT | O_TRUNC, 0664) : -1;
                int ok = ofd >= 0 && write_all(ofd, buf + (db.UnpackPositions[i] - start), (size_t)SzArEx_GetFileSize(&db, i)) == 0;
                if (ofd >= 0) close(ofd);
                if (!ok) { fail(err, errlen, "cannot write", strerror(errno)); munmap(buf, size); close(fd); unlink(tmp); goto done; }
            }
            munmap(buf, size);
            close(fd);
        }
        if (!renamed) unlink(tmp);
    }
    rc = 0;

done:
    SzArEx_Free(&db, &g_alloc);
    free(look.buf);
    File_Close(&in.file);
    return rc;
}


/* ---- JNI (com.ff9recomp.ModDownloader), in libff9launcher.so ---- */
#include <jni.h>
JNIEXPORT void JNICALL Java_com_ff9recomp_ModDownloader_nativeUn7zCancel(JNIEnv *env, jclass c) { ff9_un7z_cancel(); }
JNIEXPORT jlongArray JNICALL Java_com_ff9recomp_ModDownloader_nativeUn7zProgress(JNIEnv *env, jclass c) {
    unsigned long long d, t;
    ff9_un7z_progress(&d, &t);
    jlong v[2] = { (jlong)d, (jlong)t };
    jlongArray a = (*env)->NewLongArray(env, 2);
    if (a) (*env)->SetLongArrayRegion(env, a, 0, 2, v);
    return a;
}
JNIEXPORT jstring JNICALL Java_com_ff9recomp_ModDownloader_nativeUn7z(JNIEnv *env, jclass c, jstring jarchive, jstring joutdir) {
    const char *a = (*env)->GetStringUTFChars(env, jarchive, NULL);
    const char *o = (*env)->GetStringUTFChars(env, joutdir, NULL);
    char err[512];
    int rc = ff9_un7z(a, o, err, sizeof err);
    (*env)->ReleaseStringUTFChars(env, jarchive, a);
    (*env)->ReleaseStringUTFChars(env, joutdir, o);
    return rc == 0 ? NULL : (*env)->NewStringUTF(env, err);
}
