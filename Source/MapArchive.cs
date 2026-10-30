using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>
    /// 原版 Map.ExposeData 的离线承载。Game.DeinitAndRemoveMap 会转移船员、清空图内容，
    /// 因而归档仅移出 Game.maps 并释放运行时图资源；恢复交给原版 Map.FinalizeLoading。
    /// 整段只能在帧边界、Scribe 空闲时执行。
    /// </summary>
    public static class MapArchive
    {
        private static readonly FieldInfo GameMaps = AccessTools.Field(typeof(Game), "maps");
        private static readonly FieldInfo LoadedDirectory = AccessTools.Field(typeof(CrossRefHandler),
            "loadedObjectDirectory");

        public static bool TryCapture(Map map, out string data)
        {
            data = null;
            if (map == null || map.Disposed || Scribe.mode != LoadSaveMode.Inactive) return false;
            using var operation = new ScribeOperation();
            string path = Path.GetTempFileName();
            try
            {
                var things = new List<Thing>();
                ThingOwnerUtility.GetAllThingsRecursively(map, things);
                ValidateCaptureIds(things);
                Scribe.saver.InitSaving(path, "archivedMap");
                Map current = map;
                Scribe_Deep.Look(ref current, "map");
                RegisterSavingExternalReferences(ExternalReferences(map), things.Cast<ILoadReferenceable>().Append(map));
                Scribe.saver.FinalizeSaving();
                byte[] xml = File.ReadAllBytes(path);
                var document = new XmlDocument();
                document.Load(path);
                XmlNode mapNode = document.DocumentElement?["map"];
                if (mapNode?["mapInfo"] == null || mapNode["things"] == null)
                    throw new InvalidDataException("Serialized map core nodes are missing.");
                var serializedIds = SavedIds(document);
                foreach (Thing thing in map.listerThings.AllThings)
                    if (thing.def.isSaveable && !thing.IsSaveCompressible() && !serializedIds.Contains(thing.ThingID))
                        throw new InvalidDataException($"Map serialization lost {thing.ThingID}.");
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
                    gzip.Write(xml, 0, xml.Length);
                data = Convert.ToBase64String(output.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus.Maps] Full map archive capture failed ({map?.uniqueID.ToString()}): {ex}");
                Scribe.ForceStop();
                return false;
            }
            finally { File.Delete(path); }
        }

        public static bool CanUnloadLosslessly(Map map)
        {
            if (map == null || map.Disposed) return false;
            if (MapRuntime.Current?.Combat.IsInUse(map) == true) return false;
            foreach (PocketMapParent parent in Find.World.pocketMaps)
                if (parent.sourceMap == map && parent.HasMap) return false;
            if (Find.Anomaly?.monolith?.Map == map) return false;
            foreach (QueuedIncident queued in Find.Storyteller.incidentQueue)
                if (queued.FiringIncident?.parms?.target == map) return false;
            foreach (Quest quest in Find.QuestManager.QuestsListForReading)
                if (!quest.Historical && (quest.QuestLookTargets.Any(target => target.Map == map)
                    || quest.QuestSelectTargets.Any(target => target.Map == map))) return false;
            return true;
        }

        public static bool TryUnload(Map map, bool preserveExternalEvents = true)
        {
            if (map == null || map.Disposed || GameMaps?.GetValue(Current.Game) is not List<Map> maps)
                return false;
            int index = maps.IndexOf(map);
            if (index < 0 || map == Find.CurrentMap) return false;
            // 这些全局事件不在 Map.ExposeData 内；先留图等待事件结束，避免归档时丢失。
            if (preserveExternalEvents && !CanUnloadLosslessly(map)) return false;
            Map focused = Current.Game.CurrentMap;
            var indexed = new List<(Map map, List<Thing> things, List<District> districts, List<Region> regions)>();
            try
            {
                // 所有可失败的枚举先完成；预检失败时不碰原图、tick 注册或地图索引。
                for (int i = index; i < maps.Count; i++)
                {
                    Map current = maps[i];
                    indexed.Add((current, current.spawnedThings.ToList(),
                        current.regionGrid.AllRooms.SelectMany(r => r.Districts).Distinct().ToList(),
                        current.regionGrid.AllRegions_NoRebuild_InvalidAllowed.ToList()));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus.Maps] Full map archive unload preflight failed ({map?.uniqueID.ToString()}): {ex}");
                return false;
            }
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception ex) { Log.Error($"[RimExodus.Maps] Archived map resource cleanup failed ({map?.uniqueID.ToString()}): {ex}"); }
            }
            // 从这里开始完成卸载；资源通知异常只报错，不返回一张已被部分拆掉的“原图”。
            // 不调用 MapDeiniter 或 Thing.Notify_MyMapRemoved，不交接或销毁船员。
            Cleanup(() => Find.Archive.Notify_MapRemoved(map));
            Cleanup(() => Find.TickManager.RemoveAllFromMap(map));
            Cleanup(() => Find.SoundRoot.sustainerManager.EndAllInMap(map));
            Cleanup(() => map.weatherManager.EndAllSustainers());
            foreach (Thing thing in indexed[0].things)
            {
                if (thing.def.receivesSignals) Cleanup(() => Find.SignalManager.DeregisterReceiver(thing));
                thing.ForceSetStateToUnspawned();
            }
            foreach (District district in indexed[0].districts) district.Notify_MyMapRemoved();
            foreach (Region region in indexed[0].regions) region.Notify_MyMapRemoved();
            for (int i = 1; i < indexed.Count; i++)
            {
                foreach (Thing thing in indexed[i].things) thing.DecrementMapIndex();
                foreach (District district in indexed[i].districts) district.DecrementMapIndex();
                foreach (Region region in indexed[i].regions) region.DecrementMapIndex();
            }
            maps.RemoveAt(index);
            Current.Game.currentMapIndex = focused == null ? (sbyte)-1 : (sbyte)maps.IndexOf(focused);
            Cleanup(() => MapComponentUtility.MapRemoved(map));
            Cleanup(() => Find.Storyteller.incidentQueue.Notify_MapRemoved(map));
            Cleanup(() => Find.Scenario.MapRemoved(map));
            Cleanup(map.Dispose);
            Cleanup(() => Find.ColonistBar.MarkColonistsDirty());
            MapRuntime.Current?.Forget(map);
            return true;
        }

        public static bool TryRestore(string data, int expectedMapId,
            MapParent expectedParent, out Map map)
        {
            map = null;
            if (data.NullOrEmpty() || Scribe.mode != LoadSaveMode.Inactive) return false;
            if (expectedParent == null || Find.Maps.Any(m => m.uniqueID == expectedMapId || m.Parent == expectedParent)) return false;
            string path = Path.GetTempFileName();
            Map loaded = null;
            bool added = false;
            using var loading = new ScribeOperation(loading: true);
            try
            {
                byte[] compressed = Convert.FromBase64String(data);
                using (var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress))
                using (var output = File.Create(path)) input.CopyTo(output);
                var document = new XmlDocument();
                document.Load(path);
                Scribe.loader.InitLoading(path);
                Scribe_Deep.Look(ref loaded, "map");
                if (loaded == null) throw new InvalidDataException("Archive contains no map.");
                // Scribe catches nested constructor errors and can return a partial Pawn.
                // Verify saved needs before publishing the map or discarding its archive.
                ValidateRestoredPawnNeeds(document, Scribe.loader.crossRefs.crossReferencingExposables.OfType<Pawn>());
                var loadedIds = new HashSet<string>(Scribe.loader.crossRefs.crossReferencingExposables
                    .OfType<ILoadReferenceable>().Where(CanRegisterExternalReference).Select(t => t.GetUniqueLoadID()));
                RegisterExternalReferences(loaded, loadedIds);
                Current.Game.AddMap(loaded);
                added = true;
                Scribe.loader.FinalizeLoading();
                if (loaded.uniqueID != expectedMapId || loaded.Parent != expectedParent)
                    throw new InvalidDataException("Restored map identity or pocket parent differs from archive.");
                loaded.FinalizeLoading();
                var restoredIds = new HashSet<string>(loaded.listerThings.AllThings.Select(t => t.ThingID));
                if (SavedIds(document).Any(id => !restoredIds.Contains(id)))
                    throw new InvalidDataException("Map loading failed to spawn one or more persistent Things.");
                map = loaded;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[RimExodus.Maps] Full map archive restore failed: {ex}");
                Scribe.ForceStop();
                if (added)
                {
                    // FinalizeLoading 可能已生成部分 Thing/Region；复用唯一卸图入口清理索引。
                    if (TryUnload(loaded, preserveExternalEvents: false))
                    {
                        try { MapRuntime.Current?.Forget(loaded); }
                        catch (Exception cleanupError)
                        { Log.Error($"[RimExodus.Maps] Partial map restore runtime cleanup failed: {cleanupError}"); }
                        finally { loaded.spawnedThings.Clear(); }
                    }
                    else
                        Log.Error($"[RimExodus.Maps] Failed to remove partially restored map {loaded?.uniqueID.ToString()}");
                }
                else loaded?.Dispose();
                return false;
            }
            finally { File.Delete(path); }
        }

        internal readonly struct MapLoadingScope : IDisposable
        {
            private readonly ProgramState previous;
            // Same loading context as Game.LoadGame; synchronous, never spans frames.
            internal MapLoadingScope(ProgramState previous)
            { this.previous = previous; Current.ProgramState = ProgramState.MapInitializing; }
            public void Dispose() => Current.ProgramState = previous;
        }

        internal static void ValidateCaptureIds(IEnumerable<Thing> things)
        {
            var seen = new Dictionary<int, Thing>();
            foreach (Thing thing in things)
            {
                if (thing == null) continue;
                if (thing.def == null) throw new InvalidDataException("Archive Thing has no def.");
                if (!thing.def.isSaveable) continue;
                if (!thing.def.HasThingIDNumber || thing.thingIDNumber < 0)
                    throw new InvalidDataException($"Cannot archive persistent {thing.def.defName} with Thing ID {thing.thingIDNumber}.");
                if (seen.TryGetValue(thing.thingIDNumber, out Thing previous) && !ReferenceEquals(previous, thing))
                    throw new InvalidDataException($"Cannot archive duplicate Thing ID {thing.thingIDNumber}: {previous.def.defName}, {thing.def.defName}.");
                seen[thing.thingIDNumber] = thing;
            }
        }

        internal static void ValidateRestoredPawnNeeds(XmlDocument document, IEnumerable<Pawn> loadedPawns)
        {
            var pawns = new Dictionary<string, Pawn>();
            foreach (Pawn pawn in loadedPawns) pawns[pawn.ThingID] = pawn;
            // Pawn.ExposeData writes id + needs/needs; use the existing XML, no extra saved state.
            foreach (XmlNode node in document.SelectNodes("//*[id and needs/needs]"))
            {
                string id = node["id"].InnerText;
                if (!pawns.TryGetValue(id, out Pawn pawn) || pawn.needs == null)
                    throw new InvalidDataException($"Archived Pawn {id} or its needs tracker did not load.");
                foreach (XmlNode need in node.SelectNodes("needs/needs/li[def]"))
                {
                    string defName = need["def"].InnerText;
                    if (!pawn.needs.AllNeeds.Any(n => n?.def?.defName == defName))
                        throw new InvalidDataException($"Archived Pawn {id} lost saved need {defName} during loading.");
                }
            }
        }

        internal static void RegisterExternalReferences(Map restoringMap, ISet<string> reservedIds = null)
        {
            if (LoadedDirectory?.GetValue(Scribe.loader.crossRefs) is not LoadedObjectDirectory directory)
                throw new MissingFieldException("CrossRefHandler.loadedObjectDirectory");
            // Deduplicate the same instance reached through multiple holders. Distinct
            // persistent objects with colliding IDs must still reach vanilla's diagnostics.
            var seen = new HashSet<ILoadReferenceable>(ReferenceComparer.Instance);
            void Register(ILoadReferenceable value)
            {
                if (!CanRegisterExternalReference(value)) return;
                if (reservedIds?.Contains(value.GetUniqueLoadID()) == true)
                    throw new InvalidDataException($"Cannot restore an identity still present in the game: {value.GetUniqueLoadID()}.");
                if (seen.Add(value)) directory.RegisterLoaded(value);
            }
            foreach (ILoadReferenceable value in ExternalReferences(restoringMap)) Register(value);
        }

        // A map archive is only one part of the game save. Acknowledge the exact
        // external objects that the restore path can resolve; unknown IDs still warn.
        internal static void RegisterSavingExternalReferences(IEnumerable<ILoadReferenceable> external,
            IEnumerable<ILoadReferenceable> saved)
        {
            if (!Prefs.DevMode) return;
            var seen = new HashSet<ILoadReferenceable>(saved, ReferenceComparer.Instance);
            foreach (ILoadReferenceable value in external)
                if (CanRegisterExternalReference(value) && seen.Add(value))
                    Scribe.saver.loadIDsErrorsChecker.RegisterDeepSaved(value, "archiveExternal");
        }

        internal static IEnumerable<ILoadReferenceable> ExternalReferences(Map restoringMap)
        {
            foreach (Faction faction in Find.FactionManager.AllFactionsListForReading) yield return faction;
            if (ModsConfig.IdeologyActive && Find.IdeoManager != null)
                foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading) yield return ideo;
            foreach (Quest quest in Find.QuestManager.QuestsListForReading) yield return quest;
            foreach (WorldObject obj in Find.WorldObjects.AllWorldObjects) yield return obj;
            foreach (PocketMapParent parent in Find.World.pocketMaps) yield return parent;
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead) yield return pawn;
            foreach (Battle battle in Find.BattleLog.Battles)
            {
                yield return battle;
                foreach (LogEntry entry in battle.Entries) yield return entry;
            }
            foreach (LogEntry entry in Find.PlayLog.AllEntries) yield return entry;
            var things = new List<Thing>();
            foreach (Map other in Find.Maps)
            {
                if (other == restoringMap) continue;
                yield return other;
                things.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(other, things);
                foreach (Thing thing in things) yield return thing;
            }
        }

        internal static bool CanRegisterExternalReference(ILoadReferenceable value)
            => value != null && (!(value is Thing thing)
                || !(thing is Mote) && thing.def != null && thing.def.HasThingIDNumber && thing.thingIDNumber >= 0);

        private static HashSet<string> SavedIds(XmlDocument document)
        {
            var result = new HashSet<string>();
            foreach (XmlNode node in document.SelectNodes("/archivedMap/map/things/*/id"))
                if (!result.Add(node.InnerText)) throw new InvalidDataException($"Duplicate saved Thing ID {node.InnerText}.");
            return result;
        }

        private sealed class ReferenceComparer : IEqualityComparer<ILoadReferenceable>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(ILoadReferenceable x, ILoadReferenceable y) => ReferenceEquals(x, y);
            public int GetHashCode(ILoadReferenceable value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
