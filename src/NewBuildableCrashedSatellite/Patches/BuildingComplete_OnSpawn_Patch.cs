using HarmonyLib;
using NewBuildableCrashedSatellite.Components;

namespace NewBuildableCrashedSatellite.Patches
{
    [HarmonyPatch(typeof(BuildingComplete), "OnSpawn")]
    internal static class BuildingComplete_OnSpawn_Patch
    {
        private static void Postfix(BuildingComplete __instance)
        {
            if (__instance == null)
                return;

            var go = __instance.gameObject;
            if (go == null)
                return;

            var kpid = go.GetComponent<KPrefabID>();
            if (kpid == null)
                return;

            var tag = kpid.PrefabTag;
            bool isSatellite =
                tag == TagManager.Create(SatelliteIds.CRASHED) ||
                tag == TagManager.Create(SatelliteIds.WRECKED) ||
                tag == TagManager.Create(SatelliteIds.CRUSHED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_CRASHED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_WRECKED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_CRUSHED);

            if (!isSatellite)
                return;

            // Final completed-object timing: this is the most reliable place to enforce
            // the intended main element for the actual in-world building instance.
            SatellitePatcher.ForcePrimaryElementUnobtanium(go);

            // Re-apply on subsequent ticks to survive late material finalization/order differences.
            go.AddOrGet<SatellitePrimaryElementFinalizer>();

            // Keep player-built marker on buildable IDs.
            if (tag == TagManager.Create(SatelliteIds.BUILDABLE_CRASHED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_WRECKED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_CRUSHED))
            {
                go.AddOrGet<PlayerBuiltSatelliteMarker>();
            }
        }
    }
}
