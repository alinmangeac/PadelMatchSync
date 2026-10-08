FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Api/PadelMatchSync.Api.csproj ./
RUN dotnet restore PadelMatchSync.Api.csproj
COPY Api/ ./
RUN dotnet publish PadelMatchSync.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PadelMatchSync.Api.dll"]
