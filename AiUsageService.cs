using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotchApp;

public record AiUsage(double Session, DateTimeOffset? SessionReset, double Week, DateTimeOffset? WeekReset);

// Limites do plano do Claude (sessão de 5 h e semana). Opcional e desligado por padrão:
// usa o login local do Claude Code e só conversa com a própria Anthropic. O token nunca é gravado nem registrado.
static class AiUsageService
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string CredentialsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static bool LoginFound => File.Exists(CredentialsPath);

    // shortcut: usa o endpoint que o próprio Claude Code consulta, que não é documentado; se mudar, o indicador some sem quebrar o resto
    public static async Task<AiUsage> Fetch()
    {
        var token = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath)).RootElement
            .GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"limites do Claude: HTTP {(int)response.StatusCode}");
        var j = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AiUsage(Percent(j, "five_hour"), Reset(j, "five_hour"), Percent(j, "seven_day"), Reset(j, "seven_day"));
    }

    static double Percent(JsonElement j, string window) =>
        j.TryGetProperty(window, out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() : 0;

    static DateTimeOffset? Reset(JsonElement j, string window) =>
        j.TryGetProperty(window, out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("resets_at", out var r)
        && r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var at) ? at : null;
}
