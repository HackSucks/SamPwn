using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal sealed class ShellServer
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Any, Program.ShellPort);
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
                var psi = new ProcessStartInfo("/usr/bin/script");
                psi.Arguments = "-qfc /bin/bash /dev/null";
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.Environment["TERM"] = "xterm";
                psi.Environment["PS1"] = "\\u@\\h:\\w# ";
                p = Process.Start(psi);
                lock (_sync) _procs.Add(p);
                Task a = Pump(stream, p.StandardInput.BaseStream);
                Task b = Pump(p.StandardOutput.BaseStream, stream);
                Task c = Pump(p.StandardError.BaseStream, stream);
                Task.WhenAny(a, b, c).Wait();
            }
        }
        catch (Exception e) { Program.Log("shell client: " + e.Message); }
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

    private static async Task Pump(Stream from, Stream to)
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int n = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n <= 0) break;
                await to.WriteAsync(buffer, 0, n).ConfigureAwait(false);
                await to.FlushAsync().ConfigureAwait(false);
            }
        }
        catch { }
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
