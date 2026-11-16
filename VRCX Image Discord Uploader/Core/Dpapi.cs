using System.Runtime.InteropServices;

namespace VRCX_Image_Discord_Uploader.Core;

/// <summary>Windows DPAPI (CryptProtectData) – encrypts data so only the current Windows user can read it.</summary>
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? desc, ref DATA_BLOB pEntropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr desc, ref DATA_BLOB pEntropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr h);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);

    static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new DATA_BLOB();
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = hIn.AddrOfPinnedObject() };
            var ent = new DATA_BLOB { cbData = entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "VRCX Uploader", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            hIn.Free();
            hEnt.Free();
        }
    }
}
