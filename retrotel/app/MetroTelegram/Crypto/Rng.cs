using System.Security.Cryptography;

namespace MetroTelegram.Crypto
{
    /// <summary>
    /// The system's cryptographic random number generator. Windows Phone 8.1 has
    /// RNGCryptoServiceProvider; Windows 10 (UWP) only offers RandomNumberGenerator.Create().
    /// </summary>
    public static class Rng
    {
        public static RandomNumberGenerator Create()
        {
#if WINDOWS_UWP
            return RandomNumberGenerator.Create();
#else
            return new RNGCryptoServiceProvider();
#endif
        }
    }
}
