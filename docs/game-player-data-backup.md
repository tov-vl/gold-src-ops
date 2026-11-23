# Резервное копирование игровых данных

## Граница

Механизм сохраняет два nVault игрового addon и их журналы:

- `goldsrcops-player-stats-v1`: сохраненные kills/deaths и вычисляемый из них ранг;
- `goldsrcops-player-preferences-v1`: язык, оружие и отдельная настройка HUD.

Архив содержит только эти `.vault`, существующие `.journal` и `manifest.json`.
Отсутствующий журнал отличается от существующего пустого журнала. Наличие обоих
vault обязательно. Другие vault, конфигурация сервера, RCON, OAuth, очереди,
логи и пользовательские файлы не копируются. Схемы и данные addon не меняются.

Архив приватный: vault содержит идентификаторы игроков. Каталоги оператора
должны иметь режим `0700`, файлы - `0600`, находиться вне Git и синхронизируемых
публичных папок. Для Windows нужна эквивалентная ACL. Скрипты выводят только
агрегаты и хеш архива; содержимое записей не выводится.

## Согласованный снимок

По [документации nVault](https://www.amxmodx.org/doc/source/functions/nvault/index.htm),
журнал используется для восстановления, а синхронизация происходит при закрытии
vault или смене карты. Копирование файлов работающего процесса не дает общего
согласованного состояния двух vault.

Используйте согласованное окно обслуживания: внешний A2S/RCON preflight должен
подтвердить пустой сервер и исправное хранилище, после чего оператор останавливает
только игровой сервис. Другой оператор не должен запускать игру или менять addon
до завершения копирования. Обертка
[`player-data-capture.sh`](../ops/gameserver/player-data-capture.sh) проверяет
действующий persistent baseline, `inactive`, нулевой `MainPID`, отсутствие
`hlds_linux` у игрового пользователя и права каталога. Она удерживает отдельную
backup-блокировку и существующую блокировку переходов игрового runtime.

```bash
sudo bash ./ops/gameserver/player-data-capture.sh "$PRIVATE_NEW_BUNDLE"
```

Путь должен быть новым абсолютным именем в закрытом каталоге вне checkout.
Обертка сама не останавливает и не запускает игру. После копирования оператор
проверяет неизменность закрытых vault, запускает прежний runtime и выполняет
независимые A2S/RCON и host postflight. Сетевая передача не должна увеличивать
окно остановки: игра запускается до загрузки архива в off-host storage.

[`player-data-bundle.py`](../ops/gameserver/player-data-bundle.py) ограничивает
каждый файл 16 MiB, весь архив - 65 MiB. Несколько чтений сравнивают состав,
метаданные и байты. Symlink, hardlink, специальные файлы, пропавший vault,
изменившийся набор и существующий выходной файл отклоняются. Это дополнительная
проверка, а не замена остановки процесса. Низкоуровневый `create` предназначен
для уже остановленных файлов и изолированных fixtures.

## Зашифрованное off-host хранение

На control-plane используется существующий
[restic-контур PostgreSQL](postgresql-backup.md): тот же закрепленный digest
образа, защищенные файлы credentials, независимый escrow пароля и HTTPS backend.
Credentials не передаются на игровой хост. Архив передается через уже доверенный
SSH-канал, с проверкой SHA-256 на обеих сторонах. Идентификаторы snapshot и
местоположение backend остаются только в записи оператора.

```powershell
pwsh -NoProfile -File ./ops/production/player-data-backup.ps1 `
  -Action Archive -EnvironmentFile $DeploymentEnvironment `
  -BundlePath $PrivateBundle -ExpectedSha256 $ReviewedBundleSha256 `
  -EvidenceDirectory $NewPrivateEvidenceDirectory
```

`Archive` проверяет bundle до загрузки, создает ровно одну попытку backup,
фиксирует полный snapshot ID, проверяет структуру restic, загружает именно этот
snapshot обратно, сверяет весь SHA-256 и внутренний manifest, затем распаковывает
файлы в новый каталог `vault` внутри evidence directory. Путь в restic фиксирован:
`/player-data.tar`. Host имеет суффикс `.players`; обязательны теги
`goldsrcops-player-data-v1` и `bundle-sha256-<SHA-256>`.

Операции сериализованы общей блокировкой PostgreSQL recovery. Теги и путь отделяют
игровые данные от PostgreSQL retention. Скрипт не инициализирует backend, не
меняет credentials, не выполняет `forget`, `prune`, `unlock` или автоматический
retry. Локальный repository разрешен только явным тестовым переключателем;
такие restic-контейнеры запускаются с `--network none`.

`operation.json` отражает `started`, `uploaded` или `verified`. После таймаута
или незавершенной попытки сохраните каталог и выясните состояние snapshot по
приватному журналу и точной привязке hash/tag. Новый каталог не является
разрешением повторить неизвестную загрузку. Ошибки не публикуют сырой вывод
restic или значения конфигурации.

## Изолированное восстановление

Выберите точный ранее записанный snapshot ID и ожидаемый SHA-256:

```powershell
pwsh -NoProfile -File ./ops/production/player-data-backup.ps1 `
  -Action Recover -EnvironmentFile $DeploymentEnvironment `
  -SnapshotId $ReviewedSnapshotId -ExpectedSha256 $ReviewedBundleSha256 `
  -EvidenceDirectory $NewPrivateRecoveryDirectory
```

Проверяются workload, host, путь, теги, хеши и состав. Восстановление разрешено
только в новый каталог. Существующий, даже пустой, каталог отклоняется. Контейнер
restic получает доступ только к новому readback-каталогу. Распаковщик не использует
`extractall`, не принимает дополнительные пути, повторы, ссылки или специальные
файлы. Исходные vault никогда не являются целью восстановления.

Успех `verified` доказывает сохранность байтов выбранного snapshot. Поле
`engineAcceptance=pending` намеренно оставляет отдельную проверку настоящим AMXX.
Отдельная приемка не переписывает исходный результат Recover: она хранит свою
привязанную к копии запись. Для копии от 3 ноября такая проверка прошла в
[v2.101](v2.101-player-data-engine-recovery.md).
Для weekly-копии от 8 ноября отдельная проверка сохраненного readback также
прошла; см. [приемку и границу результата](player-backup-weekly-20261108.md).
Изолированный runtime должен использовать принятую версию AMXX/addon, копию
восстановленных файлов, не иметь сети и опубликованных портов. Проверить загрузку
статистики, языка, primary/pistol и HUD без вывода SteamID или значений записей.
Рабочий сервер и исходный архив при этом не изменяются.

Автоматический smoke
[`game-player-data-recovery-runtime.sh`](../tools/smoke/game-player-data-recovery-runtime.sh)
проверяет эту цепочку на синтетических данных с закрепленным AMXX 1.10.0.5481
и ReAPI 5.24.0.300. Он использует те же read-only `/smoke`, `/assets`, `/compiled`
и `/repo`, что существующий preferences runtime smoke. Перед запуском нужны
`PersistentStatsFixture` и `PlayerPreferencesFixture` из
[`amxx-weapon-selection.ps1`](../tools/smoke/amxx-weapon-selection.ps1).

### Проверка восстановленного TAR одной командой

[`check.py`](../tools/player-data-recovery/check.py) принимает уже
восстановленный TAR и ожидаемый SHA-256 из проверенной квитанции. Для запуска
нужны Python 3.12+, локальный Git с ревизией stable 0.40.0, проверенный stable
ZIP пары addon и nVault, каталог архивов из
[`engine-ci-lock.json`](../tools/smoke/engine-ci-lock.json) и проверенный
`steamclient.so` из того же lock. Используйте подготовленный локальный образ
Linux amd64 с базовыми файлами HLDS в `/opt/cs16/serverfiles-base`, Python 3,
Bash, `timeout` и `runuser`. Образ передается по неизменяемому ID.

На чистом стенде [`prepare.py`](../tools/player-data-recovery/prepare.py)
скачивает публичные входы по закрепленным хешам и собирает проверочный
образ по [Dockerfile](../tools/player-data-recovery/Dockerfile). Файлы
`stable.zip`, `assets`, `steamclient.so` и `engine-image-id` появляются
в новом каталоге. Сеть нужна на этапе подготовки публичных входов.

```powershell
python -B ./tools/player-data-recovery/prepare.py --output $NewLabDirectory `
  --docker-context $LocalDockerContext
```

```powershell
$runtime = @(
  '--stable-zip', $VerifiedStableZip,
  '--assets', $ReviewedEngineAssets,
  '--steam-client', $ReviewedSteamClient,
  '--engine-image-id', $ReviewedEngineImageId,
  '--docker-context', $LocalDockerContext
)
python -B ./tools/player-data-recovery/check.py --bundle $RecoveredTar `
  --expected-sha256 $ReviewedBundleSha256 @runtime
```

Команда сверяет TAR и хеши его файлов до запуска контейнера. Затем штатный
addon открывает оба vault, а диагностическая сборка из исходников той же
ревизии сверяет записи и их метки времени в двух запусках движка. Проверка
рассчитана на закрытые vault с пустыми или отсутствующими journal и максимум
4096 записей суммарно. Она принимает корректные пустые vault.

Контейнер создается в локальном Docker без сети, доступа к Docker socket и
публикации портов. TAR и данные движка передаются через память и tmpfs.
Стандартный вывод содержит JSON с результатом, счетчиками и кодом ошибки при
отказе. Код завершения 0 означает успешную проверку и удаление контейнера.
Используйте результат как отдельную запись приемки конкретной копии.

Код `input_hash_mismatch` означает отказ до запуска контейнера.
`nonempty_journal` и `vault_record_limit_exceeded` останавливают проверку
до запуска движка. `engine_mismatch` означает несовпадение при чтении движком.
После таймаута проверка удаления отражается в поле `containerRemoved`.

### Проверка weekly по квитанциям

[`check-weekly.py`](../tools/player-data-recovery/check-weekly.py)
принимает дату воскресенья, сохраненный `result.json` нужного слота и
`operation.json` выполненного Recover. Оставьте
`readback/player-data.tar` рядом с квитанцией в каталоге Recover.

```powershell
python -B ./tools/player-data-recovery/check-weekly.py --slot $WeeklyDate `
  --weekly-result $WeeklyResult --recover-receipt $RecoverOperation @runtime
```

Команда сверяет статус, снимок, SHA-256 и последовательность времен в обеих
квитанциях. Затем она проверяет SHA-256 самого TAR и время захвата в его
manifest. При несовпадении движок не запускается. Отчет содержит дату
выбранного слота, `receiptBindingVerified` и агрегаты проверки.
Файлы для проверки берутся из локальной копии квитанций и readback.

[`test-check.py`](../tools/player-data-recovery/test-check.py) принимает те
же параметры окружения (`@runtime`) и создает синтетическую копию для трех
проверок. Корректный TAR проходит два запуска. Неверный SHA-256 блокирует
запуск контейнера. Подмена ожидаемого значения вызывает отказ в движке.
Быстрые проверки формата и отказов выполняются командой
`python -B ./tools/player-data-recovery/test-inputs.py` и входят в полный CI.

### Сквозная репетиция с синтетическими данными

[`test-restic.ps1`](../tools/player-data-recovery/test-restic.ps1) связывает
штатные `Archive`, отдельный `Recover` и проверку движком. Нужен
предварительно загруженный образ restic из сценария и то же локальное
окружение. Скрипт создает синтетические vault, одноразовый зашифрованный
репозиторий и новый каталог для Recover. Отчет показывает совпадение снимка
и SHA-256, результат движка и длительность Recover.

```powershell
./tools/player-data-recovery/test-restic.ps1 -StableZip $VerifiedStableZip `
  -Assets $ReviewedEngineAssets -SteamClient $ReviewedSteamClient `
  -EngineImageId $ReviewedEngineImageId -DockerContext $LocalDockerContext
python -B ./tools/player-data-recovery/test-runtime-rejections.py @runtime
```

[`test-runtime-rejections.py`](../tools/player-data-recovery/test-runtime-rejections.py)
проверяет в локальном контейнере отказы для непустого journal, превышения
лимита записей и искусственной задержки при проверке в движке. Каждый отчет
подтверждает удаление созданного контейнера.

[`measure.py`](../tools/player-data-recovery/measure.py) с параметрами
`@runtime` прогоняет синтетические копии с 78, 1 024, 4 096 и 4 097 записями.
Он выводит по одному JSON-отчету на размер. `preparationSeconds` включает
проверку и подготовку входов на хосте. `engineSeconds` измеряет три запуска
HLDS, включая ожидание готовности, и появляется при успешной проверке.

В ручном запуске workflow `CI` параметр `player_recovery_rehearsal`
добавляет задачу сборки на GitHub runner и сквозную синтетическую репетицию.
Задача сохраняет только итоговый JSON в артефакт
`synthetic-player-recovery-report`.

## Weekly backup и Recover от 22 ноября 2026

- Штатный weekly начался в 04:00:00 МСК и завершился в 04:01:02 МСК со
  статусом `verified`. Запись слота, квитанция Archive и `last-success`
  подтвердили один снимок.
- Независимый Recover этого снимка занял 14,85 с. SHA-256 восстановленного
  TAR совпал с хешем из записи weekly. Перед загрузкой стенд также сверил
  время захвата и хеши обоих vault-файлов.
- Штатная пара addon 0.40.0 и nVault из проверенного stable ZIP открыла оба
  хранилища. Addon построил таблицу лидеров без ошибок. Диагностическая сборка
  из тех же исходников с тем же nVault в двух запусках движка сверила значения
  и метки времени всех 78 записей, включая статистику, ранги, настройки оружия,
  HUD, серии и имена для поиска. Ошибок сверки и попыток записи через
  контролируемые вызовы было 0. Тест с подмененным ожидаемым значением
  завершился ожидаемым отказом.
- Docker-стенд работал без сети и опубликованных портов. Восстановленные
  файлы загружались в память и tmpfs. После завершения проверено удаление
  контейнера.

После сбоя транспорта результат Recover подтвердили по отдельной квитанции
и повторной сверке хеша уже восстановленного TAR. Было выполнено одно
восстановление. Измеренная длительность относится к этому снимку и условиям
его восстановления.

## Ограничения и приемка

Это ручной recovery baseline. Автоматическое расписание, удаление старых
snapshot, production restore и обещание RPO/RTO в этот этап не входят. Делайте
новый backup перед изменениями durable state; согласуйте частоту отдельно с
допустимым окном остановки. До появления расписания возраст последней копии
остается обязанностью оператора. Ни нулевой RPO, ни восстановление после потери
диска без последних записей здесь не обещаются.

Журналы сохраняются побайтно. Изолированная [проверка падения процесса](player-data-crash-acceptance.md)
выявила потерю новых изменений после SIGKILL: readback прошел, но journals были
пустыми и после запуска восстановился прежний checkpoint. Приемка сохранности
исходной версии не пройдена. Отдельное [исправление](player-data-durability-implementation.md)
прошло SIGKILL и повторное открытие с новым модулем. Production-приемка
описана в [релизе 0.40.0](player-storage-0.40.0-release.md). Испытание
охватывало аварийное завершение процесса.
SHA-256 выявляет изменение копии и не заменяет
проверку исходных данных движком. Сохранность байтов не исправляет уже испорченный
vault. Пароль restic и существующий backend остаются общей границей доверия с
другими backup; это не отдельный immutable/append-only repository.

Для приемки конкретного хоста нужны: закрытый согласованный снимок, совпадение
хеша после передачи, off-host readback выбранного snapshot, изолированная
проверка его данных и измеренное время восстановления. Локальные fixtures и
зеленый CI не заменяют эти результаты. Production restore потребует отдельного
решения о downtime и потере изменений после выбранного snapshot.

Ручной v2.49 baseline сохранен. Следующий этап описан отдельно в
[еженедельном backup](game-player-data-schedule.md); его установка и первая
попытка требуют собственных доказательств.
