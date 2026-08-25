using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;
using NexoMarket.Licensing;

namespace NexoMarket.LicenseManager
{
    public sealed class MainForm : Form
    {
        TextBox client, store, machine, api, adminKey;
        ComboBox duration;
        Label result;
        string dataDir;
        string privateKeyPath, publicKeyPath;

        public MainForm()
        {
            Text="NexoMarket License Manager v1.0";
            StartPosition=FormStartPosition.CenterScreen;
            ClientSize=new Size(760,540);
            BackColor=Color.FromArgb(12,16,22); ForeColor=Color.White;
            dataDir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Data");
            Directory.CreateDirectory(dataDir);
            privateKeyPath=Path.Combine(dataDir,"license_private_key.xml");
            publicKeyPath=Path.Combine(dataDir,"license_public_key.xml");
            EnsureKeys();
            Build();
        }

        void Build()
        {
            Label title=new Label{Text="NEXOMARKET LICENSE MANAGER",Dock=DockStyle.Top,Height=58,Font=new Font("Segoe UI",19,FontStyle.Bold),ForeColor=Color.FromArgb(57,255,102),TextAlign=ContentAlignment.MiddleCenter};
            Controls.Add(title);
            int y=78;
            AddLabel("Cliente / comercio",20,y); client=AddBox(220,y,500); y+=48;
            AddLabel("Store ID",20,y); store=AddBox(220,y,500); y+=48;
            AddLabel("Machine ID",20,y); machine=AddBox(220,y,500); y+=48;
            AddLabel("Duración",20,y);
            duration=new ComboBox{Left=220,Top=y,Width=220,DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Color.FromArgb(25,32,42),ForeColor=Color.White};
            duration.Items.AddRange(new object[]{"30 días","90 días","365 días","Permanente"}); duration.SelectedIndex=0; Controls.Add(duration); y+=48;
            AddLabel("API central",20,y); api=AddBox(220,y,500); api.Text="https://nexomarket-central.onrender.com"; y+=48;
            AddLabel("Clave admin API",20,y); adminKey=AddBox(220,y,500); adminKey.UseSystemPasswordChar=true; y+=52;

            Button generate=Button("CREAR / RENOVAR",20,y,210); generate.Click+=Generate;
            Button search=Button("BUSCAR LICENCIA",240,y,190); search.Click+=Search;
            Button revoke=Button("REVOCAR",440,y,130); revoke.Click+=Revoke;
            Button pub=Button("EXPORTAR CLAVE PÚBLICA",580,y,140); pub.Click+=delegate{SavePublicKey();};
            y+=52;
            result=new Label{Left=20,Top=y,Width=700,Height=115,Font=new Font("Consolas",9),ForeColor=Color.LightGray,BorderStyle=BorderStyle.FixedSingle,Padding=new Padding(8)};
            Controls.Add(result);
            result.Text="Clave pública generada en:\r\n"+publicKeyPath+"\r\n\r\nEl archivo privado queda sólo en este equipo.";
        }

        void AddLabel(string t,int x,int y){Controls.Add(new Label{Text=t,Left=x,Top=y+5,Width=190,Height=28,ForeColor=Color.Gainsboro});}
        TextBox AddBox(int x,int y,int w){TextBox t=new TextBox{Left=x,Top=y,Width=w,Height=28,BackColor=Color.FromArgb(25,32,42),ForeColor=Color.White,BorderStyle=BorderStyle.FixedSingle};Controls.Add(t);return t;}
        Button Button(string t,int x,int y,int w){Button b=new Button{Text=t,Left=x,Top=y,Width=w,Height=36,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(30,40,52),ForeColor=Color.White};Controls.Add(b);return b;}

        void EnsureKeys()
        {
            if(File.Exists(privateKeyPath)&&File.Exists(publicKeyPath))return;
            using(var rsa=new System.Security.Cryptography.RSACryptoServiceProvider(2048))
            {
                File.WriteAllText(privateKeyPath,rsa.ToXmlString(true),Encoding.UTF8);
                File.WriteAllText(publicKeyPath,rsa.ToXmlString(false),Encoding.UTF8);
            }
        }

        int SelectedDays(){string s=duration.SelectedItem.ToString();if(s.StartsWith("30"))return 30;if(s.StartsWith("365"))return 365;if(s.StartsWith("Permanente"))return 0;return 90;}

