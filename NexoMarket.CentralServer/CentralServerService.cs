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
        private readonly object _orderCreateGate = new object();
        private readonly Dictionary<string, CentralUser> _sessions = new Dictionary<string, CentralUser>(StringComparer.OrdinalIgnoreCase);
        private TcpListener _listener;
        private System.Threading.Thread _worker;
        private System.Threading.Timer _cleanupTimer;
        private System.Threading.Timer _scheduleTimer;
        private volatile bool _running;
        private XDocument _doc;
        private readonly R2ObjectStore _r2;
        private readonly CentralDatabase _database;
        private readonly TransactionalEmailService _email;
        private readonly string _auditFile;
        private readonly string _idempotencyFile;
        private readonly string _reviewsFile;

        public CentralServerService(int port)
        {
            _port = port;
            _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _file = Path.Combine(_root, "nexomarket_stores.xml");
            _catalogFile = Path.Combine(_root, "nexomarket_catalog.xml");
            _ordersFile = Path.Combine(_root, "nexomarket_orders.xml");
            _accountsFile = Path.Combine(_root, "nexomarket_accounts.xml");
            _auditFile = Path.Combine(_root, "nexomarket_audit.xml");
            _idempotencyFile = Path.Combine(_root, "nexomarket_idempotency.xml");
            _reviewsFile = Path.Combine(_root, "nexomarket_reviews.xml");
            Directory.CreateDirectory(_root);
            _r2 = new R2ObjectStore();
            _database = new CentralDatabase();
            _email = new TransactionalEmailService(_root);
            // Restaurar TODOS los datos persistentes antes de cargar o guardar cualquier documento.
            // En versiones anteriores Load() podía crear un registro vacío y subirlo a R2 antes
            // de restaurar los datos, borrando las tiendas al reiniciar Render.
            RestoreLatest(_file, "data/nexomarket_stores.xml");
            RestoreLatest(_catalogFile, "data/nexomarket_catalog.xml");
            RestoreLatest(_ordersFile, "data/nexomarket_orders.xml");
            RestoreLatest(_accountsFile, "data/nexomarket_accounts.xml");
            RestoreLatest(_auditFile, "data/nexomarket_audit.xml");
            RestoreLatest(_idempotencyFile, "data/nexomarket_idempotency.xml");
            RestoreLatest(_reviewsFile, "data/nexomarket_reviews.xml");
            Load();
            EnsureCentralDataFiles();
            EnsureOperationalFiles();
            CleanupInactiveStores();
            _cleanupTimer = new System.Threading.Timer(delegate(object state) { try { CleanupInactiveStores(); } catch { } }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
            // Revisa los horarios automáticamente aunque nadie esté navegando por el directorio.
            // No interviene en syncKey, catálogo, stock ni recepción de pedidos.
            _scheduleTimer = new System.Threading.Timer(delegate(object state) { try { ApplyAutomaticSchedulesAllStores(); } catch { } }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
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
                try { if (_cleanupTimer != null) _cleanupTimer.Dispose(); } catch { }
                try { if (_scheduleTimer != null) _scheduleTimer.Dispose(); } catch { }
                try { if (_email != null) _email.Dispose(); } catch { }
                _cleanupTimer = null;
                _scheduleTimer = null;
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

        private static int BusinessDaysBetween(DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc <= fromUtc) return 0;
            DateTime d = fromUtc.Date; DateTime end = toUtc.Date; int count = 0;
            while (d < end)
            {
                d = d.AddDays(1);
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) count++;
            }
            return count;
        }

        private void CleanupInactiveStores()
        {
            try
            {
                bool changedStores = false;
                List<string> deleted = new List<string>();
                lock (_sync)
                {
                    XElement stores = _doc.Root.Element("Stores");
                    if (stores == null) return;
                    DateTime now = DateTime.UtcNow;
                    foreach (XElement store in stores.Elements("Store").ToList())
                    {
                        string lastRaw = S(store, "LastActivityAt");
                        DateTime last;
                        if (!DateTime.TryParse(lastRaw, null, DateTimeStyles.RoundtripKind, out last))
                        {
                            DateTime updated;
                            if (!DateTime.TryParse(S(store, "UpdatedAt"), null, DateTimeStyles.RoundtripKind, out updated)) updated = now;
                            last = updated.ToUniversalTime();
                            store.SetElementValue("LastActivityAt", last.ToString("o"));
                            changedStores = true;
                        }
                        if (BusinessDaysBetween(last.ToUniversalTime(), now) < 30) continue;
                        string id = S(store, "StoreId");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        deleted.Add(id);
                        store.Remove();
                        changedStores = true;
                    }
                    if (changedStores) Save();
                }
                foreach (string storeId in deleted)
                {
                    try
                    {
                        XDocument c = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                        if (c.Root.Element("Products") != null) foreach (XElement e in c.Root.Element("Products").Elements("Product").Where(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase)).ToList()) e.Remove();
                        if (c.Root.Element("Promotions") != null) foreach (XElement e in c.Root.Element("Promotions").Elements("Promotion").Where(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase)).ToList()) e.Remove();
                        if (c.Root.Element("Coupons") != null) foreach (XElement e in c.Root.Element("Coupons").Elements("Coupon").Where(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase)).ToList()) e.Remove();
                        SaveDoc(_catalogFile, c);
                    } catch { }
                    try
                    {
                        XDocument a = LoadFile(_accountsFile, "NexoMarketAccounts", "Users");
                        if (a.Root.Element("Users") != null) foreach (XElement e in a.Root.Element("Users").Elements("User").Where(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase)).ToList()) e.Remove();
                        SaveDoc(_accountsFile, a);
                    } catch { }
                    try { if (_database != null && _database.Enabled) _database.DeleteAccountsForStore(storeId); } catch { }
                }
            }
            catch { }
        }

        private void TouchStoreActivity(string storeId)
        {
            storeId = NormalizeStoreId(storeId);
            if (storeId.Length == 0) return;
            lock (_sync)
            {
                XElement stores = _doc.Root.Element("Stores");
                XElement store = stores == null ? null : stores.Elements("Store").FirstOrDefault(x => string.Equals(S(x,"StoreId"), storeId, StringComparison.OrdinalIgnoreCase));
                if (store == null) return;
                store.SetElementValue("LastActivityAt", DateTime.UtcNow.ToString("o"));
                store.SetAttributeValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                Save();
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
                    client.ReceiveTimeout = 60000; client.SendTimeout = 60000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        string request = ReadRequest(stream); if (string.IsNullOrEmpty(request)) return;
                        string[] first = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                        string method = first.Length > 0 ? first[0].ToUpperInvariant() : "GET";
                        string target = first.Length > 1 ? first[1] : "/";
                        string body = Body(request); string path = target; string query = ""; int q = path.IndexOf('?');
                        if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }
                        if (path == "/health" || path == "/healt") { Write(stream, 200, "text/plain", "NexoMarket Central OK\n"); return; }
                        if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase) && method == "GET") { ServeMedia(stream, path.Substring(7)); return; }
                        if (path == "/api/central/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", CentralDatabaseStatus()); return; }
                        if (path == "/api/admin/stores" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AdminStores(HeaderValue(request,"X-Nexo-Admin-Key"))); return; }
                        if (path == "/api/admin/accounts" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AdminAccounts(HeaderValue(request,"X-Nexo-Admin-Key"))); return; }
                        if (path == "/api/admin/overview" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AdminOverview(HeaderValue(request,"X-Nexo-Admin-Key"))); return; }
                        if (path == "/api/admin/store/create" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminCreateStore(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/delete" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminDeleteStore(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/active" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetStoreActive(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/featured" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetStoreFeatured(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/featured-plus" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetStoreFeaturedPlus(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/listed" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetStoreListed(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/account/trial" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetTrial(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/account/active" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetAccountActive(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/account/delete" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminDeleteAccount(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/commissions" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", AdminCommissions(HeaderValue(request,"X-Nexo-Admin-Key"))); return; }
                        if (path == "/api/admin/store/commission" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetCommission(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/commission-action" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminCommissionAction(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/store/details" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", AdminStoreDetails(HeaderValue(request,"X-Nexo-Admin-Key"), QueryValue(query,"storeId"))); return; }
                        if (path == "/api/admin/store/plan" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminSetStorePlan(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/account/password" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminResetAccountPassword(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/admin/factory-reset" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AdminFactoryReset(HeaderValue(request,"X-Nexo-Admin-Key"), Form(body))); return; }
                        if (path == "/api/accounts/upsert" && method == "POST") { Write(stream, 200, "text/plain", AccountUpsert(Form(body), true)); return; }
                        if (path == "/api/auth/register-seller" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CentralRegisterSellerApi(Form(body))); return; }
                        if (path == "/api/auth/login" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CentralLoginApi(Form(body))); return; }
                        if (path == "/api/pair/start" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", PairStart(Form(body))); return; }
                        if (path == "/api/pair/complete" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", PairComplete(Form(body))); return; }
                        if (path == "/api/devices/validate" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", DeviceValidate(Form(body))); return; }
                        if (path == "/login") { CentralLogin(stream, method, body, HeaderCookie(request, "NexoCentralSession")); return; }
                        if (path == "/seller-login") { CentralSellerStoreLogin(stream, method, body); return; }
                        if (path == "/register") { CentralRegister(stream, method, body); return; }
                        if (path == "/forgot-password") { CentralForgotPassword(stream, method, body); return; }
                        if (path == "/reset-password") { CentralResetPassword(stream, method, body); return; }
                        if (path == "/seller-register") { CentralSellerRegister(stream, method, body); return; }
                        if (path == "/logout") { CentralLogout(stream); return; }
                        if (path == "/seller") { CentralSeller(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/seller/devices") { CentralSellerDevices(stream, HeaderCookie(request, "NexoCentralSession"), method, body); return; }
                        if (path == "/seller/order-status" && method == "POST") { CentralSellerOrderStatus(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/products/save" && method == "POST") { CentralSellerProductSave(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/media/upload" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CentralSellerMediaUpload(stream, HeaderCookie(request, "NexoCentralSession"), Form(body))); return; }
                        if (path == "/seller/delivery-status" && method == "POST") { CentralSellerDeliveryStatus(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/pos-sale" && method == "POST") { CentralSellerPosSale(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/ticket" && method == "GET") { CentralSellerTicket(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/api/seller/media/status" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", SellerMediaStatus(HeaderCookie(request, "NexoCentralSession"))); return; }
                        if (path == "/seller/store/save" && method == "POST") { CentralSellerStoreSave(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/store/toggle" && method == "POST") { CentralSellerStoreToggle(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/seller/products/delete" && method == "POST") { CentralSellerProductDelete(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; } 
                        if (path == "/seller/coupon/save" && method == "POST") { CentralSellerCouponSave(stream, HeaderCookie(request, "NexoCentralSession"), Form(body)); return; }
                        if (path == "/buyer") { CentralBuyer(stream, HeaderCookie(request, "NexoCentralSession"), query); return; }
                        if (path == "/api/storage/status" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StorageStatus()); return; }
                        if (path == "/api/storage/test" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", StorageTest()); return; }
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
                        if (path == "/api/seller/commission" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", SellerCommissionJson(HeaderCookie(request, "NexoCentralSession"))); return; }
                        if (path == "/seller/order-detail" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", SellerOrderDetail(HeaderCookie(request, "NexoCentralSession"), QueryValue(query, "id"))); return; }
                        if (path == "/seller/order-approve" && method == "POST") { Write(stream, 200, "application/json; charset=utf-8", SellerApproveOrder(HeaderCookie(request, "NexoCentralSession"), Form(body))); return; }
                        if (path == "/api/catalog/lines" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", CatalogLines(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
                        if (path == "/api/sync/delta" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", SyncDelta(QueryValue(query, "storeId"), QueryValue(query, "syncKey"), QueryValue(query, "since"))); return; }
                        if (path == "/api/order-proof/upload" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", UploadOrderProof(Form(body))); return; }
                        if (path == "/api/orders/create" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CreateOrder(Form(body))); return; }
                        if (path == "/api/orders/payment" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", UpdateOrderPayment(Form(body))); return; }
                        if (path == "/api/payments/webhook" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", PaymentWebhook(request, body)); return; }
                        if (path == "/api/orders/cancel" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", CancelOrder(Form(body))); return; }
                        if (path == "/api/orders/refund" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", RefundOrder(Form(body))); return; }
                        if (path == "/api/orders/receipt" && method == "GET") { Write(stream, 200, "text/html; charset=utf-8", OrderReceipt(QueryValue(query, "centralOrderId"), QueryValue(query, "email"))); return; }
                        if (path == "/api/auth/forgot" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", ForgotPassword(Form(body))); return; }
                        if (path == "/api/auth/reset" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", ResetPassword(Form(body))); return; }
                        if (path == "/api/audit" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", AuditJson(HeaderValue(request,"X-Nexo-Admin-Key"), QueryValue(query, "storeId"), QueryValue(query, "limit"))); return; }
                        if (path == "/api/orders/pending" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", PendingOrders(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
                        if (path == "/api/orders/snapshot" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", OrdersSnapshot(QueryValue(query, "storeId"), QueryValue(query, "syncKey"), QueryValue(query, "since"))); return; }
                        if (path == "/api/orders/ack" && method == "POST") { Write(stream, 200, "text/plain", AckOrder(Form(body))); return; }
                        if (path == "/api/orders/status" && method == "POST") { Write(stream, 200, "text/plain", UpdateOrderStatus(Form(body))); return; }
                        if (path == "/api/orders/status" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", GetOrderStatus(QueryValue(query, "storeId"), QueryValue(query, "centralOrderId"))); return; }
                        if (path == "/api/orders/status-delta" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", OrderStatusDelta(QueryValue(query, "storeId"), QueryValue(query, "syncKey"), QueryValue(query, "since"))); return; }
                        if (path == "/api/orders/confirm" && method == "POST") { Write(stream, 200, "text/plain", ConfirmOrder(Form(body))); return; }
                        if (path == "/api/reviews" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", ReviewsJson(QueryValue(query, "storeId"))); return; }
                        if (path == "/api/reviews/save" && method == "POST") { Write(stream, 200, "application/json; charset=utf-8", SaveReview(HeaderCookie(request, "NexoCentralSession"), Form(body))); return; }
                        if (path == "/api/orders/history" && method == "GET") { Write(stream, 200, "application/json; charset=utf-8", HistoryOrders(QueryValue(query, "storeId"), QueryValue(query, "email"))); return; }
                        if (path == "/api/sync/heartbeat" && method == "POST") { Write(stream, 200, "text/plain", Heartbeat(Form(body))); return; }
                        if (path == "/api/accounts/auth" && method == "POST") { Write(stream, 200, "text/plain; charset=utf-8", AccountAuthenticate(Form(body))); return; }
                        if (path == "/api/accounts/lookup" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AccountLookup(QueryValue(query, "email"), QueryValue(query, "accountId"))); return; }
                        if (path == "/api/accounts" && method == "GET") { Write(stream, 200, "text/plain; charset=utf-8", AccountLines(QueryValue(query, "storeId"), QueryValue(query, "syncKey"))); return; }
                        if (path == "/" || path == "/stores") { Write(stream, 200, "text/html; charset=utf-8", Marketplace(query, HeaderCookie(request, "NexoCentralSession"))); return; }
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
                    if (total >= 16 * 1024 * 1024) break;
                    string partial = Encoding.UTF8.GetString(ms.ToArray());
                    if (partial.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                    {
                        int contentLength = 0;
                        Match m = Regex.Match(partial, @"(?im)^Content-Length:\s*(\d+)");
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
                string activity = old == null ? DateTime.UtcNow.ToString("o") : S(old, "LastActivityAt");
                if (string.IsNullOrWhiteSpace(activity)) activity = DateTime.UtcNow.ToString("o");
                XElement e = new XElement("Store", new XAttribute("UpdatedAt", string.IsNullOrWhiteSpace(Get(f,"updatedAt")) ? DateTime.UtcNow.ToString("o") : Get(f, "updatedAt")),
                    new XElement("LastActivityAt", DateTime.UtcNow.ToString("o")),
                    new XElement("StoreId", id), new XElement("SyncKey", syncKey), new XElement("Name", Get(f, "name")), new XElement("LegalName", Get(f, "legalName")),
                    new XElement("Category", Get(f, "category")), new XElement("Address", Get(f, "address")), new XElement("City", Get(f, "city")),
                    new XElement("Province", Get(f, "province")), new XElement("Description", Get(f, "description")), new XElement("SystemName", Get(f, "systemName")), new XElement("Logo", Get(f, "logo")), new XElement("StorePhoto", Get(f, "storePhoto")),
                    new XElement("AutoSchedule", Get(f,"autoSchedule") == "1" ? "1" : "0"), new XElement("OpenTime", string.IsNullOrWhiteSpace(Get(f,"openTime")) ? "08:00" : Get(f,"openTime")), new XElement("CloseTime", string.IsNullOrWhiteSpace(Get(f,"closeTime")) ? "22:00" : Get(f,"closeTime")),
                    new XElement("Slug", Get(f, "slug")), new XElement("PublicUrl", Get(f, "publicUrl")), new XElement("Featured", Get(f,"featured") == "1" ? "1" : "0"), new XElement("FeaturedPlus", Get(f,"featuredPlus") == "1" ? "1" : "0"), new XElement("Listed", Get(f,"listed") == "0" ? "0" : "1"), new XElement("Active", active),
                    new XElement("Delivery", Get(f, "delivery") == "0" ? "0" : "1"), new XElement("Pickup", Get(f, "pickup") == "0" ? "0" : "1"),
                    new XElement("Latitude", Get(f, "latitude")), new XElement("Longitude", Get(f, "longitude")),
                    new XElement("CommissionPercent", string.IsNullOrWhiteSpace(Get(f,"commissionPercent")) ? "1" : Get(f,"commissionPercent")), new XElement("CommissionPaidMonth", Get(f,"commissionPaidMonth")), new XElement("CommissionDueDate", Get(f,"commissionDueDate")));
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
                    new XElement("LastActivityAt", DateTime.UtcNow.ToString("o")),
                    new XElement("StoreId", id), new XElement("SyncKey", syncKey), new XElement("Name", name),
                    new XElement("LegalName", Get(f,"legalName")), new XElement("Category", string.IsNullOrWhiteSpace(Get(f,"category")) ? "Comercio" : Get(f,"category")),
                    new XElement("Address", Get(f,"address")), new XElement("City", Get(f,"city")), new XElement("Province", Get(f,"province")),
                    new XElement("Description", string.IsNullOrWhiteSpace(Get(f,"description")) ? "Tienda NexoMarket" : Get(f,"description")),
                    new XElement("SystemName", Get(f,"systemName")),
                    new XElement("Logo", Get(f,"logo")), new XElement("StorePhoto", Get(f,"storePhoto")), new XElement("AutoSchedule", Get(f,"autoSchedule") == "1" ? "1" : "0"), new XElement("OpenTime", string.IsNullOrWhiteSpace(Get(f,"openTime")) ? "08:00" : Get(f,"openTime")), new XElement("CloseTime", string.IsNullOrWhiteSpace(Get(f,"closeTime")) ? "22:00" : Get(f,"closeTime")), new XElement("Slug", slug),
                    new XElement("PublicUrl", string.IsNullOrWhiteSpace(Get(f,"publicUrl")) ? "/store/" + Uri.EscapeDataString(id) : Get(f,"publicUrl")),
                    new XElement("Featured", "0"), new XElement("FeaturedPlus", "0"), new XElement("Listed", "1"), new XElement("Active", "1"), new XElement("Delivery", Get(f,"delivery") == "0" ? "0" : "1"),
                    new XElement("Pickup", Get(f,"pickup") == "0" ? "0" : "1"), new XElement("Latitude", Get(f,"latitude")), new XElement("Longitude", Get(f,"longitude")),
                    new XElement("CommissionPercent", "1"), new XElement("CommissionPaidMonth", ""), new XElement("CommissionDueDate", ""));
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

        public string DatabaseStatusForLog()
        {
            return (_database != null && _database.Enabled) ? _database.Status() : "disabled";
        }

        private static string A(XElement e, string name) { XAttribute a = e == null ? null : e.Attribute(name); return a == null ? "" : a.Value; }

                private string StorageStatus()
        {
            return _r2 != null && _r2.Enabled ? "OK|R2|enabled" : "ERROR|R2|not_configured";
        }

        private string StorageTest()
        {
            if (_r2 == null || !_r2.Enabled) return "ERROR|R2_NOT_CONFIGURED";
            string key = "health/r2-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".txt";
            string error;
            bool ok = _r2.PutBytes(key, Encoding.UTF8.GetBytes("NexoMarket R2 OK " + DateTime.UtcNow.ToString("o")), "text/plain; charset=utf-8", out error);
            return ok ? "OK|R2_WRITE|" + key : "ERROR|R2_WRITE|" + Escape(error);
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
                byte[] bytes;
                try
                {
                    // El navegador envía Base64URL para evitar que +, / y = sean alterados
                    // por application/x-www-form-urlencoded.
                    string normalized = base64.Replace("-", "+").Replace("_", "/");
                    while ((normalized.Length % 4) != 0) normalized += "=";
                    bytes = Convert.FromBase64String(normalized);
                }
                catch { return "ERROR|invalid_base64"; }
                if (bytes.Length > 8 * 1024 * 1024) return "ERROR|too_large";
                string safeName = Regex.Replace(fileName, "[^a-zA-Z0-9._-]", "_");
                string key = "stores/" + Regex.Replace(storeId, "[^a-zA-Z0-9_-]", "_") + "/media/" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + safeName;
                string r2Error;
                if (!_r2.PutBytes(key, bytes, contentType, out r2Error)) return "ERROR|upload|" + Escape(r2Error);
                string url = MediaUrl(key);
                if (string.IsNullOrWhiteSpace(url)) return "ERROR|PUBLIC_BASE_URL_NOT_CONFIGURED";
                return "OK|" + key + "|" + url;
            }
            catch { return "ERROR|upload_exception"; }
        }

        private string MediaUrl(string key)
        {
            // La tienda y el endpoint /media viven en el mismo dominio de NexoMarket.
            // Usamos una URL relativa para que las imágenes funcionen aunque Render no
            // tenga PUBLIC_BASE_URL configurada y sin depender del dominio público de R2.
            // Servimos siempre por el mismo dominio de NexoMarket.
            // Así la web no depende de que el bucket R2 sea público ni de CORS.
            // El servidor central autentica contra R2 y entrega la imagen inline.
            return "/media/" + Uri.EscapeDataString(key.TrimStart('/')).Replace("%2F", "/");
        }

        private static void Write(NetworkStream stream, int status, string contentType, byte[] data)
        {
            data = data ?? new byte[0];
            string statusText = status == 200 ? "OK" : status == 404 ? "Not Found" : "Error";
            string header = "HTTP/1.1 " + status + " " + statusText + "\r\n" +
                            "Content-Type: " + contentType + "\r\n" +
                            "Cache-Control: " + (status >= 200 && status < 300 ? "public, max-age=31536000, immutable" : "no-store") + "\r\n" +
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
            string catalog = CatalogLiveJson(u.StoreId);
            int pending = 0; DateTime latestOrder = DateTime.MinValue;
            lock (_sync)
            {
                XDocument od = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                XElement root = od.Root == null ? null : od.Root.Element("Orders");
                if (root != null)
                {
                    foreach (XElement o in root.Elements("Order").Where(x => string.Equals(S(x, "StoreId"), u.StoreId, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (S(o, "Status") == "Pendiente") pending++;
                        DateTime t = ParseUtcDate(S(o, "CreatedAt")); if (t > latestOrder) latestOrder = t;
                    }
                }
            }
            string extra = catalog.TrimEnd('}') + ",\"pendingOrders\":" + pending.ToString(CultureInfo.InvariantCulture) + ",\"latestOrderAt\":" + JsonString(latestOrder == DateTime.MinValue ? "" : latestOrder.ToString("o")) + "}";
            Write(stream, 200, "application/json; charset=utf-8", extra);
        }

        private void EnsureOperationalFiles()
        {
            try
            {
                lock (_sync)
                {
                    if (!File.Exists(_auditFile)) SaveDoc(_auditFile, new XDocument(new XElement("NexoMarketAudit", new XElement("Events"))));
                    if (!File.Exists(_idempotencyFile)) SaveDoc(_idempotencyFile, new XDocument(new XElement("NexoMarketIdempotency", new XElement("Requests"))));
                    if (!File.Exists(_reviewsFile)) SaveDoc(_reviewsFile, new XDocument(new XElement("NexoMarketReviews", new XElement("Reviews"))));
                }
            }
            catch { }
        }

        private void Audit(string action, string storeId, string userEmail, string entityId, string detail)
        {
            try
            {
                lock (_sync)
                {
                    XDocument d = LoadFile(_auditFile, "NexoMarketAudit", "Events");
                    XElement root = d.Root;
                    if (root.Element("Events") == null) root.Add(new XElement("Events"));
                    root.Element("Events").Add(new XElement("Event",
                        new XElement("Id", Guid.NewGuid().ToString("N")),
                        new XElement("At", DateTime.UtcNow.ToString("o")),
                        new XElement("Action", action ?? ""),
                        new XElement("StoreId", storeId ?? ""),
                        new XElement("UserEmail", userEmail ?? ""),
                        new XElement("EntityId", entityId ?? ""),
                        new XElement("Detail", detail ?? "")));
                    var old = root.Element("Events").Elements("Event").Take(Math.Max(0, root.Element("Events").Elements("Event").Count() - 5000)).ToList();
                    foreach (XElement e in old) e.Remove();
                    SaveDoc(_auditFile, d);
                }
            }
            catch { }
        }

        private string AuditJson(string key, string storeId, string limitText)
        {
            string denied=AdminDenied(key); if(denied!=null) return "[{\"error\":\"admin_unauthorized\"}]";
            int limit = 100;
            if (!int.TryParse(limitText, out limit)) limit = 100;
            limit = Math.Max(1, Math.Min(500, limit));
            lock (_sync)
            {
                XDocument d = LoadFile(_auditFile, "NexoMarketAudit", "Events");
                IEnumerable<XElement> q = d.Root.Element("Events").Elements("Event").Reverse();
                if (!string.IsNullOrWhiteSpace(storeId)) q = q.Where(x => S(x, "StoreId") == storeId);
                q = q.Take(limit);
                StringBuilder b = new StringBuilder("[");
                bool first = true;
                foreach (XElement e in q)
                {
                    if (!first) b.Append(',');
                    first = false;
                    b.Append("{\"id\":").Append(JsonString(S(e, "Id")))
                     .Append(",\"at\":").Append(JsonString(S(e, "At")))
                     .Append(",\"action\":").Append(JsonString(S(e, "Action")))
                     .Append(",\"storeId\":").Append(JsonString(S(e, "StoreId")))
                     .Append(",\"userEmail\":").Append(JsonString(S(e, "UserEmail")))
                     .Append(",\"entityId\":").Append(JsonString(S(e, "EntityId")))
                     .Append(",\"detail\":").Append(JsonString(S(e, "Detail"))).Append('}');
                }
                return b.Append(']').ToString();
            }
        }

        private string GetIdempotentOrder(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            lock (_sync)
            {
                XDocument d = LoadFile(_idempotencyFile, "NexoMarketIdempotency", "Requests");
                XElement e = d.Root.Element("Requests").Elements("Request")
                    .FirstOrDefault(x => S(x, "Key") == key && S(x, "Type") == "order");
                return e == null ? "" : S(e, "Result");
            }
        }

        private void SaveIdempotency(string key, string result)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (_sync)
            {
                XDocument d = LoadFile(_idempotencyFile, "NexoMarketIdempotency", "Requests");
                XElement root = d.Root.Element("Requests");
                XElement old = root.Elements("Request").FirstOrDefault(x => S(x, "Key") == key && S(x, "Type") == "order");
                XElement e = new XElement("Request", new XElement("Key", key), new XElement("Type", "order"),
                    new XElement("Result", result), new XElement("At", DateTime.UtcNow.ToString("o")));
                if (old != null) old.ReplaceWith(e); else root.Add(e);
                foreach (XElement x in root.Elements("Request").OrderBy(x => S(x, "At")).Take(Math.Max(0, root.Elements("Request").Count() - 2000)).ToList()) x.Remove();
                SaveDoc(_idempotencyFile, d);
            }
        }

        private string BuildReceiptHtml(XElement o, string storeName)
        {
            StringBuilder b = new StringBuilder();
            b.Append("<!doctype html><html><head><meta charset='utf-8'><style>body{font-family:Segoe UI,Arial;background:#f4f6f8;color:#1c2430;padding:28px}main{max-width:760px;margin:auto;background:white;padding:28px;border-radius:14px}h1{margin-top:0}.total{font-size:28px;font-weight:700}.muted{color:#667085}table{width:100%;border-collapse:collapse;margin-top:20px}td,th{padding:9px;border-bottom:1px solid #e7eaf0;text-align:left}</style></head><body><main>");
            b.Append("<h1>NexoMarket · Comprobante</h1><p class='muted'>").Append(E(storeName)).Append("</p>");
            b.Append("<p><b>Pedido:</b> #").Append(E(S(o, "CentralOrderId"))).Append("<br/><b>Fecha:</b> ").Append(E(S(o, "CreatedAt"))).Append("<br/><b>Cliente:</b> ").Append(E(S(o, "CustomerName"))).Append("</p>");
            b.Append("<table><tr><th>Detalle</th><th>Importe</th></tr>");
            b.Append("<tr><td>Productos y servicios</td><td>").Append(E(S(o, "Total"))).Append("</td></tr>");
            b.Append("<tr><td>Envío</td><td>").Append(E(S(o, "ShippingCost"))).Append("</td></tr>");
            b.Append("<tr><th>TOTAL</th><th class='total'>").Append(E(S(o, "Total"))).Append("</th></tr></table>");
            b.Append("<p class='muted'>Método de pago: ").Append(E(S(o, "PaymentMethod"))).Append(" · Estado: ").Append(E(S(o, "PaymentStatus"))).Append("</p>");
            b.Append("</main></body></html>");
            return b.ToString();
        }

        private void QueueOrderEmails(XElement o, string eventName)
        {
            if (_email == null) return;
            string email = S(o, "CustomerEmail");
            if (string.IsNullOrWhiteSpace(email)) return;
            string orderId = S(o, "CentralOrderId");
            string storeName = GetStoreName(S(o, "StoreId"));
            string subject = "NexoMarket · Pedido #" + (orderId.Length > 8 ? orderId.Substring(0, 8) : orderId) + " · " + eventName;
            string receipt = BuildReceiptHtml(o, storeName);
            string html = receipt.Replace("</main>", "<p>Estado: <b>" + E(S(o, "Status")) + "</b></p></main>");
            _email.Queue(email, subject, html, "NexoMarket\nPedido #" + orderId + "\nEstado: " + S(o, "Status") + "\nTotal: " + S(o, "Total"));
        }

        private string PaymentWebhook(string requestText, string body)
        {
            // La firma se valida antes de tocar el pedido. El proveedor puede reenviar
            // el mismo evento: UpdateOrderPayment es idempotente por estado.
            string secret = Environment.GetEnvironmentVariable("NEXOMARKET_PAYMENT_WEBHOOK_SECRET") ?? "";
            string signature = HeaderValue(requestText, "X-Nexo-Payment-Signature");
            if (string.IsNullOrWhiteSpace(secret)) return "ERROR|webhook_not_configured";
            using (HMACSHA256 h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                string expected = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(body ?? "")));
                if (!FixedEquals(expected, signature)) return "ERROR|invalid_signature";
            }
            string id = JsonField(body, "centralOrderId");
            if (string.IsNullOrWhiteSpace(id)) id = JsonField(body, "orderId");
            string storeId = JsonField(body, "storeId");
            string status = JsonField(body, "paymentStatus");
            string reference = JsonField(body, "paymentReference");
            if (string.IsNullOrWhiteSpace(status)) status = JsonField(body, "status");
            return UpdateOrderPayment(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                {"storeId",storeId},{"centralOrderId",id},{"paymentStatus",NormalizePaymentStatus(status)},{"paymentReference",reference}
            });
        }

        private static string NormalizePaymentStatus(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v == "approved" || v == "aprobado" || v == "paid" || v == "success") return "Aprobado";
            if (v == "rejected" || v == "rechazado" || v == "failed" || v == "failure") return "Rechazado";
            if (v == "refunded" || v == "reembolsado") return "Reembolsado";
            return "Pendiente";
        }

        private static string JsonField(string json, string field)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(field)) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        private string UpdateOrderPayment(Dictionary<string,string> f)
        {
            string storeId = NormalizeStoreId(Get(f, "storeId"));
            string id = Get(f, "centralOrderId").Trim();
            string paymentStatus = Get(f, "paymentStatus").Trim();
            if (storeId.Length == 0 || id.Length == 0 || paymentStatus.Length == 0) return "ERROR|missing";
            string[] allowed = { "Pendiente", "Aprobado", "Rechazado", "Reembolsado" };
            if (!allowed.Contains(paymentStatus)) return "ERROR|payment_status";
            XElement order = null;
            lock (_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                order = d.Root.Element("Orders").Elements("Order").FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "CentralOrderId") == id);
                if (order == null) return "ERROR|notfound";
                string old = S(order, "PaymentStatus");
                if (old == paymentStatus) return "OK|idempotent|" + paymentStatus;
                order.SetElementValue("PaymentStatus", paymentStatus);
                order.SetElementValue("PaymentReference", Get(f, "paymentReference"));
                order.SetElementValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                if (paymentStatus == "Aprobado" && S(order, "Status") == "Pendiente") order.SetElementValue("Status", "Preparando");
                SaveDoc(_ordersFile, d);
            }
            Audit("payment_" + paymentStatus.ToLowerInvariant(), storeId, S(order, "CustomerEmail"), id, Get(f, "paymentReference"));
            QueueOrderEmails(order, paymentStatus == "Aprobado" ? "pago confirmado" : "pago " + paymentStatus.ToLowerInvariant());
            return "OK|payment|" + paymentStatus;
        }

        private string CancelOrder(Dictionary<string,string> f)
        {
            string storeId = NormalizeStoreId(Get(f, "storeId"));
            string id = Get(f, "centralOrderId").Trim();
            string reason = Get(f, "reason").Trim();
            if (storeId.Length == 0 || id.Length == 0) return "ERROR|missing";
            XElement order;
            lock (_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                order = d.Root.Element("Orders").Elements("Order").FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "CentralOrderId") == id);
                if (order == null) return "ERROR|notfound";
                string status = S(order, "Status");
                if (status == "Entregado" || status == "Cancelado") return "OK|idempotent|" + status;
                order.SetElementValue("Status", "Cancelado");
                order.SetElementValue("CancelReason", reason);
                order.SetElementValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                // El stock reservado/decrementado al crear el pedido vuelve a estar disponible.
                RestoreOrderStockLocked(storeId, S(order, "ItemsJson"));
                string coupon = S(order, "CouponCode");
                if (!string.IsNullOrWhiteSpace(coupon) && S(order, "CouponConsumed") == "1")
                {
                    XDocument cd = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
                    XElement c = cd.Root.Element("Coupons") == null ? null : cd.Root.Element("Coupons").Elements("Coupon").FirstOrDefault(x => S(x, "StoreId") == storeId && string.Equals(S(x, "Code"), coupon, StringComparison.OrdinalIgnoreCase));
                    if (c != null)
                    {
                        int used = (int)Money(S(c, "Used"));
                        c.SetElementValue("Used", Math.Max(0, used - 1).ToString(CultureInfo.InvariantCulture));
                        SaveDoc(_catalogFile, cd);
                    }
                    order.SetElementValue("CouponConsumed", "0");
                }
                SaveDoc(_ordersFile, d);
            }
            Audit("order_cancelled", storeId, S(order, "CustomerEmail"), id, reason);
            QueueOrderEmails(order, "pedido cancelado");
            return "OK|cancelled";
        }

        private string RefundOrder(Dictionary<string,string> f)
        {
            // Reembolso lógico: la devolución monetaria real la confirma el proveedor de pagos.
            string storeId = NormalizeStoreId(Get(f, "storeId"));
            string id = Get(f, "centralOrderId").Trim();
            if (storeId.Length == 0 || id.Length == 0) return "ERROR|missing";
            string paymentRef = Get(f, "paymentReference").Trim();
            XElement order;
            lock (_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                order = d.Root.Element("Orders").Elements("Order").FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "CentralOrderId") == id);
                if (order == null) return "ERROR|notfound";
                if (S(order,"PaymentStatus")=="Reembolsado" && S(order,"RefundApplied")=="1") return "OK|idempotent|refunded";
                order.SetElementValue("PaymentStatus", "Reembolsado");
                order.SetElementValue("RefundReference", paymentRef);
                order.SetElementValue("RefundReason", Get(f, "reason"));
                order.SetElementValue("RefundApplied", "1");
                order.SetElementValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                RestoreOrderStockLocked(storeId, S(order, "ItemsJson"));
                SaveDoc(_ordersFile, d);
            }
            Audit("order_refunded", storeId, S(order, "CustomerEmail"), id, paymentRef);
            QueueOrderEmails(order, "reembolso registrado");
            return "OK|refunded";
        }

        private void RestoreOrderStockLocked(string storeId, string itemsJson)
        {
            Dictionary<string, int> requested = ParseRequestedItems(itemsJson);
            if (requested.Count == 0) return;
            XDocument d = LoadFile(_catalogFile, "NexoMarketCatalog", "Products");
            XElement products = d.Root.Element("Products");
            if (products == null) return;
            foreach (KeyValuePair<string, int> pair in requested)
            {
                XElement p = products.Elements("Product").FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "ProductId") == pair.Key);
                if (p == null) continue;
                int stock; if (!int.TryParse(S(p, "Stock"), out stock)) stock = 0;
                p.SetElementValue("Stock", (stock + pair.Value).ToString(CultureInfo.InvariantCulture));
                p.SetElementValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
            }
            SaveDoc(_catalogFile, d);
        }

        private Dictionary<string, int> ParseRequestedItems(string itemsJson)
        {
            Dictionary<string, int> requested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(itemsJson)) return requested;
            foreach (Match m in Regex.Matches(itemsJson, @"""id""\s*:\s*""([^""]+)""[^}]*?""qty""\s*:\s*(\d+)", RegexOptions.IgnoreCase))
            {
                string id = m.Groups[1].Value;
                if (id.StartsWith("promo:", StringComparison.OrdinalIgnoreCase)) continue;
                int qty;
                if (!int.TryParse(m.Groups[2].Value, out qty) || qty < 1) continue;
                if (requested.ContainsKey(id)) requested[id] += qty; else requested[id] = qty;
            }
            return requested;
        }

        private string OrderReceipt(string id, string email)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(email)) return "<h1>Comprobante no disponible</h1>";
            lock (_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                XElement o = d.Root.Element("Orders").Elements("Order").FirstOrDefault(x => S(x, "CentralOrderId") == id && string.Equals(S(x, "CustomerEmail"), email, StringComparison.OrdinalIgnoreCase));
                return o == null ? "<h1>Comprobante no encontrado</h1>" : BuildReceiptHtml(o, GetStoreName(S(o, "StoreId")));
            }
        }

        private string ForgotPassword(Dictionary<string,string> f)
        {
            string email = Get(f, "email").Trim().ToLowerInvariant();
            if (email.Length < 3) return "OK|if_registered";
            CentralUser u = FindAccount(email);
            // Respuesta uniforme: no revela si la cuenta existe.
            if (u == null) return "OK|if_registered";
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "-").Replace("/", "_").TrimEnd('=');
            string hash = HashToken(token);
            lock (_sync)
            {
                XDocument d = LoadFile(_accountsFile, "NexoMarketAccounts", "Users");
                XElement e = d.Root.Element("Users").Elements("User").FirstOrDefault(x => string.Equals(S(x, "Email"), email, StringComparison.OrdinalIgnoreCase));
                if (e != null)
                {
                    e.SetElementValue("RecoveryCode", hash);
                    e.SetElementValue("RecoveryExpires", DateTime.UtcNow.AddMinutes(30).ToString("o"));
                    SaveDoc(_accountsFile, d);
                }
            }
            _email.Queue(email, "NexoMarket · Recuperación de contraseña",
                "<html><body><h2>Recuperación de contraseña</h2><p>Usá este código temporal para restablecer tu acceso:</p><p><b>" + E(token) + "</b></p><p>Vence en 30 minutos.</p></body></html>",
                "Código de recuperación NexoMarket: " + token + ". Vence en 30 minutos.");
            Audit("password_reset_requested", u.StoreId, email, u.Id, "");
            return "OK|if_registered";
        }

        private string ResetPassword(Dictionary<string,string> f)
        {
            string email = Get(f, "email").Trim().ToLowerInvariant();
            string token = Get(f, "token").Trim();
            string password = Get(f, "password");
            if (email.Length < 3 || token.Length < 10 || password.Length < 8) return "ERROR|invalid_data";
            string stored = "";
            DateTime expires = DateTime.MinValue;
            lock (_sync)
            {
                XDocument d = LoadFile(_accountsFile, "NexoMarketAccounts", "Users");
                XElement e = d.Root.Element("Users").Elements("User").FirstOrDefault(x => string.Equals(S(x, "Email"), email, StringComparison.OrdinalIgnoreCase));
                if (e != null) { stored = S(e, "RecoveryCode"); DateTime.TryParse(S(e, "RecoveryExpires"), out expires); }
            }
            if (string.IsNullOrWhiteSpace(stored) || expires.ToUniversalTime() < DateTime.UtcNow || !FixedEquals(stored, HashToken(token))) return "ERROR|invalid_or_expired";
            byte[] salt = new byte[16]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash; using (var kdf = new Rfc2898DeriveBytes(password, salt, 50000)) hash = kdf.GetBytes(32);
            string salt64 = Convert.ToBase64String(salt), hash64 = Convert.ToBase64String(hash);
            if (_database != null && _database.Enabled && !_database.UpdatePassword(email, salt64, hash64)) return "ERROR|database";
            lock (_sync)
            {
                XDocument d = LoadFile(_accountsFile, "NexoMarketAccounts", "Users");
                XElement e = d.Root.Element("Users").Elements("User").FirstOrDefault(x => string.Equals(S(x, "Email"), email, StringComparison.OrdinalIgnoreCase));
                if (e == null) return "ERROR|notfound";
                e.SetElementValue("Salt", salt64); e.SetElementValue("PasswordHash", hash64);
                e.SetElementValue("RecoveryCode", ""); e.SetElementValue("RecoveryExpires", "");
                SaveDoc(_accountsFile, d);
            }
            Audit("password_reset_completed", "", email, "", "");
            return "OK|password_updated";
        }

        private static string HashToken(string value)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }

        private static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private string CreateOrder(Dictionary<string,string> f)
        {
            lock (_orderCreateGate)
            {
            string storeId=NormalizeStoreId(Get(f,"storeId")); if(string.IsNullOrWhiteSpace(storeId)) return "ERROR|storeId";
            string idempotencyKey=Get(f,"idempotencyKey").Trim();
            string existingResult=GetIdempotentOrder(idempotencyKey);
            if(!string.IsNullOrWhiteSpace(existingResult)) return existingResult;
            lock(_sync)
            {
                XElement store=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==storeId);
                if(store==null) return "ERROR|store";
                ApplyAutomaticStoreSchedule(store);
                if(S(store,"Active")!="1") return "ERROR|store_closed";
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
            string paymentProofPath = Get(f,"paymentProofPath").Trim();
            if (string.IsNullOrWhiteSpace(paymentProofPath)) return "ERROR|payment_proof_required";
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
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement e=new XElement("Order",new XElement("CentralOrderId",centralId),new XElement("StoreId",storeId),new XElement("CustomerId",Get(f,"customerId")),new XElement("CustomerName",Get(f,"customerName")),new XElement("CustomerEmail",Get(f,"customerEmail")),new XElement("Phone",Get(f,"phone")),new XElement("Fulfillment",Get(f,"fulfillment")),new XElement("Address",Get(f,"address")),new XElement("Notes",Get(f,"notes")),new XElement("Status",string.IsNullOrWhiteSpace(Get(f,"status"))?"Pendiente":Get(f,"status")),new XElement("Total",total.ToString(System.Globalization.CultureInfo.InvariantCulture)),new XElement("CouponCode",couponCode),new XElement("CouponDiscount",couponDiscount),new XElement("CouponConsumed",string.IsNullOrWhiteSpace(couponCode)?"0":"1"),new XElement("PaymentMethod",Get(f,"paymentMethod")),new XElement("PaymentStatus",string.IsNullOrWhiteSpace(Get(f,"paymentStatus"))?"Pendiente":Get(f,"paymentStatus")),new XElement("PaymentReference",Get(f,"paymentReference")),new XElement("PaymentProofPath",paymentProofPath),new XElement("ShippingCost",Get(f,"shippingCost")),new XElement("TrackingNumber",Get(f,"trackingNumber")),new XElement("Carrier",Get(f,"carrier")),new XElement("ItemsJson",Get(f,"itemsJson")),new XElement("BuyerMessage",Get(f,"buyerMessage")),new XElement("CreatedAt",now),new XElement("Ack", "0")); d.Root.Element("Orders").Add(e);SaveDoc(_ordersFile,d);}
            string result="OK|"+centralId+"|"+now+"|"+total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SaveIdempotency(idempotencyKey,result);
            Audit("order_created",storeId,Get(f,"customerEmail"),centralId,"total="+total.ToString(CultureInfo.InvariantCulture));
            lock(_sync)
            {
                XDocument od=LoadFile(_ordersFile,"NexoMarketOrders","Orders");
                XElement oe=od.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"CentralOrderId")==centralId);
                if(oe!=null) QueueOrderEmails(oe,"pedido recibido");
            }
            return result;
            }

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

        private string PendingOrders(string storeId, string syncKey)
        {
            if(string.IsNullOrWhiteSpace(storeId) || !ValidateStoreSyncKey(storeId, syncKey)) return "[]";
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");List<XElement> list=d.Root.Element("Orders").Elements("Order").Where(x=>S(x,"StoreId")==storeId&&S(x,"Ack")!="1").ToList();StringBuilder b=new StringBuilder("[");for(int i=0;i<list.Count;i++){if(i>0)b.Append(',');XElement x=list[i];b.Append("{\"centralOrderId\":").Append(JsonString(S(x,"CentralOrderId"))).Append(",\"customerId\":").Append(JsonString(S(x,"CustomerId"))).Append(",\"customerName\":").Append(JsonString(S(x,"CustomerName"))).Append(",\"customerEmail\":").Append(JsonString(S(x,"CustomerEmail"))).Append(",\"phone\":").Append(JsonString(S(x,"Phone"))).Append(",\"fulfillment\":").Append(JsonString(S(x,"Fulfillment"))).Append(",\"address\":").Append(JsonString(S(x,"Address"))).Append(",\"notes\":").Append(JsonString(S(x,"Notes"))).Append(",\"status\":").Append(JsonString(S(x,"Status"))).Append(",\"total\":").Append(JsonString(S(x,"Total"))).Append(",\"paymentMethod\":").Append(JsonString(S(x,"PaymentMethod"))).Append(",\"paymentStatus\":").Append(JsonString(S(x,"PaymentStatus"))).Append(",\"paymentReference\":").Append(JsonString(S(x,"PaymentReference"))).Append(",\"paymentProofPath\":").Append(JsonString(S(x,"PaymentProofPath"))).Append(",\"shippingCost\":").Append(JsonString(S(x,"ShippingCost"))).Append(",\"trackingNumber\":").Append(JsonString(S(x,"TrackingNumber"))).Append(",\"carrier\":").Append(JsonString(S(x,"Carrier"))).Append(",\"itemsJson\":").Append(JsonString(S(x,"ItemsJson"))).Append(",\"buyerMessage\":").Append(JsonString(S(x,"BuyerMessage"))).Append(",\"createdAt\":").Append(JsonString(S(x,"CreatedAt"))).Append('}');}b.Append(']');return b.ToString();}
        }

        private string OrdersSnapshot(string storeId, string syncKey, string since)
        {
            storeId=NormalizeStoreId(storeId??""); if(string.IsNullOrWhiteSpace(storeId)||!ValidateStoreSyncKey(storeId,syncKey)) return "{\"error\":\"sync_key\"}";
            DateTime cursor=ParseUtcDate(since); StringBuilder b=new StringBuilder("["); bool first=true;
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders"); XElement root=d.Root==null?null:d.Root.Element("Orders"); if(root!=null) foreach(XElement e in root.Elements("Order").Where(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase))){DateTime updated=ParseUtcDate(S(e,"UpdatedAt"));if(updated==DateTime.MinValue)updated=ParseUtcDate(S(e,"CreatedAt"));if(cursor!=DateTime.MinValue&&updated<=cursor)continue;if(!first)b.Append(',');first=false;b.Append(OrderJson(e));}} b.Append(']');return b.ToString();
        }
        private string OrderJson(XElement x)
        {
            return "{\"centralOrderId\":"+JsonString(S(x,"CentralOrderId"))+",\"customerId\":"+JsonString(S(x,"CustomerId"))+",\"customerName\":"+JsonString(S(x,"CustomerName"))+",\"customerEmail\":"+JsonString(S(x,"CustomerEmail"))+",\"phone\":"+JsonString(S(x,"Phone"))+",\"fulfillment\":"+JsonString(S(x,"Fulfillment"))+",\"address\":"+JsonString(S(x,"Address"))+",\"notes\":"+JsonString(S(x,"Notes"))+",\"status\":"+JsonString(S(x,"Status"))+",\"total\":"+JsonString(S(x,"Total"))+",\"paymentMethod\":"+JsonString(S(x,"PaymentMethod"))+",\"paymentStatus\":"+JsonString(S(x,"PaymentStatus"))+",\"paymentReference\":"+JsonString(S(x,"PaymentReference"))+",\"paymentProofPath\":"+JsonString(S(x,"PaymentProofPath"))+",\"shippingCost\":"+JsonString(S(x,"ShippingCost"))+",\"trackingNumber\":"+JsonString(S(x,"TrackingNumber"))+",\"carrier\":"+JsonString(S(x,"Carrier"))+",\"itemsJson\":"+JsonString(S(x,"ItemsJson"))+",\"buyerMessage\":"+JsonString(S(x,"BuyerMessage"))+",\"createdAt\":"+JsonString(S(x,"CreatedAt"))+",\"updatedAt\":"+JsonString(S(x,"UpdatedAt"))+"}";
        }

        private string AckOrder(Dictionary<string,string> f)
        {
            string storeId=Get(f,"storeId"), id=Get(f,"centralOrderId"); if(string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(id))return "ERROR|missing";
            if(!ValidateStoreSyncKey(storeId,Get(f,"syncKey")))return "ERROR|sync_key";
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement e=d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==storeId&&S(x,"CentralOrderId")==id);if(e==null)return "ERROR|notfound";e.SetElementValue("Ack","1");e.SetElementValue("AckAt",DateTime.UtcNow.ToString("o"));SaveDoc(_ordersFile,d);}return "OK|ack";
        }

        private string UpdateOrderStatus(Dictionary<string,string> f)
        {
            string storeId = Get(f, "storeId"); string id = Get(f, "centralOrderId"); string status = Get(f, "status");
            if (string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(status)) return "ERROR|missing";
            if(!ValidateStoreSyncKey(storeId,Get(f,"syncKey"))) return "ERROR|sync_key";
            string[] allowed = { "Pendiente", "Preparando", "Listo", "Enviado", "En reparto", "Entregado", "Rechazado", "Cancelado" };
            if (!allowed.Contains(status)) return "ERROR|status";
            XElement changed;
            lock (_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                XElement e = d.Root.Element("Orders").Elements("Order").FirstOrDefault(x => S(x, "StoreId") == storeId && S(x, "CentralOrderId") == id);
                if (e == null) return "ERROR|notfound";
                if (S(e,"Status")==status && S(e,"TrackingNumber")==Get(f,"trackingNumber")) return "OK|idempotent|status";
                e.SetElementValue("Status", status);
                e.SetElementValue("Carrier", Get(f, "carrier"));
                e.SetElementValue("TrackingNumber", Get(f, "trackingNumber"));
                e.SetElementValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                SaveDoc(_ordersFile, d);
                changed=new XElement(e);
            }
            Audit("order_status_"+status.ToLowerInvariant().Replace(" ","_"),storeId,S(changed,"CustomerEmail"),id,S(changed,"TrackingNumber"));
            QueueOrderEmails(changed,"estado actualizado");
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
                return "{\"centralOrderId\":"+JsonString(S(e,"CentralOrderId"))+",\"status\":"+JsonString(S(e,"Status"))+",\"total\":"+JsonString(S(e,"Total"))+",\"buyerConfirmed\":"+JsonString(S(e,"BuyerConfirmedAt"))+",\"approvedAt\":"+JsonString(S(e,"ApprovedAt"))+",\"updatedAt\":"+JsonString(S(e,"UpdatedAt"))+"}";
            }
        }

        private string OrderStatusDelta(string storeId, string syncKey, string since)
        {
            storeId = NormalizeStoreId(storeId ?? "");
            if (string.IsNullOrWhiteSpace(storeId) || !ValidateStoreSyncKey(storeId, syncKey)) return "{\"error\":\"sync_key\"}";
            DateTime cursor = ParseUtcDate(since);
            lock(_sync)
            {
                XDocument d = LoadFile(_ordersFile, "NexoMarketOrders", "Orders");
                XElement orders = d.Root.Element("Orders");
                StringBuilder b = new StringBuilder();
                b.Append("[");
                bool first = true;
                if (orders != null)
                {
                    foreach (XElement e in orders.Elements("Order").Where(x => string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase)))
                    {
                        DateTime updated = ParseUtcDate(S(e,"UpdatedAt"));
                        if (updated == DateTime.MinValue) updated = ParseUtcDate(S(e,"CreatedAt"));
                        if (cursor != DateTime.MinValue && updated <= cursor) continue;
                        if (!first) b.Append(',');
                        first = false;
                        b.Append("{\"centralOrderId\":").Append(JsonString(S(e,"CentralOrderId")))
                         .Append(",\"status\":").Append(JsonString(S(e,"Status")))
                         .Append(",\"trackingNumber\":").Append(JsonString(S(e,"TrackingNumber")))
                         .Append(",\"carrier\":").Append(JsonString(S(e,"Carrier")))
                         .Append(",\"updatedAt\":").Append(JsonString(updated.ToString("o"))).Append('}');
                    }
                }
                b.Append(']');
                return b.ToString();
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
            TouchStoreActivity(storeId);
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
                ApplyAutomaticStoreSchedule(store);
                // Active es el estado operativo de la tienda, no una condición para sincronizar Windows.
                // Una tienda cerrada por horario debe seguir pudiendo sincronizar catálogo y pedidos.
                // Desde 5.8 la SyncKey es derivada de forma determinista del StoreId.
                // Esto repara automáticamente tiendas antiguas que no tenían clave o
                // que conservaron una clave distinta después de una actualización.
                string syncKey = ComputeStorePairKey(storeId);
                if (!string.Equals(S(store, "SyncKey"), syncKey, StringComparison.Ordinal))
                {
                    store.SetElementValue("SyncKey", syncKey);
                    store.SetAttributeValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                    Save();
                }

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
                    "|" + Escape(S(store, "Pickup")) + "|" + Escape(S(store, "Latitude")) + "|" + Escape(S(store, "Longitude")) + "|" + Escape(S(store, "SystemName")) + "|" + Escape(S(store, "Featured")) + "|" + Escape(S(store, "Listed")) + "|" + Escape(S(store, "StorePhoto")) + "|" + Escape(S(store, "AutoSchedule")) + "|" + Escape(S(store, "OpenTime")) + "|" + Escape(S(store, "CloseTime"));
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

        // Super Administrador local: no solicita clave maestra.
        // IMPORTANTE: los endpoints /api/admin/* deben permanecer accesibles solo desde la herramienta Super Admin/controlado por el propietario.
        // Se conserva el parámetro por compatibilidad con las firmas existentes.
        private bool IsAdminKey(string key)
        {
            return true;
        }
        private string AdminDenied(string key){return null;}
        private string AdminStores(string key)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            StringBuilder b=new StringBuilder();
            lock(_sync)
            {
                foreach(XElement e in _doc.Root.Element("Stores").Elements("Store"))
                {
                    CentralUser seller=FindSellerByStore(S(e,"StoreId"));
                    b.Append("STORE|").Append(Escape(S(e,"StoreId"))).Append('|').Append(Escape(S(e,"Name"))).Append('|').Append(Escape(S(e,"Category"))).Append('|').Append(Escape(S(e,"City"))).Append('|').Append(Escape(S(e,"Province"))).Append('|').Append(Escape(S(e,"Active"))).Append('|').Append(Escape(seller==null?"":seller.Email)).Append('|').Append(Escape(S(e,"UpdatedAt"))).Append('|').Append(Escape(S(e,"Logo"))).Append('|').Append(Escape(S(e,"Featured"))).Append('|').Append(Escape(S(e,"Listed"))).Append('|').Append(Escape(S(e,"SystemName"))).Append('|').Append(Escape(S(e,"FeaturedPlus"))).Append('\n');
                }
            }
            return b.ToString();
        }
        private string AdminAccounts(string key)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            StringBuilder b=new StringBuilder();
            if(_database!=null&&_database.Enabled)
            {
                foreach(Dictionary<string,string> a in _database.GetAccountsForAdmin())
                    b.Append("ACCOUNT|").Append(Escape(a.ContainsKey("id")?a["id"]:"")).Append('|').Append(Escape(a.ContainsKey("name")?a["name"]:"")).Append('|').Append(Escape(a.ContainsKey("email")?a["email"]:"")).Append('|').Append(Escape(a.ContainsKey("role")?a["role"]:"")).Append('|').Append(Escape(a.ContainsKey("storeId")?a["storeId"]:"")).Append('|').Append(Escape(a.ContainsKey("active")?a["active"]:"1")).Append('|').Append(Escape(a.ContainsKey("trialExpiresAt")?a["trialExpiresAt"]:"")).Append('|').Append(Escape(a.ContainsKey("createdAt")?a["createdAt"]:"")).Append('\n');
                return b.ToString();
            }
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users"); foreach(XElement e in d.Root.Element("Users").Elements("User")) b.Append("ACCOUNT|").Append(Escape(S(e,"Id"))).Append('|').Append(Escape(S(e,"Name"))).Append('|').Append(Escape(S(e,"Email"))).Append('|').Append(Escape(S(e,"Role"))).Append('|').Append(Escape(S(e,"StoreId"))).Append('|').Append(Escape(S(e,"Active")=="0"?"0":"1")).Append('|').Append(Escape(S(e,"TrialExpiresAt"))).Append('|').Append(Escape(S(e,"CreatedAt"))).Append('\n');}
            return b.ToString();
        }
        private string AdminOverview(string key)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            int stores=0,accounts=0,active=0; lock(_sync){stores=_doc.Root.Element("Stores").Elements("Store").Count(); active=_doc.Root.Element("Stores").Elements("Store").Count(x=>S(x,"Active")!="0");}
            if(_database!=null&&_database.Enabled)accounts=_database.GetAccountsForAdmin().Count; else {lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");accounts=d.Root.Element("Users").Elements("User").Count();}}
            return "OK|stores="+stores.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|activeStores="+active.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|accounts="+accounts.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|database="+(_database!=null&&_database.Enabled?"connected":"disabled");
        }
        private string AdminCreateStore(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            string name=Get(f,"name").Trim(); if(name.Length<2)return "ERROR|name_required";
            string storeId=NormalizeStoreId(Get(f,"storeId")); if(storeId.Length==0)storeId=Guid.NewGuid().ToString("N").ToUpperInvariant();
            string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); string owner=Get(f,"ownerName").Trim();
            if(email.Length>0 && password.Length<6)return "ERROR|password_required";
            if(email.Length>0 && FindAccount(email)!=null)return "ERROR|account_exists";
            string result=ClaimStore(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",storeId},{"name",name},{"category",string.IsNullOrWhiteSpace(Get(f,"category"))?"Comercio":Get(f,"category")},{"description",Get(f,"description")},{"city",Get(f,"city")},{"province",Get(f,"province")},{"active","1"}});
            if(!result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase))return result;
            if(email.Length>0)
            {

                byte[] salt=new byte[16];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt);string salt64=Convert.ToBase64String(salt);byte[] hash;using(var kdf=new Rfc2898DeriveBytes(password,salt,50000))hash=kdf.GetBytes(32);
                Dictionary<string,string> a=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",Guid.NewGuid().ToString("N")},{"name",string.IsNullOrWhiteSpace(owner)?name:owner},{"email",email},{"phone",""},{"role","seller"},{"storeId",storeId},{"salt",salt64},{"passwordHash",Convert.ToBase64String(hash)},{"createdAt",DateTime.UtcNow.ToString("o")}};
                string ar=AccountUpsert(a,false); if(!ar.StartsWith("OK|",StringComparison.OrdinalIgnoreCase))return "ERROR|store_created|account_failed|"+Escape(ar);
                int days; if(int.TryParse(Get(f,"trialDays"),out days)) AdminSetTrial(key,new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"email",email},{"days",Math.Max(0,days).ToString(System.Globalization.CultureInfo.InvariantCulture)}});
            }
            return "OK|"+Escape(storeId)+"|"+Escape(email);
        }
        private string AdminDeleteStore(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied; string storeId=NormalizeStoreId(Get(f,"storeId")); if(storeId.Length==0)return "ERROR|store_id_required";
            bool found=false; lock(_sync){XElement st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));if(st!=null){st.Remove();found=true;Save();}}
            string[] files={_catalogFile,_ordersFile,_accountsFile}; string[] roots={"NexoMarketCatalog","NexoMarketOrders","NexoMarketAccounts"}; string[] childs={"Products","Orders","Users"}; string[] datasets={"catalog","orders","accounts"}; string[] r2keys={"data/nexomarket_catalog.xml","data/nexomarket_orders.xml","data/nexomarket_accounts.xml"};
            for(int i=0;i<files.Length;i++){try{lock(_sync){XDocument d=LoadFile(files[i],roots[i],childs[i]);foreach(XElement e in d.Root.Element(childs[i]).Elements().Where(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase)).ToList())e.Remove();SaveDoc(files[i],d);}}catch{}}
            try{if(_database!=null&&_database.Enabled)_database.DeleteStoreLinks(storeId);}catch{}
            try{if(_r2!=null&&_r2.Enabled){_r2.DeletePrefix("stores/"+Regex.Replace(storeId,"[^a-zA-Z0-9_-]","_")+"/");}}catch{}
            return found?"OK|deleted|"+Escape(storeId):"OK|already_absent|"+Escape(storeId);
        }
        private string AdminSetStoreActive(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied; string id=NormalizeStoreId(Get(f,"storeId")); bool active=Get(f,"active")!="0"; lock(_sync){XElement e=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),id,StringComparison.OrdinalIgnoreCase));if(e==null)return "ERROR|store_not_found";e.SetElementValue("Active",active?"1":"0");e.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));Save();} return "OK|"+(active?"active":"inactive");
        }
        private string AdminSetStoreFeatured(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            string id=NormalizeStoreId(Get(f,"storeId")); bool featured=Get(f,"featured")!="0";
            lock(_sync){XElement e=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),id,StringComparison.OrdinalIgnoreCase));if(e==null)return "ERROR|store_not_found";e.SetElementValue("Featured",featured?"1":"0");if(!featured)e.SetElementValue("FeaturedPlus","0");e.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));Save();}
            return "OK|"+(featured?"featured":"unfeatured");
        }
        private string AdminSetStoreFeaturedPlus(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            string id=NormalizeStoreId(Get(f,"storeId")); bool plus=Get(f,"featuredPlus")!="0";
            lock(_sync)
            {
                XElement e=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),id,StringComparison.OrdinalIgnoreCase));
                if(e==null)return "ERROR|store_not_found";
                e.SetElementValue("FeaturedPlus",plus?"1":"0");
                if(plus)e.SetElementValue("Featured","1");
                e.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                Save();
            }
            return "OK|"+(plus?"featured_plus":"unfeatured_plus");
        }
        private string AdminSetStoreListed(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied;
            string id=NormalizeStoreId(Get(f,"storeId")); bool listed=Get(f,"listed")!="0";
            lock(_sync){XElement e=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),id,StringComparison.OrdinalIgnoreCase));if(e==null)return "ERROR|store_not_found";e.SetElementValue("Listed",listed?"1":"0");if(!listed){e.SetElementValue("Featured","0");e.SetElementValue("FeaturedPlus","0");}e.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));Save();}
            return "OK|"+(listed?"listed":"unlisted");
        }
        private string AdminSetTrial(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied; string email=Get(f,"email").Trim().ToLowerInvariant(); int days; if(!int.TryParse(Get(f,"days"),out days)||days<0)return "ERROR|invalid_days";
            if(_database!=null&&_database.Enabled)return _database.SetAccountTrial(email,days)?"OK|trial_set":"ERROR|account_not_found";
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase));if(e==null)return "ERROR|account_not_found";e.SetElementValue("Active","1");e.SetElementValue("TrialExpiresAt",DateTime.UtcNow.AddDays(days).ToString("o"));SaveDoc(_accountsFile,d);return "OK|trial_set";}
        }
        private string AdminSetAccountActive(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied; string email=Get(f,"email").Trim().ToLowerInvariant(); bool active=Get(f,"active")!="0";
            if(_database!=null&&_database.Enabled)return _database.SetAccountActive(email,active)?"OK|updated":"ERROR|account_not_found";
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase));if(e==null)return "ERROR|account_not_found";e.SetElementValue("Active",active?"1":"0");SaveDoc(_accountsFile,d);return "OK|updated";}
        }
        private string AdminDeleteAccount(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied; string email=Get(f,"email").Trim().ToLowerInvariant(); if(email.Length==0)return "ERROR|email_required"; CentralUser u=FindAccount(email); if(u==null)return "ERROR|account_not_found";
            if(!string.IsNullOrWhiteSpace(u.StoreId))return AdminDeleteStore(key,new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",u.StoreId}});
            if(_database!=null&&_database.Enabled)return _database.DeleteAccount(email)?"OK|deleted":"ERROR|account_not_found";
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");foreach(XElement e in d.Root.Element("Users").Elements("User").Where(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase)).ToList())e.Remove();SaveDoc(_accountsFile,d);return "OK|deleted";}
        }
        private string AdminFactoryReset(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key); if(denied!=null)return denied; if(Get(f,"confirm")!="NEXO-FACTORY-RESET")return "ERROR|confirmation_required";
            lock(_sync){_doc=new XDocument(new XElement("NexoMarketStores",new XElement("Stores")));Save();string[] specs={_catalogFile,_ordersFile,_accountsFile};string[] roots={"NexoMarketCatalog","NexoMarketOrders","NexoMarketAccounts"};string[] childs={"Products","Orders","Users"};for(int i=0;i<specs.Length;i++)SaveDoc(specs[i],new XDocument(new XElement(roots[i],new XElement(childs[i]))));}
            try{if(_database!=null&&_database.Enabled){_database.FactoryResetAll();}}catch{}
            try{if(_r2!=null&&_r2.Enabled){_r2.DeletePrefix("stores/");_r2.PutText("data/nexomarket_stores.xml",_doc.ToString(SaveOptions.None));}}catch{}
            return "OK|factory_reset";
        }

        private void ApplyAutomaticSchedulesAllStores()
        {
            lock (_sync)
            {
                XElement stores = _doc.Root == null ? null : _doc.Root.Element("Stores");
                if (stores == null) return;
                foreach (XElement store in stores.Elements("Store").ToList()) ApplyAutomaticStoreSchedule(store);
            }
        }

        private void ApplyAutomaticStoreSchedule(XElement store)
        {
            if (store == null || S(store,"AutoSchedule") == "0") return;
            string open=S(store,"OpenTime"), close=S(store,"CloseTime");
            TimeSpan ot, ct; if(!TimeSpan.TryParse(open,out ot)||!TimeSpan.TryParse(close,out ct)) return;
            DateTime nowUtc=DateTime.UtcNow; TimeZoneInfo tz;
            try { tz=TimeZoneInfo.FindSystemTimeZoneById("Argentina Standard Time"); } catch { try { tz=TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires"); } catch { tz=TimeZoneInfo.Utc; } }
            DateTime local=TimeZoneInfo.ConvertTimeFromUtc(nowUtc,tz); TimeSpan t=local.TimeOfDay;
            bool openNow=ct>ot ? (t>=ot && t<ct) : (t>=ot || t<ct);
            string desired=openNow?"1":"0";
            if(S(store,"Active")!=desired){store.SetElementValue("Active",desired);store.SetElementValue("LastActivityAt",nowUtc.ToString("o"));store.SetAttributeValue("UpdatedAt",nowUtc.ToString("o"));Save();}
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
                    // Listed controla la publicación en el directorio central. Active controla si acepta pedidos.
                    ApplyAutomaticStoreSchedule(e);
                    if (S(e,"Listed") == "0") continue;
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
            if (hasCoords) list = list.OrderByDescending(x => S(x.Element, "FeaturedPlus") == "1").ThenByDescending(x => S(x.Element, "Featured") == "1").ThenByDescending(x => S(x.Element, "Active") != "0").ThenBy(x => x.DistanceKm).ThenBy(x => S(x.Element, "Name")).ToList();
            else list = list.OrderByDescending(x => S(x.Element, "FeaturedPlus") == "1").ThenByDescending(x => S(x.Element, "Featured") == "1").ThenByDescending(x => S(x.Element, "Active") != "0").ThenBy(x => S(x.Element, "City")).ThenBy(x => S(x.Element, "Name")).ToList();
            StringBuilder b = new StringBuilder();
            foreach (StoreDistance x in list)
            {
                XElement e = x.Element;
                string publicUrl = S(e, "PublicUrl");
                if (string.IsNullOrWhiteSpace(publicUrl)) publicUrl = "/store/" + Uri.EscapeDataString(S(e, "StoreId"));
                else if (!publicUrl.Contains("/store/", StringComparison.OrdinalIgnoreCase)) publicUrl = publicUrl.TrimEnd('/') + "/store/" + Uri.EscapeDataString(S(e, "StoreId"));
                b.Append("STORE|").Append(Escape(S(e, "StoreId"))).Append('|').Append(Escape(S(e, "Name"))).Append('|').Append(Escape(publicUrl)).Append('|').Append(Escape(S(e, "City"))).Append('|').Append(Escape(S(e, "Province"))).Append('|').Append(Escape(S(e, "Category"))).Append('|').Append(Escape(S(e, "Latitude"))).Append('|').Append(Escape(S(e, "Longitude"))).Append('|').Append(Escape(S(e, "Active"))).Append('|').Append(Escape(S(e, "Delivery"))).Append('|').Append(Escape(S(e, "Pickup"))).Append('|').Append(Escape(e.Attribute("UpdatedAt") == null ? "" : e.Attribute("UpdatedAt").Value)).Append('|').Append(Escape(x.DistanceKm >= 999999d ? "" : x.DistanceKm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))).Append('|').Append(Escape(S(e, "Logo"))).Append('|').Append(Escape(S(e, "Featured"))).Append('|').Append(Escape(S(e, "StorePhoto"))).Append('|').Append(Escape(S(e, "Address"))).Append('|').Append(Escape(S(e, "Description"))).Append('|').Append(Escape(StoreRatingSummary(S(e,"StoreId")))).Append('|').Append(Escape(S(e,"FeaturedPlus"))).Append('\n');
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

        private string ReviewsJson(string storeId)
        {
            storeId=NormalizeStoreId(storeId);
            if(storeId.Length==0)return "{\"ok\":false,\"reviews\":[]}";
            List<XElement> list=new List<XElement>();
            lock(_sync)
            {
                XDocument d=LoadFile(_reviewsFile,"NexoMarketReviews","Reviews");
                XElement root=d.Root==null?null:d.Root.Element("Reviews");
                if(root!=null)list=root.Elements("Review").Where(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase)&&S(x,"Active")!="0").OrderByDescending(x=>S(x,"CreatedAt")).ToList();
            }
            decimal sum=0m;foreach(XElement r in list)sum+=Money(S(r,"Rating"));
            decimal avg=list.Count==0?0m:Math.Round(sum/list.Count,1,MidpointRounding.AwayFromZero);
            StringBuilder b=new StringBuilder("{\"ok\":true,\"average\":").Append(avg.ToString("0.0",CultureInfo.InvariantCulture)).Append(",\"count\":").Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(",\"reviews\":[");
            for(int i=0;i<list.Count;i++){if(i>0)b.Append(',');XElement r=list[i];b.Append("{\"rating\":").Append(Money(S(r,"Rating")).ToString("0",CultureInfo.InvariantCulture)).Append(",\"comment\":").Append(JsonString(S(r,"Comment"))).Append(",\"emoji\":").Append(JsonString(S(r,"Emoji"))).Append(",\"author\":").Append(JsonString(S(r,"AuthorName"))).Append(",\"createdAt\":").Append(JsonString(S(r,"CreatedAt"))).Append('}');}
            return b.Append("]}").ToString();
        }

        private string SaveReview(string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null)return "{\"ok\":false,\"error\":\"login_required\",\"message\":\"Iniciá sesión para dejar una reseña.\"}";
            if(u.Role!="buyer")return "{\"ok\":false,\"error\":\"buyer_only\",\"message\":\"Las reseñas están disponibles para cuentas de comprador.\"}";
            string storeId=NormalizeStoreId(Get(f,"storeId"));int rating;
            if(storeId.Length==0||!int.TryParse(Get(f,"rating"),out rating)||rating<1||rating>5)return "{\"ok\":false,\"error\":\"rating\",\"message\":\"La puntuación debe ser de 1 a 5 estrellas.\"}";
            string comment=(Get(f,"comment")??"").Trim();if(comment.Length>600)comment=comment.Substring(0,600);
            string emoji=(Get(f,"emoji")??"").Trim();if(emoji.Length>32)emoji=emoji.Substring(0,32);
            lock(_sync)
            {
                XElement store=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));
                if(store==null)return "{\"ok\":false,\"error\":\"store\",\"message\":\"La tienda no existe.\"}";
                XDocument d=LoadFile(_reviewsFile,"NexoMarketReviews","Reviews");XElement root=d.Root.Element("Reviews");
                XElement existing=root.Elements("Review").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase)&&string.Equals(S(x,"UserId"),u.Id,StringComparison.OrdinalIgnoreCase));
                string now=DateTime.UtcNow.ToString("o");
                XElement e=new XElement("Review",new XElement("ReviewId",existing==null?Guid.NewGuid().ToString("N"):S(existing,"ReviewId")),new XElement("StoreId",storeId),new XElement("UserId",u.Id),new XElement("AuthorName",u.Name),new XElement("AuthorEmail",u.Email),new XElement("Rating",rating.ToString(CultureInfo.InvariantCulture)),new XElement("Comment",comment),new XElement("Emoji",emoji),new XElement("Active","1"),new XElement("CreatedAt",existing==null?now:S(existing,"CreatedAt")),new XElement("UpdatedAt",now));
                if(existing!=null)existing.ReplaceWith(e);else root.Add(e);SaveDoc(_reviewsFile,d);
            }
            Audit("review_saved",storeId,u.Email,u.Id,"rating="+rating.ToString(CultureInfo.InvariantCulture));
            return "{\"ok\":true,\"message\":\"Reseña guardada correctamente.\"}";
        }

        private string Storefront(string slug)
        {
            string storeId = Uri.UnescapeDataString(slug ?? "").Trim('/');
            XElement store = null;
            lock (_sync) { store = _doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x => string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase) || string.Equals(S(x,"Slug"),storeId,StringComparison.OrdinalIgnoreCase)); if(store!=null) ApplyAutomaticStoreSchedule(store); }
            if(store==null) return "<!doctype html><html><body style='font-family:Arial;background:#080b10;color:#fff;padding:40px'><h1>Tienda no disponible</h1><a href='/' style='color:#39ff66'>Volver a NexoMarket</a></body></html>";
            string realId=S(store,"StoreId");
            bool featuredPlus=S(store,"FeaturedPlus")=="1", featured=S(store,"Featured")=="1";
            string publicTier=featuredPlus?"<div class='public-tier plus'>✦ DESTACADA PLUS</div>":(featured?"<div class='public-tier featured'>★ TIENDA DESTACADA</div>":"");
            StringBuilder b=new StringBuilder();
            string reviewCardHtml="<section class=\"review-card\" id=\"storeReviews\"><div class=\"review-summary\"><div><div class=\"review-label\">RESEÑAS DE LA TIENDA</div><div class=\"review-score\"><span id=\"reviewAverage\">0.0</span> <span class=\"gold-stars\" id=\"reviewStars\">☆☆☆☆☆</span></div><div class=\"review-count\" id=\"reviewCount\">Todavía no hay reseñas.</div></div><button class=\"btn review-open\" type=\"button\" onclick=\"toggleReviewForm()\">⭐ DEJAR RESEÑA</button></div><div id=\"reviewForm\" class=\"review-form\" style=\"display:none\"><div class=\"review-label\">TU PUNTUACIÓN</div><div class=\"star-picker\" id=\"starPicker\"><button type=\"button\" onclick=\"pickRating(1)\">★</button><button type=\"button\" onclick=\"pickRating(2)\">★</button><button type=\"button\" onclick=\"pickRating(3)\">★</button><button type=\"button\" onclick=\"pickRating(4)\">★</button><button type=\"button\" onclick=\"pickRating(5)\">★</button></div><input type=\"hidden\" id=\"reviewRating\" value=\"0\"/><div class=\"emoji-row\"><button type=\"button\" onclick=\"addReviewEmoji(this.getAttribute(\'data-e\'))\" data-e=\"😍\">😍</button><button type=\"button\" onclick=\"addReviewEmoji(this.getAttribute(\'data-e\'))\" data-e=\"👍\">👍</button><button type=\"button\" onclick=\"addReviewEmoji(this.getAttribute(\'data-e\'))\" data-e=\"🔥\">🔥</button><button type=\"button\" onclick=\"addReviewEmoji(this.getAttribute(\'data-e\'))\" data-e=\"❤️\">❤️</button><button type=\"button\" onclick=\"addReviewEmoji(this.getAttribute(\'data-e\'))\" data-e=\"😋\">😋</button></div><input id=\"reviewEmoji\" placeholder=\"Emoticón opcional\"/><textarea id=\"reviewComment\" maxlength=\"600\" placeholder=\"Contale a otros qué te pareció la tienda...\"></textarea><button class=\"btn violet\" type=\"button\" onclick=\"saveReview()\">PUBLICAR RESEÑA</button><div id=\"reviewMessage\" class=\"review-message\"></div></div><div id=\"reviewList\" class=\"review-list\"></div></section>";
            string logo=string.IsNullOrWhiteSpace(S(store,"Logo"))?"<div class='store-avatar'>N</div>":"<img class='store-avatar-img' src='"+E(S(store,"Logo"))+"' alt='"+E(S(store,"Name"))+"'/>";
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta http-equiv='Cache-Control' content='no-store'><title>").Append(E(S(store,"Name"))).Append(" · NexoMarket</title><style>body{font-family:'Segoe UI',Arial;background:#000;color:#fff;margin:0}.wrap{max-width:1240px;margin:auto;padding:20px 20px 100px}.top{display:flex;justify-content:space-between;align-items:center;margin-bottom:16px}.brand{font-weight:900;font-size:24px}.brand span{color:#39ff66}.nav a{color:#dbe6f0;text-decoration:none;margin-left:14px}.hero{display:flex;gap:18px;align-items:center;background:linear-gradient(135deg,#101923,#15102a);border:1px solid #2f3e55;border-radius:24px;padding:24px;box-shadow:0 16px 40px rgba(84,42,150,.16)}.store-avatar{width:82px;height:82px;border-radius:20px;background:#0b1118;border:1px solid #9b5cff;color:#c59cff;display:flex;align-items:center;justify-content:center;font-size:34px;font-weight:900}.store-avatar-img{width:82px;height:82px;border-radius:20px;object-fit:cover;border:1px solid #9b5cff}.store-cover-photo{width:100%;height:220px;border-radius:18px;object-fit:cover;border:1px solid rgba(57,255,102,.55);display:block;box-shadow:0 0 26px rgba(57,255,102,.10);filter:saturate(.9) brightness(.72)}.store-cover-shell{position:relative;margin-bottom:16px;border-radius:20px;overflow:hidden;border:1px solid rgba(255,255,255,.10);background:#080c12;box-shadow:0 18px 45px rgba(0,0,0,.32)}.store-cover-shell:after{content:'';position:absolute;inset:0;background:linear-gradient(180deg,rgba(3,7,12,.05),rgba(3,7,12,.78)),linear-gradient(90deg,rgba(57,255,102,.04),rgba(167,103,255,.10),transparent)}.store-cover-caption{position:absolute;left:18px;bottom:14px;z-index:2;color:#fff;font-size:11px;letter-spacing:1px;font-weight:900;text-shadow:0 0 10px rgba(255,255,255,.35)}.public-tier{display:inline-flex;padding:7px 12px;border-radius:999px;font-size:10px;font-weight:950;letter-spacing:.8px;margin-bottom:7px}.public-tier.featured{color:#7cff9a;border:1px solid #39ff66;box-shadow:0 0 14px rgba(57,255,102,.18);background:rgba(7,30,18,.7)}.public-tier.plus{color:#e1c7ff;border:1px solid #39ff66;background:linear-gradient(90deg,rgba(113,45,180,.45),rgba(26,17,45,.85));box-shadow:0 0 18px rgba(167,103,255,.55),0 0 28px rgba(57,255,102,.18)}.hero h1{margin:0 0 6px;font-size:31px}.muted{color:#91a3b5}.grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px;margin-top:18px}.card{background:#0f1721;border:1px solid #28394e;border-radius:18px;padding:16px;min-height:220px;position:relative}.product-img{width:100%;height:170px;object-fit:cover;border-radius:13px;background:#0a1119;margin-bottom:12px}.price{font-size:21px;font-weight:900;margin-top:9px}.sale{color:#39ff66}.btn{background:#39ff66;color:#061009;border:0;border-radius:10px;padding:10px 14px;font-weight:900;cursor:pointer}.btn.violet{background:#fff;color:#000;box-shadow:0 0 18px rgba(255,255,255,.22)}.empty{padding:22px;color:#8b99a9;border:1px dashed #3c4b60;border-radius:16px}.promos{margin-top:25px}.promo-card{border-color:#7b4bd1}.cart-fab{position:fixed;right:22px;bottom:22px;z-index:30;background:#9b5cff;color:#fff;border:0;border-radius:999px;padding:14px 20px;font-weight:900;box-shadow:0 10px 30px rgba(0,0,0,.35);cursor:pointer}.cart-panel{position:fixed;right:20px;bottom:78px;width:min(430px,calc(100vw - 40px));max-height:78vh;overflow:auto;z-index:29;background:#0c141e;border:1px solid #7650bd;border-radius:20px;padding:18px;box-shadow:0 18px 50px rgba(0,0,0,.5);display:none}.cart-panel.open{display:block}.cart input,.cart select{background:#0d141d;color:#fff;border:1px solid #2a3b51;border-radius:9px;padding:10px;margin:4px;width:calc(100% - 18px)}.item{display:flex;justify-content:space-between;gap:10px;border-bottom:1px solid #223143;padding:8px 0}.item>div{min-width:0;flex:1}.item-note{display:block;width:100%;box-sizing:border-box;background:#090d13;color:#e8edf2;border:1px solid rgba(167,103,255,.28);border-radius:8px;padding:7px 8px;margin-top:7px;font-size:11px}.item-order-note{color:#ffd84d!important}.live{color:#8dffac;font-size:11px;font-weight:900}.section{margin-top:24px}@media(max-width:950px){.grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.wrap{padding:10px 8px 100px}.top,.hero{align-items:flex-start;flex-direction:column}.grid{grid-template-columns:repeat(3,minmax(0,1fr));gap:9px}.card{padding:5px;min-height:0;border-radius:8px}.product-img{height:118px;border-radius:9px;margin-bottom:6px}.card h3{font-size:12px;line-height:1.2;margin:5px 0}.card .muted{font-size:9px;line-height:1.2;max-height:34px;overflow:hidden}.price{font-size:14px;margin-top:5px}.card .btn{font-size:10px;padding:7px 4px;border-radius:7px;width:100%}.nav a{margin-left:0;margin-right:12px}}body{position:relative;overflow-x:hidden}body:before{content:'';position:fixed;inset:-15%;pointer-events:none;z-index:0;background:radial-gradient(ellipse at 8% 18%,transparent 0 25%,rgba(57,255,102,.045) 25.1%,transparent 25.45%),radial-gradient(ellipse at 92% 72%,transparent 0 22%,rgba(167,103,255,.055) 22.1%,transparent 22.45%),radial-gradient(circle at 50% 5%,rgba(255,255,255,.03),transparent 26%);transform:rotate(-7deg)}body:after{content:'';position:fixed;width:80vw;height:45vh;left:-28vw;bottom:8vh;border:1px solid rgba(255,255,255,.07);border-right-color:rgba(57,255,102,.10);border-radius:50%;transform:rotate(-14deg);pointer-events:none;z-index:0;box-shadow:0 0 70px rgba(57,255,102,.025)}.wrap{position:relative;z-index:1}.scroll-home{position:fixed;left:14px;top:12px;z-index:9999;display:none;border:1px solid rgba(255,255,255,.14);background:rgba(5,7,10,.86);backdrop-filter:blur(10px);color:#fff;border-radius:999px;padding:6px 10px;font-size:10px;font-weight:950;letter-spacing:.6px;box-shadow:0 0 16px rgba(57,255,102,.08);cursor:pointer}.scroll-home span{color:#39ff66}.scroll-home.show{display:block}.scroll-home:hover{border-color:#39ff66;box-shadow:0 0 18px rgba(57,255,102,.18)}.card{background:linear-gradient(145deg,rgba(7,9,12,.96),rgba(12,15,20,.92));border-color:rgba(255,255,255,.09);box-shadow:0 12px 35px rgba(0,0,0,.30),inset 0 1px 0 rgba(255,255,255,.025);transition:transform .2s ease,border-color .2s ease,box-shadow .2s ease}.store-card-cover{position:absolute;inset:0;z-index:0;overflow:hidden;border-radius:18px}.store-card-cover img{width:100%;height:100%;object-fit:cover;display:block;filter:saturate(.82) brightness(.62)}.store-card-cover:after{content:'';position:absolute;inset:0;background:linear-gradient(90deg,rgba(3,7,12,.94) 0%,rgba(5,10,17,.84) 43%,rgba(5,10,17,.62) 100%),linear-gradient(180deg,rgba(3,7,12,.16),rgba(3,7,12,.88))}.store-card-content{position:relative;z-index:1}.notify-setup{display:flex;align-items:center;justify-content:space-between;gap:12px;margin:0 0 14px;padding:10px 13px;border:1px solid rgba(255,255,255,.12);border-radius:12px;background:rgba(8,12,18,.78);color:#bfcbd6;font-size:11px;box-shadow:0 0 18px rgba(167,103,255,.06)}.notify-setup b{color:#fff}.notify-setup .btn{padding:7px 10px;font-size:10px;white-space:nowrap}.notify-setup.ok{border-color:rgba(57,255,102,.28)}.notify-setup.warn{border-color:rgba(255,216,77,.30)}.sound-unlock{position:fixed;right:14px;top:12px;z-index:99998;display:none;border:1px solid rgba(57,255,102,.38);background:rgba(4,10,8,.92);color:#dfffe8;border-radius:999px;padding:7px 11px;font-size:10px;font-weight:900;box-shadow:0 0 18px rgba(57,255,102,.12);cursor:pointer}.sound-unlock.show{display:block}.card:hover{transform:translateY(-4px);border-color:rgba(255,255,255,.35);box-shadow:0 0 22px rgba(255,255,255,.08),0 14px 35px rgba(0,0,0,.4)}.btn:hover{background:#fff!important;color:#000!important;box-shadow:0 0 14px rgba(255,255,255,.55),0 0 28px rgba(255,255,255,.12)!important}.price{text-shadow:0 0 10px rgba(255,255,255,.10)}body{position:relative;overflow-x:hidden}body:before{content:'';position:fixed;inset:-18%;pointer-events:none;z-index:0;background:radial-gradient(ellipse at 7% 20%,transparent 0 24%,rgba(57,255,102,.055) 24.1%,transparent 24.5%),radial-gradient(ellipse at 93% 70%,transparent 0 23%,rgba(167,103,255,.06) 23.1%,transparent 23.5%),radial-gradient(circle at 50% 10%,rgba(255,255,255,.035),transparent 26%);transform:rotate(-7deg)}body:after{content:'';position:fixed;width:85vw;height:42vh;right:-30vw;bottom:8vh;border:1px solid rgba(255,255,255,.06);border-left-color:rgba(167,103,255,.10);border-radius:50%;transform:rotate(-16deg);pointer-events:none;z-index:0;box-shadow:0 0 80px rgba(167,103,255,.025)}.wrap{position:relative;z-index:1}.scroll-home{position:fixed;left:14px;top:12px;z-index:9999;display:none;border:1px solid rgba(255,255,255,.14);background:rgba(5,7,10,.86);backdrop-filter:blur(10px);color:#fff;border-radius:999px;padding:6px 10px;font-size:10px;font-weight:950;letter-spacing:.6px;box-shadow:0 0 16px rgba(57,255,102,.08);cursor:pointer}.scroll-home span{color:#39ff66}.scroll-home.show{display:block}.scroll-home:hover{border-color:#39ff66;box-shadow:0 0 18px rgba(57,255,102,.18)}.hero,.panel,.card{background:linear-gradient(145deg,rgba(7,9,12,.95),rgba(12,15,20,.91));border-color:rgba(255,255,255,.09);box-shadow:0 14px 38px rgba(0,0,0,.28),inset 0 1px 0 rgba(255,255,255,.025)}.card:hover{border-color:rgba(255,255,255,.32);box-shadow:0 0 22px rgba(255,255,255,.07),0 14px 32px rgba(0,0,0,.36);transform:translateY(-4px)}.btn:hover{background:#fff!important;color:#000!important;box-shadow:0 0 14px rgba(255,255,255,.55),0 0 30px rgba(255,255,255,.12)!important}.closed-store-banner{margin:0 0 16px;padding:13px 15px;border:1px solid rgba(255,59,95,.45);border-radius:13px;background:rgba(255,59,95,.06);color:#ff8ca0;font-weight:800;box-shadow:0 0 20px rgba(255,59,95,.05)}.top,.section-head{position:relative}.top:after{content:'';position:absolute;left:0;right:0;bottom:4px;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.16),rgba(57,255,102,.18),rgba(167,103,255,.18),transparent)}.proof-actions{display:flex;gap:8px;flex-wrap:wrap;margin:10px 0}.proof-actions .btn{flex:1;min-width:140px}.proof-preview img{max-width:100%;max-height:220px;border-radius:12px;margin-top:8px;object-fit:contain}.approved-order-alert{position:fixed;left:50%;top:18px;transform:translateX(-50%);z-index:99999;display:none;min-width:min(650px,calc(100vw - 28px));padding:18px 24px;border:2px solid #39ff66;border-radius:16px;background:linear-gradient(135deg,#073a18,#0b7b31 55%,#063215);color:#fff;text-align:center;font-weight:900;box-shadow:0 0 24px rgba(57,255,102,.48),0 16px 45px rgba(0,0,0,.55)}.approved-order-alert.show{display:block;animation:approvedPulse .55s ease-in-out 2}.approved-order-alert .ao-title{font-size:20px;letter-spacing:1px}.approved-order-alert .ao-sub{font-size:12px;margin-top:5px;color:#e8fff0}@keyframes approvedPulse{0%,100%{transform:translateX(-50%) scale(1)}50%{transform:translateX(-50%) scale(1.025)}}.review-card{margin-top:18px;background:linear-gradient(145deg,#17120a,#0d0f12);border:1px solid rgba(212,175,55,.55);border-radius:20px;padding:18px;box-shadow:0 0 24px rgba(212,175,55,.08),inset 0 1px 0 rgba(255,255,255,.04)}.review-summary{display:flex;justify-content:space-between;gap:15px;align-items:center}.review-label{font-size:11px;letter-spacing:1.6px;color:#d7bd62;font-weight:900}.review-score{font-size:26px;font-weight:900;margin-top:5px}.gold-stars{color:#ffd84d;letter-spacing:2px;text-shadow:0 0 10px rgba(255,216,77,.22)}.review-count{color:#9e9580;font-size:12px;margin-top:4px}.review-form{margin-top:16px;padding-top:16px;border-top:1px solid rgba(212,175,55,.18)}.star-picker{display:flex;gap:4px;margin:7px 0 12px}.star-picker button{background:none;border:0;color:#6b6250;font-size:32px;cursor:pointer;padding:0 3px}.star-picker button.selected{color:#ffd84d;text-shadow:0 0 12px rgba(255,216,77,.35)}.emoji-row{display:flex;gap:6px;margin-bottom:7px}.emoji-row button{border:1px solid #3b3428;background:#15120e;border-radius:9px;padding:7px 9px;font-size:18px;cursor:pointer}.review-form input,.review-form textarea{width:100%;box-sizing:border-box;background:#08090b;color:#fff;border:1px solid #3b3428;border-radius:10px;padding:10px;margin:5px 0}.review-form textarea{min-height:90px;resize:vertical}.review-message{font-size:12px;margin-top:8px}.review-list{display:grid;gap:8px;margin-top:15px}.review-item{background:rgba(255,255,255,.025);border:1px solid rgba(212,175,55,.16);border-radius:12px;padding:11px}.review-item .stars{color:#ffd84d;letter-spacing:1px}.review-item small{color:#827c6e}.review-open{white-space:nowrap}@media(max-width:650px){.review-summary{align-items:flex-start;flex-direction:column}.review-open{width:100%}.review-score{font-size:23px}}h1,h2,h3{color:#dce2ea;text-shadow:0 0 8px rgba(255,255,255,.12),0 0 16px rgba(167,103,255,.08)}.nav a{color:#bfc8d2;text-shadow:0 0 7px rgba(255,255,255,.10)}.muted{color:#aab5c0}.hero{box-shadow:0 18px 55px rgba(0,0,0,.36),0 0 28px rgba(167,103,255,.06),inset 0 1px 0 rgba(255,255,255,.06)}@media(max-width:650px){.store-cover-photo{height:170px}.store-cover-shell{border-radius:16px}.hero h1{font-size:25px}}</style></head><body><div class='wrap'><div class='top'><div class='brand'><span>NEXO</span>MARKET</div><div class='nav'><a href='/'>Tiendas</a><a href='/seller-login'>Ingresar como vendedor</a></div></div><button id='scrollHome' class='scroll-home' type='button' onclick=\"location.href='/'\">NEXO<span>MARKET</span></button>"+(string.IsNullOrWhiteSpace(S(store,"StorePhoto"))?"":"<div class='store-cover-shell'><img class='store-cover-photo' src='"+E(S(store,"StorePhoto"))+"' alt='Foto del local' loading='eager'/><div class='store-cover-caption'>"+E(S(store,"Name"))+"</div></div>")+"<section class='hero'>").Append(logo).Append("<div>").Append(publicTier).Append("<div class='live'>● TIENDA CENTRAL EN TIEMPO REAL</div><h1>").Append(E(S(store,"Name"))).Append("</h1><div class='muted'>").Append(E(S(store,"Category"))).Append(" · ").Append(E(S(store,"City"))).Append("</div><p class='muted'>").Append(E(S(store,"Description"))).Append("</p></div></section>"+(S(store,"Active")=="0"?"<div class='closed-store-banner'>🔴 TIENDA CERRADA · En este momento no se aceptan nuevos pedidos.</div>":"")+reviewCardHtml+"<div class='section'><div style='display:flex;justify-content:space-between;align-items:end'><div><h2>Productos</h2><div class='muted'>El catálogo se actualiza automáticamente desde NexoMarket Central.</div></div><span id='liveState' class='live'>● LIVE</span></div><div id='products' class='grid'>");
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
                    string image = string.IsNullOrWhiteSpace(img) ? "" : "<img class='product-img' src='" + E(img) + "' alt='" + E(S(x, "Name")) + "' loading='eager' decoding='async' onerror=\"this.style.display='none';this.parentNode.classList.add('image-error')\"/>";
                    string onclick = "add(" + JsonString(id) + "," + JsonString(S(x, "Name")) + "," + JsonNumber(shown) + ")";
                    b.Append("<div class='card'>").Append(image).Append("<h3>").Append(E(S(x, "Name"))).Append("</h3><div class='muted'>")
                        .Append(E(S(x, "Category"))).Append(" · ").Append(E(S(x, "Brand"))).Append("</div><div class='muted'>")
                        .Append(E(S(x, "PublicDescription"))).Append("</div><div class='price ")
                        .Append(string.IsNullOrWhiteSpace(sale) || sale == "0" ? "" : "sale").Append("'>$ ").Append(E(shown))
                        .Append("</div><div class='muted'>Stock: ").Append(E(S(x, "Stock"))).Append("</div><button class='btn' onclick='")
                        .Append(onclick).Append("'>AGREGAR</button></div>");
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
            b.Append("</div></section><div id='approvedOrderAlert' class='approved-order-alert' role='status'><div class='ao-title'>🟢 PEDIDO APROBADO</div><div class='ao-sub'>La tienda confirmó tu pedido. Ya está siendo preparado.</div></div><button class='cart-fab' onclick='toggleCart()'>🛒 Carrito <span id='cartCount'>0</span></button>").Append("<aside id='cartPanel' class='cart-panel cart'><h2>Tu carrito</h2><div id='cartItems'>Carrito vacío.</div><h3>Total: $ <span id='total'>0</span></h3><form onsubmit='return sendOrder(event)'><input type='hidden' id='storeId' value='").Append(E(realId)).Append("'/><input id='name' placeholder='Nombre completo' required/><input id='email' type='email' placeholder='Correo electrónico'/><input id='phone' placeholder='Teléfono'/><select id='fulfillment'><option>Delivery</option><option>Retiro</option></select><input id='address' placeholder='Dirección / punto de retiro'/><select id='paymentMethod'><option>Transferencia</option><option>Mercado Pago</option><option>Efectivo</option></select><input id='paymentReference' placeholder='Referencia de pago (opcional)'/><label class='proof-upload'><b>📎 COMPROBANTE DE PAGO (OBLIGATORIO)</b><small>Elegí una imagen del dispositivo o sacá una foto.</small><input id='paymentProofFile' type='file' accept='image/jpeg,image/png,image/webp,image/gif' style='display:none'/><input id='paymentProofCamera' type='file' accept='image/*' style='display:none'/><div class='proof-actions'><button type='button' class='btn' onclick='openProofFile()'>📁 SUBIR FOTO</button><button type='button' class='btn violet' onclick='openProofCamera()'>📷 SACAR FOTO</button></div><div id='proofPreview' class='proof-preview'></div><input type='hidden' id='paymentProofPath'/><span id='proofState' class='muted'>Todavía no cargaste el comprobante.</span></label><input id='notes' placeholder='Notas para el vendedor'/><input id='couponCode' placeholder='Código de cupón (opcional)'/><button class='btn' type='submit'>CONFIRMAR PEDIDO</button></form><hr style='border-color:#253447;margin:18px 0'><h3>Seguimiento</h3><div id='orderStatus' class='muted'>Después de confirmar un pedido aparecerá aquí.</div><button class='btn' id='confirmReceived' style='display:none' onclick='confirmReceived()'>CONFIRMAR RECEPCIÓN</button><button class='btn violet' style='margin-left:8px' onclick='loadHistory()'>VER HISTORIAL</button><div id='history' class='muted' style='margin-top:12px'></div></aside><script>var cart=[],lastOrderId='';function toggleCart(){document.getElementById('cartPanel').classList.toggle('open')}function addPromotion(id,name,price,productIds){var key='promo:'+id,x=cart.filter(function(i){return i.id===key})[0];if(x)x.qty++;else cart.push({id:key,name:name,price:price,qty:1,note:'',promotionId:id,productIds:productIds});render();toggleOpen()}function add(id,name,price){var x=cart.filter(function(i){return i.id===id})[0];if(x)x.qty++;else cart.push({id:id,name:name,price:price,qty:1,note:''});render();toggleOpen()}function toggleOpen(){document.getElementById('cartPanel').classList.add('open')}function updateItemNote(n,v){if(cart[n])cart[n].note=v||'';}function render(){var h='',t=0,c=0;cart.forEach(function(i,n){h+='<div class=item><div><span>'+i.name+' × '+i.qty+'</span><input class=item-note placeholder="Nota para este producto (opcional)" value="'+escReview(i.note||'')+'" oninput="updateItemNote('+n+',this.value)"></div><b>$ '+(i.price*i.qty).toFixed(2)+'</b></div>';t+=i.price*i.qty;c+=i.qty});document.getElementById('cartItems').innerHTML=h||'Carrito vacío.';document.getElementById('total').innerHTML=t.toFixed(2);document.getElementById('cartCount').innerHTML=c}function refreshCatalog(){var x=new XMLHttpRequest();x.open('GET','/api/catalog/live?storeId='+encodeURIComponent(document.getElementById('storeId').value),true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);var stamp=d.updatedAt||'';if(window.lastCatalogStamp===undefined)window.lastCatalogStamp=stamp;else if(stamp!==window.lastCatalogStamp){window.lastCatalogStamp=stamp;location.reload();}document.getElementById('liveState').innerHTML='● LIVE '+(stamp?new Date(stamp).toLocaleTimeString():'');}catch(e){}}};x.send()}function pollStatus(){if(!lastOrderId)return;var u='/api/orders/status?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId),x=new XMLHttpRequest();x.open('GET',u,true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);if(d.error)return;var prev=window.__nexoBuyerStatus||'';document.getElementById('orderStatus').innerHTML='Pedido <b>'+d.centralOrderId+'</b> · Estado: <b>'+d.status+'</b><br>Total: $ '+d.total+(d.updatedAt?' · Actualizado: '+new Date(d.updatedAt).toLocaleString():'');document.getElementById('confirmReceived').style.display=(d.status==='Entregado'&&!d.buyerConfirmed)?'inline-block':'none';if((prev==='Pendiente'||prev==='')&&d.status==='Preparando')showApprovedSignal();window.__nexoBuyerStatus=d.status;}catch(e){}}};x.send()}function approvedBeep(){try{var C=window.AudioContext||window.webkitAudioContext;if(!C)return;window.__nexoApprovedAudio=window.__nexoApprovedAudio||new C();var a=window.__nexoApprovedAudio,o=a.createOscillator(),g=a.createGain();o.type='sine';o.frequency.value=880;g.gain.value=.07;o.connect(g);g.connect(a.destination);o.start();setTimeout(function(){o.frequency.value=1175;},120);setTimeout(function(){o.stop();},240);}catch(e){}}function showApprovedSignal(){var el=document.getElementById('approvedOrderAlert');if(!el)return;el.className='approved-order-alert show';clearTimeout(window.__nexoApprovedTimer);clearInterval(window.__nexoApprovedBeepTimer);var n=0;approvedBeep();n=1;window.__nexoApprovedBeepTimer=setInterval(function(){if(n>=10){clearInterval(window.__nexoApprovedBeepTimer);return;}approvedBeep();n++;},1000);window.__nexoApprovedTimer=setTimeout(function(){el.className='approved-order-alert';clearInterval(window.__nexoApprovedBeepTimer);},10000);}function loadHistory(){var email=document.getElementById('email').value;if(!email){alert('Ingresá tu correo para consultar el historial.');return}var x=new XMLHttpRequest();x.open('GET','/api/orders/history?storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&email='+encodeURIComponent(email),true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var a=JSON.parse(x.responseText),h='';a.forEach(function(o){h+='<div class=item><span>'+o.centralOrderId+' · '+o.status+'</span><b>$ '+o.total+'</b></div>'});document.getElementById('history').innerHTML=h||'No hay pedidos para este correo.'}catch(e){document.getElementById('history').innerHTML='No se pudo cargar el historial.'}}};x.send()}function confirmReceived(){var data='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&centralOrderId='+encodeURIComponent(lastOrderId)+'&email='+encodeURIComponent(document.getElementById('email').value),x=new XMLHttpRequest();x.open('POST','/api/orders/confirm',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){alert(x.responseText.indexOf('OK|')===0?'Recepción confirmada.':'No se pudo confirmar.');pollStatus()}};x.send(data)}function useCoupon(code){document.getElementById('couponCode').value=code;toggleOpen()}function toggleReviewForm(){var f=document.getElementById('reviewForm');if(f)f.style.display=f.style.display==='none'?'block':'none';}function pickRating(n){document.getElementById('reviewRating').value=n;var bs=document.querySelectorAll('#starPicker button');for(var i=0;i<bs.length;i++)bs[i].className=(i<n?'selected':'');}function addReviewEmoji(e){var x=document.getElementById('reviewEmoji');x.value=(x.value+' '+(e||'')).trim();}function escReview(s){return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\"/g,'&quot;').replace(/'/g,'&#39;');}function starsText(n){n=Math.max(0,Math.min(5,Number(n)||0));var full=Math.round(n),s='';for(var i=0;i<full;i++)s+='★';while(s.length<5)s+='☆';return s;}function loadReviews(){var x=new XMLHttpRequest();x.open('GET','/api/reviews?storeId='+encodeURIComponent(document.getElementById('storeId').value),true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText);document.getElementById('reviewAverage').textContent=Number(d.average||0).toFixed(1);document.getElementById('reviewStars').textContent=starsText(d.average||0);document.getElementById('reviewCount').textContent=(d.count||0)+' reseña'+((d.count||0)===1?'':'s');var h='';(d.reviews||[]).slice(0,20).forEach(function(r){h+='<div class=review-item><div class=stars>'+starsText(r.rating)+'</div><b>'+r.author+'</b>'+(r.emoji?' <span>'+r.emoji+'</span>':'')+'<small> · '+new Date(r.createdAt).toLocaleDateString()+'</small>'+(r.comment?'<div style=margin-top:5px>'+r.comment+'</div>':'')+'</div>';});document.getElementById('reviewList').innerHTML=h;}catch(e){}}};x.send();}function saveReview(){var rating=parseInt(document.getElementById('reviewRating').value||'0',10),msg=document.getElementById('reviewMessage');if(rating<1||rating>5){msg.textContent='Elegí de 1 a 5 estrellas.';return}var data='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&rating='+encodeURIComponent(rating)+'&comment='+encodeURIComponent(document.getElementById('reviewComment').value)+'&emoji='+encodeURIComponent(document.getElementById('reviewEmoji').value),x=new XMLHttpRequest();x.open('POST','/api/reviews/save',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded;charset=UTF-8');x.onreadystatechange=function(){if(x.readyState===4){try{var d=JSON.parse(x.responseText);msg.textContent=d.message||'Listo.';if(d.ok){loadReviews();document.getElementById('reviewComment').value='';document.getElementById('reviewEmoji').value='';pickRating(0);}}catch(e){msg.textContent='No se pudo guardar la reseña.';}}};x.send(data);}function proofToBase64(file){return new Promise(function(resolve,reject){var r=new FileReader();r.onload=function(){resolve(r.result.split(',')[1]||'')};r.onerror=reject;r.readAsDataURL(file);});}function uploadProof(file){return new Promise(async function(resolve,reject){try{if(!file){reject(new Error('El comprobante es obligatorio.'));return}if(file.type.indexOf('image/')!==0){reject(new Error('El comprobante debe ser una imagen.'));return}if(file.size>5*1024*1024){reject(new Error('El comprobante supera 5 MB.'));return}var base=await proofToBase64(file),body='storeId='+encodeURIComponent(document.getElementById('storeId').value)+'&fileName='+encodeURIComponent('comprobante-'+Date.now()+'.jpg')+'&contentType='+encodeURIComponent(file.type)+'&base64='+encodeURIComponent(base);var x=new XMLHttpRequest();x.open('POST','/api/order-proof/upload',true);x.timeout=90000;x.setRequestHeader('Content-Type','application/x-www-form-urlencoded;charset=UTF-8');x.onreadystatechange=function(){if(x.readyState===4){if(x.status===200&&x.responseText.indexOf('OK|')===0)resolve(x.responseText.split('|')[2]);else reject(new Error(x.responseText||'No se pudo subir el comprobante.'));}};x.onerror=function(){reject(new Error('No se pudo conectar con el almacenamiento.'));};x.ontimeout=function(){reject(new Error('La carga tardó demasiado.'));};x.send(body); }catch(e){reject(e);}});}function handleProofInput(input){if(!input)return;input.addEventListener('change',async function(){var f=this.files&&this.files[0],pr=document.getElementById('proofPreview'),st=document.getElementById('proofState');if(!f)return;if(f.type.indexOf('image/')!==0){st.textContent='Error: el comprobante debe ser una imagen.';this.value='';return}pr.innerHTML='<img src=\"'+URL.createObjectURL(f)+'\" alt=\"Comprobante\">';st.textContent='Subiendo comprobante...';try{var url=await uploadProof(f);document.getElementById('paymentProofPath').value=url;st.textContent='✓ Comprobante cargado correctamente.';}catch(err){document.getElementById('paymentProofPath').value='';st.textContent='Error: '+err.message;this.value='';}});}var proofInput=document.getElementById('paymentProofFile'),proofCamera=document.getElementById('paymentProofCamera');handleProofInput(proofInput);handleProofInput(proofCamera);function openProofFile(){if(proofInput)proofInput.click();}function openProofCamera(){if(proofCamera)proofCamera.click();}function sendOrder(e){e.preventDefault();if(!cart.length){alert('Agregá al menos un producto.');return false}var proof=document.getElementById('paymentProofPath').value;if(!proof){alert('Tenés que adjuntar el comprobante de pago antes de confirmar el pedido.');return false}var name=(document.getElementById('name').value||'').trim();if(!name){alert('Ingresá tu nombre completo.');document.getElementById('name').focus();return false}var fulfillment=document.getElementById('fulfillment').value;var address=(document.getElementById('address').value||'').trim();if(fulfillment==='Delivery'&&!address){alert('Ingresá la dirección de entrega.');document.getElementById('address').focus();return false}var total=cart.reduce(function(a,i){return a+(Number(i.price)||0)*(Number(i.qty)||0)},0);var data={storeId:document.getElementById('storeId').value,customerName:name,customerEmail:document.getElementById('email').value,phone:document.getElementById('phone').value,fulfillment:fulfillment,address:address,paymentMethod:document.getElementById('paymentMethod').value,paymentReference:document.getElementById('paymentReference').value,paymentProofPath:proof,notes:document.getElementById('notes').value,couponCode:document.getElementById('couponCode').value,total:total.toFixed(2),itemsJson:JSON.stringify(cart)},body=[];Object.keys(data).forEach(function(k){body.push(encodeURIComponent(k)+'='+encodeURIComponent(data[k]))});var btn=e.target.querySelector('button[type=submit]');if(btn){btn.disabled=true;btn.textContent='ENVIANDO PEDIDO...'}var x=new XMLHttpRequest();x.open('POST','/api/orders/create',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded;charset=UTF-8');x.timeout=90000;x.onreadystatechange=function(){if(x.readyState===4){if(btn){btn.disabled=false;btn.textContent='CONFIRMAR PEDIDO'}if(x.status===200&&x.responseText.indexOf('OK|')===0){lastOrderId=x.responseText.split('|')[1];alert('Pedido enviado. Número central: '+lastOrderId);cart=[];render();pollStatus()}else{var msg=x.responseText||'sin respuesta';var map={'ERROR|payment_proof_required':'Falta el comprobante de pago.','ERROR|store':'La tienda no está disponible en este momento.','ERROR|total':'El total del pedido no es válido.','ERROR|storeId':'No se pudo identificar la tienda.'};alert('No se pudo enviar el pedido: '+(map[msg]||msg));}}};x.onerror=function(){if(btn){btn.disabled=false;btn.textContent='CONFIRMAR PEDIDO'}alert('No se pudo conectar con NexoMarket.');};x.ontimeout=function(){if(btn){btn.disabled=false;btn.textContent='CONFIRMAR PEDIDO'}alert('El envío tardó demasiado. Intentá nuevamente.');};x.send(body.join('&'));return false}render();loadReviews();setInterval(refreshCatalog,1800);setInterval(pollStatus,4000);(function(){var lastScroll=window.pageYOffset||0,home=document.getElementById('scrollHome');window.addEventListener('scroll',function(){var y=window.pageYOffset||0;if(y>55&&y<lastScroll)home.className='scroll-home show';else if(y<=55||y>lastScroll)home.className='scroll-home';lastScroll=y;},{passive:true});})();</script></body></html>");
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
                    cs.Distance = p.Length > 13 ? ParseDouble(Uri.UnescapeDataString(p[13])) : 0d; cs.Logo = p.Length > 14 ? Uri.UnescapeDataString(p[14]) : ""; cs.Featured = p.Length > 15 && Uri.UnescapeDataString(p[15]) == "1"; cs.StorePhoto = p.Length > 16 ? Uri.UnescapeDataString(p[16]) : ""; cs.Address = p.Length > 17 ? Uri.UnescapeDataString(p[17]) : ""; cs.Description = p.Length > 18 ? Uri.UnescapeDataString(p[18]) : ""; cs.RatingSummary = p.Length > 19 ? Uri.UnescapeDataString(p[19]) : "0.0|0"; cs.FeaturedPlus = p.Length > 20 && Uri.UnescapeDataString(p[20]) == "1";
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
                 .Append(",\"pickup\":").Append(x.Pickup ? "true" : "false").Append(",\"distanceKm\":").Append(x.Distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",\"featured\":").Append(x.Featured ? "true" : "false").Append(",\"featuredPlus\":").Append(x.FeaturedPlus ? "true" : "false").Append(",\"ratingSummary\":").Append(JsonString(x.RatingSummary)).Append('}');
            }
            b.Append(']'); return b.ToString();
        }

        private string Marketplace(string query, string cookie)
        {
            string q = QueryValue(query, "q"); double lat = 0d, lon = 0d;
            bool latOk = double.TryParse(QueryValue(query, "lat"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
            bool lonOk = double.TryParse(QueryValue(query, "lon"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
            bool hasCoords = latOk && lonOk;
            List<CentralStore> stores = new List<CentralStore>();
            {
                string storeQuery = hasCoords ? "lat=" + lat.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&lon=" + lon.ToString(System.Globalization.CultureInfo.InvariantCulture) : (string.IsNullOrWhiteSpace(q) ? "" : "q=" + Uri.EscapeDataString(q));
                string lines = StoreLines(storeQuery);
                using (StringReader reader = new StringReader(lines)) { string line; while ((line = reader.ReadLine()) != null) { string[] p = line.Split('|'); if (p.Length < 12) continue; CentralStore cs = new CentralStore(); cs.StoreId = Uri.UnescapeDataString(p[1]); cs.Name = Uri.UnescapeDataString(p[2]); cs.PublicUrl = Uri.UnescapeDataString(p[3]); cs.City = Uri.UnescapeDataString(p[4]); cs.Province = Uri.UnescapeDataString(p[5]); cs.Category = Uri.UnescapeDataString(p[6]); cs.Latitude = ParseDouble(Uri.UnescapeDataString(p[7])); cs.Longitude = ParseDouble(Uri.UnescapeDataString(p[8])); cs.Active = Uri.UnescapeDataString(p[9]) == "1"; cs.Delivery = Uri.UnescapeDataString(p[10]) == "1"; cs.Pickup = Uri.UnescapeDataString(p[11]) == "1"; cs.Distance = p.Length > 13 ? ParseDouble(Uri.UnescapeDataString(p[13])) : 0d; cs.Logo = p.Length > 14 ? Uri.UnescapeDataString(p[14]) : ""; cs.Featured = p.Length > 15 && Uri.UnescapeDataString(p[15]) == "1"; cs.StorePhoto = p.Length > 16 ? Uri.UnescapeDataString(p[16]) : ""; cs.Address = p.Length > 17 ? Uri.UnescapeDataString(p[17]) : ""; cs.Description = p.Length > 18 ? Uri.UnescapeDataString(p[18]) : ""; cs.RatingSummary = p.Length > 19 ? Uri.UnescapeDataString(p[19]) : "0.0|0"; cs.FeaturedPlus = p.Length > 20 && Uri.UnescapeDataString(p[20]) == "1"; stores.Add(cs); } }
            }
            StringBuilder b = new StringBuilder();
            string locationTitle = hasCoords ? (string.IsNullOrWhiteSpace(q) ? "Tu ubicación" : q) : (string.IsNullOrWhiteSpace(q) ? "Sin ubicación definida" : q);
            b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><meta http-equiv='Cache-Control' content='no-store'><title>NexoMarket · Tiendas</title>");
            b.Append("<style>\n*{box-sizing:border-box}html{background:#05070a}body{font-family:'Segoe UI',Arial,sans-serif;background:#05070a;color:#fff;margin:0;position:relative;overflow-x:hidden;min-height:100vh}body:before{content:'';position:fixed;inset:-18%;pointer-events:none;z-index:0;background:radial-gradient(ellipse at 5% 12%,transparent 0 24%,rgba(57,255,102,.10) 24.1%,transparent 24.65%),radial-gradient(ellipse at 94% 24%,transparent 0 21%,rgba(54,164,255,.12) 21.1%,transparent 21.65%),radial-gradient(ellipse at 74% 88%,transparent 0 23%,rgba(167,103,255,.12) 23.1%,transparent 23.65%),radial-gradient(circle at 18% 78%,rgba(57,255,102,.055),transparent 25%),radial-gradient(circle at 82% 8%,rgba(167,103,255,.065),transparent 24%);transform:rotate(-7deg);animation:marketGlow 18s ease-in-out infinite alternate}body:after{content:'';position:fixed;width:92vw;height:48vh;left:-30vw;bottom:4vh;border:1px solid rgba(255,255,255,.09);border-right-color:rgba(57,255,102,.24);border-radius:50%;transform:rotate(-15deg);pointer-events:none;z-index:0;box-shadow:0 0 90px rgba(57,255,102,.07),0 0 140px rgba(54,164,255,.04)}@keyframes marketGlow{from{transform:translate3d(-1%,0,0) rotate(-7deg)}to{transform:translate3d(1%,1%,0) rotate(-4deg)}}.wrap{max-width:1240px;margin:auto;padding:22px 22px 70px;position:relative;z-index:1}.wrap:before{content:'';position:fixed;inset:-10%;pointer-events:none;z-index:-1;background:linear-gradient(116deg,transparent 0 34%,rgba(255,255,255,.028) 34.1%,transparent 34.3%,transparent 63%,rgba(57,255,102,.025) 63.1%,transparent 63.35%),radial-gradient(ellipse at 15% 48%,transparent 0 28%,rgba(57,255,102,.05) 28.1%,transparent 28.55%),radial-gradient(ellipse at 88% 62%,transparent 0 24%,rgba(167,103,255,.055) 24.1%,transparent 24.55%);animation:marketLines 20s ease-in-out infinite alternate}@keyframes marketLines{from{transform:translateX(-1%) rotate(-3deg)}to{transform:translateX(1%) rotate(2deg)}}.top{display:flex;justify-content:space-between;align-items:center;padding:6px 4px 18px;position:relative}.top:after{content:'';position:absolute;left:0;right:0;bottom:4px;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.25),rgba(57,255,102,.35),rgba(167,103,255,.30),transparent)}.brand{font-weight:900;font-size:23px;letter-spacing:-.5px}.brand .n{color:#39ff66;text-shadow:0 0 12px rgba(57,255,102,.45)}.top a{color:#dce7f1;text-decoration:none;margin-left:18px;font-weight:800}.top a:hover{color:#fff;text-shadow:0 0 12px rgba(255,255,255,.35)}.hero{padding:30px;border:1px solid rgba(255,255,255,.12);background:linear-gradient(135deg,rgba(12,22,31,.78),rgba(8,14,23,.68));border-radius:26px;box-shadow:0 24px 70px rgba(0,0,0,.34),inset 0 1px 0 rgba(255,255,255,.05);backdrop-filter:blur(18px);-webkit-backdrop-filter:blur(18px);position:relative;overflow:hidden}.hero:before,.hero:after{content:'';position:absolute;pointer-events:none;border-radius:50%;border:1px solid rgba(255,255,255,.10);box-shadow:0 0 60px rgba(57,255,102,.08)}.hero:before{width:760px;height:340px;right:-390px;top:-190px;border-left-color:rgba(54,164,255,.35);transform:rotate(-15deg)}.hero:after{width:700px;height:300px;left:-430px;bottom:-190px;border-right-color:rgba(167,103,255,.28);transform:rotate(-8deg)}.hero>*{position:relative;z-index:1}.eyebrow{font-size:11px;letter-spacing:2px;color:#39ff66;font-weight:900;text-shadow:0 0 14px rgba(57,255,102,.35)}.nexo{color:#39ff66;font-size:49px;font-weight:950;text-shadow:0 0 18px rgba(57,255,102,.24)}.market{font-size:43px;font-weight:850;text-shadow:0 0 14px rgba(255,255,255,.10)}.hero-sub{color:#b9cce0;margin-top:10px;font-size:15px;line-height:1.55}.location-box{margin-top:20px;display:flex;align-items:center;gap:10px;flex-wrap:wrap;padding:10px;border:1px solid rgba(255,255,255,.09);border-radius:17px;background:rgba(2,7,12,.42);box-shadow:inset 0 1px 0 rgba(255,255,255,.03)}.location-box input{background:rgba(4,10,16,.62);color:#fff;border:1px solid rgba(82,170,255,.28);border-radius:12px;padding:12px 14px;width:330px;outline:none}.location-box input:focus{border-color:rgba(255,255,255,.55);box-shadow:0 0 18px rgba(54,164,255,.10)}.btn{background:linear-gradient(135deg,#39ff66,#72ff91);color:#041008;border:0;border-radius:12px;padding:11px 17px;font-weight:950;cursor:pointer;box-shadow:0 0 20px rgba(57,255,102,.13);transition:all .2s ease}.btn:hover{background:#fff;color:#000;transform:translateY(-2px);box-shadow:0 0 18px rgba(255,255,255,.5),0 0 38px rgba(57,255,102,.12)}.btn.alt{background:rgba(255,255,255,.045);color:#fff;border:1px solid rgba(255,255,255,.16);box-shadow:inset 0 1px 0 rgba(255,255,255,.05)}.hint{color:#7f96aa;font-size:12px;margin-top:12px}.section-head{display:flex;justify-content:space-between;align-items:end;margin:28px 2px 14px}.section-head h2{margin:0;font-size:25px}.section-head p{margin:6px 0 0;color:#8fa4b7;font-size:13px}.mini{color:#7dff9e;font-size:11px;font-weight:900;letter-spacing:.7px;text-shadow:0 0 12px rgba(57,255,102,.22)}.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px}.grid .card{display:grid;grid-template-columns:210px 1fr;column-gap:18px;align-items:start;min-height:260px}.grid .card .logo{float:none;width:210px;height:220px;grid-row:1 / span 9;margin:0}.grid .card .store-photo-mini{grid-column:2}.grid .card .promo-badges{grid-column:2}.grid .card .name{font-size:23px}.grid .card .meta{font-size:13px}.grid .card .open{font-size:13px}.grid .card .distance{font-size:12px}.card{display:block;color:#fff;text-decoration:none;background:linear-gradient(145deg,rgba(14,25,36,.76),rgba(8,13,21,.70));border:1px solid rgba(126,184,255,.20);border-radius:21px;padding:18px;min-height:160px;transition:transform .22s ease,border-color .22s ease,box-shadow .22s ease;position:relative;overflow:hidden;backdrop-filter:blur(15px);-webkit-backdrop-filter:blur(15px);box-shadow:0 16px 40px rgba(0,0,0,.24),inset 0 1px 0 rgba(255,255,255,.035)}.card:before{content:'';position:absolute;left:8%;right:8%;top:0;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.36),rgba(54,164,255,.28),rgba(167,103,255,.30),transparent)}.card:after{content:'';position:absolute;width:190px;height:190px;right:-120px;bottom:-125px;border-radius:50%;border:1px solid rgba(54,164,255,.12);box-shadow:0 0 38px rgba(54,164,255,.06);pointer-events:none}.card:hover{transform:translateY(-5px);border-color:rgba(255,255,255,.38);box-shadow:0 0 28px rgba(54,164,255,.09),0 22px 48px rgba(0,0,0,.36),inset 0 1px 0 rgba(255,255,255,.07)}.logo-wrap{position:relative;width:210px;height:220px;grid-row:1 / span 10}.logo-wrap .logo{width:100%;height:100%}.logo-star{position:absolute;right:8px;top:8px;width:34px;height:34px;border-radius:50%;display:flex;align-items:center;justify-content:center;background:#05070a;color:#ffd84d;border:1px solid #ffd84d;box-shadow:0 0 16px rgba(255,216,77,.55);font-size:18px}.tier-badge{display:inline-flex;align-items:center;gap:6px;width:max-content;padding:7px 12px;border-radius:999px;font-size:10px;font-weight:950;letter-spacing:.7px;margin-bottom:8px}.tier-featured{color:#7cff9a;border:1px solid #39ff66;background:rgba(7,30,18,.7);box-shadow:0 0 14px rgba(57,255,102,.18),inset 0 0 12px rgba(57,255,102,.04)}.card.featured{border:1px solid rgba(57,255,102,.72);box-shadow:0 0 18px rgba(57,255,102,.12),inset 0 0 24px rgba(57,255,102,.025)}.tier-plus{color:#e1c7ff;border:1px solid #39ff66;background:linear-gradient(90deg,rgba(113,45,180,.42),rgba(26,17,45,.82));box-shadow:0 0 18px rgba(167,103,255,.55),0 0 30px rgba(57,255,102,.20),inset 0 0 22px rgba(167,103,255,.18)}.card.plus{border:1px solid #39ff66;background:linear-gradient(145deg,rgba(48,19,75,.78),rgba(7,18,24,.76));box-shadow:0 0 26px rgba(167,103,255,.30),0 0 38px rgba(57,255,102,.14),inset 0 0 30px rgba(167,103,255,.10)}.card.plus:before{height:2px;background:linear-gradient(90deg,transparent,#39ff66,#b86cff,#39ff66,transparent);box-shadow:0 0 12px rgba(57,255,102,.5)}.rating-public{margin:7px 0 4px;color:#ffd84d;font-size:13px;font-weight:900;text-shadow:0 0 8px rgba(255,216,77,.18)}.rating-public b{color:#fff}.card .logo{width:78px;height:78px;border-radius:18px;background:linear-gradient(145deg,rgba(5,14,21,.75),rgba(11,12,25,.72));border:1px solid rgba(167,103,255,.45);display:flex;align-items:center;justify-content:center;color:#d1b4ff;font-size:30px;font-weight:900;float:left;margin-right:15px;overflow:hidden;box-shadow:0 0 22px rgba(167,103,255,.08)}.logo img{width:100%;height:100%;object-fit:cover;display:block}.name{font-size:20px;font-weight:950;padding-top:3px}.meta{color:#92a9bc;font-size:13px;margin-top:7px}.open{color:#55ff7e;font-size:12px;margin-top:12px;font-weight:800}.open.closed{color:#ff7187}.distance{color:#d9bb5c;font-size:12px;margin-top:6px}.store-photo-mini{margin-top:10px;height:58px;border-radius:10px;overflow:hidden;border:1px solid rgba(255,255,255,.08)}.store-photo-mini img{width:100%;height:100%;object-fit:cover}.promo-badges{display:flex;gap:4px;flex-wrap:wrap;margin-top:9px}.promo-badges span{font-size:9px;color:#fff;background:rgba(167,103,255,.18);border:1px solid rgba(167,103,255,.38);border-radius:999px;padding:4px 7px;white-space:nowrap}.empty{margin-top:18px;border:1px dashed rgba(126,184,255,.28);border-radius:18px;padding:28px;color:#a2b4c2;background:rgba(8,16,25,.56);backdrop-filter:blur(12px)}.empty b{font-size:18px}.auth-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:15px;margin-top:22px}.panel{background:rgba(7,13,20,.72);border:1px solid rgba(255,255,255,.10);border-radius:19px;padding:20px;backdrop-filter:blur(14px);box-shadow:0 14px 35px rgba(0,0,0,.24),inset 0 1px 0 rgba(255,255,255,.035)}.panel h2{margin-top:0}.panel p{color:#92a6b7;font-size:13px;line-height:1.5}.footer{margin-top:38px;border-top:1px solid rgba(255,255,255,.08);padding-top:15px;color:#60768a;font-size:11px}@media(max-width:950px){.grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.grid{grid-template-columns:1fr}.grid .card{display:block;min-height:0}.grid .card .logo{width:100%;height:150px;margin-bottom:10px}.grid .card .store-photo-mini,.grid .card .promo-badges{grid-column:auto}}@media(max-width:650px){.wrap{padding:14px}.top{align-items:flex-start;gap:10px;flex-direction:column}.top a{margin-left:0;margin-right:14px}.grid{grid-template-columns:1fr}.nexo{font-size:37px}.market{font-size:32px}.location-box{align-items:stretch}.location-box input{width:100%}.hero{padding:22px}.section-head{align-items:flex-start;gap:10px;flex-direction:column}}.store-card-cover{position:absolute;inset:0;z-index:0;overflow:hidden;border-radius:21px}.store-card-cover img{width:100%;height:100%;object-fit:cover;display:block;filter:saturate(.78) brightness(.52)}.store-card-cover:after{content:'';position:absolute;inset:0;background:linear-gradient(90deg,rgba(3,7,12,.95) 0%,rgba(4,9,16,.86) 46%,rgba(5,9,16,.60) 100%),linear-gradient(180deg,rgba(3,7,12,.08),rgba(3,7,12,.90))}.grid .card .store-card-content{position:relative;z-index:1;display:block}.grid .card .logo-wrap{width:78px;height:78px;display:block;margin:0 0 10px}.grid .card .logo-wrap .logo{width:78px;height:78px;float:none;margin:0}.grid .card .name{color:#dfe5ed;text-shadow:0 0 8px rgba(255,255,255,.18),0 0 18px rgba(167,103,255,.10)}.grid .card .meta{color:#aeb9c5;text-shadow:0 0 7px rgba(255,255,255,.07)}.grid .card .open,.grid .card .distance,.grid .card .rating-public{position:relative;z-index:2}.card.featured .store-card-cover img{filter:saturate(.86) brightness(.50)}.card.plus .store-card-cover img{filter:saturate(.90) brightness(.46)}@media(max-width:650px){.grid .card .logo-wrap,.grid .card .logo-wrap .logo{width:72px;height:72px}.grid .card .name{font-size:19px}}.nexo-cta{margin-top:26px;padding:28px;border-radius:24px;border:1px solid rgba(57,255,102,.30);background:linear-gradient(135deg,rgba(15,22,31,.72),rgba(47,19,77,.55),rgba(7,34,20,.52));box-shadow:0 18px 55px rgba(0,0,0,.30),0 0 35px rgba(167,103,255,.10),inset 0 1px 0 rgba(255,255,255,.08);backdrop-filter:blur(18px);display:flex;justify-content:space-between;gap:24px;align-items:center;overflow:hidden;position:relative}.nexo-cta:before{content:'';position:absolute;inset:-50%;background:radial-gradient(circle at 25% 40%,rgba(57,255,102,.10),transparent 28%),radial-gradient(circle at 80% 60%,rgba(167,103,255,.14),transparent 30%);animation:ctaGlow 8s ease-in-out infinite alternate}.nexo-cta>*{position:relative;z-index:1}.nexo-cta h2{font-size:26px;margin:8px 0;color:#dfe6ed;text-shadow:0 0 10px rgba(255,255,255,.15),0 0 20px rgba(167,103,255,.12)}.nexo-cta p{max-width:850px;color:#b7c3ce;line-height:1.6}.cta-glow{font-size:38px;line-height:1.15;text-align:center;filter:drop-shadow(0 0 14px rgba(57,255,102,.35));min-width:70px}.buyer-panel,.seller-panel{background:linear-gradient(145deg,rgba(12,20,29,.70),rgba(20,12,34,.60));border-color:rgba(255,255,255,.12);backdrop-filter:blur(16px);box-shadow:0 16px 45px rgba(0,0,0,.28),inset 0 1px 0 rgba(255,255,255,.06)}.panel-icon{font-size:28px;margin-bottom:4px;filter:drop-shadow(0 0 10px rgba(255,255,255,.18))}.auth-grid .panel{transition:transform .22s ease,border-color .22s ease,box-shadow .22s ease}.auth-grid .panel:hover{transform:translateY(-5px);border-color:rgba(57,255,102,.32);box-shadow:0 0 28px rgba(57,255,102,.08),0 18px 45px rgba(0,0,0,.35)}@keyframes ctaGlow{from{transform:translate3d(-1%,0,0) scale(1)}to{transform:translate3d(1%,1%,0) scale(1.04)}}h1,h2,h3,.name{color:#d7dde4;text-shadow:0 0 7px rgba(255,255,255,.12),0 0 15px rgba(167,103,255,.09)}.meta,.muted,.hint,.hero-sub,.footer{color:#aeb9c4}.grid .card{background:linear-gradient(145deg,rgba(7,11,17,.72),rgba(14,18,28,.62));backdrop-filter:blur(13px);border-color:rgba(255,255,255,.12)}.section-head{margin-top:28px}.btn{box-shadow:0 0 16px rgba(57,255,102,.12)}@media(max-width:650px){.nexo-cta{padding:20px;flex-direction:column;align-items:flex-start}.nexo-cta h2{font-size:21px}.cta-glow{display:none}.nexo-cta .btn{margin-top:7px;width:100%}}</style></head><body><div class='wrap'>");

            CentralUser viewer=SessionUser(cookie); string accountLinks=viewer!=null && viewer.Role=="seller" ? "<a href='/'>Tiendas</a><a href='/seller'>Mi cuenta</a><a href='/store/"+Uri.EscapeDataString(viewer.StoreId??"")+"'>Mi tienda</a><a href='/logout'>Salir</a>" : "<a href='/'>Tiendas</a><a href='/login'>Ingresar</a><a href='/register'>Crear cuenta</a>"; b.Append("<div class='top'><div class='brand'><span class='n'>NEXO</span>MARKET</div><div>"+accountLinks+"</div></div><button id='scrollHome' class='scroll-home' type='button' onclick=\"location.href='/'\">NEXO<span>MARKET</span></button>");
            b.Append("<section class='hero'><span class='eyebrow'>MARKETPLACE</span><div><span class='nexo'>NEXO</span><span class='market'>MARKET</span></div><div class='hero-sub'>Encontrá todas las tiendas disponibles y priorizá las más cercanas" + (hasCoords ? " a <b>" + E(locationTitle) + "</b>." : ".") + "</div><form class='location-box' method='get' action='/'><input id='q' name='q' value='" + E(q) + "' placeholder='¿Desde dónde estás? Ej.: Mendoza, Luján...'/><input type='hidden' id='lat' name='lat'/><input type='hidden' id='lon' name='lon'/><button class='btn' type='submit'>Buscar tiendas</button><button class='btn' type='button' onclick='geo()'>Usar mi ubicación</button></form><div class='hint'>La ubicación se convierte a coordenadas y las tiendas se ordenan por cercanía. Los datos se actualizan desde NexoMarket Central.</div></section>");
            b.Append("<div class='section-head'><div><h2>Tiendas disponibles</h2><p>Todas las tiendas se muestran desde el directorio central. Las cerradas permanecen visibles con su estado.</p></div><span class='mini'>DIRECTORIO MULTI-TIENDA</span></div>");
            if (stores.Count > 0) { b.Append("<div class='grid'>"); foreach (CentralStore cs in stores) { string href = "/store/" + Uri.EscapeDataString(cs.StoreId); string d = cs.Distance > 0 ? cs.Distance.ToString("0.0") + " km · " : ""; string logo = string.IsNullOrWhiteSpace(cs.Logo) ? "<span>N</span>" : "<img src='" + E(cs.Logo) + "' alt='" + E(cs.Name) + "' loading='lazy'/>"; string promoHtml=StorePromoBadges(cs.StoreId); string photoHtml=string.IsNullOrWhiteSpace(cs.StorePhoto)?"":"<div class='store-card-cover'><img src='"+E(cs.StorePhoto)+"' alt='"+E(cs.Name)+"' loading='lazy'/></div>"; string[] ratingParts=(cs.RatingSummary??"0.0|0").Split('|'); string rating=ratingParts.Length>0?ratingParts[0]:"0.0"; string reviewCount=ratingParts.Length>1?ratingParts[1]:"0"; string tier=cs.FeaturedPlus?"plus":(cs.Featured?"featured":""); string tierBadge=cs.FeaturedPlus?"<div class='tier-badge tier-plus'>✦ DESTACADA PLUS</div>":(cs.Featured?"<div class='tier-badge tier-featured'>★ TIENDA DESTACADA</div>":""); string starCorner=(cs.FeaturedPlus||cs.Featured)?"<span class='logo-star'>★</span>":""; b.Append("<a class='card "+tier+"' href='" + E(href) + "'>"+photoHtml+"<div class='store-card-content'><div class='logo-wrap'><div class='logo'>" + logo + "</div>"+starCorner+"</div>" + tierBadge + "<div class='name'>" + E(cs.Name) + "</div><div class='meta'>" + E(cs.Category.Length == 0 ? "Comercio" : cs.Category) + " · " + E(cs.City) + "</div><div class='open " + (cs.Active ? "" : "closed") + "'>● " + (cs.Active ? "Abierta" : "Cerrada") + " · " + (cs.Delivery ? "Delivery" : "Retiro") + "</div><div class='distance'>📍 " + E(d) + (cs.Delivery ? "🚚 Delivery" : "🏪 Retiro") + "</div><div class='rating-public'>★ <b>"+E(rating)+"</b>/5 · "+E(reviewCount)+" reseña"+(reviewCount=="1"?"":"s")+"</div><div class='meta'>"+E(cs.Address)+"</div><div class='meta'>"+E(cs.Description.Length>110?cs.Description.Substring(0,110)+"…":cs.Description)+"</div>"+promoHtml+"</div></a>"); } b.Append("</div>"); }
            else b.Append("<div class='empty'><b>No hay tiendas publicadas todavía.</b><p>Cuando un vendedor publique o actualice su tienda, aparecerá automáticamente aquí. Si acabás de publicarla, volvé a cargar esta página.</p></div>");
            b.Append("<section class='nexo-cta'><div class='cta-copy'><span class='eyebrow'>🚀 TU PRÓXIMA COMPRA, MÁS CERCA</span><h2>🛍️ Comprá, seguí tu pedido y recibí avisos en NexoMarket</h2><p>Creá tu cuenta de comprador para guardar tus compras, seguir envíos y recibir notificaciones cuando tu pedido sea aprobado, preparado, enviado o entregado. 📦🔔✨</p><a class='btn alt' href='/register'>👤 CREAR CUENTA DE COMPRADOR</a><a class='btn ghost' href='/login'>🔐 YA TENGO CUENTA</a></div><div class='cta-glow'>💚<br/>⚡<br/>💜</div></section><section class='auth-grid'><div class='panel buyer-panel'><div class='panel-icon'>🛒</div><h2>¿Ya tenés cuenta?</h2><p>Entrá como comprador y seguí todos tus pedidos desde un solo lugar. Recibí alertas cuando el vendedor apruebe o cambie el estado de tu compra. 📲</p><a class='btn alt' href='/register'>✨ CREAR CUENTA</a></div><div class='panel seller-panel'><div class='panel-icon'>🏪</div><h2>¿Sos vendedor?</h2><p>Creá tu tienda online en NexoMarket, publicá productos, recibí pedidos y hacé crecer tu negocio. 🚀💚</p><a class='btn' href='/seller-register'>🏪 CREAR MI TIENDA</a><div class='mini'>● SINCRONIZACIÓN CENTRAL ACTIVA</div></div></section>");
            b.Append("<div class='footer'>NexoMarket Central · " + stores.Count + " tiendas encontradas · datos actualizados sin caché</div></div><script>function geo(){if(!navigator.geolocation){alert('Tu navegador no permite ubicación. Escribí una ciudad.');return;}navigator.geolocation.getCurrentPosition(function(p){document.getElementById('lat').value=p.coords.latitude;document.getElementById('lon').value=p.coords.longitude;document.getElementById('q').value='Mi ubicación';document.querySelector('.location-box').submit();},function(){alert('No se pudo obtener la ubicación.');},{enableHighAccuracy:false,timeout:8000,maximumAge:300000});}(function(){var last=window.pageYOffset||0,btn=document.getElementById('scrollHome');window.addEventListener('scroll',function(){var y=window.pageYOffset||0;if(y>55&&y<last)btn.className='scroll-home show';else if(y<=55||y>last)btn.className='scroll-home';last=y;},{passive:true});})();</script></body></html>");
            return b.ToString();
        }

        private string StoreRatingSummary(string storeId)
        {
            try { lock(_sync) { XDocument d=LoadFile(_reviewsFile,"NexoMarketReviews","Reviews"); XElement root=d.Root==null?null:d.Root.Element("Reviews"); if(root==null)return "0.0|0"; List<XElement> rs=root.Elements("Review").Where(x=>S(x,"StoreId")==storeId&&S(x,"Active")!="0").ToList(); if(rs.Count==0)return "0.0|0"; decimal avg=rs.Average(x=>Money(S(x,"Rating"))); return avg.ToString("0.0",CultureInfo.InvariantCulture)+"|"+rs.Count.ToString(CultureInfo.InvariantCulture); } } catch{return "0.0|0";}
        }

        private string StorePromoBadges(string storeId)
        {
            try { lock(_sync) { XDocument d=LoadFile(_catalogFile,"NexoMarketCatalog","Products"); XElement root=d.Root==null?null:d.Root.Element("Promotions"); if(root==null)return ""; List<XElement> ps=root.Elements("Promotion").Where(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase)&&S(x,"Active")!="0").Take(3).ToList(); if(ps.Count==0)return ""; StringBuilder b=new StringBuilder("<div class='promo-badges'>"); foreach(XElement p in ps){string n=S(p,"Name"); b.Append("<span>🎁 ").Append(E(string.IsNullOrWhiteSpace(n)?"PROMO":n)).Append("</span>");} b.Append("</div>"); return b.ToString(); } } catch { return ""; }
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
            storeId = NormalizeStoreId(storeId ?? "");
            syncKey = (syncKey ?? "").Trim();
            if(string.IsNullOrWhiteSpace(storeId) || string.IsNullOrWhiteSpace(syncKey)) return false;
            lock(_sync)
            {
                XElement stores = _doc.Root.Element("Stores");
                XElement store = stores == null ? null : stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));
                if(store==null || S(store,"Active")!="1") return false;
                string expected=S(store,"SyncKey");
                string deterministic=ComputeStorePairKey(storeId);
                // Compatibilidad: instalaciones antiguas pueden conservar una SyncKey distinta.
                // La clave derivada del StoreId es ahora la clave canónica y permite reparar
                // automáticamente la vinculación sin pedir una contraseña al usuario.
                if(string.IsNullOrWhiteSpace(expected))
                {
                    store.SetElementValue("SyncKey", deterministic);
                    store.SetAttributeValue("UpdatedAt", DateTime.UtcNow.ToString("o"));
                    Save();
                    expected=deterministic;
                }
                if(string.Equals(expected,syncKey,StringComparison.Ordinal)) return true;
                if(string.Equals(deterministic,syncKey,StringComparison.Ordinal))
                {
                    if(!string.Equals(expected,deterministic,StringComparison.Ordinal))
                    {
                        store.SetElementValue("SyncKey",deterministic);
                        store.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                        Save();
                    }
                    return true;
                }
                return false;
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
            user=FindAccount(email); if(user==null || !user.Active) return false;
            if(!string.IsNullOrWhiteSpace(user.TrialExpiresAt)){DateTime trial; if(DateTime.TryParse(user.TrialExpiresAt,null,DateTimeStyles.RoundtripKind,out trial) && trial.ToUniversalTime()<DateTime.UtcNow)return false;}
            if(user.Role=="seller" && !string.IsNullOrWhiteSpace(user.StoreId))
            {
                // No capturar el parámetro out `user` dentro de la lambda de LINQ.
                // El compilador C# genera CS1628 si un parámetro ref/out se usa dentro
                // de una expresión lambda. Copiamos el valor a una variable local normal.
                string verifiedStoreId = user.StoreId;
                lock(_sync)
                {
                    XElement storesRoot = _doc.Root == null ? null : _doc.Root.Element("Stores");
                    XElement st = storesRoot == null ? null : storesRoot.Elements("Store")
                        .FirstOrDefault(x => string.Equals(S(x,"StoreId"),verifiedStoreId,StringComparison.OrdinalIgnoreCase));
                    if(st!=null && S(st,"Active")=="0") return false;
                }
            }
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
            // Web-first: StoreId es interno. Si no viene, la Central crea la tienda automáticamente.
            if(string.IsNullOrWhiteSpace(storeId)) storeId=Guid.NewGuid().ToString("N").ToUpperInvariant();
            if(!StoreExists(storeId))
            {
                string boot=ClaimStore(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",storeId},{"name",name},{"category","Comercio"},{"description","Tienda NexoMarket"},{"active","1"}});
                if(!boot.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)) return "ERROR|store_bootstrap_failed|"+Escape(boot);
            }
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
            TouchStoreActivity(u.StoreId);
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
            if(d==null)return "ERROR|pairing_invalid_or_expired|El código no existe, ya fue utilizado o venció.";
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
                string html = "<div class='card'><div class='eyebrow'>VENDEDOR</div><h1>Seller Center</h1><p class='muted'>Ingresá con el correo y la contraseña de tu cuenta.</p><a class='btn violet' href='/seller-login'>INGRESAR COMO VENDEDOR</a></div>" +
                    "<div class='card'><div class='eyebrow'>CUENTA</div><h2>Ingreso general</h2><form method='post' action='/login'><input name='email' type='email' placeholder='Correo electrónico' required/><input name='password' type='password' placeholder='Contraseña' required/><button class='btn' type='submit'>INGRESAR</button></form><p class='muted'>¿No tenés cuenta? <a href='/register'>Crear cuenta</a> · <a href='/forgot-password'>Recuperar contraseña</a></p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingresar", html)); return; }
            CentralUser u; Dictionary<string,string> f=Form(body);
            if(!VerifyAccount(Get(f,"email"),Get(f,"password"),out u)) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingreso", "<div class='error'>Correo o contraseña incorrectos.</div><a class='btn' href='/login'>Volver a intentar</a>")); return; }
            string token=CreateSessionToken(u); lock(_sync)_sessions[token]=u;
            string dest=u.Role=="seller"?"/seller":"/buyer"; WriteRedirectCookie(stream,dest,"NexoCentralSession="+token+"; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax");
        }

        // Acceso de vendedor por Store ID: el Store ID es el vínculo común entre
        // NexoMarket Windows y el Seller Center Web. No se crea una segunda tienda
        // ni se pide correo/contraseña para este acceso operativo.
        private void CentralSellerStoreLogin(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                string html="<div class='card seller-login-card'><div class='brand'><span>NEXO</span>MARKET <small>SELLER CENTER</small></div><div class='eyebrow'>CUENTA CENTRAL</div><h1>Ingresar como vendedor</h1><p class='muted'>Si ya tenés una cuenta de vendedor, alcanza con tu correo y contraseña.</p><form method='post' action='/seller-login'><label class='muted'>Correo</label><input name='email' type='email' autocomplete='username' required/><label class='muted'>Contraseña</label><input name='password' type='password' autocomplete='current-password' required/><button class='btn violet' type='submit'>INGRESAR AL SELLER CENTER</button></form><p class='muted small'>En Windows simplemente iniciá sesión con el mismo correo y contraseña.</p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Vendedor · Cuenta central",html)); return;
            }
            Dictionary<string,string> f=Form(body); string email=Get(f,"email").Trim().ToLowerInvariant(), password=Get(f,"password"); CentralUser u;
            if(!VerifyAccount(email,password,out u)||u==null||u.Role!="seller"||string.IsNullOrWhiteSpace(u.StoreId)) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Ingreso vendedor","<div class='error'>Correo, contraseña o cuenta de vendedor incorrectos.</div><a class='btn violet' href='/seller-login'>Volver a intentar</a>")); return; }
            string token=CreateSessionToken(u); lock(_sync)_sessions[token]=u;
            WriteRedirectCookie(stream,"/seller","NexoCentralSession="+token+"; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax");
        }

        private void CentralSellerRegister(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                string html="<div class='card seller-login-card'><div class='brand'><span>NEXO</span>MARKET <small>SELLER CENTER</small></div><div class='eyebrow'>NUEVA CUENTA DE VENDEDOR</div><h1>Crear cuenta</h1><p class='muted'>Creá tu cuenta de vendedor directamente en NexoMarket Web. La tienda se crea automáticamente y queda asociada a tu cuenta.</p><form method='post' action='/seller-register'><label class='muted'>Nombre del vendedor</label><input name='name' autocomplete='name' required/><label class='muted'>Correo electrónico</label><input name='email' type='email' autocomplete='username' required/><label class='muted'>Contraseña</label><input name='password' type='password' autocomplete='new-password' minlength='6' required/><button class='btn violet' type='submit'>CREAR CUENTA DE VENDEDOR</button></form><p class='muted small'>Después de crearla, podés usar el mismo correo y contraseña en NexoMarket Windows. No necesitás códigos de vinculación.</p><p class='muted small'>¿Ya tenés cuenta? <a href='/seller-login'>Iniciar sesión</a></p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta de vendedor",html)); return;
            }
            Dictionary<string,string> f=Form(body);
            string name=Get(f,"name").Trim(), email=Get(f,"email").Trim().ToLowerInvariant(), password=Get(f,"password");
            if(name.Length<2||email.Length<3||email.IndexOf('@')<1||password.Length<6)
            { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta de vendedor","<div class='error'>Completá nombre, correo y una contraseña de al menos 6 caracteres.</div><a class='btn violet' href='/seller-register'>Volver</a>")); return; }
            if(FindAccount(email)!=null)
            { Write(stream,200,"text/html; charset=utf-8",AuthPage("Cuenta existente","<div class='error'>Ese correo ya está registrado.</div><a class='btn violet' href='/seller-login'>INICIAR SESIÓN</a>")); return; }

            string storeId=Guid.NewGuid().ToString("N").ToUpperInvariant();
            string storeName=string.IsNullOrWhiteSpace(name)?"Tienda NexoMarket":name+" · Tienda";
            lock(_sync)
            {
                XElement stores=_doc.Root.Element("Stores");
                string syncKey=Guid.NewGuid().ToString("N");
                XElement store=new XElement("Store",new XAttribute("UpdatedAt",DateTime.UtcNow.ToString("o")),
                    new XElement("StoreId",storeId),new XElement("SyncKey",syncKey),new XElement("Name",storeName),
                    new XElement("LegalName",storeName),new XElement("Category","Comercio"),new XElement("Address",""),new XElement("City",""),new XElement("Province",""),
                    new XElement("Description","Tienda NexoMarket"),new XElement("Logo",""),new XElement("Slug",Regex.Replace(storeName.ToLowerInvariant(),"[^a-z0-9]+","-").Trim('-')),
                    new XElement("PublicUrl","/store/"+Uri.EscapeDataString(storeId)),new XElement("Active","1"),new XElement("Delivery","1"),new XElement("Pickup","1"),new XElement("Latitude",""),new XElement("Longitude",""));
                stores.Add(store); Save();
            }
            byte[] salt=new byte[16]; using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt);
            string salt64=Convert.ToBase64String(salt); byte[] hash;
            using(var kdf=new Rfc2898DeriveBytes(password,salt,50000))hash=kdf.GetBytes(32);
            Dictionary<string,string> v=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {{"id",Guid.NewGuid().ToString("N")},{"name",name},{"email",email},{"phone",""},{"role","seller"},{"storeId",storeId},{"salt",salt64},{"passwordHash",Convert.ToBase64String(hash)},{"createdAt",DateTime.UtcNow.ToString("o")}};
            string result=AccountUpsert(v,false);
            if(!result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase))
            { Write(stream,200,"text/html; charset=utf-8",AuthPage("Registro","<div class='error'>No se pudo crear la cuenta: "+E(result)+"</div><a class='btn violet' href='/seller-register'>Volver</a>")); return; }
            CentralUser u=FindAccount(email); string token=CreateSessionToken(u); lock(_sync)_sessions[token]=u;
            WriteRedirectCookie(stream,"/seller","NexoCentralSession="+token+"; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax");
        }

        private void CentralRegister(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                StringBuilder form=new StringBuilder();
                form.Append("<form method='post' action='/register'><input name='name' placeholder='Nombre completo' required/><input name='email' type='email' placeholder='Correo electrónico' required/><select name='role'><option value='buyer'>Soy comprador</option><option value='seller'>Soy vendedor</option></select><input name='password' type='password' placeholder='Contraseña (mínimo 6 caracteres)' required/><button class='btn' type='submit'>CREAR CUENTA</button></form><p class='muted'>Para vendedores, NexoMarket crea automáticamente la tienda. No necesitás teléfono ni código de Windows. ¿Ya tenés cuenta? <a href='/login'>Ingresar</a></p>");
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta",form.ToString())); return;
            }
            Dictionary<string,string> f=Form(body); string email=Get(f,"email").Trim().ToLowerInvariant(); string password=Get(f,"password"); string role=Get(f,"role")=="seller"?"seller":"buyer"; string storeId="";
            if(password.Length<6||email.Length<3||email.IndexOf('@')<1) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Completá los datos y usá una contraseña de al menos 6 caracteres.</div><a class='btn' href='/register'>Volver</a>")); return; }
            if(role=="seller")
            {
                if(FindAccount(email)!=null) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Ese correo ya está registrado. Esta identidad no se reemplaza al cambiar de versión.</div><a class='btn' href='/login'>Ingresar</a>")); return; }
                lock(_sync)
                {
                    XElement stores=_doc.Root.Element("Stores");
                    // Web-first: la tienda se crea siempre de forma interna.
                    // El usuario nunca introduce Store ID, código de Windows ni teléfono.
                    storeId=Guid.NewGuid().ToString("N").ToUpperInvariant();
                    string storeName=(Get(f,"name").Trim()+" · Tienda").Trim();
                    if(string.IsNullOrWhiteSpace(storeName)) storeName="Tienda NexoMarket";
                    string syncKey=Guid.NewGuid().ToString("N");
                    XElement store=new XElement("Store",new XAttribute("UpdatedAt",DateTime.UtcNow.ToString("o")),
                        new XElement("StoreId",storeId),new XElement("SyncKey",syncKey),new XElement("Name",storeName),
                        new XElement("LegalName",storeName),new XElement("Category","Comercio"),new XElement("Address",""),
                        new XElement("City",""),new XElement("Province",""),new XElement("Description","Tienda NexoMarket"),
                        new XElement("Logo",""),new XElement("Slug",Regex.Replace(storeName.ToLowerInvariant(),"[^a-z0-9]+","-").Trim('-')),
                        new XElement("PublicUrl","/store/"+Uri.EscapeDataString(storeId)),new XElement("Active","1"),
                        new XElement("Delivery","1"),new XElement("Pickup","1"),new XElement("Latitude",""),new XElement("Longitude",""));
                    stores.Add(store);
                    Save();
                }
            }
            if(FindAccount(email)!=null) { Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>Ese correo ya está registrado.</div><a class='btn' href='/login'>Ingresar</a>")); return; }
            byte[] salt=new byte[16]; using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt); string salt64=Convert.ToBase64String(salt); byte[] hash; using(var kdf=new Rfc2898DeriveBytes(password,salt,50000))hash=kdf.GetBytes(32);
            Dictionary<string,string> v=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",Guid.NewGuid().ToString("N")},{"name",Get(f,"name")},{"email",email},{"phone",""},{"role",role},{"storeId",storeId},{"salt",salt64},{"passwordHash",Convert.ToBase64String(hash)},{"createdAt",DateTime.UtcNow.ToString("o")}};
            string result=AccountUpsert(v, false); if(!result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)){Write(stream,200,"text/html; charset=utf-8",AuthPage("Crear cuenta","<div class='error'>No se pudo registrar la cuenta: "+E(result)+"</div><a class='btn' href='/register'>Volver</a>"));return;}
            CentralUser u=FindAccount(email);
            if(_email!=null) _email.Queue(email,"Bienvenido a NexoMarket","<html><body><h2>Bienvenido a NexoMarket, "+E(u.Name)+"</h2><p>Tu cuenta fue creada correctamente. Ya podés ingresar y comenzar a comprar o administrar tu tienda.</p></body></html>","Bienvenido a NexoMarket, "+u.Name+". Tu cuenta fue creada correctamente.");
            Audit("account_created",storeId,email,u.Id,role);
            string token=CreateSessionToken(u); lock(_sync)_sessions[token]=u; WriteRedirectCookie(stream,u.Role=="seller"?"/seller":"/buyer","NexoCentralSession="+token+"; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax");
        }

        private void CentralForgotPassword(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                string html="<div class='card'><div class='eyebrow'>SEGURIDAD</div><h1>Recuperar contraseña</h1><p class='muted'>Ingresá tu correo y te enviaremos instrucciones si existe una cuenta.</p><form method='post' action='/forgot-password'><input name='email' type='email' placeholder='Correo electrónico' required/><button class='btn' type='submit'>ENVIAR INSTRUCCIONES</button></form><p><a href='/login'>Volver al ingreso</a></p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Recuperar contraseña",html)); return;
            }
            string result=ForgotPassword(Form(body));
            Write(stream,200,"text/html; charset=utf-8",AuthPage("Recuperar contraseña","<div class='card'><h2>Solicitud recibida</h2><p>Si el correo está registrado, recibirás un mensaje con el código de recuperación.</p><a class='btn' href='/reset-password'>RESTABLECER CONTRASEÑA</a></div>"));
        }

        private void CentralResetPassword(NetworkStream stream,string method,string body)
        {
            if(method=="GET")
            {
                string html="<div class='card'><div class='eyebrow'>SEGURIDAD</div><h1>Nueva contraseña</h1><form method='post' action='/reset-password'><input name='email' type='email' placeholder='Correo electrónico' required/><input name='token' placeholder='Código recibido por correo' required/><input name='password' type='password' placeholder='Nueva contraseña (mínimo 8 caracteres)' required/><button class='btn' type='submit'>CAMBIAR CONTRASEÑA</button></form><p><a href='/login'>Volver al ingreso</a></p></div>";
                Write(stream,200,"text/html; charset=utf-8",AuthPage("Restablecer contraseña",html)); return;
            }
            string result=ResetPassword(Form(body));
            Write(stream,200,"text/html; charset=utf-8",AuthPage("Restablecer contraseña",result.StartsWith("OK|")?"<div class='card'><h2>Contraseña actualizada</h2><p>Ya podés ingresar con tu nueva contraseña.</p><a class='btn' href='/login'>INGRESAR</a></div>":"<div class='card'><h2>No se pudo actualizar</h2><p>El código es inválido, venció o los datos no son correctos.</p><a class='btn' href='/reset-password'>Intentar nuevamente</a></div>"));
        }

        private void CentralLogout(NetworkStream stream)
        {
            WriteRedirectCookie(stream,"/","NexoCentralSession=deleted; Path=/; Max-Age=0; HttpOnly; SameSite=Lax");
        }

        private string CreateSessionToken(CentralUser u)
        {
            if(u==null || string.IsNullOrWhiteSpace(u.Email)) return Guid.NewGuid().ToString("N");
            string payload=u.Email.Trim().ToLowerInvariant();
            string material="NexoMarketSession|"+payload+"|"+(u.PasswordHash??"");
            using(SHA256 sha=SHA256.Create())
            {
                byte[] h=sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                string sig=Convert.ToBase64String(h).TrimEnd('=').Replace('+','-').Replace('/','_');
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+','-').Replace('/','_')+"."+sig;
            }
        }

        private CentralUser SessionUser(string cookie)
        {
            if(string.IsNullOrWhiteSpace(cookie))return null;
            lock(_sync)
            {
                CentralUser u;
                if(_sessions.TryGetValue(cookie,out u) && u!=null) return u;
            }
            try
            {
                string[] parts=cookie.Split('.'); if(parts.Length!=2)return null;
                string encoded=parts[0].Replace('-','+').Replace('_','/'); while(encoded.Length%4!=0)encoded+="=";
                string email=Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                CentralUser u=FindAccount(email); if(u==null || !string.Equals(u.Role,"seller",StringComparison.OrdinalIgnoreCase))return null;
                string expected=CreateSessionToken(u);
                return string.Equals(expected,cookie,StringComparison.Ordinal) ? u : null;
            }
            catch{return null;}
        }

        private string GetStoreName(string storeId)
        {
            if (string.IsNullOrWhiteSpace(storeId)) return "Sin tienda";
            lock(_sync)
            {
                XElement stores=_doc.Root.Element("Stores");
                XElement store=stores==null?null:stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(storeId),StringComparison.OrdinalIgnoreCase));
                return store==null?"Sin tienda":S(store,"Name");
            }
        }

        private bool IsStoreActive(string storeId)
        {
            lock(_sync)
            {
                XElement stores=_doc.Root.Element("Stores");
                XElement store=stores==null?null:stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(storeId),StringComparison.OrdinalIgnoreCase));
                return store!=null && S(store,"Active")!="0";
            }
        }

        private void CentralSeller(NetworkStream stream,string cookie,string query)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            _sellerRenderStoreId=u.StoreId??"";
            TouchStoreActivity(u.StoreId);
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
            b.Append("<header class='sc-top'><div class='brand'><span>NEXO</span>MARKET <small>SELLER CENTER</small></div><div class='top-actions'><a href='/' class='btn ghost'>Tiendas</a><a href='/store/"+Uri.EscapeDataString(u.StoreId??"")+"' class='btn ghost'>Mi tienda</a><form method='post' action='/seller/store/toggle' class='store-toggle'><input type='hidden' name='active' value='"+(IsStoreActive(u.StoreId)?"0":"1")+"'/><button class='btn "+(IsStoreActive(u.StoreId)?"danger-outline":"violet")+"' type='submit'>"+(IsStoreActive(u.StoreId)?"CERRAR TIENDA":"ABRIR TIENDA")+"</button></form><a href='/logout' class='btn ghost'>Salir</a></div></header>");
            bool storeOpen = IsStoreActive(u.StoreId);
            b.Append("<aside class='sc-side'><div class='account-box'><div class='avatar'>"+E((u.Name??"V").Length>0?(u.Name??"V").Substring(0,1).ToUpperInvariant():"V")+"</div><b>"+E(u.Name)+"</b><small>"+E(u.Email)+"</small><small class='store-account-name'>TIENDA: "+E(GetStoreName(u.StoreId))+"</small></div><form method='post' action='/seller/store/toggle' class='side-store-status'><input type='hidden' name='active' value='"+(storeOpen?"0":"1")+"'/><button type='submit' class='store-status-btn "+(storeOpen?"is-open":"is-closed")+"'><span class='status-dot'></span><span><b>TIENDA</b><small>"+(storeOpen?"ABIERTA":"CERRADA")+"</small></span><strong>"+(storeOpen?"CERRAR":"ABRIR")+"</strong></button></form>");
            b.Append(SellerLink("Resumen","",view)+SellerLink("Tienda Online","online",view)+SellerLink("Pedidos","orders",view)+SellerLink("Deliveries","deliveries",view)+SellerLink("Punto de venta","pos",view)+SellerLink("Productos e inventario","products",view)+SellerLink("Clientes","customers",view)+SellerLink("Analítica","analytics",view)+SellerLink("Finanzas y caja","finance",view)+SellerLink("Marketing","marketing",view)+SellerLink("Reputación","reputation",view)+SellerLink("Herramientas","tools",view)+SellerLink("Configuración","settings",view)+"</aside>");
            b.Append("<main class='sc-main'><button id='soundUnlock' class='sound-unlock show' type='button'>🔔 ACTIVAR SONIDO</button><div id='notifySetup' class='notify-setup warn'><div><b>Alertas de pedidos</b> · Activá las notificaciones para recibir avisos aunque cambies de pestaña.</div><button id='enableNotifications' class='btn violet' type='button'>ACTIVAR NOTIFICACIONES</button></div><div id='newOrderAlert' class='new-order-alert' role='alert' aria-live='assertive'><div class='oa-title'>🔴 NUEVO PEDIDO RECIBIDO</div><div class='oa-sub'>Tenés un pedido nuevo pendiente. Revisá la sección Pedidos.</div></div>");
            b.Append("<div class='welcome'><div><span class='eyebrow'>CENTRAL DE VENTAS</span><h1>Hola, "+E(u.Name)+" 👋</h1><p>Tu Seller Center y NexoMarket Windows usan la misma cuenta. Los cambios se sincronizan automáticamente.</p><div class='section-actions'><a class='btn ghost' href='/seller?view=products'>"+SellerIcon("Productos")+"PRODUCTOS</a><a class='btn ghost' href='/seller?view=orders'>"+SellerIcon("Pedidos")+"PEDIDOS</a></div></div><div class='account-mini'><b>CUENTA</b><strong>"+E(u.Email)+"</strong><small>TIENDA: "+E(GetStoreName(u.StoreId))+"</small><small>Windows + Web sincronizados</small></div></div>");
            int pending=orders.Count(x=>S(x,"Status")=="Pendiente"); int delivery=orders.Count(x=>(S(x,"Fulfillment")=="Delivery"||S(x,"Fulfillment")=="En reparto")&&S(x,"Status")!="Entregado"&&S(x,"Status")!="Cancelado");
            decimal sales=orders.Where(x=>S(x,"Status")!="Cancelado").Sum(x=>Money(S(x,"Total"))); int low=products.Count(x=>Money(S(x,"Stock"))<=Money(S(x,"MinimumStock"))); int customers=orders.Select(x=>S(x,"CustomerEmail").Trim().ToLowerInvariant()).Where(x=>x.Length>0).Distinct().Count();
            b.Append("<div class='kpis'>"+KpiC("Ventas", "$ "+sales.ToString("N2"), "operaciones válidas", "green")+KpiC("Pedidos pendientes", pending.ToString(), "requieren atención", pending>0?"yellow":"green")+KpiC("Productos", products.Count.ToString(), low+" con stock bajo", low>0?"red":"green")+KpiC("Clientes", customers.ToString(), "compradores únicos", "green")+KpiC("Delivery", delivery.ToString(), "entregas abiertas", delivery>0?"yellow":"green")+"</div>");
            if(view=="orders") b.Append(SellerOrdersView(orders));
            else if(view=="deliveries") b.Append(SellerDeliveryView(orders));
            else if(view=="pos") b.Append(SellerPosView(products));
            else if(view=="products") b.Append(SellerProductsView(products));
            else if(view=="customers") b.Append(SellerCustomersView(orders));
            else if(view=="analytics") b.Append(SellerAnalyticsView(orders,products));
            else if(view=="finance") b.Append(SellerFinanceView(orders));
            else if(view=="marketing") { _sellerRenderStoreId=u.StoreId??""; b.Append(SellerMarketingView(promotions)); }
            else if(view=="reputation") b.Append(SellerReputationView(orders));
            else if(view=="tools") b.Append(SellerToolsView(u,products,orders));
            else if(view=="settings") b.Append(SellerSettingsView(u));
            else if(view=="online") b.Append(SellerOnlineSettingsView(u));
            else if(view=="devices") b.Append("<section class='card'><div class='eyebrow'>CUENTA WINDOWS</div><h2>Conexión automática</h2><p>Windows utiliza la misma cuenta del Seller Center. No hay que copiar códigos de vinculación.</p></section>");
            else b.Append(SellerSummaryView(orders,products));
            b.Append("<script>(function(){var last='',lastPending=-1,lastOrderAt='',audioCtx=null,audioReady=false;function ensureAudio(){try{var C=window.AudioContext||window.webkitAudioContext;if(!C)return false;audioCtx=audioCtx||new C();if(audioCtx.state==='suspended')audioCtx.resume();audioReady=true;var u=document.getElementById('soundUnlock');if(u)u.className='sound-unlock';return true;}catch(e){return false;}}function beep(){try{if(!audioReady&&!ensureAudio())return;var o=audioCtx.createOscillator(),g=audioCtx.createGain();o.type='sine';o.frequency.setValueAtTime(880,audioCtx.currentTime);o.frequency.linearRampToValueAtTime(1175,audioCtx.currentTime+.12);g.gain.setValueAtTime(.11,audioCtx.currentTime);g.gain.exponentialRampToValueAtTime(.001,audioCtx.currentTime+.30);o.connect(g);g.connect(audioCtx.destination);o.start();o.stop(audioCtx.currentTime+.31);}catch(e){}}function notifySetup(){var el=document.getElementById('notifySetup'),btn=document.getElementById('enableNotifications');if(!el||!btn)return;var state=('Notification' in window)?Notification.permission:'unsupported';if(state==='granted'){el.className='notify-setup ok';btn.textContent='✓ NOTIFICACIONES ACTIVAS';btn.disabled=true;}else if(state==='denied'){el.className='notify-setup warn';btn.textContent='PERMISOS BLOQUEADOS';}else{btn.textContent='ACTIVAR NOTIFICACIONES';}}async function enableNotifications(){ensureAudio();if('Notification' in window){try{await Notification.requestPermission();}catch(e){}}notifySetup();}function osNotify(){if(!('Notification' in window)||Notification.permission!=='granted')return;try{var n=new Notification('NexoMarket · Nuevo pedido',{body:'Tenés un pedido nuevo pendiente. Abrí el Seller Center para revisarlo.',tag:'nexo-new-order',requireInteraction:true});n.onclick=function(){try{window.focus();}catch(e){}try{n.close();}catch(e){}};}catch(e){}}function showNewOrderAlert(){var el=document.getElementById('newOrderAlert');if(!el)return;el.className='new-order-alert show';clearTimeout(window.__nexoOrderAlertTimer);clearInterval(window.__nexoOrderBeepTimer);var n=0;beep();n=1;try{if(navigator.vibrate)navigator.vibrate([180,90,180]);}catch(e){}osNotify();window.__nexoOrderBeepTimer=setInterval(function(){if(n>=10){clearInterval(window.__nexoOrderBeepTimer);return;}beep();n++;try{if(navigator.vibrate)navigator.vibrate(120);}catch(e){}},1000);window.__nexoOrderAlertTimer=setTimeout(function(){el.className='new-order-alert';clearInterval(window.__nexoOrderBeepTimer);},10000);}function live(){var x=new XMLHttpRequest();x.open('GET','/api/seller/live',true);x.onreadystatechange=function(){if(x.readyState===4&&x.status===200){try{var d=JSON.parse(x.responseText),v=d.updatedAt||'',pending=parseInt(d.pendingOrders||0,10),orderAt=d.latestOrderAt||'';if(lastOrderAt&&orderAt&&orderAt!==lastOrderAt)showNewOrderAlert();else if(lastPending>=0&&pending>lastPending)showNewOrderAlert();lastOrderAt=orderAt;lastPending=pending;var editing=document.querySelector('form.product-form input:focus,form.product-form textarea:focus,form.edit-form input:focus,form.edit-form textarea:focus,form.settings-form input:focus,form.settings-form textarea:focus');var hasDraft=document.querySelector('form.product-form input:not([type=hidden])[value]:not([value=\"\"])')||document.querySelector('form.product-form textarea:not(:placeholder-shown)');if(last&&v!==last&&!editing&&!document.querySelector('details[open]')&&!hasDraft&&!document.querySelector('.order-cards'))location.reload();last=v;}catch(e){}}};x.send();}var nbtn=document.getElementById('enableNotifications');if(nbtn)nbtn.addEventListener('click',enableNotifications);var sun=document.getElementById('soundUnlock');if(sun)sun.addEventListener('click',function(){ensureAudio();beep();});document.addEventListener('pointerdown',function(){ensureAudio();},{once:true,passive:true});setTimeout(function(){notifySetup();if('Notification' in window&&Notification.permission==='default'){try{Notification.requestPermission().then(notifySetup).catch(function(){});}catch(e){}}},650);setTimeout(live,900);setInterval(live,2500);})();</script>");
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
        private string SellerOrderDetail(string cookie,string id)
        {
            CentralUser u=SessionUser(cookie);if(u==null||u.Role!="seller")return "{\"error\":\"unauthorized\"}";id=(id??"").Trim();lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement o=d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==u.StoreId&&S(x,"CentralOrderId")==id);if(o==null)return "{\"error\":\"notfound\"}";StringBuilder b=new StringBuilder("{\"centralOrderId\":"+JsonString(id)+",\"status\":"+JsonString(S(o,"Status"))+",\"customerName\":"+JsonString(S(o,"CustomerName"))+",\"customerEmail\":"+JsonString(S(o,"CustomerEmail"))+",\"phone\":"+JsonString(S(o,"Phone"))+",\"fulfillment\":"+JsonString(S(o,"Fulfillment"))+",\"address\":"+JsonString(S(o,"Address"))+",\"notes\":"+JsonString(S(o,"Notes"))+",\"buyerMessage\":"+JsonString(S(o,"BuyerMessage"))+",\"total\":"+JsonString(S(o,"Total"))+",\"paymentMethod\":"+JsonString(S(o,"PaymentMethod"))+",\"paymentStatus\":"+JsonString(S(o,"PaymentStatus"))+",\"paymentProofPath\":"+JsonString(S(o,"PaymentProofPath"))+",\"items\":[");bool first=true;string items=S(o,"ItemsJson");foreach(Match m in Regex.Matches(items??"", @"""id""\s*:\s*""([^""]+)""[^}]*?""name""\s*:\s*""([^""]*)""(?:[^}]*?""note""\s*:\s*""([^""]*)"")?[^}]*?""price""\s*:\s*([0-9.,-]+)[^}]*?""qty""\s*:\s*(\d+)", RegexOptions.IgnoreCase)){string pid=m.Groups[1].Value,pname=m.Groups[2].Value,note=m.Groups[3].Value,price=m.Groups[4].Value,qty=m.Groups[5].Value;int stock=0;XDocument cd=LoadFile(_catalogFile,"NexoMarketCatalog","Products");XElement p=cd.Root.Element("Products")==null?null:cd.Root.Element("Products").Elements("Product").FirstOrDefault(x=>S(x,"StoreId")==u.StoreId&&S(x,"ProductId")==pid);if(p!=null)int.TryParse(S(p,"Stock"),out stock);if(!first)b.Append(',');first=false;b.Append("{\"id\":").Append(JsonString(pid)).Append(",\"name\":").Append(JsonString(pname)).Append(",\"note\":").Append(JsonString(note)).Append(",\"price\":").Append(JsonString(price)).Append(",\"qty\":").Append(qty).Append(",\"stock\":").Append(stock.ToString(CultureInfo.InvariantCulture)).Append(",\"stockAvailable\":").Append(p!=null?"true":"false").Append('}');}return b.Append("]}").ToString();}
        }
        private string SellerApproveOrder(string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie);if(u==null||u.Role!="seller")return "{\"ok\":false,\"message\":\"Sesión no válida.\"}";string id=Get(f,"id").Trim();if(id.Length==0)return "{\"ok\":false,\"message\":\"Pedido inválido.\"}";lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement o=d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==u.StoreId&&S(x,"CentralOrderId")==id);if(o==null)return "{\"ok\":false,\"message\":\"Pedido no encontrado.\"}";if(S(o,"Status")!="Pendiente")return "{\"ok\":true,\"message\":\"El pedido ya fue procesado.\"}";o.SetElementValue("Status","Preparando");o.SetElementValue("ApprovedAt",DateTime.UtcNow.ToString("o"));o.SetElementValue("ApprovalNote",Get(f,"note"));o.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));SaveDoc(_ordersFile,d);XElement changed=new XElement(o);Audit("order_approved",u.StoreId,u.Email,id,Get(f,"note"));QueueOrderEmails(changed,"pedido aprobado");return "{\"ok\":true,\"approved\":true,\"message\":\"Pedido aprobado correctamente.\"}";}
        }
        private string SellerOrdersView(List<XElement> orders)
        {
            StringBuilder b=new StringBuilder();
            b.Append("<div class='section-title'><div><span class='eyebrow'>OPERACIONES</span><h2>Pedidos y estados</h2><p>Doble clic o clic derecho sobre un pedido para ver el detalle completo, revisar stock y aprobarlo.</p></div><span class='sync-pill'>● PEDIDOS CENTRALIZADOS</span></div>");
            b.Append("<section class='card'><div class='inventory-toolbar'><input id='orderSearch' placeholder='Buscar pedido, cliente o correo...' oninput='filterOrders()'/><select id='orderStatusFilter' onchange='filterOrders()'><option value='all'>Todos los estados</option><option>Pendiente</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Rechazado</option><option>Cancelado</option></select></div><div class='order-cards' id='sellerOrderCards'>");
            foreach(XElement o in orders)
            {
                string id=S(o,"CentralOrderId"), searchable=(id+" "+S(o,"CustomerName")+" "+S(o,"CustomerEmail")).ToLowerInvariant(), status=S(o,"Status");
                string proof=S(o,"PaymentProofPath"); string proofHtml=string.IsNullOrWhiteSpace(proof)||proof=="POS"?"":"<a class='btn small' href='"+E(proof)+"' target='_blank'>📎 VER COMPROBANTE</a>";
                string items=E(S(o,"ItemsJson"));
                b.Append("<article class='order-card order-interactive' tabindex='0' id='order-").Append(E(id)).Append("' data-order-id='").Append(E(id)).Append("' data-items='").Append(items).Append("' data-search='").Append(E(searchable)).Append("' data-status='").Append(E(status)).Append("'><div><span class='eyebrow'>PEDIDO</span><h3>#").Append(E(id.Length>10?id.Substring(0,10):id)).Append("</h3><small>").Append(E(S(o,"CreatedAt"))).Append(" · ").Append(E(S(o,"Fulfillment"))).Append("</small></div><div><b>").Append(E(S(o,"CustomerName"))).Append("</b><small>").Append(E(S(o,"CustomerEmail"))).Append("</small><small>").Append(E(S(o,"Phone"))).Append(" · 📍 ").Append(E(S(o,"Address"))).Append("</small>").Append(proofHtml).Append("</div><div><strong>$ ").Append(Money(S(o,"Total")).ToString("N2")).Append("</strong><div class='order-status-badges'>").Append(BadgeC(status)).Append(" · ").Append(BadgeC(S(o,"PaymentStatus"))).Append("</div></div><form method='post' action='/seller/order-status' class='inline-form order-status-form' onsubmit='return updateSellerOrderStatus(this)'><input type='hidden' name='id' value='").Append(E(id)).Append("'/><select name='status' aria-label='Estado del pedido'><option>").Append(E(status)).Append("</option><option>Pendiente</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Rechazado</option><option>Cancelado</option></select><button class='btn small' type='submit'>ACTUALIZAR</button><button class='btn small approve-btn' type='button' onclick='approveOrder(\"").Append(E(id)).Append("\")' ").Append(status=="Pendiente"?"":"style='display:none'").Append(">✓ APROBAR PEDIDO</button><span class='order-saving' aria-live='polite'></span></form></article>");
            }
            if(orders.Count==0)b.Append("<div class='empty-inventory'>No hay pedidos sincronizados.</div>");
            b.Append("</div></section><div id='orderDetailModal' class='order-modal' aria-hidden='true'><div class='order-modal-card'><div class='order-modal-head'><div><span class='eyebrow'>DETALLE DEL PEDIDO</span><h2 id='odTitle'>Pedido</h2></div><button class='btn ghost small' type='button' onclick='closeOrderDetail()'>CERRAR</button></div><div id='odBody' class='order-detail-body'>Cargando...</div><div class='order-modal-actions'><button id='odApprove' class='btn violet' type='button'>✓ APROBAR PEDIDO</button><button id='odReject' class='btn danger-outline' type='button'>RECHAZAR / CANCELAR</button></div></div></div><script>function filterOrders(){var q=(document.getElementById('orderSearch').value||'').toLowerCase(),f=document.getElementById('orderStatusFilter').value;document.querySelectorAll('.order-card').forEach(function(c){c.style.display=(c.getAttribute('data-search').indexOf(q)>=0&&(f==='all'||c.getAttribute('data-status')===f))?'grid':'none';});}function updateSellerOrderStatus(form){var card=form.closest('.order-card'),select=form.querySelector('select[name=status]'),btn=form.querySelector('button[type=submit]'),msg=form.querySelector('.order-saving'),status=select.value;btn.disabled=true;msg.textContent='Guardando...';var data='id='+encodeURIComponent(form.querySelector('input[name=id]').value)+'&status='+encodeURIComponent(status);fetch(form.action,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8'},body:data,credentials:'same-origin'}).then(function(r){return r.text().then(function(t){return {ok:r.ok,text:t};});}).then(function(x){var d;try{d=JSON.parse(x.text);}catch(e){d=null;}if(!x.ok||!d||!d.ok){msg.textContent=(d&&d.message)||'No se pudo actualizar';btn.disabled=false;return;}card.setAttribute('data-status',status);var badges=card.querySelector('.order-status-badges');if(badges){var cls=(status==='Cancelado'||status==='Rechazado')?'red':(status==='Pendiente')?'yellow':'green';var pay=badges.innerHTML.split(' · ')[1]||'';badges.innerHTML='<span class=\"badge '+cls+'\">'+status.replace(/&/g,'&amp;').replace(/</g,'&lt;')+'</span> · '+pay;}var ap=card.querySelector('.approve-btn');if(ap)ap.style.display=status==='Pendiente'?'inline-block':'none';msg.textContent='Actualizado';btn.disabled=false;setTimeout(function(){msg.textContent='';},1800);filterOrders();}).catch(function(){msg.textContent='Error de conexión';btn.disabled=false;});return false;}function closeOrderDetail(){var m=document.getElementById('orderDetailModal');m.classList.remove('open');m.setAttribute('aria-hidden','true');}function esc(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\"/g,'&quot;').replace(/'/g,'&#39;');}function openOrderDetail(card){var id=card.getAttribute('data-order-id'),m=document.getElementById('orderDetailModal');m.classList.add('open');m.setAttribute('aria-hidden','false');document.getElementById('odTitle').textContent='#'+id;document.getElementById('odBody').innerHTML='Consultando pedido...';fetch('/seller/order-detail?id='+encodeURIComponent(id),{credentials:'same-origin'}).then(function(r){return r.json();}).then(function(d){if(d.error){document.getElementById('odBody').innerHTML='<div class=\"empty-inventory\">No se pudo consultar el pedido.</div>';return;}var h='<div class=\"od-grid\"><div><b>Cliente</b><span>'+esc(d.customerName)+'</span><span>'+esc(d.customerEmail)+'</span><span>'+esc(d.phone)+'</span></div><div><b>Entrega</b><span>'+esc(d.fulfillment)+'</span><span>'+esc(d.address)+'</span><span>'+esc(d.notes)+'</span></div><div><b>Pago</b><span>'+esc(d.paymentMethod)+' · '+esc(d.paymentStatus)+'</span>'+(d.paymentProofPath?'<a class=\"btn small\" target=\"_blank\" href=\"'+esc(d.paymentProofPath)+'\">📎 Ver comprobante</a>':'')+'</div><div><b>Total</b><strong>$ '+esc(d.total)+'</strong></div></div><h3>Productos solicitados</h3><div class=\"od-items\">';(d.items||[]).forEach(function(i){h+='<div class=\"od-item\"><div><b>'+esc(i.name)+'</b><small>ID '+esc(i.id)+' · Cantidad: '+esc(i.qty)+'</small>'+(i.note?'<small class=\"item-order-note\">📝 Nota: '+esc(i.note)+'</small>':'')+'</div><div><b>$ '+esc(i.price)+'</b><small>Stock actual: '+esc(i.stock)+' · '+(i.stockAvailable?'RESERVADO':'REVISAR STOCK')+'</small></div></div>';});h+='</div><div class=\"od-message\">'+(d.buyerMessage?('💬 '+esc(d.buyerMessage)):'')+'</div>';document.getElementById('odBody').innerHTML=h;var ab=document.getElementById('odApprove');ab.style.display=d.status==='Pendiente'?'inline-block':'none';ab.onclick=function(){approveOrder(id);};document.getElementById('odReject').onclick=function(){rejectOrder(id);};}).catch(function(){document.getElementById('odBody').innerHTML='<div class=\"empty-inventory\">Error de conexión.</div>';});}function approveOrder(id){var note=prompt('Confirmá la aprobación del pedido. Podés dejar una nota interna opcional:','');if(note===null)return;var data='id='+encodeURIComponent(id)+'&note='+encodeURIComponent(note||'');fetch('/seller/order-approve',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8'},credentials:'same-origin',body:data}).then(function(r){return r.json();}).then(function(d){if(!d.ok){alert(d.message||'No se pudo aprobar.');return;}alert('Pedido aprobado. El comprador recibirá la señal verde.');closeOrderDetail();location.reload();}).catch(function(){alert('Error de conexión al aprobar el pedido.');});}function rejectOrder(id){var reason=prompt('Motivo del rechazo/cancelación:','Sin stock');if(!reason)return;var data='id='+encodeURIComponent(id)+'&reason='+encodeURIComponent(reason)+'&status=Cancelado';fetch('/seller/order-status',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8'},credentials:'same-origin',body:data}).then(function(r){return r.json();}).then(function(d){if(!d.ok){alert(d.message||'No se pudo cancelar.');return;}closeOrderDetail();location.reload();}).catch(function(){alert('Error de conexión.');});}document.querySelectorAll('.order-interactive').forEach(function(c){c.addEventListener('dblclick',function(){openOrderDetail(c);});c.addEventListener('contextmenu',function(e){e.preventDefault();openOrderDetail(c);});c.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();openOrderDetail(c);}});});</script>");
            return b.ToString();
        }
        private string SellerProductsView(List<XElement> products)
        {
            StringBuilder b=new StringBuilder();
            b.Append("<div class='section-title'><div><span class='eyebrow'>CATÁLOGO CENTRAL</span><h2>Productos e inventario</h2><p>Primero elegí <b>NUEVO PRODUCTO</b>. La ficha se abre sólo cuando la necesitás y cada campo explica qué dato corresponde.</p></div><div class='section-actions'><button class='btn violet' type='button' onclick=\"toggleNewProduct()\">＋ NUEVO PRODUCTO</button><span class='sync-pill'>● CENTRAL EN TIEMPO REAL</span></div></div>");
            b.Append("<section class='card' id='newProductPanel' style='display:none'><div class='section-title'><div><span class='eyebrow'>ALTA DE PRODUCTO</span><h3>Nuevo producto</h3><p>Completá los datos básicos. Los textos grises son ayudas y no se guardan como contenido.</p></div><button class='btn ghost small' type='button' onclick=\"toggleNewProduct(false)\">CERRAR</button></div>");
            b.Append("<form method='post' action='/seller/products/save' class='product-form' id='newProductForm' autocomplete='off'><input type='hidden' name='imageUrl' id='newImageUrl'/><input type='hidden' name='videoUrl' id='newVideoUrl'/><div class='form-grid'>");
            b.Append("<label>Nombre del producto<input id='productName' name='name' placeholder='Ej.: Coca Cola 2,25 L' title='Nombre comercial que verá el cliente' required/></label><label>Categoría<input name='category' placeholder='Ej.: Bebidas' title='Grupo al que pertenece el producto'/></label><label>Marca<input name='brand' placeholder='Ej.: Coca Cola' title='Marca o fabricante'/></label><label>SKU<input name='sku' placeholder='Ej.: BEB-001' title='Código interno único del producto'/></label><label>Código de barras<input name='barcode' placeholder='Ej.: 7790895000012' title='EAN/UPC o código que usás para escanear'/></label><label>Precio<input name='price' placeholder='Ej.: 2500,00' title='Precio normal de venta' value='0'/></label><label>Precio de oferta<input name='salePrice' placeholder='Ej.: 2200,00' title='Precio promocional; dejá 0 si no aplica' value='0'/></label><label>Costo<input name='cost' placeholder='Ej.: 1500,00' title='Costo de compra para calcular rentabilidad' value='0'/></label><label>Stock actual<input name='stock' placeholder='Ej.: 25' title='Cantidad disponible ahora' value='0'/></label><label>Stock mínimo<input name='minimumStock' placeholder='Ej.: 5' title='Cantidad a partir de la cual se considera stock bajo' value='0'/></label><label>Talle / tamaño<input name='size' placeholder='Ej.: M, 500 ml, 42' title='Talle, volumen o tamaño; opcional'/></label><label>Color<input name='color' placeholder='Ej.: Negro' title='Color o variante; opcional'/></label></div>");
            b.Append("<div class='media-pickers'><label class='upload-box'><span>📷</span><b>Imagen principal</b><small>En Android se abre el selector de imágenes del sistema para elegir Galería/Fotos/Archivos. No se fuerza la cámara.</small><input id='newImageFile' type='file' accept='image/*'/></label><div class='media-preview' id='newMediaPreview'><span>La vista previa aparecerá aquí</span></div></div>");
            b.Append("<textarea name='description' placeholder='Descripción interna · Ej.: proveedor, observaciones, ubicación en depósito' title='Notas internas que no necesariamente verá el comprador'></textarea><textarea name='publicDescription' placeholder='Descripción pública · Ej.: bebida sin azúcar, presentación 2,25 L' title='Texto que verá el comprador en la tienda web'></textarea><div class='form-checks'><label><input type='checkbox' name='onlineEnabled' value='1' checked/> Publicar online</label><label><input type='checkbox' name='active' value='1' checked/> Activo</label></div><div class='form-actions'><span id='uploadState' class='muted'>Sin imagen todavía.</span><button class='btn' id='saveProductBtn' type='submit'>GUARDAR PRODUCTO</button></div></form></section>");
            b.Append("<section class='card'><div class='section-title inventory-head'><div><span class='eyebrow'>INVENTARIO</span><h3>Vista de productos</h3></div><span class='muted'>").Append(products.Count).Append(" productos</span></div><div class='inventory-toolbar'><input id='inventorySearch' placeholder='Buscar por nombre, SKU, categoría o código de barras...' oninput=\"filterInventory()\"/><select id='inventoryStockFilter' onchange=\"filterInventory()\"><option value='all'>Todo el stock</option><option value='low'>Stock bajo</option><option value='zero'>Sin stock</option></select></div><div class='inventory-grid' id='inventoryGrid'>");
            foreach(XElement x in products)
            {
                string pid=S(x,"ProductId"), img=ProductImageUrl(x); decimal stock=Money(S(x,"Stock")), min=Money(S(x,"MinimumStock")); string price=Money(S(x,"SalePrice")=="0"?S(x,"Price"):S(x,"SalePrice")).ToString("N2");
                string image="<img src='"+E(img)+"' alt='"+E(S(x,"Name"))+"' loading='lazy' decoding='async' onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex'\"/><div class='no-image' style='display:none'>SIN<br/>FOTO</div>";
                b.Append("<article class='inventory-card' data-name='").Append(E((S(x,"Name")+" "+S(x,"SKU")+" "+S(x,"Category")+" "+S(x,"Barcode")).ToLowerInvariant())).Append("' data-stock='").Append(stock<=0?"zero":stock<=min?"low":"ok").Append("'><div class='inventory-photo'>").Append(image).Append("</div><div class='inventory-name'>").Append(E(S(x,"Name"))).Append("</div><div class='inventory-meta'>").Append(E(S(x,"Category"))).Append(" · SKU ").Append(E(S(x,"SKU"))).Append(" · CB ").Append(E(S(x,"Barcode"))).Append("</div><div class='inventory-bottom'><span class='inventory-stock ").Append(stock<=min?"low":"").Append("'>Stock ").Append(stock.ToString("N0")).Append("</span><strong>$ ").Append(price).Append("</strong></div><div class='card-actions'><details><summary class='btn small'>EDITAR</summary><form method='post' action='/seller/products/save' class='edit-form'><input type='hidden' name='id' value='").Append(E(pid)).Append("'/><input type='hidden' name='imageUrl' value='").Append(E(S(x,"WebImageUrl"))).Append("'/><div class='form-grid'>");
                b.Append("<label>Nombre<input name='name' value='").Append(E(S(x,"Name"))).Append("' placeholder='Nombre del producto' required/></label><label>Categoría<input name='category' value='").Append(E(S(x,"Category"))).Append("' placeholder='Categoría'/></label><label>Marca<input name='brand' value='").Append(E(S(x,"Brand"))).Append("' placeholder='Marca'/></label><label>SKU<input name='sku' value='").Append(E(S(x,"SKU"))).Append("' placeholder='SKU'/></label><label>Código de barras<input name='barcode' value='").Append(E(S(x,"Barcode"))).Append("' placeholder='Código de barras'/></label><label>Precio<input name='price' value='").Append(E(S(x,"Price"))).Append("' placeholder='Precio'/></label><label>Precio oferta<input name='salePrice' value='").Append(E(S(x,"SalePrice"))).Append("' placeholder='Precio oferta'/></label><label>Costo<input name='cost' value='").Append(E(S(x,"Cost"))).Append("' placeholder='Costo'/></label><label>Stock<input name='stock' value='").Append(E(S(x,"Stock"))).Append("' placeholder='Stock'/></label><label>Stock mínimo<input name='minimumStock' value='").Append(E(S(x,"MinimumStock"))).Append("' placeholder='Stock mínimo'/></label><label>Talle/tamaño<input name='size' value='").Append(E(S(x,"Size"))).Append("' placeholder='Talle / tamaño'/></label><label>Color<input name='color' value='").Append(E(S(x,"Color"))).Append("' placeholder='Color'/></label></div>");
                b.Append("<label class='upload-box compact'><span>📷</span><b>Cambiar imagen</b><small>En Android podés elegir desde Galería/Fotos/Archivos. No se fuerza la cámara.</small><input class='editImageFile' type='file' accept='image/*'/></label><textarea name='description' placeholder='Descripción interna'>").Append(E(S(x,"Description"))).Append("</textarea><textarea name='publicDescription' placeholder='Descripción pública'>").Append(E(S(x,"PublicDescription"))).Append("</textarea><div class='form-checks'><label><input type='checkbox' name='onlineEnabled' value='1' ").Append(S(x,"OnlineEnabled")!="0"?"checked":"").Append("/> Publicar online</label><label><input type='checkbox' name='active' value='1' ").Append(S(x,"Active")!="0"?"checked":"").Append("/> Activo</label></div><button class='btn small' type='submit'>GUARDAR CAMBIOS</button></form></details></div></article>");
            }
            if(products.Count==0)b.Append("<div class='empty-inventory'>No hay productos. Presioná <b>NUEVO PRODUCTO</b> para crear el primero.</div>");
            b.Append("</div></section><script>function toggleNewProduct(force){var p=document.getElementById('newProductPanel');if(typeof force==='boolean')p.style.display=force?'block':'none';else{p.style.display=p.style.display==='none'?'block':'none';}if(p.style.display!=='none'){setTimeout(function(){var n=document.getElementById('productName');if(n)n.focus();},80);}}function filterInventory(){var q=(document.getElementById('inventorySearch').value||'').toLowerCase(),f=document.getElementById('inventoryStockFilter').value;document.querySelectorAll('#inventoryGrid .inventory-card').forEach(function(c){var ok=c.getAttribute('data-name').indexOf(q)>=0&&(f==='all'||c.getAttribute('data-stock')===f);c.style.display=ok?'block':'none';});}function imageForUpload(file){return new Promise(function(resolve,reject){if(!file){resolve(null);return}if(file.type.indexOf('image/')!==0){reject(new Error('El archivo seleccionado no es una imagen.'));return}var max=1400,r=new FileReader();r.onload=function(){var im=new Image();im.onload=function(){var w=im.naturalWidth,h=im.naturalHeight;if(w>max||h>max){var sc=Math.min(max/w,max/h);w=Math.max(1,Math.round(w*sc));h=Math.max(1,Math.round(h*sc));}var c=document.createElement('canvas');c.width=w;c.height=h;var ctx=c.getContext('2d');ctx.drawImage(im,0,0,w,h);var q=.86,data=c.toDataURL('image/jpeg',q);while(data.length>2200000&&q>.50){q-=.06;data=c.toDataURL('image/jpeg',q);}var raw=atob(data.split(',')[1]),arr=new Uint8Array(raw.length);for(var i=0;i<raw.length;i++)arr[i]=raw.charCodeAt(i);resolve(new Blob([arr],{type:'image/jpeg'}));};im.onerror=function(){reject(new Error('El navegador no pudo leer la imagen.'));};im.src=r.result;};r.onerror=function(){reject(new Error('No se pudo leer el archivo.'));};r.readAsDataURL(file);});}function toBase64Url(buf){var bytes=new Uint8Array(buf),bin='';for(var i=0;i<bytes.length;i++)bin+=String.fromCharCode(bytes[i]);return btoa(bin).replace(/\\+/g,'-').replace(/\\//g,'_').replace(/=+$/,'');}function uploadSellerImage(file){return new Promise(async function(resolve,reject){try{if(!file){resolve('');return}var blob=await imageForUpload(file),buf=await blob.arrayBuffer(),base=toBase64Url(buf),name='producto-'+Date.now()+'.jpg',body='fileName='+encodeURIComponent(name)+'&contentType=image/jpeg&base64='+base;var x=new XMLHttpRequest();x.open('POST','/seller/media/upload',true);x.timeout=90000;x.setRequestHeader('Content-Type','application/x-www-form-urlencoded;charset=UTF-8');x.onreadystatechange=function(){if(x.readyState!==4)return;if(x.status===200&&x.responseText.indexOf('OK|')===0){var parts=x.responseText.split('|');resolve(parts.length>2?parts[2]:'');}else reject(new Error(x.responseText||('HTTP '+x.status)));};x.onerror=function(){reject(new Error('No se pudo conectar con el almacenamiento de imágenes.'));};x.ontimeout=function(){reject(new Error('La carga tardó demasiado. Probá una foto más chica.'));};x.send(body);}catch(e){reject(e);}});}function previewImage(input,target){var f=input.files&&input.files[0];if(!f)return;var u=URL.createObjectURL(f);target.innerHTML='<img src=\"'+u+'\" alt=\"Vista previa\"/>';}var ni=document.getElementById('newImageFile');if(ni)ni.addEventListener('change',function(){previewImage(this,document.getElementById('newMediaPreview'));});var nf=document.getElementById('newProductForm');if(nf)nf.addEventListener('submit',async function(e){e.preventDefault();var btn=document.getElementById('saveProductBtn'),st=document.getElementById('uploadState'),file=ni&&ni.files?ni.files[0]:null;btn.disabled=true;try{if(file){st.textContent='Subiendo imagen al almacenamiento central...';var url=await uploadSellerImage(file);if(!url)throw new Error('El servidor no devolvió una URL de imagen.');document.getElementById('newImageUrl').value=url;st.textContent='✓ Imagen guardada correctamente.';}else{st.textContent='Guardando producto sin imagen...';}HTMLFormElement.prototype.submit.call(nf);}catch(err){st.textContent='Error de imagen: '+err.message;btn.disabled=false;}});document.querySelectorAll('.edit-form').forEach(function(form){form.addEventListener('submit',async function(e){var input=form.querySelector('.editImageFile');if(!input||!input.files||!input.files[0])return;e.preventDefault();var btn=form.querySelector('button[type=submit]');if(btn)btn.disabled=true;try{var url=await uploadSellerImage(input.files[0]);if(!url)throw new Error('El servidor no devolvió una URL de imagen.');form.querySelector('input[name=imageUrl]').value=url;HTMLFormElement.prototype.submit.call(form);}catch(err){alert('No se pudo subir la nueva foto: '+err.message);if(btn)btn.disabled=false;}});});</script>");
            return b.ToString();
        }

        private string SellerDeliveryView(List<XElement> orders)
        {
            var deliveries=orders.Where(x=>string.Equals(S(x,"Fulfillment"),"Delivery",StringComparison.OrdinalIgnoreCase)||string.Equals(S(x,"Fulfillment"),"En reparto",StringComparison.OrdinalIgnoreCase)||string.Equals(S(x,"Status"),"En reparto",StringComparison.OrdinalIgnoreCase)).ToList();
            StringBuilder b=new StringBuilder();b.Append("<div class='section-title'><div><span class='eyebrow'>LOGÍSTICA</span><h2>Deliveries</h2><p>Seguimiento de entregas, dirección, cliente y estado. Los cambios se aplican sin salir de esta pestaña.</p></div><span class='sync-pill'>● DELIVERY EN TIEMPO REAL</span></div>");
            b.Append("<section class='card'><div class='inventory-toolbar'><input id='deliverySearch' placeholder='Buscar cliente, teléfono, pedido o dirección...' oninput='filterDeliveries()'/><select id='deliveryFilter' onchange='filterDeliveries()'><option value='all'>Todos</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Cancelado</option></select></div><div class='order-cards' id='deliveryCards'>");
            foreach(XElement o in deliveries){string id=S(o,"CentralOrderId"),status=S(o,"Status");b.Append("<article class='order-card delivery-card' data-search='").Append(E((id+" "+S(o,"CustomerName")+" "+S(o,"Phone")+" "+S(o,"Address")).ToLowerInvariant())).Append("' data-status='").Append(E(status)).Append("'><div><span class='eyebrow'>DELIVERY</span><h3>#").Append(E(id.Length>10?id.Substring(0,10):id)).Append("</h3><small>").Append(E(S(o,"CreatedAt"))).Append("</small></div><div><b>").Append(E(S(o,"CustomerName"))).Append("</b><small>📞 ").Append(E(S(o,"Phone"))).Append("</small><small>📍 ").Append(E(S(o,"Address"))).Append("</small></div><div><strong>$ ").Append(Money(S(o,"Total")).ToString("N2")).Append("</strong><div class='order-status-badges'>").Append(BadgeC(status)).Append("</div></div><form method='post' action='/seller/delivery-status' class='inline-form delivery-status-form' onsubmit='return updateDeliveryStatus(this)'><input type='hidden' name='id' value='").Append(E(id)).Append("'/><select name='status'><option>").Append(E(status)).Append("</option><option>Preparando</option><option>Listo</option><option>Enviado</option><option>En reparto</option><option>Entregado</option><option>Cancelado</option></select><button class='btn small' type='submit'>ACTUALIZAR</button><span class='order-saving'></span></form></article>");}
            if(deliveries.Count==0)b.Append("<div class='empty-inventory'>No hay deliveries abiertos todavía.</div>");
            b.Append("</div></section><script>function filterDeliveries(){var q=(document.getElementById('deliverySearch').value||'').toLowerCase(),f=document.getElementById('deliveryFilter').value;document.querySelectorAll('.delivery-card').forEach(function(c){c.style.display=(c.getAttribute('data-search').indexOf(q)>=0&&(f==='all'||c.getAttribute('data-status')===f))?'grid':'none';});}function updateDeliveryStatus(form){var card=form.closest('.delivery-card'),sel=form.querySelector('select'),btn=form.querySelector('button'),msg=form.querySelector('.order-saving');btn.disabled=true;msg.textContent='Guardando...';var data='id='+encodeURIComponent(form.querySelector('input[name=id]').value)+'&status='+encodeURIComponent(sel.value);fetch(form.action,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8'},credentials:'same-origin',body:data}).then(function(r){return r.text().then(function(t){return {ok:r.ok,text:t};});}).then(function(x){var d;try{d=JSON.parse(x.text);}catch(e){d=null;}if(!x.ok||!d||!d.ok){msg.textContent=(d&&d.message)||'No se pudo actualizar';btn.disabled=false;return;}card.setAttribute('data-status',sel.value);var badges=card.querySelector('.order-status-badges');if(badges)badges.innerHTML=(sel.value==='Cancelado'?'<span class=\"badge red\">Cancelado</span>':sel.value==='Preparando'||sel.value==='Listo'?'<span class=\"badge yellow\">'+sel.value+'</span>':'<span class=\"badge green\">'+sel.value+'</span>');msg.textContent='Actualizado';btn.disabled=false;filterDeliveries();}).catch(function(){msg.textContent='Error de conexión';btn.disabled=false;});return false;}</script>");return b.ToString();
        }

        private string SellerPosView(List<XElement> products)
        {
            StringBuilder b=new StringBuilder();b.Append("<div class='section-title'><div><span class='eyebrow'>PUNTO DE VENTA WEB</span><h2>POS / Ticket de venta</h2><p>Buscá por nombre, SKU o código de barras, armá el ticket y generá un comprobante imprimible o guardable como PDF.</p></div><span class='sync-pill'>● SINCRONIZADO CON WINDOWS</span></div>");
            b.Append("<div class='checkout-grid'><section class='card'><div class='inventory-toolbar'><input id='posSearch' autofocus placeholder='Escaneá o escribí código de barras / nombre...' oninput='filterPos()' onkeydown='if(event.key===\"Enter\"){event.preventDefault();addFirstPos();}'/><button class='btn violet' type='button' onclick='addFirstPos()'>AGREGAR</button></div><div id='posProducts' class='pos-products'>");
            foreach(XElement p in products.Where(x=>S(x,"Active")!="0")){decimal price=Money(S(p,"SalePrice")=="0"?S(p,"Price"):S(p,"SalePrice"));b.Append("<button type='button' class='pos-product' data-search='").Append(E((S(p,"Name")+" "+S(p,"SKU")+" "+S(p,"Barcode")).ToLowerInvariant())).Append("' onclick=\"addPos(").Append(JsonString(S(p,"ProductId"))).Append(",").Append(JsonString(S(p,"Name"))).Append(",").Append(price.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(")\"><span>").Append(E(S(p,"Name"))).Append("</span><small>SKU ").Append(E(S(p,"SKU"))).Append(" · CB ").Append(E(S(p,"Barcode"))).Append(" · Stock ").Append(E(S(p,"Stock"))).Append("</small><b>$ ").Append(price.ToString("N2")).Append("</b></button>");}
            b.Append("</div></section><aside class='card'><h3>Ticket actual</h3><div id='posCart'>Vacío.</div><div class='cart-total'><span>TOTAL</span><strong>$ <span id='posTotal'>0.00</span></strong></div><input id='posCustomer' placeholder='Cliente (opcional)'/><select id='posPayment'><option>Efectivo</option><option>Mercado Pago</option><option>Transferencia</option><option>Tarjeta</option></select><input id='posReference' placeholder='Referencia / comprobante interno (opcional)'/><button class='btn' style='width:100%' onclick='finishPosSale()'>COBRAR Y GENERAR TICKET</button><div id='posResult' class='muted' style='margin-top:10px'></div></aside></div><script>var posCart=[];function filterPos(){var q=(document.getElementById('posSearch').value||'').toLowerCase();document.querySelectorAll('.pos-product').forEach(function(p){p.style.display=p.getAttribute('data-search').indexOf(q)>=0?'grid':'none';});}function addFirstPos(){var q=(document.getElementById('posSearch').value||'').toLowerCase(),a=Array.prototype.slice.call(document.querySelectorAll('.pos-product')).filter(function(p){return p.getAttribute('data-search').indexOf(q)>=0});if(a.length)a[0].click();}function addPos(id,name,price){var x=posCart.filter(function(i){return i.id===id})[0];if(x)x.qty++;else posCart.push({id:id,name:name,price:price,qty:1});renderPos();}function renderPos(){var h='',t=0;posCart.forEach(function(i,n){h+='<div class=\"line\"><span>'+i.name+' × '+i.qty+'</span><span><b>$ '+(i.price*i.qty).toFixed(2)+'</b> <button type=\"button\" onclick=\"removePos('+n+')\">×</button></span></div>';t+=i.price*i.qty});document.getElementById('posCart').innerHTML=h||'Vacío.';document.getElementById('posTotal').textContent=t.toFixed(2);}function removePos(n){posCart.splice(n,1);renderPos();}function finishPosSale(){if(!posCart.length){alert('Agregá productos al ticket.');return}var total=posCart.reduce(function(a,i){return a+i.price*i.qty},0),data='itemsJson='+encodeURIComponent(JSON.stringify(posCart))+'&total='+encodeURIComponent(total.toFixed(2))+'&customerName='+encodeURIComponent(document.getElementById('posCustomer').value)+'&paymentMethod='+encodeURIComponent(document.getElementById('posPayment').value)+'&paymentReference='+encodeURIComponent(document.getElementById('posReference').value);var x=new XMLHttpRequest();x.open('POST','/seller/pos-sale',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded;charset=UTF-8');x.onreadystatechange=function(){if(x.readyState===4){try{var d=JSON.parse(x.responseText);if(d.ok){document.getElementById('posResult').innerHTML='✓ Venta registrada. <a href=\"'+d.ticket+'\" target=\"_blank\">ABRIR / GUARDAR PDF</a>';posCart=[];renderPos();}else document.getElementById('posResult').textContent=d.message||'No se pudo registrar.';}catch(e){document.getElementById('posResult').textContent=x.responseText||'Error';}}};x.send(data);}renderPos();</script>");return b.ToString();
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

        private decimal GetCommissionPercent(string storeId)
        {
            decimal p=1m; lock(_sync){XElement st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(storeId),StringComparison.OrdinalIgnoreCase));if(st!=null){decimal parsed;if(decimal.TryParse(S(st,"CommissionPercent"),NumberStyles.Any,CultureInfo.InvariantCulture,out parsed))p=parsed;if(p<0m)p=0m;if(p>100m)p=100m;}} return p;
        }
        private string CurrentMonthKey(){return DateTime.UtcNow.ToString("yyyy-MM",CultureInfo.InvariantCulture);}
        private decimal GetCurrentMonthSales(string storeId)
        {
            DateTime now=DateTime.UtcNow;decimal total=0m;lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");XElement root=d.Root==null?null:d.Root.Element("Orders");if(root!=null)foreach(XElement o in root.Elements("Order").Where(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase))){DateTime at=ParseUtcDate(S(o,"CreatedAt"));string st=S(o,"Status");if(at!=DateTime.MinValue&&at.Year==now.Year&&at.Month==now.Month&&st!="Pendiente"&&st!="Cancelado"&&st!="Rechazado")total+=Money(S(o,"Total"));}}return total;
        }
        private string CommissionInfoJson(string storeId)
        {
            storeId=NormalizeStoreId(storeId);decimal percent=GetCommissionPercent(storeId),sales=GetCurrentMonthSales(storeId),due=Math.Round(sales*percent/100m,2);string month=CurrentMonthKey(),paidMonth="",dueDate="";lock(_sync){XElement st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==storeId);if(st!=null){paidMonth=S(st,"CommissionPaidMonth");dueDate=S(st,"CommissionDueDate");}}if(string.IsNullOrWhiteSpace(dueDate)){DateTime next=new DateTime(DateTime.UtcNow.Year,DateTime.UtcNow.Month,1).AddMonths(1);dueDate=next.AddDays(-1).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);}bool paid=string.Equals(paidMonth,month,StringComparison.OrdinalIgnoreCase);return "{\"storeId\":"+JsonString(storeId)+",\"month\":"+JsonString(month)+",\"percent\":"+percent.ToString("0.##",CultureInfo.InvariantCulture)+",\"sales\":"+sales.ToString("0.00",CultureInfo.InvariantCulture)+",\"due\":"+(paid?"0.00":due.ToString("0.00",CultureInfo.InvariantCulture)) + ",\"dueDate\":"+JsonString(dueDate)+",\"paid\":"+(paid?"true":"false")+"}";
        }
        private string SellerCommissionJson(string cookie){CentralUser u=SessionUser(cookie);if(u==null||u.Role!="seller")return "{\"error\":\"unauthorized\"}";return CommissionInfoJson(u.StoreId);}
        private string AdminCommissions(string key)
        {
            string denied=AdminDenied(key);if(denied!=null)return denied;StringBuilder b=new StringBuilder("[");bool first=true;List<string> ids=new List<string>();lock(_sync){foreach(XElement st in _doc.Root.Element("Stores").Elements("Store")){string id=S(st,"StoreId");if(id.Length>0)ids.Add(id);}}foreach(string id in ids){CentralUser seller=FindSellerByStore(id);string info=CommissionInfoJson(id);if(!first)b.Append(',');first=false;b.Append(info.Substring(0,info.Length-1)).Append(",\"storeName\":").Append(JsonString(GetStoreName(id))).Append(",\"sellerEmail\":").Append(JsonString(seller==null?"":seller.Email)).Append(",\"sellerName\":").Append(JsonString(seller==null?"":seller.Name)).Append('}');}return b.Append(']').ToString();
        }
        private string AdminSetCommission(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key);if(denied!=null)return denied;string id=NormalizeStoreId(Get(f,"storeId"));decimal p;if(!decimal.TryParse(Get(f,"percent").Replace(",","."),NumberStyles.Any,CultureInfo.InvariantCulture,out p)||p<0m||p>100m)return "ERROR|invalid_percent";lock(_sync){XElement st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==id);if(st==null)return "ERROR|store_not_found";st.SetElementValue("CommissionPercent",p.ToString("0.##",CultureInfo.InvariantCulture));st.SetElementValue("UpdatedAt",DateTime.UtcNow.ToString("o"));Save();}Audit("commission_percent_changed",id,"","",p.ToString(CultureInfo.InvariantCulture));return "OK|commission|"+p.ToString("0.##",CultureInfo.InvariantCulture);
        }
        private string AdminStoreDetails(string key,string storeId)
        {
            string denied=AdminDenied(key);if(denied!=null)return denied;storeId=NormalizeStoreId(storeId);if(storeId.Length==0)return "{\"error\":\"store_required\"}";
            XElement st;CentralUser seller;
            lock(_sync){st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==storeId);}
            if(st==null)return "{\"error\":\"store_not_found\"}"; seller=FindSellerByStore(storeId);
            string info=CommissionInfoJson(storeId);
            StringBuilder b=new StringBuilder("{");
            string[] keys={"StoreId","Name","SystemName","Category","Description","City","Province","Address","Latitude","Longitude","Logo","StorePhoto","PublicUrl","Slug","Active","Listed","Featured","FeaturedPlus","Delivery","Pickup","OpenTime","CloseTime","AutoSchedule","CreatedAt","UpdatedAt","LastActivityAt","CommissionPercent","CommissionDueDate","CommissionPaidMonth"};
            bool first=true;foreach(string k in keys){if(!first)b.Append(',');first=false;b.Append(JsonString(k)).Append(':').Append(JsonString(S(st,k)));}
            b.Append(",\"sellerEmail\":").Append(JsonString(seller==null?"":seller.Email)).Append(",\"sellerName\":").Append(JsonString(seller==null?"":seller.Name)).Append(",\"commission\":").Append(info).Append('}');return b.ToString();
        }
        private string AdminSetStorePlan(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key);if(denied!=null)return denied;string id=NormalizeStoreId(Get(f,"storeId"));string plan=(Get(f,"plan")??"").Trim().ToLowerInvariant();if(id.Length==0)return "ERROR|store_required";
            CentralUser seller=FindSellerByStore(id);if(seller==null)return "ERROR|seller_not_found";
            if(plan=="permanent"){
                bool ok=false;if(_database!=null&&_database.Enabled)ok=_database.SetAccountPermanent(seller.Email);
                if(!ok){lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),seller.Email,StringComparison.OrdinalIgnoreCase));if(e!=null){e.SetElementValue("Active","1");e.SetElementValue("TrialExpiresAt","");SaveDoc(_accountsFile,d);ok=true;}}}
                Audit("admin_plan_permanent",id,seller.Email,"","");return ok?"OK|permanent":"ERROR|account_not_found";
            }
            int days;if(!int.TryParse(Get(f,"days"),out days)||days<1||days>3650)return "ERROR|invalid_days";
            return AdminSetTrial(key,new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"email",seller.Email},{"days",days.ToString(CultureInfo.InvariantCulture)}});
        }
        private string AdminResetAccountPassword(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key);if(denied!=null)return denied;string storeId=NormalizeStoreId(Get(f,"storeId"));string email=Get(f,"email").Trim().ToLowerInvariant();string password=Get(f,"password");if(password.Length<8)return "ERROR|password_min_8";
            CentralUser seller=storeId.Length>0?FindSellerByStore(storeId):FindAccount(email);if(seller==null)return "ERROR|account_not_found";email=seller.Email;
            byte[] salt=new byte[16];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(salt);string salt64=Convert.ToBase64String(salt);byte[] hash;using(var kdf=new Rfc2898DeriveBytes(password,salt,50000))hash=kdf.GetBytes(32);string hash64=Convert.ToBase64String(hash);bool ok=false;
            if(_database!=null&&_database.Enabled)ok=_database.UpdatePassword(email,salt64,hash64);
            lock(_sync){XDocument d=LoadFile(_accountsFile,"NexoMarketAccounts","Users");XElement e=d.Root.Element("Users").Elements("User").FirstOrDefault(x=>string.Equals(S(x,"Email"),email,StringComparison.OrdinalIgnoreCase));if(e!=null){e.SetElementValue("Salt",salt64);e.SetElementValue("PasswordHash",hash64);e.SetElementValue("Active","1");SaveDoc(_accountsFile,d);ok=true;}}
            Audit("admin_password_reset",seller.StoreId,email,"","");return ok?"OK|password_updated":"ERROR|password_update_failed";
        }
        private string AdminCommissionAction(string key,Dictionary<string,string> f)
        {
            string denied=AdminDenied(key);if(denied!=null)return denied;string id=NormalizeStoreId(Get(f,"storeId")),action=Get(f,"action").Trim().ToLowerInvariant();if(id.Length==0)return "ERROR|store_required";if(action=="paid"){lock(_sync){XElement st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==id);if(st==null)return "ERROR|store_not_found";st.SetElementValue("CommissionPaidMonth",CurrentMonthKey());st.SetElementValue("CommissionPaidAt",DateTime.UtcNow.ToString("o"));Save();}Audit("commission_marked_paid",id,"","",CurrentMonthKey());return "OK|paid";}if(action=="postpone"){int days;if(!int.TryParse(Get(f,"days"),out days)||days<1||days>365)return "ERROR|invalid_days";DateTime due=DateTime.UtcNow.Date.AddDays(days);lock(_sync){XElement st=_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>S(x,"StoreId")==id);if(st==null)return "ERROR|store_not_found";st.SetElementValue("CommissionDueDate",due.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));st.SetElementValue("CommissionPostponedUntil",due.ToString("o"));Save();}return "OK|postponed|"+due.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);}if(action=="block"||action=="enable"){bool on=action=="enable";CentralUser seller=FindSellerByStore(id);string r=AdminSetStoreActive(key,new Dictionary<string,string>{{"storeId",id},{"active",on?"1":"0"}});if(!r.StartsWith("OK|",StringComparison.OrdinalIgnoreCase))return r;if(seller!=null)AdminSetAccountActive(key,new Dictionary<string,string>{{"email",seller.Email},{"active",on?"1":"0"}});return "OK|"+(on?"enabled":"blocked");}return "ERROR|unknown_action";
        }
        private string SellerCommissionCard(string storeId)
        {
            string j=CommissionInfoJson(storeId),percent=JsonValue(j,"percent"),sales=JsonValue(j,"sales"),due=JsonValue(j,"due"),date=JsonValue(j,"dueDate");bool paid=j.IndexOf("\"paid\":true",StringComparison.OrdinalIgnoreCase)>=0;return "<section class='card commission-card'><div class='section-title'><div><span class='eyebrow'>COMISIÓN NEXOMARKET</span><h3>Liquidación del mes</h3><p>Porcentaje configurado por el Super Admin. Se calcula sobre ventas válidas del mes.</p></div><div class='commission-percent'>"+E(percent)+"%</div></div><div class='commission-grid'><div><small>Ventas del mes</small><strong>$ "+E(sales)+"</strong></div><div><small>A pagar</small><strong class='commission-due'>$ "+E(due)+"</strong></div><div><small>Vencimiento</small><strong>"+E(date)+"</strong></div><div><small>Estado</small><strong>"+(paid?"🟢 PAGADO":"🟡 PENDIENTE")+"</strong></div></div></section>";
        }
        private string SellerFinanceView(List<XElement> orders){decimal total=orders.Where(x=>S(x,"Status")!="Cancelado"&&S(x,"Status")!="Rechazado").Sum(x=>Money(S(x,"Total")));decimal cash=orders.Where(x=>S(x,"PaymentMethod")=="Efectivo"&&S(x,"Status")!="Cancelado"&&S(x,"Status")!="Rechazado").Sum(x=>Money(S(x,"Total")));decimal mp=orders.Where(x=>S(x,"PaymentMethod")=="Mercado Pago"&&S(x,"Status")!="Cancelado"&&S(x,"Status")!="Rechazado").Sum(x=>Money(S(x,"Total")));decimal tr=orders.Where(x=>S(x,"PaymentMethod")=="Transferencia"&&S(x,"Status")!="Cancelado"&&S(x,"Status")!="Rechazado").Sum(x=>Money(S(x,"Total")));return "<div class='section-title'><div><span class='eyebrow'>FINANZAS</span><h2>Ventas, cobros y comisión</h2><p>Resumen central de las operaciones web. La apertura/cierre física de caja continúa en Windows.</p></div></div><div class='kpis mini-kpis'>"+KpiC("Total vendido","$ "+total.ToString("N2"),"operaciones web","green")+KpiC("Efectivo","$ "+cash.ToString("N2"),"ventas","green")+KpiC("Mercado Pago","$ "+mp.ToString("N2"),"ventas","green")+KpiC("Transferencias","$ "+tr.ToString("N2"),"ventas","green")+"</div>"+SellerCommissionCard(CurrentSellerStoreId())+"<section class='card'><h3>Conciliación</h3><p>Los números del Seller Center se alimentan de los pedidos centralizados. La comisión mensual es administrada por NexoMarket Super Admin.</p></section>";}
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

        private string SellerReputationView(List<XElement> orders){int delivered=orders.Count(x=>S(x,"Status")=="Entregado"),cancel=orders.Count(x=>S(x,"Status")=="Cancelado"||S(x,"Status")=="Rechazado");string rs=StoreRatingSummary(CurrentSellerStoreId());string[] rp=rs.Split('|');string avg=rp.Length>0?rp[0]:"0.0",cnt=rp.Length>1?rp[1]:"0";return "<div class='section-title'><div><span class='eyebrow'>REPUTACIÓN</span><h2>Salud de la operación</h2><p>Pedidos y reseñas de compradores.</p></div></div><div class='kpis mini-kpis'>"+KpiC("Puntuación",avg+" ★","sobre 5","green")+KpiC("Reseñas",cnt,"opiniones publicadas","yellow")+KpiC("Entregados",delivered.ToString(),"pedidos finalizados","green")+KpiC("Cancelados/Rechazados",cancel.ToString(),"incidencias","red")+"</div><section class='card'><h3>Buenas prácticas</h3><div class='insights'><div>🟢 Actualizá estados rápidamente para que el comprador vea el seguimiento.</div><div>🟡 Mantené stock y precios sincronizados con Windows.</div><div>⭐ Las reseñas se calculan de 1 a 5 estrellas y se promedian automáticamente.</div></div></section>";}
        private void CentralSellerDevices(NetworkStream stream,string cookie,string method,string body)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            // El vínculo Windows ya no usa códigos ni Store ID visibles.
            // Se conserva esta ruta para instalaciones antiguas, pero no forma parte
            // del flujo actual. Windows autentica directamente con correo + contraseña.
            string html="<div class='card'><div class='eyebrow'>CUENTA WINDOWS</div><h1>Conexión automática</h1><p class='muted'>No necesitás código de vinculación. Abrí NexoMarket Windows e iniciá sesión con el mismo correo y contraseña de esta cuenta.</p><a class='btn violet' href='/seller'>VOLVER AL SELLER CENTER</a></div>";
            Write(stream,200,"text/html; charset=utf-8",AuthPage("Conexión Windows",html));
        }

        private string CentralSellerMediaUpload(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller") return "ERROR|session";
            f["storeId"]=u.StoreId;
            return UploadMedia(f);
        }

        private string UploadOrderProof(Dictionary<string,string> f)
        {
            string storeId=NormalizeStoreId(Get(f,"storeId"));
            if(string.IsNullOrWhiteSpace(storeId)) return "ERROR|storeId";
            lock(_sync){XElement st=_doc.Root.Element("Stores")==null?null:_doc.Root.Element("Stores").Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),storeId,StringComparison.OrdinalIgnoreCase));if(st==null)return "ERROR|store";}
            string fileName=Path.GetFileName(Get(f,"fileName"));
            string contentType=Get(f,"contentType");
            string base64=Get(f,"base64");
            if(string.IsNullOrWhiteSpace(fileName)||string.IsNullOrWhiteSpace(base64)) return "ERROR|missing";
            if(contentType.IndexOf("image/",StringComparison.OrdinalIgnoreCase)!=0) return "ERROR|image_only";
            f["storeId"]=storeId;
            string result=UploadMedia(f);
            return result;
        }

        private string SellerMediaStatus(string cookie)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller") return "{\"ok\":false,\"session\":true}";
            bool enabled=_r2!=null&&_r2.Enabled;
            return "{\"ok\":true,\"r2\":"+(enabled?"true":"false")+",\"publicUrl\":"+JsonString(_r2==null?"":_r2.PublicBaseUrl)+"}";
        }

        private void CentralSellerDeliveryStatus(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller"){Write(stream,401,"application/json; charset=utf-8","{\"ok\":false,\"session\":true}");return;}
            string id=Get(f,"id"), status=Get(f,"status");
            if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(status)){Write(stream,400,"application/json; charset=utf-8","{\"ok\":false,\"message\":\"Faltan datos del delivery.\"}");return;}
            string result=UpdateOrderStatus(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",u.StoreId},{"syncKey",ComputeStorePairKey(u.StoreId)},{"centralOrderId",id},{"status",status}});
            if(result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)) Write(stream,200,"application/json; charset=utf-8","{\"ok\":true,\"id\":"+JsonString(id)+",\"status\":"+JsonString(status)+"}");
            else Write(stream,400,"application/json; charset=utf-8","{\"ok\":false,\"message\":"+JsonString(result)+"}");
        }

        private void CentralSellerPosSale(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller"){Write(stream,401,"application/json; charset=utf-8","{\"ok\":false,\"session\":true}");return;}
            string items=Get(f,"itemsJson"); decimal total;
            if(string.IsNullOrWhiteSpace(items)||!decimal.TryParse(Get(f,"total"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out total)||total<=0m){Write(stream,400,"application/json; charset=utf-8","{\"ok\":false,\"message\":\"El ticket está vacío.\"}");return;}
            Dictionary<string,string> order=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",u.StoreId},{"customerName",Get(f,"customerName")},{"customerEmail",Get(f,"customerEmail")},{"phone",Get(f,"phone")},{"fulfillment","Mostrador"},{"address","Mostrador"},{"notes",Get(f,"notes")},{"status","Entregado"},{"total",total.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"paymentMethod",Get(f,"paymentMethod")},{"paymentStatus","Pagado"},{"paymentReference",Get(f,"paymentReference")},{"paymentProofPath","POS"},{"itemsJson",items},{"buyerMessage","Venta de mostrador"}};
            string result=CreateOrder(order);
            if(result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase)){string id=result.Split('|')[1];Write(stream,200,"application/json; charset=utf-8","{\"ok\":true,\"id\":"+JsonString(id)+",\"ticket\":\"/seller/ticket?orderId="+Uri.EscapeDataString(id)+"\"}");}else Write(stream,400,"application/json; charset=utf-8","{\"ok\":false,\"message\":"+JsonString(result)+"}");
        }

        private void CentralSellerTicket(NetworkStream stream,string cookie,string query)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            string id=QueryValue(query,"orderId"); XElement o=null;
            lock(_sync){XDocument d=LoadFile(_ordersFile,"NexoMarketOrders","Orders");o=d.Root.Element("Orders")==null?null:d.Root.Element("Orders").Elements("Order").FirstOrDefault(x=>S(x,"StoreId")==u.StoreId&&S(x,"CentralOrderId")==id);}
            if(o==null){Write(stream,404,"text/html; charset=utf-8","Ticket no encontrado");return;}
            StringBuilder b=new StringBuilder();b.Append("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Ticket ").Append(E(id)).Append("</title><style>body{font-family:Arial;background:#eee;margin:0}.ticket{width:360px;max-width:calc(100vw - 24px);margin:20px auto;background:#fff;color:#111;padding:20px;box-sizing:border-box}.center{text-align:center}.line{display:flex;justify-content:space-between;padding:5px 0;border-bottom:1px dashed #ccc}.actions{width:360px;max-width:calc(100vw - 24px);margin:12px auto}.actions button{padding:10px 14px;margin-right:6px}@media print{body{background:#fff}.actions{display:none}.ticket{margin:0;width:80mm;max-width:none;box-shadow:none}}</style></head><body><div class='actions'><button onclick='window.print()'>IMPRIMIR / GUARDAR PDF</button><button onclick='window.close()'>CERRAR</button></div><div class='ticket'><div class='center'><h2>NEXOMARKET</h2><small>COMPROBANTE DE VENTA</small><hr/></div><div><b>Pedido:</b> #").Append(E(id)).Append("</div><div><b>Fecha:</b> ").Append(E(S(o,"CreatedAt"))).Append("</div><div><b>Cliente:</b> ").Append(E(S(o,"CustomerName"))).Append("</div><hr/>");
            string items=S(o,"ItemsJson"); foreach(Match m in Regex.Matches(items, "\"name\"\\s*:\\s*\"([^\"]*)\"[^}]*?\"price\"\\s*:\\s*([0-9.]+)[^}]*?\"qty\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase)){decimal price=Money(m.Groups[2].Value);int qty;int.TryParse(m.Groups[3].Value,out qty);b.Append("<div class='line'><span>").Append(E(m.Groups[1].Value)).Append(" × ").Append(qty).Append("</span><b>$ ").Append((price*qty).ToString("N2")).Append("</b></div>");}
            b.Append("<h2 style='text-align:right'>TOTAL $ ").Append(Money(S(o,"Total")).ToString("N2")).Append("</h2><div>Medio de pago: ").Append(E(S(o,"PaymentMethod"))).Append("</div><div class='center' style='margin-top:20px'>Gracias por tu compra.</div></div></body></html>");Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private void CentralSellerStoreSave(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie); if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            lock(_sync)
            {
                XElement stores=_doc.Root.Element("Stores");
                XElement store=stores==null?null:stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(u.StoreId),StringComparison.OrdinalIgnoreCase));
                if(store==null){WriteRedirect(stream,"/seller?view=settings&error=store");return;}
                string[] fields={"Name","SystemName","LegalName","Category","Address","City","Province","Description","Logo","StorePhoto","Slug","Delivery","Pickup","Latitude","Longitude","AutoSchedule","OpenTime","CloseTime"};
                foreach(string field in fields){string key=char.ToLowerInvariant(field[0])+field.Substring(1); if(field=="Delivery"||field=="Pickup") store.SetElementValue(field,f.ContainsKey(key)?"1":"0"); else if(f.ContainsKey(key)) store.SetElementValue(field,Get(f,key).Trim());}
                store.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o")); Save();
            }
            WriteRedirect(stream,"/seller?view=settings&saved=1");
        }

        private void CentralSellerStoreToggle(NetworkStream stream,string cookie,Dictionary<string,string> f)
        {
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller"){WriteRedirect(stream,"/seller-login");return;}
            bool requested=Get(f,"active")=="1";
            lock(_sync)
            {
                XElement stores=_doc.Root.Element("Stores");
                XElement store=stores==null?null:stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(u.StoreId),StringComparison.OrdinalIgnoreCase));
                if(store==null){WriteRedirect(stream,"/seller?view=settings&error=store");return;}
                store.SetElementValue("Active",requested?"1":"0");
                store.SetElementValue("AutoSchedule","0");
                store.SetElementValue("LastActivityAt",DateTime.UtcNow.ToString("o"));
                store.SetAttributeValue("UpdatedAt",DateTime.UtcNow.ToString("o"));
                Save();
            }
            string sessionToken=CreateSessionToken(u); lock(_sync) _sessions[sessionToken]=u;
            WriteRedirectCookie(stream,"/seller?store=updated","NexoCentralSession="+sessionToken+"; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax");
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
            return SellerOnlineSettingsView(u);
        }
        private string SellerOnlineSettingsView(CentralUser u)
        {
            XElement store=null; lock(_sync){XElement stores=_doc.Root.Element("Stores"); if(stores!=null) store=stores.Elements("Store").FirstOrDefault(x=>string.Equals(S(x,"StoreId"),NormalizeStoreId(u.StoreId),StringComparison.OrdinalIgnoreCase));}
            if(store==null) return "<section class='card'><div class='error'>No se encontró la tienda asociada a esta cuenta.</div></section>";
            string logo=S(store,"Logo"), photo=S(store,"StorePhoto");
            return "<div class='section-title'><div><span class='eyebrow'>TIENDA ONLINE</span><h2>Configurar mi tienda online</h2><p>Acá editás nombre, logo, foto del local, ubicación y horario. Todo queda centralizado y se replica a NexoMarket Windows.</p></div><span class='sync-pill'>● CONFIGURACIÓN CENTRAL</span></div>"+
            "<section class='card'><form method='post' action='/seller/store/save' class='settings-form' id='storeOnlineForm'><input type='hidden' name='logo' id='storeLogoUrl' value='"+E(logo)+"'/><input type='hidden' name='storePhoto' id='storePhotoUrl' value='"+E(photo)+"'/><div class='form-grid'>"+
            "<label>Nombre público de la tienda<input name='name' value='"+E(S(store,"Name"))+"' required/></label>"+
            "<label>Nombre del sistema / marca<input name='systemName' value='"+E(S(store,"SystemName"))+"' placeholder='Ej.: Mi Sistema POS'/></label>"+
            "<label>Nombre legal<input name='legalName' value='"+E(S(store,"LegalName"))+"'/></label><label>Categoría<input name='category' value='"+E(S(store,"Category"))+"'/></label>"+
            "<label>Ciudad<input name='city' value='"+E(S(store,"City"))+"'/></label><label>Provincia<input name='province' value='"+E(S(store,"Province"))+"'/></label>"+
            "<label>Dirección<input id='storeAddress' name='address' value='"+E(S(store,"Address"))+"'/></label>"+
            "<label>Latitud<input id='storeLat' name='latitude' value='"+E(S(store,"Latitude"))+"' placeholder='-34.60'/></label><label>Longitud<input id='storeLon' name='longitude' value='"+E(S(store,"Longitude"))+"' placeholder='-58.38'/></label>"+
            "<label>Slug / URL<input name='slug' value='"+E(S(store,"Slug"))+"'/></label>"+
            "<label class='wide-label'>Logo del local<input id='storeLogoFile' type='file' accept='image/jpeg,image/png,image/webp,image/gif'/><small>Se guarda en el almacenamiento central. No hace falta pegar una URL.</small><div id='storeLogoPreview' class='media-preview'>"+(string.IsNullOrWhiteSpace(logo)?"Sin logo cargado":"<img src='"+E(logo)+"' alt='Logo' />")+"</div></label>"+
            "<label class='wide-label'>Foto del local / portada<input id='storePhotoFile' type='file' accept='image/jpeg,image/png,image/webp,image/gif'/><small>Foto que se mostrará en el escaparate de la tienda.</small><div id='storePhotoPreview' class='media-preview'>"+(string.IsNullOrWhiteSpace(photo)?"Sin foto cargada":"<img src='"+E(photo)+"' alt='Local' />")+"</div></label>"+
            "</div><label>Descripción pública<textarea name='description'>"+E(S(store,"Description"))+"</textarea></label>"+
            "<section class='schedule-box'><h3>⏰ Horario automático</h3><p>Si activás esta opción, la tienda se abrirá al llegar la hora indicada y se cerrará automáticamente al llegar la hora de cierre.</p><div class='form-grid'><label><input type='checkbox' name='autoSchedule' value='1' "+(S(store,"AutoSchedule")!="0"?"checked":"")+"/> Activar apertura/cierre automático</label><label>Abre<input type='time' name='openTime' value='"+E(S(store,"OpenTime").Length>0?S(store,"OpenTime"):"08:00")+"'/></label><label>Cierra<input type='time' name='closeTime' value='"+E(S(store,"CloseTime").Length>0?S(store,"CloseTime"):"22:00")+"'/></label></div></section>"+
            "<div class='form-checks'><label><input type='checkbox' name='delivery' value='1' "+(S(store,"Delivery")!="0"?"checked":"")+"/> Ofrecer delivery</label><label><input type='checkbox' name='pickup' value='1' "+(S(store,"Pickup")!="0"?"checked":"")+"/> Permitir retiro</label></div>"+
            "<div class='section-actions'><button class='btn ghost' type='button' onclick='geocodeStore()'>📍 BUSCAR UBICACIÓN</button><button class='btn violet' id='saveStoreOnline' type='submit'>GUARDAR TIENDA ONLINE</button></div><span id='storeUploadState' class='muted'></span></form></section>"+
            "<section class='card'><h3>¿Dónde queda todo?</h3><div class='identity-grid'><div><small>Nombre / horario</small><b>Se editan en este mismo panel.</b></div><div><small>Logo</small><b>Se guarda en almacenamiento central.</b></div><div><small>Foto del local</small><b>Se muestra en el escaparate público.</b></div></div></section>"+
            "<script>function imageData(file){return new Promise(function(ok,no){if(!file){ok(null);return;}var r=new FileReader();r.onload=function(){var im=new Image();im.onload=function(){var max=1400,w=im.naturalWidth,h=im.naturalHeight;if(w>max||h>max){var sc=Math.min(max/w,max/h);w=Math.round(w*sc);h=Math.round(h*sc);}var c=document.createElement('canvas');c.width=w;c.height=h;c.getContext('2d').drawImage(im,0,0,w,h);var d=c.toDataURL('image/jpeg',.84),raw=atob(d.split(',')[1]),a=new Uint8Array(raw.length);for(var i=0;i<raw.length;i++)a[i]=raw.charCodeAt(i);ok(new Blob([a],{type:'image/jpeg'}));};im.onerror=no;im.src=r.result;};r.onerror=no;r.readAsDataURL(file);});}function b64(buf){var a=new Uint8Array(buf),s='';for(var i=0;i<a.length;i++)s+=String.fromCharCode(a[i]);return btoa(s).replace(/\\+/g,'-').replace(/\\//g,'_').replace(/=+$/,'');}function uploadStoreFile(file,kind){return imageData(file).then(function(blob){if(!blob)return '';return blob.arrayBuffer();}).then(function(buf){if(!buf)return '';var x=new XMLHttpRequest();x.open('POST','/seller/media/upload',true);x.setRequestHeader('Content-Type','application/x-www-form-urlencoded;charset=UTF-8');x.timeout=90000;var body='fileName='+encodeURIComponent(kind+'-'+Date.now()+'.jpg')+'&contentType=image/jpeg&base64='+b64(buf);return new Promise(function(ok,no){x.onreadystatechange=function(){if(x.readyState!==4)return;if(x.status===200&&x.responseText.indexOf('OK|')===0){var z=x.responseText.split('|');ok(z.length>2?z[2]:'');}else no(new Error(x.responseText||'upload'));};x.onerror=function(){no(new Error('No se pudo subir la imagen.'));};x.ontimeout=function(){no(new Error('La carga tardó demasiado.'));};x.send(body);});});}function previewFile(input,target){var f=input.files&&input.files[0];if(!f)return;target.innerHTML='<img src=\"'+URL.createObjectURL(f)+'\" alt=\"Vista previa\"/>'; }var sf=document.getElementById('storeOnlineForm');sf.addEventListener('submit',function(e){e.preventDefault();var btn=document.getElementById('saveStoreOnline'),state=document.getElementById('storeUploadState');var lf=document.getElementById('storeLogoFile').files[0],pf=document.getElementById('storePhotoFile').files[0];btn.disabled=true;Promise.resolve().then(function(){if(lf){state.textContent='Subiendo logo...';return uploadStoreFile(lf,'store-logo').then(function(u){if(!u)throw new Error('El servidor no devolvió el logo.');document.getElementById('storeLogoUrl').value=u;});}}).then(function(){if(pf){state.textContent='Subiendo foto del local...';return uploadStoreFile(pf,'store-photo').then(function(u){if(!u)throw new Error('El servidor no devolvió la foto.');document.getElementById('storePhotoUrl').value=u;});}}).then(function(){state.textContent='Guardando configuración...';HTMLFormElement.prototype.submit.call(sf);}).catch(function(err){state.textContent='Error: '+err.message;btn.disabled=false;});});document.getElementById('storeLogoFile').addEventListener('change',function(){previewFile(this,document.getElementById('storeLogoPreview'));});document.getElementById('storePhotoFile').addEventListener('change',function(){previewFile(this,document.getElementById('storePhotoPreview'));});function geocodeStore(){var a=document.getElementById('storeAddress').value;if(!a){alert('Primero escribí la dirección.');return;}fetch('/api/geocode?q='+encodeURIComponent(a)).then(function(r){return r.text();}).then(function(t){var p=t.split('|');if(p[0]==='OK'){document.getElementById('storeLat').value=p[1];document.getElementById('storeLon').value=p[2];alert('Ubicación encontrada: '+(p[3]||a));}else alert('No se encontró la ubicación.');}).catch(function(){alert('No se pudo consultar la ubicación.');});}</script>";
        }
        private string SellerToolsView(CentralUser u,List<XElement> products,List<XElement> orders){return "<div class='section-title'><div><span class='eyebrow'>HERRAMIENTAS</span><h2>Centro de operaciones</h2><p>Accesos rápidos para gestionar tienda, catálogo, pedidos, clientes y conexión.</p></div></div><div class='quick-grid'><a href='/store/"+Uri.EscapeDataString(u.StoreId??"")+"'>"+SellerIcon("Herramientas")+"Ver escaparate público</a><a href='/seller?view=products'>"+SellerIcon("Productos")+"Catálogo e inventario ("+products.Count+")</a><a href='/seller?view=orders'>"+SellerIcon("Pedidos")+"Pedidos ("+orders.Count+")</a><a href='/seller?view=customers'>"+SellerIcon("Clientes")+"Clientes</a><a href='/seller?view=analytics'>"+SellerIcon("Analítica")+"Analítica y gráficos</a><a href='/seller?view=finance'>"+SellerIcon("Finanzas")+"Finanzas</a><a href='/seller?view=settings'>"+SellerIcon("Configuración")+"Configuración</a><a href='/seller?view=marketing'>"+SellerIcon("Marketing")+"Marketing</a></div><section class='card'><h3>Sincronización</h3><p>La PC con NexoMarket Windows publica productos, promociones, cuentas y recibe pedidos del marketplace. La misma cuenta de vendedor se utiliza en Web y Windows.</p><div class='sync-pill'>● CUENTA: "+E(u.Email)+" · ● WEB + WINDOWS ACTIVOS</div></section>";}
        private string SellerCenterCss(){return "<style>body{font-family:'Segoe UI',Arial,sans-serif;background:#000;color:#fff;margin:0;position:relative;overflow-x:hidden}body:before{content:'';position:fixed;inset:-25%;pointer-events:none;z-index:0;background:radial-gradient(ellipse at 8% 18%,transparent 0 28%,rgba(255,255,255,.045) 28.15%,transparent 28.45%),radial-gradient(ellipse at 92% 72%,transparent 0 25%,rgba(57,255,102,.035) 25.15%,transparent 25.45%);transform:rotate(-8deg);opacity:.9}body:after{content:'';position:fixed;width:70vw;height:36vh;right:-18vw;top:12vh;border:1px solid rgba(255,255,255,.07);border-left-color:rgba(57,255,102,.12);border-radius:50%;transform:rotate(-18deg);pointer-events:none;z-index:0;box-shadow:0 0 70px rgba(255,255,255,.025)}.wrap{max-width:1500px;margin:auto;padding:18px;position:relative;z-index:1}.sc-top{display:flex;justify-content:space-between;align-items:center;padding:8px 0 18px}.brand{font-weight:900;font-size:23px}.brand span{color:#39ff66}.brand small{color:#a978ff}.brand small{color:#8292a3;font-size:10px;letter-spacing:2px;margin-left:8px}.top-actions{display:flex;gap:8px}.sc-side{position:fixed;width:230px;top:78px;bottom:18px;background:#0c131c;border:1px solid #23364b;border-radius:18px;padding:14px;box-sizing:border-box;overflow-y:auto;overflow-x:hidden;scrollbar-width:thin}.account-box{border-bottom:1px solid #223246;padding:8px 5px 15px;margin-bottom:10px}.avatar{width:42px;height:42px;border-radius:12px;background:#39ff66;color:#061009;display:flex;align-items:center;justify-content:center;font-weight:900;font-size:20px;margin-bottom:8px}.account-box b,.account-box small{display:block}.account-box small{color:#788b9e;margin-top:4px;font-size:11px;word-break:break-word}.account-box .store-account-name{color:#39ff66;text-shadow:0 0 10px rgba(57,255,102,.16);font-weight:800}.btn .nav-ico{display:inline-block;vertical-align:middle;margin-right:6px}.sc-link{display:flex;align-items:center;gap:9px;color:#a8b8c8;text-decoration:none;padding:11px 12px;border-radius:10px;margin:3px 0;font-weight:700;font-size:13px}.nav-ico{width:17px;height:17px;flex:none}.quick-grid a{display:flex;align-items:center;gap:9px}.sc-link:hover,.sc-link.active{background:linear-gradient(90deg,#17231f,#1a1230);color:#b98cff;border-left:2px solid #9b5cff}.sc-main{margin-left:248px}.welcome{display:flex;justify-content:space-between;gap:15px;align-items:center;padding:24px;border:1px solid #202025;border-radius:20px;background:linear-gradient(135deg,#101923,#0b1118)}.welcome h1{margin:6px 0;font-size:31px}.welcome p,.section-title p,.card p{color:#899bac;line-height:1.5}.account-mini{min-width:180px;padding:14px;border:1px solid #2c445c;border-radius:14px;background:#0b141d}.account-mini b,.account-mini strong,.account-mini small{display:block}.account-mini strong{color:#39ff66;margin:5px 0}.account-mini small{color:#8292a3;word-break:break-word}.eyebrow{color:#b98cff;font-size:10px;letter-spacing:2px;font-weight:900}.kpis{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin:15px 0}.kpi{padding:17px;border:1px solid #202025;border-radius:16px;background:#050507}.kpi span,.kpi small{display:block;color:#8293a5;font-size:11px}.kpi strong{display:block;font-size:25px;margin:8px 0}.kpi.green{border-top:2px solid #39ff66}.kpi.yellow{border-top:2px solid #ffd34d}.kpi.red{border-top:2px solid #ff5967}.section-title{display:flex;justify-content:space-between;align-items:end;margin:22px 2px 12px}.section-title h2{margin:4px 0}.two-col{display:grid;grid-template-columns:1.3fr .7fr;gap:14px}.card{background:#050507;border:1px solid #202025;border-radius:18px;padding:18px;margin-bottom:14px}.table-wrap{overflow:auto}.table{width:100%;border-collapse:collapse}.table th,.table td{padding:12px 10px;border-bottom:1px solid #223144;text-align:left;font-size:12px;vertical-align:middle}.table th{color:#7f92a6;font-size:10px;text-transform:uppercase;letter-spacing:1px}.table td small{display:block;color:#718397;margin-top:4px}.badge{display:inline-block;padding:5px 8px;border-radius:999px;font-size:10px;font-weight:900}.badge.green{background:#153a22;color:#69ff91}.badge.yellow{background:#403816;color:#ffe36b}.badge.red{background:#401b22;color:#ff7380}.stock.low{color:#ff6572;font-weight:900}.mini-list{display:grid;gap:4px}.mini-row{display:flex;justify-content:space-between;gap:10px;padding:11px;border-bottom:1px solid #223144}.mini-row small{display:block;color:#718397;margin-top:4px}.quick-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.quick-grid a{display:block;text-decoration:none;color:#dce8f2;background:#020204;border:1px solid #273b50;border-radius:12px;padding:14px;font-weight:800}.quick-grid a:hover{border-color:#39ff66}.sync-pill{color:#8dffac;font-size:11px;font-weight:900}.form-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}.product-form input,.product-form textarea,.edit-form input,.edit-form textarea{box-sizing:border-box;background:#020204;color:#fff;border:1px solid #2a2a30;border-radius:8px;padding:9px;width:100%;margin:4px 0}.product-form textarea,.edit-form textarea{min-height:70px;grid-column:1/-1}.edit-form{min-width:650px;background:#010102;padding:10px;border:1px solid #202025;border-radius:12px}.inventory-head{margin:0 0 12px}.inventory-grid{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:12px}.inventory-card{background:#020204;border:1px solid #202025;border-radius:14px;padding:10px;min-width:0}.inventory-photo{width:100%;aspect-ratio:1/1;border-radius:10px;overflow:hidden;background:#101a24;border:1px solid #24384c;display:flex;align-items:center;justify-content:center}.inventory-photo img{width:100%;height:100%;object-fit:cover}.no-image{width:100%;height:100%;display:flex;align-items:center;justify-content:center;text-align:center;color:#62778a;font-size:11px;font-weight:900;line-height:1.4}.inventory-name{font-weight:900;margin-top:9px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.inventory-meta{color:#718397;font-size:10px;margin-top:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.inventory-bottom{display:flex;justify-content:space-between;gap:5px;margin:9px 0;font-size:11px}.inventory-stock{color:#69ff91;font-weight:900}.inventory-stock.low{color:#ff6572}.empty-inventory{padding:30px;text-align:center;color:#8194a6;border:1px dashed #2d465e;border-radius:12px}@media(max-width:1150px){.inventory-grid{grid-template-columns:repeat(4,minmax(0,1fr))}}@media(max-width:900px){.inventory-grid{grid-template-columns:repeat(3,minmax(0,1fr))}}@media(max-width:650px){.inventory-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}.danger{background:#401b22;color:#ff7380}@media(max-width:900px){.form-grid{grid-template-columns:repeat(2,1fr)}}.inline-form{display:flex;gap:5px}.inline-form select{min-width:115px;background:#020204;color:#fff;border:1px solid #2a2a30;border-radius:8px;padding:7px}.btn.violet{background:#fff;color:#000;box-shadow:0 0 18px rgba(255,255,255,.22)}.small{font-size:12px}.btn{display:inline-block;background:#39ff66;color:#061009;border:0;border-radius:9px;padding:9px 13px;font-weight:900;text-decoration:none;cursor:pointer}.btn.small{padding:7px 9px;font-size:10px}.btn.ghost{background:#101a24;color:#d9e5ef;border:1px solid #2a4056}.metric-list{display:grid;gap:10px}.metric-list div{padding:12px;border:1px solid #24374b;border-radius:10px;color:#91a1b0}.metric-list b{float:right;color:#fff}.insights{display:grid;gap:8px}.insights div{padding:12px;border-radius:10px;background:#020204;border:1px solid #223448}.section-actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.media-pickers{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;margin:10px 0}.upload-box{display:flex;flex-direction:column;gap:5px;padding:14px;border:1px dashed #3b5570;border-radius:14px;background:#020204;cursor:pointer}.upload-box span{font-size:24px}.upload-box b{font-size:13px}.upload-box small{color:#74879a}.upload-box input{margin-top:5px;width:100%}.media-preview{min-height:140px;border:1px solid #202025;border-radius:14px;background:#010102;display:flex;align-items:center;justify-content:center;color:#64788b;overflow:hidden}.media-preview img,.media-preview video{width:100%;height:100%;min-height:140px;object-fit:cover}.form-checks,.form-actions{display:flex;align-items:center;gap:16px;flex-wrap:wrap;margin-top:10px}.form-actions{justify-content:space-between}.inventory-toolbar{display:flex;gap:8px;margin-bottom:12px}.inventory-toolbar input,.inventory-toolbar select{background:#020204;color:#fff;border:1px solid #2a2a30;border-radius:9px;padding:10px}.inventory-toolbar input{flex:1}.card-actions{display:flex;align-items:center;gap:7px;flex-wrap:wrap}.video-link{font-size:10px;color:#9b5cff;font-weight:900;text-decoration:none;border:1px solid #3b2c5d;border-radius:8px;padding:7px 8px}.tool-strip{display:flex;gap:8px;flex-wrap:wrap}.tool-strip span{background:#101b26;border:1px solid #2a2a30;border-radius:999px;padding:8px 11px;color:#9aacbd;font-size:11px;font-weight:800}.pairing-card{text-align:center;max-width:720px;margin:20px auto}.pair-code{font-size:30px;font-weight:900;letter-spacing:5px;padding:20px;margin:16px 0;border:2px solid #fff;box-shadow:0 0 22px rgba(255,255,255,.16),0 0 42px rgba(57,255,102,.10);border-radius:16px;background:#090e15;word-break:break-all}.pair-instructions{background:#050509;border:1px solid #2a2a30;border-radius:12px;padding:14px;margin:14px 0;text-align:left}.pairing-shortcut{display:flex;justify-content:space-between;align-items:center;gap:15px}.settings-form label{display:block;color:#9aacbd;font-size:11px;font-weight:800}.settings-form input,.settings-form textarea{box-sizing:border-box;background:#020204;color:#fff;border:1px solid #2a2a30;border-radius:8px;padding:9px;width:100%;margin-top:5px}.settings-form textarea{min-height:120px}.identity-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}.identity-grid div{background:#020204;border:1px solid #202025;border-radius:12px;padding:14px}.identity-grid small,.identity-grid b{display:block}.identity-grid small{color:#718397;margin-bottom:6px}.identity-grid b{word-break:break-word}.chart-bars{height:205px;display:flex;align-items:end;justify-content:space-around;gap:8px;padding:15px 5px 5px;border-bottom:1px solid #26384e}.bar-col{height:190px;flex:1;display:flex;flex-direction:column;justify-content:end;align-items:center;gap:5px;color:#8293a5;font-size:9px}.bar-col span{font-size:9px;color:#dce8f2;min-height:14px}.bar{width:70%;max-width:42px;background:linear-gradient(180deg,#9b5cff,#39ff66);border-radius:7px 7px 2px 2px;min-height:4px}.status-bars{display:grid;gap:12px}.status-bars div{display:grid;grid-template-columns:1fr auto;gap:10px;align-items:center}.status-bars div:after{content:'';grid-column:1/-1;height:8px;background:linear-gradient(90deg,#9b5cff 0 55%,#1a2633 55%);border-radius:99px}.order-cards{display:grid;gap:10px}.order-card{display:grid;grid-template-columns:1.1fr 1fr .9fr auto;gap:14px;align-items:center;background:#020204;border:1px solid #202025;border-radius:14px;padding:14px}.order-card h3{margin:4px 0}.order-card small{display:block;color:#718397;margin-top:4px}.order-card .inline-form{justify-content:flex-end}@media(max-width:900px){.media-pickers{grid-template-columns:1fr 1fr}.order-card{grid-template-columns:1fr 1fr}.order-card .inline-form{grid-column:1/-1;justify-content:flex-start}.identity-grid{grid-template-columns:1fr}}@media(max-width:650px){.media-pickers{grid-template-columns:1fr}.inventory-toolbar{flex-direction:column}.order-card{grid-template-columns:1fr}.pair-code{font-size:22px;letter-spacing:2px}}@media(max-width:1050px){.kpis{grid-template-columns:repeat(2,1fr)}.sc-side{position:static;width:auto;margin-bottom:14px}.sc-main{margin-left:0}.two-col{grid-template-columns:1fr}}@media(max-width:650px){.wrap{padding:10px}.welcome,.sc-top{align-items:flex-start;flex-direction:column}.kpis,.quick-grid{grid-template-columns:1fr}.top-actions{flex-wrap:wrap}}/* NexoMarket Neon Premium UI v5.2 */body:before{background:radial-gradient(ellipse at 6% 14%,transparent 0 24%,rgba(255,255,255,.055) 24.15%,transparent 24.55%),radial-gradient(ellipse at 94% 28%,transparent 0 20%,rgba(57,255,102,.075) 20.15%,transparent 20.55%),radial-gradient(ellipse at 72% 88%,transparent 0 22%,rgba(167,103,255,.075) 22.15%,transparent 22.55%),radial-gradient(circle at 18% 82%,rgba(57,255,102,.045),transparent 22%),radial-gradient(circle at 82% 12%,rgba(167,103,255,.045),transparent 20%);opacity:1}.wrap:before{content:'';position:fixed;inset:-15%;pointer-events:none;z-index:-1;background:radial-gradient(ellipse at 12% 52%,transparent 0 27%,rgba(57,255,102,.045) 27.1%,transparent 27.45%),radial-gradient(ellipse at 86% 62%,transparent 0 23%,rgba(167,103,255,.055) 23.1%,transparent 23.45%),linear-gradient(112deg,transparent 0 42%,rgba(255,255,255,.025) 42.05%,transparent 42.2%,transparent 61%,rgba(57,255,102,.02) 61.05%,transparent 61.2%);transform:rotate(-5deg);animation:neonDrift 18s ease-in-out infinite alternate}@keyframes neonDrift{from{transform:translate3d(-1%,0,0) rotate(-5deg)}to{transform:translate3d(1%,1%,0) rotate(-3deg)}}.kpi{position:relative;overflow:hidden;background:linear-gradient(145deg,rgba(7,9,12,.96),rgba(12,15,20,.92));border-color:rgba(255,255,255,.09);box-shadow:inset 0 1px 0 rgba(255,255,255,.035),0 10px 30px rgba(0,0,0,.28);transition:transform .22s ease,border-color .22s ease,box-shadow .22s ease}.kpi:before{content:'';position:absolute;left:0;right:0;top:0;height:2px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.9),transparent);opacity:.8}.kpi:after{content:'';position:absolute;width:130px;height:130px;right:-70px;top:-70px;border-radius:50%;border:1px solid rgba(255,255,255,.08);box-shadow:0 0 35px rgba(255,255,255,.04)}.kpi:hover{transform:translateY(-4px);border-color:rgba(255,255,255,.18);box-shadow:0 0 24px rgba(255,255,255,.045),0 14px 38px rgba(0,0,0,.38)}.kpi.green{border-top:2px solid #39ff66;box-shadow:0 0 24px rgba(57,255,102,.055),inset 0 1px 0 rgba(255,255,255,.03)}.kpi.yellow{border-top:2px solid #fff;box-shadow:0 0 24px rgba(255,255,255,.045),inset 0 1px 0 rgba(255,255,255,.03)}.kpi.red{border-top:2px solid #a767ff;box-shadow:0 0 24px rgba(167,103,255,.055),inset 0 1px 0 rgba(255,255,255,.03)}.kpi strong{color:#fff;text-shadow:0 0 14px rgba(255,255,255,.16)}.kpi.green strong{color:#dffff0;text-shadow:0 0 14px rgba(57,255,102,.16)}.kpi.red strong{color:#f1e8ff;text-shadow:0 0 14px rgba(167,103,255,.16)}.welcome,.card,.account-mini,.sc-side{background:linear-gradient(145deg,rgba(7,9,12,.94),rgba(11,14,19,.91));border-color:rgba(255,255,255,.075);box-shadow:0 16px 45px rgba(0,0,0,.24),inset 0 1px 0 rgba(255,255,255,.025)}.quick-grid a{position:relative;overflow:hidden;background:linear-gradient(145deg,#030405,#0a0d12);border-color:rgba(255,255,255,.10);box-shadow:inset 0 1px 0 rgba(255,255,255,.035),0 8px 24px rgba(0,0,0,.25);transition:transform .2s ease,border-color .2s ease,box-shadow .2s ease}.quick-grid a:before{content:'';position:absolute;left:-25%;right:-25%;top:0;height:1px;background:linear-gradient(90deg,transparent,#39ff66,#fff,#a767ff,transparent);opacity:.65}.quick-grid a:hover{transform:translateY(-3px);border-color:rgba(255,255,255,.25);box-shadow:0 0 22px rgba(167,103,255,.08),0 12px 30px rgba(0,0,0,.35)}.btn{transition:transform .18s ease,box-shadow .18s ease,filter .18s ease}.btn:hover{transform:translateY(-1px);filter:brightness(1.06);box-shadow:0 0 20px rgba(255,255,255,.10)}.btn.violet{background:#fff;color:#050505;box-shadow:0 0 20px rgba(255,255,255,.20),inset 0 -1px 0 rgba(0,0,0,.18)}.sc-link:hover,.sc-link.active{background:linear-gradient(90deg,rgba(57,255,102,.08),rgba(167,103,255,.10));color:#fff;border-left:2px solid #39ff66;box-shadow:0 0 18px rgba(57,255,102,.05)}.inventory-card{transition:transform .2s ease,box-shadow .2s ease,border-color .2s ease}.inventory-card:hover{border-color:rgba(255,255,255,.18);box-shadow:0 0 24px rgba(167,103,255,.06);transform:translateY(-2px)}.eyebrow{color:#a767ff;text-shadow:0 0 12px rgba(167,103,255,.18)}.sync-pill{color:#bfffd3;text-shadow:0 0 12px rgba(57,255,102,.15)}.store-toggle{display:inline-flex;margin:0}.danger-outline{background:rgba(255,255,255,.025)!important;color:#fff!important;border:1px solid rgba(167,103,255,.55)!important;box-shadow:0 0 16px rgba(167,103,255,.08)}.danger-outline:hover{border-color:#fff!important;box-shadow:0 0 20px rgba(255,255,255,.16),0 0 28px rgba(167,103,255,.10)!important}.btn,.sc-link,.quick-grid a{transition:all .22s ease}.btn:not(.violet):hover{background:rgba(255,255,255,.035)!important;color:#fff!important;border-color:rgba(255,255,255,.75)!important;box-shadow:0 0 10px rgba(255,255,255,.12),0 0 22px rgba(255,255,255,.08),inset 0 0 10px rgba(255,255,255,.025)!important}.btn.violet:hover{box-shadow:0 0 12px rgba(255,255,255,.55),0 0 30px rgba(255,255,255,.20),inset 0 0 12px rgba(255,255,255,.25)!important}.sc-link:hover{border-left-color:#fff!important;box-shadow:0 0 16px rgba(255,255,255,.07),inset 0 0 14px rgba(255,255,255,.025)!important}.kpi.green strong{text-shadow:0 0 6px rgba(57,255,102,.30),0 0 16px rgba(57,255,102,.12)!important}.kpi.green{border-top-color:#39ff66!important;box-shadow:0 0 22px rgba(57,255,102,.08),inset 0 1px 0 rgba(255,255,255,.04)!important}.welcome,.card{position:relative;overflow:hidden}.welcome:before,.card:before{content:'';display:block;position:absolute;pointer-events:none;left:8%;right:8%;top:0;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.34),rgba(57,255,102,.20),rgba(167,103,255,.24),transparent);opacity:.75}.sc-top{position:relative}.sc-top:after{content:'';position:absolute;left:0;right:0;bottom:5px;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.18),rgba(57,255,102,.20),rgba(167,103,255,.18),transparent);pointer-events:none}.side-store-status{margin:10px 0 12px}.store-status-btn{width:100%;display:flex;align-items:center;gap:10px;background:#050507;border:1px solid rgba(255,255,255,.12);border-radius:13px;padding:11px;color:#fff;cursor:pointer;text-align:left;box-shadow:0 0 18px rgba(0,0,0,.25);transition:.2s}.store-status-btn:hover{border-color:#fff;box-shadow:0 0 20px rgba(255,255,255,.13),0 0 30px rgba(57,255,102,.06)}.store-status-btn .status-dot{width:10px;height:10px;border-radius:50%;flex:none}.store-status-btn span:nth-child(2){display:flex;flex-direction:column;gap:2px;flex:1}.store-status-btn b{font-size:10px;letter-spacing:1px}.store-status-btn small{font-size:11px}.store-status-btn strong{font-size:10px;letter-spacing:.8px}.store-status-btn.is-open .status-dot{background:#39ff66;box-shadow:0 0 10px #39ff66}.store-status-btn.is-open small{color:#39ff66}.store-status-btn.is-closed .status-dot{background:#ff3b5f;box-shadow:0 0 10px #ff3b5f}.store-status-btn.is-closed small{color:#ff6b83}.open.closed{color:#ff6b83}.store-toggle{display:inline-flex;margin:0}.new-order-alert{position:fixed;left:50%;top:18px;transform:translateX(-50%);z-index:99999;display:none;min-width:min(680px,calc(100vw - 28px));padding:18px 24px;border:2px solid #ff304f;border-radius:16px;background:linear-gradient(135deg,#5a0714,#a9001f 55%,#4d0612);color:#fff;text-align:center;font-weight:900;box-shadow:0 0 22px rgba(255,48,79,.55),0 16px 45px rgba(0,0,0,.55)}.new-order-alert.show{display:block;animation:orderAlertBlink .5s steps(1,end) 20}.new-order-alert .oa-title{font-size:20px;letter-spacing:1px}.new-order-alert .oa-sub{font-size:12px;margin-top:5px;color:#ffe7eb}@keyframes orderAlertBlink{0%,100%{opacity:1;filter:brightness(1.15)}50%{opacity:.12;filter:brightness(1)}}.order-interactive{cursor:pointer}.order-modal{position:fixed;inset:0;background:rgba(0,0,0,.72);z-index:100000;display:none;align-items:center;justify-content:center;padding:20px}.order-modal.open{display:flex}.order-modal-card{width:min(900px,96vw);max-height:90vh;overflow:auto;background:#080b10;border:1px solid rgba(255,255,255,.16);border-radius:20px;padding:22px;box-shadow:0 20px 70px rgba(0,0,0,.65)}.order-modal-head{display:flex;justify-content:space-between;align-items:center;gap:12px;border-bottom:1px solid rgba(255,255,255,.08);padding-bottom:12px}.order-detail-body{margin-top:15px}.od-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.od-grid>div,.od-item{background:#0d141c;border:1px solid rgba(255,255,255,.08);border-radius:13px;padding:12px}.od-grid span,.od-grid strong{display:block;margin-top:5px}.od-items{display:grid;gap:8px}.od-item{display:flex;justify-content:space-between;gap:15px}.od-item small{display:block;color:#9aa9b7;margin-top:4px}.od-message{margin-top:12px;color:#d6e1ea}.order-modal-actions{display:flex;gap:10px;margin-top:18px;flex-wrap:wrap}.commission-card{border-color:rgba(212,175,55,.45)!important}.commission-percent{font-size:28px;font-weight:900;color:#ffd84d;text-shadow:0 0 12px rgba(255,216,77,.25)}.commission-grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px}.commission-grid>div{background:#0d141c;border:1px solid rgba(212,175,55,.16);border-radius:12px;padding:12px}.commission-grid small,.commission-grid strong{display:block}.commission-grid small{color:#9a9583}.commission-grid strong{margin-top:6px;font-size:18px}.commission-due{color:#ffd84d!important}@media(max-width:800px){.commission-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.od-grid{grid-template-columns:1fr}.od-item{flex-direction:column}.order-modal-card{padding:15px}}</style>";}

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
            // Esta acción se ejecuta por AJAX desde la misma pantalla de Pedidos.
            // Nunca redirigimos a /login ni a la portada: una actualización de estado
            // debe quedarse en la consola del vendedor y devolver sólo el resultado.
            CentralUser u=SessionUser(cookie);
            if(u==null||u.Role!="seller")
            {
                Write(stream,401,"application/json; charset=utf-8","{\"ok\":false,\"session\":true,\"message\":\"La sesión del vendedor ya no es válida.\"}");
                return;
            }
            string id=Get(f,"id"), status=Get(f,"status");
            if(string.IsNullOrWhiteSpace(id)||string.IsNullOrWhiteSpace(status))
            {
                Write(stream,400,"application/json; charset=utf-8","{\"ok\":false,\"message\":\"Faltan datos del pedido.\"}");
                return;
            }
            string result=UpdateOrderStatus(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"storeId",u.StoreId},{"syncKey",ComputeStorePairKey(u.StoreId)},{"centralOrderId",id},{"status",status}});
            if(result.StartsWith("OK|",StringComparison.OrdinalIgnoreCase))
                Write(stream,200,"application/json; charset=utf-8","{\"ok\":true,\"id\":"+JsonString(id)+",\"status\":"+JsonString(status)+"}");
            else
                Write(stream,400,"application/json; charset=utf-8","{\"ok\":false,\"message\":"+JsonString("No se pudo actualizar el pedido: "+result)+"}");
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
            b.Append("</table></section></main>");b.Append("<style>.buyer-main{max-width:1200px;margin:auto}.store-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.store-card{display:flex;gap:12px;align-items:center;background:#050507;border:1px solid #202025;border-radius:16px;padding:15px;color:#fff;text-decoration:none}.store-card:hover{border-color:#39ff66}.store-logo{width:64px;height:64px;border-radius:14px;background:#0b1118;border:1px solid #7b4bd1;color:#b98cff;display:flex;align-items:center;justify-content:center;font-weight:900;font-size:23px;overflow:hidden}.store-logo img{width:100%;height:100%;object-fit:cover}.store-card b,.store-card small,.store-card span{display:block}.store-card small{color:#8193a5;margin-top:4px}.store-card span{color:#39ff66;font-size:10px;margin-top:6px}@media(max-width:1000px){.store-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:650px){.store-grid{grid-template-columns:1fr}}</style>");b.Append(AuthShellEnd());Write(stream,200,"text/html; charset=utf-8",b.ToString());
        }

        private string AuthPage(string title,string content){return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · NexoMarket</title><style>"+AuthCss()+"</style></head><body><div class='wrap'>"+content+"</div></body></html>";}
        private string AuthShellStart(string title){return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>"+E(title)+" · NexoMarket</title><style>"+AuthCss()+"</style></head><body><div class='wrap'>";}
        private string AuthShellEnd(){return "</div></body></html>";}
        private string AuthCss(){return "body{font-family:'Segoe UI',Arial;background:#000;color:#fff;margin:0;position:relative;overflow-x:hidden}body:before{content:'';position:fixed;inset:-30%;pointer-events:none;z-index:0;background:radial-gradient(ellipse at 12% 20%,transparent 0 27%,rgba(255,255,255,.045) 27.15%,transparent 27.5%),radial-gradient(ellipse at 88% 78%,transparent 0 23%,rgba(57,255,102,.035) 23.15%,transparent 23.5%);transform:rotate(-10deg)}.wrap{max-width:850px;margin:auto;padding:30px;position:relative;z-index:1}.card,.empty{background:#050507;border:1px solid #2a4660;border-radius:18px;padding:20px;margin-top:16px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px}input,select,textarea{display:block;width:100%;box-sizing:border-box;background:#0b131c;color:#fff;border:1px solid #2d4660;border-radius:10px;padding:12px;margin:10px 0}textarea{min-height:120px;resize:vertical}.btn{display:inline-block;background:#39ff66;color:#061009;text-decoration:none;border:0;border-radius:10px;padding:11px 16px;font-weight:900;cursor:pointer;margin-top:8px}.btn.alt{background:#13202c;color:#fff;border:1px solid #2d4660}.muted{color:#91a4b6}.error{background:#34151b;border:1px solid #6b2630;padding:14px;border-radius:12px;margin-bottom:14px}.empty{color:#9aabba}body:after{content:'';position:fixed;inset:-20%;pointer-events:none;z-index:0;background:radial-gradient(ellipse at 12% 62%,transparent 0 24%,rgba(57,255,102,.055) 24.15%,transparent 24.5%),radial-gradient(ellipse at 86% 32%,transparent 0 21%,rgba(167,103,255,.06) 21.15%,transparent 21.5%),radial-gradient(circle at 50% 8%,rgba(255,255,255,.035),transparent 25%);transform:rotate(-8deg);opacity:.9}.wrap:before{content:'';position:fixed;inset:-10%;pointer-events:none;z-index:-1;background:linear-gradient(118deg,transparent 0 36%,rgba(255,255,255,.025) 36.1%,transparent 36.3%,transparent 63%,rgba(57,255,102,.025) 63.1%,transparent 63.3%);animation:authNeon 16s ease-in-out infinite alternate}@keyframes authNeon{from{transform:translateX(-1%) rotate(-2deg)}to{transform:translateX(1%) rotate(1deg)}}.btn:hover{background:#fff!important;color:#000!important;box-shadow:0 0 14px rgba(255,255,255,.55),0 0 30px rgba(255,255,255,.12)!important;transform:translateY(-1px)}input:focus,select:focus,textarea:focus{outline:none;border-color:#fff;box-shadow:0 0 0 1px rgba(255,255,255,.2),0 0 18px rgba(255,255,255,.07)}";}

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
            public string Id="",Name="",Email="",Phone="",Role="buyer",StoreId="",Salt="",PasswordHash="",CreatedAt="",TrialExpiresAt=""; public bool Active=true;
            public static CentralUser From(XElement e){return new CentralUser{Id=S(e,"Id"),Name=S(e,"Name"),Email=S(e,"Email"),Phone=S(e,"Phone"),Role=S(e,"Role")=="seller"?"seller":"buyer",StoreId=S(e,"StoreId"),Salt=S(e,"Salt"),PasswordHash=S(e,"PasswordHash"),CreatedAt=S(e,"CreatedAt"),TrialExpiresAt=S(e,"TrialExpiresAt"),Active=S(e,"Active")=="0"?false:true};}
            public static CentralUser From(Dictionary<string,string> d){return new CentralUser{Id=d.ContainsKey("id")?d["id"]:"",Name=d.ContainsKey("name")?d["name"]:"",Email=d.ContainsKey("email")?d["email"]:"",Phone=d.ContainsKey("phone")?d["phone"]:"",Role=d.ContainsKey("role")&&d["role"]=="seller"?"seller":"buyer",StoreId=d.ContainsKey("storeId")?d["storeId"]:"",Salt=d.ContainsKey("salt")?d["salt"]:"",PasswordHash=d.ContainsKey("passwordHash")?d["passwordHash"]:"",CreatedAt=d.ContainsKey("createdAt")?d["createdAt"]:"",TrialExpiresAt=d.ContainsKey("trialExpiresAt")?d["trialExpiresAt"]:"",Active=!d.ContainsKey("active")||d["active"]!="0"};}
        }
        private sealed class CentralStore
        {
            public string StoreId = ""; public string Name = ""; public string Category = ""; public string City = ""; public string Province = ""; public string PublicUrl = ""; public string Logo = ""; public string StorePhoto = ""; public string Address = ""; public string Description = ""; public bool Delivery; public bool Pickup; public double Latitude; public double Longitude; public double Distance; public bool Active; public bool Featured; public bool FeaturedPlus; public bool Listed; public string RatingSummary = "0.0|0";
            public CentralStore() { }
            public CentralStore(XElement e) { StoreId=S(e,"StoreId"); Name=S(e,"Name"); Category=S(e,"Category"); City=S(e,"City"); Province=S(e,"Province"); PublicUrl=S(e,"PublicUrl"); Logo=S(e,"Logo"); StorePhoto=S(e,"StorePhoto"); Address=S(e,"Address"); Description=S(e,"Description"); Delivery=S(e,"Delivery")=="1"; Pickup=S(e,"Pickup")=="1"; Active=S(e,"Active")=="1"; Featured=S(e,"Featured")=="1"; FeaturedPlus=S(e,"FeaturedPlus")=="1"; Listed=S(e,"Listed")!="0"; RatingSummary="0.0|0"; double.TryParse(S(e,"Latitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out Latitude); double.TryParse(S(e,"Longitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out Longitude); }
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
