using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BunnyGarden2FixMod.Utils;
using HarmonyLib;

namespace BunnyGarden2FixMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    public static ConfigEntry<int> ConfigWidth;
    public static ConfigEntry<int> ConfigHeight;
    public static ConfigEntry<int> ConfigFrameRate;

    internal new static ManualLogSource Logger;

    private void Awake()
    {
        ConfigWidth = Config.Bind(
            "Resolution",
            "Width",
            1920,
            "解像度の幅（横）を指定します");

        ConfigHeight = Config.Bind(
            "Resolution",
            "Height",
            1080,
            "解像度の高さ（縦）を指定します");

        ConfigFrameRate = Config.Bind(
            "Resolution",
            "FrameRate",
            60,
            "フレームレート上限を指定します。-1にすると上限を撤廃します。");

        Logger = base.Logger;
        PatchLogger.Initialize(Logger);
        var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
        PatchLogger.LogInfo($"プラグイン起動: {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION}");
        PatchLogger.LogInfo($"解像度パッチを適用しました: {Plugin.ConfigWidth.Value}x{Plugin.ConfigHeight.Value}");
    }
}
