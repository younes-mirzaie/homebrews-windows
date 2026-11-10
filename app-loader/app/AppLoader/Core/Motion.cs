// Metro motion without the Windows Phone Toolkit (skills/metro-ui-design, section 6):
//  - Tilt: every tappable list row, button or marked element tilts toward the finger and
//    depresses on touch-down, and springs back on release or when the finger starts scrolling.
//  - Turnstile: pages swing in and out around their left edge on navigation.
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

        const double MaxAngle = 10, Depression = 25, CancelDistance = 12;
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
            // wide rows tilt less sideways, like the system lists
            double sideways = MaxAngle * Math.Min(1, 200 / w);
            target.Projection = new PlaneProjection
            {
                CenterOfRotationX = 0.5, CenterOfRotationY = 0.5,
                RotationY = dx * sideways, RotationX = -dy * MaxAngle, GlobalOffsetZ = -Depression,
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
                var a = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(100) };
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

        /// <summary>Page coming in: forward from -80 degrees, back from 50.</summary>
        public static void In(UIElement root, bool back)
        {
            Run(root, back ? 50 : -80, 0, 0, 1, 300, Ease, null);
        }

        /// <summary>Page leaving: forward to 50 degrees, back to -80; then calls done.</summary>
        public static void Out(UIElement root, bool back, Action done)
        {
            Run(root, 0, back ? -80 : 50, 1, 0, 160, new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 6 }, done);
        }

        static void Run(UIElement root, double from, double to, double opFrom, double opTo, int ms, IEasingFunction ease, Action done)
        {
            var proj = new PlaneProjection { CenterOfRotationX = 0, RotationY = from };
            root.Projection = proj;
            root.Opacity = opFrom;
            var sb = new Storyboard();
            var rot = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
            Storyboard.SetTarget(rot, proj);
            Storyboard.SetTargetProperty(rot, new PropertyPath("RotationY"));
            var op = new DoubleAnimation { From = opFrom, To = opTo, Duration = TimeSpan.FromMilliseconds(ms) };
            Storyboard.SetTarget(op, root);
            Storyboard.SetTargetProperty(op, new PropertyPath("Opacity"));
            sb.Children.Add(rot);
            sb.Children.Add(op);
            sb.Completed += (s, e) => { if (done != null) done(); };
            sb.Begin();
        }
    }
}
