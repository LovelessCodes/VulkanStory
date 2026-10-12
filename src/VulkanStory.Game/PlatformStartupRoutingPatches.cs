using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Client;

namespace VulkanStory.Game;

/// <summary>Replaces pinned platform-start graphics queries, default state and framebuffer setup after session commitment.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class PlatformStartupRoutingPatches
{
    private const string Owner = "vulkanstory.routing.graphics-platform-start";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo GetString = typeof(GL).GetMethod(nameof(GL.GetString), [typeof(StringName)]) ??
        throw new MissingMethodException("Original graphics string query is missing.");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, List<FrameBufferRef>> Buffers =
        AccessTools.FieldRefAccess<ClientPlatformWindows, List<FrameBufferRef>>("frameBuffers");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, ShaderProgramMinimalGui?> Gui =
        AccessTools.FieldRefAccess<ClientPlatformWindows, ShaderProgramMinimalGui?>("minimalGuiShaderProgram");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, int> Cores =
        AccessTools.FieldRefAccess<ClientPlatformWindows, int>("cpuCoreCount");
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, bool> Debug =
        AccessTools.FieldRefAccess<ClientPlatformWindows, bool>("glDebugMode");
    private sealed record Binding(string Name, Type Result, Type[] Parameters, string Prefix);
    private static readonly Binding[] Bindings =
    [
        new("Start", typeof(void), [], nameof(Start)),
        new("SetVSync", typeof(void), [typeof(bool)], nameof(Vsync)),
        new("Window_Resize", typeof(void), [], nameof(Resize)),
        new("UpdateMousePosition", typeof(void), [], nameof(Mouse)),
        new("LogAndTestHardwareInfosStage2", typeof(void), [], nameof(Hardware)),
        new("GetGraphicsCardRenderer", typeof(string), [], nameof(Renderer)),
        new("GetGraphicCardInfos", typeof(string), [], nameof(Infos)),
        new("GetGLShaderVersionString", typeof(string), [], nameof(ShaderVersion)),
        new("CheckGlError", typeof(void), [typeof(string)], nameof(Check)),
        new("CheckGlErrorAlways", typeof(void), [typeof(string)], nameof(CheckAlways)),
        new("GlGetError", typeof(string), [], nameof(Error)),
        new("GlGetMaxTextureSize", typeof(int), [], nameof(MaxTexture)),
        new("set_GlDebugMode", typeof(void), [typeof(bool)], nameof(DebugMode)),
    ];
    private static MethodInfo Resolve(Binding binding) => typeof(ClientPlatformWindows).GetMethod(binding.Name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
        binder: null, types: binding.Parameters, modifiers: null) is { } method && method.ReturnType == binding.Result && method.GetMethodBody() != null
            ? method : throw new MissingMethodException(typeof(ClientPlatformWindows).FullName, binding.Name);
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateSubset(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        (MethodInfo Target, string Prefix)[]? methods = null;
        MethodInfo? exit = null;
        MethodInfo? finalize = null;
        ConstructorInfo? crashReporter = null;
        MethodInfo? collectHeadlessMods = null;
        return new StartupPatchGroup("graphics-platform-start", () =>
        {
            foreach (var (name, type) in new (string, Type)[]
            { ("frameBuffers", typeof(List<FrameBufferRef>)), ("minimalGuiShaderProgram", typeof(ShaderProgramMinimalGui)), ("cpuCoreCount", typeof(int)), ("glDebugMode", typeof(bool)) })
                if (AccessTools.Field(typeof(ClientPlatformWindows), name)?.FieldType != type)
                    throw new MissingFieldException("Original platform startup metadata changed: " + name);
            methods = Bindings.Select(binding => (Resolve(binding), binding.Prefix)).ToArray();
            exit = typeof(ClientPlatformWindows).GetMethod("WindowExit", [typeof(string), typeof(EnumExitMode)]) ??
                throw new MissingMethodException("Original WindowExit is missing.");
            finalize = typeof(ClientSystemStartup).GetMethod("HandleLevelFinalize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, [typeof(Packet_Server)], null);
            if (finalize?.ReturnType != typeof(void) || finalize.GetMethodBody() is null)
                throw new MissingMethodException("Original level finalization is missing.");
            CheckFinalize(PatchProcessor.GetOriginalInstructions(finalize));
            if (HeadlessHarnessOptions.Enabled)
            {
                HeadlessGameBindings.Validate();
                collectHeadlessMods = HeadlessModDiscovery.Validate();
                crashReporter = typeof(Vintagestory.ClientNative.CrashReporter).GetConstructor([typeof(EnumAppSide)]) ??
                    throw new MissingMethodException("Original crash reporter constructor is missing.");
                if (AccessTools.Field(typeof(Vintagestory.ClientNative.CrashReporter), "launchCrashReporterGui")?.FieldType != typeof(bool))
                    throw new MissingFieldException("Original crash reporter GUI flag is missing.");
            }
        }, () =>
        {
            if (runtime != null || methods is null || exit is null || finalize is null) throw new InvalidOperationException("Platform startup routing is unvalidated or already owned.");
            runtime = owner; attempted = true;
            foreach (var method in methods) harmony.Patch(method.Target,
                prefix: new HarmonyMethod(typeof(PlatformStartupRoutingPatches), method.Prefix) { priority = Priority.First });
            harmony.Patch(exit, postfix: new HarmonyMethod(typeof(PlatformStartupRoutingPatches), nameof(Exit)));
            harmony.Patch(finalize, transpiler: new HarmonyMethod(typeof(PlatformStartupRoutingPatches),
                nameof(FinalizeTranspiler)) { priority = Priority.First });
            if (crashReporter != null) harmony.Patch(crashReporter,
                postfix: new HarmonyMethod(typeof(PlatformStartupRoutingPatches), nameof(HeadlessCrashReporter)));
            if (collectHeadlessMods != null) harmony.Patch(collectHeadlessMods,
                postfix: new HarmonyMethod(typeof(HeadlessModDiscovery), nameof(HeadlessModDiscovery.Filter)));
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner); if (ReferenceEquals(runtime, owner)) runtime = null; attempted = false;
        });
    }
    private static bool TrySession(ClientPlatformWindows platform, out GameRenderSession session)
    {
        session = null!;
        if (runtime?.HasCommitted != true) return false;
        if (!runtime.TrySession(platform, out session)) throw new InvalidOperationException("Committed startup routing lost its session.");
        return true;
    }
    private static void HeadlessCrashReporter(ref bool ___launchCrashReporterGui) => ___launchCrashReporterGui = false;

    private static void CheckFinalize(IReadOnlyList<CodeInstruction> body)
    {
        int queries = 0;
        for (int index = 0; index < body.Count; index++)
        {
            if (!body[index].Calls(GetString)) continue;
            queries++;
            if (index == 0 || body[index - 1].opcode != OpCodes.Ldc_I4 ||
                Convert.ToInt32(body[index - 1].operand) != (int)StringName.Renderer)
                throw new InvalidOperationException("Original level-finalization renderer query changed.");
        }
        if (queries != 1)
            throw new InvalidOperationException("Original level-finalization renderer query count changed.");
    }

    private static IEnumerable<CodeInstruction> FinalizeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        CheckFinalize(body);
        foreach (var instruction in body)
        {
            if (!instruction.Calls(GetString)) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(PlatformStartupRoutingPatches), nameof(FinalizeRenderer));
        }
        return body;
    }

    private static string FinalizeRenderer(StringName name)
    {
        if (runtime?.Routing.RoutingEnabled != true) return GL.GetString(name);
        if (name != StringName.Renderer || ScreenManager.Platform is not ClientPlatformWindows platform ||
            !TrySession(platform, out var session))
            throw new InvalidOperationException("Active level-finalization routing lost its renderer session.");
        return session.Device.RendererString;
    }
    private static bool Start(ClientPlatformWindows __instance)
    {
        if (!TrySession(__instance, out var session)) return true;
        session.Window.SetTitle("Vintage Story");
        Buffers(__instance) = session.Graphics.SetupDefaultFramebuffers();
        var gui = new ShaderProgramMinimalGui();
        Gui(__instance) = gui;
        gui.Compile();
        var pixels = session.Window.PixelSize;
        __instance.WindowSize.Width = pixels.Width; __instance.WindowSize.Height = pixels.Height;
        session.Graphics.RequireStatedState().LineWidth = 1.5f;
        __instance.SupportsThickLines = session.Device.SupportsThickLines;
        Cores(__instance) = Environment.ProcessorCount;
        return false;
    }
    // The session applies ClientSettings.VsyncMode before every frame and is the
    // only swap-interval owner; the original call is consumed without a second write.
    private static bool Vsync(ClientPlatformWindows __instance) => !TrySession(__instance, out _);
    private static bool Resize(ClientPlatformWindows __instance)
    { if (!TrySession(__instance, out var session)) return true; session.Input.RefreshWindowLayout(); return false; }
    private static bool Mouse(ClientPlatformWindows __instance) => !TrySession(__instance, out _);
    private static bool Hardware(ClientPlatformWindows __instance)
    {
        if (!TrySession(__instance, out var session)) return true;
        var device = session.Device;
        __instance.Logger.Notification("Graphics backend: {0}; vendor: {1}; renderer: {2}; Vulkan: {3}",
            device.BackendName, device.VendorString, device.RendererString, device.VersionString);
        __instance.Logger.Notification("Native shader target: {0}; maximum texture size: {1}", device.ShaderVersionString, device.MaxTextureSize);
        return false;
    }
    private static bool Renderer(ClientPlatformWindows __instance, ref string __result)
    { if (!TrySession(__instance, out var session)) return true; __result = session.Device.RendererString; return false; }
    private static bool Infos(ClientPlatformWindows __instance, ref string __result)
    {
        if (!TrySession(__instance, out var session)) return true;
        var device = session.Device;
        __result = "GC Vendor: " + device.VendorString + "\nGC Version: " + device.VersionString + "\nGC Renderer: " + device.RendererString + "\nGC ShaderVersion: " + device.ShaderVersionString;
        return false;
    }
    private static bool ShaderVersion(ClientPlatformWindows __instance, ref string __result)
    { if (!TrySession(__instance, out var session)) return true; __result = session.Device.ShaderVersionString; return false; }
    private static bool Check(ClientPlatformWindows __instance, string __0)
    {
        if (!TrySession(__instance, out var session)) return true;
        if (__instance.GlErrorChecking && session.Device.GetError() is { Length: > 0 } error)
            throw new InvalidOperationException((__0 ?? "") + " - Vulkan: " + error);
        return false;
    }
    private static bool CheckAlways(ClientPlatformWindows __instance, string __0)
    {
        if (!TrySession(__instance, out var session)) return true;
        if (session.Device.GetError() is { Length: > 0 } error) __instance.Logger.Error("{0} - Vulkan: {1}", __0 ?? "", error);
        return false;
    }
    private static bool Error(ClientPlatformWindows __instance, ref string? __result)
    { if (!TrySession(__instance, out var session)) return true; string error = session.Device.GetError(); __result = string.IsNullOrEmpty(error) ? null : error; return false; }
    private static bool MaxTexture(ClientPlatformWindows __instance, ref int __result)
    { if (!TrySession(__instance, out var session)) return true; __result = session.Device.MaxTextureSize; return false; }
    private static bool DebugMode(ClientPlatformWindows __instance, bool __0)
    { if (!TrySession(__instance, out var session)) return true; Debug(__instance) = __0; session.Device.DebugMode = __0; return false; }
    private static void Exit(ClientPlatformWindows __instance)
    { if (TrySessionForShutdown(__instance, out var session)) session.Input.RequestWindowExit(); }

    /// <summary>Session for teardown paths, where a missing session means the renderer already stopped.</summary>
    /// <remarks>The committed-routing invariant throws in live paths; during crash handling a gone session
    /// is expected, and throwing there turns a handled crash into an abort.</remarks>
    private static bool TrySessionForShutdown(ClientPlatformWindows platform, out GameRenderSession session)
    {
        session = null!;
        if (runtime?.HasCommitted != true) return false;
        return runtime.TrySession(platform, out session);
    }
}
