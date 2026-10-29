using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal static class Program
{
    internal static readonly string Dir = Environment.GetEnvironmentVariable("SAMPWN_DIR") ?? "/opt/etc/sampwn";
    internal static readonly string ShellStateFile = Dir + "/shell.state";
    internal static readonly string ShowPendingFile = Dir + "/show.pending";
    internal const string HookFile = "/opt/etc/parseinfo.sh";
    internal const string WidgetFile = "/opt/usr/apps/sampwnroot/res/wgt/index.html";
    internal static readonly int ShellPort = EnvInt("SAMPWN_SHELL_PORT", 2323);
    internal static readonly int CtlPort = EnvInt("SAMPWN_CTL_PORT", 2324);
    internal const string Version = "1.1.0";

    private static int EnvInt(string name, int fallback)
    {
        string s = Environment.GetEnvironmentVariable(name);
        int v;
        if (!string.IsNullOrEmpty(s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0 && v < 65536)
            return v;
        return fallback;
    }

    private static readonly object Gate = new object();
    private static ShellServer _shell;
    private static volatile bool _shellEnabled = true;

    public static int Main(string[] args)
    {
        try { Directory.CreateDirectory(Dir); } catch { }

        _shellEnabled = ReadShellState();
        EnsureShell();

        var ctl = new ControlServer();
        ctl.Start();

        Log("start ctl=127.0.0.1:" + CtlPort + " shell=" + (_shellEnabled ? "on" : "off"));

        var retry = new Thread(RetryLoop);
        retry.IsBackground = true;
        retry.Start();

        var quit = new ManualResetEvent(false);
        Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e) { e.Cancel = true; quit.Set(); };
        AppDomain.CurrentDomain.ProcessExit += delegate(object s, EventArgs e) { StopShell(); };
        quit.WaitOne();
        return 0;
    }

    private static void RetryLoop()
    {
        while (true)
        {
            Thread.Sleep(5000);
            try { EnsureShell(); }
            catch (Exception e) { Log("retry: " + e.Message); }
        }
    }

    internal static bool ShellEnabled { get { lock (Gate) return _shellEnabled; } }

    internal static bool ShellListening
    {
        get
        {
            lock (Gate)
            {
                return _shell != null && _shell.Listening;
            }
        }
    }

    internal static int ShellClients
    {
        get
        {
            lock (Gate)
            {
                return _shell == null ? 0 : _shell.ClientCount;
            }
        }
    }

    internal static bool ReadShellState()
    {
        try
        {
            if (!File.Exists(ShellStateFile)) return true; // default: on
            string s = File.ReadAllText(ShellStateFile).Trim().ToLowerInvariant();
            return !(s == "off" || s == "0" || s == "false" || s == "no");
        }
        catch { return true; }
    }

    internal static void WriteShellState(bool on)
    {
        try { File.WriteAllText(ShellStateFile, on ? "on\n" : "off\n"); }
        catch (Exception e) { Log("state write: " + e.Message); }
    }

    internal static void EnsureShell()
    {
        lock (Gate)
        {
            if (!_shellEnabled) return;
            if (_shell != null && _shell.Listening) return;
            if (_shell != null) { try { _shell.Stop(); } catch { } _shell = null; }
            try
            {
                var s = new ShellServer();
                s.Start();
                _shell = s;
                Log("shell listening 0.0.0.0:" + ShellPort);
            }
            catch (Exception e)
            {
                Log("shell bind failed: " + e.Message);
                _shell = null;
            }
        }
    }

    internal static void StopShell()
    {
        lock (Gate)
        {
            if (_shell == null) return;
            try { _shell.Stop(); } catch { }
            _shell = null;
            Log("shell stopped");
        }
    }

    internal static void SetShell(bool on)
    {
        lock (Gate) { _shellEnabled = on; }
        WriteShellState(on);
        if (on) EnsureShell();
        else StopShell();
        Log("shell desired=" + (on ? "on" : "off"));
    }

    private static readonly object LogGate = new object();

    internal static void Log(string msg)
    {
        try
        {
            lock (LogGate)
            {
                string f = Dir + "/sampwn.log";
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 262144) { try { File.Delete(f); } catch { } }
                File.AppendAllText(f, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + " " + msg + "\n");
            }
        }
        catch { }
    }

    internal static string StatusJson()
    {
        bool hook = false, widget = false;
        try { hook = File.Exists(HookFile) && File.ReadAllText(HookFile).IndexOf("SamPwn boot hook", StringComparison.Ordinal) >= 0; }
        catch { }
        try { widget = File.Exists(WidgetFile); }
        catch { }

        int pid = 0;
        try { pid = Process.GetCurrentProcess().Id; }
        catch { }

        var sb = new StringBuilder(256);
        sb.Append("{\"ok\":true");
        sb.Append(",\"shell\":").Append(ShellEnabled ? "true" : "false");
        sb.Append(",\"listening\":").Append(ShellListening ? "true" : "false");
        sb.Append(",\"clients\":").Append(ShellClients.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"port\":").Append(ShellPort.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"ip\":\"").Append(JsonEscape(GetIp())).Append("\"");
        sb.Append(",\"uptime\":").Append(UptimeSeconds().ToString("F1", CultureInfo.InvariantCulture));
        sb.Append(",\"show_pending\":").Append(File.Exists(ShowPendingFile) ? "true" : "false");
        sb.Append(",\"hook\":").Append(hook ? "true" : "false");
        sb.Append(",\"widget\":").Append(widget ? "true" : "false");
        sb.Append(",\"pid\":").Append(pid.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"version\":\"").Append(Version).Append("\"}");
        return sb.ToString();
    }

    internal static string JsonEscape(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    internal static string GetIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = ua.Address.ToString();
                    if (!s.StartsWith("127.", StringComparison.Ordinal)) return s;
                }
            }
        }
        catch { }
        return "?";
    }

    internal static double UptimeSeconds()
    {
        try
        {
            string s = File.ReadAllText("/proc/uptime").Trim();
            int sp = s.IndexOf(' ');
            if (sp > 0) s = s.Substring(0, sp);
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        catch { }
        return 0;
    }
}
