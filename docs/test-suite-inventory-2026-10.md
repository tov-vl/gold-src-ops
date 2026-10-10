# Инвентаризация тестов - октябрь 2026

Снимок исходной ревизии `06edbb3bfafb89337012ce48f6f0515981734b20`.

Методы считаются по объявлениям Fact/Theory/BrowserFact, а не по развернутым строкам Theory. Ноль означает вспомогательный файл. Количество строк включает пустые строки и комментарии. Решения и ограничения проверки описаны в [отчете](test-suite-audit-2026-10.md).

## C#

| Файл | Методы | Строки | Роль |
| --- | ---: | ---: | --- |
| [tests/GoldSrcOps.AlertReceiver.Tests/Integration/AlertReceiverFactory.cs](../tests/GoldSrcOps.AlertReceiver.Tests/Integration/AlertReceiverFactory.cs) | 0 | 75 | Fixture / helper |
| [tests/GoldSrcOps.AlertReceiver.Tests/Integration/AlertReceiverPostgreSqlFixture.cs](../tests/GoldSrcOps.AlertReceiver.Tests/Integration/AlertReceiverPostgreSqlFixture.cs) | 0 | 37 | Fixture / helper |
| [tests/GoldSrcOps.AlertReceiver.Tests/Integration/AvailabilityEventEndpointIntegrationTests.cs](../tests/GoldSrcOps.AlertReceiver.Tests/Integration/AvailabilityEventEndpointIntegrationTests.cs) | 22 | 782 | HTTP / host / contract |
| [tests/GoldSrcOps.AlertReceiver.Tests/Integration/ProviderDeliveryOperationsEndpointIntegrationTests.cs](../tests/GoldSrcOps.AlertReceiver.Tests/Integration/ProviderDeliveryOperationsEndpointIntegrationTests.cs) | 9 | 466 | HTTP / host / contract |
| [tests/GoldSrcOps.AlertReceiver.Tests/ProviderDelivery/HttpProviderDeliveryChannelTests.cs](../tests/GoldSrcOps.AlertReceiver.Tests/ProviderDelivery/HttpProviderDeliveryChannelTests.cs) | 2 | 113 | Unit / component / transport |
| [tests/GoldSrcOps.AlertReceiver.Tests/ProviderDelivery/ProviderDispatcherTests.cs](../tests/GoldSrcOps.AlertReceiver.Tests/ProviderDelivery/ProviderDispatcherTests.cs) | 3 | 132 | Unit / component / transport |
| [tests/GoldSrcOps.AlertReceiver.Tests/ProviderOperations/ProviderDeadLetterCursorTests.cs](../tests/GoldSrcOps.AlertReceiver.Tests/ProviderOperations/ProviderDeadLetterCursorTests.cs) | 3 | 46 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/A2S/A2SPacketParserTests.cs](../tests/GoldSrcOps.UnitTests/A2S/A2SPacketParserTests.cs) | 3 | 87 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryOptionsTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryOptionsTests.cs) | 6 | 153 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryReadServiceTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryReadServiceTests.cs) | 7 | 229 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryRegistrationTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryRegistrationTests.cs) | 3 | 90 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryReplayServiceTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/AlertDeliveryReplayServiceTests.cs) | 8 | 325 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/AlertDispatcherTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/AlertDispatcherTests.cs) | 6 | 410 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/ExponentialJitterAlertRetryDelayProviderTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/ExponentialJitterAlertRetryDelayProviderTests.cs) | 2 | 46 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/HttpWebhookAlertDeliveryChannelTests.cs](../tests/GoldSrcOps.UnitTests/Alerts/HttpWebhookAlertDeliveryChannelTests.cs) | 8 | 243 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/IncidentAlertEventV1Tests.cs](../tests/GoldSrcOps.UnitTests/Alerts/IncidentAlertEventV1Tests.cs) | 1 | 54 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Alerts/SyntheticWebhookServer.cs](../tests/GoldSrcOps.UnitTests/Alerts/SyntheticWebhookServer.cs) | 0 | 51 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Api/CommandEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/CommandEndpointIntegrationTests.cs) | 10 | 333 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/DeadLetterCursorTests.cs](../tests/GoldSrcOps.UnitTests/Api/DeadLetterCursorTests.cs) | 3 | 61 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/GameEventEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/GameEventEndpointIntegrationTests.cs) | 9 | 340 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/GoldSrcOpsApiFactory.cs](../tests/GoldSrcOps.UnitTests/Api/GoldSrcOpsApiFactory.cs) | 0 | 96 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Api/GoldSrcOpsSecurityTests.cs](../tests/GoldSrcOps.UnitTests/Api/GoldSrcOpsSecurityTests.cs) | 3 | 46 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/HealthEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/HealthEndpointIntegrationTests.cs) | 2 | 47 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/MetricsEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/MetricsEndpointIntegrationTests.cs) | 4 | 148 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/MonitoringEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/MonitoringEndpointIntegrationTests.cs) | 19 | 932 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/OperationsActivityCursorTests.cs](../tests/GoldSrcOps.UnitTests/Api/OperationsActivityCursorTests.cs) | 4 | 93 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/OtlpMetricsOptionsTests.cs](../tests/GoldSrcOps.UnitTests/Api/OtlpMetricsOptionsTests.cs) | 5 | 106 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/PendingDeliveryCursorTests.cs](../tests/GoldSrcOps.UnitTests/Api/PendingDeliveryCursorTests.cs) | 3 | 56 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlAlertDeliveryEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlAlertDeliveryEndpointIntegrationTests.cs) | 6 | 457 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlCommandEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlCommandEndpointIntegrationTests.cs) | 4 | 296 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlDeadLetterReplayEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlDeadLetterReplayEndpointIntegrationTests.cs) | 7 | 750 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlEndpointIntegrationTests.cs) | 10 | 881 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlGameEventInboxIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlGameEventInboxIntegrationTests.cs) | 2 | 199 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlGoldSrcOpsApiFactory.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlGoldSrcOpsApiFactory.cs) | 0 | 117 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlMigrationIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlMigrationIntegrationTests.cs) | 2 | 432 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PostgreSqlSnapshotRetentionIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/PostgreSqlSnapshotRetentionIntegrationTests.cs) | 1 | 138 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Api/PublicLeaderboardEndpointTests.cs](../tests/GoldSrcOps.UnitTests/Api/PublicLeaderboardEndpointTests.cs) | 2 | 50 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/PublicServerJoinOptionsTests.cs](../tests/GoldSrcOps.UnitTests/Api/PublicServerJoinOptionsTests.cs) | 3 | 72 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/ReverseProxyConfigurationTests.cs](../tests/GoldSrcOps.UnitTests/Api/ReverseProxyConfigurationTests.cs) | 3 | 66 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/SecurityEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/SecurityEndpointIntegrationTests.cs) | 9 | 233 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/SecurityServiceCollectionExtensionsTests.cs](../tests/GoldSrcOps.UnitTests/Api/SecurityServiceCollectionExtensionsTests.cs) | 8 | 251 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/ServerEndpointIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Api/ServerEndpointIntegrationTests.cs) | 13 | 417 | HTTP / host / contract |
| [tests/GoldSrcOps.UnitTests/Api/TestAuthentication.cs](../tests/GoldSrcOps.UnitTests/Api/TestAuthentication.cs) | 0 | 110 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Availability/AvailabilityEvaluatorTests.cs](../tests/GoldSrcOps.UnitTests/Availability/AvailabilityEvaluatorTests.cs) | 5 | 140 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/AvailabilityJsonFileTests.cs](../tests/GoldSrcOps.UnitTests/Availability/AvailabilityJsonFileTests.cs) | 2 | 83 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/AvailabilityNormalizerTests.cs](../tests/GoldSrcOps.UnitTests/Availability/AvailabilityNormalizerTests.cs) | 2 | 115 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/B2EvidenceObjectClientTests.cs](../tests/GoldSrcOps.UnitTests/Availability/B2EvidenceObjectClientTests.cs) | 7 | 189 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/CommandOptionsParserTests.cs](../tests/GoldSrcOps.UnitTests/Availability/CommandOptionsParserTests.cs) | 5 | 98 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/EvidenceArchiveTests.cs](../tests/GoldSrcOps.UnitTests/Availability/EvidenceArchiveTests.cs) | 5 | 252 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/GrafanaLogsApiClientTests.cs](../tests/GoldSrcOps.UnitTests/Availability/GrafanaLogsApiClientTests.cs) | 7 | 300 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/GrafanaMetricsExporterTests.cs](../tests/GoldSrcOps.UnitTests/Availability/GrafanaMetricsExporterTests.cs) | 5 | 319 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Availability/ProbeFailureLogClassifierTests.cs](../tests/GoldSrcOps.UnitTests/Availability/ProbeFailureLogClassifierTests.cs) | 1 | 57 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/CommandDispatcherTests.cs](../tests/GoldSrcOps.UnitTests/Commands/CommandDispatcherTests.cs) | 9 | 418 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/CommandExecutionServiceTests.cs](../tests/GoldSrcOps.UnitTests/Commands/CommandExecutionServiceTests.cs) | 4 | 166 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/CommandExecutionTests.cs](../tests/GoldSrcOps.UnitTests/Commands/CommandExecutionTests.cs) | 5 | 99 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/ConfigurationSecretReferenceResolverTests.cs](../tests/GoldSrcOps.UnitTests/Commands/ConfigurationSecretReferenceResolverTests.cs) | 5 | 108 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconClientTests.cs](../tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconClientTests.cs) | 11 | 366 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconCommandExecutorTests.cs](../tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconCommandExecutorTests.cs) | 3 | 127 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconOptionsTests.cs](../tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconOptionsTests.cs) | 3 | 69 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconProtocolTests.cs](../tests/GoldSrcOps.UnitTests/Commands/GoldSrcRconProtocolTests.cs) | 7 | 103 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Credentials/ServerCredentialTests.cs](../tests/GoldSrcOps.UnitTests/Credentials/ServerCredentialTests.cs) | 4 | 71 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Credentials/ServerCredentialsServiceTests.cs](../tests/GoldSrcOps.UnitTests/Credentials/ServerCredentialsServiceTests.cs) | 6 | 215 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/AmxModXGameEventProducerTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/AmxModXGameEventProducerTests.cs) | 3 | 94 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/ClientCredentialsAccessTokenProviderTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/ClientCredentialsAccessTokenProviderTests.cs) | 5 | 167 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentCommandLineTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentCommandLineTests.cs) | 5 | 81 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentOptionsTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentOptionsTests.cs) | 6 | 131 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentStatusTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentStatusTests.cs) | 1 | 40 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentTestDoubles.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventAgentTestDoubles.cs) | 0 | 167 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventDeliveryClientTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventDeliveryClientTests.cs) | 4 | 153 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventDispatcherTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventDispatcherTests.cs) | 5 | 127 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventSpoolTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/GameEventSpoolTests.cs) | 8 | 224 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEventAgent/SqliteGameEventOutboxTests.cs](../tests/GoldSrcOps.UnitTests/GameEventAgent/SqliteGameEventOutboxTests.cs) | 8 | 241 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEvents/GameEventInboxEntryTests.cs](../tests/GoldSrcOps.UnitTests/GameEvents/GameEventInboxEntryTests.cs) | 4 | 105 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEvents/GameEventIngestionServiceTests.cs](../tests/GoldSrcOps.UnitTests/GameEvents/GameEventIngestionServiceTests.cs) | 5 | 184 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEvents/GameEventReadServiceTests.cs](../tests/GoldSrcOps.UnitTests/GameEvents/GameEventReadServiceTests.cs) | 2 | 61 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEvents/GameEventRetentionOptionsTests.cs](../tests/GoldSrcOps.UnitTests/GameEvents/GameEventRetentionOptionsTests.cs) | 3 | 66 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/GameEvents/GameEventRetentionServiceTests.cs](../tests/GoldSrcOps.UnitTests/GameEvents/GameEventRetentionServiceTests.cs) | 2 | 87 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Helpers/AutoMoqDataAttribute.cs](../tests/GoldSrcOps.UnitTests/Helpers/AutoMoqDataAttribute.cs) | 0 | 22 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Helpers/CapturingLogger.cs](../tests/GoldSrcOps.UnitTests/Helpers/CapturingLogger.cs) | 0 | 53 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Helpers/CapturingRconCommandExecutor.cs](../tests/GoldSrcOps.UnitTests/Helpers/CapturingRconCommandExecutor.cs) | 0 | 33 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Helpers/InlineAutoMoqDataAttribute.cs](../tests/GoldSrcOps.UnitTests/Helpers/InlineAutoMoqDataAttribute.cs) | 0 | 12 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Helpers/MetricsCollector.cs](../tests/GoldSrcOps.UnitTests/Helpers/MetricsCollector.cs) | 0 | 64 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Helpers/TestData.cs](../tests/GoldSrcOps.UnitTests/Helpers/TestData.cs) | 0 | 34 | Fixture / helper |
| [tests/GoldSrcOps.UnitTests/Incidents/IncidentsServiceTests.cs](../tests/GoldSrcOps.UnitTests/Incidents/IncidentsServiceTests.cs) | 2 | 61 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Monitoring/MonitoringReadServiceTests.cs](../tests/GoldSrcOps.UnitTests/Monitoring/MonitoringReadServiceTests.cs) | 18 | 875 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Monitoring/PublicLeaderboardMetricsTests.cs](../tests/GoldSrcOps.UnitTests/Monitoring/PublicLeaderboardMetricsTests.cs) | 4 | 174 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Monitoring/PublicLeaderboardPollerTests.cs](../tests/GoldSrcOps.UnitTests/Monitoring/PublicLeaderboardPollerTests.cs) | 1 | 114 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Monitoring/PublicLeaderboardTests.cs](../tests/GoldSrcOps.UnitTests/Monitoring/PublicLeaderboardTests.cs) | 9 | 162 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Monitoring/SnapshotRetentionOptionsTests.cs](../tests/GoldSrcOps.UnitTests/Monitoring/SnapshotRetentionOptionsTests.cs) | 3 | 67 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Monitoring/SnapshotRetentionServiceTests.cs](../tests/GoldSrcOps.UnitTests/Monitoring/SnapshotRetentionServiceTests.cs) | 3 | 118 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Outbox/OutboxMessageTests.cs](../tests/GoldSrcOps.UnitTests/Outbox/OutboxMessageTests.cs) | 2 | 78 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Outbox/OutboxReplayRequestTests.cs](../tests/GoldSrcOps.UnitTests/Outbox/OutboxReplayRequestTests.cs) | 2 | 91 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Outbox/PostgreSqlOutboxStoreIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Outbox/PostgreSqlOutboxStoreIntegrationTests.cs) | 7 | 507 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Servers/AvailabilityIncidentTests.cs](../tests/GoldSrcOps.UnitTests/Servers/AvailabilityIncidentTests.cs) | 1 | 24 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Servers/PollSnapshotTests.cs](../tests/GoldSrcOps.UnitTests/Servers/PollSnapshotTests.cs) | 2 | 37 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Servers/PostgreSqlServerPollingOutboxIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Servers/PostgreSqlServerPollingOutboxIntegrationTests.cs) | 2 | 290 | PostgreSQL integration |
| [tests/GoldSrcOps.UnitTests/Servers/ServerCurrentStateTests.cs](../tests/GoldSrcOps.UnitTests/Servers/ServerCurrentStateTests.cs) | 4 | 97 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Servers/ServerPollingIntegrationTests.cs](../tests/GoldSrcOps.UnitTests/Servers/ServerPollingIntegrationTests.cs) | 5 | 298 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Servers/ServerPollingServiceTests.cs](../tests/GoldSrcOps.UnitTests/Servers/ServerPollingServiceTests.cs) | 6 | 423 | Unit / component / transport |
| [tests/GoldSrcOps.UnitTests/Servers/ServerTests.cs](../tests/GoldSrcOps.UnitTests/Servers/ServerTests.cs) | 7 | 171 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Browser/BrowserFactAttribute.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserFactAttribute.cs) | 0 | 17 | Fixture / helper |
| [tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.Leaderboard.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.Leaderboard.cs) | 2 | 68 | Chromium / BFF |
| [tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.LeaderboardRefresh.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.LeaderboardRefresh.cs) | 6 | 188 | Chromium / BFF |
| [tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.PlayStatusRefresh.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.PlayStatusRefresh.cs) | 8 | 248 | Chromium / BFF |
| [tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.PlayerGuide.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.PlayerGuide.cs) | 2 | 91 | Chromium / BFF |
| [tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryTests.cs) | 22 | 1162 | Chromium / BFF |
| [tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryWebApplicationFactory.cs](../tests/GoldSrcOps.WebTests/Browser/BrowserTokenBoundaryWebApplicationFactory.cs) | 0 | 137 | Fixture / helper |
| [tests/GoldSrcOps.WebTests/Hosting/WebHostingConfigurationTests.cs](../tests/GoldSrcOps.WebTests/Hosting/WebHostingConfigurationTests.cs) | 6 | 176 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Pages/OperatorCommandWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorCommandWorkflowIntegrationTests.cs) | 8 | 236 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorMapChangeWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorMapChangeWorkflowIntegrationTests.cs) | 10 | 335 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorProviderReviewWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorProviderReviewWorkflowIntegrationTests.cs) | 12 | 323 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorRconCredentialWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorRconCredentialWorkflowIntegrationTests.cs) | 8 | 303 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorReplayWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorReplayWorkflowIntegrationTests.cs) | 8 | 228 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorRestartWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorRestartWorkflowIntegrationTests.cs) | 12 | 310 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorServerMonitoringWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorServerMonitoringWorkflowIntegrationTests.cs) | 9 | 277 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorServerRegistrationWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorServerRegistrationWorkflowIntegrationTests.cs) | 6 | 227 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/OperatorServerUpdateWorkflowIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/OperatorServerUpdateWorkflowIntegrationTests.cs) | 8 | 282 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/PlayerGuideIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/PlayerGuideIntegrationTests.cs) | 6 | 140 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/PublicDashboardIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/PublicDashboardIntegrationTests.cs) | 4 | 280 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/PublicLeaderboardIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/PublicLeaderboardIntegrationTests.cs) | 4 | 62 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/ReaderPortalIntegrationTests.cs](../tests/GoldSrcOps.WebTests/Pages/ReaderPortalIntegrationTests.cs) | 24 | 586 | SSR / HTTP workflow |
| [tests/GoldSrcOps.WebTests/Pages/ReaderWebApplicationFactory.cs](../tests/GoldSrcOps.WebTests/Pages/ReaderWebApplicationFactory.cs) | 0 | 1251 | Fixture / helper |
| [tests/GoldSrcOps.WebTests/Security/AuthenticationEndpointsTests.cs](../tests/GoldSrcOps.WebTests/Security/AuthenticationEndpointsTests.cs) | 2 | 32 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/InMemoryTicketStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/InMemoryTicketStoreTests.cs) | 3 | 93 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorCommandConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorCommandConfirmationStoreTests.cs) | 5 | 90 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorMapChangeConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorMapChangeConfirmationStoreTests.cs) | 3 | 71 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorProviderReviewConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorProviderReviewConfirmationStoreTests.cs) | 1 | 21 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorRconCredentialConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorRconCredentialConfirmationStoreTests.cs) | 2 | 59 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorReplayConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorReplayConfirmationStoreTests.cs) | 4 | 79 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorServerMonitoringConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorServerMonitoringConfirmationStoreTests.cs) | 4 | 124 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorServerRegistrationConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorServerRegistrationConfirmationStoreTests.cs) | 4 | 85 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/OperatorServerUpdateConfirmationStoreTests.cs](../tests/GoldSrcOps.WebTests/Security/OperatorServerUpdateConfirmationStoreTests.cs) | 4 | 90 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Security/SecretFileReaderTests.cs](../tests/GoldSrcOps.WebTests/Security/SecretFileReaderTests.cs) | 3 | 47 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Services/AccessTokenHandlerTests.cs](../tests/GoldSrcOps.WebTests/Services/AccessTokenHandlerTests.cs) | 2 | 111 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Services/OperatorApiClientTests.cs](../tests/GoldSrcOps.WebTests/Services/OperatorApiClientTests.cs) | 24 | 705 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Services/ProviderOperationsClientTests.cs](../tests/GoldSrcOps.WebTests/Services/ProviderOperationsClientTests.cs) | 6 | 185 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Services/PublicStatusClientTests.cs](../tests/GoldSrcOps.WebTests/Services/PublicStatusClientTests.cs) | 6 | 153 | Unit / component / transport |
| [tests/GoldSrcOps.WebTests/Services/ReaderApiClientTests.cs](../tests/GoldSrcOps.WebTests/Services/ReaderApiClientTests.cs) | 14 | 430 | Unit / component / transport |

