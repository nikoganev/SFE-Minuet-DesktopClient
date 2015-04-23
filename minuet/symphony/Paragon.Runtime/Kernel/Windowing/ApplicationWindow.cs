﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Permissions;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.Windows.Shell;
using Paragon.Plugins;
using Paragon.Runtime.Annotations;
using Paragon.Runtime.Desktop;
using Paragon.Runtime.Kernel.HotKeys;
using Paragon.Runtime.Win32;
using Paragon.Runtime.WinForms;
using Paragon.Runtime.WPF;
using Xilium.CefGlue;
using Button = System.Windows.Controls.Button;
using DownloadProgressEventArgs = Paragon.Plugins.DownloadProgressEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Timer = System.Threading.Timer;

namespace Paragon.Runtime.Kernel.Windowing
{
    [JavaScriptPlugin(CallbackThread = CallbackThread.Main)]
    public class ApplicationWindow : ParagonWindow, IApplicationWindowEx, IDisposable
    {
        private static readonly ILogger Logger = ParagonLogManager.GetLogger();
        private static int _idSeed;
        private readonly Semaphore _timerSem = new Semaphore(1, 1);
        private ICefWebBrowser _browser;
        private HotKeyService _hotKeyService;
        private string _id;
        private bool _isClosed;
        private bool _isClosing;
        private bool _minimizeOnClose;
        private bool _firstPageLoaded = false;
        private CreateWindowOptions _options;
        private Timer _timer;
        private string _title;
        private DeveloperToolsWindow _tools;
        private IApplicationWindowManagerEx _windowManager;

        public ApplicationWindow()
        {
            Loaded += OnLoaded;
            var nativeWindow = new NativeApplicationWindow(this);
            nativeWindow.AddHook(WndProc);
        }

        public event EventHandler LoadComplete;
        public event EventHandler<DownloadProgressEventArgs> DownloadProgress;

        /// <summary>
        /// Fired when the window is resized.
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onBoundsChanged")]
        public event JavaScriptPluginCallback WindowBoundsChanged;

        /// <summary>
        /// Fired when the window is closed.
        /// Note, this should be listened to from a window other than the window being closed, for example from the background
        /// page. This is because the window being closed will be in the process of being torn down when the event is fired, which means
        /// not all APIs in the window's script context will be functional.
        /// No parameters required.
        /// </summary>
        [JavaScriptDispose]
        [JavaScriptPluginMember(Name = "onClosed")]
        public event JavaScriptPluginCallback WindowClosed;

        /// <summary>
        /// Fired when the window is fullscreened.
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onFullScreened")]
        public event JavaScriptPluginCallback WindowFullScreened;

        /// <summary>
        /// Fired when the window is maximized.
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onMaximized")]
        public event JavaScriptPluginCallback WindowMaximized;

        /// <summary>
        /// Fired when the window is minimized.
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onMinimized")]
        public event JavaScriptPluginCallback WindowMinimized;

        /// <summary>
        /// Fired when the window is restored from being minimized or maximized.
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onRestored")]
        public event JavaScriptPluginCallback WindowRestored;

        /// <summary>
        /// Fired when the browser completes loading a new page.
        /// </summary>
        [JavaScriptPluginMember(Name = "onPageLoaded")]
        public event JavaScriptPluginCallback PageLoaded;

        /// <summary>
        /// Clear attention to the window.
        /// </summary>
        [JavaScriptPluginMember]
        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        public void ClearAttention()
        {
            Flash(true);
        }

        /// <summary>
        /// Close the window.
        /// </summary>
        [JavaScriptPluginMember(Name = "close")]
        public void CloseWindow()
        {
            _minimizeOnClose = false;
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(Close));
        }

        public bool ContainsBrowser(int browserId)
        {
            return _browser != null && _browser.Identifier == browserId;
        }

        /// <summary>
        /// Draw attention to the window.
        /// </summary>
        [JavaScriptPluginMember]
        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        public void DrawAttention(bool autoclear)
        {
            Flash(false, autoclear);
        }

