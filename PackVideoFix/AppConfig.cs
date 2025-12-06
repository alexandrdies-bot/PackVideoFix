using System;
using System.IO;
using System.Text.Json;

namespace PackVideoFix;

public sealed class AppConfig
{
    // TEMP (только локально)
    public string TempRootLocal { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackVideoFix", "Temp");

    // Куда сохраняем итоговые видео/мета (можно сетевой/NAS)
    public string RecordRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PackRecords");

    public string StationName { get; set; } = "PACK-01";
    public int CameraIndex { get; set; } = 0;

    // 0 = выключить, иначе авто-стоп по времени
    public int MaxClipSeconds { get; set; } = 0;

    // Ozon (пока только хранение)
    public string? OzonClientId { get; set; } = "";
    public string? OzonApiKey { get; set; } = "";
    public string? OzonBaseUrl { get; set; } = "https://api-seller.ozon.ru";

    public static AppConfig Default() => new();

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return Default();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? Default();
        }
        catch
        {
            return Default();
        }
    }

    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
