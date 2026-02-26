using HarmonyLib;
using NewBuildableCrashedSatellite.Components;

namespace NewBuildableCrashedSatellite.Patches
{
    [HarmonyPatch(typeof(Constructable), "OnCompleteWork")]
    internal static class Constructable_OnCompleteWork_Patch
    {
        private static void Postfix(Constructable __instance)
        {
            if (__instance == null) return;
            var go = __instance.gameObject;
            if (go == null) return;

            var kpid = go.GetComponent<KPrefabID>();
            if (kpid == null) return;

            var tag = kpid.PrefabTag;
            if (tag == TagManager.Create(SatelliteIds.CRASHED) ||
                tag == TagManager.Create(SatelliteIds.WRECKED) ||
                tag == TagManager.Create(SatelliteIds.CRUSHED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_CRASHED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_WRECKED) ||
                tag == TagManager.Create(SatelliteIds.BUILDABLE_CRUSHED))
            {
                go.AddOrGet<PlayerBuiltSatelliteMarker>();

                // Construction can finalize the PrimaryElement from the selected build material
                // after prefab spawn callbacks have already run. Force Neutronium again here so
                // the completed satellite always has the requested main element.
                SatellitePatcher.ForcePrimaryElementUnobtanium(go);
            }
        }
    }
}
