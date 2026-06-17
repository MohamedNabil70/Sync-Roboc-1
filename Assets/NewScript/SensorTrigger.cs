using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// SensorTrigger — detects product objects on the belt and triggers the arm.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE.
///   The sensor works IDENTICALLY in offline mode — it uses the physical
///   trigger collider to detect objects. No PLC tag needed.
///   Use in_ManualTrigger or the Inspector button to fire the arm manually
///   without a physical object entering the zone (useful for step-testing).
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     in_ResetTag       — rising edge: reset sensor for next object
///     in_ManualTrigger  — rising edge: fire arm without physical detection
///     in_EnableTag      — FALSE = sensor disabled (belt passes through freely)
///                         TRUE  = sensor active (default behaviour)
///
///   OUTPUTS (Unity → PLC):
///     out_SensorOn      — TRUE while object is inside trigger zone
///     out_SensorOff     — TRUE after zone cleared / arm picked up
///     out_ArmTriggered  — pulse TRUE when arm is triggered by this sensor
///     out_SensorEnabled — TRUE while sensor is enabled
///     out_SensorReady   — TRUE when sensor is reset and ready for next object
/// ════════════════════════════════════════════════════════════════════════════
///
/// ══ MULTI-OBJECT PIPELINE (expectedObjects) ══════════════════════════════════
///   SetExpectedObject() appends to 'expectedObjects' (FIFO, deduped) instead of
///   overwriting a single field. While this list is non-empty, ONLY objects on
///   it can trigger the sensor — each is removed once detected. This means
///   cube #2 can be registered as "expected" while cube #1 is still travelling
///   toward this sensor, without cube #1 silently failing to trigger.
///   'cubeObject' remains as a manual single-object override for offline testing
///   when 'expectedObjects' is empty.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class SensorTrigger : MonoBehaviour
{
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = sensor works using only the physical collider — no PLC enable/disable needed. " +
             "Output tags are still written so you can verify in Inspector.")]
    public bool offlineMode = true;

    [Header("══ Detection ══════════════════════════════════════════════════")]
    [Tooltip("Tag shared by all product objects. Set to 'ProductObject' in Tag Manager.")]
    public string cubeTag = "ProductObject";
    [Tooltip("Optional: if assigned AND no objects are queued via SetExpectedObject(), " +
             "only this specific object triggers the sensor (overrides cubeTag). " +
             "Mainly useful for single-object manual testing.")]
    public GameObject cubeObject;
    [Tooltip("FIFO list of objects expected at this sensor, filled by SetExpectedObject() " +
             "(normally called by WarehouseManager / Arm2 per dispatch cycle). When this " +
             "list is non-empty, ONLY objects in it can trigger the sensor — each is " +
             "removed once detected, so multiple cubes in flight are tracked correctly " +
             "instead of one overwriting another's expected reference.")]
    public List<GameObject> expectedObjects = new List<GameObject>();
    [Tooltip("Conveyor belt to pause when object arrives.")]
    public ConveyorMotor conveyorMotor;
    [Tooltip("Robot arm to trigger when object is detected.")]
    public RobotArmController robotArm;

    [Header("══ Legacy Bridge (optional) ════════════════════════════════")]
    public UnityBridgeClient bridge;
    public string sensorTag = "";

    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════")]
    [Tooltip("Rising edge: reset sensor for next object")]
    public string in_ResetTag      = "";
    [Tooltip("Rising edge: fire arm manually without physical detection")]
    public string in_ManualTrigger = "";
    [Tooltip("FALSE disables sensor (ignored in offlineMode). " +
             "Leave empty to keep sensor always enabled.")]
    public string in_EnableTag     = "";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════")]
    [Tooltip("TRUE while object is inside trigger zone")]
    public string out_SensorOn      = "";
    [Tooltip("TRUE after zone cleared or arm picked up")]
    public string out_SensorOff     = "";
    [Tooltip("Pulse TRUE when arm is triggered by this sensor")]
    public string out_ArmTriggered  = "";
    [Tooltip("TRUE while sensor is enabled and active")]
    public string out_SensorEnabled = "";
    [Tooltip("TRUE when sensor is reset and ready for next object")]
    public string out_SensorReady   = "";

    [Header("══ Timeout Watchdog ══════════════════════════════════════════")]
    [Tooltip("Seconds to wait for arm before auto-releasing belt. 0 = disabled.")]
    public float armTimeoutSeconds = 30f;

    [Header("══ Debug (Read Only) ════════════════════════════════════════")]
    [SerializeField] bool   dbTriggered      = false;
    [SerializeField] bool   dbEnabled        = true;
    [SerializeField] bool   dbWaitingForArm  = false;
    [SerializeField] string dbDetectedObject = "—";
    [SerializeField] int    dbExpectedCount  = 0;
    [SerializeField] string dbMode           = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    bool       triggered     = false;
    bool       sensorEnabled = true;
    bool       sentValue     = false;
    GameObject currentObject = null;

    System.Action<bool> cbEcho, cbReset, cbManual, cbEnable;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode = offlineMode ? "Offline" : "PLC";

        if (string.IsNullOrEmpty(cubeTag) && cubeObject == null)
            Debug.LogError($"[SENSOR:{name}] Neither cubeTag nor cubeObject assigned!");
        if (conveyorMotor == null)
            Debug.LogError($"[SENSOR:{name}] conveyorMotor not assigned!");

        var col = GetComponent<Collider>();
        if (col == null)
            Debug.LogError($"[SENSOR:{name}] No Collider — add a Box Collider with Is Trigger = ON");
        else if (!col.isTrigger)
            Debug.LogError($"[SENSOR:{name}] Collider is NOT set to Is Trigger!");

        // PLC echo callback
        if (!string.IsNullOrEmpty(sensorTag))
        {
            cbEcho = v => Debug.Log($"[SENSOR:{name}] PLC echo {sensorTag}={v}");
            StartCoroutine(Reg(sensorTag, cbEcho));
        }

        // Reset
        if (!string.IsNullOrEmpty(in_ResetTag))
        {
            bool prev = false;
            cbReset = v => { if (v && !prev) ResetTrigger(); prev = v; };
            StartCoroutine(Reg(in_ResetTag, cbReset));
        }

        // Manual trigger
        if (!string.IsNullOrEmpty(in_ManualTrigger))
        {
            bool prev = false;
            cbManual = v =>
            {
                if (v && !prev && !triggered && sensorEnabled)
                    FireArm(null);
                prev = v;
            };
            StartCoroutine(Reg(in_ManualTrigger, cbManual));
        }

        // Enable/disable from PLC (ignored in offlineMode)
        if (!string.IsNullOrEmpty(in_EnableTag))
        {
            cbEnable = v =>
            {
                if (offlineMode) return;
                sensorEnabled = v;
                dbEnabled     = v;
                SetOutput(out_SensorEnabled, v);
                Debug.Log($"[SENSOR:{name}] Enabled={v}");
            };
            StartCoroutine(Reg(in_EnableTag, cbEnable));
        }

        // Initial outputs
        SetOutput(out_SensorEnabled, true);
        SetOutput(out_SensorReady,   true);
        Debug.Log($"[SENSOR:{name}] Started in {dbMode} mode.");
    }

    void OnDestroy()
    {
        IO_Router.Instance?.Unregister(sensorTag,        cbEcho);
        IO_Router.Instance?.Unregister(in_ResetTag,      cbReset);
        IO_Router.Instance?.Unregister(in_ManualTrigger, cbManual);
        IO_Router.Instance?.Unregister(in_EnableTag,     cbEnable);
    }

    IEnumerator Reg(string tag, System.Action<bool> cb)
    {
        if (string.IsNullOrEmpty(tag)) yield break;
        while (IO_Router.Instance == null) yield return null;
        IO_Router.Instance.Register(tag, cb);
    }

    // ── Detection ─────────────────────────────────────────────────────────────
    void OnTriggerEnter(Collider other)
    {
        if (triggered)       return;
        if (!sensorEnabled && !offlineMode) return;

        bool match;
        if (expectedObjects.Count > 0)
        {
            // Multi-object pipeline: only objects queued via SetExpectedObject() trigger.
            match = expectedObjects.Contains(other.gameObject);
        }
        else if (cubeObject != null)
        {
            match = other.gameObject == cubeObject;
        }
        else
        {
            match = !string.IsNullOrEmpty(cubeTag) && other.CompareTag(cubeTag);
        }

        if (!match) return;

        if (expectedObjects.Remove(other.gameObject))
            dbExpectedCount = expectedObjects.Count;

        triggered        = true;
        dbTriggered      = true;
        currentObject    = other.gameObject;
        dbDetectedObject = currentObject.name;

        Debug.Log($"[SENSOR:{name}] Detected '{currentObject.name}' — halting belt.");

        conveyorMotor?.SetSensorOverride(true);
        SetOutput(out_SensorOn,    true);
        SetOutput(out_SensorReady, false);

        if (!string.IsNullOrEmpty(sensorTag))
        {
            bool cur = IO_Router.Instance != null ? IO_Router.Instance.GetValue(sensorTag) : false;
            sentValue = !cur;
            bridge?.Send(sensorTag, sentValue);
        }

        if (currentObject != null && robotArm != null)
            robotArm.SetCubeReference(currentObject.transform);

        FireArm(currentObject.transform);
    }

    // OnTriggerExit — ONLY updates PLC tags, never clears belt override
    void OnTriggerExit(Collider other)
    {
        if (currentObject == null || other.gameObject != currentObject) return;
        SetOutput(out_SensorOn,  false);
        SetOutput(out_SensorOff, true);
        if (!string.IsNullOrEmpty(sensorTag)) bridge?.Send(sensorTag, !sentValue);
    }

    // ── Fire arm ──────────────────────────────────────────────────────────────
    void FireArm(Transform detectedTransform)
    {
        if (robotArm == null) { StartCoroutine(AutoRelease(2f)); return; }

        SetOutput(out_ArmTriggered, true);
        StartCoroutine(PulseArmTriggered());

        if (robotArm.IsExecuting)
        {
            Debug.LogWarning($"[SENSOR:{name}] Arm busy — queuing trigger.");
            StartCoroutine(QueuedTrigger(detectedTransform));
        }
        else
        {
            robotArm.NotifyRobotTrigger();
            StartCoroutine(WaitForArm());
        }
    }

    IEnumerator PulseArmTriggered()
    {
        yield return new WaitForSeconds(0.2f);
        SetOutput(out_ArmTriggered, false);
    }

    IEnumerator QueuedTrigger(Transform obj)
    {
        dbWaitingForArm = true;
        yield return new WaitUntil(() => robotArm == null || !robotArm.IsExecuting);
        dbWaitingForArm = false;
        if (robotArm == null) { AutoReleaseFallback(); yield break; }
        if (obj != null) robotArm.SetCubeReference(obj);
        robotArm.NotifyRobotTrigger();
        StartCoroutine(WaitForArm());
    }

    IEnumerator WaitForArm()
    {
        dbWaitingForArm = true;
        yield return null;
        while (robotArm != null && !robotArm.IsExecuting) yield return null;

        float elapsed = 0f;
        while (robotArm != null && robotArm.IsExecuting)
        {
            elapsed += Time.deltaTime;
            if (armTimeoutSeconds > 0f && elapsed >= armTimeoutSeconds)
            {
                Debug.LogWarning($"[SENSOR:{name}] ARM TIMEOUT — releasing belt.");
                break;
            }
            yield return null;
        }

        dbWaitingForArm = false;
        ResetTrigger();
    }

    IEnumerator AutoRelease(float delay)
    {
        yield return new WaitForSeconds(delay);
        ResetTrigger();
    }

    void AutoReleaseFallback() { conveyorMotor?.SetSensorOverride(false); ResetTrigger(); }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fully resets sensor — called after arm finishes or by PLC.</summary>
    public void ResetTrigger()
    {
        triggered        = false;
        sentValue        = false;
        dbTriggered      = false;
        dbWaitingForArm  = false;
        currentObject    = null;
        dbDetectedObject = expectedObjects.Count > 0
            ? $"(expecting x{expectedObjects.Count}) next: {expectedObjects[0].name}"
            : "—";
        dbExpectedCount  = expectedObjects.Count;
        conveyorMotor?.SetSensorOverride(false);
        SetOutput(out_SensorOn,    false);
        SetOutput(out_SensorOff,   false);
        SetOutput(out_SensorReady, true);
        Debug.Log($"[SENSOR:{name}] Reset — ready for next object. (expecting={dbExpectedCount})");
    }

    /// <summary>
    /// Called by WarehouseManager / Arm2 to register an object that should arrive at this
    /// sensor. Adds to 'expectedObjects' (FIFO, deduped) rather than overwriting a single
    /// field — this lets several cubes be in flight toward this sensor simultaneously.
    /// Each entry is removed automatically once that object is detected.
    /// </summary>
    public void SetExpectedObject(GameObject obj)
    {
        if (obj == null) return;
        if (!expectedObjects.Contains(obj)) expectedObjects.Add(obj);
        dbExpectedCount  = expectedObjects.Count;
        dbDetectedObject = $"(expecting x{dbExpectedCount}) next: {expectedObjects[0].name}";
    }

    /// <summary>Remove an object from the expected list without it being detected
    /// (e.g. if it was rerouted/cancelled).</summary>
    public void ClearExpectedObject(GameObject obj)
    {
        if (obj == null) return;
        if (expectedObjects.Remove(obj)) dbExpectedCount = expectedObjects.Count;
    }

    void SetOutput(string tag, bool v)
    { if (!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag, v); }
}
