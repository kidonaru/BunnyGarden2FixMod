using BunnyGarden2FixMod.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod.Patches.Settings;

/// <summary>
/// F9 設定パネルのライフサイクル / 入力ハンドラ。
/// SettingsView を保持し、F9 / Esc / ↑↓←→ Shift Tab Space Enter 1-9 を解釈する。
/// マウス入力抑制判定 (ShouldSuppressMouseInput) は Plugin.cs のマウス系 Suppress パッチから参照される。
/// </summary>
public class SettingsController : MonoBehaviour
{
    public static SettingsController Instance { get; private set; }

    public static void Initialize(GameObject parent)
    {
        var host = new GameObject("BG2SettingsController");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<SettingsController>();
    }

    private SettingsView m_view;

    public static bool ShouldSuppressMouseInput()
    {
        return Instance != null && Instance.m_view != null && Instance.m_view.IsPointerOverPanel();
    }

    /// <summary>
    /// いずれかのキーバインド行でキャプチャ中かどうかを返す。
    /// HotkeyConfig / Suppress パッチからキャプチャ中の入力遮断判定に使用する。
    /// </summary>
    public static bool IsAnyCapturing =>
        Instance != null && Instance.m_view != null && Instance.m_view.IsCapturingKey;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            PatchLogger.LogWarning("[SettingsController] 既に存在するため新規生成をキャンセルします");
            Destroy(gameObject);
            return;
        }
        Instance = this;
        m_view = gameObject.AddComponent<SettingsView>();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void Update()
    {
        if (m_view == null) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        // ── F9: 開閉トグル ─────────────────
        if (kb[Key.F9].wasPressedThisFrame)
        {
            if (m_view.IsShown) m_view.Hide();
            else                m_view.Show();
            return;
        }

        if (!m_view.IsShown) return;

        // ── KeyBinding キャプチャ中: 通常ナビゲーションをスキップしてキー押下を View に渡す ──
        if (m_view.IsCapturingKey)
        {
            // パネル外でのマウスクリックでもキャプチャを解除する (パネル内クリックは
            // SettingsView の MouseDownEvent ハンドラ側で処理され、ここには届かない)。
            var mouse = Mouse.current;
            if (mouse != null)
            {
                bool clicked = mouse.leftButton.wasPressedThisFrame
                            || mouse.rightButton.wasPressedThisFrame
                            || mouse.middleButton.wasPressedThisFrame;
                if (clicked && !m_view.IsPointerOverPanel())
                {
                    m_view.CancelKeyCapture();
                    return;
                }
            }

            // Enum.GetValues で全 Key を走査 (Unity InputSystem 版差に強い)。
            // None / AnyKey / IMESelected は仮想キーや特殊状態のため除外する。
            foreach (Key key in System.Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None) continue;
                // IMESelected は [Obsolete] enum 値、AnyKey は仮想キー。直接参照せず name で除外する
                // (Unity 版差で名前が変わっても列挙ベース判定なら build break しない)
                var keyName = key.ToString();
                if (keyName == "IMESelected" || keyName == "AnyKey") continue;
                try
                {
                    if (kb[key].wasPressedThisFrame)
                    {
                        m_view.HandleCapturedKey(key);
                        return;
                    }
                }
                catch
                {
                    // 一部の Key 値は kb[key] でアクセスエラーになる可能性 (Unity 版差) → 無視
                }
            }
            return;
        }

        // ── Esc: 閉じる ───────────────────
        if (kb[Key.Escape].wasPressedThisFrame)
        {
            m_view.Hide();
            return;
        }

        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

        // ── ↑↓ で行移動 ──────────────────
        if (kb[Key.UpArrow].wasPressedThisFrame)   m_view.HandleKeyArrowUp();
        if (kb[Key.DownArrow].wasPressedThisFrame) m_view.HandleKeyArrowDown();

        // ── ←→ で slider 値増減 ──────────
        if (kb[Key.LeftArrow].wasPressedThisFrame)  m_view.HandleKeyArrowLeft(shift);
        if (kb[Key.RightArrow].wasPressedThisFrame) m_view.HandleKeyArrowRight(shift);

        // ── Space / Enter で toggle ───────
        if (kb[Key.Space].wasPressedThisFrame || kb[Key.Enter].wasPressedThisFrame)
            m_view.HandleKeyConfirm();

        // ── Tab / Shift+Tab でカテゴリ循環 ─
        if (kb[Key.Tab].wasPressedThisFrame)
        {
            if (shift) m_view.HandleKeyTabPrev();
            else       m_view.HandleKeyTabNext();
        }

        // ── 1-9 でカテゴリ直接ジャンプ ────
        // 注意: UnityEngine.InputSystem.Key の Digit0..Digit9 は連続値である前提（連番依存）。
        // Unity InputSystem の API 安定性に依存する箇所なので、Unity アップデート時は要確認。
        for (int i = 0; i < 9; i++)
        {
            var key = Key.Digit1 + i;
            if (kb[key].wasPressedThisFrame)
            {
                m_view.HandleKeyCategoryJump(i + 1);
                break;
            }
        }
    }
}
