using System;
using System.IO;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Licencia del vendedor basada en la CUENTA, no en la computadora.
    /// La primera creación/entrada de una cuenta de vendedor activa 60 días en el servidor central.
    /// La fecha de vencimiento queda asociada al correo de la cuenta y se conserva al cambiar de PC.
    /// </summary>
    public sealed class LicenseService
    {
        private readonly string _dataRoot;
        private const int TrialDays = 60;
        public LicenseService(string dataRoot) { _dataRoot = dataRoot; }
        public string MachineId { get { return LegacyMachineId(); } }
        public string LicensePath { get { return Path.Combine(_dataRoot, "nexomarket.accountlicense"); } }

        public string StoreId()
        {
            string p = Path.Combine(_dataRoot, "nexomarket_data.xml");
            try
            {
                var doc = XDocument.Load(p);
                var e = doc.Root.Element("Settings").Elements("Setting");
                foreach (var x in e) if ((string)x.Attribute("Key") == "store_id") return (string)x.Attribute("Value") ?? "";
            } catch { }
            return "";
        }

        public string AccountEmail()
        {
            string p = Path.Combine(_dataRoot, "nexomarket_data.xml");
            try
            {
                var doc = XDocument.Load(p);
                var e = doc.Root.Element("Settings").Elements("Setting");
                foreach (var x in e) if ((string)x.Attribute("Key") == "seller_account_email") return (string)x.Attribute("Value") ?? "";
            } catch { }
            return "";
        }

        public bool EnsureAccountTrial(string baseUrl, out string status, out int daysRemaining, out DateTime expiresUtc)
        {
            status="Sin licencia"; daysRemaining=0; expiresUtc=DateTime.MinValue;
            string email=AccountEmail();
            if(string.IsNullOrWhiteSpace(email)) return false;
            baseUrl=Normalize(baseUrl);
            if(baseUrl.Length==0) baseUrl="https://nexomarket-central.onrender.com";
            try
            {
                // La cuenta creada localmente debe existir primero en Central.
                // Esto permite activar los 60 días en el mismo primer ingreso, sin esperar al sincronizador periódico.
                PublishLocalSellerAccount(baseUrl, email);
                string body="email="+Uri.EscapeDataString(email.Trim().ToLowerInvariant())+"&storeId="+Uri.EscapeDataString(StoreId())+"&role=seller";
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(baseUrl+"/api/accounts/ensure-trial");
                req.Method="POST"; req.Timeout=8000; req.ReadWriteTimeout=8000; req.ContentType="application/x-www-form-urlencoded";
                byte[] b=Encoding.UTF8.GetBytes(body); req.ContentLength=b.Length;
                using(Stream s=req.GetRequestStream())s.Write(b,0,b.Length);
                using(WebResponse resp=req.GetResponse()) using(StreamReader sr=new StreamReader(resp.GetResponseStream(),Encoding.UTF8))
                {
                    string line=sr.ReadToEnd().Trim();
                    if(!line.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)) return LoadCached(out status,out daysRemaining,out expiresUtc);
                    string[] p=line.Split('|');
                    if(p.Length<5) return LoadCached(out status,out daysRemaining,out expiresUtc);
                    status=string.Equals(p[1],"Active",StringComparison.OrdinalIgnoreCase)?"Activa":"Vencida";
                    int.TryParse(p[2],out daysRemaining);
                    DateTime.TryParse(p[4],null,System.Globalization.DateTimeStyles.RoundtripKind,out expiresUtc);
                    SaveCache(email,status,daysRemaining,expiresUtc,p.Length>3?p[3]:"");
                    return status=="Activa" && expiresUtc>DateTime.UtcNow;
                }
            }
            catch { return LoadCached(out status,out daysRemaining,out expiresUtc); }
        }


        private void PublishLocalSellerAccount(string baseUrl,string email)
        {
            try
            {
                string p=Path.Combine(_dataRoot,"nexomarket_data.xml");
                XDocument doc=XDocument.Load(p);
                XElement node=doc.Root.Element("WebUsers").Elements("WebUser");
                XElement u=null;
                foreach(XElement x in node) if(string.Equals((string)x.Element("Email")??"",email,StringComparison.OrdinalIgnoreCase) && string.Equals((string)x.Element("Role")??"","seller",StringComparison.OrdinalIgnoreCase)){u=x;break;}
                if(u==null)return;
                string body="id="+Uri.EscapeDataString((string)u.Attribute("Id")??"")+
                    "&name="+Uri.EscapeDataString((string)u.Element("Name")??"")+
                    "&email="+Uri.EscapeDataString(email)+
                    "&phone="+Uri.EscapeDataString((string)u.Element("Phone")??"")+
                    "&role=seller&storeId="+Uri.EscapeDataString(StoreId())+
                    "&salt="+Uri.EscapeDataString((string)u.Element("Salt")??"")+
                    "&passwordHash="+Uri.EscapeDataString((string)u.Element("PasswordHash")??"")+
                    "&createdAt="+Uri.EscapeDataString((string)u.Element("CreatedAt")??DateTime.UtcNow.ToString("o"));
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(baseUrl+"/api/accounts/upsert"); req.Method="POST"; req.Timeout=8000; req.ContentType="application/x-www-form-urlencoded";
                byte[] b=Encoding.UTF8.GetBytes(body); req.ContentLength=b.Length; using(Stream st=req.GetRequestStream())st.Write(b,0,b.Length);
                using(WebResponse resp=req.GetResponse()){}
            } catch { }
        }
        public bool IsValid(out string status, out int daysRemaining)
        {
            DateTime expires;
            bool ok=EnsureAccountTrial("https://nexomarket-central.onrender.com",out status,out daysRemaining,out expires);
            if(ok) return true;
            if(expires!=DateTime.MinValue && expires<=DateTime.UtcNow){status="Vencida";daysRemaining=0;}
            return false;
        }

        public bool IsValid(string baseUrl,out string status,out int daysRemaining)
        {
            DateTime expires;
            return EnsureAccountTrial(baseUrl,out status,out daysRemaining,out expires);
        }

        public bool RefreshFromServer(string baseUrl)
        {
            string status; int days; DateTime expires;
            return EnsureAccountTrial(baseUrl,out status,out days,out expires);
        }

        private void SaveCache(string email,string status,int days,DateTime expires,string started)
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                string text="Email="+email+Environment.NewLine+"Status="+status+Environment.NewLine+"Days="+days+Environment.NewLine+"StartedUtc="+started+Environment.NewLine+"ExpiresUtc="+expires.ToString("o")+Environment.NewLine;
                File.WriteAllText(LicensePath,text,Encoding.UTF8);
            } catch { }
        }

        private bool LoadCached(out string status,out int days,out DateTime expires)
        {
            status="Sin licencia"; days=0; expires=DateTime.MinValue;
            try
            {
                if(!File.Exists(LicensePath)) return false;
                foreach(string line in File.ReadAllLines(LicensePath,Encoding.UTF8))
                {
                    int i=line.IndexOf('='); if(i<1)continue; string k=line.Substring(0,i); string v=line.Substring(i+1);
                    if(k=="Status")status=v; else if(k=="Days")int.TryParse(v,out days); else if(k=="ExpiresUtc")DateTime.TryParse(v,null,System.Globalization.DateTimeStyles.RoundtripKind,out expires);
                }
                if(expires>DateTime.UtcNow){days=Math.Max(0,(int)(expires.Date-DateTime.UtcNow.Date).TotalDays);status="Activa";return true;}
                status="Vencida";days=0;return false;
            } catch { return false; }
        }

        private static string Normalize(string u){string v=(u??"").Trim().TrimEnd('/');if(v.Length==0||v.IndexOf("tudominio.com",StringComparison.OrdinalIgnoreCase)>=0)return "https://nexomarket-central.onrender.com";if(v.EndsWith("/api",StringComparison.OrdinalIgnoreCase))v=v.Substring(0,v.Length-4).TrimEnd('/');return v;}
        private static string LegacyMachineId()
        {
            string machineGuid=""; try{using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))machineGuid=Convert.ToString(key==null?null:key.GetValue("MachineGuid"));}catch{}
            string raw=(machineGuid+"|"+Environment.MachineName).Trim(); using(var sha=System.Security.Cryptography.SHA256.Create()){byte[] b=sha.ComputeHash(Encoding.UTF8.GetBytes(raw));StringBuilder s=new StringBuilder();foreach(byte x in b)s.Append(x.ToString("X2"));return s.ToString();}
        }
    }
}
