using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Puente USB Android. Detecta el teléfono por ADB, abre automáticamente
    /// el NexoMarket Scanner y recibe BARCODE:<código> desde logcat.
    /// </summary>
    public sealed class AndroidBridgeService : IDisposable
    {
        private readonly Action<string> _barcodeReceived;
        private readonly Action<string> _statusChanged;
        private readonly System.Windows.Forms.Timer _timer;
        private Thread _worker;
        private Process _logcat;
        private volatile bool _running;
        private bool _deviceConnected;
        private readonly object _sync = new object();

        public AndroidBridgeService(Action<string> barcodeReceived, Action<string> statusChanged)
        {
            _barcodeReceived = barcodeReceived;
            _statusChanged = statusChanged;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 2000;
            _timer.Tick += delegate { Poll(); };
        }

        public void Start()
        {
            if (_timer.Enabled) return;
            _timer.Start();
            Poll();
        }

        public void Stop()
        {
            _timer.Stop();
            StopReader();
            _deviceConnected = false;
            Status("Android USB: desconectado");
        }

        private void Poll()
        {
            try
            {
                string output = RunAdb("devices");
                bool connected = output.IndexOf("\tdevice", StringComparison.OrdinalIgnoreCase) >= 0;
                if (connected && !_deviceConnected)
                {
                    _deviceConnected = true;
                    Status("Android USB: conectado. Preparando escáner del teléfono...");
                    StartReader();
                }
                else if (!connected && _deviceConnected)
                {
                    _deviceConnected = false;
                    StopReader();
                    Status("Android USB: teléfono desconectado");
                }
                else if (!connected)
                {
                    Status("Android USB: esperando teléfono...");
                }
            }
            catch (Exception ex)
            {
                Status("Android USB: ADB no disponible (" + ex.Message + ")");
            }
        }

        private void StartReader()
        {
            lock (_sync)
            {
                if (_running) return;
                _running = true;
            }
            try
            {
                string package = RunAdb("shell pm path com.nexomarket.scanner");
                if (package.IndexOf("package:", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string apk = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Android", "NexoMarketScanner.apk");
                    if (File.Exists(apk))
                    {
                        Status("Android USB: instalando NexoMarket Scanner...");
                        RunAdb("install -r " + Quote(apk));
                    }
                }
                RunAdb("logcat -c");
            }
            catch { }
            _worker = new Thread(ReadLogcat);
            _worker.IsBackground = true;
            _worker.Start();
            Thread starter = new Thread(new ThreadStart(delegate
            {
                try
                {
                    Thread.Sleep(250);
                    if (_running) RunAdb("shell am start -n com.nexomarket.scanner/.MainActivity");
                }
                catch { }
            }));
            starter.IsBackground = true;
            starter.Start();
        }

        private string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "") + "\"";
        }

        private void ReadLogcat()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(AdbExecutable(), "logcat -v brief -s NexoMarketScan:D *:S");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                _logcat = Process.Start(psi);
                while (_running && _logcat != null && !_logcat.HasExited)
                {
                    string line = _logcat.StandardOutput.ReadLine();
                    if (line == null) break;
                    string code = ExtractBarcode(line);
                    if (code.Length > 0 && _barcodeReceived != null)
                    {
                        try { _barcodeReceived(code); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (_logcat != null && !_logcat.HasExited) _logcat.Kill(); } catch { }
                _logcat = null;
            }
        }

        private void StopReader()
        {
            _running = false;
            try { if (_logcat != null && !_logcat.HasExited) _logcat.Kill(); } catch { }
            _logcat = null;
        }

        private string ExtractBarcode(string text)
        {
            if (text == null) return "";
            int idx = text.IndexOf("BARCODE:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            string value = text.Substring(idx + 8).Trim();
            int space = value.IndexOf(' ');
            if (space > 0) value = value.Substring(0, space);
            if (value.Length < 4 || value.Length > 40) return "";
            for (int i = 0; i < value.Length; i++)
                if (!char.IsLetterOrDigit(value[i]) && value[i] != '-' && value[i] != '_') return "";
            return value;
        }

        private string RunAdb(string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo(AdbExecutable(), arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                return output + (error.Length == 0 ? "" : "\r\n" + error);
            }
        }

        private string AdbExecutable()
        {
            string[] candidates = new string[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Android", "adb.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Android", "Sdk", "platform-tools", "adb.exe")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            return "adb.exe";
        }

        private void Status(string text)
        {
            if (_statusChanged == null) return;
            try { _statusChanged(text); } catch { }
        }

        public void Dispose()
        {
            Stop();
            _timer.Dispose();
        }
    }
}
