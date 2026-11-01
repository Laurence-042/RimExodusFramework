using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimExodus.Maps
{
    // Native map loading consults this header even after Scribe returns to Inactive
    // (compressed Thing hashes and spawn compatibility). Keep it through FinalizeLoading.
    internal sealed class ArchiveMetadataScope : IDisposable
    {
        private static readonly FieldInfo LastMode = AccessTools.Field(typeof(ScribeMetaHeaderUtility), "lastMode");
        private readonly object mode = LastMode.GetValue(null);
        private readonly string version = ScribeMetaHeaderUtility.loadedGameVersion;
        private readonly int major = ScribeMetaHeaderUtility.loadedGameVersionMajor;
        private readonly int minor = ScribeMetaHeaderUtility.loadedGameVersionMinor;
        private readonly int build = ScribeMetaHeaderUtility.loadedGameVersionBuild;
        private readonly List<string> modIds = ScribeMetaHeaderUtility.loadedModIdsList;
        private readonly List<string> modNames = ScribeMetaHeaderUtility.loadedModNamesList;
        private readonly List<int> steamIds = ScribeMetaHeaderUtility.loadedModSteamIdsList;
        private readonly bool changed = ScribeMetaHeaderUtility.modListChanged;

        internal void Load()
        {
            if (Scribe.mode != LoadSaveMode.LoadingVars
                || string.IsNullOrEmpty(Scribe.loader.curXmlParent?["meta"]?["gameVersion"]?.InnerText))
                throw new InvalidDataException("Map archive has no native version header.");
            ScribeMetaHeaderUtility.LoadGameDataHeader(ScribeMetaHeaderUtility.ScribeHeaderMode.Map, false);
            if (ScribeMetaHeaderUtility.loadedGameVersionMajor <= 0)
                throw new InvalidDataException("Map archive has an invalid native version header.");
        }

        public void Dispose()
        {
            ScribeMetaHeaderUtility.loadedGameVersion = version;
            ScribeMetaHeaderUtility.loadedGameVersionMajor = major;
            ScribeMetaHeaderUtility.loadedGameVersionMinor = minor;
            ScribeMetaHeaderUtility.loadedGameVersionBuild = build;
            ScribeMetaHeaderUtility.loadedModIdsList = modIds;
            ScribeMetaHeaderUtility.loadedModNamesList = modNames;
            ScribeMetaHeaderUtility.loadedModSteamIdsList = steamIds;
            ScribeMetaHeaderUtility.modListChanged = changed;
            LastMode.SetValue(null, mode);
        }
    }
}
