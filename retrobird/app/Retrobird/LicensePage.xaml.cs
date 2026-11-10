// Credits Mozilla's NSPR and NSS, says where their source is, and shows the MPL 2.0 text
// (Legal\NOTICES.txt, Legal\MPL-2.0.txt and the build-file patch in the package).
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;

namespace Retrobird
{
    public partial class LicensePage : PhoneApplicationPage
    {
        bool _loaded;

        public LicensePage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (_loaded) return;
            _loaded = true;
            Add(Read(@"Legal\NOTICES.txt"), true);
            Add(Read(@"Legal\MPL-2.0.txt"), false);
            Add("Changes to UXP's NSPR and NSS build files", true);
            Add(Read(@"Legal\uxp-nss-nspr-changes.patch").Replace("\n\n", "\n \n"), false);
        }

        static string Read(string file)
        {
            try
            {
                var info = Application.GetResourceStream(new Uri(file.Replace('\\', '/'), UriKind.Relative));
                using (var r = new StreamReader(info.Stream)) return r.ReadToEnd();
            }
            catch (Exception ex) { return "(" + file + " is missing: " + ex.Message + ")"; }
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
                Paragraphs.Children.Add(new TextBlock
                {
                    Text = notices && !heading ? p.Replace("\n", " ") : p,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, heading ? 18 : 0, 0, 12),
                    FontSize = heading ? (double)Application.Current.Resources["PhoneFontSizeMedium"]
                                       : (double)Application.Current.Resources[notices ? "PhoneFontSizeNormal" : "PhoneFontSizeSmall"],
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources[heading ? "PhoneForegroundBrush" : "PhoneSubtleBrush"]
                });
            }
        }
    }
}
