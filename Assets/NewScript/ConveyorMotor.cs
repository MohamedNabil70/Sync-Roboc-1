using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ConveyorMotor — moves product objects along a world axis.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE (or leave plcTag empty).
///   Belt runs automatically — no PLC signal needed.
///   Auto-detects objects entering its trigger collider zone.
///   All output tags still fire so you can verify belt state in Inspector.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     plcTag           — BOOL: TRUE = run belt, FALSE = stop belt
///     in_EStopTag      — rising edge: emergency stop belt immediately
///
///   OUTPUTS (Unity → PLC):
///     out_BeltRunning  — TRUE while belt is actively moving an object
///     out_BeltStopped  — TRUE while belt is stopped for any reason
///     out_ObjectOnBelt — TRUE while an active object is on the belt
///     out_SensorBlocked— TRUE while sensor override is stopping the belt
///     out_BeltEStop    — TRUE while emergency stop is active
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class ConveyorMotor : MonoBehaviour
{
    public enum MoveAxis { X, NegX, Y, NegY, Z, NegZ }

    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = belt runs without PLC signal. Leave plcTag empty for same effect. " +
             "All output tags still fire for monitoring.")]
    public bool offlineMode = true;

    [Header("══ Movement ══════════════════════════════════════════════════")]
    public float    speed = 2f;
    public MoveAxis axis  = MoveAxis.X;

    [Header("══ Objects — drag ALL your cubes here ═════════════════════════")]
    [Tooltip("All product objects that may travel on this belt. " +
             "Belt moves only the active one (auto-detected or assigned via SetObjectToMove). " +
             "Add as many as you have cubes.")]
    public List<Transform> objectsOnBelt = new List<Transform>();

    [Header("══ Auto-Detect via Trigger Collider ════════════════════════════")]
    [Tooltip("If true, belt detects entering objects by tag. " +
             "Requires a Box Collider with Is Trigger = ON on this GameObject.")]
    public bool   autoDetectObjects = true;
    [Tooltip("Tag on all product objects. Must exist in Unity Tag Manager.")]
    public string productTag        = "ProductObject";

    [Header("══ PLC INPUT Tag (PLC → Unity) ══════════════════════════════")]
    [Tooltip("PLC BOOL: TRUE = run belt. Leave empty for offline/simulation mode.")]
    public string plcTag    = "";
    [Tooltip("Rising edge: emergency stop belt")]
    public string in_EStopTag = "";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════")]
    public string out_BeltRunning   = "";
    public string out_BeltStopped   = "";
    public string out_ObjectOnBelt  = "";
    public string out_SensorBlocked = "";
    public string out_BeltEStop     = "";

    [Header("══ Debug State (Read Only) ════════════════════════════════")]
    [SerializeField] bool   dbPlcOn        = false;
    [SerializeField] bool   dbSensorStop   = false;
    [SerializeField] bool   dbHeld         = false;
    [SerializeField] bool   dbRunning      = false;
    [SerializeField] bool   dbEStop        = false;
    [SerializeField] string dbActiveObject = "—";
    [SerializeField] int    dbKnownObjects = 0;
    [SerializeField] string dbMode         = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    bool      plcOn         = false;
    bool      sensorBlocked = false;
    bool      cubeHeld      = false;
    bool      eStopActive   = false;
    bool      lastRunning   = false;
    Transform activeObject  = null;

    bool IsRunning => (plcOn || offlineMode || string.IsNullOrEmpty(plcTag))
                      && !sensorBlocked && !cubeHeld && !eStopActive;

    Action<bool> plcCallback, cbEStop;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode         = (offlineMode || string.IsNullOrEmpty(plcTag)) ? "Offline" : "PLC";
        dbKnownObjects = objectsOnBelt.Count;

        // Validate auto-detect collider
        if (autoDetectObjects)
        {
            var col = GetComponent<Collider>();
            if (col == null)
                Debug.LogWarning($"[CONVEYOR:{name}] autoDetectObjects=true but no Collider. " +
                                 "Add a Box Collider with Is Trigger = ON.");
            else if (!col.isTrigger)
                Debug.LogWarning($"[CONVEYOR:{name}] Collider is not a Trigger — enable Is Trigger.");
        }

        // PLC run tag
        if (string.IsNullOrEmpty(plcTag))
        {
            plcOn = true; dbPlcOn = true;
            Debug.LogWarning($"[CONVEYOR:{name}] plcTag empty — Simulation/Offline mode.");
        }
        else
        {
            plcCallback = v =>
            {
                if (offlineMode) return;
                plcOn = v; dbPlcOn = v;
                Debug.Log($"[CONVEYOR:{name}] PLC '{plcTag}'={v}  running={IsRunning}");
            };
            StartCoroutine(RegisterTag(plcTag, plcCallback));
        }

        // E-Stop tag — active in BOTH modes
        if (!string.IsNullOrEmpty(in_EStopTag))
        {
            cbEStop = v =>
            {
                eStopActive = v;
                dbEStop     = v;
                SetOutput(out_BeltEStop, v);
                if (v)
                {
                    Debug.LogWarning($"[CONVEYOR:{name}] E-STOP — belt halted.");
                    SetOutput(out_BeltRunning, false);
                    SetOutput(out_BeltStopped, true);
                }
                else
                {
                    Debug.Log($"[CONVEYOR:{name}] E-Stop cleared.");
                }
            };
            StartCoroutine(RegisterTag(in_EStopTag, cbEStop));
        }

        StartCoroutine(DiagnosticsAfterDelay());
    }

    void OnDestroy()
    {
        if (!string.IsNullOrEmpty(plcTag)     && plcCallback != null) IO_Router.Instance?.Unregister(plcTag,      plcCallback);
        if (!string.IsNullOrEmpty(in_EStopTag) && cbEStop    != null) IO_Router.Instance?.Unregister(in_EStopTag, cbEStop);
    }

    IEnumerator RegisterTag(string tag, Action<bool> cb)
    {
        if (string.IsNullOrEmpty(tag)) yield break;
        while (IO_Router.Instance == null) yield return null;
        IO_Router.Instance.Register(tag, cb);
        Debug.Log($"[CONVEYOR:{name}] Registered tag '{tag}'.");
    }

    IEnumerator DiagnosticsAfterDelay()
    {
        yield return new WaitForSeconds(2.5f);
        Debug.Log($"=== CONVEYOR:{name}  mode={dbMode}  objects={dbKnownObjects}  " +
                  $"active='{dbActiveObject}'  tag='{plcTag}'  speed={speed}  axis={axis} ===");
    }

    // ── Auto-detect via trigger collider ──────────────────────────────────────
    void OnTriggerEnter(Collider other)
    {
        if (!autoDetectObjects)  return;
        if (activeObject != null) return;   // already has one

        bool isProduct = !string.IsNullOrEmpty(productTag) && other.CompareTag(productTag);
        if (!isProduct) return;

        Transform incoming = other.transform;

        // If known list is populated, only accept known objects
        if (objectsOnBelt.Count > 0 && !objectsOnBelt.Contains(incoming)) return;

        AssignActive(incoming);
        Debug.Log($"[CONVEYOR:{name}] Auto-detected '{incoming.name}' entering belt.");
    }

    void OnTriggerExit(Collider other)
    {
        if (!autoDetectObjects)  return;
        if (activeObject == null || other.transform != activeObject) return;
        ClearActive();
        Debug.Log($"[CONVEYOR:{name}] '{other.transform.name}' left belt zone.");
    }

    // ── FixedUpdate ───────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        dbRunning      = IsRunning;
        dbKnownObjects = objectsOnBelt.Count;

        // Offline fallback: if belt is running but activeObject was never set via trigger
        // or SetObjectToMove, auto-assign the first known object so the belt isn't silent.
        if (IsRunning && activeObject == null && objectsOnBelt.Count > 0
            && (offlineMode || string.IsNullOrEmpty(plcTag)))
        {
            AssignActive(objectsOnBelt[0]);
            Debug.Log($"[CONVEYOR:{name}] Offline auto-assigned '{objectsOnBelt[0].name}' as active object.");
        }

        bool nowRunning = IsRunning;
        if (nowRunning != lastRunning)
        {
            lastRunning = nowRunning;
            SetOutput(out_BeltRunning,  nowRunning);
            SetOutput(out_BeltStopped, !nowRunning);
            SetOutput(out_ObjectOnBelt, nowRunning && activeObject != null);
        }

        if (!IsRunning || activeObject == null) return;
        var rb = activeObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (rb.isKinematic) return;
            rb.MovePosition(rb.position + GetDir() * speed * Time.fixedDeltaTime);
        }
        else
        {
            activeObject.position += GetDir() * speed * Time.fixedDeltaTime;
        }
    }

    Vector3 GetDir() => axis switch
    {
        MoveAxis.X    =>  Vector3.right,   MoveAxis.NegX => -Vector3.right,
        MoveAxis.Y    =>  Vector3.up,      MoveAxis.NegY => -Vector3.up,
        MoveAxis.Z    =>  Vector3.forward, MoveAxis.NegZ => -Vector3.forward,
        _             =>  Vector3.right
    };

    // ── Helpers ───────────────────────────────────────────────────────────────
    void AssignActive(Transform obj)
    {
        activeObject   = obj;
        dbActiveObject = obj != null ? obj.name : "—";
        SetOutput(out_ObjectOnBelt, IsRunning && activeObject != null);
    }

    void ClearActive()
    {
        activeObject   = null;
        dbActiveObject = "—";
        SetOutput(out_ObjectOnBelt, false);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Explicitly assign the object this belt should move right now.</summary>
    public void SetObjectToMove(Transform obj)
    {
        if (obj != null && !objectsOnBelt.Contains(obj))
        {
            objectsOnBelt.Add(obj);
            dbKnownObjects = objectsOnBelt.Count;
            Debug.Log($"[CONVEYOR:{name}] Auto-registered '{obj.name}'.");
        }
        AssignActive(obj);
        Debug.Log($"[CONVEYOR:{name}] SetObjectToMove → '{dbActiveObject}'");
    }

    /// <summary>Pre-register an object in the known list without making it active.</summary>
    public void RegisterObject(Transform obj)
    {
        if (obj != null && !objectsOnBelt.Contains(obj))
        {
            objectsOnBelt.Add(obj);
            dbKnownObjects = objectsOnBelt.Count;
        }
    }

    /// <summary>Remove an object permanently (delivered to WH-B).</summary>
    public void UnregisterObject(Transform obj)
    {
        if (obj != null) objectsOnBelt.Remove(obj);
        dbKnownObjects = objectsOnBelt.Count;
        if (activeObject == obj) ClearActive();
    }

    /// <summary>Called by arm when gripping — belt stops moving object.</summary>
    public void SetHeld(bool held)
    {
        cubeHeld  = held;
        dbHeld    = held;
        if (held) ClearActive();
        Debug.Log($"[CONVEYOR:{name}] SetHeld={held}  running={IsRunning}");
    }

    /// <summary>Called by SensorTrigger — stops belt so arm can pick.</summary>
    public void SetSensorOverride(bool blocked)
    {
        sensorBlocked = blocked;
        dbSensorStop  = blocked;
        SetOutput(out_SensorBlocked, blocked);
        Debug.Log($"[CONVEYOR:{name}] SensorOverride={blocked}  running={IsRunning}");
    }

    public Transform GetActiveObject()  => activeObject;
    public bool      HasActiveObject    => activeObject != null;

    void SetOutput(string tag, bool value)
    { if (!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag, value); }
}
