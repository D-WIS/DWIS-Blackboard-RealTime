# DWIS Blackboard Rig Edition: real-time process

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** the real-time process: `DWIS.RealTime.Core` (no OPC UA dependency), the OPC Foundation adapter, the method façade, the hub role, unit tests, the Dockerfile.

**Produces:** image `digiwells/dwis.blackboard.realtime` (`sha-<commit>` and `edge` from every merge to `main`, versioned from tags; milestone 4.3).

**Depends on:** `DWIS-Blackboard-Contracts` (the manifest and report shapes, the configuration layers, the gRPC client of the semantic service); the OPC Foundation stack (`OPCFoundation.NetStandard.Opc.Ua.Server`, the version of the old server).

## Status

Milestone 4.0 (November 2026): the skeleton. The `DDHubServer` object with its nine methods and two diagnostics is served with the argument declarations of the old server; the four adapter interfaces of decision D-54 are frozen with a test double and a contract test each; the compile-only stub adapter of D-55 builds. The host runs the lab security profile only (a `None` endpoint, anonymous users): not for production.

Milestone 4.1: the injection side. `Inject` and `Update` run the two-phase injection over the gRPC contract (prepare, create the provider folder and the variables with their validity booleans, commit with their addresses); the same content again is `already_live` and creates nothing; a failed commit keeps the nodes of the live version (D-52) and aborts; `Delete` removes the manifest from the service, then its variables, and the provider folder when it was the provider's last. The answer of `Inject` and `Update` is the `ManifestInjectionResult` JSON of the old API; `Delete` answers a boolean. At start-up Core asks the service for its manifests and creates what is missing, asks again until the service answers, then once more (R-RT-6, D-52). The service unreachable or too slow answers `BadCommunicationError` within the deadline; values keep flowing (R-RT-1, R-RT-2). `Register` and `Resolve` stay `BadNotSupported` (R-BC-2).

