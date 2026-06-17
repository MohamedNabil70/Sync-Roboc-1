using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// GenericCommandButton — UI button that sends a PLC Bool tag.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE.
///   Button press goes through IO_Router.SimulateInput() instead of
///   IO_Router.SetValue() → this fires all local callbacks WITHOUT sending
///   to the PLC bridge, so the whole simulation reacts correctly to button
///   presses while completely offline. Perfect for testing.
///   Set offlineMode = FALSE for live PLC operation — then SetValue() sends
///   through the bridge to TIA Portal.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   The "tag" field IS the PLC tag. It is both:
///     INPUT  (PLC → Unity): PLC can write this tag and the button visual
///                           will sync if syncFromPLC = true.
///     OUTPUT (Unity → PLC): pressing the button writes this tag to PLC.
///
///   Additional OUTPUT tags:
///     out_ButtonPressed — pulse TRUE when button is pressed (any mode)
///     out_ButtonState   — mirrors current state continuously
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class GenericCommandButton : MonoBehaviour
{
    public enum ButtonMode { Toggle, Momentary, SetTrue, SetFalse }

    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = button uses IO_Router.SimulateInput() — fires local callbacks " +
             "WITHOUT sending to PLC bridge. Use for offline testing.\n" +
             "FALSE = button uses IO_Router.SetValue() — sends to PLC bridge as well.")]
    public bool offlineMode = true;

    [Header("══ PLC Tag ══════════════════════════════════════════════════")]
    [Tooltip("Exact PLC Bool tag name. Must match variable in TIA Portal DB.")]
    public string tag = "";

    [Header("══ Button Behaviour ══════════════════════════════════════════")]
    [Tooltip("Toggle: flips each press (latched)\n" +
             "Momentary: TRUE while held, FALSE on release\n" +
             "SetTrue: always sends TRUE (rising-edge trigger)\n" +
             "SetFalse: always sends FALSE (reset/stop)")]
    public ButtonMode buttonMode = ButtonMode.Toggle;

    [Header("══ Current State (Read Only) ════════════════════════════════")]
    public bool state = false;

    [Header("══ PLC Read-back ════════════════════════════════════════════")]
    [Tooltip("If true, mirrors the PLC tag value back onto this button's visual state.")]
    public bool syncFromPLC = true;

    [Header("══ Additional OUTPUT Tags (Unity → PLC) ═══════════════════")]
    [Tooltip("Pulse TRUE when button is pressed")]
    public string out_ButtonPressed = "";
    [Tooltip("Mirrors current button state continuously")]
    public string out_ButtonState   = "";

    [Header("══ UI Elements (optional) ═══════════════════════════════════")]
    [Tooltip("Text label — shows tag name and current state")]
    public Text  label;
    [Tooltip("Image indicator — green = ON, red = OFF")]
    public Image indicator;
    public Color colorOn  = new Color(0.2f, 0.85f, 0.3f);
    public Color colorOff = new Color(0.85f, 0.2f, 0.2f);

    [Header("══ Debug (Read Only) ════════════════════════════════════════")]
    [SerializeField] string dbMode = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    float writeTime = -10f;
    const float WRITE_LOCKOUT = 2.0f;

    System.Action<bool> plcCallback;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode = offlineMode ? "Offline" : "PLC";
        UpdateUI();

        if (syncFromPLC && !string.IsNullOrEmpty(tag))
        {
            plcCallback = v =>
            {
                // Ignore echoes shortly after our own write
                if (Time.time - writeTime < WRITE_LOCKOUT) return;
                state = v;
                UpdateUI();
            };
            StartCoroutine(RegisterWhenReady());
        }

        SetOutput(out_ButtonState, state);
        Debug.Log($"[BUTTON:{(string.IsNullOrEmpty(tag)?"?"  :tag)}] Started in {dbMode} mode.");
    }

    System.Collections.IEnumerator RegisterWhenReady()
    {
        while (IO_Router.Instance == null) yield return null;
        IO_Router.Instance.Register(tag, plcCallback);
    }

    void OnDestroy()
    {
        if (syncFromPLC && !string.IsNullOrEmpty(tag) && plcCallback != null)
            IO_Router.Instance?.Unregister(tag, plcCallback);
    }

    // ── Button event handlers — wire to Button.OnClick / PointerUp ───────────

    /// <summary>Call from Button.OnClick for Toggle, SetTrue, SetFalse, and Momentary (press).</summary>
    public void OnPress()
    {
        switch (buttonMode)
        {
            case ButtonMode.Toggle:    state = !state; break;
            case ButtonMode.SetTrue:   state = true;   break;
            case ButtonMode.SetFalse:  state = false;  break;
            case ButtonMode.Momentary: state = true;   break;
        }
        Send();
        UpdateUI();
    }

    /// <summary>Call from Button.OnPointerUp for Momentary mode release.</summary>
    public void OnRelease()
    {
        if (buttonMode != ButtonMode.Momentary) return;
        state = false;
        Send();
        UpdateUI();
    }

    // Legacy helpers for backward compat
    public void Toggle()   { buttonMode = ButtonMode.Toggle;  OnPress(); }
    public void SetTrue()  { state = true;  Send(); UpdateUI(); }
    public void SetFalse() { state = false; Send(); UpdateUI(); }

    // ─────────────────────────────────────────────────────────────────────────
    void Send()
    {
        if (string.IsNullOrEmpty(tag))
        {
            Debug.LogWarning("[BUTTON] tag is empty — assign a PLC tag in the Inspector.");
            return;
        }

        writeTime = Time.time;

        if (IO_Router.Instance == null)
        {
            Debug.LogWarning($"[BUTTON] IO_Router not found — cannot send {tag}={state}");
            return;
        }

        if (offlineMode)
        {
            // Offline: fire local callbacks only — no bridge send
            IO_Router.Instance.SimulateInput(tag, state);
            Debug.Log($"[BUTTON:{tag}] SimulateInput({state}) [OFFLINE]");
        }
        else
        {
            // PLC mode: send to bridge AND fire local callbacks
            IO_Router.Instance.SetValue(tag, state);
            Debug.Log($"[BUTTON:{tag}] SetValue({state}) [PLC]");
        }

        // Additional output tags
        SetOutput(out_ButtonState, state);
        if (state || buttonMode == ButtonMode.SetTrue)
        {
            SetOutput(out_ButtonPressed, true);
            StartCoroutine(PulsePressed());
        }
    }

    System.Collections.IEnumerator PulsePressed()
    {
        yield return new WaitForSeconds(0.1f);
        SetOutput(out_ButtonPressed, false);
    }

    void UpdateUI()
    {
        if (label != null)
        {
            string tagDisplay = string.IsNullOrEmpty(tag) ? "?" : tag;
            string modeStr    = offlineMode ? "[SIM]" : "[PLC]";
            label.text = $"{modeStr} {tagDisplay}: {(state ? "ON" : "OFF")}";
        }
        if (indicator != null)
            indicator.color = state ? colorOn : colorOff;
    }

    void SetOutput(string t, bool v)
    { if (!string.IsNullOrEmpty(t)) IO_Router.Instance?.SetValue(t, v); }
}
