using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SoundTaskbar.Services;

public readonly record struct AppVolumeState(bool Found, float Volume, bool IsMuted)
{
    public static AppVolumeState Missing { get; } = new(false, 0, false);
}

/// <summary>
/// Per-process volume via Windows Core Audio sessions (not master volume).
/// Matches SMTC SourceAppUserModelId to an audio session.
/// </summary>
public sealed class AppVolumeService : IDisposable
{
    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _disposed;

    public AppVolumeState GetState(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return AppVolumeState.Missing;
        }

        lock (_gate)
        {
            EnsureDevice();
            using var session = FindBestSession(appId);
            if (session is null)
            {
                return AppVolumeState.Missing;
            }

            return new AppVolumeState(
                Found: true,
                Volume: session.SimpleAudioVolume.Volume,
                IsMuted: session.SimpleAudioVolume.Mute);
        }
    }

    public bool TrySetVolume(string? appId, float volume)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        volume = Math.Clamp(volume, 0f, 1f);

        lock (_gate)
        {
            EnsureDevice();
            using var session = FindBestSession(appId);
            if (session is null)
            {
                return false;
            }

            session.SimpleAudioVolume.Volume = volume;
            if (volume > 0.001f && session.SimpleAudioVolume.Mute)
            {
                session.SimpleAudioVolume.Mute = false;
            }

            return true;
        }
    }

    public bool TryToggleMute(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        lock (_gate)
        {
            EnsureDevice();
            using var session = FindBestSession(appId);
            if (session is null)
            {
                return false;
            }

            session.SimpleAudioVolume.Mute = !session.SimpleAudioVolume.Mute;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    private void EnsureDevice()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enumerator ??= new MMDeviceEnumerator();
        _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _device.AudioSessionManager.RefreshSessions();
    }

    private AudioSessionControl? FindBestSession(string appId)
    {
        if (_device is null)
        {
            return null;
        }

        var tokens = BuildTokens(appId);
        AudioSessionControl? best = null;
        var bestScore = -1f;

        var sessions = _device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            try
            {
                // 0=Inactive, 1=Active, 2=Expired (NAudio AudioSessionState)
                var state = (int)session.State;
                if (state == 2)
                {
                    session.Dispose();
                    continue;
                }

                var pid = session.GetProcessID;
                if (pid == 0)
                {
                    session.Dispose();
                    continue;
                }

                if (!TryDescribe(pid, session, out var processName, out var displayName))
                {
                    session.Dispose();
                    continue;
                }

                if (!IsMatch(tokens, appId, processName, displayName))
                {
                    session.Dispose();
                    continue;
                }

                // Prefer active / louder sessions when one app has several (browsers).
                var score = 0f;
                if (state == 1)
                {
                    score += 2f;
                }

                try
                {
                    score += session.AudioMeterInformation.MasterPeakValue;
                }
                catch
                {
                    // ignore meter failures
                }

                if (score >= bestScore)
                {
                    best?.Dispose();
                    best = session;
                    bestScore = score;
                }
                else
                {
                    session.Dispose();
                }
            }
            catch
            {
                try { session.Dispose(); } catch { /* ignore */ }
            }
        }

        return best;
    }

    private static bool TryDescribe(
        uint pid,
        AudioSessionControl session,
        out string processName,
        out string displayName)
    {
        processName = string.Empty;
        displayName = session.DisplayName ?? string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName ?? string.Empty;
            return !string.IsNullOrWhiteSpace(processName);
        }
        catch
        {
            return !string.IsNullOrWhiteSpace(displayName);
        }
    }

    private static List<string> BuildTokens(string appId)
    {
        var tokens = new List<string>();
        var raw = appId.Trim();
        tokens.Add(raw);

        var withoutExe = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? raw[..^4]
            : raw;
        tokens.Add(withoutExe);

        if (withoutExe.Contains('!', StringComparison.Ordinal))
        {
            tokens.Add(withoutExe.Split('!')[^1]);
        }

        if (withoutExe.Contains('_', StringComparison.Ordinal))
        {
            tokens.Add(withoutExe.Split('_')[0]);
        }

        var dotted = tokens[^1];
        if (dotted.Contains('.', StringComparison.Ordinal))
        {
            tokens.Add(dotted.Split('.')[^1]);
        }

        return tokens
            .Where(t => t.Length >= 2)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static bool IsMatch(List<string> tokens, string appId, string processName, string displayName)
    {
        var process = processName.ToLowerInvariant();
        var display = displayName.ToLowerInvariant();
        var id = appId.ToLowerInvariant();

        if (id.Contains(process, StringComparison.Ordinal) || process.Contains(StripExe(id), StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (process.Contains(token, StringComparison.Ordinal) ||
                token.Contains(process, StringComparison.Ordinal) ||
                display.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripExe(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}
