FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore KnowledgeEngine.sln
RUN dotnet publish KnowledgeEngine.sln -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update && \
    apt-get install -y --no-install-recommends python3 python3-pip && \
    pip3 install --break-system-packages 'markitdown[all]' && \
    apt-get remove -y python3-pip && \
    apt-get autoremove -y && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "KnowledgeEngine.Api.dll"]
