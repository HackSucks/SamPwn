using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// A small telnet server for interactive use.  Port 2323 stays a raw byte
/// stream for scripts; this listener adds the telnet option handshake, a
/// terminal type and window size, and runs bash on a real PTY so Ctrl-C,
/// Ctrl-Z, job control, colours and other terminal sequences all work.
/// </summary>
internal sealed class TelnetServer
{
    private const byte IAC = 255;
    private const byte DONT = 254;
    private const byte DO = 253;
    private const byte WONT = 252;
    private const byte WILL = 251;
    private const byte SB = 250;
    private const byte SE = 240;

    private const byte OPT_ECHO = 1;
    private const byte OPT_SGA = 3;
    private const byte OPT_TTYPE = 24;
    private const byte OPT_NAWS = 31;

    private const int NEGOTIATION_MS = 450;

    private readonly TcpListener _listener = new TcpListener(IPAddress.Any, Program.TelnetPort);
    private readonly object _sync = new object();
    private readonly List<TcpClient> _clients = new List<TcpClient>();
    private readonly List<Process> _procs = new List<Process>();
    private volatile bool _listening;

    public bool Listening { get { return _listening; } }
    public int ClientCount { get { lock (_sync) return _clients.Count; } }

    public void Start()
    {
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _listening = true;
        var t = new Thread(AcceptLoop);
        t.IsBackground = true;
        t.Start();
    }

