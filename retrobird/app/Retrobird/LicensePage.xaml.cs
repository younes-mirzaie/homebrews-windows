// Credits Mozilla's NSPR and NSS, says where their source is, and shows the MPL 2.0 text
// (Legal\NOTICES.txt, Legal\MPL-2.0.txt and the build-file patch in the package).
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Retrobird
{
    public sealed partial class LicensePage : RbPage
    {
        bool _loaded;

        public LicensePage() { InitializeComponent(); }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_loaded) return;
            _loaded = true;
            Add(await Read(@"Legal\NOTICES.txt"), true);
            Add(await Read(@"Legal\MPL-2.0.txt"), false);
            Add("Changes to UXP's NSPR and NSS build files", true);
            Add((await Read(@"Legal\uxp-nss-nspr-changes.patch")).Replace("\n\n", "\n \n"), false);
        }

        static async Task<string> Read(string file)
        {
            try
            {
                var f = await Package.Current.InstalledLocation.GetFileAsync(file);
                return await FileIO.ReadTextAsync(f);
            }
            catch (Exception) { return "(" + file + " is missing from this copy of Retrobird.)"; }
        }

        void Add(string text, bool notices)
        {
            text = text.Replace("\r\n", "\n");
            foreach (var para in text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = para.Trim('\n');
                if (p.Length == 0) continue;
                // the notices are prose with short headings; the license keeps its own line breaks
                bool heading = notices && p.IndexOf('\n') < 0 && p.Length < 50 && !p.EndsWith(".");
                var tb = new TextBlock
                {
                    Text = notices && !heading ? p.Replace("\n", " ") : p,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, heading ? 14 : 0, 0, 9.5),
                };
                if (heading) { tb.FontSize = 20.27; tb.FontWeight = Windows.UI.Text.FontWeights.SemiLight; }
                else tb.Style = (Style)Application.Current.Resources[notices ? "RbSubtle" : "RbSmall"];
                Paragraphs.Children.Add(tb);
            }
        }
    }
}
