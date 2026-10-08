# Еженедельный backup игровых данных

## Политика v2.50

Выбранное владельцем расписание: воскресенье, 04:00, `Europe/Moscow`.
Попытка запуска разрешена только с 04:00 до 04:05 по этому часовому поясу.
Timer не догоняет пропущенный запуск после загрузки хоста; код повторно
проверяет окно, в том числе после preflight. При занятом сервере цикл
фиксирует `skipped-busy`, не останавливает игру и ждет следующего воскресенья.

Это автоматизация [ручного процесса v2.49](game-player-data-backup.md).
Закрытый снимок, зашифрованный restic backend, exact-snapshot readback и запрет
production restore сохраняются. Weekly cycle доказывает байты snapshot;
проверка движком остается отдельной процедурой, как в v2.49.

## Компоненты

- [Control-plane runner](../ops/production/player-data-scheduled.py) выполняет
  одну попытку capture, получает архив, проверяет его и вызывает существующий
  `player-data-backup.ps1 -Action Archive`.
- [Game-host runner](../ops/gameserver/player-data-scheduled-capture.py) выполняет
  preflight, две проверки A2S, один stop/capture/start и postflight. Он удерживает
  существующие backup/transition locks на всем участке обслуживания.
- [Host guard](../ops/gameserver/player-data-schedule-guard.sh) проверяет
  persistent baseline, отдельный hash-bound baseline addon/configuration,
  active/enabled game и agent, пустые queue/spool и права vault.
- [SSH dispatcher](../ops/gameserver/player-data-backup-ssh.sh) разрешает только
  `capture YYYY-MM-DD`, `status YYYY-MM-DD` и `bundle YYYY-MM-DD`.
  Дата должна быть воскресеньем. Пути, shell-команды и произвольные аргументы
  от клиента не принимаются.
- [Timer](../ops/production/systemd/goldsrcops-player-data-backup.timer) и
  [service](../ops/production/systemd/goldsrcops-player-data-backup.service)
  устанавливаются на control-plane. Рабочий компьютер и открытый чат для
  последующих запусков не нужны.

Перед остановкой A2S должен ответить от настроенного локального игрового endpoint
с ожидаемым именем, папкой `cstrike`, нулем игроков и ботов. Адрес должен
принадлежать локальному интерфейсу, порт - совпадать с prepared host marker.
Ошибка протокола
или timeout прекращают попытку. A2S и stop не являются атомарной операцией:
остается короткий промежуток между последним ответом и остановкой. Запрет новых
входов через addon в этот этап не входит.

После stop проверяются `inactive`, нулевой PID, отсутствие отдельного
`hlds_linux`, baseline и точное совпадение закрытых файлов с архивом.
Прежний runtime запускается до сетевой передачи. Проверяются A2S, baseline,
queue/spool и прежний InvocationID агента. Неясный результат stop/start требует
оператора. При известном `inactive` после ошибки capture допускается один
запуск прежнего runtime; повтор неизвестного start запрещен.

## Записи попыток и свежесть

Закрытые state directories создаются оператором с owner root и режимом `0700`:

- игра: `/var/lib/goldsrcops-player-backup`;
- control-plane: `/var/lib/goldsrcops/player-data-backup`.

Каждое воскресенье имеет create-only каталог по дате. До side effect создается
`pending.json`. После известного результата в каталоге сохраняется
`result.json`. Успешный повтор уже завершенного слота читает результат без
второго stop или Archive. Любая незавершенная попытка блокирует и следующие
недели до ручной сверки. Отсутствующий ответ SSH не является разрешением повторить
команду. Записи и архивы имеют режим `0600`; идентификаторы игроков и snapshot
не печатаются в systemd journal.

`last-success.json` обновляется только после verified Archive/readback.
Пропуск занятой игры его не обновляет. `status` проверяет возраст исходного
capture, а не дату повторной загрузки. Копия старше восьми суток, отсутствующая
копия или незавершенная попытка дают ненулевой exit code:

```bash
sudo python3 -I /opt/goldsrcops-player-backup/ops/production/player-data-scheduled.py status
```

Service проверяет статус после каждой недельной попытки. Это не отдельное
ежедневное оповещение: отправка уведомлений или подключение нового мониторинга
в этот этап не входят. Если одно воскресенье занято, возраст может превысить
неделю; обещания RPO в семь суток нет.

Перед первой активацией можно однократно импортировать уже проверенную ручную
копию. Исходный `operation.json` должен иметь `action=Archive`, `state=verified`,
полный snapshot ID и совпадающий hash приватного bundle. Import не обращается к
backend, не создает snapshot и не заменяет проверку сохраненной записи оператора:

```bash
sudo python3 -I /opt/goldsrcops-player-backup/ops/production/player-data-scheduled.py \
  baseline "$REVIEWED_ARCHIVE_OPERATION" "$REVIEWED_PRIVATE_BUNDLE"
```

