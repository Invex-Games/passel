# Contracts

What the API puts on the wire, committed so that every side is checked against the same thing. The HTTP contract itself is described in [docs/reference/http-api.md](../docs/reference/http-api.md); this folder holds the machine-readable copies and the files the client test suites read.

| File | What it is | Written by | Read by |
|---|---|---|---|
| `openapi.json` | The OpenAPI document the Development host serves at `/openapi/v1.json`, without its `servers` entry | `ContractTests` in the Web API integration tests, which also fail when it is stale | People and tools |
| `fixtures/*.json` | Sample payloads serialized by the API with its real JSON options: a list page with every packet type (the link with its preview), a create response, a `packet-created`, a `packet-updated` and a `packet-deleted` event | `ContractTests`, which also fail when one is stale or an endpoint answers in another shape | `test/contracts.test.ts` in the extension and `test/contracts_test.dart` in the mobile app, with their real parsers |
| `feed-scenarios.json` | Feed sequences with the list each must leave behind: first page, cursor page, live create, live delete, live update, resync, undo | By hand; the file explains its format | Both client suites, through their feed stores; the API tests check that every packet in it is a valid wire packet |

## Regenerating

`openapi.json` and `fixtures/` are generated. When a test says one is out of date, run the Web API integration tests with `PASSEL_UPDATE_CONTRACTS=1` in the environment (Docker running):

```
$env:PASSEL_UPDATE_CONTRACTS = "1"; dotnet test --project src/webapi/Invex.Passel.WebApi.IntegrationTests/Invex.Passel.WebApi.IntegrationTests.csproj
```

In a POSIX shell: `PASSEL_UPDATE_CONTRACTS=1 dotnet test --project …`. Review the diff, then run the extension and mobile app test suites: a change the clients do not handle yet fails there, which is the point. Commit the files with the change that caused them.

`feed-scenarios.json` is edited by hand. Add a scenario when a feed rule changes, and extend both interpreters (one in each client suite) when a scenario needs an action they do not know; until then, the unknown action fails both suites.
