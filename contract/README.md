# Task contract

The contract shared with the Alondra1 cloud and the other clients. [alondra1-backend `contract/`](https://github.com/nikita4sea/alondra1-backend/tree/main/contract) is the single authoritative release (completion spec v1.1 §7); this directory is its mirror, checked by the backend's `contract-sync` workflow. The Windows task model stays normative for task semantics: its serializer produces the golden fixture, which is published through the backend.

| Path | Contents |
| --- | --- |
| `schemas/todo-v1.schema.json` | The task model of Alondra1 1.3.0 as JSON Schema, generated from and tested against `src/Alondra1.Core/Tasks/TodoModels.cs`. |
| `schemas/discovery-v1.schema.json`, `schemas/discovery-signature-v1.schema.json` | Signed cloud discovery manifest and its detached signature (rules: alondra1-infra `discovery/README.md`). |
| `fixtures/discovery-v1/` | Cross-language discovery cases (`cases.json`, test-only `trust.json`, manifests and signatures). Byte-exact (`-text` in `.gitattributes`). |
| `fixtures/todo-v1/full-document.json` | Golden document written by the Windows serializer: month-end recurrence with anchor, weekly weekday sets, a reminder at a non-existent DST wall-clock time, ordered steps, attachments, grouped and tinted lists with emoji, and completion history for two occurrences of one recurring task. |

`Alondra1.Tests.ContractSchemaTests` fails when the model, the schema, and the fixture disagree. After an intended model change:

1. Run `dotnet test Alondra1.slnx` with `ALONDRA1_UPDATE_FIXTURES=1` to rewrite the fixture.
2. Publish it in alondra1-backend `contract/` in a matching pull request (the authoritative release); its `contract-sync` workflow fails until both copies match. Other contract changes (notes, calendar, discovery) start in the backend and are copied here.