Milestone 4.2: the query side. `ResolveQuery` and `GetResources` go straight to the service and answer the old `QueryResult` and `ResourceFile` JSON, every IRI rewritten to `http://ddhub.no/` and split at its last slash into a (namespace, id) pair, a resource without an OPC UA node being the plain pair (D-31). `RegisterQuery` registers the query for the calling session, creates its `QueryClient` object and `QueryDiff` string variable, and answers the snapshot as the first `QueryResultsDiff` with the diff variable's node id, which the client subscribes to; `UnregisterQuery` releases the session's hold and deletes its nodes; a session close releases everything it held. One watch stream carries every diff of every registered query (D-53) and Core writes each to the diff variable of every session holding the query; when the stream ends, Core waits the retry delay, registers every query again (the service's ids change, the nodes do not) and writes the change since as one diff (T-QR-4). Since M5.2 every registration names this process as its watcher and the first stream of a life is opened `fresh`, so that the service releases the sessions of the life before; a stream reopened after a drop is not (decision D-61).

## Layout

One solution, `DWIS.Blackboard.RealTime.slnx`, .NET 10, with the split of specification 3.1 (R-MOD-2):

| Project | Responsibility |
|---|---|
| `src/DWIS.RealTime.Core` | the façade logic over the four adapter interfaces (`IAddressSpace`, `IMethodHost`, `IQueryNodes` implemented by an adapter; `ISessionEvents` implemented by Core), the names clients depend on (`Naming`), the method declarations (`DDHubMethods`), the settings; `Semantic/`: the service as Core sees it (`ISemanticService`, the gRPC client with deadlines and the status mapping); `Injection/`: the manifest key, the table of live manifests, the injector (two-phase injection, delete, rebuild), the result JSON; `Queries/`: the query façade (resolve, resources, register, unregister, the session close, the watch loop), the registry of queries and sessions with the service's ids kept apart from the local ones, the query JSON of the old API; references the contracts only, never an OPC UA assembly (a test checks it) |
| `src/DWIS.RealTime.OpcFoundation` | the adapter for the OPC Foundation stack: `BlackboardNodeManager` (the three interfaces over a node manager; the `DDHubServer` object, provider folders and variables with their validity booleans, query nodes), `BlackboardServer` (the server with the session hooks), `ServerHost` (start and stop with the lab profile, the configuration built in code) |
| `src/DWIS.RealTime.StubAdapter` | the compile-only second adapter (D-55, T-MOD-6): the three interfaces with no stack behind them; the outline for an adapter to another stack |
| `src/DWIS.RealTime.Host` | the process: the five configuration layers of specification section 8 (`RealTime:SemanticAddress`, `RealTime:DeadlineSeconds`, `RealTime:EndpointUrl`, `RealTime:PkiRoot`), logging, the server, Core |
| `tests/DWIS.RealTime.Core.Tests` | the test double adapter and a fake semantic service in memory, Core's behaviour against them (the injection scenarios of M4.1, the query scenarios of M4.2 with a watch stream the test drives), the no-OPC-UA check (T-MOD-4), the contract tests against the double (T-MOD-5) |
| `tests/DWIS.RealTime.OpcFoundation.Tests` | the same contract tests against the adapter on an in-process server; a client round trip over the wire: the object found by browse name (D-57), the methods and their declarations, a call answered with a status, a variable read and written; and, only when `DWIS_SEMANTIC_ADDRESS` names a running semantic service, an inject and a delete over the wire through that service |
| `tests/Shared/AdapterContractTests.cs` | the contract tests, one source file compiled into both test projects; a company with another stack runs them against its adapter |

Shared settings live in `Directory.Build.props` (nullable, analyzers, warnings as errors, .NET 10; globalization is not invariant because the OPC Foundation stack needs named cultures) and `Directory.Packages.props` (central package versions). Tests use xUnit v3 on the Microsoft Testing Platform (opt-in in `global.json`) with FluentAssertions 7.

## Building and running

```bash
dotnet build DWIS.Blackboard.RealTime.slnx -c Release
```

```bash
dotnet test DWIS.Blackboard.RealTime.slnx -c Release --no-build --report-trx --results-directory TestResults
```

```bash
dotnet run --project src/DWIS.RealTime.Host -- --RealTime:EndpointUrl=opc.tcp://localhost:48030 --RealTime:SemanticAddress=http://localhost:8081
```

The host serves `opc.tcp://localhost:48030` by default, the old server's address, so the samples connect unchanged, and talks to the semantic service at `RealTime:SemanticAddress` (`http://semantic:8081` by default, the compose stack's name; `http://localhost:8081` from the host machine) with `RealTime:DeadlineSeconds` (30) on every call and `RealTime:RetrySeconds` (5) between the attempts to list its manifests at start-up. The OPC UA stack keeps its application certificate and stores under its default folder, or under `RealTime:PkiRoot`.

Against the compose stack of the Tests repository (`FUSEKI_TAG=edge SEMANTIC_TAG=edge docker compose -f compose/compose.yaml up -d` there, then its vocabulary loader), the live test runs with:

```bash
DWIS_SEMANTIC_ADDRESS=http://127.0.0.1:8081 dotnet test DWIS.Blackboard.RealTime.slnx -c Release --no-build
```

### The image

```bash
docker build -t digiwells/dwis.blackboard.realtime:dev .
```

The image runs the host as the `app` user with the plain .NET runtime (not ASP.NET: the process serves OPC UA, not HTTP) and keeps ICU, which the OPC Foundation stack needs. It serves `opc.tcp://localhost:48030` by default and talks to `http://semantic:8081`, the compose stack's name for the semantic service; override both with `DWIS__RealTime__EndpointUrl` and `DWIS__RealTime__SemanticAddress`. The application certificate and the trust stores live under `/app/pki` (`DWIS__RealTime__PkiRoot`); mount a volume there to keep the certificate across restarts. The merge workflow builds the image, scans it with Trivy, pushes the `sha-<commit>` and `edge` tags to Docker Hub and tells the Tests repository (`realtime-edge`); the pull request workflow builds and scans it without pushing and starts it once to check that the port answers and that the process keeps retrying a missing semantic service (R-RT-2).

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
