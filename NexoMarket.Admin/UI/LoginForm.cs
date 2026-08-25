using System;
using System.Drawing;
using System.Net;
using System.Net.Mail;
using System.Linq;
using System.Windows.Forms;
using NexoMarket.Admin.Data;
using NexoMarket.Admin.Models;

namespace NexoMarket.Admin.UI
{

    /// <summary>
    /// Acceso de vendedor del programa Windows. La misma cuenta (correo + contraseña)
    /// se guarda en WebUsers y se utiliza también por el Seller Center web.
    /// En el primer inicio permite crear la cuenta y vincula automáticamente el correo
    /// con el StoreId de esta instalación.
    /// </summary>
    public sealed class SellerAccountForm : Form
    {
        private readonly AppDataStore _store;
        private TextBox _name;
        private TextBox _email;
        private TextBox _pass;
        private TextBox _repeat;
        private Button _action;
        private Button _mode;
        private Label _status;
        private bool _register;

        public SellerAccountForm(AppDataStore store)
        {
            _store = store;
            _register = _store.GetWebUsers().All(u => u.Role != "seller");
            Text = "NexoMarket · Cuenta de vendedor";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, _register ? 525 : 455);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Build();
        }

        private void Build()
        {
            Controls.Clear();
            ClientSize = new Size(520, _register ? 525 : 455);
            Panel card = Theme.Card();
            card.SetBounds(35, 22, 450, ClientSize.Height - 44);
            Controls.Add(card);

            Label brand = new Label { Text = "NEXO MARKET", Font = Theme.Font(24, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = new Point(32, 25) };
            card.Controls.Add(brand);
            Label sub = new Label { Text = _register ? "CREAR CUENTA DE VENDEDOR" : "ACCESO DE VENDEDOR", Font = Theme.Font(9, FontStyle.Bold), ForeColor = Theme.Green, AutoSize = true, Location = new Point(35, 66) };
            card.Controls.Add(sub);

            int y = 104;
            if (_register)
            {
                card.Controls.Add(MakeLabel("Nombre / comercio", 35, y));
                _name = Input("", 35, y + 24, 370); card.Controls.Add(_name); y += 70;
            }
            else _name = null;

            card.Controls.Add(MakeLabel("Correo electrónico", 35, y));
            _email = Input(_store.GetSetting("seller_account_email", ""), 35, y + 24, 370); card.Controls.Add(_email); y += 70;
            card.Controls.Add(MakeLabel("Contraseña", 35, y));
            _pass = Input("", 35, y + 24, 370); _pass.PasswordChar = '●'; card.Controls.Add(_pass); y += 70;

            if (_register)
            {
                card.Controls.Add(MakeLabel("Repetir contraseña", 35, y));
                _repeat = Input("", 35, y + 24, 370); _repeat.PasswordChar = '●'; card.Controls.Add(_repeat); y += 72;
            }
            else _repeat = null;

            _action = Theme.Primary(_register ? "CREAR CUENTA Y ENTRAR" : "INGRESAR");
            _action.Width = 370; _action.Location = new Point(35, y); _action.Click += Submit; card.Controls.Add(_action); y += 50;

            _mode = Theme.Secondary(_register ? "YA TENGO UNA CUENTA · INICIAR SESIÓN" : "PRIMERA VEZ · CREAR CUENTA");
            _mode.Width = 370; _mode.Location = new Point(35, y); _mode.Click += ToggleMode; card.Controls.Add(_mode); y += 48;

            Button forgot = Theme.Secondary("¿OLVIDASTE TU CONTRASEÑA?");
            forgot.Width = 370; forgot.Location = new Point(35, y); forgot.Click += Forgot; card.Controls.Add(forgot); y += 48;

            _status = new Label { Text = "La cuenta de vendedor se comparte con el Seller Center web. El correo queda vinculado a esta tienda.", AutoSize = false, Width = 370, Height = 45, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Regular), Location = new Point(35, y) };
            card.Controls.Add(_status);
            AcceptButton = _action;
            Shown += delegate { (_register ? (_name ?? _email) : _email).Focus(); };
        }

