using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// ラベル + 値表示 + 横スライダーを 1 つに束ねたコンポーネント。
/// 上段にラベル(左)/現在値(右)、下段にスライダー本体を並べる縦構成。
///
/// 使い方:
///   var s = new UITSlider();
///   s.Setup("押し出し", 0f, 0.01f, font, v =&gt; $"{v * 1000f:F1}mm");
///   s.OnValueChanged += v =&gt; ...
///   parent.Add(s);
///   s.SetValue(0.0005f); // event を発火させずに値を反映
/// </summary>
public class UITSlider : VisualElement
{
    /// <summary>値が変わるたびに発火（ドラッグ中も連続発火）。軽量な追従用。</summary>
    public event Action<float> OnValueChanged;

    /// <summary>値の操作が確定したタイミングで発火（ドラッグ終了 / クリック離し）。
    /// 反映が重い処理（メッシュ再構築など）はこちらに繋ぐ。
    /// プログラムからの SetValue / SetRange では発火しない。</summary>
    public event Action<float> OnValueCommitted;

    private Label m_nameLabel;
    private Label m_valueLabel;
    private Slider m_slider;
    private Func<float, string> m_format;
    private bool m_suppressEvents;
    private float m_lastCommittedValue;
    private float m_step; // 0 = ホイール無効

    public float Value => m_slider != null ? m_slider.value : 0f;

    public void Setup(string label, float min, float max, Font font = null, Func<float, string> formatter = null)
    {
        Clear();
        m_format = formatter ?? (v => v.ToString("F2"));

        style.flexDirection = FlexDirection.Column;
        style.marginTop = 4;

        var row = UITFactory.CreateRow();
        row.style.alignItems = Align.Center;
        Add(row);

        m_nameLabel = UITFactory.CreateLabel(label, 10, UITTheme.Text.Primary, font, TextAnchor.MiddleLeft);
        m_nameLabel.style.flexShrink = 0;
        m_nameLabel.style.flexGrow = 1;
        row.Add(m_nameLabel);

        m_valueLabel = UITFactory.CreateLabel(m_format(min), 10, UITTheme.Text.Secondary, font, TextAnchor.MiddleRight);
        m_valueLabel.style.flexShrink = 0;
        row.Add(m_valueLabel);

        m_slider = new Slider(min, max)
        {
            value = min,
            showInputField = false,
        };
        // Unity Slider 内蔵の矢印キーナビゲーションを無効化する。
        // SettingsController が InputSystem 経由で NudgeSelectedSlider を呼ぶため、
        // focusable=true のままだと選択中でないスライダーまで反応してしまう。
        m_slider.focusable = false;
        m_slider.style.marginTop = 0;
        m_slider.style.marginBottom = 0;
        // 借用 theme stylesheet が Slider USS を含まない場合に track/dragger が高さ 0 で
        // 描画されないことがあるので、コンテナとトラッカーに明示寸法を入れる。
        m_slider.style.height = 18;
        m_slider.style.minHeight = 18;
        m_slider.RegisterCallback<GeometryChangedEvent>(_ =>
        {
            StyleSliderInternals(m_slider);
            UpdateFillWidth();
        });
        m_slider.RegisterValueChangedCallback(evt =>
        {
            if (m_valueLabel != null) m_valueLabel.text = m_format(evt.newValue);
            UpdateFillWidth();
            if (m_suppressEvents) return;
            OnValueChanged?.Invoke(evt.newValue);
        });
        // ドラッグ終了 / クリック離しで commit イベントを発火する。
        // PointerUpEvent は dragger / track どちらでもバブルアップで届く。
        // TrickleDown.TrickleDown を使うと dragger に capture される前に取れて取りこぼしを防げる。
        m_slider.RegisterCallback<PointerUpEvent>(_ => CommitIfChanged(), TrickleDown.TrickleDown);
        // ホイール対応: ±m_step。m_step==0 のときは何もしない（既存呼び出し元の挙動を変えないため）。
        m_slider.RegisterCallback<WheelEvent>(OnWheel);
        m_lastCommittedValue = m_slider.value;
        Add(m_slider);
    }

    private void CommitIfChanged()
    {
        if (m_slider == null) return;
        if (m_suppressEvents) return;
        if (Mathf.Approximately(m_slider.value, m_lastCommittedValue)) return;
        m_lastCommittedValue = m_slider.value;
        OnValueCommitted?.Invoke(m_slider.value);
    }

    /// <summary>tracker 内の active-fill 要素の幅を 0..value..max の比率で更新する。</summary>
    private void UpdateFillWidth()
    {
        if (m_slider == null) return;
        var tracker = m_slider.Q(className: "unity-base-slider__tracker");
        if (tracker == null) return;
        var fill = tracker.Q("active-fill");
        if (fill == null) return;
        float range = m_slider.highValue - m_slider.lowValue;
        if (range <= 0f) { fill.style.width = 0; return; }
        float pct = Mathf.Clamp01((m_slider.value - m_slider.lowValue) / range);
        fill.style.width = new Length(pct * 100f, LengthUnit.Percent);
    }

