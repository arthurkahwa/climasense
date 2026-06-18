using System.Security.Cryptography;
using System.Text;

namespace ClimaSense.Monitor.Services;

/// <summary>
/// Production secret store: the ups3 connection string, DPAPI-encrypted at rest (machine
/// scope) so it is never plaintext in a deployed file and cannot be found by a trivial
/// search of the app folder. The blob lives in %ProgramData% — outside the app directory —
/// so an xcopy redeploy never overwrites it. Windows-only; on dev (macOS) the connection
/// string comes from user-secrets / the env var, so this path is not used.
/// </summary>
public static class ProtectedSecret
{
    /// <summary>Machine-wide, survives app redeploys: C:\ProgramData\ClimaSense\ups3.secret.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "ClimaSense", "ups3.secret");

    /// <summary>Decrypt the secret at <paramref name="path"/>; returns null if it is missing or we are not on Windows.</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        var cipher = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Encrypt <paramref name="plaintext"/> to <paramref name="path"/>, machine-bound. Windows only.</summary>
    public static void Write(string path, string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A protected secret can only be written on Windows (DPAPI).");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext),
                                           optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
        File.WriteAllBytes(path, cipher);
    }
}
