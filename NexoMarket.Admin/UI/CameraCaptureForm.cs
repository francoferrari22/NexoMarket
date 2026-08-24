using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace NexoMarket.Admin.UI
{
    public sealed class CameraCaptureForm : Form
    {
        private const int WM_USER = 0x0400;
        private const int WM_CAP_DRIVER_CONNECT = WM_USER + 10;
        private const int WM_CAP_DRIVER_DISCONNECT = WM_USER + 11;
        private const int WM_CAP_SET_PREVIEW = WM_USER + 50;
        private const int WM_CAP_SET_PREVIEWRATE = WM_USER + 52;
        private const int WM_CAP_GRAB_FRAME_NOSTOP = WM_USER + 61;
        private const int WM_CAP_FILE_SAVEDIB = WM_USER + 25;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;

        [DllImport("avicap32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr capCreateCaptureWindowA(string lpszWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, int nID);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
        private static extern bool capGetDriverDescriptionA(short wDriverIndex, [Out] System.Text.StringBuilder lpszName, int cbName, [Out] System.Text.StringBuilder lpszVer, int cbVer);

        private Panel _preview;
        private IntPtr _camera = IntPtr.Zero;
        private int _driver = -1;
        private string _output;
        private bool _connected;

        public string CapturedFile { get { return _output; } }

        public CameraCaptureForm(string outputDirectory)
        {
            Text = "NexoMarket · Cámara del equipo";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(760, 560);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            Label title = new Label { Text = "CAPTURA DESDE LA CÁMARA DEL EQUIPO", AutoSize = true, Font = Theme.Font(13, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(22, 18) };
            Controls.Add(title);
            Label hint = new Label { Text = "Usa la webcam conectada a esta computadora. No se abre la cámara del teléfono.", AutoSize = true, Font = Theme.Font(9, FontStyle.Regular), ForeColor = Theme.Muted, Location = new Point(23, 47) };
            Controls.Add(hint);

            _preview = new Panel { BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle, Location = new Point(22, 78), Size = new Size(716, 390) };
            Controls.Add(_preview);

            Button capture = Theme.Primary("CAPTURAR FOTO");
            capture.Width = 170;
            capture.Location = new Point(22, 488);
            capture.Click += delegate { SaveCapture(outputDirectory); };
            Controls.Add(capture);
            Button close = Theme.Secondary("CERRAR");
            close.Width = 130;
            close.Location = new Point(608, 488);
            close.Click += delegate { Close(); };
            Controls.Add(close);

            Load += delegate { StartCamera(); };
            FormClosed += delegate { StopCamera(); };
        }

        private void StartCamera()
        {
            // Algunas webcams de Windows 8 tardan unos cientos de milisegundos
            // en liberar el driver después de cerrar una captura anterior.
            // Esperamos y reintentamos la conexión para permitir capturas sucesivas.
            StopCamera();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    _driver = -1;
                    for (short i = 0; i < 10; i++)
                    {
                        StringBuilder name = new StringBuilder(100);
                        StringBuilder ver = new StringBuilder(100);
                        if (capGetDriverDescriptionA(i, name, name.Capacity, ver, ver.Capacity))
                        {
                            _driver = i;
                            break;
                        }
                    }
                    if (_driver < 0)
                    {
                        MessageBox.Show("No se encontró una cámara web conectada a esta computadora.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    if (attempt > 0) System.Threading.Thread.Sleep(300);
                    _camera = capCreateCaptureWindowA("NexoMarketCamera", WS_CHILD | WS_VISIBLE, 0, 0, _preview.ClientSize.Width, _preview.ClientSize.Height, _preview.Handle, 1);
                    if (_camera == IntPtr.Zero) throw new InvalidOperationException("Windows no pudo crear la ventana de captura.");
                    if (SendMessage(_camera, WM_CAP_DRIVER_CONNECT, new IntPtr(_driver), IntPtr.Zero) == IntPtr.Zero)
                        throw new InvalidOperationException("La cámara no pudo iniciar.");
                    _connected = true;
                    SendMessage(_camera, WM_CAP_SET_PREVIEWRATE, new IntPtr(30), IntPtr.Zero);
                    SendMessage(_camera, WM_CAP_SET_PREVIEW, new IntPtr(1), IntPtr.Zero);
                    return;
                }
                catch (Exception ex)
                {
                    StopCamera();
                    if (attempt == 2)
                    {
                        MessageBox.Show("No se pudo iniciar la cámara del equipo.\r\n\r\n" + ex.Message + "\r\n\r\nVerificá que otra aplicación no esté usando la webcam.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void SaveCapture(string directory)
        {
            if (!_connected || _camera == IntPtr.Zero)
            {
                MessageBox.Show("La cámara no está disponible.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string bmp = Path.Combine(directory, "camara_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".bmp");
            string jpg = Path.ChangeExtension(bmp, ".jpg");
            try
            {
                Directory.CreateDirectory(directory);
                SendMessage(_camera, WM_CAP_GRAB_FRAME_NOSTOP, IntPtr.Zero, IntPtr.Zero);
                IntPtr filePtr = Marshal.StringToHGlobalAnsi(bmp);
                try
                {
                    if (SendMessage(_camera, WM_CAP_FILE_SAVEDIB, IntPtr.Zero, filePtr) == IntPtr.Zero)
                        throw new InvalidOperationException("La cámara no devolvió una imagen.");
                }
                finally { Marshal.FreeHGlobal(filePtr); }
                using (Bitmap image = new Bitmap(bmp))
                    image.Save(jpg, ImageFormat.Jpeg);
                try { File.Delete(bmp); } catch { }
                _output = jpg;
                // Liberar el driver inmediatamente. Esto permite volver a abrir
                // la cámara para el siguiente producto sin recibir "dispositivo en uso".
                StopCamera();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                try { File.Delete(bmp); } catch { }
                try { File.Delete(jpg); } catch { }
                MessageBox.Show("No se pudo guardar la foto.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopCamera()
        {
            try
            {
                if (_camera != IntPtr.Zero)
                {
                    // Primero detenemos la vista previa y después desconectamos
                    // explícitamente el driver antes de destruir la ventana.
                    // Es importante en Windows 8 para que la siguiente captura
                    // pueda volver a tomar el mismo dispositivo.
                    try { SendMessage(_camera, WM_CAP_SET_PREVIEW, IntPtr.Zero, IntPtr.Zero); } catch { }
                    if (_connected)
                    {
                        try { SendMessage(_camera, WM_CAP_DRIVER_DISCONNECT, IntPtr.Zero, IntPtr.Zero); } catch { }
                    }
                    try { DestroyWindow(_camera); } catch { }
                }
            }
            catch { }
            _camera = IntPtr.Zero;
            _connected = false;
            _driver = -1;
        }
    }
}
