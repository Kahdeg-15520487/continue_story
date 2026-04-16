FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore KnowledgeEngine.sln
RUN dotnet publish KnowledgeEngine.sln -c Release -o /app

FROM python:3.13-slim AS python-base
RUN pip install --no-cache-dir markitdown

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
COPY --from=python-base /usr/local/bin/markitdown /usr/local/bin/markitdown
COPY --from=python-base /usr/local/lib/python3.13/site-packages /usr/local/lib/python3.13/site-packages
COPY --from=python-base /usr/local/lib/libpython3.13.so* /usr/local/lib/
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "KnowledgeEngine.Api.dll"]
