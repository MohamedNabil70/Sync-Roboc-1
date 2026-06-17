using UnityEngine;
using System.Collections;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// RobotArmController — three roles in one script.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE in the Inspector.
///   • Arm1: triggered automatically when Car1 calls NotifyRobotTrigger().
///     No PLC signal needed — Car1 already calls it directly.
///   • Arm2: triggered automatically by SensorTrigger1 (which also has offline mode).
///     No PLC signal needed.
///   • Arm3: triggered automatically by SensorTrigger2.
///     No PLC signal needed.
///   In offline mode all output tags are still written to IO_Router so you can
///   watch them in the Inspector and verify the sequence works before connecting PLC.
///
/// ══ PLC I/O Tags — full list ════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     plcTriggerTag  — rising edge: start one arm cycle
///     plcRestartTag  — alternate rising edge trigger (e.g. manual restart button)
///     in_CNCDoneTag  — (Arm2 only) rising edge: CNC machine finished machining
///     in_EStopTag    — rising edge: emergency stop — arm aborts and releases
///
///   OUTPUTS (Unity → PLC):
///     out_ArmBusy    — TRUE while sequence is executing
///     out_ArmAtGrab  — TRUE when arm is at the grab position (about to grip)
///     out_ArmGripped — pulse TRUE when object is gripped
///     out_ArmAtCNC   — TRUE while arm is at the CNC position (Arm2 only)
///     out_CNCStart   — pulse TRUE to command CNC machine to start (Arm2 only)
///     out_ArmAtDrop  — TRUE when arm is at the drop/place position
///     out_ArmDropped — pulse TRUE when object is released
///     out_ArmIdle    — TRUE when arm is at Home / idle
///     out_ArmError   — pulse TRUE if sequence watchdog aborts the arm
///     out_ArmEStop   — TRUE while emergency stop is active
/// ════════════════════════════════════════════════════════════════════════════
///
/// ══ MULTI-OBJECT PIPELINE (cubeQueue) ════════════════════════════════════════
///   SetCubeReference() no longer overwrites 'cube' directly — it appends to
///   'cubeQueue'. At the start of each sequence, the next queued object becomes
///   'cube'. This lets cube #2 sit on Conv1 while cube #1 is still being placed
///   on Conv2 without the two references stomping on each other.
///
/// ══ PER-ROBOT POSE FILES ══════════════════════════════════════════════════════
///   Use the "Save Poses To File" / "Load Poses From File" buttons in the
///   Inspector to persist this arm's 9 captured poses to its own JSON file
///   under Assets/RobotPoses/ (or persistentDataPath in a build). The filename
///   defaults to "{Role}_{GameObjectName}", so Arm1/Arm2/Arm3 each get a separate
///   file and capturing a pose on one arm can never overwrite another's.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
[ExecuteAlways]
public class RobotArmController : MonoBehaviour
{
    public enum ArmRole
    {
        Arm1_Car1ToConveyor1,
        Arm2_Conv1CNCConv2,
        Arm3_Conv2ToCar2,
    }

    [Header("══ Arm Role ══════════════════════════════════════════════════")]
    [Tooltip("Select this arm's job in the production line.")]
    public ArmRole role = ArmRole.Arm1_Car1ToConveyor1;