    /// <summary>イベント発火させずに値を反映する（Render 等で外部状態を流し込む用）。</summary>
    public void SetValue(float v)
    {
        if (m_slider == null) return;
        m_suppressEvents = true;
        try
        {
            m_slider.value = Mathf.Clamp(v, m_slider.lowValue, m_slider.highValue);
            if (m_valueLabel != null) m_valueLabel.text = m_format(m_slider.value);
            m_lastCommittedValue = m_slider.value;
        }
        finally { m_suppressEvents = false; }
    }

    /// <summary>highValue を変更する。現在値が範囲外になれば clamp する。</summary>
    public void SetRange(float min, float max)
    {
        if (m_slider == null) return;
        m_suppressEvents = true;
        try
        {
            m_slider.lowValue = min;
            m_slider.highValue = max;
            m_slider.value = Mathf.Clamp(m_slider.value, min, max);
            if (m_valueLabel != null) m_valueLabel.text = m_format(m_slider.value);
            m_lastCommittedValue = m_slider.value;
        }
        finally { m_suppressEvents = false; }
    }

    public void SetLabel(string text)
    {
        if (m_nameLabel != null) m_nameLabel.text = text;
    }

    /// <summary>
    /// ホイール 1 ノッチで ±step。delta.y は下回転で +、上回転で - なので符号反転する。
    /// step が未設定 (==0) または負値のときは既存の挙動を変えないため何もしない。
    /// </summary>
    public void SetStep(float step)
    {
        // !(step > 0f) は step が 0 / 負 / NaN のとき true。NaN を素通りさせないため否定形で書く。
        m_step = !(step > 0f) ? 0f : step;
    }

    private void OnWheel(WheelEvent evt)
    {
        if (m_slider == null) return;
        if (m_step <= 0f) return; // step 未設定なら親へバブルさせて従来挙動を維持
        if (!m_slider.enabledInHierarchy) return; // disabled なら無視（親 ScrollView を奪わない）
        // 横ホイール (delta.y==0) を明示的に no-op。Unity の Mathf.Sign(0f) は 1f を返すので
        // delta.y を直接見るほうが安全（Math.Sign とは挙動が違う点に注意）。
        if (Mathf.Approximately(evt.delta.y, 0f)) return;
        float dir = -Mathf.Sign(evt.delta.y); // up=+, down=-
        float next = Mathf.Clamp(m_slider.value + dir * m_step, m_slider.lowValue, m_slider.highValue);
        // ホイール対応スライダーは ScrollView 内でも値変化なし時にスクロール乗っ取りされたくないので、
        // 範囲端で動かなくても StopPropagation して親 ScrollView を奪わない（明示的にホイール対応した意思表示）。
        if (Mathf.Approximately(next, m_slider.value))
        {
            evt.StopPropagation();
            return;
        }
        m_slider.value = next; // RegisterValueChangedCallback が OnValueChanged 発火
        // ホイール操作は 1 ノッチ即 commit 扱い
        m_lastCommittedValue = m_slider.value;
        OnValueCommitted?.Invoke(m_slider.value);
        evt.StopPropagation();
    }

