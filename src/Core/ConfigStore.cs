using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace APMultiTracker;

/// <summary>Config lives in %APPDATA%\APMultiTracker. Tokens and room passwords are encrypted with DPAPI.</summary>
public static class ConfigStore
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APMultiTracker");

    private static string ConfigPath => Path.Combine(Dir, "config.json");
    private static string TokenPath => Path.Combine(Dir, "tokens.dat");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool ReadOnly { get; set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Opts) ?? new AppConfig();
                foreach (var mw in cfg.Multiworlds) mw.Password = Unseal(mw.Password);
                return cfg;
            }
        }
        catch
        {
            try { File.Copy(ConfigPath, ConfigPath + ".broken", overwrite: true); } catch { }
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        if (ReadOnly) return;
        Directory.CreateDirectory(Dir);
        // Room passwords are written encrypted; the in-memory config keeps them plain.
        var plain = new string[config.Multiworlds.Count];
        for (int i = 0; i < plain.Length; i++)
        {
            plain[i] = config.Multiworlds[i].Password;
            config.Multiworlds[i].Password = Seal(plain[i]);
        }
        try
        {
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, Opts));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        finally
        {
            for (int i = 0; i < plain.Length; i++) config.Multiworlds[i].Password = plain[i];
        }
    }

    private static string Seal(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return "dpapi:" + Convert.ToBase64String(Native.Protect(System.Text.Encoding.UTF8.GetBytes(s))); }
        catch { return ""; }
    }

    private static string Unseal(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("dpapi:")) return s ?? "";
        try { return System.Text.Encoding.UTF8.GetString(Native.Unprotect(Convert.FromBase64String(s[6..]))); }
        catch { return ""; }
    }

    public static TokenSet? LoadTokens()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            return JsonSerializer.Deserialize<TokenSet>(Native.Unprotect(File.ReadAllBytes(TokenPath)));
        }
        catch { return null; }
    }

    public static void SaveTokens(TokenSet? tokens)
    {
        if (ReadOnly) return;
        try
        {
            if (tokens == null)
            {
                if (File.Exists(TokenPath)) File.Delete(TokenPath);
                return;
            }
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(TokenPath, Native.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens)));
        }
        catch { }
    }
}
