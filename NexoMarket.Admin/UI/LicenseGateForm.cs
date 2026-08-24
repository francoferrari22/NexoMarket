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
        public LicenseGateForm(AppDataStore store)
        {
            _store=store;
            _license=new LicenseService(store.Root);
            Text="NexoMarket · Licencia";
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
            Label title=new Label{Text="LICENCIA NEXOMARKET",AutoSize=false,Height=42,Dock=DockStyle.Top,Font=Theme.Font(18,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter};
            Controls.Add(title);
            Label machine=new Label{Text="Machine ID: "+_license.MachineId,AutoSize=false,Height=45,Left=25,Top=62,Width=510,Font=Theme.Font(9,FontStyle.Bold),ForeColor=Theme.Text};
            machine.TextAlign=ContentAlignment.MiddleLeft; Controls.Add(machine);
            Label store=new Label{Text="Store ID: "+_license.StoreId(),AutoSize=false,Height=35,Left=25,Top=105,Width=510,ForeColor=Theme.Text}; Controls.Add(store);
            _status=new Label{AutoSize=false,Height=32,Left=25,Top=150,Width=510,Font=Theme.Font(11,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_status);
            _days=new Label{AutoSize=false,Height=26,Left=25,Top=185,Width=510,ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter}; Controls.Add(_days);
            Button install=Theme.Primary("INSTALAR / RENOVAR LICENCIA"); install.SetBounds(105,225,350,42); install.Click+=Install; Controls.Add(install);
            Button refresh=Theme.Secondary("CONSULTAR SERVIDOR"); refresh.SetBounds(105,275,170,36); refresh.Click+=delegate{_license.RefreshFromServer(_store.GetSetting("web_api_url",""));RefreshStatus();}; Controls.Add(refresh);
            Button close=Theme.Secondary("CERRAR"); close.SetBounds(285,275,170,36); close.Click+=delegate{DialogResult=DialogResult.Cancel;Close();}; Controls.Add(close);
        }
        private void RefreshStatus()
        {
            string status; int days;
            bool ok=_license.IsValid(out status,out days);
            _status.Text="Estado: "+status;
            _status.ForeColor=ok?Theme.NeonGreen:Color.OrangeRed;
            _days.Text=days<0?"Días restantes: Permanente":"Días restantes: "+days;
        }
        private void Install(object sender,EventArgs e)
        {
            using(OpenFileDialog ofd=new OpenFileDialog{Filter="Licencia NexoMarket (*.nexolicense;*.txt)|*.nexolicense;*.txt|Todos los archivos (*.*)|*.*"})
            {
                if(ofd.ShowDialog()!=DialogResult.OK)return;
                try
                {
                    string token=File.ReadAllText(ofd.FileName,Encoding.UTF8).Trim();
                    string pub="";
                    string pubPath=Path.Combine(Path.GetDirectoryName(ofd.FileName)??"","license_public_key.xml");
                    if(File.Exists(pubPath)) pub=File.ReadAllText(pubPath,Encoding.UTF8);
                    if(string.IsNullOrWhiteSpace(pub))
                    {
                        using(OpenFileDialog p=new OpenFileDialog{Filter="Clave pública XML (*.xml)|*.xml"})
                        {
                            if(p.ShowDialog()!=DialogResult.OK)return;
                            pub=File.ReadAllText(p.FileName,Encoding.UTF8);
                        }
                    }
                    _license.InstallToken(token,pub);
                    RefreshStatus();
                    MessageBox.Show("Licencia instalada correctamente.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);
                }
                catch(Exception ex){MessageBox.Show(ex.Message,"NexoMarket · Licencia",MessageBoxButtons.OK,MessageBoxIcon.Error);}
            }
        }
    }
}
