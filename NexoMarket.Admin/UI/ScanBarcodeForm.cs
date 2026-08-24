using System;
using System.Drawing;
using System.Windows.Forms;

namespace NexoMarket.Admin.UI
{
    public sealed class ScanBarcodeForm : Form
    {
        private readonly TextBox _barcode;
        public string Barcode { get { return _barcode.Text.Trim(); } }

        public ScanBarcodeForm()
        {
            Text = "NexoMarket · Escáner de código";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 250);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            Label title = new Label { Text = "ESCANEAR CÓDIGO DE BARRAS", AutoSize = true, Font = Theme.Font(14, FontStyle.Bold), ForeColor = Theme.Text, Location = new Point(24, 22) };
            Controls.Add(title);
            Label info = new Label { Text = "Conectá un lector USB de códigos. El lector funciona como teclado: apuntá el código y el número aparecerá automáticamente en el campo.", AutoSize = false, Width = 465, Height = 58, Font = Theme.Font(9, FontStyle.Regular), ForeColor = Theme.Muted, Location = new Point(24, 58) };
            Controls.Add(info);
            _barcode = new TextBox { Width = 465, Height = 34, Font = Theme.Font(16, FontStyle.Bold), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Location = new Point(24, 125) };
            Controls.Add(_barcode);
            Button accept = Theme.Primary("ACEPTAR"); accept.Width = 145; accept.Location = new Point(24, 178); accept.Click += delegate { if (Barcode.Length == 0) return; DialogResult = DialogResult.OK; Close(); }; Controls.Add(accept);
            Button cancel = Theme.Secondary("CANCELAR"); cancel.Width = 130; cancel.Location = new Point(359, 178); cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); }; Controls.Add(cancel);
            AcceptButton = accept;
            Shown += delegate { _barcode.Focus(); };
            _barcode.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (Barcode.Length > 0) { DialogResult = DialogResult.OK; Close(); } } };
        }
    }
}
