using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using NexoMarket.Admin.Data;
using NexoMarket.Admin.Models;
using NexoMarket.Admin.UI;

namespace NexoMarket.Admin
{
    public sealed class MainForm : Form
    {
        private readonly AppDataStore _store;
        private Panel _sidebar;
        private Panel _content;
        private FlowLayoutPanel _navPanel;
        private Panel _mainHost;
        private Button _selectedNav;
        private Label _title;
        private Label _subtitle;
        private string _currentPage = "Inicio";
        private readonly List<CartLine> _cart = new List<CartLine>();
        private DataGridView _ticketGrid;
        private Label _ticketTotal;
        private PrintDocument _printDocument;
        private string _ticketHeader;
        private string _ticketFooter;
        private string _ticketPaymentMethod = "Efectivo";
        private decimal _ticketReceived;
        private readonly List<CartLine> _lastCompletedCart = new List<CartLine>();
        private AndroidBridgeService _androidBridge;
        private Label _androidStatusLabel;
        private Action<string> _androidBarcodeHandler;
        private LocalScannerServer _localScannerServer;
        private WebServerService _webServer;
        private CentralSyncService _centralSync;

        private sealed class CartLine
        {
            public Product Product;
            public int Quantity;
            public decimal UnitPrice { get { return Product.SalePrice > 0 ? Product.SalePrice : Product.Price; } }
            public decimal Total { get { return UnitPrice * Quantity; } }
        }

        public MainForm(AppDataStore store)
        {
            _store = store;
            Text = "NexoMarket · Administrador";
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(1050, 700);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            KeyPreview = true;
            _ticketHeader = _store.GetSetting("ticket_header", "NexoMarket");
            _ticketFooter = _store.GetSetting("ticket_footer", "Gracias por su compra");
            BuildShell();
            _androidBridge = new AndroidBridgeService(HandleAndroidBarcode, HandleAndroidStatus);
            _androidBridge.Start();
            _localScannerServer = new LocalScannerServer(HandleAndroidBarcode);
            _localScannerServer.Start();
            int webPort;
            if (!int.TryParse(_store.GetSetting("web_server_port", "8090"), out webPort)) webPort = 8090;
            _webServer = new WebServerService(_store, webPort);
            _webServer.Start();
            _centralSync = new CentralSyncService(_store);
            _centralSync.DataChanged += HandleCentralDataChanged;
            _centralSync.Start();
            ShowPage("Inicio", BuildDashboard);
        }

