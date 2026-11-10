// Metro motion without the Windows Phone Toolkit: tilt on touch, turnstile page entrance and
// swivel for prompts (see skills/metro-ui-design).
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroFiles.Core
{
    public static class Metro
    {
        // ---------- tilt: the touched element tilts toward the finger and springs back ----------

        public static readonly DependencyProperty TiltProperty =
            DependencyProperty.RegisterAttached("Tilt", typeof(bool), typeof(Metro), new PropertyMetadata(false, OnTiltChanged));

        public static bool GetTilt(DependencyObject o) { return (bool)o.GetValue(TiltProperty); }
        public static void SetTilt(DependencyObject o, bool v) { o.SetValue(TiltProperty, v); }

        static void OnTiltChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            var el = o as FrameworkElement;
            if (el == null) return;
            if ((bool)e.NewValue)
            {
                el.MouseLeftButtonDown += TiltDown;
                el.MouseLeftButtonUp += TiltRelease;
                el.MouseLeave += TiltRelease;
                el.LostMouseCapture += TiltRelease;
            }
            else
            {
                el.MouseLeftButtonDown -= TiltDown;
                el.MouseLeftButtonUp -= TiltRelease;
                el.MouseLeave -= TiltRelease;
                el.LostMouseCapture -= TiltRelease;
            }
        }

        const double MaxAngle = 10, Depress = 0.97;

        static void TiltDown(object sender, MouseButtonEventArgs e)
        {
            var el = (FrameworkElement)sender;
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return;
            var p = e.GetPosition(el);
            // -1..1 from the center; the touched side goes down
            double x = Math.Max(-1, Math.Min(1, (p.X - el.ActualWidth / 2) / (el.ActualWidth / 2)));
            double y = Math.Max(-1, Math.Min(1, (p.Y - el.ActualHeight / 2) / (el.ActualHeight / 2)));
            el.Projection = new PlaneProjection
            {
                CenterOfRotationX = 0.5, CenterOfRotationY = 0.5,
                RotationY = x * MaxAngle,
                RotationX = -y * MaxAngle
            };
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new ScaleTransform { ScaleX = Depress, ScaleY = Depress };
        }

        static void TiltRelease(object sender, EventArgs e)
        {
            var el = (FrameworkElement)sender;
            if (el.Projection == null) return;
            var sb = new Storyboard();
            var pp = el.Projection as PlaneProjection;
            if (pp != null)
            {
                sb.Children.Add(To(pp, "RotationX", 0, 100));
                sb.Children.Add(To(pp, "RotationY", 0, 100));
            }
            var st = el.RenderTransform as ScaleTransform;
            if (st != null)
            {
                sb.Children.Add(To(st, "ScaleX", 1, 100));
                sb.Children.Add(To(st, "ScaleY", 1, 100));
            }
            // a projection left in place renders the element through a texture: drop it when done
            sb.Completed += (o, a) => { el.Projection = null; el.RenderTransform = null; };
            sb.Begin();
        }

        // ---------- turnstile: page content swings in around its left edge ----------

        public static void TurnstileIn(UIElement root)
        {
            var pp = new PlaneProjection { CenterOfRotationX = 0, RotationY = -80 };
            root.Projection = pp;
            root.Opacity = 0;
            var sb = new Storyboard();
            sb.Children.Add(To(pp, "RotationY", 0, 300));
            var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(150) };
            Storyboard.SetTarget(fade, root);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));
            sb.Children.Add(fade);
            sb.Completed += (o, a) => { root.Projection = null; root.Opacity = 1; };
            sb.Begin();
        }

        // ---------- swivel: transient UI (prompts) drops in around its top edge ----------

        public static void SwivelIn(UIElement el)
        {
            var pp = new PlaneProjection { CenterOfRotationY = 0, RotationX = -60 };
            el.Projection = pp;
            var sb = new Storyboard();
            sb.Children.Add(To(pp, "RotationX", 0, 250));
            sb.Completed += (o, a) => el.Projection = null;
            sb.Begin();
        }

        static DoubleAnimation To(DependencyObject target, string prop, double value, int ms)
        {
            var a = new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
            };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, new PropertyPath(prop));
            return a;
        }
    }
}
