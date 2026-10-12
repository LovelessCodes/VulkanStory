using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VulkanStory.Platform.Sdl;

/// <summary>
/// SDL-owned window and Vulkan surface source. The game adapter owns the frame
/// loop; this host supplies window operations and the native surface handle.
/// Dispose the Vulkan device/swapchain before disposing this window.
/// </summary>
public sealed class SdlWindowHost : IDisposable
{
    private const uint VideoSubsystem = 0x00000020;
    private const ulong HiddenFlag = 0x0000000000000008;
    private const int HintOverridePriority = 2; // SDL_HINT_OVERRIDE
    private const ulong ResizableFlag = 0x0000000000000020;
    private const ulong HighPixelDensityFlag = 0x0000000000002000;
    private const ulong VulkanFlag = 0x0000000010000000;
    private const ulong FullscreenFlag = 0x0000000000000001;
    private const ulong BorderlessFlag = 0x0000000000000010;
    private const ulong MinimizedFlag = 0x0000000000000040;
    private const ulong MaximizedFlag = 0x0000000000000080;
    private const ulong FocusFlag = 0x0000000000000200;
    private IntPtr window;
    private readonly Dictionary<string, nint> cursors = new(StringComparer.Ordinal);
    private nint activeCursor;
    private uint pclPingMessage;
    private nint pclWindowHandle;
    private Action? pclPing;
    private static SdlWindowHost? pclHookOwner;
    private static readonly WindowsMessageHook PclWindowsHook = HandleWindowsMessage;

    private readonly bool keepHidden;
    private SdlWindowHost(IntPtr window, bool keepHidden = false) { this.window = window; this.keepHidden = keepHidden; }
    /// <summary>Whether visible/focus/capture operations are suppressed for an isolated hidden harness.</summary>
    public bool KeepsHidden => keepHidden;

