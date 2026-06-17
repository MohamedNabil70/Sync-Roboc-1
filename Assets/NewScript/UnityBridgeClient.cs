using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using UnityEngine;

/// <summary>
/// UnityBridgeClient — TCP client that connects to the OPC UA bridge (Program.cs).
///
/// ══ OFFLINE / SIMULATION MODE ═══════════════════════════════════════════════
///   Set offlineMode = TRUE (or simply leave IP/Port empty).
///   In offline mode the bridge does nothing — no TCP connection is made.
///   IO_Router's offlineMode = TRUE will also prevent any send calls from
///   reaching this component. Both flags should match.
///   All events (OnConnected, OnDisconnected) are suppressed in offline mode.
///
/// ══ PLC I/O Tags ════════════════════════════════════════════════════════════
///   OUTPUTS (Unity → PLC):
///     out_BridgeOnline  — TRUE when Unity TCP connection to bridge is active
///     out_BridgeOffline — TRUE when connection is down
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class UnityBridgeClient : MonoBehaviour
{
    [Header("══ Offline / Simulation ═════════════════════════════════════════")]
    [Tooltip("TRUE = no TCP connection attempted. Use for fully offline testing. " +
             "Should match IO_Router.offlineMode.")]
    public bool offlineMode = true;

    [Header("══ Bridge Connection ═══════════════════════════════════════")]
    [Tooltip("IP of the PC running Program.cs (OPC UA bridge)")]
    public string ip   = "127.0.0.1";
    [Tooltip("TCP port — must match port in Program.cs (default 5055)")]
    public int    port = 5055;
    [Tooltip("Seconds between reconnect attempts when disconnected")]
    public float  reconnectDelay = 2f;

    [Header("══ Heartbeat ═══════════════════════════════════════════════")]
    [Tooltip("Send heartbeat toggle every N seconds. 0 = disabled.")]
    public float  heartbeatInterval = 10f;
    [Tooltip("Bool tag in TIA Portal used for heartbeat")]
    public string heartbeatTag      = "OPC_Unity_Heartbeat";

    [Header("══ PLC OUTPUT Tags (Unity → PLC) ══════════════════════════")]
    public string out_BridgeOnline  = "Unity_BridgeOnline";
    public string out_BridgeOffline = "Unity_BridgeOffline";

    [Header("══ Debug (Read Only) ════════════════════════════════════════")]
    [SerializeField] bool   dbConnected      = false;
    [SerializeField] string dbLastSent       = "—";
    [SerializeField] string dbLastReceived   = "—";
    [SerializeField] int    dbQueueDepth     = 0;
    [SerializeField] int    dbReconnectCount = 0;
    [SerializeField] string dbMode           = "Offline";

    // ── Public events ─────────────────────────────────────────────────────────
    public static event Action<string> OnMessage;
    public static event Action         OnConnected;
    public static event Action         OnDisconnected;
    public bool IsConnected => !offlineMode && running;

    // ── Private ───────────────────────────────────────────────────────────────
    TcpClient     client;
    NetworkStream netStream;
    Thread        readThread;
    Thread        writeThread;

    volatile bool running = false;

    readonly Queue<string>  messageQueue = new Queue<string>();
    readonly object         mqLock       = new object();
    readonly Queue<byte[]>  sendQueue    = new Queue<byte[]>();
    readonly object         sqLock       = new object();

    bool  wasConnected   = false;
    float heartbeatTimer = 0f;
    bool  heartbeatState = false;

    // ─────────────────────────────────────────────────────────────────────────
    void Start()
    {
        dbMode = offlineMode ? "Offline" : "PLC";

        if (offlineMode)
        {
            Debug.Log("[BRIDGE] Offline mode — no TCP connection attempted.");
            return;
        }

        if (string.IsNullOrEmpty(ip) || port == 0)
        {
            Debug.LogError("[BRIDGE] IP or Port not set — bridge disabled.");
            return;
        }

        StartCoroutine(ConnectLoop());
    }

    void OnDestroy() { running = false; client?.Close(); }

    // ─────────────────────────────────────────────────────────────────────────
    void Update()
    {
        if (offlineMode) return;

        dbConnected  = running;
        dbQueueDepth = sendQueue.Count;

        // Fire connection events
        if (running && !wasConnected)
        {
            wasConnected = true;
            OnConnected?.Invoke();
            SetRouterOutput(out_BridgeOnline,  true);
            SetRouterOutput(out_BridgeOffline, false);
            Debug.Log("[BRIDGE] ✔ Connected — replaying cached tags.");
            ReplayCachedValues();
        }
        else if (!running && wasConnected)
        {
            wasConnected = false;
            OnDisconnected?.Invoke();
            SetRouterOutput(out_BridgeOnline,  false);
            SetRouterOutput(out_BridgeOffline, true);
            Debug.LogWarning("[BRIDGE] ✖ Disconnected.");
        }

        // Dispatch messages on main thread
        lock (mqLock)
        {
            while (messageQueue.Count > 0)
            {
                string msg = messageQueue.Dequeue();
                dbLastReceived = msg;
                if (msg == "_RECONNECT_") { StartCoroutine(ConnectLoop()); continue; }
                OnMessage?.Invoke(msg);
            }
        }

        // Heartbeat
        if (running && heartbeatInterval > 0f && !string.IsNullOrEmpty(heartbeatTag))
        {
            heartbeatTimer += Time.deltaTime;
            if (heartbeatTimer >= heartbeatInterval)
            {
                heartbeatTimer = 0f;
                heartbeatState = !heartbeatState;
                Send(heartbeatTag, heartbeatState);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    IEnumerator ConnectLoop()
    {
        while (true)
        {
            if (TryConnect()) yield break;
            dbReconnectCount++;
            Debug.LogWarning($"[BRIDGE] Cannot reach {ip}:{port} — retry in {reconnectDelay}s ({dbReconnectCount})");
            yield return new WaitForSeconds(reconnectDelay);
        }
    }

    bool TryConnect()
    {
        try
        {
            client    = new TcpClient(ip, port);
            netStream = client.GetStream();
            running   = true;
            lock (sqLock) { sendQueue.Clear(); }

            readThread  = new Thread(ReadLoop)  { IsBackground=true, Name="BridgeRead"  };
            writeThread = new Thread(WriteLoop) { IsBackground=true, Name="BridgeWrite" };
            readThread.Start();
            writeThread.Start();
            Debug.Log($"[BRIDGE] Connected to {ip}:{port}");
            return true;
        }
        catch (Exception e) { Debug.LogWarning($"[BRIDGE] Connect failed: {e.Message}"); return false; }
    }

    // ── Background READ ───────────────────────────────────────────────────────
    void ReadLoop()
    {
        byte[]        buf = new byte[4096];
        StringBuilder sb  = new StringBuilder();
        while (running)
        {
            try
            {
                int bytes = netStream!.Read(buf, 0, buf.Length);
                if (bytes == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, bytes));
                string all = sb.ToString(); int nl;
                while ((nl = all.IndexOf('\n')) >= 0)
                {
                    string line = all[..nl].Trim(); all = all[(nl+1)..];
                    if (!string.IsNullOrEmpty(line))
                        lock (mqLock) { messageQueue.Enqueue(line); }
                }
                sb.Clear(); sb.Append(all);
            }
            catch { break; }
        }
        running = false;
        lock (mqLock) { messageQueue.Enqueue("_RECONNECT_"); }
    }

    // ── Background WRITE ──────────────────────────────────────────────────────
    void WriteLoop()
    {
        while (running)
        {
            byte[] data = null;
            lock (sqLock) { if (sendQueue.Count > 0) data = sendQueue.Dequeue(); }
            if (data != null)
            {
                try { netStream!.Write(data, 0, data.Length); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[BRIDGE] Write failed ({e.Message}) — reconnecting.");
                    running = false;
                    lock (mqLock) { messageQueue.Enqueue("_RECONNECT_"); }
                    break;
                }
            }
            else { Thread.Sleep(5); }
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────
    public void Send(string tag, bool value)
    {
        if (offlineMode) return;   // silent in offline — IO_Router already logged it
        if (!running) { Debug.LogWarning($"[BRIDGE] Not connected — dropped: {tag}={value}"); return; }
        try
        {
            string json = $"{{\"Tag\":\"{tag}\",\"Value\":{value.ToString().ToLower()}}}";
            byte[] data = Encoding.UTF8.GetBytes(json + "\n");
            lock (sqLock) { sendQueue.Enqueue(data); }
            dbLastSent = json;
        }
        catch (Exception e) { Debug.LogWarning($"[BRIDGE] Send enqueue failed: {e.Message}"); }
    }

    // ── Replay on reconnect ───────────────────────────────────────────────────
    void ReplayCachedValues()
    {
        if (IO_Router.Instance == null) return;
        // Snapshot to avoid threading issues
        List<string> tags = IO_Router.Instance.GetAllKnownTags();
        int count = 0;
        foreach (string tag in tags)
        {
            Send(tag, IO_Router.Instance.GetValue(tag));
            count++;
        }
        Debug.Log($"[BRIDGE] Replayed {count} cached values.");
    }

    void SetRouterOutput(string tag, bool v)
    { if (!string.IsNullOrEmpty(tag)) IO_Router.Instance?.SetValue(tag, v); }
}
