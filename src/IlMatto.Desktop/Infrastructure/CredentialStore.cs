using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace IlMatto.Desktop.Infrastructure;

public static class CredentialStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    public static void Write(string target, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var credential = new NativeCredential
        {
            Type = Generic, TargetName = target, CredentialBlobSize = (uint)bytes.Length,
            CredentialBlob = Marshal.AllocCoTaskMem(bytes.Length), Persist = PersistLocalMachine,
            UserName = Environment.UserName
        };
        try { Marshal.Copy(bytes, 0, credential.CredentialBlob, bytes.Length); if (!CredWrite(ref credential, 0)) throw new Win32Exception(); }
        finally { Marshal.FreeCoTaskMem(credential.CredentialBlob); }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, Generic, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[checked((int)credential.CredentialBlobSize)]; Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string target)
    {
        CredDelete(target, Generic, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type; public string? TargetName; public string? Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern bool CredFree(IntPtr credential);
}
