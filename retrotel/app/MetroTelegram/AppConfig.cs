namespace MetroTelegram
{
    // Public app settings. The Telegram api_id and api_hash live in AppConfig.Secrets.cs,
    // which build.ps1 generates from the TG_API_ID and TG_API_HASH environment variables.
    // That file is git-ignored and must never be committed.
    public static partial class AppConfig
    {
        public const string AppName = "RetroTel";
#if WINDOWS_UWP
        public const string AppVersion = "0.2.0";
        public const string DeviceModel = "Windows 10 device";
        public const string SystemVersion = "Windows 10";
#else
        public const string AppVersion = "0.1.3";
        public const string DeviceModel = "Windows Phone";
        public const string SystemVersion = "Windows Phone 8.1";
#endif
        public const string LangCode = "en";
        public const string LangPack = "";
    }
}
