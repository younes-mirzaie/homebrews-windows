using System;
using System.Text.RegularExpressions;

namespace MetroTelegram.Services
{
    /// <summary>
    /// Turns Telegram error codes (e.g. PHONE_CODE_INVALID) into plain sentences for message boxes.
    /// Only for display: the RPC layer still works with the raw codes.
    /// </summary>
    public static class FriendlyErrors
    {
        public static string ToText(Exception ex)
        {
            string raw = ex == null ? string.Empty : (ex.Message ?? string.Empty);

            var flood = Regex.Match(raw, @"FLOOD_WAIT_(\d+)");
            if (flood.Success)
            {
                int seconds = int.Parse(flood.Groups[1].Value);
                return seconds >= 120
                    ? string.Format("Telegram asks you to wait about {0} minutes before trying again.", (seconds + 59) / 60)
                    : string.Format("Telegram asks you to wait {0} seconds before trying again.", seconds);
            }

            if (raw.Contains("PHONE_NUMBER_INVALID")) return "That phone number doesn't look right. Include the country code, like +374 for Armenia.";
            if (raw.Contains("PHONE_NUMBER_BANNED")) return "Telegram has blocked this phone number.";
            if (raw.Contains("PHONE_NUMBER_UNOCCUPIED")) return "There's no Telegram account for this number yet. Create one in the official Telegram app first.";
            if (raw.Contains("PHONE_CODE_INVALID")) return "That code isn't right. Check it and try again.";
            if (raw.Contains("PHONE_CODE_EXPIRED")) return "That code has expired. Go back and ask for a new one.";
            if (raw.Contains("PHONE_CODE_EMPTY")) return "Enter the code.";
            if (raw.Contains("PASSWORD_HASH_INVALID")) return "That password isn't right. Try again.";
            if (raw.Contains("UPDATE_APP_TO_LOGIN")) return "Telegram says this app is too old to sign in. RetroTel needs an update.";
            if (raw.Contains("API_ID_INVALID") || raw.Contains("API_ID_PUBLISHED_FLOOD")) return "Telegram didn't accept this app's API key. RetroTel needs a rebuild with a valid api_id.";
            if (raw.Contains("AUTH_RESTART")) return "Telegram asked to start the sign-in again. Go back and enter your number.";
            if (raw.Contains("AUTH_KEY_UNREGISTERED") || raw.Contains("SESSION_REVOKED")) return "You were signed out. Sign in again.";
            if (raw.Contains("USER_DEACTIVATED")) return "This Telegram account was deleted.";
            if (raw.Contains("CHAT_WRITE_FORBIDDEN")) return "You can't send messages in this chat.";
            if (raw.Contains("MESSAGE_DELETE_FORBIDDEN")) return "You can't delete this message.";
            if (raw.Contains("MESSAGE_TOO_LONG")) return "That message is too long.";

            // Any other all-caps Telegram code: don't show the code itself.
            if (Regex.IsMatch(raw.Trim(), @"^[A-Z0-9_]+$"))
                return "Telegram couldn't do that right now. Try again in a moment.";

            return raw;
        }
    }
}
