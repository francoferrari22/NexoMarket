using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NexoMarket.Admin.Data;

namespace NexoMarket.Admin.UI
{
    public sealed class LicenseGateForm : Form
    {
        private readonly AppDataStore _store;
        private readonly LicenseService _license;
        private Label _status;
        private Label _days;
        private Label _account;
        private Label _id;
        private Label _expires;
        private TextBox _token;
        private Button _activate;

        public LicenseGateForm(AppDataStore store)
        {
            _store = store;
            _license = new LicenseService(store.Root);

            Text = "NexoMarket · Licencia de cuenta";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(680, 560);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            Build();
            RefreshStatus();
        }

        private void Build()
        {
            Controls.Clear();

            Label title = new Label
            {
                Text = "LICENCIA DEL VENDEDOR",
                AutoSize = false,
                Height = 48,
                Dock = DockStyle.Top,
                Font = Theme.Font(18, FontStyle.Bold),
                ForeColor = Theme.NeonGreen,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(title);

            Panel card = Theme.Card();
            card.SetBounds(28, 58, 624, 476);
            Controls.Add(card);

            _account = MakeInfo("Cuenta: " + _license.AccountEmail(), 22, 18, 580, 30, 10);
            _id = MakeInfo("ID DE CUENTA: " + _license.AccountId(), 22, 52, 580, 30, 10);
            card.Controls.Add(_account);
            card.Controls.Add(_id);

            Button copyId = Theme.Secondary("COPIAR ID DE CUENTA");
            copyId.SetBounds(22, 88, 250, 36);
            copyId.Click += CopyAccountId;
            card.Controls.Add(copyId);

            _status = MakeInfo("Estado: consultando...", 22, 132, 580, 30, 13);
            _status.Font = Theme.Font(13, FontStyle.Bold);
            card.Controls.Add(_status);

            _days = MakeInfo("", 22, 164, 580, 28, 11);
            _days.Font = Theme.Font(11, FontStyle.Bold);
            card.Controls.Add(_days);

            _expires = MakeInfo("", 22, 194, 580, 28, 9);
            card.Controls.Add(_expires);

            Label help = new Label
            {
                Text = "La prueba del vendedor es de 90 días y queda asociada a la CUENTA desde su creación. No depende de esta computadora ni del Machine ID. El comprador no necesita licencia.",
                AutoSize = false,
                Left = 22,
                Top = 226,
                Width = 580,
                Height = 50,
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = Theme.Font(8.5f)
            };
            card.Controls.Add(help);

            Label lab = new Label
            {
                Text = "Código/token de licencia comprada:",
                AutoSize = true,
                Left = 22,
                Top = 286,
                ForeColor = Theme.Text,
                Font = Theme.Font(9, FontStyle.Bold)
            };
            card.Controls.Add(lab);

            _token = new TextBox
            {
                Left = 22,
                Top = 310,
                Width = 580,
                Height = 62,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Card2,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Font(8.5f)
            };
            _token.Text = _license.SavedToken();
            card.Controls.Add(_token);

            _activate = Theme.Primary("PEGAR / ACTIVAR CÓDIGO");
            _activate.SetBounds(22, 382, 250, 38);
            _activate.Click += Activate;
            card.Controls.Add(_activate);

            Button copyToken = Theme.Secondary("COPIAR TOKEN");
            copyToken.SetBounds(282, 382, 145, 38);
            copyToken.Click += CopyToken;
            card.Controls.Add(copyToken);

            Button saveToken = Theme.Secondary("GUARDAR TOKEN");
            saveToken.SetBounds(437, 382, 165, 38);
            saveToken.Click += SaveToken;
            card.Controls.Add(saveToken);

            Button close = Theme.Secondary("CONTINUAR");
            close.SetBounds(22, 430, 580, 36);
            close.Click += Continue;
            card.Controls.Add(close);
        }

        private Label MakeInfo(string text, int x, int y, int width, int height, float size)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Left = x,
                Top = y,
                Width = width,
                Height = height,
                Font = Theme.Font(size, FontStyle.Bold),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private void CopyAccountId(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(_license.AccountId() ?? "");
                MessageBox.Show("ID de cuenta copiado. Ese es el único identificador que necesitás enviar para solicitar una licencia.",
                    "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private void CopyToken(object sender, EventArgs e)
        {
            string token = (_token.Text ?? "").Trim();
            if (token.Length == 0)
            {
                MessageBox.Show("No hay ningún token para copiar.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Clipboard.SetText(token);
                MessageBox.Show("Token copiado al portapapeles. Ya podés enviárselo al vendedor.",
                    "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private void SaveToken(object sender, EventArgs e)
        {
            string token = (_token.Text ?? "").Trim();
            if (token.Length == 0)
            {
                MessageBox.Show("Pegá primero el token.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _license.SaveToken(token);
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.FileName = "NexoMarket_Licencia_Cuenta.nexotoken";
                dlg.Filter = "Token NexoMarket (*.nexotoken)|*.nexotoken|Todos (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try { File.WriteAllText(dlg.FileName, token, System.Text.Encoding.UTF8); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("No se pudo guardar el archivo: " + ex.Message,
                            "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void RefreshStatus()
        {
            string status;
            int days;
            DateTime expires;
            bool ok = _license.EnsureAccountTrial(
                _store.GetSetting("web_api_url", "https://nexomarket-central.onrender.com"),
                out status, out days, out expires);

            _account.Text = "Cuenta: " + _license.AccountEmail();
            _id.Text = "ID DE CUENTA: " + _license.AccountId();
            _status.Text = "Estado: " + status;
            _status.ForeColor = ok ? Theme.NeonGreen : Color.OrangeRed;
            _days.Text = ok ? "Días restantes: " + days : "Sin licencia activa";
            _expires.Text = expires == DateTime.MinValue
                ? ""
                : "Vence: " + expires.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

            _activate.Text = ok ? "PEGAR / ACTIVAR CÓDIGO (OPCIONAL)" : "PEGAR / ACTIVAR CÓDIGO";
        }

        private void Activate(object sender, EventArgs e)
        {
            string token = (_token.Text ?? "").Trim();
            if (token.Length == 0)
            {
                RefreshStatus();
                return;
            }

            string message;
            int days;
            DateTime expires;

            if (_license.ActivateToken(
                _store.GetSetting("web_api_url", "https://nexomarket-central.onrender.com"),
                token, out message, out days, out expires))
            {
                RefreshStatus();
                MessageBox.Show("Licencia activada correctamente.\r\nDías restantes: " + days,
                    "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(message, "NexoMarket · Licencia",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Continue(object sender, EventArgs e)
        {
            string status;
            int days;
            DateTime expires;

            if (_license.EnsureAccountTrial(
                _store.GetSetting("web_api_url", "https://nexomarket-central.onrender.com"),
                out status, out days, out expires))
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            MessageBox.Show(
                "La cuenta no tiene una prueba o licencia activa.\r\n\r\n" +
                "Si acabás de crear la cuenta de vendedor, asegurate de tener conexión a Internet para que el servidor registre automáticamente los 90 días.",
                "NexoMarket · Licencia",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
