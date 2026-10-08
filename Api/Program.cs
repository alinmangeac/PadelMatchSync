using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (int.TryParse(renderPort, out var port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "http://localhost:5209;https://localhost:7230;https://alinmangeac.github.io")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var signingKey = builder.Configuration["Auth:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Set Auth:SigningKey to a private random value containing at least 32 bytes.");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
var connection = builder.Configuration.GetConnectionString("Matches") ?? "Data Source=padel-match-sync.db";
var postgres = connection.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || connection.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
if (postgres) connection = ParsePostgresUri(connection);
builder.Services.AddDbContext<MatchDb>(options =>
{
    if (postgres) options.UseNpgsql(connection);
    else options.UseSqlite(connection);
});
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MatchDb>().Database;
    if (postgres)
        foreach (var statement in DatabaseSchema.Script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await database.ExecuteSqlRawAsync(statement);
    else await database.EnsureCreatedAsync();
}

app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Padel Match Sync API" }));

app.MapPost("/api/auth/register", async (RegisterRequest request, MatchDb db, IConfiguration config) =>
{
    var name = request.Name?.Trim(); var email = request.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80) return Results.BadRequest(new { error = "Enter a name (up to 80 characters)." });
    if (email is null || email.Length > 254 || !email.Contains('@')) return Results.BadRequest(new { error = "Enter a valid email address." });
    if (request.Password is null || request.Password.Length < 10 || request.Password.Length > 200) return Results.BadRequest(new { error = "Use a password between 10 and 200 characters." });
    if (await db.Users.AnyAsync(user => user.Email == email)) return Results.Conflict(new { error = "An account with this email already exists." });
    var user = new User { Id = Guid.NewGuid(), Name = name, Email = email, PasswordHash = Passwords.Hash(request.Password), CreatedAt = DateTime.UtcNow };
    db.Users.Add(user); await db.SaveChangesAsync();
    return Results.Ok(new AuthResponse(Tokens.Create(user, config), user.Name, user.Email));
});

app.MapPost("/api/auth/login", async (LoginRequest request, MatchDb db, IConfiguration config) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    var user = email is null ? null : await db.Users.SingleOrDefaultAsync(item => item.Email == email);
    if (user is null || request.Password is null || !Passwords.Verify(request.Password, user.PasswordHash)) return Results.Json(new { error = "Email or password is incorrect." }, statusCode: StatusCodes.Status401Unauthorized);
    return Results.Ok(new AuthResponse(Tokens.Create(user, config), user.Name, user.Email));
});

app.MapPost("/api/matches", async (CreateMatchRequest request, HttpRequest http, MatchDb db, IConfiguration config) =>
{
    var user = await Tokens.UserAsync(http, db, config); if (user is null) return Results.Unauthorized();
    var error = ValidateMatch(request); if (error is not null) return Results.BadRequest(new { error });
    var match = new PadelMatch { Id = Guid.NewGuid(), OwnerId = user.Id, Name = request.Name.Trim(), Venue = Clean(request.Venue, 160), ShareCode = Guid.NewGuid().ToString("N")[..20], CreatedAt = DateTime.UtcNow };
    foreach (var day in request.Availability) match.Days.Add(new MatchDay { Date = day.Date, Status = day.Status, From = day.From, Until = day.Until });
    db.Matches.Add(match); await db.SaveChangesAsync();
    return Results.Created($"/api/shared/{match.ShareCode}", new CreatedMatch(match.Id.ToString(), match.Name, match.ShareCode, match.CreatedAt));
});

app.MapGet("/api/matches", async (HttpRequest http, MatchDb db, IConfiguration config) =>
{
    var user = await Tokens.UserAsync(http, db, config); if (user is null) return Results.Unauthorized();
    var matches = await db.Matches.Include(item => item.Days).Include(item => item.Responses).Where(item => item.OwnerId == user.Id).OrderByDescending(item => item.CreatedAt).ToListAsync();
    return Results.Ok(matches.Select(MatchViews.Summary));
});

