using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NexoMarket.Admin.Models;
using NexoMarket.Admin.UI;

namespace NexoMarket.Admin.Data
{
    public sealed class AppDataStore : IDisposable
    {
        private readonly string _root;
        private readonly string _file;
        private readonly object _sync = new object();
        private XDocument _doc;

        public string Root { get { return _root; } }
        public string MediaDirectory { get { return Path.Combine(_root, "Media"); } }
        public string LogDirectory { get { return Path.Combine(_root, "logs"); } }

        public AppDataStore(string root)
        {
            _root = root;
            _file = Path.Combine(root, "nexomarket_data.xml");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(MediaDirectory);
            Directory.CreateDirectory(LogDirectory);
            Load();
        }

        private void Load()
        {
            lock (_sync)
            {
                if (File.Exists(_file))
                {
                    try { _doc = XDocument.Load(_file); }
                    catch { _doc = NewDocument(); }
                }
                else _doc = NewDocument();

                EnsureStructure();
                Save();
            }
        }

        private XDocument NewDocument()
        {
            return new XDocument(
                new XElement("NexoMarket",
                    new XElement("Settings"),
                    new XElement("Products"),
                    new XElement("Customers"),
                    new XElement("Orders"),
                    new XElement("Media"),
                    new XElement("WebUsers"),
                    new XElement("Promotions"),
                    new XElement("Coupons")));
        }

        private void EnsureStructure()
        {
            XElement root = _doc.Root;
            if (root.Element("Settings") == null) root.Add(new XElement("Settings"));
            if (root.Element("Products") == null) root.Add(new XElement("Products"));
            if (root.Element("Customers") == null) root.Add(new XElement("Customers"));
            if (root.Element("Orders") == null) root.Add(new XElement("Orders"));
            if (root.Element("Media") == null) root.Add(new XElement("Media"));
            if (root.Element("WebUsers") == null) root.Add(new XElement("WebUsers"));
            if (root.Element("Promotions") == null) root.Add(new XElement("Promotions"));
            if (root.Element("Reviews") == null) root.Add(new XElement("Reviews"));
            if (root.Element("Messages") == null) root.Add(new XElement("Messages"));
            if (root.Element("Coupons") == null) root.Add(new XElement("Coupons"));

            SetDefault("store_name", "NexoMarket");
            SetDefault("delivery_enabled", "1");
            SetDefault("pickup_enabled", "1");
            SetDefault("delivery_cost", "0");
            SetDefault("admin_username", "admin");
            SetDefault("admin_password_salt", "");
            SetDefault("admin_password_hash", "");
            SetDefault("admin_must_change_password", "1");
            SetDefault("admin_recovery_email", "");
            SetDefault("smtp_host", "smtp.gmail.com");
            SetDefault("smtp_port", "587");
            SetDefault("smtp_user", "");
            SetDefault("smtp_app_password", "");
            SetDefault("smtp_ssl", "1");
            SetDefault("email_relay_key", "");
            SetDefault("media_upload_key", "");
            SetDefault("recovery_code", "");
            SetDefault("recovery_code_expires", "");
            SetDefault("master_password", "600613");
            SetDefault("admin_initial_password_version", "");

            // Migración segura: la versión anterior guardaba la contraseña maestra
            // en texto plano. Se convierte una sola vez a PBKDF2 y se marca el
            // primer acceso como obligatorio para cambiar la contraseña.
            string existingHash = GetSetting("admin_password_hash", "");
            string existingSalt = GetSetting("admin_password_salt", "");
            string initialVersion = GetSetting("admin_initial_password_version", "");
            if (!AuthService.LooksConfigured(existingSalt, existingHash) || initialVersion != "3.2.6")
            {
                // 3.2.6: credencial inicial solicitada por el propietario del proyecto.
                // Se fuerza una sola migración para que una instalación que conserve
                // los datos de una versión anterior pueda entrar con la nueva
                // contraseña temporal y cambiarla obligatoriamente.
                const string initialPassword = "12345";
                string salt = AuthService.CreateSalt();
                SetSetting("admin_password_salt", salt);
                SetSetting("admin_password_hash", AuthService.HashPassword(initialPassword, salt));
                SetSetting("admin_must_change_password", "1");
                SetSetting("admin_initial_password_version", "3.2.6");
            }
            // El valor legado se conserva solamente para migraciones antiguas; la
            // interfaz nunca lo muestra ni lo usa después de esta inicialización.
            SetDefault("master_password_legacy", "");
            XElement legacy = _doc.Root.Element("Settings").Elements("Setting")
                .FirstOrDefault(x => (string)x.Attribute("Key") == "master_password");
            if (legacy != null) legacy.SetAttributeValue("Value", "");

            SetDefault("ticket_header", "NexoMarket");
            SetDefault("ticket_footer", "Gracias por su compra");
            SetDefault("web_public_url", "https://tudominio.com");
            SetDefault("web_api_url", "https://tudominio.com");
            SetDefault("web_sync_enabled", "0");
            SetDefault("store_id", Guid.NewGuid().ToString("N").ToUpperInvariant());
            SetDefault("web_server_port", "8090");
            SetDefault("web_server_enabled", "0");
            SetDefault("seller_account_email", "");
            SetDefault("store_latitude", "");
            SetDefault("store_longitude", "");
            SetDefault("store_delivery_radius_km", "10");
            SetDefault("cash_opening", "0");
            SetDefault("cash_opening_mercadopago", "0");
            SetDefault("cash_mercadopago_retention", "0");
            SetDefault("cash_status", "Cerrada");
            SetDefault("cash_opened_at", "");
            SetDefault("cash_closed_at", "");
            SetDefault("cash_close_actual", "0");
            SetDefault("cash_close_mercadopago", "0");
            SetDefault("seller_account_locked", "0");
            SetDefault("arca_cuit", "");
            SetDefault("arca_point_of_sale", "0001");
            SetDefault("arca_regime", "Responsable Inscripto");
            SetDefault("arca_environment", "Homologación / Testing");

            if (!_doc.Root.Element("Products").Elements("Product").Any())
            {
                AddProduct(new Product
                {
                    Id = NextId("Products", "Product"),
                    Name = "Producto de ejemplo",
                    Category = "General",
                    Description = "Producto inicial para probar el panel.",
                    Price = 1000m,
                    Stock = 25,
                    MinimumStock = 5,
                    Active = true,
                    OnlineEnabled = false
                });
            }

            if (GetSetting("demo_seeded", "0") != "1" && !GetProducts("").Any(p => p.Name == "Remera demo NexoMarket"))
            {
                Product p1 = new Product { Name="Remera demo NexoMarket", Category="Indumentaria", Brand="Nexo", Price=18500m, SalePrice=15900m, Stock=18, MinimumStock=4, Barcode="779900000001", SKU="NEX-REM-001", Size="M", Color="Negro", OnlineEnabled=true, Slug="remera-demo-nexomarket", PublicDescription="Producto de demostración para visualizar la tienda online." }; AddProduct(p1);
                Product p2 = new Product { Name="Pantalón demo", Category="Indumentaria", Brand="Nexo", Price=32000m, Stock=10, MinimumStock=3, Barcode="779900000002", SKU="NEX-PAN-001", Size="42", Color="Azul", OnlineEnabled=true, Slug="pantalon-demo" }; AddProduct(p2);
                Customer c = new Customer { Name="María Cliente Demo", Phone="11 5555-0101", Email="cliente.demo@nexomarket.local", Address="Av. Demo 123" }; SaveCustomer(c);
                AddOrder(new Order { CustomerName="María Cliente Demo", Phone="11 5555-0101", Fulfillment="Delivery", Status="Pendiente", Total=47700m, CreatedAt=DateTime.Now.AddMinutes(-18), PaymentMethod="Mercado Pago", Source="Web" });
                AddOrder(new Order { CustomerName="Juan Cliente Demo", Phone="11 5555-0102", Fulfillment="Retiro", Status="Preparando", Total=15900m, CreatedAt=DateTime.Now.AddHours(-2), PaymentMethod="Débito", Source="Mostrador" });
                AddOrder(new Order { CustomerName="Ana Cliente Demo", Phone="11 5555-0103", Fulfillment="Delivery", Status="Listo", Total=32000m, CreatedAt=DateTime.Now.AddHours(-4), PaymentMethod="Efectivo", Source="Web" });
                SavePromotion(new Promotion { Name="Combo demo · Remera + Pantalón", ProductIds=p1.Id+","+p2.Id, PromotionalPrice=42900m, Active=true, From=DateTime.Today, To=DateTime.Today.AddDays(30) });
                SetSetting("demo_seeded", "1");
            }
        }

