using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>Owned VMA allocator using the renderer's current Vulkan dispatch table.</summary>
internal sealed unsafe class VmaRuntime : IDisposable
{
    private static readonly object LoadGate = new();
    private static Exports? _exports;
    private readonly Exports _api;
    private ulong _allocator;

    internal VmaRuntime(VulkanContext context)
    {
        lock (LoadGate) _api = _exports ??= new Exports(BridgePath());
        nint instanceProc = context.Api.Context.GetProcAddress("vkGetInstanceProcAddr");
        nint deviceProc = context.Api.Context.GetProcAddress("vkGetDeviceProcAddr");
        if (instanceProc == 0 || deviceProc == 0)
            throw new InvalidOperationException("The active Vulkan dispatch table has no instance/device resolver for VMA.");
        var info = new VmaCreateInfo
        {
            AbiVersion = 1,
            ApiVersion = VulkanContext.MinimumApiVersion,
            Flags = (context.MemoryBudgetAvailable ? 1u : 0u) |
                (context.BufferDeviceAddressEnabled ? 2u : 0u),
            Instance = (ulong)context.Instance.Handle,
            PhysicalDevice = (ulong)context.PhysicalDevice.Handle,
            Device = (ulong)context.Device.Handle,
            GetInstanceProcAddr = (ulong)instanceProc,
            GetDeviceProcAddr = (ulong)deviceProc,
        };
        ulong allocator = 0;
        VulkanResult.Check((Result)_api.Create(&info, &allocator), "creating the Vulkan Memory Allocator");
        if (allocator == 0) throw new InvalidOperationException("VMA returned an empty allocator handle.");
        _allocator = allocator;
    }

    internal Result Allocate(in VmaAllocateInfo info, out VmaAllocationInfo allocation)
    {
        allocation = default;
        fixed (VmaAllocateInfo* request = &info)
        fixed (VmaAllocationInfo* result = &allocation)
            return (Result)_api.Allocate(_allocator, request, result);
    }

    internal void Free(ulong allocation) => _api.Free(_allocator, allocation);
    internal void SetFrameIndex(uint frame) => _api.SetFrameIndex(_allocator, frame);

    internal void GetStatistics(VmaHeapStats[] heaps, VmaClassStats[] classes)
    {
        uint count = 0;
        fixed (VmaHeapStats* heapData = heaps)
        fixed (VmaClassStats* classData = classes)
            VulkanResult.Check((Result)_api.GetStats(_allocator, heapData, (uint)heaps.Length,
                classData, (uint)classes.Length, &count), "querying VMA heap budgets and allocation statistics");
        if (count != heaps.Length)
            throw new InvalidOperationException("VMA returned a different physical-device heap count.");
    }

    public void Dispose()
    {
        ulong allocator = _allocator;
        if (allocator == 0) return;
        _allocator = 0;
        _api.Destroy(allocator);
    }

    private static string BridgePath()
    {
        string binary = OperatingSystem.IsWindows() ? "VulkanStoryVma.dll" :
            OperatingSystem.IsMacOS() ? "libVulkanStoryVma.dylib" :
            OperatingSystem.IsLinux() ? "libVulkanStoryVma.so" :
            throw new PlatformNotSupportedException("The VMA bridge supports Windows, Linux and macOS.");
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The VMA bridge requires a 64-bit process.");
        if (NativeRuntimePaths.IsDeployedPayload)
            return Path.Combine(NativeRuntimePaths.PackageNativeDirectory ??
                throw new PlatformNotSupportedException("No native package RID for VMA."), binary);

        string? root = FindRepository(NativeRuntimePaths.AssemblyDirectory) ?? FindRepository(Environment.CurrentDirectory);
        if (root == null)
            throw new DllNotFoundException("The VMA bridge requires the VulkanStory native package or a verified repository checkout.");
        string? configured = Environment.GetEnvironmentVariable("VULKANSTORY_VMA_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new DllNotFoundException("VULKANSTORY_VMA_BRIDGE must name an absolute repository-local bridge.");
            string path = Path.GetFullPath(configured);
            VerifyRepositoryPath(root, path);
            return path;
        }
        string rid = OperatingSystem.IsWindows() ? "win-x64" :
            OperatingSystem.IsMacOS()
                ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : "linux-x64";
        string configuration = "Release";
        for (DirectoryInfo? directory = new(NativeRuntimePaths.AssemblyDirectory); directory != null; directory = directory.Parent)
        {
            if (directory.Name is "Debug" or "Release") { configuration = directory.Name; break; }
            if (string.Equals(directory.FullName, root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        string alternateConfiguration = configuration == "Debug" ? "Release" : "Debug";
        string[] candidates =
        [
            Path.Combine(NativeRuntimePaths.AssemblyDirectory, binary),
            Path.Combine(root, "artifacts", "native-vma", configuration, rid, binary),
            Path.Combine(root, "artifacts", "native-vma", alternateConfiguration, rid, binary),
            Path.Combine(root, "native", "vma", "build", "Release", binary),
            Path.Combine(root, "native", "vma", "build", binary),
        ];
        foreach (string path in candidates)
        {
            if (!File.Exists(path)) continue;
            VerifyRepositoryPath(root, path);
            return path;
        }
        throw new DllNotFoundException("The repository VMA bridge is missing; build it and set VULKANSTORY_VMA_BRIDGE to its repository-local path.");
    }

    private static string? FindRepository(string directory)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(directory)); current != null; current = current.Parent)
            if ((Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) &&
                File.Exists(Path.Combine(current.FullName, "src", "VulkanStory.Render.Vulkan", "Core", "VulkanAllocator.cs")) &&
                File.Exists(Path.Combine(current.FullName, "sdk", "vma", "include", "vk_mem_alloc.h")))
                return current.FullName;
        return null;
    }

