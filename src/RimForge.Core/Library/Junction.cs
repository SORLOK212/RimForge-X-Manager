using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RimForge.Core.Library;

/// <summary>
/// Directory junctions: a folder that is really another folder somewhere else on the PC. The game sees a mod in
/// its Mods folder while the files stay in the library. Unlike symbolic links, junctions need no administrator
/// rights. Removing a junction never touches the folder it points to.
/// </summary>
public static class Junction
{
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize,
        out int returned, IntPtr overlapped);

    /// <summary>Creates <paramref name="link"/> (must not exist) pointing to the existing folder <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        Directory.CreateDirectory(link);
        try
        {
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target.TrimEnd('\\'));
            var print = Encoding.Unicode.GetBytes(target.TrimEnd('\\'));
            // Mount point layout: four offsets/lengths, then both names, each followed by a 2-byte terminator.
            var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
            var buffer = new byte[8 + dataLength];
            BitConverter.GetBytes(ReparseTagMountPoint).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                               // substitute name offset
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);        // print name offset
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);

            using var handle = CreateFile(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            Directory.Delete(link);
            throw;
        }
    }

    public static bool IsLink(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Where a junction points, or null for an ordinary folder.</summary>
    public static string? TargetOf(string path) => IsLink(path) ? new DirectoryInfo(path).LinkTarget : null;

    /// <summary>Removes the junction itself; the folder it points to stays as it is.</summary>
    public static void Remove(string link)
    {
        if (!IsLink(link)) throw new InvalidOperationException("Not a junction: " + link);
        Directory.Delete(link, recursive: false);
    }

    /// <summary>
    /// Deletes a folder tree without ever following junctions inside it (their targets are left untouched).
    /// </summary>
    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        if (IsLink(dir))
        {
            Directory.Delete(dir, false);
            return;
        }
        foreach (var sub in Directory.GetDirectories(dir)) DeleteTree(sub);
        foreach (var file in Directory.GetFiles(dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(dir, false);
    }
}
