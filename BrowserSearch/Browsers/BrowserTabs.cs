using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using Wox.Plugin;
using BrowserInfo = Wox.Plugin.Common.DefaultBrowserInfo;

namespace BrowserSearch.Browsers
{
    // Represents a currently open browser tab (as opposed to an entry from the browsing history)
    internal sealed record OpenTab
    {
        public required string Title { get; init; }
        public required string IcoPath { get; init; }
        public required Func<ActionContext, bool> Action { get; init; }

        public Result ToResult()
        {
            return new Result
            {
                Title = Title,
                SubTitle = "Open tab",
                QueryTextDisplay = Title,
                IcoPath = IcoPath,
                Action = Action,
            };
        }
    }

    // Enumerates the currently open tabs of running browsers by walking their UI Automation trees.
    // Chromium browsers expose their tab strip as a ControlType.Tab (named "Tabs") whose children
    // (ControlType.TabItem) are the open tabs. Firefox exposes its strip as a ToolBar named
    // "Browser tabs" containing a ControlType.Tab strip. Selecting a result activates the tab
    // (SelectionItemPattern first, falling back to a click for Chromium).
    internal static class BrowserTabEnumerator
    {
        // Best-effort process names for all supported browsers; only running ones are scanned.
        private static readonly string[] AllProcessNames =
        [
            "Arc", "brave", "CentBrowser", "chrome", "firefox", "librewolf",
            "msedge", "whale", "opera", "thorium", "vivaldi", "waterfox", "wavebox", "zen"
        ];

        private const long CacheTtlMs = 700;
        private static readonly object Sync = new();
        private static List<OpenTab>? _cache;
        private static long _cacheTimestamp;

        public static List<OpenTab> GetOpenTabs(string[] processNames, string icoPath)
        {
            // Tabs change as the user opens/closes them, but re-scanning the whole tab strip on
            // every keystroke is wasteful, so cache the result for a short time.
            long now = Environment.TickCount64;
            lock (Sync)
            {
                if (_cache is not null && now - _cacheTimestamp < CacheTtlMs)
                {
                    return _cache;
                }
            }

            List<OpenTab> tabs = EnumerateOnStaThread(processNames, icoPath);

            lock (Sync)
            {
                _cache = tabs;
                _cacheTimestamp = now;
            }
            return tabs;
        }

        // Returns the open tabs of every running supported browser.
        public static List<OpenTab> GetAllOpenTabs(string icoPath)
        {
            return GetOpenTabs(AllProcessNames, icoPath);
        }

        // UI Automation must run on an STA thread.
        private static List<OpenTab> EnumerateOnStaThread(string[] processNames, string icoPath)
        {
            List<OpenTab> result = [];
            Thread worker = new(() =>
            {
                try
                {
                    result = EnumerateCore(processNames, icoPath);
                }
                catch
                {
                    // leave result empty on failure
                }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            worker.Join(2000);
            return result;
        }

        private static List<OpenTab> EnumerateCore(string[] processNames, string icoPath)
        {
            List<OpenTab> tabs = [];
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string processName in processNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    continue;
                }

                foreach (Process process in processes)
                {
                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        continue;
                    }

                    AutomationElement? root;
                    try
                    {
                        root = AutomationElement.FromHandle(hwnd);
                    }
                    catch
                    {
                        continue;
                    }
                    if (root is null)
                    {
                        continue;
                    }

                    try
                    {
                        CollectWindowTabs(root, hwnd, icoPath, tabs, seenTitles);
                    }
                    catch
                    {
                        // ignore failures for this window and keep going
                    }
                }
            }

            return tabs;
        }

        private static void CollectWindowTabs(AutomationElement root, IntPtr hwnd, string icoPath, List<OpenTab> tabs, HashSet<string> seenTitles)
        {
            var tabCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab);
            var tabItemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

            // Collect candidate tab strips and their tab items
            var candidates = new List<(AutomationElement Strip, AutomationElementCollection Items)>();

            // Chromium exposes its tab strip as a ControlType.Tab
            AutomationElementCollection strips = root.FindAll(TreeScope.Descendants, tabCondition);
            foreach (AutomationElement strip in strips)
            {
                AutomationElementCollection items = strip.FindAll(TreeScope.Children, tabItemCondition);
                if (items.Count > 0)
                {
                    candidates.Add((strip, items));
                }
            }

            // Firefox exposes its tab strip as a ToolBar named "Browser tabs" -> Tab -> TabItems
            AutomationElementCollection toolbars = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
            foreach (AutomationElement toolbar in toolbars)
            {
                if (!string.Equals(toolbar.Current.Name?.Trim(), "Browser tabs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AutomationElement? tabStrip = toolbar.FindFirst(TreeScope.Children, tabCondition);
                if (tabStrip is null)
                {
                    continue;
                }

                AutomationElementCollection items = tabStrip.FindAll(TreeScope.Children, tabItemCondition);
                if (items.Count > 0)
                {
                    candidates.Add((tabStrip, items));
                }
            }

            // Prefer strips named "Tabs" so we don't pick up ARIA tab lists from web pages
            var namedTabs = candidates
                .Where(c => string.Equals(c.Strip.Current.Name?.Trim(), "Tabs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var chosen = namedTabs.Count > 0 ? namedTabs : candidates;

            foreach ((_, AutomationElementCollection items) in chosen)
            {
                foreach (AutomationElement item in items)
                {
                    string title;
                    try
                    {
                        title = item.Current.Name?.Trim() ?? string.Empty;
                    }
                    catch
                    {
                        continue;
                    }
                    if (title.Length == 0 || !seenTitles.Add(title))
                    {
                        continue;
                    }

                    tabs.Add(new OpenTab
                    {
                        Title = title,
                        IcoPath = icoPath,
                        Action = _ => ActivateTab(item, hwnd),
                    });
                }
            }
        }

        private static bool ActivateTab(AutomationElement tab, IntPtr windowHwnd)
        {
            try
            {
                // Bring the browser window to the front (the TOPMOST dance works even when
                // SetForegroundWindow is restricted because we're not the foreground app).
                Native.ShowWindow(windowHwnd, 9 /* SW_RESTORE */);
                Native.SetWindowPos(windowHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
                Native.SetWindowPos(windowHwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE);
                Native.SetForegroundWindow(windowHwnd);

                // SelectionItemPattern.Select() switches tabs on Firefox but is a no-op on Chromium
                // (e.g. Vivaldi), so check whether it worked and fall back to clicking the tab.
                bool selected = false;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? pattern))
                {
                    SelectionItemPattern selection = (SelectionItemPattern)pattern;
                    selection.Select();
                    try
                    {
                        selected = selection.Current.IsSelected;
                    }
                    catch
                    {
                        // element may have gone stale
                    }
                }

                if (!selected)
                {
                    System.Windows.Rect rect = tab.Current.BoundingRectangle;
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        Native.SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
                        Thread.Sleep(40);
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    }
                }

                return true;
            }
            catch
            {
                // The tab may have been closed since the results were built
            }
            return false;
        }

        private static class Native
        {
            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            public static extern bool SetCursorPos(int x, int y);

            [DllImport("user32.dll")]
            public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

            public static readonly IntPtr HWND_TOPMOST = new(-1);
            public static readonly IntPtr HWND_NOTOPMOST = new(-2);
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_SHOWWINDOW = 0x0040;
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        }
    }
}