        /// <summary>
        /// Focus the window.
        /// </summary>
        [JavaScriptPluginMember(Name = "focus")]
        public void FocusWindow()
        {
            Dispatcher.Invoke(new Action(() => Focus()));
        }

        /// <summary>
        /// Fullscreens the window.
        /// The user will be able to restore the window by pressing ESC.
        /// An application can prevent the fullscreen state to be left when ESC is pressed by requesting the
        /// overrideEscFullscreen
        /// permission and canceling the event by calling .preventDefault(), like this:
        /// window.onKeyDown = function(e) { if (e.keyCode == 27 /* ESC */) { e.preventDefault(); } };
        /// </summary>
        [JavaScriptPluginMember(Name = "fullscreen")]
        public void FullScreenWindow()
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                if (IsFullScreenEnabled)
                {
                    return;
                }

                IsFullScreenEnabled = true;
                WindowFullScreened.Raise();
            }));
        }

        /// <summary>
        /// The id to uniquely identify the window instance within the application.
        /// </summary>
        /// <returns></returns>
        [JavaScriptPluginMember]
        public string GetId()
        {
            // Unless specified in the CreateWindowOptions object, we generate a window ID based on the
            // application instance ID. Everytime the app creates a window, we increment the seed to
            // provide a unique ID. It's by no means perfect, but it's good enough for now.
            if (string.IsNullOrEmpty(_id))
            {
                if (_options != null && !string.IsNullOrEmpty(_options.Id))
                {
                    _id = _options.Id;
                }
                else
                {
                    _id = _windowManager.Application.Metadata.InstanceId + "_win" + (++_idSeed);
                }
            }

            return _id;
        }

        /// <summary>
        /// The id to uniquely identify the window instance within the application.
        /// </summary>
        /// <returns></returns>
        [JavaScriptPluginMember]
        public string GetTitle()
        {
            return Dispatcher.Invoke(new Func<string>(() => Title)) as string;
        }

        /// <summary>
        /// The position, size and constraints of the window's content, which does not include window decorations.
        /// FOR REFERENCE: in Chrome Apps, the returned type is Bounds, which includes various functions for setting the size
        /// and position.
        /// </summary>
        /// <returns></returns>
        [JavaScriptPluginMember]
        public BoundsSpecification GetInnerBounds()
        {
            return Dispatcher.Invoke(new Func<BoundsSpecification>(() =>
            {
                var r = (FrameworkElement)Content;
                return new BoundsSpecification
                {
                    Left = 0,
                    Top = 0,
                    Height = (int)r.ActualHeight,
                    Width = (int)r.ActualWidth,
                    MaxHeight = (int)(MaxHeight - (Height - r.ActualHeight)),
                    MaxWidth = (int)(MaxWidth - (Width - r.ActualWidth)),
                    MinHeight = (int)(MinHeight - (Height - r.Height)),
                    MinWidth = (int)(MinWidth - (Width - r.Width)),
                };
            })) as BoundsSpecification;
        }

        /// <summary>
        /// The position, size and constraints of the window, which includes window decorations, such as the title bar and frame.
        /// FOR REFERENCE: in Chrome Apps, the returned type is Bounds, which includes various functions for setting the size and position.
        /// </summary>
        /// <returns></returns>
        [JavaScriptPluginMember]
        public BoundsSpecification GetOuterBounds()
        {
            return Dispatcher.Invoke(new Func<BoundsSpecification>(GetBounds)) as BoundsSpecification;
        }

        /// <summary>
        /// Hide the window. Does nothing if the window is already hidden.
        /// </summary>
        [JavaScriptPluginMember(Name = "hide")]
        public void HideWindow()
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(Hide));
        }

        public void Initialize(IApplicationWindowManagerEx windowManager, ICefWebBrowser browser,
            string startUrl, string title, CreateWindowOptions options)
        {
            _windowManager = windowManager;
            _browser = browser;
            _title = title;
            _options = options;

            AttachToBrowser();
            var control = browser as FrameworkElement;

            if (control != null)
            {
                control.Width = double.NaN;
                control.Height = double.NaN;
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                control.VerticalAlignment = VerticalAlignment.Stretch;
                control.PreviewKeyDown += OnBrowserPreviewKeyDown;
                browser.LoadEnd += OnPageLoaded;
            }

            ApplyWindowOptions();
        }

        /// <summary>
        /// Maximize the window.
        /// </summary>
        [JavaScriptPluginMember]
        public void Maximize()
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                if (WindowState != WindowState.Maximized)
                {
                    IsFullScreenEnabled = false;
                    WindowState = WindowState.Maximized;
                }
            }));
        }

        /// <summary>
        /// Minimize the window.
        /// </summary>
        [JavaScriptPluginMember]
        public void Minimize()
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                if (WindowState != WindowState.Minimized)
                {
                    IsFullScreenEnabled = false;
                    WindowState = WindowState.Minimized;
                }
            }));
        }

        /// <summary>
        /// Move the window to the position (|left|, |top|).
        /// </summary>
        /// <param name="left"></param>
        /// <param name="top"></param>
        [JavaScriptPluginMember]
        public void MoveTo(int left, int top)
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                Left = left;
                Top = top;
            }));
        }

        /// <summary>
        /// Resize the window to |width|x|height| pixels in size.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        [JavaScriptPluginMember]
        public void ResizeTo(int width, int height)
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                Width = width;
                Height = height;
            }));
        }

        /// <summary>
        /// Restore the window, exiting a maximized, minimized, or fullscreen state.
        /// </summary>
        [JavaScriptPluginMember]
        public void Restore()
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                if (IsFullScreenEnabled)
                {
                    IsFullScreenEnabled = false;
                }
                else
                {
                    WindowState = WindowState.Normal;
                }
            }));
        }

        [JavaScriptPluginMember]
        public void SetOuterBounds(BoundsSpecification bounds)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                int top = (int)bounds.Top,
                    left = (int)bounds.Left,
                    width = bounds.Width > 0 ? (int)bounds.Width : (int)Width,
                    height = bounds.Height > 0 ? (int)bounds.Height : (int)Height;

                Win32Api.SetWindowPosition(Handle, IntPtr.Zero, left, top, width, height, SWP.NOZORDER);

                if (bounds.MinHeight > 0)
                {
                    MinHeight = bounds.MinHeight;
                }
                if (bounds.MinWidth > 0)
                {
                    MinWidth = bounds.MinWidth;
                }
                if (bounds.MaxHeight > 0)
                {
                    MaxHeight = bounds.MaxHeight;
                }
                if (bounds.MaxWidth > 0)
                {
                    MaxWidth = bounds.MaxWidth;
                }
            }));
        }

        /// <summary>
        /// Show the window. Does nothing if the window is already visible. Focus the window if |focused| is set to true or omitted.
        /// </summary>
        [JavaScriptPluginMember(Name = "show")]
        public void ShowWindow(bool focused = true)
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                if (!IsVisible)
                {
                    Show();
                }
                else if (!focused && !Topmost) // Window already visible
                {
                    Win32Api.ShowWindow(Handle, SW.SHOWNOACTIVATE);
                    Win32Api.ActivateWindowNoFocus(Handle);
                }

                if (focused)
                {
                    Focus();
                }
            }));
        }

        [JavaScriptPluginMember(Name = "refresh")]
        public void RefreshWindow(bool ignoreCache = true)
        {
            Dispatcher.Invoke(new Action(() => _browser.Reload(ignoreCache)));
        }

        [JavaScriptPluginMember(Name = "executeJavaScript")]
        public void ExecuteJavaScript(string script)
        {
            Dispatcher.Invoke(new Action(() => _browser.ExecuteJavaScript(script)));
        }

        public IntPtr Handle
        {
            get { return new WindowInteropHelper(this).EnsureHandle(); }
        }

        public ICefWebBrowser Browser
        {
            get { return _browser; }
        }

        public void ShowDeveloperTools(Point element)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowDeveloperTools(element)), null);
                return;
            }
            if (_tools == null)
            {
                var c = _browser.GetDeveloperToolsControl(new CefPoint((int)element.X, (int)element.Y), null, null);
                _tools = new DeveloperToolsWindow();
                _tools.Initialize(c, Title, (_options == null || _options.Frame == null || _options.Frame.Type == FrameType.None) ? FrameType.Paragon : _options.Frame.Type);
                _tools.Closed += ToolsClosed;
                _tools.Owner = this;
                _tools.Show();
            }
            else
            {
                _tools.Activate();
            }
        }

        /// <summary>
        /// Fired when a property is change.
        /// property name and new value is required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onPropertyChanged"), UsedImplicitly]
        public event JavaScriptPluginCallback PropertyChanged;

        /// <summary>
        /// Fired when the window receives a registered hotkey.
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onHotKeyPressed"), UsedImplicitly]
        public event JavaScriptPluginCallback HotKeyPressed;

        /// <summary>
        /// Fired when the window system menu item was clicked
        /// No parameters required.
        /// </summary>
        [JavaScriptPluginMember(Name = "onSystemMenuItemClicked"), UsedImplicitly]
        public event JavaScriptPluginCallback SystemMenuItemClicked;

        [JavaScriptPluginMember, UsedImplicitly]
        public void Dock(string edge, bool autoHide)
        {
            // TODO: Implement this.
        }

        /// <summary>
        /// If you think this function is a bit messy, you would be right. What it is doing
        /// is detecting when the window receives focus and then deciding whether or not to 
        /// pass that focus onto the CEF browser control.
        /// 
        /// Whilst we exist in a world which is a mix of WPF and Winforms, this will remain 
        /// complex. Hopefully when the Winforms window is removed from the equation, this
        /// function will be a lot simpler.
        /// 
        /// When the user clicks on a button on the title bar, we receive two messages;
        /// one for the window receiving focus and one for the button receiving focus. If the
        /// button receives focus, we want to execute the associated command (e.g. close or
        /// minimize), but if something else on the window was clicked, we want to pass the
        /// focus onto the CEF control. The problem is that we receive the window focus message
        /// before the button focus event, so we don't know if a button focus event is going to
        /// follow on or not. So to get round this, we start a timer and wait. If no button
        /// focus message arrives, we pass focus to the CEF control. If one does arrive, we
        /// prevent the focus from being passed on, so the button command is executed.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            if (_browser != null)
            {
                // If the focus is not going to min/max/close buttons, pass the focus to the application.
                if (!(e.NewFocus is Button) && _timer == null)
                {
                    _timer = new Timer(obj => Dispatcher.Invoke( new Action(() =>
                    {
                        if (_timerSem.WaitOne(0) && _browser != null)
                        {
                            _browser.FocusBrowser();
                        }
                        _timerSem.Release();
                        _timer = null;
                    })),
                    null, 50, Timeout.Infinite);
                }
                else if (_timer != null)
                {
                    _timerSem.WaitOne(0);
                }
            }
            base.OnGotKeyboardFocus(e);
        }
        
        /// <summary>
        /// The JavaScript 'window' object for the created child.
        /// </summary>
        [UsedImplicitly]
        public object GetContentWindow()
        {
            // Should be a property, needs plugin model to support dynamic plugins returning the global cef object from the CefV8Context that created the window.
            throw new NotImplementedException();
        }

        /// <summary>
        /// Is the window always on top?
        /// </summary>
        [JavaScriptPluginMember, UsedImplicitly]
        public bool IsAlwaysOnTop()
        {
            // Dispatcher.Invoke(Func<bool>) uses a path that works out whether it is already on the right thread or not
            var isAlwaysOnTop = false;
            Dispatcher.Invoke(new Action(() => isAlwaysOnTop = Topmost));
            return isAlwaysOnTop;
        }

        /// <summary>
        /// Is the window fullscreen?
        /// </summary>
        [JavaScriptPluginMember, UsedImplicitly]
        public bool IsFullScreen()
        {
            // Dispatcher.Invoke(Func<bool>) uses a path that works out whether it is already on the right thread or not
            var isFullScreen = false;
            Dispatcher.Invoke(new Action(() => isFullScreen = IsFullScreenEnabled));
            return isFullScreen;
        }

        /// <summary>
        /// Is the window maximized?
        /// </summary>
        [JavaScriptPluginMember, UsedImplicitly]
        public bool IsMaximized()
        {
            // Dispatcher.Invoke(Func<bool>) uses a path that works out whether it is already on the right thread or not
            var isMaximized = false;
            Dispatcher.Invoke(new Action(() => isMaximized = WindowState == WindowState.Maximized));
            return isMaximized;
        }

        /// <summary>
        /// Is the window minimized?
        /// </summary>
        [JavaScriptPluginMember, UsedImplicitly]
        public bool IsMinimized()
        {
            // Dispatcher.Invoke(Func<bool>) uses a path that works out whether it is already on the right thread or not
            var isMinimized = false;
            Dispatcher.Invoke(new Action(() => isMinimized = WindowState == WindowState.Minimized));
            return isMinimized;
        }

        /// <summary>
        /// Set whether the window should stay above most other windows. Requires the "alwaysOnTopWindows" permission.
        /// </summary>
        [JavaScriptPluginMember, UsedImplicitly]
        public void SetAlwaysOnTop(bool alwaysOnTop)
        {
            // Dispatcher.Invoke(Action) uses a path that works out whether it is already on the right thread or not
            Dispatcher.Invoke(new Action(() =>
            {
                Topmost = alwaysOnTop;
                _browser.SetTopMost(alwaysOnTop);
            }));
        }

        /// <summary>
        /// Set whether window should minimize when the close button is pressed
        /// </summary>
        [JavaScriptPluginMember, UsedImplicitly]
        public void SetMinimizeOnClose(bool minimizeOnClose)
        {
            _minimizeOnClose = minimizeOnClose;
        }

        [JavaScriptPluginMember(Name = "setHotKeys"), UsedImplicitly]
        [SecurityPermission(SecurityAction.LinkDemand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        public void SetHotKeys(string name, string modifiers, string keys)
        {
            _hotKeyService.Remove(name);
            var modifier = (ModifierKeys)Enum.Parse(typeof(ModifierKeys), modifiers);
            var key = (Keys)Enum.Parse(typeof(Keys), keys);
            _hotKeyService.Add(name, modifier, key);
        }

        [JavaScriptPluginMember(Name = "setHotKeysEnabled"), UsedImplicitly]
        public void SetHotKeysEnabled(bool enabled)
        {
            _hotKeyService.IsEnabled = enabled;
        }

        [JavaScriptPluginMember, UsedImplicitly]
        public void StartDrag()
        {
            Dispatcher.Invoke(new Action(StartDragMove));
        }

        protected override void OnClosed(EventArgs e)
        {
            DoCleanup();
            base.OnClosed(e);
            WindowClosed.Raise(() => new object[] { this });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (!_isClosing)
            {
                _isClosing = true;
                if (_browser != null)
                {
                    if (_tools != null)
                    {
                        _tools.Close();
                    }

                    _browser.Close();
                }
            }

            if (!_isClosed)
            {
                e.Cancel = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (IsFullScreenEnabled && e.Key == Key.Escape)
            {
                Restore();
            }
            base.OnKeyDown(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            HandleKeyPress(e.Key);
        }

        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (_options != null && _options.Frame != null && _options.Frame.SystemMenu != null)
            {
                var interceptor = new SystemMenuInterceptor(_options.Frame.SystemMenu);
                interceptor.ApplyTo(this);
                interceptor.ItemClicked += (sender, args) => SystemMenuItemClicked.Raise(() => new object[] { args.Id, args.IsChecked });
            }

            _hotKeyService = new HotKeyService(this);
            _hotKeyService.HotKeyPressed += (sender, args) => HotKeyPressed.Raise(() => new object[] { args.Name });
            if (_options != null)
            {
                _hotKeyService.IsEnabled = _options.HotKeysEnabled;
            }

            if (_windowManager != null)
            {
                var hwnd = Handle;
                var appId = _windowManager.Application.Metadata.Id;
                var instanceId = _windowManager.Application.Metadata.InstanceId;
                ParagonDesktop.RegisterAppWindow(hwnd, appId, instanceId);

                if (_options != null && _options.AutoSaveLocation)
                {
                    var autoSaveWindowPositionBehavior = new AutoSaveWindowPositionBehavior();
                    autoSaveWindowPositionBehavior.Attach(this);
                }
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Note that _browser is not set in the case of a dev tools window.
            switch (WindowState)
            {
                case WindowState.Maximized:
                    WindowMaximized.Raise();

                    if (_browser != null)
                    {
                        _browser.FocusBrowser();
                    }
                    break;

                case WindowState.Minimized:
                    WindowMinimized.Raise();
                    break;

                case WindowState.Normal:
                    WindowRestored.Raise();

                    if (_browser != null)
                    {
                        _browser.FocusBrowser();
                    }
                    break;
            }
        }

        private void ApplyFrameStyle(FrameType frameType)
        {
            switch (frameType)
            {
                case FrameType.NotSpecified:
                case FrameType.Paragon:
                    CustomChromeEnabled = WindowsVersion.IsWin7OrNewer;
                    break;

                case FrameType.None:
                    // Hide the window chrome.
                    TitlebarHeight = 0;
                    WindowStyle = WindowStyle.None;
                    break;

                case FrameType.WindowsDefault:
                    // Nothing to do here - custom chrome is disabled by default.
                    break;
            }
        }

        private void ApplyWindowOptions()
        {
            if (_options == null)
            {
                return;
            }

            var bounds = _options.OuterBounds;
            var frameOptions = _options.Frame;

            if (frameOptions != null)
            {
                if (frameOptions.Type == FrameType.NotSpecified)
                {
                    frameOptions.Type = _windowManager.Application.Package.Manifest.DefaultFrameType;
                }

                ApplyFrameStyle(frameOptions.Type);

                if (frameOptions.Icon)
                {
                    using (var stream = _windowManager.Application.Package.GetIcon128())
                    {
                        if (stream != null)
                        {
                            Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(_options.InitialState))
            {
                switch (_options.InitialState.ToLower())
                {
                    case "normal":
                        WindowState = WindowState.Normal;
                        break;
                    case "maximized":
                        WindowState = WindowState.Maximized;
                        break;
                    case "minimized":
                        WindowState = WindowState.Minimized;
                        break;
                    case "fullscreen":
                        FullScreenWindow();
                        break;
                }
            }

            if (bounds != null)
            {
                Left = bounds.Left;
                Top = bounds.Top;

                if (bounds.Width > 0)
                {
                    Width = bounds.Width;
                }
                if (bounds.Height > 0)
                {
                    Height = bounds.Height;
                }

                if (bounds.MinWidth > 0)
                {
                    MinWidth = bounds.MinWidth;
                }
                if (bounds.MinHeight > 0)
                {
                    MinHeight = bounds.MinHeight;
                }
                if (bounds.MaxWidth > 0)
                {
                    MaxWidth = bounds.MaxWidth;
                }
                if (bounds.MaxHeight > 0)
                {
                    MaxHeight = bounds.MaxHeight;
                }
            }

            if (!string.IsNullOrEmpty(_title))
            {
                Title = _title;
            }

            ResizeMode = _options.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            MinMaxButtonsVisible = _options.Resizable;

            Topmost = _options.AlwaysOnTop;
            _minimizeOnClose = _options.MinimizeOnClose;

            if (!_options.Focused)
            {
                ShowActivated = false;
            }

            InvalidateArrange();
        }

        private void AttachToBrowser()
        {
            _browser.TitleChanged += TitleChanged;
            _browser.BeforePopup += BeforePopup;
            _browser.ShowPopup += ShowPopup;
            _browser.JSDialog += OnJavaScriptDialog;
            _browser.BeforeUnloadDialog += OnUnloadPageDialog;
            _browser.BrowserClosed += OnBrowserClosed;
            _browser.BeforeResourceLoad += OnBeforeResourceLoad;
            _browser.DownloadUpdated += OnDownloadUpdated;
            _browser.ProtocolExecution += OnProtocolExecution;
        }

        private void OnProtocolExecution(object sender, ProtocolExecutionEventArgs e)
        {            
            if(_windowManager.Application.Package.Manifest.CustomProtocolWhitelist != null)
            {
                e.Allow = _windowManager.Application.Package.Manifest.CustomProtocolWhitelist.Contains("*") ||
                          _windowManager.Application.Package.Manifest.CustomProtocolWhitelist.Contains(new Uri(e.Url).Scheme);
            }
        }

        private void OnDownloadUpdated(object sender, DownloadProgressEventArgs e)
        {
            if (DownloadProgress != null)
            {
                DownloadProgress(this, e);
            }
        }

        private void OnBeforeResourceLoad(object sender, ResourceLoadEventArgs e)
        {
            e.Cancel = true;

            if (_windowManager != null && _windowManager.Application != null &&
                _windowManager.Application.Package.Manifest.ExternalUrlWhitelist != null)
            {
                var reqUrl = e.Url;
                var whitelist = _windowManager.Application.Package.Manifest.ExternalUrlWhitelist;
                foreach (var url in whitelist)
                {
                    try
                    {
                        if (url.Equals(reqUrl, StringComparison.InvariantCultureIgnoreCase) ||
                            url.Equals("*"))
                        {
                            e.Cancel = false;
                            break;
                        }

                        var pattern = Regex.Escape(url).Replace(@"\*", ".+?").Replace(@"\?", ".");
                        if (pattern.EndsWith(".+?"))
                        {
                            pattern = url.Remove(pattern.Length - 3, 3);
                            pattern += ".*";
                        }

                        var regEx = new Regex(pattern);
                        if (regEx.Match(reqUrl).Success)
                        {
                            e.Cancel = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(string.Format("Invalid pattern in ExternalUrlWhitelist itemn {0}: {1}", url, ex.Message));
                    }
                }
            }
        }

        private void BeforePopup(object sender, BeforePopupEventArgs eventArgs)
        {
            _windowManager.BeforeApplicationWindowPopup(eventArgs);
        }

        private void DetachFromBrowser()
        {
            _browser.LoadEnd -= OnPageLoaded;
            _browser.TitleChanged -= TitleChanged;
            _browser.BeforePopup -= BeforePopup;
            _browser.ShowPopup -= ShowPopup;
            _browser.JSDialog -= OnJavaScriptDialog;
            _browser.BeforeUnloadDialog -= OnUnloadPageDialog;
            _browser.BeforeResourceLoad -= OnBeforeResourceLoad;
            _browser.DownloadUpdated -= OnDownloadUpdated;
            _browser.ProtocolExecution -= OnProtocolExecution;
        }

        private void DoCleanup()
        {
            if (_browser != null)
            {
                DetachFromBrowser();
                _browser.BrowserClosed -= OnBrowserClosed;
                _browser.Dispose();
                _browser = null;
                if (_windowManager != null)
                {
                    _windowManager.RemoveApplicationWindow(this);
                    _windowManager = null;
                }
                Content = null;
            }
        }

        private void OnBrowserClosed(object sender, EventArgs e)
        {
            if (_browser != null)
            {
                _isClosed = true;
                DoCleanup();
                Close();
            }
        }

        private void OnBrowserPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleKeyPress(e.Key);
        }

        private void OnJavaScriptDialog(object sender, JsDialogEventArgs ea)
        {
            try
            {
                // Bring up the dialog asynchronously
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var result = MessageBoxResult.Cancel;
                    var msg = string.Empty;
                    switch (ea.DialogType)
                    {
                        case CefJSDialogType.Alert:
                            JavaScriptDialog.Alert("Alert", ea.MessageText, this);
                            result = MessageBoxResult.OK;
                            ea.Handled = true;
                            break;
                        case CefJSDialogType.Confirm:
                            result = JavaScriptDialog.Confirm("Confirm", ea.MessageText, this);
                            break;
                        case CefJSDialogType.Prompt:
                            msg = ea.DefaultPromptText;
                            result = JavaScriptDialog.Prompt("Prompt", ea.MessageText, ref msg, this);
                            break;
                    }
                    if (_browser != null)
                    {
                        ea.Callback.Continue(result == MessageBoxResult.OK, msg);
                    }
                }));
            }
            finally
            {
                ea.Handled = true;
            }
        }

        private void OnPageLoaded(object sender, LoadEndEventArgs e)
        {
            if (!_firstPageLoaded)
            {
                _firstPageLoaded = true;
                Content = _browser;
                LoadComplete.Raise(this, EventArgs.Empty);
                InvalidateArrange();
            }
            if (PageLoaded != null)
                PageLoaded(e.Url);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SizeChanged += OnSizeChanged;
            LocationChanged += OnLocationChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Close();
        }

        private void OnLocationChanged(object sender, EventArgs e)
        {
            var bounds = GetBounds();
            WindowBoundsChanged.Raise(() => new object[] { bounds });
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var bounds = GetBounds();
            WindowBoundsChanged.Raise(() => new object[] { bounds });
        }

        private BoundsSpecification GetBounds()
        {
            return (BoundsSpecification)Dispatcher.Invoke(new Func<BoundsSpecification>(() =>
            {
                var rect = RECT.FromHandle(Handle);

                return new BoundsSpecification
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Height = rect.Height,
                    Width = rect.Width,
                    MaxHeight = MaxHeight,
                    MaxWidth = MaxWidth,
                    MinHeight = MinHeight,
                    MinWidth = MinWidth
                };
            }));
        }

        private void OnUnloadPageDialog(object sender, UnloadDialogEventArgs ea)
        {
            try
            {
                // Bring up the dialog asynchronously
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var msg = string.Empty;
                    var r = JavaScriptDialog.Confirm("JavaScript", ea.MessageText, this);
                    if (_browser != null)
                    {
                        _isClosing = (r == MessageBoxResult.OK);
                        ea.Callback.Continue(_isClosing, msg);
                    }
                }));
            }
            finally
            {
                ea.Handled = true;
            }
        }

        private void Reload(bool ignoreCache)
        {
            _browser.Reload(ignoreCache);
        }

        private void ShowPopup(object sender, ShowPopupEventArgs eventArgs)
        {
            _windowManager.ShowApplicationWindowPopup(this, eventArgs);
        }

        private void TitleChanged(object sender, TitleChangedEventArgs e)
        {
            Title = e.Title;
            PropertyChanged.Raise(() => new object[] { "title", e.Title });
        }

        private void ToolsClosed(object sender, EventArgs ea)
        {
            if (_tools != null)
            {
                _tools.Closed -= ToolsClosed;
            }

            _tools = null;
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (_browser != null)
            {
                _browser.BlurBrowser();
            }
        }

        private void HandleKeyPress(Key key)
        {
            switch (key)
            {
                case Key.J:
                    if ((key & Key.LeftCtrl) == Key.LeftCtrl &&
                        (key & Key.LeftCtrl) == Key.LeftShift)
                    {
                        ShowDeveloperTools(new Point(int.MinValue, int.MinValue));
                    }
                    break;

                case Key.F12:
                    ShowDeveloperTools(new Point(int.MinValue, int.MinValue));
                    break;

                case Key.F5:
                    Reload((key & Key.LeftCtrl) == Key.LeftCtrl);
                    break;

                case Key.Escape:
                    if (IsFullScreenEnabled)
                    {
                        Restore();
                    }
                    break;
            }
        }

        [DebuggerStepThrough]
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_minimizeOnClose && msg == (int)WM.CLOSE)
            {
                Minimize();
                handled = true;
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timerSem.Close();

                if (_tools != null)
                {
                    _tools.Dispose();
                    _tools = null;
                }

                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        public class DeveloperToolsWindow : ApplicationWindow
        {
            public void Initialize(IDisposable control, string title, FrameType frameType)
            {
                Content = control;
                Title = "Paragon Developer Tools - " + title;
                ApplyFrameStyle(frameType);
            }

            protected override void OnClosed(EventArgs e)
            {
                var control = Content as IDisposable;
                if (control != null)
                {
                    control.Dispose();
                }

                base.OnClosed(e);
            }

            protected override void OnClosing(CancelEventArgs e)
            {
                // Required to prevent the base class logic that handles closing.
            }
        }
    }
}