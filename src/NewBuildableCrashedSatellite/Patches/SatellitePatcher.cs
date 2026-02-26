using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NewBuildableCrashedSatellite.Components;
using NewBuildableCrashedSatellite.Utils;
using UnityEngine;

namespace NewBuildableCrashedSatellite.Patches
{
    /// <summary>
    /// Central place for applying all changes to the three satellite POI buildings.
    /// </summary>
    internal static class SatellitePatcher
    {
        private const string ModVersion = "30.4";

        /// <summary>
        /// Safety fallback kanim (must exist in base game). Used only if the vanilla satellite kanim
        /// cannot be resolved for some reason.
        /// </summary>
        internal const string FallbackKanim = "clock_poi_kanim";

        private static bool stringsRegistered;

        // Cache resolved vanilla satellite kanim names so buildable defs can reuse correct icons.
        private static readonly Dictionary<string, string> vanillaKanimById = new Dictionary<string, string>();

        // Cache the "back-like" layer we use to allow overlap for Wrecked/Crushed.
        private static ObjectLayer? cachedBackLayer;

        private static bool IsCrashed(string id) => id == SatelliteIds.CRASHED || id == SatelliteIds.BUILDABLE_CRASHED;
        private static bool IsWrecked(string id) => id == SatelliteIds.WRECKED || id == SatelliteIds.BUILDABLE_WRECKED;
        private static bool IsCrushed(string id) => id == SatelliteIds.CRUSHED || id == SatelliteIds.BUILDABLE_CRUSHED;
        private static bool IsAnySatellite(string id) => IsCrashed(id) || IsWrecked(id) || IsCrushed(id);
        private static bool IsBuildableSatellite(string id) =>
            id == SatelliteIds.BUILDABLE_CRASHED || id == SatelliteIds.BUILDABLE_WRECKED || id == SatelliteIds.BUILDABLE_CRUSHED;

        private static string GetVanillaIdFor(string id)
        {
            if (IsCrashed(id)) return SatelliteIds.CRASHED;
            if (IsWrecked(id)) return SatelliteIds.WRECKED;
            if (IsCrushed(id)) return SatelliteIds.CRUSHED;
            return id;
        }

        /// <summary>
        /// Copy vanilla satellite localized strings onto NBCS buildable IDs.
        /// Called from Db.Initialize (safe) so build menu / tech tree show the same translations.
        /// </summary>
        internal static void RegisterBuildableStrings()
        {
            if (stringsRegistered)
                return;
            stringsRegistered = true;

            try
            {
                CopyBuildingStrings("PROPSURFACESATELLITE1", SatelliteIds.BUILDABLE_CRASHED.ToUpperInvariant());
                CopyBuildingStrings("PROPSURFACESATELLITE2", SatelliteIds.BUILDABLE_WRECKED.ToUpperInvariant());
                CopyBuildingStrings("PROPSURFACESATELLITE3", SatelliteIds.BUILDABLE_CRUSHED.ToUpperInvariant());
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS] RegisterBuildableStrings failed: " + e);
            }
        }

        private static void CopyBuildingStrings(string fromUpperId, string toUpperId)
        {
            string fromBase = "STRINGS.BUILDINGS.PREFABS." + fromUpperId;
            string toBase = "STRINGS.BUILDINGS.PREFABS." + toUpperId;

            string name = Strings.Get(fromBase + ".NAME");
            string desc = Strings.Get(fromBase + ".DESC");
            string effect = Strings.Get(fromBase + ".EFFECT");

            if (!string.IsNullOrEmpty(name)) Strings.Add(toBase + ".NAME", name);
            if (!string.IsNullOrEmpty(desc)) Strings.Add(toBase + ".DESC", desc);
            if (!string.IsNullOrEmpty(effect)) Strings.Add(toBase + ".EFFECT", effect);
        }

        /// <summary>
        /// Cache vanilla satellite kanim names from prefabs, if available.
        /// </summary>
        internal static void CaptureVanillaSatelliteVisuals()
        {
            TryCacheVanillaKanim(SatelliteIds.CRASHED);
            TryCacheVanillaKanim(SatelliteIds.WRECKED);
            TryCacheVanillaKanim(SatelliteIds.CRUSHED);
        }