    // ── Offline mode ──────────────────────────────────────────────────────────
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = arm runs using internal triggers only (no PLC needed). " +
             "All output tags are still written so you can verify in Inspector.")]
    public bool offlineMode = true;

    // ── Joints ────────────────────────────────────────────────────────────────
    [Header("── Joints — drag each bone Transform here ───────────────────")]
    public Transform joint1, joint2, joint3, joint4, joint5, joint6;

    [Header("── Gripper tip (empty child at fingertip) ────────────────────")]
    public Transform gripPoint;

    [Header("── Object — updated at runtime via SetCubeReference() ────────")]
    [Tooltip("Currently-active object for this arm's sequence. " +
             "Do NOT rely on this being the 'next' object when multiple cubes are in the " +
             "pipeline — SetCubeReference() now queues objects in 'cubeQueue' below, and " +
             "this field is refreshed from that queue at the start of each sequence. " +
             "For offline manual testing, assign this directly and leave cubeQueue empty.")]
    public Transform cube;

    [Tooltip("FIFO queue of objects waiting to be handled by this arm, filled by " +
             "SetCubeReference(). When multiple cubes are in the pipeline at once " +
             "(e.g. one at Arm2/CNC while another is on Conv1), this ensures each " +
             "sequence grabs the correct object instead of one cube's reference " +
             "overwriting another's mid-sequence.")]
    public List<Transform> cubeQueue = new List<Transform>();

    // ── Conveyors ─────────────────────────────────────────────────────────────
    [Header("── Conveyors ────────────────────────────────────────────────")]
    public ConveyorMotor sourceConveyorMotor;
    public ConveyorMotor destConveyorMotor;

    // ── CNC (Arm2) ────────────────────────────────────────────────────────────
    [Header("── CNC  (Arm2 only) ──────────────────────────────────────────")]
    [Range(0f, 120f)]
    [Tooltip("CNC dwell time — used when in_CNCDoneTag is empty")]
    public float cncProcessTime = 4f;
    [Tooltip("World position of CNC work surface")]
    public Transform cncDropPoint;

    // ── Arm1 staging ──────────────────────────────────────────────────────────
    [Header("── Arm1 Staging ─────────────────────────────────────────────")]
    public Transform car1GrabPoint;
    public Transform conv1DropPoint;
    public ConveyorMotor conveyor1Motor;

    // ── Arm2 chaining ─────────────────────────────────────────────────────────
    [Header("── Arm2 Chaining ─────────────────────────────────────────────")]
    [Tooltip("Arm3 — receives cube reference after Arm2 places on Conv2")]
    public RobotArmController arm3;
    [Tooltip("SensorTrigger2 — receives expected object after Arm2 places on Conv2")]
    public SensorTrigger sensor2;

    // ── Arm3 / Car2 ───────────────────────────────────────────────────────────
    [Header("── Arm3 / Car2 ──────────────────────────────────────────────")]
    public RobotCar2   robotCar2;
    public Transform   car2DropPoint;

    // ── CubeProcessor note ───────────────────────────────────────────────────
    // NO fallback field here — CubeProcessor is fetched directly from whichever
    // cube is currently active (cube.GetComponent<CubeProcessor>()).
    // Every product object (prefab or scene object) must have a CubeProcessor
    // component attached to it. If one is missing, the self-check and sequence
    // will log a clear warning naming the specific object that is missing it.

    // ── Rotation axes ─────────────────────────────────────────────────────────
    public enum RotAxis { X, Y, Z }
    [Header("── Joint Rotation Axes ────────────────────────────────────────")]
    public RotAxis j1Axis=RotAxis.Y, j2Axis=RotAxis.Z, j3Axis=RotAxis.Z;
    public RotAxis j4Axis=RotAxis.X, j5Axis=RotAxis.Z, j6Axis=RotAxis.X;

    // ── Speed ─────────────────────────────────────────────────────────────────
    [Header("── Speed & Timing ────────────────────────────────────────────")]
    [Range(5f,  360f)] public float jointSpeed        = 60f;
    [Range(0f,  1f)]   public float smoothing         = 0.85f;
    [Range(0f,  2f)]   public float pauseBetweenMoves = 0.2f;
    [Range(0f,  3f)]   public float gripDelay         = 0.35f;

    // ── Watchdog ──────────────────────────────────────────────────────────────
    [Header("── Safety Watchdog ──────────────────────────────────────────")]
    [Tooltip("Abort sequence if it runs longer than this. 0 = disabled.")]
    [Range(0f, 300f)] public float sequenceTimeoutSeconds = 60f;

    // ── Poses ─────────────────────────────────────────────────────────────────
    [System.Serializable]
    public class RobotPose
    {
        [HideInInspector] public string name = "";
        [Range(-180f,180f)] public float j1,j2,j3,j4,j5,j6;
        public bool savePoseOnCapture = false;
        public float[] ToArr()         => new[]{j1,j2,j3,j4,j5,j6};
        public void  FromArr(float[] a){ if(a.Length<6)return; j1=a[0];j2=a[1];j3=a[2];j4=a[3];j5=a[4];j6=a[5]; }
    }

    /// <summary>Snapshot of all poses for one robot, written to/read from its own JSON file.</summary>
    [System.Serializable]
    public class PoseFileData
    {
        public RobotPose poseHome      = new RobotPose{name="Home"};
        public RobotPose poseWaypointA = new RobotPose{name="WaypointA"};
        public RobotPose poseGrab      = new RobotPose{name="Grab"};
        public RobotPose poseWaypointB = new RobotPose{name="WaypointB"};
        public RobotPose poseConv1Drop = new RobotPose{name="Conv1Drop"};
        public RobotPose poseCNCPlace  = new RobotPose{name="CNCPlace"};
        public RobotPose poseWaypointC = new RobotPose{name="WaypointC"};
        public RobotPose poseConv2Drop = new RobotPose{name="Conv2Drop"};
        public RobotPose poseCar2Drop  = new RobotPose{name="Car2Drop"};
    }

    [Header("── Poses — capture via Inspector buttons during Play ─────────")]
    public RobotPose poseHome      = new RobotPose{name="Home"};
    public RobotPose poseWaypointA = new RobotPose{name="WaypointA"};
    public RobotPose poseGrab      = new RobotPose{name="Grab"};
    public RobotPose poseWaypointB = new RobotPose{name="WaypointB"};
    public RobotPose poseConv1Drop = new RobotPose{name="Conv1Drop"};
    public RobotPose poseCNCPlace  = new RobotPose{name="CNCPlace"};
    public RobotPose poseWaypointC = new RobotPose{name="WaypointC"};
    public RobotPose poseConv2Drop = new RobotPose{name="Conv2Drop"};
    public RobotPose poseCar2Drop  = new RobotPose{name="Car2Drop"};

    [HideInInspector] public bool _capHome=false,_capWpA=false,_capGrab=false,_capWpB=false;
    [HideInInspector] public bool _capConv1Drop=false,_capCNCPlace=false;
    [HideInInspector] public bool _capWpC=false,_capConv2Drop=false,_capCar2Drop=false;

    [Header("── Pose File Persistence (per-robot, conflict-free) ─────────")]
    [Tooltip("Filename (no extension) used to Save/Load this arm's poses to/from disk. " +
             "Leave EMPTY to auto-generate one from Role + GameObject name, e.g. " +
             "'Arm1_Car1ToConveyor1_RobotArm_1'. Each arm gets its OWN file, so capturing " +
             "a pose on Arm1 can NEVER overwrite Arm2/Arm3's saved positions — even if " +
             "they share the same prefab.")]
    public string poseFileName = "";
    [Tooltip("If true, this arm's poses are loaded from its file automatically on Start " +
             "(useful in builds, or after reverting scene changes).")]
    public bool autoLoadPosesOnStart = false;

    // ── PLC INPUT Tags ────────────────────────────────────────────────────────
    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════")]
    [Tooltip("Rising edge: start one arm cycle. In offlineMode this is ignored — " +
             "trigger comes from Car1 / SensorTrigger automatically.")]
    public string plcTriggerTag = "";
    [Tooltip("Alternate rising edge trigger (e.g. HMI restart button)")]
    public string plcRestartTag = "";
    [Tooltip("(Arm2) Rising edge from CNC: machining done — arm returns to pick part")]
    public string in_CNCDoneTag = "";
    [Tooltip("Rising edge: emergency stop — aborts current sequence and releases grip")]
    public string in_EStopTag   = "";

    // ── PLC OUTPUT Tags ───────────────────────────────────────────────────────
    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════")]
    [Tooltip("TRUE while arm sequence is running")]
    public string out_ArmBusy    = "";
    [Tooltip("TRUE when arm is at grab position")]
    public string out_ArmAtGrab  = "";
    [Tooltip("Pulse TRUE when object is gripped")]
    public string out_ArmGripped = "";
    [Tooltip("TRUE while arm is at CNC (Arm2 only)")]
    public string out_ArmAtCNC   = "";
    [Tooltip("Pulse TRUE to command CNC machine to start (Arm2 only)")]
    public string out_CNCStart   = "";
    [Tooltip("TRUE when arm is at drop/place position")]
    public string out_ArmAtDrop  = "";
    [Tooltip("Pulse TRUE when object is released")]
    public string out_ArmDropped = "";
    [Tooltip("TRUE when arm is at Home and idle")]
    public string out_ArmIdle    = "";
    [Tooltip("Pulse TRUE when sequence watchdog aborts the arm")]
    public string out_ArmError   = "";
    [Tooltip("TRUE while emergency stop is active")]
    public string out_ArmEStop   = "";

    // ── Debug ─────────────────────────────────────────────────────────────────
    [Header("── Runtime State (Read Only) ──────────────────────────────")]
    [SerializeField] string dbStep      = "Idle";
    [SerializeField] bool   dbExecuting = false;
    [SerializeField] bool   dbHeld      = false;
    [SerializeField] string dbCubeName  = "—";
    [SerializeField] int    dbQueuedCubes = 0;
    [SerializeField] bool   dbEStop     = false;
    [SerializeField] string dbMode      = "Offline";

    // ── Private ───────────────────────────────────────────────────────────────
    bool isExecuting      = false;
    bool hasGripped       = false;
    bool coroutineRunning = false;
    bool eStopActive      = false;

    public bool IsExecuting => isExecuting;
    public bool HasGripped  => hasGripped;

    List<Transform> joints;
    List<RotAxis>   axes;
    ConveyorMotor   frozenBelt;
    Rigidbody       cubeRb;

    // Cached yield objects — avoids allocating new WaitForSeconds every sequence step
    WaitForSeconds  waitGripDelay;
    WaitForSeconds  waitPause;
    WaitForSeconds  waitPulse;
    static readonly WaitForSeconds waitHalfSec  = new WaitForSeconds(0.5f);
    static readonly WaitForSeconds waitShortPulse = new WaitForSeconds(0.15f);

    System.Action<bool> cbTrigger, cbCNCDone, cbEStop;
    bool cncDoneFlag = false;

    // ─────────────────────────────────────────────────────────────────────────
    void OnEnable()   => BuildLists();
    void OnValidate() => ApplyCaptures();

    void Start()
    {
        BuildLists();
        dbMode = offlineMode ? "Offline" : "PLC";

        // Cache coroutine yield objects based on current Inspector values
        waitGripDelay = new WaitForSeconds(gripDelay);
        waitPause     = new WaitForSeconds(pauseBetweenMoves);
        waitPulse     = new WaitForSeconds(0.5f);

        if (gripPoint == null)
            Debug.LogError($"[ARM:{role}] gripPoint not assigned!");

        if (autoLoadPosesOnStart) LoadPosesFromFile();

        if (cube != null) dbCubeName = cube.name;
        dbQueuedCubes = cubeQueue.Count;

        // ── Startup self-check ────────────────────────────────────────────────
        // Runs immediately on Play so you see what's wrong in the Console
        // before the arm is triggered, not after it silently does nothing.
        StartCoroutine(StartupSelfCheck());

        // Register PLC trigger (only active in PLC mode, but registered always)
        bool prevT = false;
        cbTrigger = v =>
        {
            if (v && !prevT && !offlineMode) NotifyRobotTrigger();
            prevT = v;
        };

        // Restart tag
        bool prevR = false;
        System.Action<bool> cbR = v =>
        {
            if (v && !prevR && !offlineMode) NotifyRobotTrigger();
            prevR = v;
        };

        cbCNCDone = v => { if (v) cncDoneFlag = true; };

        // E-Stop — active in BOTH modes
        cbEStop = v =>
        {
            if (v && !eStopActive)
            {
                eStopActive = true;
                dbEStop     = true;
                SetOutput(out_ArmEStop, true);
                if (isExecuting)
                {
                    StopAllCoroutines();
                    coroutineRunning = false;
                    EmergencyRelease();
                    SetOutput(out_ArmError, true);
                    StartCoroutine(PulseError());
                }
                Debug.LogWarning($"[ARM:{role}] E-STOP received!");
            }
            else if (!v && eStopActive)
            {
                eStopActive = false;
                dbEStop     = false;
                SetOutput(out_ArmEStop, false);
                Debug.Log($"[ARM:{role}] E-STOP cleared.");
            }
        };

        StartCoroutine(RegisterTag(plcTriggerTag, cbTrigger));
        if (!string.IsNullOrEmpty(plcRestartTag) && plcRestartTag != plcTriggerTag)
            StartCoroutine(RegisterTag(plcRestartTag, cbR));
        StartCoroutine(RegisterTag(in_CNCDoneTag, cbCNCDone));
        StartCoroutine(RegisterTag(in_EStopTag,   cbEStop));

        SetOutput(out_ArmIdle, true);
        Debug.Log($"[ARM:{role}] Started in {dbMode} mode.");
    }

    void OnDestroy()
    {
        IO_Router.Instance?.Unregister(plcTriggerTag, cbTrigger);
        IO_Router.Instance?.Unregister(in_CNCDoneTag, cbCNCDone);
        IO_Router.Instance?.Unregister(in_EStopTag,   cbEStop);
    }

    IEnumerator RegisterTag(string tag, System.Action<bool> cb)
    {
        if (string.IsNullOrEmpty(tag)) yield break;
        while (IO_Router.Instance == null) yield return null;
        IO_Router.Instance.Register(tag, cb);
    }

    IEnumerator PulseError()
    {
        yield return waitHalfSec;
        SetOutput(out_ArmError, false);
    }

    // ── Startup self-check ────────────────────────────────────────────────────
    IEnumerator StartupSelfCheck()
    {
        // Wait one frame so all other Start() methods have run
        yield return null;

        int issues = 0;
        void Fail(string m) { Debug.LogError($"[ARM:{role}] ✖ {m}"); issues++; }
        void Warn(string m) { Debug.LogWarning($"[ARM:{role}] ⚠ {m}"); }
        void Ok  (string m) { Debug.Log($"[ARM:{role}] ✔ {m}"); }

        Debug.Log($"╔══ ARM SELF-CHECK [{role} / {gameObject.name}] ══╗");

        // Joints
        bool hasAnyJoint = joints != null && joints.Exists(j => j != null);
        if (!hasAnyJoint)
            Fail("No joints assigned. Drag joint1…joint6 GameObjects into the joint fields in the Inspector.");
        else
        {
            int nullJ = joints.FindAll(j => j == null).Count;
            if (nullJ > 0) Warn($"{nullJ} joint slot(s) are null — arm will skip those axes.");
            else Ok("All 6 joints assigned.");
        }

        // GripPoint
        if (gripPoint == null)
            Fail("gripPoint not assigned — arm cannot parent the cube to grip it.");
        else Ok("gripPoint assigned.");

        // Poses
        bool IsZero(RobotPose p) => p.j1==0&&p.j2==0&&p.j3==0&&p.j4==0&&p.j5==0&&p.j6==0;
        if (IsZero(poseHome))      Fail("poseHome is all zeros — not captured. Use 'Capture Home' button.");
        if (IsZero(poseWaypointA)) Fail("poseWaypointA is all zeros — not captured.");
        if (IsZero(poseGrab))      Fail("poseGrab is all zeros — not captured.");
        if (issues == 0)           Ok("Core poses (Home, WaypointA, Grab) are non-zero.");

        // Role-specific references
        switch (role)
        {
            case ArmRole.Arm1_Car1ToConveyor1:
                if (conveyor1Motor == null) Warn("conveyor1Motor not assigned — cube won't be placed on Conv1.");
                break;
            case ArmRole.Arm2_Conv1CNCConv2:
                if (sourceConveyorMotor == null) Warn("sourceConveyorMotor (Conv1) not assigned.");
                if (destConveyorMotor   == null) Warn("destConveyorMotor (Conv2) not assigned.");
                if (IsZero(poseCNCPlace))  Fail("poseCNCPlace is all zeros — not captured.");
                if (IsZero(poseConv2Drop)) Fail("poseConv2Drop is all zeros — not captured.");

                // Check every pre-placed scene object and queued cube has CubeProcessor
                // so we catch missing components NOW, not mid-sequence when the arm is holding the cube.
                var allSceneObjects = new List<Transform>();
                allSceneObjects.AddRange(cubeQueue);
                if (cube != null && !allSceneObjects.Contains(cube)) allSceneObjects.Add(cube);

                // Also check any objects already in the scene tagged ProductObject
                foreach (var go in GameObject.FindGameObjectsWithTag("ProductObject"))
                {
                    var t = go.transform;
                    if (!allSceneObjects.Contains(t)) allSceneObjects.Add(t);
                }

                int missingCP = 0;
                foreach (var t in allSceneObjects)
                {
                    if (t != null && t.GetComponent<CubeProcessor>() == null)
                    {
                        Fail($"'{t.name}' has no CubeProcessor component — add one so Arm2 can change its shape/colour at CNC.");
                        missingCP++;
                    }
                }
                if (missingCP == 0 && allSceneObjects.Count > 0)
                    Ok($"All {allSceneObjects.Count} product object(s) have CubeProcessor.");
                else if (allSceneObjects.Count == 0)
                    Warn("No product objects found yet — CubeProcessor check will run at sequence time.");
                break;
            case ArmRole.Arm3_Conv2ToCar2:
                if (sourceConveyorMotor == null) Warn("sourceConveyorMotor (Conv2) not assigned.");
                if (robotCar2           == null) Warn("robotCar2 not assigned — cube won't be loaded onto Car2.");
                if (IsZero(poseCar2Drop)) Fail("poseCar2Drop is all zeros — not captured.");
                break;
        }

        Debug.Log($"╚══ ARM SELF-CHECK DONE: {issues} failure(s). " +
                  $"{(issues == 0 ? "All clear — arm ready to run." : "FIX THE ERRORS ABOVE BEFORE TESTING.")} ══╝");
    }

    // ── Public API ────────────────────────────────────────────────────────────
    /// <summary>
    /// Queue an object for this arm. Multiple cubes can be in the pipeline at once
    /// (e.g. one being machined at Arm2 while another rides Conv1 toward it), so this
    /// no longer overwrites 'cube' directly — it appends to 'cubeQueue'. The arm pulls
    /// the next queued object into 'cube' at the start of each sequence (see RunSequence).
    /// </summary>
    public void SetCubeReference(Transform newCube)
    {
        if (newCube == null) return;

        if (!cubeQueue.Contains(newCube)) cubeQueue.Add(newCube);
        dbQueuedCubes = cubeQueue.Count;

        // If idle and nothing else queued, reflect it immediately for Inspector/manual preview.
        if (!isExecuting && cubeQueue.Count == 1)
        {
            cube       = newCube;
            dbCubeName = newCube.name;
        }

        Debug.Log($"[ARM:{role}] Queued '{newCube.name}' (queue={dbQueuedCubes})");
    }

    public void NotifyRobotTrigger()
    {
        if (isExecuting || eStopActive) return;

        // Guard: joints must be assigned
        bool hasJoints = joints != null && joints.Exists(j => j != null);
        if (!hasJoints)
        {
            Debug.LogError($"[ARM:{role}] Cannot run — no joints assigned! " +
                           "Assign at least joint1 in the Inspector.");
            return;
        }

        // Guard: warn if all poses are zero (not captured yet)
        if (AllPosesAreZero())
        {
            Debug.LogError($"[ARM:{role}] Cannot run — all poses are zero (0,0,0,0,0,0). " +
                           "Use the Inspector capture buttons to record each pose position first, " +
                           "then Save Poses To File.");
            return;
        }

        // Guard: cube must be available
        if (cube == null && cubeQueue.Count == 0)
        {
            Debug.LogError($"[ARM:{role}] Cannot run — no cube assigned or queued. " +
                           "Check WarehouseManager dispatched an object and SetCubeReference was called.");
            return;
        }

        StartCoroutine(RunSequence());
    }

    bool AllPosesAreZero()
    {
        // Returns true if EVERY relevant pose for this arm role is still at default (0,0,0,0,0,0)
        // This means poses have never been captured — a common setup mistake.
        bool IsZero(RobotPose p) => p.j1==0 && p.j2==0 && p.j3==0 && p.j4==0 && p.j5==0 && p.j6==0;
        return IsZero(poseHome) && IsZero(poseWaypointA) && IsZero(poseGrab);
    }

    // ── Sequence runner with watchdog ─────────────────────────────────────────
    IEnumerator RunSequence()
    {
        isExecuting      = true;
        dbExecuting      = true;
        hasGripped       = false;
        coroutineRunning = true;

        // Pull the next queued object, if any. Falls back to the manually-assigned
        // 'cube' field (offline / Inspector testing without a queue).
        if (cubeQueue.Count > 0)
        {
            cube = cubeQueue[0];
            cubeQueue.RemoveAt(0);
            dbQueuedCubes = cubeQueue.Count;
            dbCubeName    = cube != null ? cube.name : "—";
            Debug.Log($"[ARM:{role}] Sequence start — using queued '{dbCubeName}' " +
                      $"({dbQueuedCubes} remaining in queue).");
        }
        else if (cube != null)
        {
            dbCubeName = cube.name;
            Debug.Log($"[ARM:{role}] Sequence start — queue empty, using assigned '{dbCubeName}'.");
        }
        else
        {
            Debug.LogWarning($"[ARM:{role}] Sequence start — no cube queued or assigned!");
        }

        SetOutput(out_ArmBusy, true);
        SetOutput(out_ArmIdle, false);

        switch (role)
        {
            case ArmRole.Arm1_Car1ToConveyor1: StartCoroutine(Arm1_Sequence()); break;
            case ArmRole.Arm2_Conv1CNCConv2:   StartCoroutine(Arm2_Sequence()); break;
            case ArmRole.Arm3_Conv2ToCar2:     StartCoroutine(Arm3_Sequence()); break;
        }

        float elapsed = 0f;
        while (coroutineRunning)
        {
            elapsed += Time.deltaTime;
            if (sequenceTimeoutSeconds > 0f && elapsed >= sequenceTimeoutSeconds)
            {
                Debug.LogError($"[ARM:{role}] ⚠ TIMEOUT after {elapsed:F1}s — aborting.");
                StopAllCoroutines();
                coroutineRunning = false;
                EmergencyRelease();
                SetOutput(out_ArmError, true);
                yield return waitHalfSec;
                SetOutput(out_ArmError, false);
                break;
            }
            yield return null;
        }

        isExecuting = false;
        dbExecuting = false;
        SetOutput(out_ArmBusy, false);
        SetOutput(out_ArmIdle, true);
        SetStep("Idle");

        // If more objects queued, start next sequence immediately without waiting
        // for an external trigger — prevents pipeline stall when Conv1/Conv2 delivers
        // a second cube before Sensor fires again.
        if (cubeQueue.Count > 0 && !eStopActive)
        {
            Debug.Log($"[ARM:{role}] Auto-chaining next queued object ({cubeQueue.Count} remaining).");
            StartCoroutine(RunSequence());
        }
    }

    // ══ ARM 1 — Car1 staging → Conveyor 1 ════════════════════════════════════
    IEnumerator Arm1_Sequence()
    {
        SetStep("Arm1 ▸ Home → WaypointA");
        yield return GoTo(poseWaypointA);

        SetStep("Arm1 ▸ WaypointA → Grab");
        SetOutput(out_ArmAtGrab, true);
        yield return GoTo(poseGrab);

        SetStep("Arm1 ▸ Gripping from Car1");
        if (cube != null)
        {
            cube.SetParent(null, worldPositionStays: true);
            if (car1GrabPoint != null) cube.position = car1GrabPoint.position;
        }
        Grip(sourceBelt: null);
        // Car1 staging is not a conveyor belt, but clear any sensor override on
        // sourceConveyorMotor in case it was set externally before this sequence.
        sourceConveyorMotor?.SetSensorOverride(false);
        SetOutput(out_ArmGripped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmGripped, false);
        SetOutput(out_ArmAtGrab,  false);
        hasGripped = true;

        SetStep("Arm1 ▸ Grab → WaypointA");
        yield return GoTo(poseWaypointA);

        SetStep("Arm1 ▸ WaypointA → WaypointB");
        yield return GoTo(poseWaypointB);

        SetStep("Arm1 ▸ WaypointB → Conv1Drop");
        SetOutput(out_ArmAtDrop, true);
        yield return GoTo(poseConv1Drop);

        SetStep("Arm1 ▸ Releasing on Conveyor 1");
        if (conv1DropPoint != null && cube != null)
            cube.position = conv1DropPoint.position;
        conveyor1Motor?.SetObjectToMove(cube);
        ReleaseOntoBelt(conveyor1Motor);

        SetOutput(out_ArmDropped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmDropped, false);
        SetOutput(out_ArmAtDrop,  false);

        SetStep("Arm1 ▸ Conv1Drop → Home");
        yield return GoTo(poseHome);

        coroutineRunning = false;
    }

    // ══ ARM 2 — Conveyor 1 → CNC → Conveyor 2 ════════════════════════════════
    IEnumerator Arm2_Sequence()
    {
        SetStep("Arm2 ▸ Home → WaypointA");
        yield return GoTo(poseWaypointA);

        SetStep("Arm2 ▸ WaypointA → Grab (Conv1)");
        SetOutput(out_ArmAtGrab, true);
        yield return GoTo(poseGrab);

        SetStep("Arm2 ▸ Gripping from Conveyor 1");
        Grip(sourceBelt: sourceConveyorMotor);
        SetOutput(out_ArmGripped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmGripped, false);
        SetOutput(out_ArmAtGrab,  false);
        hasGripped = true;

        SetStep("Arm2 ▸ Grab → WaypointA");
        yield return GoTo(poseWaypointA);

        SetStep("Arm2 ▸ WaypointA → WaypointB");
        yield return GoTo(poseWaypointB);

        SetStep("Arm2 ▸ WaypointB → CNCPlace");
        SetOutput(out_ArmAtCNC, true);
        yield return GoTo(poseCNCPlace);

        SetStep("Arm2 ▸ Placing on CNC");
        if (cncDropPoint != null && cube != null) cube.position = cncDropPoint.position;

        // Always get CubeProcessor from the actual active cube — not a single fallback field.
        // This means every product object in the scene MUST have a CubeProcessor component.
        // If one is missing the arm logs exactly which object is the problem.
        CubeProcessor cp = cube != null ? cube.GetComponent<CubeProcessor>() : null;
        if (cp == null && cube != null)
            Debug.LogWarning($"[ARM:{role}] '{cube.name}' has no CubeProcessor component — " +
                             "shape and colour will NOT change at CNC. " +
                             "Add a CubeProcessor component to every product object prefab/scene object.");

        Transform cncCube = cube;   // cache before release — cube field must survive the CNC wait
        ReleaseToSurface();         // unparent FIRST so collider update works correctly
        cp?.Process();              // then change mesh/collider (no-op if cp is null)
        yield return waitGripDelay;

        SetOutput(out_CNCStart, true);
        yield return waitShortPulse;
        SetOutput(out_CNCStart, false);

        SetStep("Arm2 ▸ CNCPlace → WaypointB (retract)");
        yield return GoTo(poseWaypointB);
        SetOutput(out_ArmAtCNC, false);

        // Wait for CNC — timer in offline, PLC tag when connected
        SetStep($"Arm2 ▸ Waiting CNC ({cncProcessTime:F1}s)");
        cncDoneFlag = false;
        if (!offlineMode && !string.IsNullOrEmpty(in_CNCDoneTag))
        {
            float t2 = 0f, limit = cncProcessTime + 60f;
            yield return new WaitUntil(() => { t2 += Time.deltaTime; return cncDoneFlag || t2 >= limit; });
        }
        else
        {
            yield return new WaitForSeconds(cncProcessTime);
        }
        cncDoneFlag = false;

        // Restore cube reference from cache in case field was touched during wait
        if (cube == null) cube = cncCube;

        SetStep("Arm2 ▸ WaypointB → CNCPlace (pick)");
        SetOutput(out_ArmAtCNC, true);
        yield return GoTo(poseCNCPlace);

        SetStep("Arm2 ▸ Picking processed part");
        Grip(sourceBelt: null);
        SetOutput(out_ArmGripped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmGripped, false);

        SetStep("Arm2 ▸ CNCPlace → WaypointB");
        yield return GoTo(poseWaypointB);
        SetOutput(out_ArmAtCNC, false);

        SetStep("Arm2 ▸ WaypointB → WaypointC");
        yield return GoTo(poseWaypointC);

        SetStep("Arm2 ▸ WaypointC → Conv2Drop");
        SetOutput(out_ArmAtDrop, true);
        yield return GoTo(poseConv2Drop);

        SetStep("Arm2 ▸ Releasing on Conveyor 2");
        Transform cubeRef = cube;
        arm3?.SetCubeReference(cubeRef);
        sensor2?.SetExpectedObject(cubeRef?.gameObject);
        destConveyorMotor?.SetObjectToMove(cube);
        ReleaseOntoBelt(destConveyorMotor);

        SetOutput(out_ArmDropped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmDropped, false);
        SetOutput(out_ArmAtDrop,  false);

        SetStep("Arm2 ▸ Conv2Drop → Home");
        yield return GoTo(poseHome);

        coroutineRunning = false;
    }

    // ══ ARM 3 — Conveyor 2 → RobotCar2 ══════════════════════════════════════
    IEnumerator Arm3_Sequence()
    {
        SetStep("Arm3 ▸ Home → WaypointA");
        yield return GoTo(poseWaypointA);

        SetStep("Arm3 ▸ WaypointA → Grab (Conv2)");
        SetOutput(out_ArmAtGrab, true);
        yield return GoTo(poseGrab);

        SetStep("Arm3 ▸ Gripping from Conveyor 2");
        Grip(sourceBelt: sourceConveyorMotor);
        SetOutput(out_ArmGripped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmGripped, false);
        SetOutput(out_ArmAtGrab,  false);
        hasGripped = true;

        SetStep("Arm3 ▸ Grab → WaypointA");
        yield return GoTo(poseWaypointA);

        SetStep("Arm3 ▸ WaypointA → WaypointB");
        yield return GoTo(poseWaypointB);

        SetStep("Arm3 ▸ WaypointB → Car2Drop");
        SetOutput(out_ArmAtDrop, true);
        yield return GoTo(poseCar2Drop);

        SetStep("Arm3 ▸ Placing into RobotCar2");
        if (car2DropPoint != null && cube != null) cube.position = car2DropPoint.position;
        Transform cubeRef = cube;
        ReleaseToSurface();
        robotCar2?.NotifyBoxLoaded(cubeRef);

        SetOutput(out_ArmDropped, true);
        yield return waitGripDelay;
        SetOutput(out_ArmDropped, false);
        SetOutput(out_ArmAtDrop,  false);

        SetStep("Arm3 ▸ Car2Drop → Home");
        yield return GoTo(poseHome);

        coroutineRunning = false;
    }

    // ── Grip / Release ────────────────────────────────────────────────────────

    void Grip(ConveyorMotor sourceBelt)
    {
        if (cube == null || gripPoint == null) return;
        if (sourceBelt != null) { sourceBelt.SetHeld(true); frozenBelt = sourceBelt; }

        cubeRb = cube.GetComponent<Rigidbody>();   // cache once
        if (cubeRb != null) { cubeRb.linearVelocity=Vector3.zero; cubeRb.angularVelocity=Vector3.zero; cubeRb.isKinematic=true; cubeRb.detectCollisions=false; }
        cube.SetParent(gripPoint, worldPositionStays: true);
        dbHeld = true;
    }

    void ReleaseOntoBelt(ConveyorMotor destBelt)
    {
        if (cube == null) return;
        cube.SetParent(null, worldPositionStays: true);
        if (cubeRb != null) { cubeRb.isKinematic=false; cubeRb.detectCollisions=true; cubeRb.linearVelocity=Vector3.zero; cubeRb.angularVelocity=Vector3.zero; }
        cubeRb = null;
        if (frozenBelt != null) { frozenBelt.SetHeld(false); frozenBelt.SetSensorOverride(false); frozenBelt=null; }
        if (destBelt != null)   { destBelt.SetHeld(false); }
        dbHeld = false;
    }

    void ReleaseToSurface()
    {
        if (cube == null) return;
        cube.SetParent(null, worldPositionStays: true);
        if (cubeRb != null) { cubeRb.isKinematic=true; cubeRb.detectCollisions=false; cubeRb.linearVelocity=Vector3.zero; cubeRb.angularVelocity=Vector3.zero; }
        cubeRb = null;
        if (frozenBelt != null) { frozenBelt.SetHeld(false); frozenBelt.SetSensorOverride(false); frozenBelt=null; }
        dbHeld = false;
    }

    public void EmergencyRelease()
    {
        if (cube != null)
        {
            cube.SetParent(null, worldPositionStays: true);
            var rb = cubeRb != null ? cubeRb : cube.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic=false; rb.detectCollisions=true; rb.linearVelocity=Vector3.zero; rb.angularVelocity=Vector3.zero; }
        }
        cubeRb = null;
        if (frozenBelt != null) { frozenBelt.SetHeld(false); frozenBelt.SetSensorOverride(false); frozenBelt=null; }
        sourceConveyorMotor?.SetHeld(false);
        destConveyorMotor?.SetHeld(false);
        dbHeld=false; isExecuting=false; dbExecuting=false; coroutineRunning=false;
        SetOutput(out_ArmBusy, false);
        SetOutput(out_ArmIdle, true);
    }

    // ── Motion ────────────────────────────────────────────────────────────────
    IEnumerator GoTo(RobotPose pose)
    {
        yield return MoveToAngles(pose);
        yield return waitPause;
    }

    IEnumerator MoveToAngles(RobotPose pose)
    {
        float[] tgt=pose.ToArr(), cur=GetAngles();
        float maxD=0f;
        for(int i=0;i<joints.Count;i++)
        {
            if(joints[i]==null) continue;
            float d=Mathf.Abs(Mathf.DeltaAngle(cur[i],tgt[i]));
            if(d>maxD) maxD=d;
        }
        float dur=maxD/Mathf.Max(jointSpeed,0.1f);
        if(dur<0.001f) yield break;

        float el=0f;
        while(el<dur)
        {
            el+=Time.deltaTime;
            float t=Mathf.Clamp01(el/dur);
            float s=Mathf.Lerp(t,Mathf.SmoothStep(0f,1f,t),smoothing);
            for(int i=0;i<joints.Count;i++)
            {
                if(joints[i]==null) continue;
                SetAngle(joints[i],axes[i],Mathf.LerpAngle(cur[i],tgt[i],s));
            }
            yield return null;
        }
        for(int i=0;i<joints.Count;i++) { if(joints[i]==null) continue; SetAngle(joints[i],axes[i],tgt[i]); }
    }

    void  SetAngle(Transform j,RotAxis ax,float a)
    { Vector3 e=j.localEulerAngles; switch(ax){case RotAxis.X:e.x=a;break;case RotAxis.Y:e.y=a;break;default:e.z=a;break;} j.localEulerAngles=e; }

    float GetAngle(Transform j,RotAxis ax)
    { if(j==null)return 0f; Vector3 e=j.localEulerAngles; float r=ax==RotAxis.X?e.x:ax==RotAxis.Y?e.y:e.z; return r>180f?r-360f:r; }

    float[] GetAngles() { var a=new float[joints.Count]; for(int i=0;i<joints.Count;i++) a[i]=GetAngle(joints[i],axes[i]); return a; }

    void BuildLists()
    {
        joints=new List<Transform>{joint1,joint2,joint3,joint4,joint5,joint6};
        axes  =new List<RotAxis>  {j1Axis,j2Axis,j3Axis,j4Axis,j5Axis,j6Axis};
    }

    void ApplyCaptures()
    {
        if(_capHome)      {CaptureInto(poseHome);      _capHome=false;}
        if(_capWpA)       {CaptureInto(poseWaypointA); _capWpA=false;}
        if(_capGrab)      {CaptureInto(poseGrab);      _capGrab=false;}
        if(_capWpB)       {CaptureInto(poseWaypointB); _capWpB=false;}
        if(_capConv1Drop) {CaptureInto(poseConv1Drop); _capConv1Drop=false;}
        if(_capCNCPlace)  {CaptureInto(poseCNCPlace);  _capCNCPlace=false;}
        if(_capWpC)       {CaptureInto(poseWaypointC); _capWpC=false;}
        if(_capConv2Drop) {CaptureInto(poseConv2Drop); _capConv2Drop=false;}
        if(_capCar2Drop)  {CaptureInto(poseCar2Drop);  _capCar2Drop=false;}
    }

    public void CaptureInto(RobotPose p)
    {
        if(joints==null||joints.Count==0) BuildLists();
#if UNITY_EDITOR
        Undo.RecordObject(this,$"Capture [{p.name}]");
#endif
        p.FromArr(GetAngles());
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
        if(p.savePoseOnCapture) EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    // ── Per-robot Pose File Persistence ──────────────────────────────────────
    /// <summary>
    /// Auto-generated, filesystem-safe default filename for this arm's pose file,
    /// based on its Role + GameObject name (e.g. "Arm1_Car1ToConveyor1_RobotArm_1").
    /// Two different arms will always get two different files, even on identical prefabs.
    /// </summary>
    public string DefaultPoseFileName()
    {
        string safe = gameObject.name;
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        safe = safe.Replace(' ', '_');
        return $"{role}_{safe}";
    }

    string PoseFolder()
    {
        string dir =
#if UNITY_EDITOR
            System.IO.Path.Combine(Application.dataPath, "RobotPoses");
#else
            System.IO.Path.Combine(Application.persistentDataPath, "RobotPoses");
#endif
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Full path to this arm's pose file (per-robot — never shared between arms).</summary>
    public string PoseFilePath()
    {
        string fname = string.IsNullOrEmpty(poseFileName) ? DefaultPoseFileName() : poseFileName;
        return System.IO.Path.Combine(PoseFolder(), fname + ".json");
    }

    /// <summary>Write all 9 captured poses to this arm's own JSON file.</summary>
    public void SavePosesToFile()
    {
        var data = new PoseFileData
        {
            poseHome      = poseHome,
            poseWaypointA = poseWaypointA,
            poseGrab      = poseGrab,
            poseWaypointB = poseWaypointB,
            poseConv1Drop = poseConv1Drop,
            poseCNCPlace  = poseCNCPlace,
            poseWaypointC = poseWaypointC,
            poseConv2Drop = poseConv2Drop,
            poseCar2Drop  = poseCar2Drop,
        };

        string path = PoseFilePath();
        try
        {
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(data, true));
            Debug.Log($"[ARM:{role}] ✔ Poses saved → {path}");
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ARM:{role}] Save poses failed: {e.Message}");
        }
    }

    /// <summary>Read all 9 poses from this arm's own JSON file (if it exists).</summary>
    public void LoadPosesFromFile()
    {
        string path = PoseFilePath();
        if (!System.IO.File.Exists(path))
        {
            Debug.LogWarning($"[ARM:{role}] No pose file found at '{path}'.");
            return;
        }

        try
        {
            var data = JsonUtility.FromJson<PoseFileData>(System.IO.File.ReadAllText(path));
#if UNITY_EDITOR
            Undo.RecordObject(this, $"Load Poses [{role}]");
#endif
            poseHome      = data.poseHome;
            poseWaypointA = data.poseWaypointA;
            poseGrab      = data.poseGrab;
            poseWaypointB = data.poseWaypointB;
            poseConv1Drop = data.poseConv1Drop;
            poseCNCPlace  = data.poseCNCPlace;
            poseWaypointC = data.poseWaypointC;
            poseConv2Drop = data.poseConv2Drop;
            poseCar2Drop  = data.poseCar2Drop;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
            Debug.Log($"[ARM:{role}] ✔ Poses loaded ← {path}");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ARM:{role}] Load poses failed: {e.Message}");
        }
    }

    void SetStep(string s) => dbStep=s;
    void SetOutput(string tag,bool v) { if(!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag,v); }

    public void PreviewHome()      => Preview(poseHome);
    public void PreviewWpA()       => Preview(poseWaypointA);
    public void PreviewGrab()      => Preview(poseGrab);
    public void PreviewWpB()       => Preview(poseWaypointB);
    public void PreviewConv1Drop() => Preview(poseConv1Drop);
    public void PreviewCNCPlace()  => Preview(poseCNCPlace);
    public void PreviewWpC()       => Preview(poseWaypointC);
    public void PreviewConv2Drop() => Preview(poseConv2Drop);
    public void PreviewCar2Drop()  => Preview(poseCar2Drop);

    void Preview(RobotPose p) { if(!isExecuting) StartCoroutine(PreviewCoroutine(p)); }
    IEnumerator PreviewCoroutine(RobotPose p)
    { isExecuting=true; SetStep($"Preview→{p.name}"); yield return MoveToAngles(p); isExecuting=false; SetStep("Idle"); }
}

