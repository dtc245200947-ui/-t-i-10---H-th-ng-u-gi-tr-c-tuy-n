FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/Auction.Api/Auction.Api.csproj", "src/Auction.Api/"]
RUN dotnet restore "src/Auction.Api/Auction.Api.csproj"

COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/BidValidation.Grpc/Protos/", "src/BidValidation.Grpc/Protos/"]
COPY ["src/Auction.Api/", "src/Auction.Api/"]

WORKDIR "/src/src/Auction.Api"
RUN dotnet publish "Auction.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5050
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Auction.Api.dll"]