    /// <summary>Borrowed SDL_Window pointer; zero after disposal. It is not a Win32 HWND or a game window pointer.</summary>
    public IntPtr NativeHandle => window;
    /// <summary>SDL window identity, or zero after disposal.</summary>
    public uint WindowId => window == IntPtr.Zero ? 0 : SDL_GetWindowID(window);
    /// <summary>Current SDL keyboard/input focus flag.</summary>
    public bool IsFocused => HasFlag(FocusFlag);
    /// <summary>Whether SDL's hidden flag is clear.</summary>
    public bool IsVisible => !HasFlag(HiddenFlag);
    /// <summary>Current SDL fullscreen flag.</summary>
    public bool IsFullscreen => HasFlag(FullscreenFlag);
    /// <summary>Current SDL minimized flag.</summary>
    public bool IsMinimized => HasFlag(MinimizedFlag);
    /// <summary>Current SDL maximized flag.</summary>
    public bool IsMaximized => HasFlag(MaximizedFlag);
    /// <summary>Whether the window is in its normal state: neither fullscreen, minimized nor maximized.</summary>
    public bool IsRestored => !HasFlag(FullscreenFlag | MinimizedFlag | MaximizedFlag);
    /// <summary>Whether the SDL window has no border.</summary>
    public bool IsBorderless => HasFlag(BorderlessFlag);
    /// <summary>Whether SDL allows user resizing.</summary>
    public bool IsResizable => HasFlag(ResizableFlag);
    /// <summary>Current per-window relative mouse mode used for gameplay capture.</summary>
    public bool RelativeMouseMode => SDL_GetWindowRelativeMouseMode(RequireWindow());
    /// <summary>Whether SDL text input is enabled for this window.</summary>
    public bool TextInputActive => SDL_TextInputActive(RequireWindow());
    /// <summary>SDL display identity associated with the current window.</summary>
    public uint DisplayId => SDL_GetDisplayForWindow(RequireWindow());
    /// <summary>Current SDL mouse position in logical window coordinates.</summary>
    public (float X, float Y) MousePosition
    {
        get
        {
            RequireWindow();
            SDL_GetMouseState(out float x, out float y);
            return (x, y);
        }
    }
    /// <summary>Logical window extent; use PixelSize for drawable resolution.</summary>
    public (int Width, int Height) WindowSize
    {
        get
        {
            Check(SDL_GetWindowSize(RequireWindow(), out int width, out int height), "SDL_GetWindowSize");
            return (width, height);
        }
    }
    /// <summary>Drawable extent in pixels, which may differ from logical size under high DPI.</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            Check(SDL_GetWindowSizeInPixels(RequireWindow(), out int width, out int height),
                "SDL_GetWindowSizeInPixels");
            return (width, height);
        }
    }
    /// <summary>Bounds extent of the window's current SDL display, in drawable pixels like <see cref="PixelSize"/>.</summary>
    /// <remarks>SDL reports display bounds in logical window units; they are scaled by this window's
    /// pixel/logical ratio, so the result equals the SDL bounds where logical units are pixels.</remarks>
    public (int Width, int Height) DisplaySize
    {
        get
        {
            uint display = SDL_GetDisplayForWindow(RequireWindow());
            if (display == 0) throw new InvalidOperationException("SDL_GetDisplayForWindow failed: " + Error());
            Check(SDL_GetDisplayBounds(display, out SdlRect bounds), "SDL_GetDisplayBounds");
            System.Numerics.Vector2 pixels = SdlWindowCoordinates.ToPixels(new(bounds.Width, bounds.Height), WindowSize, PixelSize);
            return ((int)MathF.Round(pixels.X), (int)MathF.Round(pixels.Y));
        }
    }
    /// <summary>Borrowed native HWND on Windows, or zero on other platforms/after disposal.</summary>
    public nint Win32Handle => OperatingSystem.IsWindows() && window != IntPtr.Zero
        ? SDL_GetPointerProperty(SDL_GetWindowProperties(window), "SDL.window.win32.hwnd", IntPtr.Zero)
        : 0;

    /// <summary>Initializes SDL video and creates a resizable high-DPI Vulkan window with native IME UI.</summary>
    /// <param name="title">Initial window title.</param>
    /// <param name="width">Positive initial logical width.</param>
    /// <param name="height">Positive initial logical height.</param>
    /// <param name="hidden">Whether the window begins hidden but may later be shown.</param>
    /// <param name="keepHidden">Whether later show/focus/capture operations remain suppressed.</param>
    /// <returns>The owner of the SDL window and video-subsystem reference.</returns>
    /// <exception cref="InvalidOperationException">SDL video initialization or Vulkan window creation fails.</exception>
    public static SdlWindowHost Create(string title, int width, int height, bool hidden = false, bool keepHidden = false)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        SdlNativeLibrary.EnsureRegistered();
        // SDL loads its own Vulkan runtime on the first Vulkan call; pin it to the
        // process runtime so instance and surface come from one implementation.
        SdlVulkanLibrary.EnsurePinned();
        // The game has no inline composition renderer. Let the OS display IME
        // preedit and candidates; SDL_TEXT_INPUT remains the only committed text.
        SDL_SetHintWithPriority("SDL_IME_IMPLEMENTED_UI", "none", HintOverridePriority);
        if (!SDL_InitSubSystem(VideoSubsystem))
            throw new InvalidOperationException("SDL3 video initialization failed: " + Error());
        ulong flags = VulkanFlag | ResizableFlag | HighPixelDensityFlag | (hidden || keepHidden ? HiddenFlag : 0);
        IntPtr window = SDL_CreateWindow(title, width, height, flags);
        if (window != IntPtr.Zero) return new SdlWindowHost(window, keepHidden);
        string reason = Error();
        SDL_QuitSubSystem(VideoSubsystem);
        throw new InvalidOperationException("SDL3 Vulkan window creation failed: " + reason);
    }

    /// <summary>Sets the SDL title, throwing when SDL refuses the operation.</summary>
    public void SetTitle(string title) => Check(SDL_SetWindowTitle(RequireWindow(), title), "SDL_SetWindowTitle");
    /// <summary>Sets a tightly packed RGBA8 icon through a temporary SDL surface released before return.</summary>
    public void SetIcon(int width, int height, byte[] rgba)
    {
        RequireWindow();
        WithRgbaSurface(width, height, rgba, "Window icon", surface =>
        {
            Check(SDL_SetWindowIcon(window, surface), "SDL_SetWindowIcon");
            return true;
        });
    }
    /// <summary>Requests a positive logical window size, preserving SDL's error result.</summary>
    public void SetSize(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Check(SDL_SetWindowSize(RequireWindow(), width, height), "SDL_SetWindowSize");
    }
    /// <summary>Requests a window whose drawable extent is approximately the given pixel size.</summary>
    /// <remarks>Converts through the current logical/pixel ratio, so it equals <see cref="SetSize"/> where SDL logical units are pixels.</remarks>
    public void SetPixelSize(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        System.Numerics.Vector2 logical = SdlWindowCoordinates.ToLogical(new(width, height), WindowSize, PixelSize);
        SetSize(Math.Max(1, (int)MathF.Round(logical.X)), Math.Max(1, (int)MathF.Round(logical.Y)));
    }
    /// <summary>Sets nonnegative logical minimum dimensions; zero leaves that axis without a minimum.</summary>
    public void SetMinimumSize(int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        Check(SDL_SetWindowMinimumSize(RequireWindow(), width, height), "SDL_SetWindowMinimumSize");
    }
    /// <summary>Sets nonnegative minimum dimensions in drawable pixels; zero leaves that axis without a minimum.</summary>
    /// <remarks>Converts through the current logical/pixel ratio like <see cref="SetPixelSize"/>; the limit is not
    /// re-derived if the window later moves to a display with a different pixel density.</remarks>
    public void SetMinimumPixelSize(int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        System.Numerics.Vector2 logical = SdlWindowCoordinates.ToLogical(new(width, height), WindowSize, PixelSize);
        // Round up (ignoring float noise) so the drawable minimum is not undershot; zero keeps "no minimum".
        SetMinimumSize(width == 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(logical.X - 0.01f)),
            height == 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(logical.Y - 0.01f)));
    }
    /// <summary>Changes SDL fullscreen state unless the host is required to stay hidden.</summary>
    public void SetFullscreen(bool enabled) { if (!keepHidden) Check(SDL_SetWindowFullscreen(RequireWindow(), enabled), "SDL_SetWindowFullscreen"); }
    /// <summary>Minimizes the window unless the host is required to stay hidden.</summary>
    public void Minimize() { if (!keepHidden) Check(SDL_MinimizeWindow(RequireWindow()), "SDL_MinimizeWindow"); }
    /// <summary>Requests SDL's centered window position.</summary>
    public void Center() => Check(SDL_SetWindowPosition(RequireWindow(), 0x2FFF0000, 0x2FFF0000), "SDL_SetWindowPosition");
    /// <summary>Maximizes the window unless the host is required to stay hidden.</summary>
    public void Maximize() { if (!keepHidden) Check(SDL_MaximizeWindow(RequireWindow()), "SDL_MaximizeWindow"); }
    /// <summary>Restores the window unless the host is required to stay hidden.</summary>
    public void Restore() { if (!keepHidden) Check(SDL_RestoreWindow(RequireWindow()), "SDL_RestoreWindow"); }
    /// <summary>Requests native window decorations.</summary>
    public void SetBordered(bool bordered) => Check(SDL_SetWindowBordered(RequireWindow(), bordered),
        "SDL_SetWindowBordered");
    /// <summary>Changes whether user resizing is allowed.</summary>
    public void SetResizable(bool resizable) => Check(SDL_SetWindowResizable(RequireWindow(), resizable),
        "SDL_SetWindowResizable");
    /// <summary>Sets SDL's process-wide minimize-on-focus-loss hint for fullscreen behavior.</summary>
    /// <returns>Whether SDL accepted the hint.</returns>
    public bool SetMinimizeOnFocusLoss(bool enabled)
    {
        RequireWindow();
        return SDL_SetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", enabled ? "1" : "0");
    }
    /// <summary>Sets SDL's process-wide Vulkan-library hint to an absolute runtime path.</summary>
    internal static void PinVulkanLibrary(string path) => SDL_SetHint("SDL_VULKAN_LIBRARY", path);
    /// <summary>Waits through SDL for pending asynchronous window operations to settle.</summary>
    public void Sync() => Check(SDL_SyncWindow(RequireWindow()), "SDL_SyncWindow");
    /// <summary>Shows the window unless the host is required to stay hidden.</summary>
    public void Show() { if (!keepHidden) Check(SDL_ShowWindow(RequireWindow()), "SDL_ShowWindow"); }
    /// <summary>Requests foreground raising unless the host is required to stay hidden.</summary>
    public void Raise() { if (!keepHidden) Check(SDL_RaiseWindow(RequireWindow()), "SDL_RaiseWindow"); }
    /// <summary>Restores a minimized window and requests foreground raising.</summary>
    public void Focus()
    {
        if (IsMinimized) Restore();
        Raise();
    }
    /// <summary>Hides the SDL window.</summary>
    public void Hide() => Check(SDL_HideWindow(RequireWindow()), "SDL_HideWindow");
    /// <summary>Installs this window as the sole SDL Windows-message observer for the supplied latency ping ID.</summary>
    /// <param name="messageId">Nonzero PCL ping message token.</param>
    /// <param name="onPing">Callback invoked inside SDL's message pump; callback exceptions are suppressed.</param>
    /// <returns>False off Windows, for a missing HWND/message ID, or while another host owns the hook.</returns>
    public bool InstallPclPingHook(uint messageId, Action onPing)
    {
        ArgumentNullException.ThrowIfNull(onPing);
        if (!OperatingSystem.IsWindows() || messageId == 0 ||
            (pclHookOwner != null && pclHookOwner != this)) return false;
        nint hwnd = Win32Handle;
        if (hwnd == 0) return false;
        pclPingMessage = messageId;
        pclWindowHandle = hwnd;
        pclPing = onPing;
        pclHookOwner = this;
        SDL_SetWindowsMessageHook(PclWindowsHook, 0);
        return true;
    }

    /// <summary>Removes the global SDL message hook only when this host owns it.</summary>
    public void RemovePclPingHook()
    {
        if (!ReferenceEquals(pclHookOwner, this)) return;
        SDL_SetWindowsMessageHook(null, 0);
        pclHookOwner = null;
        pclPing = null;
        pclPingMessage = 0;
        pclWindowHandle = 0;
    }

    private static bool HandleWindowsMessage(nint userdata, nint message)
    {
        SdlWindowHost? owner = pclHookOwner;
        if (owner != null && IsPclPingMessage(message, owner.pclWindowHandle, owner.pclPingMessage))
        {
            // SDL invokes this inside its message pump; the current frame token was
            // acquired before SDL_PumpEvents, so the ping belongs to that frame.
            try { owner.pclPing?.Invoke(); }
            catch { /* Never unwind a managed exception through SDL's callback. */ }
        }
        return true; // SDL must still process the Windows message.
    }

    /// <summary>Matches a valid native MSG pointer against this HWND and PCL message ID.</summary>
    public static bool IsPclPingMessage(nint message, nint windowHandle, uint pingMessage) =>
        message != 0 && windowHandle != 0 && pingMessage != 0 &&
        Marshal.ReadIntPtr(message) == windowHandle &&
        unchecked((uint)Marshal.ReadInt32(message, IntPtr.Size)) == pingMessage;
    /// <summary>Changes gameplay relative mouse capture unless the host is required to stay hidden.</summary>
    public void SetRelativeMouseMode(bool enabled) { if (!keepHidden) Check(
        SDL_SetWindowRelativeMouseMode(RequireWindow(), enabled), "SDL_SetWindowRelativeMouseMode"); }
    /// <summary>Warps the cursor in logical SDL coordinates unless the host is required to stay hidden.</summary>
    public void WarpMouse(float x, float y) { if (!keepHidden) SDL_WarpMouseInWindow(RequireWindow(), x, y); }
    /// <summary>Starts/stops committed SDL text input unless the host is required to stay hidden.</summary>
    public void SetTextInputActive(bool active) { if (keepHidden) return; Check(active
        ? SDL_StartTextInput(RequireWindow()) : SDL_StopTextInput(RequireWindow()),
        active ? "SDL_StartTextInput" : "SDL_StopTextInput"); }
    /// <summary>Asks SDL to clear current IME composition and returns SDL's success result.</summary>
    public bool ClearComposition() => SDL_ClearComposition(RequireWindow());
    /// <summary>Sets the logical IME rectangle and logical cursor offset, preserving SDL failure as an exception.</summary>
    public void SetTextInputArea(int x, int y, int width, int height, int cursor)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        var area = new SdlRect(x, y, width, height);
        Check(SDL_SetTextInputArea(RequireWindow(), ref area, cursor), "SDL_SetTextInputArea");
    }

    /// <summary>Copies clipboard UTF-8 text into managed storage and frees SDL's returned buffer.</summary>
    public string GetClipboardText()
    {
        RequireWindow();
        nint utf8 = SDL_GetClipboardText();
        if (utf8 == 0) throw new InvalidOperationException("SDL_GetClipboardText failed: " + Error());
        try { return Marshal.PtrToStringUTF8(utf8) ?? ""; }
        finally { SDL_free(utf8); }
    }

    /// <summary>Replaces SDL clipboard text, throwing when SDL refuses the operation.</summary>
    public void SetClipboardText(string text)
    {
        RequireWindow();
        Check(SDL_SetClipboardText(text), "SDL_SetClipboardText");
    }

    /// <summary>Creates/replaces an owned RGBA8 cursor, switching an active old cursor before its destruction.</summary>
    /// <param name="code">Stable cursor key retained until replacement or window disposal.</param>
    /// <param name="hotX">Horizontal hotspot passed to SDL.</param>
    /// <param name="hotY">Vertical hotspot passed to SDL.</param>
    /// <param name="width">Positive image width in pixels.</param>
    /// <param name="height">Positive image height in pixels.</param>
    /// <param name="rgba">Exactly four RGBA bytes per image texel; native creation occurs while pinned.</param>
    public void LoadCursor(string code, int hotX, int hotY, int width, int height, byte[] rgba)
    {
        RequireWindow();
        ArgumentNullException.ThrowIfNull(code);
        nint cursor = WithRgbaSurface(width, height, rgba, "Cursor image",
            surface => SDL_CreateColorCursor(surface, hotX, hotY));
        if (cursor == 0) throw new InvalidOperationException("SDL_CreateColorCursor failed: " + Error());
        if (cursors.TryGetValue(code, out nint old))
        {
            if (old == activeCursor)
            {
                // A cursor can be reloaded while selected. Swap before freeing
                // the old handle so the platform's selected code stays valid.
                if (!SDL_SetCursor(cursor))
                {
                    string reason = Error();
                    SDL_DestroyCursor(cursor);
                    throw new InvalidOperationException("SDL_SetCursor failed: " + reason);
                }
                activeCursor = cursor;
            }
            SDL_DestroyCursor(old);
        }
        cursors[code] = cursor;
    }

    /// <summary>Selects an owned cursor; an unknown key restores the default and returns false.</summary>
    public bool UseCursor(string code)
    {
        RequireWindow();
        if (!cursors.TryGetValue(code, out nint cursor)) { RestoreCursor(); return false; }
        Check(SDL_SetCursor(cursor), "SDL_SetCursor");
        activeCursor = cursor;
        return true;
    }

    /// <summary>Selects SDL's default cursor and clears the active custom handle.</summary>
    public void RestoreCursor()
    {
        RequireWindow();
        nint cursor = SDL_GetDefaultCursor();
        if (cursor == 0) throw new InvalidOperationException("SDL_GetDefaultCursor failed: " + Error());
        Check(SDL_SetCursor(cursor), "SDL_SetCursor");
        activeCursor = 0;
    }

    /// <summary>Copies the Vulkan instance-extension names required by SDL's video backend.</summary>
    /// <remarks>The Vulkan instance owner must enable these before creating this window's surface.</remarks>
    public string[] RequiredInstanceExtensions()
    {
        uint count;
        nint names = SDL_Vulkan_GetInstanceExtensions(out count);
        if (names == 0 || count == 0) throw new InvalidOperationException("SDL3 Vulkan extensions unavailable: " + Error());
        var extensions = new string[count];
        for (int i = 0; i < extensions.Length; i++)
            extensions[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(names, i * IntPtr.Size))
                ?? throw new InvalidOperationException("SDL3 returned a null Vulkan extension name");
        return extensions;
    }

    /// <summary>Attempts surface creation for the caller's Vulkan instance, converting failures/exceptions to a reason.</summary>
    /// <param name="instanceHandle">Live Vulkan instance created with SDL's required extensions.</param>
    /// <param name="surface">Created Vulkan surface on success; its destruction belongs to the Vulkan instance owner.</param>
    /// <param name="failureReason">Null on success, otherwise the closed-window/null-instance/native failure detail.</param>
    /// <returns>Whether SDL created the surface.</returns>
    public bool TryCreateVulkanSurface(nint instanceHandle, out ulong surface, out string? failureReason)
    {
        surface = 0;
        failureReason = null;
        if (window == IntPtr.Zero)
        {
            failureReason = "SDL3 window is closed";
            return false;
        }
        if (instanceHandle == 0)
        {
            failureReason = "Vulkan instance is null";
            return false;
        }
        try
        {
            if (!SDL_Vulkan_CreateSurface(window, instanceHandle, IntPtr.Zero, out surface))
            {
                failureReason = "SDL_Vulkan_CreateSurface failed: " + Error();
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            failureReason = "SDL3 surface creation threw: " + error.Message;
            return false;
        }
    }

    /// <summary>Removes owned message hooks/cursors, destroys the SDL window, and releases its video-subsystem reference.</summary>
    /// <remarks>Call after the Vulkan swapchain/device no longer uses this window. Repeated disposal is ignored.</remarks>
    public void Dispose()
    {
        if (window == IntPtr.Zero) return;
        RemovePclPingHook();
        if (cursors.Count > 0)
        {
            nint defaultCursor = SDL_GetDefaultCursor();
            if (defaultCursor != 0) SDL_SetCursor(defaultCursor);
            foreach (nint cursor in cursors.Values) SDL_DestroyCursor(cursor);
            cursors.Clear();
            activeCursor = 0;
        }
        SDL_DestroyWindow(window);
        window = IntPtr.Zero;
        SDL_QuitSubSystem(VideoSubsystem);
    }

    private IntPtr RequireWindow() => window != IntPtr.Zero ? window : throw new ObjectDisposedException(nameof(SdlWindowHost));
    private bool HasFlag(ulong flag) => (SDL_GetWindowFlags(RequireWindow()) & flag) != 0;

    /// <summary>Validates tightly packed RGBA8 pixels and lends a temporary SDL surface over them, destroyed before return.</summary>
    /// <param name="what">Image description used in validation and failure messages.</param>
    /// <param name="use">Consumer of the surface; it must not retain the surface after returning.</param>
    private static unsafe T WithRgbaSurface<T>(int width, int height, byte[] rgba, string what, Func<nint, T> use)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException(what + " must contain width × height RGBA pixels", nameof(rgba));
        uint format = SDL_GetPixelFormatForMasks(32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000);
        if (format == 0) throw new InvalidOperationException("SDL " + what + " pixel format unavailable: " + Error());
        fixed (byte* pixels = rgba)
        {
            nint surface = SDL_CreateSurfaceFrom(width, height, format, (nint)pixels, width * 4);
            if (surface == 0) throw new InvalidOperationException("SDL_CreateSurfaceFrom failed: " + Error());
            try { return use(surface); }
            finally { SDL_DestroySurface(surface); }
        }
    }
    private static void Check(bool success, string operation)
    {
        if (!success) throw new InvalidOperationException(operation + " failed: " + Error());
    }
    private static string Error() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "unknown SDL3 error";

    /// <summary>Native SDL_Rect memory layout used for display bounds and text-input areas.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SdlRect(int x, int y, int width, int height)
    {
        public int X = x;
        public int Y = y;
        public int Width = width;
        public int Height = height;
    }

    /// <summary>SDL native callback ABI for observing a Windows MSG during the event pump.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool WindowsMessageHook(nint userdata, nint message);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowsMessageHook(WindowsMessageHook? callback, nint userdata);

    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_InitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetError();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int width, int height, ulong flags);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroyWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetWindowID(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong SDL_GetWindowFlags(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowSize(IntPtr window, out int width, out int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowSizeInPixels(IntPtr window, out int width, out int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetDisplayForWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetDisplayBounds(uint display, out SdlRect bounds);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowTitle(
        IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowIcon(
        IntPtr window, nint icon);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowSize(IntPtr window, int width, int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowMinimumSize(IntPtr window, int width, int height);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowFullscreen(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool fullscreen);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_MinimizeWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowPosition(IntPtr window, int x, int y);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_MaximizeWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_RestoreWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowBordered(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool bordered);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowResizable(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool resizable);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetHint(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetHintWithPriority(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int priority);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_ClearComposition(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SyncWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_ShowWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_RaiseWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_HideWindow(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetWindowRelativeMouseMode(
        IntPtr window, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_GetWindowRelativeMouseMode(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_WarpMouseInWindow(IntPtr window, float x, float y);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetMouseState(out float x, out float y);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_StartTextInput(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_StopTextInput(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_TextInputActive(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetTextInputArea(IntPtr window, ref SdlRect area, int cursor);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetClipboardText();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetClipboardText(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(nint memory);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetPixelFormatForMasks(int bitsPerPixel,
        uint redMask, uint greenMask, uint blueMask, uint alphaMask);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_CreateSurfaceFrom(int width, int height, uint format, nint pixels, int pitch);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroySurface(nint surface);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_CreateColorCursor(nint surface, int hotX, int hotY);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_DestroyCursor(nint cursor);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetDefaultCursor();
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_SetCursor(nint cursor);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint SDL_GetWindowProperties(IntPtr window);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_GetPointerProperty(uint properties, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint defaultValue);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint SDL_Vulkan_GetInstanceExtensions(out uint count);
    [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SDL_Vulkan_CreateSurface(
        IntPtr window, nint instance, IntPtr allocator, out ulong surface);
}
