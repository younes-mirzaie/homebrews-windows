// An account's inbox pinned to Start: a tile of its own with that account's unread count and
// newest mail, which opens straight into that inbox. Shared by the 8.1 and Windows 10 apps.
using System;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.StartScreen;

namespace Retrobird.Core
{
    public static class Pins
    {
        /// <summary>The launch arguments of a pinned inbox ("inbox=&lt;account id&gt;").</summary>
        public const string Prefix = "inbox=";

        public static bool IsPinned(AccountInfo a)
        {
            try { return SecondaryTile.Exists(Notify.AccountTileId(a.Id)); }
            catch (Exception) { return false; }
        }

        public static async Task<bool> PinAsync(AccountInfo a)
        {
            var tile = new SecondaryTile(Notify.AccountTileId(a.Id), a.ShortName, Prefix + Uri.EscapeDataString(a.Id),
                                         new Uri("ms-appx:///Assets/Square150x150Logo.png"), TileSize.Square150x150);
            tile.VisualElements.Square71x71Logo = new Uri("ms-appx:///Assets/Square71x71Logo.png");
            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            // transparent: the tile takes the accent color, like the app's own
            tile.VisualElements.BackgroundColor = Colors.Transparent;
            bool ok = await tile.RequestCreateAsync();
            if (ok) Notify.UpdateAccountTile(a.Id, a.Unseen, null);
            return ok;
        }

        public static async Task UnpinAsync(AccountInfo a)
        {
            var id = Notify.AccountTileId(a.Id);
            if (SecondaryTile.Exists(id)) await new SecondaryTile(id).RequestDeleteAsync();
        }
    }
}
