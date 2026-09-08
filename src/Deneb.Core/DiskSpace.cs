using System.Runtime.InteropServices;
using System.Text;

namespace Deneb.Core;

public sealed record DiskVolume(string Id, long AvailableBytes);
public interface IDiskSpaceService { DiskVolume Measure(string destination); }

public sealed class DiskSpaceService : IDiskSpaceService
{
    // Darwin sys/mount.h statfs64; only fields needed here are exposed.
    [StructLayout(LayoutKind.Explicit, Size = 2168)]
    private struct MacStat
    {
        [FieldOffset(0)] public uint BlockSize;
        [FieldOffset(24)] public long Available;
        [FieldOffset(48)] public int Id1;
        [FieldOffset(52)] public int Id2;
    }
    // Linux asm-generic/statfs.h, 64-bit x64/ARM64 ABI.
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct LinuxStat
    {
        [FieldOffset(8)] public long BlockSize;
        [FieldOffset(32)] public long Available;
        [FieldOffset(56)] public int Id1;
        [FieldOffset(60)] public int Id2;
    }
    [DllImport("libc", EntryPoint = "statfs64", SetLastError = true)]
    private static extern int MacStatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat result);
    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int LinuxStatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out LinuxStat result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePath, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);

    public DiskVolume Measure(string destination)
    {
        try
        {
            var path = ResolveExistingDirectory(destination);
            if (OperatingSystem.IsWindows())
            {
                var mount = new StringBuilder(32768); var identity = new StringBuilder(1024);
                if (!GetVolumePathName(path, mount, (uint)mount.Capacity) ||
                    !GetVolumeNameForVolumeMountPoint(mount.ToString(), identity, (uint)identity.Capacity))
                    throw new IOException();
                if (!GetDiskFreeSpaceEx(path, out var available, out _, out _)) throw new IOException();
                return new(identity.ToString(), (long)Math.Min((ulong)long.MaxValue, available));
            }
            if (OperatingSystem.IsMacOS())
            {
                if (MacStatFs(path, out var stat) != 0) throw new IOException();
                return new($"mac:{stat.Id1}:{stat.Id2}", FreeBytes(stat.Available, stat.BlockSize));
            }
            if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            {
                if (LinuxStatFs(path, out var stat) != 0) throw new IOException();
                return new($"linux:{stat.Id1}:{stat.Id2}", FreeBytes(stat.Available, stat.BlockSize));
            }
            throw new ProblemException(ProblemCode.DiskSpaceUnavailable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new ProblemException(ProblemCode.DiskSpaceUnavailable); }
    }
    private static long FreeBytes(long available, long blockSize) => blockSize <= 0 ? throw new IOException() :
        (long)Math.Min(long.MaxValue, (decimal)Math.Max(0, available) * blockSize);
    public static string ResolveExistingDirectory(string destination)
    {
        var full = Path.GetFullPath(destination);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new DirectoryInfo(Path.Combine(current, component));
            if (!next.Exists) break;
            current = next.ResolveLinkTarget(true)?.FullName ?? next.FullName;
        }
        return current;
    }
}

public sealed record DiskSpaceSnapshot(long AvailableBytes, long RequiredBytes, long ReservedByOthersBytes, long SafetyBytes, bool UnknownSize);

