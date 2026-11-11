using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrontDeskKiosk;

/// <summary>
/// Settings saved to %LOCALAPPDATA%\FrontDeskKiosk\settings.json.
/// Passwords are never stored, only salted PBKDF2 hashes.
/// Deleting settings.json resets the app to first-time setup (see README, "Plan B").
/// </summary>
public sealed class KioskConfig
{
    public string Url { get; set; } = "";
    public bool AutoStart { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string RecoveryHash { get; set; } = "";
    public string RecoverySalt { get; set; } = "";

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrontDeskKiosk");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public bool HasPassword => PasswordHash.Length > 0 && PasswordSalt.Length > 0;
    public bool HasRecoveryCode => RecoveryHash.Length > 0 && RecoverySalt.Length > 0;
    public bool IsSetUp => HasPassword && IsValidUrl(Url);

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public static KioskConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<KioskConfig>(File.ReadAllText(FilePath)) ?? new KioskConfig();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings, starting fresh: " + ex.Message);
        }
        return new KioskConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void SetPassword(string password) => (PasswordSalt, PasswordHash) = Hash(password);

    public bool CheckPassword(string input) => Verify(input, PasswordSalt, PasswordHash);

    public bool CheckRecoveryCode(string input) => Verify(NormalizeCode(input), RecoverySalt, RecoveryHash);

    /// <summary>Creates a new recovery code (replacing any old one) and returns it so it can be shown once.</summary>
    public string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O or 1/I lookalikes
        var chars = new char[12];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var code = new string(chars);
        (RecoverySalt, RecoveryHash) = Hash(code);
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    static string NormalizeCode(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    const int Iterations = 200_000;

    static (string salt, string hash) Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    static bool Verify(string input, string salt, string hash)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(hash)) return false;
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(input), Convert.FromBase64String(salt),
                Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}

static class Log
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(KioskConfig.Dir);
            var path = Path.Combine(KioskConfig.Dir, "kiosk.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the kiosk down
        }
    }
}
