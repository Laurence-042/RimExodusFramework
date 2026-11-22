using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using RimWorld;
using Verse;

namespace RimExodus.Maps
{
    /// <summary>侧车条目：掩码内一格的基架（原版 PrefabDef 不保存 foundation 层）。</summary>
    public class PrefabFoundationEntry
    {
        public IntVec3 pos;
        public TerrainDef def;
    }

    /// <summary>侧车条目：掩码内一格的屋顶（原版 PrefabDef 不保存 roof）。</summary>
    public class PrefabRoofEntry
    {
        public IntVec3 pos;
        public RoofDef def;
    }

    /// <summary>
    /// 通用地图 prefab 文档：原版 PrefabTerrainData + size、逐实例 Thing 状态与
    /// foundation/roof 侧车。地形的 PrefabDef 仍经原版 XML 和交叉引用管线读取；
    /// Thing 的完整 ExposeData XML 在每次重建时由 Scribe 整批读取。
    /// </summary>
    public class MapPrefabDocument
    {
        public const int CurrentFormatVersion = 2;

        public string name;
        public int formatVersion = CurrentFormatVersion;
        public IntVec2 size;
        public List<IntVec3> mask = new List<IntVec3>();
        // Native map state is a content layer for whole-map selections (grids, components, Pawns, references).
        public string mapState;
        public int sourceMapId = -1;
        public IntVec3 sourceOrigin;
        public List<MapPrefabThingEntry> thingEntries = new List<MapPrefabThingEntry>();
        public List<PrefabTerrainData> terrain = new List<PrefabTerrainData>();
        public List<PrefabFoundationEntry> foundations = new List<PrefabFoundationEntry>();
        public List<PrefabRoofEntry> roofs = new List<PrefabRoofEntry>();
        public List<IntVec3> noRoof = new List<IntVec3>();
        public List<PrefabEnvironmentEntry> environment = new List<PrefabEnvironmentEntry>();
        internal string ImportSource => importSource ?? name;
        internal bool SkipMissingFilterDefs { get; private set; }
        internal bool SkipThingsOutOfBounds { get; private set; }
        internal readonly List<PrefabImportChange> ImportChanges = new List<PrefabImportChange>();
        private string importSource;

