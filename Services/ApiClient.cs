using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace PadelMatchSync.Services;

public sealed class ApiClient(HttpClient http, IJSRuntime js)
{
    private const string TokenKey = "padel-match-sync-token";
    private const string NameKey = "padel-match-sync-name";
    private string? token;
    public string? DisplayName { get; private set; }
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(token);
    private string ApiBase => (http.BaseAddress?.ToString() ?? "").TrimEnd('/');
    private HttpClient Client()
    {
        var client = new HttpClient { BaseAddress = new Uri(ApiBase + "/") };
        if (IsSignedIn) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    public async Task RestoreAsync()
    {
        try { token = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey); DisplayName = await js.InvokeAsync<string?>("localStorage.getItem", NameKey); }
        catch (InvalidOperationException) { }
    }
    public async Task<AuthResult> RegisterAsync(string name, string email, string password) => await AuthenticateAsync("api/auth/register", new { name, email, password });
    public async Task<AuthResult> LoginAsync(string email, string password) => await AuthenticateAsync("api/auth/login", new { email, password });
    private async Task<AuthResult> AuthenticateAsync(string path, object request)
    {
        using var client = new HttpClient { BaseAddress = new Uri(ApiBase + "/") };
        using var response = await client.PostAsJsonAsync(path, request);
        if (!response.IsSuccessStatusCode) return new(false, await ReadError(response));
        var result = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (result is null) return new(false, "The server returned an empty response.");
        token = result.Token; DisplayName = result.Name;
        await js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        await js.InvokeVoidAsync("localStorage.setItem", NameKey, DisplayName);
        return new(true, "Signed in.");
    }
    public async Task SignOutAsync()
    {
        token = null; DisplayName = null;
        await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("localStorage.removeItem", NameKey);
    }
    public async Task<(bool Ok, string Message, CreatedMatch? Match)> CreateMatchAsync(CreateMatchRequest request)
    {
        using var response = await Client().PostAsJsonAsync("api/matches", request);
        if (!response.IsSuccessStatusCode) return (false, await ReadError(response), null);
        return (true, "Match created.", await response.Content.ReadFromJsonAsync<CreatedMatch>());
    }
    public async Task<(bool Ok, string Message, IReadOnlyList<MatchSummary> Matches)> GetMatchesAsync()
    {
        using var response = await Client().GetAsync("api/matches");
        if (!response.IsSuccessStatusCode) return (false, await ReadError(response), Array.Empty<MatchSummary>());
        return (true, "", await response.Content.ReadFromJsonAsync<List<MatchSummary>>() ?? []);
    }
    public async Task<(bool Ok, string Message, SharedMatchDetails? Match)> GetSharedMatchAsync(string code)
    {
        using var response = await Client().GetAsync($"api/shared/{Uri.EscapeDataString(code)}");
        if (!response.IsSuccessStatusCode) return (false, await ReadError(response), null);
        return (true, "", await response.Content.ReadFromJsonAsync<SharedMatchDetails>());
    }
    public async Task<(bool Ok, string Message)> RespondAsync(string code, string name, string? email, IReadOnlyList<AvailabilityInput> availability)
    {
        using var response = await Client().PostAsJsonAsync($"api/shared/{Uri.EscapeDataString(code)}/responses", new { name, email, availability });
        return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Availability sent!" : await ReadError(response));
    }
    private static async Task<string> ReadError(HttpResponseMessage response)
    {
        try { return (await response.Content.ReadFromJsonAsync<ApiError>())?.Error ?? $"Request failed ({(int)response.StatusCode})."; }
        catch { return $"Request failed ({(int)response.StatusCode})."; }
    }
}

public sealed record AuthResult(bool Ok, string Message);
public sealed record AuthResponse(string Token, string Name, string Email);
public sealed record ApiError(string Error);
public sealed record AvailabilityInput(DateOnly Date, string Status, TimeOnly? From, TimeOnly? Until);
public sealed record CreateMatchRequest(string Name, string? Venue, IReadOnlyList<AvailabilityInput> Availability);
public sealed record CreatedMatch(string Id, string Name, string ShareCode, DateTime CreatedAt);
public sealed record MatchSummary(string Id, string Name, string? Venue, string ShareCode, DateTime CreatedAt, IReadOnlyList<AvailabilityInput> Availability, int ResponseCount);
public sealed record SharedMatchDetails(string Name, string? Venue, string OrganizerName, DateTime CreatedAt, IReadOnlyList<AvailabilityInput> Availability, IReadOnlyList<SharedResponse> Responses);
public sealed record SharedResponse(string Name, DateTime SubmittedAt, IReadOnlyList<AvailabilityInput> Availability);
