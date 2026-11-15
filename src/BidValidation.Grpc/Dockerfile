FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/BidValidation.Grpc/BidValidation.Grpc.csproj", "src/BidValidation.Grpc/"]
RUN dotnet restore "src/BidValidation.Grpc/BidValidation.Grpc.csproj"

COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/BidValidation.Grpc/", "src/BidValidation.Grpc/"]

WORKDIR "/src/src/BidValidation.Grpc"
RUN dotnet publish "BidValidation.Grpc.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5051
EXPOSE 5151
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BidValidation.Grpc.dll"]
