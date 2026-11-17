// Resources.UnloadUnusedAssets (GitHub issue 16). FF9 calls it on every scene change
// (SceneDirector.ChangeScene) and leaves the rest to it: each field's background textures, meshes
// and materials are made at runtime and never destroyed. With it a stub, a field change kept about
// 40 MB (the Moguri Mod's backgrounds) and ~1100 meshes; after an hour the game had 1.6-2 GB and the
// GPU driver crashed in a draw.
//
// Unity's rule: an asset is unused when nothing reaches it from the scenes' objects or from
// scripts (statics, stacks). Here components and scripts hold assets through their managed
// objects (Mesh, Texture, Material, Sprite), and the registry holds every record. So: take each
// candidate out of the registry and drop its record's link to its managed object, let the GC run
// with a weak handle on that object, then put back the ones still reached and remove the rest. A
// material or sprite removed can free the textures it held: up to three rounds. Copies of the
// game files' assets (Renderer.material's "(Instance)" materials, which hold each field model's
// 512x512 render texture) are NAsset records and count too.
// FF9_TEST_FIELDS (Core/TestLoad.cs) changes fields over and over and logs memory: 68 changes kept
// ~70 textures and ~2000 meshes, against 290 and 53000 (and climbing) before.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FF9Engine.Gfx;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Core;

public static class Unloader {
    static readonly bool off = Environment.GetEnvironmentVariable("FF9_TEST_NO_UNLOAD") != null;

    /// Made at runtime (not read from the game's files, which stay cached), not kept on purpose,
    /// and of a kind Unity unloads. Render textures too (each field model's 512x512 one piled up),
    /// but not temporary ones (RenderTexture.GetTemporary's pool) or the one being drawn into.
    static bool Candidate(NObject o) =>
        o.Managed != null && !o.IsAsset && !o.DontDestroy && (o.HideFlags & HideFlags.DontUnloadUnusedAsset) == 0 &&
        Kind(o is Assets.NAsset a ? a.Data : o);

    static bool Kind(object? o) => o switch {
        NRenderTexture rt => !rt.Temporary && rt != RenderTargets.Active,
        NTexture or NMesh or NMaterial or NSprite => true,
        _ => false,
    };

    public static void Run() {
        if (off) return;
        var sw = Stopwatch.StartNew();
        int freed = 0, rounds = 0;
        long texBytes = 0;
        for (; rounds < 3; rounds++) {
            Pipeline.ForgetDeadMaterials();
            var list = new List<(NObject n, GCHandle h)>();
            foreach (var o in Registry.All)
                if (Candidate(o)) list.Add((o, default));
            if (list.Count == 0) break;
            for (int i = 0; i < list.Count; i++) {
                var n = list[i].n;
                list[i] = (n, GCHandle.Alloc(n.Managed, GCHandleType.Weak));
                n.Managed = null;
                Registry.Hide(n);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            bool holders = false;
            int before = freed;
            foreach (var (n, h) in list) {
                var m = h.Target as UObject;
                h.Free();
                if (m is not null) { n.Managed = m; Registry.Unhide(n); continue; }
                freed++;
                Registry.MarkGone(n);
                var d = n is Assets.NAsset a ? a.Data : n;
                if (d is NTexture t) texBytes += (long)t.Width * t.Height * 4;
                if (d is NMaterial or NSprite) holders = true;
                if (Device.Ready) n.FreeGpu();
            }
            if (!holders || freed == before) { rounds++; break; }
        }
        Pipeline.ForgetDeadMaterials();
        if (freed > 0) GC.Collect();
        Log.Info($"unloaded {freed} unused assets ({texBytes >> 20} MB of textures) in {rounds} rounds, {sw.ElapsedMilliseconds} ms; objects {Registry.Count}");
    }
}
