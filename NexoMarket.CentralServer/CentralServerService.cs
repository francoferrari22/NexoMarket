using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace NexoMarket.CentralServer
{
    /// <summary>
    /// Directorio central liviano, sin IIS ni dependencias externas.
    /// Recibe tiendas por StoreId y sirve la página principal del marketplace.
    /// Está pensado para .NET 8 y ejecución multiplataforma (Windows/Linux/Render).
    /// </summary>
    public sealed class CentralServerService : IDisposable
    {
        private readonly int _port;
        private readonly string _root;
        private readonly string _file;
        private readonly string _catalogFile;
        private readonly string _ordersFile;
        private readonly string _accountsFile;
        private readonly object _sync = new object();
        private readonly Dictionary<string, CentralUser> _sessions = new Dictionary<string, CentralUser>(StringComparer.OrdinalIgnoreCase);
        private TcpListener _listener;
        private System.Threading.Thread _worker;
        private volatile bool _running;
        private XDocument _doc;
        private readonly R2ObjectStore _r2;

        public CentralServerService(int port)
        {
            _port = port;
            _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _file = Path.Combine(_root, "nexomarket_stores.xml");
            _catalogFile = Path.Combine(_root, "nexomarket_catalog.xml");
            _ordersFile = Path.Combine(_root, "nexomarket_orders.xml");
            _accountsFile = Path.Combine(_root, "nexomarket_accounts.xml");
            Directory.CreateDirectory(_root);
            _r2 = new R2ObjectStore();
            // Restaurar TODOS los datos persistentes antes de cargar o guardar cualquier documento.
            // En versiones anteriores Load() podía crear un registro vacío y subirlo a R2 antes
            // de restaurar los datos, borrando las tiendas al reiniciar Render.
            RestoreLatest(_file, "data/nexomarket_stores.xml");
            RestoreLatest(_catalogFile, "data/nexomarket_catalog.xml");
            RestoreLatest(_ordersFile, "data/nexomarket_orders.xml");
            RestoreLatest(_accountsFile, "data/nexomarket_accounts.xml");
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

        private void RestoreLatest(string file, string key)
        {
            if (_r2 == null || !_r2.Enabled) return;
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
                        if (path == "/api/accounts/upsert" && method == "POST") { Write(stream, 200, "text/plain", AccountUpsert(Form(body), true)); return; }
                        if (path == "/login") { CentralLogin(stream, method, body, HeaderCookie(request, "NexoCentralSession")); return; }
                        if (path == "/register") { CentralRegister(stream, method, body); return; }
                        if (path == "/logout") { CentralLogout(stream); return; }
                        if (path == "/seller") { CentralSeller(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/seller/order-status" && method == "POST") { CentralSellerOrderStatus(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/buyer") { CentralBuyer(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/api/storage/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StorageStatus()); return; }
                        if (path == "/api/media/upload" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", UploadMedia(Form(body))); return; }
                        if (path == "/api/stores" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StoreLines(query)); return; }
                        if (path == "/api/stores/connect" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StoreConnect(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/sync/diagnostics" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", SyncDiagnostics(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/stores/json" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", StoreJson(query)); return; }
                        if (path == "/api/geocode" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", Geocode(QueryValue(query, "q"))); return; }
                        if (path == "/api/stores/register" && method == "POST") { Write(stream, 200, "text/plain", Register(Form(body))); return; }
                        if (path == "/api/products/publish" && method == "POST") { Write(stream, 200, "text/plain", PublishProduct(Form(body))); return; }
                        if (path == "/api/promotions/publish" && method == "POST") { Write(stream, 200, "text/plain", PublishPromotion(Form(body))); return; }
                        if (path == "/api/catalog" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", CatalogJson(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/catalog/lines" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", CatalogLines(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
                        if (path == "/api/orders/create" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CreateOrder(Form(body))); return; }
                        if (path == "/api/orders/pending" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", PendingOrders(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/orders/ack" && method == "POST") { Write(stream, 200, "text/plain", AckOrder(Form(body))); return; }
                        if (path == "/api/orders/status" && method == "POST") { Write(stream, 200, "text/plain", UpdateOrderStatus(Form(body))); return; }
                        if (path == "/api/orders/status" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", GetOrderStatus(QueryValue(query, "storeId"), QueryValue(query, "centralOrderId"))); return; }
                        if (path == "/api/orders/confirm" && method == "POST") { Write(stream, 200, "text/plain", ConfirmOrder(Form(body))); return; }
                        if (path == "/api/orders/history" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", HistoryOrders(QueryValue(query, "storeId"), QueryValue(query, "email"))); return; }
                        if (path == "/api/sync/heartbeat" && method == "POST") { Write(stream, 200, "text/plain", Heartbeat(Form(body))); return; }
                        if (path == "/api/accounts/auth" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AccountAuthenticate(Form(body))); return; }
                        if (path == "/api/accounts/lookup" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AccountLookup(QueryValue(query, "email"), QueryValue(query, "accountId"))); return; }
                        if (path == "/api/accounts" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AccountLines(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
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
                            "Cache-Control: no-store, no-cache, must-revalidate, max-age=0\r\n" +
                            "Pragma: no-cache\r\n" +
                            "Content-Length: " + data.Length + "\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private string Register(Dictionary<string,string> f)
        {
            string id = Get(f, "storeId").Trim();
            if (string.IsNullOrWhiteSpace(id)) return "ERROR|store_id_required";
            string syncKey = Get(f, "syncKey").Trim();
            if (string.IsNullOrWhiteSpace(syncKey)) return "ERROR|sync_key_required";
            lock (_sync)
            {
                XElement stores = _doc.Root.Element("Stores");
                XElement old = stores.Elements("Store").FirstOrDefault(x => string.Equals(S(x, "StoreId"), id, StringComparison.OrdinalIgnoreCase));
                string existingKey = old == null ? "" : S(old, "SyncKey");
                if (!string.IsNullOrWhiteSpace(existingKey) && !string.Equals(existingKey, syncKey, StringComparison.Ordinal)) return "ERROR|store_sync_key_mismatch";
                string active = Get(f, "active"); if (active != "0") active = "1";
                XElement e = new XElement("Store", new XAttribute("UpdatedAt", string.IsNullOrWhiteSpace(Get(f,"updatedAt")) ? DateTime.UtcNow.ToString("o") : Get(f, "updatedAt")),
                    new XElement("StoreId", id), new XElement("SyncKey", syncKey), new XElement("Name", Get(f, "name")), new XElement("LegalName", Get(f, "legalName")),
                    new XElement("Category", Get(f, "category")), new XElement("Address", Get(f, "address")), new XElement("City", Get(f, "city")),
                    new XElement("Province", Get(f, "province")), new XElement("Description", Get(f, "description")), new XElement("Logo", Get(f, "logo")),
                    new XElement("Slug", Get(f, "slug")), new XElement("PublicUrl", Get(f, "publicUrl")), new XElement("Active", active),
                    new XElement("Delivery", Get(f, "delivery") == "0" ? "0" : "1"), new XElement("Pickup", Get(f, "pickup") == "0" ? "0" : "1"),
                    new XElement("Latitude", Get(f, "latitude")), new XElement("Longitude", Get(f, "longitude")));
                if (old != null) old.ReplaceWith(e); else stores.Add(e);
                Save();
                return "OK|registered|" + Escape(id);
            }
        }

        private void EnsureCentralDataFiles()
        {
            lock (_sync)
            {
                // La restauración se realiza antes de Load(). Acá solamente creamos archivos
                // que realmente no existen; jamás reemplazamos una copia persistente válida.
                if (!File.Exists(_accountsFile))
                    File.WriteAllText(_accountsFile, new XDocument(new XElement("NexoMarketAccounts", new XElement("Users"))).ToString(SaveOptions.None), Encoding.UTF8);
                if (!File.Exists(_catalogFile))
                    File.WriteAllText(_catalogFile, new XDocument(new XElement("NexoMarketCatalog", new XElement("Products"), new XElement("Promotions"))).ToString(SaveOptions.None), Encoding.UTF8);
                if (!File.Exists(_ordersFile))
                    File.WriteAllText(_ordersFile, new XDocument(new XElement("NexoMarketOrders", new XElement("Orders"))).ToString(SaveOptions.None), Encoding.UTF8);
                // Subir los archivos recién creados o los existentes restaurados.
                if (_r2 != null && _r2.Enabled)
                {
                    if (File.Exists(_accountsFile)) _r2.PutText("data/nexomarket_accounts.xml", File.ReadAllText(_accountsFile, Encoding.UTF8));
                    if (File.Exists(_catalogFile)) _r2.PutText("data/nexomarket_catalog.xml", File.ReadAllText(_catalogFile, Encoding.UTF8));
                    if (File.Exists(_ordersFile)) _r2.PutText("data/nexomarket_orders.xml", File.ReadAllText(_ordersFile, Encoding.UTF8));
                }
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
            string storeId = Get(f, "storeId");
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

        private string AccountAuthenticate(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); CentralUser u;
            if(!VerifyAccount(email,password,out u)||u==null)return "ERROR|invalid_credentials";
            return "OK|"+Escape(u.Id)+"|"+Escape(u.Name)+"|"+Escape(u.Email)+"|"+Escape(u.Phone)+"|"+Escape(u.Role)+"|"+Escape(u.StoreId)+"|"+Escape(u.Salt)+"|"+Escape(u.PasswordHash)+"|"+Escape(u.CreatedAt);
        }

        private string AccountLookup(string email,string accountId)
        {
            CentralUser u=null;
            if(!string.IsNullOrWhiteSpace(email)) u=FindAccount(email);
            else if(!string.IsNullOrWhiteSpace(accountId))
            {
                lock(_sync)
                {
                    XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");
                    XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Id"),accountId,StringComparison.OrdinalIgnoreCase));
                    u=e==null?null:CentralUser.From(e);
                }
            }
            if(u==null)return "ERROR|notfound";
            return "OK|"+Escape(u.Id)+"|"+Escape(u.Email)+"|"+Escape(u.Name)+"|"+Escape(u.StoreId)+"|"+Escape(u.Role);
        }

        private string PublishProduct(Dictionary<string,string> f)
        {
            string storeId = Get(f, "storeId").Trim(), productId = Get(f, "productId").Trim();
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(productId)) return "ERROR|missing";
            if (!ValidateStoreSyncKey(storeId, Get(f,"syncKey"))) return "ERROR|sync_key";
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
            return "OK|product";
        }

        private string PublishPromotion(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId").Trim(), promotionId=Get(f,"promotionId").Trim(); if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(promotionId)) return "ERROR|missing"; if(!ValidateStoreSyncKey(storeId,Get(f,"syncKey"))) return "ERROR|sync_key";
            lock(_sync){ XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Promotions"); if(d.Root.Element("Promotions")==null)d.Root.Add(new XElement("Promotions")); XElement old=d.Root.Element("Promotions").Elements("Promotion").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"PromotionId")==promotionId);
                XElement e=new XElement("Promotion",new XElement("StoreId",storeId),new XElement("PromotionId",promotionId),new XElement("Name",Get(f,"name")),new XElement("ProductIds",Get(f,"productIds")),new XElement("PromotionalPrice",Get(f,"promotionalPrice")),new XElement("Active",Get(f,"active")),new XElement("From",Get(f,"from")),new XElement("To",Get(f,"to")),new XElement("UpdatedAt",Get(f,"updatedAt")));
                if(old!=null)old.ReplaceWith(e);else d.Root.Element("Promotions").Add(e);SaveDoc(_catalogFile,d); } return "OK|promotion";
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
            decimal total; if(!decimal.TryParse(Get(f,"total"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out total) || total<=0m) return "ERROR|total";
            string itemsJson = Get(f,"itemsJson");
            string stockError = ValidateAndReserveStock(storeId, itemsJson);
            if (!string.IsNullOrWhiteSpace(stockError)) return stockError;
            string centralId=Guid.NewGuid().ToString("N"); string now=DateTime.UtcNow.ToString("o");
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement e=new XElement("Order",new XElement("CentralOrderId",centralId),new XElement("StoreId",storeId),new XElement("CustomerId",Get(f,"customerId")),new XElement("CustomerName",Get(f,"customerName")),new XElement("CustomerEmail",Get(f,"customerEmail")),new XElement("Phone",Get(f,"phone")),new XElement("Fulfillment",Get(f,"fulfillment")),new XElement("Address",Get(f,"address")),new XElement("Notes",Get(f,"notes")),new XElement("Status",string.IsNullOrWhiteSpace(Get(f,"status"))?"Pendiente":Get(f,"status")),new XElement("Total",Get(f,"total")),new XElement("PaymentMethod",Get(f,"paymentMethod")),new XElement("PaymentStatus",string.IsNullOrWhiteSpace(Get(f,"paymentStatus"))?"Pendiente":Get(f,"paymentStatus")),new XElement("PaymentReference",Get(f,"paymentReference")),new XElement("PaymentProofPath",Get(f,"paymentProofPath")),new XElement("ShippingCost",Get(f,"shippingCost")),new XElement("TrackingNumber",Get(f,"trackingNumber")),new XElement("Carrier",Get(f,"carrier")),new XElement("ItemsJson",Get(f,"itemsJson")),new XElement("BuyerMessage",Get(f,"buyerMessage")),new XElement("CreatedAt",now),new XElement("Ack", "0")); d.Root.Element("Orders").Add(e);SaveDoc(_ordersFile,d);}
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

        private string Heartbeat(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"); if(string.IsNullOrWhiteSpace(storeId))return "ERROR|storeId";
            return "OK|"+storeId+"|"+DateTime.UtcNow.ToString("o");
        }

        private string StoreConnect(string storeId)
        {
            storeId = NormalizeStoreId(storeId);
            if (storeId.Length == 0) return "ERROR|store_id_required";
            lock (_sync)
            {
                XElement stores = _doc.Root.Element("Stores");
                XElement store = stores == null ? null : stores.Elements("Store").FirstOrDefault(x => string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase));
                if (store == null) return "ERROR|store_not_found|" + Escape(storeId);
                if (S(store, "Active") != "1") return "ERROR|store_inactive|" + Escape(storeId);
                string syncKey = S(store, "SyncKey");
                if (string.IsNullOrWhiteSpace(syncKey)) return "ERROR|store_sync_key_missing|" + Escape(storeId);

                string sellerEmail = "", sellerName = "";
                XDocument accounts = LoadFile(_accountsFile, "NexoMarketAccounts", "Users");
                XElement users = accounts.Root.Element("Users");
                if (users != null)
                {
                    XElement seller = users.Elements("User").FirstOrDefault(x =>
                        string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(S(x, "Role"), "seller", StringComparison.OrdinalIgnoreCase));
                    if (seller != null)
                    {
                        sellerEmail = S(seller, "Email");
                        sellerName = S(seller, "Name");
                    }
                }

                // StoreId es la identidad de la instalación. La cuenta web es información
                // complementaria; su ausencia NO debe impedir conectar Windows a una tienda
                // que ya existe y está activa.
                return "OK|" + Escape(storeId) + "|" + Escape(S(store, "Name")) + "|1|" + Escape(syncKey) +
                    "|" + Escape(sellerEmail) + "|" + Escape(sellerName) + "|" +
                    Escape(A(store, "UpdatedAt")) + "|" + Escape(S(store, "LegalName")) + "|" + Escape(S(store, "Category")) +
                    "|" + Escape(S(store, "Address")) + "|" + Escape(S(store, "City")) + "|" + Escape(S(store, "Province")) +
                    "|" + Escape(S(store, "Description")) + "|" + Escape(S(store, "Logo")) + "|" + Escape(S(store, "Slug")) +
                    "|" + Escape(S(store, "PublicUrl")) + "|" + Escape(S(store, "Active")) + "|" + Escape(S(store, "Delivery")) +
                    "|" + Escape(S(store, "Pickup")) + "|" + Escape(S(store, "Latitude")) + "|" + Escape(S(store, "Longitude"));
            }
        }

        private string SyncDiagnostics(string storeId)
        {
            storeId = NormalizeStoreId(storeId);
            if (storeId.Length == 0) return "ERROR|store_id_required";
            lock (_sync)
            {
                XElement store = _doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x => string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase));
                if (store == null) return "ERROR|store_not_found";
                string syncKey = S(store, "SyncKey");
                XDocument accounts = LoadFile(_accountsFile, "NexoMarketAccounts", "Users");
                int accountCount = accounts.Root.Element("Users") == null ? 0 : accounts.Root.Element("Users").Elements("User").Count(x => string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase));
                XDocument catalog = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                int productCount = catalog.Root.Element("Products") == null ? 0 : catalog.Root.Element("Products").Elements("Product").Count(x => string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase));
                return "OK|store=" + Escape(storeId) + "|active=" + Escape(S(store, "Active")) + "|accounts=" + accountCount.ToString(CultureInfo.InvariantCulture) + "|products=" + productCount.ToString(CultureInfo.InvariantCulture) + "|r2=" + ((_r2 != null && _r2.Enabled) ? "1" : "0") + "|syncKey=" + (string.IsNullOrWhiteSpace(syncKey) ? "0" : "1");
            }
        }

        private static string NormalizeStoreId(string value)
        {
            return (value ?? "").Trim().Replace(" ", "").ToUpperInvariant();
        }

        private string CatalogLines(string storeId, string syncKey)
        {
            storeId = (storeId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(storeId) || !ValidateStoreSyncKey(storeId, syncKey)) return "";
            StringBuilder b = new StringBuilder();
            lock (_sync)
            {
                XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                XElement products = d.Root.Element("Products");
                if (products == null) return "";
                foreach (XElement x in products.Elements("Product").Where(x => string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase)))
                {
                    b.Append("PRODUCT|").Append(Escape(S(x,"ProductId"))).Append('|').Append(Escape(S(x,"Name"))).Append('|').Append(Escape(S(x,"Category"))).Append('|').Append(Escape(S(x,"Description"))).Append('|').Append(Escape(S(x,"Price"))).Append('|').Append(Escape(S(x,"SalePrice"))).Append('|').Append(Escape(S(x,"Stock"))).Append('|').Append(Escape(S(x,"MinimumStock"))).Append('|').Append(Escape(S(x,"SKU"))).Append('|').Append(Escape(S(x,"Brand"))).Append('|').Append(Escape(S(x,"Size"))).Append('|').Append(Escape(S(x,"Color"))).Append('|').Append(Escape(S(x,"Active"))).Append('|').Append(Escape(S(x,"OnlineEnabled"))).Append('|').Append(Escape(S(x,"ImagePath"))).Append('|').Append(Escape(S(x,"Slug"))).Append('|').Append(Escape(S(x,"PublicDescription"))).Append('|').Append(Escape(S(x,"VideoUrl"))).Append('|').Append(Escape(S(x,"BarcodeImagePath"))).Append('|').Append(Escape(S(x,"UpdatedAt"))).Append('\n');
                }
            }
            return b.ToString();
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
            b.Append("</div></section><div class='cart'><h2>Carrito</h2><div id='cartItems'>Carrito vacío.</div><h3>Total: $ <span id='total'>0</span></h3><form method='post' action='/api/orders/create' onsubmit='return sendOrder(event)'><input type='hidden' id='storeId' value='").Append(E(realId)).Append("'/><input id='name' placeholder='Nombre completo' required/><input id='email' type='email' placeholder='Correo electrónico'/><input id='phone' placeholder='Teléfono'/><select id='fulfillment'><option>Delivery</option><option>Retiro</option></select><input id='address' placeholder='Dirección / punto de retiro'/><select id='paymentMethod'><option>Transferencia</option><option>Mercado Pago</option><option>Efectivo</option></select><input id='paymentReference' placeholder='Referencia de pago (opcional)'/><input id='notes' placeholder='Notas para el vendedor'/><button class='btn' type='submit'>CONFIRMAR PEDIDO</button></form></div><p class='muted'>Pedido almacenado en NexoMarket Central. La PC del vendedor puede estar apagada.</p><div class='cart' style='margin-top:15px'><h2>Seguimiento del pedido</h2><div id='orderStatus' class='muted'>Después de confirmar un pedido aparecerá aquí su estado.</div><button class='btn' id='confirmReceived' style='display:none' onclick='confirmReceived()'>CONFIRMAR RECEPCIÓN</button><button class='btn' style='margin-left:8px' onclick='loadHistory()'>VER HISTORIAL</button><div id='history' class='muted' style='margin-top:12px'></div></div></div><script>var cart=[];var lastOrderId='';function addPromotion(id,name,price,productIds){var key='promo:'+id;var x=cart.filter(function(i){return i.id===key})[0];if(x)x.qty++;else cart.push({id:key,name:name,price:price,qty:1,promotionId:id,productIds:productIds});render();}function add(id,name,price){var x=cart.filter(function(i){return i.id===id})[0];if(x)x.qty++;else cart.push({id:id,name:name,price:price,qty:1});render();}function render(){var h='',t=0;cart.forEach(function(i){h+='<div class=item><span>'+i.name+' × '+i.qty+'</span><b>$ '+(i.price*i.qty).toFixed(2)+'</b></div>';t+=i.price*i.qty;});document.getElementById('cartItems').innerHTML=h||'Carrito vacío.';document.getElementById('total').innerHTML=t.toFixed(2);}function pollStatus(){if(!lastOrderId)return;var u='/api/orders/status?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId);var x=new XMLHttpRequest();x.open('GET',u,true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);if(d.error)return;document.getElementById('orderStatus').innerHTML='Pedido <b>'+d.centralOrderId+'</b> · Estado: <b>'+d.status+'</b><br>Total: $ '+d.total+(d.updatedAt?' · Actualizado: '+new Date(d.updatedAt).toLocaleString():'');document.getElementById('confirmReceived').style.display=(d.status==='Entregado'&& !d.buyerConfirmed)?'inline-block':'none';}catch(e){}}};x.send();}function loadHistory(){var email=document.getElementById('email').value;if(!email){alert('Ingresá tu correo para consultar el historial.');return;}var u='/api/orders/history?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&email='+encodeURIComponent(email);var x=new XMLHttpRequest();x.open('GET',u,true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var a=JSON.parse(x.responseText),h='';a.forEach(function(o){h+='<div class=item><span>'+o.centralOrderId+' · '+o.status+'</span><b>$ '+o.total+'</b></div>';});document.getElementById('history').innerHTML=h||'No hay pedidos para este correo.';}catch(e){document.getElementById('history').innerHTML='No se pudo cargar el historial.';}}};x.send();}function confirmReceived(){var data='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId)+'&email='+encodeURIComponent(document.getElementById('email').value);var x=new XMLHttpRequest();x.open('POST','/api/orders/confirm',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){alert(x.responseText.indexOf('OK|')===0?'Recepción confirmada.':'No se pudo confirmar.');pollStatus();}};x.send(data);}function sendOrder(e){e.preventDefault();if(!cart.length){alert('Agregá al menos un producto.');return false;}var data={storeId:document.getElementById('storeId').value,customerName:document.getElementById('name').value,customerEmail:document.getElementById('email').value,phone:document.getElementById('phone').value,fulfillment:document.getElementById('fulfillment').value,address:document.getElementById('address').value,paymentMethod:document.getElementById('paymentMethod').value,paymentReference:document.getElementById('paymentReference').value,notes:document.getElementById('notes').value,total:document.getElementById('total').innerHTML,itemsJson:JSON.stringify(cart)};var x=new XMLHttpRequest();var body=[];Object.keys(data).forEach(function(k){body.push(encodeURIComponent(k)+'='+encodeURIComponent(data[k]));});x.open('POST','/api/orders/create',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.status===200&&x.responseText.indexOf('OK|')===0){lastOrderId=x.responseText.split('|')[1];alert('Pedido enviado. Número central: '+lastOrderId);cart=[];render();pollStatus();}else alert('No se pudo enviar el pedido.');}};x.send(body.join('&'));return false;}setInterval(pollStatus,5000);</script></body></html>");
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
            string locationTitle = hasCoords ? (string.IsNullOrWhiteSpace(q) ? "Tu ubicación" : q) : (string.IsNullOrWhiteSpace(q) ? "Sin ubicación definida" : q);
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta http-equiv='Cache-Control' content='no-store'><title>NexoMarket · Tiendas</title>");
            b.Append("<style>body{font-family:'Segoe UI',Arial,sans-serif;background:#070b10;color:#fff;margin:0}.wrap{max-width:1180px;margin:auto;padding:22px}.top{display:flex;justify-content:space-between;align-items:center;padding:6px 4px 18px}.brand{font-weight:900;font-size:23px}.brand .n{color:#39ff66}.top a{color:#fff;text-decoration:none;margin-left:18px;font-weight:700}.hero{padding:28px;border:1px solid #2a4660;background:linear-gradient(135deg,#101925,#0b1722);border-radius:22px;box-shadow:0 12px 35px rgba(0,0,0,.18)}.eyebrow{font-size:11px;letter-spacing:2px;color:#39ff66;font-weight:900}.nexo{color:#39ff66;font-size:45px;font-weight:900}.market{font-size:40px;font-weight:800}.hero-sub{color:#a8c0d4;margin-top:9px;font-size:15px}.location-box{margin-top:20px;display:flex;align-items:center;gap:10px;flex-wrap:wrap}.location-box input{background:#0c141d;color:#fff;border:1px solid #2c4963;border-radius:10px;padding:12px;width:330px}.btn{background:#39ff66;color:#061009;border:0;border-radius:10px;padding:11px 16px;font-weight:900;cursor:pointer}.btn.alt{background:#0d1721;color:#fff;border:1px solid #2d4a64}.hint{color:#7e94a8;font-size:12px;margin-top:12px}.section-head{display:flex;justify-content:space-between;align-items:end;margin:24px 2px 12px}.section-head h2{margin:0;font-size:24px}.section-head p{margin:5px 0 0;color:#8da3b6;font-size:13px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:15px}.card{display:block;color:#fff;text-decoration:none;background:#0e1721;border:1px solid #2a4660;border-radius:18px;padding:18px;min-height:118px;transition:.15s}.card:hover{transform:translateY(-2px);border-color:#39ff66}.logo{width:58px;height:58px;border-radius:14px;background:#07110a;border:1px solid #2d7042;display:flex;align-items:center;justify-content:center;color:#39ff66;font-size:29px;font-weight:900;float:left;margin-right:14px}.name{font-size:20px;font-weight:900;padding-top:3px}.meta{color:#92a6b7;font-size:13px;margin-top:6px}.open{color:#39ff66;font-size:12px;margin-top:11px}.distance{color:#ffd34d;font-size:12px;margin-top:5px}.empty{margin-top:18px;border:1px dashed #38516a;border-radius:18px;padding:28px;color:#a2b2c0;background:#0b131c}.empty b{font-size:18px}.auth-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:15px;margin-top:20px}.panel{background:#0e1721;border:1px solid #2a4660;border-radius:18px;padding:20px}.panel h2{margin-top:0}.panel p{color:#92a6b7;font-size:13px;line-height:1.5}.mini{color:#39ff66;font-size:12px;font-weight:800}.footer{margin-top:35px;border-top:1px solid #203448;padding-top:14px;color:#60768a;font-size:11px}@media(max-width:650px){.wrap{padding:14px}.nexo{font-size:35px}.market{font-size:31px}.location-box input{width:100%}}</style></head><body><div class='wrap'>");
            b.Append("<div class='top'><div class='brand'><span class='n'>NEXO</span>MARKET</div><div><a href='/'>Tiendas</a><a href='/login'>Ingresar</a><a href='/register'>Crear cuenta</a></div></div>");
            b.Append("<section class='hero'><span class='eyebrow'>MARKETPLACE</span><div><span class='nexo'>NEXO</span><span class='market'>MARKET</span></div><div class='hero-sub'>Encontrá todas las tiendas disponibles y priorizá las más cercanas" + (hasCoords ? " a <b>" + E(locationTitle) + "</b>." : ".") + "</div><form class='location-box' method='get' action='/'><input id='q' name='q' value='" + E(q) + "' placeholder='¿Desde dónde estás? Ej.: Mendoza, Luján...'/><input type='hidden' id='lat' name='lat'/><input type='hidden' id='lon' name='lon'/><button class='btn' type='submit'>Buscar tiendas</button><button class='btn' type='button' onclick='geo()'>Usar mi ubicación</button></form><div class='hint'>La ubicación se convierte a coordenadas y las tiendas se ordenan por cercanía. Los datos se actualizan desde NexoMarket Central.</div></section>");
            b.Append("<div class='section-head'><div><h2>Tiendas disponibles</h2><p>Las tiendas activas se muestran automáticamente desde el directorio central.</p></div><span class='mini'>DIRECTORIO MULTI-TIENDA</span></div>");
            if (stores.Count > 0) { b.Append("<div class='grid'>"); foreach (CentralStore cs in stores) { string href = "/store/" + Uri.EscapeDataString(cs.StoreId); string d = cs.Distance > 0 ? cs.Distance.ToString("0.0") + " km · " : ""; b.Append("<a class='card' href='" + E(href) + "'><div class='logo'>N</div><div class='name'>" + E(cs.Name) + "</div><div class='meta'>" + E(cs.Category.Length == 0 ? "Comercio" : cs.Category) + " · " + E(cs.City) + "</div><div class='open'>● Abierta · " + (cs.Delivery ? "Delivery" : "Retiro") + "</div><div class='distance'>📍 " + E(d) + (cs.Delivery ? "🚚 Delivery" : "🏪 Retiro") + "</div></a>"); } b.Append("</div>"); }
            else b.Append("<div class='empty'><b>No hay tiendas publicadas todavía.</b><p>Cuando un vendedor publique o actualice su tienda, aparecerá automáticamente aquí. Si acabás de publicarla, volvé a cargar esta página.</p></div>");
            b.Append("<section id='cuenta' class='auth-grid'><div class='panel'><h2>¿Ya tenés cuenta?</h2><p>El acceso de comprador/vendedor sigue gestionado por el Seller Center de cada tienda mientras el directorio central sincroniza las publicaciones.</p><a class='btn alt' href='/stores'>VER DIRECTORIO</a></div><div class='panel'><h2>¿Sos vendedor?</h2><p>Publicá tu tienda desde NexoMarket Admin. La configuración central está preparada para sincronizar automáticamente tiendas, productos y promociones.</p><div class='mini'>● SINCRONIZACIÓN CENTRAL ACTIVA</div></div></section>");
            b.Append("<div class='footer'>NexoMarket Central · " + stores.Count + " tiendas encontradas · datos actualizados sin caché</div></div><script>function geo(){if(!navigator.geolocation){alert('Tu navegador no permite ubicación. Escribí una ciudad.');return;}navigator.geolocation.getCurrentPosition(function(p){document.getElementById('lat').value=p.coords.latitude;document.getElementById('lon').value=p.coords.longitude;document.getElementById('q').value='Mi ubicación';document.querySelector('.location-box').submit();},function(){alert('No se pudo obtener la ubicación.');},{enableHighAccuracy:false,timeout:8000,maximumAge:300000});}</script></body></html>");
            return b.ToString();
        }

        private string AccountUpsert(Dictionary<string,string> f, bool requireSyncKey)
        {
            string email = Get(f,"email").Trim().ToLowerInvariant();
            string role = Get(f,"role").Trim().ToLowerInvariant() == "seller" ? "seller" : "buyer";
            string storeId = Get(f,"storeId").Trim();
            string salt = Get(f,"salt"); string hash = Get(f,"passwordHash"); string syncKey = Get(f,"syncKey");
            if (email.Length < 3 || salt.Length == 0 || hash.Length == 0) return "ERROR|account";
            if(requireSyncKey && !ValidateStoreSyncKey(storeId, syncKey)) return "ERROR|sync_key";
            if(role=="seller")
            {
                if(string.IsNullOrWhiteSpace(storeId)) return "ERROR|store_required";
                lock(_sync)
                {
                    XElement store=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));
                    if(store==null) return "ERROR|store_not_found";
                    storeId=S(store,"StoreId");
                }
            }
            lock(_sync)
            {
                XDocument d = LoadFile(_accountsFile,"NexoMarketAccounts","Users");
                XElement root=d.Root.Element("Users");
                XElement old=root.Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase));
                XElement e=new XElement("User",new XElement("Id",Get(f,"id")),new XElement("Name",Get(f,"name")),new XElement("Email",email),new XElement("Phone",Get(f,"phone")),new XElement("Role",role),new XElement("StoreId",storeId),new XElement("Salt",salt),new XElement("PasswordHash",hash),new XElement("CreatedAt",Get(f,"createdAt")));
                if (old != null)
                {
                    string oldId=S(old,"Id"); if(!string.IsNullOrWhiteSpace(oldId))e.SetElementValue("Id",oldId);
                    string oldStore=S(old,"StoreId");
                    if(role=="seller" && !string.IsNullOrWhiteSpace(oldStore))
                    {
                        if(string.IsNullOrWhiteSpace(storeId)) e.SetElementValue("StoreId",oldStore);
                        else if(!string.Equals(oldStore,storeId,StringComparison.OrdinalIgnoreCase)) return "ERROR|account_store_conflict|"+Escape(oldStore);
                    }
                }
                if(old!=null) old.ReplaceWith(e); else root.Add(e);
                SaveDoc(_accountsFile,d);
            }
            return "OK|account";
        }

        private string AccountLines(string storeId, string syncKey)
        {
            StringBuilder b=new StringBuilder();
            storeId=(storeId??"").Trim();
            if(string.IsNullOrWhiteSpace(storeId) || !ValidateStoreSyncKey(storeId, syncKey)) return "";
            lock(_sync)
            {
                XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");
                XElement users=d.Root.Element("Users");
                if(users==null)return "";
                foreach(XElement e in users.Elements("User"))
                {
                    string role=S(e,"Role"); string sid=S(e,"StoreId");
                    if(!string.Equals(sid,storeId,StringComparison.OrdinalIgnoreCase)) continue;
                    b.Append("ACCOUNT|").Append(Escape(S(e,"Id"))).Append('|').Append(Escape(S(e,"Name"))).Append('|').Append(Escape(S(e,"Email"))).Append('|').Append(Escape(S(e,"Phone"))).Append('|').Append(Escape(role)).Append('|').Append(Escape(sid)).Append('|').Append(Escape(S(e,"Salt"))).Append('|').Append(Escape(S(e,"PasswordHash"))).Append('|').Append(Escape(S(e,"CreatedAt"))).Append('\n');
                }
            }
            return b.ToString();
        }

        private bool ValidateStoreSyncKey(string storeId, string syncKey)
        {
            if(string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(syncKey)) return false;
            lock(_sync)
            {
                XElement store=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));
                if(store==null) return false;
                string expected=S(store,"SyncKey");
                return !string.IsNullOrWhiteSpace(expected) && string.Equals(expected,syncKey,StringComparison.Ordinal);
            }
        }

        private CentralUser FindAccount(string email)
        {
            if(string.IsNullOrWhiteSpace(email)) return null;
            lock(_sync)
            {
                XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");
                XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),email.Trim(),StringComparison.OrdinalIgnoreCase));
                return e==null?null:CentralUser.From(e);
            }
        }

        private bool VerifyAccount(string email,string password,out CentralUser user)
        {
            user=FindAccount(email); if(user==null) return false;
            try
            {
                byte[] salt=Convert.FromBase64String(user.Salt); byte[] expected=Convert.FromBase64String(user.PasswordHash);
                using(var kdf=new Rfc2898DeriveBytes(password??"",salt,50000))
                {
                    byte[] actual=kdf.GetBytes(32); if(actual.Length!=expected.Length)return false; int diff=0; for(int i=0;i<actual.Length;i++)diff|=actual[i]^expected[i]; return diff==0;
                }
            } catch { return false; }
        }

        private void CentralLogin(NetworkStream stream,string method,string body,string cookie)
        {
            if(method=="GET") { Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingresar", "<form method='post' action='/login'><input name='email' type='email' placeholder='Correo electrónico' required/><input name='password' type='password' placeholder='Contraseña' required/><button class='btn' type='submit'>INGRESAR</button></form><p class='muted'>¿No tenés cuenta? <a href='/register'>Crear cuenta</a></p>")); return; }
            CentralUser u; Dictionary<string,string> f=Form(body);
            if(!VerifyAccount(Get(f,"email"),Get(f,"password"),out u)) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingreso", "<div class='error'>Correo o contraseña incorrectos.</div><a class='btn' href='/login'>Volver a intentar</a>")); return; }
            string token=Guid.NewGuid().ToString("N"); lock(_sync)_sessions[token]=u;
            string dest=u.Role=="seller"?"/seller":"/buyer"; WriteRedirectCookie(stream,dest,"NexoCentralSession="+token+"; Path=/; HttpOnly; SameSite=Lax");
        }

        private void CentralRegister(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                StringBuilder form=new StringBuilder();
                form.Append("<form method='post' action='/register'><input name='name' placeholder='Nombre completo' required/><input name='email' type='email' placeholder='Correo electrónico' required/><input name='phone' placeholder='Teléfono'/><select name='role' id='role' onchange='toggleStore()'><option value='buyer'>Soy comprador</option><option value='seller'>Soy vendedor</option></select><div id='storeBox'><label class='muted'>Tienda del vendedor</label><input name='storeName' placeholder='Nombre de la nueva tienda (si no tenés una)'/><select name='storeId'><option value=''>Crear una nueva tienda</option>");
                string lines=StoreLines("");
                using(StringReader reader=new StringReader(lines))
                {
                    string line;
                    while((line=reader.ReadLine())!=null)
                    {
                        if(!line.StartsWith("STORE|",StringComparison.OrdinalIgnoreCase))continue;
                        string[] p=line.Split('|'); if(p.Length<12)continue;
                        form.Append("<option value='").Append(E(Uri.UnescapeDataString(p[1]))).Append("'>").Append(E(Uri.UnescapeDataString(p[2]))).Append(" · ").Append(E(Uri.UnescapeDataString(p[4]))).Append("</option>");
                    }
                }
                form.Append("</select></div><input name='password' type='password' placeholder='Contraseña (mínimo 6 caracteres)' required/><button class='btn' type='submit'>CREAR CUENTA</button></form><script>function toggleStore(){var r=document.getElementById('role').value;document.getElementById('storeBox').style.display=r==='seller'?'block':'none';}toggleStore();</script><p class='muted'>La cuenta de vendedor queda vinculada a una única tienda mediante StoreId. ¿Ya tenés cuenta? <a href='/login'>Ingresar</a></p>");
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta",form.ToString())); return;
            }
            Dictionary<string,string> f=Form(body); string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); string role=Get(f,"role")=="seller"?"seller":"buyer"; string storeId=Get(f,"storeId").Trim();
            if(password.Length<6||email.Length<3||email.IndexOf('@')<1) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Completá los datos y usá una contraseña de al menos 6 caracteres.</div><a class='btn' href='/register'>Volver</a>")); return; }
            if(role=="seller")
            {
                lock(_sync)
                {
                    XElement stores=_doc.Root.Element("Stores");
                    if(string.IsNullOrWhiteSpace(storeId))
                    {
                        storeId=Guid.NewGuid().ToString("N").ToUpperInvariant(); string syncKey=Guid.NewGuid().ToString("N");
                        string storeName=Get(f,"storeName").Trim(); if(string.IsNullOrWhiteSpace(storeName)) storeName="Tienda de "+Get(f,"name").Trim();
                        XElement store=new XElement("Store",new XAttribute("UpdatedAt",DateTime.UtcNow.ToString("o")),new XElement("StoreId",storeId),new XElement("SyncKey",syncKey),new XElement("Name",storeName),new XElement("LegalName",storeName),new XElement("Category","Comercio"),new XElement("Address",""),new XElement("City",""),new XElement("Province",""),new XElement("Description","Tienda NexoMarket"),new XElement("Logo",""),new XElement("Slug",Regex.Replace(storeName.ToLowerInvariant(),"[^a-z0-9]+","-").Trim('-')),new XElement("PublicUrl","/store/"+Uri.EscapeDataString(storeId)),new XElement("Active","1"),new XElement("Delivery","1"),new XElement("Pickup","1"),new XElement("Latitude",""),new XElement("Longitude",""));
                        stores.Add(store); Save();
                    }
                    else
                    {
                        XElement store=stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));
                        if(store==null) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>La tienda seleccionada no existe.</div><a class='btn' href='/register'>Volver</a>")); return; }
                        storeId=S(store,"StoreId");
                    }
                }
            }
            if(FindAccount(email)!=null) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Ese correo ya está registrado.</div><a class='btn' href='/login'>Ingresar</a>")); return; }
            byte[] salt=new byte[16]; using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt); string salt64=Convert.ToBase64String(salt); byte[] hash; using(var kdf=new Rfc2898DeriveBytes(password,salt,50000))hash=kdf.GetBytes(32);
            Dictionary<string,string> v=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",Guid.NewGuid().ToString("N")},{"name",Get(f,"name")},{"email",email},{"phone",Get(f,"phone")},{"role",role},{"storeId",storeId},{"salt",salt64},{"passwordHash",Convert.ToBase64String(hash)},{"createdAt",DateTime.UtcNow.ToString("o")}};
            string result=AccountUpsert(v, false); if(!result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)){Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>No se pudo registrar la cuenta: "+E(result)+"</div><a class='btn' href='/register'>Volver</a>"));return;}
            CentralUser u=FindAccount(email); string token=Guid.NewGuid().ToString("N"); lock(_sync)_sessions[token]=u; WriteRedirectCookie(stream,u.Role=="seller"?"/seller":"/buyer","NexoCentralSession="+token+"; Path=/; HttpOnly; SameSite=Lax");
        }

        private void CentralLogout(NetworkStream stream)
        { lock(_sync){} WriteRedirectCookie(stream,"/","NexoCentralSession=deleted; Path=/; Max-Age=0; HttpOnly; SameSite=Lax"); }

        private CentralUser SessionUser(string cookie)
        { if(string.IsNullOrWhiteSpace(cookie))return null; lock(_sync){CentralUser u; return _sessions.TryGetValue(cookie,out u)?u:null;} }

        private void CentralSeller(NetworkStream stream,string cookie,string query)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/login");return;}
            string view=(QueryValue(query,"view")??"").Trim().ToLowerInvariant();
            List<XElement> products; List<XElement> orders; List<XElement> promotions;
            lock(_sync)
            {
                XDocument cd=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                products=cd.Root.Element("Products")==null?new List<XElement>():cd.Root.Element("Products").Elements("Product").Where(x=>S(x,"StoreId")==u.StoreId).OrderBy(x=>S(x,"Name")).ToList();
                promotions=cd.Root.Element("Promotions")==null?new List<XElement>():cd.Root.Element("Promotions").Elements("Promotion").Where(x=>S(x,"StoreId")==u.StoreId).OrderByDescending(x=>S(x,"From")).ToList();
                XDocument od=LoadFile(_ordersFile,"NexoMarketOrders","Orders");
                orders=od.Root.Element("Orders")==null?new List<XElement>():od.Root.Element("Orders").Elements("Order").Where(x=>S(x,"StoreId")==u.StoreId).OrderByDescending(x=>S(x,"CreatedAt")).ToList();
            }
            StringBuilder b=new StringBuilder(AuthShellStart("Seller Center · NexoMarket"));
            b.Append(SellerCenterCss());
            b.Append("<header class='sc-top'><div class='brand'><span>NEXO</span>MARKET <small>SELLER CENTER</small></div><div class='top-actions'><a href='/' class='btn ghost'>Tiendas</a><a href='/store/"+Uri.EscapeDataString(u.StoreId??"")+"' class='btn ghost'>Mi tienda</a><a href='/logout' class='btn ghost'>Salir</a></div></header>");
            b.Append("<aside class='sc-side'><div class='account-box'><div class='avatar'>"+E((u.Name??"V").Length>0?(u.Name??"V").Substring(0,1).ToUpperInvariant():"V")+"</div><b>"+E(u.Name)+"</b><small>"+E(u.Email)+"</small><small>Store ID: "+E(u.StoreId)+"</small></div>");
            b.Append(SellerLink("Resumen","",view)+SellerLink("Pedidos","orders",view)+SellerLink("Productos e inventario","products",view)+SellerLink("Clientes","customers",view)+SellerLink("Analítica","analytics",view)+SellerLink("Finanzas y caja","finance",view)+SellerLink("Marketing","marketing",view)+SellerLink("Reputación","reputation",view)+SellerLink("Herramientas","tools",view)+"</aside>");
            b.Append("<main class='sc-main'>");
            b.Append("<div class='welcome'><div><span class='eyebrow'>CENTRAL DE VENTAS</span><h1>Hola, "+E(u.Name)+" 👋</h1><p>Tu Seller Center está conectado por Store ID con NexoMarket Windows. Los cambios se sincronizan automáticamente.</p></div><div class='account-mini'><b>STORE ID</b><strong>"+E(u.StoreId)+"</strong><small>"+E(u.Email)+"</small></div></div>");
            int pending=orders.Count(x=>S(x,"Status")=="Pendiente"); int delivery=orders.Count(x=>(S(x,"Fulfillment")=="Delivery"||S(x,"Fulfillment")=="En reparto")&&S(x,"Status")!="Entregado"&&S(x,"Status")!="Cancelado");
            decimal sales=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total"))); int low=products.Count(x=>Money(S(x,"Stock"))<=Money(S(x,"MinimumStock"))); int customers=orders.Select(x=>S(x,"CustomerEmail").Trim().ToLowerInvariant()).Where(x=>x.Length>0).Distinct().Count();
            b.Append("<div class='kpis'>"+KpiC("Ventas", "$ "+sales.ToString("N2"), "operaciones válidas", "green")+KpiC("Pedidos pendientes", pending.ToString(), "requieren atención", pending>0?"yellow":"green")+KpiC("Productos", products.Count.ToString(), low+" con stock bajo", low>0?"red":"green")+KpiC("Clientes", customers.ToString(), "compradores únicos", "green")+KpiC("Delivery", delivery.ToString(), "entregas abiertas", delivery>0?"yellow":"green")+"</div>");
            if(view=="orders") b.Append(SellerOrdersView(orders));
            else if(view=="products") b.Append(SellerProductsView(products));
            else if(view=="customers") b.Append(SellerCustomersView(orders));
            else if(view=="analytics") b.Append(SellerAnalyticsView(orders,products));
            else if(view=="finance") b.Append(SellerFinanceView(orders));
            else if(view=="marketing") b.Append(SellerMarketingView(promotions));
            else if(view=="reputation") b.Append(SellerReputationView(orders));
            else if(view=="tools") b.Append(SellerToolsView(u,products,orders));
            else b.Append(SellerSummaryView(orders,products));
            b.Append("</main>").Append(AuthShellEnd()); Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private string SellerLink(string label,string target,string active){return "<a class='sc-link "+(string.Equals(active,target,StringComparison.OrdinalIgnoreCase)?"active":"")+"' href='/seller"+(string.IsNullOrEmpty(target)?"":"?view="+Uri.EscapeDataString(target))+"'>"+label+"</a>";}
        private string KpiC(string title,string value,string hint,string cls){return "<div class='kpi "+cls+"'><span>"+E(title)+"</span><strong>"+E(value)+"</strong><small>"+E(hint)+"</small></div>";}
        private decimal Money(string s){decimal v;return decimal.TryParse((s??"").Replace(",","."),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out v)?v:0m;}
        private string SellerSummaryView(List<XElement> orders,List<XElement> products)
        {
            StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>RESUMEN</span><h2>Actividad del negocio</h2></div><a class='btn ghost' href='/seller?view=orders'>Ver pedidos</a></div><div class='two-col'><section class='card'><h3>Últimos pedidos</h3><table class='table'><tr><th>Pedido</th><th>Cliente</th><th>Total</th><th>Estado</th></tr>");
            foreach(XElement o in orders.Take(8)) b.Append(OrderRow(o));
            if(orders.Count==0)b.Append("<tr><td colspan='4' class='muted'>Todavía no hay pedidos web.</td></tr>");
            b.Append("</table></section><section class='card'><h3>Productos destacados</h3><div class='mini-list'>"); foreach(XElement p in products.Take(8)) b.Append("<div class='mini-row'><div><b>"+E(S(p,"Name"))+"</b><small>SKU "+E(S(p,"SKU"))+" · Stock "+E(S(p,"Stock"))+"</small></div><strong>$ "+Money(S(p,"SalePrice")=="0"?S(p,"Price"):S(p,"SalePrice")).ToString("N0")+"</strong></div>"); if(products.Count==0)b.Append("<p class='muted'>Sin productos sincronizados.</p>"); b.Append("</div></section></div><section class='card'><h3>Acciones rápidas</h3><div class='quick-grid'><a href='/seller?view=products'>📦 Gestionar catálogo e inventario</a><a href='/seller?view=orders'>🧾 Atender pedidos</a><a href='/seller?view=customers'>👥 Ver clientes</a><a href='/seller?view=analytics'>📊 Analizar ventas</a><a href='/seller?view=finance'>💰 Revisar finanzas</a><a href='/seller?view=marketing'>🎯 Marketing y promociones</a></div></section>"); return b.ToString();
        }
        private string OrderRow(XElement o){return "<tr><td><b>#"+E(S(o,"CentralOrderId").Length>8?S(o,"CentralOrderId").Substring(0,8):S(o,"CentralOrderId"))+"</b></td><td>"+E(S(o,"CustomerName"))+"<small>"+E(S(o,"CustomerEmail"))+"</small></td><td><b>$ "+Money(S(o,"Total")).ToString("N2")+"</b></td><td>"+BadgeC(S(o,"Status"))+"</td></tr>";}
        private string BadgeC(string s){string c=(s??"").IndexOf("rech",StringComparison.OrdinalIgnoreCase)>=0||(s??"")=="Cancelado"?"red":(s??"")=="Pendiente"?"yellow":"green";return "<span class='badge "+c+"'>"+E(s)+"</span>";}
        private string SellerOrdersView(List<XElement> orders)
        {
            StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>OPERACIONES</span><h2>Pedidos y estados</h2><p>Los pedidos hechos desde el marketplace llegan al mismo comercio y pueden actualizarse desde este panel.</p></div></div><section class='card table-wrap'><table class='table'><tr><th>Pedido</th><th>Cliente</th><th>Fecha</th><th>Pago</th><th>Total</th><th>Estado</th><th>Acción</th></tr>");
            foreach(XElement o in orders){string id=S(o,"CentralOrderId");b.Append("<tr><td><b>#"+E(id.Length>8?id.Substring(0,8):id)+"</b><small>"+E(S(o,"Fulfillment"))+"</small></td><td>"+E(S(o,"CustomerName"))+"<small>"+E(S(o,"CustomerEmail"))+"</small></td><td>"+E(S(o,"CreatedAt"))+"</td><td>"+BadgeC(S(o,"PaymentStatus"))+"</td><td><b>$ "+Money(S(o,"Total")).ToString("N2")+"</b></td><td>"+BadgeC(S(o,"Status"))+"</td><td><form method='post' action='/seller/order-status' class='inline-form'><input type='hidden' name='id' value='"+E(id)+"'/><select name='status'><option>Pendiente</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Rechazado</option><option>Cancelado</option></select><button class='btn small' type='submit'>Actualizar</button></form></td></tr>");}
            if(orders.Count==0)b.Append("<tr><td colspan='7' class='muted'>No hay pedidos sincronizados.</td></tr>"); b.Append("</table></section>"); return b.ToString();
        }
        private string SellerProductsView(List<XElement> products)
        {
            StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>CATÁLOGO</span><h2>Productos e inventario</h2><p>Estos datos vienen de NexoMarket Windows y se actualizan automáticamente.</p></div><span class='sync-pill'>● SINCRONIZADO</span></div><section class='card table-wrap'><table class='table'><tr><th>Producto</th><th>SKU</th><th>Precio</th><th>Stock</th><th>Mínimo</th><th>Publicación</th></tr>"); foreach(XElement p in products){decimal stock=Money(S(p,"Stock")),min=Money(S(p,"MinimumStock"));b.Append("<tr><td><b>"+E(S(p,"Name"))+"</b><small>"+E(S(p,"Category"))+" · "+E(S(p,"Brand"))+"</small></td><td>"+E(S(p,"SKU"))+"</td><td>$ "+Money(S(p,"SalePrice")=="0"?S(p,"Price"):S(p,"SalePrice")).ToString("N2")+"</td><td><span class='stock "+(stock<=min?"low":"")+"'>"+stock.ToString("N0")+"</span></td><td>"+min.ToString("N0")+"</td><td>"+(S(p,"OnlineEnabled")=="0"?"Pausado":"Publicado")+"</td></tr>");} if(products.Count==0)b.Append("<tr><td colspan='6' class='muted'>No hay productos sincronizados. Verificá que NexoMarket Windows tenga activada la sincronización central.</td></tr>"); b.Append("</table></section><section class='card'><h3>Inventario</h3><p>La fuente principal de stock sigue siendo el panel de Windows. Cada cambio realizado allí se publica al Seller Center sin cambiar el Store ID.</p></section>"); return b.ToString();
        }
        private string SellerCustomersView(List<XElement> orders)
        {
            var groups=orders.Where(x=>!string.IsNullOrWhiteSpace(S(x,"CustomerEmail"))).GroupBy(x=>S(x,"CustomerEmail").Trim().ToLowerInvariant()).OrderByDescending(g=>g.Count()).ToList(); StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>CRM</span><h2>Clientes</h2><p>Compradores que interactuaron con tu tienda online.</p></div></div><section class='card table-wrap'><table class='table'><tr><th>Cliente</th><th>Correo</th><th>Pedidos</th><th>Total comprado</th><th>Última compra</th></tr>"); foreach(var g in groups){XElement last=g.OrderByDescending(x=>S(x,"CreatedAt")).First();b.Append("<tr><td><b>"+E(S(last,"CustomerName"))+"</b></td><td>"+E(g.Key)+"</td><td>"+g.Count()+"</td><td><b>$ "+g.Sum(x=>Money(S(x,"Total"))).ToString("N2")+"</b></td><td>"+E(S(last,"CreatedAt"))+"</td></tr>");} if(groups.Count==0)b.Append("<tr><td colspan='5' class='muted'>Todavía no hay clientes web.</td></tr>"); b.Append("</table></section>"); return b.ToString();
        }
        private string SellerAnalyticsView(List<XElement> orders,List<XElement> products){decimal sales=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal avg=orders.Count==0?0:orders.Average(x=>Money(S(x,"Total")));var by=orders.Where(x=>S(x,"Status")!="Cancelado").GroupBy(x=>S(x,"PaymentMethod")).OrderByDescending(g=>g.Sum(z=>Money(S(z,"Total"))));StringBuilder b=new StringBuilder();b.Append("<div class='section-title'><div><span class='eyebrow'>MÉTRICAS</span><h2>Rendimiento del negocio</h2></div></div><div class='two-col'><section class='card'><h3>Indicadores</h3><div class='metric-list'><div>Ventas acumuladas <b>$ "+sales.ToString("N2")+"</b></div><div>Ticket medio <b>$ "+avg.ToString("N2")+"</b></div><div>Operaciones <b>"+orders.Count+"</b></div><div>Productos publicados <b>"+products.Count(x=>S(x,"OnlineEnabled")!="0")+"</b></div></div></section><section class='card'><h3>Medios de pago</h3><table class='table'><tr><th>Medio</th><th>Operaciones</th><th>Total</th></tr>");foreach(var g in by)b.Append("<tr><td>"+E(g.Key)+"</td><td>"+g.Count()+"</td><td><b>$ "+g.Sum(x=>Money(S(x,"Total"))).ToString("N2")+"</b></td></tr>");b.Append("</table></section></div><section class='card'><h3>Oportunidades</h3><div class='insights'><div>📦 Revisá productos con stock bajo antes de que se agoten.</div><div>🎯 Usá promociones para productos con mucho stock y baja rotación.</div><div>👥 Trabajá recompra sobre clientes con más de un pedido.</div></div></section>");return b.ToString();}
        private string SellerFinanceView(List<XElement> orders){decimal total=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal cash=orders.Where(x=>S(x,"PaymentMethod")=="Efectivo"&&S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal mp=orders.Where(x=>S(x,"PaymentMethod")=="Mercado Pago"&&S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal tr=orders.Where(x=>S(x,"PaymentMethod")=="Transferencia"&&S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));return "<div class='section-title'><div><span class='eyebrow'>FINANZAS</span><h2>Ventas y medios de cobro</h2><p>Resumen central de las operaciones web. La apertura/cierre física de caja continúa en Windows.</p></div></div><div class='kpis mini-kpis'>"+KpiC("Total vendido","$ "+total.ToString("N2"),"operaciones web","green")+KpiC("Efectivo","$ "+cash.ToString("N2"),"ventas","green")+KpiC("Mercado Pago","$ "+mp.ToString("N2"),"ventas","green")+KpiC("Transferencias","$ "+tr.ToString("N2"),"ventas","green")+"</div><section class='card'><h3>Conciliación</h3><p>Los números del Seller Center se alimentan de los pedidos centralizados. La caja de mostrador, apertura, arqueo y retenciones se mantienen sincronizados desde NexoMarket Windows.</p></section>";}
        private string SellerMarketingView(List<XElement> promotions){StringBuilder b=new StringBuilder();b.Append("<div class='section-title'><div><span class='eyebrow'>CRECIMIENTO</span><h2>Marketing y promociones</h2><p>Promociones publicadas desde NexoMarket Windows.</p></div></div><section class='card table-wrap'><table class='table'><tr><th>Promoción</th><th>Precio</th><th>Estado</th><th>Vigencia</th></tr>");foreach(XElement p in promotions)b.Append("<tr><td><b>"+E(S(p,"Name"))+"</b></td><td>$ "+Money(S(p,"PromotionalPrice")).ToString("N2")+"</td><td>"+BadgeC(S(p,"Active")=="0"?"Pausada":"Activa")+"</td><td>"+E(S(p,"From"))+" → "+E(S(p,"To"))+"</td></tr>");if(promotions.Count==0)b.Append("<tr><td colspan='4' class='muted'>No hay promociones sincronizadas.</td></tr>");b.Append("</table></section>");return b.ToString();}
        private string SellerReputationView(List<XElement> orders){int delivered=orders.Count(x=>S(x,"Status")=="Entregado"),cancel=orders.Count(x=>S(x,"Status")=="Cancelado"||S(x,"Status")=="Rechazado");return "<div class='section-title'><div><span class='eyebrow'>REPUTACIÓN</span><h2>Salud de la operación</h2><p>Indicadores construidos con pedidos centralizados.</p></div></div><div class='kpis mini-kpis'>"+KpiC("Entregados",delivered.ToString(),"pedidos finalizados","green")+KpiC("Cancelados/Rechazados",cancel.ToString(),"incidencias","red")+KpiC("Pendientes",orders.Count(x=>S(x,"Status")=="Pendiente").ToString(),"atención","yellow")+"</div><section class='card'><h3>Buenas prácticas</h3><div class='insights'><div>🟢 Actualizá estados rápidamente para que el comprador vea el seguimiento.</div><div>🟡 Mantené stock y precios sincronizados con Windows.</div><div>🔴 Revisá pedidos rechazados o cancelados para detectar problemas.</div></div></section>";}
        private string SellerToolsView(CentralUser u,List<XElement> products,List<XElement> orders){return "<div class='section-title'><div><span class='eyebrow'>HERRAMIENTAS</span><h2>Herramientas del vendedor</h2><p>Accesos rápidos para operar tu cuenta centralizada.</p></div></div><div class='quick-grid'><a href='/store/"+Uri.EscapeDataString(u.StoreId??"")+"'>🌐 Ver escaparate público</a><a href='/seller?view=products'>📦 Catálogo ("+products.Count+")</a><a href='/seller?view=orders'>🧾 Pedidos ("+orders.Count+")</a><a href='/seller?view=customers'>👥 Clientes</a><a href='/seller?view=analytics'>📊 Analítica</a><a href='/seller?view=finance'>💰 Finanzas</a></div><section class='card'><h3>Sincronización</h3><p>La PC con NexoMarket Windows publica productos, promociones, cuentas y recibe pedidos del marketplace cada 30 segundos. El Store ID identifica al comercio y evita cambiar IP por cliente.</p><div class='sync-pill'>● CUENTA: "+E(u.Email)+"</div></section>";}
        private string SellerCenterCss(){return "<style>body{font-family:'Segoe UI',Arial,sans-serif;background:#070b10;color:#fff;margin:0}.wrap{max-width:1500px;margin:auto;padding:18px}.sc-top{display:flex;justify-content:space-between;align-items:center;padding:8px 0 18px}.brand{font-weight:900;font-size:23px}.brand span{color:#39ff66}.brand small{color:#8292a3;font-size:10px;letter-spacing:2px;margin-left:8px}.top-actions{display:flex;gap:8px}.sc-side{position:fixed;width:230px;top:78px;bottom:18px;background:#0c131c;border:1px solid #23364b;border-radius:18px;padding:14px;box-sizing:border-box}.account-box{border-bottom:1px solid #223246;padding:8px 5px 15px;margin-bottom:10px}.avatar{width:42px;height:42px;border-radius:12px;background:#39ff66;color:#061009;display:flex;align-items:center;justify-content:center;font-weight:900;font-size:20px;margin-bottom:8px}.account-box b,.account-box small{display:block}.account-box small{color:#788b9e;margin-top:4px;font-size:11px;word-break:break-word}.sc-link{display:block;color:#a8b8c8;text-decoration:none;padding:11px 12px;border-radius:10px;margin:3px 0;font-weight:700;font-size:13px}.sc-link:hover,.sc-link.active{background:#13221b;color:#39ff66}.sc-main{margin-left:248px}.welcome{display:flex;justify-content:space-between;gap:15px;align-items:center;padding:24px;border:1px solid #26384e;border-radius:20px;background:linear-gradient(135deg,#101923,#0b1118)}.welcome h1{margin:6px 0;font-size:31px}.welcome p,.section-title p,.card p{color:#899bac;line-height:1.5}.account-mini{min-width:180px;padding:14px;border:1px solid #2c445c;border-radius:14px;background:#0b141d}.account-mini b,.account-mini strong,.account-mini small{display:block}.account-mini strong{color:#39ff66;margin:5px 0}.account-mini small{color:#8292a3;word-break:break-word}.eyebrow{color:#39ff66;font-size:10px;letter-spacing:2px;font-weight:900}.kpis{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin:15px 0}.kpi{padding:17px;border:1px solid #26384e;border-radius:16px;background:#0e1721}.kpi span,.kpi small{display:block;color:#8293a5;font-size:11px}.kpi strong{display:block;font-size:25px;margin:8px 0}.kpi.green{border-top:2px solid #39ff66}.kpi.yellow{border-top:2px solid #ffd34d}.kpi.red{border-top:2px solid #ff5967}.section-title{display:flex;justify-content:space-between;align-items:end;margin:22px 2px 12px}.section-title h2{margin:4px 0}.two-col{display:grid;grid-template-columns:1.3fr .7fr;gap:14px}.card{background:#0e1721;border:1px solid #26384e;border-radius:18px;padding:18px;margin-bottom:14px}.table-wrap{overflow:auto}.table{width:100%;border-collapse:collapse}.table th,.table td{padding:12px 10px;border-bottom:1px solid #223144;text-align:left;font-size:12px;vertical-align:middle}.table th{color:#7f92a6;font-size:10px;text-transform:uppercase;letter-spacing:1px}.table td small{display:block;color:#718397;margin-top:4px}.badge{display:inline-block;padding:5px 8px;border-radius:999px;font-size:10px;font-weight:900}.badge.green{background:#153a22;color:#69ff91}.badge.yellow{background:#403816;color:#ffe36b}.badge.red{background:#401b22;color:#ff7380}.stock.low{color:#ff6572;font-weight:900}.mini-list{display:grid;gap:4px}.mini-row{display:flex;justify-content:space-between;gap:10px;padding:11px;border-bottom:1px solid #223144}.mini-row small{display:block;color:#718397;margin-top:4px}.quick-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.quick-grid a{display:block;text-decoration:none;color:#dce8f2;background:#0a121a;border:1px solid #273b50;border-radius:12px;padding:14px;font-weight:800}.quick-grid a:hover{border-color:#39ff66}.sync-pill{color:#39ff66;font-size:11px;font-weight:900}.inline-form{display:flex;gap:5px}.inline-form select{min-width:115px;background:#0a121a;color:#fff;border:1px solid #2b4056;border-radius:8px;padding:7px}.btn{display:inline-block;background:#39ff66;color:#061009;border:0;border-radius:9px;padding:9px 13px;font-weight:900;text-decoration:none;cursor:pointer}.btn.small{padding:7px 9px;font-size:10px}.btn.ghost{background:#101a24;color:#d9e5ef;border:1px solid #2a4056}.metric-list{display:grid;gap:10px}.metric-list div{padding:12px;border:1px solid #24374b;border-radius:10px;color:#91a1b0}.metric-list b{float:right;color:#fff}.insights{display:grid;gap:8px}.insights div{padding:12px;border-radius:10px;background:#0a121a;border:1px solid #223448}@media(max-width:1050px){.kpis{grid-template-columns:repeat(2,1fr)}.sc-side{position:static;width:auto;margin-bottom:14px}.sc-main{margin-left:0}.two-col{grid-template-columns:1fr}}@media(max-width:650px){.wrap{padding:10px}.welcome,.sc-top{align-items:flex-start;flex-direction:column}.kpis,.quick-grid{grid-template-columns:1fr}.top-actions{flex-wrap:wrap}}</style>";}

        private void CentralSellerOrderStatus(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/login");return;}
            string id=Get(f,"id"), status=Get(f,"status"); string msg="";
            if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(status)) msg="Faltan datos del pedido.";
            else
            {
                string result=UpdateOrderStatus(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",u.StoreId},{"centralOrderId",id},{"status",status}});
                msg=result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)?"Estado actualizado correctamente.":"No se pudo actualizar el pedido: "+result;
            }
            WriteRedirect(stream,"/seller?view=orders");
        }

        private void CentralBuyer(NetworkStream stream,string cookie,string query)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="buyer"){WriteRedirect(stream,"/login");return;}
            List<CentralStore> stores=new List<CentralStore>(); string lines=StoreLines(query); using(StringReader reader=new StringReader(lines)){string line;while((line=reader.ReadLine())!=null){string[] p=line.Split('|');if(p.Length<12)continue;CentralStore cs=new CentralStore();cs.StoreId=Uri.UnescapeDataString(p[1]);cs.Name=Uri.UnescapeDataString(p[2]);cs.PublicUrl=Uri.UnescapeDataString(p[3]);cs.City=Uri.UnescapeDataString(p[4]);cs.Province=Uri.UnescapeDataString(p[5]);cs.Category=Uri.UnescapeDataString(p[6]);cs.Delivery=Uri.UnescapeDataString(p[10])=="1";cs.Pickup=Uri.UnescapeDataString(p[11])=="1";stores.Add(cs);}}
            List<XElement> orders=new List<XElement>();lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");orders=d.Root.Element("Orders")==null?new List<XElement>():d.Root.Element("Orders").Elements("Order").Where(x=>string.Equals(S(x,"CustomerEmail"),u.Email,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>S(x,"CreatedAt")).Take(20).ToList();}
            StringBuilder b=new StringBuilder(AuthShellStart("Mi cuenta · NexoMarket"));b.Append(SellerCenterCss());b.Append("<header class='sc-top'><div class='brand'><span>NEXO</span>MARKET <small>BUYER CENTER</small></div><div class='top-actions'><a href='/' class='btn ghost'>Explorar tiendas</a><a href='/logout' class='btn ghost'>Salir</a></div></header><main class='buyer-main'><div class='welcome'><div><span class='eyebrow'>CUENTA DE COMPRADOR</span><h1>Hola, "+E(u.Name)+" 👋</h1><p>Encontrá tiendas, comprá y seguí tus pedidos desde una sola cuenta.</p></div><div class='account-mini'><b>Cuenta</b><strong>Activa</strong><small>"+E(u.Email)+"</small></div></div><div class='kpis'>"+KpiC("Pedidos",orders.Count.ToString(),"historial central","green")+KpiC("En proceso",orders.Count(x=>S(x,"Status")!="Entregado"&&S(x,"Status")!="Cancelado").ToString(),"compras abiertas","yellow")+KpiC("Entregados",orders.Count(x=>S(x,"Status")=="Entregado").ToString(),"compras finalizadas","green")+"</div><div class='section-title'><div><span class='eyebrow'>MARKETPLACE</span><h2>Tiendas disponibles</h2><p>Elegí una tienda para ver productos y comprar.</p></div></div><div class='store-grid'>");
            foreach(CentralStore st in stores)b.Append("<a class='store-card' href='/store/"+Uri.EscapeDataString(st.StoreId)+"'><div class='store-logo'>N</div><div><b>"+E(st.Name)+"</b><small>"+E(st.Category)+" · "+E(st.City)+"</small><span>● Disponible · "+(st.Delivery?"Delivery":"Retiro")+"</span></div></a>");
            if(stores.Count==0)b.Append("<div class='card'><b>No hay tiendas disponibles todavía.</b><p>Volvé a explorar más tarde.</p></div>");
            b.Append("</div><div class='section-title'><div><span class='eyebrow'>MIS COMPRAS</span><h2>Últimos pedidos</h2></div></div><section class='card table-wrap'><table class='table'><tr><th>Pedido</th><th>Tienda</th><th>Fecha</th><th>Estado</th><th>Total</th></tr>");
            foreach(XElement o in orders)b.Append("<tr><td><b>#"+E(S(o,"CentralOrderId").Length>8?S(o,"CentralOrderId").Substring(0,8):S(o,"CentralOrderId"))+"</b></td><td>"+E(S(o,"StoreId"))+"</td><td>"+E(S(o,"CreatedAt"))+"</td><td>"+BadgeC(S(o,"Status"))+"</td><td><b>$ "+Money(S(o,"Total")).ToString("N2")+"</b></td></tr>");
            if(orders.Count==0)b.Append("<tr><td colspan='5' class='muted'>Todavía no realizaste compras. Entrá a una tienda para comenzar.</td></tr>");
            b.Append("</table></section></main>");b.Append("<style>.buyer-main{max-width:1200px;margin:auto}.store-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}.store-card{display:flex;gap:12px;align-items:center;background:#0e1721;border:1px solid #26384e;border-radius:16px;padding:15px;color:#fff;text-decoration:none}.store-card:hover{border-color:#39ff66}.store-logo{width:48px;height:48px;border-radius:12px;background:#07110a;border:1px solid #2d7042;color:#39ff66;display:flex;align-items:center;justify-content:center;font-weight:900;font-size:23px}.store-card b,.store-card small,.store-card span{display:block}.store-card small{color:#8193a5;margin-top:4px}.store-card span{color:#39ff66;font-size:10px;margin-top:6px}@media(max-width:800px){.store-grid{grid-template-columns:1fr}}</style>");b.Append(AuthShellEnd());Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private string AuthPage(string title,string content){return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · NexoMarket</title><style>"+AuthCss()+"</style></head><body><div class='wrap'>"+content+"</div></body></html>";}
        private string AuthShellStart(string title){return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · NexoMarket</title><style>"+AuthCss()+"</style></head><body><div class='wrap'>";}
        private string AuthShellEnd(){return "</div></body></html>";}
        private string AuthCss(){return "body{font-family:'Segoe UI',Arial;background:#080b10;color:#fff;margin:0}.wrap{max-width:850px;margin:auto;padding:30px}.card,.empty{background:#0e1721;border:1px solid #2a4660;border-radius:18px;padding:20px;margin-top:16px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px}input,select,textarea{display:block;width:100%;box-sizing:border-box;background:#0b131c;color:#fff;border:1px solid #2d4660;border-radius:10px;padding:12px;margin:10px 0}textarea{min-height:120px;resize:vertical}.btn{display:inline-block;background:#39ff66;color:#061009;text-decoration:none;border:0;border-radius:10px;padding:11px 16px;font-weight:900;cursor:pointer;margin-top:8px}.btn.alt{background:#13202c;color:#fff;border:1px solid #2d4660}.muted{color:#91a4b6}.error{background:#34151b;border:1px solid #6b2630;padding:14px;border-radius:12px;margin-bottom:14px}.empty{color:#9aabba}";}

        private static string HeaderCookie(string request,string name){string c=HeaderValue(request,"Cookie");foreach(string part in c.Split(';')){string[] p=part.Trim().Split(new[]{'='},2);if(p.Length==2&&string.Equals(p[0],name,StringComparison.OrdinalIgnoreCase))return p[1];}return "";}
        private static string HeaderValue(string request,string name){foreach(string line in (request??"").Split(new[]{"\r\n"},StringSplitOptions.None)){int i=line.IndexOf(':');if(i>0&&string.Equals(line.Substring(0,i).Trim(),name,StringComparison.OrdinalIgnoreCase))return line.Substring(i+1).Trim();}return "";}
        private static void WriteRedirectCookie(NetworkStream stream,string location,string cookie){byte[] data=Encoding.UTF8.GetBytes("<html><body>Redirigiendo...</body></html>");string h="HTTP/1.1 302 Found\r\nLocation: "+location+"\r\nSet-Cookie: "+cookie+"\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: "+data.Length+"\r\nConnection: close\r\n\r\n";byte[] head=Encoding.ASCII.GetBytes(h);stream.Write(head,0,head.Length);stream.Write(data,0,data.Length);stream.Flush();}

        private double ParseDouble(string value) { double d; return double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0d; }

        private sealed class CentralUser
        {
            public string Id="",Name="",Email="",Phone="",Role="buyer",StoreId="",Salt="",PasswordHash="",CreatedAt="";
            public static CentralUser From(XElement e){return new CentralUser{Id=S(e,"Id"),Name=S(e,"Name"),Email=S(e,"Email"),Phone=S(e,"Phone"),Role=S(e,"Role")=="seller"?"seller":"buyer",StoreId=S(e,"StoreId"),Salt=S(e,"Salt"),PasswordHash=S(e,"PasswordHash"),CreatedAt=S(e,"CreatedAt")};}
        }
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
