using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using NexoMarket.Admin.Data;
using NexoMarket.Admin.Models;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Sincronizador central: publica catálogo/promociones y recupera pedidos pendientes.
    /// Diseñado para .NET Framework 4.x y funciona sin dejar puertos abiertos en la PC.
    /// </summary>
    public sealed class CentralSyncService : IDisposable
    {
        private readonly AppDataStore _store;
        private Timer _timer;
        private volatile bool _busy;
        private const string DefaultCentralUrl = "https://nexomarket-central.onrender.com";
        public CentralSyncService(AppDataStore store) { _store = store; }
        public void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(delegate { SyncOnce(); }, null, 2000, 3000);
        }
        public void Dispose() { if (_timer != null) { try { _timer.Dispose(); } catch { } _timer = null; } }
        public void SyncOnce()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
            if (_busy || !Enabled()) return;
            _busy = true;
            try
            {
                string baseUrl = ResolveCentralBaseUrl();
                if (baseUrl.Length == 0) return;
                PublishStore(baseUrl);
                // Central es la fuente canónica de identidad. Primero descargamos la cuenta
                // vinculada al Store ID; recién después publicamos la copia local.
                PullAccounts(baseUrl);
                PublishAccounts(baseUrl);
                List<Product> products = _store.GetProducts("");
                foreach (Product p in products) PublishProduct(baseUrl, p);
                List<Promotion> promotions = _store.GetPromotions();
                foreach (Promotion p in promotions) PublishPromotion(baseUrl, p);
                PullProducts(baseUrl);
                string pending = Request(baseUrl + "/api/orders/pending?storeId=" + Uri.EscapeDataString(_store.StoreId), "GET", null);
                foreach (Dictionary<string,string> order in ParseObjects(pending))
                {
                    string centralId = Get(order, "centralOrderId");
                    if (centralId.Length == 0 || AlreadyImported(centralId)) continue;
                    Order o = new Order();
                    o.CentralOrderId = centralId;
                    o.CustomerId = ParseLong(Get(order,"customerId"));
                    o.CustomerName = Get(order,"customerName"); o.CustomerEmail = Get(order,"customerEmail"); o.Phone = Get(order,"phone");
                    o.Fulfillment = Get(order,"fulfillment"); o.Address = Get(order,"address"); o.Notes = Get(order,"notes"); o.Status = Get(order,"status");
                    o.Total = ParseDecimal(Get(order,"total")); o.PaymentMethod = Get(order,"paymentMethod"); o.PaymentStatus = Get(order,"paymentStatus");
                    o.PaymentReference = Get(order,"paymentReference"); o.PaymentProofPath = Get(order,"paymentProofPath"); o.ShippingCost = ParseDecimal(Get(order,"shippingCost"));
                    o.TrackingNumber = Get(order,"trackingNumber"); o.Carrier = Get(order,"carrier"); o.ItemsJson = Get(order,"itemsJson"); o.BuyerMessage = Get(order,"buyerMessage");
                    o.StoreId = _store.StoreId; o.Source = "Web Central"; o.CreatedAt = ParseDate(Get(order,"createdAt"));
                    ApplyOrderStock(o.ItemsJson);
                    _store.AddOrder(o);
                    Request(baseUrl + "/api/orders/ack", "POST", Form(new Dictionary<string,string>{{"storeId",_store.StoreId},{"centralOrderId",centralId}}));
                }
                _store.SetSetting("central_sync_last", DateTime.Now.ToString("o"));
            }
            catch (Exception ex)
            {
                try { _store.SetSetting("central_sync_last_error", ex.GetType().Name + ":" + (ex.Message ?? "")); } catch { }
            }
            finally { _busy = false; }
        }
        private bool Enabled() { return true; }
        private bool AlreadyImported(string id) { foreach (Order o in _store.GetOrders("")) if (string.Equals(o.CentralOrderId,id,StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private void PublishStore(string baseUrl)
        {
            try
            {
                StoreDirectoryClient client = new StoreDirectoryClient(_store);
                if (!client.PublishStore(_store.GetSetting("web_public_url", "")))
                    _store.SetSetting("central_sync_last_error", "store_publish_failed");
                else
                    _store.SetSetting("central_sync_last_error", "");
            }
            catch (Exception ex)
            {
                _store.SetSetting("central_sync_last_error", "store_publish:" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Conecta una instalación de Windows únicamente mediante StoreId.
        /// Central devuelve la identidad de la tienda, su clave de sincronización y la cuenta
        /// de vendedor asociada. El StoreId pasa a ser la identidad común entre Windows y Web.
        /// </summary>
        public bool ConnectByStoreId(string storeId, out string storeName, out string sellerEmail, out string sellerName)
        {
            storeName = ""; sellerEmail = ""; sellerName = "";
            try
            {
                string id = NormalizeStoreId(storeId);
                if (id.Length == 0) return false;
                string configuredUrl = (_store.GetSetting("web_api_url", "") ?? "").Trim();
                if (IsLegacyLocalUrl(configuredUrl) || string.IsNullOrWhiteSpace(configuredUrl) || configuredUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0)
                    configuredUrl = GetCentralUrl();
                string baseUrl = Normalize(configuredUrl);
                if (baseUrl.Length == 0) return false;
                string health = Request(baseUrl + "/health", "GET", null);
                if (string.IsNullOrWhiteSpace(health) || health.IndexOf("NexoMarket Central", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _store.SetSetting("central_sync_last_error", "central_unreachable");
                    return false;
                }
                string response = Request(baseUrl + "/api/stores/connect?storeId=" + Uri.EscapeDataString(id), "GET", null);
                string[] p = (response ?? "").Split('|');
                if (p.Length < 5 || !string.Equals(p[0], "OK", StringComparison.OrdinalIgnoreCase))
                {
                    string reason = p.Length > 1 ? Decode(p[1]) : "no_response";
                    // Si la tienda fue creada en Windows y todavía no llegó al Central,
                    // la vinculamos por StoreId. No se pide correo ni contraseña.
                    if (string.Equals(reason, "store_not_found", StringComparison.OrdinalIgnoreCase))
                    {
                        string bootstrapKey = (_store.GetSetting("central_sync_key", "") ?? "").Trim();
                        if (bootstrapKey.Length < 16) { bootstrapKey = Guid.NewGuid().ToString("N"); _store.SetSetting("central_sync_key", bootstrapKey); }
                        string claim = Request(baseUrl + "/api/stores/claim", "POST", Form(new Dictionary<string,string>
                        {
                            {"storeId", id}, {"syncKey", bootstrapKey},
                            {"name", _store.GetSetting("store_name", "Tienda NexoMarket")},
                            {"legalName", _store.GetSetting("store_legal_name", "")},
                            {"category", _store.GetSetting("store_category", "Comercio")},
                            {"address", _store.GetSetting("store_address", "")},
                            {"city", _store.GetSetting("store_city", "")},
                            {"province", _store.GetSetting("store_province", "")},
                            {"description", _store.GetSetting("store_description", "Tienda NexoMarket")},
                            {"logo", _store.GetSetting("store_logo", "")},
                            {"slug", _store.GetSetting("store_slug", "")},
                            {"publicUrl", _store.GetSetting("web_public_url", "")},
                            {"delivery", _store.GetSetting("delivery_enabled", "1")},
                            {"pickup", _store.GetSetting("pickup_enabled", "1")},
                            {"latitude", _store.GetSetting("store_latitude", "")},
                            {"longitude", _store.GetSetting("store_longitude", "")}
                        }));
                        if (claim != null && claim.StartsWith("OK|", StringComparison.OrdinalIgnoreCase))
                        {
                            response = Request(baseUrl + "/api/stores/connect?storeId=" + Uri.EscapeDataString(id), "GET", null);
                            p = (response ?? "").Split('|');
                        }
                    }
                    if (p.Length < 5 || !string.Equals(p[0], "OK", StringComparison.OrdinalIgnoreCase))
                    {
                        reason = p.Length > 1 ? Decode(p[1]) : "no_response";
                        _store.SetSetting("central_sync_last_error", "store_connect:" + reason);
                        return false;
                    }
                }
                if (!string.Equals(Decode(p[1]), id, StringComparison.OrdinalIgnoreCase)) return false;
                storeName = Decode(p[2]);
                string active = Decode(p[3]);
                string syncKey = Decode(p[4]);
                sellerEmail = Decode(p.Length > 5 ? p[5] : "");
                sellerName = Decode(p.Length > 6 ? p[6] : "");
                if (active != "1" || string.IsNullOrWhiteSpace(syncKey)) return false;

                // La PC adopta el StoreId central como identidad de conexión y trae el perfil
                // de la tienda antes de publicar nada, evitando que los valores locales por defecto
                // sobrescriban una tienda creada desde la web.
                _store.SetSetting("store_id", id);
                _store.SetSetting("central_sync_key", syncKey);
                _store.SetSetting("web_api_url", baseUrl);
                _store.SetSetting("web_public_url", baseUrl);
                _store.SetSetting("web_sync_enabled", "1");
                _store.SetSetting("store_web_active", "1");
                if (!string.IsNullOrWhiteSpace(storeName)) _store.SetSetting("store_name", storeName);
                SetCentralSetting(p, 8, "store_legal_name");
                SetCentralSetting(p, 9, "store_category");
                SetCentralSetting(p, 10, "store_address");
                SetCentralSetting(p, 11, "store_city");
                SetCentralSetting(p, 12, "store_province");
                SetCentralSetting(p, 13, "store_description");
                SetCentralSetting(p, 14, "store_logo");
                SetCentralSetting(p, 15, "store_slug");
                SetCentralSetting(p, 16, "web_public_url");
                SetCentralSetting(p, 17, "store_web_active");
                SetCentralSetting(p, 18, "delivery_enabled");
                SetCentralSetting(p, 19, "pickup_enabled");
                SetCentralSetting(p, 20, "store_latitude");
                SetCentralSetting(p, 21, "store_longitude");
                if (!string.IsNullOrWhiteSpace(sellerEmail)) _store.SetSetting("seller_account_email", sellerEmail);
                if (!string.IsNullOrWhiteSpace(sellerName)) _store.SetSetting("seller_account_name", sellerName);
                _store.SetSetting("seller_account_locked", "1");
                _store.SetSetting("central_sync_last_error", "");
                PullAccounts(baseUrl);
                return true;
            }
            catch (Exception ex)
            {
                try { _store.SetSetting("central_sync_last_error", "store_connect:" + ex.GetType().Name + ":" + (ex.Message ?? "")); } catch { }
                return false;
            }
        }

        private void SetCentralSetting(string[] parts, int index, string key)
        {
            if (parts == null || index >= parts.Length) return;
            string value = Decode(parts[index]);
            if (!string.IsNullOrWhiteSpace(value)) _store.SetSetting(key, value);
        }

        public bool PublishAccountNow(WebUser user)
        {
            try
            {
                string configuredUrl = (_store.GetSetting("web_api_url", "") ?? "").Trim();
                if (IsLegacyLocalUrl(configuredUrl) || string.IsNullOrWhiteSpace(configuredUrl) || configuredUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0) configuredUrl = GetCentralUrl();
                string baseUrl = Normalize(configuredUrl);
                if (baseUrl.Length == 0 || user == null) return false;
                // Publicar primero la tienda para que el servidor pueda validar la relación cuenta -> StoreId.
                try { new StoreDirectoryClient(_store).PublishStore(_store.GetSetting("web_public_url", "")); } catch { }
                string syncKey = _store.GetSetting("central_sync_key", "") ?? "";
                string response = Request(baseUrl+"/api/accounts/upsert","POST",Form(new Dictionary<string,string>{{"id",user.Id.ToString(CultureInfo.InvariantCulture)},{"name",user.Name},{"email",user.Email},{"phone",user.Phone},{"role",user.Role},{"storeId",user.StoreId},{"syncKey",syncKey},{"salt",user.Salt},{"passwordHash",user.PasswordHash},{"createdAt",user.CreatedAt.ToUniversalTime().ToString("o")}}));
                return response.StartsWith("OK|",StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                try { _store.SetSetting("central_sync_last_error", "account_publish:" + ex.GetType().Name); } catch { }
                return false;
            }
        }

        private void PublishAccounts(string baseUrl)
        {
            try
            {
                foreach (var u in _store.GetWebUsers())
                {
                    Request(baseUrl+"/api/accounts/upsert","POST",Form(new Dictionary<string,string>{{"id",u.Id.ToString(CultureInfo.InvariantCulture)},{"name",u.Name},{"email",u.Email},{"phone",u.Phone},{"role",u.Role},{"storeId",u.StoreId},{"syncKey",_store.GetSetting("central_sync_key","")},{"salt",u.Salt},{"passwordHash",u.PasswordHash},{"createdAt",u.CreatedAt.ToUniversalTime().ToString("o")}}));
                }
            }
            catch { }
        }

        private void PullAccounts(string baseUrl)
        {
            try
            {
                string storeId = _store.StoreId;
                if (string.IsNullOrWhiteSpace(storeId)) return;
                string syncKey = _store.GetSetting("central_sync_key", "") ?? "";
                string response = Request(baseUrl + "/api/accounts?storeId=" + Uri.EscapeDataString(storeId) + "&syncKey=" + Uri.EscapeDataString(syncKey), "GET", null);
                using (StringReader reader = new StringReader(response ?? ""))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.StartsWith("ACCOUNT|", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] p = line.Split('|');
                        if (p.Length < 10) continue;
                        WebUser u = new WebUser();
                        u.Name = Decode(p[2]); u.Email = Decode(p[3]); u.Phone = Decode(p[4]);
                        u.Role = Decode(p[5]) == "seller" ? "seller" : "buyer"; u.StoreId = Decode(p[6]);
                        u.Salt = Decode(p[7]); u.PasswordHash = Decode(p[8]); u.CreatedAt = ParseDate(Decode(p[9]));
                        if (string.IsNullOrWhiteSpace(u.Email) || string.IsNullOrWhiteSpace(u.StoreId)) continue;
                        if (!_store.UpsertWebUserFromCentral(u)) continue;
                        if (u.Role == "seller" && string.Equals(u.StoreId, storeId, StringComparison.OrdinalIgnoreCase))
                        {
                            _store.SetSetting("seller_account_email", u.Email);
                            _store.SetSetting("seller_account_name", u.Name ?? "");
                            _store.SetSetting("seller_account_locked", "1");
                        }
                    }
                }
            }
            catch { }
        }

        private static string Decode(string value)
        {
            try { return Uri.UnescapeDataString(value ?? ""); } catch { return value ?? ""; }
        }

        /// <summary>Autentica contra NexoMarket Central y devuelve la misma identidad que usa la web.
        /// Esto evita que Windows y la web mantengan dos cuentas paralelas con el mismo correo.
        /// </summary>
        public bool AuthenticateCentral(string email, string password, out WebUser user)
        {
            user = null;
            try
            {
                string configuredUrl = (_store.GetSetting("web_api_url", "") ?? "").Trim();
                if (IsLegacyLocalUrl(configuredUrl) || string.IsNullOrWhiteSpace(configuredUrl) || configuredUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0) configuredUrl = GetCentralUrl();
                string baseUrl = Normalize(configuredUrl);
                string response = Request(baseUrl + "/api/accounts/auth", "POST", Form(new Dictionary<string,string>
                { {"email", (email ?? "").Trim().ToLowerInvariant()}, {"password", password ?? ""} }));
                string[] p = (response ?? "").Split('|');
                if (p.Length < 10 || !string.Equals(p[0], "OK", StringComparison.OrdinalIgnoreCase)) return false;
                user = new WebUser
                {
                    Name = Decode(p[2]), Email = Decode(p[3]), Phone = Decode(p[4]),
                    Role = string.Equals(Decode(p[5]), "seller", StringComparison.OrdinalIgnoreCase) ? "seller" : "buyer",
                    StoreId = Decode(p[6]), Salt = Decode(p[7]), PasswordHash = Decode(p[8]), CreatedAt = ParseDate(Decode(p[9]))
                };
                if (string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.PasswordHash) || string.IsNullOrWhiteSpace(user.Salt)) return false;
                _store.UpsertWebUserFromCentral(user);
                if (user.Role == "seller")
                {
                    _store.SetSetting("seller_account_email", user.Email);
                    _store.SetSetting("seller_account_name", user.Name ?? "");
                    _store.SetSetting("seller_account_locked", "1");
                    if (!string.IsNullOrWhiteSpace(user.StoreId)) _store.SetSetting("store_id", user.StoreId);
                }
                return true;
            }
            catch { return false; }
        }
        public bool PublishProductNow(Product p)
        {
            try
            {
                string baseUrl = ResolveCentralBaseUrl();
                if (baseUrl.Length == 0 || p == null) return false;
                PublishProduct(baseUrl, p);
                return true;
            }
            catch { return false; }
        }

        public bool PublishPromotionNow(Promotion p)
        {
            try
            {
                string baseUrl = ResolveCentralBaseUrl();
                if (baseUrl.Length == 0 || p == null) return false;
                PublishPromotion(baseUrl, p);
                return true;
            }
            catch { return false; }
        }

        private string ResolveCentralBaseUrl()
        {
            string configuredUrl = (_store.GetSetting("web_api_url", "") ?? "").Trim();
            if (IsLegacyLocalUrl(configuredUrl) || string.IsNullOrWhiteSpace(configuredUrl) || configuredUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) >= 0)
                configuredUrl = GetCentralUrl();
            return Normalize(configuredUrl);
        }

        private void PublishProduct(string baseUrl, Product p)
        {
            Request(baseUrl+"/api/products/publish","POST",Form(new Dictionary<string,string>{{"storeId",_store.StoreId},{"syncKey",_store.GetSetting("central_sync_key","")},{"productId",p.Id.ToString(CultureInfo.InvariantCulture)},{"name",p.Name},{"category",p.Category},{"description",p.Description},{"price",p.Price.ToString(CultureInfo.InvariantCulture)},{"salePrice",p.SalePrice.ToString(CultureInfo.InvariantCulture)},{"stock",p.Stock.ToString(CultureInfo.InvariantCulture)},{"minimumStock",p.MinimumStock.ToString(CultureInfo.InvariantCulture)},{"sku",p.SKU},{"brand",p.Brand},{"size",p.Size},{"color",p.Color},{"active",p.Active?"1":"0"},{"onlineEnabled",p.OnlineEnabled?"1":"0"},{"imagePath",p.ImagePath},{"slug",p.Slug},{"publicDescription",p.PublicDescription},{"updatedAt",DateTime.UtcNow.ToString("o")}}));
        }
        private void PublishPromotion(string baseUrl, Promotion p)
        { Request(baseUrl+"/api/promotions/publish","POST",Form(new Dictionary<string,string>{{"storeId",_store.StoreId},{"syncKey",_store.GetSetting("central_sync_key","")},{"promotionId",p.Id.ToString(CultureInfo.InvariantCulture)},{"name",p.Name},{"productIds",p.ProductIds},{"promotionalPrice",p.PromotionalPrice.ToString(CultureInfo.InvariantCulture)},{"active",p.Active?"1":"0"},{"from",p.From.ToString("o")},{"to",p.To.ToString("o")},{"updatedAt",DateTime.UtcNow.ToString("o")}})); }

        private void PullProducts(string baseUrl)
        {
            try
            {
                string response = Request(baseUrl + "/api/catalog/lines?storeId=" + Uri.EscapeDataString(_store.StoreId) + "&syncKey=" + Uri.EscapeDataString(_store.GetSetting("central_sync_key", "") ?? ""), "GET", null);
                using (StringReader reader = new StringReader(response ?? ""))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!line.StartsWith("PRODUCT|", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] p = line.Split('|');
                        if (p.Length < 21) continue;
                        Product product = new Product();
                        long id; if (!long.TryParse(Decode(p[1]), out id)) continue;
                        product.Id = id; product.Name = Decode(p[2]); product.Category = Decode(p[3]); product.Description = Decode(p[4]);
                        decimal dec; int integer;
                        decimal.TryParse(Decode(p[5]), NumberStyles.Any, CultureInfo.InvariantCulture, out dec); product.Price = dec;
                        decimal.TryParse(Decode(p[6]), NumberStyles.Any, CultureInfo.InvariantCulture, out dec); product.SalePrice = dec;
                        int.TryParse(Decode(p[7]), NumberStyles.Any, CultureInfo.InvariantCulture, out integer); product.Stock = integer;
                        int.TryParse(Decode(p[8]), NumberStyles.Any, CultureInfo.InvariantCulture, out integer); product.MinimumStock = integer;
                        product.SKU = Decode(p[9]); product.Brand = Decode(p[10]); product.Size = Decode(p[11]); product.Color = Decode(p[12]);
                        product.Active = Decode(p[13]) != "0"; product.OnlineEnabled = Decode(p[14]) != "0"; product.ImagePath = Decode(p[15]); product.Slug = Decode(p[16]); product.PublicDescription = Decode(p[17]); product.VideoUrl = Decode(p[18]); product.BarcodeImagePath = Decode(p[19]);
                        _store.UpsertProductFromCentral(product);
                    }
                }
            }
            catch (Exception ex) { try { _store.SetSetting("central_sync_last_error", "catalog_pull:" + ex.GetType().Name); } catch { } }
        }

        private void ApplyOrderStock(string itemsJson)
        {
            if (string.IsNullOrWhiteSpace(itemsJson)) return;
            foreach (Match m in Regex.Matches(itemsJson, @"""id""\s*:\s*""([^""]+)""[^}]*?""qty""\s*:\s*(\d+)", RegexOptions.IgnoreCase))
            {
                string id=m.Groups[1].Value;
                if(id.StartsWith("promo:",StringComparison.OrdinalIgnoreCase))continue;
                int qty; if(!int.TryParse(m.Groups[2].Value,out qty)||qty<1)continue;
                long pid; if(!long.TryParse(id,out pid))continue;
                Product p=_store.GetProducts("").Find(x=>x.Id==pid);
                if(p==null)continue;
                p.Stock=Math.Max(0,p.Stock-qty);
                _store.SaveProduct(p);
            }
        }

        private static string Get(Dictionary<string,string> d,string k){string v;return d!=null&&d.TryGetValue(k,out v)?v:"";}
        /// <summary>
        /// Detecta endpoints antiguos/locales para evitar que una instalación de Windows
        /// siga sincronizando contra localhost o una IP LAN en vez del servidor central.
        /// </summary>
        private static string GetCentralUrl()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NexoMarketCentral.url");
                if (File.Exists(path))
                {
                    string value = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return Normalize(value);
                }
            }
            catch { }
            return DefaultCentralUrl;
        }

        private static string NormalizeStoreId(string value)
        {
            return (value ?? "").Trim().Replace(" ", "").ToUpperInvariant();
        }

        private static bool IsLegacyLocalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            string u = url.Trim().ToLowerInvariant();
            if (u.StartsWith("http://localhost") || u.StartsWith("https://localhost")) return true;
            if (u.StartsWith("http://127.0.0.1") || u.StartsWith("https://127.0.0.1")) return true;
            if (u.StartsWith("http://192.168.") || u.StartsWith("https://192.168.")) return true;
            if (u.StartsWith("http://10.") || u.StartsWith("https://10.")) return true;
            if (u.StartsWith("http://172.16.") || u.StartsWith("https://172.16.")) return true;
            if (u.StartsWith("http://172.17.") || u.StartsWith("https://172.17.")) return true;
            if (u.StartsWith("http://172.18.") || u.StartsWith("https://172.18.")) return true;
            if (u.StartsWith("http://172.19.") || u.StartsWith("https://172.19.")) return true;
            if (u.StartsWith("http://172.20.") || u.StartsWith("https://172.20.")) return true;
            if (u.StartsWith("http://172.21.") || u.StartsWith("https://172.21.")) return true;
            if (u.StartsWith("http://172.22.") || u.StartsWith("https://172.22.")) return true;
            if (u.StartsWith("http://172.23.") || u.StartsWith("https://172.23.")) return true;
            if (u.StartsWith("http://172.24.") || u.StartsWith("https://172.24.")) return true;
            if (u.StartsWith("http://172.25.") || u.StartsWith("https://172.25.")) return true;
            if (u.StartsWith("http://172.26.") || u.StartsWith("https://172.26.")) return true;
            if (u.StartsWith("http://172.27.") || u.StartsWith("https://172.27.")) return true;
            if (u.StartsWith("http://172.28.") || u.StartsWith("https://172.28.")) return true;
            if (u.StartsWith("http://172.29.") || u.StartsWith("https://172.29.")) return true;
            if (u.StartsWith("http://172.30.") || u.StartsWith("https://172.30.")) return true;
            if (u.StartsWith("http://172.31.") || u.StartsWith("https://172.31.")) return true;
            return false;
        }

        private static string Normalize(string u){string v=(u??"").Trim().TrimEnd('/');if(v.EndsWith("/api",StringComparison.OrdinalIgnoreCase))v=v.Substring(0,v.Length-4).TrimEnd('/');return v;}
        private static string Form(Dictionary<string,string> v){StringBuilder b=new StringBuilder();foreach(KeyValuePair<string,string> x in v){if(b.Length>0)b.Append('&');b.Append(Uri.EscapeDataString(x.Key??""));b.Append('=').Append(Uri.EscapeDataString(x.Value??""));}return b.ToString();}
        private static string Request(string url,string method,string body){try{ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;HttpWebRequest r=(HttpWebRequest)WebRequest.Create(url);r.Method=method;r.Timeout=20000;r.ReadWriteTimeout=20000;r.UserAgent="NexoMarket Central Sync/4.1.24";r.KeepAlive=false;if(method=="POST"){byte[] d=Encoding.UTF8.GetBytes(body??"");r.ContentType="application/x-www-form-urlencoded; charset=utf-8";r.ContentLength=d.Length;using(Stream s=r.GetRequestStream())s.Write(d,0,d.Length);}using(WebResponse x=r.GetResponse())using(StreamReader sr=new StreamReader(x.GetResponseStream(),Encoding.UTF8))return sr.ReadToEnd();}catch(WebException ex){try{if(ex.Response!=null)using(StreamReader sr=new StreamReader(ex.Response.GetResponseStream(),Encoding.UTF8))return sr.ReadToEnd();}catch{}return null;}catch{return null;}}
        private static long ParseLong(string s){long v;return long.TryParse(s,out v)?v:0;}
        private static decimal ParseDecimal(string s){decimal v;return decimal.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out v)?v:0m;}
        private static DateTime ParseDate(string s){DateTime v;return DateTime.TryParse(s,null,DateTimeStyles.RoundtripKind,out v)?v.ToLocalTime():DateTime.Now;}
        private static List<Dictionary<string,string>> ParseObjects(string json)
        {
            List<Dictionary<string,string>> result=new List<Dictionary<string,string>>();
            if(string.IsNullOrEmpty(json)) return result;
            int i=0;
            while(i<json.Length)
            {
                int a=json.IndexOf('{',i); if(a<0) break;
                int b=a+1; bool inString=false; bool escape=false; int depth=1;
                for(;b<json.Length;b++)
                {
                    char c=json[b];
                    if(inString){ if(escape) escape=false; else if(c=='\\') escape=true; else if(c=='"') inString=false; continue; }
                    if(c=='"'){inString=true;continue;}
                    if(c=='{') depth++; else if(c=='}'){depth--;if(depth==0)break;}
                }
                if(b>=json.Length) break;
                string obj=json.Substring(a+1,b-a-1);
                Dictionary<string,string> d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                int p=0;
                while(p<obj.Length)
                {
                    while(p<obj.Length && (char.IsWhiteSpace(obj[p])||obj[p]==',')) p++;
                    if(p>=obj.Length) break;
                    if(obj[p]!='"') break;
                    int k2=p+1; bool ke=false;
                    for(;k2<obj.Length;k2++){char c=obj[k2];if(ke){ke=false;continue;}if(c=='\\'){ke=true;continue;}if(c=='"')break;}
                    if(k2>=obj.Length) break;
                    string key=obj.Substring(p+1,k2-p-1); int colon=obj.IndexOf(':',k2+1); if(colon<0) break;
                    int v=colon+1; while(v<obj.Length&&char.IsWhiteSpace(obj[v]))v++;
                    string value="";
                    if(v<obj.Length&&obj[v]=='"')
                    {
                        v++; StringBuilder vb=new StringBuilder(); bool ve=false;
                        for(;v<obj.Length;v++){char c=obj[v];if(ve){vb.Append(c);ve=false;continue;}if(c=='\\'){ve=true;continue;}if(c=='"'){v++;break;}vb.Append(c);} value=vb.ToString();
                    }
                    else
                    {
                        int v2=v; int nested=0; bool vs=false; bool esc=false;
                        for(;v2<obj.Length;v2++){char c=obj[v2];if(vs){if(esc)esc=false;else if(c=='\\')esc=true;else if(c=='"')vs=false;continue;}if(c=='"'){vs=true;continue;}if(c=='['||c=='{')nested++;else if(c==']'||c=='}')nested--;else if(c==','&&nested==0)break;} value=obj.Substring(v,v2-v).Trim();v=v2;
                    }
                    d[key]=value; p=v;
                }
                result.Add(d); i=b+1;
            }
            return result;
        }
    }
}
