using System;
using System.Drawing;
using System.Windows.Forms;
using NexoMarket.Admin.Data;

namespace NexoMarket.Admin.UI
{
    public sealed class LicenseGateForm : Form
    {
        private readonly AppDataStore _store; private readonly LicenseService _license;
        private Label _status,_days,_account,_id,_expires; private TextBox _token; private Button _activate;
        public LicenseGateForm(AppDataStore store){_store=store;_license=new LicenseService(store.Root);Text="NexoMarket · Licencia de cuenta";StartPosition=FormStartPosition.CenterScreen;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;ClientSize=new Size(620,470);BackColor=Theme.Background;ForeColor=Theme.Text;Build();RefreshStatus();}
        private void Build(){
            Controls.Clear();
            Controls.Add(new Label{Text="LICENCIA DEL VENDEDOR",AutoSize=false,Height=45,Dock=DockStyle.Top,Font=Theme.Font(18,FontStyle.Bold),ForeColor=Theme.NeonGreen,TextAlign=ContentAlignment.MiddleCenter});
            _account=L("Cuenta: "+_license.AccountEmail(),58,510,30,10);_id=L("ID de cuenta: "+_license.AccountId(),92,510,30,9);Controls.Add(_account);Controls.Add(_id);
            Button copyId=Theme.Secondary("COPIAR ID DE CUENTA");copyId.SetBounds(205,126,210,34);copyId.Click+=delegate{try{Clipboard.SetText(_license.AccountId()??"");MessageBox.Show("ID copiado. Enviáselo al administrador para solicitar una licencia.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch{}};Controls.Add(copyId);
            _status=L("Estado: consultando...",168,510,35,13);_status.Font=Theme.Font(13,FontStyle.Bold);Controls.Add(_status);
            _days=L("",207,510,30,11);_days.Font=Theme.Font(11,FontStyle.Bold);Controls.Add(_days);
            _expires=L("",238,510,25,9);Controls.Add(_expires);
            Controls.Add(new Label{Text="La prueba inicial del vendedor es de 60 días y pertenece a la CUENTA, no a la computadora. El comprador no necesita licencia.",AutoSize=false,Left=45,Top=270,Width=530,Height=42,ForeColor=Theme.Muted,TextAlign=ContentAlignment.MiddleCenter,Font=Theme.Font(8.5f)});
            Label lab=new Label{Text="Si recibiste un código de licencia, pegalo aquí:",AutoSize=true,Left=45,Top=322,ForeColor=Theme.Text,Font=Theme.Font(9,FontStyle.Bold)};Controls.Add(lab);
            _token=new TextBox{Left=45,Top=347,Width=530,Height=52,Multiline=true,ScrollBars=ScrollBars.Vertical,BackColor=Theme.Card2,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle,Font=Theme.Font(8.5f)};Controls.Add(_token);
            _activate=Theme.Primary("PEGAR / ACTIVAR CÓDIGO");_activate.SetBounds(45,407,250,38);_activate.Click+=Activate;Controls.Add(_activate);
            Button close=Theme.Secondary("CONTINUAR / CERRAR");close.SetBounds(325,407,250,38);close.Click=delegate{if(_license.IsValid(out var s,out var d)){DialogResult=DialogResult.OK;Close();}else MessageBox.Show("La cuenta todavía no tiene una licencia activa.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Warning);};Controls.Add(close);
        }
        private Label L(string text,int y,int w,int h,float size){return new Label{Text=text,AutoSize=false,Left=55,Top=y,Width=w,Height=h,Font=Theme.Font(size,FontStyle.Bold),ForeColor=Theme.Text,TextAlign=ContentAlignment.MiddleCenter};}
        private void RefreshStatus(){string s;int d;DateTime e;bool ok=_license.EnsureAccountTrial(_store.GetSetting("web_api_url","https://nexomarket-central.onrender.com"),out s,out d,out e);_account.Text="Cuenta: "+_license.AccountEmail();_id.Text="ID de cuenta: "+_license.AccountId();_status.Text="Estado: "+s;_status.ForeColor=ok?Theme.NeonGreen:Color.OrangeRed;_days.Text=ok?"Días restantes: "+d:"Sin licencia activa";_expires.Text=e==DateTime.MinValue?"":"Vence: "+e.ToLocalTime().ToString("dd/MM/yyyy HH:mm");_activate.Text=ok?"PEGAR / ACTIVAR CÓDIGO (OPCIONAL)":"PEGAR / ACTIVAR CÓDIGO";}
        private void Activate(object sender,EventArgs e){string token=(_token.Text??"").Trim();if(token.Length==0){RefreshStatus();return;}string msg;int d;DateTime exp;if(_license.ActivateToken(_store.GetSetting("web_api_url","https://nexomarket-central.onrender.com"),token,out msg,out d,out exp)){RefreshStatus();MessageBox.Show("Licencia activada correctamente.\r\nDías restantes: "+d,"NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}else MessageBox.Show(msg,"NexoMarket · Licencia",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
}
