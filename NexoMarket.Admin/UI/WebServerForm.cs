using System;
using System.Drawing;
using System.Windows.Forms;
using NexoMarket.Admin.Data;

namespace NexoMarket.Admin.UI
{
    public sealed class WebServerForm : Form
    {
        private readonly AppDataStore _store;
        private readonly WebServerService _server;
        private Label _status, _url, _storeId, _code;
        private Button _toggle;
        private Timer _uiTimer;

        public WebServerForm(AppDataStore store, WebServerService server)
        {
            _store = store;
            _server = server;
            Text = "NexoMarket · Servidor web"; StartPosition = FormStartPosition.CenterParent; Size = new Size(760, 650); BackColor = Theme.Background; ForeColor = Theme.Text;
            Build();
            RefreshStatus();
            _uiTimer = new Timer { Interval = 1000 };
            _uiTimer.Tick += delegate { RefreshStatus(); };
            _uiTimer.Start();
        }
        private void Build()
        {
            Panel root=new Panel{Dock=DockStyle.Fill,Padding=new Padding(28),BackColor=Theme.Background}; Controls.Add(root);
            Label title=new Label{Text="SERVIDOR WEB · NEXOMARKET",Font=Theme.Font(20,FontStyle.Bold),ForeColor=Theme.NeonGreen,AutoSize=true,Location=new Point(28,25)}; root.Controls.Add(title);
            Label info=new Label{Text="El servidor queda activo en segundo plano mientras NexoMarket está abierto.\r\nSi el proceso de escucha falla, se recupera automáticamente cada 10 segundos.",AutoSize=false,Size=new Size(650,55),Location=new Point(28,65),ForeColor=Theme.Muted};root.Controls.Add(info);
            _status=Value(root,"Estado","",130); _url=Value(root,"Dirección web local","",185); _storeId=Value(root,"StoreId","",240); _code=Value(root,"Código de servidor","",295); Value(root,"API central",_store.GetSetting("web_api_url","(no configurada)"),350);
            _toggle=Theme.Primary("DETENER SERVIDOR");_toggle.Location=new Point(28,420);_toggle.Width=190;_toggle.Click+=Toggle;root.Controls.Add(_toggle);
            Button open=Theme.Primary("ABRIR EN NAVEGADOR");open.Location=new Point(230,420);open.Width=210;open.Click+=delegate{try{System.Diagnostics.Process.Start(_server.LocalUrl);}catch{}};root.Controls.Add(open);
            Button copyUrl=Theme.Primary("COPIAR URL");copyUrl.Location=new Point(455,420);copyUrl.Width=135;copyUrl.Click+=delegate{try{Clipboard.SetText(_server.LocalUrl);}catch{}};root.Controls.Add(copyUrl);
            Button copyCode=Theme.Primary("COPIAR CÓDIGO");copyCode.Location=new Point(600,420);copyCode.Width=130;copyCode.Click+=delegate{try{Clipboard.SetText(_server.LocalCode);}catch{}};root.Controls.Add(copyCode);
            Label roles=new Label{Text="WEB: Registro → elección VENDEDOR / COMPRADOR → login → panel según rol.\r\nVENDEDOR: Dashboard, pedidos, productos, analítica, finanzas y marketing.\r\nCOMPRADOR: ve tiendas primero, entra a una tienda y recién allí compra productos.\r\nCENTRAL: Configuración → Tienda web permite indicar la API central y habilitar la sincronización por StoreId.",AutoSize=false,Size=new Size(650,75),Location=new Point(28,480),ForeColor=Theme.Text};root.Controls.Add(roles);
        }
        private Label Value(Control p,string label,string value,int y){Label l=new Label{Text=label,ForeColor=Theme.Muted,AutoSize=true,Location=new Point(28,y)};p.Controls.Add(l);Label v=new Label{Text=value,ForeColor=Theme.Text,AutoSize=false,Size=new Size(600,30),Location=new Point(28,y+20),Font=Theme.Font(10,FontStyle.Bold)};p.Controls.Add(v);return v;}
        private void RefreshStatus()
        {
            if (IsDisposed) return;
            bool running = _server != null && _server.IsRunning;
            _status.Text = running ? "● ACTIVO · AUTO-RECUPERACIÓN" : "● RECUPERANDO SERVIDOR...";
            _status.ForeColor = running ? Theme.NeonGreen : Color.FromArgb(255,190,50);
            _url.Text = _server != null ? _server.LocalUrl : "No disponible";
            _storeId.Text = _server != null ? _server.StoreId : "";
            _code.Text = _server != null ? _server.LocalCode : "";
            _toggle.Text = running ? "DETENER SERVIDOR" : "INICIAR / RECUPERAR";
        }
        private void Toggle(object s,EventArgs e)
        {
            if (_server.IsRunning) _server.Stop(); else _server.Start();
            RefreshStatus();
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_uiTimer != null) { _uiTimer.Stop(); _uiTimer.Dispose(); _uiTimer = null; }
            // Importante: cerrar esta ventana NO detiene el servidor.
            base.OnFormClosed(e);
        }
    }
}
