using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NHotkey.Wpf;

namespace PowerDimmer
{
    public partial class App : Application
    {
        private IntPtr curFgHwnd;

        // True while the mask windows exist but are hidden: either the user clicked the
        // desktop (UndimOnDesktop) or a WindowFilter rule matched the focused window.
        private bool masksHidden;
        private ISettings settings;
        private List<DimWindow> dimWindows { get; } = new();

        // Stacking / spotlight bookkeeping.
        private IntPtr lastDimTarget;                 // window dimmed around last time
        private IntPtr pendingMaskAnchor;             // where the mask should end up
        private IntPtr maskAnchor;                    // window the mask currently sits under
        private DimWindow? fadeWindow;                // temporary fade-out overlay, if any
        private IntPtr fadeTargetWindow;              // window the running fade belongs to
        private int fadeGeneration;                   // invalidates stale fade callbacks
        private System.Windows.Threading.DispatcherTimer? switchDelayTimer;
        private System.Windows.Threading.DispatcherTimer? spotlightTimer;
        private Win32.POINT lastCursor;
        private bool hasCursorSample;
        private List<WindowShade> shadeWindows { get; } = new();
        private SortedSet<IntPtr> pinnedHandles { get; } = new();

        private Brush maskBrush = Brushes.Black;
        private static Mutex? singleInstanceMutex;

        // 0 = no mask at all, 100 = solid black. The old code stored the inverse
        // ("brightness") which made the slider behave backwards for a mask tool.
        private static double DimToOpacity(int dimLevel) => Math.Clamp(dimLevel, 0, 100) / 100.0;

        static GCHandle GCSafetyHandleForActive;
        static GCHandle GCSafetyHandleForClose;

