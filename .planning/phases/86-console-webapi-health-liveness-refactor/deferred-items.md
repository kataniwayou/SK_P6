# Phase 86 Deferred Items

## From 86-02 (hermetic suite, 2026-07-26)

Pre-existing external-infra integration tests that FAIL in the Docker-less hermetic sandbox
(require live Postgres / Elasticsearch; NOT caused by plan 86-02 — out of scope per the
scope boundary). Recorded here per the phases 68/73/74/75 deferred-automated precedent:

- BaseApi.Tests.Integration.ErrorMappingFacts.Delete_Step_Referenced_By_Workflow_Returns422 (Phase8WebAppFactory / Postgres)
- BaseApi.Tests.Middleware.ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak (PostgresFixture)
- BaseApi.Tests.Observability.LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource (ES/OTLP)
- BaseApi.Tests.Observability.LogExportTests.Test_LogRecord_CorrelationId_Survives_Sanitization (ES/OTLP)
- BaseApi.Tests.Observability.SchemasLogsE2ETests.PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId (Elasticsearch)
- BaseApi.Tests.Observability.LogLevelFilterTests.Test_Information_Log_Present_When_Default_Information (Observability)
