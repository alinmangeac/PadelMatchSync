# Padel Match Sync

Padel Match Sync is a mobile-friendly Blazor WebAssembly app backed by an ASP.NET Core API and SQLite. People create their own accounts, save match plans with availability for one date or a date range, and share a private invitation link. Invitees can respond without an account.

## Architecture

- **Web client:** Blazor WebAssembly hosted on GitHub Pages.
- **API:** ASP.NET Core 8 (`Api/`) for account registration, sign-in, match ownership, and public invite responses.
- **Database:** SQLite, persisted on the API host's mounted data disk.
- **Authentication:** Passwords are salted and hashed with PBKDF2-SHA256. API access tokens expire after seven days and are kept in browser local storage.
- **Invitations:** Each match gets a random, nonsequential share code. Anyone holding that link can see the proposed days and submit availability.

## Run locally

In PowerShell, from the repository root:

```powershell
$env:Auth__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:ConnectionStrings__Matches = "Data Source=$((Get-Location).Path)\padel-match-sync.db"
dotnet run --project Api/PadelMatchSync.Api.csproj --urls http://localhost:5209
```

In another terminal:

```powershell
dotnet run --project PadelMatchSync.csproj
```

The API allows the local Blazor development origins configured in `Api/appsettings.json`. The client loads its API address from `wwwroot/appsettings.json`:

```json
"PadelMatchSync": { "ApiBaseUrl": "http://localhost:5209/" }
```

For a containerized API, set `PAD_MATCH_SIGNING_KEY` to at least 32 random characters and run `docker compose up --build`. SQLite is stored in the named `padel-data` volume.

## Deploy

### GitHub Pages client

The existing `pages.yml` workflow publishes the static Blazor app when changes reach `main`. It reads the repository variable `PADEL_MATCH_SYNC_API_URL` and writes it into the published app settings. Configure that variable to the HTTPS base address of the deployed API, including a trailing slash, for example `https://your-api-host.example/`.

### API on Render

`render.yaml` describes a Docker-based ASP.NET Core API service with a persistent disk for SQLite, a generated signing key, and GitHub Pages CORS access. In Render, create a Blueprint from this repository and deploy the `padel-match-sync-api` service. After it is healthy, set the GitHub repository variable `PADEL_MATCH_SYNC_API_URL` to the service's HTTPS URL and rerun the Pages deployment workflow.

The API must be available over HTTPS in production. Keep the signing key private and back up the mounted SQLite file. The Render Blueprint uses a persistent-disk plan so saved account and match data survives service restarts.

## Important scope

This is a starter account system: it does not include email verification, password reset, social login, or invitation permissions beyond possession of the link. Anyone with a match link can view its availability and submit a response. Account owners can list their own matches; deletion and editing are not yet implemented.
