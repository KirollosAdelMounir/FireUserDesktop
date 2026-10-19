using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UserIdentityDesktop.Data;

/// <summary>Unwrap the backend response once. Never include raw response bodies in errors.</summary>
public static class ApiPayload
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var value = document.RootElement;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("status", out var status) && status.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (!status.GetBoolean() || !response.IsSuccessStatusCode)
            {
                var message = value.TryGetProperty("errorText", out var error) ? error.GetString() : "Request failed.";
                var trace = value.TryGetProperty("traceId", out var id) ? id.GetString() : null;
                throw new HttpRequestException($"{message ?? "Request failed."}{(trace is null ? "" : $" (Trace: {trace})")}", null, response.StatusCode);
            }
            value = value.GetProperty("data");
        }
        response.EnsureSuccessStatusCode();
        return value.Deserialize<T>(Json) ?? throw new HttpRequestException("The server returned an empty response.");
    }
}
