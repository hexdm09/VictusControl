# VictusControl (Linux Edition)

Нативная замена OMEN Gaming Hub для ноутбуков **HP Victus / OMEN** под Linux (Ubuntu 24.04 / 26.04+, ядро Linux 6.19+ / 7.0+ с апстримным драйвером `hp-wmi`).

> **Ветки репозитория:**
> - `linux` — нативная версия для Linux (**Python 3 + GTK4 / Libadwaita + systemd-демон `victusd` + `victusctl` CLI**).
> - `windows` — отдельная нативная версия для Windows 10/11 (**C# / .NET 9 + WPF + CIM/WMI** с исправленным протоколом Victus S `8C9C`).

---

## Ключевые возможности Linux-версии

- **Три полноценных профиля питания (`ECO`, `BALANCED`, `PERFORMANCE`)**:
  - Синхронное переключение `/sys/firmware/acpi/platform_profile` (управляет одновременно `hp-wmi` cTGP/PPAB и драйвером `amd-pmf`).
  - Автоматическая настройка `amd-pstate-epp` (`power` / `balance_performance` / `performance`) и отключение высоковольтного буста CPU (`/sys/devices/system/cpu/cpufreq/boost = 0`) в режиме **ECO** (экономит 5–12 Вт на фоновых задачах).
  - Автоматическое переключение матрицы ноутбука на **60 Гц** при работе от батареи в **ECO** через GNOME Wayland Mutter DBus (`org.gnome.Mutter.DisplayConfig`) или `xrandr`, с возвратом на 144/165 Гц в `BALANCED`/`PERF`.
- **Честное и безопасное управление кулерами (`hp-wmi` hwmon)**:
  - Режимы **AUTO** (`pwm1_enable=2`, кривая BIOS), **CURVE** (плавная программная кривая по температуре с гистерезисом 3 °C), **MAX** (`pwm1_enable=0`, 100%) и **MANUAL** (`10–100%` через `pwm1` с ядерным keep-alive 90 с и авто-учётом `gpu_delta`).
  - **Fail-Safe защита железа**: при остановке или падении сервиса `victus-controld` systemd принудительно возвращает кулеры в `pwm1_enable = 2` (`Auto`).
- **Защита сна дискретной видеокарты NVIDIA (`D3cold = 0.0 W`)**:
  - Мониторинг состояния RTX dGPU через `/sys/bus/pci/devices/.../power/runtime_status` **без вызова `nvidia-smi`**, чтобы никогда не будить спящую видеокарту в `ECO`.
  - Если dGPU удерживается в состоянии `active`, детектор показывает конкретные процессы, открывшие `/dev/nvidia*`.
- **Живой ваттметр аккумулятора** (`BAT1/power_now` в ваттах прямо в шапке виджета и в `victusctl --status`).
- **Аппаратная клавиша `Fn+P` / OMEN**: перехват событий `HP WMI hotkeys` из `/dev/input/event*` в фоновом демоне.

---

## Быстрая установка (Ubuntu 26.04 / GNOME)

Все зависимости (`Python 3`, `PyGObject`, `GTK 4`, `Libadwaita 1`) уже предустановлены в Ubuntu Desktop из коробки:

```bash
chmod +x install_linux.sh
./install_linux.sh
```

Скрипт прогонит автотесты, установит демон `victus-controld.service`, сбросит зависший ручной режим кулеров в `Auto` и добавит команды `victusctl` и `victus-control-gui` (плюс ярлык **VictusControl** в меню приложений GNOME).

Удаление:
```bash
./install_linux.sh --uninstall
```

---

## Использование CLI (`victusctl`)

```bash
# Полная сводка: профиль, обороты кулеров, температуры, сон dGPU и ватты батареи
victusctl --status

# Переключение профилей питания
victusctl --set eco
victusctl --set balanced
victusctl --set perf
victusctl --cycle

# Управление вентиляторами
victusctl --fan auto     # Штатная авто-кривая BIOS (pwm1_enable = 2)
victusctl --fan curve    # Умная кривая по температуре с гистерезисом 3°C
victusctl --fan max      # Максимум 100% (pwm1_enable = 0)
victusctl --fan 55       # Ручной режим 55%

# Частота обновления экрана (GNOME Wayland / X11)
victusctl --hz 60
victusctl --hz max

# Возможности платы в JSON
victusctl --info
```

---

## Разработка и тесты без установки

```bash
# Запуск всех unit и интеграционных тестов
PYTHONPATH=src:. python3 -m unittest discover -s tests -v

# Проверка живой телеметрии в режиме только чтения (без root)
PYTHONPATH=src python3 -m victus_control.cli --status
```
