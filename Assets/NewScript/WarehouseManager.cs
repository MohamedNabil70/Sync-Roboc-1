using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WarehouseManager — master batch controller.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE (or offlineAutoStart = TRUE).
///   Batch starts automatically on Play. No PLC needed.
///   All output tags still fire so you can verify the sequence in the
///   IO_Router tag monitor and prepare your TIA Portal data block.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     in_StartBatch    — rising edge: start processing all WH-A objects
///     in_PauseDispatch — TRUE: hold dispatch (don't send next object to Car1)
///                        FALSE: resume dispatch
///     in_EStopTag      — rising edge: pause entire dispatch immediately
///
///   OUTPUTS (Unity → PLC):
///     out_ObjectReady     — TRUE while Car1 has been given an object
///     out_BatchInProgress — TRUE while any object is still in the pipeline
///     out_AllDone         — pulse TRUE when every object is in WH-B
///     out_DispatchPaused  — TRUE while dispatch is held by PLC or E-Stop
///     out_RemainingCount  — logged to console; not a bool PLC tag
///     out_DeliveredCount  — logged to console; not a bool PLC tag
/// ════════════════════════════════════════════════════════════════════════════
///
/// ══ MASTER "RUN SCENE" / FEEDBACK TAGS (TIA Portal) ══════════════════════════
///   This is the ONE input + feedback output pair needed to run the WHOLE scene
///   from TIA Portal — every other script reacts automatically once dispatch
///   starts, so no other master tags are required:
///     IN  → in_StartBatch    (rising edge) : starts the whole production cycle
///     OUT → out_BatchInProgress (continuous "running" feedback)
///     OUT → out_AllDone      (pulse "done" feedback when WH-B is full)
///   Every other component (cars, arms, conveyors, sensors) already has its own
///   smaller in_/out_ tags for its sub-step — those are optional extras for
///   fine-grained PLC monitoring/interlocks, not required to run the scene.
///   On Start(), RunSystemDiagnostics() checks every reference below and logs
///   a PASS/WARN/FAIL report — check the Console first if the scene won't run.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class WarehouseManager : MonoBehaviour
{
    public static WarehouseManager Instance { get; private set; }

    // ── Warehouse A ───────────────────────────────────────────────────────────
    [Header("══ Warehouse A — Object Spawn Slots ══════════════════════════")]
    [Tooltip("One Transform per object. Add as many as you need.")]
    public List<Transform> warehouseASlots = new List<Transform>();
    [Tooltip("Prefab to spawn at each slot. Leave empty to use sceneObjects.")]
    public GameObject objectPrefab;
    [Tooltip("Pre-placed scene objects — same order as slots.")]
    public List<GameObject> sceneObjects = new List<GameObject>();
    [Tooltip("Offset above slot surface when spawning")]
    public Vector3 spawnOffset = new Vector3(0f, 0.05f, 0f);

    // ── Warehouse B ───────────────────────────────────────────────────────────
    [Header("══ Warehouse B — Delivery Slots ══════════════════════════════")]
    [Tooltip("One Transform per delivery position. Add as many as objects.")]
    public List<Transform> warehouseBSlots = new List<Transform>();
    [Tooltip("Offset above WH-B slot when placing delivered objects")]
    public Vector3 deliveryOffset = new Vector3(0f, 0.05f, 0f);

    // ── References ────────────────────────────────────────────────────────────
    [Header("══ System References ══════════════════════════════════════════")]
    public RobotCar1          robotCar1;
    public SensorTrigger      sensor1;
    public SensorTrigger      sensor2;
    public RobotArmController arm1;
    [Tooltip("Conveyor 1 — all objects pre-registered at spawn")]
    public ConveyorMotor      conveyor1;
    [Tooltip("Conveyor 2 — all objects pre-registered at spawn")]
    public ConveyorMotor      conveyor2;

    // ── Offline mode ──────────────────────────────────────────────────────────
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = batch starts automatically on Play — no PLC needed. " +
             "All output tags still fire for monitoring.")]
    public bool offlineMode       = true;
    [Tooltip("Alias for offlineMode — kept for backwards compatibility.")]
    public bool offlineAutoStart  = true;
    [Tooltip("Pause after Car1 returns before dispatching next object")]
    public float postDispatchDelay = 0.5f;

    // ── PLC Tags ──────────────────────────────────────────────────────────────
    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════════")]
    [Tooltip("Rising edge: start processing all WH-A objects. " +
             "Ignored in offlineMode (auto-start handles it).")]
    public string in_StartBatch    = "WH_StartBatch";
    [Tooltip("TRUE = hold dispatch (don't send next object to Car1). " +
             "FALSE = resume. Useful for HMI-controlled pausing.")]
    public string in_PauseDispatch = "WH_PauseDispatch";
    [Tooltip("Rising edge: E-Stop — pause dispatch immediately")]
    public string in_EStopTag      = "WH_EStop";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════════")]
    [Tooltip("TRUE while Car1 has been given an object and is in motion")]
    public string out_ObjectReady     = "WH_ObjectReady";
    [Tooltip("TRUE while at least one object is still in the pipeline")]
    public string out_BatchInProgress = "WH_BatchInProgress";
    [Tooltip("Pulse TRUE when ALL objects have reached WH-B")]
    public string out_AllDone         = "WH_AllDone";
    [Tooltip("TRUE while dispatch is paused by PLC or E-Stop")]
    public string out_DispatchPaused  = "WH_DispatchPaused";

    // ── Debug ─────────────────────────────────────────────────────────────────
    [Header("══ Debug — Pipeline Tracker (Read Only) ══════════════════════")]
    [SerializeField] int    dbTotalObjects  = 0;
    [SerializeField] int    dbRemainingInA  = 0;
    [SerializeField] int    dbDeliveredToB  = 0;
    [SerializeField] bool   dbPaused        = false;
    [SerializeField] bool   dbEStop         = false;
    [SerializeField] bool   dbWaitingForDelivery = false;
    [SerializeField] string dbStatus        = "Idle";
    [SerializeField] string dbCurrentObject = "—";
    [SerializeField] string dbPipelineStage = "—";
    [SerializeField] string dbMode          = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    List<GameObject> spawnedObjects   = new List<GameObject>();
    Queue<int>       pendingQueue     = new Queue<int>();
    int              nextBSlot        = 0;
    bool             batchStarted     = false;
    bool             carBusy          = false;
    bool             dispatchPaused   = false;
    bool             eStopActive      = false;
    bool             previousDelivered = true;   // TRUE = previous object reached WH-B (or this is the first)
    Coroutine        dispatchCoroutine = null;

    System.Action<bool> cbStartBatch, cbPause, cbEStop;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        dbMode = (offlineMode || offlineAutoStart) ? "Offline" : "PLC";

        RunSystemDiagnostics();
        SpawnWarehouseAObjects();

        // PLC input callbacks
        cbStartBatch = v => { if (v && !batchStarted) StartBatch(); };

        cbPause = v =>
        {
            if (offlineMode) return;
            dispatchPaused = v;
            dbPaused       = v;
            SetOutput(out_DispatchPaused, v);
            Debug.Log($"[WH] Dispatch {(v ? "PAUSED" : "RESUMED")} by PLC.");
        };

        cbEStop = v =>
        {
            eStopActive = v;
            dbEStop     = v;
            if (v)
            {
                dispatchPaused = true;
                dbPaused       = true;
                SetOutput(out_DispatchPaused, true);
                Debug.LogWarning("[WH] E-STOP — dispatch paused.");
            }
            else
            {
                dispatchPaused = false;
                dbPaused       = false;
                SetOutput(out_DispatchPaused, false);
                Debug.Log("[WH] E-Stop cleared — dispatch resumed.");
            }
        };

        StartCoroutine(RegisterWhenReady());

        // Auto-start in offline mode
        if (offlineMode || offlineAutoStart)
            StartCoroutine(AutoStart());
    }

    IEnumerator AutoStart()
    {
        // Wait for IO_Router to be available so all callbacks are registered
        // before the batch fires. Also give Unity physics one frame to settle.
        yield return null;
        while (IO_Router.Instance == null) yield return null;
        yield return new WaitForSeconds(0.5f);

        // Re-run diagnostics now that everything is initialised
        int failsBefore = 0;
        if (robotCar1 == null) failsBefore++;
        if (arm1 == null)      failsBefore++;
        int validScene = sceneObjects.FindAll(o => o != null).Count;
        if (objectPrefab == null && validScene == 0) failsBefore++;
        if (warehouseASlots.FindAll(s => s != null).Count == 0) failsBefore++;

        if (failsBefore > 0)
        {
            Debug.LogError($"[WH] AutoStart aborted — {failsBefore} critical reference(s) missing. " +
                           "Check the Diagnostics report above for which fields need assigning in the Inspector.");
            yield break;
        }

        StartBatch();
    }

    void OnDestroy()
    {
        IO_Router.Instance?.Unregister(in_StartBatch,    cbStartBatch);
        IO_Router.Instance?.Unregister(in_PauseDispatch, cbPause);
        IO_Router.Instance?.Unregister(in_EStopTag,      cbEStop);
    }

    IEnumerator RegisterWhenReady()
    {
        while (IO_Router.Instance == null) yield return null;
        if (!string.IsNullOrEmpty(in_StartBatch))    IO_Router.Instance.Register(in_StartBatch,    cbStartBatch);
        if (!string.IsNullOrEmpty(in_PauseDispatch)) IO_Router.Instance.Register(in_PauseDispatch, cbPause);
        if (!string.IsNullOrEmpty(in_EStopTag))      IO_Router.Instance.Register(in_EStopTag,      cbEStop);
    }

    // ── Spawn ─────────────────────────────────────────────────────────────────
    void SpawnWarehouseAObjects()
    {
        spawnedObjects.Clear();
        int count = 0;

        for (int i = 0; i < warehouseASlots.Count; i++)
        {
            Transform slot = warehouseASlots[i];
            if (slot == null) { spawnedObjects.Add(null); Debug.LogWarning($"[WH] Slot {i} is null — skipped."); continue; }

            GameObject obj = null;

            if (objectPrefab != null)
            {
                obj      = Instantiate(objectPrefab, slot.position + spawnOffset, slot.rotation);
                obj.name = $"ProductObject_{i:D2}";
            }
            else if (i < sceneObjects.Count && sceneObjects[i] != null)
            {
                obj = sceneObjects[i];
                obj.transform.position = slot.position + spawnOffset;
                obj.transform.rotation = slot.rotation;
                obj.SetActive(true);
            }
            else
            {
                Debug.LogWarning($"[WH] Slot {i}: no prefab and no scene object — skipped.");
                spawnedObjects.Add(null);
                continue;
            }

            // Tag for SensorTrigger detection
            try   { if (obj.CompareTag("Untagged")) obj.tag = "ProductObject"; }
            catch { Debug.LogWarning("[WH] Tag 'ProductObject' not in Tag Manager — add it."); }

            // Pre-register with both conveyors
            conveyor1?.RegisterObject(obj.transform);
            conveyor2?.RegisterObject(obj.transform);

            spawnedObjects.Add(obj);
            count++;
        }

        // Count only successfully spawned objects
        dbTotalObjects = count;
        dbRemainingInA = count;
        Debug.Log($"[WH] Spawned {count} objects. Conv1={conveyor1!=null} Conv2={conveyor2!=null}");
    }

    // ── Batch control ─────────────────────────────────────────────────────────
    public void StartBatch()
    {
        if (batchStarted) return;
        batchStarted      = true;
        previousDelivered = true;   // first object has no predecessor — start unlocked
        dbWaitingForDelivery = false;
        dbStatus          = "Batch running";
        SetOutput(out_BatchInProgress, true);

        pendingQueue.Clear();
        for (int i = 0; i < spawnedObjects.Count; i++)
            if (spawnedObjects[i] != null) pendingQueue.Enqueue(i);

        dbRemainingInA = pendingQueue.Count;
        Debug.Log($"[WH] Batch started — {pendingQueue.Count} objects in {dbMode} mode.");

        if (dispatchCoroutine != null) { StopCoroutine(dispatchCoroutine); dispatchCoroutine = null; }
        dispatchCoroutine = StartCoroutine(DispatchLoop());
        StartCoroutine(StuckWatchdog());
    }

    IEnumerator DispatchLoop()
    {
        while (pendingQueue.Count > 0)
        {
            // Wait until:
            //   1. Car1 is free and back at WH-A
            //   2. The PREVIOUS object has fully reached WH-B (previousDelivered = true)
            //   3. Dispatch is not paused or E-Stopped
            dbWaitingForDelivery = !previousDelivered;
            dbPipelineStage = previousDelivered
                ? "Waiting for Car1"
                : "Waiting — previous object not yet at WH-B";

            yield return new WaitUntil(() =>
                !carBusy &&
                previousDelivered &&
                !dispatchPaused &&
                robotCar1 != null &&
                robotCar1.IsReadyForNextObject);

            dbWaitingForDelivery = false;

            if (postDispatchDelay > 0f)
                yield return new WaitForSeconds(postDispatchDelay);

            if (pendingQueue.Count == 0) break;

            int idx = pendingQueue.Dequeue();
            dbRemainingInA = pendingQueue.Count;
            GameObject obj = spawnedObjects[idx];
            if (obj == null) continue;

            // Lock dispatch until this object reaches WH-B
            previousDelivered = false;

            carBusy         = true;
            dbCurrentObject = obj.name;
            dbPipelineStage = "Car1 → Arm1 staging";
            SetOutput(out_ObjectReady, true);

            // Wire arm1 reference for this cycle.
            arm1?.SetCubeReference(obj.transform);

            // Restore object to raw state at start of each new cycle
            var cp = obj.GetComponent<CubeProcessor>();
            cp?.Restore();

            try
            {
                robotCar1.LoadObject(obj.transform);
                Debug.Log($"[WH] Dispatched '{obj.name}' (slot {idx}) — next will wait until this reaches WH-B.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[WH] LoadObject threw: {ex.Message} — clearing carBusy and unlock.");
                carBusy           = false;
                previousDelivered = true;   // unlock on error so batch doesn't deadlock
                SetOutput(out_ObjectReady, false);
            }
        }

        dbStatus        = "All dispatched — waiting for final delivery";
        dbCurrentObject = "—";
        Debug.Log("[WH] All objects dispatched — waiting for last one to reach WH-B.");
        dispatchCoroutine = null;
    }

    // ── Callbacks from Car1 / Car2 ────────────────────────────────────────────
    public void NotifyCarFree()
    {
        carBusy         = false;
        dbPipelineStage = "Pipeline — in process";
        SetOutput(out_ObjectReady, false);
        Debug.Log("[WH] Car1 free — ready for next dispatch.");
    }

    public void NotifyDelivered(Transform deliveredObject)
    {
        if (deliveredObject == null) { Debug.LogWarning("[WH] NotifyDelivered: null!"); return; }

        dbDeliveredToB++;
        dbRemainingInA  = Mathf.Max(0, dbTotalObjects - dbDeliveredToB);
        dbPipelineStage = $"Delivered {dbDeliveredToB}/{dbTotalObjects}";

        // Unlock dispatch — previous object has fully arrived at WH-B
        previousDelivered    = true;
        dbWaitingForDelivery = false;
        Debug.Log($"[WH] '{deliveredObject.name}' reached WH-B — dispatch unlocked for next object.");

        if (nextBSlot < warehouseBSlots.Count && warehouseBSlots[nextBSlot] != null)
        {
            Transform slot = warehouseBSlots[nextBSlot];
            var rb = deliveredObject.GetComponent<Rigidbody>();
            if (rb != null) { rb.linearVelocity=Vector3.zero; rb.angularVelocity=Vector3.zero; rb.isKinematic=true; }

            deliveredObject.SetParent(null);
            deliveredObject.position = slot.position + deliveryOffset;
            deliveredObject.rotation = slot.rotation;
            deliveredObject.gameObject.SetActive(true);

            // Unregister from both conveyors — permanently done
            conveyor1?.UnregisterObject(deliveredObject);
            conveyor2?.UnregisterObject(deliveredObject);

            Debug.Log($"[WH] '{deliveredObject.name}' → WH-B slot {nextBSlot}. {dbDeliveredToB}/{dbTotalObjects}");
            nextBSlot++;
        }
        else
        {
            Debug.LogWarning($"[WH] No WH-B slot available (slot={nextBSlot}/{warehouseBSlots.Count}). Add more WH-B slots.");
        }

        if (dbDeliveredToB >= dbTotalObjects)
        {
            dbStatus        = "BATCH COMPLETE";
            dbCurrentObject = "—";
            dbPipelineStage = "Done";

            Debug.Log("╔══════════════════════════════════════════════════════╗");
            Debug.Log("║  BATCH COMPLETE — all objects delivered to WH-B     ║");
            Debug.Log($"║  Mode: {dbMode,-48}║");
            Debug.Log("║  Pulse WH_StartBatch = TRUE to run a new batch.     ║");
            Debug.Log("╚══════════════════════════════════════════════════════╝");

            SetOutput(out_AllDone,         true);
            SetOutput(out_BatchInProgress, false);
            StartCoroutine(PulseAllDone());

            dispatchCoroutine = null;
            batchStarted      = false;
        }
    }

    IEnumerator PulseAllDone()
    {
        yield return new WaitForSeconds(0.5f);
        SetOutput(out_AllDone, false);
    }

    public void SetPipelineStage(string stage) => dbPipelineStage = stage;

    // ── Diagnostics ───────────────────────────────────────────────────────────
    /// <summary>
    /// Checks every reference/setting this manager depends on and logs a clear
    /// PASS / WARN / FAIL report. Run automatically on Start(), or via the
    /// "Run System Diagnostics" context menu. If the scene doesn't run, check
    /// this block in the Console first.
    /// </summary>
    [ContextMenu("Run System Diagnostics")]
    public void RunSystemDiagnostics()
    {
        int warn = 0, fail = 0;
        void Ok  (string m) => Debug.Log    ($"[WH-DIAG]  OK    {m}");
        void Warn(string m) { Debug.LogWarning($"[WH-DIAG]  WARN  {m}"); warn++; }
        void Fail(string m) { Debug.LogError  ($"[WH-DIAG]  FAIL  {m}"); fail++; }

        Debug.Log("╔══════════════ WAREHOUSE SYSTEM DIAGNOSTICS ══════════════╗");

        if (IO_Router.Instance == null)
            Fail("IO_Router.Instance is null — add an IO_Router GameObject to the scene.");
        else
            Ok($"IO_Router found — mode={(IO_Router.Instance.offlineMode ? "Offline" : "PLC")}, " +
               $"bridge={(IO_Router.Instance.bridge != null ? "assigned" : "MISSING")}");

        if (!offlineMode && !offlineAutoStart && IO_Router.Instance != null && IO_Router.Instance.offlineMode)
            Warn("WarehouseManager is in PLC mode but IO_Router.offlineMode=true — " +
                 "PLC tags will be ignored. Set IO_Router.offlineMode=false too.");

        if (robotCar1 == null) Fail("robotCar1 not assigned — Car1 cannot be dispatched.");
        else                    Ok ("robotCar1 assigned.");

        if (sensor1 == null) Warn("sensor1 not assigned — Arm2 won't be auto-triggered from Conv1.");
        else                  Ok ("sensor1 assigned.");
        if (sensor2 == null) Warn("sensor2 not assigned — Arm3 won't be auto-triggered from Conv2.");
        else                  Ok ("sensor2 assigned.");

        if (arm1 == null) Fail("arm1 not assigned — objects can't be picked up at Arm1 staging.");
        else               Ok ("arm1 assigned.");

        if (conveyor1 == null) Warn("conveyor1 not assigned — objects won't travel Arm1→Arm2.");
        else                     Ok ("conveyor1 assigned.");
        if (conveyor2 == null) Warn("conveyor2 not assigned — objects won't travel Arm2→Arm3.");
        else                     Ok ("conveyor2 assigned.");

        int validASlots = warehouseASlots.FindAll(s => s != null).Count;
        if (validASlots == 0) Fail("No valid Warehouse A slots — nothing to spawn/dispatch.");
        else                    Ok ($"{validASlots} Warehouse A slot(s) assigned.");

        if (objectPrefab == null)
        {
            int validScene = sceneObjects.FindAll(o => o != null).Count;
            if (validScene == 0) Fail("No objectPrefab AND no sceneObjects — nothing will spawn.");
            else                  Ok ($"Using {validScene} pre-placed sceneObject(s) (no prefab set).");
        }
        else Ok ("objectPrefab assigned — objects will be instantiated.");

        int validBSlots = warehouseBSlots.FindAll(s => s != null).Count;
        if (validBSlots < validASlots) Warn($"Only {validBSlots} Warehouse B slot(s) for {validASlots} object(s) — " +
                                              "extra deliveries will have no slot.");
        else Ok ($"{validBSlots} Warehouse B slot(s) assigned.");

        Debug.Log($"╚═══ Diagnostics: {warn} warning(s), {fail} failure(s). " +
                  $"{(fail==0 ? (warn==0 ? "All clear." : "Scene should run, but check warnings.") : "FIX FAILURES BEFORE RUNNING.")} ═══╝");
    }

    /// <summary>
    /// While a batch is in progress, watches dbPipelineStage/dbCurrentObject for
    /// inactivity and logs a warning if nothing changes for too long — a sign the
    /// pipeline is stuck (e.g. waiting on an arm, sensor, or PLC tag that never arrives).
    /// </summary>
    [Tooltip("Seconds of no pipeline progress before the stuck-watchdog logs a warning. 0 = disabled.")]
    public float stuckWatchdogSeconds = 45f;

    IEnumerator StuckWatchdog()
    {
        if (stuckWatchdogSeconds <= 0f) yield break;

        string lastStage  = dbPipelineStage;
        string lastObject = dbCurrentObject;
        float  idleTime   = 0f;

        while (batchStarted)
        {
            yield return new WaitForSeconds(1f);

            if (dbPipelineStage == lastStage && dbCurrentObject == lastObject)
            {
                idleTime += 1f;
                if (idleTime >= stuckWatchdogSeconds)
                {
                    Debug.LogWarning(
                        $"[WH-WATCHDOG] Pipeline unchanged for {idleTime:F0}s — possibly stuck.\n" +
                        $"  Stage         : {dbPipelineStage}\n" +
                        $"  Current object: {dbCurrentObject}\n" +
                        $"  carBusy={carBusy}  dispatchPaused={dispatchPaused}  eStop={dbEStop}\n" +
                        $"  → Check the arm/sensor/conveyor mentioned in 'Stage' above, " +
                        $"and confirm any PLC tag it's waiting on is actually being written " +
                        $"(IO_Router → Log All Tags).");
                    idleTime = 0f; // re-warn periodically rather than spamming every frame
                }
            }
            else
            {
                idleTime  = 0f;
                lastStage  = dbPipelineStage;
                lastObject = dbCurrentObject;
            }
        }
    }

    void SetOutput(string tag, bool v)
    { if (!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag, v); }

#if UNITY_EDITOR
    [ContextMenu("Add Warehouse A Slot")]   void AddSlotA() { warehouseASlots.Add(null); }
    [ContextMenu("Add Warehouse B Slot")]   void AddSlotB() { warehouseBSlots.Add(null); }
    [ContextMenu("Start Batch Manually")]   void ManualStart() { if (Application.isPlaying) StartBatch(); }
    [ContextMenu("Log All IO_Router Tags")] void DumpTags()  { IO_Router.Instance?.LogAllTags(); }
#endif
}