app.MapGet("/api/shared/{code}", async (string code, MatchDb db) =>
{
    var match = await db.Matches.Include(item => item.Owner).Include(item => item.Days).Include(item => item.Responses).ThenInclude(item => item.Days).SingleOrDefaultAsync(item => item.ShareCode == code);
    return match is null ? Results.NotFound(new { error = "This match link is invalid or no longer available." }) : Results.Ok(MatchViews.Shared(match));
});

app.MapPost("/api/shared/{code}/responses", async (string code, ResponseRequest request, MatchDb db) =>
{
    var match = await db.Matches.Include(item => item.Days).SingleOrDefaultAsync(item => item.ShareCode == code);
    if (match is null) return Results.NotFound(new { error = "This match link is invalid or no longer available." });
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80) return Results.BadRequest(new { error = "Enter your name (up to 80 characters)." });
    var requestedDays = request.Availability ?? [];
    var error = ValidateAvailability(requestedDays, requireStatus: true); if (error is not null) return Results.BadRequest(new { error });
    var allowedDates = match.Days.Select(day => day.Date).ToHashSet();
    if (requestedDays.Any(day => !allowedDates.Contains(day.Date))) return Results.BadRequest(new { error = "Availability must use dates included in the match." });
    var email = Clean(request.Email, 254)?.ToLowerInvariant();
    var response = new PlayerResponse { Id = Guid.NewGuid(), MatchId = match.Id, Name = request.Name.Trim(), Email = email, SubmittedAt = DateTime.UtcNow };
    foreach (var day in requestedDays) response.Days.Add(new ResponseDay { Date = day.Date, Status = day.Status, From = day.From, Until = day.Until });
    db.Responses.Add(response); await db.SaveChangesAsync(); return Results.Ok(new { message = "Availability submitted." });
});

app.Run();

static string? ValidateMatch(CreateMatchRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return "Enter a match name (up to 120 characters).";
    if (string.IsNullOrWhiteSpace(request.Venue) || request.Venue.Length > 160) return "Enter a venue (up to 160 characters).";
    if (request.Availability is null || request.Availability.Count == 0 || request.Availability.Count > 31) return "Choose between 1 and 31 match dates.";
    return ValidateAvailability(request.Availability, requireStatus: true);
}
static string? ValidateAvailability(IReadOnlyList<AvailabilityInput>? days, bool requireStatus)
{
    if (days is null || days.Count == 0 || days.Count > 31) return "Choose between 1 and 31 dates.";
    if (days.Select(day => day.Date).Distinct().Count() != days.Count) return "Each date can only be included once.";
    foreach (var day in days)
    {
        if (day.Status is not ("Available" or "Maybe" or "Unavailable")) return "Choose an availability for every date.";
        if (day.Status is "Available" or "Maybe")
            if (day.From is null || day.Until is null || day.From >= day.Until) return "Set a valid start and end time for each available day.";
    }
    return null;
}
static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
static string ParsePostgresUri(string value)
{
    var uri = new Uri(value);
    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        Username = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]),
        Password = Uri.UnescapeDataString(uri.UserInfo.Contains(':') ? uri.UserInfo.Split(':', 2)[1] : ""),
        SslMode = SslMode.Require,
        Pooling = true,
        MaxPoolSize = 10,
        Timeout = 30,
        CommandTimeout = 30
    }.ConnectionString;
}
static class DatabaseSchema
{
public const string Script = """
CREATE TABLE IF NOT EXISTS "Users" ("Id" uuid PRIMARY KEY, "Name" text NOT NULL, "Email" text NOT NULL, "PasswordHash" text NOT NULL, "CreatedAt" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email" ON "Users" ("Email");
CREATE TABLE IF NOT EXISTS "Matches" ("Id" uuid PRIMARY KEY, "OwnerId" uuid NOT NULL REFERENCES "Users" ("Id") ON DELETE CASCADE, "Name" text NOT NULL, "Venue" text NULL, "ShareCode" text NOT NULL, "CreatedAt" timestamp with time zone NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Matches_ShareCode" ON "Matches" ("ShareCode");
CREATE TABLE IF NOT EXISTS "MatchDays" ("Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, "MatchId" uuid NOT NULL REFERENCES "Matches" ("Id") ON DELETE CASCADE, "Date" date NOT NULL, "Status" text NOT NULL, "From" time without time zone NULL, "Until" time without time zone NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_MatchDays_MatchId_Date" ON "MatchDays" ("MatchId", "Date");
CREATE TABLE IF NOT EXISTS "Responses" ("Id" uuid PRIMARY KEY, "MatchId" uuid NOT NULL REFERENCES "Matches" ("Id") ON DELETE CASCADE, "Name" text NOT NULL, "Email" text NULL, "SubmittedAt" timestamp with time zone NOT NULL);
CREATE TABLE IF NOT EXISTS "ResponseDays" ("Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, "ResponseId" uuid NOT NULL REFERENCES "Responses" ("Id") ON DELETE CASCADE, "Date" date NOT NULL, "Status" text NOT NULL, "From" time without time zone NULL, "Until" time without time zone NULL);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ResponseDays_ResponseId_Date" ON "ResponseDays" ("ResponseId", "Date");
""";
}