        private Label MakeLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Bold), Location = new Point(x, y) };
        }

        private TextBox Input(string text, int x, int y, int width)
        {
            return new TextBox { Text = text, Width = width, Height = 30, Font = Theme.Font(10, FontStyle.Regular), BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Location = new Point(x, y) };
        }

        private void ToggleMode(object sender, EventArgs e)
        {
            _register = !_register;
            Build();
        }

        private void Submit(object sender, EventArgs e)
        {
            string email = (_email.Text ?? "").Trim().ToLowerInvariant();
            string password = _pass.Text ?? "";
            if (email.Length < 5 || !email.Contains("@")) { Fail("Ingresá un correo electrónico válido."); return; }
            if (password.Length < 6) { Fail("La contraseña debe tener al menos 6 caracteres."); return; }

            // Antes de crear o validar localmente, traer las cuentas de la tienda desde Central.
            // Esto evita que una cuenta creada en la web aparezca como inexistente en Windows.
            try { using (CentralSyncService sync = new CentralSyncService(_store)) sync.SyncOnce(); } catch { }

            if (_register)
            {
                if (_repeat == null || password != _repeat.Text) { Fail("Las contraseñas no coinciden."); return; }
                string linkedEmail = (_store.GetSetting("seller_account_email", "") ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(linkedEmail) && !string.Equals(linkedEmail, email, StringComparison.OrdinalIgnoreCase))
                { Fail("Esta instalación ya tiene una cuenta de vendedor vinculada: " + linkedEmail + ". Cerrá sesión desde NexoMarket y desvinculá la cuenta si querés usar otra."); return; }

                WebUser existing = _store.FindWebUser(email);
                if (existing != null)
                {
                    if (existing.Role != "seller") { Fail("Ese correo ya pertenece a una cuenta de comprador. Usá otro correo para la cuenta de vendedor."); return; }
                    if (!_store.VerifyWebUser(email, password, out existing)) { Fail("El correo ya está registrado pero la contraseña no coincide."); return; }
                    string targetStore = string.IsNullOrWhiteSpace(existing.StoreId) ? _store.StoreId : existing.StoreId;
                    if (string.IsNullOrWhiteSpace(targetStore)) { Fail("No se pudo asignar el identificador de la tienda. Reiniciá NexoMarket e intentá nuevamente."); return; }
                    if (!string.Equals(existing.StoreId, targetStore, StringComparison.OrdinalIgnoreCase))
                    {
                        // Completa cuentas antiguas que quedaron sin StoreId.
                        _store.UpdateWebUserStore(existing.Id, targetStore);
                        existing.StoreId = targetStore;
                    }
                    if (!string.Equals(existing.StoreId, _store.StoreId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Al iniciar una cuenta existente, se conserva la tienda asociada a esa cuenta.
                        _store.SetSetting("store_id", existing.StoreId);
                    }
                    _store.SetSetting("seller_account_email", email);
                    _store.SetSetting("seller_account_name", existing.Name ?? "");
                    new CentralSyncService(_store).PublishAccountNow(existing);
                    DialogResult = DialogResult.OK; Close(); return;
                }

                string salt = AuthService.CreateSalt();
                string storeId = _store.StoreId;
                if (string.IsNullOrWhiteSpace(storeId)) { Fail("La tienda todavía no tiene StoreId. Reiniciá NexoMarket para generarlo automáticamente."); return; }
                WebUser user = new WebUser { Name = (_name == null ? "" : _name.Text.Trim()), Email = email, Role = "seller", StoreId = storeId, Salt = salt, PasswordHash = AuthService.HashPassword(password, salt), CreatedAt = DateTime.Now };
                if (!_store.CreateWebUser(user)) { Fail("No se pudo crear la cuenta. Revisá los datos e intentá nuevamente."); return; }
                _store.SetSetting("seller_account_email", email);
                _store.SetSetting("seller_account_name", user.Name ?? "");
                // Publicación inmediata: la cuenta aparece en el servidor central sin esperar al ciclo de 30 segundos.
                new CentralSyncService(_store).PublishAccountNow(user);
                DialogResult = DialogResult.OK; Close(); return;
            }

            WebUser found;
            bool localOk = _store.VerifyWebUser(email, password, out found) && found.Role == "seller";
            if (!localOk)
            {
                try
                {
                    using (CentralSyncService sync = new CentralSyncService(_store))
                    {
                        if (sync.AuthenticateCentral(email, password, out found) && found != null && found.Role == "seller")
                        { localOk = true; sync.PublishAccountNow(found); }
                    }
                } catch { }
            }
            if (!localOk) { Fail("Correo o contraseña incorrectos. Verificá que la cuenta exista en NexoMarket Central."); return; }
            if (!string.IsNullOrWhiteSpace(found.StoreId) && !string.Equals(found.StoreId, _store.StoreId, StringComparison.OrdinalIgnoreCase)) _store.SetSetting("store_id", found.StoreId);
            _store.SetSetting("seller_account_email", found.Email);
            _store.SetSetting("seller_account_name", found.Name ?? "");
            _store.SetSetting("seller_account_locked", "1");
            DialogResult = DialogResult.OK; Close();
        }

        private void Fail(string text) { _status.Text = text; _status.ForeColor = Theme.Danger; }

        private void Forgot(object sender, EventArgs e)
        {
            string email = (_email == null ? "" : _email.Text.Trim()).ToLowerInvariant();
            if (email.Length == 0) { MessageBox.Show("Primero ingresá el correo de tu cuenta de vendedor.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            WebUser user = _store.FindWebUser(email);
            if (user == null || user.Role != "seller") { MessageBox.Show("No existe una cuenta de vendedor con ese correo.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string smtpUser = _store.GetSetting("smtp_user", "").Trim();
            string smtpPassword = _store.GetSetting("smtp_app_password", "");
            if (smtpUser.Length == 0 || smtpPassword.Length == 0) { MessageBox.Show("Configurá el correo emisor y la App Password en Configuración → Seguridad.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string code = _store.CreateWebRecoveryCode(email, 10);
            try
            {
                string host = _store.GetSetting("smtp_host", "smtp.gmail.com"); int port; if (!int.TryParse(_store.GetSetting("smtp_port", "587"), out port)) port = 587;
                using (MailMessage mail = new MailMessage())
                {
                    mail.From = new MailAddress(smtpUser, "NexoMarket"); mail.To.Add(email); mail.Subject = "NexoMarket · Recuperación de cuenta de vendedor";
                    mail.Body = "Hola " + (user.Name ?? "") + ",\r\n\r\nTu código de recuperación es: " + code + "\r\n\r\nVence en 10 minutos.";
                    using (SmtpClient smtp = new SmtpClient(host, port)) { smtp.EnableSsl = _store.GetSetting("smtp_ssl", "1") == "1"; smtp.Credentials = new NetworkCredential(smtpUser, smtpPassword); smtp.Timeout = 15000; smtp.Send(mail); }
                }
                using (SellerRecoveryCodeForm recovery = new SellerRecoveryCodeForm(_store, email)) recovery.ShowDialog(this);
            }
            catch (Exception ex) { MessageBox.Show("No se pudo enviar el correo de recuperación.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    public sealed class LoginForm : Form
    {
        private readonly AppDataStore _store;
        private TextBox _user;
        private TextBox _pass;
        private Button _login;
        private Label _status;

        public LoginForm(AppDataStore store)
        {
            _store = store;
            Text = "NexoMarket · Acceso";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 455);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            Panel card = Theme.Card();
            card.SetBounds(35, 24, 430, 395);
            Controls.Add(card);

            Label brand = new Label();
            brand.Text = "NEXO MARKET";
            brand.Font = Theme.Font(24, FontStyle.Bold);
            brand.ForeColor = Theme.Text;
            brand.AutoSize = true;
            brand.Location = new Point(34, 28);
            card.Controls.Add(brand);

            Label sub = new Label();
            sub.Text = "PANEL DE ADMINISTRACIÓN";
            sub.Font = Theme.Font(9, FontStyle.Bold);
            sub.ForeColor = Theme.Green;
            sub.AutoSize = true;
            sub.Location = new Point(37, 68);
            card.Controls.Add(sub);

            card.Controls.Add(Label("Usuario", 37, 112));
            _user = Input(_store.AdminUsername, 37, 136, 355);
            card.Controls.Add(_user);

            card.Controls.Add(Label("Contraseña", 37, 181));
            _pass = Input("", 37, 205, 355);
            _pass.PasswordChar = '●';
            card.Controls.Add(_pass);

            _login = Theme.Primary("INGRESAR");
            _login.Width = 355;
            _login.Location = new Point(37, 257);
            _login.Click += Login;
            card.Controls.Add(_login);

            Button forgot = Theme.Secondary("¿OLVIDASTE TU CONTRASEÑA?");
            forgot.Width = 355;
            forgot.Location = new Point(37, 305);
            forgot.Click += ForgotPassword;
            card.Controls.Add(forgot);

            _status = new Label();
            _status.Text = "Ingresá con las credenciales proporcionadas por el administrador.";
            _status.AutoSize = false;
            _status.Width = 355;
            _status.Height = 42;
            _status.ForeColor = Theme.Muted;
            _status.Font = Theme.Font(8.5f, FontStyle.Regular);
            _status.Location = new Point(37, 350);
            card.Controls.Add(_status);

            AcceptButton = _login;
            Shown += delegate { _pass.Focus(); };
        }

        private Label Label(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Theme.Muted;
            l.Location = new Point(x, y);
            l.Font = Theme.Font(9, FontStyle.Bold);
            return l;
        }

        private TextBox Input(string text, int x, int y, int width)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.Width = width;
            t.Height = 30;
            t.Font = Theme.Font(10, FontStyle.Regular);
            t.BackColor = Theme.Card2;
            t.ForeColor = Theme.Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Location = new Point(x, y);
            return t;
        }

        private void Login(object sender, EventArgs e)
        {
            string username = _user.Text.Trim();
            string password = _pass.Text;
            if (!_store.VerifyAdminPassword(username, password))
            {
                _status.Text = "Usuario o contraseña incorrectos.";
                _status.ForeColor = Theme.Danger;
                _pass.SelectAll();
                _pass.Focus();
                return;
            }

            if (_store.AdminMustChangePassword)
            {
                using (ChangePasswordForm change = new ChangePasswordForm(_store, true))
                {
                    if (change.ShowDialog(this) != DialogResult.OK)
                    {
                        _status.Text = "Debés cambiar la contraseña inicial para continuar.";
                        _status.ForeColor = Theme.Warning;
                        return;
                    }
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void ForgotPassword(object sender, EventArgs e)
        {
            string email = _store.GetSetting("admin_recovery_email", "").Trim();
            if (email.Length == 0)
            {
                MessageBox.Show("No hay un correo de recuperación configurado. Entrá a Configuración > Seguridad desde una sesión autorizada y cargalo.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string smtpUser = _store.GetSetting("smtp_user", "").Trim();
            string smtpPassword = _store.GetSetting("smtp_app_password", "");
            if (smtpUser.Length == 0 || smtpPassword.Length == 0)
            {
                MessageBox.Show("El correo de recuperación todavía no tiene configurado Gmail/SMTP. Configurá el correo emisor y la App Password.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string code = new Random().Next(100000, 999999).ToString();
            _store.SetSetting("recovery_code", code);
            _store.SetSetting("recovery_code_expires", DateTime.Now.AddMinutes(10).ToString("o"));

            try
            {
                SendRecoveryMail(email, smtpUser, smtpPassword, code);
                using (RecoveryCodeForm recovery = new RecoveryCodeForm(_store))
                {
                    recovery.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                _store.SetSetting("recovery_code", "");
                _store.SetSetting("recovery_code_expires", "");
                MessageBox.Show("No se pudo enviar el correo de recuperación.\r\n\r\n" + ex.Message, "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SendRecoveryMail(string destination, string smtpUser, string smtpPassword, string code)
        {
            string host = _store.GetSetting("smtp_host", "smtp.gmail.com");
            int port;
            if (!int.TryParse(_store.GetSetting("smtp_port", "587"), out port)) port = 587;
            bool ssl = _store.GetSetting("smtp_ssl", "1") == "1";

            using (MailMessage mail = new MailMessage())
            {
                mail.From = new MailAddress(smtpUser, "NexoMarket");
                mail.To.Add(destination);
                mail.Subject = "NexoMarket · Código de recuperación";
                mail.Body = "Se solicitó recuperar el acceso de NexoMarket.\r\n\r\n" +
                            "Código de recuperación: " + code + "\r\n\r\n" +
                            "El código vence en 10 minutos. Si no solicitaste este cambio, ignorá este mensaje.";
                mail.IsBodyHtml = false;

                using (SmtpClient smtp = new SmtpClient(host, port))
                {
                    smtp.EnableSsl = ssl;
                    smtp.Credentials = new NetworkCredential(smtpUser, smtpPassword);
                    smtp.Timeout = 15000;
                    smtp.Send(mail);
                }
            }
        }
    }

    internal sealed class ChangePasswordForm : Form
    {
        private readonly AppDataStore _store;
        private TextBox _current;
        private TextBox _newPass;
        private TextBox _repeat;
        private bool _force;

        public ChangePasswordForm(AppDataStore store, bool force)
        {
            _store = store;
            _force = force;
            Text = force ? "NexoMarket · Cambio obligatorio" : "NexoMarket · Cambiar contraseña";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(460, force ? 345 : 315);
            BackColor = Theme.Background;

            Panel card = Theme.Card();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(28);
            Controls.Add(card);

            Label title = new Label { Text = force ? "CAMBIO DE CONTRASEÑA OBLIGATORIO" : "CAMBIAR CONTRASEÑA", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(15, FontStyle.Bold), Location = new Point(28, 24) };
            card.Controls.Add(title);
            Label info = new Label { Text = force ? "Por seguridad, la contraseña inicial solo puede utilizarse una vez." : "Elegí una nueva contraseña para tu cuenta.", AutoSize = false, Width = 390, Height = 40, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular), Location = new Point(28, 55) };
            card.Controls.Add(info);

            int y = 102;
            if (!force)
            {
                card.Controls.Add(MakeLabel("Contraseña actual", 28, y));
                _current = MakeInput(28, y + 20, 360);
                _current.PasswordChar = '●';
                card.Controls.Add(_current);
                y += 54;
            }
            else
            {
                _current = MakeInput(28, y + 20, 360);
                _current.Visible = false;
            }

            card.Controls.Add(MakeLabel("Nueva contraseña", 28, y));
            _newPass = MakeInput(28, y + 20, 360);
            _newPass.PasswordChar = '●';
            card.Controls.Add(_newPass);
            y += 54;

            card.Controls.Add(MakeLabel("Repetir nueva contraseña", 28, y));
            _repeat = MakeInput(28, y + 20, 360);
            _repeat.PasswordChar = '●';
            card.Controls.Add(_repeat);
            y += 58;

            Button save = Theme.Primary("GUARDAR NUEVA CONTRASEÑA");
            save.Width = 360;
            save.Location = new Point(28, y);
            save.Click += Save;
            card.Controls.Add(save);
            AcceptButton = save;
        }

        private Label MakeLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(x, y) };
        }

        private TextBox MakeInput(int x, int y, int width)
        {
            return new TextBox { Width = width, Height = 29, BackColor = Theme.Card2, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Font(10, FontStyle.Regular), Location = new Point(x, y) };
        }

        private void Save(object sender, EventArgs e)
        {
            string password = _newPass.Text;
            if (password.Length < 6)
            {
                MessageBox.Show("La nueva contraseña debe tener al menos 6 caracteres.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (password != _repeat.Text)
            {
                MessageBox.Show("Las nuevas contraseñas no coinciden.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_force && !_store.VerifyAdminPassword(_store.AdminUsername, _current.Text))
            {
                MessageBox.Show("La contraseña actual no es correcta.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _store.SetAdminPassword(password);
            MessageBox.Show("Contraseña actualizada correctamente. La contraseña temporal anterior ya no es válida.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class SellerRecoveryCodeForm : Form
    {
        private readonly AppDataStore _store;
        private readonly string _email;
        private TextBox _code;
        private TextBox _newPass;
        private TextBox _repeat;

        public SellerRecoveryCodeForm(AppDataStore store, string email)
        {
            _store = store; _email = email ?? "";
            Text = "NexoMarket · Recuperar vendedor";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(470, 355);
            BackColor = Theme.Background;
            Panel card = Theme.Card(); card.Dock = DockStyle.Fill; card.Padding = new Padding(28); Controls.Add(card);
            card.Controls.Add(new Label { Text = "RECUPERAR CUENTA DE VENDEDOR", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(15, FontStyle.Bold), Location = new Point(28, 24) });
            card.Controls.Add(new Label { Text = "Código enviado a " + _email + ". Tiene una vigencia de 10 minutos.", AutoSize = false, Width = 390, Height = 38, ForeColor = Theme.Muted, Font = Theme.Font(9), Location = new Point(28, 56) });
            card.Controls.Add(new Label { Text = "Código recibido", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(28, 101) });
            _code = new TextBox { Width = 360, Height = 29, Location = new Point(28, 121), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(11, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle, MaxLength = 6 };
            card.Controls.Add(_code);
            card.Controls.Add(new Label { Text = "Nueva contraseña", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(28, 158) });
            _newPass = new TextBox { Width = 360, Height = 29, Location = new Point(28, 178), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(10), BorderStyle = BorderStyle.FixedSingle, PasswordChar = '●' }; card.Controls.Add(_newPass);
            card.Controls.Add(new Label { Text = "Repetir nueva contraseña", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(28, 215) });
            _repeat = new TextBox { Width = 360, Height = 29, Location = new Point(28, 235), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(10), BorderStyle = BorderStyle.FixedSingle, PasswordChar = '●' }; card.Controls.Add(_repeat);
            Button save = Theme.Primary("RESTABLECER CONTRASEÑA"); save.Width = 360; save.Location = new Point(28, 278); save.Click += ResetPassword; card.Controls.Add(save); AcceptButton = save;
        }

        private void ResetPassword(object sender, EventArgs e)
        {
            WebUser user;
            if (!_store.VerifyWebRecoveryCode(_email, _code.Text.Trim(), out user)) { MessageBox.Show("El código no es válido o ya venció.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (_newPass.Text.Length < 6 || _newPass.Text != _repeat.Text) { MessageBox.Show("La nueva contraseña debe tener al menos 6 caracteres y coincidir.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!_store.SetWebUserPassword(user.Id, _newPass.Text)) { MessageBox.Show("No se pudo actualizar la contraseña.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            _store.SetSetting("seller_account_email", user.Email);
            MessageBox.Show("Contraseña de vendedor actualizada. Ya podés entrar en Windows y en el Seller Center web con la nueva contraseña.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK; Close();
        }
    }

    internal sealed class RecoveryCodeForm : Form
    {
        private readonly AppDataStore _store;
        private TextBox _code;
        private TextBox _newPass;
        private TextBox _repeat;

        public RecoveryCodeForm(AppDataStore store)
        {
            _store = store;
            Text = "NexoMarket · Recuperar acceso";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(470, 355);
            BackColor = Theme.Background;

            Panel card = Theme.Card(); card.Dock = DockStyle.Fill; card.Padding = new Padding(28); Controls.Add(card);
            card.Controls.Add(new Label { Text = "RECUPERAR ACCESO", AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(16, FontStyle.Bold), Location = new Point(28, 24) });
            card.Controls.Add(new Label { Text = "Revisá el correo configurado. El código tiene una vigencia de 10 minutos.", AutoSize = false, Width = 390, Height = 38, ForeColor = Theme.Muted, Font = Theme.Font(9, FontStyle.Regular), Location = new Point(28, 56) });

            card.Controls.Add(new Label { Text = "Código recibido", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(28, 101) });
            _code = new TextBox { Width = 360, Height = 29, Location = new Point(28, 121), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(11, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle, MaxLength = 6 };
            card.Controls.Add(_code);
            card.Controls.Add(new Label { Text = "Nueva contraseña", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(28, 158) });
            _newPass = new TextBox { Width = 360, Height = 29, Location = new Point(28, 178), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(10), BorderStyle = BorderStyle.FixedSingle, PasswordChar = '●' };
            card.Controls.Add(_newPass);
            card.Controls.Add(new Label { Text = "Repetir nueva contraseña", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold), Location = new Point(28, 215) });
            _repeat = new TextBox { Width = 360, Height = 29, Location = new Point(28, 235), BackColor = Theme.Card2, ForeColor = Theme.Text, Font = Theme.Font(10), BorderStyle = BorderStyle.FixedSingle, PasswordChar = '●' };
            card.Controls.Add(_repeat);
            Button save = Theme.Primary("RESTABLECER CONTRASEÑA"); save.Width = 360; save.Location = new Point(28, 278); save.Click += ResetPassword; card.Controls.Add(save);
            AcceptButton = save;
        }

        private void ResetPassword(object sender, EventArgs e)
        {
            DateTime expires;
            string savedCode = _store.GetSetting("recovery_code", "");
            if (!DateTime.TryParse(_store.GetSetting("recovery_code_expires", ""), out expires) || DateTime.Now > expires || savedCode.Length == 0 || savedCode != _code.Text.Trim())
            {
                MessageBox.Show("El código no es válido o ya venció.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_newPass.Text.Length < 6 || _newPass.Text != _repeat.Text)
            {
                MessageBox.Show("La nueva contraseña debe tener al menos 6 caracteres y coincidir en ambos campos.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _store.SetAdminPassword(_newPass.Text);
            _store.SetSetting("recovery_code", "");
            _store.SetSetting("recovery_code_expires", "");
            MessageBox.Show("Contraseña restablecida correctamente. Ya podés iniciar sesión.", "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