    private void AcceptLoop()
    {
        while (_listening)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); }
            catch { break; }
            lock (_sync) _clients.Add(c);
            var t = new Thread(delegate() { Serve(c); });
            t.IsBackground = true;
            t.Start();
        }
    }

    private void Serve(TcpClient client)
    {
        Process p = null;
        try
        {
            client.NoDelay = true;
            using (client)
            {
                NetworkStream stream = client.GetStream();
                var session = new Session();
                Negotiate(client, stream, session);
                p = StartShell(session);
                lock (_sync) _procs.Add(p);

                if (session.Pending != null && session.Pending.Length > 0)
                {
                    byte[] early = session.Pending.ToArray();
                    try
                    {
                        p.StandardInput.BaseStream.Write(early, 0, early.Length);
                        p.StandardInput.BaseStream.Flush();
                    }
                    catch { }
                }

                var done = new ManualResetEvent(false);
                var toPty = new Thread(delegate() { PumpToPty(stream, p, session, done); });
                var fromOut = new Thread(delegate() { PumpFromPty(p.StandardOutput.BaseStream, stream, session, done); });
                var fromErr = new Thread(delegate() { PumpFromPty(p.StandardError.BaseStream, stream, session, done); });
                toPty.IsBackground = fromOut.IsBackground = fromErr.IsBackground = true;
                toPty.Start();
                fromOut.Start();
                fromErr.Start();
                done.WaitOne();
            }
        }
        catch (Exception e) { Program.Log("telnet client: " + e.Message); }
        finally
        {
            if (p != null)
            {
                try { lock (_sync) _procs.Remove(p); } catch { }
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            try { lock (_sync) _clients.Remove(client); } catch { }
        }
    }

    private sealed class Session
    {
        public int Cols = 80;
        public int Rows = 24;
        public string Term = "xterm-256color";
        public MemoryStream Pending;
        public readonly object WriteGate = new object();
    }

    private static Process StartShell(Session session)
    {
        string bash = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        string script = null;
        if (File.Exists("/usr/bin/script")) script = "/usr/bin/script";
        else if (File.Exists("/bin/script")) script = "/bin/script";

        ProcessStartInfo psi;
        if (script != null)
        {
            // util-linux script(1) allocates a PTY, makes the child a session
            // leader and forwards our stdin/stdout to it.  That gives bash a
            // controlling terminal, which is what job control needs.
            psi = new ProcessStartInfo(script);
            psi.ArgumentList.Add("-qfc");
            psi.ArgumentList.Add(
                "stty rows " + session.Rows + " cols " + session.Cols +
                " 2>/dev/null; exec " + bash);
            psi.ArgumentList.Add("/dev/null");
        }
        else
        {
            // Older firmware may not ship script(1).  The shell still works,
            // but without a PTY there is no job control or window size.
            psi = new ProcessStartInfo(bash);
            psi.ArgumentList.Add("-i");
        }

        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.Environment["TERM"] = session.Term;
        psi.Environment["COLORTERM"] = "truecolor";
        psi.Environment["PS1"] = "\\u@\\h:\\w# ";
        psi.Environment["PROMPT_COMMAND"] = "";
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        return Process.Start(psi);
    }

    private static void Send(NetworkStream stream, object gate, params byte[] bytes)
    {
        try
        {
            lock (gate)
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch { }
    }

    private static void SendIac(NetworkStream stream, object gate, byte command, byte option)
    {
        Send(stream, gate, IAC, command, option);
    }

    private static void SendSub(NetworkStream stream, object gate, byte option, byte[] data)
    {
        var buf = new byte[data.Length + 5];
        buf[0] = IAC; buf[1] = SB; buf[2] = option;
        Buffer.BlockCopy(data, 0, buf, 3, data.Length);
        buf[buf.Length - 2] = IAC;
        buf[buf.Length - 1] = SE;
        Send(stream, gate, buf);
    }

    /// <summary>Read the first telnet negotiation so the PTY starts at the right size.</summary>
    private static void Negotiate(TcpClient client, NetworkStream stream, Session session)
    {
        SendIac(stream, session.WriteGate, WILL, OPT_ECHO);
        SendIac(stream, session.WriteGate, WILL, OPT_SGA);
        SendIac(stream, session.WriteGate, DO, OPT_TTYPE);
        SendIac(stream, session.WriteGate, DO, OPT_NAWS);

        var parser = new InputParser(stream, null, session);
        var one = new byte[1];
        try
        {
            client.ReceiveTimeout = NEGOTIATION_MS;
            while (true)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) break;
                parser.Feed(one, 1);
            }
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { client.ReceiveTimeout = 0; } catch { }
        }
        session.Pending = parser.Pending;
    }

    private sealed class InputParser
    {
        private readonly NetworkStream _telnet;
        private readonly Stream _pty;
        private readonly Session _session;
        private int _state;          // 0 data, 1 after IAC, 2 option, 3 sub data, 4 IAC in sub, 5 sub option
        private byte _command;
        private byte _subOption;
        private readonly MemoryStream _sub = new MemoryStream();
        private bool _lastCr;
        public readonly MemoryStream Pending = new MemoryStream();

        public InputParser(NetworkStream telnet, Stream pty, Session session)
        {
            _telnet = telnet;
            _pty = pty;
            _session = session;
        }

        public void Feed(byte[] data, int count)
        {
            for (int i = 0; i < count; i++) Feed(data[i]);
        }

        private void Feed(byte b)
        {
            switch (_state)
            {
                case 1: // after IAC
                    if (b == IAC)
                    {
                        _state = 0;
                        Emit(0xFF);
                    }
                    else if (b == WILL || b == WONT || b == DO || b == DONT)
                    {
                        _command = b;
                        _state = 2;
                    }
                    else if (b == SB)
                    {
                        _sub.SetLength(0);
                        _state = 5;
                    }
                    else
                    {
                        _state = 0;
                        HandleControl(b);
                    }
                    break;

                case 2: // option byte
                    _state = 0;
                    HandleOption(_command, b);
                    break;

                case 5: // subnegotiation option
                    _subOption = b;
                    _state = 3;
                    break;

                case 3: // subnegotiation data
                    if (b == IAC) _state = 4;
                    else if (_sub.Length < 256) _sub.WriteByte(b);
                    break;

                case 4: // IAC inside subnegotiation
                    if (b == SE)
                    {
                        HandleSub(_subOption, _sub.ToArray());
                        _state = 0;
                    }
                    else if (b == IAC)
                    {
                        if (_sub.Length < 256) _sub.WriteByte(IAC);
                        _state = 3;
                    }
                    else
                    {
                        _state = 3;
                    }
                    break;

                default: // data
                    if (b == IAC)
                    {
                        _state = 1;
                        _subOption = 0;
                    }
                    else
                    {
                        EmitData(b);
                    }
                    break;
            }
        }

        private void HandleOption(byte command, byte option)
        {
            if (command == DO)
            {
                // We advertised ECHO/SGA; TTYPE/NAWS are accepted from the client.
                if (option == OPT_ECHO || option == OPT_SGA ||
                    option == OPT_TTYPE || option == OPT_NAWS) return;
                SendIac(_telnet, _session.WriteGate, WONT, option);
            }
            else if (command == WILL)
            {
                if (option == OPT_TTYPE)
                {
                    SendSub(_telnet, _session.WriteGate, OPT_TTYPE, new byte[] { 1 }); // SEND
                }
                else if (option == OPT_NAWS || option == OPT_SGA)
                {
                    // Accepted; NAWS size arrives as a subnegotiation.
                }
                else if (option == OPT_ECHO)
                {
                    // The server echoes; do not let the client echo as well.
                    SendIac(_telnet, _session.WriteGate, DONT, OPT_ECHO);
                }
                else
                {
                    SendIac(_telnet, _session.WriteGate, DONT, option);
                }
            }
            // WONT / DONT: nothing to do.
        }

        private void HandleSub(byte option, byte[] data)
        {
            if (option == OPT_TTYPE && data.Length >= 1)
            {
                if (data[0] == 1) // SEND -> answer IS
                {
                    var name = Encoding.ASCII.GetBytes(_session.Term);
                    var reply = new byte[name.Length + 1];
                    reply[0] = 0; // IS
                    Buffer.BlockCopy(name, 0, reply, 1, name.Length);
                    SendSub(_telnet, _session.WriteGate, OPT_TTYPE, reply);
                }
                else if (data[0] == 0 && data.Length > 1) // IS
                {
                    string term = Encoding.ASCII.GetString(data, 1, data.Length - 1).Trim();
                    if (term.Length > 0 && term.Length < 64)
                    {
                        foreach (char ch in term)
                        {
                            if (ch < 0x21 || ch > 0x7e) return;
                        }
                        _session.Term = term;
                    }
                }
            }
            else if (option == OPT_NAWS && data.Length >= 4)
            {
                int cols = (data[0] << 8) | data[1];
                int rows = (data[2] << 8) | data[3];
                if (cols >= 20 && cols <= 500) _session.Cols = cols;
                if (rows >= 10 && rows <= 200) _session.Rows = rows;
            }
        }

        private void HandleControl(byte command)
        {
            if (command == 243) Emit(0x03);          // BRK -> Ctrl-C
            else if (command == 247) Emit(0x7F);     // EC -> backspace
            else if (command == 248) Emit(0x15);     // EL -> Ctrl-U
            else if (command == 246)                 // AYT
            {
                var msg = Encoding.ASCII.GetBytes("\r\n[SamPwn telnet]\r\n");
                Send(_telnet, _session.WriteGate, msg);
            }
            // NOP (241), IP (244), AO (245) and others are ignored.
        }

        private void EmitData(byte b)
        {
            // Telnet sends Enter as CR LF or CR NUL.  A PTY with ICRNL already
            // turns CR into NL, so drop the second byte to avoid a blank line.
            if (_lastCr)
            {
                _lastCr = false;
                if (b == 0x0A || b == 0x00) return;
            }
            if (b == 0x0D) _lastCr = true;
            Emit(b);
        }

        private void Emit(byte b)
        {
            if (_pty != null)
            {
                try { _pty.WriteByte(b); _pty.Flush(); }
                catch { }
            }
            else
            {
                Pending.WriteByte(b);
            }
        }
    }

    private static void PumpToPty(NetworkStream telnet, Process p, Session session, ManualResetEvent done)
    {
        var parser = new InputParser(telnet, p.StandardInput.BaseStream, session);
        var buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = telnet.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                parser.Feed(buf, n);
            }
        }
        catch { }
        finally { done.Set(); }
    }

    private static void PumpFromPty(Stream pty, NetworkStream telnet, Session session, ManualResetEvent done)
    {
        var buf = new byte[8192];
        var escaped = new byte[16384];
        try
        {
            while (true)
            {
                int n = pty.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                int o = 0;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    escaped[o++] = b;
                    if (b == IAC) escaped[o++] = IAC;
                }
                lock (session.WriteGate)
                {
                    telnet.Write(escaped, 0, o);
                    telnet.Flush();
                }
            }
        }
        catch { }
        finally { done.Set(); }
    }

    public void Stop()
    {
        _listening = false;
        try { _listener.Stop(); } catch { }
        List<TcpClient> clients;
        List<Process> procs;
        lock (_sync)
        {
            clients = new List<TcpClient>(_clients);
            procs = new List<Process>(_procs);
            _clients.Clear();
            _procs.Clear();
        }
        foreach (var c in clients) { try { c.Close(); } catch { } }
        foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch { } }
    }
}