        void Generate(object sender,EventArgs e)
        {
            if(string.IsNullOrWhiteSpace(store.Text)||string.IsNullOrWhiteSpace(machine.Text)||string.IsNullOrWhiteSpace(client.Text)){MessageBox.Show("Cliente, Store ID y Machine ID son obligatorios.");return;}
            try
            {
                int days=SelectedDays(); DateTime issued=DateTime.UtcNow; DateTime expires=days==0?new DateTime(9999,12,31,23,59,59,DateTimeKind.Utc):issued.AddDays(days);
                LicenseRecord r=new LicenseRecord{StoreId=store.Text.Trim(),MachineId=machine.Text.Trim().ToUpperInvariant(),ClientName=client.Text.Trim(),Days=days,IssuedUtc=issued,ExpiresUtc=expires,Status="Active"};
                r.Signature=LicenseCore.Sign(r,File.ReadAllText(privateKeyPath,Encoding.UTF8));
                string token=LicenseCore.Serialize(r);
                string file=Path.Combine(dataDir,"NexoMarket_"+r.StoreId+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".nexolicense");
                File.WriteAllText(file,token,Encoding.UTF8);
                SavePublicKey();
                bool remote=Register(token);
                result.Text="LICENCIA CREADA\r\nArchivo: "+file+"\r\nEstado: Activa · "+(days==0?"Permanente":days+" días")+"\r\nServidor: "+(remote?"Registrada":"No registrada (se guardó localmente)")+"\r\n\r\nEntregar también license_public_key.xml al vendedor.";
            }
            catch(Exception ex){result.Text="ERROR: "+ex.Message;}
        }

        void SavePublicKey()
        {
            using(SaveFileDialog s=new SaveFileDialog{FileName="license_public_key.xml",Filter="XML (*.xml)|*.xml"})
            {
                if(s.ShowDialog()==DialogResult.OK)File.Copy(publicKeyPath,s.FileName,true);
            }
        }

        bool Register(string token)
        {
            try
            {
                string baseUrl=(api.Text??"").Trim().TrimEnd('/'); if(baseUrl.Length==0)return false;
                string body="license="+Uri.EscapeDataString(token)+"&adminKey="+Uri.EscapeDataString(adminKey.Text??"");
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(baseUrl+"/api/licenses/upsert");req.Method="POST";req.Timeout=8000;byte[] b=Encoding.UTF8.GetBytes(body);req.ContentType="application/x-www-form-urlencoded";req.ContentLength=b.Length;
                using(Stream s=req.GetRequestStream())s.Write(b,0,b.Length);
                using(WebResponse r=req.GetResponse())using(StreamReader sr=new StreamReader(r.GetResponseStream()))return sr.ReadToEnd().StartsWith("OK|",StringComparison.OrdinalIgnoreCase);
            }catch{return false;}
        }

        void Search(object sender,EventArgs e)
        {
            try
            {
                string baseUrl=(api.Text??"").Trim().TrimEnd('/'); string q="";
                if(!string.IsNullOrWhiteSpace(store.Text))q="storeId="+Uri.EscapeDataString(store.Text.Trim());
                if(!string.IsNullOrWhiteSpace(machine.Text))q+=(q.Length>0?"&":"")+"machineId="+Uri.EscapeDataString(machine.Text.Trim());
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(baseUrl+"/api/licenses/search?"+q);req.Timeout=8000;
                using(WebResponse r=req.GetResponse())using(StreamReader sr=new StreamReader(r.GetResponseStream(),Encoding.UTF8))result.Text=sr.ReadToEnd();
            }catch(Exception ex){result.Text="No se pudo consultar: "+ex.Message;}
        }

        void Revoke(object sender,EventArgs e)
        {
            try
            {
                string baseUrl=(api.Text??"").Trim().TrimEnd('/'); string body="storeId="+Uri.EscapeDataString(store.Text.Trim())+"&machineId="+Uri.EscapeDataString(machine.Text.Trim())+"&adminKey="+Uri.EscapeDataString(adminKey.Text??"");
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(baseUrl+"/api/licenses/revoke");req.Method="POST";req.Timeout=8000;byte[] b=Encoding.UTF8.GetBytes(body);req.ContentType="application/x-www-form-urlencoded";req.ContentLength=b.Length;
                using(Stream s=req.GetRequestStream())s.Write(b,0,b.Length);
                using(WebResponse r=req.GetResponse())using(StreamReader sr=new StreamReader(r.GetResponseStream()))result.Text=sr.ReadToEnd();
            }catch(Exception ex){result.Text="No se pudo revocar: "+ex.Message;}
        }
    }
}
