using System.Runtime.InteropServices;
using Deneb.Core;

namespace Deneb.Control;

public interface ITrashService
{
    bool Supported { get; }
    // Returns the actual system-assigned location for verification, never a guessed .Trash path.
    string Move(string path);
}

public sealed class MacTrashService : ITrashService
{
    public bool Supported => OperatingSystem.IsMacOS();
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendObject(IntPtr obj, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr obj, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte Trash(IntPtr obj, IntPtr selector, IntPtr url, out IntPtr result, out IntPtr error);
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct Stat { [FieldOffset(4)] public ushort Mode; }
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out Stat value);
    private static readonly Lazy<IntPtr> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"));

    public string Move(string path)
    {
        if (!Supported) throw new ProblemException(ProblemCode.UnsupportedPlatform);
        if (!Path.IsPathFullyQualified(path)) throw new ProblemException(ProblemCode.AbsolutePath);
        if (LStat(path, out var stat) != 0)
            throw new ProblemException(Marshal.GetLastPInvokeError() is 2 or 20 ? ProblemCode.MissingFile : ProblemCode.TrashFailed);
        if ((stat.Mode & 0xf000) != 0x8000) throw new ProblemException(ProblemCode.UnsafeFileType);
        _ = Foundation.Value;
        var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        try
        {
            var name = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), path);
            var url = SendObject(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), name);
            var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
            if (Trash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, out var result, out _) == 0)
                throw new ProblemException(ProblemCode.TrashFailed);
            // A successful move must remain a success even if Foundation does not return its location.
            return result == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Send(Send(result, sel_registerName("path")), sel_registerName("UTF8String"))) ?? "";
        }
        finally { Send(pool, sel_registerName("drain")); }
    }
}

public static class TrashOperations
{
    public static BatchResult Execute(DownloadEngine engine, IEnumerable<Guid> ids, ITrashService trash)
    {
        List<Guid> processed = [], skipped = [], failed = []; List<BatchFailure> errors = [];
        foreach (var id in ids.Distinct())
        {
            var job = engine.Snapshots().FirstOrDefault(j => j.Id == id);
            if (job == null || job.State != DownloadState.Completed) { skipped.Add(id); continue; }
            var moved = false;
            try
            {
                if (!trash.Supported) throw new ProblemException(ProblemCode.UnsupportedPlatform);
                if (job.Target == null) throw new ProblemException(ProblemCode.MissingFile);
                var info = new FileInfo(job.Target);
                if (info.LinkTarget != null || Directory.Exists(job.Target)) throw new ProblemException(ProblemCode.UnsafeFileType);
                try
                {
                    var attributes = File.GetAttributes(job.Target);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new ProblemException(ProblemCode.UnsafeFileType);
                }
                catch (FileNotFoundException) { throw new ProblemException(ProblemCode.MissingFile); }
                catch (DirectoryNotFoundException) { throw new ProblemException(ProblemCode.MissingFile); }
                trash.Move(job.Target); moved = true;
                engine.ForgetCompleted(id, job.Target);
                processed.Add(id);
            }
            catch (Exception ex)
            {
                failed.Add(id); errors.Add(new(id, Problem.FromException(ex), moved));
            }
        }
        return new(processed, skipped, failed) { Errors = errors };
    }
}
