# Task contract

The task model shared with the Alondra1 cloud and future clients. This repository is the source of truth for these files, because they are generated from the Windows model; [alondra1-backend](https://github.com/nikita4sea/alondra1-backend/tree/main/contract) keeps a copy next to the HTTP contract (`openapi.yaml`).

| Path | Contents |
| --- | --- |
| `schemas/todo-v1.schema.json` | The task model of Alondra1 1.3.0 as JSON Schema, generated from and tested against `src/Alondra1.Core/Tasks/TodoModels.cs`. |
| `fixtures/todo-v1/full-document.json` | Golden document written by the Windows serializer: month-end recurrence with anchor, weekly weekday sets, a reminder at a non-existent DST wall-clock time, ordered steps, attachments, grouped and tinted lists with emoji, and completion history for two occurrences of one recurring task. |

`Alondra1.Tests.ContractSchemaTests` fails when the model, the schema, and the fixture disagree. After an intended model change:

1. Run `dotnet test Alondra1.slnx` with `ALONDRA1_UPDATE_FIXTURES=1` to rewrite the fixture.
2. Commit the change here, then copy `contract/schemas` and `contract/fixtures` to alondra1-backend in a matching pull request. Its `contract-sync` workflow fails until both copies match.
