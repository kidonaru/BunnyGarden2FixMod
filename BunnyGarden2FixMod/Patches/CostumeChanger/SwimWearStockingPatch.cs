using System.Collections.Generic;
using System.Linq;
using BunnyGarden2FixMod.Utils;
using GB.Game;
using GB.Scene;
using HarmonyLib;
using UnityEngine;

namespace BunnyGarden2FixMod.Patches.CostumeChanger;

/// <summary>
/// 水着コスチューム着用中、CharacterHandle.ApplyStocking は IsDisableStocking で早期 return し
/// ストッキングが一切適用されない。本パッチは Prefix で水着を検出し、async をスキップして
/// 同期的にストッキングを適用／解除する。
///
/// 実装:
///   1. StockingsDonorLoader が Uniform prefab からキャラ別に mesh_stockings / mesh_skin_lower をキャッシュ
///   2. 水着キャラ配下に新 GameObject "mesh_stockings" を注入（bone リマップ）
///   3. 水着の mesh_skin_lower / mesh_skin_lower_foot に blendShape の delta を nearest-neighbor で移植
///      - Uniform donor mesh_skin_lower の skin_stocking / skin_socks / skin_stocking_lower blendShape デルタを
///        swim 各頂点に対して「Uniform 側の最近傍頂点のデルタ」として転写する
///      - 頂点数が違っても適用できる（Uniform 2193 → swim 1238〜1304 や 892〜946）
///      - mesh 構造は swim のまま保持。material/UV/外形シルエットが崩れない
///   4. blendShape weight を 100 に設定 → 脚が少し細くなり stocking にフィット
///
/// 順序: HarmonyPriority.First で他の ApplyStocking Prefix より先に走らせ、水着時は return false で打ち切る。
/// </summary>
[HarmonyPatch(typeof(CharacterHandle), nameof(CharacterHandle.ApplyStocking))]
[HarmonyPriority(Priority.First)]
public static class SwimWearStockingPatch
{
    private const string InjectedName = "mesh_stockings";

    private static readonly string[] s_transplantShapeNames = new[]
    {
        "blendShape_skin_lower.skin_stocking",
        "blendShape_skin_lower.skin_socks",
        "blendShape_skin_lower.skin_stocking_lower",
    };

    /// <summary>水着キャラの lower 系メッシュを差し替え前に保存。</summary>
    private class SwimwearBackup
    {
        public SkinnedMeshRenderer LowerSmr;
        public Mesh LowerOriginalMesh;
        public SkinnedMeshRenderer LowerFootSmr;
        public Mesh LowerFootOriginalMesh;
    }

    private static readonly Dictionary<int, SwimwearBackup> s_backups = new();

    // (swimMeshInstanceId, charId) → nearest-neighbor 移植済みメッシュ
    private static readonly Dictionary<(int, int), Mesh> s_transplantedCache = new();

    static bool Prepare()
    {
        bool enabled = Plugin.ConfigCostumeChangerEnabled?.Value ?? true;
        if (enabled) PatchLogger.LogInfo("[SwimWearStockingPatch] 適用");
        return enabled;
    }

    /// <summary>
    /// シーンアンロード時にキャッシュを一掃する。StockingsDonorLoader MonoBehaviour 側から呼ばれる。
    /// 破棄済み SMR 参照を持ち越すと次シーンでの復元時に fake-null 例外 / InstanceID 再利用による
    /// 誤 hit が発生するため、毎シーンでリセットする。
    /// </summary>
    internal static void OnSceneUnloaded()
    {
        int destroyed = 0;
        foreach (var m in s_transplantedCache.Values)
        {
            if (m != null)
            {
                Object.Destroy(m);
                destroyed++;
            }
        }
        s_transplantedCache.Clear();
        s_backups.Clear();
        PatchLogger.LogInfo($"[SwimWearStockingPatch] シーンアンロード: transplanted mesh {destroyed} 件破棄、キャッシュクリア");
    }