Второй import, неверные байты, незавершенный Archive и слишком старая копия
отклоняются. Импорт не перезаписывает существующий `last-success.json`.

Автоматического удаления нет. Не более 52 записей слотов на каждом хосте и
свободное место не менее четырех максимальных архивов ограничивают накопление.
Достижение лимита требует отдельного решения по хранению, а не автоматического
`forget`, `prune` или удаления evidence.

## Установка и новая граница доступа

До установки требуется один одобренный R3 bundle с точной source revision.
Он включает обычный push/PR, merge после required CI, ограниченный SSH-канал,
установку проверенных файлов, импорт v2.49 baseline, включение timer и одну
итоговую запись приемки. Включение расписания не разрешает запуск вне окна.

1. Разместить immutable reviewed source на обоих хостах в
   `/opt/goldsrcops-player-backup`, root-owned, без group/other write.
   Проверить SHA-256 всех переданных файлов. Не использовать временный
   operator checkout как исполняемый путь timer.
2. На control-plane создать отдельный Ed25519 identity в закрытом каталоге
   `/etc/goldsrcops/player-data-backup`. Private key остается только там.
   Не переносить личный ключ оператора, не включать agent forwarding.
3. На игре создать отдельного пользователя `gso-player-backup` с заблокированным
   паролем, без дополнительных групп. Его shell нужен для forced command;
   обычный вход ограничивается root-owned authorized_keys с
   `restrict,from="<reviewed-control-plane-IPv4>/32",command="/usr/local/libexec/goldsrcops-player-data-backup"`.
   Public key не дает интерактивного shell, PTY, forwarding или SFTP.
4. Установить dispatcher в указанный root-owned путь и дать этому пользователю
   sudo только для него по [шаблону](../ops/gameserver/player-data-backup.sudoers.example).
   Dispatcher повторно проверяет argv, очищает окружение
   и запускает фиксированный Python-файл с `-I`. Проверить sudoers через
   `visudo -cf` и эффективные ограничения sshd для этого пользователя.
   Если действует AllowUsers, добавить только выделенного пользователя с
   источником control-plane, сохранив операторское правило. При разборе
   `sshd -T` учитывать несколько строк `allowusers`.
5. При необходимости добавить ровно один SSH ingress от control-plane `/32`.
   Существующий операторский доступ сохранить; проверить provider firewall/UFW
   и сохранность действующих runtime guards. Новые публичные порты не нужны.
6. Настроить root-only `ssh_config` по [шаблону](../ops/production/player-data-ssh.config.example)
   на control-plane для единственного alias
   `goldsrcops-player-backup`, выделенного пользователя/key и отдельного
   pinned `known_hosts`. Host key сверить через уже доверенный операторский
   канал; не доверять результату `ssh-keyscan` без независимой сверки.
7. Создать root-only game config по [шаблону](../ops/gameserver/player-data-backup.config.example.json):
   `/etc/goldsrcops/gameserver/player-data-backup.json` с `schema=1`,
   `host`, `port`, `expectedName`, `vaultDirectory`. Указать реально слушающий
   локальный адрес игры и существующий каталог двух vault. Проверить A2S.
   Снять reviewed `player-data-backup-baseline.sha256` с текущих plugin,
   dictionary, map payload, receipts и остальных принятых baseline-файлов.
8. Подготовить приватные state directories. Импортировать проверенную v2.49
   запись и bundle только на control-plane. Проверить `status` и чтение backend
   из transient service с теми же sandbox properties. При использовании общего
   restic helper явно выбрать `--host <backup-host>.players` вместе с workload
   tag: у helper основной RESTIC_HOST относится к PostgreSQL.
9. Проверить запрет произвольных SSH-команд, shell/forwarding и выхода за
   расписание; в этот момент игровой сервис не останавливать.
10. Проверить units через `systemd-analyze verify`, календарь через
    `systemd-analyze calendar`, затем установить service/timer и выполнить
    `systemctl enable --now goldsrcops-player-data-backup.timer`.
    Не запускать service вручную для обхода недельного окна.

Откат установки: отключить только новый timer, дождаться окончания текущей
операции или сначала сверить ее состояние, затем отозвать новый ключ и
выделенные sudo/firewall разрешения. Существующий ручной backup, restic repository,
gameplay и сохраненные snapshot остаются доступны. Не удалять `pending.json`
или приватные архивы как способ получить зеленый статус.

Первая установка, первый фактический weekly cycle и приемка его snapshot -
разные доказательства. Пока первого запуска не было, отмечать его как pending.

Источники семантики: [systemd v255 timer](https://github.com/systemd/systemd/blob/v255/man/systemd.timer.xml)
и [OpenSSH authorized_keys](https://man.openbsd.org/sshd.8#AUTHORIZED_KEYS_FILE_FORMAT).