## Smoke и игровые fixtures

Все перечисленные файлы сохраняются. Ссылки ниже показывают один подтвержденный путь вызова, а не полный граф зависимостей. Запуск скрипта из CI не означает, что CI выполняет все его необязательные режимы или проверяет реальный сервер.

| Файл | Строки | Путь вызова |
| --- | ---: | --- |
| [alert-receiver-container.ps1](../tools/smoke/alert-receiver-container.ps1) | 769 | CI workflow |
| [amxx-game-event-producer.ps1](../tools/smoke/amxx-game-event-producer.ps1) | 125 | Через `amxx-map-menu.ps1` |
| [amxx-map-language-provider.sma](../tools/smoke/amxx-map-language-provider.sma) | 16 | Через `amxx-map-menu.ps1` |
| [amxx-map-menu.ps1](../tools/smoke/amxx-map-menu.ps1) | 44 | CI workflow |
| [amxx-map-menu.sma](../tools/smoke/amxx-map-menu.sma) | 212 | Через `amxx-map-menu.ps1` |
| [amxx-map-stats.sma](../tools/smoke/amxx-map-stats.sma) | 466 | Через `amxx-weapon-selection.ps1` |
| [amxx-persistent-stats.sma](../tools/smoke/amxx-persistent-stats.sma) | 1338 | Через `amxx-weapon-selection.ps1` |
| [amxx-player-menu.sma](../tools/smoke/amxx-player-menu.sma) | 511 | Через `amxx-weapon-selection.ps1` |
| [amxx-player-preferences.sma](../tools/smoke/amxx-player-preferences.sma) | 403 | Через `amxx-weapon-selection.ps1` |
| [amxx-spawn-loadout.sma](../tools/smoke/amxx-spawn-loadout.sma) | 194 | Через `amxx-weapon-selection.ps1` |
| [amxx-weapon-selection.ps1](../tools/smoke/amxx-weapon-selection.ps1) | 71 | CI workflow |
| [availability-dns-proof.ps1](../tools/smoke/availability-dns-proof.ps1) | 166 | CI workflow |
| [availability-evidence-schedule.ps1](../tools/smoke/availability-evidence-schedule.ps1) | 436 | CI workflow |
| [availability-shadow-audit.ps1](../tools/smoke/availability-shadow-audit.ps1) | 373 | CI workflow |
| [ci-release-fast-path.ps1](../tools/smoke/ci-release-fast-path.ps1) | 526 | CI workflow |
| [container.ps1](../tools/smoke/container.ps1) | 1307 | CI workflow |
| [game-event-pilot-bundle.ps1](../tools/smoke/game-event-pilot-bundle.ps1) | 141 | CI workflow |
| [game-kill-rewards-runtime.sh](../tools/smoke/game-kill-rewards-runtime.sh) | 59 | CI workflow |
| [game-map-menu-runtime.sh](../tools/smoke/game-map-menu-runtime.sh) | 42 | CI workflow |
| [game-player-data-recovery-runtime.sh](../tools/smoke/game-player-data-recovery-runtime.sh) | 65 | CI workflow |
| [game-player-preferences-runtime.sh](../tools/smoke/game-player-preferences-runtime.sh) | 74 | CI workflow |
| [gameserver-fast-reentry-transition.sh](../tools/smoke/gameserver-fast-reentry-transition.sh) | 494 | CI workflow |
| [gameserver-fast-reentry.sh](../tools/smoke/gameserver-fast-reentry.sh) | 195 | CI workflow |
| [gameserver-game-event-persistent.sh](../tools/smoke/gameserver-game-event-persistent.sh) | 411 | CI workflow |
| [gameserver-game-event-pilot-activate.sh](../tools/smoke/gameserver-game-event-pilot-activate.sh) | 585 | CI workflow |
| [gameserver-game-event-pilot-install.sh](../tools/smoke/gameserver-game-event-pilot-install.sh) | 327 | CI workflow |
| [gameserver-guarded-autostart.sh](../tools/smoke/gameserver-guarded-autostart.sh) | 171 | CI workflow |
| [gameserver-host-bootstrap.sh](../tools/smoke/gameserver-host-bootstrap.sh) | 263 | CI workflow |
| [gameserver-managed-profile.sh](../tools/smoke/gameserver-managed-profile.sh) | 249 | CI workflow |
| [gameserver-public-game-firewall.sh](../tools/smoke/gameserver-public-game-firewall.sh) | 116 | CI workflow |
| [gameserver-runtime-activate.sh](../tools/smoke/gameserver-runtime-activate.sh) | 348 | CI workflow |
| [gameserver-runtime-install.sh](../tools/smoke/gameserver-runtime-install.sh) | 228 | CI workflow |
| [gameserver-soak-readiness.sh](../tools/smoke/gameserver-soak-readiness.sh) | 225 | CI workflow |
| [gameserver-weapon-selection-addon.sh](../tools/smoke/gameserver-weapon-selection-addon.sh) | 289 | CI workflow |
| [host-bootstrap.sh](../tools/smoke/host-bootstrap.sh) | 102 | CI workflow |
| [host-preflight.ps1](../tools/smoke/host-preflight.ps1) | 326 | CI workflow |
| [image-publication.ps1](../tools/smoke/image-publication.ps1) | 297 | CI workflow |
| [oidc-live-contract.ps1](../tools/smoke/oidc-live-contract.ps1) | 188 | CI workflow |
| [oidc-live.ps1](../tools/smoke/oidc-live.ps1) | 511 | Синтетический contract smoke в CI; live-приемка вручную |
| [player-data-backup.ps1](../tools/smoke/player-data-backup.ps1) | 87 | CI workflow |
| [player-data-bundle.py](../tools/smoke/player-data-bundle.py) | 151 | CI workflow |
| [player-data-capture.sh](../tools/smoke/player-data-capture.sh) | 53 | CI workflow |
| [player-data-monitor-stack.py](../tools/smoke/player-data-monitor-stack.py) | 233 | CI workflow |
| [player-data-monitor.py](../tools/smoke/player-data-monitor.py) | 275 | CI workflow |
| [player-data-schedule.py](../tools/smoke/player-data-schedule.py) | 353 | CI workflow |
| [player-data-schedule.sh](../tools/smoke/player-data-schedule.sh) | 79 | CI workflow |
| [player-menu-dictionary.ps1](../tools/smoke/player-menu-dictionary.ps1) | 52 | Через `amxx-map-menu.ps1` |
| [postgres-backup-schedule.ps1](../tools/smoke/postgres-backup-schedule.ps1) | 179 | CI workflow |
| [postgres-backup-schedule.sh](../tools/smoke/postgres-backup-schedule.sh) | 45 | CI workflow |
| [public_leaderboard_monitor.py](../tools/smoke/public_leaderboard_monitor.py) | 154 | Импорт из `player-data-monitor-stack.py`, затем CI |
| [rcon-live.ps1](../tools/smoke/rcon-live.ps1) | 294 | Ручная проверка разрешенного live-сервера |
| [soak-readiness.ps1](../tools/smoke/soak-readiness.ps1) | 397 | CI workflow |
| [weapon-selection-package.ps1](../tools/smoke/weapon-selection-package.ps1) | 82 | CI workflow |
| [web-container.ps1](../tools/smoke/web-container.ps1) | 403 | CI workflow |

## Границы

- 138 C# файлов: 122 с методами тестов и 16 вспомогательных; 702 объявления методов, 28 396 строк.
- 54 файла smoke/fixtures: 21 PowerShell, 21 shell, 7 Pawn и 5 Python; 15 500 строк.
- `ops/` и `src/` рассмотрены как проверяемые реализации и контракты, а не дополнительные тесты.
- Классификация укрупненная: имя проекта `UnitTests` не исключает HTTP, сокеты, SQLite или PostgreSQL.
- Все файлы и имена сценариев инвентаризированы; углубленное чтение выполнялось по риску, стоимости и обнаруженным слабым местам. Это не доказательство полноты покрытия каждой ветви.