        private static readonly FieldInfo PrefabThingsField =
            typeof(PrefabDef).GetField("things", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PrefabTerrainField =
            typeof(PrefabDef).GetField("terrain", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>spawn 对账用：things 里 positions 的总格数（chance 恒 1）。
        /// SpawnPrefab 对放不下的 thing 静默跳过，靠此计数暴露丢失。</summary>
        public int ThingCellCount
        {
            get
            {
                return thingEntries.Count;
            }
        }

        /// <summary>生成 SpawnPrefab 入参。游戏更新若改名 internal 字段（等于改 XML 契约，
        /// 可能性极低），这里抛异常交由调用方回退。</summary>
        public PrefabDef BuildPrefabDef(bool omitSpace = false)
        {
            if (PrefabThingsField == null || PrefabTerrainField == null)
            {
                throw new NotSupportedException("PrefabDef internal fields 'things'/'terrain' not found; game update changed the prefab XML contract.");
            }
            var def = new PrefabDef
            {
                size = size,
            };
            PrefabThingsField.SetValue(def, new List<PrefabThingData>());
            PrefabTerrainField.SetValue(def, omitSpace ? terrain.Where(t => t.def != TerrainDefOf.Space).ToList() : terrain);
            return def;
        }

        public static MapPrefabDocument Load(string path) => Load(path, null);

        public static MapPrefabDocument Load(string path, PrefabImportOptions options)
        {
            var xml = new XmlDocument();
            xml.Load(path);
            return Read(xml, path, options);
        }

        public static MapPrefabDocument FromXml(string content)
        {
            var xml = new XmlDocument();
            xml.LoadXml(content);
            return Read(xml, "embedded prefab", null);
        }

        private static MapPrefabDocument Read(XmlDocument xml, string path, PrefabImportOptions options)
        {
            XmlElement root = xml.DocumentElement;
            if (root == null || root.Name != "MapPrefab")
            {
                throw new InvalidDataException($"Not a MapPrefab document: {path}");
            }
            string versionText = root.GetAttribute("formatVersion");
            int version = versionText.NullOrEmpty() ? 0 : ParseHelper.FromString<int>(versionText);
            if (version != CurrentFormatVersion)
            {
                throw new InvalidDataException($"MapPrefab formatVersion {version} is not supported (expected {CurrentFormatVersion}); export this prefab again: {path}");
            }
            XmlElement prefabNode = root["prefab"];
            if (prefabNode == null)
            {
                throw new InvalidDataException($"MapPrefab has no <prefab> core node: {path}");
            }

            XmlElement foundationsNode = root["foundations"];
            XmlElement roofsNode = root["roofs"];
            XmlElement statesNode = root["thingStates"];
            if (statesNode == null)
                throw new InvalidDataException($"MapPrefab has no <thingStates> node; export again: {path}");

            string mapState = root["mapState"]?.InnerText;
            if (!string.IsNullOrEmpty(mapState)) options = null;
            var doc = new MapPrefabDocument
            {
                name = root["name"]?.InnerText,
                importSource = path,
                SkipMissingFilterDefs = options?.SkipMissingFilterDefs == true,
                SkipThingsOutOfBounds = options?.SkipThingsOutOfBounds == true,
                mapState = mapState,
                sourceMapId = root["sourceMapId"] == null ? -1 : ParseHelper.FromString<int>(root["sourceMapId"].InnerText),
                sourceOrigin = root["sourceOrigin"] == null ? IntVec3.Zero : ParseHelper.FromString<IntVec3>(root["sourceOrigin"].InnerText),
            };
            if (options != null)
            {
                doc.ImportTerrain(prefabNode["terrain"], options);
                doc.ImportSidecar(foundationsNode, "foundation", options.MissingFoundation,
                    ValidTerrain(options.DefaultFoundation, foundation: true));
                doc.ImportSidecar(roofsNode, "roof", options.MissingRoof, LoadedDef(options.DefaultRoof));
            }

            PrefabDef parsed = DirectXmlToObject.ObjectFromXml<PrefabDef>(prefabNode, doPostLoad: false);
            if (parsed == null)
            {
                throw new InvalidDataException($"MapPrefab <prefab> core node failed to parse: {path}");
            }
            // foundations/roofs 必须与核心节点一起在 resolve 之前解析：Def 引用（TerrainDef/
            // RoofDef）在 ObjectFromXml 里注册为延迟交叉引用，任何一组在 resolve 之后才解析，
            // 其引用就永远不会被解析（def 留 null，下游 SetFoundation 逐格静默失败）。
            List<PrefabFoundationEntry> foundations = foundationsNode != null
                ? DirectXmlToObject.ObjectFromXml<List<PrefabFoundationEntry>>(foundationsNode, doPostLoad: false)
                : new List<PrefabFoundationEntry>();
            List<PrefabRoofEntry> roofs = roofsNode != null
                ? DirectXmlToObject.ObjectFromXml<List<PrefabRoofEntry>>(roofsNode, doPostLoad: false)
                : new List<PrefabRoofEntry>();
            DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences(FailMode.LogErrors);

            // resolve 失败（defName 不存在等）只写游戏日志、字段留 null——这里显式拒绝，
            // 让坏文件以异常走调用方回退，而不是静默 null 在下游产生难查的连锁故障。
            if (parsed.GetThings().Any(entry => entry.data.def == null)
                || parsed.GetTerrain().Any(entry => entry.data.def == null)
                || foundations.Any(entry => entry.def == null)
                || roofs.Any(entry => entry.def == null))
            {
                throw new InvalidDataException($"MapPrefab contains unresolved def references (see game log for 'Could not resolve cross-reference' entries): {path}");
            }

            doc.size = parsed.size;
            doc.foundations = foundations;
            doc.roofs = roofs;
            if (root["mask"] != null)
                foreach (XmlNode cell in root["mask"].ChildNodes)
                    if (cell.Name == "li") doc.mask.Add(ParseHelper.FromString<IntVec3>(cell.InnerText));
            if (root["environment"] != null)
                doc.environment = DirectXmlToObject.ObjectFromXml<List<PrefabEnvironmentEntry>>(root["environment"], doPostLoad: false);
            if (root["noRoof"] != null)
                doc.noRoof = DirectXmlToObject.ObjectFromXml<List<IntVec3>>(root["noRoof"], doPostLoad: false);
            doc.ValidateNoRoof();
            MapPrefabEnvironment.Validate(doc);
            foreach (XmlNode child in statesNode.ChildNodes)
            {
                if (child is not XmlElement node || node.Name != "li") continue;
                MapPrefabThingEntry entry = doc.ReadThing(node, options);
                if (entry != null) doc.thingEntries.Add(entry);
            }
            if (parsed.GetThings().Any())
                throw new InvalidDataException($"MapPrefab state format cannot include legacy <prefab><things>: {path}");
            // 地形仍由原版 PrefabDef 解析；Thing 状态在独立列表中逐实例保存。
            var terrainByData = new Dictionary<(TerrainDef def, ColorDef color), PrefabTerrainData>();
            foreach (var (data, cell) in parsed.GetTerrain())
            {
                if (!terrainByData.TryGetValue((data.def, data.color), out PrefabTerrainData entry))
                {
                    entry = new PrefabTerrainData
                    {
                        def = data.def,
                        color = data.color,
                        rects = new List<CellRect>(),
                    };
                    terrainByData[(data.def, data.color)] = entry;
                }
                entry.rects.Add(new CellRect(cell.x, cell.z, 1, 1));
            }
            doc.terrain.AddRange(terrainByData.Values);
            return doc;
        }

        private MapPrefabThingEntry ReadThing(XmlElement node, PrefabImportOptions options)
        {
            string defName = node["def"]?.InnerText;
            if (string.IsNullOrWhiteSpace(defName))
                throw new InvalidDataException($"MapPrefab Thing entry has no Def name: {ImportSource}");
            IntVec3 pos = ParseHelper.FromString<IntVec3>(node["pos"].InnerText);
            ThingDef def = FindDef<ThingDef>(defName);
            if (def == null)
            {
                if (options?.SkipMissingThings != true)
                    throw new InvalidDataException($"MapPrefab state has missing ThingDef '{defName}' at {pos}: {ImportSource}");
                ImportChanges.Add(new PrefabImportChange("ThingDef", defName, null, pos.ToString()));
                return null;
            }

            XmlElement state = node["state"]?["saveable"];
            string stuffName = node["stuff"]?.InnerText;
            ThingDef stuff = FindDef<ThingDef>(stuffName);
            string missingStuff = !string.IsNullOrEmpty(stuffName) && stuff == null ? stuffName : null;
            if (options != null && missingStuff == null && def.MadeFromStuff)
            {
                string stateStuff = state?["stuff"]?.InnerText;
                if (FindDef<ThingDef>(stateStuff) == null)
                    missingStuff = string.IsNullOrEmpty(stateStuff) || stateStuff == "null" ? "(unset)" : stateStuff;
                else if (stuff == null)
                    missingStuff = "(unset)";
            }

            if (missingStuff != null)
            {
                if (options == null || options.MissingStuff == MissingPrefabDef.Error)
                    throw new InvalidDataException($"MapPrefab state has missing stuff '{missingStuff}' for '{defName}' at {pos}: {ImportSource}");
                stuff = options.MissingStuff == MissingPrefabDef.Replace ? SelectStuff(def, options) : null;
                ImportChanges.Add(new PrefabImportChange("stuff for " + defName, missingStuff, stuff?.defName, pos.ToString()));
                if (stuff == null) return null;
                if (state != null) MapPrefabThingState.ReplaceStuff(state, stuff);
            }

            return new MapPrefabThingEntry
            {
                pos = pos,
                def = def,
                stuff = stuff,
                rotation = ParseHelper.FromString<Rot4>(node["rotation"].InnerText),
                hitPoints = ParseHelper.FromString<int>(node["hitPoints"].InnerText),
                stackCount = ParseHelper.FromString<int>(node["stackCount"].InnerText),
                stateXml = state?.OuterXml,
                replacedStuff = missingStuff != null,
            };
        }

        private void ImportTerrain(XmlElement terrainNode, PrefabImportOptions options)
        {
            if (terrainNode == null) return;
            TerrainDef fallback = ValidTerrain(options.DefaultTerrain, foundation: false);
            foreach (XmlElement node in terrainNode.ChildNodes.OfType<XmlElement>().ToList())
            {
                string name = node.Name == "li" ? node.InnerText : node.Name;
                string location = node["rects"] == null ? "(no rects)"
                    : string.Join(", ", node["rects"].ChildNodes.OfType<XmlElement>().Select(rect => rect.InnerText));
                XmlElement entry = node;
                if (FindDef<TerrainDef>(name) == null)
                {
                    TerrainDef replacement = ReplaceMissing("terrain", name, location, options.MissingTerrain, fallback);
                    if (replacement == null)
                    {
                        terrainNode.RemoveChild(node);
                        continue;
                    }
                    if (node.Name == "li")
                    {
                        node.InnerText = replacement.defName;
                    }
                    else
                    {
                        entry = node.OwnerDocument.CreateElement(replacement.defName);
                        while (node.HasChildNodes) entry.AppendChild(node.FirstChild);
                        terrainNode.ReplaceChild(entry, node);
                    }
                }

                XmlElement color = entry["color"];
                if (color == null || FindDef<ColorDef>(color.InnerText) != null) continue;
                ReplaceMissing<ColorDef>("terrain color", color.InnerText, location,
                    options.SkipMissingTerrainColors ? MissingPrefabDef.Skip : MissingPrefabDef.Error, null);
                entry.RemoveChild(color);
            }
        }

        private void ImportSidecar<T>(XmlElement layerNode, string layer, MissingPrefabDef mode, T fallback) where T : Def, new()
        {
            if (layerNode == null) return;
            foreach (XmlElement node in layerNode.ChildNodes.OfType<XmlElement>().ToList())
            {
                XmlElement def = node["def"];
                if (def == null || string.IsNullOrWhiteSpace(def.InnerText))
                    throw new InvalidDataException($"MapPrefab {layer} entry has no Def name: {ImportSource}");
                if (FindDef<T>(def.InnerText) != null) continue;
                IntVec3 pos = ParseHelper.FromString<IntVec3>(node["pos"].InnerText);
                T replacement = ReplaceMissing(layer, def.InnerText, pos.ToString(), mode, fallback);
                if (replacement == null) layerNode.RemoveChild(node);
                else def.InnerText = replacement.defName;
            }
        }

        private T ReplaceMissing<T>(string layer, string name, string position, MissingPrefabDef mode, T fallback) where T : Def
        {
            if (mode == MissingPrefabDef.Error)
                throw new InvalidDataException($"MapPrefab {layer} has missing Def '{name}' at {position}: {ImportSource}");
            T replacement = mode == MissingPrefabDef.Replace ? fallback : null;
            ImportChanges.Add(new PrefabImportChange(layer, name, replacement?.defName, position));
            return replacement;
        }

        private static T FindDef<T>(string name) where T : Def, new()
            => string.IsNullOrEmpty(name) || name == "null" ? null : DefDatabase<T>.GetNamedSilentFail(name);

        private static T LoadedDef<T>(T def) where T : Def, new()
            => def != null && FindDef<T>(def.defName) == def ? def : null;

        private static TerrainDef ValidTerrain(TerrainDef def, bool foundation)
            => LoadedDef(def) != null && def.isFoundation == foundation ? def : null;

        private static ThingDef SelectStuff(ThingDef thing, PrefabImportOptions options)
        {
            ThingDef fallback = LoadedDef(options.DefaultStuff);
            if (!thing.MadeFromStuff) return null;
            if (fallback?.stuffProps?.CanMake(thing) == true) return fallback;
            if (!options.UseVanillaDefaultStuff
                || thing.defaultStuff == null && !GenStuff.AllowedStuffsFor(thing).Any()) return null;
            fallback = LoadedDef(GenStuff.DefaultStuffFor(thing));
            return fallback?.stuffProps?.CanMake(thing) == true ? fallback : null;
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!directory.NullOrEmpty())
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, ToXml(), new UTF8Encoding(false));
        }

