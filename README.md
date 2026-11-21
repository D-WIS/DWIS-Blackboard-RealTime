# DWIS Blackboard Rig Edition: real-time process

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** the real-time process: `DWIS.RealTime.Core` (no OPC UA dependency), the OPC Foundation adapter, the method façade, the hub role, unit tests, the Dockerfile.

**Produces:** image `digiwells/dwis.blackboard.realtime` (from milestone 4.3).

**Depends on:** `DWIS-Blackboard-Contracts` (the manifest and report shapes, the configuration layers, the gRPC client of the semantic service); the OPC Foundation stack (`OPCFoundation.NetStandard.Opc.Ua.Server`, the version of the old server).

## Status

Milestone 4.0 (November 2026): the skeleton. The `DDHubServer` object with its nine methods and two diagnostics is served with the argument declarations of the old server; every method answers a status (`BadNotSupported` until M4.1 and M4.2 fill the injection and the query sides); the four adapter interfaces of decision D-54 are frozen with a test double and a contract test each; the compile-only stub adapter of D-55 builds. The host runs the lab security profile only (a `None` endpoint, anonymous users): not for production.

## Layout

One solution, `DWIS.Blackboard.RealTime.slnx`, .NET 10, with the split of specification 3.1 (R-MOD-2):

| Project | Responsibility |
|---|---|
| `src/DWIS.RealTime.Core` | the façade logic over the four adapter interfaces (`IAddressSpace`, `IMethodHost`, `IQueryNodes` implemented by an adapter; `ISessionEvents` implemented by Core), the names clients depend on (`Naming`), the method declarations (`DDHubMethods`), the settings; references the contracts only, never an OPC UA assembly (a test checks it) |
| `src/DWIS.RealTime.OpcFoundation` | the adapter for the OPC Foundation stack: `BlackboardNodeManager` (the three interfaces over a node manager; the `DDHubServer` object, provider folders and variables with their validity booleans, query nodes), `BlackboardServer` (the server with the session hooks), `ServerHost` (start and stop with the lab profile, the configuration built in code) |
| `src/DWIS.RealTime.StubAdapter` | the compile-only second adapter (D-55, T-MOD-6): the three interfaces with no stack behind them; the outline for an adapter to another stack |
| `src/DWIS.RealTime.Host` | the process: the five configuration layers of specification section 8 (`RealTime:SemanticAddress`, `RealTime:DeadlineSeconds`, `RealTime:EndpointUrl`, `RealTime:PkiRoot`), logging, the server, Core |
| `tests/DWIS.RealTime.Core.Tests` | the test double adapter, Core's behaviour against it, the no-OPC-UA check (T-MOD-4), the contract tests against the double (T-MOD-5) |
| `tests/DWIS.RealTime.OpcFoundation.Tests` | the same contract tests against the adapter on an in-process server, and a client round trip over the wire: the object found by its namespace name (D-56), the methods and their declarations, a call answered with a status, a variable read and written |
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

The host serves `opc.tcp://localhost:48030` by default, the old server's address, so the samples connect unchanged. The OPC UA stack keeps its application certificate and stores under its default folder, or under `RealTime:PkiRoot`.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
