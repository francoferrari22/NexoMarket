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
        private readonly CentralDatabase _database;

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
            _database = new CentralDatabase();
            // Restaurar TODOS los datos persistentes antes de cargar o guardar cualquier documento.
            // En versiones anteriores Load() podía crear un registro vacío y subirlo a R2 antes
            // de restaurar los datos, borrando las tiendas al reiniciar Render.
            RestoreLatest(_file, "data/nexomarket_stores.xml");
            RestoreLatest(_catalogFile, "data/nexomarket_catalog.xml");
            RestoreLatest(_ordersFile, "data/nexomarket_orders.xml");
            RestoreLatest(_accountsFile, "data/nexomarket_accounts.xml");
            Load();
            EnsureCentralDataFiles();
            MigrateLegacyAccountsToPostgres();
        }

        private void MigrateLegacyAccountsToPostgres()
        {
            if(_database==null||!_database.Enabled)return;
            try
            {
                lock(_sync)
                {
                    XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users"); XElement users=d.Root.Element("Users");
                    if(users==null)return;
                    foreach(XElement e in users.Elements("User"))
                    {
                        string email=S(e,"Email"); if(string.IsNullOrWhiteSpace(email)||_database.GetAccount(email)!=null)continue;
                        _database.UpsertAccount(S(e,"Id"),S(e,"Name"),email,S(e,"Phone"),S(e,"Role"),S(e,"StoreId"),S(e,"Salt"),S(e,"PasswordHash"),S(e,"CreatedAt"));
                    }
                }
            }catch{}
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
                string central = _database == null ? null : _database.GetDocument("stores");
                if (!string.IsNullOrWhiteSpace(central))
                {
                    try { _doc = XDocument.Parse(central); } catch { _doc = NewDoc(); }
                }
                else if (File.Exists(_file))
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
                string xml = _doc.ToString(SaveOptions.None);
                _doc.Save(_file);
                if (_database != null && _database.Enabled) _database.SaveDocument("stores", xml);
                if (_r2 != null && _r2.Enabled) _r2.PutText("data/nexomarket_stores.xml", xml);
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
                        if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) && method == "GET") { ServeMedia(stream, path.Substring(7)); return; }
                        if (path == "/api/central/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", CentralDatabaseStatus()); return; }
                        if (path == "/api/accounts/upsert" && method == "POST") { Write(stream, 200, "text/plain", AccountUpsert(Form(body), true)); return; }
                        if (path == "/api/auth/register-seller" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CentralRegisterSellerApi(Form(body))); return; }
                        if (path == "/api/auth/login" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CentralLoginApi(Form(body))); return; }
                        if (path == "/api/pair/start" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", PairStart(Form(body))); return; }
                        if (path == "/api/pair/complete" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", PairComplete(Form(body))); return; }
                        if (path == "/api/devices/validate" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", DeviceValidate(Form(body))); return; }
                        if (path == "/login") { CentralLogin(stream, method, body, HeaderCookie(request, "NexoCentralSession")); return; }
                        if (path == "/seller-login") { CentralSellerStoreLogin(stream, method, body); return; }
                        if (path == "/register") { CentralRegister(stream, method, body); return; }
                        if (path == "/logout") { CentralLogout(stream); return; }
                        if (path == "/seller") { CentralSeller(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/seller/devices") { CentralSellerDevices(stream, HeaderCookie(request, "NexoCentralSession"), method, body); return; }
                        if (path == "/seller/order-status" && method == "POST") { CentralSellerOrderStatus(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/products/save" && method == "POST") { CentralSellerProductSave(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/media/upload" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CentralSellerMediaUpload(stream, HeaderCookie(request, "NexoCentralSession"), Form(body))); return; }
                        if (path == "/seller/store/save" && method == "POST") { CentralSellerStoreSave(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/products/delete" && method == "POST") { CentralSellerProductDelete(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; } 
                        if (path == "/seller/coupon/save" && method == "POST") { CentralSellerCouponSave(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/buyer") { CentralBuyer(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/api/storage/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StorageStatus()); return; }
                        if (path == "/api/media/upload" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", UploadMedia(Form(body))); return; }
                        if (path == "/api/stores" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StoreLines(query)); return; }
                        if (path == "/api/stores/connect" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StoreConnect(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/sync/diagnostics" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", SyncDiagnostics(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/stores/json" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", StoreJson(query)); return; }
                        if (path == "/api/geocode" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", Geocode(QueryValue(query, "q"))); return; }
                        if (path == "/api/stores/register" && method == "POST") { Write(stream, 200, "text/plain", Register(Form(body))); return; }
                        if (path == "/api/stores/claim" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", ClaimStore(Form(body))); return; }
                        if (path == "/api/products/publish" && method == "POST") { Write(stream, 200, "text/plain", PublishProduct(Form(body))); return; }
                        if (path == "/api/products/delete" && method == "POST") { Write(stream, 200, "text/plain", DeleteProduct(Form(body))); return; }
                        if (path == "/api/promotions/publish" && method == "POST") { Write(stream, 200, "text/plain", PublishPromotion(Form(body))); return; }
                        if (path == "/api/coupons/publish" && method == "POST") { Write(stream, 200, "text/plain", PublishCoupon(Form(body))); return; }
                        if (path == "/api/coupons" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", CouponLines(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
                        if (path == "/api/catalog" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", CatalogJson(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/catalog/live" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", CatalogLiveJson(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/seller/live" && method == "GET") { CentralSellerLive(stream, HeaderCookie(request, "NexoCentralSession")); return; }
                        if (path == "/api/catalog/lines" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", CatalogLines(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
                        if (path == "/api/sync/delta" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", SyncDelta(QueryValue(query, "storeId"), QueryValue(query, "syncKey"), QueryValue(query, "since"))); return; }
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
                    if (total >= 12 * 1024 * 1024) break;
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
            string id = NormalizeStoreId(Get(f, "storeId"));
            if (string.IsNullOrWhiteSpace(id)) return "ERROR|store_id_required";
            string syncKey = ComputeStorePairKey(id);
            lock (_sync)
            {
                XElement stores = _doc.Root.Element("Stores");
                XElement old = stores.Elements("Store").FirstOrDefault(x => string.Equals(S(x, "StoreId"), id, StringComparison.OrdinalIgnoreCase));
                string existingKey = old == null ? "" : S(old, "SyncKey");
                // Store ID is the canonical pairing identity. If the Web created the store
                // first, keep Central's SyncKey; Windows will learn it on the next connect.
                // If Windows created it first, its SyncKey is accepted. Never create a second store.
                if (old != null && !string.IsNullOrWhiteSpace(existingKey)) syncKey = existingKey;
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

        /// <summary>
        /// Bootstrap seguro por StoreId. El StoreId es la credencial de emparejamiento:
        /// no se solicita correo ni contraseña al programa Windows. Si la tienda todavía
        /// no fue registrada en Central, se crea con la clave de sincronización enviada
        /// por la instalación Windows. Si ya existe, nunca reemplaza una clave distinta.
        /// </summary>
        private string ClaimStore(Dictionary<string,string> f)
        {
            string id = NormalizeStoreId(Get(f, "storeId"));
            if (id.Length == 0) return "ERROR|store_id_required";
            string syncKey = ComputeStorePairKey(id);
            lock (_sync)
            {
                XElement stores = _doc.Root.Element("Stores");
                XElement existing = stores.Elements("Store").FirstOrDefault(x => string.Equals(S(x, "StoreId"), id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    string existingKey = S(existing, "SyncKey");
                    // El Store ID es el código de emparejamiento común. Si Windows y Web
                    // llegan con claves internas diferentes, Central conserva la clave canónica
                    // de la tienda y Windows la adoptará al conectar. No se crean dos tiendas.
                    existing.SetElementValue("SyncKey", syncKey);
                    if (string.IsNullOrWhiteSpace(S(existing, "Name"))) existing.SetElementValue("Name", string.IsNullOrWhiteSpace(Get(f,"name")) ? "Tienda NexoMarket" : Get(f,"name"));
                    if (S(existing,"Active") != "1") existing.SetElementValue("Active", "1");
                    existing.SetAttributeValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                    Save();
                    return "OK|existing|" + Escape(id);
                }
                string name = Get(f,"name").Trim();
                if (name.Length == 0) name = "Tienda NexoMarket";
                string slug = Get(f,"slug").Trim();
                if (slug.Length == 0) slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                XElement store = new XElement("Store", new XAttribute("UpdatedAt", DateTime.UtcNow.ToString("o")),
                    new XElement("StoreId", id), new XElement("SyncKey", syncKey), new XElement("Name", name),
                    new XElement("LegalName", Get(f,"legalName")), new XElement("Category", string.IsNullOrWhiteSpace(Get(f,"category")) ? "Comercio" : Get(f,"category")),
                    new XElement("Address", Get(f,"address")), new XElement("City", Get(f,"city")), new XElement("Province", Get(f,"province")),
                    new XElement("Description", string.IsNullOrWhiteSpace(Get(f,"description")) ? "Tienda NexoMarket" : Get(f,"description")),
                    new XElement("Logo", Get(f,"logo")), new XElement("Slug", slug),
                    new XElement("PublicUrl", string.IsNullOrWhiteSpace(Get(f,"publicUrl")) ? "/store/" + Uri.EscapeDataString(id) : Get(f,"publicUrl")),
                    new XElement("Active", "1"), new XElement("Delivery", Get(f,"delivery") == "0" ? "0" : "1"),
                    new XElement("Pickup", Get(f,"pickup") == "0" ? "0" : "1"), new XElement("Latitude", Get(f,"latitude")), new XElement("Longitude", Get(f,"longitude")));
                stores.Add(store);
                Save();
                return "OK|created|" + Escape(id);
            }
        }

        private void EnsureCentralDataFiles()
        {
            lock (_sync)
            {
                EnsureDatasetFile(_accountsFile, "NexoMarketAccounts", "Users", "accounts", "data/nexomarket_accounts.xml");
                EnsureDatasetFile(_catalogFile, "NexoMarketCatalog", "Products", "catalog", "data/nexomarket_catalog.xml");
                EnsureDatasetFile(_ordersFile, "NexoMarketOrders", "Orders", "orders", "data/nexomarket_orders.xml");
            }
        }

        private void EnsureDatasetFile(string file, string rootName, string childName, string dataset, string r2Key)
        {
            string central = _database == null ? null : _database.GetDocument(dataset);
            if (!string.IsNullOrWhiteSpace(central))
            {
                File.WriteAllText(file, central, Encoding.UTF8);
                return;
            }
            if (!File.Exists(file))
                File.WriteAllText(file, new XDocument(new XElement(rootName, new XElement(childName))).ToString(SaveOptions.None), Encoding.UTF8);
            string text = File.ReadAllText(file, Encoding.UTF8);
            if (_database != null && _database.Enabled) _database.SaveDocument(dataset, text);
            if (_r2 != null && _r2.Enabled) _r2.PutText(r2Key, text);
        }

        private XDocument LoadFile(string file, string rootName, string childName)
        {
            string dataset = DatasetForFile(file);
            if (_database != null && _database.Enabled)
            {
                string central = _database.GetDocument(dataset);
                if (!string.IsNullOrWhiteSpace(central))
                {
                    try { File.WriteAllText(file, central, Encoding.UTF8); return XDocument.Parse(central); } catch { }
                }
            }
            try { if (File.Exists(file))
                {
                    XDocument d = XDocument.Load(file);
                    if (_database != null && _database.Enabled) _database.SaveDocument(dataset, d.ToString(SaveOptions.None));
                    return d;
                }
            } catch { }
            XDocument fresh = new XDocument(new XElement(rootName, new XElement(childName)));
            if (_database != null && _database.Enabled) _database.SaveDocument(dataset, fresh.ToString(SaveOptions.None));
            return fresh;
        }

        private string DatasetForFile(string file)
        {
            string name = Path.GetFileName(file) ?? "";
            if (name.IndexOf("stores", StringComparison.OrdinalIgnoreCase) >= 0) return "stores";
            if (name.IndexOf("catalog", StringComparison.OrdinalIgnoreCase) >= 0) return "catalog";
            if (name.IndexOf("orders", StringComparison.OrdinalIgnoreCase) >= 0) return "orders";
            if (name.IndexOf("accounts", StringComparison.OrdinalIgnoreCase) >= 0) return "accounts";
            return name;
        }

        private void SaveDoc(string file, XDocument doc)
        {
            string xml = doc.ToString(SaveOptions.None);
            string temp = file + ".tmp";
            doc.Save(temp);
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
            if (_database != null && _database.Enabled) _database.SaveDocument(DatasetForFile(file), xml);
            if (_r2 != null && _r2.Enabled)
            {
                string key = "data/" + Path.GetFileName(file);
                _r2.PutText(key, xml);
            }
        }

        private string CentralDatabaseStatus()
        {
            return "OK|database=" + ((_database != null && _database.Enabled) ? _database.Status() : "disabled") + "|r2=" + ((_r2 != null && _r2.Enabled) ? "enabled" : "disabled");
        }

        private static string A(XElement e, string name) { XAttribute a = e == null ? null : e.Attribute(name); return a == null ? "" : a.Value; }

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
                string url = MediaUrl(key);
                if (string.IsNullOrWhiteSpace(url)) return "ERROR|PUBLIC_BASE_URL_NOT_CONFIGURED";
                return "OK|" + key + "|" + url;
            }
            catch { return "ERROR|invalid_base64"; }
        }

        private string MediaUrl(string key)
        {
            // La tienda y el endpoint /media viven en el mismo dominio de NexoMarket.
            // Usamos una URL relativa para que las imágenes funcionen aunque Render no
            // tenga PUBLIC_BASE_URL configurada y sin depender del dominio público de R2.
            return "/media/" + Uri.EscapeDataString(key.TrimStart('/')).Replace("%2F", "/");
        }

        private static void Write(NetworkStream stream, int status, string contentType, byte[] data)
        {
            data = data ?? new byte[0];
            string statusText = status == 200 ? "OK" : status == 404 ? "Not Found" : "Error";
            string header = "HTTP/1.1 " + status + " " + statusText + "\r\n" +
                            "Content-Type: " + contentType + "\r\n" +
                            "Cache-Control: public, max-age=31536000, immutable\r\n" +
                            "Content-Length: " + data.Length + "\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private void ServeMedia(NetworkStream stream, string rawKey)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                Write(stream, 404, "text/plain; charset=utf-8", "media_not_found");
                return;
            }
            try
            {
                string key = Uri.UnescapeDataString(rawKey).TrimStart('/');
                if (key.StartsWith("placeholder/", StringComparison.OrdinalIgnoreCase))
                {
                    string category = key.Substring("placeholder/".Length);
                    if (category.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) category=category.Substring(0,category.Length-4);
                    string svg=CategoryPlaceholderSvg(Uri.UnescapeDataString(category));
                    Write(stream,200,"image/svg+xml; charset=utf-8",Encoding.UTF8.GetBytes(svg));
                    return;
                }
                if (_r2 == null || !_r2.Enabled || key.IndexOf("..", StringComparison.Ordinal) >= 0 || !key.StartsWith("stores/", StringComparison.OrdinalIgnoreCase))
                {
                    Write(stream, 404, "text/plain; charset=utf-8", "media_not_found");
                    return;
                }
                byte[] data = _r2.GetBytes(key);
                if (data == null || data.Length == 0)
                {
                    Write(stream, 404, "text/plain; charset=utf-8", "media_not_found");
                    return;
                }
                string ext = Path.GetExtension(key).ToLowerInvariant();
                string contentType = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : ext == ".svg" ? "image/svg+xml" : ext == ".mp4" ? "video/mp4" : ext == ".webm" ? "video/webm" : ext == ".mov" ? "video/quicktime" : "image/jpeg";
                Write(stream, 200, contentType, data);
            }
            catch
            {
                Write(stream, 404, "text/plain; charset=utf-8", "media_not_found");
            }
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
            string storeId = NormalizeStoreId(Get(f, "storeId"));
            string productId = Get(f, "productId").Trim();
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(productId)) return "ERROR|missing";
            if (!ValidateStoreSyncKey(storeId, Get(f,"syncKey"))) return "ERROR|sync_key";
            DateTime incoming = ParseUtcDate(Get(f,"updatedAt"));
            lock (_sync)
            {
                XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                XElement root = d.Root; if (root.Element("Products") == null) root.Add(new XElement("Products"));
                XElement old = root.Element("Products").Elements("Product").FirstOrDefault(x => S(x,"StoreId")==storeId && S(x,"ProductId")==productId);
                if (old != null)
                {
                    DateTime existing = ParseUtcDate(S(old,"UpdatedAt"));
                    if (existing > incoming.AddMilliseconds(10)) return "OK|product|central_newer";
                }
                XElement e = new XElement("Product", new XElement("StoreId",storeId), new XElement("ProductId",productId),
                    new XElement("Name",Get(f,"name")),new XElement("Category",Get(f,"category")),new XElement("Description",Get(f,"description")),
                    new XElement("Price",Get(f,"price")),new XElement("SalePrice",Get(f,"salePrice")),new XElement("Stock",Get(f,"stock")),
                    new XElement("MinimumStock",Get(f,"minimumStock")),new XElement("SKU",Get(f,"sku")),new XElement("Brand",Get(f,"brand")),
                    new XElement("Size",Get(f,"size")),new XElement("Color",Get(f,"color")),new XElement("Barcode",Get(f,"barcode")),
                    new XElement("Active",Get(f,"active")),new XElement("OnlineEnabled",Get(f,"onlineEnabled")),new XElement("ImagePath",Get(f,"imagePath")),
                    new XElement("WebImageUrl",Get(f,"webImageUrl")),new XElement("Slug",Get(f,"slug")),new XElement("PublicDescription",Get(f,"publicDescription")),
                    new XElement("VideoUrl",Get(f,"videoUrl")),new XElement("BarcodeImagePath",Get(f,"barcodeImagePath")),new XElement("Cost",Get(f,"cost")),
                    new XElement("TaxRate",Get(f,"taxRate")),new XElement("UpdatedAt",incoming.ToString("o")),new XElement("Deleted",Get(f,"deleted") == "1" ? "1" : "0"));
                if(old!=null) old.ReplaceWith(e); else root.Element("Products").Add(e); SaveDoc(_catalogFile,d);
            }
            return "OK|product|saved";
        }

        private string DeleteProduct(Dictionary<string,string> f)
        {
            string storeId = NormalizeStoreId(Get(f,"storeId"));
            string productId = Get(f,"productId").Trim();
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(productId)) return "ERROR|missing";
            if (!ValidateStoreSyncKey(storeId, Get(f,"syncKey"))) return "ERROR|sync_key";
            DateTime incoming = ParseUtcDate(Get(f,"updatedAt"));
            lock (_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                XElement products=d.Root.Element("Products");
                XElement old=products==null?null:products.Elements("Product").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"ProductId")==productId);
                if(old==null) return "OK|deleted";
                DateTime existing=ParseUtcDate(S(old,"UpdatedAt"));
                if(existing > incoming.AddMilliseconds(10)) return "OK|delete|central_newer";
                old.SetElementValue("Deleted","1"); old.SetElementValue("Active","0"); old.SetElementValue("OnlineEnabled","0"); old.SetElementValue("UpdatedAt",incoming.ToString("o"));
                SaveDoc(_catalogFile,d);
            }
            return "OK|deleted";
        }

        private string PublishPromotion(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId").Trim(), promotionId=Get(f,"promotionId").Trim(); if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(promotionId)) return "ERROR|missing"; if(!ValidateStoreSyncKey(storeId,Get(f,"syncKey"))) return "ERROR|sync_key";
            lock(_sync){ XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Promotions"); if(d.Root.Element("Promotions")==null)d.Root.Add(new XElement("Promotions")); XElement old=d.Root.Element("Promotions").Elements("Promotion").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"PromotionId")==promotionId);
                XElement e=new XElement("Promotion",new XElement("StoreId",storeId),new XElement("PromotionId",promotionId),new XElement("Name",Get(f,"name")),new XElement("ProductIds",Get(f,"productIds")),new XElement("PromotionalPrice",Get(f,"promotionalPrice")),new XElement("Active",Get(f,"active")),new XElement("From",Get(f,"from")),new XElement("To",Get(f,"to")),new XElement("UpdatedAt",Get(f,"updatedAt")));
                if(old!=null)old.ReplaceWith(e);else d.Root.Element("Promotions").Add(e);SaveDoc(_catalogFile,d); } return "OK|promotion";
        }

        private string PublishCoupon(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId").Trim(), couponId=Get(f,"couponId").Trim(), code=Get(f,"code").Trim().ToUpperInvariant();
            if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(couponId)||string.IsNullOrWhiteSpace(code)) return "ERROR|missing";
            if(!ValidateStoreSyncKey(storeId,Get(f,"syncKey"))) return "ERROR|sync_key";
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                if(d.Root.Element("Coupons")==null)d.Root.Add(new XElement("Coupons"));
                // Idempotencia por StoreId + CouponId, pero también por StoreId + Code.
                // Windows puede tener un Id local distinto al que nació en Seller Center;
                // en ese caso actualizamos el cupón central existente en lugar de duplicarlo.
                XElement old=d.Root.Element("Coupons").Elements("Coupon").FirstOrDefault(x=>
                    S(x,"StoreId")==storeId &&
                    (S(x,"CouponId")==couponId || string.Equals(S(x,"Code"),code,StringComparison.OrdinalIgnoreCase)));
                string canonicalCouponId = old==null ? couponId : S(old,"CouponId");
                XElement e=new XElement("Coupon",
                    new XElement("StoreId",storeId),new XElement("CouponId",canonicalCouponId),
                    new XElement("Code",code),new XElement("Description",Get(f,"description")),
                    new XElement("DiscountPercent",Get(f,"discountPercent")),new XElement("DiscountAmount",Get(f,"discountAmount")),
                    new XElement("MaxUses",Get(f,"maxUses")),new XElement("Used",Get(f,"used")),
                    new XElement("Active",Get(f,"active")),new XElement("From",Get(f,"from")),new XElement("To",Get(f,"to")),
                    new XElement("UpdatedAt",Get(f,"updatedAt")));
                if(old!=null)old.ReplaceWith(e);else d.Root.Element("Coupons").Add(e);
                SaveDoc(_catalogFile,d);
            }
            return "OK|coupon";
        }

        private string CouponLines(string storeId,string syncKey)
        {
            storeId=NormalizeStoreId(storeId);
            if(string.IsNullOrWhiteSpace(storeId)||!ValidateStoreSyncKey(storeId,syncKey))return "";
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                XElement root=d.Root.Element("Coupons"); if(root==null)return "";
                StringBuilder b=new StringBuilder();
                foreach(XElement x in root.Elements("Coupon").Where(x=>S(x,"StoreId")==storeId))
                {
                    b.Append("COUPON|").Append(Escape(S(x,"Code"))).Append('|').Append(Escape(S(x,"Description"))).Append('|')
                     .Append(Escape(S(x,"DiscountPercent"))).Append('|').Append(Escape(S(x,"DiscountAmount"))).Append('|')
                     .Append(Escape(S(x,"MaxUses"))).Append('|').Append(Escape(S(x,"Used"))).Append('|')
                     .Append(Escape(S(x,"Active"))).Append('|').Append(Escape(S(x,"From"))).Append('|')
                     .Append(Escape(S(x,"To"))).Append('|').Append(Escape(S(x,"UpdatedAt"))).Append('\n');
                }
                return b.ToString();
            }
        }

        private string CatalogJson(string storeId)
        {
            if(string.IsNullOrWhiteSpace(storeId)) return "{\"products\":[],\"promotions\":[]}";
            lock(_sync){ XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products"); StringBuilder b=new StringBuilder(); b.Append("{\"storeId\":").Append(JsonString(storeId)).Append(",\"products\":[");
                List<XElement> ps=d.Root.Element("Products")==null?new List<XElement>():d.Root.Element("Products").Elements("Product").Where(x=>S(x,"StoreId")==storeId&&S(x,"Deleted")!="1"&&S(x,"OnlineEnabled")!="0"&&S(x,"Active")!="0").ToList();
                for(int i=0;i<ps.Count;i++){if(i>0)b.Append(',');XElement x=ps[i];b.Append("{\"productId\":").Append(JsonString(S(x,"ProductId"))).Append(",\"name\":").Append(JsonString(S(x,"Name"))).Append(",\"category\":").Append(JsonString(S(x,"Category"))).Append(",\"price\":").Append(JsonString(S(x,"Price"))).Append(",\"salePrice\":").Append(JsonString(S(x,"SalePrice"))).Append(",\"stock\":").Append(JsonString(S(x,"Stock"))).Append(",\"sku\":").Append(JsonString(S(x,"SKU"))).Append(",\"brand\":").Append(JsonString(S(x,"Brand"))).Append(",\"size\":").Append(JsonString(S(x,"Size"))).Append(",\"color\":").Append(JsonString(S(x,"Color"))).Append(",\"image\":").Append(JsonString(ProductImageUrl(x))).Append(",\"description\":").Append(JsonString(S(x,"PublicDescription"))).Append('}');}
                b.Append("],\"promotions\":["); List<XElement> pr=d.Root.Element("Promotions")==null?new List<XElement>():d.Root.Element("Promotions").Elements("Promotion").Where(x=>S(x,"StoreId")==storeId&&S(x,"Active")!="0").ToList();
                for(int i=0;i<pr.Count;i++){if(i>0)b.Append(',');XElement x=pr[i];b.Append("{\"promotionId\":").Append(JsonString(S(x,"PromotionId"))).Append(",\"name\":").Append(JsonString(S(x,"Name"))).Append(",\"productIds\":").Append(JsonString(S(x,"ProductIds"))).Append(",\"price\":").Append(JsonString(S(x,"PromotionalPrice"))).Append(",\"from\":").Append(JsonString(S(x,"From"))).Append(",\"to\":").Append(JsonString(S(x,"To"))).Append('}');}
                b.Append("]}"); return b.ToString(); }
        }

        private string CatalogLiveJson(string storeId)
        {
            storeId = NormalizeStoreId(storeId);
            if (storeId.Length == 0) return "{\"storeId\":\"\",\"updatedAt\":\"\",\"products\":[]}";
            lock (_sync)
            {
                XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                XElement ps = d.Root.Element("Products");
                List<XElement> list = ps == null ? new List<XElement>() : ps.Elements("Product").Where(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase) && S(x,"Deleted") != "1" && S(x,"OnlineEnabled") != "0" && S(x,"Active") != "0").ToList();
                DateTime latest = DateTime.MinValue; foreach (XElement x in list) { DateTime t = ParseUtcDate(S(x,"UpdatedAt")); if (t > latest) latest = t; }
                StringBuilder b = new StringBuilder(); b.Append("{\"storeId\":").Append(JsonString(storeId)).Append(",\"updatedAt\":").Append(JsonString(latest == DateTime.MinValue ? "" : latest.ToString("o"))).Append(",\"products\":[");
                for (int i=0;i<list.Count;i++) { if(i>0)b.Append(','); XElement x=list[i]; string image=ProductImageUrl(x); b.Append("{\"productId\":").Append(JsonString(S(x,"ProductId"))).Append(",\"name\":").Append(JsonString(S(x,"Name"))).Append(",\"category\":").Append(JsonString(S(x,"Category"))).Append(",\"price\":").Append(JsonString(S(x,"Price"))).Append(",\"salePrice\":").Append(JsonString(S(x,"SalePrice"))).Append(",\"stock\":").Append(JsonString(S(x,"Stock"))).Append(",\"sku\":").Append(JsonString(S(x,"SKU"))).Append(",\"brand\":").Append(JsonString(S(x,"Brand"))).Append(",\"image\":").Append(JsonString(image)).Append(",\"description\":").Append(JsonString(S(x,"PublicDescription"))).Append(",\"updatedAt\":").Append(JsonString(S(x,"UpdatedAt"))).Append('}'); }
                b.Append("]}"); return b.ToString();
            }
        }

        private void CentralSellerLive(NetworkStream stream, string cookie)
        {
            CentralUser u = SessionUser(cookie);
            if (u == null || u.Role != "seller") { Write(stream, 401, "application/json; charset=utf-8", "{\"error\":\"unauthorized\"}"); return; }
            string json = CatalogLiveJson(u.StoreId);
            Write(stream, 200, "application/json; charset=utf-8", json);
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
            string couponCode=Get(f,"couponCode").Trim().ToUpperInvariant(), couponDiscount= "0";
            if(!string.IsNullOrWhiteSpace(couponCode))
            {
                lock(_sync)
                {
                    XDocument cd=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                    XElement coupon=cd.Root.Element("Coupons")==null?null:cd.Root.Element("Coupons").Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&string.Equals(S(x,"Code"),couponCode,StringComparison.OrdinalIgnoreCase));
                    if(coupon==null||S(coupon,"Active")=="0")return "ERROR|coupon_invalid";
                    DateTime from,to; if(DateTime.TryParse(S(coupon,"From"),out from)&&from>DateTime.Now)return "ERROR|coupon_not_started"; if(DateTime.TryParse(S(coupon,"To"),out to)&&to<DateTime.Now)return "ERROR|coupon_expired";
                    int used=(int)Money(S(coupon,"Used")), max=(int)Money(S(coupon,"MaxUses")); if(max>0&&used>=max)return "ERROR|coupon_limit";
                    decimal pct=Money(S(coupon,"DiscountPercent")), fixedAmount=Money(S(coupon,"DiscountAmount"));
                    decimal discount=pct>0m?Math.Round(total*pct/100m,2):fixedAmount; discount=Math.Min(total,Math.Max(0m,discount)); total-=discount; couponDiscount=discount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            string itemsJson = Get(f,"itemsJson");
            // La validación/reserva de stock y el consumo del cupón se ejecutan dentro
            // del mismo lock. Así dos pedidos simultáneos no pueden consumir el mismo
            // último uso disponible. Si el stock falla, tampoco se consume el cupón.
            lock(_sync)
            {
                if(!string.IsNullOrWhiteSpace(couponCode))
                {
                    XDocument cd=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                    XElement coupon=cd.Root.Element("Coupons")==null?null:cd.Root.Element("Coupons").Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&string.Equals(S(x,"Code"),couponCode,StringComparison.OrdinalIgnoreCase));
                    if(coupon==null||S(coupon,"Active")=="0")return "ERROR|coupon_invalid";
                    int used=(int)Money(S(coupon,"Used")), max=(int)Money(S(coupon,"MaxUses")); if(max>0&&used>=max)return "ERROR|coupon_limit";
                }
                string stockError = ValidateAndReserveStock(storeId, itemsJson);
                if (!string.IsNullOrWhiteSpace(stockError)) return stockError;
                if(!string.IsNullOrWhiteSpace(couponCode))
                {
                    XDocument cd=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                    XElement coupon=cd.Root.Element("Coupons")==null?null:cd.Root.Element("Coupons").Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&string.Equals(S(x,"Code"),couponCode,StringComparison.OrdinalIgnoreCase));
                    if(coupon==null||S(coupon,"Active")=="0")return "ERROR|coupon_invalid";
                    int used=(int)Money(S(coupon,"Used")), max=(int)Money(S(coupon,"MaxUses")); if(max>0&&used>=max)return "ERROR|coupon_limit";
                    coupon.SetElementValue("Used",(used+1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    coupon.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                    SaveDoc(_catalogFile,cd);
                }
            }
            string centralId=Guid.NewGuid().ToString("N"); string now=DateTime.UtcNow.ToString("o");
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement e=new XElement("Order",new XElement("CentralOrderId",centralId),new XElement("StoreId",storeId),new XElement("CustomerId",Get(f,"customerId")),new XElement("CustomerName",Get(f,"customerName")),new XElement("CustomerEmail",Get(f,"customerEmail")),new XElement("Phone",Get(f,"phone")),new XElement("Fulfillment",Get(f,"fulfillment")),new XElement("Address",Get(f,"address")),new XElement("Notes",Get(f,"notes")),new XElement("Status",string.IsNullOrWhiteSpace(Get(f,"status"))?"Pendiente":Get(f,"status")),new XElement("Total",total.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("CouponCode",couponCode),new XElement("CouponDiscount",couponDiscount),new XElement("PaymentMethod",Get(f,"paymentMethod")),new XElement("PaymentStatus",string.IsNullOrWhiteSpace(Get(f,"paymentStatus"))?"Pendiente":Get(f,"paymentStatus")),new XElement("PaymentReference",Get(f,"paymentReference")),new XElement("PaymentProofPath",Get(f,"paymentProofPath")),new XElement("ShippingCost",Get(f,"shippingCost")),new XElement("TrackingNumber",Get(f,"trackingNumber")),new XElement("Carrier",Get(f,"carrier")),new XElement("ItemsJson",Get(f,"itemsJson")),new XElement("BuyerMessage",Get(f,"buyerMessage")),new XElement("CreatedAt",now),new XElement("Ack", "0")); d.Root.Element("Orders").Add(e);SaveDoc(_ordersFile,d);}
            return "OK|"+centralId+"|"+now+"|"+total.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        private static string ComputeStorePairKey(string storeId)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] data = Encoding.UTF8.GetBytes("NexoMarket.StorePair.v1:" + (storeId ?? "").Trim().Replace(" ", "").ToUpperInvariant());
                byte[] hash = sha.ComputeHash(data); StringBuilder b = new StringBuilder(hash.Length * 2);
                foreach (byte x in hash) b.Append(x.ToString("x2")); return b.ToString();
            }
        }

        private static string NormalizeStoreId(string value)
        {
            return (value ?? "").Trim().Replace(" ", "").ToUpperInvariant();
        }

        /// <summary>
        /// Delta de sincronización para Windows. Devuelve únicamente productos modificados
        /// desde el cursor indicado y un cursor central nuevo. Central sigue siendo la fuente
        /// de verdad; Windows no vuelve a descargar el catálogo completo cada ciclo.
        /// </summary>
        private string SyncDelta(string storeId, string syncKey, string since)
        {
            storeId = NormalizeStoreId(storeId ?? "");
            if (string.IsNullOrWhiteSpace(storeId) || !ValidateStoreSyncKey(storeId, syncKey)) return "ERROR|sync_key";
            DateTime cursor = ParseUtcDate(since);
            DateTime serverNow = DateTime.UtcNow;
            StringBuilder b = new StringBuilder();
            b.Append("SYNC|").Append(Escape(serverNow.ToString("o"))).Append('\n');
            lock (_sync)
            {
                XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                XElement products = d.Root.Element("Products");
                if (products != null)
                {
                    foreach (XElement x in products.Elements("Product").Where(x => string.Equals(S(x, "StoreId"), storeId, StringComparison.OrdinalIgnoreCase)))
                    {
                        DateTime updated = ParseUtcDate(S(x, "UpdatedAt"));
                        if (cursor != DateTime.MinValue && updated <= cursor) continue;
                        if (S(x, "Deleted") == "1")
                        {
                            b.Append("DELETED|").Append(Escape(S(x, "ProductId"))).Append('|').Append(Escape(S(x, "UpdatedAt"))).Append('\n');
                            continue;
                        }
                        b.Append("PRODUCT|").Append(Escape(S(x,"ProductId"))).Append('|').Append(Escape(S(x,"Name"))).Append('|').Append(Escape(S(x,"Category"))).Append('|').Append(Escape(S(x,"Description"))).Append('|').Append(Escape(S(x,"Price"))).Append('|').Append(Escape(S(x,"SalePrice"))).Append('|').Append(Escape(S(x,"Stock"))).Append('|').Append(Escape(S(x,"MinimumStock"))).Append('|').Append(Escape(S(x,"SKU"))).Append('|').Append(Escape(S(x,"Brand"))).Append('|').Append(Escape(S(x,"Size"))).Append('|').Append(Escape(S(x,"Color"))).Append('|').Append(Escape(S(x,"Active"))).Append('|').Append(Escape(S(x,"OnlineEnabled"))).Append('|').Append(Escape(S(x,"ImagePath"))).Append('|').Append(Escape(S(x,"WebImageUrl"))).Append('|').Append(Escape(S(x,"Slug"))).Append('|').Append(Escape(S(x,"PublicDescription"))).Append('|').Append(Escape(S(x,"VideoUrl"))).Append('|').Append(Escape(S(x,"BarcodeImagePath"))).Append('|').Append(Escape(S(x,"Cost"))).Append('|').Append(Escape(S(x,"TaxRate"))).Append('|').Append(Escape(S(x,"UpdatedAt"))).Append('|').Append(Escape(S(x,"Deleted"))).Append('\n');
                    }
                }
            }
            return b.ToString();
        }

        private string CatalogLines(string storeId, string syncKey)
        {
            storeId = NormalizeStoreId(storeId ?? "");
            if (string.IsNullOrWhiteSpace(storeId) || !ValidateStoreSyncKey(storeId, syncKey)) return "";
            StringBuilder b = new StringBuilder();
            lock (_sync)
            {
                XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                XElement products = d.Root.Element("Products");
                if (products == null) return "";
                foreach (XElement x in products.Elements("Product").Where(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase)))
                {
                    if (S(x,"Deleted") == "1")
                    {
                        b.Append("DELETED|").Append(Escape(S(x,"ProductId"))).Append('|').Append(Escape(S(x,"UpdatedAt"))).Append('\n');
                        continue;
                    }
                    b.Append("PRODUCT|").Append(Escape(S(x,"ProductId"))).Append('|').Append(Escape(S(x,"Name"))).Append('|').Append(Escape(S(x,"Category"))).Append('|').Append(Escape(S(x,"Description"))).Append('|').Append(Escape(S(x,"Price"))).Append('|').Append(Escape(S(x,"SalePrice"))).Append('|').Append(Escape(S(x,"Stock"))).Append('|').Append(Escape(S(x,"MinimumStock"))).Append('|').Append(Escape(S(x,"SKU"))).Append('|').Append(Escape(S(x,"Brand"))).Append('|').Append(Escape(S(x,"Size"))).Append('|').Append(Escape(S(x,"Color"))).Append('|').Append(Escape(S(x,"Active"))).Append('|').Append(Escape(S(x,"OnlineEnabled"))).Append('|').Append(Escape(S(x,"ImagePath"))).Append('|').Append(Escape(S(x,"WebImageUrl"))).Append('|').Append(Escape(S(x,"Slug"))).Append('|').Append(Escape(S(x,"PublicDescription"))).Append('|').Append(Escape(S(x,"VideoUrl"))).Append('|').Append(Escape(S(x,"BarcodeImagePath"))).Append('|').Append(Escape(S(x,"Cost"))).Append('|').Append(Escape(S(x,"TaxRate"))).Append('|').Append(Escape(S(x,"UpdatedAt"))).Append('|').Append(Escape(S(x,"Deleted"))).Append('\n');
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
                b.Append("STORE|").Append(Escape(S(e, "StoreId"))).Append('|').Append(Escape(S(e, "Name"))).Append('|').Append(Escape(publicUrl)).Append('|').Append(Escape(S(e, "City"))).Append('|').Append(Escape(S(e, "Province"))).Append('|').Append(Escape(S(e, "Category"))).Append('|').Append(Escape(S(e, "Latitude"))).Append('|').Append(Escape(S(e, "Longitude"))).Append('|').Append(Escape(S(e, "Active"))).Append('|').Append(Escape(S(e, "Delivery"))).Append('|').Append(Escape(S(e, "Pickup"))).Append('|').Append(Escape(e.Attribute("UpdatedAt") == null ? "" : e.Attribute("UpdatedAt").Value)).Append('|').Append(Escape(x.DistanceKm >= 999999d ? "" : x.DistanceKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))).Append('|').Append(Escape(S(e, "Logo"))).Append('\n');
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
            string logo=string.IsNullOrWhiteSpace(S(store,"Logo"))?"<div class='store-avatar'>N</div>":"<img class='store-avatar-img' src='"+E(S(store,"Logo"))+"' alt='"+E(S(store,"Name"))+"'/>";
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta http-equiv='Cache-Control' content='no-store'><title>").Append(E(S(store,"Name"))).Append(" · NexoMarket</title><style>body{font-family:'Segoe UI',Arial;background:#070a10;color:#fff;margin:0}.wrap{max-width:1240px;margin:auto;padding:20px 20px 100px}.top{display:flex;justify-content:space-between;align-items:center;margin-bottom:16px}.brand{font-weight:900;font-size:24px}.brand span{color:#39ff66}.nav a{color:#dbe6f0;text-decoration:none;margin-left:14px}.hero{display:flex;gap:18px;align-items:center;background:linear-gradient(135deg,#101923,#15102a);border:1px solid #2f3e55;border-radius:24px;padding:24px;box-shadow:0 16px 40px rgba(84,42,150,.16)}.store-avatar{width:82px;height:82px;border-radius:20px;background:#0b1118;border:1px solid #9b5cff;color:#c59cff;display:flex;align-items:center;justify-content:center;font-size:34px;font-weight:900}.store-avatar-img{width:82px;height:82px;border-radius:20px;object-fit:cover;border:1px solid #9b5cff}.hero h1{margin:0 0 6px;font-size:31px}.muted{color:#91a3b5}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px;margin-top:18px}.card{background:#0f1721;border:1px solid #28394e;border-radius:18px;padding:16px;min-height:220px;position:relative}.product-img{width:100%;height:170px;object-fit:cover;border-radius:13px;background:#0a1119;margin-bottom:12px}.price{font-size:21px;font-weight:900;margin-top:9px}.sale{color:#39ff66}.btn{background:#39ff66;color:#061009;border:0;border-radius:10px;padding:10px 14px;font-weight:900;cursor:pointer}.btn.violet{background:#9b5cff;color:#fff}.empty{padding:22px;color:#8b99a9;border:1px dashed #3c4b60;border-radius:16px}.promos{margin-top:25px}.promo-card{border-color:#7b4bd1}.cart-fab{position:fixed;right:22px;bottom:22px;z-index:30;background:#9b5cff;color:#fff;border:0;border-radius:999px;padding:14px 20px;font-weight:900;box-shadow:0 10px 30px rgba(0,0,0,.35);cursor:pointer}.cart-panel{position:fixed;right:20px;bottom:78px;width:min(430px,calc(100vw - 40px));max-height:78vh;overflow:auto;z-index:29;background:#0c141e;border:1px solid #7650bd;border-radius:20px;padding:18px;box-shadow:0 18px 50px rgba(0,0,0,.5);display:none}.cart-panel.open{display:block}.cart input,.cart select{background:#0d141d;color:#fff;border:1px solid #2a3b51;border-radius:9px;padding:10px;margin:4px;width:calc(100% - 18px)}.item{display:flex;justify-content:space-between;border-bottom:1px solid #223143;padding:8px 0}.live{color:#8dffac;font-size:11px;font-weight:900}.section{margin-top:24px}@media(max-width:950px){.grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.wrap{padding:12px 12px 100px}.top,.hero{align-items:flex-start;flex-direction:column}.grid{grid-template-columns:1fr}.nav a{margin-left:0;margin-right:12px}.product-img{height:200px}}.card{transition:transform .28s cubic-bezier(.2,.8,.2,1),box-shadow .28s,border-color .28s;transform-style:preserve-3d;overflow:hidden}.card:hover{border-color:rgba(255,255,255,.28);box-shadow:0 0 0 1px rgba(255,255,255,.05),0 18px 45px rgba(57,255,102,.10),0 0 30px rgba(155,92,255,.12)}.mega{position:relative}.mega-panel{position:absolute;top:calc(100% + 12px);left:0;width:min(760px,calc(100vw - 32px));padding:18px;border:1px solid rgba(255,255,255,.14);border-radius:20px;background:rgba(5,7,10,.88);backdrop-filter:blur(22px);-webkit-backdrop-filter:blur(22px);box-shadow:0 24px 70px rgba(0,0,0,.55),0 0 35px rgba(57,255,102,.08);opacity:0;visibility:hidden;transform:translateY(-8px) scale(.98);transition:.22s ease;z-index:50}.mega:hover .mega-panel,.mega:focus-within .mega-panel{opacity:1;visibility:visible;transform:none}.mega-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}.mega-grid a{display:block;padding:12px;border:1px solid rgba(255,255,255,.08);border-radius:12px;color:#eaf2f8;text-decoration:none;background:rgba(255,255,255,.025)}.mega-grid a:hover{border-color:#39ff66}.quick-modal{position:fixed;inset:0;display:none;place-items:center;background:rgba(0,0,0,.64);backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);z-index:100}.quick-modal.open{display:grid}.quick-dialog{width:min(880px,calc(100vw - 28px));background:rgba(8,11,15,.92);border:1px solid rgba(255,255,255,.18);border-radius:24px;padding:20px;box-shadow:0 30px 100px rgba(0,0,0,.7),0 0 45px rgba(57,255,102,.10);animation:scaleIn .24s ease}.quick-grid{display:grid;grid-template-columns:1fr 1fr;gap:22px}.quick-dialog img{width:100%;height:360px;object-fit:cover;border-radius:18px;background:#080c10}.quick-close{float:right;border:1px solid rgba(255,255,255,.18);background:#0d131a;color:#fff;border-radius:999px;width:38px;height:38px;cursor:pointer}@keyframes scaleIn{from{opacity:0;transform:scale(.94)}to{opacity:1;transform:scale(1)}}.neon-cta:hover{box-shadow:0 0 22px rgba(57,255,102,.35),inset 0 0 16px rgba(255,255,255,.18)}@media(max-width:700px){.mega-grid,.quick-grid{grid-template-columns:1fr 1fr}.quick-dialog img{height:260px}}</style></head><body><div class='wrap'><div class='top'><div class='brand'><span>NEXO</span>MARKET</div><div class='nav'><a href='/'>Tiendas</a><span class='mega'><a href='#' onclick='return false'>Categorías ▾</a><span class='mega-panel'><b style='display:block;margin-bottom:10px;color:#fff'>EXPLORAR CATEGORÍAS</b><span class='mega-grid'><a href='#'>Tecnología<br><small class='muted'>Celulares · PC · Gaming</small></a><a href='#'>Moda<br><small class='muted'>Ropa · Calzado · Accesorios</small></a><a href='#'>Hogar<br><small class='muted'>Muebles · Cocina · Deco</small></a><a href='#'>Ofertas<br><small class='muted'>Flash · Liquidación</small></a></span></span></span><a href='/seller-login'>Ingresar como vendedor</a></div></div><section class='hero'>").Append(logo).Append("<div><div class='live'>● TIENDA CENTRAL EN TIEMPO REAL</div><h1>").Append(E(S(store,"Name"))).Append("</h1><div class='muted'>").Append(E(S(store,"Category"))).Append(" · ").Append(E(S(store,"City"))).Append("</div><p class='muted'>").Append(E(S(store,"Description"))).Append("</p></div></section><div class='section'><div style='display:flex;justify-content:space-between;align-items:end'><div><h2>Productos</h2><div class='muted'>El catálogo se actualiza automáticamente desde NexoMarket Central.</div></div><span id='liveState' class='live'>● LIVE</span></div><div id='products' class='grid'>");
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                List<XElement> ps=d.Root.Element("Products")==null?new List<XElement>():d.Root.Element("Products").Elements("Product").Where(x=>S(x,"StoreId")==realId&&S(x,"Deleted")!="1"&&S(x,"OnlineEnabled")!="0"&&S(x,"Active")!="0").ToList();
                if(ps.Count==0)b.Append("<div class='empty'>Esta tienda todavía no publicó productos.</div>");
                foreach (XElement x in ps)
                {
                    string id = S(x, "ProductId");
                    string price = S(x, "Price");
                    string sale = S(x, "SalePrice");
                    string shown = string.IsNullOrWhiteSpace(sale) || sale == "0" ? price : sale;
                    string img = ProductImageUrl(x);
                    string image = string.IsNullOrWhiteSpace(img) ? "" : "<img class='product-img' src='" + E(img) + "' alt='" + E(S(x, "Name")) + "' loading='lazy'/>";
                    string onclick = "add(" + JsonString(id) + "," + JsonString(S(x, "Name")) + "," + JsonNumber(shown) + ")";
                    b.Append("<div class='card'>").Append(image).Append("<h3>").Append(E(S(x, "Name"))).Append("</h3><div class='muted'>")
                        .Append(E(S(x, "Category"))).Append(" · ").Append(E(S(x, "Brand"))).Append("</div><div class='muted'>")
                        .Append(E(S(x, "PublicDescription"))).Append("</div><div class='price ")
                        .Append(string.IsNullOrWhiteSpace(sale) || sale == "0" ? "" : "sale").Append("'>$ ").Append(E(shown))
                        .Append("</div><div class='muted'>Stock: ").Append(E(S(x, "Stock"))).Append("</div><button class='btn' onclick='")
                        .Append(onclick).Append("'>AGREGAR</button><button class='btn violet neon-cta' style='margin-left:6px' onclick='quickView(this)' data-name='").Append(E(S(x,"Name"))).Append("' data-price='").Append(E(shown)).Append("' data-image='").Append(E(img)).Append("' data-desc='").Append(E(S(x,"PublicDescription"))).Append("'>VISTA RÁPIDA</button></div>");
                }
            }
            b.Append("</div></div><section class='promos section'><h2>Promociones vigentes</h2><div id='promos' class='grid'>");
            lock (_sync)
            {
                XDocument pd = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                List<XElement> promotions = pd.Root.Element("Promotions") == null ? new List<XElement>() : pd.Root.Element("Promotions").Elements("Promotion")
                    .Where(x => S(x, "StoreId") == realId && S(x, "Active") != "0").ToList();
                if (promotions.Count == 0) b.Append("<div class='empty'>No hay promociones vigentes.</div>");
                foreach (XElement p in promotions)
                {
                    string pid = S(p, "PromotionId");
                    string pids = S(p, "ProductIds");
                    string pname = S(p, "Name");
                    string pp = S(p, "PromotionalPrice");
                    string onclick = "addPromotion(" + JsonString(pid) + "," + JsonString(pname) + "," + JsonNumber(pp) + "," + JsonString(pids) + ")";
                    b.Append("<div class='card promo-card'><div class='sale'>OFERTA</div><h3>").Append(E(pname))
                        .Append("</h3><div class='price sale'>$ ").Append(E(pp)).Append("</div><div class='muted'>Vigencia: ")
                        .Append(E(S(p, "From"))).Append(" → ").Append(E(S(p, "To"))).Append("</div><button class='btn violet' onclick='")
                        .Append(onclick).Append("'>COMPRAR PROMOCIÓN</button></div>");
                }
            }
            b.Append("</div></section></div><section class='section'><h2>Cupones disponibles</h2><div id='coupons' class='grid'>");
            lock(_sync)
            {
                XDocument cd=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                List<XElement> coupons=cd.Root.Element("Coupons")==null?new List<XElement>():cd.Root.Element("Coupons").Elements("Coupon").Where(x=>S(x,"StoreId")==realId&&S(x,"Active")!="0"&&DateTime.TryParse(S(x,"From"),out var cf)&&cf<=DateTime.Now&&DateTime.TryParse(S(x,"To"),out var ct)&&ct>=DateTime.Now&&(S(x,"MaxUses")=="0"||Money(S(x,"Used"))<Money(S(x,"MaxUses")))).ToList();
                if(coupons.Count==0)b.Append("<div class='empty'>No hay cupones disponibles en este momento.</div>");
                foreach(XElement c in coupons)
                {
                    string discount=Money(S(c,"DiscountPercent"))>0?Money(S(c,"DiscountPercent")).ToString("0.##")+"%":"$ "+Money(S(c,"DiscountAmount")).ToString("N2");
                    b.Append("<div class='card'><div class='sale'>CUPÓN</div><h3>"+E(S(c,"Code"))+"</h3><div class='price sale'>"+E(discount)+" OFF</div><div class='muted'>"+E(S(c,"Description"))+"</div><button class='btn violet' onclick='useCoupon("+JsonString(S(c,"Code"))+")'>USAR CUPÓN</button></div>");
                }
            }
            b.Append("</div></section><div id='quickModal' class='quick-modal' onclick='if(event.target===this)closeQuick()'><div class='quick-dialog'><button class='quick-close' onclick='closeQuick()'>×</button><div class='quick-grid'><div><img id='quickImg' alt='Producto'/></div><div><div class='live'>● VISTA RÁPIDA</div><h2 id='quickName'></h2><div class='price sale' id='quickPrice'></div><p class='muted' id='quickDesc'></p><button id='quickBuy' class='btn neon-cta'>AGREGAR AL CARRITO</button></div></div></div></div><button class='cart-fab' onclick='toggleCart()'>🛒 Carrito <span id='cartCount'>0</span></button>");<aside id='cartPanel' class='cart-panel cart'><h2>Tu carrito</h2><div id='cartItems'>Carrito vacío.</div><h3>Total: $ <span id='total'>0</span></h3><form onsubmit='return sendOrder(event)'><input type='hidden' id='storeId' value='").Append(E(realId)).Append("'/><input id='name' placeholder='Nombre completo' required/><input id='email' type='email' placeholder='Correo electrónico'/><input id='phone' placeholder='Teléfono'/><select id='fulfillment'><option>Delivery</option><option>Retiro</option></select><input id='address' placeholder='Dirección / punto de retiro'/><select id='paymentMethod'><option>Transferencia</option><option>Mercado Pago</option><option>Efectivo</option></select><input id='paymentReference' placeholder='Referencia de pago (opcional)'/><input id='notes' placeholder='Notas para el vendedor'/><input id='couponCode' placeholder='Código de cupón (opcional)'/><button class='btn' type='submit'>CONFIRMAR PEDIDO</button></form><hr style='border-color:#253447;margin:18px 0'><h3>Seguimiento</h3><div id='orderStatus' class='muted'>Después de confirmar un pedido aparecerá aquí.</div><button class='btn' id='confirmReceived' style='display:none' onclick='confirmReceived()'>CONFIRMAR RECEPCIÓN</button><button class='btn violet' style='margin-left:8px' onclick='loadHistory()'>VER HISTORIAL</button><div id='history' class='muted' style='margin-top:12px'></div></aside><script>function quickView(el){document.getElementById('quickName').innerHTML=el.getAttribute('data-name')||'';document.getElementById('quickPrice').innerHTML='$ '+(el.getAttribute('data-price')||'0');document.getElementById('quickDesc').innerHTML=el.getAttribute('data-desc')||'Producto disponible en la tienda.';var img=el.getAttribute('data-image')||'';document.getElementById('quickImg').src=img;document.getElementById('quickBuy').onclick=function(){add('__quick_'+Date.now(),el.getAttribute('data-name')||'Producto',parseFloat(el.getAttribute('data-price')||0));closeQuick()};document.getElementById('quickModal').classList.add('open')}function closeQuick(){document.getElementById('quickModal').classList.remove('open')}document.addEventListener('keydown',function(e){if(e.key==='Escape')closeQuick()});var cart=[],lastOrderId='';function toggleCart(){document.getElementById('cartPanel').classList.toggle('open')}function addPromotion(id,name,price,productIds){var key='promo:'+id,x=cart.filter(function(i){return i.id===key})[0];if(x)x.qty++;else cart.push({id:key,name:name,price:price,qty:1,promotionId:id,productIds:productIds});render();toggleOpen()}function add(id,name,price){var x=cart.filter(function(i){return i.id===id})[0];if(x)x.qty++;else cart.push({id:id,name:name,price:price,qty:1});render();toggleOpen()}function toggleOpen(){document.getElementById('cartPanel').classList.add('open')}function render(){var h='',t=0,c=0;cart.forEach(function(i){h+='<div class=item><span>'+i.name+' × '+i.qty+'</span><b>$ '+(i.price*i.qty).toFixed(2)+'</b></div>';t+=i.price*i.qty;c+=i.qty});document.getElementById('cartItems').innerHTML=h||'Carrito vacío.';document.getElementById('total').innerHTML=t.toFixed(2);document.getElementById('cartCount').innerHTML=c}function refreshCatalog(){var x=new XMLHttpRequest();x.open('GET','/api/catalog/live?storeId='+encodeURIComponent(document.getElementById('storeId').value),true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);var stamp=d.updatedAt||'';if(window.lastCatalogStamp===undefined)window.lastCatalogStamp=stamp;else if(stamp!==window.lastCatalogStamp){window.lastCatalogStamp=stamp;location.reload();}document.getElementById('liveState').innerHTML='● LIVE '+(stamp?new Date(stamp).toLocaleTimeString():'');}catch(e){}}};x.send()}function pollStatus(){if(!lastOrderId)return;var u='/api/orders/status?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId),x=new XMLHttpRequest();x.open('GET',u,true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);if(d.error)return;document.getElementById('orderStatus').innerHTML='Pedido <b>'+d.centralOrderId+'</b> · Estado: <b>'+d.status+'</b><br>Total: $ '+d.total+(d.updatedAt?' · Actualizado: '+new Date(d.updatedAt).toLocaleString():'');document.getElementById('confirmReceived').style.display=(d.status==='Entregado'&&!d.buyerConfirmed)?'inline-block':'none'}catch(e){}}};x.send()}function loadHistory(){var email=document.getElementById('email').value;if(!email){alert('Ingresá tu correo para consultar el historial.');return}var x=new XMLHttpRequest();x.open('GET','/api/orders/history?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&email='+encodeURIComponent(email),true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var a=JSON.parse(x.responseText),h='';a.forEach(function(o){h+='<div class=item><span>'+o.centralOrderId+' · '+o.status+'</span><b>$ '+o.total+'</b></div>'});document.getElementById('history').innerHTML=h||'No hay pedidos para este correo.'}catch(e){document.getElementById('history').innerHTML='No se pudo cargar el historial.'}}};x.send()}function confirmReceived(){var data='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId)+'&email='+encodeURIComponent(document.getElementById('email').value),x=new XMLHttpRequest();x.open('POST','/api/orders/confirm',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){alert(x.responseText.indexOf('OK|')===0?'Recepción confirmada.':'No se pudo confirmar.');pollStatus()}};x.send(data)}function useCoupon(code){document.getElementById('couponCode').value=code;toggleOpen()}function sendOrder(e){e.preventDefault();if(!cart.length){alert('Agregá al menos un producto.');return false}var data={storeId:document.getElementById('storeId').value,customerName:document.getElementById('name').value,customerEmail:document.getElementById('email').value,phone:document.getElementById('phone').value,fulfillment:document.getElementById('fulfillment').value,address:document.getElementById('address').value,paymentMethod:document.getElementById('paymentMethod').value,paymentReference:document.getElementById('paymentReference').value,notes:document.getElementById('notes').value,couponCode:document.getElementById('couponCode').value,total:document.getElementById('total').innerHTML,itemsJson:JSON.stringify(cart)},body=[];Object.keys(data).forEach(function(k){body.push(encodeURIComponent(k)+'='+encodeURIComponent(data[k]))});var x=new XMLHttpRequest();x.open('POST','/api/orders/create',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.status===200&&x.responseText.indexOf('OK|')===0){lastOrderId=x.responseText.split('|')[1];alert('Pedido enviado. Número central: '+lastOrderId);cart=[];render();pollStatus()}else alert('No se pudo enviar el pedido: '+(x.responseText||'sin respuesta'))}};x.send(body.join('&'));return false}render();setInterval(refreshCatalog,1800);setInterval(pollStatus,4000);</script></body></html>");
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
                using (StringReader reader = new StringReader(lines)) { string line; while ((line = reader.ReadLine()) != null) { string[] p = line.Split('|'); if (p.Length < 12) continue; CentralStore cs = new CentralStore(); cs.StoreId = Uri.UnescapeDataString(p[1]); cs.Name = Uri.UnescapeDataString(p[2]); cs.PublicUrl = Uri.UnescapeDataString(p[3]); cs.City = Uri.UnescapeDataString(p[4]); cs.Province = Uri.UnescapeDataString(p[5]); cs.Category = Uri.UnescapeDataString(p[6]); cs.Latitude = ParseDouble(Uri.UnescapeDataString(p[7])); cs.Longitude = ParseDouble(Uri.UnescapeDataString(p[8])); cs.Active = Uri.UnescapeDataString(p[9]) == "1"; cs.Delivery = Uri.UnescapeDataString(p[10]) == "1"; cs.Pickup = Uri.UnescapeDataString(p[11]) == "1"; cs.Distance = p.Length > 13 ? ParseDouble(Uri.UnescapeDataString(p[13])) : 0d; cs.Logo = p.Length > 14 ? Uri.UnescapeDataString(p[14]) : ""; stores.Add(cs); } }
            }
            StringBuilder b = new StringBuilder();
            string locationTitle = hasCoords ? (string.IsNullOrWhiteSpace(q) ? "Tu ubicación" : q) : (string.IsNullOrWhiteSpace(q) ? "Sin ubicación definida" : q);
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta http-equiv='Cache-Control' content='no-store'><title>NexoMarket · Tiendas</title>");
            b.Append("<style>body{font-family:'Segoe UI',Arial,sans-serif;background:#070b10;color:#fff;margin:0}.wrap{max-width:1180px;margin:auto;padding:22px}.top{display:flex;justify-content:space-between;align-items:center;padding:6px 4px 18px}.brand{font-weight:900;font-size:23px}.brand .n{color:#39ff66}.top a{color:#fff;text-decoration:none;margin-left:18px;font-weight:700}.hero{padding:28px;border:1px solid #2a4660;background:linear-gradient(135deg,#101925,#0b1722);border-radius:22px;box-shadow:0 12px 35px rgba(0,0,0,.18)}.eyebrow{font-size:11px;letter-spacing:2px;color:#39ff66;font-weight:900}.nexo{color:#39ff66;font-size:45px;font-weight:900}.market{font-size:40px;font-weight:800}.hero-sub{color:#a8c0d4;margin-top:9px;font-size:15px}.location-box{margin-top:20px;display:flex;align-items:center;gap:10px;flex-wrap:wrap}.location-box input{background:#0c141d;color:#fff;border:1px solid #2c4963;border-radius:10px;padding:12px;width:330px}.btn{background:#39ff66;color:#061009;border:0;border-radius:10px;padding:11px 16px;font-weight:900;cursor:pointer}.btn.alt{background:#0d1721;color:#fff;border:1px solid #2d4a64}.hint{color:#7e94a8;font-size:12px;margin-top:12px}.section-head{display:flex;justify-content:space-between;align-items:end;margin:24px 2px 12px}.section-head h2{margin:0;font-size:24px}.section-head p{margin:5px 0 0;color:#8da3b6;font-size:13px}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:18px}.card{display:block;color:#fff;text-decoration:none;background:linear-gradient(145deg,#101923,#0b121a);border:1px solid #2a4660;border-radius:18px;padding:18px;min-height:150px;transition:.15s;position:relative;overflow:hidden}.card:hover{transform:translateY(-3px);border-color:#9b5cff;box-shadow:0 12px 28px rgba(123,67,220,.18)}.logo{width:72px;height:72px;border-radius:16px;background:#0b1118;border:1px solid #7b4bd1;display:flex;align-items:center;justify-content:center;color:#b98cff;font-size:29px;font-weight:900;float:left;margin-right:14px;overflow:hidden}.logo img{width:100%;height:100%;object-fit:cover}.name{font-size:20px;font-weight:900;padding-top:3px}.meta{color:#92a6b7;font-size:13px;margin-top:6px}.open{color:#39ff66;font-size:12px;margin-top:11px}.distance{color:#ffd34d;font-size:12px;margin-top:5px}.empty{margin-top:18px;border:1px dashed #38516a;border-radius:18px;padding:28px;color:#a2b2c0;background:#0b131c}.empty b{font-size:18px}.auth-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:15px;margin-top:20px}.panel{background:#0e1721;border:1px solid #2a4660;border-radius:18px;padding:20px}.panel h2{margin-top:0}.panel p{color:#92a6b7;font-size:13px;line-height:1.5}.mini{color:#39ff66;font-size:12px;font-weight:800}.footer{margin-top:35px;border-top:1px solid #203448;padding-top:14px;color:#60768a;font-size:11px}@media(max-width:950px){.grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.wrap{padding:14px}.grid{grid-template-columns:1fr}.nexo{font-size:35px}.market{font-size:31px}.location-box input{width:100%}}</style></head><body><div class='wrap'>");
            b.Append("<div class='top'><div class='brand'><span class='n'>NEXO</span>MARKET</div><div><a href='/'>Tiendas</a><a href='/login'>Ingresar</a><a href='/register'>Crear cuenta</a></div></div>");
            b.Append("<section class='hero'><span class='eyebrow'>MARKETPLACE</span><div><span class='nexo'>NEXO</span><span class='market'>MARKET</span></div><div class='hero-sub'>Encontrá todas las tiendas disponibles y priorizá las más cercanas" + (hasCoords ? " a <b>" + E(locationTitle) + "</b>." : ".") + "</div><form class='location-box' method='get' action='/'><input id='q' name='q' value='" + E(q) + "' placeholder='¿Desde dónde estás? Ej.: Mendoza, Luján...'/><input type='hidden' id='lat' name='lat'/><input type='hidden' id='lon' name='lon'/><button class='btn' type='submit'>Buscar tiendas</button><button class='btn' type='button' onclick='geo()'>Usar mi ubicación</button></form><div class='hint'>La ubicación se convierte a coordenadas y las tiendas se ordenan por cercanía. Los datos se actualizan desde NexoMarket Central.</div></section>");
            b.Append("<div class='section-head'><div><h2>Tiendas disponibles</h2><p>Las tiendas activas se muestran automáticamente desde el directorio central.</p></div><span class='mini'>DIRECTORIO MULTI-TIENDA</span></div>");
            if (stores.Count > 0) { b.Append("<div class='grid'>"); foreach (CentralStore cs in stores) { string href = "/store/" + Uri.EscapeDataString(cs.StoreId); string d = cs.Distance > 0 ? cs.Distance.ToString("0.0") + " km · " : ""; string logo = string.IsNullOrWhiteSpace(cs.Logo) ? "<span>N</span>" : "<img src='" + E(cs.Logo) + "' alt='" + E(cs.Name) + "' loading='lazy'/>"; b.Append("<a class='card' href='" + E(href) + "'><div class='logo'>" + logo + "</div><div class='name'>" + E(cs.Name) + "</div><div class='meta'>" + E(cs.Category.Length == 0 ? "Comercio" : cs.Category) + " · " + E(cs.City) + "</div><div class='open'>● Abierta · " + (cs.Delivery ? "Delivery" : "Retiro") + "</div><div class='distance'>📍 " + E(d) + (cs.Delivery ? "🚚 Delivery" : "🏪 Retiro") + "</div></a>"); } b.Append("</div>"); }
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
            if (_database != null && _database.Enabled)
            {
                if (!_database.UpsertAccount(Get(f,"id"), Get(f,"name"), email, Get(f,"phone"), role, storeId, salt, hash, Get(f,"createdAt"))) return "ERROR|database";
            }
            lock(_sync)
            {
                XDocument d = LoadFile(_accountsFile,"NexoMarketAccounts","Users");
                XElement root=d.Root.Element("Users");
                XElement old=root.Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase));
                // Para vendedores la identidad real es StoreId. Una tienda tiene una cuenta
                // vendedora canónica; si Web y Windows usaron correos distintos, el último
                // emparejamiento reemplaza la identidad anterior en lugar de crear dos vendedores.
                if(role=="seller" && !string.IsNullOrWhiteSpace(storeId))
                {
                    XElement byStore=root.Elements("User").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase) && string.Equals(S(x,"Role"),"seller",StringComparison.OrdinalIgnoreCase));
                    if(byStore!=null && old==null) old=byStore;
                }
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

        private CentralUser FindSellerByStore(string storeId)
        {
            if(string.IsNullOrWhiteSpace(storeId)) return null;
            if(_database!=null && _database.Enabled)
            {
                Dictionary<string,string> a=_database.GetSellerByStore(storeId);
                if(a!=null) return CentralUser.From(a);
            }
            lock(_sync)
            {
                XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");
                XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase)&&string.Equals(S(x,"Role"),"seller",StringComparison.OrdinalIgnoreCase));
                return e==null?null:CentralUser.From(e);
            }
        }

        private CentralUser FindAccount(string email)
        {
            if(string.IsNullOrWhiteSpace(email)) return null;
            if (_database != null && _database.Enabled)
            {
                Dictionary<string,string> a = _database.GetAccount(email);
                if (a != null) return CentralUser.From(a);
            }
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

        private string CentralRegisterSellerApi(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(), password=Get(f,"password"), name=Get(f,"name").Trim(), storeId=NormalizeStoreId(Get(f,"storeId"));
            if(email.Length<3||email.IndexOf('@')<1||password.Length<6||name.Length<2)return "ERROR|invalid_data";
            if(string.IsNullOrWhiteSpace(storeId)) return "ERROR|store_required";
            if(!StoreExists(storeId)) return "ERROR|store_not_found";
            CentralUser existing=FindAccount(email);
            if(existing!=null) return "ERROR|account_exists";
            CentralUser sellerForStore=FindSellerByStore(storeId);
            if(sellerForStore!=null) return "ERROR|store_account_exists";
            byte[] salt=new byte[16]; using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt); string salt64=Convert.ToBase64String(salt); byte[] hash; using(var kdf=new Rfc2898DeriveBytes(password,salt,50000))hash=kdf.GetBytes(32);
            string result=AccountUpsert(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",existing==null?Guid.NewGuid().ToString("N"):existing.Id},{"name",name},{"email",email},{"phone",Get(f,"phone")},{"role","seller"},{"storeId",storeId},{"salt",salt64},{"passwordHash",Convert.ToBase64String(hash)},{"createdAt",DateTime.UtcNow.ToString("o")}},false);
            if(!result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase))return result;
            return AccountAuthenticate(new Dictionary<string,string>{{"email",email},{"password",password}});
        }
        private string CentralLoginApi(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(), password=Get(f,"password"), requestedStore=NormalizeStoreId(Get(f,"storeId")); CentralUser u;
            if(!VerifyAccount(email,password,out u)||u==null)return "ERROR|invalid_credentials";
            if(!string.Equals(u.Role,"seller",StringComparison.OrdinalIgnoreCase))return "ERROR|not_seller";
            if(!string.IsNullOrWhiteSpace(requestedStore)&&!string.Equals(requestedStore,u.StoreId,StringComparison.OrdinalIgnoreCase))return "ERROR|store_mismatch";
            return "OK|"+Escape(u.Id)+"|"+Escape(u.Name)+"|"+Escape(u.Email)+"|"+Escape(u.Phone)+"|"+Escape(u.Role)+"|"+Escape(u.StoreId)+"|"+Escape(u.Salt)+"|"+Escape(u.PasswordHash)+"|"+Escape(u.CreatedAt);
        }
        private string PairStart(Dictionary<string,string> f)
        {
            string email=Get(f,"email").Trim().ToLowerInvariant(), password=Get(f,"password"), storeId=NormalizeStoreId(Get(f,"storeId")); CentralUser u;
            if(!VerifyAccount(email,password,out u)||u==null||u.Role!="seller")return "ERROR|invalid_credentials";
            if(!string.Equals(u.StoreId,storeId,StringComparison.OrdinalIgnoreCase))return "ERROR|store_mismatch";
            string token=_database==null?null:_database.CreatePairing(storeId,email,10); if(string.IsNullOrWhiteSpace(token))return "ERROR|database";
            return "OK|"+Escape(token)+"|"+Escape(storeId)+"|600";
        }
        private string PairComplete(Dictionary<string,string> f)
        {
            string token=Get(f,"pairingToken"), deviceId=Get(f,"deviceId").Trim(), deviceName=Get(f,"deviceName").Trim();
            if(string.IsNullOrWhiteSpace(token)||string.IsNullOrWhiteSpace(deviceId))return "ERROR|missing";
            Dictionary<string,string> d=_database==null?null:_database.CompletePairing(token,deviceId,string.IsNullOrWhiteSpace(deviceName)?"Windows":deviceName);
            if(d==null)return "ERROR|pairing_invalid_or_expired";
            return "OK|"+Escape(d["deviceId"])+"|"+Escape(d["deviceToken"])+"|"+Escape(d["storeId"])+"|"+Escape(d["email"]);
        }
        private string DeviceValidate(Dictionary<string,string> f)
        {
            string id=Get(f,"deviceId"), token=Get(f,"deviceToken"), store=NormalizeStoreId(Get(f,"storeId"));
            return _database!=null&&_database.ValidateDevice(id,token,store)?"OK|device_valid":"ERROR|device_invalid";
        }
        private bool StoreExists(string storeId)
        {
            lock(_sync){XElement stores=_doc.Root.Element("Stores");return stores!=null&&stores.Elements("Store").Any(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));}
        }

        private void CentralLogin(NetworkStream stream,string method,string body,string cookie)
        {
            if(method=="GET") {
                string html = "<div class='card'><div class='eyebrow'>VENDEDOR</div><h1>Seller Center</h1><p class='muted'>Ingresá con tu correo, contraseña y Store ID. Los tres datos pertenecen a una única cuenta central.</p><a class='btn violet' href='/seller-login'>INGRESAR COMO VENDEDOR</a></div>" +
                    "<div class='card'><div class='eyebrow'>CUENTA</div><h2>Ingreso general</h2><form method='post' action='/login'><input name='email' type='email' placeholder='Correo electrónico' required/><input name='password' type='password' placeholder='Contraseña' required/><button class='btn' type='submit'>INGRESAR</button></form><p class='muted'>¿No tenés cuenta? <a href='/register'>Crear cuenta</a></p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingresar", html)); return; }
            CentralUser u; Dictionary<string,string> f=Form(body);
            if(!VerifyAccount(Get(f,"email"),Get(f,"password"),out u)) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingreso", "<div class='error'>Correo o contraseña incorrectos.</div><a class='btn' href='/login'>Volver a intentar</a>")); return; }
            string token=Guid.NewGuid().ToString("N"); lock(_sync)_sessions[token]=u;
            string dest=u.Role=="seller"?"/seller":"/buyer"; WriteRedirectCookie(stream,dest,"NexoCentralSession="+token+"; Path=/; HttpOnly; SameSite=Lax");
        }

        // Acceso de vendedor por Store ID: el Store ID es el vínculo común entre
        // NexoMarket Windows y el Seller Center Web. No se crea una segunda tienda
        // ni se pide correo/contraseña para este acceso operativo.
        private void CentralSellerStoreLogin(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                string html="<div class='card seller-login-card'><div class='brand'><span>NEXO</span>MARKET <small>SELLER CENTER</small></div><div class='eyebrow'>CUENTA CENTRAL</div><h1>Ingresar como vendedor</h1><p class='muted'>Si ya tenés una cuenta de vendedor, alcanza con tu correo y contraseña. El Store ID se toma automáticamente de la cuenta.</p><form method='post' action='/seller-login'><label class='muted'>Correo</label><input name='email' type='email' autocomplete='username' required/><label class='muted'>Contraseña</label><input name='password' type='password' autocomplete='current-password' required/><button class='btn violet' type='submit'>INGRESAR AL SELLER CENTER</button></form><p class='muted small'>¿Todavía no vinculaste Windows? Entrá al Seller Center y usá <b>VINCULAR WINDOWS</b> para generar un código temporal.</p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Vendedor · Cuenta central",html)); return;
            }
            Dictionary<string,string> f=Form(body); string email=Get(f,"email").Trim().ToLowerInvariant(), password=Get(f,"password"); CentralUser u;
            if(!VerifyAccount(email,password,out u)||u==null||u.Role!="seller"||string.IsNullOrWhiteSpace(u.StoreId)) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingreso vendedor","<div class='error'>Correo, contraseña o cuenta de vendedor incorrectos.</div><a class='btn violet' href='/seller-login'>Volver a intentar</a>")); return; }
            string token=Guid.NewGuid().ToString("N"); lock(_sync)_sessions[token]=u;
            WriteRedirectCookie(stream,"/seller","NexoCentralSession="+token+"; Path=/; HttpOnly; SameSite=Lax");
        }

        private void CentralRegister(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                StringBuilder form=new StringBuilder();
                form.Append("<form method='post' action='/register'><input name='name' placeholder='Nombre completo' required/><input name='email' type='email' placeholder='Correo electrónico' required/><input name='phone' placeholder='Teléfono'/><select name='role' id='role' onchange='toggleStore()'><option value='buyer'>Soy comprador</option><option value='seller'>Soy vendedor</option></select><div id='storeBox'><label class='muted'>Código / Store ID de Windows</label><input name='storeId' placeholder='Pegá aquí el Store ID que te dio NexoMarket Windows'/><input name='storeName' placeholder='Nombre de la tienda (solo si todavía no existe)'/><p class='muted'>Si creaste primero la cuenta en Windows, usá exactamente el mismo Store ID. Así la cuenta web queda en la misma tienda.</p></div><input name='password' type='password' placeholder='Contraseña (mínimo 6 caracteres)' required/><button class='btn' type='submit'>CREAR CUENTA</button></form><script>function toggleStore(){var r=document.getElementById('role').value;document.getElementById('storeBox').style.display=r==='seller'?'block':'none';}toggleStore();</script><p class='muted'>La cuenta de vendedor queda vinculada a una única tienda mediante Store ID. ¿Ya tenés cuenta? <a href='/login'>Ingresar</a></p>");
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta",form.ToString())); return;
            }
            Dictionary<string,string> f=Form(body); string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); string role=Get(f,"role")=="seller"?"seller":"buyer"; string storeId=Get(f,"storeId").Trim();
            if(password.Length<6||email.Length<3||email.IndexOf('@')<1) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Completá los datos y usá una contraseña de al menos 6 caracteres.</div><a class='btn' href='/register'>Volver</a>")); return; }
            if(role=="seller")
            {
                if(FindAccount(email)!=null) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Ese correo ya está registrado. Esta identidad no se reemplaza al cambiar de versión.</div><a class='btn' href='/login'>Ingresar</a>")); return; }
                string requestedStore=NormalizeStoreId(storeId);
                if(requestedStore.Length>0)
                {
                    CentralUser sellerForStore=FindSellerByStore(requestedStore);
                    if(sellerForStore!=null) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Esta tienda ya tiene una cuenta de vendedor vinculada. No se puede crear otra cuenta para el mismo Store ID.</div><a class='btn' href='/login'>Ingresar con la cuenta existente</a>")); return; }
                }
                lock(_sync)
                {
                    XElement stores=_doc.Root.Element("Stores");
                    if(string.IsNullOrWhiteSpace(storeId))
                    {
                        // Flujo Web-first: se crea una tienda nueva y su Store ID.
                        storeId=Guid.NewGuid().ToString("N").ToUpperInvariant();
                    }
                    else
                    {
                        storeId=NormalizeStoreId(storeId);
                    }
                    XElement store=stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));
                    if(store==null)
                    {
                        // Flujo Windows-first: el Store ID ya existe en la PC pero todavía
                        // puede no haber llegado a Render. La Web lo puede reclamar sin
                        // pedir una segunda cuenta ni generar otra tienda.
                        string storeName=Get(f,"storeName").Trim();
                        if(string.IsNullOrWhiteSpace(storeName)) storeName="Tienda NexoMarket";
                        string syncKey=Guid.NewGuid().ToString("N");
                        store=new XElement("Store",new XAttribute("UpdatedAt",DateTime.UtcNow.ToString("o")),
                            new XElement("StoreId",storeId),new XElement("SyncKey",syncKey),new XElement("Name",storeName),
                            new XElement("LegalName",storeName),new XElement("Category","Comercio"),new XElement("Address",""),
                            new XElement("City",""),new XElement("Province",""),new XElement("Description","Tienda NexoMarket"),
                            new XElement("Logo",""),new XElement("Slug",Regex.Replace(storeName.ToLowerInvariant(),"[^a-z0-9]+","-").Trim('-')),
                            new XElement("PublicUrl","/store/"+Uri.EscapeDataString(storeId)),new XElement("Active","1"),
                            new XElement("Delivery","1"),new XElement("Pickup","1"),new XElement("Latitude",""),new XElement("Longitude",""));
                        stores.Add(store);
                    }
                    else
                    {
                        // El código identifica la tienda existente. No se crea otra.
                        if(S(store,"Active")!="1") store.SetElementValue("Active","1");
                        string requestedName=Get(f,"storeName").Trim();
                        if(!string.IsNullOrWhiteSpace(requestedName) && string.IsNullOrWhiteSpace(S(store,"Name"))) store.SetElementValue("Name",requestedName);
                        store.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                    }
                    Save();
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
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
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
            b.Append(SellerLink("Resumen","",view)+SellerLink("Pedidos","orders",view)+SellerLink("Productos e inventario","products",view)+SellerLink("Clientes","customers",view)+SellerLink("Analítica","analytics",view)+SellerLink("Finanzas y caja","finance",view)+SellerLink("Marketing","marketing",view)+SellerLink("Reputación","reputation",view)+SellerLink("Herramientas","tools",view)+SellerLink("Configuración","settings",view)+SellerLink("Dispositivos / QR","devices",view)+"</aside>");
            b.Append("<main class='sc-main'>");
            b.Append("<div class='welcome'><div><span class='eyebrow'>CENTRAL DE VENTAS</span><h1>Hola, "+E(u.Name)+" 👋</h1><p>Tu Seller Center está conectado por Store ID con NexoMarket Windows. Los cambios se sincronizan automáticamente.</p><div class='section-actions'><a class='btn violet' href='/seller/devices'>"+SellerIcon("Dispositivos")+"VINCULAR WINDOWS</a><a class='btn ghost' href='/seller?view=products'>"+SellerIcon("Productos")+"PRODUCTOS</a><a class='btn ghost' href='/seller?view=orders'>"+SellerIcon("Pedidos")+"PEDIDOS</a></div></div><div class='account-mini'><b>STORE ID</b><strong>"+E(u.StoreId)+"</strong><small>"+E(u.Email)+"</small></div></div>");
            int pending=orders.Count(x=>S(x,"Status")=="Pendiente"); int delivery=orders.Count(x=>(S(x,"Fulfillment")=="Delivery"||S(x,"Fulfillment")=="En reparto")&&S(x,"Status")!="Entregado"&&S(x,"Status")!="Cancelado");
            decimal sales=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total"))); int low=products.Count(x=>Money(S(x,"Stock"))<=Money(S(x,"MinimumStock"))); int customers=orders.Select(x=>S(x,"CustomerEmail").Trim().ToLowerInvariant()).Where(x=>x.Length>0).Distinct().Count();
            b.Append("<div class='kpis'>"+KpiC("Ventas", "$ "+sales.ToString("N2"), "operaciones válidas", "green")+KpiC("Pedidos pendientes", pending.ToString(), "requieren atención", pending>0?"yellow":"green")+KpiC("Productos", products.Count.ToString(), low+" con stock bajo", low>0?"red":"green")+KpiC("Clientes", customers.ToString(), "compradores únicos", "green")+KpiC("Delivery", delivery.ToString(), "entregas abiertas", delivery>0?"yellow":"green")+"</div>");
            if(view=="orders") b.Append(SellerOrdersView(orders));
            else if(view=="products") b.Append(SellerProductsView(products));
            else if(view=="customers") b.Append(SellerCustomersView(orders));
            else if(view=="analytics") b.Append(SellerAnalyticsView(orders,products));
            else if(view=="finance") b.Append(SellerFinanceView(orders));
            else if(view=="marketing") { _sellerRenderStoreId=u.StoreId??""; b.Append(SellerMarketingView(promotions)); }
            else if(view=="reputation") b.Append(SellerReputationView(orders));
            else if(view=="tools") b.Append(SellerToolsView(u,products,orders));
            else if(view=="settings") b.Append(SellerSettingsView(u));
            else if(view=="devices") b.Append("<section class='card pairing-shortcut'><div><span class='eyebrow'>VINCULACIÓN</span><h2>Conectar Windows</h2><p>Generá ahora mismo el código temporal para pegarlo en la Cuenta Central de NexoMarket Windows.</p></div><a class='btn violet' href='/seller/devices'>GENERAR CÓDIGO</a></section>");
            else b.Append(SellerSummaryView(orders,products));
            b.Append("<script>(function(){var last='';function live(){var x=new XMLHttpRequest();x.open('GET','/api/seller/live',true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText),v=d.updatedAt||'';var editing=document.querySelector('form.product-form input:focus,form.product-form textarea:focus,form.edit-form input:focus,form.edit-form textarea:focus,form.settings-form input:focus,form.settings-form textarea:focus');var hasDraft=document.querySelector('form.product-form input:not([type=hidden])[value]:not([value=\"\"])')||document.querySelector('form.product-form textarea:not(:placeholder-shown)');if(last&&v!==last&&!editing&&!document.querySelector('details[open]')&&!hasDraft)location.reload();last=v;}catch(e){}}};x.send();}setTimeout(live,900);setInterval(live,2500);})();</script>");
            b.Append("</main>").Append(AuthShellEnd()); Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private string SellerIcon(string label)
        {
            string n=(label??"").ToLowerInvariant(), path;
            if(n.Contains("pedido"))path="<path d='M5 4h14v16H5z'/><path d='M8 8h8M8 12h8M8 16h5'/>";
            else if(n.Contains("producto"))path="<path d='M4 7l8-4 8 4-8 4z'/><path d='M4 7v10l8 4 8-4V7'/><path d='M12 11v10'/>";
            else if(n.Contains("cliente"))path="<circle cx='12' cy='8' r='3'/><path d='M5 20c.7-3.3 3-5 7-5s6.3 1.7 7 5'/>";
            else if(n.Contains("anal"))path="<path d='M5 19V9M12 19V5M19 19v-8'/>";
            else if(n.Contains("finan"))path="<circle cx='12' cy='12' r='8'/><path d='M12 7v10M9 10c0-1 1-2 3-2s3 1 3 2-1 2-3 2-3 1-3 2 1 2 3 2 3-1 3-2'/>";
            else if(n.Contains("marketing"))path="<path d='M4 11h5l7-4v10l-7-4H4z'/><path d='M9 15l2 5'/>";
            else if(n.Contains("reput"))path="<path d='M12 3l2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-3-5.6 3 1.1-6.2L3 9.6l6.2-.9z'/>";
            else if(n.Contains("herram"))path="<path d='M14 6l4 4M4 20l7-7 3 3-7 7H4z'/>";
            else if(n.Contains("config"))path="<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.7 1.7 0 000-6l-1.2-.7-.1-1.4-1.4-.8-1.1.8a7 7 0 00-2.8 0l-1.1-.8-1.4.8-.1 1.4L8.9 9a1.7 1.7 0 000 6l1.2.7.1 1.4 1.4.8 1.1-.8a7 7 0 002.8 0l1.1.8 1.4-.8.1-1.4z'/>";
            else if(n.Contains("dispositivo"))path="<rect x='5' y='3' width='14' height='18' rx='2'/><path d='M9 17h6'/>";
            else path="<circle cx='12' cy='12' r='8'/>";
            return "<svg class='nav-ico' viewBox='0 0 24 24' aria-hidden='true' fill='none' stroke='currentColor' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'>"+path+"</svg>";
        }
        private string SellerLink(string label,string target,string active){return "<a class='sc-link "+(string.Equals(active,target,StringComparison.OrdinalIgnoreCase)?"active":"")+"' href='/seller"+(string.IsNullOrEmpty(target)?"":"?view="+Uri.EscapeDataString(target))+"'>"+SellerIcon(label)+label+"</a>";}

        private string KpiC(string title,string value,string hint,string cls){return "<div class='kpi "+cls+"'><span>"+E(title)+"</span><strong>"+E(value)+"</strong><small>"+E(hint)+"</small></div>";}
        private decimal Money(string s){decimal v;return decimal.TryParse((s??"").Replace(",","."),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out v)?v:0m;}
        private string SellerSummaryView(List<XElement> orders,List<XElement> products)
        {
            StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>RESUMEN</span><h2>Actividad del negocio</h2></div><a class='btn ghost' href='/seller?view=orders'>Ver pedidos</a></div><div class='two-col'><section class='card'><h3>Últimos pedidos</h3><table class='table'><tr><th>Pedido</th><th>Cliente</th><th>Total</th><th>Estado</th></tr>");
            foreach(XElement o in orders.Take(8)) b.Append(OrderRow(o));
            if(orders.Count==0)b.Append("<tr><td colspan='4' class='muted'>Todavía no hay pedidos web.</td></tr>");
            b.Append("</table></section><section class='card'><h3>Productos destacados</h3><div class='mini-list'>"); foreach(XElement p in products.Take(8)) b.Append("<div class='mini-row'><div><b>"+E(S(p,"Name"))+"</b><small>SKU "+E(S(p,"SKU"))+" · Stock "+E(S(p,"Stock"))+"</small></div><strong>$ "+Money(S(p,"SalePrice")=="0"?S(p,"Price"):S(p,"SalePrice")).ToString("N0")+"</strong></div>"); if(products.Count==0)b.Append("<p class='muted'>Sin productos sincronizados.</p>"); b.Append("</div></section></div><section class='card'><h3>Acciones rápidas</h3><div class='quick-grid'><a href='/seller?view=products'>"+SellerIcon("Productos")+"Gestionar catálogo e inventario</a><a href='/seller?view=orders'>"+SellerIcon("Pedidos")+"Atender pedidos</a><a href='/seller?view=customers'>"+SellerIcon("Clientes")+"Ver clientes</a><a href='/seller?view=analytics'>"+SellerIcon("Analítica")+"Analizar ventas</a><a href='/seller?view=finance'>"+SellerIcon("Finanzas")+"Revisar finanzas</a><a href='/seller?view=marketing'>"+SellerIcon("Marketing")+"Marketing y promociones</a></div></section>"); return b.ToString();
        }
        private string OrderRow(XElement o){return "<tr><td><b>#"+E(S(o,"CentralOrderId").Length>8?S(o,"CentralOrderId").Substring(0,8):S(o,"CentralOrderId"))+"</b></td><td>"+E(S(o,"CustomerName"))+"<small>"+E(S(o,"CustomerEmail"))+"</small></td><td><b>$ "+Money(S(o,"Total")).ToString("N2")+"</b></td><td>"+BadgeC(S(o,"Status"))+"</td></tr>";}
        private string BadgeC(string s){string c=(s??"").IndexOf("rech",StringComparison.OrdinalIgnoreCase)>=0||(s??"")=="Cancelado"?"red":(s??"")=="Pendiente"?"yellow":"green";return "<span class='badge "+c+"'>"+E(s)+"</span>";}
        private string SellerOrdersView(List<XElement> orders)
        {
            StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>OPERACIONES</span><h2>Pedidos y estados</h2><p>Todos los pedidos del marketplace aparecen aquí. Podés buscar, filtrar y actualizar el estado sin salir del Seller Center.</p></div><span class='sync-pill'>● PEDIDOS CENTRALIZADOS</span></div>");
            b.Append("<section class='card'><div class='inventory-toolbar'><input id='orderSearch' placeholder='Buscar pedido, cliente o correo...' oninput='filterOrders()'/><select id='orderStatusFilter' onchange='filterOrders()'><option value='all'>Todos los estados</option><option>Pendiente</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Rechazado</option><option>Cancelado</option></select></div><div class='order-cards'>");
            foreach(XElement o in orders){string id=S(o,"CentralOrderId"), searchable=(id+" "+S(o,"CustomerName")+" "+S(o,"CustomerEmail")).ToLowerInvariant(), status=S(o,"Status"); b.Append("<article class='order-card' data-search='"+E(searchable)+"' data-status='"+E(status)+"'><div><span class='eyebrow'>PEDIDO</span><h3>#"+E(id.Length>10?id.Substring(0,10):id)+"</h3><small>"+E(S(o,"CreatedAt"))+" · "+E(S(o,"Fulfillment"))+"</small></div><div><b>"+E(S(o,"CustomerName"))+"</b><small>"+E(S(o,"CustomerEmail"))+"</small></div><div><strong>$ "+Money(S(o,"Total")).ToString("N2")+"</strong><div>"+BadgeC(status)+" · "+BadgeC(S(o,"PaymentStatus"))+"</div></div><form method='post' action='/seller/order-status' class='inline-form'><input type='hidden' name='id' value='"+E(id)+"'/><select name='status'><option>"+E(status)+"</option><option>Pendiente</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Rechazado</option><option>Cancelado</option></select><button class='btn small' type='submit'>ACTUALIZAR</button></form></article>"); }
            if(orders.Count==0)b.Append("<div class='empty-inventory'>No hay pedidos sincronizados.</div>");
            b.Append("</div></section><script>function filterOrders(){var q=(document.getElementById('orderSearch').value||'').toLowerCase(),f=document.getElementById('orderStatusFilter').value;document.querySelectorAll('.order-card').forEach(function(c){c.style.display=(c.getAttribute('data-search').indexOf(q)>=0&&(f==='all'||c.getAttribute('data-status')===f))?'grid':'none';});}</script>"); return b.ToString();
        }

        private string SellerProductsView(List<XElement> products)
        {
            StringBuilder b=new StringBuilder();
            b.Append("<div class='section-title'><div><span class='eyebrow'>CATÁLOGO CENTRAL</span><h2>Productos e inventario</h2><p>Publicá productos con fotos desde el dispositivo, video corto, variantes, stock y precios. La grilla se adapta al tamaño de pantalla.</p></div><div class='section-actions'><button class='btn ghost' type='button' onclick=\"document.getElementById('productName').focus()\">+ NUEVO PRODUCTO</button><span class='sync-pill'>● CENTRAL EN TIEMPO REAL</span></div></div>");
            b.Append("<section class='card'><h3>Nuevo producto</h3><form method='post' action='/seller/products/save' class='product-form' id='newProductForm' autocomplete='off'><input type='hidden' name='imageUrl' id='newImageUrl'/><input type='hidden' name='videoUrl' id='newVideoUrl'/><div class='form-grid'>");
            b.Append("<input id='productName' name='name' placeholder='Nombre del producto' required/><input name='category' placeholder='Categoría'/><input name='brand' placeholder='Marca'/><input name='sku' placeholder='SKU'/><input name='barcode' placeholder='Código de barras'/><input name='price' placeholder='Precio' value='0'/><input name='salePrice' placeholder='Precio de oferta' value='0'/><input name='cost' placeholder='Costo' value='0'/><input name='stock' placeholder='Stock' value='0'/><input name='minimumStock' placeholder='Stock mínimo' value='0'/><input name='size' placeholder='Talle / tamaño'/><input name='color' placeholder='Color'/></div>");
            b.Append("<div class='media-pickers'><label class='upload-box'><span>📷</span><b>Subir foto</b><small>Galería / archivos · JPG, PNG, WEBP · máx. 8 MB</small><input id='newImageFile' type='file' accept='image/*'/></label><label class='upload-box'><span>🎬</span><b>Subir video corto</b><small>MP4, WEBM, MOV · máx. 8 MB</small><input id='newVideoFile' type='file' accept='video/mp4,video/webm,video/quicktime'/></label><div class='media-preview' id='newMediaPreview'><span>Vista previa</span></div></div>");
            b.Append("<textarea name='description' placeholder='Descripción interna'></textarea><textarea name='publicDescription' placeholder='Descripción pública para compradores'></textarea><div class='form-checks'><label><input type='checkbox' name='onlineEnabled' value='1' checked/> Publicar online</label><label><input type='checkbox' name='active' value='1' checked/> Activo</label></div><div class='form-actions'><span id='uploadState' class='muted'>Podés cargar la foto/video antes de guardar.</span><button class='btn' id='saveProductBtn' type='submit'>GUARDAR PRODUCTO</button></div></form></section>");
            b.Append("<section class='card'><div class='section-title inventory-head'><div><span class='eyebrow'>INVENTARIO</span><h3>Vista de productos</h3></div><span class='muted'>"+products.Count+" productos · 5 columnas en escritorio</span></div><div class='inventory-toolbar'><input id='inventorySearch' placeholder='Buscar por nombre, SKU o categoría...' oninput=\"filterInventory()\"/><select id='inventoryStockFilter' onchange=\"filterInventory()\"><option value='all'>Todo el stock</option><option value='low'>Stock bajo</option><option value='zero'>Sin stock</option></select></div><div class='inventory-grid' id='inventoryGrid'>");
            foreach(XElement x in products)
            {
                string pid=S(x,"ProductId"), img=ProductImageUrl(x); decimal stock=Money(S(x,"Stock")), min=Money(S(x,"MinimumStock")); string price=Money(S(x,"SalePrice")=="0"?S(x,"Price"):S(x,"SalePrice")).ToString("N2");
                string image=string.IsNullOrWhiteSpace(img)?"<div class='no-image'>SIN<br/>FOTO</div>":"<img src='"+E(img)+"' alt='"+E(S(x,"Name"))+"' loading='lazy' onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex'\"/><div class='no-image' style='display:none'>SIN<br/>FOTO</div>";
                string video=string.IsNullOrWhiteSpace(S(x,"VideoUrl"))?"":"<a class='video-link' href='"+E(S(x,"VideoUrl"))+"' target='_blank'>▶ VIDEO</a>";
                b.Append("<article class='inventory-card' data-name='"+E((S(x,"Name")+" "+S(x,"SKU")+" "+S(x,"Category")).ToLowerInvariant())+"' data-stock='"+(stock<=0?"zero":stock<=min?"low":"ok")+"'><div class='inventory-photo'>"+image+"</div><div class='inventory-name'>"+E(S(x,"Name"))+"</div><div class='inventory-meta'>"+E(S(x,"Category"))+" · SKU "+E(S(x,"SKU"))+"</div><div class='inventory-bottom'><span class='inventory-stock "+(stock<=min?"low":"")+"'>Stock "+stock.ToString("N0")+"</span><strong>$ "+price+"</strong></div><div class='card-actions'>"+video+"<details><summary class='btn small'>EDITAR</summary><form method='post' action='/seller/products/save' class='edit-form'><input type='hidden' name='id' value='"+E(pid)+"'/><div class='form-grid'><input name='name' value='"+E(S(x,"Name"))+"' required/><input name='category' value='"+E(S(x,"Category"))+"'/><input name='brand' value='"+E(S(x,"Brand"))+"'/><input name='sku' value='"+E(S(x,"SKU"))+"'/><input name='barcode' value='"+E(S(x,"Barcode"))+"'/><input name='price' value='"+E(S(x,"Price"))+"'/><input name='salePrice' value='"+E(S(x,"SalePrice"))+"'/><input name='cost' value='"+E(S(x,"Cost"))+"'/><input name='stock' value='"+E(S(x,"Stock"))+"'/><input name='minimumStock' value='"+E(S(x,"MinimumStock"))+"'/><input name='size' value='"+E(S(x,"Size"))+"'/><input name='color' value='"+E(S(x,"Color"))+"'/><input name='imageUrl' value='"+E(S(x,"WebImageUrl"))+"'/><input name='videoUrl' value='"+E(S(x,"VideoUrl"))+"'/></div><textarea name='description'>"+E(S(x,"Description"))+"</textarea><textarea name='publicDescription'>"+E(S(x,"PublicDescription"))+"</textarea><div class='form-checks'><label><input type='checkbox' name='onlineEnabled' value='1' "+(S(x,"OnlineEnabled")!="0"?"checked":"")+"/> Publicar online</label><label><input type='checkbox' name='active' value='1' "+(S(x,"Active")!="0"?"checked":"")+"/> Activo</label></div><button class='btn small' type='submit'>GUARDAR CAMBIOS</button></form><form method='post' action='/seller/products/delete' onsubmit='return confirm(&quot;¿Eliminar producto?&quot;)'><input type='hidden' name='id' value='"+E(pid)+"'/><button class='btn small danger' type='submit'>ELIMINAR</button></form></details></div></article>");
            }
            if(products.Count==0)b.Append("<div class='empty-inventory'>No hay productos todavía. Crealos desde este panel o desde NexoMarket Windows.</div>");
            b.Append("</div></section><section class='card'><h3>Publicación masiva</h3><p>Prepará una planilla con SKU, nombre, categoría, precio y stock para importar en lote. Esta base deja el módulo listo para la próxima ampliación del publicador masivo.</p><div class='tool-strip'><span>CSV / Excel</span><span>Variantes</span><span>Stock</span><span>Precios</span></div></section>");
            b.Append("<script>function filterInventory(){var q=(document.getElementById('inventorySearch').value||'').toLowerCase(),f=document.getElementById('inventoryStockFilter').value;document.querySelectorAll('#inventoryGrid .inventory-card').forEach(function(c){var ok=c.getAttribute('data-name').indexOf(q)>=0&&(f==='all'||c.getAttribute('data-stock')===f);c.style.display=ok?'block':'none';});}function uploadMedia(file,kind){return new Promise(function(resolve,reject){if(!file){resolve('');return}if(file.size>8*1024*1024){reject(new Error('El archivo supera 8 MB.'));return}var r=new FileReader();r.onload=function(){var base=String(r.result).split(',')[1]||'',data='fileName='+encodeURIComponent(file.name)+'&contentType='+encodeURIComponent(file.type)+'&base64='+encodeURIComponent(base);var x=new XMLHttpRequest();x.open('POST','/seller/media/upload',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4){if(x.status===200&&x.responseText.indexOf('OK|')===0)resolve(x.responseText.split('|')[2]);else reject(new Error(x.responseText||'No se pudo subir el archivo.'));}};x.send(data)};r.onerror=function(){reject(new Error('No se pudo leer el archivo.'))};r.readAsDataURL(file);});}function previewFile(input){var p=document.getElementById('newMediaPreview'),f=input.files&&input.files[0];if(!f)return;var u=URL.createObjectURL(f);p.innerHTML=f.type.indexOf('video/')===0?'<video src=\"'+u+'\" controls></video>':'<img src=\"'+u+'\"/>';}</script>");
            b.Append("<script>document.getElementById('newImageFile').addEventListener('change',function(){previewFile(this)});document.getElementById('newVideoFile').addEventListener('change',function(){previewFile(this)});document.getElementById('newProductForm').addEventListener('submit',async function(e){e.preventDefault();var btn=document.getElementById('saveProductBtn'),state=document.getElementById('uploadState');btn.disabled=true;state.textContent='Subiendo archivos...';try{var im=await uploadMedia(document.getElementById('newImageFile').files[0],'image');document.getElementById('newImageUrl').value=im||'';state.textContent='Foto lista. Subiendo video...';var vid=await uploadMedia(document.getElementById('newVideoFile').files[0],'video');document.getElementById('newVideoUrl').value=vid||'';state.textContent='Guardando producto...';this.submit();}catch(err){state.textContent='Error: '+err.message;btn.disabled=false;}});</script>");
            return b.ToString();
        }

        private string SellerCustomersView(List<XElement> orders)
        {
            var groups=orders.Where(x=>!string.IsNullOrWhiteSpace(S(x,"CustomerEmail"))).GroupBy(x=>S(x,"CustomerEmail").Trim().ToLowerInvariant()).OrderByDescending(g=>g.Count()).ToList(); StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>CRM</span><h2>Clientes</h2><p>Compradores que interactuaron con tu tienda online.</p></div></div><section class='card table-wrap'><table class='table'><tr><th>Cliente</th><th>Correo</th><th>Pedidos</th><th>Total comprado</th><th>Última compra</th></tr>"); foreach(var g in groups){XElement last=g.OrderByDescending(x=>S(x,"CreatedAt")).First();b.Append("<tr><td><b>"+E(S(last,"CustomerName"))+"</b></td><td>"+E(g.Key)+"</td><td>"+g.Count()+"</td><td><b>$ "+g.Sum(x=>Money(S(x,"Total"))).ToString("N2")+"</b></td><td>"+E(S(last,"CreatedAt"))+"</td></tr>");} if(groups.Count==0)b.Append("<tr><td colspan='5' class='muted'>Todavía no hay clientes web.</td></tr>"); b.Append("</table></section>"); return b.ToString();
        }
        private string SellerAnalyticsView(List<XElement> orders,List<XElement> products)
        {
            decimal sales=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total"))), avg=orders.Count==0?0:orders.Average(x=>Money(S(x,"Total")));
            DateTime now=DateTime.UtcNow.Date; List<decimal> daySales=new List<decimal>(); List<int> dayOrders=new List<int>();
            for(int i=6;i>=0;i--){DateTime d=now.AddDays(-i);var q=orders.Where(x=>DateTime.TryParse(S(x,"CreatedAt"),out var dt)&&dt.ToUniversalTime().Date==d).ToList();daySales.Add(q.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total"))));dayOrders.Add(q.Count);}
            decimal maxDay=daySales.Max()==0?1:daySales.Max(); int delivered=orders.Count(x=>S(x,"Status")=="Entregado"),pending=orders.Count(x=>S(x,"Status")=="Pendiente"),cancel=orders.Count(x=>S(x,"Status")=="Cancelado"||S(x,"Status")=="Rechazado");
            StringBuilder b=new StringBuilder(); b.Append("<div class='section-title'><div><span class='eyebrow'>MÉTRICAS</span><h2>Rendimiento del negocio</h2><p>Indicadores calculados con los pedidos y productos reales de esta tienda.</p></div></div>");
            b.Append("<div class='kpis'>"+KpiC("Ventas","$ "+sales.ToString("N2"),"operaciones válidas","green")+KpiC("Ticket medio","$ "+avg.ToString("N2"),"por pedido","green")+KpiC("Pedidos",orders.Count.ToString(),"totales","yellow")+KpiC("Publicados",products.Count(x=>S(x,"OnlineEnabled")!="0").ToString(),"productos online","green")+KpiC("Stock bajo",products.Count(x=>Money(S(x,"Stock"))<=Money(S(x,"MinimumStock"))).ToString(),"requiere reposición","red")+"</div>");
            b.Append("<div class='two-col'><section class='card'><h3>Ventas · últimos 7 días</h3><div class='chart-bars'>");
            for(int i=0;i<7;i++){string day=now.AddDays(i-6).ToString("dd/MM");int h=(int)Math.Round((daySales[i]/maxDay)*150);b.Append("<div class='bar-col'><span>$ "+daySales[i].ToString("N0")+"</span><div class='bar' style='height:"+Math.Max(4,h)+"px'></div><small>"+day+"</small></div>");}
            b.Append("</div></section><section class='card'><h3>Estado de pedidos</h3><div class='status-bars'><div><span>Entregados</span><b>"+delivered+"</b></div><div><span>Pendientes</span><b>"+pending+"</b></div><div><span>Cancelados / rechazados</span><b>"+cancel+"</b></div><div><span>Otros en proceso</span><b>"+Math.Max(0,orders.Count-delivered-pending-cancel)+"</b></div></div></section></div>");
            b.Append("<section class='card'><h3>Oportunidades</h3><div class='insights'><div>📦 "+products.Count(x=>Money(S(x,"Stock"))<=Money(S(x,"MinimumStock")))+" productos tienen stock bajo.</div><div>🧾 "+pending+" pedidos pendientes necesitan atención.</div><div>📈 El gráfico usa datos de pedidos reales y se actualiza con la sincronización central.</div></div></section>"); return b.ToString();
        }

        private string SellerFinanceView(List<XElement> orders){decimal total=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal cash=orders.Where(x=>S(x,"PaymentMethod")=="Efectivo"&&S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal mp=orders.Where(x=>S(x,"PaymentMethod")=="Mercado Pago"&&S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));decimal tr=orders.Where(x=>S(x,"PaymentMethod")=="Transferencia"&&S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total")));return "<div class='section-title'><div><span class='eyebrow'>FINANZAS</span><h2>Ventas y medios de cobro</h2><p>Resumen central de las operaciones web. La apertura/cierre física de caja continúa en Windows.</p></div></div><div class='kpis mini-kpis'>"+KpiC("Total vendido","$ "+total.ToString("N2"),"operaciones web","green")+KpiC("Efectivo","$ "+cash.ToString("N2"),"ventas","green")+KpiC("Mercado Pago","$ "+mp.ToString("N2"),"ventas","green")+KpiC("Transferencias","$ "+tr.ToString("N2"),"ventas","green")+"</div><section class='card'><h3>Conciliación</h3><p>Los números del Seller Center se alimentan de los pedidos centralizados. La caja de mostrador, apertura, arqueo y retenciones se mantienen sincronizados desde NexoMarket Windows.</p></section>";}
        private string SellerMarketingView(List<XElement> promotions)
        {
            List<XElement> coupons=new List<XElement>();
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                coupons=d.Root.Element("Coupons")==null?new List<XElement>():d.Root.Element("Coupons").Elements("Coupon").Where(x=>S(x,"StoreId")==CurrentSellerStoreId()).OrderByDescending(x=>S(x,"UpdatedAt")).ToList();
            }
            StringBuilder b=new StringBuilder();
            b.Append("<div class='section-title'><div><span class='eyebrow'>CRECIMIENTO</span><h2>Marketing y promociones</h2><p>Promociones y cupones sincronizados entre NexoMarket Windows y Seller Center.</p></div></div>");
            b.Append("<section class='card'><h3>Generador de cupones</h3><form method='post' action='/seller/coupon/save' class='form-grid'>");
            b.Append("<input name='code' placeholder='Código, ejemplo VERANO10' required/><input name='description' placeholder='Descripción'/>");
            b.Append("<input name='percent' type='number' step='0.01' min='0' max='100' placeholder='% de descuento'/>");
            b.Append("<input name='amount' type='number' step='0.01' min='0' placeholder='$ descuento fijo'/>");
            b.Append("<input name='maxUses' type='number' min='0' placeholder='Usos máximos (0 = sin límite)'/>");
            b.Append("<button class='btn violet' type='submit'>GENERAR CUPÓN</button></form></section>");
            b.Append("<section class='card table-wrap'><h3>Cupones creados</h3><table class='table'><tr><th>Código</th><th>Descuento</th><th>Usos</th><th>Estado</th><th>Vigencia</th></tr>");
            foreach(XElement c in coupons)
                b.Append("<tr><td><b>"+E(S(c,"Code"))+"</b><small>"+E(S(c,"Description"))+"</small></td><td>"+(Money(S(c,"DiscountPercent"))>0?Money(S(c,"DiscountPercent")).ToString("0.##")+"%":"$ "+Money(S(c,"DiscountAmount")).ToString("N2"))+"</td><td>"+E(S(c,"Used"))+" / "+(S(c,"MaxUses")=="0"?"∞":E(S(c,"MaxUses")))+"</td><td>"+BadgeC(S(c,"Active")=="0"?"Pausado":"Activo")+"</td><td>"+E(S(c,"From"))+" → "+E(S(c,"To"))+"</td></tr>");
            if(coupons.Count==0)b.Append("<tr><td colspan='5' class='muted'>No hay cupones creados todavía.</td></tr>");
            b.Append("</table></section>");
            b.Append("<section class='card table-wrap'><h3>Promociones de productos</h3><table class='table'><tr><th>Promoción</th><th>Precio</th><th>Estado</th><th>Vigencia</th></tr>");
            foreach(XElement p in promotions)b.Append("<tr><td><b>"+E(S(p,"Name"))+"</b></td><td>$ "+Money(S(p,"PromotionalPrice")).ToString("N2")+"</td><td>"+BadgeC(S(p,"Active")=="0"?"Pausada":"Activa")+"</td><td>"+E(S(p,"From"))+" → "+E(S(p,"To"))+"</td></tr>");
            if(promotions.Count==0)b.Append("<tr><td colspan='4' class='muted'>No hay promociones sincronizadas.</td></tr>");
            b.Append("</table></section>");
            return b.ToString();
        }

        private string CurrentSellerStoreId()
        {
            // The caller has already authenticated the seller. The active Store ID is
            // carried by the session in CentralSeller; this helper is intentionally
            // conservative when used outside that flow.
            return _sellerRenderStoreId ?? "";
        }
        private string _sellerRenderStoreId = "";
        private void CentralSellerCouponSave(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u; u=SessionUser(cookie); if(u==null||u.Role!="seller"){Write(stream,403,"text/plain; charset=utf-8","Acceso denegado.");return;}
            string code=Get(f,"code").Trim().ToUpperInvariant(); decimal percent=0m,amount=0m; int maxUses=0;
            decimal.TryParse(Get(f,"percent").Replace(",","."),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out percent);
            decimal.TryParse(Get(f,"amount").Replace(",","."),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out amount);
            int.TryParse(Get(f,"maxUses"),out maxUses);
            if(code.Length<3||(percent<=0m&&amount<=0m)||(percent>100m)||(percent>0m&&amount>0m)){WriteRedirectCookie(stream,"/seller?view=marketing","NexoCentralNotice=invalid_coupon; Path=/; SameSite=Lax");return;}
            string storeId=u.StoreId??"";
            lock(_sync)
            {
                XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products");
                if(d.Root.Element("Coupons")==null)d.Root.Add(new XElement("Coupons"));
                XElement existing=d.Root.Element("Coupons").Elements("Coupon").FirstOrDefault(x=>S(x,"StoreId")==storeId&&string.Equals(S(x,"Code"),code,StringComparison.OrdinalIgnoreCase));
                if(existing!=null){WriteRedirectCookie(stream,"/seller?view=marketing","NexoCentralNotice=coupon_exists; Path=/; SameSite=Lax");return;}
                string id=Guid.NewGuid().ToString("N");
                d.Root.Element("Coupons").Add(new XElement("Coupon",new XElement("StoreId",storeId),new XElement("CouponId",id),new XElement("Code",code),new XElement("Description",Get(f,"description")),new XElement("DiscountPercent",percent.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("DiscountAmount",amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("MaxUses",Math.Max(0,maxUses)),new XElement("Used","0"),new XElement("Active","1"),new XElement("From",DateTime.Today.ToString("o")),new XElement("To",DateTime.Today.AddDays(30).ToString("o")),new XElement("UpdatedAt",DateTime.UtcNow.ToString("o"))));
                SaveDoc(_catalogFile,d);
            }
            WriteRedirectCookie(stream,"/seller?view=marketing","NexoCentralNotice=coupon_saved; Path=/; SameSite=Lax");
        }

        private string SellerReputationView(List<XElement> orders){int delivered=orders.Count(x=>S(x,"Status")=="Entregado"),cancel=orders.Count(x=>S(x,"Status")=="Cancelado"||S(x,"Status")=="Rechazado");return "<div class='section-title'><div><span class='eyebrow'>REPUTACIÓN</span><h2>Salud de la operación</h2><p>Indicadores construidos con pedidos centralizados.</p></div></div><div class='kpis mini-kpis'>"+KpiC("Entregados",delivered.ToString(),"pedidos finalizados","green")+KpiC("Cancelados/Rechazados",cancel.ToString(),"incidencias","red")+KpiC("Pendientes",orders.Count(x=>S(x,"Status")=="Pendiente").ToString(),"atención","yellow")+"</div><section class='card'><h3>Buenas prácticas</h3><div class='insights'><div>🟢 Actualizá estados rápidamente para que el comprador vea el seguimiento.</div><div>🟡 Mantené stock y precios sincronizados con Windows.</div><div>🔴 Revisá pedidos rechazados o cancelados para detectar problemas.</div></div></section>";}
        private void CentralSellerDevices(NetworkStream stream,string cookie,string method,string body)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            // Web-first pairing: an authenticated Seller Center session can generate the
            // temporary code directly. No second password prompt is necessary.
            // The authenticated Seller Center session is enough; the database creates the one-time token.
            if (_database==null || !_database.Enabled)
            {
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Vincular Windows","<div class='error'>PostgreSQL no está disponible. No se puede generar un código seguro.</div><a class='btn violet' href='/seller'>Volver al Seller Center</a>"));
                return;
            }
            string pair=_database.CreatePairing(u.StoreId,u.Email,10);
            string displayPair=pair==null?"":(pair.Length==8?pair.Substring(0,4)+"-"+pair.Substring(4):pair);
            if(string.IsNullOrWhiteSpace(pair))
            {
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Vincular Windows","<div class='error'>No se pudo generar el código temporal. Verificá la conexión con PostgreSQL.</div><a class='btn violet' href='/seller'>Volver al Seller Center</a>"));
                return;
            }
            string payload=PairPayload(u.StoreId,displayPair);
            string qr="<div id='qrcode' style='width:260px;height:260px;background:#fff;padding:12px;border-radius:16px;margin:15px auto'></div><script src='https://cdnjs.cloudflare.com/ajax/libs/qrcodejs/1.0.0/qrcode.min.js'></script><script>new QRCode(document.getElementById('qrcode'),{text:'"+E(payload)+"',width:236,height:236,colorDark:'#000000',colorLight:'#ffffff'});</script>";
            StringBuilder b=new StringBuilder(AuthShellStart("Dispositivos · NexoMarket")); b.Append(SellerCenterCss());
            b.Append("<header class='sc-top'><div class='brand'><span>NEXO</span>MARKET <small>SELLER CENTER</small></div><div class='top-actions'><a href='/seller' class='btn ghost'>Volver</a></div></header>");
            b.Append("<main class='sc-main' style='margin-left:0'><div class='welcome'><div><span class='eyebrow'>VINCULACIÓN WINDOWS</span><h1>Conectar NexoMarket Windows</h1><p>Usá el código corto de 6 dígitos. Es temporal, de un solo uso y está vinculado a tu cuenta y Store ID actuales.</p></div><div class='account-mini'><b>STORE ID</b><strong>"+E(u.StoreId)+"</strong><small>"+E(u.Email)+"</small></div></div>");
            b.Append("<section class='card pairing-card'><div class='eyebrow'>CÓDIGO PARA COPIAR</div><h2>Copiá este código en Windows</h2><div class='pair-code' id='pairCode' style='letter-spacing:6px;font-size:34px;text-align:center'>"+E(displayPair)+"</div><button class='btn violet' type='button' onclick=\"navigator.clipboard&&navigator.clipboard.writeText(document.getElementById('pairCode').innerText).then(function(){this.innerText='✓ COPIADO';}.bind(this))\">COPIAR CÓDIGO</button><p class='muted'>Vence en 10 minutos y solo puede utilizarse una vez.</p><div class='pair-instructions'><b>En Windows:</b><br/>Cuenta central → <b>Vincular Windows</b> → pegá el código → confirmá.</div>"+qr+"<a class='btn ghost' href='/seller/devices'>GENERAR OTRO CÓDIGO</a></section></main>");
            b.Append(AuthShellEnd()); Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private string CentralSellerMediaUpload(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller") return "ERROR|session";
            f["storeId"]=u.StoreId;
            return UploadMedia(f);
        }

        private void CentralSellerStoreSave(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            lock(_sync)
            {
                XElement stores=_doc.Root.Element("Stores");
                XElement store=stores==null?null:stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(u.StoreId),StringComparison.OrdinalIgnoreCase));
                if(store==null){WriteRedirect(stream,"/seller?view=settings&error=store");return;}
                string[] fields={"Name","LegalName","Category","Address","City","Province","Description","Logo","Slug","Delivery","Pickup"};
                foreach(string field in fields){string key=char.ToLowerInvariant(field[0])+field.Substring(1); if(field=="Delivery"||field=="Pickup") store.SetElementValue(field,f.ContainsKey(key)?"1":"0"); else if(f.ContainsKey(key)) store.SetElementValue(field,Get(f,key).Trim());}
                store.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o")); Save();
            }
            WriteRedirect(stream,"/seller?view=settings&saved=1");
        }

        private string ProductImageUrl(XElement product)
        {
            string url=S(product,"WebImageUrl").Trim();
            if(url.Length>0)
            {
                int media=url.IndexOf("/media/",StringComparison.OrdinalIgnoreCase);
                if(media>=0) return url.Substring(media);
                int stores=url.IndexOf("/stores/",StringComparison.OrdinalIgnoreCase);
                if(stores>=0) return "/media"+url.Substring(stores);
                if(url.StartsWith("http://",StringComparison.OrdinalIgnoreCase)||url.StartsWith("https://",StringComparison.OrdinalIgnoreCase)||url.StartsWith("/",StringComparison.OrdinalIgnoreCase)) return url;
            }
            string category=S(product,"Category");
            string name=S(product,"Name");
            return "/media/placeholder/"+Uri.EscapeDataString((string.IsNullOrWhiteSpace(category)?"producto":category)+".svg");
        }
        private string CategoryPlaceholderSvg(string category)
        {
            string c=(category??"Producto").Trim(); string l=c.ToLowerInvariant(); string icon="<circle cx='120' cy='90' r='42' fill='#39ff66' opacity='.18'/><circle cx='120' cy='90' r='28' fill='none' stroke='#39ff66' stroke-width='8'/>";
            if(l.Contains("farm")||l.Contains("salud")||l.Contains("medic")) icon="<rect x='104' y='45' width='32' height='90' rx='8' fill='#ff5967'/><rect x='75' y='74' width='90' height='32' rx='8' fill='#ff5967'/>";
            else if(l.Contains("verd")||l.Contains("frut")||l.Contains("alimento")||l.Contains("super")) icon="<path d='M65 102 Q120 45 175 102 Q120 155 65 102Z' fill='#39ff66' opacity='.25' stroke='#39ff66' stroke-width='6'/><path d='M120 55 Q145 25 170 42 Q155 68 120 70Z' fill='#39ff66'/>";
            else if(l.Contains("ropa")||l.Contains("calzado")||l.Contains("moda")) icon="<path d='M82 58 L110 42 L120 65 L130 42 L158 58 L180 88 L148 104 L138 84 L138 145 L102 145 L102 84 L92 104 L60 88Z' fill='#9b5cff' opacity='.8'/>";
            else if(l.Contains("elect")||l.Contains("tecn")||l.Contains("celular")||l.Contains("comput")) icon="<rect x='62' y='42' width='116' height='82' rx='8' fill='none' stroke='#9b5cff' stroke-width='8'/><rect x='96' y='132' width='48' height='8' rx='4' fill='#9b5cff'/>";
            return "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 240 180'><rect width='240' height='180' rx='18' fill='#0b1118'/><g>"+icon+"</g><text x='120' y='160' text-anchor='middle' fill='#dce8f2' font-family='Arial, sans-serif' font-size='16' font-weight='700'>"+E(c.Length>24?c.Substring(0,24):c)+"</text></svg>";
        }

        private string PairPayload(string storeId,string token){return "NEXOMARKETPAIR:"+storeId+"|"+token;}

        private string SellerSettingsView(CentralUser u)
        {
            XElement store=null; lock(_sync){XElement stores=_doc.Root.Element("Stores"); if(stores!=null) store=stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(u.StoreId),StringComparison.OrdinalIgnoreCase));}
            if(store==null) return "<section class='card'><div class='error'>No se encontró la tienda asociada al Store ID.</div></section>";
            string saved=""; return "<div class='section-title'><div><span class='eyebrow'>CONFIGURACIÓN</span><h2>Configuración de la tienda</h2><p>Los datos principales se guardan en la identidad central y sobreviven a cambios de versión.</p></div></div><section class='card'><form method='post' action='/seller/store/save' class='settings-form'><div class='form-grid'><label>Nombre de tienda<input name='name' value='"+E(S(store,"Name"))+"' required/></label><label>Nombre legal<input name='legalName' value='"+E(S(store,"LegalName"))+"'/></label><label>Categoría<input name='category' value='"+E(S(store,"Category"))+"'/></label><label>Ciudad<input name='city' value='"+E(S(store,"City"))+"'/></label><label>Provincia<input name='province' value='"+E(S(store,"Province"))+"'/></label><label>Dirección<input name='address' value='"+E(S(store,"Address"))+"'/></label><label>Logo URL<input name='logo' value='"+E(S(store,"Logo"))+"'/></label><label>Slug<input name='slug' value='"+E(S(store,"Slug"))+"'/></label></div><label>Descripción<textarea name='description'>"+E(S(store,"Description"))+"</textarea></label><div class='form-checks'><label><input type='checkbox' name='delivery' value='1' "+(S(store,"Delivery")!="0"?"checked":"")+"/> Ofrecer delivery</label><label><input type='checkbox' name='pickup' value='1' "+(S(store,"Pickup")!="0"?"checked":"")+"/> Permitir retiro</label></div><button class='btn violet' type='submit'>GUARDAR CONFIGURACIÓN</button></form></section><section class='card'><h3>Identidad permanente</h3><div class='identity-grid'><div><small>Store ID</small><b>"+E(u.StoreId)+"</b></div><div><small>Cuenta</small><b>"+E(u.Email)+"</b></div><div><small>Regla</small><b>Una tienda · una cuenta</b></div></div></section>";
        }

        private string SellerToolsView(CentralUser u,List<XElement> products,List<XElement> orders){return "<div class='section-title'><div><span class='eyebrow'>HERRAMIENTAS</span><h2>Centro de operaciones</h2><p>Accesos rápidos para gestionar tienda, catálogo, pedidos, clientes y conexión.</p></div></div><div class='quick-grid'><a href='/store/"+Uri.EscapeDataString(u.StoreId??"")+"'>"+SellerIcon("Herramientas")+"Ver escaparate público</a><a href='/seller?view=products'>"+SellerIcon("Productos")+"Catálogo e inventario ("+products.Count+")</a><a href='/seller?view=orders'>"+SellerIcon("Pedidos")+"Pedidos ("+orders.Count+")</a><a href='/seller?view=customers'>"+SellerIcon("Clientes")+"Clientes</a><a href='/seller?view=analytics'>"+SellerIcon("Analítica")+"Analítica y gráficos</a><a href='/seller?view=finance'>"+SellerIcon("Finanzas")+"Finanzas</a><a href='/seller?view=settings'>"+SellerIcon("Configuración")+"Configuración</a><a href='/seller/devices'>"+SellerIcon("Dispositivos")+"Vincular Windows</a><a href='/seller?view=marketing'>"+SellerIcon("Marketing")+"Marketing</a></div><section class='card'><h3>Sincronización</h3><p>La PC con NexoMarket Windows publica productos, promociones, cuentas y recibe pedidos del marketplace. El Store ID identifica al comercio y mantiene una sola identidad aunque cambien las versiones.</p><div class='sync-pill'>● CUENTA: "+E(u.Email)+" · ● STORE ID: "+E(u.StoreId)+"</div></section>";}
        private string SellerCenterCss(){return "<style>body{font-family:'Segoe UI',Arial,sans-serif;background:#070b10;color:#fff;margin:0}.wrap{max-width:1500px;margin:auto;padding:18px}.sc-top{display:flex;justify-content:space-between;align-items:center;padding:8px 0 18px}.brand{font-weight:900;font-size:23px}.brand span{color:#39ff66}.brand small{color:#a978ff}.brand small{color:#8292a3;font-size:10px;letter-spacing:2px;margin-left:8px}.top-actions{display:flex;gap:8px}.sc-side{position:fixed;width:230px;top:78px;bottom:18px;background:#0c131c;border:1px solid #23364b;border-radius:18px;padding:14px;box-sizing:border-box}.account-box{border-bottom:1px solid #223246;padding:8px 5px 15px;margin-bottom:10px}.avatar{width:42px;height:42px;border-radius:12px;background:#39ff66;color:#061009;display:flex;align-items:center;justify-content:center;font-weight:900;font-size:20px;margin-bottom:8px}.account-box b,.account-box small{display:block}.account-box small{color:#788b9e;margin-top:4px;font-size:11px;word-break:break-word}.btn .nav-ico{display:inline-block;vertical-align:middle;margin-right:6px}.sc-link{display:flex;align-items:center;gap:9px;color:#a8b8c8;text-decoration:none;padding:11px 12px;border-radius:10px;margin:3px 0;font-weight:700;font-size:13px}.nav-ico{width:17px;height:17px;flex:none}.quick-grid a{display:flex;align-items:center;gap:9px}.sc-link:hover,.sc-link.active{background:linear-gradient(90deg,#17231f,#1a1230);color:#b98cff;border-left:2px solid #9b5cff}.sc-main{margin-left:248px}.welcome{display:flex;justify-content:space-between;gap:15px;align-items:center;padding:24px;border:1px solid #26384e;border-radius:20px;background:linear-gradient(135deg,#101923,#0b1118)}.welcome h1{margin:6px 0;font-size:31px}.welcome p,.section-title p,.card p{color:#899bac;line-height:1.5}.account-mini{min-width:180px;padding:14px;border:1px solid #2c445c;border-radius:14px;background:#0b141d}.account-mini b,.account-mini strong,.account-mini small{display:block}.account-mini strong{color:#39ff66;margin:5px 0}.account-mini small{color:#8292a3;word-break:break-word}.eyebrow{color:#b98cff;font-size:10px;letter-spacing:2px;font-weight:900}.kpis{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin:15px 0}.kpi{padding:17px;border:1px solid #26384e;border-radius:16px;background:#0e1721}.kpi span,.kpi small{display:block;color:#8293a5;font-size:11px}.kpi strong{display:block;font-size:25px;margin:8px 0}.kpi.green{border-top:2px solid #39ff66}.kpi.yellow{border-top:2px solid #ffd34d}.kpi.red{border-top:2px solid #ff5967}.section-title{display:flex;justify-content:space-between;align-items:end;margin:22px 2px 12px}.section-title h2{margin:4px 0}.two-col{display:grid;grid-template-columns:1.3fr .7fr;gap:14px}.card{background:#0e1721;border:1px solid #26384e;border-radius:18px;padding:18px;margin-bottom:14px}.table-wrap{overflow:auto}.table{width:100%;border-collapse:collapse}.table th,.table td{padding:12px 10px;border-bottom:1px solid #223144;text-align:left;font-size:12px;vertical-align:middle}.table th{color:#7f92a6;font-size:10px;text-transform:uppercase;letter-spacing:1px}.table td small{display:block;color:#718397;margin-top:4px}.badge{display:inline-block;padding:5px 8px;border-radius:999px;font-size:10px;font-weight:900}.badge.green{background:#153a22;color:#69ff91}.badge.yellow{background:#403816;color:#ffe36b}.badge.red{background:#401b22;color:#ff7380}.stock.low{color:#ff6572;font-weight:900}.mini-list{display:grid;gap:4px}.mini-row{display:flex;justify-content:space-between;gap:10px;padding:11px;border-bottom:1px solid #223144}.mini-row small{display:block;color:#718397;margin-top:4px}.quick-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.quick-grid a{display:block;text-decoration:none;color:#dce8f2;background:#0a121a;border:1px solid #273b50;border-radius:12px;padding:14px;font-weight:800}.quick-grid a:hover{border-color:#39ff66}.sync-pill{color:#8dffac;font-size:11px;font-weight:900}.form-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}.product-form input,.product-form textarea,.edit-form input,.edit-form textarea{box-sizing:border-box;background:#0a121a;color:#fff;border:1px solid #2b4056;border-radius:8px;padding:9px;width:100%;margin:4px 0}.product-form textarea,.edit-form textarea{min-height:70px;grid-column:1/-1}.edit-form{min-width:650px;background:#091018;padding:10px;border:1px solid #26384e;border-radius:12px}.inventory-head{margin:0 0 12px}.inventory-grid{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:12px}.inventory-card{background:#0a121a;border:1px solid #26384e;border-radius:14px;padding:10px;min-width:0}.inventory-photo{width:100%;aspect-ratio:1/1;border-radius:10px;overflow:hidden;background:#101a24;border:1px solid #24384c;display:flex;align-items:center;justify-content:center}.inventory-photo img{width:100%;height:100%;object-fit:cover}.no-image{width:100%;height:100%;display:flex;align-items:center;justify-content:center;text-align:center;color:#62778a;font-size:11px;font-weight:900;line-height:1.4}.inventory-name{font-weight:900;margin-top:9px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.inventory-meta{color:#718397;font-size:10px;margin-top:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.inventory-bottom{display:flex;justify-content:space-between;gap:5px;margin:9px 0;font-size:11px}.inventory-stock{color:#69ff91;font-weight:900}.inventory-stock.low{color:#ff6572}.empty-inventory{padding:30px;text-align:center;color:#8194a6;border:1px dashed #2d465e;border-radius:12px}@media(max-width:1150px){.inventory-grid{grid-template-columns:repeat(4,minmax(0,1fr))}}@media(max-width:900px){.inventory-grid{grid-template-columns:repeat(3,minmax(0,1fr))}}@media(max-width:650px){.inventory-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}.danger{background:#401b22;color:#ff7380}@media(max-width:900px){.form-grid{grid-template-columns:repeat(2,1fr)}}.inline-form{display:flex;gap:5px}.inline-form select{min-width:115px;background:#0a121a;color:#fff;border:1px solid #2b4056;border-radius:8px;padding:7px}.btn.violet{background:#9b5cff;color:#fff}.small{font-size:12px}.btn{display:inline-block;background:#39ff66;color:#061009;border:0;border-radius:9px;padding:9px 13px;font-weight:900;text-decoration:none;cursor:pointer}.btn.small{padding:7px 9px;font-size:10px}.btn.ghost{background:#101a24;color:#d9e5ef;border:1px solid #2a4056}.metric-list{display:grid;gap:10px}.metric-list div{padding:12px;border:1px solid #24374b;border-radius:10px;color:#91a1b0}.metric-list b{float:right;color:#fff}.insights{display:grid;gap:8px}.insights div{padding:12px;border-radius:10px;background:#0a121a;border:1px solid #223448}.section-actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.media-pickers{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;margin:10px 0}.upload-box{display:flex;flex-direction:column;gap:5px;padding:14px;border:1px dashed #3b5570;border-radius:14px;background:#0a121a;cursor:pointer}.upload-box span{font-size:24px}.upload-box b{font-size:13px}.upload-box small{color:#74879a}.upload-box input{margin-top:5px;width:100%}.media-preview{min-height:140px;border:1px solid #26384e;border-radius:14px;background:#091018;display:flex;align-items:center;justify-content:center;color:#64788b;overflow:hidden}.media-preview img,.media-preview video{width:100%;height:100%;min-height:140px;object-fit:cover}.form-checks,.form-actions{display:flex;align-items:center;gap:16px;flex-wrap:wrap;margin-top:10px}.form-actions{justify-content:space-between}.inventory-toolbar{display:flex;gap:8px;margin-bottom:12px}.inventory-toolbar input,.inventory-toolbar select{background:#0a121a;color:#fff;border:1px solid #2b4056;border-radius:9px;padding:10px}.inventory-toolbar input{flex:1}.card-actions{display:flex;align-items:center;gap:7px;flex-wrap:wrap}.video-link{font-size:10px;color:#9b5cff;font-weight:900;text-decoration:none;border:1px solid #3b2c5d;border-radius:8px;padding:7px 8px}.tool-strip{display:flex;gap:8px;flex-wrap:wrap}.tool-strip span{background:#101b26;border:1px solid #2b4056;border-radius:999px;padding:8px 11px;color:#9aacbd;font-size:11px;font-weight:800}.pairing-card{text-align:center;max-width:720px;margin:20px auto}.pair-code{font-size:30px;font-weight:900;letter-spacing:5px;padding:20px;margin:16px 0;border:2px solid #9b5cff;border-radius:16px;background:#090e15;word-break:break-all}.pair-instructions{background:#101923;border:1px solid #2b4056;border-radius:12px;padding:14px;margin:14px 0;text-align:left}.pairing-shortcut{display:flex;justify-content:space-between;align-items:center;gap:15px}.settings-form label{display:block;color:#9aacbd;font-size:11px;font-weight:800}.settings-form input,.settings-form textarea{box-sizing:border-box;background:#0a121a;color:#fff;border:1px solid #2b4056;border-radius:8px;padding:9px;width:100%;margin-top:5px}.settings-form textarea{min-height:120px}.identity-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.identity-grid div{background:#0a121a;border:1px solid #26384e;border-radius:12px;padding:14px}.identity-grid small,.identity-grid b{display:block}.identity-grid small{color:#718397;margin-bottom:6px}.identity-grid b{word-break:break-word}.chart-bars{height:205px;display:flex;align-items:end;justify-content:space-around;gap:8px;padding:15px 5px 5px;border-bottom:1px solid #26384e}.bar-col{height:190px;flex:1;display:flex;flex-direction:column;justify-content:end;align-items:center;gap:5px;color:#8293a5;font-size:9px}.bar-col span{font-size:9px;color:#dce8f2;min-height:14px}.bar{width:70%;max-width:42px;background:linear-gradient(180deg,#9b5cff,#39ff66);border-radius:7px 7px 2px 2px;min-height:4px}.status-bars{display:grid;gap:12px}.status-bars div{display:grid;grid-template-columns:1fr auto;gap:10px;align-items:center}.status-bars div:after{content:'';grid-column:1/-1;height:8px;background:linear-gradient(90deg,#9b5cff 0 55%,#1a2633 55%);border-radius:99px}.order-cards{display:grid;gap:10px}.order-card{display:grid;grid-template-columns:1.1fr 1fr .9fr auto;gap:14px;align-items:center;background:#0a121a;border:1px solid #26384e;border-radius:14px;padding:14px}.order-card h3{margin:4px 0}.order-card small{display:block;color:#718397;margin-top:4px}.order-card .inline-form{justify-content:flex-end}@media(max-width:900px){.media-pickers{grid-template-columns:1fr 1fr}.order-card{grid-template-columns:1fr 1fr}.order-card .inline-form{grid-column:1/-1;justify-content:flex-start}.identity-grid{grid-template-columns:1fr}}@media(max-width:650px){.media-pickers{grid-template-columns:1fr}.inventory-toolbar{flex-direction:column}.order-card{grid-template-columns:1fr}.pair-code{font-size:22px;letter-spacing:2px}}@media(max-width:1050px){.kpis{grid-template-columns:repeat(2,1fr)}.sc-side{position:static;width:auto;margin-bottom:14px}.sc-main{margin-left:0}.two-col{grid-template-columns:1fr}}@media(max-width:650px){.wrap{padding:10px}.welcome,.sc-top{align-items:flex-start;flex-direction:column}.kpis,.quick-grid{grid-template-columns:1fr}.top-actions{flex-wrap:wrap}}body{background:#000!important;background-image:radial-gradient(circle at 15% 10%,rgba(57,255,102,.06),transparent 26%),radial-gradient(circle at 85% 20%,rgba(255,30,60,.05),transparent 24%)}.sc-top{position:sticky;top:0;z-index:40;background:rgba(0,0,0,.72);backdrop-filter:blur(18px);-webkit-backdrop-filter:blur(18px)}.welcome,.card,.kpi,.inventory-card,.order-card{box-shadow:0 10px 35px rgba(0,0,0,.22)}.btn{transition:transform .2s,box-shadow .2s}.btn:hover{transform:translateY(-1px);box-shadow:0 0 24px rgba(57,255,102,.25),inset 0 0 12px rgba(255,255,255,.08)}.btn.violet:hover{box-shadow:0 0 24px rgba(155,92,255,.32)}.sc-link:hover,.sc-link.active{box-shadow:inset 0 0 20px rgba(57,255,102,.05),0 0 18px rgba(155,92,255,.06)}</style>";}

        private void CentralSellerProductSave(NetworkStream stream, string cookie, Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/login");return;}
            string id=Get(f,"id").Trim(); if(id.Length==0) id=NextWebProductId(u.StoreId);
            if(Get(f,"name").Trim().Length==0){WriteRedirect(stream,"/seller?view=products&error=nombre");return;}
            Dictionary<string,string> v=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); foreach(KeyValuePair<string,string> x in f) v[x.Key]=x.Value;
            v["storeId"]=u.StoreId; v["syncKey"]=GetStoreSyncKey(u.StoreId); v["productId"]=id; v["updatedAt"]=DateTime.UtcNow.ToString("o"); v["deleted"]="0";
            v["active"]=Get(f,"active")=="1"?"1":"0"; v["onlineEnabled"]=Get(f,"onlineEnabled")=="1"?"1":"0"; v["webImageUrl"]=Get(f,"imageUrl");
            if(string.IsNullOrWhiteSpace(Get(f,"slug"))) v["slug"]=Regex.Replace(Get(f,"name").ToLowerInvariant(),"[^a-z0-9]+","-").Trim('-');
            if(string.IsNullOrWhiteSpace(Get(f,"publicDescription"))) v["publicDescription"]=Get(f,"description");
            PublishProduct(v); WriteRedirect(stream,"/seller?view=products");
        }

        private void CentralSellerProductDelete(NetworkStream stream, string cookie, Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/login");return;}
            string id=Get(f,"id").Trim(); if(id.Length>0) DeleteProduct(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",u.StoreId},{"syncKey",GetStoreSyncKey(u.StoreId)},{"productId",id},{"updatedAt",DateTime.UtcNow.ToString("o")} });
            WriteRedirect(stream,"/seller?view=products");
        }

        private string GetStoreSyncKey(string storeId)
        {
            lock(_sync){ XElement store=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(storeId),StringComparison.OrdinalIgnoreCase)); return store==null?"":S(store,"SyncKey"); }
        }

        private string NextWebProductId(string storeId)
        {
            long max=1000000000000L; lock(_sync){ XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products"); XElement products=d.Root.Element("Products"); if(products!=null) foreach(XElement p in products.Elements("Product").Where(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(storeId),StringComparison.OrdinalIgnoreCase))){ long n; if(long.TryParse(S(p,"ProductId"),out n)&&n>=max) max=n+1; }} return max.ToString(CultureInfo.InvariantCulture);
        }

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
            List<CentralStore> stores=new List<CentralStore>(); string lines=StoreLines(query); using(StringReader reader=new StringReader(lines)){string line;while((line=reader.ReadLine())!=null){string[] p=line.Split('|');if(p.Length<12)continue;CentralStore cs=new CentralStore();cs.StoreId=Uri.UnescapeDataString(p[1]);cs.Name=Uri.UnescapeDataString(p[2]);cs.PublicUrl=Uri.UnescapeDataString(p[3]);cs.City=Uri.UnescapeDataString(p[4]);cs.Province=Uri.UnescapeDataString(p[5]);cs.Category=Uri.UnescapeDataString(p[6]);cs.Delivery=Uri.UnescapeDataString(p[10])=="1";cs.Pickup=Uri.UnescapeDataString(p[11])=="1";cs.Logo=p.Length>14?Uri.UnescapeDataString(p[14]):"";stores.Add(cs);}}
            List<XElement> orders=new List<XElement>();lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");orders=d.Root.Element("Orders")==null?new List<XElement>():d.Root.Element("Orders").Elements("Order").Where(x=>string.Equals(S(x,"CustomerEmail"),u.Email,StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>S(x,"CreatedAt")).Take(20).ToList();}
            StringBuilder b=new StringBuilder(AuthShellStart("Mi cuenta · NexoMarket"));b.Append(SellerCenterCss());b.Append("<header class='sc-top'><div class='brand'><span>NEXO</span>MARKET <small>BUYER CENTER</small></div><div class='top-actions'><a href='/' class='btn ghost'>Explorar tiendas</a><a href='/logout' class='btn ghost'>Salir</a></div></header><main class='buyer-main'><div class='welcome'><div><span class='eyebrow'>CUENTA DE COMPRADOR</span><h1>Hola, "+E(u.Name)+" 👋</h1><p>Encontrá tiendas, comprá y seguí tus pedidos desde una sola cuenta.</p></div><div class='account-mini'><b>Cuenta</b><strong>Activa</strong><small>"+E(u.Email)+"</small></div></div><div class='kpis'>"+KpiC("Pedidos",orders.Count.ToString(),"historial central","green")+KpiC("En proceso",orders.Count(x=>S(x,"Status")!="Entregado"&&S(x,"Status")!="Cancelado").ToString(),"compras abiertas","yellow")+KpiC("Entregados",orders.Count(x=>S(x,"Status")=="Entregado").ToString(),"compras finalizadas","green")+"</div><div class='section-title'><div><span class='eyebrow'>MARKETPLACE</span><h2>Tiendas disponibles</h2><p>Elegí una tienda para ver productos y comprar.</p></div></div><div class='store-grid'>");
            foreach(CentralStore st in stores){string logo=string.IsNullOrWhiteSpace(st.Logo)?"<span>N</span>":"<img src='"+E(st.Logo)+"' alt='"+E(st.Name)+"' loading='lazy'/>";b.Append("<a class='store-card' href='/store/"+Uri.EscapeDataString(st.StoreId)+"'><div class='store-logo'>"+logo+"</div><div><b>"+E(st.Name)+"</b><small>"+E(st.Category)+" · "+E(st.City)+"</small><span>● Disponible · "+(st.Delivery?"Delivery":"Retiro")+"</span></div></a>");}
            if(stores.Count==0)b.Append("<div class='card'><b>No hay tiendas disponibles todavía.</b><p>Volvé a explorar más tarde.</p></div>");
            b.Append("</div><div class='section-title'><div><span class='eyebrow'>MIS COMPRAS</span><h2>Últimos pedidos</h2></div></div><section class='card table-wrap'><table class='table'><tr><th>Pedido</th><th>Tienda</th><th>Fecha</th><th>Estado</th><th>Total</th></tr>");
            foreach(XElement o in orders)b.Append("<tr><td><b>#"+E(S(o,"CentralOrderId").Length>8?S(o,"CentralOrderId").Substring(0,8):S(o,"CentralOrderId"))+"</b></td><td>"+E(S(o,"StoreId"))+"</td><td>"+E(S(o,"CreatedAt"))+"</td><td>"+BadgeC(S(o,"Status"))+"</td><td><b>$ "+Money(S(o,"Total")).ToString("N2")+"</b></td></tr>");
            if(orders.Count==0)b.Append("<tr><td colspan='5' class='muted'>Todavía no realizaste compras. Entrá a una tienda para comenzar.</td></tr>");
            b.Append("</table></section></main>");b.Append("<style>.buyer-main{max-width:1200px;margin:auto}.store-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.store-card{display:flex;gap:12px;align-items:center;background:#0e1721;border:1px solid #26384e;border-radius:16px;padding:15px;color:#fff;text-decoration:none}.store-card:hover{border-color:#39ff66}.store-logo{width:64px;height:64px;border-radius:14px;background:#0b1118;border:1px solid #7b4bd1;color:#b98cff;display:flex;align-items:center;justify-content:center;font-weight:900;font-size:23px;overflow:hidden}.store-logo img{width:100%;height:100%;object-fit:cover}.store-card b,.store-card small,.store-card span{display:block}.store-card small{color:#8193a5;margin-top:4px}.store-card span{color:#39ff66;font-size:10px;margin-top:6px}@media(max-width:1000px){.store-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.store-grid{grid-template-columns:1fr}}</style>");b.Append(AuthShellEnd());Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private string AuthPage(string title,string content){return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · NexoMarket</title><style>"+AuthCss()+"</style></head><body><div class='wrap'>"+content+"</div></body></html>";}
        private string AuthShellStart(string title){return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · NexoMarket</title><style>"+AuthCss()+"</style></head><body><div class='wrap'>";}
        private string AuthShellEnd(){return "</div></body></html>";}
        private string AuthCss(){return "body{font-family:'Segoe UI',Arial;background:#080b10;color:#fff;margin:0}.wrap{max-width:850px;margin:auto;padding:30px}.card,.empty{background:#0e1721;border:1px solid #2a4660;border-radius:18px;padding:20px;margin-top:16px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px}input,select,textarea{display:block;width:100%;box-sizing:border-box;background:#0b131c;color:#fff;border:1px solid #2d4660;border-radius:10px;padding:12px;margin:10px 0}textarea{min-height:120px;resize:vertical}.btn{display:inline-block;background:#39ff66;color:#061009;text-decoration:none;border:0;border-radius:10px;padding:11px 16px;font-weight:900;cursor:pointer;margin-top:8px}.btn.alt{background:#13202c;color:#fff;border:1px solid #2d4660}.muted{color:#91a4b6}.error{background:#34151b;border:1px solid #6b2630;padding:14px;border-radius:12px;margin-bottom:14px}.empty{color:#9aabba}";}

        private static string HeaderCookie(string request,string name){string c=HeaderValue(request,"Cookie");foreach(string part in c.Split(';')){string[] p=part.Trim().Split(new[]{'='},2);if(p.Length==2&&string.Equals(p[0],name,StringComparison.OrdinalIgnoreCase))return p[1];}return "";}
        private static string HeaderValue(string request,string name){foreach(string line in (request??"").Split(new[]{"\r\n"},StringSplitOptions.None)){int i=line.IndexOf(':');if(i>0&&string.Equals(line.Substring(0,i).Trim(),name,StringComparison.OrdinalIgnoreCase))return line.Substring(i+1).Trim();}return "";}
        private static void WriteRedirectCookie(NetworkStream stream,string location,string cookie){byte[] data=Encoding.UTF8.GetBytes("<html><body>Redirigiendo...</body></html>");string h="HTTP/1.1 302 Found\r\nLocation: "+location+"\r\nSet-Cookie: "+cookie+"\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: "+data.Length+"\r\nConnection: close\r\n\r\n";byte[] head=Encoding.ASCII.GetBytes(h);stream.Write(head,0,head.Length);stream.Write(data,0,data.Length);stream.Flush();}

        private DateTime ParseUtcDate(string value)
        {
            DateTime d;
            if (DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out d)) return d.ToUniversalTime();
            return DateTime.MinValue;
        }

        private double ParseDouble(string value) { double d; return double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0d; }

        private sealed class CentralUser
        {
            public string Id="",Name="",Email="",Phone="",Role="buyer",StoreId="",Salt="",PasswordHash="",CreatedAt="";
            public static CentralUser From(XElement e){return new CentralUser{Id=S(e,"Id"),Name=S(e,"Name"),Email=S(e,"Email"),Phone=S(e,"Phone"),Role=S(e,"Role")=="seller"?"seller":"buyer",StoreId=S(e,"StoreId"),Salt=S(e,"Salt"),PasswordHash=S(e,"PasswordHash"),CreatedAt=S(e,"CreatedAt")};}
            public static CentralUser From(Dictionary<string,string> d){return new CentralUser{Id=d.ContainsKey("id")?d["id"]:"",Name=d.ContainsKey("name")?d["name"]:"",Email=d.ContainsKey("email")?d["email"]:"",Phone=d.ContainsKey("phone")?d["phone"]:"",Role=d.ContainsKey("role")&&d["role"]=="seller"?"seller":"buyer",StoreId=d.ContainsKey("storeId")?d["storeId"]:"",Salt=d.ContainsKey("salt")?d["salt"]:"",PasswordHash=d.ContainsKey("passwordHash")?d["passwordHash"]:"",CreatedAt=d.ContainsKey("createdAt")?d["createdAt"]:""};}
        }
        private sealed class CentralStore
        {
            public string StoreId = ""; public string Name = ""; public string Category = ""; public string City = ""; public string Province = ""; public string PublicUrl = ""; public string Logo = ""; public bool Delivery; public bool Pickup; public double Latitude; public double Longitude; public double Distance; public bool Active;
            public CentralStore() { }
            public CentralStore(XElement e) { StoreId=S(e,"StoreId"); Name=S(e,"Name"); Category=S(e,"Category"); City=S(e,"City"); Province=S(e,"Province"); PublicUrl=S(e,"PublicUrl"); Logo=S(e,"Logo"); Delivery=S(e,"Delivery")=="1"; Pickup=S(e,"Pickup")=="1"; Active=S(e,"Active")=="1"; double.TryParse(S(e,"Latitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out Latitude); double.TryParse(S(e,"Longitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out Longitude); }
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
