using System;
using System.Security.Cryptography;
using System.Text;
#if WINDOWS_UWP
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Security.Cryptography.DataProtection;
#endif

namespace MetroTelegram.Crypto
{
    /// <summary>
    /// Seals secrets with the system's data protection API before they are written to app storage,
    /// so the Telegram login key is never stored in plain text. Windows Phone 8.1 uses DPAPI
    /// (ProtectedData); Windows 10 uses DataProtectionProvider for the current user.
    /// </summary>
    public static class SecureStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RetroTel.AuthKey.v1");

        public static byte[] Seal(byte[] plain)
        {
            if (plain == null) return null;
#if WINDOWS_UWP
            var provider = new DataProtectionProvider("LOCAL=user");
            var sealedBuffer = provider.ProtectAsync(plain.AsBuffer()).AsTask().GetAwaiter().GetResult();
            return sealedBuffer.ToArray();
#else
            return ProtectedData.Protect(plain, Entropy);
#endif
        }

        /// <summary>Returns null if the data can't be unsealed (for example after a restore to another phone).</summary>
        public static byte[] Unseal(byte[] sealedData)
        {
            if (sealedData == null) return null;
            try
            {
#if WINDOWS_UWP
                var provider = new DataProtectionProvider();
                var plain = provider.UnprotectAsync(sealedData.AsBuffer()).AsTask().GetAwaiter().GetResult();
                return plain.ToArray();
#else
                return ProtectedData.Unprotect(sealedData, Entropy);
#endif
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