        private static void TryCacheVanillaKanim(string vanillaId)
        {
            if (string.IsNullOrEmpty(vanillaId))
                return;

            if (vanillaKanimById.ContainsKey(vanillaId))
                return;

            try
            {
                string animName = TryGetKanimNameFromVanillaPrefab(vanillaId);
                if (!string.IsNullOrEmpty(animName))
                    vanillaKanimById[vanillaId] = animName;
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Attempts to reuse the vanilla satellite kanims for the buildable versions so the build menu / tech tree uses the correct icon.
        /// </summary>
        internal static void TryApplyBuildableVisualsFromVanilla()
        {
            try
            {
                CaptureVanillaSatelliteVisuals();

                TryApplyBuildableVisual(SatelliteIds.BUILDABLE_CRASHED, SatelliteIds.CRASHED);
                TryApplyBuildableVisual(SatelliteIds.BUILDABLE_WRECKED, SatelliteIds.WRECKED);
                TryApplyBuildableVisual(SatelliteIds.BUILDABLE_CRUSHED, SatelliteIds.CRUSHED);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS] TryApplyBuildableVisualsFromVanilla failed: " + e);
            }
        }

        private static void TryApplyBuildableVisual(string buildableId, string vanillaId)
        {
            if (string.IsNullOrEmpty(buildableId) || string.IsNullOrEmpty(vanillaId))
                return;

            var def = Assets.GetBuildingDef(buildableId);
            if (def == null)
                return;

            // Copy key visual/placement layers from the vanilla POI so the buildable behaves similarly (overlap, draw order).
            TryCopyLayersFromVanillaDef(def, vanillaId);

            // Resolve kanim name.
            vanillaKanimById.TryGetValue(vanillaId, out var animName);
            if (string.IsNullOrEmpty(animName))
            {
                animName = TryGetKanimNameFromVanillaPrefab(vanillaId);
                if (!string.IsNullOrEmpty(animName))
                    vanillaKanimById[vanillaId] = animName;
            }

            if (string.IsNullOrEmpty(animName))
                return;

            var animFile = Assets.GetAnim(animName);
            if (animFile == null)
                return;

            SetKAnimFiles(def, new[] { animFile });
            SetStringField(def, new[] { "anim", "Anim" }, animName);

            // Also update the buildable prefab itself (in-world animation), not just the BuildingDef (UI icon).
            TrySwapPrefabAnims(buildableId, animFile);
        }

        private static void TrySwapPrefabAnims(string prefabId, KAnimFile animFile)
        {
            try
            {
                if (string.IsNullOrEmpty(prefabId) || animFile == null)
                    return;

                var prefab = Assets.GetPrefab(TagManager.Create(prefabId));
                if (prefab == null)
                    return;

                var anim = prefab.GetComponent<KBatchedAnimController>();
                if (anim == null)
                    return;

                try
                {
                    anim.SwapAnims(new[] { animFile });
                }
                catch
                {
                    try
                    {
                        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        var mi = anim.GetType().GetMethod("SwapAnims", flags, null, new[] { typeof(KAnimFile[]) }, null);
                        if (mi != null)
                        {
                            mi.Invoke(anim, new object[] { new[] { animFile } });
                        }
                        else
                        {
                            // Fallback: attempt to assign AnimFiles directly.
                            var p = anim.GetType().GetProperty("AnimFiles", flags);
                            if (p != null && p.CanWrite)
                                p.SetValue(anim, new[] { animFile }, null);
                            var f = anim.GetType().GetField("AnimFiles", flags);
                            if (f != null)
                                f.SetValue(anim, new[] { animFile });
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }

                try
                {
                    anim.Play("idle", KAnim.PlayMode.Loop);
                }
                catch
                {
                    // ignore
                }
            }
            catch
            {
                // ignored
            }
        }


        private static void TryCopyLayersFromVanillaDef(BuildingDef buildableDef, string vanillaId)
        {
            try
            {
                if (buildableDef == null || string.IsNullOrEmpty(vanillaId))
                    return;

                var vanillaDef = Assets.GetBuildingDef(vanillaId);
                if (vanillaDef == null)
                    return;

                // These fields control draw order and collision with other buildings.
                CopyField(buildableDef, vanillaDef, "ObjectLayer");
                CopyField(buildableDef, vanillaDef, "SceneLayer");
                CopyField(buildableDef, vanillaDef, "TileLayer");
                CopyField(buildableDef, vanillaDef, "ForegroundLayer");
            }
            catch
            {
                // ignored
            }
        }

        private static void CopyField(object dst, object src, string name)
        {
            if (dst == null || src == null || string.IsNullOrEmpty(name))
                return;

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var df = dst.GetType().GetField(name, flags);
                var sf = src.GetType().GetField(name, flags);
                if (df != null && sf != null)
                {
                    object v = sf.GetValue(src);
                    if (v == null)
                        return;

                    if (df.FieldType.IsAssignableFrom(v.GetType()))
                    {
                        df.SetValue(dst, v);
                        return;
                    }

                    // Basic enum/int conversion fallback.
                    if (df.FieldType.IsEnum)
                    {
                        if (v.GetType().IsEnum)
                            df.SetValue(dst, v);
                        else if (v is int i)
                            df.SetValue(dst, Enum.ToObject(df.FieldType, i));
                        return;
                    }
                    if (df.FieldType == typeof(int) && v.GetType().IsEnum)
                    {
                        df.SetValue(dst, (int)v);
                        return;
                    }

                    return;
                }

                var dp = dst.GetType().GetProperty(name, flags);
                var sp = src.GetType().GetProperty(name, flags);
                if (dp != null && sp != null && dp.CanWrite && sp.CanRead)
                {
                    object v = sp.GetValue(src, null);
                    if (v == null)
                        return;

                    if (dp.PropertyType.IsAssignableFrom(v.GetType()))
                    {
                        dp.SetValue(dst, v, null);
                        return;
                    }

                    if (dp.PropertyType.IsEnum)
                    {
                        if (v.GetType().IsEnum)
                            dp.SetValue(dst, v, null);
                        else if (v is int i)
                            dp.SetValue(dst, Enum.ToObject(dp.PropertyType, i), null);
                        return;
                    }

                    if (dp.PropertyType == typeof(int) && v.GetType().IsEnum)
                    {
                        dp.SetValue(dst, (int)v, null);
                        return;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }
        private static void SetKAnimFiles(BuildingDef def, KAnimFile[] animFiles)
        {
            if (def == null || animFiles == null)
                return;

            try
            {
                var type = def.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Property first.
                foreach (var name in new[] { "AnimFiles", "animFiles" })
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.PropertyType == typeof(KAnimFile[]) && prop.CanWrite)
                    {
                        prop.SetValue(def, animFiles, null);
                        return;
                    }
                }

                // Field fallback.
                foreach (var name in new[] { "AnimFiles", "animFiles" })
                {
                    var field = type.GetField(name, flags);
                    if (field != null && field.FieldType == typeof(KAnimFile[]))
                    {
                        field.SetValue(def, animFiles);
                        return;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        private static void SetStringField(object target, string[] memberNames, string value)
        {
            if (target == null || memberNames == null)
                return;

            try
            {
                var type = target.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // Prefer property.
                foreach (var name in memberNames)
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(target, value, null);
                        return;
                    }
                }

                // Fallback to field.
                foreach (var name in memberNames)
                {
                    var field = type.GetField(name, flags);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        field.SetValue(target, value);
                        return;
                    }
                }
            }
            catch
            {
                // best effort
            }
        }

        /// <summary>
        /// Adds all satellite recipe tags to GameTags.MaterialBuildingElements so the build material picker can resolve them.
        /// </summary>
        internal static void RegisterBuildMaterials()
        {
            try
            {
                var mats = new HashSet<string>();

                AddRecipeMaterials(mats, SatelliteTuning.GetRecipe(SatelliteIds.BUILDABLE_CRASHED));
                AddRecipeMaterials(mats, SatelliteTuning.GetRecipe(SatelliteIds.BUILDABLE_WRECKED));
                AddRecipeMaterials(mats, SatelliteTuning.GetRecipe(SatelliteIds.BUILDABLE_CRUSHED));

                foreach (var mat in mats)
                {
                    if (string.IsNullOrEmpty(mat))
                        continue;

                    var tag = TagManager.Create(mat);

                    try
                    {
                        if (!GameTags.MaterialBuildingElements.Contains(tag))
                            GameTags.MaterialBuildingElements.Add(tag);
                    }
                    catch
                    {
                        GameTags.MaterialBuildingElements.Add(tag);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS] RegisterBuildMaterials failed: " + e);
            }
        }

        private static void AddRecipeMaterials(HashSet<string> mats, SatelliteTuning.BuildRecipe recipe)
        {
            if (mats == null || recipe.Materials == null)
                return;

            foreach (var m in recipe.Materials)
                mats.Add(m);
        }

        /// <summary>
        /// Resolve the kanim name from an already-created vanilla prefab.
        /// </summary>
        internal static string TryGetKanimNameFromVanillaPrefab(string vanillaPrefabId)
        {
            try
            {
                GameObject prefab = Assets.TryGetPrefab(TagManager.Create(vanillaPrefabId));
                if (prefab == null)
                    return null;

                var kbac = prefab.GetComponent<KBatchedAnimController>();
                if (kbac == null || kbac.AnimFiles == null || kbac.AnimFiles.Length == 0 || kbac.AnimFiles[0] == null)
                    return null;

                return kbac.AnimFiles[0].name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Try to obtain the vanilla satellite kanim name from the vanilla config type's const/static string fields.
        /// This is reliable during building-def creation (when the prefab may not exist yet).
        /// </summary>
        internal static string TryGetKanimNameFromVanillaConfig(string vanillaConfigTypeName)
        {
            try
            {
                if (string.IsNullOrEmpty(vanillaConfigTypeName))
                    return null;

                Type t = FindTypeByName(vanillaConfigTypeName);
                if (t == null)
                    return null;

                // Prefer fields containing "satellite".
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(string))
                        continue;

                    string v = f.GetValue(null) as string;
                    if (string.IsNullOrEmpty(v))
                        continue;

                    if (!v.EndsWith("_kanim", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (v.IndexOf("satellite", StringComparison.OrdinalIgnoreCase) >= 0)
                        return v;
                }

                // Fallback: any *_kanim.
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(string))
                        continue;
                    string v = f.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v) && v.EndsWith("_kanim", StringComparison.OrdinalIgnoreCase))
                        return v;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] TryGetKanimNameFromVanillaConfig failed: " + e);
            }

            return null;
        }

        private static Type FindTypeByName(string typeName)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t != null)
                return t;

            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (Type tt in a.GetTypes())
                        {
                            if (tt != null && tt.Name == typeName)
                                return tt;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        /// <summary>
        /// Configure vanilla (worldgen) satellite prefabs after all prefabs exist.
        /// </summary>
        internal static void ConfigureVanillaSatellitePrefabs()
        {
            ConfigureOneVanillaPrefab(SatelliteIds.CRASHED);
            ConfigureOneVanillaPrefab(SatelliteIds.WRECKED);
            ConfigureOneVanillaPrefab(SatelliteIds.CRUSHED);
        }

        private static void ConfigureOneVanillaPrefab(string vanillaId)
        {
            GameObject prefab = Assets.TryGetPrefab(TagManager.Create(vanillaId));
            if (prefab == null)
            {
                Debug.LogWarning("[NBCS] Vanilla prefab not found: " + vanillaId);
                return;
            }

            ConfigurePrefab(prefab);
        }

        internal static void ConfigureBuildingDef(BuildingDef def)
        {
            if (def == null)
                return;

            string id = def.PrefabID;
            if (string.IsNullOrEmpty(id))
                return;

            if (!IsAnySatellite(id))
                return;

            bool isBuildable = IsBuildableSatellite(id);

            // IMPORTANT:
            // Vanilla POI satellites (PropSurfaceSatellite1/2/3) must NOT be converted into a power/logic building.
            // If we touch their BuildingDef (recipe, ports, layers, build-menu flags), sandbox spawning/worldgen can
            // crash due to missing conduit/logic networks.
            // We only configure BuildingDef for our *buildable* IDs (NBCS_*).
            if (!isBuildable)
                return;

            try
            {
                // Make buildable (shows in plan screen once added by our GeneratedBuildings patch).
                def.ShowInBuildMenu = true;
                def.Deprecated = false;

                // DLC requirement: avoid compile-time dependency on a specific BuildingDef field/property.
                // DLC1 ID constant is DlcManager.EXPANSION1_ID.
                TrySetRequiredDlc(def);


                // Build recipe (also drives deconstruction refund for player-built versions).
                ApplyBuildRecipe(def, id);

                // Overlap rules
                // - Crashed: normal building rules
                // - Wrecked/Crushed: allow overlap with most buildings by moving them to a "back-like" object layer.
                if (IsWrecked(id) || IsCrushed(id))
                    TrySetObjectLayer(def, GetBackLikeLayer());

                // Power/automation ports
                if (IsWrecked(id))
                {
                    ApplyPowerAndLogic(def, SatelliteTuning.WRECKED_POWER_WATTS);
                }
                else if (IsCrushed(id))
                {
                    ApplyPowerAndLogic(def, SatelliteTuning.CRUSHED_POWER_WATTS,
                        SatelliteTuning.CRUSHED_POWER_OFFSET,
                        SatelliteTuning.CRUSHED_LOGIC_OFFSET);
                    ApplyCrushedConduitOutputs(def);
                }

                // Overheat temperature is handled by controllers (to enforce "base works without power").
                // But we still set OverheatTemperature so the UI shows correct info.
                if (IsCrashed(id))
                    TrySetOverheatTemperature(def, SatelliteTuning.CRASHED_OVERHEAT_TEMP_K);
                else if (IsWrecked(id))
                    TrySetOverheatTemperature(def, SatelliteTuning.WRECKED_OVERHEAT_TEMP_K);
                else if (IsCrushed(id))
                    TrySetOverheatTemperature(def, SatelliteTuning.CRUSHED_OVERHEAT_TEMP_K);

                // Ensure the building is registered as a heat source in the temperature overlay.
                // Actual heat injection is handled by our controller, so this is intentionally tiny.
                // (kDTU/s == kW)
                def.SelfHeatKilowattsWhenActive = 0.1f;
            }
            catch (Exception e)
            {
                Debug.LogError("[NBCS v" + ModVersion + "] ConfigureBuildingDef failed for " + id + ": " + e);
            }
        }

        /// <summary>
        /// Some community examples call BuildingTemplates.DoPostConfigure(go) in DoPostConfigureComplete.
        /// Not all ONI builds expose the same helper methods, so we call via reflection when available.
        /// </summary>
        internal static void TryInvokeBuildingTemplatesDoPostConfigure(GameObject go)
        {
            if (go == null)
                return;

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                // Prefer BuildingTemplates.DoPostConfigureComplete(go) (present in most ONI builds),
                // fall back to BuildingTemplates.DoPostConfigure(go) if needed.
                var t = typeof(BuildingTemplates);

                MethodInfo mi =
                    t.GetMethod("DoPostConfigureComplete", flags, null, new[] { typeof(GameObject) }, null) ??
                    t.GetMethod("DoPostConfigureComplete", flags, null, new[] { typeof(GameObject), typeof(Tag) }, null) ??
                    t.GetMethod("DoPostConfigure", flags, null, new[] { typeof(GameObject) }, null);

                if (mi == null)
                    return;

                var ps = mi.GetParameters();
                if (ps.Length == 1)
                {
                    mi.Invoke(null, new object[] { go });
                }
                else if (ps.Length == 2)
                {
                    // Second parameter is typically a prefab tag.
                    mi.Invoke(null, new object[] { go, default(Tag) });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS] BuildingTemplates post-config invocation failed: " + e);
            }
        }


        internal static void ConfigurePrefab(GameObject go)
        {
            if (go == null)
                return;

            string id = GetPrefabId(go);
            if (string.IsNullOrEmpty(id))
                return;

            if (!IsAnySatellite(id))
                return;

            bool isBuildable = IsBuildableSatellite(id);

            try
            {

                // Buildable satellites should behave like POIs (Neutronium base element) so they do not melt
                // because a construction ingredient (e.g. Uranium Ore) reached its melting point.
                if (isBuildable)
                {
                    ForcePrimaryElementUnobtanium(go);
                }
                // Remove vanilla POI actions
                RemoveRummageInspect(go);

                // Ensure deconstruction refunds match our build recipes even for worldgen (not player-built) satellites.
                ApplyDeconstructDrops(go, id, isBuildable);

                // Ensure we have a RadiationEmitter with the same range/falloff/offset as the vanilla satellite.
                EnsureRadiationEmitter(go, GetVanillaIdFor(id));

                // Controllers
                if (IsCrashed(id))
                {
                    go.AddOrGet<CrashedSatelliteController>();

                    // Prevent Wrecked/Crushed (back-layer buildings) from overlapping Crashed.
                    TryAddExtraBackOccupancy(go, width: 3, height: 3);
                }
                else if (IsWrecked(id))
                {
                    // Only the buildable wrecked satellite should have power/automation/light.
                    // Applying those components to the vanilla POI can crash (no ports/networks).
                    if (isBuildable)
                    {
                        go.AddOrGet<WreckedSatelliteController>();
                        EnsureLightComponent(go);
                        EnsureLogicOperational(go);

                        // Ensure power component exists so we can reliably detect power satisfaction.
                        go.AddOrGet<EnergyConsumer>();
                    }
                }
                else if (IsCrushed(id))
                {
                    // Buildable only (same rationale as wrecked).
                    if (isBuildable)
                    {
                        go.AddOrGet<CrushedSatelliteController>();
                        go.AddOrGet<CrushedSatelliteRangeSettings>();

                        // Provide a secondary gas output port offset (primary liquid output is defined on the BuildingDef).
                        var secondaryOutput = go.AddOrGet<NewBuildableCrashedSatellite.Components.SatelliteSecondaryOutput>();
                        secondaryOutput.gasOutputOffset = SatelliteTuning.CRUSHED_GAS_OUTPUT_OFFSET;
                        EnsureLightComponent(go);
                        EnsureLogicOperational(go);

                        // Ensure power component exists so we can reliably detect power satisfaction.
                        go.AddOrGet<EnergyConsumer>();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[NBCS v" + ModVersion + "] ConfigurePrefab failed for " + id + ": " + e);
            }
        }

        private static string GetPrefabId(GameObject go)
        {
            try
            {
                var kpid = go.GetComponent<KPrefabID>();
                if (kpid != null)
                    return kpid.PrefabTag.ToString();
            }
            catch { }

            // Fallback for edge cases
            try
            {
                var n = go.name ?? "";
                return n.Replace("(Clone)", string.Empty).Trim();
            }
            catch { return null; }
        }

        private static void ApplyBuildRecipe(BuildingDef def, string id)
        {
            var recipe = SatelliteTuning.GetRecipe(id);
            if (recipe.materialTags == null || recipe.massesKg == null)
                return;

            // Most reliable: set ConstructionMaterials/ConstructionMass.
            SetStringArrayField(def, recipe.materialTags,
                "ConstructionMaterials", "constructionMaterials", "construction_materials");
            SetFloatArrayField(def, recipe.massesKg,
                "ConstructionMass", "constructionMass", "construction_mass");

            // Some UI paths use MaterialCategory; setting it does not hurt.
            try { def.MaterialCategory = recipe.materialTags; } catch { }
        }

        private static void TrySetRequiredDlc(BuildingDef def)
        {
            try
            {
                if (def == null)
                    return;

                var t = def.GetType();

                // Some ONI builds expose a single DLC id as string; others expose a string[] of required DLC ids.
                var p1 = ReflectionUtil.FindProperty(t, "DlcId", "dlcId", "DLCId", "RequiredDlcId", "requiredDlcId");
                if (p1 != null && p1.CanWrite && p1.PropertyType == typeof(string))
                {
                    p1.SetValue(def, DlcManager.EXPANSION1_ID, null);
                    return;
                }

                var f1 = ReflectionUtil.FindField(t, "DlcId", "dlcId", "DLCId", "RequiredDlcId", "requiredDlcId");
                if (f1 != null && f1.FieldType == typeof(string))
                {
                    f1.SetValue(def, DlcManager.EXPANSION1_ID);
                    return;
                }

                var p2 = ReflectionUtil.FindProperty(t, "RequiredDlcIds", "requiredDlcIds", "DlcIds", "dlcIds");
                if (p2 != null && p2.CanWrite && p2.PropertyType == typeof(string[]))
                {
                    p2.SetValue(def, new[] { DlcManager.EXPANSION1_ID }, null);
                    return;
                }

                var f2 = ReflectionUtil.FindField(t, "RequiredDlcIds", "requiredDlcIds", "DlcIds", "dlcIds");
                if (f2 != null && f2.FieldType == typeof(string[]))
                {
                    f2.SetValue(def, new[] { DlcManager.EXPANSION1_ID });
                }
            }
            catch
            {
                // Ignore: DLC gating is also enforced via mod_info.yaml (requiredDlcIds).
            }
        }

        private static void TrySetOverheatTemperature(BuildingDef def, float tempK)
        {
            try { def.OverheatTemperature = tempK; } catch { }
        }

        private static void ApplyPowerAndLogic(BuildingDef def, float watts)
        {
            // Default offsets:
            // Center is defined in the user's spec (red dot). We approximate it as bottom-middle tile:
            // centerX = floor((width-1)/2), centerY = 0.
            int centerX = Math.Max(0, (def.WidthInCells - 1) / 2);
            int centerY = 0;

            ApplyPowerAndLogic(def, watts,
                new CellOffset(centerX - 1, centerY),
                new CellOffset(centerX + 1, centerY));
        }

        private static void ApplyPowerAndLogic(BuildingDef def, float watts, CellOffset powerOffset, CellOffset logicOffset)
        {
            try
            {
                def.RequiresPowerInput = true;
                def.PowerInputOffset = powerOffset;
                def.EnergyConsumptionWhenActive = watts;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] ApplyPowerAndLogic(power) failed for " + def.PrefabID + ": " + e);
            }

            try
            {
                def.LogicInputPorts = LogicOperationalController.CreateSingleInputPortList(logicOffset);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] ApplyPowerAndLogic(logic) failed for " + def.PrefabID + ": " + e);
            }
        }

private static void ApplyCrushedConduitOutputs(BuildingDef def)
{
    if (def == null) return;

    // Primary output: polluted water
    def.OutputConduitType = ConduitType.Liquid;
    def.UtilityOutputOffset = SatelliteTuning.CRUSHED_LIQUID_OUTPUT_OFFSET;

    // Secondary output: natural gas (best-effort across ONI builds)
    TrySetMemberValue(def, "SecondaryOutputConduitType", ConduitType.Gas);
    TrySetMemberValue(def, "secondaryOutputConduitType", ConduitType.Gas);

    TrySetMemberValue(def, "SecondaryUtilityOutputOffset", SatelliteTuning.CRUSHED_GAS_OUTPUT_OFFSET);
    TrySetMemberValue(def, "secondaryUtilityOutputOffset", SatelliteTuning.CRUSHED_GAS_OUTPUT_OFFSET);
    TrySetMemberValue(def, "SecondaryOutputOffset", SatelliteTuning.CRUSHED_GAS_OUTPUT_OFFSET);
    TrySetMemberValue(def, "secondaryOutputOffset", SatelliteTuning.CRUSHED_GAS_OUTPUT_OFFSET);
}

        // === Prefab helpers ===

        private static void EnsureLightComponent(GameObject go)
        {
            try
            {
                var l = go.AddOrGet<Light2D>();
                // Configure like vanilla light sources (range/shape/overlay). See typical Klei usages
                // in existing building/artifact configs.
                l.overlayColour = Color.white;
                l.Color = Color.white;
                l.Range = 16f;
                l.Angle = 0f;
                l.Direction = Vector2.up;
                l.Offset = Vector2.zero;
                l.shape = LightShape.Circle;
                l.drawOverlay = true;
                l.Lux = 0;
                l.enabled = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] EnsureLightComponent failed: " + e);
            }
        }

        private static void EnsureLogicOperational(GameObject go)
        {
            try
            {
                go.AddOrGet<LogicOperationalController>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] EnsureLogicOperational failed: " + e);
            }
        }

