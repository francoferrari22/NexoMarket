using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NexoMarket.Admin.UI
{
    public static class DeviceIdentity
    {
        public static string GetDeviceId()
        {
            string machineGuid="";
            try { using(RegistryKey k=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")) machineGuid=Convert.ToString(k==null?null:k.GetValue("MachineGuid")); } catch { }
            string raw=(Environment.MachineName+"|"+machineGuid+"|NexoMarket").Trim();
            using(SHA256 sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-","").Substring(0,32).ToUpperInvariant();
        }
    }

    public static class AuthService
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 50000;

        public static string CreateSalt()
        {
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            return Convert.ToBase64String(salt);
        }

        public static string HashPassword(string password, string saltBase64)
        {
            if (password == null) password = "";
            byte[] salt = Convert.FromBase64String(saltBase64);
            using (var kdf = new Rfc2898DeriveBytes(password, salt, Iterations))
            {
                return Convert.ToBase64String(kdf.GetBytes(HashSize));
            }
        }

        public static bool VerifyPassword(string password, string saltBase64, string hashBase64)
        {
            try
            {
                byte[] expected = Convert.FromBase64String(hashBase64 ?? "");
                byte[] actual = Convert.FromBase64String(HashPassword(password, saltBase64));
                if (expected.Length != actual.Length) return false;
                int diff = 0;
                for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
                return diff == 0;
            }
            catch { return false; }
        }

        public static bool LooksConfigured(string salt, string hash)
        {
            if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(hash)) return false;
            try { Convert.FromBase64String(salt); Convert.FromBase64String(hash); return true; }
            catch { return false; }
        }
    }
}
