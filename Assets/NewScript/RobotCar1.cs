using System.Collections;
using UnityEngine;

/// <summary>
/// RobotCar1 — picks up raw objects from WarehouseManager, drives to Arm1 staging.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE.
///   Car drives immediately without waiting for PLC in_CycleStart.
///   All output tags still fire so you can verify timing in Inspector.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     in_CycleStart      — rising edge: release car to drive (ignored offline)
///     in_EStopTag        — rising edge: emergency stop car
///
///   OUTPUTS (Unity → PLC):
///     out_CarAtWarehouseA   — TRUE while parked at WH-A
///     out_CarDriving        — TRUE while moving
///     out_CarAtArm1Staging  — TRUE when stopped at Arm1 staging position
///     out_ReadyForPickup    — TRUE while waiting for Arm1 to grip
///     out_CarReturning      — TRUE while driving back to WH-A
///     out_ReadyForNextCycle — TRUE when back at WH-A and free
///     out_CarEStop          — TRUE while emergency stop is active
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class RobotCar1 : MonoBehaviour
{
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = car drives without waiting for PLC in_CycleStart. " +
             "All output tags still fire for monitoring.")]
    public bool offlineMode = true;

    [Header("══ Waypoints ══════════════════════════════════════════════════")]
    public Transform wpWarehouseA;
    public Transform wpMid1;
    public Transform wpArm1Staging;
    public Transform wpMid2Return;

    [Header("══ Object Carry ════════════════════════════════════════════════")]
    public Vector3 carryOffset = new Vector3(0f, 0.3f, 0f);

    [Header("══ References ══════════════════════════════════════════════════")]
    public RobotArmController arm1;
    public WarehouseManager   warehouseManager;

    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════════")]
    [Tooltip("Rising edge: release car to drive. Ignored when offlineMode = true.")]
    public string in_CycleStart = "Car1_CycleStart";
    [Tooltip("Rising edge: emergency stop")]
    public string in_EStopTag   = "Car1_EStop";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════════")]
    public string out_CarAtWarehouseA   = "Car1_AtWarehouseA";
    public string out_CarDriving        = "Car1_Driving";
    public string out_CarAtArm1Staging  = "Car1_AtArm1Staging";
    public string out_ReadyForPickup    = "Car1_ReadyForPickup";
    public string out_CarReturning      = "Car1_Returning";
    public string out_ReadyForNextCycle = "Car1_ReadyForNextCycle";
    public string out_CarEStop          = "Car1_EStop_Active";

    [Header("══ Movement ════════════════════════════════════════════════════")]
    public float moveSpeed         = 5f;
    [Range(0.5f,10f)]  public float acceleration    = 2.5f;
    public float waypointTolerance = 0.05f;
    [Range(30f,720f)]  public float rotationSpeed   = 200f;
    [Range(0.5f,15f)]  public float alignThreshold  = 3f;
    public float arrivalPause      = 0.4f;
    public float postReturnDelay   = 0.5f;

    public enum ForwardAxis { Z, NegZ, X, NegX, Y, NegY }
    public ForwardAxis forwardAxis = ForwardAxis.Z;

    [Header("══ Debug (Read Only) ════════════════════════════════════════════")]
    [SerializeField] string dbState    = "Idle";
    [SerializeField] bool   dbCarrying = false;
    [SerializeField] string dbNextWP   = "—";
    [SerializeField] bool   dbEStop    = false;
    [SerializeField] string dbMode     = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    Transform carriedObject  = null;
    bool      isReadyForObj  = true;
    bool      cycleStartFlag = false;
    bool      eStopActive    = false;
    float     currentSpeed   = 0f;

    public bool IsReadyForNextObject => isReadyForObj && !eStopActive;

    System.Action<bool> cbCycleStart, cbEStop;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode = offlineMode ? "Offline" : "PLC";

        if (wpWarehouseA != null)
        {
            transform.position = wpWarehouseA.position;
            transform.rotation = wpWarehouseA.rotation;
        }

        bool prev = false;
        cbCycleStart = v => { if (v && !prev) cycleStartFlag = true; prev = v; };

        cbEStop = v =>
        {
            eStopActive = v;
            dbEStop     = v;
            SetOutput(out_CarEStop, v);
            if (v)
            {
                Debug.LogWarning("[CAR1] E-STOP received!");
                StopAllCoroutines();
                isReadyForObj = false;
                SetOutput(out_CarDriving,       false);
                SetOutput(out_CarAtArm1Staging, false);
                SetOutput(out_ReadyForPickup,   false);
                SetOutput(out_CarReturning,     false);
            }
            else
            {
                Debug.Log("[CAR1] E-Stop cleared.");
                isReadyForObj = true;
                SetOutput(out_CarAtWarehouseA,   true);
                SetOutput(out_ReadyForNextCycle, true);
            }
        };

        StartCoroutine(RegisterWhenReady());

        SetOutput(out_CarAtWarehouseA,   true);
        SetOutput(out_ReadyForNextCycle, true);
        Debug.Log($"[CAR1] Started in {dbMode} mode.");
    }

    void OnDestroy()
    {
        IO_Router.Instance?.Unregister(in_CycleStart, cbCycleStart);
        IO_Router.Instance?.Unregister(in_EStopTag,   cbEStop);
    }

    IEnumerator RegisterWhenReady()
    {
        while (IO_Router.Instance == null) yield return null;
        if (!string.IsNullOrEmpty(in_CycleStart)) IO_Router.Instance.Register(in_CycleStart, cbCycleStart);
        if (!string.IsNullOrEmpty(in_EStopTag))   IO_Router.Instance.Register(in_EStopTag,   cbEStop);
    }

    // ─────────────────────────────────────────────────────────────────────────
    public void LoadObject(Transform obj)
    {
        isReadyForObj = false;
        carriedObject = obj;

        var rb = carriedObject.GetComponent<Rigidbody>();
        if (rb != null) { rb.linearVelocity=Vector3.zero; rb.angularVelocity=Vector3.zero; rb.isKinematic=true; }
        carriedObject.SetParent(transform);
        carriedObject.localPosition = carryOffset;
        carriedObject.localRotation = Quaternion.identity;
        dbCarrying = true;

        Debug.Log($"[CAR1] '{obj.name}' loaded — driving to Arm1 staging ({dbMode} mode).");
        StartCoroutine(DriveToStaging());
    }

    IEnumerator DriveToStaging()
    {
        // PLC interlock — only in PLC mode
        if (!offlineMode)
        {
            cycleStartFlag = false;
            SetState("Waiting for CycleStart");
            SetOutput(out_CarAtWarehouseA,   true);
            SetOutput(out_ReadyForNextCycle, true);
            yield return new WaitUntil(() => cycleStartFlag || eStopActive);
            if (eStopActive) yield break;
            cycleStartFlag = false;
        }

        SetOutput(out_CarAtWarehouseA,   false);
        SetOutput(out_ReadyForNextCycle, false);
        SetOutput(out_CarDriving,        true);

        if (wpMid1 != null) { SetState("Driving → Mid1"); dbNextWP="Mid1"; yield return DriveTo(wpMid1); }
        SetState("Driving → Arm1 Staging"); dbNextWP="Arm1Staging";
        yield return DriveTo(wpArm1Staging);

        SetOutput(out_CarDriving,       false);
        SetOutput(out_CarAtArm1Staging, true);
        SetOutput(out_ReadyForPickup,   true);
        yield return new WaitForSeconds(arrivalPause);

        if (arm1 != null) arm1.NotifyRobotTrigger();

        SetState("Waiting for Arm1 to grip");
        float gripWaitTime = 0f;
        float gripTimeout  = 30f;   // if arm doesn't grip in 30s something is wrong
        yield return new WaitUntil(() =>
        {
            if (eStopActive) return true;
            gripWaitTime += Time.deltaTime;
            if (gripWaitTime >= gripTimeout)
            {
                Debug.LogError("[CAR1] Timed out waiting for Arm1 to grip — arm may have no poses " +
                               "captured, no joints assigned, or no cube reference. Check Arm1 in Inspector.");
                return true;
            }
            bool detached   = carriedObject != null && carriedObject.parent != transform;
            bool armGripped = arm1 != null && arm1.HasGripped;
            return detached || armGripped;
        });

        if (eStopActive) yield break;

        carriedObject = null;
        dbCarrying    = false;
        SetOutput(out_CarAtArm1Staging, false);
        SetOutput(out_ReadyForPickup,   false);

        yield return ReturnToWarehouseA();
    }

    IEnumerator ReturnToWarehouseA()
    {
        SetOutput(out_CarReturning, true);
        if (wpMid2Return != null) { SetState("Returning → Mid2"); dbNextWP="Mid2Return"; yield return DriveTo(wpMid2Return); }
        SetState("Returning → WarehouseA"); dbNextWP="WarehouseA";
        yield return DriveTo(wpWarehouseA);

        SetOutput(out_CarReturning,      false);
        SetOutput(out_CarAtWarehouseA,   true);
        SetOutput(out_ReadyForNextCycle, true);
        SetState("Idle at WarehouseA");

        if (postReturnDelay > 0f) yield return new WaitForSeconds(postReturnDelay);

        isReadyForObj = true;
        warehouseManager?.NotifyCarFree();
        Debug.Log("[CAR1] Returned to WH-A.");
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
            if (dist <= waypointTolerance) { currentSpeed=0f; break; }  // stop cleanly, no snap
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
