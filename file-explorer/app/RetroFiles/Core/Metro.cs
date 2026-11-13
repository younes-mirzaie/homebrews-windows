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

        // Windows Phone Toolkit values: up to 0.3 rad at the corners, 25 px down on Z,
        // held while pressed, back over 100 ms after a 200 ms delay
        const double MaxAngle = 0.3, Depression = 25;

        static void TiltDown(object sender, MouseButtonEventArgs e)
        {
            var el = (FrameworkElement)sender;
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return;
            var p = e.GetPosition(el);
            double nx = Math.Max(0, Math.Min(1, p.X / el.ActualWidth)), ny = Math.Max(0, Math.Min(1, p.Y / el.ActualHeight));
            double xMag = Math.Abs(nx - 0.5), yMag = Math.Abs(ny - 0.5);
            double mag = xMag + yMag;
            double angle = mag * MaxAngle * 180 / Math.PI;
            el.Projection = new PlaneProjection
            {
                CenterOfRotationX = 0.5, CenterOfRotationY = 0.5,
                RotationY = mag > 0 ? angle * (xMag / mag) * -Math.Sign(nx - 0.5) : 0,
                RotationX = mag > 0 ? angle * (yMag / mag) * Math.Sign(ny - 0.5) : 0,
                GlobalOffsetZ = -Depression
            };
        }

        static void TiltRelease(object sender, EventArgs e)
        {
            var el = (FrameworkElement)sender;
            var pp = el.Projection as PlaneProjection;
            if (pp == null || pp.GlobalOffsetZ == 0) return;
            var sb = new Storyboard { BeginTime = TimeSpan.FromMilliseconds(200) };
            sb.Children.Add(To(pp, "RotationX", 0, 100));
            sb.Children.Add(To(pp, "RotationY", 0, 100));
            sb.Children.Add(To(pp, "GlobalOffsetZ", 0, 100));
            // a projection left in place renders the element through a texture: drop it when done
            sb.Completed += (o, a) => { if (el.Projection == pp) el.Projection = null; };
            sb.Begin();
        }

        // ---------- turnstile: page content swings in around its left edge ----------

        /// <summary>Forward: in from -80 degrees; back: in from 50 degrees. 350 ms, exponential ease out;
        /// opacity snaps (never fades).</summary>
        public static void TurnstileIn(UIElement root, bool back)
        {
            var pp = new PlaneProjection { CenterOfRotationX = 0, RotationY = back ? 50 : -80 };
            root.Projection = pp;
            var sb = new Storyboard();
            sb.Children.Add(To(pp, "RotationY", 0, 350));
            sb.Completed += (o, a) => { if (root.Projection == pp) root.Projection = null; };
            sb.Begin();
        }

        // ---------- swivel: transient UI (prompts) drops in around its top edge ----------

        public static void SwivelIn(UIElement el)
        {
            var pp = new PlaneProjection { CenterOfRotationY = 0, RotationX = -45 };
            el.Projection = pp;
            var sb = new Storyboard();
            sb.Children.Add(To(pp, "RotationX", 0, 350));
            sb.Completed += (o, a) => { if (el.Projection == pp) el.Projection = null; };
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
