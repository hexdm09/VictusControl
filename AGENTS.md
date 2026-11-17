# AGENTS.md — инструкция по Linux-ветке VictusControl

Эта ветка (`linux`) содержит нативную Linux-версию VictusControl (Python 3.11+, 0 внешних pip-зависимостей, GTK4 + Libadwaita + `systemd` демон `victusd` + `victusctl` CLI). Отдельная Windows-версия (C# / .NET 9 WPF) находится в ветке `windows`.

## Архитектурные инварианты (НЕ нарушать)

1. **Никогда не будить спящую NVIDIA dGPU в `ECO` или при `runtime_status == "suspended"`**:
   - Чтение статуса dGPU идёт строго через `/sys/bus/pci/devices/0000:01:00.0/power/runtime_status`. Запрещено вызывать `nvidia-smi` или открывать `/dev/nvidia*`, когда карта в `suspended` (`D3cold`).
2. **Никаких прямых записей в порты EC (`/dev/port` / `ec_sys`) в обход ядра**:
   - Ядро Linux 6.19+/7.0+ (`hp-wmi`) уже содержит нативную поддержку платы `8C9C` (`victus_s`), включая таблицу `0x2F`, `gpu_delta` и 90-секундный keep-alive workqueue. Все операции выполняются только через `sysfs` (`platform_profile`, `hwmon/pwm1_enable`, `hwmon/pwm1`, `amd-pstate-epp`, `cpufreq/boost`).
3. **Fail-Safe возврат кулеров в `Auto` (`pwm1_enable = 2`)**:
   - При завершении `victusd` (`VictusController.shutdown()` и `ExecStopPost` в `systemd/victus-controld.service`) кулеры обязаны возвращаться в `2` (`BIOS Auto`).
4. **Проверка перед коммитом**:
   ```bash
   PYTHONPATH=src:. python3 -m unittest discover -s tests -v
   ```
