using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// CubeProcessor — changes shape and colour when placed on CNC, restores when recycled.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   No offlineMode field needed — Process() and Restore() are always called
///   directly by Arm2 and WarehouseManager respectively, in both offline and
///   PLC modes. The in_ProcessCmd / in_RestoreCmd PLC input tags are optional
///   extras for manual HMI testing only.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   INPUTS  (PLC → Unity):
///     in_ProcessCmd  — rising edge: trigger Process() manually from PLC/HMI
///     in_RestoreCmd  — rising edge: trigger Restore() manually from PLC/HMI
///
///   OUTPUTS (Unity → PLC):
///     out_IsProcessed   — TRUE while object has been processed (CNC output state)
///     out_IsRaw         — TRUE while object is in raw/unprocessed state
///     out_ProcessDone   — pulse TRUE when Process() completes
///     out_RestoreDone   — pulse TRUE when Restore() completes
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class CubeProcessor : MonoBehaviour
{
    public enum ProcessedShape { Sphere, Cylinder, Capsule, Cube }

    [Header("══ CNC Output Appearance (set in Inspector) ══════════════════")]
    [Tooltip("Shape the object becomes after CNC machining")]
    public ProcessedShape processedShape = ProcessedShape.Sphere;
    [Tooltip("Colour the object becomes after CNC machining")]
    public Color processedColor = new Color(0.2f, 0.85f, 0.3f);

    [Header("══ PLC INPUT Tags (PLC → Unity) ════════════════════════════════")]
    [Tooltip("Rising edge: call Process() from PLC (normally done by arm automatically). " +
             "Useful for manual HMI testing.")]
    public string in_ProcessCmd = "";
    [Tooltip("Rising edge: call Restore() from PLC (normally done at start of each cycle). " +
             "Useful for manual HMI testing.")]
    public string in_RestoreCmd = "";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════════")]
    [Tooltip("TRUE while object is in processed (CNC output) state")]
    public string out_IsProcessed = "";
    [Tooltip("TRUE while object is in raw/unprocessed state")]
    public string out_IsRaw       = "";
    [Tooltip("Pulse TRUE when Process() completes")]
    public string out_ProcessDone = "";
    [Tooltip("Pulse TRUE when Restore() completes")]
    public string out_RestoreDone = "";

    [Header("══ Debug (Read Only) ════════════════════════════════════════════")]
    [SerializeField] bool   dbIsProcessed = false;
    [SerializeField] string dbShape       = "Raw";

    // ── Private ───────────────────────────────────────────────────────────────
    Mesh     originalMesh;
    Material originalMaterial;
    Material processedMaterial;

    MeshFilter   mf;
    MeshRenderer mr;

    public bool IsProcessed => dbIsProcessed;

    System.Action<bool> cbProcess, cbRestore;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        mf = GetComponent<MeshFilter>();
        mr = GetComponent<MeshRenderer>();

        if (mf == null) { Debug.LogError("[PROCESSOR] No MeshFilter on object!"); return; }
        if (mr == null) { Debug.LogError("[PROCESSOR] No MeshRenderer on object!"); return; }

        originalMesh     = mf.sharedMesh;
        originalMaterial = mr.material;

        processedMaterial       = new Material(originalMaterial);
        processedMaterial.color = processedColor;
    }

    void Start()
    {
        // PLC manual commands — optional HMI testing triggers
        if (!string.IsNullOrEmpty(in_ProcessCmd))
        {
            bool prev = false;
            cbProcess = v => { if (v && !prev) Process(); prev = v; };
            StartCoroutine(RegisterTag(in_ProcessCmd, cbProcess));
        }
        if (!string.IsNullOrEmpty(in_RestoreCmd))
        {
            bool prev = false;
            cbRestore = v => { if (v && !prev) Restore(); prev = v; };
            StartCoroutine(RegisterTag(in_RestoreCmd, cbRestore));
        }

        // Publish initial state
        SetOutput(out_IsRaw,       true);
        SetOutput(out_IsProcessed, false);
        Debug.Log($"[PROCESSOR:{name}] Ready. Shape={processedShape}");
    }

    void OnDestroy()
    {
        IO_Router.Instance?.Unregister(in_ProcessCmd, cbProcess);
        IO_Router.Instance?.Unregister(in_RestoreCmd, cbRestore);
    }

    System.Collections.IEnumerator RegisterTag(string tag, System.Action<bool> cb)
    {
        if (string.IsNullOrEmpty(tag)) yield break;
        while (IO_Router.Instance == null) yield return null;
        IO_Router.Instance.Register(tag, cb);
    }

    // ── Core API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply CNC output appearance. Called by Arm2 automatically.
    /// Can also be triggered by PLC via in_ProcessCmd.
    /// </summary>
    public void Process()
    {
        if (dbIsProcessed) return;

        // Rebuild processed material with current inspector colour
        if (processedMaterial == null && mr != null)
            processedMaterial = new Material(mr.material);
        if (processedMaterial != null)
            processedMaterial.color = processedColor;

        if (mf != null) mf.sharedMesh = GetMesh(processedShape);
        if (mr != null) mr.material   = processedMaterial;

        UpdateCollider(processed: true);
        dbIsProcessed = true;
        dbShape       = processedShape.ToString();

        SetOutput(out_IsProcessed, true);
        SetOutput(out_IsRaw,       false);
        SetOutput(out_ProcessDone, true);
        StartCoroutine(PulseDone(out_ProcessDone));

        Debug.Log($"[PROCESSOR:{name}] Processed → shape={processedShape}  color={processedColor}");
    }

    /// <summary>
    /// Restore to raw state. Called by WarehouseManager at start of each new cycle.
    /// Can also be triggered by PLC via in_RestoreCmd.
    /// </summary>
    public void Restore()
    {
        if (!dbIsProcessed) return;

        if (mf != null && originalMesh     != null) mf.sharedMesh = originalMesh;
        if (mr != null && originalMaterial != null) mr.material   = originalMaterial;

        UpdateCollider(processed: false);
        dbIsProcessed = false;
        dbShape       = "Raw";

        SetOutput(out_IsProcessed, false);
        SetOutput(out_IsRaw,       true);
        SetOutput(out_RestoreDone, true);
        StartCoroutine(PulseDone(out_RestoreDone));

        Debug.Log($"[PROCESSOR:{name}] Restored to raw state.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    void UpdateCollider(bool processed)
    {
        var mc = GetComponent<MeshCollider>();
        if (mc == null) return;
        mc.sharedMesh = null;
        mc.sharedMesh = processed ? GetMesh(processedShape) : originalMesh;
        mc.convex     = true;
    }

    // ── Static mesh cache — one entry per shape, shared across all CubeProcessor instances ──
    static readonly Dictionary<ProcessedShape, Mesh> meshCache = new Dictionary<ProcessedShape, Mesh>();

    static Mesh GetMesh(ProcessedShape s)
    {
        if (meshCache.TryGetValue(s, out Mesh cached) && cached != null) return cached;

        PrimitiveType pt = s switch
        {
            ProcessedShape.Sphere   => PrimitiveType.Sphere,
            ProcessedShape.Cylinder => PrimitiveType.Cylinder,
            ProcessedShape.Capsule  => PrimitiveType.Capsule,
            ProcessedShape.Cube     => PrimitiveType.Cube,
            _                       => PrimitiveType.Sphere
        };
        // Create a temporary primitive just to grab its shared mesh, then destroy the GO.
        // Result is cached so this happens only ONCE per shape across the entire session.
        var tmp = GameObject.CreatePrimitive(pt);
        Mesh m  = tmp.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(tmp);
        meshCache[s] = m;
        return m;
    }

    System.Collections.IEnumerator PulseDone(string tag)
    {
        yield return new WaitForSeconds(0.2f);
        SetOutput(tag, false);
    }

    void SetOutput(string tag, bool v)
    { if (!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag, v); }
}
