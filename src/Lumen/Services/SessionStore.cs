using System.IO;
using System.Text;
using System.Text.Json;
using Lumen.Models;

namespace Lumen.Services;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public List<SessionProfile> Sessions { get; private set; } = new();

    public void Load()
    {
        bool existed = File.Exists(AppPaths.SessionsFile);
        try
        {
            if (existed)
            {
                var json = File.ReadAllText(AppPaths.SessionsFile, Encoding.UTF8);
                Sessions = JsonSerializer.Deserialize<List<SessionProfile>>(json, JsonOptions) ?? new();
            }
        }
        catch (Exception ex)
        {
            App.LogError("session load", ex);
            Sessions = new();
        }

        if (!existed && Sessions.Count == 0)
        {
            Sessions = CreateSeedSessions();
            Save();
        }

        Sessions.RemoveAll(session => session is null);
        foreach (var session in Sessions)
        {
            session.Normalize();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var json = JsonSerializer.Serialize(Sessions, JsonOptions);
            var tmp = AppPaths.SessionsFile + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, AppPaths.SessionsFile, true);
        }
        catch (Exception ex)
        {
            App.LogError("session save", ex);
        }
    }

    public void Add(SessionProfile profile)
    {
        Sessions.Add(profile);
        Save();
    }

    public void Update(SessionProfile profile)
    {
        var index = Sessions.FindIndex(s => s.Id == profile.Id);
        if (index >= 0) Sessions[index] = profile;
        else Sessions.Add(profile);
        Save();
    }

    public void Remove(SessionProfile profile)
    {
        Sessions.RemoveAll(s => s.Id == profile.Id);
        Save();
    }

    public void Export(string path)
    {
        var json = JsonSerializer.Serialize(Sessions, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public List<SessionProfile> Import(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var imported = JsonSerializer.Deserialize<List<SessionProfile>>(json, JsonOptions) ?? new();
        imported.RemoveAll(profile => profile is null);

        // Validate/normalize the whole file before mutating the store, so a bad
        // entry cannot leave the persisted list half-imported.
        var prepared = new List<SessionProfile>(imported.Count);
        foreach (var profile in imported)
        {
            profile.Normalize();
            if (Sessions.Any(existing => existing.Id == profile.Id) ||
                prepared.Any(existing => existing.Id == profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "导入的会话";
            prepared.Add(profile);
        }

        foreach (var profile in prepared) Sessions.Add(profile);
        if (prepared.Count > 0) Save();
        return prepared;
    }

    private static List<SessionProfile> CreateSeedSessions() => new();
}
