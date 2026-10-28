using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QLaunch;

/// <summary>
/// 云端翻译凭据。JSON 序列化后用 DPAPI（当前用户）加密，整体写入
/// %APPDATA%\QLaunch\cloud.bin；换用户或换机器都无法解密，不会留下明文。
/// </summary>
internal sealed class CloudCredentials
{
    private const int CryptProtectUiForbidden = 0x1;
    private const string FileName = "cloud.bin";

    /// <summary>翻译方式：local / baidu / microsoft / deepl；local 与旧配置一律禁用云端。</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("baiduAppId")]
    public string BaiduAppId { get; set; } = "";

    [JsonPropertyName("baiduKey")]
    public string BaiduKey { get; set; } = "";

    [JsonPropertyName("microsoftKey")]
    public string MicrosoftKey { get; set; } = "";

    [JsonPropertyName("microsoftRegion")]
    public string MicrosoftRegion { get; set; } = "";

    [JsonPropertyName("microsoftEndpoint")]
    public string MicrosoftEndpoint { get; set; } = "";

    [JsonPropertyName("deeplKey")]
    public string DeepLKey { get; set; } = "";

    /// <summary>留空时按密钥是否以 :fx 结尾自动选 Free / Pro 官方端点。</summary>
    [JsonPropertyName("deeplEndpoint")]
    public string DeepLEndpoint { get; set; } = "";

    [JsonIgnore]
    public string DataDir { get; set; } = "";

    /// <summary>读取并解密凭据；文件缺失或无法解密时返回空凭据，程序仍可只用本地模型。</summary>
    /// <param name="dataDir">数据目录。</param>
    /// <returns>凭据对象，DataDir 已填好。</returns>
    public static CloudCredentials Load(string dataDir)
    {
        CloudCredentials credentials = new() { DataDir = dataDir };
        string file = Path.Combine(dataDir, FileName);
        if (!File.Exists(file))
        {
            return credentials;
        }
        try
        {
            byte[] plain = Unprotect(File.ReadAllBytes(file));
            CloudCredentials? loaded = JsonSerializer.Deserialize<CloudCredentials>(plain);
            if (loaded is not null)
            {
                loaded.DataDir = dataDir;
                return loaded;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[cloud] 凭据读取失败: {e.Message}");
        }
        return credentials;
    }

    /// <summary>加密并写回凭据文件。</summary>
    /// <returns>无返回值。</returns>
    public void Save()
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(this);
        File.WriteAllBytes(Path.Combine(DataDir, FileName), Protect(plain));
    }

    private static byte[] Protect(byte[] data) => Transform(data, encrypt: true);

    private static byte[] Unprotect(byte[] data) => Transform(data, encrypt: false);

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        Blob input = Blob.FromBytes(data);
        Blob output = default;
        try
        {
            bool ok = encrypt
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return output.ToBytes();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;

        public static Blob FromBytes(byte[] bytes)
        {
            Blob blob = new() { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public readonly byte[] ToBytes()
        {
            byte[] bytes = new byte[Size];
            Marshal.Copy(Data, bytes, 0, Size);
            return bytes;
        }

        public void Free()
        {
            if (Data != IntPtr.Zero)
            {
                LocalFree(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
