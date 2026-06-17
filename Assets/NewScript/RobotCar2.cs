using System.Collections;
using UnityEngine;

/// <summary>
/// RobotCar2 — receives processed object from Arm3, drives to WH-B.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE.
///   Car drives to WH-B immediately after Arm3 calls NotifyBoxLoaded().
///   No PLC in_ProceedToDrop needed.
///   All output tags still fire for monitoring.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     in_ProceedToDrop   — rising edge: go-ahead from PLC to drive to WH-B
///                          (only used when offlineMode = false)
///     in_EStopTag        — rising edge: emergency stop car
///
///   OUTPUTS (Unity → PLC):
///     out_CarAtConv2       — TRUE while waiting at Conv2 exit
///     out_CarDriving       — TRUE while moving
///     out_CarAtWarehouseB  — TRUE when arrived at WH-B
///     out_ObjectDelivered  — pulse TRUE when object deposited at WH-B
///     out_CarReturning     — TRUE while returning to Conv2
///     out_ReadyForNext     — TRUE when parked at Conv2 and waiting
///     out_BoxReceived      — pulse TRUE when Arm3 loads object onto car
///     out_CarEStop         — TRUE while emergency stop is active
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class RobotCar2 : MonoBehaviour
{
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = car drives to WH-B immediately after box loaded — " +
             "no PLC in_ProceedToDrop needed. Output tags still fire.")]
    public bool offlineMode = true;

    [Header("══ Waypoints ══════════════════════════════════════════════════")]
    public Transform wpConv2Exit;
    public Transform wpMid1;
    public Transform wpWarehouseB;
    public Transform wpMid2Return;

    [Header("══ References ══════════════════════════════════════════════════")]
    public WarehouseManager warehouseManager;
    public ConveyorMotor    secondConveyor;

    [Header("══ Object Carry ════════════════════════════════════════════════")]
    public Vector3 carryOffset = new Vector3(0f, 0.3f, 0f);

    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════════")]
    [Tooltip("Rising edge: PLC go-ahead to drive to WH-B. Ignored in offlineMode.")]
    public string in_ProceedToDrop = "Car2_ProceedToDrop";
    [Tooltip("Rising edge: emergency stop")]
    public string in_EStopTag      = "Car2_EStop";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════════")]
    public string out_CarAtConv2      = "Car2_AtConv2";
    public string out_CarDriving      = "Car2_Driving";
    public string out_CarAtWarehouseB = "Car2_AtWarehouseB";
    public string out_ObjectDelivered = "Car2_ObjectDelivered";
    public string out_CarReturning    = "Car2_Returning";
    public string out_ReadyForNext    = "Car2_ReadyForNext";
    public string out_BoxReceived     = "Car2_BoxReceived";
    public string out_CarEStop        = "Car2_EStop_Active";

    [Header("══ Movement ════════════════════════════════════════════════════")]
    public float moveSpeed         = 5f;
    [Range(0.5f,10f)]  public float acceleration    = 2.5f;
    public float waypointTolerance = 0.05f;
    [Range(30f,720f)]  public float rotationSpeed   = 200f;
    [Range(0.5f,15f)]  public float alignThreshold  = 3f;
    public float arrivalPause      = 0.5f;

    public enum ForwardAxis { Z, NegZ, X, NegX, Y, NegY }
    public ForwardAxis forwardAxis = ForwardAxis.Z;

    [Header("══ Debug (Read Only) ════════════════════════════════════════════")]
    [SerializeField] string dbState    = "Idle";
    [SerializeField] bool   dbCarrying = false;
    [SerializeField] string dbNextWP   = "—";
    [SerializeField] bool   dbEStop    = false;
    [SerializeField] string dbMode     = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    Transform carriedObject = null;
    bool      boxLoadedFlag = false;
    bool      proceedFlag   = false;
    bool      eStopActive   = false;
    float     currentSpeed  = 0f;

    System.Action<bool> cbProceed, cbEStop;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode = offlineMode ? "Offline" : "PLC";

        if (wpConv2Exit != null)
        {
            transform.position = wpConv2Exit.position;
            transform.rotation = wpConv2Exit.rotation;
        }

        bool prevP = false;
        cbProceed = v => { if (v && !prevP) proceedFlag = true; prevP = v; };

        cbEStop = v =>
        {
            eStopActive = v;
            dbEStop     = v;
            SetOutput(out_CarEStop, v);
            if (v)
            {
                Debug.LogWarning("[CAR2] E-STOP!");
                StopAllCoroutines();
                SetOutput(out_CarDriving,  false);
                SetOutput(out_CarReturning,false);
                StartCoroutine(RestartMainLoopAfterEStop());
            }
            else
            {
                Debug.Log("[CAR2] E-Stop cleared.");
            }
        };

        StartCoroutine(RegisterWhenReady());
        StartCoroutine(MainLoop());

        SetOutput(out_CarAtConv2,   true);
        SetOutput(out_ReadyForNext, true);
        Debug.Log($"[CAR2] Started in {dbMode} mode.");
    }

    IEnumerator RestartMainLoopAfterEStop()
    {
        yield return new WaitUntil(() => !eStopActive);
        StartCoroutine(MainLoop());
    }

    void OnDestroy()
    {
        if (!string.IsNullOrEmpty(in_ProceedToDrop)) IO_Router.Instance?.Unregister(in_ProceedToDrop, cbProceed);
        if (!string.IsNullOrEmpty(in_EStopTag))      IO_Router.Instance?.Unregister(in_EStopTag,      cbEStop);
    }

    IEnumerator RegisterWhenReady()
    {
        while (IO_Router.Instance == null) yield return null;
        if (!string.IsNullOrEmpty(in_ProceedToDrop)) IO_Router.Instance.Register(in_ProceedToDrop, cbProceed);
        if (!string.IsNullOrEmpty(in_EStopTag))      IO_Router.Instance.Register(in_EStopTag,      cbEStop);
    }

    // ─────────────────────────────────────────────────────────────────────────
    IEnumerator MainLoop()
    {
        while (true)
        {
            SetState("Waiting at Conv2 exit");
            SetOutput(out_CarAtConv2,   true);
            SetOutput(out_ReadyForNext, true);
            boxLoadedFlag = false;
            proceedFlag   = false;

            yield return new WaitUntil(() => boxLoadedFlag && !eStopActive);
            boxLoadedFlag = false;

            SetOutput(out_CarAtConv2,   false);
            SetOutput(out_ReadyForNext, false);

            secondConveyor?.SetSensorOverride(false);
            secondConveyor?.SetHeld(false);

            // PLC go-ahead interlock (PLC mode only)
            if (!offlineMode && !string.IsNullOrEmpty(in_ProceedToDrop))
            {
                SetState("Waiting for ProceedToDrop");
                yield return new WaitUntil(() => proceedFlag || eStopActive);
                if (eStopActive) { yield return new WaitUntil(() => !eStopActive); continue; }
                proceedFlag = false;
            }

            // Drive to WH-B
            SetOutput(out_CarDriving, true);
            if (wpMid1 != null) { SetState("Driving → Mid1"); dbNextWP="Mid1"; yield return DriveTo(wpMid1); }
            SetState("Driving → WarehouseB"); dbNextWP="WarehouseB";
            yield return DriveTo(wpWarehouseB);

            SetOutput(out_CarDriving,      false);
            SetOutput(out_CarAtWarehouseB, true);

            // Deposit
            SetState("Depositing at WarehouseB");
            yield return new WaitForSeconds(arrivalPause);
            Transform delivered = carriedObject;
            DetachObject();
            warehouseManager?.NotifyDelivered(delivered);

            SetOutput(out_CarAtWarehouseB, false);
            SetOutput(out_ObjectDelivered, true);
            yield return new WaitForSeconds(0.2f);
            SetOutput(out_ObjectDelivered, false);

            // Return
            SetOutput(out_CarReturning, true);
            if (wpMid2Return != null) { SetState("Returning → Mid2"); dbNextWP="Mid2Return"; yield return DriveTo(wpMid2Return); }
            SetState("Returning → Conv2Exit"); dbNextWP="Conv2Exit";
            yield return DriveTo(wpConv2Exit);

            SetOutput(out_CarReturning, false);
            SetState("Idle — ready at Conv2");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    public void NotifyBoxLoaded(Transform obj)
    {
        if (obj == null) { Debug.LogWarning("[CAR2] NotifyBoxLoaded: null!"); return; }

        carriedObject = obj;
        var rb = carriedObject.GetComponent<Rigidbody>();
        if (rb != null) { rb.linearVelocity=Vector3.zero; rb.angularVelocity=Vector3.zero; rb.isKinematic=true; }
        carriedObject.SetParent(transform);
        carriedObject.localPosition = carryOffset;
        carriedObject.localRotation = Quaternion.identity;
        dbCarrying = true;

        boxLoadedFlag = true;   // signal MainLoop directly — no IO_Router echo risk

        SetOutput(out_BoxReceived, true);
        StartCoroutine(PulseBoxReceived());
        Debug.Log($"[CAR2] '{obj.name}' loaded ({dbMode} mode) — driving to WH-B.");
    }

    IEnumerator PulseBoxReceived()
    {
        yield return new WaitForSeconds(0.2f);
        SetOutput(out_BoxReceived, false);
    }

    void DetachObject()
    {
        if (carriedObject == null) return;
        carriedObject.SetParent(null);
        carriedObject = null;
        dbCarrying    = false;
    }

    // ── DriveTo ───────────────────────────────────────────────────────────────
    IEnumerator DriveTo(Transform target)
    {
        if (target == null) yield break;
        Vector3 FlatPos(Transform t) { var p=t.position; p.y=transform.position.y; return p; }

        Vector3 toTarget = FlatPos(target) - transform.position;
        if (toTarget.sqrMagnitude > waypointTolerance * waypointTolerance)
        {
            Quaternion desired = Quaternion.LookRotation(toTarget.normalized, Vector3.up) * AxisCorr();
            float rs = 0f;
            while (Quaternion.Angle(transform.rotation, desired) > alignThreshold)
            {
                if (eStopActive) yield break;
                toTarget = FlatPos(target) - transform.position;
                if (toTarget.sqrMagnitude < 0.0001f) break;
                desired = Quaternion.LookRotation(toTarget.normalized, Vector3.up) * AxisCorr();
                float ad = Quaternion.Angle(transform.rotation, desired);
                float tr = ad < 20f ? Mathf.Lerp(20f, rotationSpeed, ad/20f) : rotationSpeed;
                rs = Mathf.MoveTowards(rs, tr, rotationSpeed * acceleration * Time.deltaTime);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, rs * Time.deltaTime);
                yield return null;
            }
            transform.rotation = desired;
        }

        currentSpeed = 0f;
        while (true)
        {
            if (eStopActive) yield break;
            Vector3 myPos=transform.position, tPos=FlatPos(target);
            float dist=Vector3.Distance(myPos,tPos);
            if (dist<=waypointTolerance) { currentSpeed=0f; break; }  // stop cleanly, no snap
            float dec=(currentSpeed*currentSpeed)/(2f*acceleration*moveSpeed);
            float ts=dist<=dec+waypointTolerance?Mathf.Lerp(0.3f,moveSpeed,dist/Mathf.Max(dec,0.01f)):moveSpeed;
            currentSpeed=Mathf.Clamp(Mathf.MoveTowards(currentSpeed,ts,acceleration*moveSpeed*Time.deltaTime),0f,moveSpeed);
            transform.position=Vector3.MoveTowards(myPos,tPos,currentSpeed*Time.deltaTime);
            yield return null;
        }
    }

    Quaternion AxisCorr() => forwardAxis switch
    {
        ForwardAxis.NegZ=>Quaternion.Euler(0,180,0), ForwardAxis.X  =>Quaternion.Euler(0,-90,0),
        ForwardAxis.NegX=>Quaternion.Euler(0, 90,0), ForwardAxis.Y  =>Quaternion.Euler(-90,0,0),
        ForwardAxis.NegY=>Quaternion.Euler(90,  0,0), _=>Quaternion.identity,
    };

    void SetState(string s) => dbState=s;
    void SetOutput(string tag,bool v) { if(!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag,v); }
}
