// Metro motion without the toolkit: tilt feedback on tappable items and a turnstile when a page
// comes in (see the metro-ui-design skill, section 6).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Retrobird
{
    public static class Motion
    {
        // ---------------- tilt ----------------

        public static readonly DependencyProperty TiltProperty =
            DependencyProperty.RegisterAttached("Tilt", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnTiltChanged));

        public static bool GetTilt(DependencyObject o) { return (bool)o.GetValue(TiltProperty); }
        public static void SetTilt(DependencyObject o, bool v) { o.SetValue(TiltProperty, v); }

        static void OnTiltChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            var el = o as FrameworkElement;
            if (el == null || !(bool)e.NewValue) return;
            // handledEventsToo: buttons mark these handled before we would see them
            el.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, a) => Press(el, a.GetPosition(el))), true);
            el.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((s, a) => Release(el)), true);
            el.MouseLeave += (s, a) => Release(el);
            el.LostMouseCapture += (s, a) => Release(el);
        }

        // Tilts toward the touch point and presses in a little (about 10 degrees, scale 0.97)
        static void Press(FrameworkElement el, Point p)
        {
            double w = el.ActualWidth, h = el.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var proj = new PlaneProjection
            {
                CenterOfRotationX = 0.5, CenterOfRotationY = 0.5,
                // touch near an edge rotates that edge away; the middle just presses in
                RotationY = Clamp((p.X / w - 0.5) * -20, 10),
                RotationX = Clamp((p.Y / h - 0.5) * 20, 10),
            };
            el.Projection = proj;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new ScaleTransform { ScaleX = 0.97, ScaleY = 0.97 };
        }

        static void Release(FrameworkElement el)
        {
            var proj = el.Projection as PlaneProjection;
            var scale = el.RenderTransform as ScaleTransform;
            if (proj == null && scale == null) return;
            var sb = new Storyboard();
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var dur = TimeSpan.FromMilliseconds(150);
            if (proj != null)
            {
                Add(sb, proj, "RotationX", 0, dur, ease);
                Add(sb, proj, "RotationY", 0, dur, ease);
            }
            if (scale != null)
            {
                Add(sb, scale, "ScaleX", 1, dur, ease);
                Add(sb, scale, "ScaleY", 1, dur, ease);
            }
            sb.Begin();
        }

        static double Clamp(double v, double max) { return Math.Max(-max, Math.Min(max, v)); }

        static void Add(Storyboard sb, DependencyObject target, string prop, double to, TimeSpan dur, IEasingFunction ease,
                        double? from = null, TimeSpan? delay = null, bool hold = true)
        {
            var a = new DoubleAnimation { To = to, Duration = dur, EasingFunction = ease };
            // Stop hands the property back to its own value afterwards (pages change Opacity later)
            if (!hold) a.FillBehavior = FillBehavior.Stop;
            if (from.HasValue) a.From = from.Value;
            if (delay.HasValue) a.BeginTime = delay.Value;
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, new PropertyPath(prop));
            sb.Children.Add(a);
        }

        // ---------------- turnstile ----------------

        /// <summary>Feathered turnstile in: the page's top-level rows swing in around the left
        /// edge, one after another (about 300 ms).</summary>
        public static void TurnstileIn(Page page)
        {
            var root = page.Content as Panel;
            if (root == null) return;
            var sb = new Storyboard();
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
            int i = 0;
            foreach (var child in root.Children)
            {
                var el = child as FrameworkElement;
                // the web view draws outside XAML's projection; it fades in by itself once loaded
                if (el == null || el.Visibility != Visibility.Visible || el is Microsoft.Phone.Controls.WebBrowser) continue;
                var proj = new PlaneProjection { CenterOfRotationX = 0, RotationY = -80 };
                el.Projection = proj;
                var delay = TimeSpan.FromMilliseconds(40 * i++);
                Add(sb, proj, "RotationY", 0, TimeSpan.FromMilliseconds(300), ease, -80, delay);
                Add(sb, el, "Opacity", el.Opacity, TimeSpan.FromMilliseconds(120), null, 0, delay, false);
            }
            sb.Completed += (s, e) =>
            {
                foreach (var child in root.Children)
                {
                    var el = child as FrameworkElement;
                    if (el != null && el.Projection is PlaneProjection) el.Projection = null;
                }
            };
            sb.Begin();
        }
    }
}
