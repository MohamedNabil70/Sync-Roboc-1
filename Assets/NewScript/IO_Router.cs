using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// IO_Router — central message bus between the OPC UA Bridge and all Unity components.
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE.
///   All SetValue() and SimulateInput() calls fire local callbacks as normal
///   but are NOT sent to the bridge. Perfect for testing without TIA Portal.
///   Switch to offlineMode = FALSE when connecting to real PLC.
///
///   SimulateInput(tag, value) — fires callbacks as if PLC sent the tag.
///     Use this in offline mode to simulate PLC inputs from UI buttons.
///     Does NOT send to bridge even in PLC mode.
///   SetValue(tag, value)      — fires callbacks AND sends to bridge in PLC mode.
///     Use for all Unity→PLC output tags.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   IO_Router itself has no tags — it routes all other components' tags.
///   Use LogAllTags() (context menu or auto-periodic) to see every registered
///   tag and its current value. Copy the list into your TIA Portal data block.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class IO_Router : MonoBehaviour
{
    public static IO_Router Instance { get; private set; }

    [Header("── Bridge (drag UnityBridgeClient GameObject here) ──────────")]
    public UnityBridgeClient bridge;

    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = all tags work locally only — nothing sent to PLC bridge. " +
             "Switch to FALSE when connecting to TIA Portal.")]
    public bool offlineMode = true;

    [Header("── Settings ────────────────────────────────────────────────")]
    [Tooltip("Keep this GameObject alive across scene loads")]
    public bool dontDestroyOnLoad = true;
    [Tooltip("Print all tags periodically to console")]
    public bool periodicTagDump   = false;
    [Tooltip("Seconds between tag dumps (when periodicTagDump = true)")]
    public float tagDumpInterval  = 30f;

    [Header("── Debug (Read Only) ─────────────────────────────────────────")]
    [SerializeField] int    dbRegisteredTags = 0;
    [SerializeField] int    dbCachedTags     = 0;
    [SerializeField] string dbLastTagIn      = "—";
    [SerializeField] string dbLastTagOut     = "—";
    [SerializeField] bool   dbBridgeOnline   = false;
    [SerializeField] string dbMode           = "Offline";

    // ── Internal ──────────────────────────────────────────────────────────────
    readonly Dictionary<string, List<Action<bool>>> map   = new();
    readonly Dictionary<string, bool>               cache = new();
    float dumpTimer = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);

        if (bridge == null)
        {
            bridge = GetComponentInChildren<UnityBridgeClient>();
            if (bridge == null) bridge = FindObjectOfType<UnityBridgeClient>();
            if (bridge != null) Debug.Log($"[IO ROUTER] Auto-found bridge on '{bridge.gameObject.name}'");
            else                Debug.LogWarning("[IO ROUTER] No UnityBridgeClient found — OFFLINE mode forced.");
        }

        dbMode = offlineMode ? "Offline" : "PLC";
    }

    void OnEnable()  { UnityBridgeClient.OnMessage += HandleMessage; }
    void OnDisable() { UnityBridgeClient.OnMessage -= HandleMessage; }

    void Update()
    {
        dbBridgeOnline   = bridge != null && !offlineMode;
        dbRegisteredTags = map.Count;
        dbCachedTags     = cache.Count;
        dbMode           = offlineMode ? "Offline" : "PLC";

        if (periodicTagDump)
        {
            dumpTimer += Time.deltaTime;
            if (dumpTimer >= tagDumpInterval) { dumpTimer = 0f; LogAllTags(); }
        }
    }

    // ── Register / Unregister ─────────────────────────────────────────────────
    public void Register(string tag, Action<bool> callback)
    {
        if (string.IsNullOrEmpty(tag) || callback == null) return;
        if (!map.ContainsKey(tag)) map[tag] = new List<Action<bool>>();
        if (!map[tag].Contains(callback)) map[tag].Add(callback);
        if (cache.TryGetValue(tag, out bool cached))
        {
            try { callback(cached); }
            catch (Exception e) { Debug.LogError($"[IO ROUTER] Register callback error for '{tag}': {e.Message}"); }
        }
    }

    public void Unregister(string tag, Action<bool> callback)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (map.TryGetValue(tag, out var list))
        {
            list.Remove(callback);
            if (list.Count == 0) map.Remove(tag);
        }
    }

    public void Unregister(string tag) { if (!string.IsNullOrEmpty(tag)) map.Remove(tag); }

    // ── Read ──────────────────────────────────────────────────────────────────
    public bool GetValue(string tag) =>
        !string.IsNullOrEmpty(tag) && cache.TryGetValue(tag, out bool v) ? v : false;

    public IEnumerable<string> KnownTags       => cache.Keys;
    public List<string>        GetAllKnownTags() => cache.Keys.OrderBy(k => k).ToList();

    // ── Write — Unity → PLC ──────────────────────────────────────────────────
    /// <summary>
    /// Write an output tag. In PLC mode, sends to bridge AND fires local callbacks.
    /// In offline mode, fires local callbacks ONLY (no bridge send).
    /// Use this for all Unity → PLC output tags.
    /// </summary>
    public void SetValue(string tag, bool value)
    {
        if (string.IsNullOrEmpty(tag)) return;
        cache[tag]   = value;
        dbLastTagOut = $"{tag}={value}";

        if (!offlineMode && bridge != null)
        {
            bridge.Send(tag, value);
            Debug.Log($"[IO ROUTER] OUT → PLC: {tag} = {value}");
        }
        else
        {
            Debug.Log($"[IO ROUTER] OUT (offline): {tag} = {value}");
        }

        FireCallbacks(tag, value);
    }

    /// <summary>
    /// Simulate a PLC INPUT tag arriving — fires local callbacks only.
    /// Does NOT send to bridge even in PLC mode.
    /// Use this from GenericCommandButton in offline mode to simulate PLC inputs.
    /// </summary>
    public void SimulateInput(string tag, bool value)
    {
        if (string.IsNullOrEmpty(tag)) return;
        cache[tag]  = value;
        dbLastTagIn = $"[SIM] {tag}={value}";
        Debug.Log($"[IO ROUTER] SIM IN: {tag} = {value}");
        FireCallbacks(tag, value);
    }

    // ── Incoming message from PLC bridge ─────────────────────────────────────
    void HandleMessage(string msg)
    {
        if (string.IsNullOrEmpty(msg) || msg == "_RECONNECT_") return;
        if (offlineMode) return;   // ignore PLC messages in offline mode

        string tag      = Extract(msg, "Tag");
        string valueStr = Extract(msg, "Value");
        if (string.IsNullOrEmpty(tag)) return;

        bool value  = valueStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        cache[tag]  = value;
        dbLastTagIn = $"{tag}={value}";
        FireCallbacks(tag, value);
    }

    readonly List<Action<bool>> callbackScratch = new List<Action<bool>>();

    void FireCallbacks(string tag, bool value)
    {
        if (!map.TryGetValue(tag, out var callbacks)) return;
        callbackScratch.Clear();
        callbackScratch.AddRange(callbacks);   // snapshot without allocating a new list
        foreach (var cb in callbackScratch)
        {
            try   { cb(value); }
            catch (Exception e) { Debug.LogError($"[IO ROUTER] Callback error for '{tag}': {e.Message}"); }
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────
    /// <summary>
    /// Prints all known tags and values. Copy the list into your TIA Portal DB.
    /// Call via context menu or set periodicTagDump = true.
    /// </summary>
    public void LogAllTags()
    {
        Debug.Log($"╔══════════════ IO_ROUTER TAG DUMP  [{dbMode}] ══════════════╗");
        Debug.Log($"  Bridge     : {(bridge!=null?"connected":"not found")}");
        Debug.Log($"  Registered : {map.Count} tags   Cached: {cache.Count} tags");
        Debug.Log("  ── All cached values ──");
        foreach (var kv in cache.OrderBy(k => k.Key))
            Debug.Log($"    {kv.Key,-45} = {kv.Value}");
        Debug.Log("╚═════════════════════════════════════════════════════════════╝");
    }

    static string Extract(string json, string key)
    {
        string search = $"\"{key}\":";
        int start = json.IndexOf(search);
        if (start == -1) return "";
        start += search.Length;
        int end = json.IndexOfAny(new char[]{ ',', '}' }, start);
        if (end == -1) end = json.Length;
        return json[start..end].Replace("\"", "").Trim();
    }

#if UNITY_EDITOR
    [ContextMenu("Log All Tags")]
    void EditorLogAllTags() => LogAllTags();

    [ContextMenu("Toggle Offline Mode")]
    void EditorToggleOffline()
    {
        offlineMode = !offlineMode;
        dbMode      = offlineMode ? "Offline" : "PLC";
        Debug.Log($"[IO ROUTER] Mode switched to: {dbMode}");
    }
#endif
}
