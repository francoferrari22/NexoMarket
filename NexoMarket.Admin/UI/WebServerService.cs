using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Mail;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using NexoMarket.Admin.Data;
using NexoMarket.Admin.Models;

namespace NexoMarket.Admin.UI
{
    /// <summary>
    /// Web local de NexoMarket. Compatible con .NET Framework 4.0 / Windows 8.
    /// Incluye marketplace por tiendas, login comprador/vendedor y consola vendedor.
    /// La lista central de tiendas se obtiene mediante StoreDirectoryClient cuando
    /// web_sync_enabled está activo y web_api_url apunta al servidor central.
    /// </summary>
    public sealed class WebServerService : IDisposable
    {
        private readonly AppDataStore _store;
        private readonly int _port;
        private TcpListener _listener;
        private System.Threading.Thread _worker;
        private System.Threading.Timer _watchdog;
        private readonly object _lifecycleSync = new object();
        private volatile bool _running;
        private volatile bool _desiredRunning;
        private readonly object _sessionSync = new object();
        private readonly Dictionary<string, WebUser> _sessions = new Dictionary<string, WebUser>();
        private readonly Dictionary<string, Dictionary<long, int>> _carts = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<PromotionSelection>> _promotionCarts = new Dictionary<string, List<PromotionSelection>>(StringComparer.OrdinalIgnoreCase);

        private void LogWeb(string message)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "logs_web_server.log");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + (message ?? "") + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public int Port { get { return _port; } }
        public bool IsRunning { get { return _running; } }
        public string StoreId { get { return _store.StoreId; } }
        public string LocalUrl { get { return "http://" + GetLocalIPv4() + ":" + _port + "/"; } }
        public string LocalCode { get { return StoreId + "@" + GetLocalIPv4() + ":" + _port; } }

        public WebServerService(AppDataStore store, int port) { _store = store; _port = port; }

        public bool Start()
        {
            lock (_lifecycleSync)
            {
                _desiredRunning = true;
                if (_running) return true;
                bool ok = StartCoreLocked();
                EnsureWatchdogLocked();
                return ok;
            }
        }

