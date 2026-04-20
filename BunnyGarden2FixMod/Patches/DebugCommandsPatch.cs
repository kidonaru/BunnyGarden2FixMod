using BunnyGarden2FixMod.Utils;
using Cysharp.Threading.Tasks;
using GB;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// 開発・検証用のデバッグコマンドを実行する MonoBehaviour。
///
/// ■ 使い方
///   Config の Debug.CommandsEnabled を true にすると有効化。
///   F10 キーでミニメニューの表示／非表示を切り替え、メニュー内のボタンから各コマンドを発火。
///
/// ■ コマンド一覧
///   - 「いきなりエンディング再生」:
///       GBSystem.ChangeSceneAsyncFromTo(currentScene, "EpilogueScene") を呼ぶ。
///       エンディング種別は EpilogueScene 側で QueryEndingType() が現在のセーブ状態から自動判定する。
///       遷移元は BarScene / HomeScene / AfterScene / HolidayAfterScene のホワイトリスト制。
///       二重発火防止のため _busy フラグで遷移完了まで再発火をブロック。
///   - 「所持金 +10000」:
///       GameData.AddMoney(10000, immediate: true) を呼ぶ。DOTween/SE の重複を避ける。
///
/// ■ 設計方針
///   - UI は uGUI（Canvas + Image + Button + TextMeshProUGUI）で構築する。UpdateChecker の
///     ダイアログと同じ方式。IMGUI はクリック判定・フォント・見た目の問題があるため不採用。
///   - コマンドは GUI ボタン経由で発火する。ワンキー直打ちで発火する頻用コマンドは設けない
///     （UI 一貫性のため）。追加コマンドは CreateButton を増やすだけで拡張可能。
///   - async メソッド ChangeSceneAsyncFromTo は Harmony パッチ不可のため、外部から
///     UniTaskVoid で fire-and-forget 呼び出し。
///   - F10 キー検知は UnityEngine.InputSystem の Keyboard.current（CastOrderPatch と整合）。
/// </summary>
public class DebugCommandsPatch : MonoBehaviour
{
    private static readonly string[] EndingJumpAllowedScenes =
    {
        "BarScene",
        "HomeScene",
        "AfterScene",
        "HolidayAfterScene",
    };

    private static bool _busy;

    private GameObject _menuRoot;
    private TextMeshProUGUI _sceneLabel;
    private Button _endingButton;

    public static void Initialize(GameObject parent)
    {
        parent.AddComponent<DebugCommandsPatch>();
        if (Plugin.ConfigDebugCommandsEnabled.Value)
            PatchLogger.LogWarning("[DebugCommandsPatch] デバッグコマンド有効（セーブ破壊注意）");
    }

    private void Update()
    {
        if (!Plugin.ConfigDebugCommandsEnabled.Value)
        {
            if (_menuRoot != null) HideMenu();
            return;
        }

        if (Keyboard.current?[Key.F10].wasPressedThisFrame == true)
        {
            if (_menuRoot != null) HideMenu();
            else ShowMenu();
        }

        if (_menuRoot == null) return;

        if (_sceneLabel != null)
            _sceneLabel.text = $"Scene: {GBSystem.GetCurrentSceneName()}";
        if (_endingButton != null)
            _endingButton.interactable = !_busy;
    }

    // ── メニュー構築 ─────────────────────────────────────────────────

    private void ShowMenu()
    {
        if (_menuRoot != null) return;

        var font = FindGameFont();

        var root = new GameObject("BG2DebugCommandsMenu");
        Object.DontDestroyOnLoad(root);
        _menuRoot = root;

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9998;

        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        root.AddComponent<GraphicRaycaster>();

        // パネル（画面右上固定）
        var panel = MakeImage(root.transform, "Panel", new Color(0.1f, 0.1f, 0.12f, 0.92f));
        var prt = panel.rectTransform;
        prt.anchorMin = prt.anchorMax = new Vector2(1f, 1f);
        prt.pivot = new Vector2(1f, 1f);
        prt.anchoredPosition = new Vector2(-20f, -20f);
        prt.sizeDelta = new Vector2(420f, 320f);

        // タイトル
        MakeLabel(panel.transform, "Title",
            "Debug Commands (F10 で閉じる)",
            anchorY: 1f, offsetY: -18f, fontSize: 22, bold: true,
            color: new Color(1f, 0.85f, 0.3f), font: font);

        // Scene ラベル
        _sceneLabel = MakeLabel(panel.transform, "SceneLabel",
            $"Scene: {GBSystem.GetCurrentSceneName()}",
            anchorY: 1f, offsetY: -56f, fontSize: 18,
            color: new Color(0.85f, 0.85f, 0.85f), font: font);

        // エンディング強制再生ボタン
        _endingButton = MakeButton(panel.transform, "EndingBtn",
            "いきなりエンディング再生\n(現セーブ状態で自動判定)",
            anchorY: 1f, offsetY: -90f,
            size: new Vector2(380f, 64f),
            bgColor: new Color(0.22f, 0.42f, 0.75f), font: font);
        _endingButton.onClick.AddListener(OnEndingClicked);

        // 所持金 +10000 ボタン
        var moneyBtn = MakeButton(panel.transform, "MoneyBtn",
            "所持金 +10000",
            anchorY: 1f, offsetY: -164f,
            size: new Vector2(380f, 52f),
            bgColor: new Color(0.35f, 0.35f, 0.4f), font: font);
        moneyBtn.onClick.AddListener(() => AddMoney(10000));
    }

