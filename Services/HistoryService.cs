using TransitLab.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TransitLab.Services;

public static class HistoryService
{
    private static readonly string HistoryPath =
        Path.Combine(ConfigService.AppDataDir, "history.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<HistoryEntry> Load()
    {
        Directory.CreateDirectory(ConfigService.AppDataDir);

        // One-time migration from old exe-directory location
        var oldPath = Path.Combine(AppContext.BaseDirectory, "history.json");
        if (!File.Exists(HistoryPath) && File.Exists(oldPath))
            try { File.Copy(oldPath, HistoryPath); } catch { }

        if (!File.Exists(HistoryPath)) return [];
        try
        {
            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[History] ERROR loading history.json: {ex.Message}");
            return [];
        }
    }

    public static void Save(IEnumerable<HistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppDataDir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, JsonOpts));
        }
        catch (Exception ex)
        {
            SessionLogService.Write($"[History] ERROR saving history.json: {ex.Message}");
        }
    }
}
