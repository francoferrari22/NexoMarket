using System;
using System.Drawing;
using System.Windows.Forms;
using NexoMarket.Admin.Data;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Puerta de acceso del vendedor. La licencia es de la CUENTA y se activa una sola vez
    /// por 60 días en NexoMarket Central. No se ata al Machine ID.
    /// </summary>
    public sealed class LicenseGateForm : Form
    {
        private readonly AppDataStore _store;
        private readonly LicenseService _license;
        private Label _status;
        private Label _days;
        private Label _account;
        private Label _expires;
        private Button _retry;

        public LicenseGateForm(AppDataStore store)
        {
            _store=store;
            _license=new LicenseService(store.Root);
            Text="NexoMarket · Licencia de cuenta";
            StartPosition=FormStartPosition.CenterScreen;
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false; MinimizeBox=false;
            ClientSize=new Size(560,360);
            BackColor=Theme.Background; ForeColor=Theme.Text;
            Build();
            RefreshStatus();
        }

        private void Build()
        {
            Label title=new Label{Text="LICENCIA DEL VENDEDOR",AutoSize=false,Height=48,Dock=DockStyle.Top,Font=Theme.Font(18,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter};
            Controls.Add(title);
            _account=new Label{Text="Cuenta: "+_license.AccountEmail(),AutoSize=false,Height=30,Left=25,Top=62,Width=510,Font=Theme.Font(10,FontStyle.Bold),ForeColor=Theme.Text,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_account);
            _status=new Label{AutoSize=false,Height=38,Left=25,Top=102,Width=510,Font=Theme.Font(13,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_status);
            _days=new Label{AutoSize=false,Height=30,Left=25,Top=142,Width=510,Font=Theme.Font(11,FontStyle.Bold),ForeColor=Theme.Text,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_days);
            _expires=new Label{AutoSize=false,Height=26,Left=25,Top=174,Width=510,ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_expires);
            Label info=new Label{Text="La licencia pertenece a tu cuenta de vendedor, no a esta computadora. Si iniciás sesión con la misma cuenta en otra PC, conserva la misma fecha de vencimiento.",AutoSize=false,Left=35,Top=205,Width=490,Height=48,ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter,Font=Theme.Font(8.5f)}; Controls.Add(info);
            _retry=Theme.Primary("ACTIVAR / CONSULTAR MIS 60 DÍAS"); _retry.SetBounds(105,265,350,42); _retry.Click+=RetryClick; Controls.Add(_retry);
            Button close=Theme.Secondary("CERRAR"); close.SetBounds(185,313,190,34); close.Click+=delegate{DialogResult=DialogResult.Cancel;Close();}; Controls.Add(close);
        }

        private void RefreshStatus()
        {
            string status; int days; DateTime expires;
            bool ok=_license.EnsureAccountTrial(_store.GetSetting("web_api_url","https://nexomarket-central.onrender.com"),out status,out days,out expires);
            _account.Text="Cuenta: "+_license.AccountEmail();
            _status.Text="Estado: "+status;
            _status.ForeColor=ok?Theme.NeonGreen:Color.OrangeRed;
            _days.Text=ok?"Días restantes: "+days:"No hay licencia activa para esta cuenta";
            _expires.Text=expires==DateTime.MinValue?"":("Vence: "+expires.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
            _retry.Text=ok?"CONTINUAR AL PROGRAMA":"ACTIVAR / CONSULTAR MIS 60 DÍAS";
        }

        private void RetryClick(object sender,EventArgs e)
        {
            string status; int days; DateTime expires;
            bool ok=_license.EnsureAccountTrial(_store.GetSetting("web_api_url","https://nexomarket-central.onrender.com"),out status,out days,out expires);
            RefreshStatus();
            if(ok){DialogResult=DialogResult.OK;Close();}
        }

    }
}