    private static bool Prefix(CharacterHandle __instance, int __0)
    {
        if (__instance == null || __instance.Chara == null || __instance.m_lastLoadArg == null) return true;
        if (__instance.m_lastLoadArg.Costume != CostumeType.SwimWear) return true;
        if (KneeSocksLoader.IsPreloading) return true;

        var charId = __instance.GetCharID();

        // 水着×ストッキングは CostumeChanger の StockingOverrideStore 経由でのみ適用する。
        // override が無いと vanilla の IsDisableStocking 相当（= ストッキング無し）。
        // （m_lastLoadArg.Stocking に他コスチューム由来の値が残っていても無視する）
        // override 未設定 or override=0（デフォルト／ストッキング無し）は本パッチでは何もせず
        // 注入済みメッシュだけ掃除する。vanilla の IsDisableStocking 相当の挙動に委ねる。
        bool hasOverride = StockingOverrideStore.TryGet(charId, out var overrideType)
                           && overrideType != 0;
        bool isKneeSocks = hasOverride && StockingOverrideStore.IsKneeSocksType(overrideType);

        if (Plugin.ConfigDisableStockings.Value) { hasOverride = false; isKneeSocks = false; }

        if (!hasOverride)
        {
            ClearStockingSync(__instance);
            __instance.m_lastLoadArg.Stocking = 0;
            return false;
        }

        if (!StockingsDonorLoader.IsReady)
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] donor 未 Ready（{charId}）、通常 async に委譲");
            return true;
        }

        if (!StockingsDonorLoader.TryGetDonor(charId, out var donor))
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] donor 未キャッシュ: {charId}");
            return true;
        }

        // KneeSocks 系は KneeSocksLoader のプリロードが必要
        if (isKneeSocks && !KneeSocksLoader.IsLoaded)
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] KneeSocks 未ロード（{charId}）、通常 async に委譲");
            return true;
        }

        if (!ApplyStockingSync(__instance, overrideType, donor, isKneeSocks))
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] ApplyStockingSync 失敗: {charId} override={overrideType} knee={isKneeSocks}");
            return true;
        }

        // CostumeChangerPatch に合わせ、KneeSocks 系は m_lastLoadArg.Stocking=0 として記録
        __instance.m_lastLoadArg.Stocking = isKneeSocks ? 0 : overrideType;
        return false;
    }

    private static void ClearStockingSync(CharacterHandle handle)
    {
        var chara = handle.Chara;
        if (chara == null) return;

        int key = (int)handle.GetCharID();

        // 注入 mesh_stockings を削除
        var injected = chara.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == InjectedName);
        if (injected != null)
        {
            Object.Destroy(injected.gameObject);
            PatchLogger.LogInfo($"[SwimWearStockingPatch] 注入 mesh_stockings 削除: {handle.GetCharID()}");
        }

        // sharedMesh を元に戻す（SMR が破棄済みなら Unity == が true で skip される）
        if (s_backups.TryGetValue(key, out var backup))
        {
            if (backup.LowerSmr != null && backup.LowerOriginalMesh != null)
                backup.LowerSmr.sharedMesh = backup.LowerOriginalMesh;
            if (backup.LowerFootSmr != null && backup.LowerFootOriginalMesh != null)
                backup.LowerFootSmr.sharedMesh = backup.LowerFootOriginalMesh;
            s_backups.Remove(key);
            PatchLogger.LogInfo($"[SwimWearStockingPatch] sharedMesh 復元: {handle.GetCharID()}");
        }
    }

    private static bool ApplyStockingSync(CharacterHandle handle, int overrideType, SkinnedMeshRenderer donor, bool isKneeSocks)
    {
        var chara = handle.Chara;
        if (chara == null) return false;

        var renderers = chara.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        // 目標メッシュ/ボーン/マテリアルを overrideType から決定
        Mesh targetMesh;
        Transform[] targetBonesSrc;
        Transform targetRootBoneSrc;
        Material targetMat;

        if (isKneeSocks)
        {
            var kneeSocks = KneeSocksLoader.KneeSocksSmr;
            if (kneeSocks == null || kneeSocks.sharedMesh == null)
            {
                PatchLogger.LogWarning("[SwimWearStockingPatch] KneeSocks SMR 未ロード");
                return false;
            }
            targetMesh = kneeSocks.sharedMesh;
            targetBonesSrc = kneeSocks.bones;
            targetRootBoneSrc = kneeSocks.rootBone;
            targetMat = KneeSocksLoader.GetMaterialForOverride(overrideType);
        }
        else
        {
            targetMesh = donor.sharedMesh;
            targetBonesSrc = donor.bones;
            targetRootBoneSrc = donor.rootBone;
            targetMat = StockingsDonorLoader.GetMaterial(overrideType);
        }

        // 1. 注入 SMR を取得 or 生成（生成時は空の GO+SMR。mesh/bones は下で統一的に設定）
        var existing = renderers.FirstOrDefault(m => m.name == InjectedName);
        SkinnedMeshRenderer smr;
        bool created = existing == null;
        if (!created)
        {
            smr = existing;
        }
        else
        {
            smr = CreateInjected(chara);
            if (smr == null) return false;
        }

        // 2. mesh + bones を常にセット（新規は初回、既存は type 変更時のみ差替え）
        if (smr.sharedMesh != targetMesh)
        {
            smr.sharedMesh = targetMesh;
            var boneDict = BuildBoneDict(chara);
            RemapBonesInto(smr, targetBonesSrc, targetRootBoneSrc, boneDict, chara.transform);
        }

        if (targetMat != null) smr.sharedMaterial = targetMat;
        else PatchLogger.LogWarning($"[SwimWearStockingPatch] material 未ロード: override={overrideType}");

        smr.gameObject.SetActive(true);

        ApplyBlendShapeTransplant(handle, chara, renderers, 100f);

        PatchLogger.LogInfo($"[SwimWearStockingPatch] 適用: {handle.GetCharID()} override={overrideType} knee={isKneeSocks} created={created} meshVerts={targetMesh.vertexCount} bones={smr.bones?.Length ?? 0}");
        return true;
    }

    private static void ApplyBlendShapeTransplant(CharacterHandle handle, GameObject chara, SkinnedMeshRenderer[] renderers, float shrinkWeight)
    {
        var charId = handle.GetCharID();
        int key = (int)charId;

        if (!StockingsDonorLoader.TryGetLowerDonor(charId, out var lowerDonor) || lowerDonor.sharedMesh == null)
        {
            PatchLogger.LogWarning($"[SwimWearStockingPatch] lower donor 未キャッシュ: {charId}");
            return;
        }

        var swimLower = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower");
        var swimLowerFoot = renderers.FirstOrDefault(m => m.name == "mesh_skin_lower_foot");

        if (!s_backups.TryGetValue(key, out var backup))
        {
            backup = new SwimwearBackup();
            s_backups[key] = backup;
        }

        TransplantInto(swimLower, lowerDonor.sharedMesh, key, shrinkWeight, ref backup.LowerSmr, ref backup.LowerOriginalMesh);
        TransplantInto(swimLowerFoot, lowerDonor.sharedMesh, key, shrinkWeight, ref backup.LowerFootSmr, ref backup.LowerFootOriginalMesh);
    }

    private static void TransplantInto(SkinnedMeshRenderer target, Mesh donorMesh, int charKey, float shrinkWeight,
        ref SkinnedMeshRenderer backupSmr, ref Mesh backupOriginal)
    {
        if (target == null || target.sharedMesh == null) return;

        var cacheKey = (target.sharedMesh.GetInstanceID(), charKey);

        if (!s_transplantedCache.TryGetValue(cacheKey, out var transplanted) || transplanted == null)
        {
            // 元メッシュが既に transplanted だった場合（reload で cache miss）はバックアップ済みと想定しスキップ
            // ここでは target.sharedMesh が元の mesh（swim 生）であることを期待
            transplanted = BuildTransplantedMesh(target.sharedMesh, donorMesh);
            if (transplanted == null) return;
            s_transplantedCache[cacheKey] = transplanted;
        }

        if (target.sharedMesh != transplanted)
        {
            if (backupSmr == null || backupOriginal == null)
            {
                backupSmr = target;
                backupOriginal = target.sharedMesh;
            }
            target.sharedMesh = transplanted;
        }

        // weight 設定
        SetBlendShape(target, "blendShape_skin_lower.skin_stocking", shrinkWeight);
        SetBlendShape(target, "blendShape_skin_lower.skin_socks", shrinkWeight);
        SetBlendShape(target, "blendShape_skin_lower.skin_stocking_lower", shrinkWeight);
        SetBlendShape(target, "blendShape_skin_lower.skin_kneehigh", 0f);
    }

    /// <summary>
    /// targetMesh の頂点構造を保ったまま、donorMesh の blendShape delta を
    /// nearest-neighbor で転写した新 Mesh を生成する。
    /// </summary>
    private static Mesh BuildTransplantedMesh(Mesh targetMesh, Mesh donorMesh)
    {
        if (targetMesh == null || donorMesh == null) return null;
        if (donorMesh.blendShapeCount == 0) return null;

        var targetVerts = targetMesh.vertices;
        var donorVerts = donorMesh.vertices;
        if (targetVerts.Length == 0 || donorVerts.Length == 0) return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // nearest-neighbor 索引: targetVert[i] → donorVert の最近傍 index
        var nearestMap = new int[targetVerts.Length];
        for (int i = 0; i < targetVerts.Length; i++)
        {
            float minD = float.MaxValue;
            int minJ = 0;
            var tv = targetVerts[i];
            for (int j = 0; j < donorVerts.Length; j++)
            {
                var dx = donorVerts[j].x - tv.x;
                var dy = donorVerts[j].y - tv.y;
                var dz = donorVerts[j].z - tv.z;
                float sq = dx * dx + dy * dy + dz * dz;
                if (sq < minD) { minD = sq; minJ = j; }
            }
            nearestMap[i] = minJ;
        }
        long nearestMs = sw.ElapsedMilliseconds;

        var newMesh = Object.Instantiate(targetMesh);
        newMesh.name = targetMesh.name + "_transplanted";

        int shapesAdded = 0;
        foreach (var shapeName in s_transplantShapeNames)
        {
            int idx = donorMesh.GetBlendShapeIndex(shapeName);
            if (idx < 0) continue;
            if (newMesh.GetBlendShapeIndex(shapeName) >= 0) continue;

            int frameCount = donorMesh.GetBlendShapeFrameCount(idx);
            for (int f = 0; f < frameCount; f++)
            {
                var donorDv = new Vector3[donorMesh.vertexCount];
                var donorDn = new Vector3[donorMesh.vertexCount];
                var donorDt = new Vector3[donorMesh.vertexCount];
                donorMesh.GetBlendShapeFrameVertices(idx, f, donorDv, donorDn, donorDt);

                var swimDv = new Vector3[targetMesh.vertexCount];
                var swimDn = new Vector3[targetMesh.vertexCount];
                var swimDt = new Vector3[targetMesh.vertexCount];
                for (int k = 0; k < targetMesh.vertexCount; k++)
                {
                    int src = nearestMap[k];
                    swimDv[k] = donorDv[src];
                    swimDn[k] = donorDn[src];
                }
                float weight = donorMesh.GetBlendShapeFrameWeight(idx, f);
                newMesh.AddBlendShapeFrame(shapeName, weight, swimDv, swimDn, swimDt);
            }
            shapesAdded++;
        }

        sw.Stop();
        PatchLogger.LogInfo(
            $"[SwimWearStockingPatch] blendShape 移植完了: target={targetMesh.name} verts={targetVerts.Length} donorVerts={donorVerts.Length} shapes={shapesAdded} nearest={nearestMs}ms total={sw.ElapsedMilliseconds}ms");

        return newMesh;
    }

    private static void SetBlendShape(SkinnedMeshRenderer smr, string name, float weight)
    {
        if (smr == null || smr.sharedMesh == null) return;
        int idx = smr.sharedMesh.GetBlendShapeIndex(name);
        if (idx >= 0) smr.SetBlendShapeWeight(idx, weight);
    }

    /// <summary>
    /// 水着キャラ配下に空の "mesh_stockings" GO + SkinnedMeshRenderer を作る。
    /// mesh/bones/material は呼び出し側が設定する。親は既存 mesh_skin_lower と揃える。
    /// </summary>
    private static SkinnedMeshRenderer CreateInjected(GameObject chara)
    {
        var referenceSmr = chara.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(m => m.name == "mesh_skin_lower");
        var parentTransform = referenceSmr != null ? referenceSmr.transform.parent : chara.transform;

        var go = new GameObject(InjectedName);
        go.transform.SetParent(parentTransform, false);

        return go.AddComponent<SkinnedMeshRenderer>();
    }

    /// <summary>
    /// donor の bones[] を chara 階層の同名 Transform にリマップして target SMR に適用する。
    /// </summary>
    private static void RemapBonesInto(SkinnedMeshRenderer target, Transform[] donorBones, Transform donorRootBone,
        Dictionary<string, Transform> boneDict, Transform fallback)
    {
        var src = donorBones ?? System.Array.Empty<Transform>();
        var newBones = new Transform[src.Length];
        int missing = 0;
        for (int i = 0; i < src.Length; i++)
        {
            var db = src[i];
            if (db != null && boneDict.TryGetValue(db.name, out var mapped))
                newBones[i] = mapped;
            else
            {
                missing++;
                newBones[i] = fallback;
            }
        }
        target.bones = newBones;

        if (donorRootBone != null && boneDict.TryGetValue(donorRootBone.name, out var root))
            target.rootBone = root;
        else
            target.rootBone = fallback;

        if (missing > 0)
            PatchLogger.LogWarning($"[SwimWearStockingPatch] bone 未対応 {missing}/{src.Length}");
    }

    /// <summary>
    /// キャラ配下の Transform を名前 → Transform の辞書にする。名前衝突時は先勝ち（最初に見つけた方を採用）し、
    /// 衝突件数をまとめてログに出す（L_hand / R_hand は名前が違うので衝突しないが、LOD 複製やミラーノードで同名が出ると後勝ち上書きで誤マッピングする恐れがあるため）。
    /// </summary>
    private static Dictionary<string, Transform> BuildBoneDict(GameObject chara)
    {
        var dict = new Dictionary<string, Transform>(System.StringComparer.OrdinalIgnoreCase);
        int collisions = 0;
        foreach (var t in chara.GetComponentsInChildren<Transform>(true))
        {
            if (!dict.ContainsKey(t.name))
                dict[t.name] = t;
            else
                collisions++;
        }
        if (collisions > 0)
            PatchLogger.LogWarning($"[SwimWearStockingPatch] bone 名衝突 {collisions} 件を先勝ちで無視 (chara={chara.name})");
        return dict;
    }
}
