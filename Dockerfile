# Image digiwells/dwis.blackboard.realtime (specification 10.1, R-ENG-2): the real-time process, an OPC UA server
# on 48030 that talks to the semantic service over gRPC.
# Build: docker build -t digiwells/dwis.blackboard.realtime:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/DWIS.RealTime.Host/DWIS.RealTime.Host.csproj -c Release -o /app

# The plain runtime image, not the ASP.NET one: the process serves OPC UA, not HTTP. It keeps ICU: the OPC
# Foundation stack builds its resource texts with named cultures and fails to start in invariant mode.
FROM mcr.microsoft.com/dotnet/runtime:11.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The OPC UA stack keeps its application certificate and its trust stores here (the lab profile accepts any
# client certificate; the production profile of milestone 5 mounts a prepared store).
RUN mkdir -p /app/pki && chown -R app:app /app/pki
ENV DWIS__RealTime__PkiRoot=/app/pki \
    DWIS__RealTime__EndpointUrl=opc.tcp://localhost:48030 \
    DWIS__RealTime__SemanticAddress=http://semantic:8081
EXPOSE 48030
USER app
ENTRYPOINT ["dotnet", "DWIS.RealTime.Host.dll"]