/// <summary>Logical reservations. All calls are serialized by the engine gate.</summary>
public sealed class DiskReservations(IDiskSpaceService service, TimeProvider clock)
{
    public const long SafetyBytes = 512L * 1024 * 1024;
    private sealed class Volume(long free, long sampled)
    { public long Free = free; public long Sampled = sampled; }
    private sealed class Entry(string path, string volume, long? total, long parts)
    {
        public string Path = path, Volume = volume;
        public long? Total = total;
        public long Parts = parts, Assembled, Pending;
        public bool Assembly, Stopped;
        public long Required => Stopped ? Pending : Assembly ? Math.Max(0, Parts - Assembled) :
            Total is { } total ? (long)Math.Min(long.MaxValue, (decimal)Math.Max(0, total - Parts) + total) :
            (long)Math.Min(long.MaxValue, (decimal)Parts + 2m * Pending);
    }
    private readonly Dictionary<string, Volume> volumes = [];
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Dictionary<Guid, DiskSpaceSnapshot> stoppedSnapshots = [];
    public void Add(Guid id, string path, long? total, long parts, bool assembly)
    {
        var measured = service.Measure(path);
        if (measured.AvailableBytes < 0) throw new ProblemException(ProblemCode.DiskSpaceUnavailable);
        volumes[measured.Id] = new(Math.Max(0, measured.AvailableBytes - entries.Values.Where(e => e.Volume == measured.Id).Sum(e => e.Pending)), clock.GetTimestamp());
        stoppedSnapshots.Remove(id);
        entries[id] = new(path, measured.Id, total, parts) { Assembly = assembly };
    }
    public DiskSpaceSnapshot? Snapshot(Guid id)
    {
        if (stoppedSnapshots.TryGetValue(id, out var previous)) return previous;
        if (!entries.TryGetValue(id, out var e)) return null;
        return new(volumes[e.Volume].Free, e.Required, Sum(e.Volume, id), SafetyBytes, e.Total == null);
    }
    private long Sum(string volume, Guid? except = null) => (long)Math.Min(long.MaxValue, entries.Where(p => p.Key != except && p.Value.Volume == volume).Sum(p => (decimal)p.Value.Required));
    public bool Fits(Guid id) => entries.TryGetValue(id, out var e) &&
        volumes[e.Volume].Free >= SafetyBytes && Sum(e.Volume) <= volumes[e.Volume].Free - SafetyBytes;
    public void Stop(Guid id)
    {
        if (entries.TryGetValue(id, out var e) && !e.Stopped)
        { stoppedSnapshots[id] = Snapshot(id)!; e.Stopped = true; }
    }
    public void Remove(Guid id) { entries.Remove(id); stoppedSnapshots.Remove(id); }
    public bool Contains(Guid id) => entries.ContainsKey(id);
    public void BeginAssembly(Guid id)
    {
        var e = entries[id]; e.Assembly = true; e.Assembled = 0;
    }
    public void ReserveWrite(Guid id, int bytes)
    {
        var e = entries[id];
        if (e.Stopped) throw new ProblemException(ProblemCode.InsufficientDiskSpace);
        e.Pending = checked(e.Pending + bytes);
        if (!Fits(id)) { e.Pending -= bytes; throw new ProblemException(ProblemCode.InsufficientDiskSpace); }
    }
    public void CompleteWrite(Guid id, int granted, int written)
    {
        var e = entries[id]; e.Pending -= granted;
        if (e.Assembly) e.Assembled += written; else e.Parts += written;
        volumes[e.Volume].Free = Math.Max(0, volumes[e.Volume].Free - written);
    }
    public IReadOnlyList<(Guid Id, ProblemCode Reason)> Refresh(IReadOnlyList<Guid> queue, bool force = false)
    {
        List<(Guid, ProblemCode)> stopped = [];
        foreach (var group in entries.GroupBy(p => p.Value.Volume).ToArray())
        {
            var volume = volumes[group.Key];
            if (!force && clock.GetElapsedTime(volume.Sampled) < TimeSpan.FromSeconds(1)) continue;
            try
            {
                var measurement = service.Measure(group.First().Value.Path);
                if (measurement.Id != group.Key || measurement.AvailableBytes < 0) throw new ProblemException(ProblemCode.DiskSpaceUnavailable);
                // Outstanding writes may not yet be reflected by the filesystem.
                volume.Free = Math.Max(0, measurement.AvailableBytes - group.Sum(p => p.Value.Pending));
                volume.Sampled = clock.GetTimestamp();
                foreach (var id in queue.Reverse().Where(id => entries.TryGetValue(id, out var e) && e.Volume == group.Key && !e.Stopped))
                {
                    if (Fits(id)) break;
                    stopped.Add((id, ProblemCode.InsufficientDiskSpace)); Stop(id);
                }
            }
            catch (ProblemException)
            {
                foreach (var pair in group.Where(p => !p.Value.Stopped))
                { stopped.Add((pair.Key, ProblemCode.DiskSpaceUnavailable)); Stop(pair.Key); }
            }
        }
        return stopped;
    }
}
