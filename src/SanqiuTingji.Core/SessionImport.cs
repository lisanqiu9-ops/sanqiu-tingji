using System.Text.Json;

namespace SanqiuTingji;

public sealed partial class SessionStore
{
    public string Import(Session session)
    {
        if (session.EndedAt == null || session.EndedAt < session.StartedAt || session.Lines.Count == 0)
            throw new ArgumentException("A completed transcript is required", nameof(session));
        // Copy before publishing so the decoder cannot keep changing the saved record.
        var copy = JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(session, Json.Options), Json.Options)!;
        copy.Id = Guid.NewGuid().ToString("N");
        var folder = Folder(copy.Id);
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "session.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(copy, Json.Options));
            File.Move(file + ".tmp", file);
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
        lock (gate)
        {
            sessions.Add(copy);
            sessions.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        }
        Changed?.Invoke();
        return copy.Id;
    }

    /// Creates a placeholder session for an audio recording recovered after an unexpected app crash.
    public string CreateRecoveredAudioSession(string preferredId, string title, DateTime startedAt, double durationSeconds)
    {
        var id = string.IsNullOrWhiteSpace(preferredId) ? Guid.NewGuid().ToString("N") : preferredId;
        Session session;
        lock (gate)
        {
            if (sessions.FirstOrDefault(x => x.Id == id) is { } existing) return existing.Id;
            session = new Session
            {
                Id = id,
                Title = title,
                Renamed = true,
                StartedAt = startedAt.ToUniversalTime(),
                EndedAt = startedAt.ToUniversalTime().AddSeconds(Math.Max(1, durationSeconds)),
                HasAudio = true,
            };
            sessions.Add(session);
            sessions.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        }
        SaveNow(session.Id);
        Changed?.Invoke();
        return session.Id;
    }
}