    /// <summary>
    /// テーマ USS に依存せずトラック/ドラッガーが見えるよう、UnityEngine.UIElements.Slider 内部の
    /// dragger-border/tracker/dragger 要素に最小限の塗り・寸法を入れる。
    /// 名前は Unity 公式 USS の class 名 (.unity-base-slider__tracker など) と一致。
    /// </summary>
    /// <summary>
    /// Setup 後に呼ぶ。F9 設定パネル等で使う、ラベル無し横並びレイアウトに切り替える。
    /// 表示構造: [slider本体] [値ラベル(指定幅, 右寄せ)] を 1 行で並べる。
    /// sliderFlex=true のとき、slider 本体は flex で残り幅を埋める。false のとき固定幅。
    /// 既存の縦レイアウト呼び出し元 (CostumeChanger 等) には影響しない。
    /// </summary>
    public void SetCompactLayout(float sliderWidth = 160f, float valueWidth = 50f, bool sliderFlex = false)
    {
        // ルート自体を横並びに変更
        style.flexDirection = FlexDirection.Row;
        style.alignItems = Align.Center;
        style.marginTop = 0;

        // sliderFlex=true のとき、UITSlider 自体も親 row 内で伸びる必要がある
        // （内側の m_slider が flexGrow=1 でも親が伸びないと 0px に潰れる）
        if (sliderFlex)
        {
            style.flexGrow = 1;
            style.flexShrink = 1;
            style.minWidth = 0;
        }

        // Setup() が作った row（m_nameLabel + m_valueLabel を持つ上段コンテナ）を非表示にする
        // this の子は [0]=row, [1]=m_slider の順で Add されている
        if (childCount > 0 && this[0] is VisualElement topRow)
            topRow.style.display = DisplayStyle.None;

        // m_slider を横並びに配置
        if (m_slider != null)
        {
            if (sliderFlex)
            {
                // flex モード: 残り幅をすべて埋める
                m_slider.style.flexGrow = 1;
                m_slider.style.flexShrink = 1;
                m_slider.style.width = StyleKeyword.Auto;
                m_slider.style.minWidth = 0;
                m_slider.style.maxWidth = StyleKeyword.None;
            }
            else
            {
                // 固定幅モード: minWidth / maxWidth も固定しないと内部の unity-base-slider__tracker が伸縮してしまう
                m_slider.style.flexGrow = 0;
                m_slider.style.flexShrink = 0;
                m_slider.style.width = sliderWidth;
                m_slider.style.minWidth = sliderWidth;
                m_slider.style.maxWidth = sliderWidth;
            }
            m_slider.style.marginRight = 8;
        }

        // m_valueLabel を topRow から取り外し、this 直下に右寄せで追加
        // 幅を固定しないと長い値テキストで行が広がってしまうため min/max も固定する
        if (m_valueLabel != null)
        {
            m_valueLabel.RemoveFromHierarchy();
            m_valueLabel.style.width = valueWidth;
            m_valueLabel.style.minWidth = valueWidth;
            m_valueLabel.style.maxWidth = valueWidth;
            m_valueLabel.style.flexShrink = 0;
            m_valueLabel.style.flexGrow = 0;
            m_valueLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            m_valueLabel.style.whiteSpace = WhiteSpace.NoWrap;
            m_valueLabel.style.overflow = Overflow.Hidden;
            m_valueLabel.style.textOverflow = TextOverflow.Ellipsis;
            Add(m_valueLabel);
        }
    }

    private static void StyleSliderInternals(Slider slider)
    {
        // 想定 slider height = 18, tracker height = 4 → tracker.top = 7 で中央
        const float kSliderHeight = 18f;
        const float kTrackerHeight = 4f;
        const float kTrackerTop = (kSliderHeight - kTrackerHeight) / 2f;
        const float kDraggerSize = 12f;

        var tracker = slider.Q(className: "unity-base-slider__tracker");
        if (tracker != null)
        {
            tracker.style.height = kTrackerHeight;
            tracker.style.top = kTrackerTop;
            tracker.style.backgroundColor = UITTheme.Tab.InactiveFill;
            tracker.style.borderTopLeftRadius = 2;
            tracker.style.borderTopRightRadius = 2;
            tracker.style.borderBottomLeftRadius = 2;
            tracker.style.borderBottomRightRadius = 2;
            tracker.style.overflow = Overflow.Hidden; // fill が角丸内に収まるように

            // アクティブ部分（左端〜dragger の間）の塗り。tracker 内に絶対配置で重ねる。
            var fill = tracker.Q("active-fill");
            if (fill == null)
            {
                fill = new VisualElement { name = "active-fill" };
                fill.pickingMode = PickingMode.Ignore;
                fill.style.position = Position.Absolute;
                fill.style.left = 0;
                fill.style.top = 0;
                fill.style.bottom = 0;
                tracker.Add(fill);
            }
            fill.style.backgroundColor = UITTheme.Tab.ActiveFill;
        }
        var dragger = slider.Q(className: "unity-base-slider__dragger");
        if (dragger != null)
        {
            dragger.style.width = kDraggerSize;
            dragger.style.height = kDraggerSize;
            // Unity 既定 USS は top: 50% + margin-top: -draggerHeight/2 で縦中央寄せする想定だが、
            // テーマ USS が借用元と異なると margin-top が dragger サイズと一致せず下寄りになる。
            // 自前のサイズ (kDraggerSize) で margin-top を再計算してセンター合わせを保証する。
            dragger.style.top = new Length(50f, LengthUnit.Percent);
            dragger.style.marginTop = -kDraggerSize / 2f;
            dragger.style.backgroundColor = UITTheme.Tab.ActiveFill;
            dragger.style.borderTopLeftRadius = kDraggerSize / 2f;
            dragger.style.borderTopRightRadius = kDraggerSize / 2f;
            dragger.style.borderBottomLeftRadius = kDraggerSize / 2f;
            dragger.style.borderBottomRightRadius = kDraggerSize / 2f;
        }
        var draggerBorder = slider.Q(className: "unity-base-slider__dragger-border");
        if (draggerBorder != null)
        {
            draggerBorder.style.display = DisplayStyle.None;
        }
    }
}