    private void HideMenu()
    {
        if (_menuRoot == null) return;
        Object.Destroy(_menuRoot);
        _menuRoot = null;
        _sceneLabel = null;
        _endingButton = null;
    }

    // ── コマンド ─────────────────────────────────────────────────────

    private void OnEndingClicked()
    {
        HideMenu(); // 遷移中にメニューを残さない
        ForceEndingAsync().Forget();
    }

    private static async UniTaskVoid ForceEndingAsync()
    {
        if (_busy) return;

        var sys = GBSystem.Instance;
        if (sys == null)
        {
            PatchLogger.LogWarning("[DebugCommandsPatch] GBSystem.Instance が null");
            return;
        }

        var from = GBSystem.GetCurrentSceneName();
        if (string.IsNullOrEmpty(from))
        {
            PatchLogger.LogWarning("[DebugCommandsPatch] 現在シーンの特定に失敗");
            return;
        }

        bool allowed = false;
        foreach (var s in EndingJumpAllowedScenes)
        {
            if (s == from) { allowed = true; break; }
        }
        if (!allowed)
        {
            PatchLogger.LogWarning($"[DebugCommandsPatch] {from} からのエンディング遷移は未サポート (許可: {string.Join(", ", EndingJumpAllowedScenes)})");
            return;
        }

        _busy = true;
        try
        {
            PatchLogger.LogInfo($"[DebugCommandsPatch] {from} -> EpilogueScene 強制遷移");
            await sys.ChangeSceneAsyncFromTo(from, "EpilogueScene");
        }
        finally
        {
            _busy = false;
        }
    }

    private static void AddMoney(long amount)
    {
        var data = GBSystem.Instance?.RefGameData();
        if (data == null)
        {
            PatchLogger.LogWarning("[DebugCommandsPatch] GameData が取得できない");
            return;
        }

        data.AddMoney(amount, immediate: true);
        PatchLogger.LogInfo($"[DebugCommandsPatch] AddMoney({amount}, immediate) 実行");
    }

    // ── UI ヘルパー（UpdateChecker と同方式）─────────────────────────

    private static TMP_FontAsset FindGameFont()
    {
        var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        foreach (var f in allFonts)
        {
            if (f != null && f.HasCharacter('の'))
                return f;
        }
        return null;
    }

    private static Image MakeImage(Transform parent, string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        return img;
    }

    private static TextMeshProUGUI MakeLabel(Transform parent, string name, string text,
        float anchorY, float offsetY, int fontSize, bool bold = false,
        Color color = default, TMP_FontAsset font = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(0f, anchorY);
        rt.anchorMax = new Vector2(1f, anchorY);
        rt.pivot = new Vector2(0.5f, anchorY);
        rt.sizeDelta = new Vector2(-20f, fontSize * 2.4f);
        rt.anchoredPosition = new Vector2(0f, offsetY);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color == default ? Color.white : color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return tmp;
    }

    private static Button MakeButton(Transform parent, string name, string label,
        float anchorY, float offsetY, Vector2 size, Color bgColor,
        TMP_FontAsset font = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, anchorY);
        rt.pivot = new Vector2(0.5f, anchorY);
        rt.sizeDelta = size;
        rt.anchoredPosition = new Vector2(0f, offsetY);

        var img = go.AddComponent<Image>();
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        var cb = btn.colors;
        cb.highlightedColor = bgColor * 1.25f;
        cb.pressedColor = bgColor * 0.75f;
        cb.disabledColor = new Color(bgColor.r * 0.5f, bgColor.g * 0.5f, bgColor.b * 0.5f, 0.8f);
        btn.colors = cb;

        var textGO = new GameObject("Label", typeof(RectTransform));
        textGO.transform.SetParent(go.transform, false);
        var trt = (RectTransform)textGO.transform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = trt.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = label;
        tmp.fontSize = 18;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontStyle = FontStyles.Bold;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;

        return btn;
    }
}
