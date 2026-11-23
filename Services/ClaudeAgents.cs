using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace NotchApp.Services;

public enum AgentState { Working, NeedsYou, Done }

public sealed class AgentSession
{
    public string Id = "";
    public string Title = "New session";
    public string Detail = "Starting…";
    public string Project = "";
    public AgentState State = AgentState.Working;
    public DateTime Since = DateTime.Now;
}

/// <summary>
/// Live status of your Claude Code sessions.
///
/// Claude Code runs "hooks" (commands) at moments like "you sent a prompt", "about to use a
/// tool", "needs your permission" and "finished". "Connect Claude Code" in Settings adds hooks
/// that pipe that event (JSON) with curl to this app on 127.0.0.1 - nothing leaves the PC.
/// A tiny TCP listener (no HTTP stack) receives them.
/// </summary>
public sealed class ClaudeAgents : IDisposable
{
    public const int Port = 47321;
    const string Marker = "127.0.0.1:47321/claude";
    // Run curl.exe directly with its arguments (no shell). A shell command line broke on PCs
    // without Git Bash, where Claude Code falls back to PowerShell 5 and can't parse "@-" or "||".
    // curl.exe ships with Windows 10/11. If the notch isn't running, curl just fails quietly.
    static readonly string[] CurlArgs = ["-s", "-m", "2", "-X", "POST", "--data-binary", "@-", $"http://{Marker}"];
    static readonly string[] HookEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "Notification", "Stop", "SessionEnd"];
    // NOTCHAPP_CLAUDE_SETTINGS lets tests work on a copy instead of your real settings.
    static readonly string SettingsPath = Environment.GetEnvironmentVariable("NOTCHAPP_CLAUDE_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    readonly System.Windows.Threading.Dispatcher _ui = Application.Current.Dispatcher;
    TcpListener? _listener;
    volatile bool _stopping;

    public List<AgentSession> Sessions { get; } = new();
    public event Action? Changed;
    /// <summary>Raised when a session starts needing you or finishes (for a toast).</summary>
    public event Action<AgentSession>? Alert;

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "ClaudeHooks" }.Start();
        }
        catch { _listener = null; } // port taken: the tab just stays empty
    }

    void AcceptLoop()
    {
        while (!_stopping)
        {
            try
            {
                using var client = _listener!.AcceptTcpClient();
                client.ReceiveTimeout = 2000;
                using var stream = client.GetStream();
                var body = ReadRequest(stream, out var path);
                var reply = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(reply);
                Log.Write($"claude hook: path={path} bytes={body?.Length ?? -1}");
                if (path == "/claude" && body != null) _ui.InvokeAsync(() => Handle(body));
            }
            catch (Exception ex) { Log.Write("claude hook error: " + ex.Message); if (_stopping) return; }
        }
    }

    /// <summary>Minimal HTTP: request line, headers, Content-Length body.</summary>
    static string? ReadRequest(NetworkStream s, out string path)
    {
        path = "";
        var buf = new List<byte>(4096);
        int b;
        while ((b = s.ReadByte()) != -1)
        {
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) break;
            if (n > 16384) return null;
        }
        var head = Encoding.ASCII.GetString(buf.ToArray());
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2 || parts[0] != "POST") return null;
        path = parts[1];
        int len = 0;
        foreach (var l in lines)
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(l[15..].Trim(), out len);
        if (len <= 0 || len > 1_000_000) return null;
        var body = new byte[len];
        int read = 0;
        while (read < len) { int r = s.Read(body, read, len - read); if (r <= 0) break; read += r; }
        return Encoding.UTF8.GetString(body, 0, read).TrimStart('﻿'); // tolerate a byte-order mark
    }

    void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string ev = Str(root, "hook_event_name");
            string id = Str(root, "session_id");
            if (id.Length == 0) return;

            var s = Sessions.FirstOrDefault(x => x.Id == id);
            if (ev == "SessionEnd") { if (s != null) { Sessions.Remove(s); Changed?.Invoke(); } return; }
            if (s == null)
            {
                s = new AgentSession { Id = id };
                Sessions.Insert(0, s);
            }
            var cwd = Str(root, "cwd");
            if (cwd.Length > 0) s.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            var before = s.State;

            switch (ev)
            {
                case "SessionStart":
                    s.State = AgentState.Done; s.Detail = "Ready"; s.Since = DateTime.Now;
                    break;
                case "UserPromptSubmit":
                    var prompt = Str(root, "prompt").Trim();
                    if (prompt.Length > 0) s.Title = OneLine(prompt, 70);
                    s.State = AgentState.Working; s.Detail = "Thinking…"; s.Since = DateTime.Now;
                    break;
                case "PreToolUse":
                    if (s.State != AgentState.Working) s.Since = DateTime.Now;
                    s.State = AgentState.Working;
                    s.Detail = Describe(Str(root, "tool_name"), root.TryGetProperty("tool_input", out var ti) ? ti : default);
                    break;
                case "Notification":
                    var msg = Str(root, "message");
                    s.State = AgentState.NeedsYou; s.Since = DateTime.Now;
                    var m = System.Text.RegularExpressions.Regex.Match(msg, @"permission to use (\w+)");
                    s.Detail = m.Success ? $"Allow {m.Groups[1].Value}?" : msg.Contains("waiting", StringComparison.OrdinalIgnoreCase) ? "Waiting for you" : OneLine(msg, 60);
                    break;
                case "Stop":
                    s.State = AgentState.Done; s.Detail = "Finished"; s.Since = DateTime.Now;
                    break;
                default: return;
            }
            // most recent activity first
            Sessions.Remove(s); Sessions.Insert(0, s);
            Changed?.Invoke();
            if (s.State != before && (s.State == AgentState.NeedsYou || (s.State == AgentState.Done && ev == "Stop"))) Alert?.Invoke(s);
            Log.Write($"claude {ev} {id}: {s.State} / {s.Detail}");
        }
        catch (Exception ex) { Log.Write("claude handle error: " + ex.Message); }
    }

    static string Describe(string tool, JsonElement input)
    {
        string Arg(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string file = Path.GetFileName(Arg("file_path"));
        return tool switch
        {
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => $"Editing {file}",
            "Read" => $"Reading {file}",
            "Bash" or "PowerShell" => $"Running {OneLine(Arg("command"), 40)}",
            "Grep" or "Glob" => "Searching the code",
            "WebFetch" or "WebSearch" => "Searching the web",
            "Task" or "Agent" => "Running a subagent",
            "TodoWrite" => "Planning",
            _ => $"Using {tool}",
        };
    }

    static string OneLine(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dismiss(AgentSession s) { Sessions.Remove(s); Changed?.Invoke(); }
    public void ClearFinished() { Sessions.RemoveAll(s => s.State == AgentState.Done); Changed?.Invoke(); }

    // ---- connecting Claude Code: add/remove our hooks in ~/.claude/settings.json ----

    public static bool IsConnected
    {
        get
        {
            try { return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker); }
            catch { return false; }
        }
    }

    /// <summary>Adds our hooks, keeping everything else in the file untouched. Backs it up first.</summary>
    public static void Connect()
    {
        var root = ReadSettings();
        RemoveOurs(root); // replaces entries written by older versions
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var ev in HookEvents)
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            var args = new JsonArray();
            foreach (var a in CurlArgs) args.Add(a);
            var entry = new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "curl.exe", ["args"] = args, ["timeout"] = 5 })
            };
            if (ev == "PreToolUse") entry["matcher"] = "*";
            list.Add(entry);
        }
        WriteSettings(root);
    }

    /// <summary>Removes only our hook entries.</summary>
    public static void Disconnect()
    {
        var root = ReadSettings();
        RemoveOurs(root);
        WriteSettings(root);
    }

    static void RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var ev in HookEvents)
        {
            if (hooks[ev] is not JsonArray list) continue;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i]?.ToJsonString().Contains(Marker) == true) list.RemoveAt(i);
            if (list.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    static JsonObject ReadSettings()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    static void WriteSettings(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".notchapp-backup", overwrite: true);
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose()
    {
        _stopping = true;
        try { _listener?.Stop(); } catch { }
    }
}
