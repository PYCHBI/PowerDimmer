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
        }

        private void dimOff()
        {
            masksHidden = false;
            dimWindows.ForEach(w => w.Close());
            dimWindows.Clear();
            // the following maintains the proper
            // foreground window upon disabling
            Win32.SetForegroundWindow(curFgHwnd);
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
            foreach (var dimWin in dimWindows)
            {
                // Finally place the dimmer window behind the first pinned or foreground
                Win32.SetWindowPos(dimWin.Handle, firstPinned ?? fgHwnd, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
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

        /// <summary>Substrings matched against process name, exe path and window title.</summary>
        List<string> FilterKeywords { get; set; }

        /// <summary>
        /// Re-reads settings.json on demand. Returns false when the file cannot be parsed.
        /// This is a menu action, not a background file watcher.
        /// </summary>
        bool Reload();
    }
}
