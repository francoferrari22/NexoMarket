using System;
using System.Security.Cryptography;
using System.Text;

namespace NexoMarket.Licensing
{
    public sealed class LicenseRecord
    {
        public string StoreId;
        public string MachineId;
        public string ClientName;
        public int Days;
        public DateTime IssuedUtc;
        public DateTime ExpiresUtc;
        public string Status;
        public string Signature;
    }

    public static class LicenseCore
    {
        public const string Prefix = "NLM1";
        public static string MachineId()
        {
            string machineGuid = "";
            try { using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")) machineGuid = Convert.ToString(key == null ? null : key.GetValue("MachineGuid")); } catch { }
            string raw = (machineGuid + "|" + Environment.MachineName).Trim();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder s = new StringBuilder();
                foreach (byte x in b) s.Append(x.ToString("X2"));
                return s.ToString();
            }
        }

        public static string Canonical(LicenseRecord r)
        {
            return Prefix + "|" + Clean(r.StoreId) + "|" + Clean(r.MachineId) + "|" + Clean(r.ClientName) + "|" +
                   r.Days.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
                   r.IssuedUtc.ToString("o") + "|" + r.ExpiresUtc.ToString("o") + "|" + Clean(r.Status);
        }

        private static string Clean(string s) { return (s ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim(); }

        public static string Sign(LicenseRecord r, string privateKeyXml)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.FromXmlString(privateKeyXml);
                byte[] data = Encoding.UTF8.GetBytes(Canonical(r));
                return Convert.ToBase64String(rsa.SignData(data, CryptoConfig.MapNameToOID("SHA256")));
            }
        }

        public static bool Verify(LicenseRecord r, string publicKeyXml)
        {
            try
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Signature) || string.IsNullOrWhiteSpace(publicKeyXml)) return false;
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
                {
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(Encoding.UTF8.GetBytes(Canonical(r)), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(r.Signature));
                }
            }
            catch { return false; }
        }

        public static string Serialize(LicenseRecord r)
        {
            string[] values = {
                Prefix, r.StoreId, r.MachineId, r.ClientName,
                r.Days.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.IssuedUtc.ToString("o"), r.ExpiresUtc.ToString("o"), r.Status, r.Signature
            };
            for (int i=0;i<values.Length;i++) values[i] = Convert.ToBase64String(Encoding.UTF8.GetBytes(values[i] ?? ""));
            return string.Join(".", values);
        }

        public static bool TryParse(string token, out LicenseRecord r)
        {
            r = null;
            try
            {
                string[] p = (token ?? "").Trim().Split('.');
                if (p.Length != 9) return false;
                string prefix = D(p[0]); if (prefix != Prefix) return false;
                r = new LicenseRecord();
                r.StoreId = D(p[1]); r.MachineId = D(p[2]); r.ClientName = D(p[3]);
                r.Days = int.Parse(D(p[4]), System.Globalization.CultureInfo.InvariantCulture);
                r.IssuedUtc = DateTime.Parse(D(p[5]), null, System.Globalization.DateTimeStyles.RoundtripKind);
                r.ExpiresUtc = DateTime.Parse(D(p[6]), null, System.Globalization.DateTimeStyles.RoundtripKind);
                r.Status = D(p[7]); r.Signature = D(p[8]);
                return true;
            }
            catch { return false; }
        }

        private static string D(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

        public static string Status(LicenseRecord r, DateTime utcNow)
        {
            if (r == null) return "Sin licencia";
            if (!string.Equals(r.Status, "Active", StringComparison.OrdinalIgnoreCase)) return r.Status;
            if (r.ExpiresUtc <= utcNow) return "Vencida";
            return "Activa";
        }

        public static int DaysRemaining(LicenseRecord r, DateTime utcNow)
        {
            if (r == null || !string.Equals(r.Status, "Active", StringComparison.OrdinalIgnoreCase) || r.ExpiresUtc <= utcNow) return 0;
            if (r.ExpiresUtc.Year >= 9999) return -1;
            TimeSpan d = r.ExpiresUtc.Date - utcNow.Date;
            return Math.Max(0, (int)d.TotalDays);
        }
    }
}
