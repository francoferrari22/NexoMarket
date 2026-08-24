using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Campo preparado para lectores USB HID: los lectores normalmente se presentan
    /// ante Windows como un teclado. Detecta ráfagas rápidas de teclas y también
    /// acepta Enter/Tab como sufijo del lector.
    /// </summary>
    public sealed class ScannerTextBox : TextBox
    {
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Timer _commitTimer;
        private long _lastKeyMs = -1;
        private bool _scannerMode;
        private bool _internalClear;

        public event EventHandler BarcodeScanned;
        public int MinimumBarcodeLength { get; set; }

        public ScannerTextBox()
        {
            MinimumBarcodeLength = 4;
            _commitTimer = new Timer { Interval = 180 };
            _commitTimer.Tick += delegate { _commitTimer.Stop(); Commit(); };
            _clock.Start();
            KeyDown += ScannerKeyDown;
            KeyPress += ScannerKeyPress;
        }

        private void ScannerKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
            {
                e.SuppressKeyPress = true;
                Commit();
            }
        }

        private void ScannerKeyPress(object sender, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar)) return;
            long now = _clock.ElapsedMilliseconds;
            if (_lastKeyMs >= 0 && now - _lastKeyMs <= 75) _scannerMode = true;
            else if (_lastKeyMs >= 0 && now - _lastKeyMs > 180) _scannerMode = false;
            _lastKeyMs = now;
            if (_scannerMode || Text.Length == 0)
            {
                _commitTimer.Stop();
                _commitTimer.Start();
            }
        }

        private void Commit()
        {
            if (_internalClear) return;
            string code = Text.Trim();
            if (code.Length < MinimumBarcodeLength) return;
            EventHandler h = BarcodeScanned;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void ClearAfterScan()
        {
            _internalClear = true;
            try { Clear(); } finally { _internalClear = false; }
            _scannerMode = false;
            _lastKeyMs = -1;
            _commitTimer.Stop();
            Focus();
            SelectAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _commitTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
