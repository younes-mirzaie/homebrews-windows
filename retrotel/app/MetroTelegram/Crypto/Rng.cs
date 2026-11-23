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

    /// <summary>
    /// SHA-1 and SHA-256. Windows Phone 8.1 has SHA1Managed/SHA256Managed; Windows 10 (UWP) only
    /// offers SHA1.Create()/SHA256.Create().
    /// </summary>
    public static class Hashes
    {
        public static HashAlgorithm Sha1()
        {
#if WINDOWS_UWP
            return SHA1.Create();
#else
            return new SHA1Managed();
#endif
        }

        public static HashAlgorithm Sha256()
        {
#if WINDOWS_UWP
            return SHA256.Create();
#else
            return new SHA256Managed();
#endif
        }
    }
}
