using System;
using System.Reflection;
using BunnyGarden2FixMod.Utils;
using GB;
using GB.Bar.MiniGame;
using GB.Scene;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 競馬のベット画面でキーボードから直接金額を数値入力できるようにするパッチ。
///
/// <para>
/// <b>使い方</b><br/>
/// ベット画面表示中に数字キー（1〜9）を押すと入力モードに入る。<br/>
/// 入力中はUp/Down操作がロックされ、代わりに以下のキー操作が有効になる：
/// <list type="bullet">
///   <item><b>0〜9（テンキー含む）</b>: 桁を追加</item>
///   <item><b>Backspace</b>: 末尾1桁を削除</item>
///   <item><b>Enter / Numpad Enter</b>: 確定（1000円単位に切り捨て・範囲クランプ）</item>
///   <item><b>Escape</b>: 入力を破棄して元の金額に戻す（ベット画面は閉じない）</item>
/// </list>
/// </para>
///
/// <para>
/// <b>バリデーション（確定時）</b>
/// <list type="bullet">
///   <item>1000円単位に切り捨て（例: 12345 → 12000）</item>
///   <item>最小 1,000円</item>
///   <item>最大 100,000円 + 現所持金（1000円単位切り捨て）</item>
/// </list>
/// </para>
/// </summary>
[HarmonyPatch(typeof(HorseRacing), "Update")]
public class HorseRacingBetInputPatch
{
    // ── 入力状態 ─────────────────────────────────────────────────
    private static string s_buffer = "";
    private static bool s_isTyping;
    private static long s_prevBetMoney;

    // ── リフレクションキャッシュ ──────────────────────────────────
    private static readonly FieldInfo s_fBetting =
        AccessTools.Field(typeof(HorseRacing), "m_betting");
    private static readonly FieldInfo s_fBetMoney =
        AccessTools.Field(typeof(HorseRacing), "m_betMoney");
    private static readonly FieldInfo s_fBetStep =
        AccessTools.Field(typeof(HorseRacing), "m_betStep");
    private static readonly MethodInfo s_mGetBetWindow =
        AccessTools.PropertyGetter(typeof(HorseRacing), "BetWindow");

    // ── 数字キーマップ（通常＋テンキー）─────────────────────────
    private static readonly (Key key, char ch)[] s_digitKeys =
    {
        (Key.Digit1, '1'), (Key.Digit2, '2'), (Key.Digit3, '3'),
        (Key.Digit4, '4'), (Key.Digit5, '5'), (Key.Digit6, '6'),
        (Key.Digit7, '7'), (Key.Digit8, '8'), (Key.Digit9, '9'),
        (Key.Digit0, '0'),
        (Key.Numpad1, '1'), (Key.Numpad2, '2'), (Key.Numpad3, '3'),
        (Key.Numpad4, '4'), (Key.Numpad5, '5'), (Key.Numpad6, '6'),
        (Key.Numpad7, '7'), (Key.Numpad8, '8'), (Key.Numpad9, '9'),
        (Key.Numpad0, '0'),
    };

    static HorseRacingBetInputPatch()
    {
        Plugin.GUICallback += OnGUI;
    }

    // ── GUI オーバーレイ ─────────────────────────────────────────
    private static void OnGUI()
    {
        if (!s_isTyping) return;

        string display = string.IsNullOrEmpty(s_buffer) ? "0" : s_buffer;
        GUI.color = new Color(1f, 0.93f, 0.4f); // 黄色
        GUILayout.Label($"掛け金入力中: {display}円");
        GUILayout.Label($"[Enter=確定 / Esc=キャンセル / Backspace=削除]");
        GUI.color = Color.white;
    }

