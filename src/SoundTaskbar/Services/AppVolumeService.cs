using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SoundTaskbar.Services;

public readonly record struct AppVolumeState(bool Found, float Volume, bool IsMuted, float Peak)
{
    public static AppVolumeState Missing { get; } = new(false, 0, false, 0);
}

/// <summary>
/// Per-process volume via Windows Core Audio sessions (not master volume).
/// Matches SMTC SourceAppUserModelId to audio sessions; writes apply to all matches.
/// </summary>
public sealed class AppVolumeService : IDisposable
{
    private const int ProcessNameCacheMs = 500;
    private const int SessionRefreshMs = 400;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string[]> _tokenCache =
        new(StringComparer.OrdinalIgnoreCase);
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private long _lastRefreshTicks;
    private long _processNamesTicks;
    private HashSet<string>? _processNames;
    private bool _disposed;

    public AppVolumeState GetState(string? appId, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return AppVolumeState.Missing;
        }

        lock (_gate)
        {
            EnsureDevice(forceRefresh);
            using var matches = FindMatchingSessions(appId);
            if (matches.Count == 0)
            {
                return AppVolumeState.Missing;
            }

            AudioSessionControl? best = null;
            var bestScore = float.MinValue;
            var maxPeak = 0f;

            foreach (var session in matches)
            {
                var peak = ReadPeak(session);
                if (peak > maxPeak)
                {
                    maxPeak = peak;
                }

                var score = Score(session, peak);
                if (score >= bestScore)
                {
                    bestScore = score;
                    best = session;
                }
            }

            if (best is null)
            {
                return AppVolumeState.Missing;
            }

            return new AppVolumeState(
                Found: true,
                Volume: best.SimpleAudioVolume.Volume,
                IsMuted: best.SimpleAudioVolume.Mute,
                Peak: Math.Clamp(maxPeak, 0f, 1f));
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
            EnsureDevice(forceRefresh: true);
            using var matches = FindMatchingSessions(appId);
            if (matches.Count == 0)
            {
                return false;
            }

            foreach (var session in matches)
            {
                session.SimpleAudioVolume.Volume = volume;
                if (volume > 0.001f && session.SimpleAudioVolume.Mute)
                {
                    session.SimpleAudioVolume.Mute = false;
                }
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
            EnsureDevice(forceRefresh: true);
            using var matches = FindMatchingSessions(appId);
            if (matches.Count == 0)
            {
                return false;
            }

            // Toggle from the primary session, then mirror to every match.
            var muted = !matches[0].SimpleAudioVolume.Mute;
            foreach (var session in matches)
            {
                session.SimpleAudioVolume.Mute = muted;
            }

            return true;
        }
    }

    /// <summary>
    /// True if a process matching the SMTC AppId is still running (parked-chip eviction).
    /// Process-name snapshot is cached ~500ms — callers poll often.
    /// </summary>
    public bool IsAppRunning(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        var tokens = GetTokens(appId);
        var names = GetCachedProcessNames();
        if (names is null)
        {
            return true; // fail open — keep chip until SMTC drops it
        }

        foreach (var name in names)
        {
            if (IsMatch(tokens, appId, name, string.Empty))
            {
                return true;
            }
        }

        return false;
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
            _processNames = null;
        }
    }

    private HashSet<string>? GetCachedProcessNames()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_processNames is not null && now - _processNamesTicks < ProcessNameCacheMs)
            {
                return _processNames;
            }
        }

        HashSet<string> names;
        try
        {
            var processes = Process.GetProcesses();
            names = new HashSet<string>(processes.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var process in processes)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
                catch
                {
                    // process exited mid-enumeration
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return null;
        }

        lock (_gate)
        {
            _processNames = names;
            _processNamesTicks = now;
            return _processNames;
        }
    }

    private void EnsureDevice(bool forceRefresh = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enumerator ??= new MMDeviceEnumerator();
        _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        var now = Environment.TickCount64;
        if (forceRefresh || now - _lastRefreshTicks >= SessionRefreshMs)
        {
            _device.AudioSessionManager.RefreshSessions();
            _lastRefreshTicks = now;
        }
    }

    private SessionList FindMatchingSessions(string appId)
    {
        var list = new SessionList();
        if (_device is null)
        {
            return list;
        }

        var tokens = GetTokens(appId);
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

                if (!TryDescribe(pid, session, out var processName, out var displayName) ||
                    !IsMatch(tokens, appId, processName, displayName))
                {
                    session.Dispose();
                    continue;
                }

                list.Add(session);
            }
            catch
            {
                try { session.Dispose(); } catch { /* ignore */ }
            }
        }

        return list;
    }

    private static float Score(AudioSessionControl session, float peak)
    {
        var score = peak;
        try
        {
            if ((int)session.State == 1)
            {
                score += 2f;
            }
        }
        catch
        {
            // ignore
        }

        return score;
    }

    private static float ReadPeak(AudioSessionControl session)
    {
        try
        {
            return Math.Clamp(session.AudioMeterInformation.MasterPeakValue, 0f, 1f);
        }
        catch
        {
            return 0f;
        }
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

    private string[] GetTokens(string appId) =>
        _tokenCache.GetOrAdd(appId, static id => BuildTokens(id));

    private static string[] BuildTokens(string appId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = appId.Trim();
        AddToken(set, raw);

        var withoutExe = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? raw[..^4]
            : raw;
        AddToken(set, withoutExe);

        if (withoutExe.Contains('!', StringComparison.Ordinal))
        {
            AddToken(set, withoutExe.Split('!')[^1]);
        }

        if (withoutExe.Contains('_', StringComparison.Ordinal))
        {
            AddToken(set, withoutExe.Split('_')[0]);
        }

        var dotted = withoutExe.Contains('!', StringComparison.Ordinal)
            ? withoutExe.Split('!')[^1]
            : withoutExe;
        if (dotted.Contains('_', StringComparison.Ordinal))
        {
            dotted = dotted.Split('_')[0];
        }

        if (dotted.Contains('.', StringComparison.Ordinal))
        {
            AddToken(set, dotted.Split('.')[^1]);
        }

        return set.ToArray();
    }

    private static void AddToken(HashSet<string> set, string token)
    {
        if (token.Length >= 2)
        {
            set.Add(token);
        }
    }

    private static bool IsMatch(string[] tokens, string appId, string processName, string displayName)
    {
        if (processName.Length == 0 && displayName.Length == 0)
        {
            return false;
        }

        if (appId.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
            processName.Contains(StripExe(appId), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var token in tokens)
        {
            if (processName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                token.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
                (displayName.Length > 0 && displayName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripExe(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private sealed class SessionList : List<AudioSessionControl>, IDisposable
    {
        public void Dispose()
        {
            foreach (var session in this)
            {
                try { session.Dispose(); } catch { /* ignore */ }
            }

            Clear();
        }
    }
}