        private bool StartCoreLocked()
        {
            if (_running) return true;
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _running = true;
                LogWeb("SERVIDOR INICIADO puerto=" + _port);
                _store.SetSetting("web_server_enabled", "1");
                _worker = new System.Threading.Thread(Worker) { IsBackground = true };
                _worker.Start();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { new StoreDirectoryClient(_store).PublishStore(_store.GetSetting("web_public_url", LocalUrl)); } catch { } });
                return true;
            }
            catch
            {
                _running = false;
                LogWeb("ERROR AL INICIAR SERVIDOR puerto=" + _port);
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
                return false;
            }
        }

        private void EnsureWatchdogLocked()
        {
            if (_watchdog == null)
                _watchdog = new System.Threading.Timer(WatchdogTick, null, 10000, 10000);
        }

        private void WatchdogTick(object state)
        {
            if (!_desiredRunning) return;
            lock (_lifecycleSync)
            {
                if (!_desiredRunning) return;

                // No alcanza con mirar _running: también comprobamos que el hilo
                // que acepta conexiones siga vivo y que el listener exista.
                bool workerAlive = _worker != null && _worker.IsAlive;
                bool listenerAlive = _listener != null;
                if (_running && workerAlive && listenerAlive) return;

                _running = false;
                LogWeb("WATCHDOG: servidor caido, intentando recuperar");
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
                StartCoreLocked();
            }
        }

        public void Stop()
        {
            lock (_lifecycleSync)
            {
                _desiredRunning = false;
                _running = false;
                LogWeb("SERVIDOR DETENIDO");
                _store.SetSetting("web_server_enabled", "0");
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
                lock (_sessionSync) _sessions.Clear();
            }
        }

        private void Worker()
        {
            while (_running)
            {
                try
                {
                    TcpListener listener = _listener;
                    if (listener == null) break;
                    TcpClient c = listener.AcceptTcpClient();
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
                }
                catch
                {
                    if (!_running) break;
                    // El listener falló inesperadamente. Lo marcamos como caído;
                    // el watchdog lo volverá a levantar en un máximo de 10 segundos.
                    lock (_lifecycleSync)
                    {
                        _running = false;
                        LogWeb("WORKER: listener fallo; watchdog intentara recuperar");
                        try { if (_listener != null) _listener.Stop(); } catch { }
                        _listener = null;
                    }
                    break;
                }
            }
        }

        private void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    using (NetworkStream stream = client.GetStream())
                    {
                        string request = ReadRequest(stream);
                        if (string.IsNullOrEmpty(request)) return;
                        string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        string[] first = lines[0].Split(' ');
                        string method = first.Length > 0 ? first[0].ToUpperInvariant() : "GET";
                        string target = first.Length > 1 ? first[1] : "/";
                        string body = ExtractBody(request);
                        string cookie = HeaderValue(request, "Cookie");
                        WebUser user = GetSession(cookie);
                        string path = target; int q = path.IndexOf('?'); string query = "";
                        if (q >= 0) { query = path.Substring(q + 1); path = path.Substring(0, q); }

                        if (path == "/media")
                        {
                            ServeMedia(stream, query);
                            return;
                        }

                        bool setCookie; string cookieValue;
                        string response = Route(method, path, query, body, user, cookie, out setCookie, out cookieValue);
                        WriteHtml(stream, 200, response, setCookie, cookieValue);
                    }
                }
                catch { }
            }
        }

        private string Route(string method, string path, string query, string body, WebUser user, string rawCookie, out bool setCookie, out string cookieValue)
        {
            setCookie = false; cookieValue = "";
            if (path == "/health") return "OK";
            if (path == "/") return Home(user, rawCookie);
            if (path == "/store") return StorePage(user, query);
            if (path == "/product") return ProductDetail(user, query);
            if (path == "/login" && method == "GET") return Page("Ingreso", LoginForm() + "<p class='user'>¿Todavía no tenés cuenta? <a href='/register'>Crear cuenta</a></p>");
            if (path == "/register" && method == "GET") return Page("Crear cuenta", RegisterForm() + "<p class='user'>¿Ya tenés cuenta? <a href='/login'>Ingresar</a></p>");
            if (path == "/onboarding" && method == "POST") return SaveOnboarding(body, out setCookie, out cookieValue);
            if (path == "/login" && method == "POST")
            {
                Dictionary<string,string> f = Form(body); WebUser found;
                if (!_store.VerifyWebUser(f.Get("email"), f.Get("password"), out found))
                {
                    try { using (CentralSyncService sync = new CentralSyncService(_store)) { sync.SyncOnce(); sync.AuthenticateCentral(f.Get("email"), f.Get("password"), out found); } } catch { }
                }
                if (_store.VerifyWebUser(f.Get("email"), f.Get("password"), out found))
                {
                    string token = CreateWebSessionToken(found);
                    lock (_sessionSync) _sessions[token] = found;
                    setCookie = true; cookieValue = "NexoSession=" + token + "; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax";
                    return RedirectPage(found.Role == "seller" ? "/seller" : "/", "Ingreso correcto");
                }
                return Page("Ingreso", "<div class='error'>Correo o contraseña incorrectos.</div>" + LoginForm());
            }
            if (path == "/forgot-password" && method == "GET") return Page("Recuperar contraseña", ForgotPasswordForm());
            if (path == "/forgot-password" && method == "POST") return BeginWebRecovery(body);
            if (path == "/reset-password" && method == "GET") return Page("Restablecer contraseña", ResetPasswordForm(query));
            if (path == "/reset-password" && method == "POST") return CompleteWebRecovery(body);
            if (path == "/seller/password" && method == "GET") return SellerPassword(user);
            if (path == "/seller/password" && method == "POST") return SellerPasswordChange(body, user);
            if (path == "/register" && method == "POST")
            {
                Dictionary<string,string> f = Form(body); string role = f.Get("role");
                if (role != "seller" && role != "buyer") role = "buyer";
                if (f.Get("password").Length < 6) return Page("Crear cuenta", "<div class='error'>La contraseña debe tener al menos 6 caracteres.</div>" + RegisterForm());
                string email = (f.Get("email") ?? "").Trim().ToLowerInvariant();
                try { using (CentralSyncService sync = new CentralSyncService(_store)) sync.SyncOnce(); } catch { }
                if (role == "seller")
                {
                    string linked = (_store.GetSetting("seller_account_email", "") ?? "").Trim();
                    bool sellerLocked = _store.GetSetting("seller_account_locked", "0") == "1";
                    if (sellerLocked && string.IsNullOrWhiteSpace(linked))
                        return Page("Crear cuenta", "<div class='error'>La cuenta de vendedor de este comercio ya fue asociada y no puede reemplazarse.</div>" + RegisterForm());
                    if (!string.IsNullOrWhiteSpace(linked) && !string.Equals(linked, email, StringComparison.OrdinalIgnoreCase))
                        return Page("Crear cuenta", "<div class='error'><b>Esta tienda ya tiene una cuenta de vendedor vinculada.</b><p>Usá el correo " + E(linked) + " para ingresar o cambiá la vinculación desde el panel de Windows.</p></div>" + RegisterForm());
                }
                string salt = AuthService.CreateSalt();
                WebUser nu = new WebUser { Name = f.Get("name"), Email = email, Phone = f.Get("phone"), Role = role, StoreId = role == "seller" ? _store.StoreId : "", Salt = salt, PasswordHash = AuthService.HashPassword(f.Get("password"), salt), CreatedAt = DateTime.Now };
                if (!_store.CreateWebUser(nu)) return Page("Crear cuenta", "<div class='error'>Ese correo ya está registrado. Si es tu cuenta, iniciá sesión.</div>" + RegisterForm());
                if (role == "seller")
                {
                    _store.SetSetting("seller_account_email", email);
                    _store.SetSetting("seller_account_name", f.Get("name"));
                    _store.SetSetting("seller_account_locked", "1");
                    try
                    {
                        WebUser created = _store.FindWebUser(email);
                        if (created != null) new CentralSyncService(_store).PublishAccountNow(created);
                    }
                    catch { }
                }
                return RedirectPage("/", "Cuenta creada. Ahora podés ingresar como " + (role == "seller" ? "vendedor" : "comprador") + ". La cuenta queda sincronizada con el servidor central por StoreId.");
            }
            if (path == "/logout") return LogoutPage(out setCookie, out cookieValue);
            if (path == "/promotion" && method == "POST")
            {
                if (user == null) { string token=Guid.NewGuid().ToString("N"); user=new WebUser{Name="Invitado",Email="guest_"+token+"@guest.local",Role="buyer",StoreId=StoreId}; lock(_sessionSync)_sessions[token]=user; setCookie=true; cookieValue="NexoSession="+token+"; Path=/; HttpOnly"; }
                return AddPromotionToCart(body, user);
            }
            if (path == "/order" && method == "POST")
            {
                if (user == null)
                {
                    string token = Guid.NewGuid().ToString("N");
                    user = new WebUser { Name = "Invitado", Email = "guest_" + token + "@guest.local", Role = "buyer", StoreId = StoreId };
                    lock (_sessionSync) _sessions[token] = user;
                    setCookie = true; cookieValue = "NexoSession=" + token + "; Path=/; HttpOnly";
                }
                return AddToCart(body, user);
            }
            if (path == "/cart") return CartPage(user);
            if (path == "/cart/remove" && method == "POST") return RemoveFromCart(body, user);
            if (path == "/checkout" && method == "GET") return CheckoutPage(user);
            if (path == "/checkout" && method == "POST") return CheckoutSubmit(body, user);
            if (path == "/buyer") return BuyerHome(user);
            if (path == "/buyer/orders") return BuyerOrders(user);
            if (path == "/buyer/order" && method == "GET") return BuyerOrderDetail(query, user);
            if (path == "/buyer/order-status" && method == "POST") return BuyerOrderStatus(body, user);
            if (path == "/buyer/review" && method == "POST") return BuyerReview(body, user);
            if (path == "/messages" && method == "GET") return MessagesPage(user);
            if (path == "/messages/send" && method == "POST") return SendMessage(body, user);
            if (path == "/seller") return SellerHome(user);
            if (path == "/seller/orders") return SellerOrders(user);
            if (path == "/seller/order-status" && method == "POST") return SellerOrderStatus(body, user);
            if (path == "/seller/payment-status" && method == "POST") return SellerPaymentStatus(body, user);
            if (path == "/seller/cash/open" && method == "POST") return SellerCashOpen(body, user);
            if (path == "/seller/cash/close" && method == "POST") return SellerCashClose(body, user);
            if (path == "/seller/order-detail" && method == "GET") return SellerOrderDetail(query, user);
            if (path == "/seller/order-negotiate" && method == "POST") return SellerOrderNegotiate(body, user);
            if (path == "/buyer/order-negotiate" && method == "POST") return BuyerOrderNegotiate(body, user);
            if (path == "/seller/products") return SellerProducts(user);
            if (path == "/seller/products/new" && method == "GET") return SellerProductForm(user, 0);
            if (path == "/seller/products/edit" && method == "GET") return SellerProductForm(user, ParseLong(QueryValue(query, "id")));
            if (path == "/seller/products/save" && method == "POST") return SellerProductSave(body, user);
            if (path == "/seller/products/delete" && method == "POST") return SellerProductDelete(body, user);
            if (path == "/seller/analytics") return SellerAnalytics(user);
            if (path == "/seller/finance") return SellerFinance(user);
            if (path == "/seller/marketing") return SellerMarketing(user);
            if (path == "/seller/coupon/save" && method == "POST") return SellerCouponSave(body, user);
            if (path == "/seller/customers") return SellerCustomers(user);
            if (path == "/seller/reputation") return SellerReputation(user);
            if (path == "/seller/tools") return SellerTools(user);
            if (path == "/seller/tools/import" && method == "POST") return SellerImportCsv(body, user);
            return Page("NexoMarket", "<div class='error'>Página no encontrada.</div><a class='btn' href='/'>Inicio</a>");
        }

        private string Home(WebUser user, string rawCookie)
        {
            string role, location, display; double lat, lon; bool hasLocation = ReadVisitorPrefs(rawCookie, out role, out location, out lat, out lon, out display);
            StringBuilder b = new StringBuilder(); b.Append(Header("Marketplace")); b.Append(TopNav(user));
            string placeTitle = hasLocation ? (string.IsNullOrEmpty(display) ? location : display) : "todo el marketplace";
            b.Append("<section class='hero hero-market'><div><span class='eyebrow'>MARKETPLACE</span><div><span class='nexo'>NEXO</span><span class='market'>MARKET</span></div><div class='hero-sub'>Encontrá todas las tiendas disponibles" + (hasLocation ? " y priorizá las más cercanas a <b>" + E(placeTitle) + "</b>." : ". Podés indicar tu ubicación cuando quieras para ordenar por distancia.") + "</div></div><div class='location-box'><b>📍 Tu zona</b><strong>" + E(hasLocation ? location : "Sin ubicación definida") + "</strong><form method='post' action='/onboarding'><input type='hidden' name='role' value='buyer'/><input name='location' placeholder='Ciudad o localidad' value='" + E(hasLocation ? location : "") + "'/><button class='btn small' type='submit'>" + (hasLocation ? "Cambiar" : "Definir") + "</button></form><button type='button' class='geo-btn' onclick='useGeoHome()'>Usar mi ubicación actual</button><div id='geoHomeMsg' class='geo-msg'></div></div></section>");
            b.Append("<div class='section-head'><div><h2>Tiendas disponibles</h2><p>Al entrar por primera vez se muestran todas las tiendas. Con ubicación se ordenan por cercanía.</p></div>" + (user != null && user.Role == "seller" ? "<a class='btn alt' href='/seller'>Ir a mi consola</a>" : "<span class='near-note'>🏪 Directorio multi-tienda</span>") + "</div>");
            List<RemoteStore> stores = new List<RemoteStore>(); try { stores = new StoreDirectoryClient(_store).GetStores("", lat, lon, hasLocation && (Math.Abs(lat) > 0.00001 || Math.Abs(lon) > 0.00001)); } catch { }
            RemoteStore local = LocalRemoteStore(); if (local.Active && !stores.Any(x => string.Equals(x.StoreId, local.StoreId, StringComparison.OrdinalIgnoreCase))) stores.Add(local);
            b.Append("<div class='market-store-grid'>"); foreach (RemoteStore st in stores) b.Append(StoreCard(st, st.StoreId == StoreId ? "/store" : st.PublicUrl)); b.Append("</div>");
            if (stores.Count == 0) b.Append("<div class='empty'><b>No hay tiendas publicadas todavía.</b><p>Cuando los vendedores publiquen sus tiendas aparecerán automáticamente aquí.</p></div>");
            if (user == null) b.Append("<section class='auth-grid'><div><h2>¿Ya tenés cuenta?</h2>" + LoginForm() + "</div><div id='register'><h2>Crear cuenta</h2>" + RegisterForm() + "</div></section>");
            b.Append("<script>function useGeoHome(){var m=document.getElementById('geoHomeMsg');if(!navigator.geolocation){m.innerHTML='Tu navegador no permite ubicación.';return;}m.innerHTML='Solicitando ubicación...';navigator.geolocation.getCurrentPosition(function(p){var f=document.createElement('form');f.method='post';f.action='/onboarding';[['role','buyer'],['location','Mi ubicación actual'],['latitude',String(p.coords.latitude)],['longitude',String(p.coords.longitude)]].forEach(function(a){var i=document.createElement('input');i.type='hidden';i.name=a[0];i.value=a[1];f.appendChild(i);});document.body.appendChild(f);f.submit();},function(){m.innerHTML='No pudimos obtener la ubicación. Podés escribir una ciudad.';},{enableHighAccuracy:false,timeout:8000,maximumAge:300000});}</script>");
            b.Append(Footer()); return b.ToString();
        }

        private string SaveOnboarding(string body, out bool setCookie, out string cookieValue)
        {
            setCookie = false; cookieValue = "";
            Dictionary<string,string> f = Form(body); string role = f.Get("role"); if (role != "seller" && role != "buyer") role = "buyer";
            string location = (f.Get("location") ?? "").Trim(); double lat = 0d, lon = 0d;
            bool latOk = double.TryParse(f.Get("latitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
            bool lonOk = double.TryParse(f.Get("longitude"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
            bool hasCoords = latOk && lonOk;
            string display = location;
            if (!hasCoords && !string.IsNullOrWhiteSpace(location))
            {
                try { LocationResult g = new StoreDirectoryClient(_store).Geocode(location); if (g.Success) { lat = g.Latitude; lon = g.Longitude; hasCoords = true; display = g.DisplayName; } } catch { }
            }
            string packed = role + "|" + location + "|" + (hasCoords ? lat.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + "|" + (hasCoords ? lon.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + "|" + display;
            setCookie = true; cookieValue = "NexoPrefs=" + Uri.EscapeDataString(packed) + "; Path=/";
            return RedirectPage("/", "Ubicación guardada. Buscando tiendas cercanas...");
        }

        private string StorePage(WebUser user, string query)
        {
            StringBuilder b = new StringBuilder(); b.Append(Header(_store.GetSetting("store_name", "NexoMarket"))); b.Append(TopNav(user));
            b.Append("<section class='store-hero'>");
            string cover = _store.GetSetting("store_cover", "");
            if (!string.IsNullOrEmpty(cover)) b.Append("<div class='cover' style=\"background-image:url('/media?p=" + Uri.EscapeDataString(cover) + "')\"></div>");
            b.Append("<div class='store-title'><span class='nexo'>NEXO</span><span class='market'>MARKET</span><h1>" + E(_store.GetSetting("store_name", "NexoMarket")) + "</h1><p>" + E(_store.GetSetting("store_description", "Tienda NexoMarket")) + "</p><span class='pill green'>● Abierta</span> <span class='pill'>" + E(_store.GetSetting("store_category", "Comercio")) + "</span></div></section>");
            List<Product> products = _store.GetProducts("").Where(p => p.Active && p.OnlineEnabled).ToList();
            string requestedSearch = QueryValue(query, "q");
            if (!string.IsNullOrWhiteSpace(requestedSearch)) products = products.Where(p => (p.Name+" "+p.Brand+" "+p.SKU+" "+p.Barcode).IndexOf(Uri.UnescapeDataString(requestedSearch), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            string requestedCategory = QueryValue(query, "cat");
            if (!string.IsNullOrEmpty(requestedCategory)) products = products.Where(p => string.Equals(p.Category ?? "", Uri.UnescapeDataString(requestedCategory), StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> categories = products.Select(p => p.Category ?? "General").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            b.Append("<div class='section-head'><div><h2>Catálogo</h2><p>" + products.Count + " productos publicados</p></div><a class='btn alt' href='/'>← Tiendas</a></div><form class='searchbar' method='get' action='/store'><input name='q' value='" + E(Uri.UnescapeDataString(QueryValue(query,"q"))) + "' placeholder='Buscar producto, marca, SKU...'/><input name='cat' value='" + E(Uri.UnescapeDataString(QueryValue(query,"cat"))) + "' placeholder='Categoría'/><button class='btn' type='submit'>BUSCAR</button></form>");
            b.Append("<div class='chips'><a href='/store'>Todos</a>"); foreach (string c in categories) b.Append("<a href='/store?cat=" + Uri.EscapeDataString(c) + "'>" + E(c) + "</a>"); b.Append("</div>");
            List<Promotion> visiblePromos = _store.GetPromotions().Where(p => p.Active && p.From.Date <= DateTime.Today && p.To.Date >= DateTime.Today).ToList();
            if (visiblePromos.Count > 0)
            {
                b.Append("<section class='panel'><div class='panel-title'><h2>🔥 Promociones vigentes</h2><span class='user'>Combos y precios especiales</span></div><div class='campaign-grid'>");
                foreach (Promotion pr in visiblePromos)
                {
                    List<long> ids = (pr.ProductIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => ParseLong(x)).ToList();
                    string names = string.Join(" + ", products.Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToArray());
                    b.Append("<div class='campaign'><b>" + E(pr.Name) + "</b><p>" + E(names) + "</p><strong>$ " + pr.PromotionalPrice.ToString("N2") + "</strong><form method='post' action='/promotion'><input type='hidden' name='promotionId' value='" + pr.Id + "'/><button class='btn' type='submit'>🛒 COMPRAR PROMOCIÓN</button></form></div>");
                }
                b.Append("</div></section>");
            }
            b.Append("<div class='product-grid'>");
            foreach (Product p in products)
            {
                decimal price = p.SalePrice > 0 ? p.SalePrice : p.Price;
                b.Append("<article class='product-card'>");
                if (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath)) b.Append("<img class='product-image' src='/media?p=" + Uri.EscapeDataString(p.ImagePath) + "' alt='" + E(p.Name) + "'/>");
                else b.Append("<div class='product-image placeholder'>NEXO</div>");
                b.Append("<div class='product-body'><span class='mini-tag'>" + E(p.Category) + "</span><h3><a href='/product?id=" + p.Id + "'>" + E(p.Name) + "</a></h3><small>SKU " + E(p.SKU) + "</small><div class='price'>$ " + price.ToString("N2") + "</div><div class='stock'>" + (p.Stock > 0 ? "● Disponible" : "● Sin stock") + "</div>");
                if (p.Stock > 0 && !IsSeller(user)) b.Append("<form method='post' action='/order'><input type='hidden' name='productId' value='" + p.Id + "'/><div class='buy-row'><input name='qty' type='number' min='1' max='" + p.Stock + "' value='1'/><button class='btn' type='submit'>🛒 Agregar al carrito</button></div></form>");
                else if (p.Stock > 0 && IsSeller(user)) b.Append("<span class='user'>🔒 Tu tienda: los vendedores no pueden comprarse a sí mismos.</span>");
                else if (user == null) b.Append("<span class='user'>Disponible para compra como invitado</span>");
                b.Append("</div></article>");
            }
            b.Append("</div>"); b.Append(Footer()); return b.ToString();
        }

        private string AddPromotionToCart(string body, WebUser user)
        {
            if (!CanBuyAsBuyer(user) || IsSeller(user)) return Page("Promoción", "<div class='error'><b>No podés comprar una promoción desde tu propia tienda.</b><p>Entrá a otra tienda para comprar.</p><a class='btn' href='/'>VER OTRAS TIENDAS</a></div>");
            long id = ParseLong(Form(body).Get("promotionId"));
            Promotion p = _store.GetPromotions().FirstOrDefault(x => x.Id == id && x.Active && x.From.Date <= DateTime.Today && x.To.Date >= DateTime.Today);
            if (p == null) return Page("Promoción", "<div class='error'>La promoción ya no está vigente.</div>");
            List<long> productIds = (p.ProductIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => ParseLong(x)).Where(x => x > 0).Distinct().ToList();
            if (productIds.Count == 0) return Page("Promoción", "<div class='error'>La promoción no tiene productos configurados.</div>");
            foreach (long productId in productIds) { Product product = FindProduct(productId); if (product == null || !product.Active || !product.OnlineEnabled || product.Stock < 1) return Page("Promoción", "<div class='error'>Uno de los productos de esta promoción está sin stock.</div>"); }
            string key = (user.Email ?? "").Trim().ToLowerInvariant();
            lock (_sessionSync)
            {
                List<PromotionSelection> list; if (!_promotionCarts.TryGetValue(key, out list)) { list = new List<PromotionSelection>(); _promotionCarts[key] = list; }
                PromotionSelection existing = list.FirstOrDefault(x => x.PromotionId == p.Id);
                if (existing == null) list.Add(new PromotionSelection { PromotionId = p.Id, Name = p.Name, ProductIds = productIds, UnitPrice = p.PromotionalPrice, Quantity = 1 }); else existing.Quantity++;
            }
            return RedirectPage("/cart", "Promoción agregada al carrito.");
        }

        private string AddToCart(string body, WebUser user)
        {
            if (!CanBuyAsBuyer(user)) return Page("Carrito", "<div class='error'>Ingresá como comprador para agregar productos.</div>");
            if (IsSeller(user)) return Page("Compra", "<div class='error'><b>No podés comprar en tu propia tienda.</b><p>Tu cuenta de vendedor también funciona como identidad de comprador para otras tiendas.</p><a class='btn' href='/'>VER OTRAS TIENDAS</a></div>");
            Dictionary<string,string> f = Form(body); long id; int qty;
            if (!long.TryParse(f.Get("productId"), out id) || !int.TryParse(f.Get("qty"), out qty) || qty < 1) return Page("Carrito", "<div class='error'>Cantidad inválida.</div>");
            Product p = FindProduct(id);
            if (p == null || !p.Active || !p.OnlineEnabled || p.Stock < qty) return Page("Carrito", "<div class='error'>El producto no está disponible en esa cantidad.</div>");
            string key = (user.Email ?? "").Trim().ToLowerInvariant();
            lock (_sessionSync)
            {
                Dictionary<long,int> cart;
                if (!_carts.TryGetValue(key, out cart)) { cart = new Dictionary<long,int>(); _carts[key] = cart; }
                int current = cart.ContainsKey(id) ? cart[id] : 0;
                cart[id] = Math.Min(p.Stock, current + qty);
            }
            return RedirectPage("/cart", "Producto agregado al carrito.");
        }

        private string CartPage(WebUser user)
        {
            if (!CanBuyAsBuyer(user)) return Page("Carrito", "<div class='error'>Ingresá como comprador para ver tu carrito.</div>");
            if (IsSeller(user)) return Page("Carrito", "<div class='error'><b>El carrito de comprador no está disponible dentro de tu propia tienda.</b><p>Podés utilizarlo al entrar a otra tienda.</p><a class='btn' href='/'>VER OTRAS TIENDAS</a></div>");
            Dictionary<long,int> cart = GetCart(user); List<PromotionSelection> promoCart = GetPromotionCart(user);
            StringBuilder b = new StringBuilder(); b.Append(Header("Mi carrito")); b.Append(TopNav(user));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>COMPRA</span><h1>Mi carrito</h1><p>Revisá cantidades antes de confirmar tu compra.</p></div><a class='btn alt' href='/store'>← Seguir comprando</a></div>");
            if (cart.Count == 0 && promoCart.Count == 0) { b.Append("<div class='empty'><b>Tu carrito está vacío.</b><p>Agregá productos desde una tienda.</p><a class='btn' href='/'>Buscar tiendas</a></div>"); b.Append(Footer()); return b.ToString(); }
            decimal subtotal = 0m; b.Append("<div class='panel table-panel'><table class='pro-table'><tr><th>Producto / Promoción</th><th>Precio</th><th>Cantidad</th><th>Subtotal</th><th></th></tr>");
            foreach (KeyValuePair<long,int> item in cart.ToList())
            {
                Product p = FindProduct(item.Key); if (p == null || !p.Active) { cart.Remove(item.Key); continue; }
                decimal unit = p.SalePrice > 0 ? p.SalePrice : p.Price; decimal sub = unit * item.Value; subtotal += sub;
                b.Append("<tr><td><b>" + E(p.Name) + "</b><small>SKU " + E(p.SKU) + "</small></td><td>$ " + unit.ToString("N2") + "</td><td>" + item.Value + "</td><td><b>$ " + sub.ToString("N2") + "</b></td><td><form method='post' action='/cart/remove'><input type='hidden' name='productId' value='" + p.Id + "'/><button class='btn danger' type='submit'>Quitar</button></form></td></tr>");
            }
            foreach (PromotionSelection pr in promoCart.ToList()) { decimal sub = pr.UnitPrice * pr.Quantity; subtotal += sub; b.Append("<tr><td><b>🔥 " + E(pr.Name) + "</b><small>Combo promocional</small></td><td>$ " + pr.UnitPrice.ToString("N2") + "</td><td>" + pr.Quantity + "</td><td><b>$ " + sub.ToString("N2") + "</b></td><td><form method='post' action='/cart/remove'><input type='hidden' name='promotionId' value='" + pr.PromotionId + "'/><button class='btn danger' type='submit'>Quitar</button></form></td></tr>"); }
            b.Append("</table><div class='cart-total'><span>Subtotal</span><strong>$ " + subtotal.ToString("N2") + "</strong></div><div class='cart-total'><span>Envío</span><strong id='shipCost'>$ 0,00</strong></div><div class='cart-total'><span>Total estimado</span><strong id='grandTotal'>$ " + subtotal.ToString("N2") + "</strong></div><a class='btn' href='/checkout'>CONTINUAR AL PAGO</a></div>"); b.Append(Footer()); return b.ToString();
        }

        private string RemoveFromCart(string body, WebUser user)
        {
            if (user == null || user.Role != "buyer") return SellerDenied();
            Dictionary<string,string> f = Form(body); long id;
            long promotionId = ParseLong(f.Get("promotionId"));
            if (promotionId > 0) { string keyp=(user.Email??"").Trim().ToLowerInvariant(); lock(_sessionSync){ List<PromotionSelection> list; if(_promotionCarts.TryGetValue(keyp,out list)){ list.RemoveAll(x=>x.PromotionId==promotionId); } } return RedirectPage("/cart", "Promoción quitada del carrito."); }
            if (!long.TryParse(f.Get("productId"), out id)) return RedirectPage("/cart", "No se pudo quitar el producto.");
            lock (_sessionSync) { Dictionary<long,int> cart; string key = (user.Email ?? "").Trim().ToLowerInvariant(); if (_carts.TryGetValue(key, out cart)) cart.Remove(id); }
            return RedirectPage("/cart", "Producto quitado del carrito.");
        }

        private Dictionary<long,int> GetCart(WebUser user)
        {
            string key = (user.Email ?? "").Trim().ToLowerInvariant(); lock (_sessionSync)
            {
                Dictionary<long,int> cart; if (!_carts.TryGetValue(key, out cart)) { cart = new Dictionary<long,int>(); _carts[key] = cart; }
                return new Dictionary<long,int>(cart);
            }
        }

        private void ClearCart(WebUser user)
        {
            if (user == null) return; string key = (user.Email ?? "").Trim().ToLowerInvariant(); lock (_sessionSync) { _carts.Remove(key); _promotionCarts.Remove(key); }
        }

        private string CheckoutPage(WebUser user)
        {
            if (!CanBuyAsBuyer(user)) return Page("Checkout", "<div class='error'>El carrito necesita una sesión de compra.</div>");
            if (IsSeller(user)) return Page("Checkout", "<div class='error'><b>No podés finalizar una compra en tu propia tienda.</b><p>El sistema permite que un vendedor compre como cliente únicamente en otras tiendas.</p><a class='btn' href='/'>VER OTRAS TIENDAS</a></div>");
            Dictionary<long,int> cart = GetCart(user); List<PromotionSelection> promoCart = GetPromotionCart(user); if (cart.Count == 0 && promoCart.Count == 0) return RedirectPage("/cart", "Tu carrito está vacío.");
            decimal subtotal = CartTotal(cart) + PromotionCartTotal(user); StringBuilder b = new StringBuilder(); b.Append(Header("Finalizar compra")); b.Append(TopNav(user));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CHECKOUT SEGURO</span><h1>Finalizar compra</h1><p>Completá los datos y, si pagás por transferencia o billetera, adjuntá el comprobante.</p></div></div>");
            b.Append("<div class='checkout-grid'><section class='panel'><h2>Datos de entrega</h2><form method='post' action='/checkout'><input name='name' value='" + (IsGuestUser(user) ? "" : E(user.Name)) + "' placeholder='Nombre completo' required/><input name='phone' value='" + (IsGuestUser(user) ? "" : E(user.Phone)) + "' placeholder='Teléfono de contacto'/><input name='contactEmail' type='email' value='" + (IsGuestUser(user) ? "" : E(user.Email)) + "' placeholder='Correo electrónico (opcional)'/><div class='user'>Dejanos al menos un medio de contacto: teléfono o correo.</div><select name='fulfillment'><option value='Delivery'>Delivery</option><option value='Retiro'>Retiro en tienda</option></select><input name='address' placeholder='Dirección / punto de retiro'/><input name='postalCode' placeholder='Código postal' oninput='calcShip(this.value)'/><div id='shipHint' class='user'>Costo de envío se calcula según zona.</div><textarea name='notes' placeholder='Notas para el vendedor'></textarea><h2>Pago</h2><select name='paymentMethod' onchange='toggleProof(this.value)'><option value='Transferencia'>Transferencia bancaria</option><option value='Mercado Pago'>Mercado Pago</option><option value='Efectivo'>Efectivo al retirar/recibir</option></select><input name='paymentReference' placeholder='Referencia / número de operación (opcional)'/><div class='upload-box'><b>📎 Comprobante de pago</b><div class='media-actions'><label class='btn alt small'>SUBIR FOTO<input id='proofFile' type='file' accept='image/*' capture='environment' onchange='loadProof(this)'/></label><button class='btn small' type='button' onclick='openProofCamera()'>📷 SACAR FOTO</button></div><div id='proofPreview' class='proof-preview'></div><input type='hidden' id='proofData' name='proofData'/></div><div id='proofHint' class='user'>Para efectivo no hace falta comprobante.</div><div id='proofCamera' class='camera-box' hidden><video id='proofVideo' autoplay playsinline></video><div class='media-actions'><button class='btn small' type='button' onclick='takeProofPhoto()'>CAPTURAR</button><button class='btn alt small' type='button' onclick='closeProofCamera()'>CERRAR CÁMARA</button></div><canvas id='proofCanvas' hidden></canvas></div><button class='btn' type='submit'>CONFIRMAR COMPRA Y ENVIAR PEDIDO</button></form></section><aside class='panel'><h2>Resumen</h2><div class='cart-total'><span>Subtotal</span><strong>$ " + subtotal.ToString("N2") + "</strong></div><div class='cart-total'><span>Envío</span><strong id='shipCost'>$ 0,00</strong></div><div class='cart-total'><span>Total estimado</span><strong id='grandTotal'>$ " + subtotal.ToString("N2") + "</strong></div><a href='/cart' class='btn alt'>Modificar carrito</a></aside></div><script>function toggleProof(v){document.getElementById('proofHint').innerHTML=v==='Efectivo'?'Para efectivo no hace falta comprobante.':'Adjuntá una captura o foto del comprobante para que el vendedor pueda validar el pago.';}var proofStream=null;function loadProof(i){var f=i.files&&i.files[0];if(!f)return;if(f.size>4*1024*1024){alert('El comprobante supera 4 MB.');i.value='';return;}var r=new FileReader();r.onload=function(){document.getElementById('proofData').value=r.result;document.getElementById('proofPreview').innerHTML='<img src=\"'+r.result+'\" alt=\"Comprobante\"/>';document.getElementById('proofHint').innerHTML='✓ Comprobante listo: '+f.name;};r.readAsDataURL(f);}function openProofCamera(){if(proofStream){proofStream.getTracks().forEach(function(t){t.stop();});proofStream=null;}var box=document.getElementById('proofCamera');box.hidden=false;if(!navigator.mediaDevices||!navigator.mediaDevices.getUserMedia){document.getElementById('proofHint').innerHTML='Tu navegador no permite cámara directa. Usá SUBIR FOTO.';return;}navigator.mediaDevices.getUserMedia({video:{facingMode:{ideal:'environment'}},audio:false}).then(function(stream){proofStream=stream;document.getElementById('proofVideo').srcObject=stream;}).catch(function(){proofStream=null;document.getElementById('proofCamera').hidden=true;document.getElementById('proofHint').innerHTML='No se pudo abrir la cámara. Verificá el permiso o usá SUBIR FOTO.';});}function closeProofCamera(){if(proofStream){proofStream.getTracks().forEach(function(t){t.stop();});proofStream=null;}document.getElementById('proofCamera').hidden=true;}function takeProofPhoto(){var v=document.getElementById('proofVideo'),c=document.getElementById('proofCanvas');if(!v.videoWidth){document.getElementById('proofHint').innerHTML='Esperá a que la cámara esté lista.';return;}var max=1280,w=v.videoWidth,h=v.videoHeight;if(w>max){h=Math.round(h*max/w);w=max;}c.width=w;c.height=h;c.getContext('2d').drawImage(v,0,0,w,h);var data=c.toDataURL('image/jpeg',0.82);document.getElementById('proofData').value=data;document.getElementById('proofPreview').innerHTML='<img src=\"'+data+'\" alt=\"Comprobante capturado\"/>';document.getElementById('proofHint').innerHTML='✓ Foto del comprobante capturada.';closeProofCamera();}function calcShip(cp){var n=(cp||'').replace(/[^0-9]/g,'');var cost=n.length>=4?(n.substring(0,2)==='55'?1200:(n.substring(0,2)==='50'?900:1500)):0;document.getElementById('shipCost').innerHTML='$ '+cost.toFixed(2);document.getElementById('grandTotal').innerHTML='$ ' + ("+subtotal.ToString(System.Globalization.CultureInfo.InvariantCulture)+"+cost).toFixed(2);document.getElementById('shipHint').innerHTML=cost>0?'✓ Envío estimado para CP '+n:'Ingresá el código postal para estimar el envío.';}</script>");
            b.Append(Footer()); return b.ToString();
        }

        private string CheckoutSubmit(string body, WebUser user)
        {
            if (user == null || user.Role != "buyer") return SellerDenied();
            Dictionary<long,int> cart = GetCart(user); List<PromotionSelection> promoCart = GetPromotionCart(user); if (cart.Count == 0 && promoCart.Count == 0) return RedirectPage("/cart", "Tu carrito está vacío.");
            foreach (KeyValuePair<long,int> item in cart) { Product p = FindProduct(item.Key); if (p == null || !p.Active || !p.OnlineEnabled || p.Stock < item.Value) return Page("Checkout", "<div class='error'>El producto <b>" + E(p == null ? "" : p.Name) + "</b> ya no tiene stock suficiente. Volvé al carrito.</div><a class='btn' href='/cart'>Volver al carrito</a>"); }
            Dictionary<string,string> f = Form(body);
            string contactPhone = (f.Get("phone") ?? "").Trim();
            string contactEmail = (f.Get("contactEmail") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(f.Get("name")) || (string.IsNullOrWhiteSpace(contactPhone) && string.IsNullOrWhiteSpace(contactEmail)))
                return Page("Checkout", "<div class='error'><b>Falta un dato de contacto.</b><p>Ingresá tu nombre y al menos un teléfono o correo electrónico para que el comercio pueda contactarte.</p></div>" + CheckoutPage(user));
            foreach (PromotionSelection pr in promoCart) { foreach (long pid in pr.ProductIds) { Product pp = FindProduct(pid); if (pp == null || !pp.Active || !pp.OnlineEnabled || pp.Stock < pr.Quantity) return Page("Checkout", "<div class='error'>La promoción <b>" + E(pr.Name) + "</b> ya no tiene stock suficiente.</div>" + CheckoutPage(user)); } }
            decimal subtotal = CartTotal(cart) + PromotionCartTotal(user); decimal shipping = ShippingCost(f.Get("postalCode")); decimal total = subtotal + shipping; StringBuilder items = new StringBuilder("["); bool first = true;
            foreach (KeyValuePair<long,int> item in cart) { Product p = FindProduct(item.Key); decimal unit = p.SalePrice > 0 ? p.SalePrice : p.Price; if (!first) items.Append(","); first=false; items.Append("{\"productId\":" + p.Id + ",\"sku\":\"" + JsonSafe(p.SKU) + "\",\"name\":\"" + JsonSafe(p.Name) + "\",\"quantity\":" + item.Value + ",\"unitPrice\":" + unit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"); }
            foreach (PromotionSelection pr in promoCart) { if (!first) items.Append(","); first=false; items.Append("{\"promotionId\":" + pr.PromotionId + ",\"name\":\"" + JsonSafe(pr.Name) + "\",\"quantity\":" + pr.Quantity + ",\"unitPrice\":" + pr.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"productIds\":\"" + JsonSafe(string.Join(",", pr.ProductIds)) + "\"}"); }
            items.Append("]");
            string proof = SaveDataUrl(f.Get("proofData"), "comprobante", string.IsNullOrWhiteSpace(contactEmail) ? user.Email : contactEmail); string method = f.Get("paymentMethod"); bool hasProof = proof.Length > 0;
            if (method != "Transferencia" && method != "Mercado Pago" && method != "Efectivo") method = "Transferencia";
            if ((method == "Transferencia" || method == "Mercado Pago") && !hasProof)
                return Page("Checkout", "<div class='error'><b>Falta el comprobante de pago.</b><p>Para " + E(method) + " tenés que adjuntar la captura o foto del comprobante antes de enviar el pedido.</p></div>" + CheckoutPage(user));
            foreach (KeyValuePair<long,int> item in cart) { Product p = FindProduct(item.Key); p.Stock -= item.Value; _store.SaveProduct(p); }
            foreach (PromotionSelection pr in promoCart) foreach (long pid in pr.ProductIds) { Product p = FindProduct(pid); if (p != null) { p.Stock -= pr.Quantity; _store.SaveProduct(p); } }
            Customer customer = _store.GetCustomers("").FirstOrDefault(c =>
                (!string.IsNullOrWhiteSpace(contactEmail) && string.Equals(c.Email, contactEmail, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(contactPhone) && string.Equals(c.Phone, contactPhone, StringComparison.OrdinalIgnoreCase)));
            if (customer == null) customer = new Customer { Name=f.Get("name"), Email=contactEmail, Phone=contactPhone, Address=f.Get("address") };
            else { customer.Name=f.Get("name"); if(!string.IsNullOrWhiteSpace(contactEmail)) customer.Email=contactEmail; if(!string.IsNullOrWhiteSpace(contactPhone)) customer.Phone=contactPhone; customer.Address=f.Get("address"); }
            _store.SaveCustomer(customer);
            Order order = new Order { CustomerId=customer.Id, CustomerName=f.Get("name"), CustomerEmail=contactEmail, Phone=contactPhone, Fulfillment=f.Get("fulfillment")=="Retiro"?"Retiro":"Delivery", Address=f.Get("address"), PostalCode=f.Get("postalCode"), ShippingCost=shipping, Notes=f.Get("notes"), Status="Pendiente", Total=total, CreatedAt=DateTime.Now, PaymentMethod=method, PaymentStatus=hasProof ? "Comprobante enviado" : "Pendiente", PaymentReference=f.Get("paymentReference"), PaymentProofPath=proof, Source="Web", ItemsJson=items.ToString(), StoreId=StoreId };
            _store.AddOrder(order); ClearCart(user);
            string next = IsGuestUser(user)
                ? "<a class='btn' href='/'>VOLVER A LAS TIENDAS</a>"
                : "<a class='btn' href='/buyer/order?id=" + order.Id + "'>VER MI PEDIDO</a>";
            return Page("Compra confirmada", "<div class='ok'><b>Compra enviada correctamente.</b><p>Pedido <b>#" + order.Id + "</b>. " + (hasProof ? "El comprobante fue enviado al vendedor." : "El vendedor recibirá tu pedido y te indicará cómo continuar con el pago.") + "</p><p>El comercio conservará tus datos de contacto como cliente para poder comunicarse con vos sobre el pedido.</p>" + next + "</div>");
        }

        private List<PromotionSelection> GetPromotionCart(WebUser user) { string key=(user==null?"":(user.Email??"").Trim().ToLowerInvariant()); lock(_sessionSync){ List<PromotionSelection> list; return _promotionCarts.TryGetValue(key,out list)?list.Select(x=>x.Clone()).ToList():new List<PromotionSelection>(); } }
        private decimal PromotionCartTotal(WebUser user) { return GetPromotionCart(user).Sum(x=>x.UnitPrice*x.Quantity); }

        private decimal CartTotal(Dictionary<long,int> cart) { decimal total=0m; foreach(KeyValuePair<long,int> x in cart){Product p=FindProduct(x.Key); if(p!=null){decimal u=p.SalePrice>0?p.SalePrice:p.Price; total+=u*x.Value;}} return total; }
        private string JsonSafe(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " "); }
        private long ParseLong(string s) { long x; return long.TryParse(s, out x) ? x : 0; }

        private string BuyerHome(WebUser user)
        {
            if (!CanBuyAsBuyer(user)) return Page("Mi cuenta", "<div class='error'>Ingresá como comprador.</div>");
            List<Order> orders = _store.GetOrders("").Where(o => string.Equals(o.CustomerEmail, user.Email, StringComparison.OrdinalIgnoreCase)).OrderByDescending(o=>o.CreatedAt).Take(5).ToList();
            Dictionary<long,int> cart=GetCart(user); StringBuilder b=new StringBuilder(); b.Append(Header("Mi cuenta")); b.Append(TopNav(user));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CUENTA DE COMPRADOR</span><h1>Hola, " + E(user.Name) + " 👋</h1><p>Desde acá controlás tus compras, comprobantes y pedidos.</p></div><a class='btn' href='/cart'>🛒 Carrito (" + cart.Values.Sum() + ")</a></div><div class='kpi-grid'>" + Kpi("PEDIDOS", orders.Count.ToString(), "últimos pedidos", "green") + Kpi("EN PROCESO", orders.Count(o=>o.Status!="Entregado" && o.Status!="Cancelado").ToString(), "pendientes", "yellow") + "</div><section class='panel'><div class='panel-title'><h2>Últimos pedidos</h2><a href='/buyer/orders'>Ver todos</a></div>" + OrderList(orders) + "</section>");
            b.Append(Footer()); return b.ToString();
        }

        private string BuyerOrders(WebUser user)
        {
            if (!CanBuyAsBuyer(user)) return Page("Mis pedidos", "<div class='error'>Ingresá como comprador.</div>");
            List<Order> orders=_store.GetOrders("").Where(o=>string.Equals(o.CustomerEmail,user.Email,StringComparison.OrdinalIgnoreCase)).OrderByDescending(o=>o.CreatedAt).ToList(); StringBuilder b=new StringBuilder(); b.Append(Header("Mis pedidos")); b.Append(TopNav(user)); b.Append("<div class='seller-head'><div><span class='eyebrow'>HISTORIAL</span><h1>Mis pedidos</h1><p>Seguimiento de estado y pagos.</p></div></div><div class='panel table-panel'><table class='pro-table'><tr><th>Pedido</th><th>Fecha</th><th>Pago</th><th>Estado</th><th>Total</th></tr>"); foreach(Order o in orders)b.Append("<tr><td><a href='/buyer/order?id="+o.Id+"'><b>#"+o.Id+"</b></a></td><td>"+o.CreatedAt.ToString("dd/MM/yyyy HH:mm")+"</td><td>"+StatusBadge(o.PaymentStatus)+"</td><td>"+StatusBadge(o.Status)+"</td><td><b>$ "+o.Total.ToString("N2")+"</b></td></tr>"); b.Append("</table></div>"); b.Append(Footer()); return b.ToString();
        }

        private string BuyerOrderDetail(string query, WebUser user)
        {
            if (!CanBuyAsBuyer(user)) return SellerDenied();
            long id = ParseLong(QueryValue(query, "id")); Order o = _store.GetOrders("").FirstOrDefault(x => x.Id == id && string.Equals(x.CustomerEmail, user.Email, StringComparison.OrdinalIgnoreCase));
            if (o == null) return Page("Pedido", "<div class='error'>Pedido no encontrado.</div>");
            StringBuilder b = new StringBuilder(); b.Append(Header("Pedido #" + o.Id)); b.Append(TopNav(user));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>PEDIDO #" + o.Id + "</span><h1>Estado de tu compra</h1><p>" + o.CreatedAt.ToString("dd/MM/yyyy HH:mm") + " · " + E(o.Fulfillment) + "</p></div>" + StatusBadge(o.Status) + "</div>");
            b.Append("<div class='panel'><div class='kpi-grid'>" + Kpi("TOTAL", "$ " + o.Total.ToString("N2"), "compra", "green") + Kpi("PAGO", o.PaymentStatus, "estado del comprobante", o.PaymentStatus == "Comprobante enviado" ? "yellow" : "green") + "</div>");
            b.Append("<div class='checkout-grid'><section class='panel subpanel'><h2>Productos solicitados</h2>" + OrderItemsHtml(o.ItemsJson) + "<p><b>Dirección:</b> " + E(o.Address) + "</p><p><b>Código postal:</b> " + E(o.PostalCode) + "</p><p><b>Referencia:</b> " + E(o.PaymentReference) + "</p>");
            if (!string.IsNullOrWhiteSpace(o.PaymentProofPath)) b.Append("<div class='proof-box'><h3>Comprobante enviado</h3><a href='/media?p=" + Uri.EscapeDataString(o.PaymentProofPath) + "' target='_blank'><img class='proof-full' src='/media?p=" + Uri.EscapeDataString(o.PaymentProofPath) + "' alt='Comprobante de pago'/></a></div>");
            b.Append("</section><aside class='panel subpanel'>");
            if (!string.IsNullOrWhiteSpace(o.SellerMessage)) b.Append("<div class='negotiation seller-note'><b>Mensaje del vendedor</b><p>" + E(o.SellerMessage) + "</p><span class='badge yellow'>" + E(o.NegotiationStatus) + "</span></div>");
            if (o.NegotiationStatus == "Propuesta enviada") b.Append("<form method='post' action='/buyer/order-negotiate'><input type='hidden' name='id' value='" + o.Id + "'/><input type='hidden' name='action' value='accept'/><button class='btn' type='submit'>✓ ACEPTAR PROPUESTA</button></form><form method='post' action='/buyer/order-negotiate' style='margin-top:8px'><input type='hidden' name='id' value='" + o.Id + "'/><input type='hidden' name='action' value='reject'/><button class='btn danger' type='submit'>RECHAZAR PROPUESTA</button></form>");
            b.Append("<h3>Responder al vendedor</h3><form method='post' action='/buyer/order-negotiate'><input type='hidden' name='id' value='" + o.Id + "'/><input type='hidden' name='action' value='message'/><textarea name='message' placeholder='Ej.: No tengo problema en cambiarlo por el modelo X.' required></textarea><button class='btn' type='submit'>ENVIAR MENSAJE</button></form>");
            if (string.IsNullOrWhiteSpace(o.BuyerMessage) == false) b.Append("<p class='user'><b>Tu último mensaje:</b><br/>" + E(o.BuyerMessage) + "</p>");
            if (o.Status == "Listo" || o.Status == "Enviado") b.Append("<form method='post' action='/buyer/order-status'><input type='hidden' name='id' value='" + o.Id + "'/><button class='btn' type='submit'>✓ CONFIRMAR RECEPCIÓN Y FINALIZAR</button></form>");
            else if (o.Status == "Pendiente" && o.PaymentStatus != "Confirmado") b.Append("<form method='post' action='/buyer/order-status'><input type='hidden' name='id' value='" + o.Id + "'/><button class='btn danger' type='submit'>CANCELAR PEDIDO</button></form>");
            if (o.Status == "Entregado") b.Append("<hr/><h3>Calificá tu compra</h3><form method='post' action='/buyer/review'><input type='hidden' name='orderId' value='" + o.Id + "'/><select name='rating'><option value='5'>★★★★★ Excelente</option><option value='4'>★★★★ Muy bueno</option><option value='3'>★★★ Bueno</option><option value='2'>★★ Regular</option><option value='1'>★ Malo</option></select><textarea name='text' placeholder='Contanos tu experiencia' required></textarea><button class='btn' type='submit'>PUBLICAR RESEÑA</button></form>");
            b.Append("</aside></div></div>"); b.Append(Footer()); return b.ToString();
        }

        private string OrderItemsHtml(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return "<div class='empty'>No hay detalle de productos disponible.</div>";
            System.Text.RegularExpressions.MatchCollection ms = System.Text.RegularExpressions.Regex.Matches(json, "\\{[^}]*\\}");
            StringBuilder b = new StringBuilder(); b.Append("<div class='requested-items'>");
            foreach (System.Text.RegularExpressions.Match m in ms)
            {
                string item = m.Value; long id = ParseJsonLong(item, "productId"); int qty = (int)ParseJsonLong(item, "quantity"); decimal price = ParseJsonDecimal(item, "unitPrice"); string name = ParseJsonString(item, "name");
                Product p = id > 0 ? FindProduct(id) : null; if (string.IsNullOrWhiteSpace(name) && p != null) name = p.Name;
                b.Append("<div class='requested-item'>" + ProductThumb(p, 58) + "<div><b>" + E(name) + "</b><small>Cantidad: " + qty + " · Unitario: $ " + price.ToString("N2") + "</small>" + (p == null ? "<span class='badge red'>Producto ya no disponible</span>" : (p.Stock < qty ? "<span class='badge red'>Stock actual: " + p.Stock + "</span>" : "<span class='badge green'>Stock disponible</span>")) + "</div></div>");
            }
            b.Append("</div>"); return b.ToString();
        }
        private long ParseJsonLong(string json, string key) { string v = ParseJsonToken(json, key); long n; return long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0; }
        private decimal ParseJsonDecimal(string json, string key) { string v = ParseJsonToken(json, key); decimal n; return decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0m; }
        private string ParseJsonString(string json, string key) { string v = ParseJsonToken(json, key); return v.Replace("\\\"", "\""); }
        private string ParseJsonToken(string json, string key)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*(?:\"(?<str>(?:\\\"|[^\"])*)\"|(?<num>-?[0-9.]+))");
            return m.Success ? (m.Groups["str"].Success ? m.Groups["str"].Value : m.Groups["num"].Value) : "";
        }

        private string BuyerOrderNegotiate(string body, WebUser user)
        {
            if (user == null || user.Role != "buyer") return SellerDenied(); Dictionary<string,string> f = Form(body); long id = ParseLong(f.Get("id"));
            Order o = _store.GetOrders("").FirstOrDefault(x => x.Id == id && string.Equals(x.CustomerEmail, user.Email, StringComparison.OrdinalIgnoreCase)); if (o == null) return Page("Pedido", "<div class='error'>Pedido no encontrado.</div>");
            string action = f.Get("action");
            if (action == "accept") { o.NegotiationStatus = "Propuesta aceptada"; o.BuyerMessage = "Acepto la propuesta del vendedor."; _store.SaveOrder(o); }
            else if (action == "reject") { o.NegotiationStatus = "Propuesta rechazada"; o.BuyerMessage = "No acepto la propuesta. Necesito otra alternativa."; _store.SaveOrder(o); }
            else { o.BuyerMessage = f.Get("message"); o.NegotiationStatus = "Respuesta del comprador"; _store.SaveOrder(o); }
            return RedirectPage("/buyer/order?id=" + id, "Respuesta enviada al vendedor.");
        }

        private string BuyerOrderStatus(string body, WebUser user)
        {
            if (user == null || user.Role != "buyer") return SellerDenied();
            long id=ParseLong(Form(body).Get("id")); Order o=_store.GetOrders("").FirstOrDefault(x=>x.Id==id && string.Equals(x.CustomerEmail,user.Email,StringComparison.OrdinalIgnoreCase));
            if(o==null)return Page("Pedido","<div class='error'>Pedido no encontrado.</div>");
            if(o.Status=="Entregado") return RedirectPage("/buyer/order?id="+id,"La compra ya estaba finalizada.");
            if(o.Status=="Listo" || o.Status=="Enviado") { _store.UpdateOrderStatus(id,"Entregado"); return RedirectPage("/buyer/order?id="+id,"Compra finalizada. Gracias por tu compra."); }
            if(o.Status=="Pendiente" && (o.PaymentStatus=="Pendiente" || o.PaymentStatus=="Rechazado")) { _store.UpdateOrderStatus(id,"Cancelado"); return RedirectPage("/buyer/orders","Pedido cancelado."); }
            return RedirectPage("/buyer/order?id="+id,"El vendedor todavía está procesando tu pedido.");
        }

        private string SellerHome(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            List<Order> orders = _store.GetOrders(""); List<Product> products = _store.GetProducts("");
            DateTime today = DateTime.Today;
            List<Order> todayOrders = orders.Where(o => o.CreatedAt.Date == today && o.Status != "Cancelado").ToList();
            decimal todaySales = todayOrders.Sum(o => o.Total);
            int pending = orders.Count(o => o.Status == "Pendiente"); int delivery = orders.Count(o => o.Fulfillment == "Delivery" && o.Status != "Entregado" && o.Status != "Cancelado");
            int low = products.Count(p => p.Active && p.Stock <= p.MinimumStock);
            StringBuilder b = new StringBuilder(); b.Append(Header("Dashboard vendedor")); b.Append(SellerNav(user, "Resumen"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CENTRAL DE VENTAS</span><h1>Hola, " + E(user.Name) + " 👋</h1><p>Control completo de tu tienda, pedidos, inventario y rendimiento.</p></div><a class='btn' href='/store'>Ver mi tienda</a><a class='btn alt' href='/'>🛍 Comprar en otras tiendas</a></div>");
            b.Append("<div class='kpi-grid'>" + Kpi("VENTAS HOY", "$ " + todaySales.ToString("N2"), "↗ " + todayOrders.Count + " operaciones", "green") + Kpi("PEDIDOS", pending.ToString(), "pendientes de atención", pending > 0 ? "yellow" : "green") + Kpi("PRODUCTOS", products.Count.ToString(), low + " con stock bajo", low > 0 ? "red" : "green") + Kpi("DELIVERY", delivery.ToString(), "entregas abiertas", delivery > 0 ? "yellow" : "green") + "</div>");
            b.Append("<div class='dash-grid'><section class='panel'><div class='panel-title'><h2>Actividad de hoy</h2><a href='/seller/analytics'>Ver métricas</a></div><div class='bar-chart'>");
            int[] buckets = new int[8]; foreach (Order o in todayOrders) { int h = o.CreatedAt.Hour; int idx = Math.Max(0, Math.Min(7, (h - 9) / 2)); buckets[idx]++; }
            int max = Math.Max(1, buckets.Max()); for (int i = 0; i < buckets.Length; i++) b.Append("<div class='bar-col'><div class='bar' style='height:" + (30 + (int)(110.0 * buckets[i] / max)) + "px'></div><small>" + (9 + i * 2) + "h</small></div>");
            b.Append("</div></section><section class='panel'><div class='panel-title'><h2>Acciones urgentes</h2></div><div class='todo-list'>" + Todo("Pedidos pendientes", pending, "/seller/orders", pending > 0 ? "yellow" : "green") + Todo("Stock bajo", low, "/seller/products", low > 0 ? "red" : "green") + Todo("Delivery abierto", delivery, "/seller/orders", delivery > 0 ? "yellow" : "green") + "</div></section></div>");
            b.Append("<div class='dash-grid'><section class='panel'><div class='panel-title'><h2>Productos destacados</h2><a href='/seller/products'>Ver catálogo</a></div><div class='mini-products'>");
            foreach (Product p in products.Where(x => x.Active).OrderByDescending(x => x.Stock).Take(5)) b.Append("<div class='mini-product'>" + ProductThumb(p, 58) + "<div><b>" + E(p.Name) + "</b><small>SKU " + E(p.SKU) + " · Stock " + p.Stock + "</small></div><strong>$ " + (p.SalePrice > 0 ? p.SalePrice : p.Price).ToString("N0") + "</strong></div>");
            b.Append("</div></section><section class='panel'><div class='panel-title'><h2>Últimos pedidos</h2><a href='/seller/orders'>Ver todos</a></div>" + OrderList(orders.OrderByDescending(x => x.CreatedAt).Take(5).ToList()) + "</section></div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>🧠 Recomendaciones inteligentes</h2></div><div class='insight-grid'>"+PredictiveInsights(products,orders)+"</div></div>"); b.Append(Footer()); return b.ToString();
        }

        
        private string PredictiveInsights(List<Product> products, List<Order> orders)
        {
            Product low=products.Where(x=>x.Active).OrderBy(x=>x.Stock).FirstOrDefault();
            decimal avg=orders.Count==0?0:orders.Average(x=>x.Total);
            StringBuilder insights=new StringBuilder();
            if(low!=null) insights.Append(Insight("Reposición","Revisá " + E(low.Name) + " porque tiene " + low.Stock + " unidades."));
            if(avg>0) insights.Append(Insight("Ticket medio","El ticket promedio actual es $ " + avg.ToString("N0") + ". Usalo como referencia para promociones y combos."));
            insights.Append(Insight("Contenido","Los productos con foto, descripción pública y video tienen una ficha más completa para compartir."));
            return insights.ToString();
        }
        private string SellerOrderDetail(string query, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied(); long id = ParseLong(QueryValue(query, "id")); Order o = _store.GetOrders("").FirstOrDefault(x => x.Id == id);
            if (o == null) return Page("Pedido", "<div class='error'>Pedido no encontrado.</div>");
            StringBuilder b = new StringBuilder(); b.Append(Header("Pedido #" + o.Id)); b.Append(SellerNav(user, "Pedidos"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>DETALLE OPERATIVO</span><h1>Pedido #" + o.Id + "</h1><p>" + E(o.CustomerName) + " · " + E(o.CustomerEmail) + " · " + o.CreatedAt.ToString("dd/MM/yyyy HH:mm") + "</p></div>" + StatusBadge(o.Status) + "</div>");
            b.Append("<div class='checkout-grid'><section class='panel'><h2>Lo que pidió el cliente</h2>" + OrderItemsHtml(o.ItemsJson) + "<p><b>Entrega:</b> " + E(o.Fulfillment) + " · " + E(o.Address) + " · CP " + E(o.PostalCode) + "</p><p><b>Notas:</b> " + E(o.Notes) + "</p><h2>Pago</h2><p>" + StatusBadge(o.PaymentStatus) + " · " + E(o.PaymentMethod) + " · Referencia: <b>" + E(o.PaymentReference) + "</b></p>");
            if (!string.IsNullOrWhiteSpace(o.PaymentProofPath)) b.Append("<div class='proof-box'><h3>Comprobante enviado por el comprador</h3><a href='/media?p=" + Uri.EscapeDataString(o.PaymentProofPath) + "' target='_blank'><img class='proof-full' src='/media?p=" + Uri.EscapeDataString(o.PaymentProofPath) + "' alt='Comprobante de pago'/></a></div>");
            else b.Append("<div class='empty'>El comprador todavía no envió comprobante.</div>");
            b.Append("</section><aside class='panel'><h2>Operación</h2><form method='post' action='/seller/payment-status'><input type='hidden' name='id' value='" + o.Id + "'/><input type='hidden' name='reference' value='" + E(o.PaymentReference) + "'/><select name='paymentStatus'><option" + (o.PaymentStatus == "Pendiente" ? " selected" : "") + ">Pendiente</option><option" + (o.PaymentStatus == "Confirmado" ? " selected" : "") + ">Confirmado</option><option" + (o.PaymentStatus == "Rechazado" ? " selected" : "") + ">Rechazado</option></select><button class='btn small' type='submit'>ACTUALIZAR PAGO</button></form><hr/><form method='post' action='/seller/order-status'><input type='hidden' name='id' value='" + o.Id + "'/><select name='status'><option>Seleccionar estado</option><option" + (o.Status == "Pendiente" ? " selected" : "") + ">Pendiente</option><option" + (o.Status == "Preparando" ? " selected" : "") + ">Preparando</option><option" + (o.Status == "Listo" ? " selected" : "") + ">Listo</option><option" + (o.Status == "Enviado" ? " selected" : "") + ">Enviado</option><option" + (o.Status == "En reparto" ? " selected" : "") + ">En reparto</option><option" + (o.Status == "Entregado" ? " selected" : "") + ">Entregado</option><option" + (o.Status == "Rechazado" ? " selected" : "") + ">Rechazado</option><option" + (o.Status == "Cancelado" ? " selected" : "") + ">Cancelado</option></select><input name='carrier' placeholder='Logística' value='" + E(o.Carrier) + "'/><input name='tracking' placeholder='Tracking' value='" + E(o.TrackingNumber) + "'/><button class='btn small' type='submit'>ACTUALIZAR PEDIDO</button></form><hr/><h2>Falta de stock / negociación</h2><form method='post' action='/seller/order-negotiate'><input type='hidden' name='id' value='" + o.Id + "'/><select name='action'><option value='proposal'>Proponer cambio</option><option value='message'>Enviar mensaje</option><option value='resolve'>Marcar resuelto</option></select><textarea name='message' placeholder='Ej.: No tengo el producto azul talle M. Puedo ofrecer azul talle L o el modelo X por el mismo precio.' required>" + E(o.SellerMessage) + "</textarea><button class='btn' type='submit'>ENVIAR AL COMPRADOR</button></form>");
            if (!string.IsNullOrWhiteSpace(o.BuyerMessage)) b.Append("<div class='negotiation buyer-note'><b>Respuesta del comprador:</b><p>" + E(o.BuyerMessage) + "</p></div>");
            b.Append("</aside></div>"); b.Append(Footer()); return b.ToString();
        }

        private string SellerOrderNegotiate(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied(); Dictionary<string,string> f = Form(body); long id = ParseLong(f.Get("id")); Order o = _store.GetOrders("").FirstOrDefault(x => x.Id == id); if (o == null) return Page("Pedido", "<div class='error'>Pedido no encontrado.</div>");
            string action = f.Get("action"); string msg = (f.Get("message") ?? "").Trim(); o.SellerMessage = msg; o.BuyerMessage = ""; o.NegotiationStatus = action == "resolve" ? "Resuelto" : (action == "proposal" ? "Propuesta enviada" : "Mensaje enviado"); _store.SaveOrder(o);
            return RedirectPage("/seller/order-detail?id=" + id, "Comunicación enviada al comprador.");
        }

        private string SellerOrders(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            List<Order> orders = _store.GetOrders("").OrderByDescending(x=>x.CreatedAt).ToList();
            StringBuilder b = new StringBuilder(); b.Append(Header("Pedidos vendedor")); b.Append(SellerNav(user, "Pedidos"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>OPERACIONES</span><h1>Pedidos</h1><p>Actualizá el estado y la barra de progreso sin salir de la consola. Los cambios se aplican en esta misma pantalla.</p></div></div>");
            b.Append("<div class='panel table-panel'><div class='filters'><span class='filter active'>Todos</span><span class='filter'>Pendientes</span><span class='filter'>Delivery</span><span class='filter'>Finalizados</span></div><table class='pro-table'><tr><th>Pedido</th><th>Cliente</th><th>Fecha</th><th>Estado</th><th>Total</th><th>Acción</th></tr>");
            foreach (Order o in orders)
            {
                b.Append("<tr id='local-order-"+o.Id+"'><td><a href='/seller/order-detail?id="+o.Id+"'><b>#"+o.Id+"</b></a><small>"+E(o.Source)+" · "+E(o.Fulfillment)+"</small></td><td>"+E(o.CustomerName)+"<small>"+E(o.Phone)+"</small></td><td>"+o.CreatedAt.ToString("dd/MM HH:mm")+"</td><td class='local-order-state'>"+StatusBadge(o.Status)+"<div class='progress'><span class='"+StatusClass(o.Status)+"' style='width:"+StatusPercent(o.Status)+"%'></span></div></td><td><b>$ "+o.Total.ToString("N2")+"</b><small>"+E(o.PaymentMethod)+"</small></td><td>"+StatusBadge(o.PaymentStatus)+(string.IsNullOrEmpty(o.PaymentProofPath)?"":" <a href='/media?p="+Uri.EscapeDataString(o.PaymentProofPath)+"' target='_blank'>Ver comprobante</a>")+"<form method='post' action='/seller/payment-status'><input type='hidden' name='id' value='"+o.Id+"'/><input type='hidden' name='reference' value='"+E(o.PaymentReference)+"'/><select name='paymentStatus'><option>Pendiente</option><option>Confirmado</option><option>Rechazado</option></select><button class='btn small' type='submit'>Pago</button></form><form method='post' action='/seller/order-status' class='local-order-form' onsubmit='return updateLocalOrderStatus(this)'><input type='hidden' name='id' value='"+o.Id+"'/><select name='status'><option>Seleccionar</option><option"+(o.Status=="Pendiente"?" selected":"")+">Pendiente</option><option"+(o.Status=="Preparando"?" selected":"")+">Preparando</option><option"+(o.Status=="Listo"?" selected":"")+">Listo</option><option"+(o.Status=="Enviado"?" selected":"")+">Enviado</option><option"+(o.Status=="Entregado"?" selected":"")+">Entregado</option><option"+(o.Status=="Rechazado"?" selected":"")+">Rechazado</option><option"+(o.Status=="Cancelado"?" selected":"")+">Cancelado</option></select><input name='carrier' placeholder='Logística' value='"+E(o.Carrier)+"'/><input name='tracking' placeholder='Tracking' value='"+E(o.TrackingNumber)+"'/><button class='btn small' type='submit'>Actualizar</button><span class='local-order-msg' aria-live='polite'></span></form></td></tr>");
            }
            b.Append("</table></div>");
            b.Append("<script>function updateLocalOrderStatus(form){var row=form.closest('tr'),msg=form.querySelector('.local-order-msg'),btn=form.querySelector('button[type=submit]'),data=new URLSearchParams();Array.prototype.forEach.call(new FormData(form),function(v,k){data.append(k,v);});btn.disabled=true;msg.textContent=' Guardando...';fetch(form.action,{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded;charset=UTF-8','X-Nexo-Ajax':'1'},body:data.toString(),credentials:'same-origin'}).then(function(r){return r.text().then(function(t){return {ok:r.ok,text:t};});}).then(function(x){if(x.text.indexOf('OK|status|')!==0){msg.textContent=' No se pudo actualizar';btn.disabled=false;return;}var status=form.querySelector('select[name=status]').value,state=row.querySelector('.local-order-state');if(state){var cls=(status==='Cancelado'||status==='Rechazado')?'red':(status==='Pendiente'||status==='Preparando')?'yellow':'green';var pct=status==='Pendiente'?20:status==='Preparando'?40:status==='Listo'?65:status==='Enviado'?82:status==='Entregado'?100:12;state.innerHTML='<span class=\"badge '+cls+'\">'+status+'</span><div class=\"progress\"><span class=\"'+cls+'\" style=\"width:'+pct+'%\"></span></div>';}msg.textContent=' ✓ Actualizado';setTimeout(function(){msg.textContent='';},1800);btn.disabled=false;}).catch(function(){msg.textContent=' Error de conexión';btn.disabled=false;});return false;}</script>");
            b.Append(Footer()); return b.ToString();
        }

        private string SellerOrderStatus(string body, WebUser user)
        {
            if (!IsSeller(user)) return "ERROR|session";
            Dictionary<string,string> f = Form(body); long id;
            if (!long.TryParse(f.Get("id"), out id)) return "ERROR|id";
            string status = (f.Get("status") ?? "").Trim(); string[] allowed = { "Pendiente", "Preparando", "Listo", "Enviado", "En reparto", "Entregado", "Rechazado", "Cancelado" };
            Order tracked = _store.GetOrders("").FirstOrDefault(x => x.Id == id);
            if (tracked == null || !allowed.Contains(status)) return "ERROR|status";
            _store.UpdateOrderStatus(id, status);
            tracked.Status = status;
            tracked.Carrier = f.Get("carrier");
            tracked.TrackingNumber = f.Get("tracking");
            _store.SaveOrder(tracked);
            PushCentralOrderStatus(tracked);
            return "OK|status|" + id + "|" + WebUtility.UrlEncode(status);
        }

        private void PushCentralOrderStatus(Order order)
        {
            try
            {
                if (order == null || string.IsNullOrWhiteSpace(order.CentralOrderId)) return;
                string baseUrl = (_store.GetSetting("web_api_url", "") ?? "").Trim().TrimEnd('/');
                if (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl.Substring(0, baseUrl.Length - 4).TrimEnd('/');
                if (baseUrl.Length == 0) return;
                string body = "storeId=" + Uri.EscapeDataString(_store.StoreId) + "&centralOrderId=" + Uri.EscapeDataString(order.CentralOrderId) + "&syncKey=" + Uri.EscapeDataString(ComputeStorePairKey(_store.StoreId)) + "&status=" + Uri.EscapeDataString(order.Status ?? "Pendiente") + "&carrier=" + Uri.EscapeDataString(order.Carrier ?? "") + "&trackingNumber=" + Uri.EscapeDataString(order.TrackingNumber ?? "");
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/orders/status");
                req.Method = "POST"; req.Timeout = 6000; req.ReadWriteTimeout = 6000; req.ContentType = "application/x-www-form-urlencoded";
                byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                using (Stream st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
                using (WebResponse resp = req.GetResponse()) { using (StreamReader rr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) rr.ReadToEnd(); }
            }
            catch { }
        }

        private string SellerProducts(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            List<Product> products = _store.GetProducts("").Where(p => p.Active).OrderBy(x => x.Category).ThenBy(x => x.Name).ToList();
            StringBuilder b = new StringBuilder(); b.Append(Header("Productos vendedor")); b.Append(SellerNav(user, "Productos"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CATÁLOGO</span><h1>Productos</h1><p>Cargá, editá y publicá productos desde el Seller Center, con imagen desde archivo o cámara del dispositivo.</p></div><a class='btn' href='/seller/products/new'>+ NUEVO PRODUCTO</a></div><div class='product-grid seller-products'>");
            foreach (Product p in products) b.Append("<article class='product-card'><div class='seller-photo'>" + ProductThumb(p, 220) + "</div><div class='product-body'><span class='mini-tag'>" + E(p.Category) + "</span><h3>" + E(p.Name) + "</h3><small>SKU: " + E(p.SKU) + " · Código: " + E(p.Barcode) + "</small><div class='price'>$ " + (p.SalePrice > 0 ? p.SalePrice : p.Price).ToString("N2") + "</div><div class='stock-row'><span>Stock <b>" + p.Stock + "</b></span><span>Mínimo " + p.MinimumStock + "</span></div><div class='buy-row'><a class='btn alt' href='/seller/products/edit?id=" + p.Id + "'>Editar</a><form method='post' action='/seller/products/delete' style='display:inline'><input type='hidden' name='id' value='" + p.Id + "'/><button class='btn danger' type='submit' onclick='return confirm(&quot;¿Eliminar producto?&quot;)'>Eliminar</button></form></div></div></article>");
            b.Append("</div>"); b.Append(Footer()); return b.ToString();
        }

        private string SellerProductForm(WebUser user, long id)
        {
            if (!IsSeller(user)) return SellerDenied(); Product p = id > 0 ? FindProduct(id) : new Product(); if (p == null) return Page("Producto", "<div class='error'>Producto no encontrado.</div>");
            string title = id > 0 ? "Editar producto" : "Nuevo producto"; List<MediaItem> media = _store.GetMedia().Where(m => string.Equals(m.Type, "Imagen", StringComparison.OrdinalIgnoreCase) && File.Exists(m.Path)).ToList(); List<MediaItem> videos = _store.GetMedia().Where(m => string.Equals(m.Type, "Video", StringComparison.OrdinalIgnoreCase) && File.Exists(m.Path)).ToList(); StringBuilder mediaOptions = new StringBuilder(); StringBuilder videoOptions = new StringBuilder();
            foreach (MediaItem m in media) mediaOptions.Append("<option value='" + E(m.Path) + "'" + (string.Equals(p.ImagePath, m.Path, StringComparison.OrdinalIgnoreCase) ? " selected" : "") + ">" + E(m.FileName) + "</option>");
            foreach (MediaItem m in videos) videoOptions.Append("<option value='" + E(m.Path) + "'" + (string.Equals(p.VideoUrl, m.Path, StringComparison.OrdinalIgnoreCase) ? " selected" : "") + ">" + E(m.FileName) + "</option>");
            StringBuilder b = new StringBuilder(); b.Append(Header(title)); b.Append(SellerNav(user,"Productos"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CATÁLOGO WEB</span><h1>" + title + "</h1><p>Subí una foto, sacala con la cámara o reutilizá una imagen que ya tengas en Multimedia.</p></div></div><div class='panel'><form method='post' action='/seller/products/save'><input type='hidden' name='id' value='" + p.Id + "'/><div class='form-grid'><input name='name' value='" + E(p.Name) + "' placeholder='Nombre del producto' required/><input name='category' value='" + E(p.Category) + "' placeholder='Categoría'/><input name='brand' value='" + E(p.Brand) + "' placeholder='Marca'/><input name='sku' value='" + E(p.SKU) + "' placeholder='SKU'/><input name='barcode' value='" + E(p.Barcode) + "' placeholder='Código de barras'/><input name='price' value='" + p.Price.ToString(CultureInfo.InvariantCulture) + "' placeholder='Precio' required/><input name='salePrice' value='" + p.SalePrice.ToString(CultureInfo.InvariantCulture) + "' placeholder='Precio oferta'/><input name='cost' value='" + p.Cost.ToString(CultureInfo.InvariantCulture) + "' placeholder='Costo'/><input name='stock' value='" + p.Stock + "' placeholder='Stock' required/><input name='minimumStock' value='" + p.MinimumStock + "' placeholder='Stock mínimo'/><input name='size' value='" + E(p.Size) + "' placeholder='Talle / tamaño'/><input name='color' value='" + E(p.Color) + "' placeholder='Color'/></div><textarea name='description' placeholder='Descripción interna'>" + E(p.Description) + "</textarea><textarea name='publicDescription' placeholder='Descripción pública'>" + E(p.PublicDescription) + "</textarea><div class='media-link-row'><input name='videoUrl' value='" + E(p.VideoUrl) + "' placeholder='Video del producto (URL opcional)'/><select name='mediaVideoPath'><option value=''>Usar URL o mantener actual</option>" + videoOptions.ToString() + "</select></div><div class='upload-box'><b>📷 Imagen del producto</b><div class='media-actions'><label class='btn alt small'>SUBIR FOTO<input id='productFile' type='file' accept='image/*' capture='environment' onchange='loadProductImage(this)'/></label><button class='btn small' type='button' onclick='openProductCamera()'>📷 SACAR FOTO</button></div><div id='productPreview' class='proof-preview'></div><input type='hidden' id='productImageData' name='imageData'/><div class='media-link-row'><select name='mediaPath'><option value=''>No cambiar imagen</option>" + mediaOptions.ToString() + "</select><span class='user'>Elegí una imagen ya guardada en Multimedia.</span></div></div><div id='imageMsg' class='user'>" + (string.IsNullOrEmpty(p.ImagePath) ? "Todavía no hay imagen." : "✓ Ya existe una imagen cargada.") + "</div><div id='productCamera' class='camera-box' hidden><video id='productVideo' autoplay playsinline></video><div class='media-actions'><button class='btn small' type='button' onclick='takeProductPhoto()'>CAPTURAR</button><button class='btn alt small' type='button' onclick='closeProductCamera()'>CERRAR CÁMARA</button></div><canvas id='productCanvas' hidden></canvas></div><label><input type='checkbox' name='onlineEnabled' value='1' " + (p.OnlineEnabled ? "checked" : "") + "/> Publicar en la tienda web</label><label><input type='checkbox' name='active' value='1' " + (p.Active ? "checked" : "") + "/> Producto activo</label><button class='btn' type='submit'>GUARDAR PRODUCTO</button> <a class='btn alt' href='/seller/products'>Cancelar</a></form></div><script>var productStream=null;function loadProductImage(i){var f=i.files&&i.files[0];if(!f)return;if(f.size>4*1024*1024){alert('La imagen supera 4 MB.');i.value='';return;}var r=new FileReader();r.onload=function(){document.getElementById('productImageData').value=r.result;document.getElementById('productPreview').innerHTML='<img src=\"'+r.result+'\" alt=\"Vista previa\"/>';document.getElementById('imageMsg').innerHTML='✓ Imagen lista: '+f.name;};r.readAsDataURL(f);}function openProductCamera(){if(productStream){productStream.getTracks().forEach(function(t){t.stop();});productStream=null;}var box=document.getElementById('productCamera');box.hidden=false;if(!navigator.mediaDevices||!navigator.mediaDevices.getUserMedia){document.getElementById('imageMsg').innerHTML='Tu navegador no permite cámara directa. Usá SUBIR FOTO o Multimedia.';return;}navigator.mediaDevices.getUserMedia({video:{facingMode:{ideal:'environment'}},audio:false}).then(function(stream){productStream=stream;document.getElementById('productVideo').srcObject=stream;}).catch(function(){productStream=null;document.getElementById('productCamera').hidden=true;document.getElementById('imageMsg').innerHTML='No se pudo abrir la cámara. Verificá el permiso.';});}function closeProductCamera(){if(productStream){productStream.getTracks().forEach(function(t){t.stop();});productStream=null;}document.getElementById('productCamera').hidden=true;}function takeProductPhoto(){var v=document.getElementById('productVideo'),c=document.getElementById('productCanvas');if(!v.videoWidth){document.getElementById('imageMsg').innerHTML='Esperá a que la cámara esté lista.';return;}var max=1280,w=v.videoWidth,h=v.videoHeight;if(w>max){h=Math.round(h*max/w);w=max;}c.width=w;c.height=h;c.getContext('2d').drawImage(v,0,0,w,h);var data=c.toDataURL('image/jpeg',0.82);document.getElementById('productImageData').value=data;document.getElementById('productPreview').innerHTML='<img src=\"'+data+'\" alt=\"Foto capturada\"/>';document.getElementById('imageMsg').innerHTML='✓ Foto capturada.';document.querySelector('[name=mediaPath]').value='';closeProductCamera();}</script>"); b.Append(Footer()); return b.ToString();
        }

        private string SellerProductSave(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied(); Dictionary<string,string> f=Form(body); long id=ParseLong(f.Get("id")); Product p=id>0?FindProduct(id):new Product(); if(p==null)return Page("Producto","<div class='error'>Producto no encontrado.</div>");
            decimal price, sale, cost; int stock, min; if(!decimal.TryParse(f.Get("price"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out price) || price<0 || !int.TryParse(f.Get("stock"),out stock) || stock<0) return Page("Producto", "<div class='error'>Precio o stock inválido.</div>"+SellerProductForm(user,id));
            decimal.TryParse(f.Get("salePrice"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out sale); decimal.TryParse(f.Get("cost"),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out cost); int.TryParse(f.Get("minimumStock"),out min);
            p.Name=f.Get("name"); p.Category=f.Get("category"); p.Brand=f.Get("brand"); p.SKU=f.Get("sku"); p.Barcode=f.Get("barcode"); p.Price=price; p.SalePrice=sale; p.Cost=cost; p.Stock=stock; p.MinimumStock=Math.Max(0,min); p.Size=f.Get("size"); p.Color=f.Get("color"); p.Description=f.Get("description"); p.PublicDescription=f.Get("publicDescription"); p.VideoUrl=f.Get("videoUrl"); string selectedVideo=f.Get("mediaVideoPath"); if(!string.IsNullOrWhiteSpace(selectedVideo) && File.Exists(selectedVideo) && Path.GetFullPath(selectedVideo).StartsWith(Path.GetFullPath(_store.MediaDirectory), StringComparison.OrdinalIgnoreCase)) p.VideoUrl=selectedVideo; p.OnlineEnabled=f.Get("onlineEnabled")=="1"; p.Active=f.Get("active")=="1"; p.Slug=Slugify(p.Name); string image=SaveDataUrl(f.Get("imageData"),"producto",p.SKU); if(image.Length>0) p.ImagePath=image; else { string mediaPath=f.Get("mediaPath"); if(!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath) && Path.GetFullPath(mediaPath).StartsWith(Path.GetFullPath(_store.MediaDirectory), StringComparison.OrdinalIgnoreCase)) p.ImagePath=mediaPath; } if(string.IsNullOrWhiteSpace(p.PublicDescription))p.PublicDescription=p.Description; _store.SaveProduct(p);
            return RedirectPage("/seller/products","Producto guardado y catálogo actualizado.");
        }

        private string SellerProductDelete(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied(); long id=ParseLong(Form(body).Get("id")); if(id>0)_store.DeleteProduct(id); return RedirectPage("/seller/products","Producto eliminado.");
        }

        private string SellerPaymentStatus(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied(); Dictionary<string,string> f=Form(body); long id=ParseLong(f.Get("id")); string status=f.Get("paymentStatus"); if(status!="Confirmado" && status!="Rechazado" && status!="Pendiente")status="Pendiente"; Order current = _store.GetOrders("").FirstOrDefault(x => x.Id == id); _store.UpdateOrderPayment(id,status,current == null ? "" : current.PaymentProofPath,f.Get("reference")); return RedirectPage("/seller/orders","Estado de pago actualizado.");
        }

        private string Slugify(string text)
        {
            string s=(text??"").Trim().ToLowerInvariant(); StringBuilder b=new StringBuilder(); foreach(char c in s){if(char.IsLetterOrDigit(c))b.Append(c); else if(c==' '||c=='-'||c=='_')b.Append('-');} return b.ToString().Trim('-');
        }

        private string SaveDataUrl(string data, string prefix, string key)
        {
            if(string.IsNullOrWhiteSpace(data) || data.Length>6000000) return ""; try { int comma=data.IndexOf(','); if(comma<0)return ""; string meta=data.Substring(0,comma); string b64=data.Substring(comma+1); byte[] bytes=Convert.FromBase64String(b64); if(bytes.Length>4*1024*1024)return ""; string ext=meta.IndexOf("image/png",StringComparison.OrdinalIgnoreCase)>=0?".png":meta.IndexOf("image/gif",StringComparison.OrdinalIgnoreCase)>=0?".gif":meta.IndexOf("image/webp",StringComparison.OrdinalIgnoreCase)>=0?".webp":".jpg"; string dir=_store.MediaDirectory; Directory.CreateDirectory(dir); string safe=(prefix+"_"+(key??"").Replace("@","_").Replace("\\","_").Replace("/","_")+"_"+Guid.NewGuid().ToString("N")); string path=Path.Combine(dir,safe+ext); File.WriteAllBytes(path,bytes); return path; } catch(Exception ex){LogWeb("ERROR guardando archivo: "+ex.Message);return "";}
        }

        private decimal ShippingCost(string postalCode)
        {
            string cp = new string((postalCode ?? "").Where(char.IsDigit).ToArray());
            if (cp.Length < 4) return 0m;
            string prefix = cp.Substring(0, 2);
            decimal configured; if (decimal.TryParse(_store.GetSetting("shipping_cost_" + prefix, ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out configured)) return Math.Max(0m, configured);
            if (prefix == "50") return 900m; if (prefix == "55") return 1200m; return 1500m;
        }

        private string ProductDetail(WebUser user, string query)
        {
            long id=ParseLong(QueryValue(query,"id")); Product p=FindProduct(id); if(p==null || !p.Active || !p.OnlineEnabled) return Page("Producto","<div class='error'>Producto no disponible.</div><a class='btn' href='/'>Volver</a>");
            decimal price=p.SalePrice>0?p.SalePrice:p.Price; StringBuilder b=new StringBuilder(); b.Append(Header(p.Name)); b.Append(TopNav(user));
            b.Append("<div class='panel product-detail'><div>"+ProductThumb(p,420)+"</div><div><span class='mini-tag'>"+E(p.Category)+"</span><h1>"+E(p.Name)+"</h1><p class='hero-sub'>"+E(string.IsNullOrWhiteSpace(p.PublicDescription)?p.Description:p.PublicDescription)+"</p><div class='price'>$ "+price.ToString("N2")+"</div><p>Stock disponible: <b>"+p.Stock+"</b></p>");
            if(user!=null && CanBuyAsBuyer(user) && !IsSeller(user) && p.Stock>0) b.Append("<form method='post' action='/order'><input type='hidden' name='productId' value='"+p.Id+"'/><input name='qty' type='number' min='1' max='"+p.Stock+"' value='1'/><button class='btn' type='submit'>🛒 Agregar al carrito</button></form>");
            b.Append("<p style='margin-top:18px'><a class='btn alt' target='_blank' href='https://wa.me/?text="+Uri.EscapeDataString("Mirá este producto de NexoMarket: "+p.Name+" "+_store.GetSetting("web_public_url",LocalUrl)+"/product?id="+p.Id)+"'>Compartir por WhatsApp</a> <button class='btn alt' onclick='navigator.clipboard&&navigator.clipboard.writeText(location.href);return false;'>Copiar enlace</button></p>");
            if(!string.IsNullOrWhiteSpace(p.VideoUrl)) { string videoHref = File.Exists(p.VideoUrl) ? "/media?p=" + Uri.EscapeDataString(p.VideoUrl) : p.VideoUrl; b.Append("<p><a class='btn alt' target='_blank' href='"+E(videoHref)+"'>▶ Ver video del producto</a></p>"); }
            b.Append("</div></div>"); b.Append(Footer()); return b.ToString();
        }

        private string SellerCustomers(WebUser user)
        {
            if(!IsSeller(user)) return SellerDenied(); List<Customer> cs=_store.GetCustomers(""); StringBuilder b=new StringBuilder(); b.Append(Header("Clientes")); b.Append(SellerNav(user,"Clientes"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CRM</span><h1>Clientes</h1><p>Historial y valor de cada comprador para tomar decisiones comerciales.</p></div></div><div class='panel table-panel'><table class='pro-table'><tr><th>Cliente</th><th>Contacto</th><th>Pedidos</th><th>Gastado</th><th>Dirección</th></tr>");
            foreach(Customer c in cs) b.Append("<tr><td><b>"+E(c.Name)+"</b></td><td>"+E(c.Email)+"<small>"+E(c.Phone)+"</small></td><td>"+c.Orders+"</td><td><b>$ "+c.TotalSpent.ToString("N2")+"</b></td><td>"+E(c.Address)+"</td></tr>");
            b.Append("</table></div>"); b.Append(Footer()); return b.ToString();
        }

        private string SellerReputation(WebUser user)
        {
            if(!IsSeller(user)) return SellerDenied(); List<Review> reviews=_store.GetReviews().Where(x=>string.Equals(x.StoreId,StoreId,StringComparison.OrdinalIgnoreCase)).ToList(); double avg=reviews.Count==0?0:reviews.Average(x=>x.Rating);
            StringBuilder b=new StringBuilder(); b.Append(Header("Reputación")); b.Append(SellerNav(user,"Reputación")); b.Append("<div class='seller-head'><div><span class='eyebrow'>CONFIANZA</span><h1>Reputación de la tienda</h1><p>Las calificaciones se habilitan después de una compra entregada.</p></div></div><div class='kpi-grid'>"+Kpi("PUNTAJE",avg.ToString("0.0")+" / 5",reviews.Count+" reseñas","green")+Kpi("5 ESTRELLAS",reviews.Count(x=>x.Rating==5).ToString(),"clientes satisfechos","green")+Kpi("1–2 ESTRELLAS",reviews.Count(x=>x.Rating<=2).ToString(),"casos a revisar",reviews.Any(x=>x.Rating<=2)?"red":"green")+"</div><div class='panel'><h2>Últimas reseñas</h2>");
            foreach(Review r in reviews.Take(30)) b.Append("<div class='insight'><b>"+new string('★',Math.Max(0,Math.Min(5,r.Rating)))+"</b><p>"+E(r.Text)+"</p><small>"+r.CreatedAt.ToString("dd/MM/yyyy HH:mm")+" · "+E(r.CustomerEmail)+"</small></div>");
            if(reviews.Count==0) b.Append("<div class='empty'>Todavía no hay reseñas.</div>"); b.Append("</div>"); b.Append(Footer()); return b.ToString();
        }

        private string BuyerReview(string body, WebUser user)
        {
            if(user==null || user.Role!="buyer") return SellerDenied(); Dictionary<string,string> f=Form(body); long id=ParseLong(f.Get("orderId")); Order o=_store.GetOrders("").FirstOrDefault(x=>x.Id==id && string.Equals(x.CustomerEmail,user.Email,StringComparison.OrdinalIgnoreCase) && x.Status=="Entregado"); if(o==null) return Page("Reseña","<div class='error'>La compra no puede calificarse todavía.</div>");
            if(_store.GetReviews().Any(r=>r.OrderId==o.Id && string.Equals(r.CustomerEmail,user.Email,StringComparison.OrdinalIgnoreCase))) return RedirectPage("/buyer/order?id="+o.Id,"Esta compra ya fue calificada."); int rating; if(!int.TryParse(f.Get("rating"),out rating)) rating=5; rating=Math.Max(1,Math.Min(5,rating)); _store.SaveReview(new Review{OrderId=o.Id,CustomerId=o.CustomerId,CustomerEmail=user.Email,StoreId=StoreId,Rating=rating,Text=f.Get("text"),CreatedAt=DateTime.Now}); return RedirectPage("/buyer/order?id="+o.Id,"Gracias por calificar tu compra.");
        }

        private string MessagesPage(WebUser user)
        {
            if(user==null) return Page("Mensajes","<div class='error'>Ingresá para usar mensajería.</div>"+LoginForm()); List<ChatMessage> ms=_store.GetMessages(user.Email); StringBuilder b=new StringBuilder(); b.Append(Header("Mensajes")); b.Append(TopNav(user)); b.Append("<div class='seller-head'><div><span class='eyebrow'>CHAT</span><h1>Mensajería</h1><p>Canal directo para dudas antes y después de la compra.</p></div></div><div class='panel'><div class='message-list'>");
            foreach(ChatMessage m in ms) b.Append("<div class='insight'><b>"+E(m.FromEmail)+"</b><small>"+m.CreatedAt.ToString("dd/MM HH:mm")+"</small><p>"+E(m.Body)+"</p></div>");
            b.Append("</div><form method='post' action='/messages/send' class='form-grid'><input name='toEmail' placeholder='Correo del destinatario' required/><input name='orderId' placeholder='N.º de pedido (opcional)'/><textarea name='body' placeholder='Escribí tu mensaje' required></textarea><button class='btn' type='submit'>ENVIAR MENSAJE</button></form></div>"); b.Append(Footer()); return b.ToString();
        }

        private string SendMessage(string body, WebUser user)
        {
            if(user==null) return Page("Mensajes","<div class='error'>Ingresá para enviar mensajes.</div>"); Dictionary<string,string> f=Form(body); _store.AddMessage(new ChatMessage{OrderId=ParseLong(f.Get("orderId")),FromEmail=user.Email,ToEmail=f.Get("toEmail"),Body=f.Get("body"),CreatedAt=DateTime.Now}); return RedirectPage("/messages","Mensaje enviado.");
        }

        private string SellerTools(WebUser user)
        {
            if(!IsSeller(user)) return SellerDenied(); List<Product> products=_store.GetProducts(""); StringBuilder csv=new StringBuilder(); csv.Append("SKU;Nombre;Categoria;Marca;Precio;PrecioOferta;Stock;StockMinimo;CodigoBarras;Talle;Color;Publicado\n"); foreach(Product p in products) csv.Append(Csv(p.SKU)+";"+Csv(p.Name)+";"+Csv(p.Category)+";"+Csv(p.Brand)+";"+p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)+";"+p.SalePrice.ToString(System.Globalization.CultureInfo.InvariantCulture)+";"+p.Stock+";"+p.MinimumStock+";"+Csv(p.Barcode)+";"+Csv(p.Size)+";"+Csv(p.Color)+";"+(p.OnlineEnabled?"1":"0")+"\n");
            string data="data:text/csv;charset=utf-8,"+Uri.EscapeDataString(csv.ToString()); StringBuilder b=new StringBuilder(); b.Append(Header("Herramientas avanzadas")); b.Append(SellerNav(user,"Herramientas")); b.Append("<div class='seller-head'><div><span class='eyebrow'>PROFESIONAL</span><h1>Herramientas del vendedor</h1><p>Funciones de alto volumen para operar la tienda desde Windows y Web.</p></div></div><div class='campaign-grid'>"+Campaign("📦","Carga masiva","Importá productos pegando CSV y exportá tu catálogo.","#bulk")+Campaign("🧾","POS y hardware","Escáner, cámara, impresión y operación local disponibles en Windows.","#pos")+Campaign("⚡","Modo offline","El catálogo y las operaciones locales siguen funcionando sin internet y pueden sincronizarse al volver.","#offline")+Campaign("💳","Pagos","Mercado Pago, transferencia, efectivo y futuras pasarelas se administran desde esta capa.","#payments")+Campaign("🛡","KYC","Estado de verificación de vendedor y controles antifraude.","#kyc")+Campaign("🤖","Inteligencia","Alertas de stock y oportunidades de precio/venta.","/seller/analytics")+"</div>");
            b.Append("<div id='bulk' class='panel'><h2>Exportar catálogo</h2><p>Descargá el catálogo actual para Excel/CSV.</p><a class='btn' download='nexomarket_productos.csv' href='"+data+"'>⬇ DESCARGAR CSV</a><hr/><h2>Importación rápida</h2><p>Pegá un CSV separado por punto y coma con la primera fila de encabezados.</p><form method='post' action='/seller/tools/import'><textarea name='csv' rows='12' placeholder='SKU;Nombre;Categoria;Marca;Precio;PrecioOferta;Stock;StockMinimo;CodigoBarras;Talle;Color;Publicado'></textarea><button class='btn' type='submit'>IMPORTAR PRODUCTOS</button></form></div>");
            b.Append("<div id='pos' class='panel'><h2>Operación profesional Windows</h2><p>Lectores de código de barras, cámara para productos, caja y operación local están separados del Seller Center web para evitar que el navegador limite el hardware.</p></div><div id='offline' class='panel'><h2>Offline + sincronización</h2><p>La base local XML permite continuar operando sin conexión. La sincronización central debe habilitarse con el servidor NexoMarket Central y una URL pública estable.</p></div><div id='payments' class='panel'><h2>Pagos seguros</h2><p>La aplicación registra método, referencia, comprobante y estado. Las APIs reales de Mercado Pago/PayPal se conectarán cuando se carguen las credenciales del comercio; no se inventan credenciales ni se simula una aprobación bancaria.</p></div><div id='kyc' class='panel'><h2>Verificación de vendedor</h2><p>Estado actual: <b>"+E(_store.GetSetting("seller_kyc_status","Pendiente"))+"</b>. La verificación documental real queda lista para conectarse al proveedor KYC elegido.</p></div>"); b.Append(Footer()); return b.ToString();
        }

        private string SellerImportCsv(string body, WebUser user)
        {
            if(!IsSeller(user)) return SellerDenied(); string csv=Form(body).Get("csv"); if(string.IsNullOrWhiteSpace(csv)) return RedirectPage("/seller/tools","No se recibió CSV."); string[] lines=csv.Replace("\r","").Split('\n'); int ok=0; for(int i=1;i<lines.Length;i++){string line=lines[i].Trim(); if(line.Length==0) continue; string[] c=line.Split(';'); if(c.Length<12) continue; Product p=new Product(); p.SKU=Unquote(c[0]); p.Name=Unquote(c[1]); p.Category=Unquote(c[2]); p.Brand=Unquote(c[3]); decimal importedPrice; decimal importedSalePrice; int importedStock; int importedMinimumStock; decimal.TryParse(Unquote(c[4]),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out importedPrice); decimal.TryParse(Unquote(c[5]),System.Globalization.NumberStyles.Any,System.Globalization.CultureInfo.InvariantCulture,out importedSalePrice); int.TryParse(Unquote(c[6]),out importedStock); int.TryParse(Unquote(c[7]),out importedMinimumStock); p.Price=importedPrice; p.SalePrice=importedSalePrice; p.Stock=importedStock; p.MinimumStock=importedMinimumStock; p.Barcode=Unquote(c[8]); p.Size=Unquote(c[9]); p.Color=Unquote(c[10]); p.OnlineEnabled=Unquote(c[11])=="1"; p.Active=true; p.Slug=Slugify(p.Name); Product existing=_store.GetProducts("").FirstOrDefault(x=>x.SKU==p.SKU && !string.IsNullOrWhiteSpace(p.SKU)); if(existing!=null)p.Id=existing.Id; _store.SaveProduct(p); ok++; } return RedirectPage("/seller/products","Importación finalizada: "+ok+" productos procesados.");
        }

        private string Csv(string s) { string x=s??""; if(x.IndexOf(';')>=0||x.IndexOf('"')>=0||x.IndexOf('\n')>=0) return "\""+x.Replace("\"","\"\"")+"\""; return x; }
        private string Unquote(string s) { string x=(s??"").Trim(); if(x.StartsWith("\"")&&x.EndsWith("\"")) x=x.Substring(1,x.Length-2).Replace("\"\"","\""); return x; }

        private string SellerAnalytics(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            List<Order> orders = _store.GetOrders("").Where(o => o.Status != "Cancelado").ToList(); List<Product> products = _store.GetProducts("");
            decimal sales = orders.Sum(x => x.Total); int units = products.Sum(x => Math.Max(0, x.Stock)); decimal avg = orders.Count == 0 ? 0 : orders.Average(x => x.Total);
            StringBuilder b = new StringBuilder(); b.Append(Header("Analítica vendedor")); b.Append(SellerNav(user, "Analítica"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>MÉTRICAS</span><h1>Rendimiento del negocio</h1><p>Una lectura ejecutiva inspirada en las mejores consolas de vendedores: ventas, operaciones, clientes y oportunidades.</p></div></div><div class='kpi-grid'>" + Kpi("VENTAS ACUMULADAS", "$ " + sales.ToString("N2"), "operaciones válidas", "green") + Kpi("OPERACIONES", orders.Count.ToString(), "ticket medio $ " + avg.ToString("N0"), "green") + Kpi("STOCK ACTUAL", units.ToString(), "unidades registradas", "yellow") + Kpi("PUBLICADOS", products.Count(x => x.Active && x.OnlineEnabled).ToString(), "en la tienda web", "green") + "</div><div class='panel'><div class='panel-title'><h2>Qué mirar cada día</h2></div><div class='insight-grid'>" + Insight("Conversión", "Cuando tengamos tráfico web central, compararemos visitas → intención → pedido.") + Insight("Productos", "Identificá publicaciones con mucho stock y pocas ventas para ajustar precio, fotos o promoción.") + Insight("Operación", "Los pedidos demorados impactan la experiencia; mantené los estados actualizados.") + Insight("Clientes", "Separaremos compradores nuevos, recurrentes y tasa de recompra en la API central.") + "</div></div>");
            b.Append(Footer()); return b.ToString();
        }

        private string SellerFinance(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            List<Order> orders = _store.GetOrders("").Where(o => o.Status != "Cancelado").ToList();
            decimal total = orders.Sum(x => x.Total);
            decimal opening = ParseMoneySetting(_store.GetSetting("cash_opening","0"));
            decimal openingMp = ParseMoneySetting(_store.GetSetting("cash_opening_mercadopago","0"));
            decimal retention = ParseMoneySetting(_store.GetSetting("cash_mercadopago_retention","0"));
            decimal cash = orders.Where(x => x.PaymentMethod == "Efectivo").Sum(x => x.Total);
            decimal mp = orders.Where(x => x.PaymentMethod == "Mercado Pago").Sum(x => x.Total);
            bool open = string.Equals(_store.GetSetting("cash_status","Cerrada"), "Abierta", StringComparison.OrdinalIgnoreCase);
            decimal expectedCash = opening + cash;
            decimal expectedMp = openingMp + mp - retention;
            var groups = orders.GroupBy(x => x.PaymentMethod ?? "Pendiente").OrderByDescending(g => g.Sum(x => x.Total));
            StringBuilder b = new StringBuilder(); b.Append(Header("Finanzas vendedor")); b.Append(SellerNav(user, "Finanzas"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CAJA + FINANZAS</span><h1>Ingresos y medios de cobro</h1><p>Apertura y cierre de caja, Mercado Pago y retenciones quedan registrados en el mismo comercio.</p></div></div>");
            b.Append("<div class='kpi-grid'>" + Kpi("ESTADO CAJA", open ? "ABIERTA" : "CERRADA", open ? "operativa" : "requiere apertura", open ? "green" : "yellow") + Kpi("EFECTIVO ESPERADO", "$ " + expectedCash.ToString("N2"), "apertura + ventas", "green") + Kpi("MERCADO PAGO DISPONIBLE", "$ " + expectedMp.ToString("N2"), "descontando retención", "green") + Kpi("TOTAL VENDIDO", "$ " + total.ToString("N2"), "antes de costos", "green") + "</div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>Apertura de caja</h2></div><form method='post' action='/seller/cash/open' class='form-grid'><input name='cashOpening' type='number' step='0.01' min='0' value='" + opening.ToString(CultureInfo.InvariantCulture) + "' placeholder='Efectivo inicial'/><input name='mpOpening' type='number' step='0.01' min='0' value='" + openingMp.ToString(CultureInfo.InvariantCulture) + "' placeholder='Mercado Pago inicial'/><input name='retention' type='number' step='0.01' min='0' value='" + retention.ToString(CultureInfo.InvariantCulture) + "' placeholder='Retención Mercado Pago'/><button class='btn' type='submit' " + (open ? "disabled" : "") + ">ABRIR CAJA</button></form><p class='user'>La retención de Mercado Pago se descuenta del saldo disponible esperado, sin modificar las ventas registradas.</p></div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>Cierre de caja</h2></div><form method='post' action='/seller/cash/close' class='form-grid'><input name='actualCash' type='number' step='0.01' min='0' value='" + expectedCash.ToString(CultureInfo.InvariantCulture) + "' placeholder='Efectivo contado'/><input name='actualMp' type='number' step='0.01' min='0' value='" + expectedMp.ToString(CultureInfo.InvariantCulture) + "' placeholder='Mercado Pago disponible'/><button class='btn alt' type='submit' " + (!open ? "disabled" : "") + ">CERRAR CAJA</button></form></div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>Distribución de cobros</h2></div><table class='pro-table simple'><tr><th>Medio</th><th>Operaciones</th><th>Total</th></tr>");
            foreach (var g in groups) b.Append("<tr><td>" + E(g.Key) + "</td><td>" + g.Count() + "</td><td><b>$ " + g.Sum(x => x.Total).ToString("N2") + "</b></td></tr>");
            b.Append("</table></div>"); b.Append(Footer()); return b.ToString();
        }

        private decimal ParseMoneySetting(string value)
        {
            decimal x; return decimal.TryParse((value ?? "0").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out x) ? x : 0m;
        }

        private string SellerCashOpen(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            if (string.Equals(_store.GetSetting("cash_status","Cerrada"), "Abierta", StringComparison.OrdinalIgnoreCase))
                return RedirectPage("/seller/finance", "La caja ya está abierta.");
            Dictionary<string,string> f = Form(body);
            decimal a = ParseMoneySetting(f.Get("cashOpening")), m = ParseMoneySetting(f.Get("mpOpening")), r = ParseMoneySetting(f.Get("retention"));
            _store.SetSetting("cash_opening", a.ToString(CultureInfo.InvariantCulture));
            _store.SetSetting("cash_opening_mercadopago", m.ToString(CultureInfo.InvariantCulture));
            _store.SetSetting("cash_mercadopago_retention", r.ToString(CultureInfo.InvariantCulture));
            _store.SetSetting("cash_status", "Abierta"); _store.SetSetting("cash_opened_at", DateTime.Now.ToString("o"));
            return RedirectPage("/seller/finance", "Caja abierta correctamente.");
        }

        private string SellerCashClose(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            if (!string.Equals(_store.GetSetting("cash_status","Cerrada"), "Abierta", StringComparison.OrdinalIgnoreCase))
                return RedirectPage("/seller/finance", "La caja ya está cerrada.");
            Dictionary<string,string> f = Form(body);
            decimal a = ParseMoneySetting(f.Get("actualCash")), m = ParseMoneySetting(f.Get("actualMp"));
            _store.SetSetting("cash_close_actual", a.ToString(CultureInfo.InvariantCulture));
            _store.SetSetting("cash_close_mercadopago", m.ToString(CultureInfo.InvariantCulture));
            _store.SetSetting("cash_closed_at", DateTime.Now.ToString("o")); _store.SetSetting("cash_status", "Cerrada");
            return RedirectPage("/seller/finance", "Caja cerrada correctamente.");
        }

        private string SellerMarketing(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            List<Promotion> promos = _store.GetPromotions();
            List<Coupon> coupons = _store.GetCoupons();
            StringBuilder b = new StringBuilder(); b.Append(Header("Marketing vendedor")); b.Append(SellerNav(user, "Marketing"));
            b.Append("<div class='seller-head'><div><span class='eyebrow'>CRECIMIENTO</span><h1>Marketing y promociones</h1><p>Promociones visibles en la tienda web y generador de cupones para descuentos.</p></div></div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>Generador de cupones</h2></div><form method='post' action='/seller/coupon/save' class='form-grid'><input name='code' placeholder='Código, ejemplo VERANO10' required/><input name='description' placeholder='Descripción'/><input name='percent' type='number' step='0.01' min='0' max='100' placeholder='% de descuento'/><input name='amount' type='number' step='0.01' min='0' placeholder='$ descuento fijo'/><input name='maxUses' type='number' min='0' placeholder='Usos máximos (0 = sin límite)'/><button class='btn' type='submit'>GENERAR CUPÓN</button></form></div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>Promociones visibles en la web</h2></div><table class='pro-table simple'><tr><th>Promoción</th><th>Precio</th><th>Estado</th><th>Vigencia</th></tr>");
            foreach (Promotion p in promos) b.Append("<tr><td><b>" + E(p.Name) + "</b></td><td>$ " + p.PromotionalPrice.ToString("N2") + "</td><td>" + (p.Active ? StatusBadge("Activa") : StatusBadge("Pausada")) + "</td><td>" + p.From.ToString("dd/MM/yyyy") + " → " + p.To.ToString("dd/MM/yyyy") + "</td></tr>");
            b.Append("</table></div>");
            b.Append("<div class='panel'><div class='panel-title'><h2>Cupones creados</h2></div><table class='pro-table simple'><tr><th>Código</th><th>Descuento</th><th>Usos</th><th>Estado</th><th>Vigencia</th></tr>");
            foreach (Coupon c in coupons) b.Append("<tr><td><b>" + E(c.Code) + "</b><small>" + E(c.Description) + "</small></td><td>" + (c.DiscountPercent > 0 ? c.DiscountPercent.ToString("0.##") + "%" : "$ " + c.DiscountAmount.ToString("N2")) + "</td><td>" + c.Used + " / " + (c.MaxUses == 0 ? "∞" : c.MaxUses.ToString()) + "</td><td>" + (c.Active ? StatusBadge("Activo") : StatusBadge("Pausado")) + "</td><td>" + c.From.ToString("dd/MM/yyyy") + " → " + c.To.ToString("dd/MM/yyyy") + "</td></tr>");
            b.Append("</table></div>");
            b.Append(Footer()); return b.ToString();
        }

        private string SellerCouponSave(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            Dictionary<string,string> f = Form(body);
            string code = (f.Get("code") ?? "").Trim().ToUpperInvariant();
            decimal percent = 0m, amount = 0m; int maxUses = 0;
            decimal.TryParse(f.Get("percent").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out percent);
            decimal.TryParse(f.Get("amount").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
            int.TryParse(f.Get("maxUses"), out maxUses);
            if (string.IsNullOrWhiteSpace(code) || (percent <= 0 && amount <= 0) || (percent > 100) || (percent > 0 && amount > 0))
                return RedirectPage("/seller/marketing", "Ingresá un descuento válido: porcentaje o importe fijo.");
            Coupon existing = _store.GetCoupons().FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return RedirectPage("/seller/marketing", "Ese cupón ya existe.");
            _store.SaveCoupon(new Coupon { Code=code, Description=f.Get("description"), DiscountPercent=percent, DiscountAmount=amount, MaxUses=Math.Max(0,maxUses), Active=true, From=DateTime.Today, To=DateTime.Today.AddDays(30) });
            return RedirectPage("/seller/marketing", "Cupón generado correctamente.");
        }

        private bool CanBuyAsBuyer(WebUser user) { return user != null && (user.Role == "buyer" || IsSeller(user)); }

        private bool IsSeller(WebUser user) { return user != null && user.Role == "seller" && string.Equals(user.StoreId ?? "", StoreId, StringComparison.OrdinalIgnoreCase) && string.Equals((user.Email ?? "").Trim(), _store.GetSetting("seller_account_email", "").Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_store.GetSetting("seller_account_email", "")); }
        private string SellerDenied() { return Page("Vendedor", "<div class='error'><b>Cuenta de vendedor no vinculada.</b><p>Asociá en Configuración → Tienda web el mismo correo electrónico con el que registraste la cuenta.</p></div><a class='btn' href='/'>Inicio</a>"); }

        private string SellerNav(WebUser user, string active)
        {
            string[] names = { "Resumen", "Pedidos", "Productos", "Analítica", "Finanzas", "Marketing", "Clientes", "Reputación", "Mensajes", "Herramientas", "Contraseña" };
            string[] urls = { "/seller", "/seller/orders", "/seller/products", "/seller/analytics", "/seller/finance", "/seller/marketing", "/seller/customers", "/seller/reputation", "/messages", "/seller/tools", "/seller/password" };
            StringBuilder b = new StringBuilder(); b.Append("<nav class='seller-nav'><div class='brand'><span class='nexo'>NEXO</span><span class='market'>MARKET</span><small>SELLER CENTER</small></div><div class='seller-links'>");
            for (int i = 0; i < names.Length; i++) b.Append("<a class='" + (active == names[i] ? "active" : "") + "' href='" + urls[i] + "'>" + names[i] + "</a>");
            b.Append("</div><div class='account'>" + E(user.Name) + " · <a href='/logout'>Salir</a></div></nav>"); return b.ToString();
        }

        private string TopNav(WebUser user)
        {
            StringBuilder b = new StringBuilder(); b.Append("<nav class='topnav'><a class='brand' href='/'><span class='nexo'>NEXO</span><span class='market'>MARKET</span></a><div><a href='/'>Tiendas</a>");
            if (user != null && user.Role == "seller") { b.Append("<a href='/seller'>Mi consola</a>"); b.Append("<a href='/buyer'>👤 Modo comprador</a>"); b.Append("<a href='/'>🛍 Otras tiendas</a>"); }
            if (user != null && user.Role == "buyer")
            {
                if (!IsGuestUser(user)) b.Append("<a href='/buyer'>Mi cuenta</a>");
                b.Append("<a href='/cart'>🛒 Carrito</a>");
                if (!IsGuestUser(user)) b.Append("<a href='/messages'>Mensajes</a>");
            }
            if (user != null) b.Append("<a href='/logout'>Salir</a>"); else b.Append("<a href='/login'>Ingresar</a><a href='/register'>Crear cuenta</a>");
            b.Append("</div></nav>"); return b.ToString();
        }

        private string StoreCard(RemoteStore s, string href)
        {
            string service = s.Delivery ? "🚚 Delivery" : (s.Pickup ? "🏪 Retiro" : "Disponible");
            string cover = !string.IsNullOrWhiteSpace(s.StorePhoto) ? "<img class='store-cover' src='" + E(s.StorePhoto) + "' alt='' loading='lazy'/>" : "<div class='store-cover store-cover-fallback'><span>NEXO</span></div>";
            string logo = !string.IsNullOrWhiteSpace(s.Logo) ? "<img src='" + E(s.Logo) + "' alt='' loading='lazy'/>" : "<span class='logo-letter'>N</span>";
            string tier = s.FeaturedPlus ? "<span class='store-tier plus'>✦ DESTACADA PLUS</span>" : (s.Featured ? "<span class='store-tier featured-common'>★ TIENDA DESTACADA</span>" : "");
            string rating = "";
            string[] rp = (s.RatingSummary ?? "").Split('|');
            if (rp.Length > 0 && !string.IsNullOrWhiteSpace(rp[0]) && rp[0] != "0.0") rating = "<span>★ " + E(rp[0]) + "</span>";
            string location = s.City.Length > 0 ? E(s.City) : "Ubicación disponible";
            string description = string.IsNullOrWhiteSpace(s.Description) ? "Descubrí este local y sus productos." : E(s.Description);
            string distance = s.DistanceKm > 0 ? " · " + s.DistanceKm.ToString("0.0") + " km" : "";
            return "<a class='market-store-card' href='" + E(href ?? "/store") + "'><div class='store-visual'>" + cover + "<div class='store-vignette'></div><div class='store-logo-float'>" + logo + "</div>" + tier + "<div class='store-shine'></div></div><div class='store-card-content'><div class='store-card-top'><span class='store-category'>" + E(s.Category.Length == 0 ? "Comercio" : s.Category) + "</span><span class='store-open-badge " + (s.Active ? "is-open" : "is-closed") + "'>● " + (s.Active ? "Abierto" : "Cerrado") + "</span></div><h3>" + E(s.Name) + "</h3><p class='store-desc'>" + description + "</p><div class='store-facts'><span>📍 " + location + distance + "</span><span>" + service + "</span>" + rating + "</div><div class='store-card-cta'>Ver local <b>→</b></div></div></a>";
        }

        private RemoteStore LocalRemoteStore()
        {
            return new RemoteStore { StoreId = StoreId, Name = _store.GetSetting("store_name", "NexoMarket"), PublicUrl = _store.GetSetting("web_public_url", LocalUrl), City = _store.GetSetting("store_city", ""), Province = _store.GetSetting("store_province", ""), Category = _store.GetSetting("store_category", "Comercio"), Logo = _store.GetSetting("store_logo", ""), StorePhoto = _store.GetSetting("store_photo", _store.GetSetting("store_cover", "")), Description = _store.GetSetting("store_description", ""), Featured = _store.GetSetting("store_featured", "0") == "1", FeaturedPlus = _store.GetSetting("store_featured_plus", "0") == "1", Active = _store.GetSetting("store_web_active", "0") == "1", Delivery = _store.GetSetting("delivery_enabled", "1") == "1", Pickup = _store.GetSetting("pickup_enabled", "1") == "1" };
        }

        private Product FindProduct(long id) { return _store.GetProducts("").FirstOrDefault(p => p.Id == id); }

        private string ProductThumb(Product p, int size)
        {
            if (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath)) return "<img style='width:" + size + "px;height:" + size + "px;object-fit:cover;border-radius:14px' src='/media?p=" + Uri.EscapeDataString(p.ImagePath) + "' alt='" + E(p.Name) + "'/>";
            return "<div class='thumb-placeholder' style='width:" + size + "px;height:" + size + "px'>N</div>";
        }

        private string OrderList(List<Order> orders)
        {
            StringBuilder b = new StringBuilder(); b.Append("<div class='order-list'>");
            foreach (Order o in orders) b.Append("<div class='order-line'><div><b>#" + o.Id + "</b><small>" + E(o.CustomerName) + " · " + o.CreatedAt.ToString("dd/MM HH:mm") + "</small></div><div>" + StatusBadge(o.Status) + "</div><strong>$ " + o.Total.ToString("N0") + "</strong></div>");
            if (orders.Count == 0) b.Append("<div class='empty-inline'>No hay pedidos todavía.</div>"); b.Append("</div>"); return b.ToString();
        }

        private string StatusBadge(string status)
        {
            string cls = StatusClass(status); return "<span class='badge " + cls + "'>" + E(status) + "</span>";
        }
        private string StatusClass(string status)
        {
            if (status == "Rechazado" || status == "Cancelado") return "red";
            if (status == "Pendiente" || status == "Preparando") return "yellow";
            return "green";
        }
        private int StatusPercent(string status)
        {
            if (status == "Pendiente") return 20; if (status == "Preparando") return 40; if (status == "Listo") return 65; if (status == "Enviado") return 82; if (status == "Entregado") return 100; return 12;
        }
        private string Kpi(string title, string value, string hint, string cls) { return "<div class='kpi " + cls + "'><span>" + title + "</span><strong>" + value + "</strong><small>" + hint + "</small></div>"; }
        private string Todo(string title, int count, string href, string cls) { return "<a class='todo " + cls + "' href='" + href + "'><span>" + title + "</span><b>" + count + "</b><small>Revisar →</small></a>"; }
        private string Insight(string title, string text) { return "<div class='insight'><b>" + title + "</b><p>" + text + "</p></div>"; }
        private string Campaign(string icon, string title, string text, string href) { return "<a class='campaign' href='" + href + "'><div class='campaign-icon'>" + icon + "</div><h3>" + title + "</h3><p>" + text + "</p><span>Configurar →</span></a>"; }

        private string Header(string title)
        {
            return "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>" + E(title) + " · NexoMarket</title><meta name='description' content='Marketplace NexoMarket · compra y venta local'><meta property='og:title' content='" + E(title) + " · NexoMarket'><meta property='og:description' content='Encontrá productos y tiendas en NexoMarket'><style>" + Css() + "</style></head><body><div class='wrap'>";
        }

        private string Css()
        {
            return "*{box-sizing:border-box}body{font-family:'Segoe UI',Arial,sans-serif;background:#080b10;color:#f5f7fa;margin:0}.wrap{max-width:1280px;margin:auto;padding:18px 24px 50px}.topnav,.seller-nav{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:12px 4px;margin-bottom:18px}.topnav a,.seller-nav a{color:#aeb9c7;text-decoration:none;margin-left:18px}.topnav a:hover,.seller-nav a:hover,.seller-nav a.active{color:#39ff66}.brand{display:flex!important;align-items:center;gap:7px;margin-left:0!important}.brand small{font-size:9px;color:#6f7d8e;letter-spacing:1.5px;margin-left:8px}.nexo{color:#39ff66;font-weight:900;letter-spacing:-1px}.market{color:#fff;font-weight:800}.topnav .nexo{font-size:25px}.topnav .market{font-size:22px}.hero-market{display:grid;grid-template-columns:1.4fr .8fr;gap:18px}.hero,.store-hero{padding:28px;border:1px solid #223044;background:linear-gradient(135deg,#111823,#0d141d);border-radius:22px;margin-bottom:24px}.hero .nexo{font-size:48px}.hero .market{font-size:42px;margin-left:7px}.hero-sub{color:#9aa8b8;margin-top:10px;font-size:16px}.location{background:#0a1119;border:1px solid #26384e;border-radius:16px;padding:20px;display:flex;align-items:center}.section-head,.seller-head{display:flex;justify-content:space-between;align-items:flex-end;gap:20px;margin:22px 0}.section-head h2,.seller-head h1{margin:0 0 5px}.section-head p,.seller-head p{margin:0;color:#7f8c9d}.store-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:16px}.store-card{display:flex;align-items:center;gap:14px;position:relative;background:#101720;border:1px solid #26384e;border-radius:18px;padding:16px;text-decoration:none;color:#fff;min-height:130px;transition:.15s}.store-card:hover{transform:translateY(-2px);border-color:#39ff66}.store-logo{width:64px;height:64px;border-radius:16px;background:#07100a;border:1px solid #2c6740;display:flex;align-items:center;justify-content:center;font-size:32px}.store-name{font-size:20px;font-weight:800}.store-meta{color:#8795a5;font-size:13px;margin-top:4px}.store-open{color:#39ff66;font-size:12px;margin-top:8px}.arrow{margin-left:auto;color:#39ff66;font-size:24px}.btn{display:inline-block;background:#39ff66;color:#06100a;padding:10px 15px;border:0;border-radius:10px;text-decoration:none;font-weight:800;cursor:pointer}.btn.alt{background:#fff;color:#10151c}.btn.small{padding:7px 10px;font-size:11px}.auth-grid{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-top:35px}.auth-grid>div,.panel,.trust>div,.empty{background:#0f161f;border:1px solid #223044;border-radius:18px;padding:20px}.auth-grid input,.auth-grid select{display:block;width:100%;margin:7px 0}.trust{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin:26px 0}.trust div{display:flex;flex-direction:column;gap:5px}.trust span{color:#7f8c9d;font-size:13px}.empty{text-align:center;color:#a8b2bf}.store-hero{padding:0;overflow:hidden}.cover{height:170px;background-size:cover;background-position:center;opacity:.65}.store-title{padding:24px}.store-title .nexo{font-size:30px}.store-title .market{font-size:27px;margin-left:5px}.store-title h1{margin:12px 0 5px}.store-title p{color:#8f9cac}.pill,.mini-tag,.badge{display:inline-block;padding:5px 9px;border-radius:999px;background:#17212d;color:#9ba8b7;font-size:11px}.pill.green,.badge.green{color:#39ff66;background:#0d2919}.badge.yellow{color:#ffd34d;background:#332a0e}.badge.red{color:#ff6572;background:#34151b}.chips{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:18px}.chips a{padding:7px 11px;border:1px solid #26384e;border-radius:999px;color:#b5c0cd;text-decoration:none}.product-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:16px}.product-card{background:#0f161f;border:1px solid #223044;border-radius:18px;overflow:hidden}.product-image,.seller-photo{width:100%;height:220px;object-fit:cover;background:#141f2c}.placeholder{display:flex;align-items:center;justify-content:center;color:#39ff66;font-weight:900;font-size:42px}.product-body{padding:15px}.product-body h3{margin:9px 0 4px;font-size:17px}.product-body small,.order-line small,.pro-table small{display:block;color:#718092;margin-top:4px}.price{font-size:23px;font-weight:900;margin:13px 0}.stock{font-size:12px;color:#39ff66}.buy-row{display:flex;gap:8px;margin-top:12px}.buy-row input,select,input{background:#0a1119;color:#fff;border:1px solid #304157;border-radius:9px;padding:9px}.buy-row input{width:70px}.seller-nav{border-bottom:1px solid #1d2a39;padding:8px 0 15px}.seller-links{display:flex;flex-wrap:wrap;gap:4px}.seller-links a{margin-left:4px;padding:7px 10px;border-radius:9px}.seller-links a.active{background:#102718}.account{color:#8c99a8;font-size:12px}.seller-head h1{font-size:31px}.eyebrow{color:#39ff66;font-size:10px;font-weight:900;letter-spacing:2px}.kpi-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:20px 0}.kpi{padding:20px;border:1px solid #26384e;border-radius:18px;background:#0f161f;position:relative;overflow:hidden}.kpi:after{content:'';position:absolute;right:-25px;top:-25px;width:90px;height:90px;border-radius:50%;background:#1b2b3d;opacity:.35}.kpi span,.kpi small{display:block;color:#7e8c9e;font-size:11px}.kpi strong{display:block;font-size:28px;margin:8px 0}.kpi.green{border-top:2px solid #39ff66}.kpi.yellow{border-top:2px solid #ffd34d}.kpi.red{border-top:2px solid #ff6572}.dash-grid{display:grid;grid-template-columns:1.3fr .9fr;gap:16px;margin:16px 0}.panel-title{display:flex;align-items:center;justify-content:space-between;gap:12px}.panel-title h2{font-size:17px;margin:0}.panel-title a{color:#39ff66;text-decoration:none;font-size:12px}.bar-chart{height:170px;display:flex;align-items:flex-end;justify-content:space-around;padding-top:15px}.bar-col{height:150px;display:flex;flex-direction:column;justify-content:flex-end;align-items:center;gap:7px}.bar{width:26px;border-radius:7px 7px 2px 2px;background:#39ff66;opacity:.75;min-height:5px}.bar-col small{color:#667486;font-size:10px}.todo-list{display:grid;gap:10px;margin-top:14px}.todo{display:grid;grid-template-columns:1fr auto;gap:4px;padding:13px;border:1px solid #25364a;border-radius:12px;text-decoration:none;color:#fff}.todo b{grid-row:span 2;font-size:23px}.todo small{color:#7e8c9d}.todo.green b{color:#39ff66}.todo.yellow b{color:#ffd34d}.todo.red b{color:#ff6572}.mini-products{display:grid;gap:8px;margin-top:14px}.mini-product{display:flex;align-items:center;gap:12px;padding:9px;border-bottom:1px solid #1d2a38}.mini-product>div:nth-child(2){flex:1}.mini-product small{display:block;color:#718092;margin-top:3px}.mini-product strong{font-size:13px}.thumb-placeholder{display:flex;align-items:center;justify-content:center;background:#152131;color:#39ff66;border-radius:14px;font-weight:900}.order-list{margin-top:12px}.order-line{display:grid;grid-template-columns:1fr auto auto;gap:10px;align-items:center;padding:12px 0;border-bottom:1px solid #1d2a38}.table-panel{overflow:auto}.filters{display:flex;gap:8px;margin-bottom:12px}.filter{padding:7px 10px;background:#131d29;border-radius:8px;color:#8f9cac;font-size:11px}.filter.active{color:#39ff66}.pro-table{width:100%;border-collapse:collapse;min-width:850px}.pro-table th,.pro-table td{padding:13px 11px;border-bottom:1px solid #202e3e;text-align:left;vertical-align:middle}.pro-table th{font-size:10px;color:#728195;text-transform:uppercase;letter-spacing:1px}.progress{height:5px;background:#1a2634;border-radius:999px;margin-top:7px;overflow:hidden}.progress span{display:block;height:100%;border-radius:999px}.progress .green{background:#39ff66}.progress .yellow{background:#ffd34d}.progress .red{background:#ff6572}.seller-products .product-card{min-width:0}.seller-photo{height:220px;display:flex;align-items:center;justify-content:center}.seller-photo img{max-width:100%;max-height:100%;object-fit:cover}.stock-row{display:flex;justify-content:space-between;color:#8a97a7;font-size:12px}.campaign-grid{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:20px 0}.campaign{background:#0f161f;border:1px solid #26384e;border-radius:18px;padding:20px;color:#fff;text-decoration:none}.campaign:hover{border-color:#39ff66}.campaign-icon{font-size:30px}.campaign h3{margin:12px 0 6px}.campaign p{color:#7f8c9d;min-height:44px}.campaign span{color:#39ff66;font-size:12px}.insight-grid{display:grid;grid-template-columns:repeat(2,1fr);gap:12px;margin-top:15px}.insight{padding:15px;background:#111b26;border:1px solid #223044;border-radius:13px}.insight p{color:#8390a0;line-height:1.5}.simple{min-width:0}.simple th,.simple td{padding:12px}.ok{background:#0d2919;border:1px solid #215b35;padding:14px;border-radius:12px}.error{background:#34151b;border:1px solid #6b2630;padding:14px;border-radius:12px}.user{color:#8f9cac}.user a,a{color:#39ff66}.onboard{min-height:72vh;display:flex;align-items:center;justify-content:center;padding:30px 0}.onboard-card{max-width:780px;width:100%;padding:42px;border:1px solid #26384e;background:linear-gradient(145deg,#101923,#0b1017);border-radius:26px;box-shadow:0 25px 70px rgba(0,0,0,.35)}.onboard-brand{margin-bottom:25px}.onboard h1{font-size:38px;line-height:1.08;margin:10px 0}.onboard p{color:#8996a7;line-height:1.65;max-width:690px}.role-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px;margin:25px 0}.role-choice{border:1px solid #293a4f;border-radius:16px;padding:18px;background:#0d151e;cursor:pointer}.role-choice:has(input:checked){border-color:#39ff66;box-shadow:0 0 0 1px #39ff66 inset}.role-choice input{margin-right:8px}.role-choice b,.role-choice small{display:block}.role-choice small{color:#788698;margin-top:7px;line-height:1.4}.location-form{display:flex;gap:10px;align-items:center;background:#0a1118;border:1px solid #2a3b50;border-radius:14px;padding:8px}.location-form span{font-size:20px}.location-form input{flex:1;background:transparent!important;border:0!important;margin:0!important;outline:0}.geo-btn{background:transparent;border:0;color:#39ff66;padding:12px 0;cursor:pointer}.geo-msg{color:#8d9aaa;font-size:12px}.location-box{border:1px solid #26384e;background:#0c141d;border-radius:16px;padding:18px;min-width:260px}.location-box strong{display:block;font-size:18px;margin:5px 0 12px}.location-box form{display:flex;gap:6px}.location-box input{min-width:0}.near-note{color:#7e8c9c;font-size:12px}.footer{margin-top:45px;color:#5f6d7d;border-top:1px solid #1d2937;padding-top:18px;font-size:11px}.form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.form-grid input{width:100%;box-sizing:border-box}.panel textarea{width:100%;min-height:100px;box-sizing:border-box;background:#0a1119;color:#fff;border:1px solid #304157;border-radius:9px;padding:10px;margin:8px 0;resize:vertical}.upload-box{display:block;border:1px dashed #3a5068;background:#0b141e;padding:16px;border-radius:12px;margin:12px 0;color:#9eabba}.upload-box input[type=file]{display:block;margin-top:10px}.checkout-grid{display:grid;grid-template-columns:1.35fr .65fr;gap:16px}.subpanel{background:#0b121a}.media-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}.media-actions input[type=file]{display:none}.proof-preview{margin-top:10px;max-width:320px}.proof-preview img{max-width:100%;max-height:220px;border-radius:12px;border:1px solid #304157}.camera-box{margin-top:12px;padding:12px;border:1px solid #304157;border-radius:12px;background:#081018}.camera-box video{width:100%;max-height:280px;background:#000;border-radius:10px;object-fit:contain}.proof-box{margin-top:14px;padding:12px;border:1px solid #26384e;border-radius:14px;background:#0a1119}.proof-full{max-width:100%;max-height:360px;border-radius:10px;object-fit:contain}.requested-items{display:grid;gap:8px;margin:12px 0}.requested-item{display:flex;align-items:center;gap:12px;padding:10px;border:1px solid #223044;border-radius:12px;background:#0b121a}.requested-item small{display:block;color:#7e8c9d;margin-top:4px}.negotiation{padding:12px;border-radius:12px;margin:10px 0;border:1px solid #3b4b5f}.seller-note{background:#161b12;border-color:#4a5a27}.buyer-note{background:#111a24}.media-link-row{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-top:10px}.checkout-grid .panel{min-width:0}.checkout-grid textarea{min-height:100px}.checkout-grid .btn{margin-top:6px}.cart-total{display:flex;align-items:center;justify-content:space-between;margin:20px 0;padding:18px;border:1px solid #26384e;border-radius:14px;background:#0d151e}.cart-total strong{font-size:30px}.danger{border-color:#6b2630!important;color:#ff6572!important;background:#241118!important}@media(max-width:900px){.hero-market,.dash-grid,.auth-grid,.checkout-grid{grid-template-columns:1fr}.form-grid{grid-template-columns:1fr}.kpi-grid{grid-template-columns:repeat(2,1fr)}.campaign-grid{grid-template-columns:repeat(2,1fr)}.trust{grid-template-columns:1fr}.seller-nav{align-items:flex-start;flex-direction:column}.seller-links{width:100%}.seller-links a{margin:0}.account{align-self:flex-end}}@media(max-width:560px){.wrap{padding:12px}.kpi-grid,.campaign-grid{grid-template-columns:1fr}.section-head,.seller-head{align-items:flex-start;flex-direction:column}.store-grid,.product-grid{grid-template-columns:1fr}.topnav{align-items:flex-start}.topnav>div{display:flex;flex-wrap:wrap;gap:8px}.topnav a{margin-left:8px}.hero .nexo{font-size:38px}.hero .market{font-size:33px}.seller-head h1{font-size:25px}}body{background:#05070a!important;position:relative;overflow-x:hidden}.wrap{position:relative;z-index:1}.wrap:before{content:'';position:fixed;inset:-15%;pointer-events:none;z-index:-1;background:radial-gradient(ellipse at 6% 14%,transparent 0 24%,rgba(57,255,102,.08) 24.1%,transparent 24.6%),radial-gradient(ellipse at 94% 28%,transparent 0 20%,rgba(54,164,255,.09) 20.1%,transparent 20.6%),radial-gradient(ellipse at 72% 88%,transparent 0 22%,rgba(167,103,255,.09) 22.1%,transparent 22.6%),linear-gradient(116deg,transparent 0 38%,rgba(255,255,255,.022) 38.1%,transparent 38.3%,transparent 62%,rgba(57,255,102,.02) 62.1%,transparent 62.3%);transform:rotate(-5deg);animation:localNeon 18s ease-in-out infinite alternate}@keyframes localNeon{from{transform:translateX(-1%) rotate(-5deg)}to{transform:translateX(1%) rotate(-2deg)}}.wrap:after{content:'';position:fixed;width:88vw;height:45vh;right:-28vw;bottom:7vh;border:1px solid rgba(255,255,255,.08);border-left-color:rgba(57,255,102,.18);border-radius:50%;transform:rotate(-16deg);pointer-events:none;z-index:-1;box-shadow:0 0 80px rgba(57,255,102,.05),0 0 120px rgba(167,103,255,.04)}.panel,.card,.kpi,.campaign,.store-card,.onboard-card,.sc-side,.welcome{background:linear-gradient(145deg,rgba(9,16,24,.76),rgba(6,10,16,.68))!important;border-color:rgba(126,184,255,.16)!important;box-shadow:0 16px 42px rgba(0,0,0,.28),inset 0 1px 0 rgba(255,255,255,.04)!important;backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px)}.panel,.card,.kpi,.campaign,.store-card{position:relative;overflow:hidden}.panel:before,.card:before,.kpi:before,.campaign:before,.store-card:before{content:'';position:absolute;left:7%;right:7%;top:0;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.34),rgba(54,164,255,.25),rgba(57,255,102,.20),transparent);pointer-events:none}.card:hover,.campaign:hover,.store-card:hover{transform:translateY(-3px);border-color:rgba(255,255,255,.28)!important;box-shadow:0 0 24px rgba(54,164,255,.07),0 18px 42px rgba(0,0,0,.35)!important}.btn{transition:all .2s ease!important;box-shadow:0 0 18px rgba(57,255,102,.08)}.btn:hover{transform:translateY(-2px)!important;box-shadow:0 0 18px rgba(255,255,255,.42),0 0 34px rgba(57,255,102,.10)!important}.seller-nav{position:relative}.seller-nav:after{content:'';position:absolute;left:0;right:0;bottom:4px;height:1px;background:linear-gradient(90deg,transparent,rgba(255,255,255,.18),rgba(57,255,102,.25),rgba(167,103,255,.20),transparent);pointer-events:none}.seller-links a.active,.seller-links a:hover{background:linear-gradient(90deg,rgba(57,255,102,.08),rgba(167,103,255,.10));border-color:rgba(255,255,255,.12);box-shadow:0 0 16px rgba(57,255,102,.05)}.inventory-card{background:linear-gradient(145deg,rgba(8,14,21,.82),rgba(4,7,12,.72))!important;border-color:rgba(126,184,255,.13)!important}.inventory-photo,.media-preview{background:rgba(3,8,13,.72)!important;border-color:rgba(126,184,255,.18)!important}.order-card{background:linear-gradient(145deg,rgba(8,14,21,.82),rgba(4,7,12,.72))!important;border-color:rgba(126,184,255,.14)!important;box-shadow:0 12px 30px rgba(0,0,0,.24),inset 0 1px 0 rgba(255,255,255,.035)}.local-order-msg,.order-saving{color:#79ff9a;font-size:10px;font-weight:900;margin-left:4px;min-width:72px}.upload-box{background:rgba(4,9,15,.58)!important;border-color:rgba(126,184,255,.28)!important}.upload-box:hover{border-color:rgba(255,255,255,.45)!important;box-shadow:0 0 22px rgba(54,164,255,.07)};\n/* NEXOMARKET HOME DIRECTORY PREMIUM v2 */\n.market-store-grid{display:grid!important;grid-template-columns:repeat(3,minmax(0,1fr))!important;gap:18px!important;align-items:stretch!important}\n.market-store-card{display:block!important;min-height:0!important;height:100%!important;padding:0!important;border-radius:22px!important;overflow:hidden!important;position:relative!important;background:linear-gradient(145deg,#0b1119,#070b11)!important;border:1px solid rgba(126,184,255,.18)!important;box-shadow:0 18px 45px rgba(0,0,0,.34),inset 0 1px 0 rgba(255,255,255,.05)!important;transition:transform .28s ease,box-shadow .28s ease,border-color .28s ease!important}\n.market-store-card:hover{transform:translateY(-7px)!important;border-color:rgba(57,255,102,.55)!important;box-shadow:0 22px 55px rgba(0,0,0,.48),0 0 35px rgba(57,255,102,.10),0 0 60px rgba(167,103,255,.07)!important}\n.store-visual{height:158px!important;position:relative!important;overflow:hidden!important;background:radial-gradient(circle at 20% 10%,rgba(57,255,102,.18),transparent 42%),radial-gradient(circle at 90% 20%,rgba(167,103,255,.20),transparent 45%),#080d14!important}\n.store-cover{position:absolute!important;inset:0!important;width:100%!important;height:100%!important;object-fit:cover!important;display:block!important;filter:saturate(.92) contrast(1.03) brightness(.62)!important;transform:scale(1.035)!important;transition:transform .5s ease,filter .5s ease!important}\n.market-store-card:hover .store-cover{transform:scale(1.09)!important;filter:saturate(1.05) contrast(1.05) brightness(.72)!important}\n.store-cover-fallback{display:flex!important;align-items:center!important;justify-content:center!important;color:rgba(255,255,255,.16)!important;font-size:38px!important;font-weight:950!important;letter-spacing:5px!important}\n.store-vignette{position:absolute!important;inset:0!important;background:linear-gradient(180deg,rgba(3,7,12,.02),rgba(3,7,12,.16) 32%,rgba(4,8,13,.90) 100%),linear-gradient(105deg,rgba(57,255,102,.08),transparent 45%,rgba(167,103,255,.10))!important}\n.store-vignette:after{content:'';position:absolute;left:-10%;right:-10%;bottom:-48%;height:85%;background:radial-gradient(ellipse,rgba(57,255,102,.14),transparent 62%);filter:blur(20px)!important}\n.store-logo-float{position:absolute!important;left:16px!important;top:16px!important;width:56px!important;height:56px!important;border-radius:17px!important;display:flex!important;align-items:center!important;justify-content:center!important;overflow:hidden!important;background:rgba(5,10,15,.72)!important;border:1px solid rgba(255,255,255,.30)!important;box-shadow:0 8px 24px rgba(0,0,0,.42),0 0 20px rgba(57,255,102,.10)!important;backdrop-filter:blur(12px)!important;z-index:4!important}\n.store-logo-float img{width:100%!important;height:100%!important;object-fit:cover!important}\n.store-logo-float .logo-letter{font-size:25px!important;font-weight:950!important;color:#39ff66!important;text-shadow:0 0 16px rgba(57,255,102,.45)!important}\n.store-tier{position:absolute!important;right:14px!important;top:16px!important;z-index:5!important;padding:7px 10px!important;border-radius:999px!important;font-size:9px!important;font-weight:950!important;letter-spacing:.7px!important;color:#7cff9a!important;background:rgba(4,18,10,.74)!important;border:1px solid rgba(57,255,102,.72)!important;box-shadow:0 0 18px rgba(57,255,102,.18)!important;backdrop-filter:blur(8px)!important}\n.store-tier.featured-common{box-shadow:0 0 10px rgba(57,255,102,.34),0 0 24px rgba(57,255,102,.14)!important;text-shadow:0 0 8px rgba(57,255,102,.72)!important}\n.store-tier.featured-common:after{content:''!important;position:absolute!important;inset:-7px!important;border-radius:999px!important;background:radial-gradient(ellipse at center,rgba(57,255,102,.28),transparent 72%)!important;filter:blur(8px)!important;opacity:.52!important;z-index:-1!important;pointer-events:none!important}\n.store-tier.plus{color:#f0dfff!important;border-color:#c88cff!important;background:linear-gradient(90deg,rgba(96,38,150,.68),rgba(17,12,29,.78))!important;box-shadow:0 0 18px rgba(167,103,255,.42),0 0 28px rgba(57,255,102,.10)!important}\n.store-tier.plus:after{content:''!important;position:absolute!important;inset:-14px!important;border-radius:999px!important;background:radial-gradient(ellipse at center,rgba(167,103,255,.38),transparent 70%)!important;filter:blur(12px)!important;opacity:.28!important;z-index:-1!important;pointer-events:none!important;animation:nexoPlusPulse5s 5s ease-in-out infinite!important}\n@keyframes nexoPlusPulse5s{0%,100%{opacity:.20;transform:scale(.98)}50%{opacity:.68;transform:scale(1.035)}}\n@media(prefers-reduced-motion:reduce){.store-tier.plus:after{animation:none!important;opacity:.30!important}}\n.store-shine{position:absolute!important;inset:-80% -25%!important;z-index:3!important;background:linear-gradient(115deg,transparent 43%,rgba(255,255,255,.13) 48%,transparent 53%)!important;transform:translateX(-30%) rotate(4deg)!important;transition:transform .65s ease!important;pointer-events:none!important}\n.market-store-card:hover .store-shine{transform:translateX(30%) rotate(4deg)!important}\n.store-card-content{position:relative!important;margin-top:-28px!important;z-index:6!important;padding:0 17px 16px!important}\n.market-store-card-top{display:flex!important;align-items:center!important;justify-content:space-between!important;gap:8px!important;margin-bottom:6px!important}\n.store-category{font-size:9px!important;text-transform:uppercase!important;letter-spacing:1.1px!important;color:#9eabb9!important;font-weight:800!important;white-space:nowrap!important;overflow:hidden!important;text-overflow:ellipsis!important}\n.store-open-badge{font-size:9px!important;font-weight:950!important;white-space:nowrap!important;text-shadow:0 0 10px rgba(57,255,102,.25)!important}\n.store-open-badge.is-open{color:#39ff66!important}\n.store-open-badge.is-closed{color:#ff6572!important;text-shadow:0 0 10px rgba(255,101,114,.18)!important}\n.market-store-card h3{font-size:21px!important;line-height:1.08!important;margin:0!important;color:#fff!important;font-weight:950!important;letter-spacing:-.35px!important;text-shadow:0 2px 14px rgba(0,0,0,.45)!important}\n.store-desc{font-size:11px!important;line-height:1.4!important;color:#8998a8!important;margin:7px 0 10px!important;display:-webkit-box!important;-webkit-line-clamp:2!important;-webkit-box-orient:vertical!important;overflow:hidden!important;min-height:31px!important}\n.store-facts{display:flex!important;align-items:center!important;gap:7px!important;flex-wrap:wrap!important;color:#b9c4cf!important;font-size:9px!important;font-weight:700!important}\n.store-facts span{display:inline-flex!important;align-items:center!important;gap:3px!important;padding:5px 7px!important;border-radius:8px!important;background:rgba(255,255,255,.045)!important;border:1px solid rgba(255,255,255,.07)!important}\n.store-facts span:last-child{color:#ffd76b!important}\n.market-store-card-cta{margin-top:12px!important;padding-top:10px!important;border-top:1px solid rgba(255,255,255,.08)!important;color:#39ff66!important;font-size:10px!important;font-weight:950!important;display:flex!important;justify-content:space-between!important;align-items:center!important;letter-spacing:.3px!important}\n.market-store-card-cta b{font-size:17px!important;line-height:1!important;transition:transform .25s ease!important}\n.market-store-card:hover .market-store-card-cta b{transform:translateX(4px)!important}\n@media(max-width:980px){.market-store-grid{grid-template-columns:repeat(2,minmax(0,1fr))!important}}\n@media(max-width:600px){.market-store-grid{grid-template-columns:1fr!important;gap:14px!important}.store-visual{height:165px!important}.market-store-card h3{font-size:20px!important}}\n";
        }

        private string LoginForm() { return "<form method='post' action='/login'><input name='email' type='email' placeholder='Correo' required/><input name='password' type='password' placeholder='Contraseña' required/><button class='btn' type='submit'>Ingresar</button></form><p><a href='/forgot-password'>¿Olvidaste tu contraseña?</a></p>"; }
        private string ForgotPasswordForm(string message = "")
        {
            return (message ?? "") + "<div class='panel'><h2>Recuperar contraseña del vendedor</h2><p class='user'>Ingresá el correo electrónico que está vinculado a tu tienda. Te enviaremos un código de recuperación.</p><form method='post' action='/forgot-password'><input name='email' type='email' placeholder='Correo del vendedor' required/><button class='btn' type='submit'>ENVIAR CÓDIGO</button></form><p class='user'>Si el correo emisor todavía no está configurado, configurá SMTP desde Seguridad en el panel de Windows.</p></div>";
        }

        private string BeginWebRecovery(string body)
        {
            Dictionary<string,string> f = Form(body);
            string email = (f.Get("email") ?? "").Trim();
            WebUser user = _store.FindWebUser(email);
            if (user == null || user.Role != "seller")
                return Page("Recuperar contraseña", ForgotPasswordForm("<div class='ok'>Si la cuenta de vendedor existe, el proceso de recuperación continuará. Verificá el correo ingresado.</div>"));

            string smtpUser = _store.GetSetting("smtp_user", "").Trim();
            string smtpPassword = _store.GetSetting("smtp_app_password", "");
            if (smtpUser.Length == 0 || smtpPassword.Length == 0)
                return Page("Recuperar contraseña", "<div class='error'><b>El correo de recuperación no está configurado todavía.</b><p>El administrador debe configurar el correo emisor y la App Password en Seguridad del panel de Windows.</p></div>" + ForgotPasswordForm());

            string code = _store.CreateWebRecoveryCode(email, 10);
            try
            {
                SendWebRecoveryMail(email, user.Name, code, smtpUser, smtpPassword);
                return Page("Código enviado", "<div class='ok'><b>Código enviado.</b><p>Revisá tu correo y colocá el código de 6 dígitos para crear una contraseña nueva.</p><form method='post' action='/reset-password'><input type='email' name='email' value='" + E(email) + "' readonly/><input name='code' inputmode='numeric' maxlength='6' placeholder='Código de 6 dígitos' required/><input name='password' type='password' placeholder='Nueva contraseña' required/><input name='repeat' type='password' placeholder='Repetir nueva contraseña' required/><button class='btn' type='submit'>RESTABLECER CONTRASEÑA</button></form></div>");
            }
            catch (Exception ex)
            {
                return Page("Recuperar contraseña", "<div class='error'>No se pudo enviar el correo de recuperación.<br/>" + E(ex.Message) + "</div>" + ForgotPasswordForm());
            }
        }

        private string ResetPasswordForm(string query)
        {
            string email = QueryValue(query, "email");
            if (!string.IsNullOrEmpty(email)) email = Uri.UnescapeDataString(email);
            return "<div class='panel'><h2>Nueva contraseña</h2><form method='post' action='/reset-password'><input type='email' name='email' value='" + E(email) + "' placeholder='Correo' required/><input name='code' inputmode='numeric' maxlength='6' placeholder='Código recibido por correo' required/><input name='password' type='password' placeholder='Nueva contraseña' required/><input name='repeat' type='password' placeholder='Repetir nueva contraseña' required/><button class='btn' type='submit'>RESTABLECER CONTRASEÑA</button></form></div>";
        }

        private string CompleteWebRecovery(string body)
        {
            Dictionary<string,string> f = Form(body);
            string email = (f.Get("email") ?? "").Trim(); string code = (f.Get("code") ?? "").Trim();
            string password = f.Get("password") ?? "";
            if (password.Length < 6 || password != f.Get("repeat")) return Page("Restablecer contraseña", "<div class='error'>La nueva contraseña debe tener al menos 6 caracteres y coincidir en ambos campos.</div>" + ResetPasswordForm("email=" + Uri.EscapeDataString(email)));
            WebUser user;
            if (!_store.VerifyWebRecoveryCode(email, code, out user)) return Page("Restablecer contraseña", "<div class='error'>El código es incorrecto o ya venció. Solicitá un código nuevo.</div>" + ResetPasswordForm("email=" + Uri.EscapeDataString(email)));
            if (!_store.SetWebUserPassword(user.Id, password)) return Page("Restablecer contraseña", "<div class='error'>No se pudo actualizar la contraseña.</div>" + ResetPasswordForm("email=" + Uri.EscapeDataString(email)));
            return Page("Contraseña actualizada", "<div class='ok'><b>Contraseña actualizada correctamente.</b><p>Ya podés ingresar al Seller Center con tu nueva contraseña.</p><a class='btn' href='/'>IR A INGRESAR</a></div>");
        }

        private void SendWebRecoveryMail(string destination, string name, string code, string smtpUser, string smtpPassword)
        {
            string host = _store.GetSetting("smtp_host", "smtp.gmail.com"); int port;
            if (!int.TryParse(_store.GetSetting("smtp_port", "587"), out port)) port = 587;
            bool ssl = _store.GetSetting("smtp_ssl", "1") == "1";
            using (MailMessage mail = new MailMessage())
            {
                mail.From = new MailAddress(smtpUser, "NexoMarket");
                mail.To.Add(destination); mail.Subject = "NexoMarket · Código de recuperación";
                mail.Body = "Hola " + (name ?? "") + ",\r\n\r\nTu código para recuperar la contraseña de vendedor de NexoMarket es: " + code + "\r\n\r\nEl código vence en 10 minutos. Si no solicitaste este cambio, ignorá este mensaje.";
                using (SmtpClient smtp = new SmtpClient(host, port)) { smtp.EnableSsl = ssl; smtp.Credentials = new System.Net.NetworkCredential(smtpUser, smtpPassword); smtp.Send(mail); }
            }
        }

        private string SellerPassword(WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            return Page("Cambiar contraseña", SellerNav(user, "") + "<div class='panel'><h2>Cambiar contraseña</h2><form method='post' action='/seller/password'><input name='current' type='password' placeholder='Contraseña actual' required/><input name='password' type='password' placeholder='Nueva contraseña' required/><input name='repeat' type='password' placeholder='Repetir nueva contraseña' required/><button class='btn' type='submit'>GUARDAR NUEVA CONTRASEÑA</button></form></div>");
        }

        private string SellerPasswordChange(string body, WebUser user)
        {
            if (!IsSeller(user)) return SellerDenied();
            Dictionary<string,string> f = Form(body);
            WebUser verified;
            if (!_store.VerifyWebUser(user.Email, f.Get("current"), out verified)) return Page("Cambiar contraseña", "<div class='error'>La contraseña actual no es correcta.</div>" + SellerPassword(user));
            string password = f.Get("password") ?? "";
            if (password.Length < 6 || password != f.Get("repeat")) return Page("Cambiar contraseña", "<div class='error'>La nueva contraseña debe tener al menos 6 caracteres y coincidir.</div>" + SellerPassword(user));
            if (!_store.SetWebUserPassword(user.Id, password)) return Page("Cambiar contraseña", "<div class='error'>No se pudo actualizar la contraseña.</div>" + SellerPassword(user));
            return Page("Contraseña actualizada", "<div class='ok'><b>Contraseña cambiada correctamente.</b><p>Tu acceso de vendedor sigue vinculado al correo " + E(user.Email) + ".</p><a class='btn' href='/seller'>VOLVER AL PANEL</a></div>");
        }

        private string RegisterForm() { return "<form method='post' action='/register'><input name='name' placeholder='Nombre' required/><input name='email' type='email' placeholder='Correo' required/><input name='phone' placeholder='Teléfono'/><input name='password' type='password' placeholder='Contraseña' required/><select name='role'><option value='buyer'>Soy comprador</option><option value='seller'>Soy vendedor</option></select><button class='btn' type='submit'>Crear cuenta</button></form>"; }
        private string Page(string title, string body) { return Header(title) + body + Footer(); }
        private string RedirectPage(string url, string message) { return Header("NexoMarket") + "<div class='ok'>" + E(message) + "</div><meta http-equiv='refresh' content='0;url=" + E(url) + "'>" + Footer(); }
        private string LogoutPage(out bool setCookie, out string cookie) { setCookie = true; cookie = "NexoSession=; Max-Age=0; Path=/"; return RedirectPage("/", "Sesión cerrada."); }
        private bool ReadVisitorPrefs(string rawCookie, out string role, out string location, out double lat, out double lon, out string display)
        {
            role = "buyer"; location = ""; lat = 0d; lon = 0d; display = "";
            string value = CookieValue(rawCookie, "NexoPrefs"); if (string.IsNullOrEmpty(value)) return false;
            try
            {
                string[] p = Uri.UnescapeDataString(value).Split(new[] { '|' }, 5); if (p.Length < 2) return false;
                role = p[0] == "seller" ? "seller" : "buyer"; location = p[1]; double.TryParse(p.Length > 2 ? p[2] : "", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat); double.TryParse(p.Length > 3 ? p[3] : "", System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon); display = p.Length > 4 ? p[4] : location; return !string.IsNullOrWhiteSpace(location);
            }
            catch { return false; }
        }

        private string CookieValue(string cookie, string name)
        {
            foreach (string part in (cookie ?? "").Split(';')) { string[] p = part.Trim().Split(new[] { '=' }, 2); if (p.Length == 2 && string.Equals(p[0], name, StringComparison.OrdinalIgnoreCase)) return p[1]; }
            return "";
        }

        private bool IsGuestUser(WebUser user)
        {
            return user != null && user.Role == "buyer" && (user.Email ?? "").EndsWith("@guest.local", StringComparison.OrdinalIgnoreCase);
        }

        private WebUser GetSession(string cookie)
        {
            if (string.IsNullOrEmpty(cookie)) return null;
            foreach (string part in cookie.Split(';'))
            {
                string[] p = part.Trim().Split(new[] { '=' }, 2);
                if (p.Length != 2 || p[0] != "NexoSession") continue;
                string token = p[1];
                lock (_sessionSync) { WebUser cached; if (_sessions.TryGetValue(token, out cached) && cached != null) return cached; }
                // Sesión persistente: si el servidor web local se reinicia (watchdog,
                // cierre de la PC o actualización), reconstruimos la sesión desde la
                // cuenta guardada en disco en lugar de expulsar al vendedor.
                try
                {
                    string[] parts = token.Split('.');
                    if (parts.Length != 2) return null;
                    string encoded = parts[0].Replace('-', '+').Replace('_', '/'); while (encoded.Length % 4 != 0) encoded += "=";
                    string email = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    WebUser found = _store.FindWebUser(email);
                    if (found == null || string.IsNullOrWhiteSpace(found.PasswordHash)) return null;
                    string expected = CreateWebSessionToken(found);
                    if (!string.Equals(expected, token, StringComparison.Ordinal)) return null;
                    lock (_sessionSync) _sessions[token] = found;
                    return found;
                }
                catch { return null; }
            }
            return null;
        }

        private string CreateWebSessionToken(WebUser user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.Email)) return Guid.NewGuid().ToString("N");
            string email = user.Email.Trim().ToLowerInvariant();
            string material = "NexoMarketWebSession|" + email + "|" + (user.PasswordHash ?? "");
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                string sig = Convert.ToBase64String(hash).TrimEnd('=').Replace('+','-').Replace('/','_');
                string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(email)).TrimEnd('=').Replace('+','-').Replace('/','_');
                return payload + "." + sig;
            }
        }
        private string ReadRequest(NetworkStream stream)
        {
            byte[] buf = new byte[8 * 1024 * 1024]; int n = 0; int content = 0;
            while (n < buf.Length)
            {
                int r = stream.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r;
                string x = Encoding.UTF8.GetString(buf, 0, n); int pos = x.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (content == 0) { int.TryParse(HeaderValue(x, "Content-Length"), out content); }
                if (pos >= 0 && n >= pos + 4 + content) break; if (pos >= 0 && content == 0) break;
            }
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        private string HeaderValue(string request, string name) { string[] ls = request.Split(new[] { "\r\n" }, StringSplitOptions.None); foreach (string l in ls) { int c = l.IndexOf(':'); if (c > 0 && l.Substring(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return l.Substring(c + 1).Trim(); } return ""; }
        private string ExtractBody(string r) { int i = r.IndexOf("\r\n\r\n", StringComparison.Ordinal); return i >= 0 ? r.Substring(i + 4) : ""; }
        private Dictionary<string,string> Form(string body) { Dictionary<string,string> d = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); foreach (string x in (body ?? "").Split('&')) { string[] p = x.Split(new[] { '=' }, 2); if (p.Length == 2) d[WebUtility.UrlDecode(p[0])] = WebUtility.UrlDecode(p[1]); } return d; }
        private string E(string s) { return WebUtility.HtmlEncode(s ?? ""); }
        private void WriteHtml(NetworkStream s, int code, string html, bool cookie, string value)
        {
            byte[] b = Encoding.UTF8.GetBytes(html); string text = code == 200 ? "OK" : "Error";
            string h = "HTTP/1.1 " + code + " " + text + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + b.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n" + (cookie ? ("Set-Cookie: " + value + "\r\n") : "") + "\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(h); s.Write(hb, 0, hb.Length); s.Write(b, 0, b.Length);
        }
        private void ServeMedia(NetworkStream s, string query)
        {
            try
            {
                string p = QueryValue(query, "p"); if (string.IsNullOrEmpty(p)) { WriteRaw(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
                p = Uri.UnescapeDataString(p); string full = Path.GetFullPath(p); string root = Path.GetFullPath(_store.Root);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { WriteRaw(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
                WriteRaw(s, 200, Mime(full), File.ReadAllBytes(full));
            }
            catch { WriteRaw(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); }
        }
        private void WriteRaw(NetworkStream s, int code, string mime, byte[] data)
        {
            string h = "HTTP/1.1 " + code + (code == 200 ? " OK" : " Not Found") + "\r\nContent-Type: " + mime + "\r\nContent-Length: " + data.Length + "\r\nCache-Control: public,max-age=300\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(h); s.Write(hb, 0, hb.Length); s.Write(data, 0, data.Length);
        }
        private string Mime(string path) { string x = Path.GetExtension(path).ToLowerInvariant(); if (x == ".jpg" || x == ".jpeg") return "image/jpeg"; if (x == ".png") return "image/png"; if (x == ".gif") return "image/gif"; if (x == ".bmp") return "image/bmp"; return "application/octet-stream"; }
        private string QueryValue(string query, string key) { foreach (string part in (query ?? "").Split('&')) { string[] p = part.Split(new[] { '=' }, 2); if (p.Length == 2 && string.Equals(Uri.UnescapeDataString(p[0]), key, StringComparison.OrdinalIgnoreCase)) return p[1]; } return ""; }
        private string Footer() { return "<footer>NexoMarket · StoreId " + E(StoreId) + " · consola web compatible con Windows 8 / .NET 4.0</footer></div></body></html>"; }
        public string GetLocalIPv4()
        {
            try { foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) { if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue; foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses) if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address)) return ip.Address.ToString(); } } catch { }
            return "127.0.0.1";
        }
        private static string ComputeStorePairKey(string storeId)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] data = Encoding.UTF8.GetBytes("NexoMarket.StorePair.v1:" + (storeId ?? "").Trim().Replace(" ", "").ToUpperInvariant());
                byte[] hash = sha.ComputeHash(data);
                StringBuilder b = new StringBuilder(hash.Length * 2);
                foreach (byte x in hash) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }

        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                _desiredRunning = false;
                _running = false;
                LogWeb("SERVIDOR DETENIDO");
                try { if (_watchdog != null) _watchdog.Dispose(); } catch { }
                _watchdog = null;
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
            }
            lock (_sessionSync) _sessions.Clear();
        }
    }

    internal sealed class PromotionSelection
    {
        public long PromotionId;
        public string Name;
        public List<long> ProductIds = new List<long>();
        public decimal UnitPrice;
        public int Quantity;

        public PromotionSelection Clone()
        {
            return new PromotionSelection
            {
                PromotionId = PromotionId,
                Name = Name,
                ProductIds = new List<long>(ProductIds),
                UnitPrice = UnitPrice,
                Quantity = Quantity
            };
        }
    }

    internal static class WebFormDictionaryExtension
    {
        public static string Get(this Dictionary<string,string> d, string key) { string v; return d.TryGetValue(key, out v) ? v ?? "" : ""; }
    }
}
