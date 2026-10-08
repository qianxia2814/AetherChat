using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AetherChat;

// Windows DPAPI binds saved keys to the current Windows user. Plaintext is only kept for a request.
internal static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    public static string Protect(string text) => string.IsNullOrEmpty(text) ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));
    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false)); }
        catch (Exception ex) when (ex is FormatException or Win32Exception) { throw new InvalidOperationException("无法解密已保存的密钥，请在当前 Windows 用户下重新填写。" ); }
    }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect ? CryptProtectData(ref input, "AetherChat", 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
            Array.Clear(bytes);
            if (output.Data != 0) { Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size); LocalFree(output.Data); }
        }
    }
}
