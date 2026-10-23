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
        catch
        {
            Sessions = new();
        }

        if (!existed && Sessions.Count == 0)
        {
            Sessions = CreateSeedSessions();
            Save();
        }

        foreach (var session in Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Id)) session.Id = Guid.NewGuid().ToString("N");
            if (session.QuickCommands.Count == 0) session.QuickCommands = SessionProfile.DefaultQuickCommands();
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
        catch
        {
            // best effort
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

    private static List<SessionProfile> CreateSeedSessions()
    {
        var a = SessionProfile.CreateDefault("核心交换机（示例）", "示例设备");
        a.Serial.PortName = "COM3";
        a.Serial.BaudRate = 9600;
        a.InitCommands = "terminal length 0";

        var b = SessionProfile.CreateDefault("接入交换机（示例）", "示例设备");
        b.Serial.PortName = "COM4";
        b.Serial.BaudRate = 9600;

        return new List<SessionProfile> { a, b };
    }
}