record RegisterRequest(string? Name, string? Email, string? Password);
record LoginRequest(string? Email, string? Password);
record ResponseRequest(string? Name, string? Email, List<AvailabilityInput>? Availability);
record CreateMatchRequest(string Name, string? Venue, List<AvailabilityInput> Availability);
record AvailabilityInput(DateOnly Date, string Status, TimeOnly? From, TimeOnly? Until);
record AuthResponse(string Token, string Name, string Email);
record CreatedMatch(string Id, string Name, string ShareCode, DateTime CreatedAt);
record MatchSummary(string Id, string Name, string? Venue, string ShareCode, DateTime CreatedAt, IReadOnlyList<AvailabilityInput> Availability, int ResponseCount);
record SharedMatch(string Name, string? Venue, string OrganizerName, DateTime CreatedAt, IReadOnlyList<AvailabilityInput> Availability, IReadOnlyList<SharedResponse> Responses);
record SharedResponse(string Name, DateTime SubmittedAt, IReadOnlyList<AvailabilityInput> Availability);

static class Passwords
{
    public static string Hash(string password) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32); return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}"; }
    public static bool Verify(string password, string encoded)
    {
        try { var parts = encoded.Split('.'); var salt = Convert.FromBase64String(parts[0]); var expected = Convert.FromBase64String(parts[1]); var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, expected.Length); return CryptographicOperations.FixedTimeEquals(actual, expected); }
        catch (Exception) { return false; }
    }
}
static class Tokens
{
    public static string Create(User user, IConfiguration config)
    {
        var secret = config["Auth:SigningKey"] ?? throw new InvalidOperationException("Auth:SigningKey must be configured with a private random value.");
        if (Encoding.UTF8.GetByteCount(secret) < 32) throw new InvalidOperationException("Auth:SigningKey must contain at least 32 bytes.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new TokenPayload(user.Id, DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()));
        var body = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"{body}.{Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }
    public static async Task<User?> UserAsync(HttpRequest request, MatchDb db, IConfiguration config)
    {
        var parts = request.Headers.Authorization.ToString().Split(' ', 2); if (parts.Length != 2 || parts[0] != "Bearer") return null;
        var token = parts[1].Split('.'); if (token.Length != 2) return null;
        try
        {
            var secret = config["Auth:SigningKey"]; if (string.IsNullOrEmpty(secret)) return null;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)); var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(token[0]));
            var signature = FromBase64Url(token[1]); if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return null;
            var payload = JsonSerializer.Deserialize<TokenPayload>(FromBase64Url(token[0]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is null || payload.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return await db.Users.FindAsync(payload.UserId);
        }
        catch (Exception) { return null; }
    }
    private static byte[] FromBase64Url(string value) { value = value.Replace('-', '+').Replace('_', '/'); value += new string('=', (4 - value.Length % 4) % 4); return Convert.FromBase64String(value); }
    private sealed record TokenPayload(Guid UserId, long ExpiresAt);
}
static class MatchViews
{
    public static MatchSummary Summary(PadelMatch match) => new(match.Id.ToString(), match.Name, match.Venue, match.ShareCode, match.CreatedAt, match.Days.OrderBy(day => day.Date).Select(day => new AvailabilityInput(day.Date, day.Status, day.From, day.Until)).ToList(), match.Responses.Count);
    public static SharedMatch Shared(PadelMatch match) => new(match.Name, match.Venue, match.Owner.Name, match.CreatedAt, match.Days.OrderBy(day => day.Date).Select(day => new AvailabilityInput(day.Date, day.Status, day.From, day.Until)).ToList(), match.Responses.OrderByDescending(response => response.SubmittedAt).Select(response => new SharedResponse(response.Name, response.SubmittedAt, response.Days.OrderBy(day => day.Date).Select(day => new AvailabilityInput(day.Date, day.Status, day.From, day.Until)).ToList())).ToList());
}

sealed class MatchDb(DbContextOptions<MatchDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>(); public DbSet<PadelMatch> Matches => Set<PadelMatch>(); public DbSet<MatchDay> MatchDays => Set<MatchDay>(); public DbSet<PlayerResponse> Responses => Set<PlayerResponse>(); public DbSet<ResponseDay> ResponseDays => Set<ResponseDay>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>().HasIndex(user => user.Email).IsUnique(); model.Entity<PadelMatch>().HasIndex(match => match.ShareCode).IsUnique();
        model.Entity<PadelMatch>().HasOne(match => match.Owner).WithMany().HasForeignKey(match => match.OwnerId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MatchDay>().HasOne(day => day.Match).WithMany(match => match.Days).HasForeignKey(day => day.MatchId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<PlayerResponse>().HasOne(response => response.Match).WithMany(match => match.Responses).HasForeignKey(response => response.MatchId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ResponseDay>().HasOne(day => day.Response).WithMany(response => response.Days).HasForeignKey(day => day.ResponseId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MatchDay>().HasIndex(day => new { day.MatchId, day.Date }).IsUnique(); model.Entity<ResponseDay>().HasIndex(day => new { day.ResponseId, day.Date }).IsUnique();
    }
}
sealed class User { public Guid Id { get; set; } public string Name { get; set; } = ""; public string Email { get; set; } = ""; public string PasswordHash { get; set; } = ""; public DateTime CreatedAt { get; set; } }
sealed class PadelMatch { public Guid Id { get; set; } public Guid OwnerId { get; set; } public User Owner { get; set; } = null!; public string Name { get; set; } = ""; public string? Venue { get; set; } public string ShareCode { get; set; } = ""; public DateTime CreatedAt { get; set; } public List<MatchDay> Days { get; set; } = []; public List<PlayerResponse> Responses { get; set; } = []; }
sealed class MatchDay { public int Id { get; set; } public Guid MatchId { get; set; } public PadelMatch Match { get; set; } = null!; public DateOnly Date { get; set; } public string Status { get; set; } = ""; public TimeOnly? From { get; set; } public TimeOnly? Until { get; set; } }
sealed class PlayerResponse { public Guid Id { get; set; } public Guid MatchId { get; set; } public PadelMatch Match { get; set; } = null!; public string Name { get; set; } = ""; public string? Email { get; set; } public DateTime SubmittedAt { get; set; } public List<ResponseDay> Days { get; set; } = []; }
sealed class ResponseDay { public int Id { get; set; } public Guid ResponseId { get; set; } public PlayerResponse Response { get; set; } = null!; public DateOnly Date { get; set; } public string Status { get; set; } = ""; public TimeOnly? From { get; set; } public TimeOnly? Until { get; set; } }