        private void HandleCentralDataChanged()
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) { BeginInvoke(new Action(HandleCentralDataChanged)); return; }
                switch (_currentPage)
                {
                    case "Productos": ShowPage("Productos", BuildProducts); break;
                    case "Inventario": ShowPage("Inventario", BuildInventory); break;
                    case "Pedidos nuevos": ShowPage("Pedidos nuevos", BuildOrders); break;
                    case "Delivery": ShowPage("Delivery", BuildDelivery); break;
                    case "Ventas": ShowPage("Ventas", BuildSalesHistory); break;
                    case "Clientes": ShowPage("Clientes", BuildCustomers); break;
                    case "Promociones": ShowPage("Promociones", BuildPromotions); break;
                    case "Cupones": ShowPage("Cupones", BuildCoupons); break;
                    case "Estadísticas": ShowPage("Estadísticas", BuildStats); break;
                    case "Inicio": ShowPage("Inicio", BuildDashboard); break;
                }
            }
            catch { }
        }

        private void BuildShell()
        {
            // Estructura estable: barra lateral + host principal. Cada página vive
            // exclusivamente dentro de _content para evitar solapamientos por Z-Order.
            _sidebar = new TexturedPanel
            {
                Dock = DockStyle.Left,
                Width = 238,
                BackColor = Color.Transparent,
                Padding = new Padding(12, 18, 12, 10),
                TextureOpacity = 78
            };

            TableLayoutPanel sidebarLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112f));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
            _sidebar.Controls.Add(sidebarLayout);

            Panel sidebarHeader = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(205, Theme.Sidebar), Padding = new Padding(10, 8, 10, 8) };
            TableLayoutPanel brandLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            brandLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            brandLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            brandLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Label brandNexo = new Label { Text = "NEXO", AutoSize = false, Dock = DockStyle.Fill, Font = Theme.Font(23, FontStyle.Bold), ForeColor = Theme.NeonGreen, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
            Label brandMarket = new Label { Text = "MARKET", AutoSize = false, Dock = DockStyle.Fill, Font = Theme.Font(18, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
            Label tag = new Label { Text = "ADMINISTRADOR", AutoSize = false, Dock = DockStyle.Fill, Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.Green, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            brandLayout.Controls.Add(brandNexo, 0, 0);
            brandLayout.Controls.Add(brandMarket, 0, 1);
            brandLayout.Controls.Add(tag, 0, 2);
            sidebarHeader.Controls.Add(brandLayout);
            sidebarLayout.Controls.Add(sidebarHeader, 0, 0);

            _navPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = Color.Transparent, AutoScroll = true,
                Padding = new Padding(0, 8, 0, 8), FlowDirection = FlowDirection.TopDown, WrapContents = false
            };
            sidebarLayout.Controls.Add(_navPanel, 0, 1);

            AddNavGroup("COMERCIO");
            AddNav("⌂   Inicio", "Inicio", BuildDashboard);
            AddNav("▣   Punto de venta", "Punto de venta", BuildPOS);
            AddNav("▥   Pedidos", "Pedidos nuevos", BuildOrders);
            AddNav("➜   Delivery", "Delivery", BuildDelivery);
            AddNav("▤   Ventas", "Ventas", BuildSalesHistory);
            AddNavGroup("CATÁLOGO");
            AddNav("▤   Productos", "Productos", BuildProducts);
            AddNav("▥   Inventario", "Inventario", BuildInventory);
            AddNav("★   Promociones", "Promociones", BuildPromotions);
            AddNav("🎟   Cupones", "Cupones", BuildCoupons);
            AddNav("▧   Multimedia", "Multimedia", BuildMedia);
            AddNavGroup("CLIENTES Y FINANZAS");
            AddNav("♙   Clientes", "Clientes", BuildCustomers);
            AddNav("▣   Caja / arqueo", "Caja / arqueo", BuildCashRegister);
            AddNav("◒   Analítica", "Estadísticas", BuildStats);
            AddNavShortcut("F12", "💳   COBRAR", OpenPaymentCenter);
            AddNavGroup("CANALES");
            AddNav("🌐   Tienda online", "Configuración", BuildSettings);
            AddNavAction("🌐   Servidor web", "Servidor web", OpenWebServer);
            AddNavAction("👤   Cuenta vendedor", "Cuenta vendedor", OpenSellerAccount);
            AddNav("📱  Android", "Android", BuildAndroid);
            AddNavGroup("SISTEMA");
            AddNav("⚙   Configuración", "Configuración", BuildSettings);

            Panel sidebarFooter = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
            Button logout = Theme.NavButton("⏻   Cerrar sesión");
            logout.Dock = DockStyle.Fill; logout.Width = 205;
            logout.Click += delegate { Close(); };
            sidebarFooter.Controls.Add(logout);
            sidebarLayout.Controls.Add(sidebarFooter, 0, 2);

            _mainHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Padding = new Padding(0)
            };

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Theme.Background, Margin = new Padding(0), Padding = new Padding(0)
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _mainHost.Controls.Add(mainLayout);

            Panel top = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Background,
                Padding = new Padding(28, 10, 28, 8)
            };
            _title = new Label
            {
                Text = "Inicio", AutoSize = true, Font = Theme.Font(20, FontStyle.Bold),
                ForeColor = Theme.Text, Location = new Point(28, 13)
            };
            top.Controls.Add(_title);
            _subtitle = new Label
            {
                Text = "Resumen general de tu tienda", AutoSize = true, Font = Theme.Font(9, FontStyle.Regular),
                ForeColor = Theme.Muted, Location = new Point(29, 49)
            };
            top.Controls.Add(_subtitle);
            Panel accentLine = new Panel { Height = 2, Dock = DockStyle.Bottom, BackColor = Theme.Accent };
            top.Controls.Add(accentLine);
            Label store = new Label
            {
                Text = _store.GetSetting("store_name", "NexoMarket") + "  ·  ADMINISTRADOR", AutoSize = false, Width = 270, Height = 24,
                Font = Theme.Font(8.5f, FontStyle.Bold), ForeColor = Theme.NeonGreen, TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            top.Controls.Add(store);

            TextBox globalSearch = new TextBox
            {
                Width = 250, Height = 30, BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Font(9, FontStyle.Regular), Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            globalSearch.PlaceholderTextSafe("Buscar producto, SKU, cliente o pedido...");
            top.Controls.Add(globalSearch);
            Button searchButton = Theme.Secondary("BUSCAR"); searchButton.Width = 82; searchButton.Height = 30; searchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            searchButton.Click += delegate
            {
                string q = globalSearch.Text.Trim();
                if (string.IsNullOrWhiteSpace(q)) return;
                Product prod = _store.GetProducts("").FirstOrDefault(x => string.Equals(x.SKU, q, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Barcode, q, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, q, StringComparison.OrdinalIgnoreCase));
                if (prod != null) { ShowPage("Productos", BuildProducts); return; }
                Customer customer = _store.GetCustomers("").FirstOrDefault(x => string.Equals(x.Name, q, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Phone, q, StringComparison.OrdinalIgnoreCase));
                if (customer != null) { ShowPage("Clientes", BuildCustomers); return; }
                Order order = _store.GetOrders("").FirstOrDefault(x => x.Id.ToString() == q || string.Equals(x.CustomerName, q, StringComparison.OrdinalIgnoreCase));
                if (order != null) { ShowPage("Pedidos nuevos", BuildOrders); return; }
                MessageBox.Show("No se encontró una coincidencia para: " + q, "NexoMarket · Búsqueda", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            top.Controls.Add(searchButton);
            top.Resize += delegate
            {
                store.Left = Math.Max(500, top.ClientSize.Width - store.Width - 28);
                store.Top = 10;
                searchButton.Left = Math.Max(300, store.Left - searchButton.Width - 10);
                searchButton.Top = 8;
                globalSearch.Left = Math.Max(300, searchButton.Left - globalSearch.Width - 8);
                globalSearch.Top = 8;
            };

            _content = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Background,
                Padding = new Padding(18, 8, 24, 18),
                Margin = new Padding(0)
            };
            _content.Resize += delegate { ResizeCurrentPage(); };
            mainLayout.Controls.Add(top, 0, 0);
            mainLayout.Controls.Add(_content, 0, 1);

            // El host principal se agrega primero y la barra lateral después para que
            // Dock=Left reserve su ancho sin tapar el área de contenido.
            Controls.Add(_mainHost);
            Controls.Add(_sidebar);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            switch (e.KeyCode)
            {
                case Keys.F1: ShowPage("Inicio", BuildDashboard); e.Handled = true; break;
                case Keys.F2: if (string.Equals(_currentPage, "Punto de venta", StringComparison.OrdinalIgnoreCase)) OpenPaymentCenter(); else ShowPage("Punto de venta", BuildPOS); e.Handled = true; break;
                case Keys.F3: ShowPage("Pedidos nuevos", BuildOrders); e.Handled = true; break;
                case Keys.F4: ShowPage("Delivery", BuildDelivery); e.Handled = true; break;
                case Keys.F5: ShowPage("Ventas", BuildSalesHistory); e.Handled = true; break;
                case Keys.F6: ShowPage("Productos", BuildProducts); e.Handled = true; break;
                case Keys.F7: ShowPage("Inventario", BuildInventory); e.Handled = true; break;
                case Keys.F8: ShowPage("Promociones", BuildPromotions); e.Handled = true; break;
                case Keys.F9: ShowPage("Multimedia", BuildMedia); e.Handled = true; break;
                case Keys.F10: ShowPage("Clientes", BuildCustomers); e.Handled = true; break;
                case Keys.F11: ShowPage("Caja / arqueo", BuildCashRegister); e.Handled = true; break;
                case Keys.F12: OpenPaymentCenter(); e.Handled = true; break;
            }
        }

        private void AddNavShortcut(string shortcut, string text, Action action)
        {
            Button b = Theme.NavButton(text);
            ModernButton mb = b as ModernButton;
            if (mb != null) mb.ShortcutText = shortcut;
            b.Width = 205; b.Height = 42; b.Margin = new Padding(0, 1, 0, 1);
            b.Click += delegate { action(); };
            _navPanel.Controls.Add(b);
        }

        private void AddNavGroup(string text)
        {
            Label label = new Label
            {
                Text = text, AutoSize = false, Width = 205, Height = 24,
                ForeColor = Theme.Muted, Font = Theme.Font(7.5f, FontStyle.Bold),
                Padding = new Padding(12, 8, 0, 0), Margin = new Padding(0, 8, 0, 1)
            };
            _navPanel.Controls.Add(label);
        }

        private void OpenSellerAccount()
        {
            using (SellerAccountForm form = new SellerAccountForm(_store))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    MessageBox.Show("Cuenta de vendedor guardada y vinculada a esta tienda. La sincronización central se ejecutará automáticamente.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void AddNavAction(string text, string title, Action action)
        {
            Button b = Theme.NavButton(text);
            b.Tag = title;
            ModernButton shortcutButton = b as ModernButton;
            if (shortcutButton != null)
            {
                if (title == "Servidor web") shortcutButton.ShortcutText = "";
            }
            b.Dock = DockStyle.None;
            b.Width = 205;
            b.Height = 42;
            b.Margin = new Padding(0, 1, 0, 1);
            b.Click += delegate
            {
                if (_selectedNav != null)
                {
                    _selectedNav.BackColor = Theme.Sidebar;
                    _selectedNav.ForeColor = Theme.Muted;
                }
                _selectedNav = b;
                b.BackColor = Theme.Accent;
                b.ForeColor = Color.White;
                action();
            };
            _navPanel.Controls.Add(b);
        }

        private void AddNav(string text, string title, Func<Control> builder)
        {
            Button b = Theme.NavButton(text);
            b.Tag = title;
            ModernButton shortcutButton = b as ModernButton;
            if (shortcutButton != null)
            {
                switch (title)
                {
                    case "Inicio": shortcutButton.ShortcutText = "F1"; break;
                    case "Punto de venta": shortcutButton.ShortcutText = "F2"; break;
                    case "Pedidos nuevos": shortcutButton.ShortcutText = "F3"; break;
                    case "Delivery": shortcutButton.ShortcutText = "F4"; break;
                    case "Ventas": shortcutButton.ShortcutText = "F5"; break;
                    case "Productos": shortcutButton.ShortcutText = "F6"; break;
                    case "Inventario": shortcutButton.ShortcutText = "F7"; break;
                    case "Promociones": shortcutButton.ShortcutText = "F8"; break;
                    case "Multimedia": shortcutButton.ShortcutText = "F9"; break;
                    case "Clientes": shortcutButton.ShortcutText = "F10"; break;
                    case "Caja / arqueo": shortcutButton.ShortcutText = "F11"; break;
                }
            }
            b.Dock = DockStyle.None;
            b.Width = 205;
            b.Height = 42;
            b.Margin = new Padding(0, 1, 0, 1);
            b.Click += delegate
            {
                if (_selectedNav != null)
                {
                    _selectedNav.BackColor = Theme.Sidebar;
                    _selectedNav.ForeColor = Theme.Muted;
                }
                _selectedNav = b;
                b.BackColor = Theme.Accent;
                b.ForeColor = Color.White;
                ShowPage(title, builder);
            };
            _navPanel.Controls.Add(b);
        }

        private void ShowPage(string title, Func<Control> builder)
        {
            _androidBarcodeHandler = null;
            _currentPage = title;
            foreach (Control control in _navPanel.Controls)
            {
                Button nav = control as Button;
                if (nav != null && string.Equals(Convert.ToString(nav.Tag), title, StringComparison.OrdinalIgnoreCase))
                {
                    if (_selectedNav != null && _selectedNav != nav)
                    {
                        _selectedNav.BackColor = Theme.Sidebar;
                        _selectedNav.ForeColor = Theme.Muted;
                    }
                    _selectedNav = nav;
                    nav.BackColor = Theme.Accent;
                    nav.ForeColor = Color.White;
                    break;
                }
            }
            _title.Text = title;
            _subtitle.Text = PageSubtitle(title);
            _content.SuspendLayout();
            _content.Controls.Clear();
            Control page = builder();
            SizePage(page);
            _content.Controls.Add(page);
            _content.ResumeLayout(true);
            ResizeCurrentPage();
            page.PerformLayout();
            UpdatePageScroll(page as Panel);
            _content.PerformLayout();
        }

        private string PageSubtitle(string title)
        {
            switch (title)
            {
                case "Inicio": return "Centro de comercio · operaciones, catálogo, pedidos y rendimiento";
                case "Punto de venta": return "Ticket directo, código de barras, cobro e impresión";
                case "Caja / arqueo": return "Ventas por medio de pago, efectivo esperado y cierre de caja";
                case "Productos": return "Catálogo, códigos de barras, precios, stock y multimedia";
                case "Promociones": return "Combos y precios especiales seleccionando productos del inventario";
                case "Pedidos nuevos": return "Pedidos online y de mostrador pendientes de preparación";
                case "Ventas": return "Historial completo de ventas y comprobantes";
                case "Inventario": return "Stock actual, alertas y reposición";
                case "Clientes": return "Base de clientes, compras e historial comercial";
                case "Multimedia": return "Fotos, videos y cámara local del equipo";
                case "Estadísticas": return "Ventas, pedidos, stock y distribución comercial";
                case "Android": return "Conectá un teléfono Android por USB o Bluetooth y usalo como escáner";
                case "Delivery": return "Pedidos pendientes de entrega";
                case "Configuración": return "Datos, operación, ticket, ARCA, web, Android y seguridad";
                default: return "Resumen general de tu tienda";
            }
        }

        private Panel Page()
        {
            Panel p = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                AutoScroll = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0, 4, 0, 24)
            };
            p.Resize += delegate { UpdatePageScroll(p); };
            return p;
        }

        private void SizePage(Control page)
        {
            page.Dock = DockStyle.Top;
            page.Margin = new Padding(0);
            page.MinimumSize = new Size(0, 0);
            page.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            ResizePage(page as Panel);
        }

        private void ResizeCurrentPage()
        {
            if (_content == null || _content.IsDisposed || _content.Controls.Count == 0) return;
            ResizePage(_content.Controls[0] as Panel);
        }

        private void ResizePage(Panel page)
        {
            if (page == null || page.IsDisposed || _content == null || _content.IsDisposed) return;
            page.AutoScroll = false;

            int bottom = 24;
            foreach (Control c in page.Controls)
            {
                if (c == null || !c.Visible) continue;
                bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom + 12);
            }
            page.Height = Math.Max(_content.ClientSize.Height - _content.Padding.Vertical, bottom);

            int availableWidth = _content.ClientSize.Width - _content.Padding.Horizontal - 4;
            if (page.Height > _content.ClientSize.Height) availableWidth -= SystemInformation.VerticalScrollBarWidth;
            page.Width = Math.Max(300, availableWidth);

            // Una sola barra de desplazamiento para toda la sección.
            _content.AutoScrollMinSize = new Size(0, page.Height + _content.Padding.Vertical + 4);
        }

        private void UpdatePageScroll(Panel page)
        {
            if (page == null || page.IsDisposed || _content == null || _content.IsDisposed) return;
            page.AutoScroll = false;
            int bottom = 24;
            foreach (Control c in page.Controls)
            {
                if (c == null || !c.Visible) continue;
                bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom + 12);
            }
            if (bottom > page.Height) page.Height = bottom;
            int availableWidth = _content.ClientSize.Width - _content.Padding.Horizontal - 4;
            if (page.Height > _content.ClientSize.Height) availableWidth -= SystemInformation.VerticalScrollBarWidth;
            page.Width = Math.Max(300, availableWidth);
            _content.AutoScrollMinSize = new Size(0, page.Height + _content.Padding.Vertical + 4);
        }

        private Label H2(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = Theme.Font(14, FontStyle.Bold), ForeColor = Theme.Text };
        }

        private Button Primary(string text, EventHandler click)
        {
            Button b = Theme.Primary(text); b.Click += click; return b;
        }

        private Panel Section(string title, int height)
        {
            Panel p = Theme.Card(); p.Height = height; p.Dock = DockStyle.Top;
            Label h = H2(title); h.Location = new Point(18, 16); p.Controls.Add(h); return p;
        }

        private Control BuildDashboard()
        {
            DashboardData d = _store.GetDashboard();
            List<Order> orders = _store.GetOrders("");
            List<Product> products = _store.GetProducts("");
            List<Customer> customers = _store.GetCustomers("");
            DateTime today = DateTime.Today;
            List<Order> todayOrders = orders.Where(o => o.CreatedAt.ToLocalTime().Date == today && o.Status != "Cancelado").ToList();
            decimal todaySales = todayOrders.Sum(o => o.Total);
            decimal yesterdaySales = orders.Where(o => o.CreatedAt.ToLocalTime().Date == today.AddDays(-1) && o.Status != "Cancelado").Sum(o => o.Total);
            decimal growth = yesterdaySales <= 0 ? 0 : ((todaySales - yesterdaySales) / yesterdaySales) * 100m;
            int pendingDelivery = orders.Count(o => o.Fulfillment == "Delivery" && o.Status != "Entregado" && o.Status != "Cancelado");
            int activeProducts = products.Count(p => p.Active);
            int lowStock = products.Count(p => p.Stock <= p.MinimumStock);
            decimal averageTicket = todayOrders.Count == 0 ? 0 : todaySales / todayOrders.Count;

            Panel page = Page();
            page.Padding = new Padding(0, 4, 0, 28);

            Panel hero = Theme.HeroCard();
            hero.Dock = DockStyle.Top;
            hero.Height = 112;
            Label eyebrow = new Label { Text = "CENTRO DE COMERCIO · TIENDA", AutoSize = true, ForeColor = Theme.Accent, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(20, 14) };
            Label greeting = new Label { Text = "Tu negocio, en una sola consola", AutoSize = false, Width = 650, Height = 32, ForeColor = Theme.Text, Font = Theme.Font(18, FontStyle.Bold), Location = new Point(20, 34) };
            Label hint = new Label { Text = "Ventas, pedidos, catálogo, stock y clientes con lectura inmediata y sin elementos superpuestos.", AutoSize = false, Width = 760, Height = 25, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular), Location = new Point(20, 72) };
            hero.Controls.Add(hint); hero.Controls.Add(greeting); hero.Controls.Add(eyebrow);
            Label storeBadge = new Label { Text = GetStoreIdShort(), AutoSize = false, Width = 170, Height = 32, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Green, BackColor = Theme.Card2, Font = Theme.Font(8, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            hero.Controls.Add(storeBadge);
            hero.Resize += delegate { storeBadge.Left = Math.Max(400, hero.ClientSize.Width - storeBadge.Width - 20); storeBadge.Top = 38; };
            page.Controls.Add(hero);

            TableLayoutPanel kpis = new TableLayoutPanel { Dock = DockStyle.Top, Height = 116, ColumnCount = 5, RowCount = 1, Padding = new Padding(0, 10, 0, 8) };
            for (int i = 0; i < 5; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            AddExecutiveKpi(kpis, 0, "VENTAS HOY", todaySales.ToString("C0"), growth >= 0 ? "↑ " + growth.ToString("0.0") + "%" : "↓ " + Math.Abs(growth).ToString("0.0") + "%", growth >= 0 ? Theme.Green : Theme.Danger);
            AddExecutiveKpi(kpis, 1, "PEDIDOS", todayOrders.Count.ToString(), d.NewOrders + " nuevos", Theme.Accent);
            AddExecutiveKpi(kpis, 2, "TICKET PROMEDIO", averageTicket.ToString("C0"), "hoy", Theme.Green);
            AddExecutiveKpi(kpis, 3, "DELIVERY", pendingDelivery.ToString(), "pendientes", pendingDelivery > 0 ? Theme.Warning : Theme.Green);
            AddExecutiveKpi(kpis, 4, "STOCK CRÍTICO", lowStock.ToString(), activeProducts + " activos", lowStock > 0 ? Theme.Danger : Theme.Green);
            page.Controls.Add(kpis);

            TableLayoutPanel command = new TableLayoutPanel { Dock = DockStyle.Top, Height = 72, ColumnCount = 6, RowCount = 1, Padding = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 6; i++) command.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666f));
            AddCommand(command, 0, "+ PRODUCTO", delegate { using (Form f = ProductDialog(null)) if (f.ShowDialog(this) == DialogResult.OK) { _store.SaveProduct((Product)f.Tag); try { _centralSync.SyncOnce(); } catch { } ShowPage("Productos", BuildProducts); } });
            AddCommand(command, 1, "NUEVO PEDIDO", delegate { ShowPage("Pedidos nuevos", BuildOrders); });
            AddCommand(command, 2, "ABRIR CAJA", delegate { ShowPage("Caja / arqueo", BuildCashRegister); });
            AddCommand(command, 3, "VER DELIVERY", delegate { ShowPage("Delivery", BuildDelivery); });
            AddCommand(command, 4, "INVENTARIO", delegate { ShowPage("Inventario", BuildInventory); });
            AddCommand(command, 5, "ANALÍTICA", delegate { ShowPage("Estadísticas", BuildStats); });
            page.Controls.Add(command);

            TableLayoutPanel main = new TableLayoutPanel { Dock = DockStyle.Top, Height = 408, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 0, 0, 10) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52f));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24f));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24f));

            Panel recent = Theme.Card(); recent.Dock = DockStyle.Fill; recent.Padding = new Padding(14);
            Label rh = H2("Pedidos recientes"); rh.Dock = DockStyle.Top; rh.Height = 34;
            DataGridView rg = Theme.Grid(); rg.Dock = DockStyle.Fill; rg.DataSource = orders.OrderByDescending(o => o.CreatedAt).Take(8).Select(OrderRow).ToList(); ConfigureOrderGrid(rg); rg.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0) return; long id = Convert.ToInt64(rg.Rows[e.RowIndex].Cells["Id"].Value); Order o = orders.FirstOrDefault(x => x.Id == id); if (o != null) using (Form f = OrderDialog(o)) if (f.ShowDialog(this) == DialogResult.OK) ShowPage(_currentPage, BuildDashboard); };
            recent.Controls.Add(rg); recent.Controls.Add(rh); main.Controls.Add(recent, 0, 0);

            Panel funnel = Theme.Card(); funnel.Dock = DockStyle.Fill; funnel.Padding = new Padding(14);
            Label fh = H2("Flujo operativo"); fh.Dock = DockStyle.Top; fh.Height = 34; funnel.Controls.Add(fh);
            TableLayoutPanel flow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(0, 5, 0, 5) };
            for (int i = 0; i < 5; i++) flow.RowStyles.Add(new RowStyle(SizeType.Percent, 20f));
            AddFlowRow(flow, 0, "Pendientes", orders.Count(o => o.Status == "Pendiente"), Theme.Warning);
            AddFlowRow(flow, 1, "Preparando", orders.Count(o => o.Status == "Preparando"), Theme.Warning);
            AddFlowRow(flow, 2, "Listos", orders.Count(o => o.Status == "Listo"), Theme.Green);
            AddFlowRow(flow, 3, "Enviados", orders.Count(o => o.Status == "Enviado" || o.Status == "En reparto"), Theme.Green);
            AddFlowRow(flow, 4, "Rechazados", orders.Count(o => o.Status == "Rechazado" || o.Status == "Cancelado"), Theme.Danger);
            funnel.Controls.Add(flow); main.Controls.Add(funnel, 1, 0);

            Panel insights = Theme.Card(); insights.Dock = DockStyle.Fill; insights.Padding = new Padding(14);
            Label ih = H2("Oportunidades"); ih.Dock = DockStyle.Top; ih.Height = 34; insights.Controls.Add(ih);
            FlowLayoutPanel insightList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 4, 0, 4) };
            AddInsight(insightList, "Stock crítico", lowStock + " productos requieren atención", lowStock > 0 ? Theme.Danger : Theme.Green);
            AddInsight(insightList, "Delivery", pendingDelivery + " pedidos pendientes de entrega", pendingDelivery > 0 ? Theme.Warning : Theme.Green);
            Product best = products.OrderByDescending(p => p.Stock <= p.MinimumStock ? 0 : p.Stock).FirstOrDefault();
            AddInsight(insightList, "Catálogo", activeProducts + " productos activos", Theme.Accent);
            AddInsight(insightList, "Clientes", customers.Count + " clientes registrados", Theme.Accent);
            if (best != null) AddInsight(insightList, "Producto", best.Name, Theme.Green);
            insights.Controls.Add(insightList); main.Controls.Add(insights, 2, 0);
            page.Controls.Add(main);

            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Top, Height = 300, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 0, 0, 10) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));

            Panel sales = Theme.Card(); sales.Dock = DockStyle.Fill; sales.Padding = new Padding(14);
            Label sh = H2("Rendimiento comercial"); sh.Dock = DockStyle.Top; sh.Height = 34; sales.Controls.Add(sh);
            TableLayoutPanel salesRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(0, 5, 0, 5) };
            salesRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f)); salesRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
            AddReportRow(salesRows, 0, "Ventas del día", todaySales.ToString("C0"), Theme.Green);
            AddReportRow(salesRows, 1, "Ventas de ayer", yesterdaySales.ToString("C0"), Theme.Muted);
            AddReportRow(salesRows, 2, "Ticket promedio", averageTicket.ToString("C0"), Theme.Accent);
            AddReportRow(salesRows, 3, "Pedidos hoy", todayOrders.Count.ToString(), Theme.Accent);
            AddReportRow(salesRows, 4, "Clientes registrados", customers.Count.ToString(), Theme.Warning);
            sales.Controls.Add(salesRows); bottom.Controls.Add(sales, 0, 0);

            Panel topProducts = Theme.Card(); topProducts.Dock = DockStyle.Fill; topProducts.Padding = new Padding(14);
            Label tph = H2("Catálogo destacado"); tph.Dock = DockStyle.Top; tph.Height = 34; topProducts.Controls.Add(tph);
            DataGridView pg = Theme.Grid(); pg.Dock = DockStyle.Fill;
            pg.DataSource = products.Where(p => p.Active).OrderByDescending(p => p.SalePrice > 0 ? p.SalePrice : p.Price).Take(6).Select(p => new { Producto = p.Name, SKU = p.SKU, Stock = p.Stock, Precio = (p.SalePrice > 0 ? p.SalePrice : p.Price).ToString("C0") }).ToList();
            topProducts.Controls.Add(pg); bottom.Controls.Add(topProducts, 1, 0);
            page.Controls.Add(bottom);
            return page;
        }

        private string GetStoreIdShort()
        {
            string id = _store.GetSetting("store_id", "");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N").ToUpperInvariant();
                _store.SetSetting("store_id", id);
            }
            return "STORE ID · " + id.Substring(0, Math.Min(10, id.Length));
        }

        private void AddExecutiveKpi(TableLayoutPanel host, int column, string title, string value, string meta, Color accent)
        {
            Panel card = Theme.Card(); card.Dock = DockStyle.Fill; card.Margin = new Padding(4, 0, 4, 0); card.Padding = new Padding(14, 10, 12, 8);
            Label t = new Label { Text = title, AutoSize = false, Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Font(7.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            Label v = new Label { Text = value, AutoSize = false, Dock = DockStyle.Fill, Height = 38, ForeColor = accent, Font = Theme.Font(16, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            Label m = new Label { Text = meta, AutoSize = false, Dock = DockStyle.Bottom, Height = 18, ForeColor = Theme.Muted, Font = Theme.Font(7.5f, FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft };
            card.Controls.Add(m); card.Controls.Add(v); card.Controls.Add(t); host.Controls.Add(card, column, 0);
        }

        private void AddCommand(TableLayoutPanel host, int column, string text, EventHandler click)
        {
            Button b = Theme.Secondary(text); b.Dock = DockStyle.Fill; b.Margin = new Padding(4, 0, 4, 0); b.Height = 50; b.Font = Theme.Font(8.5f, FontStyle.Bold); b.Click += click; host.Controls.Add(b, column, 0);
        }

        private void AddFlowRow(TableLayoutPanel host, int row, string label, int value, Color accent)
        {
            Panel p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card2, Margin = new Padding(0, 2, 0, 3), Padding = new Padding(10, 3, 8, 3) };
            Label l = new Label { Text = label, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            Label v = new Label { Text = value.ToString(), Dock = DockStyle.Right, Width = 52, ForeColor = accent, Font = Theme.Font(13, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight };
            p.Controls.Add(v); p.Controls.Add(l); host.Controls.Add(p, 0, row);
        }

        private void AddInsight(FlowLayoutPanel host, string title, string text, Color accent)
        {
            Panel p = new Panel { Width = 260, Height = 48, BackColor = Theme.Card2, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(10, 5, 8, 5) };
            Label t = new Label { Text = title, AutoSize = false, Dock = DockStyle.Top, Height = 17, ForeColor = accent, Font = Theme.Font(7.5f, FontStyle.Bold) };
            Label v = new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(8, FontStyle.Regular), AutoEllipsis = true };
            p.Controls.Add(v); p.Controls.Add(t); host.Controls.Add(p);
        }

        private void AddReportRow(TableLayoutPanel host, int row, string title, string value, Color accent)
        {
            Label t = new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            Label v = new Label { Text = value, Dock = DockStyle.Fill, ForeColor = accent, Font = Theme.Font(11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0) };
            host.Controls.Add(t, 0, row); host.Controls.Add(v, 1, row);
        }

        private void AddMetricTable(TableLayoutPanel host, int column, string title, string value, Color accent)
        {
            Panel card = Theme.Card(); card.Dock = DockStyle.Fill; card.Margin = new Padding(4, 0, 4, 0); card.Padding = new Padding(12, 8, 8, 6);
            Label t = new Label { Text = title, AutoSize = false, Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Muted, Font = Theme.Font(7.6f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            Label v = new Label { Text = value, AutoSize = false, Dock = DockStyle.Fill, ForeColor = accent, Font = Theme.Font(14, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            card.Controls.Add(v); card.Controls.Add(t); host.Controls.Add(card, column, 0);
        }

        private void AddMiniTable(TableLayoutPanel host, int row, string title, string value, Color accent)
        {
            Panel p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card2, Margin = new Padding(0, 2, 0, 4), Padding = new Padding(10, 4, 10, 4) };
            Label t = new Label { Text = title, AutoSize = false, Dock = DockStyle.Left, Width = 150, ForeColor = Theme.Muted, Font = Theme.Font(8.2f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            Label v = new Label { Text = value, AutoSize = false, Dock = DockStyle.Fill, ForeColor = accent, Font = Theme.Font(13, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight };
            p.Controls.Add(v); p.Controls.Add(t); host.Controls.Add(p, 0, row);
        }


        private void AddMetric(FlowLayoutPanel host, string title, string value, Color accent)
        {
            Panel card = Theme.Card(); card.Width = 200; card.Height = 92; card.Margin = new Padding(0, 0, 10, 0);
            Label t = new Label { Text = title, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8, FontStyle.Bold), Location = new Point(15, 12) };
            Label v = new Label { Text = value, AutoSize = true, ForeColor = accent, Font = Theme.Font(19, FontStyle.Bold), Location = new Point(15, 40) };
            card.Controls.Add(t); card.Controls.Add(v); host.Controls.Add(card);
        }

        private void AddMiniInfo(FlowLayoutPanel host, string title, string value, Color accent)
        {
            Panel p = new Panel { Width = 280, Height = 58, BackColor = Theme.Card2, Margin = new Padding(0, 0, 0, 8) };
            Label t = new Label { Text = title, AutoSize = true, Location = new Point(12, 9), ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold) };
            Label v = new Label { Text = value, AutoSize = true, Location = new Point(12, 28), ForeColor = accent, Font = Theme.Font(15, FontStyle.Bold) };
            p.Controls.Add(t); p.Controls.Add(v); host.Controls.Add(p);
        }

        private Control BuildPOS()
        {
            Panel page = Page();
            Panel ticketCard = Theme.Card(); ticketCard.Dock = DockStyle.Top; ticketCard.Height = 610; ticketCard.Padding = new Padding(18);
            Label h = H2("TICKET DE VENTA"); h.Dock = DockStyle.Top; h.Height = 34; ticketCard.Controls.Add(h);
            Label sub = new Label { Text = "Escaneá el código de barras o escribí el nombre. No se muestra el catálogo completo: cada lectura agrega directamente al ticket.", AutoSize = false, Dock = DockStyle.Top, Height = 38, ForeColor = Theme.Muted, Font = Theme.Font(8.8f, FontStyle.Regular) }; ticketCard.Controls.Add(sub);
            FlowLayoutPanel searchBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
            ScannerTextBox barcode = new ScannerTextBox { Width = 420, Height = 32, Font = Theme.Font(12, FontStyle.Bold), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, TabIndex = 0 }; barcode.PlaceholderTextSafe("Código de barras / SKU interno / producto"); searchBar.Controls.Add(barcode);
            Button add = Theme.Primary("AGREGAR AL TICKET"); searchBar.Controls.Add(add);
            Button scan = Theme.Secondary("ESCANEAR"); scan.Click += delegate { using (ScanBarcodeForm sf = new ScanBarcodeForm()) if (sf.ShowDialog(this) == DialogResult.OK) { barcode.Text = sf.Barcode; AddBarcodeToTicket(barcode); } }; searchBar.Controls.Add(scan);
            Button android = Theme.Secondary("TELÉFONO ANDROID"); android.Click += delegate { using (AndroidScannerForm af = new AndroidScannerForm(delegate(string code) { barcode.Text = code; AddBarcodeToTicket(barcode); })) af.ShowDialog(this); }; searchBar.Controls.Add(android);
            ticketCard.Controls.Add(searchBar);

            _ticketGrid = Theme.Grid(); _ticketGrid.Dock = DockStyle.Top; _ticketGrid.Height = 380; ticketCard.Controls.Add(_ticketGrid);
            FlowLayoutPanel ticketButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            Button remove = Theme.Secondary("Quitar seleccionado"); remove.Click += delegate { RemoveSelectedCart(); }; ticketButtons.Controls.Add(remove);
            Button clear = Theme.Secondary("Vaciar ticket"); clear.Click += delegate { _cart.Clear(); RefreshCart(); }; ticketButtons.Controls.Add(clear);
            Button print = Theme.Primary("COBRAR E IMPRIMIR TICKET"); print.Click += delegate { CompleteSaleAndPrint(); }; ticketButtons.Controls.Add(print);
            ticketCard.Controls.Add(ticketButtons);
            _ticketTotal = new Label { Text = "TOTAL $ 0,00", Dock = DockStyle.Top, Height = 58, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Green, Font = Theme.Font(23, FontStyle.Bold) }; ticketCard.Controls.Add(_ticketTotal);
            page.Controls.Add(ticketCard);

            _androidBarcodeHandler = delegate(string code)
            {
                barcode.Text = code;
                AddBarcodeToTicket(barcode);
            };
            add.Click += delegate { AddBarcodeToTicket(barcode); };
            barcode.BarcodeScanned += delegate { AddBarcodeToTicket(barcode); };
            barcode.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddBarcodeToTicket(barcode); } };
            page.HandleCreated += delegate { BeginInvoke((MethodInvoker)delegate { barcode.Focus(); barcode.SelectAll(); }); };
            barcode.Focus(); RefreshCart(); return page;
        }

        private void AddBarcodeToTicket(TextBox barcode)
        {
            string q = barcode.Text.Trim(); if (q.Length == 0) return;
            List<Product> found = _store.GetProducts(q).Where(x => x.Active).ToList();
            // El SKU interno también funciona como identificador de lectura/búsqueda.
            // Primero priorizamos coincidencia exacta para evitar ambigüedades.
            Product exact = found.FirstOrDefault(x =>
                string.Equals((x.Barcode ?? "").Trim(), q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((x.SKU ?? "").Trim(), q, StringComparison.OrdinalIgnoreCase));
            if (exact != null) AddToCart(exact, 1);
            else if (found.Count == 1) AddToCart(found[0], 1);
            else if (found.Count > 1) MessageBox.Show("Hay varios productos que coinciden. Escribí el código de barras, SKU interno exacto o seleccioná el nombre completo.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else MessageBox.Show("No se encontró un producto con ese código de barras, SKU interno o nombre.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ScannerTextBox scanner = barcode as ScannerTextBox;
            if (scanner != null) scanner.ClearAfterScan(); else { barcode.Clear(); barcode.Focus(); }
        }


        private void AddToCart(Product p, int quantity)
        {
            if (p == null || p.Stock <= 0) { MessageBox.Show("El producto no tiene stock disponible.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            CartLine line = _cart.FirstOrDefault(x => x.Product.Id == p.Id);
            if (line == null) { _cart.Add(new CartLine { Product = p, Quantity = Math.Max(1, quantity) }); }
            else { line.Quantity = Math.Min(p.Stock, line.Quantity + Math.Max(1, quantity)); }
            RefreshCart();
        }

        private void RemoveSelectedCart()
        {
            if (_ticketGrid == null || _ticketGrid.SelectedRows.Count == 0) return;
            int idx = _ticketGrid.SelectedRows[0].Index;
            if (idx >= 0 && idx < _cart.Count) _cart.RemoveAt(idx);
            RefreshCart();
        }

        private void RefreshCart()
        {
            if (_ticketGrid == null) return;
            _ticketGrid.DataSource = _cart.Select(x => new { Producto = x.Product.Name, Código = x.Product.Barcode, Cantidad = x.Quantity, Unitario = x.UnitPrice, Total = x.Total }).ToList();
            decimal total = _cart.Sum(x => x.Total);
            if (_ticketTotal != null) _ticketTotal.Text = "TOTAL " + total.ToString("C") ;
        }

        private void CompleteSaleAndPrint()
        {
            OpenPaymentCenter();
        }

        private string BuildItemsJson()
        {
            StringBuilder json = new StringBuilder();
            json.Append("[");
            for (int i = 0; i < _cart.Count; i++)
            {
                CartLine line = _cart[i];
                if (i > 0) json.Append(",");
                json.Append("{\"productId\":").Append(line.Product.Id)
                    .Append(",\"barcode\":\"").Append(JsonEscape(line.Product.Barcode))
                    .Append("\",\"name\":\"").Append(JsonEscape(line.Product.Name))
                    .Append("\",\"quantity\":").Append(line.Quantity)
                    .Append(",\"unitPrice\":").Append(line.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"total\":").Append(line.Total.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append("}");
            }
            json.Append("]");
            return json.ToString();
        }

        private static string JsonEscape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private bool ShowPaymentDialog(decimal total, out string method, out decimal received)
        {
            string resultMethod = "Efectivo";
            decimal resultReceived = total;
            using (Form f = Dialog("Cobro"))
            {
                f.ClientSize = new Size(520, 390);
                Label amount = new Label { Text = "TOTAL A COBRAR\r\n" + total.ToString("C"), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Width = 440, Height = 85, Location = new Point(30, 18), ForeColor = Theme.Green, Font = Theme.Font(20, FontStyle.Bold) };
                f.Controls.Add(amount);

                Label ml = new Label { Text = "Medio de pago", AutoSize = true, Location = new Point(35, 118), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) };
                f.Controls.Add(ml);
                ComboBox combo = new ComboBox { Width = 440, Height = 30, Location = new Point(35, 140), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Card2, ForeColor = Theme.Text };
                combo.Items.AddRange(new object[] { "Efectivo", "Mercado Pago", "Tarjeta de débito", "Tarjeta de crédito", "Transferencia" });
                combo.SelectedIndex = 0;
                f.Controls.Add(combo);

                Label rl = new Label { Text = "Dinero recibido (solo efectivo)", AutoSize = true, Location = new Point(35, 182), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) };
                f.Controls.Add(rl);
                TextBox receivedBox = new TextBox { Text = total.ToString("0.00"), Width = 210, Height = 30, Location = new Point(35, 204), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
                f.Controls.Add(receivedBox);
                Label change = new Label { Text = "Vuelto: " + (0m).ToString("C"), AutoSize = true, Location = new Point(265, 208), ForeColor = Theme.Warning, Font = Theme.Font(11, FontStyle.Bold) };
                f.Controls.Add(change);
                combo.SelectedIndexChanged += delegate
                {
                    bool cash = combo.SelectedItem != null && combo.SelectedItem.ToString() == "Efectivo";
                    receivedBox.Enabled = cash;
                    change.Visible = cash;
                };
                receivedBox.TextChanged += delegate
                {
                    decimal r;
                    if (decimal.TryParse(receivedBox.Text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out r))
                        change.Text = "Vuelto: " + Math.Max(0m, r - total).ToString("C");
                };

                Button cancel = Theme.Secondary("CANCELAR"); cancel.Location = new Point(35, 285); cancel.Width = 140; f.Controls.Add(cancel); cancel.Click += delegate { f.DialogResult = DialogResult.Cancel; f.Close(); };
                Button confirm = Theme.Primary("CONFIRMAR COBRO"); confirm.Location = new Point(275, 285); confirm.Width = 200; f.Controls.Add(confirm);
                confirm.Click += delegate
                {
                    resultMethod = combo.SelectedItem == null ? "Efectivo" : combo.SelectedItem.ToString();
                    if (resultMethod == "Efectivo")
                    {
                        decimal r;
                        if (!decimal.TryParse(receivedBox.Text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out r) || r < total)
                        {
                            MessageBox.Show("El dinero recibido no alcanza para cubrir el total.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        resultReceived = r;
                    }
                    else resultReceived = total;
                    f.DialogResult = DialogResult.OK;
                    f.Close();
                };
                f.AcceptButton = confirm;
                bool ok = f.ShowDialog(this) == DialogResult.OK;
                method = resultMethod;
                received = resultReceived;
                return ok;
            }
        }

        private void OpenPaymentCenter()
        {
            if (_cart.Count == 0)
            {
                MessageBox.Show("El ticket está vacío. Agregá productos antes de cobrar.", "NexoMarket · Cobro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (!string.Equals(_currentPage, "Punto de venta", StringComparison.OrdinalIgnoreCase)) ShowPage("Punto de venta", BuildPOS);
                return;
            }

            decimal total = _cart.Sum(x => x.Total);
            using (Form f = Dialog("Centro de cobro"))
            {
                f.ClientSize = new Size(860, 650);
                f.MinimumSize = new Size(780, 590);
                TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
                TabPage cobro = Tab("Cobro"); TabPage comprobante = Tab("Ticket / Factura"); TabPage enviar = Tab("Enviar al cliente");
                tabs.TabPages.Add(cobro); tabs.TabPages.Add(comprobante); tabs.TabPages.Add(enviar);
                f.Controls.Add(tabs);

                Label totalLabel = new Label { Text = "TOTAL A COBRAR\r\n" + total.ToString("C"), AutoSize = false, Width = 760, Height = 90, Location = new Point(30, 20), ForeColor = Theme.NeonGreen, Font = Theme.Font(24, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
                cobro.Controls.Add(totalLabel);
                Label payHint = new Label { Text = "Elegí el medio de cobro. Todos quedan registrados en la venta.", AutoSize = false, Width = 720, Height = 30, Location = new Point(50, 112), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) }; cobro.Controls.Add(payHint);
                string[] methods = { "Efectivo", "Mercado Pago", "Tarjeta de débito", "Tarjeta de crédito", "Transferencia" };
                FlowLayoutPanel methodsBar = new FlowLayoutPanel { Location = new Point(45, 150), Size = new Size(730, 105), WrapContents = true, Padding = new Padding(0) };
                ComboBox method = new ComboBox { Width = 300, Height = 34, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(10, FontStyle.Bold) }; method.Items.AddRange(methods); method.SelectedIndex = 0; methodsBar.Controls.Add(method);
                TextBox receivedBox = new TextBox { Width = 180, Height = 32, Text = total.ToString("0.00"), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Font(10, FontStyle.Bold) }; methodsBar.Controls.Add(receivedBox);
                Label change = new Label { Text = "Vuelto: $ 0,00", AutoSize = false, Width = 190, Height = 32, ForeColor = Theme.Warning, Font = Theme.Font(10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }; methodsBar.Controls.Add(change);
                cobro.Controls.Add(methodsBar);
                method.SelectedIndexChanged += delegate { bool cash = Convert.ToString(method.SelectedItem) == "Efectivo"; receivedBox.Enabled = cash; change.Visible = cash; };
                receivedBox.TextChanged += delegate { decimal r; if (decimal.TryParse(receivedBox.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out r)) change.Text = "Vuelto: " + Math.Max(0m, r - total).ToString("C"); };

                Label cartSummary = new Label { Text = string.Join("\r\n", _cart.Select(x => x.Quantity + " x " + x.Product.Name + "  " + x.Total.ToString("C"))), AutoSize = false, Location = new Point(50, 265), Size = new Size(700, 160), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) }; cobro.Controls.Add(cartSummary);

                TextBox receiptPreview = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(28, 22), Size = new Size(770, 420), BackColor = Color.White, ForeColor = Color.Black, Font = new Font("Consolas", 9f), Text = BuildTicketText(total, "Efectivo", total) }; comprobante.Controls.Add(receiptPreview);
                ComboBox invoiceType = new ComboBox { Location = new Point(28, 455), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList }; invoiceType.Items.AddRange(new object[] { "Ticket", "Factura A", "Factura B", "Factura C" }); invoiceType.SelectedIndex = 0; comprobante.Controls.Add(invoiceType);
                Button printTicket = Theme.Primary("IMPRIMIR TICKET"); printTicket.Location = new Point(195, 450); printTicket.Width = 170; comprobante.Controls.Add(printTicket);
                Button draftInvoice = Theme.Secondary("GENERAR FACTURA"); draftInvoice.Location = new Point(380, 450); draftInvoice.Width = 180; comprobante.Controls.Add(draftInvoice);
                Label fiscal = new Label { Text = "Factura A/B/C: NexoMarket genera el borrador y el texto del comprobante. La autorización fiscal/CAE se realiza únicamente cuando esté configurado ARCA.", AutoSize = false, Location = new Point(28, 500), Size = new Size(740, 60), ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) }; comprobante.Controls.Add(fiscal);

                Label sendHelp = new Label { Text = "Después de cobrar podés guardar el comprobante como TXT o abrir el correo/WhatsApp del cliente con el detalle preparado.", AutoSize = false, Location = new Point(30, 25), Size = new Size(760, 45), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) }; enviar.Controls.Add(sendHelp);
                TextBox email = new TextBox { Location = new Point(30, 105), Width = 360, Height = 30, BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }; email.PlaceholderTextSafe("Email del cliente"); enviar.Controls.Add(new Label { Text = "EMAIL", AutoSize = true, Location = new Point(30, 82), ForeColor = Theme.Muted, Font = Theme.Font(8, FontStyle.Bold) }); enviar.Controls.Add(email);
                TextBox phone = new TextBox { Location = new Point(420, 105), Width = 360, Height = 30, BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }; phone.PlaceholderTextSafe("Teléfono con código de país"); enviar.Controls.Add(new Label { Text = "WHATSAPP", AutoSize = true, Location = new Point(420, 82), ForeColor = Theme.Muted, Font = Theme.Font(8, FontStyle.Bold) }); enviar.Controls.Add(phone);
                TextBox shareText = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(30, 160), Size = new Size(750, 300), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = new Font("Consolas", 9f), Text = BuildTicketText(total, "Efectivo", total) }; enviar.Controls.Add(shareText);
                Button saveTxt = Theme.Primary("GUARDAR TXT"); saveTxt.Location = new Point(30, 485); saveTxt.Width = 150; enviar.Controls.Add(saveTxt);
                Button emailBtn = Theme.Secondary("ABRIR EMAIL"); emailBtn.Location = new Point(195, 485); emailBtn.Width = 150; enviar.Controls.Add(emailBtn);
                Button whatsBtn = Theme.Secondary("ABRIR WHATSAPP"); whatsBtn.Location = new Point(360, 485); whatsBtn.Width = 175; enviar.Controls.Add(whatsBtn);

                bool registered = false; string finalMethod = "Efectivo"; decimal finalReceived = total;
                Button cancel = Theme.Secondary("CERRAR"); cancel.Location = new Point(555, 485); cancel.Width = 120; enviar.Controls.Add(cancel);
                Button charge = Theme.Primary("CONFIRMAR COBRO  ·  F12"); charge.Location = new Point(665, 485); charge.Width = 155; enviar.Controls.Add(charge);

                Action refreshTexts = delegate { string txt = BuildTicketText(total, finalMethod, finalReceived); receiptPreview.Text = txt; shareText.Text = txt; };
                method.SelectedIndexChanged += delegate { if (!registered) { finalMethod = Convert.ToString(method.SelectedItem); refreshTexts(); } };
                receivedBox.TextChanged += delegate { if (!registered) { decimal r; if (decimal.TryParse(receivedBox.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out r)) { finalReceived = r; refreshTexts(); } } };

                printTicket.Click += delegate { if (!registered) { MessageBox.Show("Primero confirmá el cobro.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } PrintTicket(total, finalMethod, finalReceived); };
                draftInvoice.Click += delegate { if (!registered) { MessageBox.Show("Primero confirmá el cobro.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } using (Form inv = InvoiceDraftForm(Convert.ToString(invoiceType.SelectedItem), _store.GetSetting("arca_cuit", ""), _store.GetSetting("arca_point_of_sale", "0001"))) inv.ShowDialog(f); };
                saveTxt.Click += delegate { using (SaveFileDialog dlg = new SaveFileDialog { Filter = "Texto TXT|*.txt", FileName = "NexoMarket_Comprobante_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" }) { if (dlg.ShowDialog(f) == DialogResult.OK) { File.WriteAllText(dlg.FileName, shareText.Text, Encoding.UTF8); MessageBox.Show("Comprobante guardado en:\r\n" + dlg.FileName, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); } } };
                emailBtn.Click += delegate { string address = email.Text.Trim(); if (address.Length == 0) { MessageBox.Show("Ingresá el email del cliente.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } try { string uri = "mailto:" + address + "?subject=" + Uri.EscapeDataString("NexoMarket · Comprobante") + "&body=" + Uri.EscapeDataString(shareText.Text); Process.Start(uri); } catch (Exception ex) { MessageBox.Show("No se pudo abrir el correo.\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error); } };
                whatsBtn.Click += delegate { string number = new string(phone.Text.Where(char.IsDigit).ToArray()); if (number.Length == 0) { MessageBox.Show("Ingresá el número de WhatsApp con código de país.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } try { string uri = "https://wa.me/" + number + "?text=" + Uri.EscapeDataString(shareText.Text); Process.Start(uri); } catch (Exception ex) { MessageBox.Show("No se pudo abrir WhatsApp.\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error); } };
                cancel.Click += delegate { f.Close(); };
                charge.Click += delegate
                {
                    if (registered) { tabs.SelectedTab = comprobante; return; }
                    finalMethod = Convert.ToString(method.SelectedItem);
                    if (finalMethod == "Efectivo") { decimal r; if (!decimal.TryParse(receivedBox.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out r) || r < total) { MessageBox.Show("El dinero recibido no alcanza para cubrir el total.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; } finalReceived = r; } else finalReceived = total;
                    List<Product> sold = new List<Product>(); foreach (CartLine line in _cart) for (int i = 0; i < line.Quantity; i++) sold.Add(line.Product);
                    if (!_store.RegisterCounterSale(sold, total, finalMethod, "Mostrador", BuildItemsJson())) { MessageBox.Show("No se pudo registrar la venta. Revisá el stock disponible.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    _lastCompletedCart.Clear();
                    foreach (CartLine line in _cart) _lastCompletedCart.Add(new CartLine { Product = line.Product, Quantity = line.Quantity });
                    string finalReceiptText = BuildTicketText(total, finalMethod, finalReceived);
                    registered = true; receiptPreview.Text = finalReceiptText; shareText.Text = finalReceiptText; tabs.SelectedTab = comprobante; charge.Text = "COBRO REGISTRADO"; charge.Enabled = false; _cart.Clear(); RefreshCart();
                };
                f.FormClosed += delegate { if (registered) MessageBox.Show("Venta registrada correctamente.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); };
                f.ShowDialog(this);
            }
        }

        private string BuildTicketText(decimal total, string paymentMethod, decimal received)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine(_ticketHeader); b.AppendLine("NexoMarket"); b.AppendLine(DateTime.Now.ToString("dd/MM/yyyy HH:mm")); b.AppendLine(new string('-', 58));
            List<CartLine> lines = _cart.Count > 0 ? _cart : _lastCompletedCart;
            foreach (CartLine line in lines) b.AppendLine(line.Quantity + " x " + line.Product.Name + "  " + line.UnitPrice.ToString("C") + " = " + line.Total.ToString("C"));
            b.AppendLine(new string('-', 58)); b.AppendLine("TOTAL: " + total.ToString("C")); b.AppendLine("MEDIO DE PAGO: " + paymentMethod);
            if (paymentMethod == "Efectivo") b.AppendLine("RECIBIDO: " + received.ToString("C") + "   VUELTO: " + Math.Max(0m, received - total).ToString("C"));
            b.AppendLine(_ticketFooter); return b.ToString();
        }

        private void PrintTicket(decimal total, string paymentMethod, decimal received)
        {
            _printDocument = new PrintDocument();
            _ticketPaymentMethod = paymentMethod;
            _ticketReceived = received;
            _printDocument.DocumentName = "NexoMarket Ticket";
            _printDocument.PrintPage += PrintTicketPage;
            using (PrintPreviewDialog preview = new PrintPreviewDialog())
            {
                preview.Document = _printDocument; preview.Width = 900; preview.Height = 700;
                if (preview.ShowDialog(this) == DialogResult.OK) { }
            }
        }

        private void PrintTicketPage(object sender, PrintPageEventArgs e)
        {
            float y = 25; Font title = Theme.Font(15, FontStyle.Bold); Font normal = Theme.Font(9, FontStyle.Regular); Font bold = Theme.Font(10, FontStyle.Bold);
            using (Brush b = new SolidBrush(Color.Black))
            {
                e.Graphics.DrawString(_ticketHeader, title, b, 35, y); y += 32;
                e.Graphics.DrawString(DateTime.Now.ToString("dd/MM/yyyy HH:mm"), normal, b, 35, y); y += 25;
                e.Graphics.DrawLine(Pens.Black, 35, y, e.PageBounds.Width - 35, y); y += 12;
                List<CartLine> printableLines = _lastCompletedCart.Count > 0 ? _lastCompletedCart : _cart;
                foreach (CartLine line in printableLines)
                {
                    e.Graphics.DrawString(line.Product.Name, bold, b, 35, y); y += 18;
                    e.Graphics.DrawString(line.Quantity + " x " + line.UnitPrice.ToString("C") + " = " + line.Total.ToString("C"), normal, b, 50, y); y += 20;
                }
                e.Graphics.DrawLine(Pens.Black, 35, y, e.PageBounds.Width - 35, y); y += 12;
                decimal printableTotal = printableLines.Sum(x => x.Total);
                e.Graphics.DrawString("TOTAL " + printableTotal.ToString("C"), title, b, 35, y); y += 25;
                e.Graphics.DrawString("Pago: " + _ticketPaymentMethod, normal, b, 35, y); y += 18;
                if (_ticketPaymentMethod == "Efectivo")
                {
                    e.Graphics.DrawString("Recibido: " + _ticketReceived.ToString("C") + "   Vuelto: " + Math.Max(0m, _ticketReceived - printableTotal).ToString("C"), normal, b, 35, y);
                    y += 20;
                }
                e.Graphics.DrawString(_ticketFooter, normal, b, 35, y);
            }
            e.HasMorePages = false;
        }

        private object ProductPOSRow(Product p) { return new { Id = p.Id, Código = p.Barcode, Producto = p.Name, Categoría = p.Category, Precio = p.SalePrice > 0 ? p.SalePrice : p.Price, Stock = p.Stock }; }

        private Control BuildCashRegister()
        {
            Panel page = Page();
            List<Order> today = _store.GetOrders("").Where(o => o.CreatedAt.ToLocalTime().Date == DateTime.Today && o.Status != "Cancelado").ToList();
            decimal opening, openingMp, retention;
            decimal.TryParse(_store.GetSetting("cash_opening", "0").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out opening);
            decimal.TryParse(_store.GetSetting("cash_opening_mercadopago", "0").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out openingMp);
            decimal.TryParse(_store.GetSetting("cash_mercadopago_retention", "0").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out retention);
            bool isOpen = string.Equals(_store.GetSetting("cash_status", "Cerrada"), "Abierta", StringComparison.OrdinalIgnoreCase);
            decimal cash = today.Where(o => o.PaymentMethod == "Efectivo").Sum(o => o.Total);
            decimal mercado = today.Where(o => o.PaymentMethod == "Mercado Pago").Sum(o => o.Total);
            decimal debit = today.Where(o => o.PaymentMethod == "Tarjeta de débito" || o.PaymentMethod == "Débito").Sum(o => o.Total);
            decimal credit = today.Where(o => o.PaymentMethod == "Tarjeta de crédito" || o.PaymentMethod == "Crédito").Sum(o => o.Total);
            decimal transfer = today.Where(o => o.PaymentMethod == "Transferencia").Sum(o => o.Total);
            decimal totalSales = today.Sum(o => o.Total);
            decimal expectedCash = opening + cash;
            decimal expectedMp = openingMp + mercado - retention;

            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 240, ColumnCount = 4, RowCount = 2, Padding = new Padding(0, 4, 0, 10) };
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50f)); cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            AddMetricTable(cards, 0, "ESTADO", isOpen ? "ABIERTA" : "CERRADA", isOpen ? Theme.Green : Theme.Warning);
            AddMetricTable(cards, 1, "APERTURA EFECTIVO", opening.ToString("C0"), Theme.Accent);
            AddMetricTable(cards, 2, "APERTURA MERCADO PAGO", openingMp.ToString("C0"), Theme.Accent);
            AddMetricTable(cards, 3, "MERCADO PAGO VENTAS", mercado.ToString("C0"), Theme.Accent);
            AddMetricTable(cards, 4, "EFECTIVO", cash.ToString("C0"), Theme.Green);
            AddMetricTable(cards, 5, "RETENCIÓN MP", retention.ToString("C0"), Theme.Warning);
            AddMetricTable(cards, 6, "EFECTIVO ESPERADO", expectedCash.ToString("C0"), Theme.Green);
            AddMetricTable(cards, 7, "MP DISPONIBLE", expectedMp.ToString("C0"), Theme.Accent);
            page.Controls.Add(cards);

            Panel config = Theme.Card(); config.Dock = DockStyle.Top; config.Height = 205; config.Padding = new Padding(18);
            Label ch = H2("Apertura y cierre de caja"); ch.Dock = DockStyle.Top; ch.Height = 30; config.Controls.Add(ch);
            TextBox openingBox = new TextBox { Text = opening.ToString("0.00"), Width = 170, Height = 30, BackColor = Theme.Card2, ForeColor = Theme.Text, Location = new Point(18, 52) }; config.Controls.Add(openingBox);
            Label l1 = new Label { Text = "Efectivo inicial", Location = new Point(18, 34), AutoSize = true, ForeColor = Theme.Muted }; config.Controls.Add(l1);
            TextBox mpBox = new TextBox { Text = openingMp.ToString("0.00"), Width = 170, Height = 30, BackColor = Theme.Card2, ForeColor = Theme.Text, Location = new Point(205, 52) }; config.Controls.Add(mpBox);
            Label l2 = new Label { Text = "Mercado Pago inicial", Location = new Point(205, 34), AutoSize = true, ForeColor = Theme.Muted }; config.Controls.Add(l2);
            TextBox retentionBox = new TextBox { Text = retention.ToString("0.00"), Width = 170, Height = 30, BackColor = Theme.Card2, ForeColor = Theme.Text, Location = new Point(392, 52) }; config.Controls.Add(retentionBox);
            Label l3 = new Label { Text = "Retención Mercado Pago", Location = new Point(392, 34), AutoSize = true, ForeColor = Theme.Muted }; config.Controls.Add(l3);

            Button open = Theme.Primary(isOpen ? "CAJA ABIERTA" : "ABRIR CAJA"); open.Location = new Point(580, 48); open.Width = 150; open.Enabled = !isOpen;
            open.Click += delegate {
                decimal a,m,r;
                if (!decimal.TryParse(openingBox.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out a) || a < 0 ||
                    !decimal.TryParse(mpBox.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out m) || m < 0 ||
                    !decimal.TryParse(retentionBox.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out r) || r < 0)
                { MessageBox.Show("Ingresá importes válidos.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                _store.SetSetting("cash_opening", a.ToString(CultureInfo.InvariantCulture));
                _store.SetSetting("cash_opening_mercadopago", m.ToString(CultureInfo.InvariantCulture));
                _store.SetSetting("cash_mercadopago_retention", r.ToString(CultureInfo.InvariantCulture));
                _store.SetSetting("cash_status", "Abierta"); _store.SetSetting("cash_opened_at", DateTime.Now.ToString("o"));
                ShowPage("Caja / arqueo", BuildCashRegister);
            }; config.Controls.Add(open);

            Button close = Theme.Secondary("CERRAR CAJA"); close.Location = new Point(18, 112); close.Width = 180; close.Enabled = isOpen;
            close.Click += delegate {
                using (Form f = Dialog("Cierre de caja"))
                {
                    f.ClientSize = new Size(520, 300);
                    TextBox actual = Field(f, "Efectivo contado al cierre", expectedCash.ToString("0.00"), 20);
                    TextBox actualMp = Field(f, "Mercado Pago contado/disponible", expectedMp.ToString("0.00"), 82);
                    Button ok = Theme.Primary("CONFIRMAR CIERRE"); ok.Location = new Point(280, 190); ok.Width = 190; f.Controls.Add(ok);
                    ok.Click += delegate {
                        decimal av,mv;
                        if (!decimal.TryParse(actual.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out av) || av < 0 ||
                            !decimal.TryParse(actualMp.Text.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out mv) || mv < 0)
                        { MessageBox.Show("Ingresá importes válidos."); return; }
                        _store.SetSetting("cash_close_actual", av.ToString(CultureInfo.InvariantCulture));
                        _store.SetSetting("cash_close_mercadopago", mv.ToString(CultureInfo.InvariantCulture));
                        _store.SetSetting("cash_closed_at", DateTime.Now.ToString("o"));
                        _store.SetSetting("cash_status", "Cerrada");
                        f.DialogResult = DialogResult.OK; f.Close();
                    };
                    if (f.ShowDialog(this) == DialogResult.OK) ShowPage("Caja / arqueo", BuildCashRegister);
                }
            }; config.Controls.Add(close);

            Label status = new Label { Text = isOpen ? "Caja abierta. Podés vender y luego cerrar desde acá." : "Caja cerrada. Para operar, primero ingresá la apertura de efectivo y Mercado Pago.", AutoSize = false, Width = 680, Height = 55, Location = new Point(205, 108), ForeColor = isOpen ? Theme.Green : Theme.Warning, Font = Theme.Font(9, FontStyle.Regular) };
            config.Controls.Add(status);
            page.Controls.Add(config);
            return page;
        }

        private Control BuildCoupons()
        {
            Panel page=Page();
            Panel head=Theme.Card(); head.Dock=DockStyle.Top; head.Height=105; head.Padding=new Padding(18);
            Label h=H2("CUPONES Y CÓDIGOS DE PROMOCIÓN"); h.Dock=DockStyle.Top; h.Height=30; head.Controls.Add(h);
            Label info=new Label{Text="Creá códigos con descuento porcentual o fijo y definí un límite de usos. Se sincronizan con el Seller Center web.",AutoSize=false,Dock=DockStyle.Fill,ForeColor=Theme.Muted,Font=Theme.Font(9)}; head.Controls.Add(info); page.Controls.Add(head);
            DataGridView grid=Theme.Grid(); grid.Dock=DockStyle.Top; grid.Height=330;
            grid.DataSource=_store.GetCoupons().Select(c=>new {Id=c.Id,Código=c.Code,Descripción=c.Description,Descuento=c.DiscountPercent>0?c.DiscountPercent.ToString("0.##")+"%":"$ "+c.DiscountAmount.ToString("N2"),Usos=c.Used+" / "+(c.MaxUses==0?"∞":c.MaxUses.ToString()),Estado=c.Active?"ACTIVO":"PAUSADO",Desde=c.From.ToString("dd/MM/yyyy"),Hasta=c.To.ToString("dd/MM/yyyy")}).ToList();
            if(grid.Columns.Contains("Id"))grid.Columns["Id"].Visible=false;
            page.Controls.Add(grid);
            FlowLayoutPanel actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=60,WrapContents=false,Padding=new Padding(0,10,0,0)};
            Button add=Theme.Primary("+ NUEVO CUPÓN"); add.Click+=delegate{using(Form f=CouponDialog())if(f.ShowDialog(this)==DialogResult.OK)ShowPage("Cupones",BuildCoupons);}; actions.Controls.Add(add);
            Button del=Theme.Secondary("ELIMINAR"); del.Click+=delegate{if(grid.SelectedRows.Count==0)return;long id=Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value);_store.DeleteCoupon(id);ShowPage("Cupones",BuildCoupons);}; actions.Controls.Add(del);
            page.Controls.Add(actions); return page;
        }

        private Form CouponDialog()
        {
            Form f=Dialog("Nuevo cupón"); f.ClientSize=new Size(620,430);
            TextBox code=Field(f,"Código","VERANO10",20);
            TextBox desc=Field(f,"Descripción","10% de descuento",82);
            TextBox percent=Field(f,"Descuento %","10",144);
            TextBox amount=Field(f,"Descuento fijo $","0",206);
            TextBox maxUses=Field(f,"Usos máximos (0 = sin límite)","100",268);
            Label hint=new Label{Text="Usá porcentaje o importe fijo, no ambos. El límite se aplica al usar el código.",AutoSize=false,Width=540,Height=40,Location=new Point(20,330),ForeColor=Theme.Muted,Font=Theme.Font(8.5f)};f.Controls.Add(hint);
            Button save=Theme.Primary("GENERAR CUPÓN");save.Location=new Point(400,365);save.Width=160;f.Controls.Add(save);
            save.Click+=delegate{
                string c=(code.Text??"").Trim().ToUpperInvariant();decimal p=0,a=0;int max=0;
                decimal.TryParse(percent.Text.Replace(",","."),NumberStyles.Any,CultureInfo.InvariantCulture,out p);
                decimal.TryParse(amount.Text.Replace(",","."),NumberStyles.Any,CultureInfo.InvariantCulture,out a);
                int.TryParse(maxUses.Text,out max);
                if(c.Length<3|| (p<=0&&a<=0)||(p>100)||(p>0&&a>0)){MessageBox.Show("Ingresá un código y un descuento válido: porcentaje o importe fijo.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
                if(_store.GetCoupons().Any(x=>string.Equals(x.Code,c,StringComparison.OrdinalIgnoreCase))){MessageBox.Show("Ese cupón ya existe.");return;}
                _store.SaveCoupon(new Coupon{Code=c,Description=desc.Text,DiscountPercent=p,DiscountAmount=a,MaxUses=Math.Max(0,max),Used=0,Active=true,From=DateTime.Today,To=DateTime.Today.AddDays(30)});
                f.DialogResult=DialogResult.OK;f.Close();
            };
            return f;
        }

        private Control BuildPromotions()
        {
            Panel page = Page();
            Panel head = Theme.Card(); head.Dock = DockStyle.Top; head.Height = 92; head.Padding = new Padding(18);
            Label h = H2("PROMOCIONES Y COMBOS"); h.Dock = DockStyle.Top; h.Height = 30; head.Controls.Add(h);
            Label info = new Label { Text = "Armá combos para ropa, farmacia, ferretería o cualquier comercio seleccionando productos del inventario y definiendo un precio promocional.", AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) }; head.Controls.Add(info); page.Controls.Add(head);
            DataGridView grid = Theme.Grid(); grid.Dock = DockStyle.Top; grid.Height = 350; grid.DataSource = _store.GetPromotions().Select(p => new { Id=p.Id, Promoción=p.Name, Productos=p.ProductIds, Precio=p.PromotionalPrice.ToString("C"), Activa=p.Active ? "SI" : "NO", Desde=p.From.ToString("dd/MM/yyyy"), Hasta=p.To.ToString("dd/MM/yyyy") }).ToList(); if(grid.Columns.Contains("Id"))grid.Columns["Id"].Visible=false; SetFill(grid,"Promoción",22); SetFill(grid,"Productos",35); SetFill(grid,"Precio",13); SetFill(grid,"Activa",10); SetFill(grid,"Desde",10); SetFill(grid,"Hasta",10); page.Controls.Add(grid);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock=DockStyle.Top, Height=60, WrapContents=false, Padding=new Padding(0,10,0,0) }; Button add=Theme.Primary("+ NUEVA PROMOCIÓN"); add.Click+=delegate{ using(Form f=PromotionDialog()) if(f.ShowDialog(this)==DialogResult.OK) ShowPage("Promociones",BuildPromotions); }; actions.Controls.Add(add); Button del=Theme.Secondary("ELIMINAR"); del.Click+=delegate{ if(grid.SelectedRows.Count==0)return; long id=Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value); _store.DeletePromotion(id); ShowPage("Promociones",BuildPromotions); }; actions.Controls.Add(del); page.Controls.Add(actions); return page;
        }

        private Form PromotionDialog()
        {
            Form f=Dialog("Nueva promoción"); f.ClientSize=new Size(760,560);
            TextBox name=Field(f,"Nombre de promoción","Combo remera + pantalón",20); TextBox price=Field(f,"Precio promocional","0",82);
            Label l=new Label{Text="Seleccioná productos del inventario",AutoSize=true,Location=new Point(20,145),ForeColor=Theme.Muted,Font=Theme.Font(8.5f,FontStyle.Bold)}; f.Controls.Add(l);
            CheckedListBox list=new CheckedListBox{Location=new Point(20,170),Size=new Size(700,260),BackColor=Theme.Card2,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle}; foreach(Product p in _store.GetProducts("")) list.Items.Add(new ProductChoice(p),false); f.Controls.Add(list);
            Button save=Theme.Primary("GUARDAR PROMOCIÓN"); save.Location=new Point(520,455); save.Width=200; f.Controls.Add(save); save.Click+=delegate{decimal v;if(!decimal.TryParse(price.Text.Replace(",","."),NumberStyles.Any,CultureInfo.InvariantCulture,out v)||v<=0){MessageBox.Show("Ingresá un precio promocional válido.");return;} List<string> ids=new List<string>(); foreach(object o in list.CheckedItems){ProductChoice pc=o as ProductChoice;if(pc!=null)ids.Add(pc.Product.Id.ToString());} if(ids.Count<2){MessageBox.Show("Seleccioná al menos dos productos.");return;} Promotion p=new Promotion{Name=name.Text.Trim(),ProductIds=string.Join(",",ids.ToArray()),PromotionalPrice=v,Active=true,From=DateTime.Today,To=DateTime.Today.AddDays(30)}; _store.SavePromotion(p);f.DialogResult=DialogResult.OK;f.Close();}; return f;
        }

        private sealed class ProductChoice { public Product Product; public ProductChoice(Product p){Product=p;} public override string ToString(){return Product.Name+" · "+(Product.SalePrice>0?Product.SalePrice:Product.Price).ToString("C")+" · Stock "+Product.Stock;} }

        private Control BuildProducts()
        {
            Panel page = Page();
            Panel card = Theme.Card();
            card.Dock = DockStyle.Top;
            card.Height = 650;
            card.Padding = new Padding(14);

            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, Padding = new Padding(0, 4, 0, 4) };
            TextBox search = SearchBox(); search.Width = 320; bar.Controls.Add(search);
            bar.Controls.Add(Primary("+ AÑADIR PRODUCTO", delegate { EditProduct(null); }));
            Button delete = Theme.Secondary("ELIMINAR"); bar.Controls.Add(delete);
            Label hint = new Label { Text = "Seleccioná un producto para ver su imagen, SKU, stock y multimedia.", AutoSize = false, Width = 330, Height = 34, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular), Padding = new Padding(8, 7, 0, 0) }; bar.Controls.Add(hint);
            card.Controls.Add(bar);

            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0), BackColor = Color.Transparent };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));

            Panel list = Theme.Card(); list.Dock = DockStyle.Fill; list.Padding = new Padding(10); list.Margin = new Padding(0, 0, 6, 0);
            DataGridView grid = Theme.Grid(); grid.Dock = DockStyle.Fill; grid.DataSource = _store.GetProducts("").Select(ProductRow).ToList(); ConfigureProductGrid(grid);
            list.Controls.Add(grid); body.Controls.Add(list, 0, 0);

            Panel preview = Theme.Card(); preview.Dock = DockStyle.Fill; preview.Padding = new Padding(14); preview.Margin = new Padding(6, 0, 0, 0);
            Label ph = H2("VISTA DEL PRODUCTO"); ph.Dock = DockStyle.Top; ph.Height = 34; preview.Controls.Add(ph);
            PictureBox photo = new PictureBox { Dock = DockStyle.Top, Height = 245, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card2, BorderStyle = BorderStyle.FixedSingle }; preview.Controls.Add(photo);
            Label name = new Label { Text = "Seleccioná un producto", AutoSize = false, Dock = DockStyle.Top, Height = 48, ForeColor = Theme.Text, Font = Theme.Font(12, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }; preview.Controls.Add(name);
            Label info = new Label { Text = "La imagen guardada se mostrará automáticamente.", AutoSize = false, Dock = DockStyle.Top, Height = 90, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) }; preview.Controls.Add(info);
            Button edit = Theme.Primary("EDITAR PRODUCTO"); edit.Dock = DockStyle.Bottom; edit.Height = 40; preview.Controls.Add(edit);
            body.Controls.Add(preview, 1, 0);
            card.Controls.Add(body);

            Action refreshPreview = delegate
            {
                if (grid.SelectedRows.Count == 0) { ReplacePicture(photo, ""); name.Text = "Seleccioná un producto"; info.Text = "La imagen guardada se mostrará automáticamente."; return; }
                long id = Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value);
                Product selected = _store.GetProducts("").FirstOrDefault(x => x.Id == id);
                if (selected == null) return;
                name.Text = selected.Name;
                string image = FirstProductImage(selected);
                ReplacePicture(photo, image);
                info.Text = "SKU: " + (selected.SKU ?? "") + "\r\n" +
                            "Código: " + (selected.Barcode ?? "") + "\r\n" +
                            "Stock: " + selected.Stock + "   •   Precio: " + (selected.SalePrice > 0 ? selected.SalePrice : selected.Price).ToString("C0") +
                            "\r\nImagen: " + (string.IsNullOrWhiteSpace(image) ? "sin imagen" : Path.GetFileName(image));
            };
            grid.SelectionChanged += delegate { refreshPreview(); };
            refreshPreview();
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            { if (e.RowIndex >= 0) { long id = Convert.ToInt64(grid.Rows[e.RowIndex].Cells["Id"].Value); Product product = _store.GetProducts("").FirstOrDefault(x => x.Id == id); if (product != null) EditProduct(product); } };
            edit.Click += delegate { if (grid.SelectedRows.Count == 0) return; long id = Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value); Product product = _store.GetProducts("").FirstOrDefault(x => x.Id == id); if (product != null) EditProduct(product); };
            search.TextChanged += delegate { grid.DataSource = _store.GetProducts(search.Text).Select(ProductRow).ToList(); ConfigureProductGrid(grid); refreshPreview(); };
            delete.Click += delegate
            {
                if (grid.SelectedRows.Count == 0) return;
                long id = Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value);
                if (MessageBox.Show("¿Eliminar el producto seleccionado?", "NexoMarket", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { _store.DeleteProduct(id); try { _centralSync.SyncOnce(); } catch { } ShowPage("Productos", BuildProducts); }
            };
            page.Controls.Add(card);
            return page;
        }

        private object ProductRow(Product p) { return new { Id = p.Id, Código = p.Barcode, SKU = p.SKU, Producto = p.Name, Categoría = p.Category, Marca = p.Brand, Precio = p.SalePrice > 0 ? p.SalePrice : p.Price, Stock = p.Stock, Mínimo = p.MinimumStock, Web = p.OnlineEnabled ? "PUBLICADO" : "OCULTO", Estado = p.Active ? "ACTIVO" : "INACTIVO" }; }

        private void ConfigureProductGrid(DataGridView g)
        {
            if (g.Columns.Contains("Id")) g.Columns["Id"].Visible = false;
            SetFill(g, "Código", 14);
            SetFill(g, "SKU", 12);
            SetFill(g, "Producto", 23);
            SetFill(g, "Categoría", 13);
            SetFill(g, "Marca", 12);
            SetFill(g, "Precio", 11);
            SetFill(g, "Stock", 8);
            SetFill(g, "Mínimo", 8);
            SetFill(g, "Web", 10);
            SetFill(g, "Estado", 10);
        }

        private TextBox SearchBox() { return new TextBox { Width = 270, Height = 30, Font = Theme.Font(10, FontStyle.Regular), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }; }

        private void EditProduct(Product existing)
        {
            try
            {
                using (Form f = ProductDialog(existing))
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        _store.SaveProduct((Product)f.Tag);
                        try { _centralSync.SyncOnce(); } catch { }
                        ShowPage("Productos", BuildProducts);
                    }
                }
            }
            finally { _androidBarcodeHandler = null; }
        }

        private Form ProductDialog(Product existing)
        {
            Form f = Dialog("Producto");
            f.ClientSize = new Size(720, 600);
            f.MinimumSize = new Size(650, 540);

            TableLayoutPanel dialogLayout = new TableLayoutPanel
            { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background, Padding = new Padding(0) };
            dialogLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            dialogLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));
            f.Controls.Add(dialogLayout);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
            TabPage general = Tab("General");
            TabPage inventory = Tab("Inventario");
            TabPage online = Tab("Tienda online");
            tabs.TabPages.Add(general); tabs.TabPages.Add(inventory); tabs.TabPages.Add(online);
            dialogLayout.Controls.Add(tabs, 0, 0);

            string imagePathForGeneral = existing == null ? "" : (existing.ImagePath ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string barcodeImagePathForProduct = existing == null ? "" : existing.BarcodeImagePath;
            ScannerTextBox barcode = new ScannerTextBox { Text = existing == null ? "" : existing.Barcode, Width = 360, Height = 28, BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Location = new Point(20, 38), Font = Theme.Font(10, FontStyle.Regular) };
            general.Controls.Add(new Label { Text = "Código de barras", AutoSize = true, ForeColor = Theme.Muted, Location = new Point(20, 18), Font = Theme.Font(8.5f, FontStyle.Bold) });
            Button scan = Theme.Secondary("ESCANEAR"); scan.Location = new Point(395, 38); scan.Width = 115; general.Controls.Add(scan);
            scan.Click += delegate { using (ScanBarcodeForm sf = new ScanBarcodeForm()) if (sf.ShowDialog(f) == DialogResult.OK) { barcode.Text = sf.Barcode; barcode.Focus(); } };
            barcode.BarcodeScanned += delegate { barcode.Focus(); };
            Button androidScan = Theme.Secondary("ANDROID"); androidScan.Location = new Point(520, 38); androidScan.Width = 110; general.Controls.Add(androidScan);
            androidScan.Click += delegate { _androidBarcodeHandler = delegate(string code) { barcode.Text = code; barcode.Focus(); }; MessageBox.Show("El teléfono queda asociado al campo de código. Si está conectado por USB y autorizado, NexoMarket abrirá automáticamente el escáner Android.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            PictureBox barcodePreview = new PictureBox { Location = new Point(470, 220), Size = new Size(190, 72), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle }; general.Controls.Add(barcodePreview);
            Action loadBarcodePreview = delegate { try { if (!string.IsNullOrWhiteSpace(barcodeImagePathForProduct) && File.Exists(barcodeImagePathForProduct)) using (Image img = Image.FromFile(barcodeImagePathForProduct)) barcodePreview.Image = new Bitmap(img); } catch { } }; loadBarcodePreview();
            Button generateBarcode = Theme.Secondary("GENERAR CÓDIGO"); generateBarcode.Location = new Point(470, 300); generateBarcode.Width = 110; general.Controls.Add(generateBarcode); generateBarcode.Click += delegate { barcode.Text = GenerateEan13(); barcodeImagePathForProduct = GenerateEan13Image(barcode.Text); try { using (Image img = Image.FromFile(barcodeImagePathForProduct)) barcodePreview.Image = new Bitmap(img); } catch { } barcode.Focus(); };
            TextBox sku = FieldInPanel(general, "SKU / código interno", existing == null ? "" : existing.SKU, 82);
            TextBox name = FieldInPanel(general, "Nombre del producto", existing == null ? "" : existing.Name, 146);
            TextBox cat = FieldInPanel(general, "Categoría", existing == null ? "" : existing.Category, 210);
            TextBox brand = FieldInPanel(general, "Marca", existing == null ? "" : existing.Brand, 274);
            TextBox color = FieldInPanel(general, "Color", existing == null ? "" : existing.Color, 338);
            TextBox size = FieldInPanel(general, "Talle / variante", existing == null ? "" : existing.Size, 402);
            // Vista de imagen en la pestaña General: el producto se ve mientras se carga.
            PictureBox productPreview = new PictureBox { Location = new Point(470, 95), Size = new Size(190, 190), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card2, BorderStyle = BorderStyle.FixedSingle };
            general.Controls.Add(productPreview);
            Button uploadGeneralImage = Theme.Secondary("SUBIR IMAGEN"); uploadGeneralImage.Location = new Point(470, 390); uploadGeneralImage.Width = 92; general.Controls.Add(uploadGeneralImage);
            Button cameraGeneralImage = Theme.Secondary("📷 CÁMARA"); cameraGeneralImage.Location = new Point(568, 390); cameraGeneralImage.Width = 92; general.Controls.Add(cameraGeneralImage);
            Label imageHint = new Label { Text = "Imagen principal\r\nJPG / PNG / BMP · Cámara", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Width = 190, Height = 42, Location = new Point(470, 430), ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) }; general.Controls.Add(imageHint);
            Action loadPreview = delegate { string fp = (existing == null ? "" : existing.ImagePath ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(); try { if (!string.IsNullOrWhiteSpace(fp) && File.Exists(fp)) using (Image img = Image.FromFile(fp)) productPreview.Image = new Bitmap(img); } catch { } };
            loadPreview();

            TextBox cost = FieldInPanel(inventory, "Costo", existing == null ? "0" : existing.Cost.ToString("0.00"), 18);
            TextBox price = FieldInPanel(inventory, "Precio de venta", existing == null ? "0" : existing.Price.ToString("0.00"), 82);
            TextBox sale = FieldInPanel(inventory, "Precio promocional", existing == null ? "0" : existing.SalePrice.ToString("0.00"), 146);
            TextBox stock = FieldInPanel(inventory, "Stock actual", existing == null ? "0" : existing.Stock.ToString(), 210);
            TextBox min = FieldInPanel(inventory, "Stock mínimo", existing == null ? "0" : existing.MinimumStock.ToString(), 274);
            TextBox variants = FieldInPanel(inventory, "Variantes / medidas", existing == null ? "" : existing.Variants, 338);
            TextBox tax = FieldInPanel(inventory, "Impuesto %", existing == null ? "0" : existing.TaxRate.ToString("0.##"), 402);
            CheckBox active = new CheckBox { Text = "Producto activo", Checked = existing == null || existing.Active, ForeColor = Theme.Text, Location = new Point(20, 470), AutoSize = true }; inventory.Controls.Add(active);

            TextBox slug = FieldInPanel(online, "URL amigable (slug)", existing == null ? "" : existing.Slug, 18);
            TextBox publicName = FieldInPanel(online, "Descripción pública", existing == null ? "" : (existing.PublicDescription.Length == 0 ? existing.Description : existing.PublicDescription), 82);
            publicName.Multiline = true; publicName.Height = 70;
            TextBox image = FieldInPanel(online, "Imágenes del producto", imagePathForGeneral, 176); image.Width = 500; image.ReadOnly = true;
            uploadGeneralImage.Click += delegate { using (OpenFileDialog dlg = new OpenFileDialog()) { dlg.Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.bmp;*.gif"; if (dlg.ShowDialog(f) == DialogResult.OK) { try { string dest = Path.Combine(_store.MediaDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_producto_" + Path.GetFileName(dlg.FileName)); File.Copy(dlg.FileName, dest, true); imagePathForGeneral = dest; image.Text = dest; using (Image img = Image.FromFile(dest)) productPreview.Image = new Bitmap(img); _store.AddMedia(Path.GetFileName(dest), dest, "Imagen", name.Text.Trim()); } catch (Exception ex) { MessageBox.Show("No se pudo cargar la imagen.\r\n" + ex.Message); } } } };
            cameraGeneralImage.Click += delegate {
                using (CameraCaptureForm cf = new CameraCaptureForm(_store.MediaDirectory))
                {
                    if (cf.ShowDialog(f) == DialogResult.OK && File.Exists(cf.CapturedFile))
                    {
                        try
                        {
                            imagePathForGeneral = cf.CapturedFile;
                            image.Text = cf.CapturedFile;
                            using (Image img = Image.FromFile(cf.CapturedFile)) productPreview.Image = new Bitmap(img);
                            _store.AddMedia(Path.GetFileName(cf.CapturedFile), cf.CapturedFile, "Imagen", name.Text.Trim());
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("La foto se capturó, pero no se pudo asociar al producto.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }
            };
            Button uploadImages = Theme.Secondary("SUBIR IMÁGENES"); uploadImages.Location = new Point(535, 196); uploadImages.Width = 120; online.Controls.Add(uploadImages);
            Button mediaLibrary = Theme.Secondary("MULTIMEDIA"); mediaLibrary.Location = new Point(410, 196); mediaLibrary.Width = 115; online.Controls.Add(mediaLibrary);
            mediaLibrary.Click += delegate
            {
                List<MediaItem> available = _store.GetMedia().Where(m => string.Equals(m.Type, "Imagen", StringComparison.OrdinalIgnoreCase) && File.Exists(m.Path)).ToList();
                if (available.Count == 0) { MessageBox.Show("Todavía no hay imágenes en Multimedia.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                using (Form mf = Dialog("Seleccionar imagen de Multimedia"))
                {
                    mf.ClientSize = new Size(620, 430); ListBox list = new ListBox { Dock = DockStyle.Top, Height = 300, BackColor = Theme.Card2, ForeColor = Theme.Text };
                    foreach (MediaItem mi in available) list.Items.Add(mi.FileName + " · " + mi.Path); mf.Controls.Add(list);
                    Button use = Theme.Primary("USAR IMAGEN SELECCIONADA"); use.Dock = DockStyle.Bottom; use.Height = 48; mf.Controls.Add(use);
                    use.Click += delegate { if (list.SelectedIndex < 0) return; MediaItem mi = available[list.SelectedIndex]; imagePathForGeneral = mi.Path; image.Text = mi.Path; try { using (Image img = Image.FromFile(mi.Path)) productPreview.Image = new Bitmap(img); } catch { } mf.DialogResult = DialogResult.OK; mf.Close(); };
                    mf.ShowDialog(f);
                }
            };
            uploadImages.Click += delegate { using (OpenFileDialog dlg = new OpenFileDialog()) { dlg.Multiselect = true; dlg.Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.gif;*.bmp"; if (dlg.ShowDialog(f) == DialogResult.OK) { List<string> saved = new List<string>(); foreach (string source in dlg.FileNames) { try { string dest = Path.Combine(_store.MediaDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Path.GetFileName(source)); File.Copy(source, dest, true); saved.Add(dest); _store.AddMedia(Path.GetFileName(dest), dest, "Imagen", name.Text.Trim()); } catch { } } image.Text = string.Join(";", saved.ToArray()); } } };
            CheckBox onlineEnabled = new CheckBox { Text = "Publicar este producto en la tienda web", Checked = existing == null || existing.OnlineEnabled, ForeColor = Theme.Text, Location = new Point(20, 244), AutoSize = true }; online.Controls.Add(onlineEnabled);
            Button previewWeb = Theme.Primary("VISTA PREVIA CLIENTE"); previewWeb.Location = new Point(240, 244); previewWeb.Width = 180; online.Controls.Add(previewWeb);
            previewWeb.Click += delegate { Product previewProduct = new Product(); previewProduct.Name = name.Text.Trim(); previewProduct.Description = publicName.Text.Trim(); previewProduct.PublicDescription = publicName.Text.Trim(); previewProduct.Price = pvSafe(price.Text); previewProduct.SalePrice = pvSafe(sale.Text); previewProduct.ImagePath = image.Text.Trim(); previewProduct.Brand = brand.Text.Trim(); previewProduct.Size = size.Text.Trim(); previewProduct.Color = color.Text.Trim(); using (Form wf = ProductWebPreviewForm(previewProduct)) wf.ShowDialog(f); };
            Label slugHelp = new Label { Text = "URL amigable: es el nombre corto que identifica al producto en la web. Ejemplo: remera-roja-cuello-redondo. Se genera automáticamente si lo dejás vacío.", AutoSize = false, Width = 610, Height = 48, Location = new Point(20, 285), ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) }; online.Controls.Add(slugHelp);
            Label webInfo = new Label { Text = "Podés cargar varias imágenes; se guardan separadas y quedan disponibles para la futura ficha web del producto.", AutoSize = false, Width = 610, Height = 42, Location = new Point(20, 340), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) }; online.Controls.Add(webInfo);

            FlowLayoutPanel buttonBar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(12, 8, 18, 8) };
            Button save = Theme.Primary("GUARDAR PRODUCTO"); save.Width = 190;
            Button cancel = Theme.Secondary("CANCELAR"); cancel.Width = 120;
            buttonBar.Controls.Add(save); buttonBar.Controls.Add(cancel);
            dialogLayout.Controls.Add(buttonBar, 0, 1);
            cancel.Click += delegate { f.DialogResult = DialogResult.Cancel; f.Close(); };
            save.Click += delegate
            {
                decimal pv, sv, cv, tr; int st, mn;
                if (!decimal.TryParse(price.Text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out pv) ||
                    !decimal.TryParse(sale.Text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out sv) ||
                    !decimal.TryParse(cost.Text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out cv) ||
                    !decimal.TryParse(tax.Text.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tr) ||
                    !int.TryParse(stock.Text, out st) || !int.TryParse(min.Text, out mn) || name.Text.Trim().Length == 0)
                {
                    MessageBox.Show("Revisá nombre, precios, costo, impuesto y stock.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Product product = existing == null ? new Product() : existing;
                product.Barcode = barcode.Text.Trim(); product.SKU = sku.Text.Trim(); product.Name = name.Text.Trim(); product.Category = cat.Text.Trim();
                product.Brand = brand.Text.Trim(); product.Color = color.Text.Trim(); product.Size = size.Text.Trim(); product.Cost = cv; product.Price = pv; product.SalePrice = sv;
                product.Stock = st; product.MinimumStock = mn; product.Variants = variants.Text.Trim(); product.TaxRate = tr; product.Active = active.Checked;
                product.Slug = slug.Text.Trim(); product.PublicDescription = publicName.Text.Trim(); product.BarcodeImagePath = barcodeImagePathForProduct ?? ""; product.ImagePath = string.IsNullOrWhiteSpace(imagePathForGeneral) ? image.Text.Trim() : imagePathForGeneral + (image.Text.Trim().Length > 0 && image.Text.Trim() != imagePathForGeneral ? ";" + image.Text.Trim() : ""); product.OnlineEnabled = onlineEnabled.Checked;
                if (product.Slug.Length == 0) product.Slug = Slugify(product.Name);
                f.Tag = product; f.DialogResult = DialogResult.OK; f.Close();
            };
            f.AcceptButton = save;
            barcode.Focus();
            return f;
        }

        private string GenerateEan13()
        {
            string base12 = "779" + (DateTime.Now.Ticks % 1000000000L).ToString("D9");
            int sum=0; for(int i=0;i<12;i++){int d=base12[i]-'0'; sum += (i%2==0)?d:d*3;} int check=(10-(sum%10))%10; return base12+check.ToString();
        }

        private string GenerateEan13Image(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length != 13) return "";
            string[] L = { "0001101","0011001","0010011","0111101","0100011","0110001","0101111","0111011","0110111","0001011" };
            string[] G = { "0100111","0110011","0011011","0100001","0011101","0111001","0000101","0010001","0001001","0010111" };
            string[] R = { "1110010","1100110","1101100","1000010","1011100","1001110","1010000","1000100","1001000","1110100" };
            string[] P = { "AAAAAA","AABABB","AABBAB","AABBBA","ABAABB","ABBAAB","ABBBAA","ABABAB","ABABBA","ABBABA" };
            StringBuilder bits=new StringBuilder("101"); int first=code[0]-'0'; string parity=P[first];
            for(int i=1;i<=6;i++){int d=code[i]-'0'; bits.Append(parity[i-1]=='A'?L[d]:G[d]);} bits.Append("01010");
            for(int i=7;i<=12;i++){int d=code[i]-'0'; bits.Append(R[d]);} bits.Append("101");
            int width=bits.Length*3+40; int height=120; Bitmap bmp=new Bitmap(width,height); using(Graphics g=Graphics.FromImage(bmp)){g.Clear(Color.White); using(SolidBrush b=new SolidBrush(Color.Black)){int x=20; for(int i=0;i<bits.Length;i++){if(bits[i]=='1')g.FillRectangle(b,x,10,3,82); x+=3;} using(Font f=new Font("Arial",9f)){g.DrawString(code,new Font("Arial",9f),b,25,96);}}} string path=Path.Combine(_store.MediaDirectory,"barcode_"+code+".png"); bmp.Save(path,System.Drawing.Imaging.ImageFormat.Png); bmp.Dispose(); return path;
        }

        private decimal pvSafe(string text)
        {
            decimal value;
            if (!decimal.TryParse((text ?? "0").Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return 0m;
            return value;
        }

        private string FirstProductImage(Product p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.ImagePath)) return "";
            string[] paths = p.ImagePath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in paths)
            {
                string path = ResolveImagePath(raw);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            return paths.Length > 0 ? ResolveImagePath(paths[0]) : "";
        }

        private string ResolveImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string value = path.Trim().Trim('\"');
            try
            {
                if (File.Exists(value)) return Path.GetFullPath(value);
                if (!Path.IsPathRooted(value))
                {
                    string mediaPath = Path.Combine(_store.MediaDirectory, value);
                    if (File.Exists(mediaPath)) return Path.GetFullPath(mediaPath);
                    string appPath = Path.Combine(Application.StartupPath, value);
                    if (File.Exists(appPath)) return Path.GetFullPath(appPath);
                }
            }
            catch { }
            return value;
        }

        private void ReplacePicture(PictureBox box, string path)
        {
            if (box == null || box.IsDisposed) return;
            Image old = box.Image;
            box.Image = null;
            if (old != null) old.Dispose();
            string resolved = ResolveImagePath(path);
            if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved)) return;
            try
            {
                using (Image img = Image.FromFile(resolved))
                {
                    box.Image = new Bitmap(img);
                }
            }
            catch { box.Image = null; }
        }

        private Form ProductWebPreviewForm(Product p)
        {
            Form f = Dialog("Vista previa · Tienda online"); f.ClientSize = new Size(720, 620);
            Panel shell = Theme.Card(); shell.Dock = DockStyle.Fill; shell.Padding = new Padding(22); f.Controls.Add(shell);
            PictureBox picture = new PictureBox { Location = new Point(22, 22), Size = new Size(300, 300), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card2 };
            string firstImage = (p.ImagePath ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            try { if (!string.IsNullOrWhiteSpace(firstImage) && File.Exists(firstImage)) using (Image img = Image.FromFile(firstImage)) picture.Image = new Bitmap(img); } catch { }
            shell.Controls.Add(picture);
            Label name = new Label { Text = string.IsNullOrWhiteSpace(p.Name) ? "Nombre del producto" : p.Name, AutoSize = false, Width = 340, Height = 55, Location = new Point(350, 28), ForeColor = Theme.Text, Font = Theme.Font(20, FontStyle.Bold) }; shell.Controls.Add(name);
            Label brand = new Label { Text = p.Brand ?? "", AutoSize = true, Location = new Point(350, 92), ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold) }; shell.Controls.Add(brand);
            Label price = new Label { Text = (p.SalePrice > 0 ? p.SalePrice : p.Price).ToString("C"), AutoSize = true, Location = new Point(350, 125), ForeColor = Theme.Green, Font = Theme.Font(24, FontStyle.Bold) }; shell.Controls.Add(price);
            Label variants = new Label { Text = "Talle: " + (p.Size ?? "-") + "    Color: " + (p.Color ?? "-"), AutoSize = false, Width = 340, Height = 42, Location = new Point(350, 180), ForeColor = Theme.Text, Font = Theme.Font(10, FontStyle.Regular) }; shell.Controls.Add(variants);
            Label desc = new Label { Text = string.IsNullOrWhiteSpace(p.PublicDescription) ? "Descripción del producto para el cliente." : p.PublicDescription, AutoSize = false, Width = 620, Height = 120, Location = new Point(22, 350), ForeColor = Theme.Muted, Font = Theme.Font(10, FontStyle.Regular) }; shell.Controls.Add(desc);
            Button close = Theme.Primary("CERRAR VISTA PREVIA"); close.Width = 190; close.Location = new Point(480, 535); close.Click += delegate { f.Close(); }; shell.Controls.Add(close);
            return f;
        }

        private Form InvoiceDraftForm(string type, string cuit, string point)
        {
            Form f = Dialog("Borrador de comprobante " + type); f.ClientSize = new Size(760, 620);
            StringBuilder invoice = new StringBuilder();
            invoice.Append("NEXOMARKET\r\n").Append(type.ToUpperInvariant()).Append("\r\n");
            invoice.Append("BORRADOR - NO FISCAL\r\n\r\n");
            invoice.Append("CUIT: ").Append(string.IsNullOrWhiteSpace(cuit) ? "________________" : cuit).Append("    Pto. Venta: ").Append(string.IsNullOrWhiteSpace(point) ? "0001" : point).Append("\r\n");
            invoice.Append("Comprobante: ").Append(type).Append("\r\n");
            invoice.Append("Fecha: ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).Append("\r\n\r\n");
            invoice.Append("Cliente: ______________________________\r\n");
            invoice.Append("CUIT/DNI: ______________________________\r\n\r\n");
            invoice.Append("DETALLE\r\n");
            invoice.Append("------------------------------------------------------------\r\n");
            decimal invoiceTotal = 0m;
            foreach (CartLine line in _lastCompletedCart) { invoice.Append(line.Product.Name).Append("  ").Append(line.Quantity).Append(" x ").Append(line.UnitPrice.ToString("C")).Append(" = ").Append(line.Total.ToString("C")).Append("\r\n"); invoiceTotal += line.Total; }
            invoice.Append("------------------------------------------------------------\r\n");
            invoice.Append("TOTAL: ").Append(invoiceTotal.ToString("C")).Append("\r\n\r\n");
            invoice.Append("CAE: (se asigna únicamente luego de la autorización de ARCA)\r\n");
            invoice.Append("Vencimiento CAE: ______________________________\r\n");
            TextBox body = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Color.Black, Font = new Font("Consolas", 10f), Text = invoice.ToString() };
            f.Controls.Add(body);
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            Button copy=Theme.Primary("COPIAR"); copy.Width=130; copy.Click+=delegate{try{Clipboard.SetText(body.Text);MessageBox.Show("Borrador copiado al portapapeles.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch{}}; bar.Controls.Add(copy);
            Button close=Theme.Secondary("CERRAR"); close.Width=120; close.Click+=delegate{f.Close();}; bar.Controls.Add(close); f.Controls.Add(bar);
            return f;
        }

        private string Slugify(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "producto";
            string s = text.Trim().ToLowerInvariant();
            string normalized = s.Normalize(System.Text.NormalizationForm.FormD);
            StringBuilder b = new StringBuilder();
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) b.Append(c); else if (b.Length > 0 && b[b.Length - 1] != '-') b.Append('-');
            }
            return b.ToString().Trim('-');
        }

        private TextBox Field(Form f, string label, string value, int y)
        {
            f.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted, Location = new Point(30, y), Font = Theme.Font(8.5f, FontStyle.Bold) });
            TextBox t = new TextBox { Text = value, Width = 420, Height = 28, BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Location = new Point(30, y + 20) }; f.Controls.Add(t); return t;
        }

        private Form Dialog(string title)
        {
            Form f = new Form
            {
                Text = "NexoMarket · " + title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = true,
                MinimizeBox = false,
                MinimumSize = new Size(470, 400),
                ClientSize = new Size(470, 485),
                AutoScroll = true,
                AutoScrollMargin = new Size(0, 12),
                BackColor = Theme.Background,
                ForeColor = Theme.Text
            };
            return f;
        }

        private Control BuildOrders()
        {
            Panel page = Page();
            FlowLayoutPanel filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false };
            string[] statuses = { "Todos", "Pendiente", "Preparando", "Listo", "En reparto", "Entregado", "Cancelado" };
            foreach (string status in statuses) { Button b = Theme.Secondary(status); b.Width = 105; filters.Controls.Add(b); b.Click += delegate { DataGridView g = page.Controls.OfType<DataGridView>().FirstOrDefault(); if (g != null) { g.DataSource = _store.GetOrders(b.Text == "Todos" ? "" : b.Text).Select(OrderRow).ToList(); ConfigureOrderGrid(g); } }; }
            page.Controls.Add(filters);
            DataGridView grid = Theme.Grid(); grid.Dock = DockStyle.Top; grid.Height = 560; grid.DataSource = _store.GetOrders("").Select(OrderRow).ToList(); ConfigureOrderGrid(grid); page.Controls.Add(grid);
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { OpenOrderFromGrid(grid, e.RowIndex); };
            return page;
        }

        private void OpenOrderFromGrid(DataGridView grid, int rowIndex)
        {
            if (rowIndex < 0 || !grid.Columns.Contains("Id")) return; long id = Convert.ToInt64(grid.Rows[rowIndex].Cells["Id"].Value); Order o = _store.GetOrders("").FirstOrDefault(x => x.Id == id); if (o == null) return;
            using (Form f = OrderDialog(o)) if (f.ShowDialog(this) == DialogResult.OK) ShowPage("Pedidos nuevos", BuildOrders);
        }

        private object OrderRow(Order o) { return new { Id = o.Id, Cliente = o.CustomerName, Teléfono = o.Phone, Entrega = o.Fulfillment, Estado = o.Status, Pago = o.PaymentStatus, Comprobante = string.IsNullOrWhiteSpace(o.PaymentProofPath) ? "NO" : "SI", Total = o.Total, Fecha = o.CreatedAt.ToString("dd/MM/yyyy HH:mm") }; }

        private Form OrderDialog(Order o)
        {
            Form f = Dialog("Pedido #" + o.Id); f.ClientSize = new Size(980, 700);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(18) }; root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f)); f.Controls.Add(root);
            Panel left = Theme.Card(); left.Dock = DockStyle.Fill; left.Padding = new Padding(14); root.Controls.Add(left, 0, 0);
            Label h = H2("PEDIDO #" + o.Id + " · " + o.CustomerName); h.Dock = DockStyle.Top; h.Height = 34; left.Controls.Add(h);
            Label customer = new Label { Text = "Correo: " + o.CustomerEmail + "\r\nTeléfono: " + o.Phone + "\r\nEntrega: " + o.Fulfillment + "\r\nDirección: " + o.Address + "\r\nCódigo postal: " + o.PostalCode + "\r\nNotas: " + o.Notes, AutoSize = false, Dock = DockStyle.Top, Height = 120, ForeColor = Theme.Muted, Font = Theme.Font(9.5f, FontStyle.Regular) }; left.Controls.Add(customer);
            Label ih = H2("PRODUCTOS QUE PIDIÓ EL CLIENTE"); ih.Dock = DockStyle.Top; ih.Height = 32; left.Controls.Add(ih);
            DataGridView items = Theme.Grid(); items.Dock = DockStyle.Fill; items.DataSource = ParseOrderItems(o.ItemsJson).ToList(); if (items.Columns.Contains("ProductId")) items.Columns["ProductId"].Visible = false; SetFill(items,"Producto",45); SetFill(items,"Cantidad",14); SetFill(items,"Unitario",18); SetFill(items,"Stock actual",18); left.Controls.Add(items);

            Panel right = Theme.Card(); right.Dock = DockStyle.Fill; right.Padding = new Padding(14); root.Controls.Add(right, 1, 0);
            Label ph = H2("PAGO Y COMPROBANTE"); ph.Dock = DockStyle.Top; ph.Height = 46; ph.Padding = new Padding(0, 8, 0, 0); right.Controls.Add(ph);
            Label payment = new Label { Text = "Medio: " + o.PaymentMethod + "\r\nEstado: " + o.PaymentStatus + "\r\nReferencia: " + o.PaymentReference + "\r\nTotal: " + o.Total.ToString("C0"), AutoSize = false, Dock = DockStyle.Top, Height = 118, ForeColor = Theme.Muted, Font = Theme.Font(9.5f, FontStyle.Regular), Padding = new Padding(0, 6, 0, 0) }; right.Controls.Add(payment);
            PictureBox proof = new PictureBox { Dock = DockStyle.Top, Height = 220, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card2, BorderStyle = BorderStyle.FixedSingle }; ReplacePicture(proof, o.PaymentProofPath); right.Controls.Add(proof);
            Button openProof = Theme.Secondary("ABRIR COMPROBANTE"); openProof.Dock = DockStyle.Top; openProof.Height = 36; openProof.Click += delegate { if (!string.IsNullOrWhiteSpace(o.PaymentProofPath) && File.Exists(o.PaymentProofPath)) Process.Start(o.PaymentProofPath); else MessageBox.Show("Este pedido no tiene comprobante.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); }; right.Controls.Add(openProof);
            Label negotiation = H2("NEGOCIACIÓN / FALTA DE STOCK"); negotiation.Dock = DockStyle.Top; negotiation.Height = 32; right.Controls.Add(negotiation);
            TextBox sellerMessage = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, Height = 75, Text = o.SellerMessage ?? "", BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }; right.Controls.Add(sellerMessage);
            ComboBox negotiationStatus = new ComboBox { Dock = DockStyle.Top, Height = 30, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Card2, ForeColor = Theme.Text }; negotiationStatus.Items.AddRange(new object[] { "Ninguna", "Producto no disponible", "Propuesta enviada", "Propuesta aceptada", "Propuesta rechazada", "Resuelto" }); negotiationStatus.SelectedItem = negotiationStatus.Items.Contains(o.NegotiationStatus) ? o.NegotiationStatus : "Ninguna"; right.Controls.Add(negotiationStatus);
            Label buyerMsg = new Label { Text = string.IsNullOrWhiteSpace(o.BuyerMessage) ? "El comprador no respondió todavía." : "Respuesta del comprador:\r\n" + o.BuyerMessage, AutoSize = false, Dock = DockStyle.Top, Height = 70, ForeColor = Theme.Muted }; right.Controls.Add(buyerMsg);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, WrapContents = false };
            Button save = Theme.Primary("GUARDAR Y ACTUALIZAR"); save.Width = 190; save.Click += delegate { o.SellerMessage = sellerMessage.Text.Trim(); o.NegotiationStatus = Convert.ToString(negotiationStatus.SelectedItem); _store.SaveOrder(o); f.Tag = o.Status; f.DialogResult = DialogResult.OK; f.Close(); }; actions.Controls.Add(save);
            Button close = Theme.Secondary("CERRAR"); close.Width = 100; close.Click += delegate { f.Close(); }; actions.Controls.Add(close); right.Controls.Add(actions);
            return f;
        }

        private List<object> ParseOrderItems(string json)
        {
            List<object> rows = new List<object>(); if (string.IsNullOrWhiteSpace(json)) return rows; MatchCollection ms = Regex.Matches(json, "\\{[^}]*\\}");
            foreach (Match m in ms) { string item = m.Value; long id = ParseLongField(item, "productId"); int qty = (int)ParseLongField(item, "quantity"); decimal unit = ParseDecimalField(item, "unitPrice"); string name = ParseStringField(item, "name"); Product p = id > 0 ? _store.GetProducts("").FirstOrDefault(x => x.Id == id) : null; if (string.IsNullOrWhiteSpace(name) && p != null) name = p.Name; rows.Add(new { ProductId = id, Producto = name, Cantidad = qty, Unitario = unit.ToString("C0"), StockActual = p == null ? "NO DISP." : p.Stock.ToString() }); } return rows;
        }
        private long ParseLongField(string json, string key) { string v = ParseStringField(json, key); long n; return long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0; }
        private decimal ParseDecimalField(string json, string key) { string v = ParseStringField(json, key); decimal n; return decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0m; }
        private string ParseStringField(string json, string key) { Match m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?:\"(?<s>(?:\\\"|[^\"])*)\"|(?<n>-?[0-9.]+))"); return m.Success ? (m.Groups["s"].Success ? m.Groups["s"].Value.Replace("\\\"", "\"") : m.Groups["n"].Value) : ""; }

        private Control BuildInventory()
        {
            Panel page = Page();
            DashboardData d = _store.GetDashboard();
            List<Product> products = _store.GetProducts("");
            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 4, 0, 6) };
            for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            AddMetricTable(cards, 0, "PRODUCTOS", d.TotalProducts.ToString(), Theme.Accent); AddMetricTable(cards, 1, "STOCK BAJO", d.LowStock.ToString(), Theme.Danger); AddMetricTable(cards, 2, "UNIDADES", products.Sum(p => p.Stock).ToString(), Theme.Green); AddMetricTable(cards, 3, "REPOSICIÓN", products.Sum(p => Math.Max(0, p.MinimumStock * 2 - p.Stock)).ToString(), Theme.Warning);
            page.Controls.Add(cards);

            FlowLayoutPanel categoryBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, AutoScroll = true, Padding = new Padding(4, 6, 4, 4) };
            TextBox search = SearchBox(); search.Width = 230; search.PlaceholderTextSafe("Buscar producto..."); categoryBar.Controls.Add(search);
            Button all = Theme.Primary("TODAS"); all.Width = 92; categoryBar.Controls.Add(all);
            List<string> categories = products.Select(p => p.Category ?? "").Where(x => x.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            foreach (string cat in categories) { Button b = Theme.Secondary(cat); b.Width = Math.Max(105, Math.Min(180, 25 + cat.Length * 8)); b.Tag = cat; categoryBar.Controls.Add(b); }
            page.Controls.Add(categoryBar);

            TableLayoutPanel area = new TableLayoutPanel { Dock = DockStyle.Top, Height = 620, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 4, 0, 8) };
            area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70f)); area.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));
            Panel list = Theme.Card(); list.Dock = DockStyle.Fill; list.Padding = new Padding(10);
            FlowLayoutPanel tiles = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4), BackColor = Color.Transparent };
            list.Controls.Add(tiles); area.Controls.Add(list, 0, 0);

            Panel right = Theme.Card(); right.Dock = DockStyle.Fill; right.Padding = new Padding(14);
            Label rh = H2("PRODUCTO SELECCIONADO"); rh.Dock = DockStyle.Top; rh.Height = 34;
            PictureBox productImage = new PictureBox { Dock = DockStyle.Top, Height = 245, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card2, BorderStyle = BorderStyle.FixedSingle };
            Label productName = new Label { Text = "Seleccioná un producto", AutoSize = false, Dock = DockStyle.Top, Height = 46, ForeColor = Theme.Text, Font = Theme.Font(11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            Label productInfo = new Label { Text = "Elegí una categoría y tocá una tarjeta.", AutoSize = false, Dock = DockStyle.Top, Height = 80, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) };
            Button refresh = Theme.Primary("ACTUALIZAR INVENTARIO"); refresh.Dock = DockStyle.Bottom; refresh.Height = 40; refresh.Click += delegate { ShowPage("Inventario", BuildInventory); };
            Button productsBtn = Theme.Secondary("GESTIONAR PRODUCTOS"); productsBtn.Dock = DockStyle.Bottom; productsBtn.Height = 38; productsBtn.Margin = new Padding(0, 0, 0, 8); productsBtn.Click += delegate { ShowPage("Productos", BuildProducts); };
            right.Controls.Add(refresh); right.Controls.Add(productsBtn); right.Controls.Add(productInfo); right.Controls.Add(productName); right.Controls.Add(productImage); right.Controls.Add(rh); area.Controls.Add(right, 1, 0);

            Action<Product> selectProduct = delegate(Product selected)
            {
                if (selected == null) return;
                productName.Text = selected.Name;
                productInfo.Text = "SKU: " + (selected.SKU ?? "") + "\r\nStock: " + selected.Stock + "   •   Mínimo: " + selected.MinimumStock + "\r\nPrecio: " + (selected.SalePrice > 0 ? selected.SalePrice : selected.Price).ToString("C0");
                ReplacePicture(productImage, FirstProductImage(selected));
            };

            Action<string> renderTiles = delegate(string filter)
            {
                tiles.SuspendLayout(); tiles.Controls.Clear();
                IEnumerable<Product> filtered = products;
                if (!string.IsNullOrWhiteSpace(filter)) filtered = filtered.Where(p => string.Equals(p.Category ?? "", filter, StringComparison.OrdinalIgnoreCase));
                string q = search.Text.Trim();
                if (q.Length > 0) filtered = filtered.Where(p => (p.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (p.SKU ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (p.Barcode ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (Product product in filtered.OrderBy(p => p.Category).ThenBy(p => p.Name))
                {
                    Panel tile = new Panel { Width = 154, Height = 190, BackColor = Theme.Card2, Margin = new Padding(5), Padding = new Padding(7), Cursor = Cursors.Hand };
                    PictureBox pic = new PictureBox { Dock = DockStyle.Top, Height = 108, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 30, 41) }; ReplacePicture(pic, FirstProductImage(product));
                    Label n = new Label { Text = product.Name, Dock = DockStyle.Top, Height = 30, ForeColor = Theme.Text, Font = Theme.Font(8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
                    Label st = new Label { Text = "Stock " + product.Stock + "  •  " + (product.SalePrice > 0 ? product.SalePrice : product.Price).ToString("C0"), Dock = DockStyle.Fill, ForeColor = product.Stock <= product.MinimumStock ? Theme.Warning : Theme.Green, Font = Theme.Font(8, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
                    tile.Controls.Add(st); tile.Controls.Add(n); tile.Controls.Add(pic);
                    WireClick(tile, delegate { selectProduct(product); });
                    tiles.Controls.Add(tile);
                }
                tiles.ResumeLayout(true);
            };
            all.Click += delegate { renderTiles(""); };
            foreach (Control c in categoryBar.Controls) { Button b = c as Button; if (b != null && b != all) { Button categoryButton = b; categoryButton.Click += delegate(object sender, EventArgs e) { renderTiles(Convert.ToString(categoryButton.Tag)); }; } }
            search.TextChanged += delegate { renderTiles(""); };
            renderTiles("");
            page.Controls.Add(area);
            return page;
        }

        private void WireClick(Control root, EventHandler handler)
        {
            root.Click += handler;
            foreach (Control child in root.Controls) WireClick(child, handler);
        }

        private Control BuildCustomers()
        {
            Panel page = Page(); List<Customer> cs = _store.GetCustomers("");
            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 96, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 4, 0, 6) }; for (int i=0;i<3;i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.333f));
            AddMetricTable(cards,0,"CLIENTES",cs.Count.ToString(),Theme.Accent); AddMetricTable(cards,1,"COMPRAS",cs.Sum(x=>x.TotalSpent).ToString("C0"),Theme.Green); AddMetricTable(cards,2,"PEDIDOS",cs.Sum(x=>x.Orders).ToString(),Theme.Warning); page.Controls.Add(cards);
            Panel list = Theme.Card(); list.Dock = DockStyle.Top; list.Height = 650; list.Padding = new Padding(14, 18, 14, 14);
            DataGridView grid=Theme.Grid(); grid.Dock=DockStyle.Fill; grid.DataSource=cs.Select(CustomerRow).ToList(); ConfigureCustomerGrid(grid);
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock=DockStyle.Top, Height=50, WrapContents=false, Padding=new Padding(0,4,0,4) }; TextBox search=SearchBox(); search.Width=330; bar.Controls.Add(search); Button add=Theme.Primary("+ NUEVO CLIENTE"); bar.Controls.Add(add);
            Label title = H2("Clientes y actividad comercial"); title.Dock=DockStyle.Top; title.Height=34;
            // El orden de agregado evita que Dock=Fill de la grilla tape el título y la barra.
            list.Controls.Add(grid); list.Controls.Add(bar); list.Controls.Add(title); page.Controls.Add(list);
            search.TextChanged += delegate { grid.DataSource=_store.GetCustomers(search.Text).Select(CustomerRow).ToList(); ConfigureCustomerGrid(grid); }; add.Click += delegate { EditCustomer(null); };
            grid.CellDoubleClick += delegate(object sender,DataGridViewCellEventArgs e){if(e.RowIndex<0)return; long id=Convert.ToInt64(grid.Rows[e.RowIndex].Cells["Id"].Value); Customer c=_store.GetCustomers("").FirstOrDefault(x=>x.Id==id); if(c!=null) EditCustomer(c);}; return page;
        }

        private void ConfigureCustomerGrid(DataGridView g)
        {
            if (g.Columns.Contains("Id")) g.Columns["Id"].Visible=false;
            SetFill(g,"Cliente",20); SetFill(g,"Teléfono",13); SetFill(g,"Email",17); SetFill(g,"Dirección",18); SetFill(g,"Pedidos",9); SetFill(g,"Compras",13); SetFill(g,"Foto",10);
        }

        private void ConfigureOrderGrid(DataGridView g)
        {
            if (g.Columns.Contains("Id")) g.Columns["Id"].Visible=false;
            SetFill(g,"Cliente",20); SetFill(g,"Teléfono",10); SetFill(g,"Entrega",10); SetFill(g,"Estado",13); SetFill(g,"Pago",13); SetFill(g,"Comprobante",10); SetFill(g,"Total",11); SetFill(g,"Fecha",16);
        }

        private void SetFill(DataGridView g, string column, float weight)
        {
            if (g.Columns.Contains(column)) { g.Columns[column].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill; g.Columns[column].FillWeight=weight; }
        }


        private object CustomerRow(Customer c) { return new { Id = c.Id, Cliente = c.Name, Teléfono = c.Phone, Email = c.Email, Dirección = c.Address, Pedidos = c.Orders, Compras = c.TotalSpent, Foto = string.IsNullOrWhiteSpace(c.PhotoPath) ? "NO" : "SI" }; }

        private void EditCustomer(Customer existing)
        {
            using (Form f = CustomerDialog(existing)) if (f.ShowDialog(this) == DialogResult.OK) { _store.SaveCustomer((Customer)f.Tag); ShowPage("Clientes", BuildCustomers); }
        }

        private Form CustomerDialog(Customer existing)
        {
            Form f = Dialog("Cliente"); f.ClientSize = new Size(720, 470);
            TextBox name = Field(f, "Nombre", existing == null ? "" : existing.Name, 20); TextBox phone = Field(f, "Teléfono", existing == null ? "" : existing.Phone, 74); TextBox email = Field(f, "Email", existing == null ? "" : existing.Email, 128); TextBox address = Field(f, "Dirección", existing == null ? "" : existing.Address, 182); TextBox notes = Field(f, "Notas", existing == null ? "" : existing.Notes, 236);
            name.Width=285; phone.Width=285; email.Width=285; address.Width=285; notes.Width=285;
            PictureBox photo = new PictureBox { Location = new Point(335, 25), Size = new Size(125, 125), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Card2, BorderStyle = BorderStyle.FixedSingle }; f.Controls.Add(photo); string customerPhoto = existing == null ? "" : existing.PhotoPath; try { if (!string.IsNullOrWhiteSpace(customerPhoto) && File.Exists(customerPhoto)) using (Image img = Image.FromFile(customerPhoto)) photo.Image = new Bitmap(img); } catch { } Button photoBtn = Theme.Secondary("SUBIR FOTO"); photoBtn.Location = new Point(335, 160); photoBtn.Width = 125; f.Controls.Add(photoBtn); photoBtn.Click += delegate { using(OpenFileDialog dlg=new OpenFileDialog()){dlg.Filter="Imágenes|*.jpg;*.jpeg;*.png;*.bmp";if(dlg.ShowDialog(f)==DialogResult.OK){try{string dest=Path.Combine(_store.MediaDirectory,DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_cliente_"+Path.GetFileName(dlg.FileName));File.Copy(dlg.FileName,dest,true);customerPhoto=dest;using(Image img=Image.FromFile(dest))photo.Image=new Bitmap(img);}catch{}}}};
            Button save = Theme.Primary("GUARDAR"); save.Location = new Point(315, 360); save.Width = 150; save.Click += delegate { if (name.Text.Trim().Length == 0) { MessageBox.Show("Ingresá el nombre del cliente.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; } Customer c = existing == null ? new Customer() : existing; c.Name = name.Text.Trim(); c.Phone = phone.Text.Trim(); c.Email = email.Text.Trim(); c.Address = address.Text.Trim(); c.Notes = notes.Text.Trim(); c.PhotoPath = customerPhoto; f.Tag = c; f.DialogResult = DialogResult.OK; f.Close(); }; f.Controls.Add(save); return f;
        }

        private Control BuildMedia()
        {
            Panel page = Page();
            Panel drop = Theme.Card(); drop.Dock = DockStyle.Top; drop.Height = 170; drop.AllowDrop = true;
            Label text = new Label { Text = "MULTIMEDIA\r\n\r\nArrastrá fotos o videos aquí. También podés importar archivos o usar la webcam conectada a esta computadora.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, Font = Theme.Font(11, FontStyle.Bold) }; drop.Controls.Add(text); drop.DragEnter += MediaDragEnter; drop.DragDrop += MediaDragDrop; page.Controls.Add(drop);
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 60, WrapContents = false, Padding = new Padding(4, 10, 4, 5) };
            Button import = Theme.Primary("IMPORTAR ARCHIVOS"); import.Click += delegate { ImportMedia(); }; bar.Controls.Add(import);
            Button camera = Theme.Secondary("CAPTURAR DESDE CÁMARA DEL EQUIPO"); camera.Click += delegate { CaptureFromCamera(); }; bar.Controls.Add(camera);
            Button webPreview = Theme.Primary("VISTA PREVIA TIENDA WEB"); webPreview.Click += delegate { Product first = _store.GetProducts("").FirstOrDefault(x => x.Active && x.OnlineEnabled); if (first == null) { MessageBox.Show("Primero creá un producto y marcá la opción de publicar en la tienda web.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; } using (Form wf = ProductWebPreviewForm(first)) wf.ShowDialog(this); }; bar.Controls.Add(webPreview); page.Controls.Add(bar);
            DataGridView grid = Theme.Grid(); grid.Dock = DockStyle.Top; grid.Height = 480; grid.DataSource = _store.GetMedia().Select(m => new { m.Id, Archivo = m.FileName, Tipo = m.Type, Producto = m.ProductName, Ruta = m.Path }).ToList(); page.Controls.Add(grid); return page;
        }

        private void MediaDragEnter(object sender, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }
        private void MediaDragDrop(object sender, DragEventArgs e) { SaveMediaFiles((string[])e.Data.GetData(DataFormats.FileDrop)); ShowPage("Multimedia", BuildMedia); }
        private void ImportMedia()
        {
            using (OpenFileDialog dlg = new OpenFileDialog()) { dlg.Multiselect = true; dlg.Filter = "Imágenes y videos|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.mp4;*.webm;*.mov|Todos|*.*"; if (dlg.ShowDialog(this) == DialogResult.OK) { SaveMediaFiles(dlg.FileNames); ShowPage("Multimedia", BuildMedia); } }
        }
        private void SaveMediaFiles(IEnumerable<string> files)
        {
            foreach (string source in files) { if (!File.Exists(source)) continue; string ext = Path.GetExtension(source).ToLowerInvariant(); string type = IsImage(ext) ? "Imagen" : (IsVideo(ext) ? "Video" : "Archivo"); string dest = Path.Combine(_store.MediaDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Path.GetFileName(source)); try { File.Copy(source, dest, true); _store.AddMedia(Path.GetFileName(dest), dest, type, ""); } catch (Exception ex) { MessageBox.Show("No se pudo copiar " + Path.GetFileName(source) + "\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
        }
        private bool IsImage(string ext) { return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".gif" || ext == ".bmp"; }
        private bool IsVideo(string ext) { return ext == ".mp4" || ext == ".webm" || ext == ".mov"; }
        private void CaptureFromCamera()
        {
            using (CameraCaptureForm f = new CameraCaptureForm(_store.MediaDirectory)) if (f.ShowDialog(this) == DialogResult.OK && File.Exists(f.CapturedFile)) { _store.AddMedia(Path.GetFileName(f.CapturedFile), f.CapturedFile, "Imagen", "Captura webcam local"); ShowPage("Multimedia", BuildMedia); }
        }

        private Control BuildStats()
        {
            Panel page = Page(); DashboardData d = _store.GetDashboard(); List<Order> orders = _store.GetOrders(""); List<Product> products = _store.GetProducts("");
            TableLayoutPanel cards = new TableLayoutPanel { Dock=DockStyle.Top, Height=90, ColumnCount=4, RowCount=1, Padding=new Padding(0,4,0,6) }; for(int i=0;i<4;i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25f));
            AddMetricTable(cards,0,"VENTAS",orders.Where(o=>o.Status!="Cancelado").Sum(o=>o.Total).ToString("C0"),Theme.Green); AddMetricTable(cards,1,"PEDIDOS",orders.Count.ToString(),Theme.Accent); AddMetricTable(cards,2,"CLIENTES",d.TotalCustomers.ToString(),Theme.Warning); AddMetricTable(cards,3,"STOCK BAJO",d.LowStock.ToString(),Theme.Danger); page.Controls.Add(cards);
            TableLayoutPanel charts = new TableLayoutPanel { Dock=DockStyle.Top, Height=390, ColumnCount=2, RowCount=1, Padding=new Padding(0,8,0,8) }; charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55f)); charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45f));
            Panel bars=Theme.Card(); bars.Dock=DockStyle.Fill; Label bh=H2("Pedidos por estado"); bh.Dock=DockStyle.Top; bh.Height=34; bars.Controls.Add(bh); bars.Paint += delegate(object sender,PaintEventArgs e){ DrawOrderChart(e.Graphics,bars.ClientRectangle); }; charts.Controls.Add(bars,0,0);
            Panel pie=Theme.Card(); pie.Dock=DockStyle.Fill; Label ph=H2("Distribución de productos"); ph.Dock=DockStyle.Top; ph.Height=34; pie.Controls.Add(ph); pie.Paint += delegate(object sender,PaintEventArgs e){ DrawProductPie(e.Graphics,pie.ClientRectangle,products); }; charts.Controls.Add(pie,1,0); page.Controls.Add(charts);
            Panel bottom=Theme.Card(); bottom.Dock=DockStyle.Top; bottom.Height=155; bottom.Padding=new Padding(18); Label info=new Label { Text="INDICADORES CLAVE\r\nVentas de hoy: "+d.TodaySales.ToString("C0")+"\r\nDelivery pendiente: "+d.DeliveryPending+"\r\nProductos activos: "+d.TotalProducts+"\r\nUnidades en stock: "+products.Sum(x=>x.Stock)+"\r\nTicket promedio: "+(orders.Count==0?0m:orders.Where(o=>o.Status!="Cancelado").Average(o=>o.Total)).ToString("C0"), AutoSize=false, Dock=DockStyle.Fill, ForeColor=Theme.Muted, Font=Theme.Font(9.5f,FontStyle.Regular) }; bottom.Controls.Add(info); page.Controls.Add(bottom); return page;
        }

        private void DrawOrderChart(Graphics g, Rectangle bounds)
        {
            int[] vals={_store.GetOrders("Pendiente").Count,_store.GetOrders("Preparando").Count,_store.GetOrders("Listo").Count,_store.GetOrders("En reparto").Count,_store.GetOrders("Entregado").Count}; string[] labels={"Pend.","Prep.","Listos","Reparto","Entreg."}; Color[] colors={Theme.Accent,Theme.Warning,Theme.Green,Color.FromArgb(160,115,255),Color.FromArgb(70,170,220)}; int max=Math.Max(1,vals.Max()); int left=35; int bottom=bounds.Height-45; int available=Math.Max(120,bounds.Width-60); int barW=Math.Max(28,(available-40)/5);
            using(Pen axis=new Pen(Theme.Line)) g.DrawLine(axis,left,bottom,bounds.Width-20,bottom);
            for(int i=0;i<vals.Length;i++){int h=(int)((bounds.Height-100)*vals[i]/(double)max); int x=left+i*(barW+8); using(Brush b=new SolidBrush(colors[i])) g.FillRectangle(b,x,bottom-h,barW,h); using(Brush b=new SolidBrush(Theme.Muted)){g.DrawString(vals[i].ToString(),Theme.Font(8,FontStyle.Bold),b,x+2,bottom-h-18);g.DrawString(labels[i],Theme.Font(7.5f,FontStyle.Bold),b,x,bottom+8);}}
        }

        private void DrawProductPie(Graphics g, Rectangle bounds, List<Product> products)
        {
            int active=products.Count(x=>x.Active); int low=products.Count(x=>x.Active&&x.Stock<=x.MinimumStock); int inactive=products.Count(x=>!x.Active); int[] vals={Math.Max(0,active-low),low,inactive}; Color[] c={Theme.Green,Theme.Danger,Theme.Muted}; int total=Math.Max(1,vals.Sum()); int size=Math.Min(190,Math.Max(120,bounds.Height-100)); Rectangle r=new Rectangle(24,55,size,size); float start=0;
            for(int i=0;i<vals.Length;i++){float sweep=360f*vals[i]/total;using(Brush b=new SolidBrush(c[i]))g.FillPie(b,r,start,sweep);start+=sweep;}
            using(Pen p=new Pen(Theme.Line,2))g.DrawEllipse(p,r); using(Brush b=new SolidBrush(Theme.Muted)){int x=r.Right+22;g.DrawString("Activos: "+(active-low),Theme.Font(8.5f,FontStyle.Bold),b,x,75);g.DrawString("Stock bajo: "+low,Theme.Font(8.5f,FontStyle.Bold),b,x,115);g.DrawString("Inactivos: "+inactive,Theme.Font(8.5f,FontStyle.Bold),b,x,155);}
        }


        private void DrawOrderChart(Panel panel) { }

        private void DrawProductPie(Panel panel, List<Product> products) { }

        private Control BuildAndroid()
        {
            Panel page = Page();
            Panel card = Theme.Card();
            card.Dock = DockStyle.Top;
            card.Height = 520;
            card.Padding = new Padding(24);

            Label h = H2("TELÉFONO ANDROID · ESCÁNER POR USB");
            h.Dock = DockStyle.Top; h.Height = 38; card.Controls.Add(h);

            _androidStatusLabel = new Label
            {
                Text = "Android USB: esperando teléfono...",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 42,
                ForeColor = Theme.Warning,
                Font = Theme.Font(10, FontStyle.Bold),
                Padding = new Padding(0, 4, 0, 4)
            };
            card.Controls.Add(_androidStatusLabel);

            Label info = new Label
            {
                Text = "Modo de trabajo: conectás el Android por USB, NexoMarket lo detecta automáticamente y abre la cámara del teléfono. Apuntás al código de barras y el resultado entra directamente al ticket. No hay lista de productos ni checklist en el teléfono.",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 76,
                ForeColor = Theme.Muted,
                Font = Theme.Font(9.5f, FontStyle.Regular)
            };
            card.Controls.Add(info);

            Panel steps = Theme.Card();
            steps.Dock = DockStyle.Top; steps.Height = 180; steps.Margin = new Padding(0, 12, 0, 12);
            Label st = new Label
            {
                Text = "CONEXIÓN AUTOMÁTICA\r\n1. Activá Depuración USB una sola vez en Android.\r\n2. Conectá el cable USB.\r\n3. Aceptá la autorización RSA si Android la solicita.\r\n4. NexoMarket abre el escáner del teléfono automáticamente.\r\n5. Cada lectura se agrega al ticket activo.",
                AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(18),
                ForeColor = Theme.Text, Font = Theme.Font(10, FontStyle.Regular)
            };
            steps.Controls.Add(st); card.Controls.Add(steps);

            Label bt = new Label
            {
                Text = "Bluetooth queda disponible como alternativa, pero el modo recomendado para usar la cámara del teléfono es USB + ADB porque permite iniciar automáticamente la aplicación de escaneo.",
                AutoSize = false, Dock = DockStyle.Top, Height = 58, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular)
            };
            card.Controls.Add(bt);

            Button manual = Theme.Secondary("CONFIGURAR BLUETOOTH / COM");
            manual.Dock = DockStyle.Top; manual.Height = 40;
            manual.Click += delegate { using (AndroidScannerForm af = new AndroidScannerForm(delegate(string code) { HandleAndroidBarcode(code); })) af.ShowDialog(this); };
            card.Controls.Add(manual);

            Panel network = Theme.Card(); network.Dock = DockStyle.Top; network.Height = 170; network.Padding = new Padding(14);
            Label nh = H2("RED LOCAL · ALTERNATIVA A USB / BLUETOOTH"); nh.Dock = DockStyle.Top; nh.Height = 30; network.Controls.Add(nh);
            string localIp = _localScannerServer == null ? "127.0.0.1" : _localScannerServer.GetLocalIPv4();
            Label netInfo = new Label { Text = "Conectá el teléfono y la PC a la misma Wi‑Fi. URL: http://" + localIp + ":8787/scan\r\nCódigo de conexión: " + (_localScannerServer == null ? "--------" : _localScannerServer.Token) + "\r\nEl teléfono puede enviar cada lectura por red sin ADB ni COM. Esta vía queda preparada para el futuro QR de emparejamiento.", AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular) }; network.Controls.Add(netInfo);
            card.Controls.Add(network);
            page.Controls.Add(card);
            return page;
        }

        private void HandleAndroidStatus(string text)
        {
            if (_androidStatusLabel == null || _androidStatusLabel.IsDisposed) return;
            _androidStatusLabel.Text = text;
            _androidStatusLabel.ForeColor = text.IndexOf("conectado", StringComparison.OrdinalIgnoreCase) >= 0 ? Theme.Green : Theme.Warning;
        }

        private void HandleAndroidBarcode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            Action<string> handler = _androidBarcodeHandler;
            if (handler == null) return;
            try
            {
                if (InvokeRequired) BeginInvoke((MethodInvoker)delegate { handler(code); });
                else handler(code);
            }
            catch { }
        }

        private Control BuildSalesHistory()
        {
            Panel page = Page();
            List<Order> sales = _store.GetOrders("").Where(o => o.Status == "Entregado" || o.Source == "Mostrador").ToList();
            Panel card = Theme.Card(); card.Dock = DockStyle.Top; card.Height = 650; card.Padding = new Padding(14);
            FlowLayoutPanel bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, WrapContents = false, Padding = new Padding(0, 4, 0, 4) };
            TextBox search = SearchBox(); search.Width = 300; bar.Controls.Add(search);
            Button refresh = Theme.Secondary("ACTUALIZAR"); refresh.Width = 130; bar.Controls.Add(refresh);
            Button print = Theme.Primary("IMPRIMIR / VER COMPROBANTE"); print.Width = 220; bar.Controls.Add(print);
            card.Controls.Add(bar);
            DataGridView grid = Theme.Grid(); grid.Dock = DockStyle.Fill; card.Controls.Add(grid);
            Action reload = delegate
            {
                string q = search.Text.Trim().ToLowerInvariant();
                grid.DataSource = sales.Where(o => q.Length == 0 || o.Id.ToString().Contains(q) || (o.CustomerName ?? "").ToLowerInvariant().Contains(q) || (o.PaymentMethod ?? "").ToLowerInvariant().Contains(q)).Select(o => new { Id = o.Id, Fecha = o.CreatedAt.ToString("dd/MM/yyyy HH:mm"), Cliente = o.CustomerName, Total = o.Total, Pago = o.PaymentMethod, Origen = o.Source, Estado = o.Status }).ToList();
                if (grid.Columns.Contains("Id")) grid.Columns["Id"].Visible = false;
                SetFill(grid, "Fecha", 18); SetFill(grid, "Cliente", 25); SetFill(grid, "Total", 15); SetFill(grid, "Pago", 18); SetFill(grid, "Origen", 12); SetFill(grid, "Estado", 12);
            };
            reload(); search.TextChanged += delegate { reload(); }; refresh.Click += delegate { sales = _store.GetOrders("").Where(o => o.Status == "Entregado" || o.Source == "Mostrador").ToList(); reload(); };
            print.Click += delegate
            {
                if (grid.SelectedRows.Count == 0) { MessageBox.Show("Seleccioná una venta.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                long id = Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value); Order order = sales.FirstOrDefault(x => x.Id == id);
                if (order == null) return;
                MessageBox.Show("Venta #" + order.Id + "\r\nFecha: " + order.CreatedAt.ToString("dd/MM/yyyy HH:mm") + "\r\nTotal: " + order.Total.ToString("C") + "\r\nMedio de pago: " + order.PaymentMethod + "\r\n\r\nEl detalle se conserva en el historial y puede usarse para generar el comprobante fiscal.", "NexoMarket · Venta", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            page.Controls.Add(card); return page;
        }

        private Control BuildDelivery()
        {
            Panel page = Page();
            Panel statusBar = Theme.Card();
            statusBar.Dock = DockStyle.Top;
            statusBar.Height = 92;
            statusBar.Padding = new Padding(14, 10, 14, 10);
            Label help = new Label { Text = "Seleccioná un pedido y cambiá su estado. La barra de cada pedido se colorea automáticamente.", AutoSize = false, Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular) };
            statusBar.Controls.Add(help);
            DataGridView grid = Theme.Grid();
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            string[] statuses = { "Pendiente", "Preparando", "Listo", "Enviado", "Rechazado", "Entregado" };
            foreach (string status in statuses)
            {
                Button b = Theme.Secondary(status);
                b.Width = 108;
                b.Height = 34;
                b.Tag = status;
                Color statusColor = Theme.Card2;
                if (status == "Pendiente") statusColor = Theme.Warning;
                else if (status == "Rechazado") statusColor = Theme.Danger;
                else if (status == "Listo" || status == "Enviado" || status == "Entregado") statusColor = Theme.Green;
                else if (status == "Preparando") statusColor = Theme.Warning;
                b.BackColor = statusColor;
                ModernButton modern = b as ModernButton;
                if (modern != null) { modern.NormalBackColor = statusColor; modern.HoverBackColor = ControlPaint.Light(statusColor); modern.PressedBackColor = ControlPaint.Dark(statusColor); }
                b.Click += delegate(object sender, EventArgs e)
                {
                    if (grid.SelectedRows.Count == 0) { MessageBox.Show("Seleccioná primero un pedido.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    long id = Convert.ToInt64(grid.SelectedRows[0].Cells["Id"].Value);
                    string newStatus = Convert.ToString(((Button)sender).Tag);
                    _store.UpdateOrderStatus(id, newStatus);
                    ReloadDeliveryGrid(grid);
                };
                actions.Controls.Add(b);
            }
            statusBar.Controls.Add(actions);
            grid.Dock = DockStyle.Top;
            grid.Height = 570;
            page.Controls.Add(grid);
            page.Controls.Add(statusBar);
            ReloadDeliveryGrid(grid);
            grid.SelectionChanged += delegate { PaintDeliveryRows(grid); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                long id = Convert.ToInt64(grid.Rows[e.RowIndex].Cells["Id"].Value);
                Order o = _store.GetOrders("").FirstOrDefault(x => x.Id == id);
                if (o == null) return;
                using (Form f = OrderDialog(o))
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        _store.UpdateOrderStatus(o.Id, Convert.ToString(f.Tag));
                        ReloadDeliveryGrid(grid);
                    }
                }
            };
            return page;
        }

        private void ReloadDeliveryGrid(DataGridView grid)
        {
            List<Order> deliveries = _store.GetOrders("").Where(o => o.Fulfillment == "Delivery" && o.Status != "Entregado").ToList();
            grid.DataSource = deliveries.Select(OrderRow).ToList();
            ConfigureOrderGrid(grid);
            PaintDeliveryRows(grid);
        }

        private void PaintDeliveryRows(DataGridView grid)
        {
            if (grid == null || grid.IsDisposed) return;
            foreach (DataGridViewRow row in grid.Rows)
            {
                string status = row.Cells["Estado"].Value == null ? "" : Convert.ToString(row.Cells["Estado"].Value);
                Color color = Theme.Card2;
                if (status == "Pendiente") color = Color.FromArgb(105, 82, 28);
                else if (status == "Rechazado" || status == "Cancelado") color = Color.FromArgb(105, 38, 42);
                else if (status == "Listo" || status == "Enviado" || status == "En reparto" || status == "Entregado") color = Color.FromArgb(28, 91, 70);
                else if (status == "Preparando") color = Color.FromArgb(83, 68, 28);
                row.DefaultCellStyle.BackColor = color;
                row.DefaultCellStyle.SelectionBackColor = Theme.Accent;
                row.DefaultCellStyle.ForeColor = Theme.Text;
            }
        }

        private void OpenWebServer()
        {
            if (_webServer == null)
            {
                int webPort;
                if (!int.TryParse(_store.GetSetting("web_server_port", "8090"), out webPort)) webPort = 8090;
                _webServer = new WebServerService(_store, webPort);
                _webServer.Start();
            }
            using (var form = new WebServerForm(_store, _webServer)) form.ShowDialog(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_centralSync != null) { _centralSync.DataChanged -= HandleCentralDataChanged; _centralSync.Dispose(); } } catch { }
            try { if (_webServer != null) _webServer.Dispose(); } catch { }
            try { if (_localScannerServer != null) _localScannerServer.Dispose(); } catch { }
            try { if (_androidBridge != null) _androidBridge.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        private void SaveStoreProfile(TextBox name, TextBox legal, TextBox cuitStore, TextBox phone, TextBox email, TextBox address, TextBox city, TextBox province, TextBox latitude, TextBox longitude, TextBox category, TextBox slug, TextBox desc, CheckBox active, CheckBox pickup, CheckBox delivery)
        {
            _store.SetSetting("store_name",name.Text.Trim()); _store.SetSetting("store_legal_name",legal.Text.Trim()); _store.SetSetting("store_cuit",cuitStore.Text.Trim()); _store.SetSetting("store_phone",phone.Text.Trim()); _store.SetSetting("store_email",email.Text.Trim()); _store.SetSetting("store_address",address.Text.Trim()); _store.SetSetting("store_city",city.Text.Trim()); _store.SetSetting("store_province",province.Text.Trim());
            if (string.IsNullOrWhiteSpace(latitude.Text) || string.IsNullOrWhiteSpace(longitude.Text))
            {
                try { LocationResult g = new StoreDirectoryClient(_store).Geocode((address.Text.Trim() + ", " + city.Text.Trim() + ", " + province.Text.Trim()).Trim(new[] { ' ', ',' })); if (g.Success) { latitude.Text = g.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture); longitude.Text = g.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture); } } catch { }
            }
            _store.SetSetting("store_latitude",latitude.Text.Trim()); _store.SetSetting("store_longitude",longitude.Text.Trim()); _store.SetSetting("store_category",category.Text.Trim()); _store.SetSetting("store_slug",slug.Text.Trim()); _store.SetSetting("store_description",desc.Text.Trim()); _store.SetSetting("store_web_active",active.Checked?"1":"0"); _store.SetSetting("pickup_enabled",pickup.Checked?"1":"0"); _store.SetSetting("delivery_enabled",delivery.Checked?"1":"0");
            try { new StoreDirectoryClient(_store).PublishStore(_store.GetSetting("web_public_url","")); } catch { }
            MessageBox.Show(active.Checked ? "Perfil guardado. La tienda queda marcada como activa para la web principal y sus coordenadas se usan para ordenar por cercanía." : "Perfil guardado. La tienda permanece fuera de la web principal.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }

        private Control BuildSettings()
        {
            Panel page=Page(); TabControl tabs=new TabControl{Dock=DockStyle.Top,Height=540};
            TabPage general=Tab("General"); general.AutoScroll=true;
            TextBox name=FieldInPanel(general,"Nombre comercial",_store.GetSetting("store_name","NexoMarket"),18);
            TextBox legal=FieldInPanel(general,"Razón social",_store.GetSetting("store_legal_name",""),82);
            TextBox cuitStore=FieldInPanel(general,"CUIT",_store.GetSetting("store_cuit",""),146);
            TextBox phone=FieldInPanel(general,"Teléfono",_store.GetSetting("store_phone",""),210);
            TextBox email=FieldInPanel(general,"Correo",_store.GetSetting("store_email",""),274);
            TextBox address=FieldInPanel(general,"Dirección",_store.GetSetting("store_address",""),338);
            TextBox city=FieldInPanel(general,"Ciudad",_store.GetSetting("store_city",""),402);
            TextBox province=FieldInPanel(general,"Provincia",_store.GetSetting("store_province",""),466);
            TextBox latitude=FieldInPanel(general,"Latitud (GPS)",_store.GetSetting("store_latitude",""),530);
            TextBox longitude=FieldInPanel(general,"Longitud (GPS)",_store.GetSetting("store_longitude",""),594);
            TextBox category=FieldInPanel(general,"Rubro / categoría",_store.GetSetting("store_category",""),658);
            TextBox slug=FieldInPanel(general,"URL amigable de la tienda",_store.GetSetting("store_slug",""),722);
            TextBox desc=FieldInPanel(general,"Descripción pública",_store.GetSetting("store_description",""),786);
            CheckBox active=new CheckBox{Text="TIENDA ACTIVA EN LA WEB PRINCIPAL",Checked=_store.GetSetting("store_web_active","0")=="1",ForeColor=Theme.Green,Location=new Point(20,858),AutoSize=true};general.Controls.Add(active);
            CheckBox pickup=new CheckBox{Text="Permitir retiro",Checked=_store.GetSetting("pickup_enabled","1")=="1",ForeColor=Theme.Text,Location=new Point(20,893),AutoSize=true};
            CheckBox delivery=new CheckBox{Text="Permitir delivery",Checked=_store.GetSetting("delivery_enabled","1")=="1",ForeColor=Theme.Text,Location=new Point(20,923),AutoSize=true};general.Controls.Add(pickup);general.Controls.Add(delivery);
            Label logoInfo=new Label{Text="Logo y portada de la tienda",AutoSize=true,ForeColor=Theme.Muted,Location=new Point(20,963),Font=Theme.Font(9,FontStyle.Bold)};general.Controls.Add(logoInfo);
            Button logo=Theme.Secondary("SUBIR LOGO");logo.Location=new Point(20,988);logo.Width=150;general.Controls.Add(logo);
            Button cover=Theme.Secondary("SUBIR PORTADA");cover.Location=new Point(185,988);cover.Width=160;general.Controls.Add(cover);
            Label mediaState=new Label{Text="Logo: "+_store.GetSetting("store_logo","(sin logo)")+"\r\nPortada: "+_store.GetSetting("store_cover","(sin portada)"),AutoSize=false,Width=600,Height=45,ForeColor=Theme.Muted,Location=new Point(20,1033),Font=Theme.Font(8.5f,FontStyle.Regular)};general.Controls.Add(mediaState);
            logo.Click+=delegate{using(OpenFileDialog d=new OpenFileDialog()){d.Filter="Imágenes|*.jpg;*.jpeg;*.png;*.bmp";if(d.ShowDialog(this)==DialogResult.OK){string dir=Path.Combine(_store.Root,"Store");Directory.CreateDirectory(dir);string dest=Path.Combine(dir,"logo"+Path.GetExtension(d.FileName));File.Copy(d.FileName,dest,true);_store.SetSetting("store_logo",dest);mediaState.Text="Logo: "+dest+"\r\nPortada: "+_store.GetSetting("store_cover","(sin portada)");}}};
            cover.Click+=delegate{using(OpenFileDialog d=new OpenFileDialog()){d.Filter="Imágenes|*.jpg;*.jpeg;*.png;*.bmp";if(d.ShowDialog(this)==DialogResult.OK){string dir=Path.Combine(_store.Root,"Store");Directory.CreateDirectory(dir);string dest=Path.Combine(dir,"cover"+Path.GetExtension(d.FileName));File.Copy(d.FileName,dest,true);_store.SetSetting("store_cover",dest);mediaState.Text="Logo: "+_store.GetSetting("store_logo","(sin logo)")+"\r\nPortada: "+dest;}}};
            Button save=Theme.Primary("GUARDAR PERFIL DE TIENDA");save.Location=new Point(20,1093);save.Width=230;save.Click+=delegate{SaveStoreProfile(name,legal,cuitStore,phone,email,address,city,province,latitude,longitude,category,slug,desc,active,pickup,delivery);};general.Controls.Add(save); tabs.TabPages.Add(general);
            TabPage ticket=Tab("Ticket"); TextBox header=FieldInPanel(ticket,"Encabezado del ticket",_store.GetSetting("ticket_header","NexoMarket"),22); TextBox footer=FieldInPanel(ticket,"Pie del ticket",_store.GetSetting("ticket_footer","Gracias por su compra"),88); Label tip=new Label{Text="El punto de venta imprime fecha, productos, cantidades, precios y TOTAL. Podés personalizar el encabezado y pie.",AutoSize=false,Width=680,Height=55,ForeColor=Theme.Muted,Location=new Point(22,160),Font=Theme.Font(9,FontStyle.Regular)};ticket.Controls.Add(tip);Button st=Theme.Primary("GUARDAR TICKET");st.Location=new Point(22,225);st.Click+=delegate{_store.SetSetting("ticket_header",header.Text.Trim());_store.SetSetting("ticket_footer",footer.Text.Trim());_ticketHeader=header.Text.Trim();_ticketFooter=footer.Text.Trim();MessageBox.Show("Datos del ticket guardados.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);};ticket.Controls.Add(st);tabs.TabPages.Add(ticket);
            tabs.TabPages.Add(BuildSecurityTab());
            TabPage appearance=Tab("Apariencia"); Label ap=new Label{Text="INTERFAZ\r\nTema actual: Graphite / oscuro\r\nFuente: Segoe UI\r\nDiseño optimizado para pantallas 1024×700 o superiores.\r\n\r\nNo se utilizan componentes web ni Electron.",AutoSize=false,Width=700,Height=150,ForeColor=Theme.Muted,Font=Theme.Font(10,FontStyle.Regular),Location=new Point(22,25)};appearance.Controls.Add(ap); tabs.TabPages.Add(appearance);
            TabPage data=Tab("Datos y respaldo"); Label dt=new Label{Text="Los datos se guardan localmente en nexomarket_data.xml y las imágenes en la carpeta Media.",AutoSize=false,Width=700,Height=60,ForeColor=Theme.Muted,Font=Theme.Font(9.5f,FontStyle.Regular),Location=new Point(22,25)};data.Controls.Add(dt);Button backup=Theme.Primary("CREAR COPIA DE SEGURIDAD");backup.Location=new Point(22,95);backup.Click+=delegate{try{string dir=Path.Combine(_store.Root,"Backups");Directory.CreateDirectory(dir);string dest=Path.Combine(dir,"nexomarket_backup_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".xml");File.Copy(Path.Combine(_store.Root,"nexomarket_data.xml"),dest,true);MessageBox.Show("Copia creada en:\r\n"+dest,"NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){MessageBox.Show("No se pudo crear la copia.\r\n"+ex.Message,"NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Error);}};data.Controls.Add(backup);tabs.TabPages.Add(data);
            TabPage web=Tab("Tienda web");
            TextBox publicUrl=FieldInPanel(web,"URL pública de la tienda",_store.GetSetting("web_public_url","https://nexomarket-central.onrender.com"),22);
            TextBox apiUrl=FieldInPanel(web,"URL de API / sincronización",_store.GetSetting("web_api_url","https://nexomarket-central.onrender.com"),88);
            string linkedSellerEmail = _store.GetSetting("seller_account_email","");
            TextBox sellerEmail=FieldInPanel(web,"Correo de cuenta de vendedor vinculada",linkedSellerEmail,154);
            sellerEmail.ReadOnly = true;
            Button accountButton=Theme.Secondary(string.IsNullOrWhiteSpace(linkedSellerEmail)?"CREAR CUENTA DE VENDEDOR":"GESTIONAR CUENTA DE VENDEDOR"); accountButton.Location=new Point(460,154); accountButton.Width=230;
            accountButton.Click+=delegate{using(SellerAccountForm f=new SellerAccountForm(_store)){if(f.ShowDialog(this)==DialogResult.OK){sellerEmail.Text=_store.GetSetting("seller_account_email","");accountButton.Text="GESTIONAR CUENTA DE VENDEDOR";}}}; web.Controls.Add(accountButton);
            if (sellerEmail.ReadOnly) sellerEmail.BackColor = Theme.Background;
            CheckBox publish= new CheckBox { Text="Sincronización web habilitada", Checked=_store.GetSetting("web_sync_enabled","0")=="1", ForeColor=Theme.Text, Location=new Point(22,220), AutoSize=true }; web.Controls.Add(publish);
            Label webHelp=new Label { Text="La web principal muestra TIENDAS, no productos. Cada instalación se registra por StoreId en el servidor central. El comprador entra primero a la tienda y recién allí ve categorías y productos. Las coordenadas GPS permiten ordenar por cercanía.\r\nLa exportación local crea WebCatalog\\catalog.json como respaldo del catálogo.", AutoSize=false, Width=700, Height=70, ForeColor=Theme.Muted, Font=Theme.Font(9,FontStyle.Regular), Location=new Point(22,190) }; web.Controls.Add(webHelp);
            Button exportWeb=Theme.Primary("EXPORTAR CATÁLOGO WEB"); exportWeb.Location=new Point(22,335); exportWeb.Width=220; exportWeb.Click+=delegate{try{_store.SetSetting("web_public_url",publicUrl.Text.Trim());_store.SetSetting("web_api_url",apiUrl.Text.Trim());_store.SetSetting("web_sync_enabled",publish.Checked?"1":"0");string path=new WebCatalogExporter(_store).Export();MessageBox.Show("Catálogo web actualizado:\r\n"+path,"NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}catch(Exception ex){MessageBox.Show("No se pudo exportar el catálogo.\r\n"+ex.Message,"NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Error);}}; web.Controls.Add(exportWeb);
            Button saveWeb=Theme.Secondary("GUARDAR CONFIGURACIÓN WEB"); saveWeb.Location=new Point(260,335); saveWeb.Width=220; saveWeb.Click+=delegate{_store.SetSetting("web_public_url",publicUrl.Text.Trim());_store.SetSetting("web_api_url",apiUrl.Text.Trim());_store.SetSetting("web_sync_enabled",publish.Checked?"1":"0");if(publish.Checked)_store.SetSetting("store_web_active","1");try{bool ok=new StoreDirectoryClient(_store).PublishStore(_store.GetSetting("web_public_url",""));if(ok)MessageBox.Show("Configuración guardada y tienda publicada en el directorio central. La tienda aparecerá entre TODAS las tiendas activas.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);else MessageBox.Show("Configuración guardada, pero no se pudo publicar la tienda en este momento. Verificá la URL de API.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Warning);}catch(Exception ex){MessageBox.Show("Configuración guardada, pero falló la publicación: "+ex.Message,"NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}; web.Controls.Add(saveWeb);
            tabs.TabPages.Add(web);
            TabPage arca=Tab("Facturación ARCA");
            TextBox cuit=FieldInPanel(arca,"CUIT emisor",_store.GetSetting("arca_cuit",""),18);
            TextBox punto=FieldInPanel(arca,"Punto de venta",_store.GetSetting("arca_point_of_sale","0001"),82);
            ComboBox regime=new ComboBox{Width=420,Location=new Point(20,146),DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Theme.Card2,ForeColor=Theme.Text}; regime.Items.AddRange(new object[]{"Responsable Inscripto","Monotributo / Exento"}); regime.SelectedItem=_store.GetSetting("arca_regime","Responsable Inscripto"); arca.Controls.Add(new Label{Text="Condición del emisor",AutoSize=true,Location=new Point(20,126),ForeColor=Theme.Muted,Font=Theme.Font(8.5f,FontStyle.Bold)}); arca.Controls.Add(regime);
            ComboBox environment=new ComboBox{Width=420,Location=new Point(20,210),DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Theme.Card2,ForeColor=Theme.Text}; environment.Items.AddRange(new object[]{"Homologación / Testing","Producción"}); environment.SelectedItem=_store.GetSetting("arca_environment","Homologación / Testing"); arca.Controls.Add(new Label{Text="Ambiente ARCA",AutoSize=true,Location=new Point(20,190),ForeColor=Theme.Muted,Font=Theme.Font(8.5f,FontStyle.Bold)}); arca.Controls.Add(environment);
            Label arcaInfo=new Label{Text="La emisión electrónica real requiere certificado digital X.509, asociación al Web Service y un punto de venta habilitado. NexoMarket dejará separados los comprobantes A/B/C y nunca inventará un CAE.",AutoSize=false,Width=650,Height=72,Location=new Point(20,260),ForeColor=Theme.Muted,Font=Theme.Font(9,FontStyle.Regular)};arca.Controls.Add(arcaInfo);
            ComboBox invoiceType=new ComboBox{Width=140,Location=new Point(20,335),DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Theme.Card2,ForeColor=Theme.Text}; invoiceType.Items.AddRange(new object[]{"Factura A","Factura B","Factura C"}); invoiceType.SelectedIndex=1; arca.Controls.Add(new Label{Text="Formato de comprobante",AutoSize=true,Location=new Point(20,315),ForeColor=Theme.Muted,Font=Theme.Font(8.5f,FontStyle.Bold)}); arca.Controls.Add(invoiceType);
            Button draft=Theme.Secondary("VER / COPIAR BORRADOR"); draft.Location=new Point(180,335); draft.Width=210; draft.Click+=delegate{using(Form inv=InvoiceDraftForm(Convert.ToString(invoiceType.SelectedItem),cuit.Text.Trim(),punto.Text.Trim())) inv.ShowDialog(this);}; arca.Controls.Add(draft);
            Button saveArca=Theme.Primary("GUARDAR CONFIGURACIÓN ARCA"); saveArca.Location=new Point(410,335); saveArca.Width=230; saveArca.Click+=delegate{_store.SetSetting("arca_cuit",cuit.Text.Trim());_store.SetSetting("arca_point_of_sale",punto.Text.Trim());_store.SetSetting("arca_regime",Convert.ToString(regime.SelectedItem));_store.SetSetting("arca_environment",Convert.ToString(environment.SelectedItem));MessageBox.Show("Configuración ARCA guardada. Para emitir en producción todavía necesitás cargar el certificado y asociar el servicio WSFEV1 en ARCA.","NexoMarket",MessageBoxButtons.OK,MessageBoxIcon.Information);}; arca.Controls.Add(saveArca); tabs.TabPages.Add(arca);
            TabPage compatibility=Tab("Compatibilidad"); Label ci=new Label{Text="Motor: C# + Windows Forms + .NET Framework 4.0\r\nDiseño nativo de escritorio. Sin Electron ni dependencias web obligatorias.\r\nObjetivo: Windows 7 SP1, Windows 8.1, Windows 10 y Windows 11.\r\nLa cámara usa la webcam local del equipo mediante la interfaz de captura clásica de Windows.\r\nLos lectores USB de códigos funcionan como teclado y son compatibles con el módulo de productos y el punto de venta.",AutoSize=false,Width=700,Height=180,ForeColor=Theme.Muted,Font=Theme.Font(9.5f,FontStyle.Regular),Location=new Point(22,25)};compatibility.Controls.Add(ci);tabs.TabPages.Add(compatibility);
            page.Controls.Add(tabs); return page;
        }


        private TabPage BuildSecurityTab()
        {
            TabPage security = Tab("Seguridad");
            security.AutoScroll = true;

            Label title = new Label
            {
                Text = "SEGURIDAD Y RECUPERACIÓN",
                AutoSize = true,
                ForeColor = Theme.Text,
                Font = Theme.Font(15, FontStyle.Bold),
                Location = new Point(22, 20)
            };
            security.Controls.Add(title);

            Label help = new Label
            {
                Text = "La contraseña inicial no se muestra en el programa. En el primer acceso se obliga al cliente a crear su contraseña definitiva.",
                AutoSize = false,
                Width = 760,
                Height = 45,
                ForeColor = Theme.Muted,
                Font = Theme.Font(9, FontStyle.Regular),
                Location = new Point(22, 50)
            };
            security.Controls.Add(help);

            Label userInfo = new Label
            {
                Text = "Usuario actual: " + _store.AdminUsername,
                AutoSize = true,
                ForeColor = Theme.Text,
                Font = Theme.Font(9, FontStyle.Bold),
                Location = new Point(22, 88)
            };
            security.Controls.Add(userInfo);

            Button changePassword = Theme.Secondary("CAMBIAR CONTRASEÑA");
            changePassword.Location = new Point(300, 82);
            changePassword.Width = 220;
            changePassword.Click += delegate
            {
                using (ChangePasswordForm form = new ChangePasswordForm(_store, false)) form.ShowDialog(this);
            };
            security.Controls.Add(changePassword);

            TextBox recovery = FieldInPanel(security, "Correo de recuperación", _store.GetSetting("admin_recovery_email", ""), 105);
            recovery.Width = 500;

            Label smtpTitle = new Label
            {
                Text = "GMAIL / SMTP",
                AutoSize = true,
                ForeColor = Theme.Green,
                Font = Theme.Font(10, FontStyle.Bold),
                Location = new Point(22, 190)
            };
            security.Controls.Add(smtpTitle);

            TextBox smtpHost = FieldInPanel(security, "Servidor SMTP", _store.GetSetting("smtp_host", "smtp.gmail.com"), 215);
            TextBox smtpPort = FieldInPanel(security, "Puerto", _store.GetSetting("smtp_port", "587"), 280);
            TextBox smtpUser = FieldInPanel(security, "Correo emisor Gmail", _store.GetSetting("smtp_user", ""), 345);
            TextBox smtpPass = FieldInPanel(security, "App Password de Gmail", _store.GetSetting("smtp_app_password", ""), 410);
            smtpPass.PasswordChar = '●';
            CheckBox ssl = new CheckBox
            {
                Text = "Usar SSL/TLS",
                Checked = _store.GetSetting("smtp_ssl", "1") == "1",
                AutoSize = true,
                ForeColor = Theme.Text,
                Location = new Point(22, 475)
            };
            security.Controls.Add(ssl);

            Label note = new Label
            {
                Text = "Para Gmail se recomienda una App Password. No se debe utilizar la contraseña normal de la cuenta.",
                AutoSize = false,
                Width = 720,
                Height = 42,
                ForeColor = Theme.Muted,
                Font = Theme.Font(8.5f, FontStyle.Regular),
                Location = new Point(22, 500)
            };
            security.Controls.Add(note);

            Button save = Theme.Primary("GUARDAR SEGURIDAD");
            save.Location = new Point(22, 550);
            save.Width = 220;
            save.Click += delegate
            {
                if (smtpUser.Text.Trim().Length > 0 && smtpPass.Text.Trim().Length == 0)
                {
                    MessageBox.Show("Ingresá la App Password de Gmail.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _store.SetSetting("admin_recovery_email", recovery.Text.Trim());
                _store.SetSetting("smtp_host", smtpHost.Text.Trim());
                _store.SetSetting("smtp_port", smtpPort.Text.Trim());
                _store.SetSetting("smtp_user", smtpUser.Text.Trim());
                _store.SetSetting("smtp_app_password", smtpPass.Text.Trim());
                _store.SetSetting("smtp_ssl", ssl.Checked ? "1" : "0");
                MessageBox.Show("Configuración de seguridad y recuperación guardada.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            security.Controls.Add(save);

            Button test = Theme.Secondary("ENVIAR CORREO DE PRUEBA");
            test.Location = new Point(255, 550);
            test.Width = 220;
            test.Click += delegate
            {
                if (smtpUser.Text.Trim().Length == 0 || smtpPass.Text.Trim().Length == 0 || recovery.Text.Trim().Length == 0)
                {
                    MessageBox.Show("Completá correo de recuperación, Gmail emisor y App Password antes de probar.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    int port;
                    if (!int.TryParse(smtpPort.Text.Trim(), out port)) port = 587;
                    using (System.Net.Mail.MailMessage mail = new System.Net.Mail.MailMessage())
                    {
                        mail.From = new System.Net.Mail.MailAddress(smtpUser.Text.Trim(), "NexoMarket");
                        mail.To.Add(recovery.Text.Trim());
                        mail.Subject = "NexoMarket · Prueba de correo";
                        mail.Body = "Este es un correo de prueba de la configuración de recuperación de NexoMarket.";
                        using (System.Net.Mail.SmtpClient smtp = new System.Net.Mail.SmtpClient(smtpHost.Text.Trim(), port))
                        {
                            smtp.EnableSsl = ssl.Checked;
                            smtp.Credentials = new System.Net.NetworkCredential(smtpUser.Text.Trim(), smtpPass.Text.Trim());
                            smtp.Timeout = 15000;
                            smtp.Send(mail);
                        }
                    }
                    MessageBox.Show("Correo de prueba enviado correctamente.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("No se pudo enviar el correo de prueba.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            security.Controls.Add(test);

            return security;
        }

        private TabPage Tab(string text) { TabPage p=new TabPage(text);p.BackColor=Theme.Background;p.ForeColor=Theme.Text;return p; }
        private TextBox FieldInPanel(Control panel,string label,string value,int y){Label l=new Label{Text=label,AutoSize=true,ForeColor=Theme.Muted,Location=new Point(20,y),Font=Theme.Font(8.5f,FontStyle.Bold)};panel.Controls.Add(l);TextBox t=new TextBox{Text=value,Width=420,Height=28,BackColor=Theme.Card2,ForeColor=Theme.Text,BorderStyle=BorderStyle.FixedSingle,Location=new Point(20,y+20)};panel.Controls.Add(t);return t;}
    }

    internal static class ControlExtensions
    {
        public static void PlaceholderTextSafe(this TextBox box, string text)
        {
            try { box.Tag = text; } catch { }
        }
    }
}
