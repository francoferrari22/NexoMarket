using System;
using System.Diagnostics;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NexoMarket.Admin.UI
{
    public sealed class AndroidScannerForm : Form
    {
        private readonly Action<string> _barcodeReceived;
        private TextBox _adbPath;
        private Label _usbStatus;
        private Label _btStatus;
        private ComboBox _ports;
        private Button _usbListen;
        private Button _btConnect;
        private SerialPort _serial;
        private Process _adbProcess;
        private Thread _adbThread;
        private volatile bool _adbRunning;
        private readonly StringBuilder _btBuffer = new StringBuilder();
        private System.Windows.Forms.Timer _portTimer;

        public AndroidScannerForm(Action<string> barcodeReceived)
        {
            _barcodeReceived = barcodeReceived;
            Text = "NexoMarket · Teléfono Android";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(720, 500);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            Label title = new Label { Text = "CONECTAR TELÉFONO ANDROID", AutoSize = true, Font = Theme.Font(16, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(24, 20) };
            Controls.Add(title);
            Label desc = new Label { Text = "Usá el teléfono como escáner de códigos. Hay dos modos: USB mediante ADB o Bluetooth mediante un puerto COM.", AutoSize = false, Width = 660, Height = 45, Font = Theme.Font(9, FontStyle.Regular), ForeColor = Theme.Muted, Location = new Point(25, 53) };
            Controls.Add(desc);

            TabControl tabs = new TabControl { Location = new Point(20, 105), Size = new Size(680, 320) };
            tabs.TabPages.Add(BuildUsbTab());
            tabs.TabPages.Add(BuildBluetoothTab());
            Controls.Add(tabs);

            Label footer = new Label { Text = "Los códigos recibidos se envían automáticamente al ticket activo. Formato aceptado: BARCODE:1234567890123", AutoSize = false, Width = 660, Height = 35, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular), Location = new Point(25, 440) };
            Controls.Add(footer);
            FormClosed += delegate { StopUsb(); DisconnectBluetooth(); if (_portTimer != null) { _portTimer.Stop(); _portTimer.Dispose(); _portTimer = null; } };
            Shown += delegate
            {
                _portTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _portTimer.Tick += delegate { LoadPorts(); };
                _portTimer.Start();
                DetectUsb();
                try
                {
                    string result = RunAdb("devices");
                    if (result.IndexOf("\tdevice", StringComparison.OrdinalIgnoreCase) >= 0) StartUsb();
                }
                catch { }
            };
        }

        private TabPage BuildUsbTab()
        {
            TabPage p = Tab("USB / ADB");
            Label info = new Label { Text = "1. Activá Depuración USB en Android.\r\n2. Aceptá la autorización RSA en el teléfono.\r\n3. El puente Android debe emitir BARCODE:<código> en el log NexoMarketScan.", AutoSize = false, Width = 620, Height = 75, Location = new Point(22, 20), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) };
            p.Controls.Add(info);
            Label lp = new Label { Text = "ADB:", AutoSize = true, Location = new Point(22, 108), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) };
            p.Controls.Add(lp);
            _adbPath = new TextBox { Text = "adb.exe", Width = 420, Location = new Point(70, 104), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            p.Controls.Add(_adbPath);
            Button detect = Theme.Secondary("DETECTAR TELÉFONO"); detect.Location = new Point(500, 101); detect.Width = 145; detect.Click += delegate { DetectUsb(); }; p.Controls.Add(detect);
            _usbStatus = new Label { Text = "Estado USB: no comprobado", AutoSize = false, Width = 620, Height = 40, Location = new Point(22, 150), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) };
            p.Controls.Add(_usbStatus);
            _usbListen = Theme.Primary("ESCUCHAR CÓDIGOS USB"); _usbListen.Location = new Point(22, 205); _usbListen.Width = 220; _usbListen.Click += delegate { if (_adbRunning) StopUsb(); else StartUsb(); }; p.Controls.Add(_usbListen);
            Button close = Theme.Secondary("CERRAR"); close.Location = new Point(500, 205); close.Width = 145; close.Click += delegate { Close(); }; p.Controls.Add(close);
            return p;
        }

        private TabPage BuildBluetoothTab()
        {
            TabPage p = Tab("Bluetooth / COM");
            Label info = new Label { Text = "Emparejá el Android con Windows. Si el teléfono expone Bluetooth SPP/RFCOMM, Windows creará un puerto COM. El teléfono debe enviar BARCODE:<código> y Enter.", AutoSize = false, Width = 620, Height = 70, Location = new Point(22, 20), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) };
            p.Controls.Add(info);
            Label lp = new Label { Text = "Puerto COM:", AutoSize = true, Location = new Point(22, 112), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) };
            p.Controls.Add(lp);
            _ports = new ComboBox { Width = 180, Location = new Point(105, 108), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Card2, ForeColor = Theme.Text };
            p.Controls.Add(_ports);
            Button refresh = Theme.Secondary("ACTUALIZAR"); refresh.Location = new Point(300, 105); refresh.Width = 130; refresh.Click += delegate { LoadPorts(); }; p.Controls.Add(refresh);
            _btConnect = Theme.Primary("CONECTAR BLUETOOTH"); _btConnect.Location = new Point(22, 170); _btConnect.Width = 205; _btConnect.Click += delegate { if (_serial != null && _serial.IsOpen) DisconnectBluetooth(); else ConnectBluetooth(); }; p.Controls.Add(_btConnect);
            _btStatus = new Label { Text = "Estado Bluetooth: desconectado", AutoSize = false, Width = 620, Height = 45, Location = new Point(22, 220), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) };
            p.Controls.Add(_btStatus);
            Button windowsBt = Theme.Secondary("ABRIR BLUETOOTH DE WINDOWS"); windowsBt.Location = new Point(250, 170); windowsBt.Width = 220; windowsBt.Click += delegate { try { Process.Start("ms-settings:bluetooth"); } catch { try { Process.Start("control.exe", "bthprops.cpl"); } catch { } } }; p.Controls.Add(windowsBt);
            Label portsInfo = new Label { Text = "Si el teléfono está emparejado y usa Bluetooth SPP/RFCOMM, Windows debe crear COM entrante/saliente. Si no aparece aquí, el problema está en el emparejamiento o el controlador Bluetooth, no en NexoMarket.", AutoSize = false, Width = 620, Height = 42, Location = new Point(22, 265), ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) }; p.Controls.Add(portsInfo);
            LoadPorts();
            return p;
        }

        private TabPage Tab(string text)
        {
            TabPage p = new TabPage(text);
            p.BackColor = Theme.Background;
            p.ForeColor = Theme.Text;
            return p;
        }

        private void LoadPorts()
        {
            if (_ports == null) return;
            string selected = _ports.SelectedItem == null ? "" : _ports.SelectedItem.ToString();
            _ports.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports);
            foreach (string port in ports) _ports.Items.Add(port);
            if (_ports.Items.Count == 0) _ports.Items.Add("(No hay puertos COM disponibles)");
            if (selected.Length > 0 && _ports.Items.Contains(selected)) _ports.SelectedItem = selected;
            else if (_ports.Items.Count > 0) _ports.SelectedIndex = 0;
        }

        private string AdbExecutable()
        {
            string configured = (_adbPath == null ? "adb.exe" : _adbPath.Text.Trim());
            if (configured.Length == 0) configured = "adb.exe";
            string[] candidates = new string[]
            {
                configured,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Android", "adb.exe"),
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Android", "Sdk", "platform-tools", "adb.exe")
            };
            foreach (string candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate) && System.IO.File.Exists(candidate)) return candidate;
            return configured;
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
                if (p.ExitCode != 0 && error.Length > 0) return output + "\r\n" + error;
                return output;
            }
        }

        private void DetectUsb()
        {
            try
            {
                string result = RunAdb("devices");
                if (result.IndexOf("\tdevice", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _usbStatus.Text = "Estado USB: ANDROID CONECTADO. Podés iniciar la escucha.";
                    _usbStatus.ForeColor = Theme.Green;
                }
                else
                {
                    _usbStatus.Text = "Estado USB: no se detectó un Android autorizado.\r\n" + result.Trim();
                    _usbStatus.ForeColor = Theme.Warning;
                }
            }
            catch (Exception ex)
            {
                _usbStatus.Text = "Estado USB: no se pudo ejecutar ADB. " + ex.Message;
                _usbStatus.ForeColor = Theme.Danger;
            }
        }

        private void StartUsb()
        {
            try
            {
                string result = RunAdb("devices");
                if (result.IndexOf("\tdevice", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    DetectUsb();
                    MessageBox.Show("No hay un teléfono Android autorizado por ADB.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _adbRunning = true;
                _usbListen.Text = "DETENER ESCUCHA USB";
                _usbStatus.Text = "Estado USB: escuchando códigos del teléfono...";
                _usbStatus.ForeColor = Theme.Green;
                _adbThread = new Thread(UsbWorker);
                _adbThread.IsBackground = true;
                _adbThread.Start();
            }
            catch (Exception ex)
            {
                _adbRunning = false;
                _usbListen.Text = "ESCUCHAR CÓDIGOS USB";
                MessageBox.Show("No se pudo iniciar la escucha USB.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UsbWorker()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(AdbExecutable(), "logcat -v brief NexoMarketScan:D *:S");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                _adbProcess = Process.Start(psi);
                while (_adbRunning && _adbProcess != null && !_adbProcess.HasExited)
                {
                    string line = _adbProcess.StandardOutput.ReadLine();
                    if (line == null) break;
                    string code = ExtractBarcode(line);
                    if (code.Length > 0) RaiseBarcode(code);
                }
            }
            catch { }
            finally
            {
                try { if (_adbProcess != null && !_adbProcess.HasExited) _adbProcess.Kill(); } catch { }
                _adbProcess = null;
            }
        }

        private void StopUsb()
        {
            _adbRunning = false;
            try { if (_adbProcess != null && !_adbProcess.HasExited) _adbProcess.Kill(); } catch { }
            _adbProcess = null;
            if (_usbListen != null) _usbListen.Text = "ESCUCHAR CÓDIGOS USB";
            if (_usbStatus != null) { _usbStatus.Text = "Estado USB: escucha detenida"; _usbStatus.ForeColor = Theme.Muted; }
        }

        private void ConnectBluetooth()
        {
            try
            {
                if (_ports == null || _ports.SelectedItem == null || Convert.ToString(_ports.SelectedItem).StartsWith("(")) { MessageBox.Show("No hay un puerto COM Bluetooth disponible. Emparejá primero el teléfono en Windows y volvé a actualizar.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                _serial = new SerialPort(_ports.SelectedItem.ToString(), 9600, Parity.None, 8, StopBits.One);
                _serial.NewLine = "\r\n";
                _serial.DataReceived += BluetoothData;
                _serial.Open();
                _btConnect.Text = "DESCONECTAR";
                _btStatus.Text = "Estado Bluetooth: conectado a " + _serial.PortName + ". Esperando códigos...";
                _btStatus.ForeColor = Theme.Green;
            }
            catch (Exception ex)
            {
                DisconnectBluetooth();
                MessageBox.Show("No se pudo abrir el puerto Bluetooth.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BluetoothData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string incoming = _serial.ReadExisting();
                lock (_btBuffer)
                {
                    _btBuffer.Append(incoming);
                    while (true)
                    {
                        string text = _btBuffer.ToString();
                        int pos = text.IndexOf('\n');
                        if (pos < 0) break;
                        string line = text.Substring(0, pos).Trim('\r', ' ', '\t');
                        _btBuffer.Remove(0, pos + 1);
                        string code = ExtractBarcode(line);
                        if (code.Length > 0) RaiseBarcode(code);
                    }
                }
            }
            catch { }
        }

        private void DisconnectBluetooth()
        {
            try
            {
                if (_serial != null)
                {
                    _serial.DataReceived -= BluetoothData;
                    if (_serial.IsOpen) _serial.Close();
                    _serial.Dispose();
                }
            }
            catch { }
            _serial = null;
            if (_btConnect != null) _btConnect.Text = "CONECTAR BLUETOOTH";
            if (_btStatus != null) { _btStatus.Text = "Estado Bluetooth: desconectado"; _btStatus.ForeColor = Theme.Muted; }
        }

        private string ExtractBarcode(string text)
        {
            if (text == null) return "";
            int idx = text.IndexOf("BARCODE:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string value = text.Substring(idx + 8).Trim();
                int space = value.IndexOf(' ');
                if (space > 0) value = value.Substring(0, space);
                return OnlyBarcode(value);
            }
            return OnlyBarcode(text.Trim());
        }

        private string OnlyBarcode(string value)
        {
            if (value == null) return "";
            value = value.Trim();
            if (value.Length < 4 || value.Length > 40) return "";
            for (int i = 0; i < value.Length; i++) if (!char.IsLetterOrDigit(value[i]) && value[i] != '-' && value[i] != '_') return "";
            return value;
        }

        private void RaiseBarcode(string code)
        {
            if (_barcodeReceived == null) return;
            try { BeginInvoke((MethodInvoker)delegate { _barcodeReceived(code); }); } catch { }
        }
    }
}
