using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace NexoMarket.CentralServer
{
    /// <summary>
    /// Directorio central liviano, sin IIS ni dependencias externas.
    /// Recibe tiendas por StoreId y sirve la página principal del marketplace.
    /// Está pensado para .NET Framework 4.0 / Windows 8+.
    /// </summary>
    public sealed class CentralServerService : IDisposable
    {
        private readonly int _port;
        private readonly string _root;
        private readonly string _file;
        private readonly string _catalogFile;
        private readonly string _ordersFile;
        private readonly string _licensesFile;
        private readonly string _accountsFile;
        private readonly object _sync = new object();
        private TcpListener _listener;
        private System.Threading.Thread _worker;
        private volatile bool _running;
        private XDocument _doc;
        private readonly R2ObjectStore _r2;
        private readonly Dictionary<string, string> _sellerSessions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CentralServerService(int port)
        {
            _port = port;
            _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _file = Path.Combine(_root, "nexomarket_stores.xml");
            _catalogFile = Path.Combine(_root, "nexomarket_catalog.xml");
            _ordersFile = Path.Combine(_root, "nexomarket_orders.xml");
            _licensesFile = Path.Combine(_root, "nexomarket_licenses.xml");
            _accountsFile = Path.Combine(_root, "nexomarket_accounts.xml");
            Directory.CreateDirectory(_root);
            _r2 = new R2ObjectStore();
            Load();
            EnsureCentralDataFiles();
        }

        public bool Start()
        {
            try
            {
                if (_running) return true;
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start(); _running = true;
                _worker = new System.Threading.Thread(Worker) { IsBackground = true };
                _worker.Start(); return true;
            }
            catch { _running = false; return false; }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (!_running && _listener == null) return;
                _running = false;
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
            }
            try
            {
                if (_worker != null && _worker.IsAlive && _worker != System.Threading.Thread.CurrentThread)
                    _worker.Join(1500);
            }
            catch { }
            _worker = null;
        }

        private void Load()
        {
            lock (_sync)
            {
                RestoreIfMissing(_file, "data/nexomarket_stores.xml");
                if (File.Exists(_file))
                {
                    try { _doc = XDocument.Load(_file); } catch { _doc = NewDoc(); }
                }
                else _doc = NewDoc();
                if (_doc.Root.Element("Stores") == null) _doc.Root.Add(new XElement("Stores"));
                Save();
            }
        }

        private void RestoreIfMissing(string file, string key)
        {
            if (File.Exists(file) || _r2 == null || !_r2.Enabled) return;
            try
            {
                string text = _r2.GetText(key);
                if (!string.IsNullOrWhiteSpace(text)) File.WriteAllText(file, text, Encoding.UTF8);
            }
            catch { }
        }

        private XDocument NewDoc() { return new XDocument(new XElement("NexoMarketRegistry", new XElement("Stores"))); }
        private void Save()
        {
            lock (_sync)
            {
                _doc.Save(_file);
                if (_r2 != null && _r2.Enabled) _r2.PutText("data/nexomarket_stores.xml", File.ReadAllText(_file, Encoding.UTF8));
            }
        }

        private void Worker()
        {
            while (_running)
            {
                try { TcpClient c = _listener.AcceptTcpClient(); System.Threading.ThreadPool.QueueUserWorkItem(delegate { Handle(c); }); }
                catch { if (!_running) break; }
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 7000; client.SendTimeout = 7000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        string request = ReadRequest(stream); if (string.IsNullOrEmpty(request)) return;
                        string[] first = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                        string method = first.Length > 0 ? first[0].ToUpperInvariant() : "GET";
                        string target = first.Length > 1 ? first[1] : "/";
                        string body = Body(request); string path = target; string query = ""; int q = path.IndexOf('?');
                        if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
                        if (path == "/health") { Write(stream, 200, "text/plain", "NexoMarket Central OK\n"); return; }
                        if (path == "/api/storage/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StorageStatus()); return; }
                        if (path == "/api/media/upload" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", UploadMedia(Form(body))); return; }
                        if (path == "/api/stores" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StoreLines(query)); return; }
                        if (path == "/api/stores/json" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", StoreJson(query)); return; }
                        if (path == "/api/geocode" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", Geocode(QueryValue(query, "q"))); return; }
                        if (path == "/api/stores/register" && method == "POST") { Register(Form(body)); Write(stream, 200, "text/plain", "OK|registered\n"); return; }
                        if (path == "/api/accounts/register" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AccountRegister(Form(body))); return; }
                        if (path == "/api/accounts/login" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AccountLogin(Form(body))); return; }
                        if (path == "/api/recovery/send" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", RecoverySend(Form(body))); return; }
                        if (path == "/api/recovery/reset" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", RecoveryReset(Form(body))); return; }
                        if (path == "/api/coupons/validate" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", ValidateCoupon(Form(body))); return; }
                        if (path == "/api/coupons/upsert" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CouponUpsert(Form(body))); return; }
                        if (path == "/api/coupons/list" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", CouponList(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/seller/login" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", SellerLogin(Form(body))); return; }
                        if (path == "/api/seller/products" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", SellerProducts(QueryValue(query, "token"))); return; }
                        if (path == "/api/seller/product/save" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", SellerProductSave(Form(body))); return; }
                        if (path == "/seller/login" && method == "GET") { Write(stream, 200, "text/html; charset=utf-8", SellerLoginPage()); return; }
                        if (path == "/login" && method == "GET") { Write(stream, 200, "text/html; charset=utf-8", PublicLoginPage()); return; }
                        if (path == "/api/accounts/login_plain" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AccountLoginPlain(Form(body))); return; }
                        if (path == "/api/recovery/request" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", RecoveryRequest(Form(body))); return; }
                        if (path == "/seller" || path == "/seller/") { Write(stream, 200, "text/html; charset=utf-8", SellerPortalPage()); return; }
                        if (path == "/api/products/publish" && method == "POST") { PublishProduct(Form(body)); Write(stream, 200, "text/plain", "OK|product\n"); return; }
                        if (path == "/api/promotions/publish" && method == "POST") { PublishPromotion(Form(body)); Write(stream, 200, "text/plain", "OK|promotion\n"); return; }
                        if (path == "/api/catalog" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", CatalogJson(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/orders/create" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CreateOrder(Form(body))); return; }
                        if (path == "/api/orders/pending" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", PendingOrders(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/orders/ack" && method == "POST") { Write(stream, 200, "text/plain", AckOrder(Form(body))); return; }
                        if (path == "/api/orders/status" && method == "POST") { Write(stream, 200, "text/plain", UpdateOrderStatus(Form(body))); return; }
                        if (path == "/api/orders/status" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", GetOrderStatus(QueryValue(query, "storeId"), QueryValue(query, "centralOrderId"))); return; }
                        if (path == "/api/orders/confirm" && method == "POST") { Write(stream, 200, "text/plain", ConfirmOrder(Form(body))); return; }
                        if (path == "/api/orders/history" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", HistoryOrders(QueryValue(query, "storeId"), QueryValue(query, "email"))); return; }
                        if (path == "/api/sync/heartbeat" && method == "POST") { Write(stream, 200, "text/plain", Heartbeat(Form(body))); return; }
                        if (path == "/api/licenses/public-key" && method == "GET") { Write(stream, 200, "application/xml; charset=utf-8", LicensePublicKey()); return; }
                        if (path == "/api/licenses/activate" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", LicenseActivate(Form(body))); return; }
                        if (path == "/api/licenses/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", LicenseStatus(QueryValue(query, "storeId"), QueryValue(query, "machineId"))); return; }
                        if (path == "/api/licenses/search" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", LicenseSearch(QueryValue(query, "storeId"), QueryValue(query, "machineId"))); return; }
                        if (path == "/api/licenses/upsert" && method == "POST") { Write(stream, 200, "text/plain", LicenseUpsert(Form(body))); return; }
                        if (path == "/api/licenses/revoke" && method == "POST") { Write(stream, 200, "text/plain", LicenseRevoke(Form(body))); return; }
                        if (path == "/" || path == "/stores") { Write(stream, 200, "text/html; charset=utf-8", Marketplace(query)); return; }
                        if (path.StartsWith("/store/", StringComparison.OrdinalIgnoreCase) && method == "GET") { string slug = path.Substring(7).Trim('/'); Write(stream, 200, "text/html; charset=utf-8", Storefront(slug)); return; }
                        Write(stream, 404, "text/plain", "Not found\n");
                    }
                }
                catch { }
            }
        }

        private static string S(XElement e, string name)
        {
            if (e == null) return "";
            XElement child = e.Element(name);
            return child == null ? "" : child.Value;
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            if (values == null || key == null) return "";
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static string Escape(string value)
        {
            return E(value);
        }

        private static string E(string value)
        {
            if (value == null) return "";
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static Dictionary<string, string> Form(string body)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (body ?? "").Split('&'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string[] pair = part.Split(new[] { '=' }, 2);
                string key = UrlDecode(pair[0]);
                string value = pair.Length > 1 ? UrlDecode(pair[1]) : "";
                if (!string.IsNullOrEmpty(key)) result[key] = value;
            }
            return result;
        }

        private static string UrlDecode(string value)
        {
            if (value == null) return "";
            return Uri.UnescapeDataString(value.Replace("+", " "));
        }

        private static string Body(string request)
        {
            if (string.IsNullOrEmpty(request)) return "";
            int marker = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return marker >= 0 ? request.Substring(marker + 4) : "";
        }

        private static string ReadRequest(NetworkStream stream)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int total = 0;
                int read;
                do
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                    total += read;
                    if (total >= 1024 * 1024) break;
                    string partial = Encoding.UTF8.GetString(ms.ToArray());
                    if (partial.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                    {
                        int contentLength = 0;
                        Match m = Regex.Match(partial, "(?im)^Content-Length:\\s*(\\d+)");
                        if (m.Success) int.TryParse(m.Groups[1].Value, out contentLength);
                        int headerEnd = partial.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                        int bodyBytes = partial.Length - headerEnd;
                        if (bodyBytes >= contentLength) break;
                    }
                } while (read > 0);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static void Write(NetworkStream stream, int status, string contentType, string body)
        {
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            string statusText = status == 200 ? "OK" : status == 404 ? "Not Found" : "Error";
            string header = "HTTP/1.1 " + status + " " + statusText + "\r\n" +
                            "Content-Type: " + contentType + "\r\n" +
                            "Content-Length: " + data.Length + "\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private void Register(Dictionary<string,string> f)
        {
            string id = Get(f, "storeId"); if (string.IsNullOrWhiteSpace(id)) return;
            lock (_sync)
            {
                XElement stores = _doc.Root.Element("Stores"); XElement old = stores.Elements("Store").FirstOrDefault(x => S(x, "StoreId") == id);
                XElement e = new XElement("Store", new XAttribute("UpdatedAt", Get(f, "updatedAt")),
                    new XElement("StoreId", id), new XElement("Name", Get(f, "name")), new XElement("LegalName", Get(f, "legalName")),
                    new XElement("Category", Get(f, "category")), new XElement("Address", Get(f, "address")), new XElement("City", Get(f, "city")),
                    new XElement("Province", Get(f, "province")), new XElement("Description", Get(f, "description")), new XElement("Logo", Get(f, "logo")),
                    new XElement("Slug", Get(f, "slug")), new XElement("PublicUrl", Get(f, "publicUrl")), new XElement("Active", Get(f, "active")),
                    new XElement("Delivery", Get(f, "delivery")), new XElement("Pickup", Get(f, "pickup")), new XElement("Latitude", Get(f, "latitude")), new XElement("Longitude", Get(f, "longitude")));
                if (old != null) old.ReplaceWith(e); else stores.Add(e); Save();
            }
        }


        private void EnsureCentralDataFiles()
        {
            lock (_sync)
            {
                RestoreIfMissing(_catalogFile, "data/nexomarket_catalog.xml");
                RestoreIfMissing(_ordersFile, "data/nexomarket_orders.xml");
                RestoreIfMissing(_licensesFile, "data/nexomarket_licenses.xml");
                RestoreIfMissing(_accountsFile, "data/nexomarket_accounts.xml");
                if (!File.Exists(_accountsFile)) File.WriteAllText(_accountsFile, new XDocument(new XElement("NexoMarketAccounts", new XElement("Accounts"))).ToString(SaveOptions.None), Encoding.UTF8);
                if (!File.Exists(_catalogFile)) File.WriteAllText(_catalogFile, new XDocument(new XElement("NexoMarketCatalog", new XElement("Products"), new XElement("Promotions"))).ToString(SaveOptions.None), Encoding.UTF8);
                if (!File.Exists(_ordersFile)) File.WriteAllText(_ordersFile, new XDocument(new XElement("NexoMarketOrders", new XElement("Orders"))).ToString(SaveOptions.None), Encoding.UTF8);
                if (!File.Exists(_licensesFile)) File.WriteAllText(_licensesFile, new XDocument(new XElement("NexoMarketLicenses", new XElement("Licenses"))).ToString(SaveOptions.None), Encoding.UTF8);
            }
        }

        private XDocument LoadFile(string file, string rootName, string childName)
        {
            try { if (File.Exists(file)) return XDocument.Load(file); } catch { }
            return new XDocument(new XElement(rootName, new XElement(childName)));
        }

        private static string A(XElement e, string name) { XAttribute a = e == null ? null : e.Attribute(name); return a == null ? "" : a.Value; }

        private void SaveDoc(string file, XDocument doc)
        {
            string temp = file + ".tmp";
            doc.Save(temp);
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
            if (_r2 != null && _r2.Enabled)
            {
                string key = "data/" + Path.GetFileName(file);
                _r2.PutText(key, File.ReadAllText(file, Encoding.UTF8));
            }
        }


        private string StorageStatus()
        {
            return _r2 != null && _r2.Enabled ? "OK|R2|enabled" : "ERROR|R2|not_configured";
        }

        private string UploadMedia(Dictionary<string,string> f)
        {
            if (_r2 == null || !_r2.Enabled) return "ERROR|R2_NOT_CONFIGURED";
            string mediaKey=Environment.GetEnvironmentVariable("MEDIA_UPLOAD_KEY")??"";
            string tokenStore, tokenEmail; bool tokenOk=SellerToken(Get(f,"token"),out tokenStore,out tokenEmail);
            if(mediaKey.Length>0 && !string.Equals(mediaKey,Get(f,"uploadKey"),StringComparison.Ordinal) && !tokenOk) return "ERROR|unauthorized";
            string storeId = Get(f, "storeId");
            if(tokenOk && !string.Equals(tokenStore,storeId,StringComparison.OrdinalIgnoreCase)) return "ERROR|store";
            string fileName = Path.GetFileName(Get(f, "fileName"));
            string contentType = Get(f, "contentType");
            string base64 = Get(f, "base64");
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(base64)) return "ERROR|missing";
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                if (bytes.Length > 8 * 1024 * 1024) return "ERROR|too_large";
                string safeName = Regex.Replace(fileName, "[^a-zA-Z0-9._-]", "_");
                string key = "stores/" + Regex.Replace(storeId, "[^a-zA-Z0-9_-]", "_") + "/media/" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + safeName;
                if (!_r2.PutBytes(key, bytes, contentType)) return "ERROR|upload";
                string url = _r2.PublicUrl(key);
                return "OK|" + key + "|" + url;
            }
            catch { return "ERROR|invalid_base64"; }
        }

        private string RecoverySend(Dictionary<string,string> f)
        {
            string relay=Environment.GetEnvironmentVariable("EMAIL_RELAY_KEY")??"";
            if(relay.Length>0 && !string.Equals(relay,Get(f,"relayKey"),StringComparison.Ordinal)) return "ERROR|unauthorized";
            string destination=Get(f,"email").Trim(); string code=Get(f,"code").Trim(); string name=Get(f,"name");
            if(destination.Length==0 || code.Length==0) return "ERROR|invalid";
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Accounts");XElement a=d.Root.Element("Accounts").Elements("Account").FirstOrDefault(x=>string.Equals(S(x,"Email"),destination,StringComparison.OrdinalIgnoreCase));if(a!=null){a.SetElementValue("RecoveryCode",code);a.SetElementValue("RecoveryExpires",DateTime.UtcNow.AddMinutes(10).ToString("o"));SaveDoc(_accountsFile,d);}}
            string smtpUser=Environment.GetEnvironmentVariable("SMTP_USER")??""; string smtpPassword=Environment.GetEnvironmentVariable("SMTP_APP_PASSWORD")??"";
            if(smtpUser.Length==0 || smtpPassword.Length==0) return "ERROR|email_not_configured";
            string host=Environment.GetEnvironmentVariable("SMTP_HOST"); if(string.IsNullOrWhiteSpace(host))host="smtp.gmail.com"; int port; if(!int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"),out port))port=587; bool ssl=(Environment.GetEnvironmentVariable("SMTP_SSL")??"1")=="1";
            try
            {
                using(MailMessage mail=new MailMessage()){mail.From=new MailAddress(smtpUser,"NexoMarket");mail.To.Add(destination);mail.Subject="NexoMarket · Código de recuperación";mail.Body="Hola "+name+",\r\n\r\nTu código de recuperación de NexoMarket es: "+code+"\r\n\r\nVence en 10 minutos. Si no solicitaste este cambio, ignorá este mensaje."; using(SmtpClient smtp=new SmtpClient(host,port)){smtp.EnableSsl=ssl;smtp.Credentials=new NetworkCredential(smtpUser,smtpPassword);smtp.Timeout=15000;smtp.Send(mail);}}
                return "OK|sent";
            }
            catch(Exception ex){return "ERROR|send|"+E(ex.Message);}
        }

        private string AccountRegister(Dictionary<string,string> f)
        {
            string email = Get(f, "email").Trim().ToLowerInvariant();
            string hash = Get(f, "passwordHash");
            string salt = Get(f, "salt");
            string role = Get(f, "role");
            string storeId = Get(f, "storeId");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(salt)) return "ERROR|invalid";
            lock (_sync)
            {
                XDocument d = LoadFile(_accountsFile, "NexoMarketAccounts", "Accounts");
                XElement root = d.Root.Element("Accounts");
                XElement old = root.Elements("Account").FirstOrDefault(x => string.Equals(S(x, "Email"), email, StringComparison.OrdinalIgnoreCase));
                if (old != null) return "ERROR|exists";
                root.Add(new XElement("Account",
                    new XElement("Email", email), new XElement("Name", Get(f, "name")), new XElement("Phone", Get(f, "phone")),
                    new XElement("Role", string.Equals(role, "seller", StringComparison.OrdinalIgnoreCase) ? "seller" : "buyer"),
                    new XElement("StoreId", storeId), new XElement("Salt", salt), new XElement("PasswordHash", hash),
                    new XElement("RecoveryCode", ""), new XElement("RecoveryExpires", ""),
                    new XElement("CreatedAt", DateTime.UtcNow.ToString("o"))));
                SaveDoc(_accountsFile, d);
                return "OK|registered";
            }
        }

        private string AccountLogin(Dictionary<string,string> f)
        {
            string email = Get(f, "email").Trim().ToLowerInvariant();
            string hash = Get(f, "passwordHash");
            string role = Get(f, "role");
            string storeId = Get(f, "storeId");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(hash)) return "ERROR|invalid";
            lock (_sync)
            {
                XDocument d = LoadFile(_accountsFile, "NexoMarketAccounts", "Accounts");
                XElement a = d.Root.Element("Accounts").Elements("Account").FirstOrDefault(x => string.Equals(S(x, "Email"), email, StringComparison.OrdinalIgnoreCase));
                if (a == null) return "ERROR|not_found";
                if (!string.Equals(S(a, "PasswordHash"), hash, StringComparison.Ordinal)) return "ERROR|password";
                if (!string.IsNullOrWhiteSpace(role) && !string.Equals(S(a, "Role"), role, StringComparison.OrdinalIgnoreCase)) return "ERROR|role";
                if (string.Equals(S(a, "Role"), "seller", StringComparison.OrdinalIgnoreCase) && !string.Equals(S(a, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase)) return "ERROR|store";
                return "OK|login|" + S(a, "Role") + "|" + S(a, "StoreId");
            }
        }

        private string LicenseAdminKey()
        {
            return Environment.GetEnvironmentVariable("LICENSE_ADMIN_KEY") ?? "";
        }

        private string LicensePublicKey()
        {
            return Environment.GetEnvironmentVariable("LICENSE_PUBLIC_KEY_XML") ?? "";
        }

        private bool IsLicenseAdmin(Dictionary<string,string> f)
        {
            string configured = LicenseAdminKey();
            return configured.Length > 0 && string.Equals(configured, Get(f, "adminKey"), StringComparison.Ordinal);
        }

        private string LicenseActivate(Dictionary<string,string> f)
        {
            string token = Get(f, "license");
            NexoMarket.Licensing.LicenseRecord r;
            if (!NexoMarket.Licensing.LicenseCore.TryParse(token, out r)) return "ERROR|license";
            string pub = LicensePublicKey();
            if (!NexoMarket.Licensing.LicenseCore.Verify(r, pub)) return "ERROR|signature";
            if (!string.Equals(NexoMarket.Licensing.LicenseCore.Status(r, DateTime.UtcNow), "Activa", StringComparison.OrdinalIgnoreCase)) return "ERROR|expired";
            if (string.IsNullOrWhiteSpace(r.StoreId) || string.IsNullOrWhiteSpace(r.MachineId)) return "ERROR|required";
            lock (_sync)
            {
                XDocument d = LoadFile(_licensesFile, "NexoMarketLicenses", "Licenses");
                XElement root = d.Root.Element("Licenses");
                XElement old = root.Elements("License").FirstOrDefault(x => S(x, "StoreId") == r.StoreId && S(x, "MachineId") == r.MachineId);
                XElement e = new XElement("License", new XElement("StoreId", r.StoreId), new XElement("MachineId", r.MachineId), new XElement("ClientName", r.ClientName), new XElement("Days", r.Days), new XElement("ExpiresUtc", r.ExpiresUtc.ToString("o")), new XElement("Status", r.Status), new XElement("Token", NexoMarket.Licensing.LicenseCore.ActivationCode(r)), new XElement("UpdatedAt", DateTime.UtcNow.ToString("o")));
                if (old != null) old.ReplaceWith(e); else root.Add(e);
                SaveDoc(_licensesFile, d);
            }
            return "OK|activated|" + r.StoreId + "|" + r.MachineId + "|" + r.ExpiresUtc.ToString("o");
        }

        private string LicenseStatus(string storeId, string machineId)
        {
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(machineId)) return "ERROR|missing";
            lock (_sync)
            {
                XDocument d = LoadFile(_licensesFile, "NexoMarketLicenses", "Licenses");
                XElement e = d.Root.Element("Licenses").Elements("License")
                    .FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "MachineId") == machineId);
                if (e == null) return "ERROR|notfound";
                string status = S(e, "Status");
                if (string.Equals(status, "Revoked", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase))
                    return "REVOKED|" + status;
                return S(e, "Token");
            }
        }

        private string LicenseSearch(string storeId, string machineId)
        {
            lock (_sync)
            {
                XDocument d = LoadFile(_licensesFile, "NexoMarketLicenses", "Licenses");
                IEnumerable<XElement> q = d.Root.Element("Licenses").Elements("License");
                if (!string.IsNullOrWhiteSpace(storeId)) q = q.Where(x => S(x, "StoreId").IndexOf(storeId, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(machineId)) q = q.Where(x => S(x, "MachineId").IndexOf(machineId, StringComparison.OrdinalIgnoreCase) >= 0);
                StringBuilder b = new StringBuilder();
                foreach (XElement x in q)
                {
                    b.Append("LICENSE|").Append(E(S(x,"StoreId"))).Append('|')
                     .Append(E(S(x,"MachineId"))).Append('|').Append(E(S(x,"ClientName"))).Append('|')
                     .Append(E(S(x,"Days"))).Append('|').Append(E(S(x,"ExpiresUtc"))).Append('|')
                     .Append(E(S(x,"Status"))).Append('|').Append(E(S(x,"UpdatedAt"))).Append('\n');
                }
                return b.ToString();
            }
        }

        private string LicenseUpsert(Dictionary<string,string> f)
        {
            if (!IsLicenseAdmin(f)) return "ERROR|unauthorized";
            string token = Get(f, "license");
            NexoMarket.Licensing.LicenseRecord r;
            if (!NexoMarket.Licensing.LicenseCore.TryParse(token, out r)) return "ERROR|license";
            string pub = LicensePublicKey();
            if (!NexoMarket.Licensing.LicenseCore.Verify(r, pub)) return "ERROR|signature";
            if (string.IsNullOrWhiteSpace(r.StoreId) || string.IsNullOrWhiteSpace(r.MachineId)) return "ERROR|required";
            lock (_sync)
            {
                XDocument d = LoadFile(_licensesFile, "NexoMarketLicenses", "Licenses");
                XElement root=d.Root.Element("Licenses");
                XElement old=root.Elements("License").FirstOrDefault(x => S(x,"StoreId")==r.StoreId && S(x,"MachineId")==r.MachineId);
                XElement e=new XElement("License",
                    new XElement("StoreId",r.StoreId),new XElement("MachineId",r.MachineId),new XElement("ClientName",r.ClientName),
                    new XElement("Days",r.Days.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("ExpiresUtc",r.ExpiresUtc.ToString("o")),new XElement("Status","Active"),
                    new XElement("Token",token),new XElement("UpdatedAt",DateTime.UtcNow.ToString("o")));
                if(old!=null)old.ReplaceWith(e);else root.Add(e);
                SaveDoc(_licensesFile,d);
            }
            return "OK|registered";
        }

        private string LicenseRevoke(Dictionary<string,string> f)
        {
            if (!IsLicenseAdmin(f)) return "ERROR|unauthorized";
            string storeId=Get(f,"storeId"), machineId=Get(f,"machineId");
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(machineId)) return "ERROR|missing";
            lock (_sync)
            {
                XDocument d=LoadFile(_licensesFile,"NexoMarketLicenses","Licenses");
                XElement e=d.Root.Element("Licenses").Elements("License").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"MachineId")==machineId);
                if(e==null)return "ERROR|notfound";
                e.SetElementValue("Status","Revoked");e.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));SaveDoc(_licensesFile,d);
            }
            return "OK|revoked";
        }

        private void PublishProduct(Dictionary<string,string> f)
        {
            string storeId = Get(f, "storeId"), productId = Get(f, "productId");
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(productId)) return;
            lock (_sync)
            {
                XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products"); XElement root = d.Root; if (root.Element("Products") == null) root.Add(new XElement("Products"));
                XElement old = root.Element("Products").Elements("Product").FirstOrDefault(x => S(x,"StoreId")==storeId && S(x,"ProductId")==productId);
                XElement e = new XElement("Product", new XElement("StoreId",storeId), new XElement("ProductId",productId),
                    new XElement("Name",Get(f,"name")),new XElement("Category",Get(f,"category")),new XElement("Description",Get(f,"description")),
                    new XElement("Price",Get(f,"price")),new XElement("SalePrice",Get(f,"salePrice")),new XElement("Stock",Get(f,"stock")),
                    new XElement("MinimumStock",Get(f,"minimumStock")),new XElement("SKU",Get(f,"sku")),new XElement("Brand",Get(f,"brand")),
                    new XElement("Size",Get(f,"size")),new XElement("Color",Get(f,"color")),new XElement("Active",Get(f,"active")),
                    new XElement("OnlineEnabled",Get(f,"onlineEnabled")),new XElement("ImagePath",Get(f,"imagePath")),new XElement("Slug",Get(f,"slug")),
                    new XElement("PublicDescription",Get(f,"publicDescription")),new XElement("UpdatedAt",Get(f,"updatedAt")));
                if(old!=null) old.ReplaceWith(e); else root.Element("Products").Add(e); SaveDoc(_catalogFile,d);
            }
        }

        private void PublishPromotion(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"), promotionId=Get(f,"promotionId"); if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(promotionId)) return;
            lock(_sync){ XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Promotions"); if(d.Root.Element("Promotions")==null)d.Root.Add(new XElement("Promotions")); XElement old=d.Root.Element("Promotions").Elements("Promotion").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"PromotionId")==promotionId);
                XElement e=new XElement("Promotion",new XElement("StoreId",storeId),new XElement("PromotionId",promotionId),new XElement("Name",Get(f,"name")),new XElement("ProductIds",Get(f,"productIds")),new XElement("PromotionalPrice",Get(f,"promotionalPrice")),new XElement("Active",Get(f,"active")),new XElement("From",Get(f,"from")),new XElement("To",Get(f,"to")),new XElement("UpdatedAt",Get(f,"updatedAt")));
                if(old!=null)old.ReplaceWith(e);else d.Root.Element("Promotions").Add(e);SaveDoc(_catalogFile,d); }
        }

        private string CatalogJson(string storeId)
        {
            if(string.IsNullOrWhiteSpace(storeId)) return "{\"products\":[],\"promotions\":[]}";
            lock(_sync){ XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products"); StringBuilder b=new StringBuilder(); b.Append("{\"storeId\":").Append(JsonString(storeId)).Append(",\"products\":[");
                List<XElement> ps=d.Root.Element("Products")==null?new List<XElement>():d.Root.Element("Products").Elements("Product").Where(x=>S(x,"StoreId")==storeId&&S(x,"OnlineEnabled")!="0"&&S(x,"Active")!="0").ToList();
                for(int i=0;i<ps.Count;i++){if(i>0)b.Append(',');XElement x=ps[i];b.Append("{\"productId\":").Append(JsonString(S(x,"ProductId"))).Append(",\"name\":").Append(JsonString(S(x,"Name"))).Append(",\"category\":").Append(JsonString(S(x,"Category"))).Append(",\"price\":").Append(JsonString(S(x,"Price"))).Append(",\"salePrice\":").Append(JsonString(S(x,"SalePrice"))).Append(",\"stock\":").Append(JsonString(S(x,"Stock"))).Append(",\"sku\":").Append(JsonString(S(x,"SKU"))).Append(",\"brand\":").Append(JsonString(S(x,"Brand"))).Append(",\"size\":").Append(JsonString(S(x,"Size"))).Append(",\"color\":").Append(JsonString(S(x,"Color"))).Append(",\"image\":").Append(JsonString(S(x,"ImagePath"))).Append(",\"description\":").Append(JsonString(S(x,"PublicDescription"))).Append('}');}
                b.Append("],\"promotions\":["); List<XElement> pr=d.Root.Element("Promotions")==null?new List<XElement>():d.Root.Element("Promotions").Elements("Promotion").Where(x=>S(x,"StoreId")==storeId&&S(x,"Active")!="0").ToList();
                for(int i=0;i<pr.Count;i++){if(i>0)b.Append(',');XElement x=pr[i];b.Append("{\"promotionId\":").Append(JsonString(S(x,"PromotionId"))).Append(",\"name\":").Append(JsonString(S(x,"Name"))).Append(",\"productIds\":").Append(JsonString(S(x,"ProductIds"))).Append(",\"price\":").Append(JsonString(S(x,"PromotionalPrice"))).Append(",\"from\":").Append(JsonString(S(x,"From"))).Append(",\"to\":").Append(JsonString(S(x,"To"))).Append('}');}
                b.Append("]}"); return b.ToString(); }
        }

        private string CreateOrder(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"); if(string.IsNullOrWhiteSpace(storeId)) return "ERROR|storeId";
            lock(_sync)
            {
                XElement store=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==storeId);
                if(store==null || S(store,"Active")!="1") return "ERROR|store";
            }
            string itemsJson = Get(f,"itemsJson");
            decimal subtotal = CalculateItemsTotal(storeId, itemsJson);
            if (subtotal <= 0m) return "ERROR|total";
            string couponCode = Get(f, "couponCode").Trim().ToUpperInvariant();
            decimal couponDiscount = 0m;
            string couponError = ValidateCouponForOrder(storeId, couponCode, subtotal, out couponDiscount);
            if (!string.IsNullOrWhiteSpace(couponError)) return couponError;
            decimal shipping = 0m; decimal.TryParse(Get(f,"shippingCost"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out shipping);
            decimal total = Math.Max(0m, subtotal + shipping - couponDiscount);
            string stockError = ValidateAndReserveStock(storeId, itemsJson);
            if (!string.IsNullOrWhiteSpace(stockError)) return stockError;
            string centralId=Guid.NewGuid().ToString("N"); string now=DateTime.UtcNow.ToString("o");
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement e=new XElement("Order",new XElement("CentralOrderId",centralId),new XElement("StoreId",storeId),new XElement("CustomerId",Get(f,"customerId")),new XElement("CustomerName",Get(f,"customerName")),new XElement("CustomerEmail",Get(f,"customerEmail")),new XElement("Phone",Get(f,"phone")),new XElement("Fulfillment",Get(f,"fulfillment")),new XElement("Address",Get(f,"address")),new XElement("Notes",Get(f,"notes")),new XElement("Status",string.IsNullOrWhiteSpace(Get(f,"status"))?"Pendiente":Get(f,"status")),new XElement("Total",total.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("PaymentMethod",Get(f,"paymentMethod")),new XElement("PaymentStatus",string.IsNullOrWhiteSpace(Get(f,"paymentStatus"))?"Pendiente":Get(f,"paymentStatus")),new XElement("PaymentReference",Get(f,"paymentReference")),new XElement("PaymentProofPath",Get(f,"paymentProofPath")),new XElement("ShippingCost",Get(f,"shippingCost")),new XElement("TrackingNumber",Get(f,"trackingNumber")),new XElement("Carrier",Get(f,"carrier")),new XElement("ItemsJson",Get(f,"itemsJson")),new XElement("CouponCode",couponCode),new XElement("CouponDiscount",couponDiscount.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("Subtotal",subtotal.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("BuyerMessage",Get(f,"buyerMessage")),new XElement("CreatedAt",now),new XElement("Ack", "0")); d.Root.Element("Orders").Add(e);SaveDoc(_ordersFile,d);}
            if(!string.IsNullOrWhiteSpace(couponCode)){ lock(_sync){ XDocument cd=LoadFile(_ordersFile,"NexoMarketOrders","Coupons"); XElement cr=cd.Root.Element("Coupons"); if(cr!=null){ XElement cc=cr.Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"Code").Equals(couponCode,StringComparison.OrdinalIgnoreCase)); if(cc!=null){int used;int.TryParse(S(cc,"Used"),out used);cc.SetElementValue("Used",(used+1).ToString());SaveDoc(_ordersFile,cd);}}}}
            return "OK|"+centralId+"|"+now;
        }


        private string ValidateAndReserveStock(string storeId, string itemsJson)
        {
            if (string.IsNullOrWhiteSpace(itemsJson)) return "";
            Dictionary<string,int> requested = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(itemsJson, @"""id""\s*:\s*""([^""]+)""[^}]*?""qty""\s*:\s*(\d+)", RegexOptions.IgnoreCase))
            {
                string id=m.Groups[1].Value;
                if(id.StartsWith("promo:",StringComparison.OrdinalIgnoreCase)) continue;
                int qty; if(!int.TryParse(m.Groups[2].Value,out qty)||qty<1)continue;
                if(requested.ContainsKey(id))requested[id]+=qty;else requested[id]=qty;
            }
            if(requested.Count==0)return "";
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                XElement products=d.Root.Element("Products");
                foreach(KeyValuePair<string,int> pair in requested)
                {
                    XElement p=products==null?null:products.Elements("Product").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"ProductId")==pair.Key);
                    if(p==null)continue;
                    int stock; if(!int.TryParse(S(p,"Stock"),out stock))stock=0;
                    if(stock<pair.Value)return "ERROR|stock|"+S(p,"Name")+"|"+stock;
                }
                foreach(KeyValuePair<string,int> pair in requested)
                {
                    XElement p=products.Elements("Product").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"ProductId")==pair.Key);
                    if(p==null)continue;
                    int stock; if(!int.TryParse(S(p,"Stock"),out stock))stock=0;
                    p.SetElementValue("Stock",Math.Max(0,stock-pair.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    p.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                }
                SaveDoc(_catalogFile,d);
            }
            return "";
        }

        private string PendingOrders(string storeId)
        {
            if(string.IsNullOrWhiteSpace(storeId)) return "[]";
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");List<XElement> list=d.Root.Element("Orders").Elements("Order").Where(x=>S(x,"StoreId")==storeId&&S(x,"Ack")!="1").ToList();StringBuilder b=new StringBuilder("[");for(int i=0;i<list.Count;i++){if(i>0)b.Append(',');XElement x=list[i];b.Append("{\"centralOrderId\":").Append(JsonString(S(x,"CentralOrderId"))).Append(",\"customerId\":").Append(JsonString(S(x,"CustomerId"))).Append(",\"customerName\":").Append(JsonString(S(x,"CustomerName"))).Append(",\"customerEmail\":").Append(JsonString(S(x,"CustomerEmail"))).Append(",\"phone\":").Append(JsonString(S(x,"Phone"))).Append(",\"fulfillment\":").Append(JsonString(S(x,"Fulfillment"))).Append(",\"address\":").Append(JsonString(S(x,"Address"))).Append(",\"notes\":").Append(JsonString(S(x,"Notes"))).Append(",\"status\":").Append(JsonString(S(x,"Status"))).Append(",\"total\":").Append(JsonString(S(x,"Total"))).Append(",\"paymentMethod\":").Append(JsonString(S(x,"PaymentMethod"))).Append(",\"paymentStatus\":").Append(JsonString(S(x,"PaymentStatus"))).Append(",\"paymentReference\":").Append(JsonString(S(x,"PaymentReference"))).Append(",\"paymentProofPath\":").Append(JsonString(S(x,"PaymentProofPath"))).Append(",\"shippingCost\":").Append(JsonString(S(x,"ShippingCost"))).Append(",\"trackingNumber\":").Append(JsonString(S(x,"TrackingNumber"))).Append(",\"carrier\":").Append(JsonString(S(x,"Carrier"))).Append(",\"itemsJson\":").Append(JsonString(S(x,"ItemsJson"))).Append(",\"buyerMessage\":").Append(JsonString(S(x,"BuyerMessage"))).Append(",\"createdAt\":").Append(JsonString(S(x,"CreatedAt"))).Append('}');}b.Append(']');return b.ToString();}
        }

        private string AckOrder(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"), id=Get(f,"centralOrderId"); if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(id))return "ERROR|missing";
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement e=d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"CentralOrderId")==id);if(e==null)return "ERROR|notfound";e.SetElementValue("Ack","1");e.SetElementValue("AckAt",DateTime.UtcNow.ToString("o"));SaveDoc(_ordersFile,d);}return "OK|ack";
        }

        private string UpdateOrderStatus(Dictionary<string,string> f)
        {
            string storeId = Get(f, "storeId"); string id = Get(f, "centralOrderId"); string status = Get(f, "status");
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(status)) return "ERROR|missing";
            string[] allowed = { "Pendiente", "Preparando", "Listo", "Enviado", "En reparto", "Entregado", "Rechazado", "Cancelado" };
            if (!allowed.Contains(status)) return "ERROR|status";
            lock (_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                XElement e = d.Root.Element("Orders").Elements("Order").FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "CentralOrderId") == id);
                if (e == null) return "ERROR|notfound";
                e.SetElementValue("Status", status);
                e.SetElementValue("Carrier", Get(f, "carrier"));
                e.SetElementValue("TrackingNumber", Get(f, "trackingNumber"));
                e.SetElementValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                SaveDoc(_ordersFile, d);
            }
            return "OK|status";
        }


        private string GetOrderStatus(string storeId, string id)
        {
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(id)) return "{\"error\":\"missing\"}";
            lock (_sync)
            {
                XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");
                XElement e=d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"CentralOrderId")==id);
                if(e==null)return "{\"error\":\"notfound\"}";
                return "{\"centralOrderId\":"+JsonString(S(e,"CentralOrderId"))+",\"status\":"+JsonString(S(e,"Status"))+",\"total\":"+JsonString(S(e,"Total"))+",\"buyerConfirmed\":"+JsonString(S(e,"BuyerConfirmedAt"))+",\"updatedAt\":"+JsonString(S(e,"UpdatedAt"))+"}";
            }
        }

        private string ConfirmOrder(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"), id=Get(f,"centralOrderId"), email=Get(f,"email");
            if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(id))return "ERROR|missing";
            lock(_sync)
            {
                XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");
                XElement e=d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"CentralOrderId")==id);
                if(e==null)return "ERROR|notfound";
                string owner=S(e,"CustomerEmail");
                if(!string.IsNullOrWhiteSpace(owner)&&!string.Equals(owner,email,StringComparison.OrdinalIgnoreCase))return "ERROR|email";
                e.SetElementValue("BuyerConfirmedAt",DateTime.UtcNow.ToString("o"));
                e.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                SaveDoc(_ordersFile,d);
            }
            return "OK|confirmed";
        }

        private string HistoryOrders(string storeId, string email)
        {
            if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(email))return "[]";
            lock(_sync)
            {
                XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");
                List<XElement> list=d.Root.Element("Orders").Elements("Order")
                    .Where(x=>S(x,"StoreId")==storeId&&string.Equals(S(x,"CustomerEmail"),email,StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x=>S(x,"CreatedAt")).Take(50).ToList();
                StringBuilder b=new StringBuilder("[");
                for(int i=0;i<list.Count;i++)
                {
                    if(i>0)b.Append(',');
                    XElement x=list[i];
                    b.Append("{\"centralOrderId\":").Append(JsonString(S(x,"CentralOrderId")))
                     .Append(",\"status\":").Append(JsonString(S(x,"Status")))
                     .Append(",\"total\":").Append(JsonString(S(x,"Total")))
                     .Append(",\"createdAt\":").Append(JsonString(S(x,"CreatedAt")))
                     .Append(",\"buyerConfirmed\":").Append(JsonString(S(x,"BuyerConfirmedAt"))).Append('}');
                }
                return b.Append(']').ToString();
            }
        }

        private string RecoveryReset(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(); string code=Get(f,"code").Trim(); string newHash=Get(f,"passwordHash"); string salt=Get(f,"salt");
            if(email.Length==0||code.Length==0||newHash.Length==0||salt.Length==0)return "ERROR|invalid";
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Accounts"); XElement a=d.Root.Element("Accounts").Elements("Account").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase)); if(a==null)return "ERROR|notfound"; if(S(a,"RecoveryCode")!=code)return "ERROR|code"; DateTime exp; if(!DateTime.TryParse(S(a,"RecoveryExpires"),out exp)||exp<DateTime.UtcNow)return "ERROR|expired"; a.SetElementValue("Salt",salt);a.SetElementValue("PasswordHash",newHash);a.SetElementValue("RecoveryCode","");a.SetElementValue("RecoveryExpires","");SaveDoc(_accountsFile,d);return "OK|reset";}
        }

        private string AccountLoginPlain(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); if(email.Length==0||password.Length==0)return "ERROR|invalid"; lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Accounts"); XElement a=d.Root.Element("Accounts").Elements("Account").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase)); if(a==null)return "ERROR|not_found"; if(HashPasswordWeb(password,S(a,"Salt"))!=S(a,"PasswordHash"))return "ERROR|password"; return "OK|login|"+S(a,"Role")+"|"+S(a,"StoreId");}
        }

        private string RecoveryRequest(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(); if(email.Length==0)return "ERROR|invalid"; string code=new Random().Next(100000,999999).ToString(); string name=""; lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Accounts");XElement a=d.Root.Element("Accounts").Elements("Account").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase));if(a==null)return "ERROR|notfound";name=S(a,"Name");a.SetElementValue("RecoveryCode",code);a.SetElementValue("RecoveryExpires",DateTime.UtcNow.AddMinutes(10).ToString("o"));SaveDoc(_accountsFile,d);} Dictionary<string,string> send=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"email",email},{"code",code},{"name",name}};return RecoverySend(send);
        }

        private string SellerLogin(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); if(email.Length==0||password.Length==0)return "ERROR|invalid";
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Accounts"); XElement a=d.Root.Element("Accounts").Elements("Account").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase)); if(a==null)return "ERROR|not_found"; if(S(a,"Role")!="seller")return "ERROR|role"; string salt=S(a,"Salt"); string hash=HashPasswordWeb(password,salt); if(S(a,"PasswordHash")!=hash)return "ERROR|password"; string storeId=S(a,"StoreId"); if(storeId.Length==0)return "ERROR|store"; string token=Guid.NewGuid().ToString("N"); _sellerSessions[token]=storeId+"|"+email; return "OK|"+token+"|"+storeId;}
        }

        private static string HashPasswordWeb(string password,string saltBase64)
        {
            try { byte[] salt=Convert.FromBase64String(saltBase64); using(var kdf=new Rfc2898DeriveBytes(password??"",salt,50000)){return Convert.ToBase64String(kdf.GetBytes(32));} } catch { return ""; }
        }

        private bool SellerToken(string token, out string storeId, out string email)
        {
            storeId="";email="";if(string.IsNullOrWhiteSpace(token))return false;lock(_sync){string value; if(!_sellerSessions.TryGetValue(token,out value))return false; string[] p=value.Split('|'); if(p.Length<2)return false;storeId=p[0];email=p[1];return true;}
        }

        private string SellerProducts(string token)
        {
            string storeId,email;if(!SellerToken(token,out storeId,out email))return "{\"error\":\"unauthorized\"}";
            lock(_sync){XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");var ps=d.Root.Element("Products")==null?new List<XElement>():d.Root.Element("Products").Elements("Product").Where(x=>S(x,"StoreId")==storeId).ToList();StringBuilder b=new StringBuilder("[" );for(int i=0;i<ps.Count;i++){if(i>0)b.Append(',');XElement x=ps[i];b.Append("{\"productId\":").Append(JsonString(S(x,"ProductId"))).Append(",\"name\":").Append(JsonString(S(x,"Name"))).Append(",\"price\":").Append(JsonString(S(x,"Price"))).Append(",\"salePrice\":").Append(JsonString(S(x,"SalePrice"))).Append(",\"stock\":").Append(JsonString(S(x,"Stock"))).Append(",\"image\":").Append(JsonString(S(x,"ImagePath"))).Append(",\"onlineEnabled\":").Append(S(x,"OnlineEnabled")=="0"?"false":"true").Append('}');}return b.Append(']').ToString();}
        }

        private string SellerProductSave(Dictionary<string,string> f)
        {
            string storeId,email;if(!SellerToken(Get(f,"token"),out storeId,out email))return "ERROR|unauthorized";string productId=Get(f,"productId"); if(productId.Length==0)productId=Guid.NewGuid().ToString("N");
            PublishProduct(new Dictionary<string,string>(f,StringComparer.OrdinalIgnoreCase){{"storeId",storeId},{"productId",productId}});return "OK|product|"+productId;
        }

        private string CouponList(string storeId)
        {
            if(string.IsNullOrWhiteSpace(storeId))return "[]";lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Coupons");var root=d.Root.Element("Coupons");if(root==null)return "[]";StringBuilder b=new StringBuilder("[");int i=0;foreach(XElement c in root.Elements("Coupon").Where(x=>S(x,"StoreId")==storeId)){if(i++>0)b.Append(',');b.Append("{\"code\":").Append(JsonString(S(c,"Code"))).Append(",\"description\":").Append(JsonString(S(c,"Description"))).Append(",\"percent\":").Append(JsonString(S(c,"DiscountPercent"))).Append(",\"amount\":").Append(JsonString(S(c,"DiscountAmount"))).Append(",\"used\":").Append(JsonString(S(c,"Used"))).Append(",\"maxUses\":").Append(JsonString(S(c,"MaxUses"))).Append(",\"active\":").Append(S(c,"Active")=="0"?"false":"true").Append('}');}return b.Append(']').ToString();}
        }

        private string CouponUpsert(Dictionary<string,string> f)
        {
            string storeId,email;if(!SellerToken(Get(f,"token"),out storeId,out email))return "ERROR|unauthorized";string code=Get(f,"code").Trim().ToUpperInvariant();if(code.Length<3)return "ERROR|code";
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Coupons");if(d.Root.Element("Coupons")==null)d.Root.Add(new XElement("Coupons"));XElement root=d.Root.Element("Coupons");XElement c=root.Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"Code").Equals(code,StringComparison.OrdinalIgnoreCase));if(c==null){c=new XElement("Coupon",new XElement("StoreId",storeId),new XElement("Code",code));root.Add(c);}c.SetElementValue("Description",Get(f,"description"));c.SetElementValue("DiscountPercent",Get(f,"discountPercent"));c.SetElementValue("DiscountAmount",Get(f,"discountAmount"));c.SetElementValue("MaxUses",Get(f,"maxUses"));c.SetElementValue("Used",S(c,"Used").Length==0?"0":S(c,"Used"));c.SetElementValue("Active",Get(f,"active")=="0"?"0":"1");c.SetElementValue("From",Get(f,"from"));c.SetElementValue("To",Get(f,"to"));c.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));SaveDoc(_ordersFile,d);return "OK|coupon";}
        }

        private string ValidateCoupon(Dictionary<string,string> f)
        {
            decimal subtotal; if(!decimal.TryParse(Get(f,"subtotal"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out subtotal)||subtotal<0)return "ERROR|subtotal";decimal discount;string err=ValidateCouponForOrder(Get(f,"storeId"),Get(f,"code").Trim().ToUpperInvariant(),subtotal,out discount);if(err.Length>0)return err;return "OK|"+discount.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|"+(subtotal-discount).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private string ValidateCouponForOrder(string storeId,string code,decimal subtotal,out decimal discount)
        {
            discount=0m;if(string.IsNullOrWhiteSpace(code))return "";lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Coupons");XElement root=d.Root.Element("Coupons");XElement c=root==null?null:root.Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"Code").Equals(code,StringComparison.OrdinalIgnoreCase));if(c==null)return "ERROR|coupon|notfound";if(S(c,"Active")=="0")return "ERROR|coupon|inactive";DateTime from,to;DateTime.TryParse(S(c,"From"),out from);DateTime.TryParse(S(c,"To"),out to);DateTime now=DateTime.UtcNow;if(from!=DateTime.MinValue&&now<from.ToUniversalTime())return "ERROR|coupon|notstarted";if(to!=DateTime.MinValue&&now>to.ToUniversalTime())return "ERROR|coupon|expired";int max,used;int.TryParse(S(c,"MaxUses"),out max);int.TryParse(S(c,"Used"),out used);if(max>0&&used>=max)return "ERROR|coupon|limit";decimal pct,amount;decimal.TryParse(S(c,"DiscountPercent"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out pct);decimal.TryParse(S(c,"DiscountAmount"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out amount);discount=pct>0m?subtotal*pct/100m:amount;if(discount>subtotal)discount=subtotal;return "";}
        }

        private decimal CalculateItemsTotal(string storeId,string itemsJson)
        {
            decimal total=0m;if(string.IsNullOrWhiteSpace(itemsJson))return total;lock(_sync){XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");var products=d.Root.Element("Products");XDocument pd=LoadFile(_catalogFile,"NexoMarketCatalog","Promotions");var promos=pd.Root.Element("Promotions");foreach(Match m in Regex.Matches(itemsJson,@"\"id\"\s*:\s*\"([^\"]+)\"[^}]*?\"qty\"\s*:\s*(\d+)",RegexOptions.IgnoreCase)){string id=m.Groups[1].Value;int qty;if(!int.TryParse(m.Groups[2].Value,out qty)||qty<1)continue;if(id.StartsWith("promo:",StringComparison.OrdinalIgnoreCase)){string pid=id.Substring(6);XElement pr=promos==null?null:promos.Elements("Promotion").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"PromotionId")==pid);decimal pp;decimal.TryParse(pr==null?"0":S(pr,"PromotionalPrice"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out pp);total+=pp*qty;}else{XElement p=products==null?null:products.Elements("Product").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"ProductId")==id);if(p==null)continue;decimal price,sale;decimal.TryParse(S(p,"Price"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out price);decimal.TryParse(S(p,"SalePrice"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out sale);if(sale>0m&&sale<price)price=sale;total+=price*qty;}}}return total;
        }

        private string PublicLoginPage()
        {
            return "<!doctype html><html lang='es'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>NexoMarket · Ingresar</title><style>body{font-family:Arial;background:#080b10;color:#fff;padding:30px}.card{max-width:440px;margin:7vh auto;background:#101720;border:1px solid #26384e;border-radius:18px;padding:26px}input,button{width:100%;box-sizing:border-box;padding:12px;margin:6px 0;border-radius:9px;border:1px solid #2a3b51;background:#0d141d;color:#fff}button{background:#39ff66;color:#061009;font-weight:900;cursor:pointer}a{color:#39ff66}.muted{color:#93a1b1}</style></head><body><div class='card'><h1>NEXO MARKET</h1><h2>Ingresar</h2><input id='email' type='email' placeholder='Correo electrónico'><input id='password' type='password' placeholder='Contraseña'><button onclick='login()'>INGRESAR</button><button onclick='recover()' style='background:#172231;color:#fff'>RECUPERAR CONTRASEÑA</button><div id='m' class='muted'></div></div><script>function login(){var b='email='+encodeURIComponent(document.getElementById('email').value)+'&password='+encodeURIComponent(document.getElementById('password').value);var x=new XMLHttpRequest();x.open('POST','/api/accounts/login_plain',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.responseText.indexOf('OK|login|seller|')===0){localStorage.setItem('nexoStoreId',x.responseText.split('|')[3]);location='/seller';}else if(x.responseText.indexOf('OK|login|')===0){document.getElementById('m').innerText='Ingreso correcto. Rol: '+x.responseText.split('|')[2];}else document.getElementById('m').innerText='No se pudo ingresar: '+x.responseText;}};x.send(b);}function recover(){var email=document.getElementById('email').value;if(!email){alert('Ingresá tu correo.');return;}var x=new XMLHttpRequest();x.open('POST','/api/recovery/request',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4)document.getElementById('m').innerText=x.responseText.indexOf('OK|')===0?'Código enviado. Revisá tu correo.':x.responseText;};x.send('email='+encodeURIComponent(email));}</script></body></html>";
        }

        private string SellerLoginPage()
        {
            return "<!doctype html><html lang='es'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>NexoMarket · Ingresar vendedor</title><style>body{font-family:Arial;background:#080b10;color:#fff;padding:30px}.card{max-width:430px;margin:8vh auto;background:#101720;border:1px solid #26384e;border-radius:18px;padding:26px}input,button{box-sizing:border-box;width:100%;padding:12px;margin:7px 0;border-radius:9px;border:1px solid #2a3b51;background:#0d141d;color:#fff}button{background:#39ff66;color:#061009;font-weight:900;cursor:pointer}</style></head><body><div class='card'><h1>NEXO MARKET</h1><h2>Panel de vendedores</h2><input id='email' type='email' placeholder='Correo'><input id='password' type='password' placeholder='Contraseña'><button onclick='login()'>INGRESAR</button><div id='m'></div><p><a href='/login' style='color:#39ff66'>Volver al login</a></p></div><script>function login(){var b='email='+encodeURIComponent(document.getElementById('email').value)+'&password='+encodeURIComponent(document.getElementById('password').value);var x=new XMLHttpRequest();x.open('POST','/api/seller/login',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.responseText.indexOf('OK|')===0){localStorage.setItem('nexoSellerToken',x.responseText.split('|')[1]);localStorage.setItem('nexoStoreId',x.responseText.split('|')[2]);location='/seller';}else document.getElementById('m').innerText='No se pudo ingresar: '+x.responseText;}};x.send(b);}</script></body></html>";
        }

        private string SellerPortalPage()
        {
            return "<!doctype html><html lang='es'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>NexoMarket · Seller Center</title><style>body{font-family:Arial;background:#080b10;color:#fff;margin:0}.wrap{max-width:1200px;margin:auto;padding:22px}.nav{display:flex;gap:8px;flex-wrap:wrap}.nav button{background:#111823;color:#fff;border:1px solid #26384e;border-radius:9px;padding:10px 14px;cursor:pointer}.panel{background:#101720;border:1px solid #26384e;border-radius:16px;padding:18px;margin-top:15px}input,button,select{padding:10px;border-radius:8px;border:1px solid #2a3b51;background:#0d141d;color:#fff;margin:4px}button.primary{background:#39ff66;color:#061009;font-weight:900}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}.item{padding:12px;border:1px solid #26384e;border-radius:12px}.muted{color:#93a1b1}.hide{display:none}</style></head><body><div class='wrap'><h1>NEXO MARKET · SELLER CENTER</h1><div id='store' class='muted'></div><div class='nav'><button onclick='show("dash")'>Inicio</button><button onclick='show("products")'>Productos</button><button onclick='show("coupons")'>Cupones</button><button onclick='show("orders")'>Pedidos</button><button onclick='show("license")'>Licencia</button><button onclick='logout()'>Salir</button></div><section id='dash' class='panel'><h2>Panel central</h2><p>Este panel está alojado en NexoMarket Cloud. La PC del vendedor puede estar apagada.</p></section><section id='products' class='panel hide'><h2>Productos</h2><div id='plist'>Cargando...</div><h3>Nuevo / actualizar</h3><input id='pid' placeholder='Product ID (vacío = nuevo)'><input id='pname' placeholder='Nombre'><input id='pprice' placeholder='Precio' type='number'><input id='psale' placeholder='Precio oferta' type='number'><input id='pstock' placeholder='Stock' type='number'><input id='psku' placeholder='SKU'><input id='pimage' type='file' accept='image/*' capture='environment'><label><input id='ponline' type='checkbox' checked> Publicar</label><button class='primary' onclick='saveProduct()'>GUARDAR PRODUCTO</button><div id='pm'></div></section><section id='coupons' class='panel hide'><h2>Cupones</h2><div id='clist'>Cargando...</div><input id='ccode' placeholder='Código'><input id='cdesc' placeholder='Descripción'><input id='cpct' placeholder='% descuento' type='number'><input id='camt' placeholder='Importe fijo' type='number'><input id='cmax' placeholder='Máximo de usos (0 ilimitado)' type='number'><button class='primary' onclick='saveCoupon()'>GUARDAR CUPÓN</button><div id='cm'></div></section><section id='orders' class='panel hide'><h2>Pedidos</h2><div id='olist'>Los pedidos se consultan al servidor central.</div></section><section id='license' class='panel hide'><h2>Licencia</h2><input id='lcode' placeholder='Código NexoMarket'><button class='primary' onclick='activate()'>ACTIVAR CÓDIGO</button><div id='lm'></div></section></div><script>var token=localStorage.getItem('nexoSellerToken')||'',storeId=localStorage.getItem('nexoStoreId')||'';if(!token){location='/seller/login';}document.getElementById('store').innerText='Store ID: '+storeId;function show(id){['dash','products','coupons','orders','license'].forEach(function(x){document.getElementById(x).className=x===id?'panel':'panel hide';});if(id==='products')loadProducts();if(id==='coupons')loadCoupons();if(id==='orders')loadOrders();}function logout(){localStorage.removeItem('nexoSellerToken');localStorage.removeItem('nexoStoreId');location='/seller/login';}function post(url,data,cb){var x=new XMLHttpRequest();x.open('POST',url,true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4)cb(x.responseText);};x.send(data);}function loadProducts(){var x=new XMLHttpRequest();x.open('GET','/api/seller/products?token='+encodeURIComponent(token),true);x.onreadystatechange=function(){if(x.readyState===4){try{var a=JSON.parse(x.responseText),h='';a.forEach(function(p){h+='<div class=item><b>'+p.name+'</b><br>$ '+p.price+' · Stock '+p.stock+'<br>'+p.image+'</div>';});document.getElementById('plist').innerHTML=h||'Sin productos.';}catch(e){document.getElementById('plist').innerText='No autorizado.';}}};x.send();}function saveProduct(){var f=document.getElementById('pimage').files[0];var send=function(img){var d='token='+encodeURIComponent(token)+'&productId='+encodeURIComponent(document.getElementById('pid').value)+'&name='+encodeURIComponent(document.getElementById('pname').value)+'&price='+encodeURIComponent(document.getElementById('pprice').value)+'&salePrice='+encodeURIComponent(document.getElementById('psale').value)+'&stock='+encodeURIComponent(document.getElementById('pstock').value)+'&sku='+encodeURIComponent(document.getElementById('psku').value)+'&active=1&onlineEnabled='+(document.getElementById('ponline').checked?'1':'0')+'&imagePath='+encodeURIComponent(img);post('/api/seller/product/save',d,function(r){document.getElementById('pm').innerText=r;loadProducts();});};if(!f){send('');return;}var r=new FileReader();r.onload=function(){var data=r.result||'',base64=data.split(',')[1]||'';post('/api/media/upload','token='+encodeURIComponent(token)+'&storeId='+encodeURIComponent(storeId)+'&fileName='+encodeURIComponent(f.name)+'&contentType='+encodeURIComponent(f.type)+'&base64='+encodeURIComponent(base64),function(ans){var parts=ans.split('|');send(parts[0]==='OK'&&parts.length>2?parts[2]:'');});};r.readAsDataURL(f);}function loadCoupons(){var x=new XMLHttpRequest();x.open('GET','/api/coupons/list?storeId='+encodeURIComponent(storeId),true);x.onreadystatechange=function(){if(x.readyState===4)document.getElementById('clist').innerText=x.responseText;};x.send();}function saveCoupon(){var d='token='+encodeURIComponent(token)+'&code='+encodeURIComponent(document.getElementById('ccode').value)+'&description='+encodeURIComponent(document.getElementById('cdesc').value)+'&discountPercent='+encodeURIComponent(document.getElementById('cpct').value)+'&discountAmount='+encodeURIComponent(document.getElementById('camt').value)+'&maxUses='+encodeURIComponent(document.getElementById('cmax').value)+'&active=1&from=2020-01-01&to=2099-12-31';post('/api/coupons/upsert',d,function(r){document.getElementById('cm').innerText=r;loadCoupons();});}function loadOrders(){var x=new XMLHttpRequest();x.open('GET','/api/orders/pending?storeId='+encodeURIComponent(storeId),true);x.onreadystatechange=function(){if(x.readyState===4)document.getElementById('olist').innerText=x.responseText;};x.send();}function activate(){post('/api/licenses/activate','license='+encodeURIComponent(document.getElementById('lcode').value),function(r){document.getElementById('lm').innerText=r;});}</script></body></html>";
        }

        private string Heartbeat(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"); if(string.IsNullOrWhiteSpace(storeId))return "ERROR|storeId";
            return "OK|"+storeId+"|"+DateTime.UtcNow.ToString("o");
        }

        private string StoreLines(string query)
        {
            double lat = 0d, lon = 0d;
            bool latOk = double.TryParse(QueryValue(query, "lat"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
            bool lonOk = double.TryParse(QueryValue(query, "lon"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
            bool hasCoords = latOk && lonOk;
            string text = QueryValue(query, "q");
            List<StoreDistance> list = new List<StoreDistance>();
            lock (_sync)
            {
                foreach (XElement e in _doc.Root.Element("Stores").Elements("Store"))
                {
                    if (S(e, "Active") != "1") continue;
                    string hay = (S(e, "Name") + " " + S(e, "Category") + " " + S(e, "City") + " " + S(e, "Province")).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(text) && hay.IndexOf(text.ToLowerInvariant(), StringComparison.Ordinal) < 0) continue;
                    double sla = 0d, slo = 0d;
                    bool slaOk = double.TryParse(S(e, "Latitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out sla);
                    bool sloOk = double.TryParse(S(e, "Longitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out slo);
                    bool ok = slaOk && sloOk;
                    double distance = (hasCoords && ok) ? DistanceKm(lat, lon, sla, slo) : 999999d;
                    list.Add(new StoreDistance(e, distance));
                }
            }
            if (hasCoords) list = list.OrderBy(x => x.DistanceKm).ThenBy(x => S(x.Element, "Name")).ToList();
            else list = list.OrderBy(x => S(x.Element, "City")).ThenBy(x => S(x.Element, "Name")).ToList();
            StringBuilder b = new StringBuilder();
            foreach (StoreDistance x in list)
            {
                XElement e = x.Element;
                string publicUrl = S(e, "PublicUrl");
                if (string.IsNullOrWhiteSpace(publicUrl)) publicUrl = "/store/" + Uri.EscapeDataString(S(e, "StoreId"));
                else if (!publicUrl.Contains("/store/", StringComparison.OrdinalIgnoreCase)) publicUrl = publicUrl.TrimEnd('/') + "/store/" + Uri.EscapeDataString(S(e, "StoreId"));
                b.Append("STORE|").Append(Escape(S(e, "StoreId"))).Append('|').Append(Escape(S(e, "Name"))).Append('|').Append(Escape(publicUrl)).Append('|').Append(Escape(S(e, "City"))).Append('|').Append(Escape(S(e, "Province"))).Append('|').Append(Escape(S(e, "Category"))).Append('|').Append(Escape(S(e, "Latitude"))).Append('|').Append(Escape(S(e, "Longitude"))).Append('|').Append(Escape(S(e, "Active"))).Append('|').Append(Escape(S(e, "Delivery"))).Append('|').Append(Escape(S(e, "Pickup"))).Append('|').Append(Escape(e.Attribute("UpdatedAt") == null ? "" : e.Attribute("UpdatedAt").Value)).Append('|').Append(Escape(x.DistanceKm >= 999999d ? "" : x.DistanceKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))).Append('\n');
            }
            return b.ToString();
        }

        private sealed class StoreDistance
        {
            public XElement Element; public double DistanceKm;
            public StoreDistance(XElement element, double distanceKm) { Element = element; DistanceKm = distanceKm; }
        }

        private double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371.0; double dLat = (lat2 - lat1) * Math.PI / 180.0; double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
        }

        private string Geocode(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return "NOT_FOUND";
            try
            {
                string url = "https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=ar&q=" + Uri.EscapeDataString(q + ", Argentina");
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url); req.Method = "GET"; req.Timeout = 8000; req.UserAgent = "NexoMarket/3.5.2 (marketplace geocoder)";
                using (WebResponse resp = req.GetResponse()) using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = r.ReadToEnd();
                    string lat = JsonValue(json, "lat");
                    string lon = JsonValue(json, "lon");
                    string display = JsonValue(json, "display_name");
                    if (!string.IsNullOrEmpty(lat) && !string.IsNullOrEmpty(lon)) return "OK|" + lat + "|" + lon + "|" + (string.IsNullOrEmpty(display) ? q : display);
                    return "NOT_FOUND";
                }
            }
            catch { return "ERROR"; }
        }

        private string QueryValue(string query, string key)
        {
            foreach (string part in (query ?? "").Split('&'))
            {
                string[] p = part.Split(new[] { '=' }, 2);
                if (p.Length == 2 && string.Equals(Uri.UnescapeDataString(p[0]), key, StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(p[1]);
            }
            return "";
        }

        private string Storefront(string slug)
        {
            string storeId = Uri.UnescapeDataString(slug ?? "").Trim('/');
            XElement store = null;
            lock (_sync) { store = _doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x => string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase) || string.Equals(S(x,"Slug"),storeId,StringComparison.OrdinalIgnoreCase)); }
            if(store==null || S(store,"Active")!="1") return "<!doctype html><html><body style='font-family:Arial;background:#080b10;color:#fff;padding:40px'><h1>Tienda no disponible</h1><a href='/' style='color:#39ff66'>Volver a NexoMarket</a></body></html>";
            string realId=S(store,"StoreId");
            StringBuilder b=new StringBuilder();
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>").Append(E(S(store,"Name"))).Append(" · NexoMarket</title><style>body{font-family:'Segoe UI',Arial;background:#080b10;color:#fff;margin:0}.wrap{max-width:1180px;margin:auto;padding:20px}.hero{background:#101720;border:1px solid #26384e;border-radius:22px;padding:26px}.brand{color:#39ff66;font-size:32px;font-weight:900}.muted{color:#92a0b0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(230px,1fr));gap:15px;margin-top:20px}.card{background:#101720;border:1px solid #26384e;border-radius:18px;padding:16px}.price{font-size:20px;font-weight:900;margin-top:10px}.sale{color:#39ff66}.btn{background:#39ff66;color:#061009;border:0;border-radius:10px;padding:10px 14px;font-weight:900;cursor:pointer}.cart{position:sticky;bottom:10px;margin-top:25px;background:#111823;border:1px solid #2a3b51;border-radius:18px;padding:18px}.cart input,.cart select{background:#0d141d;color:#fff;border:1px solid #2a3b51;border-radius:9px;padding:10px;margin:4px;width:calc(100% - 18px)}.item{display:flex;justify-content:space-between;border-bottom:1px solid #223143;padding:8px 0}.empty{padding:20px;color:#8b99a9}.promos{margin-top:25px}.promo-card{border-color:#39ff66;box-shadow:0 0 0 1px rgba(57,255,102,.12) inset}</style></head><body><div class='wrap'><div class='hero'><div class='brand'>NEXO MARKET</div><h1>").Append(E(S(store,"Name"))).Append("</h1><div class='muted'>").Append(E(S(store,"Category"))).Append(" · ").Append(E(S(store,"City"))).Append("</div><p class='muted'>").Append(E(S(store,"Description"))).Append("</p></div><h2>Productos</h2><div id='products' class='grid'>");
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                List<XElement> ps=d.Root.Element("Products")==null?new List<XElement>():d.Root.Element("Products").Elements("Product").Where(x=>S(x,"StoreId")==realId&&S(x,"OnlineEnabled")!="0"&&S(x,"Active")!="0").ToList();
                if(ps.Count==0)b.Append("<div class='empty'>Esta tienda todavía no publicó productos.</div>");
                foreach(XElement x in ps){string id=S(x,"ProductId");string price=S(x,"Price");string sale=S(x,"SalePrice");string shown=string.IsNullOrWhiteSpace(sale)||sale=="0"?price:sale;b.Append("<div class='card'><h3>").Append(E(S(x,"Name"))).Append("</h3><div class='muted'>").Append(E(S(x,"Category"))).Append(" · ").Append(E(S(x,"Brand"))).Append("</div><div class='muted'>").Append(E(S(x,"PublicDescription"))).Append("</div><div class='price ").Append(string.IsNullOrWhiteSpace(sale)||sale=="0"?"":"sale").Append("'>$ ").Append(E(shown)).Append("</div><div class='muted'>Stock: ").Append(E(S(x,"Stock"))).Append("</div><button class='btn' onclick=\"add(").Append(JsonString(id)).Append(",").Append(JsonString(S(x,"Name"))).Append(",").Append(JsonNumber(shown)).Append(")\">AGREGAR</button></div>");}
            }
            b.Append("</div>");
            List<XElement> promotions = new List<XElement>();
            lock(_sync)
            {
                XDocument pd = LoadFile(_catalogFile, "NexoMarketCatalog", "Promotions");
                if (pd.Root.Element("Promotions") != null) promotions = pd.Root.Element("Promotions").Elements("Promotion").Where(x => S(x,"StoreId") == realId && S(x,"Active") != "0").ToList();
            }
            b.Append("<section class='promos'><h2>Promociones vigentes</h2><div class='grid'>");
            if(promotions.Count==0) b.Append("<div class='empty'>No hay promociones vigentes.</div>");
            foreach(XElement p in promotions)
            {
                string pid=S(p,"PromotionId"), pids=S(p,"ProductIds"), pname=S(p,"Name"), pp=S(p,"PromotionalPrice");
                b.Append("<div class='card promo-card'><div class='sale'>OFERTA</div><h3>").Append(E(pname)).Append("</h3><div class='price sale'>$ ").Append(E(pp)).Append("</div><div class='muted'>Vigencia: ").Append(E(S(p,"From"))).Append(" → ").Append(E(S(p,"To"))).Append("</div><button class='btn' onclick=\"addPromotion(").Append(JsonString(pid)).Append(",").Append(JsonString(pname)).Append(",").Append(JsonNumber(pp)).Append(",").Append(JsonString(pids)).Append(")\">COMPRAR PROMOCIÓN</button></div>");
            }
            b.Append("</div></section><div class='cart'><h2>Carrito</h2><div id='cartItems'>Carrito vacío.</div><h3>Total: $ <span id='total'>0</span></h3><form method='post' action='/api/orders/create' onsubmit='return sendOrder(event)'><input type='hidden' id='storeId' value='").Append(E(realId)).Append("'/><input id='name' placeholder='Nombre completo' required/><input id='email' type='email' placeholder='Correo electrónico'/><input id='phone' placeholder='Teléfono'/><select id='fulfillment'><option>Delivery</option><option>Retiro</option></select><input id='address' placeholder='Dirección / punto de retiro'/><select id='paymentMethod'><option>Transferencia</option><option>Mercado Pago</option><option>Efectivo</option></select><input id='paymentReference' placeholder='Referencia de pago (opcional)'/><input id='couponCode' placeholder='Cupón de descuento'/><button class='btn' type='button' onclick='applyCoupon()'>APLICAR CUPÓN</button><div id='couponMsg' class='muted'></div><input id='notes' placeholder='Notas para el vendedor'/><button class='btn' type='submit'>CONFIRMAR PEDIDO</button></form></div><p class='muted'>Pedido almacenado en NexoMarket Central. La PC del vendedor puede estar apagada.</p><div class='cart' style='margin-top:15px'><h2>Seguimiento del pedido</h2><div id='orderStatus' class='muted'>Después de confirmar un pedido aparecerá aquí su estado.</div><button class='btn' id='confirmReceived' style='display:none' onclick='confirmReceived()'>CONFIRMAR RECEPCIÓN</button><button class='btn' style='margin-left:8px' onclick='loadHistory()'>VER HISTORIAL</button><div id='history' class='muted' style='margin-top:12px'></div></div></div><script>var cart=[];var lastOrderId='';var couponDiscount=0;function addPromotion(id,name,price,productIds){var key='promo:'+id;var x=cart.filter(function(i){return i.id===key})[0];if(x)x.qty++;else cart.push({id:key,name:name,price:price,qty:1,promotionId:id,productIds:productIds});render();}function add(id,name,price){var x=cart.filter(function(i){return i.id===id})[0];if(x)x.qty++;else cart.push({id:id,name:name,price:price,qty:1});render();}function render(){var h='',t=0;cart.forEach(function(i){h+='<div class=item><span>'+i.name+' × '+i.qty+'</span><b>$ '+(i.price*i.qty).toFixed(2)+'</b></div>';t+=i.price*i.qty;});document.getElementById('cartItems').innerHTML=h||'Carrito vacío.';document.getElementById('total').innerHTML=Math.max(0,t-couponDiscount).toFixed(2);}function applyCoupon(){var code=document.getElementById('couponCode').value.trim();if(!code){couponDiscount=0;render();return;}var sub=0;cart.forEach(function(i){sub+=i.price*i.qty;});var b='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&code='+encodeURIComponent(code)+'&subtotal='+encodeURIComponent(sub);var x=new XMLHttpRequest();x.open('POST','/api/coupons/validate',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.responseText.indexOf('OK|')===0){couponDiscount=parseFloat(x.responseText.split('|')[1])||0;document.getElementById('couponMsg').innerText='Cupón aplicado: -$ '+couponDiscount.toFixed(2);render();}else{couponDiscount=0;document.getElementById('couponMsg').innerText='Cupón no válido: '+x.responseText;render();}}};x.send(b);}function pollStatus(){if(!lastOrderId)return;var u='/api/orders/status?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId);var x=new XMLHttpRequest();x.open('GET',u,true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);if(d.error)return;document.getElementById('orderStatus').innerHTML='Pedido <b>'+d.centralOrderId+'</b> · Estado: <b>'+d.status+'</b><br>Total: $ '+d.total+(d.updatedAt?' · Actualizado: '+new Date(d.updatedAt).toLocaleString():'');document.getElementById('confirmReceived').style.display=(d.status==='Entregado'&& !d.buyerConfirmed)?'inline-block':'none';}catch(e){}}};x.send();}function loadHistory(){var email=document.getElementById('email').value;if(!email){alert('Ingresá tu correo para consultar el historial.');return;}var u='/api/orders/history?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&email='+encodeURIComponent(email);var x=new XMLHttpRequest();x.open('GET',u,true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var a=JSON.parse(x.responseText),h='';a.forEach(function(o){h+='<div class=item><span>'+o.centralOrderId+' · '+o.status+'</span><b>$ '+o.total+'</b></div>';});document.getElementById('history').innerHTML=h||'No hay pedidos para este correo.';}catch(e){document.getElementById('history').innerHTML='No se pudo cargar el historial.';}}};x.send();}function confirmReceived(){var data='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId)+'&email='+encodeURIComponent(document.getElementById('email').value);var x=new XMLHttpRequest();x.open('POST','/api/orders/confirm',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){alert(x.responseText.indexOf('OK|')===0?'Recepción confirmada.':'No se pudo confirmar.');pollStatus();}};x.send(data);}function sendOrder(e){e.preventDefault();if(!cart.length){alert('Agregá al menos un producto.');return false;}var data={storeId:document.getElementById('storeId').value,customerName:document.getElementById('name').value,customerEmail:document.getElementById('email').value,phone:document.getElementById('phone').value,fulfillment:document.getElementById('fulfillment').value,address:document.getElementById('address').value,paymentMethod:document.getElementById('paymentMethod').value,paymentReference:document.getElementById('paymentReference').value,couponCode:document.getElementById('couponCode').value,notes:document.getElementById('notes').value,total:document.getElementById('total').innerHTML,itemsJson:JSON.stringify(cart)};var x=new XMLHttpRequest();var body=[];Object.keys(data).forEach(function(k){body.push(encodeURIComponent(k)+'='+encodeURIComponent(data[k]));});x.open('POST','/api/orders/create',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.status===200&&x.responseText.indexOf('OK|')===0){lastOrderId=x.responseText.split('|')[1];alert('Pedido enviado. Número central: '+lastOrderId);cart=[];render();pollStatus();}else alert('No se pudo enviar el pedido.');}};x.send(body.join('&'));return false;}setInterval(pollStatus,5000);</script></body></html>");
            return b.ToString();
        }

        private static string JsonNumber(string value)
        {
            decimal d; return decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        }

        private string ResolveStoreUrl(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return "/";
            slug = Uri.UnescapeDataString(slug).Trim().Trim('/');
            lock (_sync)
            {
                XElement store = _doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x =>
                    string.Equals(S(x, "Slug"), slug, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(S(x, "StoreId"), slug, StringComparison.OrdinalIgnoreCase));
                string url = store == null ? "" : S(store, "PublicUrl");
                return string.IsNullOrWhiteSpace(url) ? "/" : url;
            }
        }

        private static void WriteRedirect(NetworkStream stream, string location)
        {
            string safe = string.IsNullOrWhiteSpace(location) ? "/" : location;
            if (safe.IndexOf("\r", StringComparison.Ordinal) >= 0 || safe.IndexOf("\n", StringComparison.Ordinal) >= 0) safe = "/";
            Uri absolute;
            if (safe != "/" && (!Uri.TryCreate(safe, UriKind.Absolute, out absolute) || (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps))) safe = "/";
            string body = "<html><body><a href='" + E(safe) + "'>Abrir tienda</a><script>location.replace(" + JsonString(safe) + ");</script></body></html>";
            byte[] data = Encoding.UTF8.GetBytes(body);
            string header = "HTTP/1.1 302 Found\r\nLocation: " + safe + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length); stream.Write(data, 0, data.Length); stream.Flush();
        }

        private static string JsonString(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private string StoreJson(string query)
        {
            List<CentralStore> stores = new List<CentralStore>();
            string lines = StoreLines(query);
            using (StringReader reader = new StringReader(lines))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] p = line.Split('|');
                    if (p.Length < 12 || !line.StartsWith("STORE|", StringComparison.OrdinalIgnoreCase)) continue;
                    CentralStore cs = new CentralStore();
                    cs.StoreId = Uri.UnescapeDataString(p[1]); cs.Name = Uri.UnescapeDataString(p[2]);
                    cs.PublicUrl = Uri.UnescapeDataString(p[3]); cs.City = Uri.UnescapeDataString(p[4]);
                    cs.Province = Uri.UnescapeDataString(p[5]); cs.Category = Uri.UnescapeDataString(p[6]);
                    cs.Latitude = ParseDouble(Uri.UnescapeDataString(p[7])); cs.Longitude = ParseDouble(Uri.UnescapeDataString(p[8]));
                    cs.Active = Uri.UnescapeDataString(p[9]) == "1"; cs.Delivery = Uri.UnescapeDataString(p[10]) == "1"; cs.Pickup = Uri.UnescapeDataString(p[11]) == "1";
                    cs.Distance = p.Length > 13 ? ParseDouble(Uri.UnescapeDataString(p[13])) : 0d;
                    stores.Add(cs);
                }
            }
            StringBuilder b = new StringBuilder(); b.Append("[" );
            for (int i = 0; i < stores.Count; i++)
            {
                if (i > 0) b.Append(','); CentralStore x = stores[i];
                b.Append("{\"storeId\":").Append(JsonString(x.StoreId)).Append(",\"name\":").Append(JsonString(x.Name))
                 .Append(",\"publicUrl\":").Append(JsonString(x.PublicUrl)).Append(",\"city\":").Append(JsonString(x.City))
                 .Append(",\"province\":").Append(JsonString(x.Province)).Append(",\"category\":").Append(JsonString(x.Category))
                 .Append(",\"active\":").Append(x.Active ? "true" : "false").Append(",\"delivery\":").Append(x.Delivery ? "true" : "false")
                 .Append(",\"pickup\":").Append(x.Pickup ? "true" : "false").Append(",\"distanceKm\":").Append(x.Distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('}');
            }
            b.Append(']'); return b.ToString();
        }

        private string Marketplace(string query)
        {
            string q = QueryValue(query, "q"); double lat = 0d, lon = 0d;
            bool latOk = double.TryParse(QueryValue(query, "lat"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
            bool lonOk = double.TryParse(QueryValue(query, "lon"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
            bool hasCoords = latOk && lonOk;
            List<CentralStore> stores = new List<CentralStore>();
            {
                string storeQuery = hasCoords ? "lat=" + lat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&lon=" + lon.ToString(System.Globalization.CultureInfo.InvariantCulture) : (string.IsNullOrWhiteSpace(q) ? "" : "q=" + Uri.EscapeDataString(q));
                string lines = StoreLines(storeQuery);
                using (StringReader reader = new StringReader(lines)) { string line; while ((line = reader.ReadLine()) != null) { string[] p = line.Split('|'); if (p.Length < 12) continue; CentralStore cs = new CentralStore(); cs.StoreId = Uri.UnescapeDataString(p[1]); cs.Name = Uri.UnescapeDataString(p[2]); cs.PublicUrl = Uri.UnescapeDataString(p[3]); cs.City = Uri.UnescapeDataString(p[4]); cs.Province = Uri.UnescapeDataString(p[5]); cs.Category = Uri.UnescapeDataString(p[6]); cs.Latitude = ParseDouble(Uri.UnescapeDataString(p[7])); cs.Longitude = ParseDouble(Uri.UnescapeDataString(p[8])); cs.Active = Uri.UnescapeDataString(p[9]) == "1"; cs.Delivery = Uri.UnescapeDataString(p[10]) == "1"; cs.Pickup = Uri.UnescapeDataString(p[11]) == "1"; cs.Distance = p.Length > 13 ? ParseDouble(Uri.UnescapeDataString(p[13])) : 0d; stores.Add(cs); } }
            }
            StringBuilder b = new StringBuilder();
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>NexoMarket · Tiendas</title><style>body{font-family:'Segoe UI',Arial;background:#080b10;color:#fff;margin:0}.wrap{max-width:1180px;margin:auto;padding:25px}.hero{padding:35px;border:1px solid #26384e;background:linear-gradient(135deg,#111823,#0c121a);border-radius:22px}.nexo{color:#39ff66;font-size:48px;font-weight:900}.market{font-size:42px;font-weight:800}.sub{color:#93a1b1;margin-top:10px}.location{margin-top:22px;display:flex;gap:10px;flex-wrap:wrap}.location input{background:#0d141d;color:#fff;border:1px solid #2a3b51;border-radius:10px;padding:13px;width:340px}.btn{background:#39ff66;color:#061009;border:0;border-radius:10px;padding:12px 18px;font-weight:800;cursor:pointer}.hint{color:#7e8b9c;font-size:12px;margin:12px 0 20px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:15px}.card{display:block;color:#fff;text-decoration:none;background:#101720;border:1px solid #26384e;border-radius:18px;padding:18px;min-height:145px}.logo{width:60px;height:60px;border-radius:14px;background:#07100a;border:1px solid #2d6440;display:flex;align-items:center;justify-content:center;color:#39ff66;font-size:30px;font-weight:900;float:left;margin-right:14px}.name{font-size:20px;font-weight:800}.meta{color:#8390a0;font-size:13px;margin-top:6px}.open{color:#39ff66;font-size:12px;margin-top:12px}.distance{color:#ffd34d;font-size:12px;margin-top:6px}.empty{margin-top:20px;border:1px dashed #34475d;border-radius:18px;padding:30px;color:#8b99a9}footer{margin-top:40px;color:#607084;border-top:1px solid #1e2b39;padding-top:16px;font-size:11px}</style></head><body><div class='wrap'><div class='hero'><span class='nexo'>NEXO</span><span class='market'>MARKET</span><div class='sub'>Marketplace central · primero encontrá una tienda cercana, después sus productos.</div><form class='location' method='get' action='/'><input id='q' name='q' value='" + E(q) + "' placeholder='¿Desde dónde estás? Ej.: Mendoza, Luján...'/><input type='hidden' id='lat' name='lat'/><input type='hidden' id='lon' name='lon'/><button class='btn' type='submit'>Buscar tiendas</button><button class='btn' type='button' onclick='geo()'>Usar mi ubicación</button></form><div class='hint'>La ubicación se convierte a coordenadas mediante un servicio de geocodificación y las tiendas se ordenan por distancia. NexoMarket no muestra productos en esta portada.</div></div>");
            b.Append("<h2>Tiendas disponibles</h2>");
            if (stores.Count > 0) { b.Append("<div class='grid'>"); foreach (CentralStore cs in stores) { string href = cs.StoreId.Length == 0 ? (cs.PublicUrl.Length == 0 ? "#" : cs.PublicUrl) : "/store/" + Uri.EscapeDataString(cs.StoreId); string d = cs.Distance > 0 ? cs.Distance.ToString("0.0") + " km · " : ""; b.Append("<a class='card' href='" + E(href) + "'><div class='logo'>N</div><div class='name'>" + E(cs.Name) + "</div><div class='meta'>" + E(cs.Category.Length == 0 ? "Comercio" : cs.Category) + " · " + E(cs.City) + "</div><div class='open'>● Tienda activa</div><div class='distance'>📍 " + d + (cs.Delivery ? "🚚 Delivery" : "🏪 Retiro") + "</div></a>"); } b.Append("</div>"); }
            else b.Append("<div class='empty'><b>No hay tiendas publicadas todavía.</b><p>Cuando los vendedores publiquen sus tiendas aparecerán automáticamente aquí.</p></div>");
            b.Append("<footer>NexoMarket Central · " + stores.Count + " tiendas encontradas · Directorio multi-tienda</footer></div><script>function geo(){if(!navigator.geolocation){alert('Tu navegador no permite ubicación. Escribí una ciudad.');return;}navigator.geolocation.getCurrentPosition(function(p){document.getElementById('lat').value=p.coords.latitude;document.getElementById('lon').value=p.coords.longitude;document.getElementById('q').value='Mi ubicación';document.querySelector('.location').submit();},function(){alert('No se pudo obtener la ubicación.');});}</script></body></html>");
            return b.ToString();
        }

        private double ParseDouble(string value) { double d; return double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0d; }

        private sealed class CentralStore
        {
            public string StoreId = ""; public string Name = ""; public string Category = ""; public string City = ""; public string Province = ""; public string PublicUrl = ""; public bool Delivery; public bool Pickup; public double Latitude; public double Longitude; public double Distance; public bool Active;
            public CentralStore() { }
            public CentralStore(XElement e) { StoreId=S(e,"StoreId"); Name=S(e,"Name"); Category=S(e,"Category"); City=S(e,"City"); Province=S(e,"Province"); PublicUrl=S(e,"PublicUrl"); Delivery=S(e,"Delivery")=="1"; Pickup=S(e,"Pickup")=="1"; Active=S(e,"Active")=="1"; double.TryParse(S(e,"Latitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out Latitude); double.TryParse(S(e,"Longitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out Longitude); }
        }
        private static string JsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return "";
            string marker = "\"" + key + "\"";
            int k = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return "";
            int colon = json.IndexOf(':', k + marker.Length);
            if (colon < 0) return "";
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start < json.Length && json[start] == '\"')
            {
                start++;
                int end = start;
                while (end < json.Length)
                {
                    if (json[end] == '\"' && (end == start || json[end - 1] != '\\')) break;
                    end++;
                }
                return end <= json.Length ? json.Substring(start, end - start) : "";
            }
            int stop = start;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}') stop++;
            return json.Substring(start, stop - start).Trim();
        }

    }
}
