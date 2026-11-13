// Metro motion without the Windows Phone Toolkit (skills/metro-ui-design, section 6):
//  - Tilt: every tappable list row, button or marked element tilts toward the finger and
//    depresses on touch-down, and springs back on release or when the finger starts scrolling.
//  - Turnstile: pages swing in and out around their left edge on navigation (350 ms in, 250 ms out).
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AppLoader.Core
{
    public static class Tilt
    {
        // Set on a page (or any container) to tilt the buttons and list items inside it
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(Tilt), new PropertyMetadata(false, OnIsEnabledChanged));
        // Set on any other element that reacts to a tap (e.g. a row in an ItemsControl)
        public static readonly DependencyProperty IsTargetProperty = DependencyProperty.RegisterAttached(
            "IsTarget", typeof(bool), typeof(Tilt), new PropertyMetadata(false));

        public static bool GetIsEnabled(DependencyObject d) { return (bool)d.GetValue(IsEnabledProperty); }
        public static void SetIsEnabled(DependencyObject d, bool v) { d.SetValue(IsEnabledProperty, v); }
        public static bool GetIsTarget(DependencyObject d) { return (bool)d.GetValue(IsTargetProperty); }
        public static void SetIsTarget(DependencyObject d, bool v) { d.SetValue(IsTargetProperty, v); }

        // 0.3 rad (17 degrees) at the edge, 25 px deep, like the Toolkit TiltEffect
        const double MaxAngle = 17, Depression = 25, CancelDistance = 12;
        static FrameworkElement _current;

        static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var fe = d as FrameworkElement;
            if (fe == null) return;
            fe.ManipulationStarted -= Started;
            if ((bool)e.NewValue) fe.ManipulationStarted += Started;
        }

        static FrameworkElement FindTarget(DependencyObject from, DependencyObject stop)
        {
            for (var d = from; d != null && d != stop; d = VisualTreeHelper.GetParent(d))
            {
                var fe = d as FrameworkElement;
                if (fe == null) continue;
                if (fe is ButtonBase || fe is ListBoxItem || GetIsTarget(fe))
                {
                    var c = fe as Control;
                    return c == null || c.IsEnabled ? fe : null;
                }
            }
            return null;
        }

        static void Started(object sender, ManipulationStartedEventArgs e)
        {
            Release();
            var target = FindTarget(e.OriginalSource as DependencyObject, sender as DependencyObject);
            if (target == null || target.ActualWidth <= 0 || target.ActualHeight <= 0) return;
            Point p;
            try { p = e.ManipulationContainer.TransformToVisual(target).Transform(e.ManipulationOrigin); }
            catch (ArgumentException) { return; }
            double w = target.ActualWidth, h = target.ActualHeight;
            double dx = Math.Max(-1, Math.Min(1, (p.X - w / 2) / (w / 2)));
            double dy = Math.Max(-1, Math.Min(1, (p.Y - h / 2) / (h / 2)));
            target.Projection = new PlaneProjection
            {
                CenterOfRotationX = 0.5, CenterOfRotationY = 0.5,
                RotationY = dx * MaxAngle, RotationX = -dy * MaxAngle, GlobalOffsetZ = -Depression,
            };
            _current = target;
            target.ManipulationDelta += Delta;
            target.ManipulationCompleted += Completed;
        }

        static void Delta(object sender, ManipulationDeltaEventArgs e)
        {
            var t = e.CumulativeManipulation.Translation;
            if (Math.Abs(t.X) > CancelDistance || Math.Abs(t.Y) > CancelDistance) Release();   // it's a scroll
        }

        static void Completed(object sender, ManipulationCompletedEventArgs e) { Release(); }

        static void Release()
        {
            var t = _current;
            _current = null;
            if (t == null) return;
            t.ManipulationDelta -= Delta;
            t.ManipulationCompleted -= Completed;
            var proj = t.Projection as PlaneProjection;
            if (proj == null) return;
            var sb = new Storyboard();
            foreach (var prop in new[] { "RotationX", "RotationY", "GlobalOffsetZ" })
            {
                // springs back over 100 ms after a 200 ms hold
                var a = new DoubleAnimation { To = 0, BeginTime = TimeSpan.FromMilliseconds(200), Duration = TimeSpan.FromMilliseconds(100) };
                Storyboard.SetTarget(a, proj);
                Storyboard.SetTargetProperty(a, new PropertyPath(prop));
                sb.Children.Add(a);
            }
            sb.Begin();
        }
    }

    public static class Turnstile
    {
        static readonly IEasingFunction Ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        static readonly IEasingFunction EaseIn = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 };

        /// <summary>Page coming in: forward from -80 degrees, back from 50, in 350 ms.</summary>
        public static void In(UIElement root, bool back)
        {
            Run(root, back ? 50 : -80, 0, 350, Ease, false, null);
        }

        /// <summary>Page leaving: forward to 50 degrees, back to -80, in 250 ms; then calls done.</summary>
        public static void Out(UIElement root, bool back, Action done)
        {
            Run(root, 0, back ? -80 : 50, 250, EaseIn, true, done);
        }

        // Opacity snaps, never fades: visible for the whole swing, hidden once a page has left
        static void Run(UIElement root, double from, double to, int ms, IEasingFunction ease, bool hideAfter, Action done)
        {
            var proj = new PlaneProjection { CenterOfRotationX = 0, RotationY = from };
            root.Projection = proj;
            root.Opacity = 1;
            var sb = new Storyboard();
            var rot = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
            Storyboard.SetTarget(rot, proj);
            Storyboard.SetTargetProperty(rot, new PropertyPath("RotationY"));
            sb.Children.Add(rot);
            sb.Completed += (s, e) =>
            {
                if (hideAfter) root.Opacity = 0;
                if (done != null) done();
            };
            sb.Begin();
        }
    }
}
