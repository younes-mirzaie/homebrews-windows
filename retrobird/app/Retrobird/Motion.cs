// Tilt for tappable things that aren't buttons or list items (those tilt by themselves on the
// phone): the system's own pointer-down/up animation (metro-ui-design skill, section 6).
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;

namespace Retrobird
{
    public static class Motion
    {
        public static readonly DependencyProperty TiltProperty =
            DependencyProperty.RegisterAttached("Tilt", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTiltChanged));

        public static bool GetTilt(DependencyObject o) { return (bool)o.GetValue(TiltProperty); }
        public static void SetTilt(DependencyObject o, bool v) { o.SetValue(TiltProperty, v); }

        static void OnTiltChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            var el = o as FrameworkElement;
            if (el == null || !(bool)e.NewValue) return;
            el.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, a) => Play(el, true)), true);
            el.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, a) => Play(el, false)), true);
            el.PointerExited += (s, a) => Play(el, false);
            el.PointerCaptureLost += (s, a) => Play(el, false);
            el.PointerCanceled += (s, a) => Play(el, false);
        }

        static void Play(FrameworkElement el, bool down)
        {
            // pointer events still arrive while a page is being left; the element may already be
            // out of the visual tree, where Storyboard.Begin throws. A missed tilt doesn't matter.
            if (Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(el) == null) return;
            try
            {
                var sb = new Storyboard();
                Timeline t = down ? (Timeline)new PointerDownThemeAnimation() : new PointerUpThemeAnimation();
                Storyboard.SetTarget(t, el);
                sb.Children.Add(t);
                sb.Begin();
            }
            catch (System.Exception) { }
        }
    }
}
