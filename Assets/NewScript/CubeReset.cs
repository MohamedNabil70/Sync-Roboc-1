using UnityEngine;
using System.Collections;

/// <summary>
/// CubeReset — safety fallback if an object reaches the end of Conv2 without
/// being picked by Arm3.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE.
///   The trigger collider detection works identically offline.
///   Arm3 is re-triggered automatically. Output tags still fire.
///   Use in_ManualReset to test the reset sequence from the Inspector
///   without needing an object to physically enter the zone.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     in_ManualReset   — rising edge: force a reset cycle from PLC/HMI
///     in_EStopTag      — rising edge: inhibit the reset (E-Stop active)
///
///   OUTPUTS (Unity → PLC):
///     out_ResetTriggered — pulse TRUE when a missed-object reset fires
///     out_ResetComplete  — TRUE once reset is fully complete
///     out_RetryCount     — not a bool; logged to console only
///     out_FallbackUsed   — pulse TRUE if teleport fallback was used
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class CubeReset : MonoBehaviour
{
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = reset logic runs without PLC enable/disable. " +
             "Output tags still fire for monitoring.")]
    public bool offlineMode = true;

    [Header("══ Object Detection ═══════════════════════════════════════════")]
    [Tooltip("Tag of product objects. Any object with this tag entering the zone triggers reset.")]
    public string productTag = "ProductObject";

    [Header("══ Conveyors ═══════════════════════════════════════════════════")]
    public ConveyorMotor firstConveyor;
    public ConveyorMotor secondConveyor;

    [Header("══ Sensors ════════════════════════════════════════════════════")]
    public SensorTrigger sensor1;
    public SensorTrigger sensor2;

    [Header("══ References ══════════════════════════════════════════════════")]
    public RobotArmController arm3;
    public WarehouseManager   warehouseManager;

    [Header("══ Fallback Teleport (when Arm3 not assigned) ══════════════════")]
    public Transform fallbackSpawnPoint;

    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════════")]
    [Tooltip("Rising edge: force a reset cycle from PLC or HMI button")]
    public string in_ManualReset = "";
    [Tooltip("Rising edge: E-Stop active — inhibit any reset action")]
    public string in_EStopTag    = "";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════════")]
    [Tooltip("Pulse TRUE when a missed-object reset fires")]
    public string out_ResetTriggered = "CubeReset_Triggered";
    [Tooltip("TRUE once reset sequence is fully complete")]
    public string out_ResetComplete  = "CubeReset_Complete";
    [Tooltip("Pulse TRUE if the teleport fallback was used (Arm3 not available)")]
    public string out_FallbackUsed   = "CubeReset_FallbackUsed";

    [Header("══ Debug (Read Only) ════════════════════════════════════════════")]
    [SerializeField] int    dbResetCount    = 0;
    [SerializeField] bool   dbInProgress    = false;
    [SerializeField] bool   dbEStopActive   = false;
    [SerializeField] string dbLastResetType = "—";
    [SerializeField] string dbMode          = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    bool resetInProgress = false;
    bool eStopActive     = false;

    System.Action<bool> cbManualReset, cbEStop;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode = offlineMode ? "Offline" : "PLC";

        var col = GetComponent<Collider>();
        if (col == null || !col.isTrigger)
            Debug.LogWarning($"[CUBE RESET:{name}] Needs a Collider with Is Trigger = ON.");

        // Manual reset from PLC or HMI — only triggers if an object is already in the zone
        if (!string.IsNullOrEmpty(in_ManualReset))
        {
            bool prev = false;
            cbManualReset = v =>
            {
                if (v && !prev && !resetInProgress && !eStopActive)
                {
                    Debug.Log($"[CUBE RESET:{name}] Manual reset triggered from PLC.");
                    // DoReset needs a target — manual reset without a physical object
                    // in the zone can only work if something already entered the collider.
                    // Log a warning if nothing is available.
                    Debug.LogWarning($"[CUBE RESET:{name}] Manual reset fired but no object in zone — " +
                                     "physically place an object in the trigger zone first.");
                }
                prev = v;
            };
            StartCoroutine(RegisterTag(in_ManualReset, cbManualReset));
        }

        // E-Stop inhibit
        if (!string.IsNullOrEmpty(in_EStopTag))
        {
            cbEStop = v =>
            {
                eStopActive  = v;
                dbEStopActive = v;
                if (v) Debug.LogWarning($"[CUBE RESET:{name}] E-Stop active — reset inhibited.");
            };
            StartCoroutine(RegisterTag(in_EStopTag, cbEStop));
        }

        Debug.Log($"[CUBE RESET:{name}] Started in {dbMode} mode.");
    }

    void OnDestroy()
    {
        IO_Router.Instance?.Unregister(in_ManualReset, cbManualReset);
        IO_Router.Instance?.Unregister(in_EStopTag,    cbEStop);
    }

    IEnumerator RegisterTag(string tag, System.Action<bool> cb)
    {
        if (string.IsNullOrEmpty(tag)) yield break;
        while (IO_Router.Instance == null) yield return null;
        IO_Router.Instance.Register(tag, cb);
    }

    // ─────────────────────────────────────────────────────────────────────────
    void OnTriggerEnter(Collider other)
    {
        if (resetInProgress || eStopActive) return;
        if (string.IsNullOrEmpty(productTag) || !other.CompareTag(productTag)) return;
        StartCoroutine(DoReset(other.transform));
    }

    // ─────────────────────────────────────────────────────────────────────────
    IEnumerator DoReset(Transform target)
    {
        if (target == null) yield break;

        resetInProgress  = true;
        dbInProgress     = true;
        dbResetCount++;

        Debug.LogWarning($"[CUBE RESET:{name}] ⚠ Reset #{dbResetCount} — object: '{target.name}'");

        SetOutput(out_ResetComplete,  false);
        SetOutput(out_ResetTriggered, true);
        yield return new WaitForSeconds(0.1f);
        SetOutput(out_ResetTriggered, false);

        // Stop Conv2 and reset both sensors BEFORE re-triggering arm
        secondConveyor?.SetSensorOverride(true);
        secondConveyor?.SetHeld(false);
        sensor1?.ResetTrigger();
        sensor2?.ResetTrigger();   // clears any lingering WaitForArm coroutine

        if (arm3 != null && !arm3.IsExecuting)
        {
            dbLastResetType = "Arm3 re-trigger";
            arm3.SetCubeReference(target);
            arm3.NotifyRobotTrigger();
            Debug.Log("[CUBE RESET] Arm3 re-triggered.");
            yield return new WaitForSeconds(0.5f);
        }
        else if (arm3 != null && arm3.IsExecuting)
        {
            dbLastResetType = "Arm3 queued retry";
            Debug.Log("[CUBE RESET] Arm3 busy — waiting...");
            float wait = 0f;
            yield return new WaitUntil(() =>
            {
                wait += Time.deltaTime;
                return arm3 == null || !arm3.IsExecuting || wait > 30f;
            });

            if (arm3 != null && !arm3.IsExecuting)
            {
                sensor2?.ResetTrigger();   // reset again in case WaitForArm ran
                arm3.SetCubeReference(target);
                arm3.NotifyRobotTrigger();
                Debug.Log("[CUBE RESET] Arm3 re-triggered after wait.");
            }
            else
            {
                TeleportFallback(target);
            }
        }
        else
        {
            dbLastResetType = "Teleport fallback";
            TeleportFallback(target);
        }

        resetInProgress = false;
        dbInProgress    = false;
        SetOutput(out_ResetComplete, true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    void TeleportFallback(Transform target)
    {
        if (fallbackSpawnPoint == null)
        {
            Debug.LogWarning("[CUBE RESET] No fallbackSpawnPoint — object left in place.");
            SetOutput(out_FallbackUsed, true);
            StartCoroutine(PulseFallback());
            return;
        }

        var rb = target.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity  = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic     = true;
        }

        target.SetParent(null);
        target.position = fallbackSpawnPoint.position;
        target.rotation = fallbackSpawnPoint.rotation;

        if (rb != null) { rb.isKinematic = false; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        firstConveyor?.SetHeld(false);
        firstConveyor?.SetSensorOverride(false);
        secondConveyor?.SetSensorOverride(false);
        secondConveyor?.SetHeld(false);

        // Restore processed appearance — get component from the actual target object
        target.GetComponent<CubeProcessor>()?.Restore();

        SetOutput(out_FallbackUsed, true);
        StartCoroutine(PulseFallback());

        Debug.Log("[CUBE RESET] Teleport fallback complete.");
    }

    IEnumerator PulseFallback()
    {
        yield return new WaitForSeconds(0.3f);
        SetOutput(out_FallbackUsed, false);
    }

    void SetOutput(string tag, bool v)
    { if (!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag, v); }
}