        private static void EnsureRadiationEmitter(GameObject go, string vanillaId)
        {
            try
            {
                var dst = go.GetComponent<RadiationEmitter>();
                if (dst == null)
                    dst = go.AddOrGet<RadiationEmitter>();

                // Copy range/falloff/emission offset from vanilla.
                GameObject vanilla = Assets.TryGetPrefab(TagManager.Create(vanillaId));
                var src = vanilla != null ? vanilla.GetComponent<RadiationEmitter>() : null;
                if (src != null)
                {
                    CopyValueTypeMembers(src, dst);
                }

                // Normalize to a constant emitter (no built-in pulsing), as required.
                NormalizeEmitterForBuildable(dst);

                // Ensure there is only one active RadiationEmitter on the prefab.
                // Some POI prefabs can have emitters on child objects; leave only the root emitter
                // so our controller can reliably toggle radiation on/off.
                try
                {
                    foreach (var other in go.GetComponentsInChildren<RadiationEmitter>(true))
                    {
                        if (other == null || other == dst)
                            continue;
                        other.emitRads = 0f;
                        other.enabled = false;
                    }
                }
                catch
                {
                    // ignore
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] EnsureRadiationEmitter failed: " + e);
            }
        }

        private static void NormalizeEmitterForBuildable(RadiationEmitter emitter)
        {
            if (emitter == null)
                return;

            // Best-effort normalization via reflection (RadiationEmitter internals can vary by build).
            try
            {
                var t = emitter.GetType();

                // Set emission type to Constant if available.
                var emitTypeField = t.GetField("emitType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                 ?? t.GetField("emissionType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var emitTypeProp = t.GetProperty("emitType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? t.GetProperty("EmissionType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                System.Action<Type, System.Action<object>> setConstant = (enumType, assign) =>
                {
                    if (enumType == null || !enumType.IsEnum)
                        return;

                    object constant = null;
                    foreach (var v in Enum.GetValues(enumType))
                    {
                        string name = v.ToString();
                        if (string.Equals(name, "Constant", StringComparison.OrdinalIgnoreCase) ||
                            name.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            constant = v;
                            break;
                        }
                    }

                    if (constant != null)
                        assign(constant);
                };

                if (emitTypeField != null && emitTypeField.FieldType.IsEnum)
                {
                    setConstant(emitTypeField.FieldType, v => emitTypeField.SetValue(emitter, v));
                }
                else if (emitTypeProp != null && emitTypeProp.CanWrite && emitTypeProp.PropertyType.IsEnum)
                {
                    setConstant(emitTypeProp.PropertyType, v => emitTypeProp.SetValue(emitter, v, null));
                }

                // Zero out common pulse-related fields if present.
                foreach (var pulseName in new[] { "pulseFrequency", "PulseFrequency", "pulsePeriod", "PulsePeriod", "pulseDuration", "PulseDuration" })
                {
                    var f = t.GetField(pulseName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null)
                    {
                        if (f.FieldType == typeof(float)) f.SetValue(emitter, 0f);
                        else if (f.FieldType == typeof(int)) f.SetValue(emitter, 0);
                    }

                    var p = t.GetProperty(pulseName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanWrite)
                    {
                        if (p.PropertyType == typeof(float)) p.SetValue(emitter, 0f, null);
                        else if (p.PropertyType == typeof(int)) p.SetValue(emitter, 0, null);
                    }
                }

                // Ensure non-zero radius to avoid “invisible” emitters in some builds.
                if (emitter.emitRadiusX <= 0) emitter.emitRadiusX = 1;
                if (emitter.emitRadiusY <= 0) emitter.emitRadiusY = 1;
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// RadiationEmitter member names have changed across ONI versions.
        /// To avoid hard-coding uncertain names (and breaking compilation), copy all simple
        /// (value-type) fields/properties from the vanilla emitter to the target.
        /// Controllers later overwrite output (emitRads) as needed.
        /// </summary>
        private static void CopyValueTypeMembers(object src, object dst)
        {
            if (src == null || dst == null) return;

            var t = src.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                foreach (var f in t.GetFields(flags))
                {
                    if (f == null) continue;
                    if (f.IsStatic || f.IsInitOnly || f.IsLiteral) continue;
                    if (!(f.FieldType.IsValueType || f.FieldType == typeof(string))) continue;

                    try
                    {
                        var v = f.GetValue(src);
                        f.SetValue(dst, v);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                foreach (var p in t.GetProperties(flags))
                {
                    if (p == null) continue;
                    if (!p.CanRead || !p.CanWrite) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    if (!(p.PropertyType.IsValueType || p.PropertyType == typeof(string))) continue;

                    try
                    {
                        var v = p.GetValue(src, null);
                        p.SetValue(dst, v, null);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static void RemoveRummageInspect(GameObject go)
        {
            try
            {
                // Some POI-side actions are not Workables; they can live on child GameObjects.
                // Scan the full hierarchy.
                var comps = go.GetComponentsInChildren<Component>(true);
                foreach (var c in comps)
                {
                    if (c == null) continue;

                    // Never remove core components.
                    if (c is KPrefabID || c is KSelectable || c is PrimaryElement || c is Building ||
                        c is Deconstructable || c is Demolishable)
                        continue;

                    string n = c.GetType().Name;
                    if (n.IndexOf("Rummage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Inspect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Investig", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("ArtifactPOI", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        UnityEngine.Object.Destroy(c);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] RemoveRummageInspect failed: " + e);
            }
        }

        private static void ApplyDeconstructDrops(GameObject prefab, string id, bool isBuildable)
        {
            try
            {
                var recipe = SatelliteTuning.GetRecipe(id);

                var decon = prefab.GetComponent<Deconstructable>();
                if (decon == null)
                {
                    // Vanilla POI prefabs may not be safe to retrofit with Deconstructable.
                    // Only create one for our buildable versions.
                    if (!isBuildable)
                        return;

                    decon = prefab.AddOrGet<Deconstructable>();
                }

                Tag[] elements = new Tag[recipe.materialTags.Length];
                for (int i = 0; i < elements.Length; i++)
                    elements[i] = TagManager.Create(recipe.materialTags[i]);

                SetTagArrayField(decon, elements, "constructionElements", "ConstructionElements");
                SetFloatArrayField(decon, recipe.massesKg, "constructionMass", "ConstructionMass", "Mass");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] ApplyDeconstructDrops failed for " + id + ": " + e);
            }
        }

        // === Overlap handling ===

        private static ObjectLayer GetBackLikeLayer()
        {
            if (cachedBackLayer.HasValue)
                return cachedBackLayer.Value;

            cachedBackLayer = FindBackLikeLayer();
            return cachedBackLayer.Value;
        }

        private static ObjectLayer FindBackLikeLayer()
        {
            // Prefer a layer that is "behind" buildings. Names differ across builds.
            string[] preferred = new string[] { "BuildingBack", "Backwall", "BackwallTile" };
            var names = Enum.GetNames(typeof(ObjectLayer));

            foreach (var p in preferred)
            {
                foreach (var n in names)
                {
                    if (string.Equals(n, p, StringComparison.OrdinalIgnoreCase))
                        return (ObjectLayer)Enum.Parse(typeof(ObjectLayer), n);
                }
            }

            foreach (var n in names)
            {
                if (n.IndexOf("Back", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (ObjectLayer)Enum.Parse(typeof(ObjectLayer), n);
            }

            return ObjectLayer.Building;
        }

        private static void TrySetObjectLayer(BuildingDef def, ObjectLayer layer)
        {
            try
            {
                // Newer builds expose a writable property.
                var pi = ReflectionUtil.FindProperty(def.GetType(), "ObjectLayer", "objectLayer");
                if (pi != null && pi.CanWrite)
                {
                    pi.SetValue(def, layer, null);
                    return;
                }

                var fi = ReflectionUtil.FindField(def.GetType(), "ObjectLayer", "objectLayer");
                if (fi != null)
                {
                    fi.SetValue(def, layer);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] TrySetObjectLayer failed: " + e);
            }
        }

        private static void TryAddExtraBackOccupancy(GameObject prefab, int width, int height)
        {
            try
            {
                ObjectLayer backLayer = GetBackLikeLayer();
                var occ = prefab.AddComponent<OccupyArea>();

                var offsets = new CellOffset[width * height];
                int idx = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                        offsets[idx++] = new CellOffset(x, y);
                }

                SetCellOffsetArrayField(occ, offsets,
                    "occupiedCellOffsets", "OccupiedCellOffsets", "cellOffsets", "CellOffsets", "offsets", "Offsets");

                SetEnumField(occ, backLayer, "objectLayer", "ObjectLayer");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] TryAddExtraBackOccupancy failed: " + e);
            }
        }

        // === Multi-output ports (Liquid + Gas) ===

        private static bool TrySetMultiOutputPorts(BuildingDef def, CellOffset liquidOffset, CellOffset gasOffset)
        {
            try
            {
                // ConduitPortInfo exists in recent ONI versions.
                // Use a broad scan first (some builds move the type namespace).
                Type portInfoType = FindTypeByName("ConduitPortInfo") ?? AccessTools.TypeByName("ConduitPortInfo");
                if (portInfoType == null) return false;

                object liquidPort = CreateConduitPortInfo(portInfoType, ConduitType.Liquid, liquidOffset, isInput: false);
                object gasPort = CreateConduitPortInfo(portInfoType, ConduitType.Gas, gasOffset, isInput: false);

                var fi = ReflectionUtil.FindField(typeof(BuildingDef), "UtilityOutputPorts", "utilityOutputPorts", "UtilityOutputPortInfos");
                if (fi != null)
                {
                    object value = BuildPortCollection(fi.FieldType, portInfoType, liquidPort, gasPort);
                    fi.SetValue(def, value);
                    return true;
                }

                var pi = ReflectionUtil.FindProperty(typeof(BuildingDef), "UtilityOutputPorts", "utilityOutputPorts", "UtilityOutputPortInfos");
                if (pi != null && pi.CanWrite)
                {
                    object value = BuildPortCollection(pi.PropertyType, portInfoType, liquidPort, gasPort);
                    pi.SetValue(def, value, null);
                    return true;
                }

                // Last-resort: scan all members for a ConduitPortInfo collection.
                foreach (var f in typeof(BuildingDef).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object value = BuildPortCollection(f.FieldType, portInfoType, liquidPort, gasPort);
                    if (value != null && f.FieldType.IsAssignableFrom(value.GetType()))
                    {
                        f.SetValue(def, value);
                        return true;
                    }
                }

                foreach (var p in typeof(BuildingDef).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!p.CanWrite) continue;
                    object value = BuildPortCollection(p.PropertyType, portInfoType, liquidPort, gasPort);
                    if (value != null && p.PropertyType.IsAssignableFrom(value.GetType()))
                    {
                        p.SetValue(def, value, null);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NBCS v" + ModVersion + "] TrySetMultiOutputPorts failed: " + e);
            }

            return false;
        }

        private static object BuildPortCollection(Type collectionType, Type portInfoType, object p1, object p2)
        {
            if (collectionType.IsArray)
            {
                Array arr = Array.CreateInstance(portInfoType, 2);
                arr.SetValue(p1, 0);
                arr.SetValue(p2, 1);
                return arr;
            }

            // Prefer a List<ConduitPortInfo>, which is assignable to most interfaces (IList/ICollection/etc.).
            try
            {
                Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(portInfoType);
                object list = Activator.CreateInstance(listType);
                var add = listType.GetMethod("Add");
                add?.Invoke(list, new object[] { p1 });
                add?.Invoke(list, new object[] { p2 });

                if (collectionType.IsAssignableFrom(listType))
                    return list;
            }
            catch
            {
                // ignore
            }

            // If the target is a concrete generic type (not interface), attempt to instantiate it.
            try
            {
                if (collectionType.IsGenericType)
                {
                    var gen = collectionType.GetGenericArguments();
                    if (gen.Length == 1 && gen[0] == portInfoType && !collectionType.IsInterface)
                    {
                        object list = Activator.CreateInstance(collectionType);
                        var add = collectionType.GetMethod("Add");
                        add?.Invoke(list, new object[] { p1 });
                        add?.Invoke(list, new object[] { p2 });
                        return list;
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Fallback to array.
            Array fallback = Array.CreateInstance(portInfoType, 2);
            fallback.SetValue(p1, 0);
            fallback.SetValue(p2, 1);
            return fallback;
        }

        private static object CreateConduitPortInfo(Type portInfoType, ConduitType conduitType, CellOffset offset, bool isInput)
        {
            try
            {
                var ctor = portInfoType.GetConstructor(new Type[] { typeof(ConduitType), typeof(CellOffset), typeof(bool) });
                if (ctor != null)
                    return ctor.Invoke(new object[] { conduitType, offset, isInput });
            }
            catch { }

            try
            {
                var ctor = portInfoType.GetConstructor(new Type[] { typeof(ConduitType), typeof(CellOffset) });
                if (ctor != null)
                {
                    object obj = ctor.Invoke(new object[] { conduitType, offset });
                    SetBoolField(obj, isInput, "isInput", "IsInput");
                    return obj;
                }
            }
            catch { }

            object port = Activator.CreateInstance(portInfoType);
            SetEnumField(port, conduitType, "conduitType", "ConduitType", "type");
            SetStructField(port, offset, "offset", "cellOffset", "CellOffset");
            SetBoolField(port, isInput, "isInput", "IsInput");
            return port;
        }

        // === Reflection helpers ===

        private static void SetEnumField(object obj, object value, params string[] names)
        {
            if (obj == null) return;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null) { fi.SetValue(obj, value); return; }
                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite) { pi.SetValue(obj, value, null); return; }
            }
        }

        private static void SetStructField(object obj, object value, params string[] names)
        {
            if (obj == null) return;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null) { fi.SetValue(obj, value); return; }
                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite) { pi.SetValue(obj, value, null); return; }
            }
        }

        private static void SetBoolField(object obj, bool value, params string[] names)
        {
            if (obj == null) return;
            var t = obj.GetType();
            foreach (var n in names)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null) { fi.SetValue(obj, value); return; }
                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite) { pi.SetValue(obj, value, null); return; }
            }
        }

        private static void SetFloatArrayField(object target, float[] values, params string[] fieldNames)
        {
            if (target == null) return;
            var t = target.GetType();
            foreach (var n in fieldNames)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null && fi.FieldType == typeof(float[])) { fi.SetValue(target, values); return; }

                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(float[])) { pi.SetValue(target, values, null); return; }
            }
        }

        private static void SetStringArrayField(object target, string[] values, params string[] fieldNames)
        {
            if (target == null) return;
            var t = target.GetType();
            foreach (var n in fieldNames)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null && fi.FieldType == typeof(string[])) { fi.SetValue(target, values); return; }

                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(string[])) { pi.SetValue(target, values, null); return; }
            }
        }



