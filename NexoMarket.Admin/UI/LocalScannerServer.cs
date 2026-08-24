using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Canal de escaneo por red local. No requiere ADB ni Bluetooth SPP.
    /// El teléfono envía GET /scan?token=...&code=... y el escritorio recibe el código.
    /// </summary>
    public sealed class LocalScannerServer : IDisposable
    {
        private readonly Action<string> _barcodeReceived;
        private readonly int _port;
        private TcpListener _listener;
        private Thread _worker;
        private volatile bool _running;
        private readonly string _token;

        public string Token { get { return _token; } }
        public int Port { get { return _port; } }
        public string LocalUrl { get { return "http://" + GetLocalIPv4() + ":" + _port + "/scan"; } }

        public LocalScannerServer(Action<string> barcodeReceived, int port = 8787)
        {
            _barcodeReceived = barcodeReceived;
            _port = port;
            _token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
        }

        public void Start()
        {
            if (_running) return;
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _running = true;
                _worker = new Thread(Worker) { IsBackground = true };
                _worker.Start();
            }
            catch { _running = false; }
        }

        private void Worker()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000;
                    client.SendTimeout = 3000;
                    using (NetworkStream stream = client.GetStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
                    {
                        string requestLine = reader.ReadLine() ?? "";
                        string line;
                        while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }
                        string code = ExtractQuery(requestLine, "code");
                        string token = ExtractQuery(requestLine, "token");
                        string path = requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "/";
                        if (path.StartsWith("/scan", StringComparison.OrdinalIgnoreCase) && string.Equals(token, _token, StringComparison.OrdinalIgnoreCase) && IsBarcode(code))
                        {
                            if (_barcodeReceived != null) _barcodeReceived(code);
                            WriteResponse(stream, 200, "OK");
                        }
                        else
                        {
                            WriteResponse(stream, 400, "INVALID");
                        }
                    }
                }
                catch { }
            }
        }

        private void WriteResponse(NetworkStream stream, int status, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string header = "HTTP/1.1 " + status + " OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(header);
            stream.Write(h, 0, h.Length); stream.Write(bytes, 0, bytes.Length);
        }

        private string ExtractQuery(string request, string key)
        {
            int q = request.IndexOf('?'); if (q < 0) return "";
            int end = request.IndexOf(' ', q); if (end < 0) end = request.Length;
            string query = request.Substring(q + 1, end - q - 1);
            foreach (string part in query.Split('&'))
            {
                string[] pair = part.Split(new[] { '=' }, 2);
                if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(pair[1]);
            }
            return "";
        }

        private bool IsBarcode(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length < 4 || code.Length > 40) return false;
            foreach (char c in code) if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            return true;
        }

        public string GetLocalIPv4()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address)) return ip.Address.ToString();
                }
                foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }
    }
}
