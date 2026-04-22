using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GB;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches;

public class KneeSocksPatch : MonoBehaviour
{
    private static CharacterHandle _handle;
    private static SkinnedMeshRenderer _kneeSocks;

    public static void Initialize(GameObject parent)
    {
        parent.AddComponent<KneeSocksPatch>();
    }

    private IEnumerator Start()
    {
        var parent = new GameObject(nameof(KneeSocksPatch));
        parent.transform.SetParent(transform, false);
        parent.SetActive(false);
        _handle = new CharacterHandle(parent);

        yield return new WaitUntil(() => GBSystem.Instance != null && GBSystem.Instance.RefSaveData() != null);

        _handle.Preload(CharID.LUNA,
            new CharacterHandle.LoadArg() { Costume = CostumeType.Casual }
        );
        yield return new WaitUntil(() => _handle.IsPreloadDone());

        _kneeSocks = parent.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(m => m.name == "mesh_kneehigh")
            .FirstOrDefault();
        if (_kneeSocks == null)
            Plugin.Logger.LogWarning($"[{nameof(KneeSocksPatch)}] ニーハイのメッシュが見つかりませんでした。");
        else
            Plugin.Logger.LogInfo($"[{nameof(KneeSocksPatch)}] ニーハイのメッシュを見つけました。");
    }

    public static void Apply(GameObject character)
    {
        if (_kneeSocks == null)
            return;

        var stockings = character.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(m => m.name == "mesh_stockings")
            .FirstOrDefault();

        var lower = character.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(m => m.name == "mesh_skin_lower")
            .FirstOrDefault();

        if (stockings == null || lower == null)
            return;

        stockings.gameObject.SetActive(true);

        var bones = new Dictionary<string, Transform>();
        foreach (var bone in character.GetComponentsInChildren<Transform>(true))
            bones[bone.name.ToLowerInvariant()] = bone;

        stockings.sharedMesh = _kneeSocks.sharedMesh;
        stockings.material = _kneeSocks.material;
        stockings.bones = [.. _kneeSocks.bones
                .Select(bone => bones.TryGetValue(bone.name.ToLowerInvariant(), out var targetBone)
                    ? targetBone
                    : null)];

        // z-fighting対策でブレンドシェイプを適当にいじる
        int blendShape = lower.sharedMesh.GetBlendShapeIndex("blendShape_skin_lower.skin_stocking");
        if (blendShape >= 0)
            lower.SetBlendShapeWeight(blendShape, 100);
    }

    public static void Process(CharacterHandle handle)
    {
        if (handle.Chara == null ||
            handle.m_lastLoadArg?.Costume != CostumeType.Uniform ||
            !Plugin.ConfigApplyKneeHigh.Value.ToLowerInvariant().Contains($"{handle.GetCharID()}".ToLowerInvariant()))
        {
            return;
        }

        Plugin.Logger.LogInfo($"[{nameof(KneeSocksPatch)}] ニーハイを適用します。");
        Apply(handle.Chara);
    }
}

// setupは衣装が初めて読み込まれるときに呼ばれ。
// その代わりにsetupPantiesOnlyはすでに衣装が読み込まれていてパンティだけ変更されたときに呼ばれる。
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setup))]
public static class KneeSocksSetupSpawnPatch
{
    private static void Postfix(CharacterHandle __instance) => KneeSocksPatch.Process(__instance);
}

[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.setupPantiesOnly))]
public static class KneeSocksSetupPantiesOnlyPatch
{
    private static void Postfix(CharacterHandle __instance) => KneeSocksPatch.Process(__instance);
}