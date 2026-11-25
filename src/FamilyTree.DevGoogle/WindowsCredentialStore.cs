using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace FamilyTree.DevGoogle;

[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialStore(string target = "FamilyTree.DevGoogle.v1")
{
    private const int NotFound = 1168;

    public Credentials? Read()
    {
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw new InvalidOperationException();
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 2560 || credential.CredentialBlob == IntPtr.Zero)
                throw new InvalidOperationException();
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var values = JsonSerializer.Deserialize<string[]>(bytes);
                if (values is not { Length: 2 }) throw new InvalidOperationException();
                return new Credentials(values[0], values[1]);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                for (var i = 0; i < credential.CredentialBlobSize; i++)
                    Marshal.WriteByte(credential.CredentialBlob, i, 0);
            }
        }
        finally { CredFree(pointer); }
    }

    public void Write(Credentials values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { values.ClientId, values.ClientSecret });
        IntPtr blob = IntPtr.Zero;
        try
        {
            if (bytes.Length > 2560) throw new InvalidOperationException();
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1, TargetName = target, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob, Persist = 2, UserName = "FamilyTree"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (blob != IntPtr.Zero)
            {
                for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
                Marshal.FreeHGlobal(blob);
            }
        }
    }

    public void Remove()
    {
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new InvalidOperationException();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
