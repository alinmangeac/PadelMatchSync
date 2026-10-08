# Rally

Rally is a mobile-friendly padel match-day planner built with C# and Blazor WebAssembly. It runs as a static site on GitHub Pages.

## Run locally

```powershell
dotnet run
```

## Deploy

Push to `main` to publish the app through the GitHub Pages workflow in `.github/workflows/pages.yml`. In the repository settings, set **Pages → Build and deployment → Source** to **GitHub Actions**.

The current dashboard uses sample match information. New match planning and checklist interactions are held in browser memory and are not saved between sessions.
