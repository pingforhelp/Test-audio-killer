using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Jellyfin.Plugin.AudioPruner.Services;
using System;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using System.Threading;

namespace Jellyfin.Plugin.AudioPruner.Controllers;

[ApiController]
[Route("AudioPruner")]
public class AudioPrunerController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly ILogger<AudioPrunerController> _log;
    private readonly RemuxService _remux;

    public AudioPrunerController(ILibraryManager library, ILogger<AudioPrunerController> log, RemuxService remux)
    {
        _library = library;
        _log = log;
        _remux = remux;
    }

    private bool IsAdmin() => HttpContext?.User?.IsInRole("Administrator") == true;

    [HttpGet("Tracks/{itemId}")]
    public async Task<IActionResult> GetAudioTracks(string itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Video video) return BadRequest("Item is not a video.");
        if (string.IsNullOrEmpty(video.Path)) return BadRequest("Video path not available.");

        try
        {
            var probe = await _remux.RunFFprobeJsonAsync(video.Path);
            var audio = _remux.GetAudioStreamsFromProbe(probe);
            return Ok(new { video = new { video.Id, video.Name, Path = video.Path }, audio });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ffprobe failed for {Path}", video.Path);
            return Problem(detail: ex.Message, title: "ffprobe failed");
        }
    }

    public record CreateNewRequest(string ItemId, int FfmpegAudioIndex, bool KeepSubs = true, bool KeepChapters = true, bool Backup = true);

    [HttpPost("KeepOnlyCreateNew")]
    public async Task<IActionResult> KeepOnlyCreateNew([FromBody] CreateNewRequest req)
    {
        if (!IsAdmin()) return Forbid();

        var item = _library.GetItemById(req.ItemId) as Video;
        if (item == null || string.IsNullOrEmpty(item.Path)) return BadRequest("invalid item");

        var sem = Services.FileLocks.GetLock(item.Path);
        await sem.WaitAsync();
        try
        {
            var probe = await _remux.RunFFprobeJsonAsync(item.Path);
            var audio = _remux.GetAudioStreamsFromProbe(probe);
            if (!audio.Any(a => ((JsonElement)JsonSerializer.SerializeToElement(a)).GetProperty("ffmpegIndex").GetInt32() == req.FfmpegAudioIndex))
            {
                return BadRequest("ffmpeg audio index not found in file.");
            }

            var outPath = await _remux.KeepOnlyAudioToNewFileAsync(item.Path, req.FfmpegAudioIndex, req.KeepSubs, req.KeepChapters, req.Backup);
            return Ok(new { status = "ok", newFile = outPath });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "KeepOnlyCreateNew failed for {Path}", item.Path);
            return Problem(detail: ex.Message, title: "Remux failed");
        }
        finally
        {
            Services.FileLocks.ReleaseAndCleanup(item.Path, sem);
        }
    }

    public record RestoreRequest(string OriginalItemPath, string BackupPath);

    [HttpPost("Restore")]
    public IActionResult Restore([FromBody] RestoreRequest req)
    {
        if (!IsAdmin()) return Forbid();

        if (!System.IO.File.Exists(req.OriginalItemPath)) return BadRequest("Original file not found.");
        if (!System.IO.File.Exists(req.BackupPath)) return BadRequest("Backup file not found.");

        var sem = Services.FileLocks.GetLock(req.OriginalItemPath);
        sem.Wait();
        try
        {
            _remux.RestoreFromBackup(req.OriginalItemPath, req.BackupPath);
            return Ok(new { status = "ok", message = "Restored from backup." });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed for {Path}", req.OriginalItemPath);
            return Problem(detail: ex.Message, title: "Restore failed");
        }
        finally
        {
            Services.FileLocks.ReleaseAndCleanup(req.OriginalItemPath, sem);
        }

[HttpGet("KeepOnlyCreateNewStream")]
public async Task KeepOnlyCreateNewStream(
    [FromQuery] string itemId,
    [FromQuery] int ffmpegAudioIndex,
    [FromQuery] bool keepSubs = true,
    [FromQuery] bool keepChapters = true,
    [FromQuery] bool backup = true)
{
    if (!IsAdmin())
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsync("Forbidden");
        return;
    }

    var item = _library.GetItemById(itemId) as Video;
    if (item == null || string.IsNullOrEmpty(item.Path))
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        await Response.WriteAsync("Invalid itemId or path not found.");
        return;
    }

    var sem = Services.FileLocks.GetLock(item.Path);
    await sem.WaitAsync();
    try
    {
        var probe = await _remux.RunFFprobeJsonAsync(item.Path);
        var audio = _remux.GetAudioStreamsFromProbe(probe);
        var found = audio.Any(a =>
        {
            var je = (JsonElement)JsonSerializer.SerializeToElement(a);
            return je.GetProperty("ffmpegIndex").GetInt32() == ffmpegAudioIndex;
        });
        if (!found)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("ffmpeg audio index not found in file.");
            return;
        }

        var ffmpegPath = _remux.GetFfmpegExecutablePath();

        var dir = Path.GetDirectoryName(item.Path)!;
        var ext = Path.GetExtension(item.Path);
        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var outName = Path.Combine(dir, $"{stem}.audiopruner-out-{timestamp}{ext}");

        var args = new List<string> { "-y", "-i", item.Path, "-map", "0:v", "-map", $"0:{ffmpegAudioIndex}" };
        if (keepSubs) args.AddRange(new[] { "-map", "0:s?" });
        if (keepChapters) args.AddRange(new[] { "-map_chapters", "0" });
        else args.AddRange(new[] { "-map_chapters", "-1" });
        if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-map", "0:t?" });
        args.AddRange(new[] { "-c", "copy", "-disposition:a", "default", outName });

        Response.Headers.Add("Content-Type", "text/event-stream");
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("X-Accel-Buffering", "no");

        var psi = new ProcessStartInfo(ffmpegPath, string.Join(' ', args.Select(s => s.Contains(' ') ? $""{s}"" : s)))
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");

        var stderr = proc.StandardError;
        while (!stderr.EndOfStream)
        {
            var line = await stderr.ReadLineAsync();
            if (line == null) break;
            var msg = "data: " + line.Replace("\n", "\\n") + "\n\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            await Response.Body.WriteAsync(bytes, 0, bytes.Length);
            await Response.Body.FlushAsync();
        }

        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 || !System.IO.File.Exists(outName))
        {
            var final = $"event: error\ndata: {{"message": "ffmpeg failed (exit {proc.ExitCode})"}}\n\n";
            var b = Encoding.UTF8.GetBytes(final);
            await Response.Body.WriteAsync(b, 0, b.Length);
            await Response.Body.FlushAsync();
            return;
        }

        if (backup)
        {
            var bakName = Path.Combine(dir, $"{stem}.bak-{timestamp}{ext}");
            try { File.Copy(item.Path, bakName, overwrite: false); }
            catch { /* ignore */ }
        }

        var doneJson = JsonSerializer.Serialize(new { newFile = outName });
        var doneMsg = $"event: done\ndata: {doneJson}\n\n";
        var doneBytes = Encoding.UTF8.GetBytes(doneMsg);
        await Response.Body.WriteAsync(doneBytes, 0, doneBytes.Length);
        await Response.Body.FlushAsync();
    }
    finally
    {
        Services.FileLocks.ReleaseAndCleanup(item.Path, sem);
    }
}
    }
}