private static void TrySetMemberValue(object target, string memberName, object value)
{
    if (target == null) return;
    if (string.IsNullOrEmpty(memberName)) return;

    var t = target.GetType();

    // Properties
    try
    {
        var p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite)
        {
            object converted = ConvertIfNeeded(value, p.PropertyType);
            if (converted != null)
            {
                p.SetValue(target, converted, null);
                return;
            }
        }
    }
    catch
    {
        // ignore
    }

    // Fields
    try
    {
        var f = t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null)
        {
            object converted = ConvertIfNeeded(value, f.FieldType);
            if (converted != null)
            {
                f.SetValue(target, converted);
            }
        }
    }
    catch
    {
        // ignore
    }
}

private static object ConvertIfNeeded(object value, Type targetType)
{
    if (value == null || targetType == null) return null;

    var valueType = value.GetType();
    if (targetType.IsAssignableFrom(valueType))
        return value;

    try
    {
        // enum -> enum (by underlying int)
        if (targetType.IsEnum)
        {
            if (valueType.IsEnum)
            {
                int intVal = (int)value;
                return Enum.ToObject(targetType, intVal);
            }

            if (valueType == typeof(int))
                return Enum.ToObject(targetType, (int)value);
        }

        // enum -> int
        if (targetType == typeof(int) && valueType.IsEnum)
            return (int)value;
    }
    catch
    {
        // ignore
    }

