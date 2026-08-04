FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Acm.Api/Acm.Api.csproj Acm.Api/
RUN dotnet restore Acm.Api/Acm.Api.csproj
COPY . .
RUN dotnet publish Acm.Api/Acm.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "Acm.Api.dll"]
