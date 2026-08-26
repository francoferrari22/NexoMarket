using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;

namespace NexoMarket.CentralServer
{
    /// <summary>
    /// Correo transaccional en segundo plano. No bloquea checkout/login.
    /// Configuración por variables de entorno:
    /// NEXOMARKET_SMTP_HOST, NEXOMARKET_SMTP_PORT, NEXOMARKET_SMTP_USER,
    /// NEXOMARKET_SMTP_PASSWORD, NEXOMARKET_SMTP_FROM, NEXOMARKET_SMTP_SSL.
    /// </summary>
    internal sealed class TransactionalEmailService : IDisposable
    {
        private readonly string _root;
        private readonly string _queue;
        private readonly object _gate = new object();
        private readonly Timer _timer;
        private volatile bool _disposed;

        public TransactionalEmailService(string root)
        {
            _root = root;
            _queue = Path.Combine(root, "email_queue");
            Directory.CreateDirectory(_queue);
            _timer = new Timer(delegate { ProcessQueue(); }, null, 2000, 15000);
        }

        public bool Enabled
        {
            get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_HOST")); }
        }

        public void Queue(string to, string subject, string html, string text)
        {
            if (string.IsNullOrWhiteSpace(to)) return;
            try
            {
                string id = Guid.NewGuid().ToString("N") + ".eml";
                string body = Convert.ToBase64String(Encoding.UTF8.GetBytes((to ?? "") + "\n" + (subject ?? "") + "\n" + (html ?? "") + "\n" + (text ?? "")));
                File.WriteAllText(Path.Combine(_queue, id), body, Encoding.UTF8);
            }
            catch { }
        }

        private void ProcessQueue()
        {
            if (_disposed || !Enabled) return;
            lock (_gate)
            {
                string[] files;
                try { files = Directory.GetFiles(_queue, "*.eml"); } catch { return; }
                int sent = 0;
                foreach (string file in files)
                {
                    if (sent >= 10) break;
                    try
                    {
                        string raw = File.ReadAllText(file, Encoding.UTF8);
                        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                        string[] p = decoded.Split(new[] { '\n' }, 4);
                        if (p.Length < 4) { File.Delete(file); continue; }
                        Send(p[0], p[1], p[2], p[3]);
                        File.Delete(file);
                        sent++;
                    }
                    catch
                    {
                        try
                        {
                            string retry = file + ".retry";
                            if (!File.Exists(retry)) File.Move(file, retry);
                        }
                        catch { }
                    }
                }
            }
        }

        private void Send(string to, string subject, string html, string text)
        {
            string host = Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_HOST");
            int port = 587;
            int.TryParse(Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_PORT"), out port);
            string user = Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_USER") ?? "";
            string password = Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_PASSWORD") ?? "";
            string from = Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_FROM");
            if (string.IsNullOrWhiteSpace(from)) from = user;
            bool ssl = !string.Equals(Environment.GetEnvironmentVariable("NEXOMARKET_SMTP_SSL"), "0", StringComparison.OrdinalIgnoreCase);

            using (SmtpClient smtp = new SmtpClient(host, port))
            using (MailMessage msg = new MailMessage(from, to))
            {
                smtp.EnableSsl = ssl;
                if (!string.IsNullOrWhiteSpace(user)) smtp.Credentials = new NetworkCredential(user, password);
                msg.Subject = subject ?? "NexoMarket";
                msg.Body = string.IsNullOrWhiteSpace(text) ? StripHtml(html) : text;
                msg.IsBodyHtml = false;
                if (!string.IsNullOrWhiteSpace(html))
                {
                    AlternateView view = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html");
                    msg.AlternateViews.Add(view);
                }
                smtp.Send(msg);
            }
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        }

        public void Dispose()
        {
            _disposed = true;
            try { _timer.Dispose(); } catch { }
        }
    }
}
