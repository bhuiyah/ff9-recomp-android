// FF9_TEST_IMMORTAL=1 (tests): in battle, the party's maximum and current HP are held at 9999 every
// frame, so a low-level test party survives late-game formations (-memoria-battle N). Battle+
// (the Battle Assistance booster) is not enough for that: it ignores only hits that wouldn't kill.
using System;
using System.Collections;
using System.Reflection;

namespace FF9Engine.Core;

public static class ImmortalTest {
    static readonly bool on = Environment.GetEnvironmentVariable("FF9_TEST_IMMORTAL") != null;
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Tick() {
        if (!on || Player.LastSceneLoaded != "BattleMap") return;
        try {
            var ss = Type.GetType("FF9StateSystem, Assembly-CSharp");
            var battleState = ss?.GetProperty("Battle", F)?.GetValue(null);
            var battle = battleState?.GetType().GetField("FF9Battle", F)?.GetValue(battleState);
            if (battle?.GetType().GetMethod("EnumerateBattleUnits", F, null, Type.EmptyTypes, null)?.Invoke(battle, null) is not IEnumerable units) return;
            foreach (var u in units) {
                var t = u.GetType();
                if (t.GetProperty("IsPlayer", F)?.GetValue(u) is not true) continue;
                var max = t.GetProperty("MaximumHp", F);
                var cur = t.GetProperty("CurrentHp", F);
                if (Convert.ToUInt32(max!.GetValue(u)) != 9999u) max.SetValue(u, 9999u);
                if (Convert.ToUInt32(cur!.GetValue(u)) != 9999u) cur.SetValue(u, 9999u);
            }
        } catch (Exception e) { Log.Once("immortal", "immortal test: " + e.Message); }
    }
}
