FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/Auction.Hub/Auction.Hub.csproj", "src/Auction.Hub/"]
RUN dotnet restore "src/Auction.Hub/Auction.Hub.csproj"

COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/Auction.Hub/", "src/Auction.Hub/"]

WORKDIR "/src/src/Auction.Hub"
RUN dotnet publish "Auction.Hub.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5052
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Auction.Hub.dll"]