    // ── Prefix パッチ ────────────────────────────────────────────
    /// <returns>
    /// <c>true</c>  → 元の Update() を続けて実行する<br/>
    /// <c>false</c> → 元の Update() を抑制する（入力モード中）
    /// </returns>
    static bool Prefix(HorseRacing __instance)
    {
        if (s_fBetting == null || s_fBetMoney == null) return true;

        bool isBetting = (bool)s_fBetting.GetValue(__instance);

        // ベット画面が閉じた → 状態リセット
        if (!isBetting)
        {
            if (s_isTyping) ExitTypingMode();
            return true;
        }

        var kb = Keyboard.current;
        if (kb == null) return true;

        // ── 通常モード: 数字キーで入力モード開始 ─────────────────
        if (!s_isTyping)
        {
            foreach (var (key, ch) in s_digitKeys)
            {
                if (!kb[key].wasPressedThisFrame) continue;
                if (ch == '0') return true; // 先頭0は無視して通常操作に委ねる

                s_prevBetMoney = (long)s_fBetMoney.GetValue(__instance);
                s_buffer = ch.ToString();
                s_isTyping = true;
                ApplyBuffer(__instance);
                PatchLogger.LogInfo($"[HorseRacingBetInput] 入力モード開始: '{s_buffer}'");
                return false;
            }
            return true; // 数字以外はゲームの通常操作へ
        }

        // ── 入力モード: キー処理 ──────────────────────────────────

        // Enter / Numpad Enter → 確定
        if (kb[Key.Enter].wasPressedThisFrame || kb[Key.NumpadEnter].wasPressedThisFrame)
        {
            ConfirmInput(__instance);
            return false;
        }

        // Escape → キャンセル（ベット画面は閉じない）
        if (kb[Key.Escape].wasPressedThisFrame)
        {
            CancelInput(__instance);
            return false;
        }

        // Backspace → 末尾1桁削除
        if (kb[Key.Backspace].wasPressedThisFrame)
        {
            if (s_buffer.Length > 0)
                s_buffer = s_buffer[..^1];
            ApplyBuffer(__instance);
            CallSetMoney(__instance, (long)s_fBetMoney.GetValue(__instance));
            return false;
        }

        // 数字キー → 桁を追加（最大10桁）
        foreach (var (key, ch) in s_digitKeys)
        {
            if (!kb[key].wasPressedThisFrame) continue;
            if (s_buffer.Length < 10)
                s_buffer += ch;
            ApplyBuffer(__instance);
            CallSetMoney(__instance, (long)s_fBetMoney.GetValue(__instance));
            return false;
        }

        // その他のキー → ゲームの上下操作などを抑制しつつ表示のみ更新
        CallSetMoney(__instance, (long)s_fBetMoney.GetValue(__instance));
        return false;
    }

    // ── 内部ヘルパー ─────────────────────────────────────────────

    /// <summary>バッファをパースして m_betMoney に反映（プレビュー用、未検証）</summary>
    private static void ApplyBuffer(HorseRacing instance)
    {
        if (string.IsNullOrEmpty(s_buffer))
        {
            s_fBetMoney.SetValue(instance, 0L);
            return;
        }

        if (long.TryParse(s_buffer, out long val))
        {
            // 確定前の表示では上限を少し緩めに（超過分は確定時にクランプ）
            long cap = CalcMaxBet() + 99_999L;
            val = Math.Min(val, cap);
            s_fBetMoney.SetValue(instance, val);
        }
    }

    /// <summary>
    /// 入力確定: 1000円単位に切り捨て、[最小1000, 最大] にクランプして適用。
    /// </summary>
    private static void ConfirmInput(HorseRacing instance)
    {
        long confirmed = s_prevBetMoney; // デフォルト: 入力前の値

        if (!string.IsNullOrEmpty(s_buffer) && long.TryParse(s_buffer, out long parsed))
        {
            long rounded = (parsed / 1000L) * 1000L;        // 1000円単位に切り捨て
            long maxBet = CalcMaxBet();
            confirmed = Math.Max(1000L, Math.Min(rounded, maxBet)); // [1000, max] クランプ
        }

        s_fBetMoney.SetValue(instance, confirmed);
        s_fBetStep.SetValue(instance, 1000L); // ステップをリセット
        ExitTypingMode();
        CallSetMoney(instance, confirmed);
        GBSystem.Instance?.PlayDecideSE();

        PatchLogger.LogInfo($"[HorseRacingBetInput] 確定: {confirmed}円 (入力: '{s_buffer}')");
    }

    /// <summary>入力キャンセル: 元の金額に戻してモードを抜ける。</summary>
    private static void CancelInput(HorseRacing instance)
    {
        s_fBetMoney.SetValue(instance, s_prevBetMoney);
        ExitTypingMode();
        CallSetMoney(instance, s_prevBetMoney);
        GBSystem.Instance?.PlayCancelSE();

        PatchLogger.LogInfo($"[HorseRacingBetInput] キャンセル: {s_prevBetMoney}円 に戻しました");
    }

    private static void ExitTypingMode()
    {
        s_isTyping = false;
        s_buffer = "";
    }

    /// <summary>BetWindow.SetMoney() をリフレクション経由で呼ぶ。</summary>
    private static void CallSetMoney(HorseRacing instance, long money)
    {
        if (s_mGetBetWindow == null) return;
        try
        {
            var betWindow = s_mGetBetWindow.Invoke(instance, null) as BetWindow;
            betWindow?.SetMoney(money);
        }
        catch (Exception ex)
        {
            PatchLogger.LogWarning($"[HorseRacingBetInput] SetMoney 呼び出し失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// ゲームの BetUp() と同じロジックで最大掛け金を計算する。
    /// max = 100,000 + 現所持金（1000円以下切り捨て）
    /// </summary>
    private static long CalcMaxBet()
    {
        long money = GBSystem.Instance?.RefGameData()?.GetMoney() ?? 0L;
        long max = 100_000L;
        if (money > 0L) max += money / 1000L * 1000L;
        return max;
    }
}
