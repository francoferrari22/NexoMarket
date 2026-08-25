using System;
using System.IO;
using System.Net;
using System.Text;
using System.Xml.Linq;
using System.Collections.Generic;
using NexoMarket.Licensing;

namespace NexoMarket.Admin.UI
{
    public sealed class LicenseService
    {
        private readonly string _dataRoot;
        public const int TrialDays = 60;
        public LicenseService(string dataRoot){_dataRoot=dataRoot;}
        public string LicensePath { get { return Path.Combine(_dataRoot,"nexomarket.accountlicense"); } }
        private XDocument Data(){return XDocument.Load(Path.Combine(_dataRoot,"nexomarket_data.xml"));}
        public string StoreId(){try{var d=Data();var e=d.Root.Element("Settings").Elements("Setting");foreach(var x in e)if((string)x.Attribute("Key")=="store_id")return (string)x.Attribute("Value")??"";}catch{}return "";}
        public string AccountEmail(){try{var d=Data();var e=d.Root.Element("Settings").Elements("Setting");foreach(var x in e)if((string)x.Attribute("Key")=="seller_account_email")return (string)x.Attribute("Value")??"";}catch{}return "";}
        public string AccountId(){
            string email=AccountEmail().Trim().ToLowerInvariant(); if(email.Length==0)return "";
            using(var sha=System.Security.Cryptography.SHA256.Create()){byte[] b=sha.ComputeHash(Encoding.UTF8.GetBytes("NEXOMARKET-ACCOUNT|"+email));var sb=new StringBuilder();foreach(byte x in b)sb.Append(x.ToString("X2"));return sb.ToString();}
        }
        private string Base(string u){u=(u??"").Trim().TrimEnd('/');if(u.Length==0||u.IndexOf("tudominio.com",StringComparison.OrdinalIgnoreCase)>=0)return "https://nexomarket-central.onrender.com";if(u.EndsWith("/api",StringComparison.OrdinalIgnoreCase))u=u.Substring(0,u.Length-4).TrimEnd('/');return u;}
        private string Post(string url,string body){HttpWebRequest req=(HttpWebRequest)WebRequest.Create(url);req.Method="POST";req.Timeout=10000;req.ReadWriteTimeout=10000;req.ContentType="application/x-www-form-urlencoded";byte[] b=Encoding.UTF8.GetBytes(body);req.ContentLength=b.Length;using(Stream s=req.GetRequestStream())s.Write(b,0,b.Length);using(WebResponse r=req.GetResponse())using(StreamReader sr=new StreamReader(r.GetResponseStream(),Encoding.UTF8))return sr.ReadToEnd().Trim();}
        private void PublishLocalSellerAccount(string baseUrl){try{string email=AccountEmail();if(string.IsNullOrWhiteSpace(email))return;XDocument doc=Data();IEnumerable<XElement> nodes=doc.Root.Element("WebUsers").Elements("WebUser");XElement u=null;foreach(XElement x in nodes)if(string.Equals((string)x.Element("Email")??"",email,StringComparison.OrdinalIgnoreCase)&&string.Equals((string)x.Element("Role")??"","seller",StringComparison.OrdinalIgnoreCase)){u=x;break;}if(u==null)return;string body="id="+Uri.EscapeDataString((string)u.Attribute("Id")??"")+"&name="+Uri.EscapeDataString((string)u.Element("Name")??"")+"&email="+Uri.EscapeDataString(email)+"&phone="+Uri.EscapeDataString((string)u.Element("Phone")??"")+"&role=seller&storeId="+Uri.EscapeDataString(StoreId())+"&salt="+Uri.EscapeDataString((string)u.Element("Salt")??"")+"&passwordHash="+Uri.EscapeDataString((string)u.Element("PasswordHash")??"")+"&createdAt="+Uri.EscapeDataString((string)u.Element("CreatedAt")??DateTime.UtcNow.ToString("o"));Post(baseUrl+"/api/accounts/upsert",body);}catch{}}
        public bool EnsureAccountTrial(string baseUrl,out string status,out int daysRemaining,out DateTime expiresUtc){status="Sin licencia";daysRemaining=0;expiresUtc=DateTime.MinValue;string email=AccountEmail();if(string.IsNullOrWhiteSpace(email))return false;baseUrl=Base(baseUrl);try{PublishLocalSellerAccount(baseUrl);string body="email="+Uri.EscapeDataString(email.Trim().ToLowerInvariant())+"&storeId="+Uri.EscapeDataString(StoreId())+"&accountId="+Uri.EscapeDataString(AccountId())+"&role=seller";string line=Post(baseUrl+"/api/accounts/ensure-trial",body);if(line.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)){ParseResponse(line,out status,out daysRemaining,out expiresUtc);SaveCache(email,status,daysRemaining,expiresUtc,line);return string.Equals(status,"Activa",StringComparison.OrdinalIgnoreCase)&&expiresUtc>DateTime.UtcNow;}return LoadCached(out status,out daysRemaining,out expiresUtc);}catch{return LoadCached(out status,out daysRemaining,out expiresUtc);}}
        public void RefreshFromServer(string baseUrl)
        {
            string status; int days; DateTime expires;
            EnsureAccountTrial(baseUrl, out status, out days, out expires);
        }
        public bool ActivateToken(string baseUrl,string token,out string message,out int daysRemaining,out DateTime expiresUtc){message="No se pudo activar el código.";daysRemaining=0;expiresUtc=DateTime.MinValue;string email=AccountEmail(), accountId=AccountId(), storeId=StoreId();if(string.IsNullOrWhiteSpace(email)){message="No hay una cuenta de vendedor iniciada.";return false;}LicenseRecord r;if(!LicenseCore.TryParse(token,out r)){message="El código/token no tiene un formato válido.";return false;}if(!string.Equals(r.AccountEmail,email,StringComparison.OrdinalIgnoreCase)||!string.Equals(r.AccountId,accountId,StringComparison.OrdinalIgnoreCase)||(!string.IsNullOrWhiteSpace(r.StoreId)&&!string.IsNullOrWhiteSpace(storeId)&&!string.Equals(r.StoreId,storeId,StringComparison.OrdinalIgnoreCase))){message="El código pertenece a otra cuenta o tienda.";return false;}try{string line=Post(Base(baseUrl)+"/api/licenses/activate-account","email="+Uri.EscapeDataString(email)+"&accountId="+Uri.EscapeDataString(accountId)+"&storeId="+Uri.EscapeDataString(storeId)+"&license="+Uri.EscapeDataString(token));if(line.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)){ParseResponse(line,out message,out daysRemaining,out expiresUtc);SaveCache(email,message,daysRemaining,expiresUtc,line);return string.Equals(message,"Activa",StringComparison.OrdinalIgnoreCase)&&expiresUtc>DateTime.UtcNow;}message=line;return false;}catch(Exception ex){message="No se pudo contactar al servidor: "+ex.Message;return false;}}
        private void ParseResponse(string line,out string status,out int days,out DateTime expires){status="Sin licencia";days=0;expires=DateTime.MinValue;string[] p=line.Split('|');if(p.Length>1)status=p[1];if(p.Length>2)int.TryParse(p[2],out days);if(p.Length>4)DateTime.TryParse(p[4],null,System.Globalization.DateTimeStyles.RoundtripKind,out expires);}
        public bool IsValid(out string status,out int daysRemaining){DateTime e;return EnsureAccountTrial("https://nexomarket-central.onrender.com",out status,out daysRemaining,out e);}
        private void SaveCache(string email,string status,int days,DateTime expires,string raw){try{Directory.CreateDirectory(_dataRoot);File.WriteAllText(LicensePath,"Email="+email+Environment.NewLine+"Status="+status+Environment.NewLine+"Days="+days+Environment.NewLine+"ExpiresUtc="+expires.ToString("o")+Environment.NewLine+"ServerResponse="+raw+Environment.NewLine,Encoding.UTF8);}catch{}}
        private bool LoadCached(out string status,out int days,out DateTime expires){status="Sin licencia";days=0;expires=DateTime.MinValue;try{if(!File.Exists(LicensePath))return false;foreach(string line in File.ReadAllLines(LicensePath,Encoding.UTF8)){int i=line.IndexOf('=');if(i<1)continue;string k=line.Substring(0,i),v=line.Substring(i+1);if(k=="Status")status=v;else if(k=="Days")int.TryParse(v,out days);else if(k=="ExpiresUtc")DateTime.TryParse(v,null,System.Globalization.DateTimeStyles.RoundtripKind,out expires);}if(expires>DateTime.UtcNow){days=Math.Max(0,(int)(expires.Date-DateTime.UtcNow.Date).TotalDays);status="Activa";return true;}return false;}catch{return false;}}
    }
}
