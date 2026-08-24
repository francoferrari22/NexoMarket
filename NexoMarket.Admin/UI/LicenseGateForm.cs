using System;
using System.Drawing;
using System.IO;
using System.Text;
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
        private TextBox _activation;
        public LicenseGateForm(AppDataStore store)
        {
            _store=store;
            _license=new LicenseService(store.Root);
            Text="NexoMarket · Licencia";
            StartPosition=FormStartPosition.CenterScreen;
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MaximizeBox=false; MinimizeBox=false;
            ClientSize=new Size(560,410);
            BackColor=Theme.Background; ForeColor=Theme.Text;
            Build();
            RefreshStatus();
        }
        private void Build()
        {
            Label title=new Label{Text="LICENCIA NEXOMARKET",AutoSize=false,Height=42,Dock=DockStyle.Top,Font=Theme.Font(18,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter};
            Controls.Add(title);
            Label machine=new Label{Text="Machine ID: "+_license.MachineId,AutoSize=false,Height=45,Left=25,Top=62,Width=400,Font=Theme.Font(9,FontStyle.Bold),ForeColor=Theme.Text};
            machine.TextAlign=ContentAlignment.MiddleLeft; Controls.Add(machine);
            Button copyMachine=Theme.Secondary("COPIAR ID"); copyMachine.SetBounds(425,67,100,34); copyMachine.Click+=delegate{Clipboard.SetText(_license.MachineId); MessageBox.Show("Machine ID copiado al portapapeles. Podés enviárselo al propietario del programa para solicitar más días de licencia.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}; Controls.Add(copyMachine);
            Label store=new Label{Text="Store ID: "+_license.StoreId(),AutoSize=false,Height=35,Left=25,Top=105,Width=400,ForeColor=Theme.Text}; Controls.Add(store);
            Button copyStore=Theme.Secondary("COPIAR"); copyStore.SetBounds(425,105,100,32); copyStore.Click+=delegate{Clipboard.SetText(_license.StoreId());}; Controls.Add(copyStore);
            _status=new Label{AutoSize=false,Height=32,Left=25,Top=150,Width=510,Font=Theme.Font(11,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_status);
            _days=new Label{AutoSize=false,Height=26,Left=25,Top=185,Width=510,ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_days);
            Label codeLabel=new Label{Text="Código de activación",AutoSize=false,Height=24,Left=25,Top=218,Width=510,ForeColor=Theme.Text,Font=Theme.Font(9,FontStyle.Bold)}; Controls.Add(codeLabel);
            _activation=new TextBox{Left=25,Top=242,Width=510,Height=44,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Theme.Panel,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle}; Controls.Add(_activation);
            Button install=Theme.Primary("ACTIVAR CÓDIGO"); install.SetBounds(25,295,170,40); install.Click+=InstallCode; Controls.Add(install);
            Button paste=Theme.Secondary("PEGAR"); paste.SetBounds(205,295,120,40); paste.Click+=delegate{try{_activation.Text=Clipboard.GetText();}catch{}}; Controls.Add(paste);
            Button refresh=Theme.Secondary("CONSULTAR SERVIDOR"); refresh.SetBounds(335,295,200,40); refresh.Click+=delegate{_license.RefreshFromServer(_store.GetSetting("web_api_url",""));RefreshStatus();}; Controls.Add(refresh);
            Button close=Theme.Secondary("CONTINUAR"); close.SetBounds(105,345,350,40); close.Click+=delegate{ string st; int d; if (_license.IsValid(out st,out d)) { DialogResult=DialogResult.OK; Close(); } else { MessageBox.Show("La licencia no está activa. Durante los primeros 30 días se habilita automáticamente la prueba inicial.","NexoMarket · Licencia",MessageBoxButtons.OK,MessageBoxIcon.Warning); } }; Controls.Add(close);
        }
        private void RefreshStatus()
        {
            string status; int days;
            bool ok=_license.IsValid(out status,out days);
            _status.Text="Estado: "+status;
            _status.ForeColor=ok?Theme.NeonGreen:Color.OrangeRed;
            _days.Text=days<0?"Días restantes: Permanente":"Días restantes: "+days;
        }
        private void InstallCode(object sender,EventArgs e)
        {
            string code=(_activation.Text??"").Trim();
            if(string.IsNullOrWhiteSpace(code)){MessageBox.Show("Pegá el código de activación.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
            try
            {
                _license.InstallActivationCode(code,_store.GetSetting("web_api_url",""));
                RefreshStatus();
                MessageBox.Show("Código de activación instalado correctamente.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception ex){MessageBox.Show(ex.Message,"NexoMarket · Activación",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
}