    private static void VerifyRepositoryPath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative))
            throw new DllNotFoundException("The development VMA bridge must remain inside the verified repository.");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new DllNotFoundException("The development VMA bridge path may not contain symbolic links or junctions.");
            if (string.Equals(current, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
    }

    /// <summary>The module remains loaded while any allocator may hold its function pointers.</summary>
    private sealed class Exports
    {
        private readonly nint _module;
        internal readonly delegate* unmanaged[Cdecl]<VmaCreateInfo*, ulong*, int> Create;
        internal readonly delegate* unmanaged[Cdecl]<ulong, void> Destroy;
        internal readonly delegate* unmanaged[Cdecl]<ulong, VmaAllocateInfo*, VmaAllocationInfo*, int> Allocate;
        internal readonly delegate* unmanaged[Cdecl]<ulong, ulong, void> Free;
        internal readonly delegate* unmanaged[Cdecl]<ulong, uint, void> SetFrameIndex;
        internal readonly delegate* unmanaged[Cdecl]<ulong, VmaHeapStats*, uint, VmaClassStats*, uint, uint*, int> GetStats;

        internal Exports(string path)
        {
            _module = NativeRuntimePaths.LoadLibrary(path);
            try
            {
                Create = (delegate* unmanaged[Cdecl]<VmaCreateInfo*, ulong*, int>)Export("vs_vma_create");
                Destroy = (delegate* unmanaged[Cdecl]<ulong, void>)Export("vs_vma_destroy");
                Allocate = (delegate* unmanaged[Cdecl]<ulong, VmaAllocateInfo*, VmaAllocationInfo*, int>)Export("vs_vma_allocate");
                Free = (delegate* unmanaged[Cdecl]<ulong, ulong, void>)Export("vs_vma_free");
                SetFrameIndex = (delegate* unmanaged[Cdecl]<ulong, uint, void>)Export("vs_vma_set_frame_index");
                GetStats = (delegate* unmanaged[Cdecl]<ulong, VmaHeapStats*, uint, VmaClassStats*, uint, uint*, int>)Export("vs_vma_get_stats");
            }
            catch { NativeLibrary.Free(_module); throw; }
        }
        private nint Export(string symbol) => NativeLibrary.GetExport(_module, symbol);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct VmaCreateInfo
{
    internal uint AbiVersion, ApiVersion, Flags, Reserved;
    internal ulong Instance, PhysicalDevice, Device, GetInstanceProcAddr, GetDeviceProcAddr;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct VmaAllocateInfo
{
    internal ulong Size, Alignment, Buffer, Image;
    internal uint MemoryTypeBits, RequiredFlags, PreferredFlags, PoolClass, Flags, Reserved;
    internal ulong GrowthLimitBytes;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct VmaAllocationInfo
{
    internal ulong Allocation, Memory, Offset, Size, Mapped;
    internal uint MemoryTypeIndex, HeapIndex, MemoryProperties, PoolClass, Flags, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct VmaHeapStats
{
    internal ulong BlockBytes, AllocationBytes, Usage, Budget, HeapSize;
    internal uint BlockCount, AllocationCount, HeapFlags, Reserved;
    internal ulong ImageBlockBytes;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct VmaClassStats
{
    internal ulong BlockBytes, AllocationBytes, BlockCount, AllocationCount, DedicatedBytes, DedicatedCount;
}
