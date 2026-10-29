using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal sealed class ControlServer
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, Program.CtlPort);
    private volatile bool _running;

    public void Start()
    {
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _running = true;
        var t = new Thread(AcceptLoop);
        t.IsBackground = true;
        t.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); }
            catch { break; }
            var t = new Thread(delegate() { Handle(c); });
            t.IsBackground = true;
            t.Start();
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            using (client)
            {
                NetworkStream s = client.GetStream();
                string raw;
                try { raw = ReadHeaders(s); }
                catch { return; }
                if (raw.Length == 0) return;

                string[] lines = raw.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string first = lines.Length > 0 ? lines[0] : "";
                string[] parts = first.Split(' ');
                string method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
                string path = parts.Length > 1 ? parts[1] : "/";

                var headers = ParseHeaders(lines);
                string upgrade;
                headers.TryGetValue("Upgrade", out upgrade);
                if (upgrade != null && upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    RunWebSocket(s, headers);
                    return;
                }

                if (method == "OPTIONS")
                {
                    SendHttp(s, 204, "No Content", "", "text/plain");
                    return;
                }

                string body;
                int code = 200;
                string reason = "OK";
                if (method == "GET" && path == "/status")
                {
                    body = Program.StatusJson();
                }
                else if (method == "GET" && path == "/health")
                {
                    body = "ok";
                }
                else if (method == "POST" && path == "/shell/on")
                {
                    Program.SetShell(true);
                    body = Program.StatusJson();
                }
                else if (method == "POST" && path == "/shell/off")
                {
                    Program.SetShell(false);
                    body = Program.StatusJson();
                }
                else if (method == "POST" && path == "/show/pending")
                {
                    TouchPending();
                    body = Program.StatusJson();
                }
                else if (method == "POST" && path == "/show/consume")
                {
                    ConsumePending();
                    body = Program.StatusJson();
                }
                else if (method == "POST" && path == "/log")
                {
                    string note = ReadBody(s, headers);
                    Program.Log("web: " + note);
                    body = "{\"ok\":true}";
                }
                else
                {
                    code = 404;
                    reason = "Not Found";
                    body = "{\"ok\":false,\"error\":\"not found\"}";
                }
                SendHttp(s, code, reason, body, path == "/health" ? "text/plain" : "application/json");
            }
        }
        catch (Exception e) { Program.Log("ctl: " + e.Message); }
    }

    internal static void TouchPending()
    {
        try { File.WriteAllText(Program.ShowPendingFile, DateTime.UtcNow.ToString("s") + "Z\n"); }
        catch (Exception e) { Program.Log("pending touch: " + e.Message); }
    }

    internal static void ConsumePending()
    {
        try { if (File.Exists(Program.ShowPendingFile)) File.Delete(Program.ShowPendingFile); }
        catch (Exception e) { Program.Log("pending consume: " + e.Message); }
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) break;
            int idx = line.IndexOf(':');
            if (idx > 0) d[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
        }
        return d;
    }

    private static string ReadBody(NetworkStream s, Dictionary<string, string> headers)
    {
        int len = 0;
        string cl;
        if (headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out len);
        if (len <= 0 || len > 4096) return "";
        byte[] buf = new byte[len];
        int off = 0;
        while (off < len)
        {
            int n = s.Read(buf, off, len - off);
            if (n <= 0) break;
            off += n;
        }
        return Encoding.UTF8.GetString(buf, 0, off);
    }

    private static string ReadHeaders(NetworkStream s)
    {
        var sb = new StringBuilder();
        byte[] one = new byte[1];
        int total = 0;
        while (total < 16384)
        {
            int n;
            try { n = s.Read(one, 0, 1); }
            catch { break; }
            if (n <= 0) break;
            sb.Append((char)one[0]);
            total++;
            int len = sb.Length;
            if (len >= 4 && sb[len - 4] == '\r' && sb[len - 3] == '\n' && sb[len - 2] == '\r' && sb[len - 1] == '\n') break;
        }
        return sb.ToString();
    }

    private static void SendHttp(NetworkStream s, int code, string reason, string body, string contentType)
    {
        byte[] p = Encoding.UTF8.GetBytes(body ?? "");
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(p.Length.ToString()).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, X-Sampwn\r\n");
        sb.Append("\r\n");
        byte[] h = Encoding.ASCII.GetBytes(sb.ToString());
        s.Write(h, 0, h.Length);
        if (p.Length > 0) s.Write(p, 0, p.Length);
        s.Flush();
    }

    // ---------------- websocket ----------------

    private void RunWebSocket(NetworkStream s, Dictionary<string, string> headers)
    {
        string key = "";
        headers.TryGetValue("Sec-WebSocket-Key", out key);
        if (string.IsNullOrEmpty(key)) { SendHttp(s, 400, "Bad Request", "missing key", "text/plain"); return; }

        string accept;
        using (var sha = SHA1.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
            accept = Convert.ToBase64String(hash);
        }
        string resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
        byte[] r = Encoding.ASCII.GetBytes(resp);
        s.Write(r, 0, r.Length);
        s.Flush();
        SendWsText(s, Program.StatusJson());

        while (true)
        {
            int op;
            string msg = ReadWsFrame(s, out op);
            if (msg == null) break;
            if (op == 8) { SendWsFrame(s, 0x88, new byte[0]); break; }
            if (op == 9) { SendWsFrame(s, 0x8A, Encoding.UTF8.GetBytes(msg)); continue; }
            if (op != 1) continue;

            string reply;
            if (msg.IndexOf("shell.on", StringComparison.Ordinal) >= 0)
            {
                Program.SetShell(true);
                reply = Program.StatusJson();
            }
            else if (msg.IndexOf("shell.off", StringComparison.Ordinal) >= 0)
            {
                Program.SetShell(false);
                reply = Program.StatusJson();
            }
            else if (msg.IndexOf("show.pending", StringComparison.Ordinal) >= 0)
            {
                TouchPending();
                reply = Program.StatusJson();
            }
            else if (msg.IndexOf("show.consume", StringComparison.Ordinal) >= 0)
            {
                ConsumePending();
                reply = Program.StatusJson();
            }
            else if (msg.IndexOf("status", StringComparison.Ordinal) >= 0)
            {
                reply = Program.StatusJson();
            }
            else
            {
                reply = "{\"ok\":false,\"error\":\"unknown\"}";
            }
            SendWsText(s, reply);
        }
    }

    private static string ReadWsFrame(NetworkStream s, out int opcode)
    {
        opcode = -1;
        byte[] h = new byte[2];
        if (!ReadExact(s, h, 2)) return null;
        int b0 = h[0], b1 = h[1];
        opcode = b0 & 0x0f;
        bool masked = (b1 & 0x80) != 0;
        long len = b1 & 0x7f;
        if (len == 126)
        {
            byte[] x = new byte[2];
            if (!ReadExact(s, x, 2)) return null;
            len = ((long)x[0] << 8) | x[1];
        }
        else if (len == 127)
        {
            byte[] x = new byte[8];
            if (!ReadExact(s, x, 8)) return null;
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | x[i];
        }
        if (len < 0 || len > 65536) return null;
        byte[] mask = new byte[4];
        if (masked && !ReadExact(s, mask, 4)) return null;
        byte[] payload = new byte[len];
        if (len > 0 && !ReadExact(s, payload, (int)len)) return null;
        if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
        return Encoding.UTF8.GetString(payload);
    }

    private static bool ReadExact(NetworkStream s, byte[] buf, int n)
    {
        int off = 0;
        while (off < n)
        {
            int r;
            try { r = s.Read(buf, off, n - off); }
            catch { return false; }
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }

    private static void SendWsText(NetworkStream s, string text)
    {
        SendWsFrame(s, 0x81, Encoding.UTF8.GetBytes(text));
    }

    private static void SendWsFrame(NetworkStream s, int opcode, byte[] payload)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode));
        int len = payload == null ? 0 : payload.Length;
        if (len < 126) ms.WriteByte((byte)len);
        else if (len <= 65535)
        {
            ms.WriteByte(126);
            ms.WriteByte((byte)(len >> 8));
            ms.WriteByte((byte)(len & 0xff));
        }
        else
        {
            ms.WriteByte(127);
            for (int i = 7; i >= 0; i--) ms.WriteByte((byte)(((long)len) >> (8 * i)));
        }
        if (len > 0) ms.Write(payload, 0, len);
        byte[] b = ms.ToArray();
        s.Write(b, 0, b.Length);
        s.Flush();
    }
}
