FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/Audit.Worker/Audit.Worker.csproj", "src/Audit.Worker/"]
RUN dotnet restore "src/Audit.Worker/Audit.Worker.csproj"

COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/Audit.Worker/", "src/Audit.Worker/"]

WORKDIR "/src/src/Audit.Worker"
RUN dotnet publish "Audit.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Audit.Worker.dll"]
