# RimForge X Manager BETA (Создана с помощью вайбкодинга с испольозванием claude)
<img width="1920" height="1033" alt="{7317BDC7-6CED-43AD-BBCD-C0AA33948D1F}" src="https://github.com/user-attachments/assets/e944f11d-f66a-4a6b-ab43-af314cc72396" />

**Бесплатный менеджер модов для RimWorld с открытым исходным кодом.**
Интуитивно понятный интерфейс (пока что) на русском и английском, проверка сборки перед запуском и подсказки на каждой кнопке.

*A free, open-source mod manager for RimWorld: clear interface in Russian and English, pre-launch checks and
tooltips everywhere. [English below](#english).*

[**⬇ Скачать последнюю версию / Download the latest release**](https://github.com/SORLOK212/RimForge-X-Manager/releases/latest)

---

## Возможности

**Моды и сборки**
- Сам находит игру (Steam / GOG), моды из Мастерской и локальные моды; папки можно указать вручную
- Два списка — доступные и активные моды, перетаскивание, поиск, фильтры, отмена и повтор (Ctrl+Z / Ctrl+Y)
- Сборки (модпаки): переключение одной кнопкой, у каждой сборки свои настройки модов
- Сборка из сохранения игры, обмен сборками с друзьями файлом
- Резервная копия списка модов перед каждым сохранением

**Проверка и сортировка**
- : Зависимости, несовместимости, версии игры, порядок загрузки — и исправление одной кнопкой
- Умная сортировка: зависимости, правила и «фазы» содержимого (библиотеки → контент → графика → патчи → переводы)
- Время запуска игры: оценка рядом с «Играть» и разбивка по модам
- Диагностика: разбор лога игры, конфликты модов и текстур, поиск мода-виновника делением списка

**Мастерская Steam внутри**
- Поиск, темы, листание страниц, галерея картинок и видео, подписка и скачивание с индикатором
- Коллекции Steam: поиск по темам, просмотр, создание отдельной сборки из коллекции
- Обновления модов, очистка неиспользуемых, поиск переводов

**Версии игры**
- Сохранение копии игры вместе с модами — обновление игры не сломает сборку
- Скачивание других версий RimWorld (1.0–1.6, тестовая) через официальный SteamCMD

**Ещё**
- Машинный перевод модов, в том числе без интернета
- Помощник и подключение ИИ-клиентов (MCP-сервер)
- Автообновление RimForge

## Установка

1. Открой [страницу последнего выпуска](https://github.com/SORLOK212/RimForge-X-Manager/releases/latest).
2. Скачай один из вариантов:
   - **`RimForge-Setup-x.y.z.exe`** — установщик (рекомендуется). Права администратора не нужны,
     RimForge появится в меню «Пуск» и сам будет предлагать обновления.
   - **`RimForge-x.y.z-portable.zip`** — портативная версия: распакуй в любую папку и запусти `RimForge.exe`.
     Все данные хранятся рядом с программой.
3. Требуется Windows 10 или 11 (64 бита). Устанавливать .NET не нужно — всё уже внутри.

### «Windows защитила ваш компьютер»

Программа пока не подписана платным сертификатом, поэтому при первом запуске Windows SmartScreen может
показать предупреждение. Нажми **«Подробнее» → «Выполнить в любом случае»**. Проверить, что файл не подменён,
можно по контрольным суммам из `SHA256SUMS.txt` в выпуске.

### Удаление

«Параметры → Приложения → RimForge X Manager → Удалить». Установщик спросит, удалить ли настройки, сборки и
библиотеку версий игры. Сама игра, её сохранения и моды из Steam не затрагиваются.

### Где RimForge хранит данные

| Что | Установленная версия | Портативная версия |
|---|---|---|
| Программа | `%LOCALAPPDATA%\Programs\RimForge` | папка, куда распакован архив |
| Настройки, сборки, резервные копии | `%APPDATA%\RimForge` | `Data` рядом с `RimForge.exe` |
| Библиотека версий игры | `%LOCALAPPDATA%\RimForge\Library` | `Library` рядом с `RimForge.exe` |

## Сборка из исходников

Нужен [.NET SDK 10](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/RimForge.App
dotnet test
```

- `publish.cmd` — один файл `RimForge.exe` в папке `publish` (.NET для запуска не нужен).
- `release.cmd` — всё для выпуска в `artifacts\<версия>`: установщик, портативный архив, `latest.json` для
  автообновления и контрольные суммы. Нужен [Inno Setup 6](https://jrsoftware.org/isdl.php).
  Версия берётся из `Directory.Build.props`.

| Папка | Что внутри |
|---|---|
| `src/RimForge.Core` | Логика без интерфейса: игра, моды, ModsConfig.xml, проверки, сортировка, библиотека версий |
| `src/RimForge.Steam` | Мастерская Steam, коллекции, загрузки |
| `src/RimForge.Translation` | Перевод модов |
| `src/RimForge.Mcp` | MCP-сервер для ИИ-клиентов |
| `src/RimForge.App` | Окно программы (Avalonia UI) |
| `tests/RimForge.Core.Tests` | Тесты |
| `installer/` | Скрипт установщика (Inno Setup) |
| `docs/` | План развития и заметки к выпускам |

---

## English

**RimForge X Manager** is a free, open-source RimWorld mod manager.

- Finds the game (Steam / GOG), Workshop and local mods; two lists with drag and drop, search and filters
- Modpacks with their own mod settings, modpacks from a save, sharing with friends
- Pre-launch checks (dependencies, incompatibilities, game versions, load order) with one-click fixes
- Smart sorting, startup-time estimate, log analysis, mod and texture conflicts, culprit-mod search
- The Steam Workshop and Steam collections inside the app, with galleries and download progress
- Saved copies of the game with frozen mods; downloading other RimWorld versions through Valve's SteamCMD
- Mod translation (also offline), an assistant and an MCP server for AI clients, self-updates

**Install:** download `RimForge-Setup-x.y.z.exe` (no administrator rights needed) or the portable zip from the
[latest release](https://github.com/SORLOK212/RimForge-X-Manager/releases/latest). Windows 10/11 x64; .NET is
bundled. The app is not code-signed yet, so SmartScreen may warn on first start: *More info → Run anyway*.

**Build:** .NET SDK 10, `dotnet run --project src/RimForge.App`; `release.cmd` builds the installer
(Inno Setup 6), the portable zip and the update feed.

---

## Лицензия / License

RimForge X Manager — свободная программа под лицензией [GNU GPL v3.0 или более поздней](LICENSE).
Copyright (C) 2026 SORLOK212 and contributors.

**Дополнительное разрешение (GPL-3.0, раздел 7).** Разрешается объединять RimForge с Steamworks SDK от Valve
(`steam_api64.dll`) и распространять результат, при условии что для остального кода соблюдается GPL.

*Additional permission under GNU GPL version 3 section 7: if you modify this program, or any covered work, by
linking or combining it with the Steamworks SDK by Valve Corporation (or a modified version of it, including
`steam_api64.dll`), containing parts covered by the terms of the Steamworks SDK license, the licensors of this
program grant you additional permission to convey the resulting work.*

RimForge не связан с Ludeon Studios и Valve. RimWorld — товарный знак Ludeon Studios; Steam — товарный знак Valve.
*RimForge is not affiliated with Ludeon Studios or Valve.*
