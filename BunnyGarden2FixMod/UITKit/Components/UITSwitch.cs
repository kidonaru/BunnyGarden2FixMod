using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace UITKit.Components;

/// <summary>
/// iOS 風の bool 切替スイッチ。親 32×16 + 子 thumb 12×12。
/// ON 時は緑系背景 + thumb 右寄せ、OFF 時は灰系背景 + thumb 左寄せ。
/// クリック / Setup 経由いずれでも値を切替可能。
/// </summary>
public class UITSwitch : VisualElement
{
    /// <summary>値が変わるたびに発火（クリック / SetValue(notify:true) で）。</summary>
    public event Action<bool> OnValueChanged;

    public bool Value { get; private set; }

    private VisualElement m_thumb;

    private const float kWidth      = 32f;
    private const float kHeight     = 16f;
    private const float kThumbSize  = 12f;
    private const float kThumbInset = 2f;

    private static readonly Color kOnColor    = new(0.35f, 0.55f, 0.35f, 1f); // #5a8c5a 相当
    private static readonly Color kOffColor   = new(0.23f, 0.26f, 0.34f, 1f); // #3a4257 相当
    private static readonly Color kThumbColor = new(0.95f, 0.95f, 0.97f, 1f);

    public UITSwitch()
    {
        BuildLayout();
        RegisterCallback<ClickEvent>(evt =>
        {
            Toggle();
            evt.StopPropagation();
        });
    }

    private void BuildLayout()
    {
        style.width = kWidth;
        style.height = kHeight;
        style.flexShrink = 0;
        style.borderTopLeftRadius = kHeight / 2f;
        style.borderTopRightRadius = kHeight / 2f;
        style.borderBottomLeftRadius = kHeight / 2f;
        style.borderBottomRightRadius = kHeight / 2f;
        style.backgroundColor = kOffColor;
        style.position = Position.Relative;

        m_thumb = new VisualElement
        {
            name = "uitswitch-thumb",
            pickingMode = PickingMode.Ignore,
        };
        m_thumb.style.position = Position.Absolute;
        m_thumb.style.width = kThumbSize;
        m_thumb.style.height = kThumbSize;
        m_thumb.style.top = (kHeight - kThumbSize) / 2f;
        m_thumb.style.left = kThumbInset;
        m_thumb.style.backgroundColor = kThumbColor;
        m_thumb.style.borderTopLeftRadius = kThumbSize / 2f;
        m_thumb.style.borderTopRightRadius = kThumbSize / 2f;
        m_thumb.style.borderBottomLeftRadius = kThumbSize / 2f;
        m_thumb.style.borderBottomRightRadius = kThumbSize / 2f;
        Add(m_thumb);
    }

    /// <summary>初期値を設定する。OnValueChanged は発火しない。</summary>
    public void Setup(bool initial)
    {
        SetValue(initial, notify: false);
    }

    /// <summary>
    /// 値を設定する。notify=true なら OnValueChanged を発火する。
    /// 同値時も ApplyVisualState を呼ぶのは初期化直後（コンストラクタで Value=false、
    /// thumb 位置も未確定）に Setup(false) が来た場合に thumb 位置を確実にレイアウトするため。
    /// </summary>
    public void SetValue(bool v, bool notify = true)
    {
        if (Value == v)
        {
            ApplyVisualState();
            return;
        }
        Value = v;
        ApplyVisualState();
        if (notify) OnValueChanged?.Invoke(Value);
    }

    /// <summary>現在値を反転する（行クリック等から呼ぶ想定）。OnValueChanged を発火する。</summary>
    public void Toggle()
    {
        SetValue(!Value, notify: true);
    }

    private void ApplyVisualState()
    {
        if (m_thumb == null) return;
        style.backgroundColor = Value ? kOnColor : kOffColor;
        m_thumb.style.left = Value ? (kWidth - kThumbSize - kThumbInset) : kThumbInset;
    }
}
