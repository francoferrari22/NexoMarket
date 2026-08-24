using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using NexoMarket.Admin.Data;
using NexoMarket.Admin.UI;

namespace NexoMarket.Admin
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexoMarket", "Admin", "Data");
            string legacyData = Path.Combine(baseDir, "Data");
            MigrateLegacyData(legacyData, userData);
            string logDir = Path.Combine(userData, "logs");
            Directory.CreateDirectory(logDir);
            string log = Path.Combine(logDir, "nexomarket_admin.log");

            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Log(log, "THREAD EXCEPTION: " + e.Exception);
                MessageBox.Show("NexoMarket encontró un error.\r\n\r\nSe guardó el detalle en:\r\n" + log,
                    "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log(log, "UNHANDLED EXCEPTION: " + Convert.ToString(e.ExceptionObject));
            };

            try
            {
                Log(log, "============================================================");
                Log(log, "NexoMarket Admin 3.0 - Inicio");
                Log(log, "Base: " + baseDir);
                Log(log, "OS: " + Environment.OSVersion);
                Log(log, "64-bit OS: " + Environment.Is64BitOperatingSystem);
                Log(log, "64-bit proceso: " + Environment.Is64BitProcess);
                Log(log, "CLR: " + Environment.Version);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var store = new AppDataStore(userData))
                using (var login = new SellerAccountForm(store))
                {
                    if (login.ShowDialog() != DialogResult.OK)
                        return;

                    using (var license = new LicenseGateForm(store))
                    {
                        if (license.ShowDialog() != DialogResult.OK)
                            return;
                    }

                    Application.Run(new MainForm(store));
                }
            }
            catch (Exception ex)
            {
                Log(log, "FATAL: " + ex);
                MessageBox.Show("NexoMarket no pudo iniciarse.\r\n\r\nDetalle guardado en:\r\n" + log,
                    "NexoMarket", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void MigrateLegacyData(string legacyDir, string userDir)
        {
            try
            {
                Directory.CreateDirectory(userDir);
                if (!Directory.Exists(legacyDir) || string.Equals(Path.GetFullPath(legacyDir).TrimEnd('\\'), Path.GetFullPath(userDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                foreach (string source in Directory.GetFiles(legacyDir, "*", SearchOption.AllDirectories))
                {
                    string relative = source.Substring(Path.GetFullPath(legacyDir).TrimEnd('\\').Length).TrimStart('\\');
                    string destination = Path.Combine(userDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    if (!File.Exists(destination)) File.Copy(source, destination, false);
                }
            }
            catch { }
        }

        private static void Log(string path, string text)
        {
            try
            {
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