    return null;
}
        internal static void ForcePrimaryElementUnobtanium(GameObject go)
        {
            if (go == null) return;

            var primaryElements = go.GetComponentsInChildren<PrimaryElement>(true);
            if (primaryElements == null || primaryElements.Length == 0) return;

            foreach (var pe in primaryElements)
            {
                if (pe == null) continue;

                // 1) Try to use SetElement(...) if the game build provides it
                try
                {
                    var methods = pe.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var m in methods)
                    {
                        if (m == null) continue;
                        if (m.Name != "SetElement") continue;

                        var ps = m.GetParameters();
                        if (ps == null || ps.Length != 1) continue;

                        var paramType = ps[0].ParameterType;

                        // Most common case: SetElement(SimHashes)
                        if (paramType == typeof(SimHashes))
                        {
                            m.Invoke(pe, new object[] { SimHashes.Unobtanium });
                            break;
                        }

                        // Some builds use an enum for element IDs
                        if (paramType.IsEnum)
                        {
                            object enumVal = Enum.ToObject(paramType, (int)SimHashes.Unobtanium);
                            m.Invoke(pe, new object[] { enumVal });
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore
                }

                // 2) Fallback: try to set element id members directly
                try
                {
                    SetElementIdValue(pe, "ElementID", SimHashes.Unobtanium, BindingFlags.Instance | BindingFlags.Public);
                    SetElementIdValue(pe, "elementID", SimHashes.Unobtanium, BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch
                {
                    // ignore
                }
            }
        }

        private static void SetElementIdValue(PrimaryElement pe, string memberName, SimHashes hash, BindingFlags flags)
        {
            if (pe == null || string.IsNullOrEmpty(memberName))
                return;

            var t = typeof(PrimaryElement);

            var prop = t.GetProperty(memberName, flags);
            if (prop != null && prop.CanWrite)
            {
                TrySetNumericOrEnum(prop.PropertyType, v => prop.SetValue(pe, v, null), hash);
                return;
            }

            var field = t.GetField(memberName, flags);
            if (field != null)
            {
                TrySetNumericOrEnum(field.FieldType, v => field.SetValue(pe, v), hash);
            }
        }

        private static void TrySetNumericOrEnum(Type memberType, Action<object> setter, SimHashes hash)
        {
            if (memberType == null || setter == null)
                return;

            try
            {
                if (memberType == typeof(SimHashes))
                {
                    setter(hash);
                    return;
                }

                int i = (int)hash;
                if (memberType == typeof(int)) { setter(i); return; }
                if (memberType == typeof(short)) { setter((short)i); return; }
                if (memberType == typeof(ushort)) { setter((ushort)i); return; }
                if (memberType == typeof(byte)) { setter((byte)i); return; }
                if (memberType == typeof(sbyte)) { setter((sbyte)i); return; }

                // Some builds store element ids as an enum other than SimHashes.
                if (memberType.IsEnum)
                {
                    var boxed = Enum.ToObject(memberType, i);
                    setter(boxed);
                }
            }
            catch
            {
                // ignored
            }
        }

        private static void RemoveOverheatable(GameObject go)
        {
            try
            {
                var over = go.GetComponent<Overheatable>();
                if (over != null)
                    UnityEngine.Object.DestroyImmediate(over);
            }
            catch
            {
                // ignored
            }
        }
        private static void SetTagArrayField(object target, Tag[] values, params string[] fieldNames)
        {
            if (target == null) return;
            var t = target.GetType();
            foreach (var n in fieldNames)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null && fi.FieldType == typeof(Tag[])) { fi.SetValue(target, values); return; }

                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(Tag[])) { pi.SetValue(target, values, null); return; }
            }
        }

        private static void SetCellOffsetArrayField(object target, CellOffset[] values, params string[] fieldNames)
        {
            if (target == null) return;
            var t = target.GetType();
            foreach (var n in fieldNames)
            {
                var fi = ReflectionUtil.FindField(t, n);
                if (fi != null && fi.FieldType == typeof(CellOffset[])) { fi.SetValue(target, values); return; }

                var pi = ReflectionUtil.FindProperty(t, n);
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(CellOffset[])) { pi.SetValue(target, values, null); return; }
            }
        }
    }
}
