using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioPruner.Services;

public class RemuxService
{
    private readonly IServerConfigurationManager _cfg;
    private readonly ILogger<RemuxService> _log;

    public RemuxService(IServerConfigurationManager cfg, ILogger<RemuxService> log)
    {
        _cfg = cfg;
        _log = log;
    }

    private string ResolveFfmpegPath(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("FFmpeg path not configured in Jellyfin.");

        if (Directory.Exists(configured))
        {
            var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var p = Path.Combine(configured, exe);
            if (File.Exists(p)) return p;
        }

        if (File.Exists(configured)) return configured;

        throw new FileNotFoundException("FFmpeg binary not found at configured path", configured);
    }

    private string ResolveFfprobePath(string configured)
    {
        var ffmpeg = ResolveFfmpegPath(configured);
        var dir = Path.GetDirectoryName(ffmpeg)!;
        var probeExe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var probePath = Path.Combine(dir, probeExe);
        if (File.Exists(probePath)) return probePath;
        if (File.Exists(configured)) return configured;
        throw new FileNotFoundException("ffprobe not found next to ffmpeg", probePath);
    }

    private static string QuoteIfNeeded(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public async Task<JsonDocument> RunFFprobeJsonAsync(string file)
    {
        var ffprobe = ResolveFfprobePath(_cfg.ApplicationConfiguration.FFmpegPath);
        var psi = new ProcessStartInfo(ffprobe,
            $"-v error -print_format json -show_streams -show_format {QuoteIfNeeded(file)}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe");
        var outStr = await p.StandardOutput.ReadToEndAsync();
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new Exception("ffprobe failed: " + err);
        return JsonDocument.Parse(outStr);
    }

    public List<object> GetAudioStreamsFromProbe(JsonDocument probe)
    {
        var res = new List<object>();
        if (!probe.RootElement.TryGetProperty("streams", out var streams)) return res;
        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "audio") continue;
            var idx = s.GetProperty("index").GetInt32();
            string lang = "";
            string title = "";
            string codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
            string ch = s.TryGetProperty("channels", out var chn) ? chn.GetInt32().ToString() : "";
            if (s.TryGetProperty("tags", out var tags))
            {
                if (tags.TryGetProperty("language", out var l)) lang = l.GetString() ?? "";
                if (tags.TryGetProperty("title", out var tt)) title = tt.GetString() ?? "";
            }
            res.Add(new { ffmpegIndex = idx, language = lang, title, codec, channels = ch });
        }
        return res;
    }

    public async Task<string> KeepOnlyAudioToNewFileAsync(string inputPath, int ffmpegAudioIndex, bool keepSubs, bool keepChapters, bool backup)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Input not found", inputPath);

        var ffmpeg = ResolveFfmpegPath(_cfg.ApplicationConfiguration.FFmpegPath);
        var dir = Path.GetDirectoryName(inputPath)!;
        var ext = Path.GetExtension(inputPath);
        var stem = Path.GetFileNameWithoutExtension(inputPath);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var outName = Path.Combine(dir, $"{stem}.audiopruner-out-{timestamp}{ext}");

        var args = new List<string> { "-y", "-i", inputPath, "-map", "0:v", "-map", $"0:{ffmpegAudioIndex}" };
        if (keepSubs) args.AddRange(new[] { "-map", "0:s?" });
        if (keepChapters) args.AddRange(new[] { "-map_chapters", "0" });
        else args.AddRange(new[] { "-map_chapters", "-1" });
        if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-map", "0:t?" });

        args.AddRange(new[] { "-c", "copy", "-disposition:a", "default", outName });

        _log.LogInformation("Running ffmpeg: {Cmd}", string.Join(' ', args.Select(QuoteIfNeeded)));

        var psi = new ProcessStartInfo(ffmpeg, string.Join(' ', args.Select(QuoteIfNeeded)))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
        var stderr = await proc.StandardError.ReadToEndAsync();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 || !File.Exists(outName))
        {
            _log.LogError("ffmpeg failed: {Err}", stderr);
            try { if (File.Exists(outName)) File.Delete(outName); } catch { }
            throw new Exception("ffmpeg remux failed: " + (stderr.Length > 0 ? stderr : stdout));
        }

        if (backup)
        {
            var bakName = Path.Combine(dir, $"{stem}.bak-{timestamp}{ext}");
            File.Copy(inputPath, bakName, overwrite: false);
            _log.LogInformation("Backup created: {Bak}", bakName);
        }

        return outName;
    }

    public void RestoreFromBackup(string originalPath, string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found", backupPath);
        if (!File.Exists(originalPath)) throw new FileNotFoundException("Original file not found", originalPath);

        var tomb = originalPath + ".origdelete";
        File.Move(originalPath, tomb);
        File.Move(backupPath, originalPath);
        try { if (File.Exists(tomb)) File.Delete(tomb); } catch { }
        _log.LogInformation("Restored {Original} from {Backup}", originalPath, backupPath);
    }


    // Public helper for controllers
    public string GetFfmpegExecutablePath()
    {
        return ResolveFfmpegPath(_cfg.ApplicationConfiguration.FFmpegPath);
    }
}
