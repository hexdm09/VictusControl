# Архитектура VictusControl (Linux Edition)

## Схема взаимодействия

```text
[Пользователь: GTK4/Libadwaita UI / victusctl CLI]
       │
       ├──► Session DBus: org.gnome.Mutter.DisplayConfig (60 Гц ↔ 144/165 Гц)
       │
       ▼ Unix Socket (/run/victus-control/victusd.sock)
[victusd (systemd сервис victus-controld.service)]
       │
       ├──► /sys/firmware/acpi/platform_profile   (синхронно: hp-wmi + amd-pmf)
       ├──► /sys/devices/platform/hp-wmi/hwmon/*  (pwm1_enable: 0=Max, 1=Manual, 2=Auto; pwm1; fan1/2_input)
       ├──► /sys/devices/system/cpu/cpu*/cpufreq  (amd-pstate-epp: powersave + EPP power/balance/perf)
       ├──► /sys/devices/system/cpu/cpufreq/boost (0 в Eco, 1 в Balanced/Perf)
       ├──► /sys/bus/pci/devices/0000:01:00.0/power/runtime_status (мониторинг сна D3cold без пробуждения!)
       └──► /dev/input/event* ("HP WMI hotkeys")  (аппаратные клавиши Fn+P / OMEN)
```

## Почему Linux-версия работает через ядерный `hp-wmi`, а не сырые WMI/EC пакеты

Начиная с ядер Linux 6.19–7.0 драйвер `drivers/platform/x86/hp/hp-wmi.c` содержит полную апстримную поддержку материнских плат **HP Victus S-series (включая `8C9C`)**:
1. При инициализации ядро само вычитывает паспортную таблицу вентиляторов `0x2F` (`min_rpm`, `max_rpm`, `gpu_delta`).
2. При записи в `pwm1` (`0..255`) ядро масштабирует обороты в единицы `RPM / 100`, прибавляет `gpu_delta` ко второму кулеру, выполняет атомарную последовательность `0x10` -> `0x27=0` -> `0x2E` (2 байта) и поддерживает 90-секундный ядерный keep-alive workqueue.
3. При записи `2` в `pwm1_enable` ядро гарантированно возвращает управление авто-кривой BIOS и останавливает keep-alive.
4. При переключении `platform_profile` ядро синхронно обновляет и `hp-wmi` (сохраняя заводской `gpu_slowdown_temp` в байте 3 команды `0x22`), и `amd-pmf` (лимиты пакета процессора AMD Ryzen).
