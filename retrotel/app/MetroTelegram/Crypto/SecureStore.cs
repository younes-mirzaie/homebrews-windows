using System;
using System.Security.Cryptography;
using System.Text;

namespace MetroTelegram.Crypto
{
    /// <summary>
    /// Seals secrets with the phone's data protection API (DPAPI) before they are written to
    /// isolated storage, so the Telegram login key is never stored in plain text.
    /// </summary>
    public static class SecureStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RetroTel.AuthKey.v1");

        public static byte[] Seal(byte[] plain)
        {
            if (plain == null) return null;
            return ProtectedData.Protect(plain, Entropy);
        }

        /// <summary>Returns null if the data can't be unsealed (for example after a restore to another phone).</summary>
        public static byte[] Unseal(byte[] sealedData)
        {
            if (sealedData == null) return null;
            try
            {
                return ProtectedData.Unprotect(sealedData, Entropy);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }
}
