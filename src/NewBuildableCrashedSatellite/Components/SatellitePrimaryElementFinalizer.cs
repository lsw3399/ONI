using UnityEngine;

namespace NewBuildableCrashedSatellite.Components
{
    /// <summary>
    /// Re-applies Neutronium on the final completed building object.
    /// Construction/game spawn order can overwrite PrimaryElement after earlier patches run.
    /// </summary>
    internal sealed class SatellitePrimaryElementFinalizer : KMonoBehaviour, ISim1000ms
    {
        private int ticks;

        protected override void OnSpawn()
        {
            base.OnSpawn();
            ticks = 0;
            SatellitePatches.ForceNow(gameObject);
        }

        public void Sim1000ms(float dt)
        {
            ticks++;
            SatellitePatches.ForceNow(gameObject);

            // Run a couple of times to survive late construction/material finalization, then self-remove.
            if (ticks >= 2)
            {
                try
                {
                    enabled = false;
                    Destroy(this);
                }
                catch
                {
                }
            }
        }
    }

    internal static class SatellitePatches
    {
        internal static void ForceNow(GameObject go)
        {
            if (go == null)
                return;

            try
            {
                NewBuildableCrashedSatellite.Patches.SatellitePatcher.ForcePrimaryElementUnobtanium(go);
            }
            catch
            {
            }
        }
    }
}
