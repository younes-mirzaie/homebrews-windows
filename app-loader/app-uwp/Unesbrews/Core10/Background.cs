// While an install job runs (Windows 10 version of ..\..\app\AppLoader\Core\Background.cs, same calls).
//
// The job itself is a batch that runs as SYSTEM outside the app, so it finishes whatever the app
// does. This only keeps the app around to report on it: the screen stays on while the app is in
// front, and an extended execution session asks Windows to keep the app running when it is
// minimized, so the notification can say how it went. If Windows ends the session anyway, the
// result is shown the next time the app is opened.
using System;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.System.Display;

namespace AppLoader.Core
{
    public static class Background
    {
        static ExtendedExecutionSession _session;
        static DisplayRequest _display;

        /// <summary>Called when a job starts.</summary>
        public static async void Watch()
        {
            try
            {
                if (_display == null) { _display = new DisplayRequest(); _display.RequestActive(); }
            }
            catch (Exception e) { AppLog.Add("couldn't keep the screen on", e.Message); _display = null; }
            if (_session != null) return;
            try
            {
                var s = new ExtendedExecutionSession
                {
                    Reason = ExtendedExecutionReason.Unspecified,
                    Description = "Following an install that is still running",
                };
                s.Revoked += (o, e) => { Release(); };
                var result = await s.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed) _session = s;
                else { s.Dispose(); AppLog.Add("Windows didn't allow running in the background", "The result shows when the app is opened again."); }
            }
            catch (Exception e) { AppLog.Add("background progress isn't available", e.Message); }
        }

        /// <summary>Called when the app is minimized or closed: nothing to do here, the session
        /// started by Watch keeps going until Windows ends it.</summary>
        public static void Nudge() { }

        /// <summary>Called when a job has finished and its result is shown.</summary>
        public static void Stop()
        {
            try { if (_display != null) _display.RequestRelease(); } catch (Exception) { }
            _display = null;
            Release();
        }

        static void Release()
        {
            var s = _session;
            _session = null;
            try { if (s != null) s.Dispose(); } catch (Exception) { }
        }
    }
}