        public App()
        {
            // A portable tool has no event log entry and no crash reporter: leave a
            // readable stack next to the exe instead of vanishing silently.
            AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
            DispatcherUnhandledException += (_, args) => LogCrash(args.Exception);

            // Settings store is local: it never touches %TEMP% and never calls File.Replace(),
            // so a TEMP folder on another volume (RAM disk, another drive) can no longer kill
            // the process at startup. See Settings.cs for the details.
            settings = new Settings();

            // Language must be resolved before any UI string is built (tray menu, tooltip).
            Loc.Configure(settings.Language);
            maskBrush = CreateMaskBrush(settings.MaskColor);

            settings.DimmingEnabled = settings.ActiveOnLaunch;
            settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(settings.DimLevel))
                {
                    var opacity = DimToOpacity(settings.DimLevel);
                    dimWindows.ForEach(w => w.Opacity = opacity);
                    shadeWindows.ForEach(w => w.Opacity = opacity);
                }
                else if (e.PropertyName == nameof(settings.MaskColor))
                {
                    maskBrush = CreateMaskBrush(settings.MaskColor);
                    dimWindows.ForEach(w => w.Background = maskBrush);
                    shadeWindows.ForEach(w => w.Background = maskBrush);
                }
                else if (e.PropertyName == nameof(settings.FilterMode) ||
                         e.PropertyName == nameof(settings.FilterKeywords))
                {
                    // Rules changed from the tray menu: re-evaluate the window in front.
                    if (settings.DimmingEnabled)
                    {
                        ApplyFocus(curFgHwnd);
                    }
                }
                else if (e.PropertyName == nameof(settings.DimmingEnabled))
                {
                    if (settings.DimmingEnabled)
                    {
                        dimOn(curFgHwnd);
                    }
                    else
                    {
                        dimOff();
                    }
                }
                else if (e.PropertyName == nameof(settings.MultiMonitorDimming))
                {
                    if (settings.DimmingEnabled)
                    {
                        dimOff();
                        dimOn(curFgHwnd);
                    }
                }
                else if (e.PropertyName == nameof(settings.SpotlightEnabled))
                {
                    if (settings.SpotlightEnabled && settings.DimmingEnabled)
                    {
                        StartSpotlightTracking();
                    }
                    else
                    {
                        StopSpotlightTracking(clear: true);
                    }
                }
                else if (e.PropertyName == nameof(settings.SpotlightRadius))
                {
                    // Force a redraw: the position did not change, only the size did.
                    hasCursorSample = false;
                    if (settings.SpotlightEnabled && settings.DimmingEnabled)
                    {
                        UpdateSpotlight();
                    }
                }
            };
        }

        private static void LogCrash(Exception? exception)
        {
            if (exception == null)
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    exception + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static Brush CreateMaskBrush(string? hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) &&
                    new BrushConverter().ConvertFromString(hex) is Brush brush)
                {
                    // Frozen brushes can be shared by every mask window without copying.
                    if (brush.CanFreeze)
                    {
                        brush.Freeze();
                    }

                    return brush;
                }
            }
            catch
            {
            }

            return Brushes.Black;
        }

        private void App_Startup(object sender, StartupEventArgs e)
        {
            // Two instances would stack two masks and fight over the same settings file.
            singleInstanceMutex = new Mutex(true, @"Local\PowerDimmer.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                Shutdown();
                return;
            }

            curFgHwnd = Win32.GetForegroundWindow();

            var iconController = new NotifyIconController(settings);
            iconController.ExitClicked += () => Shutdown();
            iconController.GetTargetWindow = () => curFgHwnd;
            iconController.ReloadRequested += () =>
            {
                // Explicit, on-demand reload. No FileSystemWatcher anywhere in this app.
                var reloaded = settings.Reload();
                Loc.Configure(settings.Language);
                iconController.RefreshMenu();
                if (settings.DimmingEnabled)
                {
                    ApplyFocus(curFgHwnd);
                }

                MessageBox.Show(
                    Loc.T(reloaded ? "Msg.Reloaded" : "Msg.ReloadFailed"),
                    "PowerDimmer",
                    MessageBoxButton.OK,
                    reloaded ? MessageBoxImage.Information : MessageBoxImage.Warning);
            };

            // Debug helper used while developing the tray menu (layout / localisation):
            // PowerDimmer.exe --menu-preview  opens the menu once at a fixed spot.
            if (e.Args.Any(a => a.Equals("--menu-preview", StringComparison.OrdinalIgnoreCase)))
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(900)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var strip = iconController.NotifyIcon.ContextMenuStrip;
                    var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                    strip?.Show(new System.Drawing.Point(area.Left + 60, area.Top + 60));
                };
                timer.Start();
            }
            // Debug helper: PowerDimmer.exe --about  opens the About/version window directly.
            if (e.Args.Any(a => a.Equals("--about", StringComparison.OrdinalIgnoreCase)))
            {
                new AboutDialog((settings as Settings)?.FilePath).ShowDialog();
            }
            // Debug helper: open either timing dialog without going through the tray menu,
            // and apply the result exactly like the menu does.
            if (e.Args.Any(a => a.Equals("--duration-delay", StringComparison.OrdinalIgnoreCase)))
            {
                var dialog = new DurationDialog(Loc.T("Dlg.DelayTitle"), settings.SwitchDelayMs,
                    Settings.SwitchDelayMinMs, Settings.SwitchDelayMaxMs);
                if (dialog.ShowDialog() == true)
                {
                    settings.SwitchDelayMs = dialog.Milliseconds;
                }
            }
            if (e.Args.Any(a => a.Equals("--duration-fade", StringComparison.OrdinalIgnoreCase)))
            {
                var dialog = new DurationDialog(Loc.T("Dlg.FadeTitle"), settings.FadeOutMs,
                    Settings.FadeOutMinMs, Settings.FadeOutMaxMs);
                if (dialog.ShowDialog() == true)
                {
                    settings.FadeOutMs = dialog.Milliseconds;
                }
            }

            Exit += (e, s) =>
            {
                iconController.NotifyIcon.Visible = false;
                iconController.NotifyIcon.Icon.Dispose();
                iconController.NotifyIcon.Dispose();
            };

            HotkeyManager.Current.AddOrReplace("PowerDimmerHotkey", Key.D, ModifierKeys.Windows | ModifierKeys.Control | ModifierKeys.Alt, true, (s, e) =>
            {
                settings.DimmingEnabled = !settings.DimmingEnabled;
            });

            HotkeyManager.Current.AddOrReplace("DimToggleHotkey", Key.D, ModifierKeys.Windows | ModifierKeys.Shift, true, (s, e) =>
            {
                if (!settings.DimmingEnabled)
                {
                    return;
                }

                var hwnd = Win32.GetForegroundWindow();
                if (pinnedHandles.Contains(hwnd))
                {
                    pinnedHandles.Remove(hwnd);
                }
                else
                {
                    pinnedHandles.Add(hwnd);
                }

                // Re-stack right away, otherwise the newly pinned window keeps the
                // position it had until the next focus change.
                UpdateDimming(hwnd);
            });

            HotkeyManager.Current.AddOrReplace("ShadeToggleHotkey", Key.S, ModifierKeys.Windows | ModifierKeys.Alt, true, (s, e) =>
            {
                if (!settings.WindowShadeEnabled)
                {
                    return;
                }

                //unshade window if exists
                var hwnd = Win32.GetForegroundWindow();
                var shadedWindow = shadeWindows.SingleOrDefault(w => w.TargetHandle == hwnd);
                if (shadedWindow != null)
                {
                    shadedWindow.Close();
                    shadeWindows.Remove(shadedWindow);
                }
                else
                {
                    var opacity = DimToOpacity(settings.DimLevel);
                    var shade = new WindowShade(hwnd, maskBrush)
                    {
                        Opacity = opacity
                    };
                    shade.Show();
                    shadeWindows.Add(shade);
                }
            });

            HotkeyManager.Current.AddOrReplace("CustomShadeHotkey", Key.A, ModifierKeys.Windows | ModifierKeys.Alt, true, (s, e) =>
            {
                if (!settings.WindowShadeEnabled)
                {
                    return;
                }

                var hwnd = Win32.GetForegroundWindow();
                var shadedWindow = shadeWindows.SingleOrDefault(w => w.TargetHandle == hwnd);
                if (shadedWindow != null)
                {
                    shadedWindow.Close();
                    shadeWindows.Remove(shadedWindow);
                }
                var opacity = DimToOpacity(settings.DimLevel);
                var customShadeCreatedDelegate = new Win32.CustomShadeCreatedEventDelegate(CreatedCustomShadeEventProc);
                var customShade = new CustomShadeTool(hwnd, customShadeCreatedDelegate);
                customShade.Show();
            });

            if (settings.ActiveOnLaunch)
            {
                dimOn(curFgHwnd);
            }

            var eventDelegate = new Win32.WinEventDelegate(WinEventProc);
            GCSafetyHandleForActive = GCHandle.Alloc(eventDelegate);
            Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
                                  IntPtr.Zero, eventDelegate, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

            var eventClosedDelegate = new Win32.WinEventDelegate(WinCloseEventProc);
            GCSafetyHandleForClose = GCHandle.Alloc(eventClosedDelegate);
            Win32.SetWinEventHook(Win32.SWEH_Events.EVENT_OBJECT_DESTROY, Win32.SWEH_Events.EVENT_OBJECT_DESTROY,
                                  IntPtr.Zero, eventClosedDelegate, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        }

        private void dimOn(IntPtr fgHwnd)//creates a dim window on each screen
        {
            masksHidden = false;
            lastDimTarget = IntPtr.Zero;
            maskAnchor = IntPtr.Zero;
            var opacity = DimToOpacity(settings.DimLevel);
            var screens = settings.MultiMonitorDimming
                ? System.Windows.Forms.Screen.AllScreens
                : new[] { System.Windows.Forms.Screen.PrimaryScreen! };
            foreach (var screen in screens)
            {
                var win = new DimWindow(settings, screen.Bounds, maskBrush)
                {
                    Opacity = opacity
                };
                win.Show();
                dimWindows.Add(win);
            }

            // Honour the exception rules for whatever is in front right now.
            ApplyFocus(fgHwnd);

            if (settings.SpotlightEnabled)
            {
                StartSpotlightTracking();
            }
        }

        private void dimOff()
        {
            masksHidden = false;
            lastDimTarget = IntPtr.Zero;
            CancelMaskMove();
            CancelFade();
            maskAnchor = IntPtr.Zero;
            StopSpotlightTracking(clear: true);
            dimWindows.ForEach(w => w.Close());
            dimWindows.Clear();
            // the following maintains the proper
            // foreground window upon disabling
            Win32.SetForegroundWindow(curFgHwnd);
        }

        /// <summary>
        /// Keeps a soft un-dimmed hole under the mouse pointer. Driven by a 33 ms timer
        /// that only does real work when the cursor actually moved - a low-level mouse
        /// hook would add no benefit here and can freeze the desktop if it stalls.
        /// </summary>
        private void StartSpotlightTracking()
        {
            if (spotlightTimer == null)
            {
                spotlightTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(33)
                };
                spotlightTimer.Tick += (_, _) => UpdateSpotlight();
            }

            hasCursorSample = false;
            spotlightTimer.Start();
            UpdateSpotlight();
        }

        private void StopSpotlightTracking(bool clear)
        {
            spotlightTimer?.Stop();
            hasCursorSample = false;

            if (!clear)
            {
                return;
            }

            foreach (var win in dimWindows)
            {
                win.ClearSpotlight();
            }
        }

        private void UpdateSpotlight()
        {
            if (!settings.SpotlightEnabled || !settings.DimmingEnabled || masksHidden || dimWindows.Count == 0)
            {
                StopSpotlightTracking(clear: true);
                return;
            }

            if (!Win32.GetCursorPos(out var cursor))
            {
                return;
            }

            if (hasCursorSample && cursor.X == lastCursor.X && cursor.Y == lastCursor.Y)
            {
                return;
            }

            lastCursor = cursor;
            hasCursorSample = true;

            foreach (var win in dimWindows)
            {
                var bounds = win.ScreenBounds;
                if (cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                    cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom)
                {
                    win.SetSpotlight(cursor.X, cursor.Y, settings.SpotlightRadius);
                }
                else
                {
                    // Only the monitor under the pointer gets a hole.
                    win.ClearSpotlight();
                }
            }

            // The fade overlay has to follow the cursor too, otherwise the hole in the
            // real mask would be filled in while the old window is still fading out.
            if (fadeWindow != null)
            {
                var bounds = fadeWindow.ScreenBounds;
                if (cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                    cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom)
                {
                    fadeWindow.SetSpotlight(cursor.X, cursor.Y, settings.SpotlightRadius);
                }
                else
                {
                    fadeWindow.ClearSpotlight();
                }
            }
        }

        // https://stackoverflow.com/questions/4372055/detect-active-window-changed-using-c-sharp-without-polling/10280800#10280800
        // https://docs.microsoft.com/en-us/windows/win32/winauto/event-constants
        public void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (settings.DimmingEnabled)
            {
                    if (Win32.IsStandardWindow(hwnd) && Win32.HasNoVisibleOwner(hwnd))
                    {
                        curFgHwnd = hwnd;
                        ApplyFocus(hwnd);
                    }
                    else if (settings.UndimOnDesktop && Win32.IsDesktopWindow(hwnd))
                    {
                        SetMasksHidden(true);
                    }
            }
            if (settings.WindowShadeEnabled)
            {
                var whwnd = Win32.GetForegroundWindow();
                WindowShade windowShade = shadeWindows.SingleOrDefault(s => s.TargetHandle == whwnd);
                if (windowShade != null)
                {
                    UpdateShade(whwnd, windowShade);
                }
            }
        }
        public void WinCloseEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (settings.WindowShadeEnabled)
            {
                WindowShade windowShade = shadeWindows.SingleOrDefault(s => s.TargetHandle == hwnd);
                if(windowShade != null)
                {
                    windowShade.Close();
                    shadeWindows.Remove(windowShade);
                }
            }

            if(settings.DimTaskbar)
            {
                IntPtr taskbarHwnd = Win32.FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    Win32.SetWindowPos(taskbarHwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                }
            }
        }

        /// <summary>
        /// Shows or hides every mask window. Used both by the "click the desktop"
        /// option and by the per-window exception rules.
        /// </summary>
        private void SetMasksHidden(bool hidden)
        {
            if (masksHidden == hidden)
            {
                return;
            }

            masksHidden = hidden;
            if (hidden)
            {
                // A fade overlay on screen would keep one window dark while the mask is
                // supposedly hidden.
                CancelFade();
            }

            foreach (var window in dimWindows)
            {
                if (hidden)
                {
                    window.Hide();
                }
                else
                {
                    window.Show();
                }
            }

            if (hidden)
            {
                StopSpotlightTracking(clear: true);
            }
            else if (settings.SpotlightEnabled)
            {
                StartSpotlightTracking();
            }
        }

        /// <summary>
        /// Applies the mask for a newly focused window, honouring the exception rules.
        /// </summary>
        private void ApplyFocus(IntPtr hwnd)
        {
            if (!settings.DimmingEnabled)
            {
                return;
            }

            if (WindowFilter.ShouldMask(settings.FilterMode, settings.FilterKeywords, hwnd))
            {
                SetMasksHidden(false);
                UpdateDimming(hwnd);
            }
            else
            {
                // A rule matched. Only hide the mask - do NOT touch the z-order here,
                // otherwise the exception window would get pinned above everything else.
                SetMasksHidden(true);
            }
        }

        private void UpdateDimming(IntPtr fgHwnd)
        {
            // Set the incoming/foreground handle as TOP...
            Win32.SetWindowPos(fgHwnd, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

            IntPtr? firstPinned = null;
            foreach (var pinHandle in pinnedHandles)
            {
                if (pinHandle == fgHwnd) continue; // if pinned but also foreground skip
                // Place each pinned window under the foreground
                Win32.SetWindowPos(pinHandle, fgHwnd, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                // store the first pinned handle we didn't skip over
                firstPinned ??= pinHandle;
            }

            var anchor = firstPinned ?? fgHwnd;
            var previous = lastDimTarget;
            var delay = Math.Clamp(settings.SwitchDelayMs, Settings.SwitchDelayMinMs, Settings.SwitchDelayMaxMs);

            // Switch delay: right after the focus moves, park the mask under the window we
            // just left instead of the new one. Everything above the mask stays readable,
            // so both windows stay bright for a moment and then only the new one is left.
            if (delay > 0 && firstPinned == null && previous != IntPtr.Zero && previous != fgHwnd
                && Win32.IsWindow(previous) && !Win32.IsIconic(previous))
            {
                // Do *not* re-stack here. The mask is already sitting under the window we
                // just left; moving it now would end the grace period before it started,
                // and anchoring it to `previous` can drag it anywhere in the z-order
                // (a previously focused, now hidden window is not below the new one).
                CancelFade();
                ScheduleMaskMove(anchor, delay);
                Trace($"linger: fg={Describe(fgHwnd)} prev={Describe(previous)}");
            }
            else
            {
                CancelMaskMove();
                MoveMasksBelow(anchor);
                Trace($"now: fg={Describe(fgHwnd)} delay={delay} prev={Describe(previous)}");
            }

            lastDimTarget = fgHwnd;

            if(settings.DimTaskbar)
            {
                IntPtr taskbarHwnd = Win32.FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    Win32.SetWindowPos(taskbarHwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                }
            }
        }

        /// <summary>Stacks every mask directly below <paramref name="anchor"/>.</summary>
        private void PlaceMasksBelow(IntPtr anchor)
        {
            foreach (var dimWin in dimWindows)
            {
                Win32.SetWindowPos(dimWin.Handle, anchor, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }

            maskAnchor = anchor;
        }

        /// <summary>
        /// Moves the masks below <paramref name="anchor"/>, fading the window we are
        /// leaving instead of cutting to black.
        ///
        /// A SetWindowPos can only be instantaneous, so the fade is a temporary overlay
        /// sitting on top of the old window: it starts fully transparent and animates up
        /// to the current dim level. It is inserted *below* the new foreground window, so
        /// only the window we left darkens - the new one never twitches. Once the overlay
        /// has reached full strength the real mask is moved underneath, which is now a
        /// pixel-for-pixel no-op.
        /// </summary>
        private void MoveMasksBelow(IntPtr anchor)
        {
            var fadeMs = Math.Clamp(settings.FadeOutMs, Settings.FadeOutMinMs, Settings.FadeOutMaxMs);
            var leaving = maskAnchor;

            if (fadeMs <= 0 || leaving == IntPtr.Zero || leaving == anchor ||
                !CanFadeFrom(leaving) || !TryGetBounds(leaving, out var bounds))
            {
                CancelFade();
                PlaceMasksBelow(anchor);
                return;
            }

            // Two switches in quick succession: if the running fade is already darkening
            // exactly the window we are leaving, let it finish and only retarget where the
            // mask lands. Restarting would flash that window back to full brightness.
            if (fadeWindow != null && fadeTargetWindow == leaving)
            {
                pendingMaskAnchor = anchor;
                Trace($"fade: retarget -> {Describe(anchor)}");
                return;
            }

            // Otherwise drop the half-faded overlay and start over from whatever is
            // actually bright right now.
            CancelFade();

            var overlay = new DimWindow(settings, bounds, maskBrush)
            {
                CornerRadiusDip = GetWindowCornerRadiusDip(leaving)
            };
            overlay.SetSurfaceOpacity(0);
            overlay.Show();

            Win32.SetWindowPos(overlay.Handle, anchor, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

            if (settings.SpotlightEnabled && Win32.GetCursorPos(out var cursor) &&
                bounds.Contains(cursor.X, cursor.Y))
            {
                overlay.SetSpotlight(cursor.X, cursor.Y, settings.SpotlightRadius);
            }

            fadeWindow = overlay;
            fadeTargetWindow = leaving;
            pendingMaskAnchor = anchor;
            var generation = ++fadeGeneration;

            overlay.FadeSurface(DimToOpacity(settings.DimLevel), TimeSpan.FromMilliseconds(fadeMs), () =>
            {
                if (generation != fadeGeneration)
                {
                    return; // superseded by a newer switch
                }

                if (!settings.DimmingEnabled || masksHidden)
                {
                    CancelFade();
                    return;
                }

                // The mask is still sitting *under* the window we are leaving, so the
                // overlay is the only thing keeping it dark. Both changes (mask under the
                // new window, overlay gone) have to land together - either one alone
                // produces a visible frame: dropping the overlay first is a bright flash,
                // moving the mask first is a double-dark layer.
                var target = Win32.IsWindow(pendingMaskAnchor) ? pendingMaskAnchor : curFgHwnd;
                if (!HandOverToMask(target, overlay))
                {
                    // Batched move unavailable: fall back to the two-step version. It costs
                    // one composed frame in which both layers cover the window.
                    PlaceMasksBelow(target);
                    overlay.SetSurfaceOpacity(0);
                }

                Trace($"fade: done -> {Describe(target)}");
                DiscardOverlayAfterFrame(overlay);
            });

            Trace($"fade: {fadeMs}ms from {Describe(leaving)} to {Describe(anchor)}");
        }

        private static bool CanFadeFrom(IntPtr hwnd) =>
            Win32.IsWindow(hwnd) && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd);

        /// <summary>
        /// Corner radius, in DIPs, that the fade overlay has to be clipped to so it covers
        /// no more than the window itself. Returns 0 for square corners.
        /// </summary>
        private static double GetWindowCornerRadiusDip(IntPtr hwnd)
        {
            // A maximised window sits flush with the screen edges and DWM leaves its
            // corners square; clipping there would bite four notches out of it.
            if (Win32.IsZoomed(hwnd))
            {
                return 0;
            }

            if (!Win32.TryGetCornerPreference(hwnd, out var preference) ||
                preference == Win32.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND)
            {
                return 0;
            }

            // Same reason for a window that fills a whole monitor without being maximised
            // (video players, games): DWM does not round those, so clipping would leave
            // four little holes showing the dimmed desktop through a bright picture.
            if (TryGetBounds(hwnd, out var bounds) &&
                System.Windows.Forms.Screen.AllScreens.Any(screen =>
                    Math.Abs(bounds.Left - screen.Bounds.Left) <= 2 &&
                    Math.Abs(bounds.Top - screen.Bounds.Top) <= 2 &&
                    Math.Abs(bounds.Right - screen.Bounds.Right) <= 2 &&
                    Math.Abs(bounds.Bottom - screen.Bounds.Bottom) <= 2))
            {
                return 0;
            }

            // Measured 19 px at 175% scaling, i.e. ~10 DIP, which is what Windows 11 uses
            // for a normal top-level window. The "small" variant is the flyout radius.
            return preference == Win32.DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUNDSMALL ? 4.0 : 10.0;
        }

        private static bool TryGetBounds(IntPtr hwnd, out System.Drawing.Rectangle bounds)
        {
            bounds = default;

            // Visible pixels only. GetWindowRect would add the invisible resize
            // border, and the fade overlay - which sits *under* the window while it
            // darkens it - would then leak that margin as a dark frame around it.
            if (!Win32.GetVisibleWindowRect(hwnd, out var rect))
            {
                return false;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            bounds = new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
            return true;
        }

        /// <summary>
        /// Swaps the fade overlay for the real mask in a single window-manager batch.
        ///
        /// At the end of a fade both layers cover the window pixel for pixel: alpha a over
        /// alpha a is 1-(1-a)^2, so the mask looks one step darker than the level the user
        /// picked and pops back the instant the overlay closes. Doing this as two separate
        /// SetWindowPos / Close calls always leaves at least one composed frame in that
        /// state, because DWM does not pick both changes up in the same frame.
        ///
        /// BeginDeferWindowPos queues both operations and applies them together: the masks
        /// land under the target while the overlay is moved far off-screen, in one step. No
        /// frame is ever composed with both layers visible, and none with neither.
        /// </summary>
        private bool HandOverToMask(IntPtr anchor, DimWindow overlay)
        {
            if (overlay.Handle == IntPtr.Zero || dimWindows.Count == 0)
            {
                return false;
            }

            var batch = Win32.BeginDeferWindowPos(dimWindows.Count + 1);
            if (batch == IntPtr.Zero)
            {
                return false;
            }

            foreach (var dimWin in dimWindows)
            {
                var next = Win32.DeferWindowPos(batch, dimWin.Handle, anchor, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                if (next == IntPtr.Zero)
                {
                    Win32.EndDeferWindowPos(batch);
                    return false;
                }

                batch = next;
            }

            // Far outside any virtual desktop; z-order deliberately left alone.
            var moved = Win32.DeferWindowPos(batch, overlay.Handle, IntPtr.Zero, -32000, -32000, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
            if (moved == IntPtr.Zero)
            {
                Win32.EndDeferWindowPos(batch);
                return false;
            }

            if (!Win32.EndDeferWindowPos(moved))
            {
                return false;
            }

            maskAnchor = anchor;
            return true;
        }

        /// <summary>Closes the fade overlay, if one is running, and voids its callback.</summary>
        private void CancelFade()
        {
            var window = fadeWindow;
            if (window != null)
            {
                CloseOverlay(window);
                return;
            }

            fadeGeneration++;
            fadeTargetWindow = IntPtr.Zero;
        }

        /// <summary>
        /// Closes <paramref name="overlay"/> - but only if it is still the current one, so
        /// a delayed cleanup can never take down a fade that started in the meantime.
        /// </summary>
        private void CloseOverlay(DimWindow overlay)
        {
            if (!ReferenceEquals(fadeWindow, overlay))
            {
                return;
            }

            fadeWindow = null;
            fadeTargetWindow = IntPtr.Zero;
            fadeGeneration++;

            try
            {
                overlay.Close();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Drops the overlay a few frames after the mask has been moved. Closing it in the
        /// same beat as the SetWindowPos lets DWM compose one frame in which neither layer
        /// covers the window - visible as a single bright flicker.
        /// </summary>
        private void DiscardOverlayAfterFrame(DimWindow overlay)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CloseOverlay(overlay);
            };

            timer.Start();
        }

        private void ScheduleMaskMove(IntPtr anchor, int delayMs)
        {
            if (switchDelayTimer == null)
            {
                switchDelayTimer = new System.Windows.Threading.DispatcherTimer();
                switchDelayTimer.Tick += (_, _) =>
                {
                    switchDelayTimer!.Stop();
                    if (!settings.DimmingEnabled || masksHidden)
                    {
                        Trace("tick: skipped (disabled/hidden)");
                        return;
                    }

                    // The window we were heading to may have been closed in the meantime.
                    var anchor = Win32.IsWindow(pendingMaskAnchor) ? pendingMaskAnchor : curFgHwnd;
                    MoveMasksBelow(anchor);
                    Trace($"tick: mask -> {Describe(anchor)}");
                };
            }

            pendingMaskAnchor = anchor;
            switchDelayTimer.Stop();
            switchDelayTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            switchDelayTimer.Start();
            Trace($"schedule: {delayMs}ms -> {Describe(anchor)}");
        }

        private void CancelMaskMove() => switchDelayTimer?.Stop();

        // Lightweight opt-in trace (--trace): a portable tool has no console, and
        // z-order problems are impossible to reason about without a log.
        private static readonly bool TraceEnabled =
            Environment.GetCommandLineArgs().Any(a => a.Equals("--trace", StringComparison.OrdinalIgnoreCase));

        private static void Trace(string message)
        {
            if (!TraceEnabled)
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "trace.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static string Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return "0x0(none)";
            }

            return "0x" + hwnd.ToString("X") + ":" + Win32.TryGetWindowTitle(hwnd);
        }

        private void UpdateShade(IntPtr shadedHwnd, WindowShade windowShade)
        {
            // Set the window shade handle as TOP...
            Win32.SetWindowPos(windowShade.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            //delay making it not top because the window seems to end up on top immediatly afterwards
            Task.Run(() => 
            {
                System.Threading.Thread.Sleep(500);
                Win32.SetWindowPos(windowShade.Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE); 
            });
        }

        public void CreatedCustomShadeEventProc(Window shadeTool, IntPtr hwnd, double left, double top, double width, double height)
        {
            var shadedWindow = shadeWindows.SingleOrDefault(w => w.TargetHandle == hwnd);
            if (shadedWindow != null)
            {
                shadedWindow.Close();
                shadeWindows.Remove(shadedWindow);
            }
            else
            {
                var opacity = DimToOpacity(settings.DimLevel);
                var shade = new WindowShade(hwnd, left, top, width, height, maskBrush)
                {
                    Opacity = opacity
                };
                shade.Show();
                shadeWindows.Add(shade);
            }
            shadeTool.Close();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // A slider drag followed by "Exit" may still be sitting in the debounce
            // window - make sure it lands on disk before the process goes away.
            (settings as Settings)?.Flush();
            base.OnExit(e);
            try
            {
                singleInstanceMutex?.ReleaseMutex();
                singleInstanceMutex?.Dispose();
            }
            catch
            {
            }
            if(GCSafetyHandleForActive.IsAllocated)
            {
                GCSafetyHandleForActive.Free();
            }
            if(GCSafetyHandleForClose.IsAllocated)
            {
                GCSafetyHandleForClose.Free();
            }
        }
    }

    public interface ISettings : INotifyPropertyChanged
    {
        bool ActiveOnLaunch { get; set; }

        bool DimmingEnabled { get; set; }

        bool DimTaskbar { get; set; }

        bool UndimOnDesktop { get; set; }

        bool MultiMonitorDimming { get; set; }

        int DimLevel { get; set; }

        bool WindowShadeEnabled { get; set; }

        /// <summary>Mask colour as #RRGGBB.</summary>
        string MaskColor { get; set; }

        /// <summary>"auto" (follow Windows), "zh" or "en".</summary>
        string Language { get; set; }

        /// <summary>"off", "exclude" or "only" - see WindowFilter.</summary>
        string FilterMode { get; set; }

        /// <summary>Grace period after a focus change, in milliseconds (0 = off).</summary>
        int SwitchDelayMs { get; set; }

        /// <summary>How long the window we left takes to darken, in milliseconds (0 = snap).</summary>
        int FadeOutMs { get; set; }

        /// <summary>Keep a soft hole in the mask around the mouse pointer.</summary>
        bool SpotlightEnabled { get; set; }

        /// <summary>Spotlight hole radius in DIPs.</summary>
        double SpotlightRadius { get; set; }

        /// <summary>Substrings matched against process name, exe path and window title.</summary>
        List<string> FilterKeywords { get; set; }

        /// <summary>
        /// Re-reads settings.json on demand. Returns false when the file cannot be parsed.
        /// This is a menu action, not a background file watcher.
        /// </summary>
        bool Reload();
    }
}