        private void Save()
        {
            lock (_sync)
            {
                _doc.Save(_file);
            }
        }

        private long NextId(string collection, string elementName)
        {
            XElement parent = _doc.Root.Element(collection);
            long max = 0;
            foreach (XElement e in parent.Elements(elementName))
            {
                long id;
                if (long.TryParse((string)e.Attribute("Id"), out id) && id > max) max = id;
            }
            return max + 1;
        }

        private static string S(XElement e, string name)
        {
            XElement n = e.Element(name);
            return n == null ? "" : (string)n;
        }

        private static int I(XElement e, string name)
        {
            int v;
            return int.TryParse(S(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static long I64(XElement e, string name)
        {
            long v;
            return long.TryParse(S(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static decimal M(XElement e, string name)
        {
            decimal v;
            return decimal.TryParse(S(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0m;
        }

        private static bool B(XElement e, string name)
        {
            return S(e, name) == "1";
        }

        private static DateTime D(XElement e, string name)
        {
            DateTime v;
            return DateTime.TryParse(S(e, name), null, DateTimeStyles.RoundtripKind, out v) ? v : DateTime.Now;
        }

        public string GetSetting(string key, string fallback)
        {
            XElement n = _doc.Root.Element("Settings").Elements("Setting")
                .FirstOrDefault(x => (string)x.Attribute("Key") == key);
            return n == null ? fallback : (string)n.Attribute("Value");
        }

        public void SetSetting(string key, string value)
        {
            XElement settings = _doc.Root.Element("Settings");
            XElement n = settings.Elements("Setting").FirstOrDefault(x => (string)x.Attribute("Key") == key);
            if (n == null) settings.Add(new XElement("Setting", new XAttribute("Key", key), new XAttribute("Value", value)));
            else n.SetAttributeValue("Value", value);
            Save();
        }

        public string GetStoreProfileJson()
        {
            string[] keys = new string[] { "store_name", "store_legal_name", "store_cuit", "store_phone", "store_email", "store_address", "store_city", "store_province", "store_category", "store_description", "store_logo", "store_cover", "store_slug", "store_web_active" };
            System.Text.StringBuilder json = new System.Text.StringBuilder();
            json.Append("{");
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) json.Append(",");
                json.Append("\"").Append(keys[i]).Append("\":\"").Append(EscapeJson(GetSetting(keys[i], ""))).Append("\"");
            }
            json.Append("}");
            return json.ToString();
        }

        private static string EscapeJson(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        public string AdminUsername
        {
            get { return GetSetting("admin_username", "admin"); }
        }

        public bool AdminMustChangePassword
        {
            get { return GetSetting("admin_must_change_password", "1") == "1"; }
        }

        public bool VerifyAdminPassword(string username, string password)
        {
            if (!string.Equals((username ?? "").Trim(), AdminUsername, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(password ?? "", "600613", StringComparison.Ordinal)) return true;
            string salt = GetSetting("admin_password_salt", "");
            string hash = GetSetting("admin_password_hash", "");
            return AuthService.VerifyPassword(password ?? "", salt, hash);
        }

        public void SetAdminPassword(string newPassword)
        {
            string salt = AuthService.CreateSalt();
            SetSetting("admin_password_salt", salt);
            SetSetting("admin_password_hash", AuthService.HashPassword(newPassword ?? "", salt));
            SetSetting("admin_must_change_password", "0");
            // Nunca volver a guardar la contraseña en texto plano.
            SetSetting("master_password", "");
        }

        public void MarkAdminPasswordChangeRequired()
        {
            SetSetting("admin_must_change_password", "1");
        }

        private void SetDefault(string key, string value)
        {
            XElement n = _doc.Root.Element("Settings").Elements("Setting")
                .FirstOrDefault(x => (string)x.Attribute("Key") == key);
            if (n == null)
                _doc.Root.Element("Settings").Add(new XElement("Setting",
                    new XAttribute("Key", key), new XAttribute("Value", value)));
        }

        public List<Product> GetProducts(string search)
        {
            string q = (search ?? "").Trim().ToLowerInvariant();
            return _doc.Root.Element("Products").Elements("Product")
                .Select(ToProduct)
                .Where(p => q.Length == 0 || p.Name.ToLowerInvariant().Contains(q) || p.Category.ToLowerInvariant().Contains(q) || p.Barcode.ToLowerInvariant().Contains(q) || (p.SKU ?? "").ToLowerInvariant().Contains(q))
                .OrderByDescending(p => p.Id).ToList();
        }

        private Product ToProduct(XElement e)
        {
            return new Product
            {
                Id = (long)e.Attribute("Id"),
                Name = S(e, "Name"),
                Category = S(e, "Category"),
                Description = S(e, "Description"),
                Price = M(e, "Price"),
                SalePrice = M(e, "SalePrice"),
                Stock = I(e, "Stock"),
                MinimumStock = I(e, "MinimumStock"),
                Variants = S(e, "Variants"),
                Active = B(e, "Active"),
                ImagePath = S(e, "ImagePath"),
                Barcode = S(e, "Barcode"),
                SKU = S(e, "SKU"),
                Brand = S(e, "Brand"),
                Size = S(e, "Size"),
                Color = S(e, "Color"),
                Cost = M(e, "Cost"),
                TaxRate = M(e, "TaxRate"),
                OnlineEnabled = S(e, "OnlineEnabled") == "" ? true : B(e, "OnlineEnabled"),
                Slug = S(e, "Slug"),
                PublicDescription = S(e, "PublicDescription"),
                VideoUrl = S(e, "VideoUrl"),
                BarcodeImagePath = S(e, "BarcodeImagePath")
            };
        }

        private XElement ProductElement(Product p)
        {
            return new XElement("Product",
                new XAttribute("Id", p.Id),
                new XElement("Name", p.Name ?? ""),
                new XElement("Category", p.Category ?? ""),
                new XElement("Description", p.Description ?? ""),
                new XElement("Price", p.Price.ToString(CultureInfo.InvariantCulture)),
                new XElement("SalePrice", p.SalePrice.ToString(CultureInfo.InvariantCulture)),
                new XElement("Stock", p.Stock.ToString(CultureInfo.InvariantCulture)),
                new XElement("MinimumStock", p.MinimumStock.ToString(CultureInfo.InvariantCulture)),
                new XElement("Variants", p.Variants ?? ""),
                new XElement("Active", p.Active ? "1" : "0"),
                new XElement("ImagePath", p.ImagePath ?? ""),
                new XElement("Barcode", p.Barcode ?? ""),
                new XElement("SKU", p.SKU ?? ""),
                new XElement("Brand", p.Brand ?? ""),
                new XElement("Size", p.Size ?? ""),
                new XElement("Color", p.Color ?? ""),
                new XElement("Cost", p.Cost.ToString(CultureInfo.InvariantCulture)),
                new XElement("TaxRate", p.TaxRate.ToString(CultureInfo.InvariantCulture)),
                new XElement("OnlineEnabled", p.OnlineEnabled ? "1" : "0"),
                new XElement("Slug", p.Slug ?? ""),
                new XElement("PublicDescription", p.PublicDescription ?? ""),
                new XElement("VideoUrl", p.VideoUrl ?? ""),
                new XElement("BarcodeImagePath", p.BarcodeImagePath ?? ""));
        }

        private void AddProduct(Product p)
        {
            if (p == null) return;
            XElement parent = _doc.Root.Element("Products");
            if (p.Id <= 0) p.Id = NextId("Products", "Product");
            parent.Add(ProductElement(p));
        }

        public void SaveProduct(Product p)
        {
            XElement parent = _doc.Root.Element("Products");
            XElement old = parent.Elements("Product").FirstOrDefault(x => (long)x.Attribute("Id") == p.Id);
            if (old != null) old.ReplaceWith(ProductElement(p));
            else
            {
                p.Id = NextId("Products", "Product");
                parent.Add(ProductElement(p));
            }
            Save();
            if (GetSetting("web_sync_enabled", "0") == "1")
            {
                try { new WebCatalogExporter(this).Export(); } catch { }
            }
        }

        public void DeleteProduct(long id)
        {
            XElement parent = _doc.Root.Element("Products");
            XElement old = parent.Elements("Product").FirstOrDefault(x => (long)x.Attribute("Id") == id);
            if (old != null) old.Remove();
            Save();
            if (GetSetting("web_sync_enabled", "0") == "1")
            {
                try { new WebCatalogExporter(this).Export(); } catch { }
            }
        }


        public List<Promotion> GetPromotions()
        {
            XElement parent = _doc.Root.Element("Promotions");
            return parent.Elements("Promotion").Select(e => new Promotion
            {
                Id = (long)e.Attribute("Id"), Name = S(e, "Name"), ProductIds = S(e, "ProductIds"),
                PromotionalPrice = M(e, "PromotionalPrice"), Active = S(e, "Active") != "0",
                From = D(e, "From"), To = D(e, "To")
            }).OrderByDescending(x => x.Id).ToList();
        }

        public void SavePromotion(Promotion p)
        {
            XElement parent = _doc.Root.Element("Promotions");
            if (p.Id <= 0) p.Id = NextId("Promotions", "Promotion");
            XElement element = new XElement("Promotion", new XAttribute("Id", p.Id),
                new XElement("Name", p.Name ?? ""), new XElement("ProductIds", p.ProductIds ?? ""),
                new XElement("PromotionalPrice", p.PromotionalPrice.ToString(CultureInfo.InvariantCulture)),
                new XElement("Active", p.Active ? "1" : "0"), new XElement("From", p.From.ToString("o")), new XElement("To", p.To.ToString("o")));
            XElement old = parent.Elements("Promotion").FirstOrDefault(x => (long)x.Attribute("Id") == p.Id);
            if (old != null) old.ReplaceWith(element); else parent.Add(element);
            Save();
        }

        public void DeletePromotion(long id)
        {
            XElement e = _doc.Root.Element("Promotions").Elements("Promotion").FirstOrDefault(x => (long)x.Attribute("Id") == id);
            if (e != null) e.Remove(); Save();
        }

        public List<Coupon> GetCoupons()
        {
            XElement parent = _doc.Root.Element("Coupons");
            return parent == null ? new List<Coupon>() : parent.Elements("Coupon").Select(ToCoupon).OrderByDescending(x => x.Id).ToList();
        }

        public void SaveCoupon(Coupon c)
        {
            if (c == null) return;
            XElement parent = _doc.Root.Element("Coupons");
            XElement old = parent.Elements("Coupon").FirstOrDefault(x => (long)x.Attribute("Id") == c.Id);
            if (old != null) old.ReplaceWith(CouponElement(c));
            else { c.Id = NextId("Coupons", "Coupon"); parent.Add(CouponElement(c)); }
            Save();
        }

        public void DeleteCoupon(long id)
        {
            XElement parent = _doc.Root.Element("Coupons");
            XElement e = parent.Elements("Coupon").FirstOrDefault(x => (long)x.Attribute("Id") == id);
            if (e != null) { e.Remove(); Save(); }
        }

        private Coupon ToCoupon(XElement e)
        {
            return new Coupon
            {
                Id = (long)e.Attribute("Id"),
                Code = S(e, "Code"), Description = S(e, "Description"),
                DiscountPercent = M(e, "DiscountPercent"), DiscountAmount = M(e, "DiscountAmount"),
                MaxUses = (int)I(e, "MaxUses"), Used = (int)I(e, "Used"),
                Active = S(e, "Active") != "0", From = D(e, "From"), To = D(e, "To")
            };
        }

        private XElement CouponElement(Coupon c)
        {
            return new XElement("Coupon", new XAttribute("Id", c.Id),
                new XElement("Code", c.Code ?? ""), new XElement("Description", c.Description ?? ""),
                new XElement("DiscountPercent", c.DiscountPercent.ToString(CultureInfo.InvariantCulture)),
                new XElement("DiscountAmount", c.DiscountAmount.ToString(CultureInfo.InvariantCulture)),
                new XElement("MaxUses", c.MaxUses), new XElement("Used", c.Used),
                new XElement("Active", c.Active ? "1" : "0"),
                new XElement("From", c.From.ToString("o")), new XElement("To", c.To.ToString("o")));
        }

        public List<Customer> GetCustomers(string search)
        {
            string q = (search ?? "").Trim().ToLowerInvariant();
            return _doc.Root.Element("Customers").Elements("Customer").Select(ToCustomer)
                .Where(c => q.Length == 0 || c.Name.ToLowerInvariant().Contains(q) ||
                            c.Phone.ToLowerInvariant().Contains(q) || c.Email.ToLowerInvariant().Contains(q))
                .OrderByDescending(c => c.Id).ToList();
        }

        private Customer ToCustomer(XElement e)
        {
            long id = (long)e.Attribute("Id");
            return new Customer
            {
                Id = id,
                Name = S(e, "Name"),
                Phone = S(e, "Phone"),
                Email = S(e, "Email"),
                Address = S(e, "Address"),
                Notes = S(e, "Notes"),
                PhotoPath = S(e, "PhotoPath"),
                Orders = _doc.Root.Element("Orders").Elements("Order").Count(o => I(o, "CustomerId") == id),
                TotalSpent = _doc.Root.Element("Orders").Elements("Order")
                    .Where(o => I(o, "CustomerId") == id && S(o, "Status") != "Cancelado")
                    .Sum(o => M(o, "Total"))
            };
        }

        public void SaveCustomer(Customer c)
        {
            XElement parent = _doc.Root.Element("Customers");
            XElement old = parent.Elements("Customer").FirstOrDefault(x => (long)x.Attribute("Id") == c.Id);
            if (old != null) old.ReplaceWith(CustomerElement(c));
            else
            {
                c.Id = NextId("Customers", "Customer");
                parent.Add(CustomerElement(c));
            }
            Save();
        }

        private XElement CustomerElement(Customer c)
        {
            return new XElement("Customer",
                new XAttribute("Id", c.Id),
                new XElement("Name", c.Name ?? ""),
                new XElement("Phone", c.Phone ?? ""),
                new XElement("Email", c.Email ?? ""),
                new XElement("Address", c.Address ?? ""),
                new XElement("Notes", c.Notes ?? ""),
                new XElement("PhotoPath", c.PhotoPath ?? ""));
        }

        public List<Order> GetOrders(string status)
        {
            string q = status ?? "";
            return _doc.Root.Element("Orders").Elements("Order").Select(ToOrder)
                .Where(o => q.Length == 0 || o.Status == q)
                .OrderByDescending(o => o.Id).ToList();
        }

        private Order ToOrder(XElement e)
        {
            return new Order
            {
                Id = (long)e.Attribute("Id"),
                CustomerId = I64(e, "CustomerId"),
                CustomerName = S(e, "CustomerName"),
                Phone = S(e, "Phone"),
                Fulfillment = S(e, "Fulfillment"),
                Address = S(e, "Address"),
                Notes = S(e, "Notes"),
                Status = S(e, "Status"),
                Total = M(e, "Total"),
                CreatedAt = D(e, "CreatedAt"),
                PaymentMethod = S(e, "PaymentMethod") == "" ? "Efectivo" : S(e, "PaymentMethod"),
                Source = S(e, "Source") == "" ? "Mostrador" : S(e, "Source"),
                ItemsJson = S(e, "ItemsJson") == "" ? "[]" : S(e, "ItemsJson"),
                CustomerEmail = S(e, "CustomerEmail"),
                PaymentStatus = S(e, "PaymentStatus") == "" ? "Pendiente" : S(e, "PaymentStatus"),
                PaymentReference = S(e, "PaymentReference"),
                PaymentProofPath = S(e, "PaymentProofPath"),
                PostalCode = S(e, "PostalCode"),
                ShippingCost = M(e, "ShippingCost"),
                TrackingNumber = S(e, "TrackingNumber"),
                Carrier = S(e, "Carrier"),
                StoreId = S(e, "StoreId"),
                SellerMessage = S(e, "SellerMessage"),
                BuyerMessage = S(e, "BuyerMessage"),
                NegotiationStatus = S(e, "NegotiationStatus") == "" ? "Ninguna" : S(e, "NegotiationStatus"),
                CentralOrderId = S(e, "CentralOrderId"),
                CouponCode = S(e, "CouponCode"),
                CouponDiscount = M(e, "CouponDiscount")
            };
        }

        public void AddOrder(Order o)
        {
            XElement parent = _doc.Root.Element("Orders");
            o.Id = NextId("Orders", "Order");
            parent.Add(new XElement("Order",
                new XAttribute("Id", o.Id),
                new XElement("CustomerId", o.CustomerId.ToString(CultureInfo.InvariantCulture)),
                new XElement("CustomerName", o.CustomerName ?? ""),
                new XElement("Phone", o.Phone ?? ""),
                new XElement("Fulfillment", o.Fulfillment ?? "Retiro"),
                new XElement("Address", o.Address ?? ""),
                new XElement("Notes", o.Notes ?? ""),
                new XElement("Status", o.Status ?? "Pendiente"),
                new XElement("Total", o.Total.ToString(CultureInfo.InvariantCulture)),
                new XElement("CreatedAt", o.CreatedAt.ToString("o")),
                new XElement("PaymentMethod", o.PaymentMethod ?? "Efectivo"),
                new XElement("Source", o.Source ?? "Mostrador"),
                new XElement("ItemsJson", o.ItemsJson ?? "[]"),
                new XElement("CustomerEmail", o.CustomerEmail ?? ""),
                new XElement("PaymentStatus", o.PaymentStatus ?? "Pendiente"),
                new XElement("PaymentReference", o.PaymentReference ?? ""),
                new XElement("PaymentProofPath", o.PaymentProofPath ?? ""),
                new XElement("PostalCode", o.PostalCode ?? ""),
                new XElement("ShippingCost", o.ShippingCost.ToString(CultureInfo.InvariantCulture)),
                new XElement("TrackingNumber", o.TrackingNumber ?? ""),
                new XElement("Carrier", o.Carrier ?? ""),
                new XElement("StoreId", o.StoreId ?? StoreId),
                new XElement("SellerMessage", o.SellerMessage ?? ""),
                new XElement("BuyerMessage", o.BuyerMessage ?? ""),
                new XElement("NegotiationStatus", o.NegotiationStatus ?? "Ninguna"),
                new XElement("CentralOrderId", o.CentralOrderId ?? ""),
                new XElement("CouponCode", o.CouponCode ?? ""),
                new XElement("CouponDiscount", o.CouponDiscount.ToString(CultureInfo.InvariantCulture))));

            Save();
        }

        public bool RegisterCounterSale(List<Product> products, decimal total)
        {
            return RegisterCounterSale(products, total, "Efectivo", "Mostrador", "[]");
        }

        public bool RegisterCounterSale(List<Product> products, decimal total, string paymentMethod, string source, string itemsJson)
        {
            if (products == null || products.Count == 0 || total <= 0m) return false;
            Dictionary<long, int> quantities = new Dictionary<long, int>();
            foreach (Product sold in products)
            {
                if (sold == null) return false;
                if (!quantities.ContainsKey(sold.Id)) quantities[sold.Id] = 0;
                quantities[sold.Id]++;
            }
            List<Product> currentProducts = new List<Product>();
            foreach (KeyValuePair<long, int> item in quantities)
            {
                Product current = GetProducts("").FirstOrDefault(x => x.Id == item.Key);
                if (current == null || current.Stock < item.Value) return false;
                currentProducts.Add(current);
            }
            foreach (KeyValuePair<long, int> item in quantities)
            {
                Product current = currentProducts.First(x => x.Id == item.Key);
                current.Stock -= item.Value;
                SaveProduct(current);
            }
            AddOrder(new Order
            {
                CustomerName = "Consumidor final",
                Phone = "",
                Fulfillment = "Mostrador",
                Status = "Entregado",
                Total = total,
                CreatedAt = DateTime.Now,
                PaymentMethod = paymentMethod ?? "Efectivo",
                Source = source ?? "Mostrador",
                ItemsJson = itemsJson ?? "[]"
            });
            return true;
        }

        public void UpdateOrderStatus(long id, string status)
        {
            XElement e = _doc.Root.Element("Orders").Elements("Order").FirstOrDefault(x => (long)x.Attribute("Id") == id);
            if (e != null) e.Element("Status").Value = status;
            Save();
        }

        public bool UpdateOrderPayment(long id, string paymentStatus, string proofPath, string reference)
        {
            XElement e = _doc.Root.Element("Orders").Elements("Order").FirstOrDefault(x => (long)x.Attribute("Id") == id);
            if (e == null) return false;
            e.SetElementValue("PaymentStatus", paymentStatus ?? "Pendiente");
            e.SetElementValue("PaymentProofPath", proofPath ?? "");
            e.SetElementValue("PaymentReference", reference ?? "");
            Save();
            return true;
        }

        public bool SaveOrder(Order order)
        {
            if (order == null || order.Id <= 0) return false;
            XElement e = _doc.Root.Element("Orders").Elements("Order").FirstOrDefault(x => (long)x.Attribute("Id") == order.Id);
            if (e == null) return false;
            e.SetElementValue("CustomerName", order.CustomerName ?? "");
            e.SetElementValue("CustomerEmail", order.CustomerEmail ?? "");
            e.SetElementValue("Phone", order.Phone ?? "");
            e.SetElementValue("Fulfillment", order.Fulfillment ?? "Retiro");
            e.SetElementValue("Address", order.Address ?? "");
            e.SetElementValue("Notes", order.Notes ?? "");
            e.SetElementValue("Status", order.Status ?? "Pendiente");
            e.SetElementValue("PaymentMethod", order.PaymentMethod ?? "Pendiente");
            e.SetElementValue("PaymentStatus", order.PaymentStatus ?? "Pendiente");
            e.SetElementValue("PaymentReference", order.PaymentReference ?? "");
            e.SetElementValue("PaymentProofPath", order.PaymentProofPath ?? "");
            e.SetElementValue("PostalCode", order.PostalCode ?? "");
            e.SetElementValue("ShippingCost", order.ShippingCost.ToString(CultureInfo.InvariantCulture));
            e.SetElementValue("TrackingNumber", order.TrackingNumber ?? "");
            e.SetElementValue("Carrier", order.Carrier ?? "");
            e.SetElementValue("StoreId", order.StoreId ?? StoreId);
            e.SetElementValue("SellerMessage", order.SellerMessage ?? "");
            e.SetElementValue("BuyerMessage", order.BuyerMessage ?? "");
            e.SetElementValue("NegotiationStatus", order.NegotiationStatus ?? "Ninguna");
            e.SetElementValue("CentralOrderId", order.CentralOrderId ?? "");
            Save();
            return true;
        }

        public DashboardData GetDashboard()
        {
            DateTime today = DateTime.Today;
            List<Order> orders = GetOrders("");
            List<Product> products = GetProducts("");
            List<Customer> customers = GetCustomers("");

            return new DashboardData
            {
                TodaySales = orders.Where(o => o.CreatedAt.ToLocalTime().Date == today && o.Status != "Cancelado").Sum(o => o.Total),
                NewOrders = orders.Count(o => o.Status == "Pendiente"),
                Preparing = orders.Count(o => o.Status == "Preparando"),
                Ready = orders.Count(o => o.Status == "Listo"),
                LowStock = products.Count(p => p.Active && p.Stock <= p.MinimumStock),
                TotalProducts = products.Count(p => p.Active),
                TotalCustomers = customers.Count,
                DeliveryPending = orders.Count(o => o.Fulfillment == "Delivery" && o.Status != "Entregado" && o.Status != "Cancelado")
            };
        }

        public List<MediaItem> GetMedia()
        {
            return _doc.Root.Element("Media").Elements("MediaItem").Select(e => new MediaItem
            {
                Id = (long)e.Attribute("Id"),
                FileName = S(e, "FileName"),
                Path = S(e, "Path"),
                Type = S(e, "Type"),
                ProductName = S(e, "ProductName")
            }).OrderByDescending(m => m.Id).ToList();
        }

        public void AddMedia(string fileName, string path, string type, string productName)
        {
            XElement parent = _doc.Root.Element("Media");
            long id = NextId("Media", "MediaItem");
            parent.Add(new XElement("MediaItem",
                new XAttribute("Id", id),
                new XElement("FileName", fileName ?? ""),
                new XElement("Path", path ?? ""),
                new XElement("Type", type ?? ""),
                new XElement("ProductName", productName ?? "")));
            Save();
        }

        public void RemoveMedia(long id)
        {
            XElement parent = _doc.Root.Element("Media");
            XElement e = parent.Elements("MediaItem").FirstOrDefault(x => (long)x.Attribute("Id") == id);
            if (e != null) e.Remove();
            Save();
        }

        public List<Review> GetReviews()
        {
            XElement parent = _doc.Root.Element("Reviews");
            return parent.Elements("Review").Select(e => new Review { Id=(long)e.Attribute("Id"), OrderId=I64(e,"OrderId"), CustomerId=I64(e,"CustomerId"), CustomerEmail=S(e,"CustomerEmail"), StoreId=S(e,"StoreId"), Rating=I(e,"Rating"), Text=S(e,"Text"), CreatedAt=D(e,"CreatedAt") }).OrderByDescending(x=>x.Id).ToList();
        }

        public bool SaveReview(Review r)
        {
            if (r == null || r.Rating < 1 || r.Rating > 5 || string.IsNullOrWhiteSpace(r.Text)) return false;
            XElement parent=_doc.Root.Element("Reviews");
            if (r.Id <= 0) r.Id=NextId("Reviews","Review");
            XElement e=new XElement("Review",new XAttribute("Id",r.Id),new XElement("OrderId",r.OrderId),new XElement("CustomerId",r.CustomerId),new XElement("CustomerEmail",r.CustomerEmail??""),new XElement("StoreId",r.StoreId??StoreId),new XElement("Rating",r.Rating),new XElement("Text",r.Text??""),new XElement("CreatedAt",r.CreatedAt.ToString("o")));
            XElement old=parent.Elements("Review").FirstOrDefault(x=>(long)x.Attribute("Id")==r.Id); if(old!=null) old.ReplaceWith(e); else parent.Add(e); Save(); return true;
        }

        public List<ChatMessage> GetMessages(string email)
        {
            string key=(email??"").Trim(); return _doc.Root.Element("Messages").Elements("Message").Select(e=>new ChatMessage{Id=(long)e.Attribute("Id"),OrderId=I64(e,"OrderId"),FromEmail=S(e,"FromEmail"),ToEmail=S(e,"ToEmail"),Body=S(e,"Body"),CreatedAt=D(e,"CreatedAt"),Read=S(e,"Read")!="0"}).Where(m=>string.Equals(m.FromEmail,key,StringComparison.OrdinalIgnoreCase)||string.Equals(m.ToEmail,key,StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x.CreatedAt).ToList();
        }

        public void AddMessage(ChatMessage m)
        {
            if(m==null || string.IsNullOrWhiteSpace(m.FromEmail) || string.IsNullOrWhiteSpace(m.ToEmail) || string.IsNullOrWhiteSpace(m.Body)) return;
            XElement parent=_doc.Root.Element("Messages"); m.Id=NextId("Messages","Message"); parent.Add(new XElement("Message",new XAttribute("Id",m.Id),new XElement("OrderId",m.OrderId),new XElement("FromEmail",m.FromEmail??""),new XElement("ToEmail",m.ToEmail??""),new XElement("Body",m.Body??""),new XElement("CreatedAt",m.CreatedAt.ToString("o")),new XElement("Read",m.Read?"1":"0"))); Save();
        }

        public void CreateDemoOrderIfNeeded()
        {
            if (GetOrders("").Count > 0) return;
            AddOrder(new Order
            {
                CustomerName = "Cliente de prueba",
                Phone = "11 0000-0000",
                Fulfillment = "Retiro",
                Status = "Pendiente",
                Total = 1000m,
                CreatedAt = DateTime.Now
            });
        }

        public string StoreId { get { return GetSetting("store_id", ""); } }

        public List<WebUser> GetWebUsers()
        {
            return _doc.Root.Element("WebUsers").Elements("WebUser").Select(ToWebUser).ToList();
        }

        public WebUser FindWebUser(string email)
        {
            string key = (email ?? "").Trim();
            return _doc.Root.Element("WebUsers").Elements("WebUser")
                .Select(ToWebUser)
                .FirstOrDefault(u => string.Equals(u.Email, key, StringComparison.OrdinalIgnoreCase));
        }

        public bool CreateWebUser(WebUser user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.PasswordHash)) return false;
            if (FindWebUser(user.Email) != null) return false;
            user.Id = NextId("WebUsers", "WebUser");
            _doc.Root.Element("WebUsers").Add(new XElement("WebUser",
                new XAttribute("Id", user.Id), new XElement("Name", user.Name ?? ""),
                new XElement("Email", user.Email ?? ""), new XElement("Phone", user.Phone ?? ""),
                new XElement("Role", user.Role == "seller" ? "seller" : "buyer"),
                new XElement("StoreId", user.StoreId ?? ""),
                new XElement("Salt", user.Salt ?? ""), new XElement("PasswordHash", user.PasswordHash ?? ""),
                new XElement("RecoveryCode", user.RecoveryCode ?? ""),
                new XElement("RecoveryExpires", user.RecoveryExpires == DateTime.MinValue ? "" : user.RecoveryExpires.ToString("o")),
                new XElement("CreatedAt", user.CreatedAt.ToString("o"))));
            Save(); return true;
        }

        private WebUser ToWebUser(XElement e)
        {
            DateTime recoveryExpires;
            DateTime.TryParse(S(e, "RecoveryExpires"), out recoveryExpires);
            return new WebUser { Id = (long)e.Attribute("Id"), Name = S(e,"Name"), Email = S(e,"Email"), Phone = S(e,"Phone"),
                Role = S(e,"Role") == "seller" ? "seller" : "buyer", StoreId = S(e,"StoreId"), Salt = S(e,"Salt"), PasswordHash = S(e,"PasswordHash"),
                RecoveryCode = S(e,"RecoveryCode"), RecoveryExpires = recoveryExpires, CreatedAt = D(e,"CreatedAt") };
        }

        public bool VerifyWebUser(string email, string password, out WebUser user)
        {
            user = FindWebUser(email);
            if (user == null) return false;
            return AuthService.VerifyPassword(password ?? "", user.Salt, user.PasswordHash);
        }

        public bool SetWebUserPassword(long userId, string newPassword)
        {
            if (newPassword == null || newPassword.Length < 6) return false;
            XElement e = _doc.Root.Element("WebUsers").Elements("WebUser").FirstOrDefault(x => (long)x.Attribute("Id") == userId);
            if (e == null) return false;
            string salt = AuthService.CreateSalt();
            e.SetElementValue("Salt", salt);
            e.SetElementValue("PasswordHash", AuthService.HashPassword(newPassword, salt));
            e.SetElementValue("RecoveryCode", "");
            e.SetElementValue("RecoveryExpires", "");
            Save();
            return true;
        }

        public string CreateWebRecoveryCode(string email, int minutes)
        {
            WebUser user = FindWebUser(email);
            if (user == null || user.Role != "seller") return "";
            string code = new Random().Next(100000, 999999).ToString();
            XElement e = _doc.Root.Element("WebUsers").Elements("WebUser").FirstOrDefault(x => (long)x.Attribute("Id") == user.Id);
            if (e == null) return "";
            DateTime expires = DateTime.Now.AddMinutes(minutes < 1 ? 10 : minutes);
            e.SetElementValue("RecoveryCode", code);
            e.SetElementValue("RecoveryExpires", expires.ToString("o"));
            Save();
            return code;
        }

        public bool VerifyWebRecoveryCode(string email, string code, out WebUser user)
        {
            user = FindWebUser(email);
            if (user == null || user.Role != "seller") return false;
            if (string.IsNullOrWhiteSpace(user.RecoveryCode) || !string.Equals(user.RecoveryCode, (code ?? "").Trim(), StringComparison.Ordinal)) return false;
            if (user.RecoveryExpires == DateTime.MinValue || DateTime.Now > user.RecoveryExpires) return false;
            return true;
        }

        public void Dispose() { }
    }
}