        public string ToXml()
        {
            ValidateNoRoof();
            MapPrefabEnvironment.Validate(this);
            var text = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = true,
            };
            using (XmlWriter writer = XmlWriter.Create(text, settings))
            {
                writer.WriteStartElement("MapPrefab");
                writer.WriteAttributeString("formatVersion", CurrentFormatVersion.ToString());
                writer.WriteElementString("name", name);
                writer.WriteElementString("sourceMapId", sourceMapId.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("sourceOrigin", sourceOrigin.ToString());
                if (mapState != null) writer.WriteElementString("mapState", mapState);
                writer.WriteStartElement("mask");
                foreach (IntVec3 cell in mask) writer.WriteElementString("li", cell.ToString());
                writer.WriteEndElement();

                writer.WriteStartElement("prefab");
                writer.WriteElementString("defName", "MapPrefab_" + name);
                writer.WriteElementString("size", size.ToString());
                if (terrain.Count > 0)
                {
                    writer.WriteStartElement("terrain");
                    foreach (PrefabTerrainData entry in terrain)
                    {
                        writer.WriteStartElement(entry.def.defName);
                        if (entry.color != null)
                        {
                            writer.WriteElementString("color", entry.color.defName);
                        }
                        writer.WriteStartElement("rects");
                        foreach (CellRect rect in entry.rects)
                        {
                            writer.WriteElementString("li", rect.ToString());
                        }
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();

                writer.WriteStartElement("thingStates");
                foreach (MapPrefabThingEntry entry in thingEntries)
                {
                    writer.WriteStartElement("li");
                    writer.WriteElementString("pos", entry.pos.ToString());
                    writer.WriteElementString("def", entry.def.defName);
                    if (entry.stuff != null) writer.WriteElementString("stuff", entry.stuff.defName);
                    writer.WriteElementString("rotation", entry.rotation.ToString());
                    writer.WriteElementString("hitPoints", entry.hitPoints.ToString(CultureInfo.InvariantCulture));
                    writer.WriteElementString("stackCount", entry.stackCount.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrWhiteSpace(entry.stateXml))
                    {
                        writer.WriteStartElement("state");
                        writer.WriteRaw(entry.stateXml);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();

                if (foundations.Count > 0)
                {
                    writer.WriteStartElement("foundations");
                    foreach (PrefabFoundationEntry entry in foundations)
                    {
                        writer.WriteStartElement("li");
                        writer.WriteElementString("pos", entry.pos.ToString());
                        writer.WriteElementString("def", entry.def.defName);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (roofs.Count > 0)
                {
                    writer.WriteStartElement("roofs");
                    foreach (PrefabRoofEntry entry in roofs)
                    {
                        writer.WriteStartElement("li");
                        writer.WriteElementString("pos", entry.pos.ToString());
                        writer.WriteElementString("def", entry.def.defName);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (noRoof.Count > 0)
                {
                    writer.WriteStartElement("noRoof");
                    foreach (IntVec3 cell in noRoof) writer.WriteElementString("li", cell.ToString());
                    writer.WriteEndElement();
                }
                writer.WriteStartElement("environment");
                foreach (PrefabEnvironmentEntry entry in environment)
                {
                    writer.WriteStartElement("li");
                    writer.WriteElementString("temperature", entry.temperature.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteElementString("vacuum", entry.vacuum.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteStartElement("cells");
                    foreach (IntVec3 cell in entry.cells) writer.WriteElementString("li", cell.ToString());
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            return text.ToString();
        }

        internal void ValidateNoRoof()
        {
            if (noRoof == null) throw new InvalidDataException("Missing prefab no-roof area list.");
            var seen = new HashSet<IntVec3>();
            var selected = mask.Count == 0 ? null : new HashSet<IntVec3>(mask);
            foreach (IntVec3 cell in noRoof)
                if (cell.y != 0 || cell.x < 0 || cell.z < 0 || cell.x >= size.x || cell.z >= size.z
                    || !seen.Add(cell) || selected != null && !selected.Contains(cell))
                    throw new InvalidDataException("Invalid, duplicate or unselected no-roof area cell: " + cell);
        }

    }
}
