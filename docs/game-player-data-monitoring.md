# Мониторинг backup игровых данных

## Граница v2.51

Наблюдатель на control-plane раз в минуту читает закрытый журнал v2.50 и
фиксированные свойства двух systemd units. Он не берет backup lock, не пишет
в журнал, не обращается к игре или restic и не выполняет capture, Archive,
restore либо повтор попытки. Существующее расписание остается воскресным,
04:00 Europe/Moscow, только при пустом сервере.

Результат передается как числовые OTLP/HTTP gauges в существующий Collector на
внутреннем адресе telemetry, порт 4318. Host publication и маршрут Caddy не
добавляются. В метриках нет имен игроков, адресов игры, snapshot IDs, хешей,
путей, credentials или текста ошибок. Единственная resource identity -
`goldsrcops-player-backup-monitor`.

Предупреждения вычисляет Prometheus. Grafana показывает их и состояние backup
на dashboard `goldsrcops-player-backup`. По решению владельца личная доставка
пока отложена: Alertmanager, email и мессенджер не подключаются. Отказ всего
Prometheus/Grafana этим же стеком обнаружить невозможно.

## Состояния и правила

| Условие | Результат |
| --- | --- |
| Свежая verified запись | Возраст от исходного capturedUtc |
| skipped-busy с молодой копией | Информационный статус, без отдельной тревоги |
| Нет копии или capture старше восьми суток | PlayerBackupCopyUnavailable |
| Timer выключен, расписание отличается или слот не появился | PlayerBackupScheduleProblem |
| Ошибка service или незавершенная попытка после его выхода | PlayerBackupInterventionRequired |
| Недостоверный журнал, права, unit или связность записей | PlayerBackupObservationInvalid |
| Нет наблюдения, оно старше трех минут или сильно из будущего | PlayerBackupMonitorUnavailable |

Правила выдерживают условие пять минут. Отсутствующий недельный слот считается
пропущенным через десять минут после воскресного окна 04:00; учитываются только
слоты после первого включения мониторинга. Обычная активная попытка до 65 минут
показывается как выполняющаяся. Более долгая или оставшаяся после завершения
службы требует сверки. Лимит runner остается один час.

Heartbeat содержит время самого чтения. Повторяемое Collector старое значение
не продлевает исправность. Панели текущего состояния используют instant queries
и скрывают старые/недостоверные значения. Порог потери наблюдения обычно дает
тревогу примерно через восемь минут с учетом scrape/evaluation; полная
недоступность Prometheus не имеет такого обещания.

Код последней попытки: 0 - еще не было, 1 - verified, 2 - skipped-busy,
3 - skipped-window, 4 - выполняется, 5 - требуется сверка, 6 - ошибка service,
7 - неизвестно. Ошибка проверки свежести в ExecStartPost старого runner также
дает service_failed; дата успешной копии при этом не меняется.

Проверяются не более 256 entries и 52 недельных каталогов, каждый JSON не более
16 KiB. Ссылки, небезопасные права, дубликаты JSON keys, несогласованные
идентификаторы и существенное будущее время отклоняются. Конкурентная смена
файла может дать один неизвестный sample; правила подавляют такой короткий
переход. Наблюдение не заменяет новую проверку off-host backend или движком.

## Подготовка установки

Установка требует одного R3 bundle с reviewed revision и следующей границей:

1. Сохранить прежние Compose/Collector/Prometheus/Grafana файлы, image digests,
   контейнерные ID и состояния weekly timer/service в закрытой записи оператора.
2. Разместить exact source bytes наблюдателя и двух новых units в
   `/opt/goldsrcops-player-monitor`, root-owned, без group/other write.
   Проверить SHA-256 относительно одобренной ревизии. Каталог v2.50 не менять.
3. По [шаблону](../ops/production/player-data-monitor.json.example) создать
   `/etc/goldsrcops/player-data-monitor.json`, root:root, 0600.
   collectorAddress - существующий RFC1918 IPv4 Collector из проверенной
   deployment configuration. enabledSinceUtc - время первого включения.
   Сохранять это время при обновлениях, иначе можно скрыть пропущенный слот.
4. Проверить [service](../ops/production/systemd/goldsrcops-player-data-monitor.service)
   и [timer](../ops/production/systemd/goldsrcops-player-data-monitor.timer)
   через systemd-analyze verify. Service имеет read-only filesystem, пустой
   CapabilityBoundingSet, скрытые backup credentials/Docker socket, лимиты
   30 секунд, 64 MiB и 16 tasks. Restart выключен.
5. Проверить production preflight с новым rule mount и pinned images.
   Обновить только Collector и Prometheus; dashboard использует существующий
   provisioned каталог Grafana. Пересоздавать Grafana только если изменился
   источник его bind mount. API/Web/PostgreSQL/game/agent не перезапускать.
6. Выполнить read-only `player-data-monitor.py inspect` и один publish из sandbox
   нового service. Установить units, daemon-reload и включить только
   `goldsrcops-player-data-monitor.timer`.
7. Подтвердить свежие метрики, два последовательных обновления heartbeat,
   rule health, текущее состояние в Grafana и отсутствие публичного 4318.
   Сверить прежние ID остальных контейнеров и weekly schedule.

Новые ключи и учетные записи не нужны. Наблюдатель не запускает weekly service
для приемки. Первый фактический воскресный cycle v2.50 остается отдельным
результатом.

## Приемка и откат

Локально проверяются реальные pinned Collector/Prometheus/Grafana, правила
promtool, недостоверный sample и его восстановление. Стенд использует только
синтетические данные и временные loopback ports. Production сохраняет internal
telemetry network без host ports.

При target-приемке допустимо временно отключить только новый monitor timer,
наблюдать PlayerBackupMonitorUnavailable и восстановить timer. Weekly timer,
его service, журнал и игровой runtime для этого не меняются. Полную ошибку
backup на production искусственно не создавать; она покрывается fixtures.

Откат: отключить новый monitor timer, сохранить его config и evidence,
вернуть прежние reviewed конфигурации Collector/Prometheus/Grafana и Compose,
пересоздать только измененные telemetry services на прежних digests.
Не удалять pending.json, last-success.json, недельные каталоги или snapshot.
Ошибки публикации метрик не запускают повтор backup.

Источники протокола: [OTLP/HTTP JSON](https://opentelemetry.io/docs/specs/otlp/)
и [OTLP receiver 0.159.0](https://github.com/open-telemetry/opentelemetry-collector/blob/v0.159.0/receiver/otlpreceiver/README.md).
