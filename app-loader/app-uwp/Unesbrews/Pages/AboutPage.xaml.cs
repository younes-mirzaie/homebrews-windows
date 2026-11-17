using Windows.ApplicationModel;
using Windows.UI.Xaml.Controls;

namespace Unesbrews.Pages
{
    public sealed partial class AboutPage : Page, ITitled
    {
        public string Title { get { return "About"; } }

        public AboutPage()
        {
            InitializeComponent();
            var v = Package.Current.Id.Version;
            VersionText.Text = "Version " + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision + " for Windows 10 Mobile";
        }
    }
}
