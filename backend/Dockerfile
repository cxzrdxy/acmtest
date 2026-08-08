FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Acm.sln ./
COPY Acm.Api/Acm.Api.csproj Acm.Api/
COPY Acm.Judge.Core/Acm.Judge.Core.csproj Acm.Judge.Core/
COPY Acm.JudgeWorker/Acm.JudgeWorker.csproj Acm.JudgeWorker/
RUN dotnet restore Acm.sln
COPY . .
RUN dotnet publish Acm.sln -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "Acm.Api.dll"]
