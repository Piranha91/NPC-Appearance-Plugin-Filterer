using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using System.Threading.Tasks;
using System.IO;
using NPCAppearancePluginFilterer.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NPCAppearancePluginFilterer
{
    public class Program
    {
        static Lazy<NAPFsettings> Settings = null!;
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .SetAutogeneratedSettings(
                    nickname: "Settings",
                    path: "settings.json",
                    out Settings)
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "NAPF Output.esp")
                .Run(args);
        }

        private static void CanRunPatch(IRunnabilityState state)
        {
            NAPFsettings settings = Settings.Value;
            if (settings.AssetOutputDirectory != "" && !Directory.Exists(settings.AssetOutputDirectory))
            {
                throw new Exception("Cannot find output directory specified in settings: " + settings.AssetOutputDirectory);
            }

            if (settings.MO2DataPath != "" && !Directory.Exists(settings.MO2DataPath))
            {
                throw new Exception("Cannot find the Mod Organizer 2 Mods folder specified in settings: " + settings.MO2DataPath);
            }
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            NAPFsettings settings = Settings.Value;

            Dictionary<ModKey, string> PluginDirectoryDict = initPluginDirectoryDict(settings, state);

            getWarningsToSuppress(settings, state);

            foreach (var PPS in settings.PluginsToForward)
            {
                Console.WriteLine("Processing {0}", PPS.Plugin.ToString());

                if (PluginDirectoryDict.ContainsKey(PPS.Plugin) == false)
                {
                    throw new Exception("Plugin -> Folder dictionary does not contain an entry for plugin " + PPS.Plugin.ToString());
                }
                string currentDataDir = PluginDirectoryDict[PPS.Plugin];

                foreach (var npcCO in state.LoadOrder.PriorityOrder.Npc().WinningContextOverrides())
                {
                    var npcWinner = npcCO.Record;
                    string NPCdispStr = npcWinner.Name + " | " + npcWinner.EditorID + " | " + npcWinner.FormKey.ToString();
                    foreach (var context in state.LinkCache.ResolveAllContexts<INpc, INpcGetter>(npcCO.Record.FormKey))
                    {
                        if (context.ModKey != PPS.Plugin)
                        {
                            continue;
                        }

                        var npc = context.Record;
                        if ((PPS.InvertSelection == false && PPS.NPCs.Contains(npc.AsLinkGetter())) || (PPS.InvertSelection == true && !PPS.NPCs.Contains(npc.AsLinkGetter())))
                        {
                            Console.WriteLine("Forwarding appearance of {0}", NPCdispStr);
                            var NPCoverride = state.PatchMod.Npcs.GetOrAddAsOverride(npc);
                            copyAssets(NPCoverride, settings, currentDataDir, state);
                        }
                    }
                }

                //remap dependencies
                Console.WriteLine("Remapping Dependencies from {0}.", PPS.Plugin.ToString());
                state.PatchMod.DuplicateFromOnlyReferenced(state.LinkCache, PPS.Plugin, out var _);
            }
        }

        public static void getWarningsToSuppress(NAPFsettings settings, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            string settingsPath = Path.Combine(state.ExtraSettingsDataPath, "Warnings To Suppress.json");
            if (!File.Exists(settingsPath) && settings.SuppressKnownMissingFileWarnings)
            {
                throw new Exception("Could not find the list of known missing files (expected at: " + settingsPath + ").");
            }

            try
            {
                var tempJArray = JArray.Parse(File.ReadAllText(settingsPath));
                foreach (var s in tempJArray)
                {
                    settings.warningsToSuppress.Add(s.ToString().Replace(@"\\", @"\"));
                }

                if (settings.SuppressKnownMissingFileWarnings)
                {
                    Console.WriteLine("Found list of known missing files to suppress (contains {0} entries).", settings.warningsToSuppress.Count);
                }
            }
            catch
            {
                throw new Exception("Could not parse the list of known missing files (expected at: " + settingsPath + ").");
            }
        }

        public static Dictionary<ModKey, string> initPluginDirectoryDict(NAPFsettings settings, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            Dictionary<ModKey, string> PluginDirectoryDict = new Dictionary<ModKey, string>();
            if (settings.MO2DataPath == null || settings.MO2DataPath.Length == 0)
            {
                return PluginDirectoryDict;
            }

            foreach (var mk in settings.PluginsToForward)
            {
                if (mk.Plugin != null)
                {
                    switch (settings.Mode)
                    {
                        case Mode.Deep:
                            bool dirFound = false;
                            foreach (var dirName in Directory.GetDirectories(settings.MO2DataPath))
                            {
                                string potentialPath = Path.Join(dirName, mk.Plugin.ToString());
                                if (File.Exists(potentialPath))
                                {
                                    PluginDirectoryDict.Add(mk.Plugin, dirName);
                                    dirFound = true;
                                    break;
                                }
                            }
                            if (dirFound == false)
                            {
                                throw new Exception("Cannot find any folder within " + settings.MO2DataPath + " that contains plugin " + mk.Plugin.ToString());
                            }
                            break;

                        case Mode.Simple:
                            PluginDirectoryDict.Add(mk.Plugin, state.DataFolderPath);
                            break;
                    }
                }
            }
            return PluginDirectoryDict;
        }

        public static void copyAssets(Npc npc, NAPFsettings settings, string currentModDirectory, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            HashSet<string> meshes = new HashSet<string>();
            HashSet<string> textures = new HashSet<string>();

            //FaceGen
            meshes.Add("actors\\character\\facegendata\\facegeom\\" + npc.FormKey.ModKey.ToString() + "\\00" + npc.FormKey.IDString() + ".nif");
            textures.Add("actors\\character\\facegendata\\facetint\\" + npc.FormKey.ModKey.ToString() + "\\00" + npc.FormKey.IDString() + ".dds");

            if (settings.CopyExtraAssets)
            {
                //headparts
                foreach (var hp in npc.HeadParts)
                {
                    if (!settings.PluginsExcludedFromMerge.Contains(hp.FormKey.ModKey))
                    {
                        getHeadPartAssetPaths(hp, textures, meshes, settings.PluginsExcludedFromMerge, state);
                    }
                }

                // armor and armature
                if (npc.WornArmor != null && state.LinkCache.TryResolve<IArmorGetter>(npc.WornArmor.FormKey, out var wnamGetter) && wnamGetter.Armature != null)
                {
                    foreach (var aa in wnamGetter.Armature)
                    {
                        if (!settings.PluginsExcludedFromMerge.Contains(aa.FormKey.ModKey))
                        {
                            {
                                getARMAAssetPaths(aa, textures, meshes, settings.PluginsExcludedFromMerge, state);
                            }
                        }
                    }
                }
            }

            // copy files
            copyAssetFiles(settings, currentModDirectory, meshes, "Meshes");
            copyAssetFiles(settings, currentModDirectory, textures, "Textures");
        }

        public static void copyAssetFiles(NAPFsettings settings, string dataPath, HashSet<string> assetPathList, string type)
        {
            
            string prepend = Path.Combine(settings.AssetOutputDirectory, type);
            if (Directory.Exists(prepend) == false)
            {
                Directory.CreateDirectory(prepend);
            }

            foreach (string s in assetPathList)
            {
                if (!isIgnored(s, settings.pathsToIgnore))
                {
                    string currentPath = Path.Join(dataPath, type, s);
                    if (File.Exists(currentPath) == false)
                    {
                        if (!(settings.SuppressKnownMissingFileWarnings && settings.warningsToSuppress.Contains(s))) // nested if statement intentional; otherwise a suppressed warning goes into the else block despite the target file not existing
                        {
                            Console.WriteLine("Warning: File " + currentPath + " was not found.");
                        }
                    }
                    else
                    {
                        string destPath = Path.Join(prepend, s);

                        FileInfo fileInfo = new FileInfo(destPath);
                        if (fileInfo != null && fileInfo.Directory != null && !fileInfo.Directory.Exists)
                        {
                            Directory.CreateDirectory(fileInfo.Directory.FullName);
                        }

                        File.Copy(currentPath, destPath, true);
                    }
                }
            }
        }

        public static bool isIgnored (string s, HashSet<string> toIgnore)
        {
            string l = s.ToLower();
            foreach (string ig in toIgnore)
            {
                if (ig.ToLower() == l)
                {
                    return true;
                }
            }
            return false;
        }

        public static void getARMAAssetPaths(IFormLinkGetter<IArmorAddonGetter> aa, HashSet<string> texturePaths, HashSet<string> meshPaths, HashSet<ModKey> PluginsExcludedFromMerge, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!state.LinkCache.TryResolve<IArmorAddonGetter>(aa.FormKey, out var aaGetter))
            {
                return;
            }

            if (aaGetter.WorldModel != null && aaGetter.WorldModel.Male != null && aaGetter.WorldModel.Male.File != null)
            {
                meshPaths.Add(aaGetter.WorldModel.Male.File);
            }
            if (aaGetter.WorldModel != null && aaGetter.WorldModel.Female != null && aaGetter.WorldModel.Female.File != null)
            {
                meshPaths.Add(aaGetter.WorldModel.Female.File);
            }

            if (aaGetter.SkinTexture != null && aaGetter.SkinTexture.Male != null && !PluginsExcludedFromMerge.Contains(aaGetter.SkinTexture.Male.FormKey.ModKey) && state.LinkCache.TryResolve<ITextureSetGetter>(aaGetter.SkinTexture.Male.FormKey, out var mSkinTxst))
            {
                getTextureSetPaths(mSkinTxst, texturePaths);
            }
            if (aaGetter.SkinTexture != null && aaGetter.SkinTexture.Female != null && !PluginsExcludedFromMerge.Contains(aaGetter.SkinTexture.Female.FormKey.ModKey) && state.LinkCache.TryResolve<ITextureSetGetter>(aaGetter.SkinTexture.Female.FormKey, out var fSkinTxst))
            {
                getTextureSetPaths(fSkinTxst, texturePaths);
            }
        }

        public static void getHeadPartAssetPaths(IFormLinkGetter<IHeadPartGetter> hp, HashSet<string> texturePaths, HashSet<string> meshPaths, HashSet<ModKey> PluginsExcludedFromMerge, IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!state.LinkCache.TryResolve<IHeadPartGetter>(hp.FormKey, out var hpGetter))
            {
                return;
            }

            if (hpGetter.Model != null && hpGetter.Model.File != null)
            {
                meshPaths.Add(hpGetter.Model.File);
            }

            if (hpGetter.Parts != null)
            {
                foreach (var part in hpGetter.Parts)
                {
                    if (part.FileName != null)
                    {
                        meshPaths.Add(part.FileName);
                    }
                }
            }

            if (hpGetter.TextureSet != null && state.LinkCache.TryResolve<ITextureSetGetter>(hpGetter.TextureSet.FormKey, out var hTxst))
            {
                getTextureSetPaths(hTxst, texturePaths);
            }

            if (hpGetter.ExtraParts != null)
            {
                foreach (var EP in hpGetter.ExtraParts)
                {
                    if (!PluginsExcludedFromMerge.Contains(EP.FormKey.ModKey))
                    {
                        getHeadPartAssetPaths(EP, texturePaths, meshPaths, PluginsExcludedFromMerge, state);
                    }
                }
            }
        }

        public static void getTextureSetPaths(ITextureSetGetter Txst, HashSet<string> texturePaths)
        {
            if (Txst.Diffuse != null)
            {
                texturePaths.Add(Txst.Diffuse);
            }
            if (Txst.NormalOrGloss != null)
            {
                texturePaths.Add(Txst.NormalOrGloss);
            }
            if (Txst.BacklightMaskOrSpecular != null)
            {
                texturePaths.Add(Txst.BacklightMaskOrSpecular);
            }
            if (Txst.Environment != null)
            {
                texturePaths.Add(Txst.Environment);
            }
            if (Txst.EnvironmentMaskOrSubsurfaceTint != null)
            {
                texturePaths.Add(Txst.EnvironmentMaskOrSubsurfaceTint);
            }
            if (Txst.GlowOrDetailMap != null)
            {
                texturePaths.Add(Txst.GlowOrDetailMap);
            }
        }
    }
}