#if UNITY_EDITOR
[CustomEditor(typeof(RobotArmController))]
public class RobotArmControllerEditor : Editor
{
    static readonly Color GREEN  = new Color(0.25f,0.75f,0.35f);
    static readonly Color BLUE   = new Color(0.25f,0.55f,0.90f);
    static readonly Color ORANGE = new Color(0.90f,0.50f,0.15f);
    static readonly Color RED    = new Color(0.85f,0.20f,0.20f);

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var arm=(RobotArmController)target;

        EditorGUILayout.Space(12);
        EditorGUILayout.LabelField("── POSE CAPTURE & PREVIEW ───────────────────────",EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Play mode: pose the arm joints in Scene, then press CAPTURE.\nPREVIEW moves arm to saved pose (arm must be idle).",MessageType.Info);
        EditorGUILayout.Space(4);

        Row(arm,"Home",      ref arm._capHome, arm.poseHome,      arm.PreviewHome);
        Row(arm,"WaypointA", ref arm._capWpA,  arm.poseWaypointA, arm.PreviewWpA);
        Row(arm,"Grab",      ref arm._capGrab, arm.poseGrab,      arm.PreviewGrab);
        Row(arm,"WaypointB", ref arm._capWpB,  arm.poseWaypointB, arm.PreviewWpB);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField($"── Role poses ({arm.role}) ──────────────────────",EditorStyles.boldLabel);
        switch(arm.role)
        {
            case RobotArmController.ArmRole.Arm1_Car1ToConveyor1:
                Row(arm,"Conv1 Drop",ref arm._capConv1Drop,arm.poseConv1Drop,arm.PreviewConv1Drop); break;
            case RobotArmController.ArmRole.Arm2_Conv1CNCConv2:
                Row(arm,"CNC Place", ref arm._capCNCPlace, arm.poseCNCPlace, arm.PreviewCNCPlace);
                Row(arm,"WaypointC", ref arm._capWpC,      arm.poseWaypointC,arm.PreviewWpC);
                Row(arm,"Conv2 Drop",ref arm._capConv2Drop,arm.poseConv2Drop,arm.PreviewConv2Drop); break;
            case RobotArmController.ArmRole.Arm3_Conv2ToCar2:
                Row(arm,"Car2 Drop", ref arm._capCar2Drop, arm.poseCar2Drop, arm.PreviewCar2Drop); break;
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("── POSE FILE (per-robot — no cross-arm conflicts) ────",EditorStyles.boldLabel);
        string fname = string.IsNullOrEmpty(arm.poseFileName) ? arm.DefaultPoseFileName() : arm.poseFileName;
        EditorGUILayout.LabelField($"File: RobotPoses/{fname}.json", EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor=GREEN;
        if(GUILayout.Button("💾  Save Poses To File",GUILayout.Height(28))) arm.SavePosesToFile();
        GUI.backgroundColor=BLUE;
        if(GUILayout.Button("📂  Load Poses From File",GUILayout.Height(28))) arm.LoadPosesFromFile();
        GUI.backgroundColor=Color.white;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("── CUBE QUEUE (multi-object pipeline) ────────────────",EditorStyles.boldLabel);
        string curName = arm.cube != null ? arm.cube.name : "—";
        EditorGUILayout.LabelField($"Active: {curName}    Queued: {arm.cubeQueue.Count}", EditorStyles.miniLabel);

        EditorGUILayout.Space(10);
        GUI.backgroundColor=ORANGE;
        if(GUILayout.Button("▶  Trigger Sequence Manually",GUILayout.Height(34)))
            if(Application.isPlaying) arm.NotifyRobotTrigger();
        GUI.backgroundColor=RED;
        if(GUILayout.Button("⚠  Emergency Release (Debug)",GUILayout.Height(28)))
            if(Application.isPlaying) arm.EmergencyRelease();
        GUI.backgroundColor=Color.white;
    }

    void Row(RobotArmController arm,string label,ref bool flag,
             RobotArmController.RobotPose pose,System.Action preview)
    {
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor=GREEN;
        if(GUILayout.Button($"⬇ Capture  {label}",GUILayout.Width(165)))
        { Undo.RecordObject(arm,$"Capture {label}"); arm.CaptureInto(pose); EditorUtility.SetDirty(arm);
          if(pose.savePoseOnCapture) EditorSceneManager.MarkSceneDirty(arm.gameObject.scene); }
        GUI.backgroundColor=BLUE;
        if(GUILayout.Button($"▶ Preview  {label}",GUILayout.Width(165)))
            if(Application.isPlaying) preview?.Invoke();
        GUI.backgroundColor=Color.white;
        EditorGUILayout.LabelField(
            $"J1={pose.j1:F0}° J2={pose.j2:F0}° J3={pose.j3:F0}° J4={pose.j4:F0}° J5={pose.j5:F0}° J6={pose.j6:F0}°",
            EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }
}
#endif
